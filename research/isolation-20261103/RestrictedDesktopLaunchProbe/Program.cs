// Owned console-process mechanism experiment. Not a browser host or clipboard test.
// Only the new desktop receives an explicit DACL, at creation; existing objects are unchanged.
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Legacy = AutumnOS.RestrictedTokenLaunchProbe.Native;
using TokenFacts = AutumnOS.RestrictedTokenLaunchProbe.TokenFacts;

namespace AutumnOS.RestrictedDesktopLaunchProbe;

internal static class Program
{
    private const uint RestrictedFlags = 0x9; // DISABLE_MAX_PRIVILEGE | WRITE_RESTRICTED.
    private const uint DesktopCreateWindow = 0x2;
    private const uint DesktopProbeRights = 0x200ff; // READ_CONTROL + desktop rights, except SWITCHDESKTOP.

    [MTAThread]
    private static int Main(string[] args)
    {
        Legacy.SetErrorMode(0x1 | 0x2 | 0x8000);
        if (args.Length == 7 && args[0] == "--child")
            return Child(args[1], args[2], args[3], args[4], args[5], args[6]);
        if (args.Length != 2 || args[0] != "--report") return 2;
        string report = Path.GetFullPath(args[1]);
        if (File.Exists(report)) return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var cases = new List<CaseResult>();
        object? failure = null;
        TokenFacts? original = null, restrictedFacts = null;
        string? station = null, parentDesktop = null, targetDesktop = null;
        IntPtr desktop = IntPtr.Zero;
        bool desktopClosed = false;
        string? restrictionSid = null;
        int result = 1;
        try
        {
            using SafeAccessTokenHandle own = Legacy.OpenOwnToken(0xb); // QUERY | DUPLICATE | ASSIGN_PRIMARY.
            original = Legacy.ReadToken(own);
            if (original.token_type != 1 || original.elevated || original.integrity_rid != 0x2000 || original.is_restricted)
                throw new InvalidOperationException("PARENT_MUST_BE_UNRESTRICTED_ORDINARY_MEDIUM_PRIMARY");
            station = DesktopNative.Name(DesktopNative.GetProcessWindowStation());
            parentDesktop = DesktopNative.Name(DesktopNative.GetThreadDesktop(DesktopNative.GetCurrentThreadId()));
            if (!station.Equals("WinSta0", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("INTERACTIVE_WINSTA0_REQUIRED_NO_ASSOCIATION_CHANGE");

            byte[] random = RandomNumberGenerator.GetBytes(12);
            restrictionSid = "S-1-5-21-" + string.Join("-", Enumerable.Range(0, 3).Select(i => BitConverter.ToUInt32(random, i * 4))) + "-1001";
            _ = new SecurityIdentifier(restrictionSid); // Syntax only, no account registration or lookup.
            using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
            string ownerSid = identity.User?.Value ?? throw new InvalidOperationException("OWN_USER_SID_MISSING");
            targetDesktop = "AutumnOS_RestrictedProbe_" + Guid.NewGuid().ToString("N");
            string sddl = "O:" + ownerSid + "G:" + ownerSid + "D:P(A;;GA;;;" + ownerSid + ")(A;;0x000200ff;;;" + restrictionSid + ")";
            desktop = DesktopNative.CreateOwnedDesktop(targetDesktop, sddl);

            CaseResult a = RunCase("A_ordinary", null, original, restrictionSid, station, targetDesktop);
            cases.Add(a);
            if (!a.passed) throw new InvalidOperationException("ORDINARY_CHILD_POSITIVE_CONTROL_FAILED_NO_RESTRICTED_LAUNCH");
            if (!Legacy.ConvertStringSidToSid(restrictionSid, out IntPtr sid)) throw Legacy.Error("SYNTHETIC_SID_PARSE_FAILED");
            SafeAccessTokenHandle restricted;
            try
            {
                var item = new Legacy.SidAndAttributes { Sid = sid, Attributes = 0 };
                if (!Legacy.CreateRestrictedToken(own, RestrictedFlags, 0, IntPtr.Zero, 0, IntPtr.Zero, 1, ref item, out restricted))
                    throw Legacy.Error("RESTRICTED_TOKEN_CREATE_FAILED");
            }
            finally { Legacy.LocalFree(sid); }
            using (restricted)
            {
                restrictedFacts = Legacy.ReadToken(restricted);
                if (!ExpectedToken(restrictedFacts, original, restrictionSid)) throw new InvalidOperationException("CREATED_RESTRICTION_MISMATCH");
                CaseResult b = RunCase("B_restricted", restricted, original, restrictionSid, station, targetDesktop);
                cases.Add(b);
                if (!b.passed) throw new InvalidOperationException("RESTRICTED_CHILD_METADATA_OR_ACCESS_CHECK_FAILED");
            }
            if (station != DesktopNative.Name(DesktopNative.GetProcessWindowStation()) || parentDesktop != DesktopNative.Name(DesktopNative.GetThreadDesktop(DesktopNative.GetCurrentThreadId())))
                throw new InvalidOperationException("PARENT_STATION_OR_DESKTOP_CHANGED");
            if (JsonSerializer.Serialize(original) != JsonSerializer.Serialize(Legacy.ReadToken(own)))
                throw new InvalidOperationException("PARENT_TOKEN_CHANGED");
            result = 0;
        }
        catch (Exception error) { failure = Failure(error); }
        finally
        {
            if (desktop != IntPtr.Zero)
            {
                desktopClosed = DesktopNative.CloseDesktop(desktop);
                if (!desktopClosed) { result = 1; failure ??= Failure(DesktopNative.Error("OWNED_DESKTOP_CLOSE_FAILED")); }
            }
        }
        using (var file = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(file, new
            {
                schema_version = 1,
                status = result == 0 ? "passed_mechanism_only" : "failed",
                kind = "owned_console_restricted_token_explicit_new_desktop",
                started_utc = started, finished_utc = DateTimeOffset.UtcNow,
                os = Environment.OSVersion.VersionString,
                requested_token_flags = RestrictedFlags,
                restriction_sid_sha256 = restrictionSid is null ? null : Hash(restrictionSid),
                original_token = original, created_restricted_token = restrictedFacts,
                station, parent_desktop = parentDesktop, target_desktop = targetDesktop,
                new_desktop_acl = new { owner = "current_user", restriction_sid_access = DesktopProbeRights, existing_acl_changes = "none", creation_only = true },
                cases, failure, parent_desktop_handle_closed = desktopClosed,
                clipboard_api_calls = "none", browser = "not_run", user_interface = "not_run",
                clipboard_isolation_acceptance = "not_run",
                scope = "Child initialization and actual token/desktop/handle metadata only. WRITE_RESTRICTED allows ordinary reads; this is not a complete filesystem or application sandbox.",
                global_changes = "none", uac = "none", impersonation = "none", inherited_handles = "two anonymous-pipe handles only",
                desktop_switch = "none", kill_or_breakaway = "none", restriction_fallback = "none"
            }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(report);
        return result;
    }

    private static bool ExpectedToken(TokenFacts value, TokenFacts original, string sid) =>
        value.token_type == 1 && !value.elevated && value.integrity_rid == 0x2000 && value.is_restricted &&
        value.user_sid_sha256 == original.user_sid_sha256 && value.session_id == original.session_id &&
        value.restricting_sids.SequenceEqual(new[] { sid }) && value.enabled_privileges.All(p => p == "SeChangeNotifyPrivilege");

    private static int Child(string nonce, string label, string sid, string station, string desktop, string expectedParent)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(nonce, "\\A[0-9a-f]{32}\\z") || label is not ("A_ordinary" or "B_restricted") ||
            !int.TryParse(expectedParent, out int parent) || parent <= 0 || !desktop.StartsWith("AutumnOS_RestrictedProbe_", StringComparison.Ordinal)) return 2;
        try
        {
            _ = new SecurityIdentifier(sid);
            using var token = Legacy.OpenOwnToken(8);
            TokenFacts facts = Legacy.ReadToken(token);
            string actualStation = DesktopNative.Name(DesktopNative.GetProcessWindowStation());
            string actualDesktop = DesktopNative.Name(DesktopNative.GetThreadDesktop(DesktopNative.GetCurrentThreadId()));
            if (facts.elevated || facts.token_type != 1 || facts.integrity_rid != 0x2000 || actualStation != station || actualDesktop != desktop)
                throw new InvalidOperationException("CHILD_IDENTITY_OR_STARTUP_DESKTOP_MISMATCH");
            HandleObservation attributes = DesktopNative.OpenStation(station, 2);
            HandleObservation clipboard = DesktopNative.OpenStation(station, 4);
            HandleObservation defaultCreate = DesktopNative.OpenDesktopForWindowCreation();
            HandleObservation ownDesktop = DesktopNative.OpenOwnDesktop(desktop, DesktopProbeRights);
            bool expected = attributes.opened && ownDesktop.opened;
            if (label == "B_restricted")
                expected &= facts.is_restricted && facts.restricting_sids.SequenceEqual(new[] { sid }) && facts.enabled_privileges.All(p => p == "SeChangeNotifyPrivilege") &&
                    !clipboard.opened && clipboard.error == 5 && !defaultCreate.opened && defaultCreate.error == 5;
            else expected &= !facts.is_restricted && clipboard.opened && defaultCreate.opened;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                schema_version = 1, status = expected ? "metadata_complete" : "metadata_or_expected_access_failed",
                nonce, label, pid = Environment.ProcessId, expected_parent_pid = parent,
                token = facts, station = actualStation, desktop = actualDesktop,
                readattributes_handle = attributes, clipboard_access_handle = clipboard,
                default_desktop_createwindow_handle = defaultCreate, own_desktop_handle = ownDesktop,
                clipboard_api_calls = "none", gui_or_browser = "never_initialized"
            }));
            return expected ? 0 : 1;
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", nonce, label, pid = Environment.ProcessId, failure = Failure(error) }));
            return 1;
        }
    }

    private static CaseResult RunCase(string label, SafeAccessTokenHandle? restricted, TokenFacts original, string sid, string station, string desktop)
    {
        string nonce = Guid.NewGuid().ToString("N"), executable = Environment.ProcessPath!;
        if (Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("APPHOST_REQUIRED");
        Legacy.ProcessInfo child = default;
        bool started = false, naturalExit = false;
        int? exitCode = null, nativeError = null;
        string? exitHex = null;
        uint? waitResult = null;
        int? waitError = null;
        long outputBytes = 0;
        bool outputTruncated = false;
        string? outputError = null;
        JsonElement? metadata = null;
        string stage = "pipe_setup";
        object? failure = null;
        using var pipes = new Legacy.OwnedPipes();
        using var outputCancellation = new CancellationTokenSource();
        FileStream? outputStream = null;
        Task<BoundedOutput>? outputTask = null;
        CaseResult Result(bool passed) => new(label, passed, started, nativeError, started ? (int?)child.ProcessId : null, exitCode, naturalExit,
            stage, metadata, failure, exitHex, waitResult, waitError, outputBytes, outputTruncated, outputError);
        try
        {
            Legacy.StartupInfoEx startup = pipes.StartupInfo();
            startup.Startup.Desktop = station + "\\" + desktop;
            var command = new StringBuilder(string.Join(" ", new[] { executable, "--child", nonce, label, sid, station, desktop, Environment.ProcessId.ToString() }.Select(Quote)));
            stage = "native_process_creation";
            started = restricted is null
                ? Legacy.CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080000, IntPtr.Zero, null, ref startup, out child)
                : Legacy.CreateProcessAsUser(restricted, executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080000, IntPtr.Zero, null, ref startup, out child);
            if (!started)
            {
                nativeError = Marshal.GetLastWin32Error();
                failure = new { reason = "native_launch_failed_no_fallback" };
                return Result(false);
            }
            pipes.CloseParentWriteEnd();
            outputStream = new FileStream(pipes.DetachOutputRead(), FileAccess.Read);
            outputTask = ReadBoundedOutput(outputStream, outputCancellation.Token);
            stage = "waiting_for_natural_exit";
            waitResult = Legacy.WaitForSingleObject(child.Process, 30000);
            if (waitResult == 0x102)
            {
                stage = "child_wait_timeout";
                throw new TimeoutException("CHILD_DID_NOT_EXIT_NO_FORCE_KILL");
            }
            if (waitResult == 0xffffffff)
            {
                waitError = Marshal.GetLastWin32Error();
                stage = "child_wait_native_failure";
                throw new Win32Exception(waitError.Value, "CHILD_WAIT_FAILED");
            }
            if (waitResult != 0)
            {
                stage = "child_wait_unexpected_result";
                throw new InvalidOperationException("CHILD_WAIT_UNEXPECTED_RESULT_0x" + waitResult.Value.ToString("X8"));
            }
            naturalExit = true;
            if (!Legacy.GetExitCodeProcess(child.Process, out uint actualExit)) throw Legacy.Error("CHILD_EXIT_QUERY_FAILED");
            exitCode = unchecked((int)actualExit);
            exitHex = "0x" + actualExit.ToString("X8");
            if (actualExit != 0)
            {
                stage = actualExit == 0xc0000142 ? "child_native_initialization_failed" : "child_nonzero_exit";
                failure = new
                {
                    reason = actualExit == 0xc0000142 ? "CHILD_NATIVE_INITIALIZATION_FAILED" : "CHILD_EXITED_NONZERO",
                    native_exit_hex = exitHex,
                    interpretation = actualExit == 0xc0000142
                        ? "Native DLL/process initialization failed; this does not identify a DLL or prove a desktop/access-denial root cause."
                        : "The owned child exited unsuccessfully; no mechanism acceptance is inferred."
                };
            }
            else stage = "collect_child_output";
            BoundedOutput output = outputTask.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            outputBytes = output.TotalBytes;
            outputTruncated = output.Truncated;
            outputError = output.Error;
            if (failure is not null)
            {
                // A native startup failure commonly has no managed JSON. Preserve that
                // exit as the primary diagnosis instead of replacing it with JsonException.
                if (!outputTruncated && outputError is null && output.Bytes.Length > 0)
                {
                    try { using var errorDocument = JsonDocument.Parse(output.Bytes); metadata = errorDocument.RootElement.Clone(); }
                    catch (JsonException) { /* Optional diagnostic metadata; never masks native failure. */ }
                }
                return Result(false);
            }
            if (outputTruncated) throw new InvalidDataException("CHILD_OUTPUT_LIMIT_EXCEEDED");
            if (outputError is not null) throw new InvalidDataException("CHILD_OUTPUT_READ_FAILED_" + outputError);
            if (output.Bytes.Length == 0)
            {
                stage = "child_metadata_missing_after_zero_exit";
                throw new InvalidDataException("CHILD_EXITED_ZERO_WITHOUT_METADATA");
            }
            stage = "validating_actual_child_metadata";
            using var doc = JsonDocument.Parse(output.Bytes);
            metadata = doc.RootElement.Clone();
            JsonElement data = metadata.Value;
            TokenFacts facts = data.GetProperty("token").Deserialize<TokenFacts>() ?? throw new InvalidDataException("CHILD_TOKEN_MISSING");
            bool valid = exitCode == 0 && data.GetProperty("status").GetString() == "metadata_complete" &&
                data.GetProperty("nonce").GetString() == nonce && data.GetProperty("label").GetString() == label &&
                data.GetProperty("pid").GetUInt32() == child.ProcessId && data.GetProperty("expected_parent_pid").GetInt32() == Environment.ProcessId &&
                data.GetProperty("station").GetString() == station && data.GetProperty("desktop").GetString() == desktop &&
                facts.user_sid_sha256 == original.user_sid_sha256 && facts.session_id == original.session_id;
            valid &= restricted is null ? !facts.is_restricted : ExpectedToken(facts, original, sid);
            if (!valid) throw new InvalidDataException("CHILD_IDENTITY_TOKEN_OR_COMPLETION_REJECTED");
            stage = "metadata_validated";
            return Result(true);
        }
        catch (Exception error) { failure ??= Failure(error); }
        finally
        {
            if (outputTask is not null && !outputTask.IsCompleted)
            {
                outputCancellation.Cancel();
                try
                {
                    BoundedOutput output = outputTask.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    outputBytes = output.TotalBytes; outputTruncated = output.Truncated; outputError = output.Error;
                }
                catch { outputError ??= "OUTPUT_DRAIN_NOT_COMPLETED"; }
            }
            outputStream?.Dispose();
            if (child.Thread != IntPtr.Zero) Legacy.CloseHandle(child.Thread);
            if (child.Process != IntPtr.Zero) Legacy.CloseHandle(child.Process);
        }
        return Result(false);
    }

    private sealed record CaseResult(string label, bool passed, bool api_started, int? api_error, int? child_pid, int? child_exit_code,
        bool natural_exit, string stage, JsonElement? child_metadata, object? failure, string? child_exit_hex, uint? wait_result,
        int? wait_error, long output_bytes, bool output_truncated, string? output_error);
    private sealed record BoundedOutput(byte[] Bytes, long TotalBytes, bool Truncated, string? Error);
    private static async Task<BoundedOutput> ReadBoundedOutput(Stream source, CancellationToken cancellation)
    {
        const int limit = 65536;
        using var captured = new MemoryStream();
        byte[] buffer = new byte[4096];
        long total = 0;
        try
        {
            while (true)
            {
                int count = await source.ReadAsync(buffer, cancellation);
                if (count == 0) return new(captured.ToArray(), total, total > limit, null);
                int keep = (int)Math.Min(count, Math.Max(0, limit - total));
                if (keep > 0) captured.Write(buffer, 0, keep);
                total += count; // Drain while retaining no more than 64 KiB.
            }
        }
        catch (Exception error) { return new(captured.ToArray(), total, total > limit, error.GetType().Name); }
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static object Failure(Exception error) => new { type = error.GetType().Name, stage = error.Message, native_error = error is Win32Exception w ? w.NativeErrorCode : (int?)null };
    private static string Quote(string value)
    {
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}

internal sealed record HandleObservation(bool opened, uint requested_access, uint? granted_access, int? error);

internal static class DesktopNative
{
    internal static Win32Exception Error(string stage) => new(Marshal.GetLastWin32Error(), stage);
    internal static string Name(IntPtr handle)
    {
        var text = new StringBuilder(512);
        if (handle == IntPtr.Zero || !GetUserObjectInformation(handle, 2, text, text.Capacity * 2, out _)) throw Error("USER_OBJECT_NAME_QUERY_FAILED");
        return text.ToString();
    }
    internal static IntPtr CreateOwnedDesktop(string name, string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out IntPtr descriptor, out _)) throw Error("NEW_DESKTOP_DESCRIPTOR_PARSE_FAILED");
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor, Inherit = false };
            IntPtr result = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0xf01ff, ref attributes);
            if (result == IntPtr.Zero) throw Error("NEW_DESKTOP_CREATE_FAILED");
            try
            {
                // CreateDesktop can open a pre-existing name. A random name plus exact
                // owner/group/DACL readback avoids treating an unexpected object as ours.
                uint information = 7; // OWNER | GROUP | DACL, never SACL.
                GetUserObjectSecurity(result, ref information, IntPtr.Zero, 0, out uint bytes);
                if (bytes == 0 || bytes > 1048576) throw Error("NEW_DESKTOP_DESCRIPTOR_SIZE_FAILED");
                IntPtr memory = Marshal.AllocHGlobal(checked((int)bytes));
                try
                {
                    if (!GetUserObjectSecurity(result, ref information, memory, bytes, out uint actual) || actual > bytes)
                        throw Error("NEW_DESKTOP_DESCRIPTOR_READBACK_FAILED");
                    byte[] value = new byte[bytes];
                    Marshal.Copy(memory, value, 0, value.Length);
                    var sections = AccessControlSections.Owner | AccessControlSections.Group | AccessControlSections.Access;
                    string expected = new RawSecurityDescriptor(sddl).GetSddlForm(sections);
                    string observed = new RawSecurityDescriptor(value, 0).GetSddlForm(sections);
                    if (expected != observed) throw new InvalidOperationException("NEW_DESKTOP_DESCRIPTOR_MISMATCH");
                }
                finally { Marshal.FreeHGlobal(memory); }
                return result;
            }
            catch { CloseDesktop(result); throw; }
        }
        finally { Legacy.LocalFree(descriptor); }
    }
    internal static HandleObservation OpenStation(string station, uint access)
    {
        IntPtr handle = OpenWindowStation(station, false, access);
        if (handle == IntPtr.Zero) return new(false, access, null, Marshal.GetLastWin32Error());
        try { return Observe(handle, access); }
        finally { CloseWindowStation(handle); }
    }
    internal static HandleObservation OpenDesktopForWindowCreation() => OpenOwnDesktop("Default", 2);
    internal static HandleObservation OpenOwnDesktop(string desktop, uint access)
    {
        IntPtr handle = OpenDesktop(desktop, 0, false, access);
        if (handle == IntPtr.Zero) return new(false, access, null, Marshal.GetLastWin32Error());
        try { return Observe(handle, access); }
        finally { CloseDesktop(handle); }
    }
    private static HandleObservation Observe(IntPtr handle, uint access)
    {
        int status = NtQueryObject(handle, 0, out ObjectBasic basic, (uint)Marshal.SizeOf<ObjectBasic>(), out _);
        if (status != 0) throw new InvalidOperationException("OPENED_HANDLE_MASK_QUERY_FAILED_0x" + unchecked((uint)status).ToString("X8"));
        if ((basic.GrantedAccess & access) != access) throw new InvalidOperationException("OPENED_HANDLE_REQUEST_NOT_GRANTED");
        return new(true, access, basic.GrantedAccess, null);
    }
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectBasic { public uint Attributes, GrantedAccess, HandleCount, PointerCount; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)] public uint[] Reserved; }
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetProcessWindowStation();
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int required);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetUserObjectSecurity(IntPtr handle, ref uint information, IntPtr descriptor, uint size, out uint required);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, ref SecurityAttributes attributes);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenWindowStation(string name, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseWindowStation(IntPtr station);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenDesktop(string name, uint flags, bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("ntdll.dll")] private static extern int NtQueryObject(IntPtr handle, int kind, out ObjectBasic info, uint size, out uint required);
}
