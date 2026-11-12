// Explicit experimental baseline: create a new LUA copy, lower only that copy to Medium.
// Never claims a real UAC Limited user or product UI acceptance; no automatic fallback.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Legacy = AutumnOS.RestrictedTokenLaunchProbe.Native;
using TokenFacts = AutumnOS.LuaTokenProbe.TokenFacts;
using Lua = AutumnOS.LuaTokenProbe.Native;

namespace AutumnOS.SyntheticLuaLauncher;

internal static class Program
{
    private const string TrustedPowerShell = @"C:\Users\admin\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe";
    private const int MaxOutputBytes = 8 * 1024 * 1024;

    [MTAThread]
    private static int Main(string[] args)
    {
        Legacy.SetErrorMode(0x1 | 0x2 | 0x8000);
        Options options;
        try { options = Options.Parse(args); }
        catch (Exception error) { Console.Error.WriteLine(error.Message); return 2; }
        DateTimeOffset started = DateTimeOffset.UtcNow;
        string launchId = Guid.NewGuid().ToString("N");
        object? failure = null;
        TokenFacts? parent = null, originalAfter = null, copyBeforeMedium = null, copyAfterMedium = null, selected = null, actualChild = null;
        string mode = "not_selected", stage = "validate_paths";
        string? targetNotRunReason = null;
        string? targetHash = null, inputFileHash = null, resolvedExecutable = null, outputFile = null;
        bool apiStarted = false, naturallyExited = false;
        int? nativeError = null, childPid = null, childExitCode = null;
        string? childExitHex = null, childFailureClass = null;
        long outputBytes = 0;
        bool outputTruncated = false;
        string? outputError = null;
        int launcherExit = 1;
        Legacy.ProcessInfo process = default;
        SafeAccessTokenHandle? source = null, synthetic = null;
        bool syntheticClosed = false, originalUnchanged = false;
        bool copiedDefaultDaclRepaired = false;
        OwnedDesktop? privateDesktop = null;
        bool desktopHandleClosed = false, parentDesktopUnchanged = false;
        string? parentStation = null, parentDesktop = null;
        FileStream? reportLease = null, executableLease = null, scriptOrAssemblyLease = null, log = null;
        Legacy.OwnedPipes? pipes = null;
        FileStream? outputStream = null;
        Task<OutputResult>? outputTask = null;
        using var outputCancellation = new CancellationTokenSource();
        try
        {
            string workspace = Native.RealDirectory(options.Workspace);
            if (!File.Exists(Path.Combine(workspace, "AutumnOS.slnx"))) throw new InvalidDataException("WORKSPACE_SOLUTION_MISSING");
            string artifacts = Path.Combine(workspace, "artifacts");
            Directory.CreateDirectory(artifacts);
            if (!SamePath(Native.RealDirectory(artifacts), artifacts)) throw new InvalidDataException("ARTIFACTS_REPARSE_DIRECTORY_FORBIDDEN");
            string report = Path.GetFullPath(options.Report);
            if (!Within(report, artifacts)) throw new InvalidDataException("REPORT_MUST_BE_WITHIN_WORKSPACE_ARTIFACTS");
            string reportDirectory = Path.GetDirectoryName(report)!;
            string ancestor = reportDirectory;
            while (!Directory.Exists(ancestor)) ancestor = Path.GetDirectoryName(ancestor) ?? throw new InvalidDataException("REPORT_PARENT_MISSING");
            string actualAncestor = Native.RealDirectory(ancestor);
            if (!SamePath(actualAncestor, artifacts) && !Within(actualAncestor, artifacts)) throw new InvalidDataException("REPORT_PARENT_REPARSE_ESCAPES_WORKSPACE");
            Directory.CreateDirectory(reportDirectory);
            if (!Within(Native.RealDirectory(reportDirectory), artifacts)) throw new InvalidDataException("REPORT_DIRECTORY_REPARSE_ESCAPES_WORKSPACE");
            reportLease = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            outputFile = Path.Combine(reportDirectory, Path.GetFileNameWithoutExtension(report) + ".output.log");
            log = new FileStream(outputFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);

            executableLease = ReadLease(options.Executable);
            string executable = Native.RealFile(executableLease.SafeFileHandle);
            resolvedExecutable = executable;
            scriptOrAssemblyLease = ValidateTarget(workspace, executable, options.Arguments);
            targetHash = Convert.ToHexStringLower(SHA256.HashData(executableLease));
            if (scriptOrAssemblyLease is not null) inputFileHash = Convert.ToHexStringLower(SHA256.HashData(scriptOrAssemblyLease));
            string commandText = string.Join(" ", new[] { executable }.Concat(options.Arguments).Select(Quote));
            if (commandText.Length > 32766) throw new InvalidDataException("COMMAND_LINE_TOO_LONG");

            stage = "require_unrestricted_high_parent";
            if (!Lua.OpenProcessToken(Lua.GetCurrentProcess(), 0x8b, out source)) throw Lua.Error("OPEN_OWN_HIGH_PRIMARY_TOKEN_FAILED");
            parent = Lua.Read(source);
            if (parent.token_type != 1 || !parent.elevated || parent.integrity_rid != 0x3000 || parent.is_token_restricted || parent.has_restrictions ||
                !parent.administrators_enabled || parent.ui_access || parent.sandbox_inert || parent.is_appcontainer || parent.restricting_sids.Length != 0)
            {
                targetNotRunReason = "SYNTHETIC_EXPERIMENT_REQUIRES_UNRESTRICTED_HIGH_ADMIN_PRIMARY";
                throw new InvalidOperationException(targetNotRunReason);
            }
            stage = "create_explicit_independent_lua_copy";
            if (!Lua.CreateRestrictedToken(source, 5, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out synthetic))
                throw Lua.Error("CREATE_EXPLICIT_LUA_FLAGS5_COPY_FAILED");
            copyBeforeMedium = Lua.Read(synthetic);
            if (!SameIdentity(copyBeforeMedium, parent) || copyBeforeMedium.token_type != 1)
                throw new InvalidOperationException("LUA_COPY_IDENTITY_OR_PRIMARY_TYPE_MISMATCH");
            stage = "set_only_independent_copy_medium";
            Lua.SetMedium(synthetic); // The source/parent token is never passed to SetTokenInformation.
            copyAfterMedium = Lua.Read(synthetic);
            stage = "repair_only_copy_private_object_default_dacl";
            Lua.SetCopyPrivateObjectDefaultDacl(synthetic);
            copiedDefaultDaclRepaired = true;
            selected = Lua.Read(synthetic);
            if (!ExpectedSyntheticBaseline(selected, parent))
            {
                targetNotRunReason = "SYNTHETIC_LUA_BASELINE_ASSERTIONS_FAILED";
                throw new InvalidOperationException(targetNotRunReason);
            }
            mode = "explicit_synthetic_lua_flags5_medium_experiment";

            // Only the explicit independent LUA copy is changed. No credentials,
            // UAC, shell verbs, parent-token mutation or fallback to another identity.
            // Only these new pipe handles are inheritable; token handles are not.
            pipes = new Legacy.OwnedPipes();
            Legacy.StartupInfoEx startup = pipes.StartupInfo();
            string station = Native.UserObjectName(Native.GetProcessWindowStation());
            string desktop = Native.UserObjectName(Native.GetThreadDesktop(Native.GetCurrentThreadId()));
            parentStation = station; parentDesktop = desktop;
            if (options.PrivateDesktop)
            {
                stage = "create_new_low_label_owned_desktop";
                privateDesktop = PrivateDesktopNative.Create();
                startup.Startup.Desktop = station + "\\" + privateDesktop.Name;
            }
            else startup.Startup.Desktop = station + "\\" + desktop;
            var command = new StringBuilder(commandText);
            stage = "native_process_creation";
            apiStarted = Legacy.CreateProcessAsUser(synthetic, executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080000, IntPtr.Zero, workspace, ref startup, out process);
            if (!apiStarted)
            {
                nativeError = Marshal.GetLastWin32Error();
                throw new Win32Exception(nativeError.Value, "NATIVE_PROCESS_CREATION_FAILED_NO_API_OR_PRIVILEGE_FALLBACK");
            }
            childPid = (int)process.ProcessId;
            pipes.CloseParentWriteEnd();
            outputStream = new FileStream(pipes.DetachOutputRead(), FileAccess.Read);
            outputTask = PumpOutput(outputStream, log, outputCancellation.Token);
            stage = "validate_actual_child_token";
            try
            {
                using SafeAccessTokenHandle childToken = Native.QueryProcessToken(process.Process);
                actualChild = Lua.Read(childToken);
                if (!ExpectedSyntheticBaseline(actualChild, parent))
                    failure = new { type = "InvalidOperationException", stage = "ACTUAL_CHILD_TOKEN_MISMATCH_NO_FORCE_KILL", native_error = (int?)null };
            }
            catch (Exception error) { failure = Error(error); } // Still wait for this owned child; never abandon it just because metadata failed.

            stage = "wait_for_natural_exit";
            var watch = Stopwatch.StartNew();
            while (true)
            {
                uint wait = Legacy.WaitForSingleObject(process.Process, 500);
                if (wait == 0) { naturallyExited = true; break; }
                if (wait != 0x102) throw Legacy.Error("OWNED_CHILD_WAIT_FAILED");
                if (watch.Elapsed.TotalSeconds >= options.TimeoutSeconds) throw new TimeoutException("CHILD_TIMEOUT_NO_TERMINATION_OR_SUBSEQUENT_LAUNCH");
            }
            if (!Legacy.GetExitCodeProcess(process.Process, out uint nativeExit)) throw Legacy.Error("CHILD_EXIT_QUERY_FAILED");
            childExitCode = unchecked((int)nativeExit);
            childExitHex = "0x" + nativeExit.ToString("X8");
            if (nativeExit != 0) childFailureClass = nativeExit == 0xc0000142 ? "native_init_failed" : "child_nonzero_exit";
            stage = "collect_child_output";
            OutputResult output = outputTask.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            outputBytes = output.Bytes;
            outputTruncated = output.Truncated;
            outputError = output.Error;
            if (output.Truncated || output.Error is not null) throw new InvalidDataException("CHILD_OUTPUT_INCOMPLETE_OR_LIMIT_EXCEEDED");
            if (failure is not null) throw new InvalidOperationException("ACTUAL_CHILD_TOKEN_VALIDATION_FAILED");
            stage = "completed";
            launcherExit = childExitCode.Value; // Preserve the child's native exit code unchanged.
        }
        catch (Exception error) { failure ??= Error(error); }
        finally
        {
            if (outputTask is not null && !outputTask.IsCompleted)
            {
                outputCancellation.Cancel();
                try
                {
                    OutputResult output = outputTask.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
                    outputBytes = output.Bytes; outputTruncated = output.Truncated; outputError = output.Error;
                }
                catch { outputError ??= "OUTPUT_DRAIN_NOT_COMPLETED"; }
            }
            outputStream?.Dispose();
            pipes?.Dispose();
            log?.Dispose();
            if (process.Thread != IntPtr.Zero) Legacy.CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) Legacy.CloseHandle(process.Process);
            if (privateDesktop is not null)
            {
                desktopHandleClosed = PrivateDesktopNative.CloseDesktop(privateDesktop.Handle);
                if (!desktopHandleClosed) { failure ??= Error(PrivateDesktopNative.Error("OWNED_DESKTOP_HANDLE_CLOSE_FAILED")); launcherExit = 1; }
            }
            if (parentStation is not null && parentDesktop is not null)
            {
                try
                {
                    parentDesktopUnchanged = parentStation == Native.UserObjectName(Native.GetProcessWindowStation()) &&
                        parentDesktop == Native.UserObjectName(Native.GetThreadDesktop(Native.GetCurrentThreadId()));
                    if (!parentDesktopUnchanged) { failure ??= new { type = "InvalidOperationException", stage = "PARENT_DESKTOP_ASSOCIATION_CHANGED", native_error = (int?)null }; launcherExit = 1; }
                }
                catch (Exception error) { failure ??= Error(error); launcherExit = 1; }
            }
            synthetic?.Dispose();
            syntheticClosed = synthetic is not null && synthetic.IsClosed;
            if (source is not null && parent is not null)
            {
                try
                {
                    originalAfter = Lua.Read(source);
                    originalUnchanged = JsonSerializer.Serialize(parent) == JsonSerializer.Serialize(originalAfter);
                    if (!originalUnchanged) { failure ??= new { type = "InvalidOperationException", stage = "PARENT_TOKEN_FACTS_CHANGED", native_error = (int?)null }; launcherExit = 1; }
                }
                catch (Exception error) { failure ??= Error(error); launcherExit = 1; }
            }
            source?.Dispose();
            scriptOrAssemblyLease?.Dispose();
            executableLease?.Dispose();
            if (reportLease is not null)
            {
                try
                {
                    JsonSerializer.Serialize(reportLease, new
                    {
                        schema_version = 1,
                        status = stage == "completed" && failure is null ? (childExitCode == 0 ? "passed_synthetic_probe_launch_only" : childFailureClass ?? "child_failed") : "failed",
                        kind = "explicit_synthetic_lua_primary_token_owned_probe_launcher",
                        launch_id = launchId, started_utc = started, finished_utc = DateTimeOffset.UtcNow,
                        executable = options.Executable, resolved_executable = resolvedExecutable, executable_sha256 = targetHash,
                        script_or_assembly_sha256 = inputFileHash,
                        argument_count = options.Arguments.Length,
                        argument_vector_sha256 = Hash(JsonSerializer.Serialize(options.Arguments)),
                        timeout_seconds = options.TimeoutSeconds,
                        token_selection = mode, parent_token_before = parent, lua_copy_before_medium = copyBeforeMedium,
                        copy_after_medium_before_default_dacl_repair = copyAfterMedium, copied_default_dacl_repaired = copiedDefaultDaclRepaired,
                        selected_synthetic_token = selected, actual_child_token = actualChild,
                        synthetic_copy_closed = syntheticClosed, parent_token_after_copy_closed = originalAfter, parent_token_facts_unchanged = originalUnchanged,
                        requested_flags = 5, requested_restricting_sids = 0,
                        not_a_real_uac_limited_user = true, real_ordinary_user_acceptance = "not_run", product_ui_acceptance = "not_run",
                        private_desktop_requested = options.PrivateDesktop,
                        private_desktop = privateDesktop is null ? null : new
                        {
                            name = privateDesktop.Name, descriptor_sha256 = privateDesktop.DescriptorSha256,
                            name_and_descriptor_readback_verified = true,
                            owner_full_access = "current_user", restricted_app_packages_sid = "S-1-15-2-2", restricted_app_packages_access = 0x200ff,
                            mandatory_label = "S-1-16-4096", mandatory_label_policy = "NW",
                            parent_handle_closed = desktopHandleClosed, object_destruction = "not_claimed",
                            child_still_alive_or_exit_unconfirmed = apiStarted && !naturallyExited
                        },
                        parent_station = parentStation, parent_desktop = parentDesktop, parent_desktop_association_unchanged = parentDesktopUnchanged,
                        target_execution = apiStarted ? "started" : "not_run",
                        target_not_run_reason = apiStarted ? null : targetNotRunReason ?? "LAUNCHER_FAILED_BEFORE_PROCESS_CREATION",
                        stage, api_started = apiStarted, api_error = nativeError, child_pid = childPid,
                        natural_exit = naturallyExited, child_exit_code = childExitCode, launcher_exit_code = launcherExit,
                        child_exit_hex = childExitHex, child_failure_class = childFailureClass, child_output_empty = outputBytes == 0,
                        combined_output = outputFile, output_bytes = outputBytes, output_truncated = outputTruncated, output_error = outputError,
                        failure,
                        scope = "Only an explicitly requested owned mechanism probe uses a newly-created synthetic LUA Medium baseline. An explicitly selected new desktop is an initialization hypothesis, not an established root cause or isolation boundary. This is not a real UAC Limited user or product acceptance run.",
                        token_changes = "only the newly-created independent flags5 copy receives Medium IL and a private new-object DefaultDacl containing exactly current-user and System GA",
                        parent_token_changes_requested = "none", existing_object_acl_changes = "none", credentials = "none", uac = "none", impersonation = "none", global_changes = "none",
                        shell_command_or_encoded_command = "forbidden", inherited_handles = "two newly-created anonymous-pipe handles only",
                        kill_or_breakaway = "none", fallback_on_denial = "none",
                        caller_authorization = "The exact target action must be separately authorized by the invoking tool/session; this launcher does not bypass approval."
                    }, new JsonSerializerOptions { WriteIndented = true });
                }
                catch (Exception error) { Console.Error.WriteLine("LAUNCHER_REPORT_WRITE_FAILED: " + error.GetType().Name); launcherExit = 1; }
                finally { reportLease.Dispose(); }
            }
            else if (failure is not null) Console.Error.WriteLine(JsonSerializer.Serialize(failure));
        }
        Console.WriteLine(options.Report);
        return launcherExit;
    }

    private static FileStream? ValidateTarget(string workspace, string executable, string[] arguments)
    {
        string projectDotnet = Path.Combine(workspace, ".tools", "dotnet", "dotnet.exe");
        string projectPowerShell = Path.Combine(workspace, ".tools", "pwsh-ci", "pwsh.exe");
        if (SamePath(executable, projectDotnet))
        {
            if (arguments.Length == 0 || !Path.IsPathFullyQualified(arguments[0]) || !arguments[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("DOTNET_ONLY_ACCEPTS_EXPLICIT_WORKSPACE_AUTUMNOS_ASSEMBLY");
            FileStream assembly = ReadLease(arguments[0]);
            try
            {
                string real = Native.RealFile(assembly.SafeFileHandle);
                RequireOwnBinary(workspace, real, ".dll");
                arguments[0] = real;
                return assembly;
            }
            catch { assembly.Dispose(); throw; }
        }
        if (SamePath(executable, projectPowerShell) || SamePath(executable, TrustedPowerShell))
        {
            string[] prefix = ["-NoLogo", "-NoProfile", "-NonInteractive", "-File"];
            if (arguments.Length < 5 || !arguments.Take(4).SequenceEqual(prefix, StringComparer.OrdinalIgnoreCase) ||
                !Path.IsPathFullyQualified(arguments[4]) || !arguments[4].EndsWith(".ps1", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("POWERSHELL_ONLY_ACCEPTS_NOLOGO_NOPROFILE_NONINTERACTIVE_FILE_WORKSPACE_SCRIPT");
            if (arguments.Any(a => a.Equals("-Command", StringComparison.OrdinalIgnoreCase) || a.Equals("-EncodedCommand", StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException("SHELL_COMMAND_ARGUMENT_FORBIDDEN");
            FileStream script = ReadLease(arguments[4]);
            try
            {
                string real = Native.RealFile(script.SafeFileHandle);
                string[] nonUiScripts = ["Test-ExecutionContext.ps1", "Test-IsolationMechanisms.ps1", "Test.ps1"];
                bool allowedScript = Within(real, Path.Combine(workspace, "research")) ||
                    nonUiScripts.Any(name => SamePath(real, Path.Combine(workspace, "scripts", name)));
                if (!allowedScript)
                    throw new InvalidDataException("SYNTHETIC_BASELINE_ONLY_ALLOWS_RESEARCH_OR_EXPLICIT_NON_UI_SCRIPTS");
                arguments[4] = real;
                return script;
            }
            catch { script.Dispose(); throw; }
        }
        RequireOwnBinary(workspace, executable, ".exe");
        return null;
    }

    private static void RequireOwnBinary(string workspace, string path, string extension)
    {
        string fileName = Path.GetFileName(path);
        bool researchProbe = Within(path, Path.Combine(workspace, "research"));
        bool lpacProbe = SamePath(path, Path.Combine(workspace, "tools", "AutumnOS.Lpac.Probe", "bin", "x64", "Release", "net10.0-windows", "win-x64", "AutumnOS.Lpac.Probe" + extension));
        if (!(fileName.StartsWith("AutumnOS.", StringComparison.OrdinalIgnoreCase) || fileName.Equals("AutumnOS" + extension, StringComparison.OrdinalIgnoreCase)) ||
            !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
            !(researchProbe || lpacProbe) ||
            FileVersionInfo.GetVersionInfo(path).ProductName != "Lab Chronicles AutumnOS")
            throw new InvalidDataException("SYNTHETIC_BASELINE_ONLY_ALLOWS_RESEARCH_PROBES_OR_EXACT_LPAC_PROBE");
    }

    private static FileStream ReadLease(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("TARGET_PATH_MUST_BE_ABSOLUTE");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    private static bool ExpectedSyntheticBaseline(TokenFacts value, TokenFacts original) =>
        value.token_type == 1 && value.elevation_type == 1 && !value.elevated && value.integrity_rid == 0x2000 &&
        value.has_restrictions && !value.is_token_restricted && value.restricting_sids.Length == 0 &&
        !value.administrators_enabled && !value.ui_access && !value.sandbox_inert && !value.is_appcontainer &&
        value.owner_sid_sha256 == value.user_sid_sha256 && SameIdentity(value, original) &&
        value.authentication_id_sha256 == original.authentication_id_sha256 && value.mandatory_policy == original.mandatory_policy &&
        value.privileges.Length == 1 && value.privileges[0].name == "SeChangeNotifyPrivilege" && (value.privileges[0].attributes & 2) != 0 &&
        !value.default_dacl.null_dacl && value.default_dacl.revision == 2 && value.default_dacl.aces.Length == 2 &&
        value.default_dacl.aces.All(a => a.ace_type == 0 && a.ace_flags == 0 && a.access_mask == 0x10000000) &&
        value.default_dacl.aces[0].sid_sha256 == value.user_sid_sha256 && value.default_dacl.aces[1].well_known_sid == "S-1-5-18" &&
        value.groups.Any(g => g.well_known_types.Contains("BuiltinAdministratorsSid")) &&
        value.groups.Where(g => g.well_known_types.Contains("BuiltinAdministratorsSid")).All(g => (g.attributes & 0x10) != 0 && (g.attributes & 4) == 0);
    private static bool SameIdentity(TokenFacts a, TokenFacts b) => a.user_sid_sha256 == b.user_sid_sha256 && a.session_id == b.session_id;
    private static bool SamePath(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string file, string directory) => Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static object Error(Exception error) => new
    {
        type = error.GetType().Name, stage = error.Message,
        native_error = error is Win32Exception win ? win.NativeErrorCode : (int?)null,
        desktop_descriptor_difference = error is DesktopDescriptorMismatchException mismatch ? mismatch.Diagnostic : null
    };
    private static string Quote(string value)
    {
        if (value.Contains('\0')) throw new InvalidDataException("ARGUMENT_CONTAINS_NUL");
        var result = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
    private sealed record OutputResult(long Bytes, bool Truncated, string? Error);
    private static async Task<OutputResult> PumpOutput(Stream source, Stream destination, CancellationToken cancellation)
    {
        long total = 0;
        byte[] buffer = new byte[8192];
        try
        {
            while (true)
            {
                int count = await source.ReadAsync(buffer, cancellation);
                if (count == 0) return new(total, total > MaxOutputBytes, null);
                int keep = (int)Math.Min(count, Math.Max(0, MaxOutputBytes - total));
                if (keep > 0) await destination.WriteAsync(buffer.AsMemory(0, keep), cancellation);
                total += count;
            }
        }
        catch (Exception error) { return new(total, total > MaxOutputBytes, error.GetType().Name); }
    }

    private sealed record Options(string Workspace, string Report, string Executable, int TimeoutSeconds, bool PrivateDesktop, string[] Arguments)
    {
        internal static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            int index = 0;
            bool privateDesktop = false;
            for (; index < args.Length && args[index] != "--";)
            {
                if (args[index] == "--private-desktop")
                {
                    if (privateDesktop) throw new ArgumentException("DUPLICATE_PRIVATE_DESKTOP_FLAG");
                    privateDesktop = true; index++; continue;
                }
                if (index + 1 >= args.Length || args[index] is not ("--workspace" or "--report" or "--executable" or "--timeout-seconds") || !values.TryAdd(args[index], args[index + 1]))
                    throw new ArgumentException("EXPECTED_UNIQUE_WORKSPACE_REPORT_EXECUTABLE_TIMEOUT_OPTIONS_THEN_DOUBLE_DASH_ARGUMENTS");
                index += 2;
            }
            if (index >= args.Length || !values.TryGetValue("--workspace", out string? workspace) || !values.TryGetValue("--report", out string? report) ||
                !values.TryGetValue("--executable", out string? executable) || !Path.IsPathFullyQualified(workspace) || !Path.IsPathFullyQualified(report) || !Path.IsPathFullyQualified(executable))
                throw new ArgumentException("ABSOLUTE_WORKSPACE_REPORT_EXECUTABLE_AND_DOUBLE_DASH_REQUIRED");
            int seconds = 180;
            if (values.TryGetValue("--timeout-seconds", out string? timeout) && (!int.TryParse(timeout, out seconds) || seconds is < 1 or > 900))
                throw new ArgumentException("TIMEOUT_SECONDS_MUST_BE_1_TO_900");
            string[] arguments = args[(index + 1)..];
            if (arguments.Length > 128) throw new ArgumentException("TOO_MANY_ARGUMENTS");
            return new(workspace, report, executable, seconds, privateDesktop, arguments);
        }
    }
}

internal static class Native
{
    internal static SafeAccessTokenHandle QueryProcessToken(IntPtr process)
    {
        if (!OpenProcessToken(process, 8, out SafeAccessTokenHandle token)) throw Legacy.Error("ACTUAL_CHILD_TOKEN_QUERY_FAILED");
        return token;
    }
    internal static string RealDirectory(string path)
    {
        using SafeFileHandle handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw Legacy.Error("DIRECTORY_RESOLUTION_FAILED");
        return RealFile(handle);
    }
    internal static string RealFile(SafeFileHandle handle)
    {
        var text = new StringBuilder(32768);
        uint size = GetFinalPathNameByHandle(handle, text, (uint)text.Capacity, 0);
        if (size == 0 || size >= text.Capacity) throw Legacy.Error("FINAL_PATH_RESOLUTION_FAILED");
        string path = text.ToString();
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) path = path[4..];
        return Path.GetFullPath(path);
    }
    internal static string UserObjectName(IntPtr handle)
    {
        var text = new StringBuilder(512);
        if (handle == IntPtr.Zero || !GetUserObjectInformation(handle, 2, text, text.Capacity * 2, out _)) throw Legacy.Error("CALLER_DESKTOP_NAME_QUERY_FAILED");
        return text.ToString();
    }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetProcessWindowStation();
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int size, out int required);
}


