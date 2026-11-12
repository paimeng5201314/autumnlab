// Local test launcher: use an existing same-user linked Medium token, never alter a token.
// This executable is not an approval boundary: callers must authorize the exact target action.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Legacy = AutumnOS.RestrictedTokenLaunchProbe.Native;
using TokenFacts = AutumnOS.RestrictedTokenLaunchProbe.TokenFacts;

namespace AutumnOS.StandardUserLauncher;

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
        TokenFacts? parent = null, selected = null, actualChild = null;
        string mode = "not_selected", stage = "validate_paths";
        int? parentElevationType = null, linkedElevationType = null;
        string? targetNotRunReason = null;
        string? targetHash = null, inputFileHash = null, resolvedExecutable = null, outputFile = null;
        bool apiStarted = false, naturallyExited = false;
        int? nativeError = null, childPid = null, childExitCode = null;
        long outputBytes = 0;
        bool outputTruncated = false;
        string? outputError = null;
        int launcherExit = 1;
        Legacy.ProcessInfo process = default;
        SafeAccessTokenHandle? linked = null;
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

            stage = "select_existing_ordinary_token";
            using SafeAccessTokenHandle own = Legacy.OpenOwnToken(0xb);
            parent = Legacy.ReadToken(own);
            parentElevationType = Native.ElevationType(own);
            SafeAccessTokenHandle? launchToken;
            if (Ordinary(parent))
            {
                mode = "already_ordinary_medium";
                selected = parent;
                launchToken = null;
            }
            else if (parent.token_type == 1 && parent.elevated && parent.integrity_rid == 0x3000 && !parent.is_restricted)
            {
                if (parentElevationType != 2)
                {
                    targetNotRunReason = "CALLER_HIGH_WITHOUT_LINKED_MEDIUM";
                    throw new InvalidOperationException(targetNotRunReason);
                }
                try { linked = Native.ExistingLinkedToken(own); }
                catch (Win32Exception error)
                {
                    targetNotRunReason = "CALLER_HIGH_WITHOUT_LINKED_MEDIUM";
                    throw new Win32Exception(error.NativeErrorCode, targetNotRunReason);
                }
                selected = Legacy.ReadToken(linked);
                linkedElevationType = Native.ElevationType(linked);
                if (!Ordinary(selected) || !SameIdentity(selected, parent) || linkedElevationType != 3)
                {
                    targetNotRunReason = "EXISTING_LINKED_TOKEN_IS_NOT_ELIGIBLE_ORDINARY_MEDIUM";
                    throw new InvalidOperationException("LINKED_TOKEN_IS_NOT_SAME_USER_ORDINARY_MEDIUM_LIMITED_PRIMARY");
                }
                mode = "existing_same_user_linked_medium";
                launchToken = linked;
            }
            else
            {
                targetNotRunReason = "CALLER_NOT_ORDINARY_MEDIUM_OR_ELIGIBLE_UAC_HIGH";
                throw new InvalidOperationException("CALLER_MUST_BE_ORDINARY_MEDIUM_OR_UNRESTRICTED_UAC_HIGH_WITH_LINKED_MEDIUM");
            }

            // No credentials, UAC, shell verbs, token adjustments or newly-created token.
            // Only these new pipe handles are inheritable; a linked token handle is not.
            pipes = new Legacy.OwnedPipes();
            Legacy.StartupInfoEx startup = pipes.StartupInfo();
            string station = Native.UserObjectName(Native.GetProcessWindowStation());
            string desktop = Native.UserObjectName(Native.GetThreadDesktop(Native.GetCurrentThreadId()));
            startup.Startup.Desktop = station + "\\" + desktop;
            var command = new StringBuilder(commandText);
            stage = "native_process_creation";
            apiStarted = launchToken is null
                ? Legacy.CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080000, IntPtr.Zero, workspace, ref startup, out process)
                : Legacy.CreateProcessAsUser(launchToken, executable, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080000, IntPtr.Zero, workspace, ref startup, out process);
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
                actualChild = Legacy.ReadToken(childToken);
                if (!Ordinary(actualChild) || !SameIdentity(actualChild, selected))
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
            linked?.Dispose();
            scriptOrAssemblyLease?.Dispose();
            executableLease?.Dispose();
            if (reportLease is not null)
            {
                try
                {
                    JsonSerializer.Serialize(reportLease, new
                    {
                        schema_version = 1,
                        status = stage == "completed" && failure is null ? (childExitCode == 0 ? "passed" : "child_failed") : "failed",
                        kind = "explicit_local_test_existing_ordinary_primary_token_launcher",
                        launch_id = launchId, started_utc = started, finished_utc = DateTimeOffset.UtcNow,
                        executable = options.Executable, resolved_executable = resolvedExecutable, executable_sha256 = targetHash,
                        script_or_assembly_sha256 = inputFileHash,
                        argument_count = options.Arguments.Length,
                        argument_vector_sha256 = Hash(JsonSerializer.Serialize(options.Arguments)),
                        timeout_seconds = options.TimeoutSeconds,
                        token_selection = mode, parent_token = parent, selected_token = selected, actual_child_token = actualChild,
                        parent_elevation_type = parentElevationType, linked_elevation_type = linkedElevationType,
                        target_execution = apiStarted ? "started" : "not_run",
                        target_not_run_reason = apiStarted ? null : targetNotRunReason ?? "LAUNCHER_FAILED_BEFORE_PROCESS_CREATION",
                        stage, api_started = apiStarted, api_error = nativeError, child_pid = childPid,
                        natural_exit = naturallyExited, child_exit_code = childExitCode, launcher_exit_code = launcherExit,
                        combined_output = outputFile, output_bytes = outputBytes, output_truncated = outputTruncated, output_error = outputError,
                        failure,
                        scope = "Only launch the explicitly requested local test as an existing ordinary same-user token. No test or isolation claim is inferred from a successful launch.",
                        token_changes = "none", credentials = "none", uac = "none", impersonation = "none", global_changes = "none",
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
                if (!Within(real, Path.Combine(workspace, "scripts")) && !Within(real, Path.Combine(workspace, "research")))
                    throw new InvalidDataException("POWERSHELL_SCRIPT_OUTSIDE_WORKSPACE_SCRIPTS_OR_RESEARCH");
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
        if (!(fileName.StartsWith("AutumnOS.", StringComparison.OrdinalIgnoreCase) || fileName.Equals("AutumnOS" + extension, StringComparison.OrdinalIgnoreCase)) ||
            !path.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ||
            !new[] { "src", "tools", "tests", "research", "artifacts" }.Any(directory => Within(path, Path.Combine(workspace, directory))) ||
            FileVersionInfo.GetVersionInfo(path).ProductName != "Lab Chronicles AutumnOS")
            throw new InvalidDataException("TARGET_MUST_BE_WORKSPACE_AUTUMNOS_BINARY_WITH_EXPECTED_PRODUCT_METADATA");
    }

    private static FileStream ReadLease(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("TARGET_PATH_MUST_BE_ABSOLUTE");
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }
    private static bool Ordinary(TokenFacts token) => token.token_type == 1 && !token.elevated && token.integrity_rid == 0x2000 && !token.is_restricted && token.restricting_sids.Length == 0;
    private static bool SameIdentity(TokenFacts a, TokenFacts b) => a.user_sid_sha256 == b.user_sid_sha256 && a.session_id == b.session_id;
    private static bool SamePath(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static bool Within(string file, string directory) => Path.GetFullPath(file).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static object Error(Exception error) => new { type = error.GetType().Name, stage = error.Message, native_error = error is Win32Exception win ? win.NativeErrorCode : (int?)null };
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

    private sealed record Options(string Workspace, string Report, string Executable, int TimeoutSeconds, string[] Arguments)
    {
        internal static Options Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            int index = 0;
            for (; index < args.Length && args[index] != "--"; index += 2)
            {
                if (index + 1 >= args.Length || args[index] is not ("--workspace" or "--report" or "--executable" or "--timeout-seconds") || !values.TryAdd(args[index], args[index + 1]))
                    throw new ArgumentException("EXPECTED_UNIQUE_WORKSPACE_REPORT_EXECUTABLE_TIMEOUT_OPTIONS_THEN_DOUBLE_DASH_ARGUMENTS");
            }
            if (index >= args.Length || !values.TryGetValue("--workspace", out string? workspace) || !values.TryGetValue("--report", out string? report) ||
                !values.TryGetValue("--executable", out string? executable) || !Path.IsPathFullyQualified(workspace) || !Path.IsPathFullyQualified(report) || !Path.IsPathFullyQualified(executable))
                throw new ArgumentException("ABSOLUTE_WORKSPACE_REPORT_EXECUTABLE_AND_DOUBLE_DASH_REQUIRED");
            int seconds = 180;
            if (values.TryGetValue("--timeout-seconds", out string? timeout) && (!int.TryParse(timeout, out seconds) || seconds is < 1 or > 900))
                throw new ArgumentException("TIMEOUT_SECONDS_MUST_BE_1_TO_900");
            string[] arguments = args[(index + 1)..];
            if (arguments.Length > 128) throw new ArgumentException("TOO_MANY_ARGUMENTS");
            return new(workspace, report, executable, seconds, arguments);
        }
    }
}

internal static class Native
{
    internal static SafeAccessTokenHandle ExistingLinkedToken(SafeAccessTokenHandle own)
    {
        if (!GetTokenInformation(own, 19, out IntPtr handle, IntPtr.Size, out int size) || size != IntPtr.Size || handle == IntPtr.Zero)
            throw Legacy.Error("EXISTING_LINKED_TOKEN_QUERY_FAILED");
        return new SafeAccessTokenHandle(handle);
    }
    internal static int ElevationType(SafeAccessTokenHandle token)
    {
        if (!GetTokenInformationInt(token, 18, out int value, sizeof(int), out int size) || size != sizeof(int)) throw Legacy.Error("TOKEN_ELEVATION_TYPE_QUERY_FAILED");
        return value;
    }
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
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out IntPtr value, int size, out int required);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)] private static extern bool GetTokenInformationInt(SafeAccessTokenHandle token, int kind, out int value, int size, out int required);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetProcessWindowStation();
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int size, out int required);
}
