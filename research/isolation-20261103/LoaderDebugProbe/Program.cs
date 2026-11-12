// Fixed owned LPAC console probe, native debug events only. No memory/context access.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Lua = AutumnOS.LuaTokenProbe.Native;
using Facts = AutumnOS.LuaTokenProbe.TokenFacts;
using DesktopApi = AutumnOS.SyntheticLuaLauncher.PrivateDesktopNative;
using Desktop = AutumnOS.SyntheticLuaLauncher.OwnedDesktop;
using DesktopNames = AutumnOS.SyntheticLuaLauncher.Native;

namespace AutumnOS.LoaderDebugProbe;

internal static class Program
{
    private const uint Continue = 0x10002, NotHandled = 0x80010001;
    private const int TimeoutMilliseconds = 60000, MaxEvents = 4096;

    [MTAThread]
    private static int Main(string[] args)
    {
        if (args.Length is not (4 or 5 or 6) || args[0] != "--report" || args[2] != "--executable" ||
            args.Length >= 5 && args[4] != "--no-debug" || args.Length == 6 && args[5] != "--no-window" ||
            !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[3])) return 2;
        bool noDebug = args.Length >= 5, noWindow = args.Length == 6;
        uint creationFlags = (noDebug ? 0x400u : 0x402u) | (noWindow ? 0x08000000u : 0u);
        string workspace = Path.GetFullPath(Environment.CurrentDirectory);
        string expectedExe = Path.Combine(workspace, "tools", "AutumnOS.Lpac.Probe", "bin", "x64", "Release", "net10.0-windows", "win-x64", "AutumnOS.Lpac.Probe.exe");
        string report = Path.GetFullPath(args[1]);
        if (!File.Exists(Path.Combine(workspace, "AutumnOS.slnx")) || !SamePath(expectedExe, args[3]) || !Within(report, Path.Combine(workspace, "artifacts"))) return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        for (string? p = Path.GetDirectoryName(report); p is not null && p.Length >= workspace.Length; p = Path.GetDirectoryName(p))
            if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return 2;
        using var reportFile = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var events = new List<object>();
        var loadedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var autoHandles = new HashSet<IntPtr>();
        Facts? before = null, copyAfterMedium = null, selected = null, actualChild = null, after = null;
        bool copiedDefaultDaclRepaired = false;
        SafeAccessTokenHandle? source = null, copy = null;
        Desktop? desktop = null;
        Native.ProcessInformation process = default;
        Native.DebugEvent pending = default;
        uint pendingContinuation = Continue;
        bool havePending = false, apiStarted = false, debugActive = false, killOnExitDisabled = false;
        bool exitEventContinued = false, naturalExit = false, initialBreakpointHandled = false, detached = false;
        bool desktopClosed = false, parentAssociationUnchanged = false, parentUnchanged = false;
        bool timedOut = false, eventLimit = false;
        int fileHandlesClosed = 0, callerHandlesClosed = 0, unexpectedPidEvents = 0;
        uint? exitCode = null;
        int? detachError = null, continueError = null;
        string? parentStation = null, parentDesktop = null, executableHash = null, childReport = null;
        object? failure = null;
        string stage = "validate_fixed_inputs_and_native_layout";
        int result = 1;
        try
        {
            if (!Environment.Is64BitProcess || Marshal.SizeOf<Native.DebugEvent>() != 176 || Marshal.SizeOf<Native.ExceptionDebugInfo>() != 160 ||
                Marshal.OffsetOf<Native.DebugEvent>(nameof(Native.DebugEvent.Data)).ToInt32() != 16 ||
                Marshal.OffsetOf<Native.ExceptionRecord>(nameof(Native.ExceptionRecord.Address)).ToInt32() != 16 ||
                Marshal.OffsetOf<Native.ExceptionDebugInfo>(nameof(Native.ExceptionDebugInfo.FirstChance)).ToInt32() != 152 ||
                Marshal.OffsetOf<Native.CreateProcessDebugInfo>(nameof(Native.CreateProcessDebugInfo.Process)).ToInt32() != 8 ||
                Marshal.OffsetOf<Native.CreateProcessDebugInfo>(nameof(Native.CreateProcessDebugInfo.Thread)).ToInt32() != 16 ||
                Marshal.OffsetOf<Native.CreateProcessDebugInfo>(nameof(Native.CreateProcessDebugInfo.Base)).ToInt32() != 24 ||
                Marshal.OffsetOf<Native.LoadDllDebugInfo>(nameof(Native.LoadDllDebugInfo.Base)).ToInt32() != 8)
                throw new InvalidOperationException("X64_DEBUG_EVENT_LAYOUT_MISMATCH");
            using var executableLease = new FileStream(expectedExe, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!SamePath(Native.FinalPath(executableLease.SafeFileHandle.DangerousGetHandle()), expectedExe) ||
                FileVersionInfo.GetVersionInfo(expectedExe).ProductName != "Lab Chronicles AutumnOS") throw new InvalidDataException("FIXED_OWNED_LPAC_EXE_REQUIRED");
            executableHash = Convert.ToHexStringLower(SHA256.HashData(executableLease));
            Native.SetErrorMode(0x8003);
            stage = "create_verified_synthetic_lua_medium";
            if (!Lua.OpenProcessToken(Lua.GetCurrentProcess(), 0x8b, out source)) throw Lua.Error("OPEN_SOURCE_TOKEN_FAILED");
            before = Lua.Read(source);
            if (before.token_type != 1 || !before.elevated || before.integrity_rid != 0x3000 || before.has_restrictions || before.is_token_restricted ||
                !before.administrators_enabled || before.ui_access || before.sandbox_inert || before.is_appcontainer)
                throw new InvalidOperationException("UNRESTRICTED_HIGH_ADMIN_PARENT_REQUIRED");
            if (!Lua.CreateRestrictedToken(source, 5, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out copy)) throw Lua.Error("CREATE_LUA_FLAGS5_COPY_FAILED");
            Lua.SetMedium(copy);
            copyAfterMedium = Lua.Read(copy);
            stage = "repair_only_copy_private_object_default_dacl";
            Lua.SetCopyPrivateObjectDefaultDacl(copy);
            copiedDefaultDaclRepaired = true;
            selected = Lua.Read(copy);
            if (!ExpectedBaseline(selected, before)) throw new InvalidOperationException("SYNTHETIC_BASELINE_ASSERTIONS_FAILED");
            parentStation = DesktopNames.UserObjectName(Native.GetProcessWindowStation());
            parentDesktop = DesktopNames.UserObjectName(Native.GetThreadDesktop(Native.GetCurrentThreadId()));
            stage = "create_verified_owned_low_desktop";
            desktop = DesktopApi.Create();
            childReport = Path.Combine(Path.GetDirectoryName(report)!, Path.GetFileNameWithoutExtension(report) + ".lpac.json");
            if (File.Exists(childReport)) throw new InvalidDataException("CHILD_REPORT_MUST_BE_NEW");
            var startup = new Native.StartupInfo { Size = Marshal.SizeOf<Native.StartupInfo>(), Desktop = parentStation + "\\" + desktop.Name };
            var command = new StringBuilder(string.Join(" ", new[] { expectedExe, "--report", childReport, "--baseline", "synthetic-lua-medium" }.Select(Quote)));
            stage = noDebug ? "create_only_fixed_child_without_debug_or_pipes" : "create_only_fixed_child_with_native_debug_connection";
            apiStarted = Native.CreateProcessAsUser(copy, expectedExe, command, IntPtr.Zero, IntPtr.Zero, false,
                creationFlags, IntPtr.Zero, workspace, ref startup, out process);
            if (!apiStarted) throw Native.Error("CREATE_DEBUG_OWNED_CHILD_FAILED");
            debugActive = !noDebug;
            if (!noDebug)
            {
                if (!Native.DebugSetProcessKillOnExit(false)) throw Native.Error("DISABLE_DEBUGGER_KILL_ON_EXIT_FAILED");
                killOnExitDisabled = true;
            }
            using (SafeAccessTokenHandle childToken = Native.QueryToken(process.Process)) actualChild = Lua.Read(childToken);
            if (!ExpectedBaseline(actualChild, before)) throw new InvalidOperationException("ACTUAL_CHILD_SYNTHETIC_TOKEN_MISMATCH");
            stage = noDebug ? "wait_for_owned_no_debug_child_natural_exit" : "collect_owned_native_debug_events";
            var clock = Stopwatch.StartNew();
            while (!noDebug && !exitEventContinued)
            {
                if (clock.ElapsedMilliseconds >= TimeoutMilliseconds) { timedOut = true; break; }
                if (events.Count >= MaxEvents) { eventLimit = true; break; }
                if (!Native.WaitForDebugEventEx(out pending, 250))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == 121) continue; // ERROR_SEM_TIMEOUT.
                    throw new Win32Exception(error, "WAIT_FOR_OWNED_DEBUG_EVENT_FAILED");
                }
                havePending = true;
                bool own = pending.ProcessId == process.ProcessId;
                pendingContinuation = ContinuationFor(pending, process.ProcessId, ref initialBreakpointHandled);
                try
                {
                    if (!own) { unexpectedPidEvents++; continue; }
                    object data;
                    switch (pending.Code)
                    {
                        case 1:
                            var ex = pending.Data.Exception;
                            data = new { code = Hex(ex.Record.Code), flags = ex.Record.Flags, first_chance = ex.FirstChance,
                                address = Address(ex.Record.Address), number_parameters = ex.Record.NumberParameters,
                                initial_breakpoint = pendingContinuation == Continue, continuation = Hex(pendingContinuation) };
                            break;
                        case 2:
                            autoHandles.Add(pending.Data.Thread.Thread);
                            data = new { start_address = Address(pending.Data.Thread.StartAddress), handle_ownership = "OS_releases_event_thread_handle_on_continued_exit_or_detach" };
                            break;
                        case 3:
                            autoHandles.Add(pending.Data.Process.Process); autoHandles.Add(pending.Data.Process.Thread);
                            data = new { image = FileEventPath(pending.Data.Process.File, workspace, loadedNames), base_address = Address(pending.Data.Process.Base),
                                event_process_thread_handles = "OS_owned_not_manually_double_closed" };
                            break;
                        case 4: data = new { exit_code = Hex(pending.Data.ExitCode) }; break;
                        case 5: exitCode = pending.Data.ExitCode; data = new { exit_code = Hex(exitCode.Value) }; break;
                        case 6:
                            data = new { image = FileEventPath(pending.Data.Dll.File, workspace, loadedNames), base_address = Address(pending.Data.Dll.Base) }; break;
                        case 7: data = new { base_address = Address(pending.Data.UnloadedBase) }; break;
                        case 8: data = new { unicode = pending.Data.String.Unicode != 0, length = pending.Data.String.Length, content = "not_read_no_process_memory_API" }; break;
                        case 9: data = new { error = pending.Data.Rip.Error, type = pending.Data.Rip.Type }; break;
                        default: data = new { unknown_native_event = pending.Code }; break;
                    }
                    events.Add(new { index = events.Count, elapsed_ms = clock.ElapsedMilliseconds, code = pending.Code, pid = pending.ProcessId, tid = pending.ThreadId, data });
                }
                finally
                {
                    IntPtr file = pending.Code == 3 ? pending.Data.Process.File : pending.Code == 6 ? pending.Data.Dll.File : IntPtr.Zero;
                    if (file != IntPtr.Zero && file != new IntPtr(-1)) { if (Native.CloseHandle(file)) fileHandlesClosed++; }
                    if (!Native.ContinueDebugEvent(pending.ProcessId, pending.ThreadId, pendingContinuation))
                    { continueError = Marshal.GetLastWin32Error(); throw new Win32Exception(continueError.Value, "CONTINUE_OWNED_DEBUG_EVENT_FAILED"); }
                    havePending = false;
                    if (own && pending.Code == 5) { exitEventContinued = true; debugActive = false; }
                }
            }
            if (noDebug)
            {
                while (true)
                {
                    uint wait = Native.WaitForSingleObject(process.Process, 250);
                    if (wait == 0) { naturalExit = true; break; }
                    if (wait != 0x102) throw Native.Error("WAIT_FOR_NO_DEBUG_CHILD_FAILED");
                    if (clock.ElapsedMilliseconds >= TimeoutMilliseconds) { timedOut = true; break; }
                }
                if (naturalExit)
                {
                    if (!Native.GetExitCodeProcess(process.Process, out uint observedExit)) throw Native.Error("NO_DEBUG_CHILD_EXIT_CODE_QUERY_FAILED");
                    exitCode = observedExit;
                }
            }
            else if (exitEventContinued) naturalExit = Native.WaitForSingleObject(process.Process, 5000) == 0;
            if ((!noDebug && !exitEventContinued) || !naturalExit)
                throw new InvalidOperationException(timedOut ? "OWNED_CHILD_OBSERVATION_TIMEOUT_INCOMPLETE" : eventLimit ? "DEBUG_CAPTURE_EVENT_LIMIT_INCOMPLETE" : "EXIT_NOT_COMPLETED");
            stage = noDebug ? "owned_no_debug_run_completed" : "native_event_capture_completed";
            result = 0; // Capture completion, never mechanism acceptance or child success.
        }
        catch (Exception error) { failure = Error(error); }
        finally
        {
            if (apiStarted && havePending)
            {
                // Never leave a received debug event intentionally suspended.
                if (Native.ContinueDebugEvent(pending.ProcessId, pending.ThreadId, pendingContinuation))
                {
                    havePending = false;
                    if (pending.ProcessId == process.ProcessId && pending.Code == 5) { exitEventContinued = true; debugActive = false; }
                }
                else continueError = Marshal.GetLastWin32Error();
            }
            if (apiStarted && debugActive)
            {
                detached = Native.DebugActiveProcessStop(process.ProcessId);
                if (detached) { debugActive = false; havePending = false; }
                else detachError = Marshal.GetLastWin32Error();
                if (!killOnExitDisabled) killOnExitDisabled = Native.DebugSetProcessKillOnExit(false);
                // The capture deadline is bounded, but never abandon a paused debuggee.
                // If detach fails, continue/retry only this same child until detached/exit;
                // do not terminate it or start any subsequent process.
                while (debugActive)
                {
                    if (havePending && Native.ContinueDebugEvent(pending.ProcessId, pending.ThreadId, pendingContinuation))
                    {
                        havePending = false;
                        if (pending.ProcessId == process.ProcessId && pending.Code == 5) debugActive = false;
                    }
                    if (debugActive && !havePending && Native.WaitForDebugEventEx(out Native.DebugEvent recovery, 250))
                    {
                        pending = recovery; havePending = true;
                        uint status = ContinuationFor(recovery, process.ProcessId, ref initialBreakpointHandled);
                        pendingContinuation = status;
                        if (recovery.ProcessId == process.ProcessId && recovery.Code == 3) { autoHandles.Add(recovery.Data.Process.Process); autoHandles.Add(recovery.Data.Process.Thread); }
                        if (recovery.ProcessId == process.ProcessId && recovery.Code == 2) autoHandles.Add(recovery.Data.Thread.Thread);
                        IntPtr file = recovery.Code == 3 ? recovery.Data.Process.File : recovery.Code == 6 ? recovery.Data.Dll.File : IntPtr.Zero;
                        if (file != IntPtr.Zero && file != new IntPtr(-1)) Native.CloseHandle(file);
                        if (Native.ContinueDebugEvent(recovery.ProcessId, recovery.ThreadId, status))
                        {
                            havePending = false;
                            if (recovery.ProcessId == process.ProcessId && recovery.Code == 5) debugActive = false;
                        }
                    }
                    if (debugActive && Native.DebugActiveProcessStop(process.ProcessId)) { detached = true; debugActive = false; havePending = false; }
                    if (debugActive && !killOnExitDisabled) killOnExitDisabled = Native.DebugSetProcessKillOnExit(false);
                }
            }
            if (process.Thread != IntPtr.Zero && !autoHandles.Contains(process.Thread)) { if (Native.CloseHandle(process.Thread)) callerHandlesClosed++; }
            if (process.Process != IntPtr.Zero && !autoHandles.Contains(process.Process)) { if (Native.CloseHandle(process.Process)) callerHandlesClosed++; }
            if (desktop is not null) desktopClosed = DesktopApi.CloseDesktop(desktop.Handle);
            if (parentStation is not null && parentDesktop is not null)
            {
                try { parentAssociationUnchanged = parentStation == DesktopNames.UserObjectName(Native.GetProcessWindowStation()) && parentDesktop == DesktopNames.UserObjectName(Native.GetThreadDesktop(Native.GetCurrentThreadId())); }
                catch (Exception error) { failure ??= Error(error); }
            }
            copy?.Dispose();
            if (source is not null && before is not null)
            {
                try { after = Lua.Read(source); parentUnchanged = JsonSerializer.Serialize(before) == JsonSerializer.Serialize(after); }
                catch (Exception error) { failure ??= Error(error); }
            }
            source?.Dispose();
            if (apiStarted && (!parentUnchanged || !parentAssociationUnchanged || !desktopClosed)) result = 1;
            JsonSerializer.Serialize(reportFile, new
            {
                schema_version = 1, kind = "fixed_owned_LPAC_synthetic_LUA_native_loader_debug_capture",
                status = result == 0 ? (noDebug ? "owned_no_debug_run_completed" : "native_capture_completed") : "incomplete_or_failed", started_utc = started, finished_utc = DateTimeOffset.UtcNow,
                stage, failure, executable = "<workspace>/tools/AutumnOS.Lpac.Probe/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.Lpac.Probe.exe", executable_sha256 = executableHash,
                native_layout = new
                {
                    debug_event_size = Marshal.SizeOf<Native.DebugEvent>(), exception_debug_info_size = Marshal.SizeOf<Native.ExceptionDebugInfo>(),
                    union_offset = Marshal.OffsetOf<Native.DebugEvent>(nameof(Native.DebugEvent.Data)).ToInt32(),
                    exception_code_offset = 16, exception_address_offset = 16 + Marshal.OffsetOf<Native.ExceptionRecord>(nameof(Native.ExceptionRecord.Address)).ToInt32(),
                    first_chance_offset = 16 + Marshal.OffsetOf<Native.ExceptionDebugInfo>(nameof(Native.ExceptionDebugInfo.FirstChance)).ToInt32(),
                    load_dll_file_offset = 16, load_dll_base_offset = 16 + Marshal.OffsetOf<Native.LoadDllDebugInfo>(nameof(Native.LoadDllDebugInfo.Base)).ToInt32(),
                    create_process_file_offset = 16, create_process_handle_offset = 16 + Marshal.OffsetOf<Native.CreateProcessDebugInfo>(nameof(Native.CreateProcessDebugInfo.Process)).ToInt32(),
                    create_thread_handle_offset = 16 + Marshal.OffsetOf<Native.CreateProcessDebugInfo>(nameof(Native.CreateProcessDebugInfo.Thread)).ToInt32(),
                    create_image_base_offset = 16 + Marshal.OffsetOf<Native.CreateProcessDebugInfo>(nameof(Native.CreateProcessDebugInfo.Base)).ToInt32()
                },
                api_started = apiStarted, child_pid = apiStarted ? (uint?)process.ProcessId : null,
                debug_enabled = !noDebug,
                create_flags = noWindow ? "CREATE_NO_WINDOW|CREATE_UNICODE_ENVIRONMENT (0x08000400)" :
                    noDebug ? "CREATE_UNICODE_ENVIRONMENT (0x400)" : "DEBUG_ONLY_THIS_PROCESS|CREATE_UNICODE_ENVIRONMENT (0x402)",
                create_flags_numeric = creationFlags, synthetic_flags = 5,
                startup_info = "STARTUPINFO", inherit_handles = false, stdout_pipe = "none", create_no_window = noWindow,
                child_report = childReport is null ? null : "<workspace>/" + Path.GetRelativePath(workspace, childReport).Replace('\\', '/'),
                child_report_created = childReport is not null && File.Exists(childReport),
                parent_before = before, copy_after_medium_before_default_dacl_repair = copyAfterMedium,
                copied_default_dacl_repaired = copiedDefaultDaclRepaired, selected_synthetic_token = selected,
                actual_child_token = actualChild, parent_after = after, parent_token_unchanged = parentUnchanged,
                not_a_real_uac_limited_user = true, real_user_product_acceptance = "not_run", clipboard_or_browser_acceptance = "not_run",
                desktop = desktop?.Name, desktop_parent_handle_closed = desktopClosed, desktop_destruction = "not_claimed", parent_desktop_association_unchanged = parentAssociationUnchanged,
                events, unexpected_pid_events_not_recorded = unexpectedPidEvents,
                timed_out = timedOut, event_limit = eventLimit, exit_event_continued = exitEventContinued, natural_exit = naturalExit,
                child_exit_hex = exitCode is null ? null : Hex(exitCode.Value), child_result = exitCode == 0xc0000142 ? "native_initialization_failed" : exitCode is null ? "incomplete" : "observe_exit_code",
                hostfxr_or_hostpolicy_load_observed = loadedNames.Contains("hostfxr.dll") || loadedNames.Contains("hostpolicy.dll"),
                coreclr_load_observed = loadedNames.Contains("coreclr.dll"), managed_entry = "not_proven_by_loader_events",
                kill_on_debugger_exit_disabled = noDebug ? (bool?)null : killOnExitDisabled, detach_confirmed = detached, detach_error = detachError, pending_received_event = havePending, continue_error = continueError,
                closed_event_file_handles = fileHandlesClosed, closed_caller_process_thread_handles = callerHandlesClosed,
                event_process_thread_handle_policy = "OS closes event handles on continued exit/detach; caller PI handles closed separately unless aliased",
                memory_read_write_context_or_PEB_access = "none", output_debug_string_content = "not_read", loader_snaps_registry_gflags = "none",
                token_changes = "only independent flags5 copy Medium IL and exactly current-user plus System GA new-object DefaultDacl", existing_object_acl_changes = "none",
                force_termination = "none", credentials_or_impersonation = "none", existing_acl_or_global_changes = "none",
                claim = noDebug ? "No-debug creation-path diagnostic for one fixed owned LPAC console probe. Natural exit and child-report presence are observations, not product UI or clipboard isolation acceptance." :
                    "Native event observations for one fixed owned child. DLL loads do not prove initialization or managed entry; debugging itself changes execution conditions."
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        Console.WriteLine(report);
        return result;
    }

    private static uint ContinuationFor(Native.DebugEvent value, uint pid, ref bool initialBreakpointHandled)
    {
        if (value.Code != 1) return Continue;
        bool initial = value.ProcessId == pid && !initialBreakpointHandled && value.Data.Exception.Record.Code == 0x80000003 && value.Data.Exception.FirstChance == 1;
        if (initial) initialBreakpointHandled = true;
        return initial ? Continue : NotHandled;
    }

    private static bool ExpectedBaseline(Facts value, Facts original) => value.token_type == 1 && value.elevation_type == 1 && !value.elevated && value.integrity_rid == 0x2000 &&
        value.has_restrictions && !value.is_token_restricted && value.restricting_sids.Length == 0 && !value.administrators_enabled && !value.ui_access && !value.sandbox_inert && !value.is_appcontainer &&
        value.owner_sid_sha256 == value.user_sid_sha256 && value.user_sid_sha256 == original.user_sid_sha256 && value.session_id == original.session_id && value.authentication_id_sha256 == original.authentication_id_sha256 &&
        !value.default_dacl.null_dacl && value.default_dacl.revision == 2 && value.default_dacl.aces.Length == 2 &&
        value.default_dacl.aces.All(a => a.ace_type == 0 && a.ace_flags == 0 && a.access_mask == 0x10000000) &&
        value.default_dacl.aces[0].sid_sha256 == value.user_sid_sha256 && value.default_dacl.aces[1].well_known_sid == "S-1-5-18" &&
        value.privileges.Length == 1 && value.privileges[0].name == "SeChangeNotifyPrivilege" && (value.privileges[0].attributes & 2) != 0;
    private static object FileEventPath(IntPtr handle, string workspace, HashSet<string> modules)
    {
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return new { path = "unavailable_null_file_handle" };
        try
        {
            string path = Native.FinalPath(handle); modules.Add(Path.GetFileName(path));
            string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
            string redacted = Within(path, workspace) ? "<workspace>/" + Path.GetRelativePath(workspace, path).Replace('\\', '/') :
                Within(path, windows) ? "<windows>/" + Path.GetRelativePath(windows, path).Replace('\\', '/') : "<external>/" + Path.GetFileName(path);
            return new { path = redacted, full_path_sha256 = Hash(path) };
        }
        catch (Exception error) { return new { path = "file_handle_path_query_failed", error = Error(error) }; }
    }
    private static object Error(Exception error) => new { type = error.GetType().Name, stage = error.Message, native_error = error is Win32Exception win ? win.NativeErrorCode : (int?)null };
    private static string Hex(uint value) => "0x" + value.ToString("X8");
    private static string Address(IntPtr value) => "0x" + unchecked((ulong)value.ToInt64()).ToString("X16");
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static bool SamePath(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string file, string root) => Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Quote(string value)
    {
        var text = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value) { if (c == '\\') { slashes++; continue; } text.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0; }
        return text.Append('\\', slashes * 2).Append('"').ToString();
    }
}

internal static class Native
{
    internal static Win32Exception Error(string stage) => new(Marshal.GetLastWin32Error(), stage);
    internal static SafeAccessTokenHandle QueryToken(IntPtr process) { if (!Lua.OpenProcessToken(process, 8, out var token)) throw Error("CHILD_TOKEN_QUERY_FAILED"); return token; }
    internal static string FinalPath(IntPtr handle)
    {
        var text = new StringBuilder(32768); uint chars = GetFinalPathNameByHandle(handle, text, (uint)text.Capacity, 0);
        if (chars == 0 || chars >= text.Capacity) throw Error("FILE_HANDLE_FINAL_PATH_FAILED");
        string path = text.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo { public int Size; public string? Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public short ShowWindow, Reserved2; public IntPtr ReservedPointer, StdInput, StdOutput, StdError; }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [StructLayout(LayoutKind.Sequential)] internal struct DebugEvent { public uint Code, ProcessId, ThreadId; public DebugData Data; }
    [StructLayout(LayoutKind.Explicit, Size = 160)] internal struct DebugData
    {
        [FieldOffset(0)] public ExceptionDebugInfo Exception;
        [FieldOffset(0)] public CreateProcessDebugInfo Process;
        [FieldOffset(0)] public CreateThreadDebugInfo Thread;
        [FieldOffset(0)] public LoadDllDebugInfo Dll;
        [FieldOffset(0)] public OutputStringInfo String;
        [FieldOffset(0)] public RipInfo Rip;
        [FieldOffset(0)] public uint ExitCode;
        [FieldOffset(0)] public IntPtr UnloadedBase;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ExceptionRecord { public uint Code, Flags; public IntPtr Nested, Address; public uint NumberParameters; public ulong I0, I1, I2, I3, I4, I5, I6, I7, I8, I9, I10, I11, I12, I13, I14; }
    [StructLayout(LayoutKind.Sequential)] internal struct ExceptionDebugInfo { public ExceptionRecord Record; public uint FirstChance; }
    [StructLayout(LayoutKind.Sequential)] internal struct CreateProcessDebugInfo { public IntPtr File, Process, Thread, Base; public uint DebugOffset, DebugSize; public IntPtr Tls, StartAddress, ImageName; public ushort Unicode; }
    [StructLayout(LayoutKind.Sequential)] internal struct CreateThreadDebugInfo { public IntPtr Thread, Tls, StartAddress; }
    [StructLayout(LayoutKind.Sequential)] internal struct LoadDllDebugInfo { public IntPtr File, Base; public uint DebugOffset, DebugSize; public IntPtr ImageName; public ushort Unicode; }
    [StructLayout(LayoutKind.Sequential)] internal struct OutputStringInfo { public IntPtr Address; public ushort Unicode, Length; }
    [StructLayout(LayoutKind.Sequential)] internal struct RipInfo { public uint Error, Type; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateProcessAsUser(SafeAccessTokenHandle token, string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInformation process);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool WaitForDebugEventEx(out DebugEvent value, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool ContinueDebugEvent(uint pid, uint tid, uint status);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool DebugSetProcessKillOnExit(bool kill);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool DebugActiveProcessStop(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr process, uint timeout);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetExitCodeProcess(IntPtr process, out uint exit);
    [DllImport("kernel32.dll")] internal static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(IntPtr handle, StringBuilder text, uint size, uint flags);
    [DllImport("user32.dll")] internal static extern IntPtr GetProcessWindowStation();
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] internal static extern IntPtr GetThreadDesktop(uint tid);
}
