// Owned console mechanism probe only. No clipboard content API or WebView is used.
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.Isolation.Probe;

internal static class Program
{
    private const string ClipboardCapability = "lpacClipboard";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    [MTAThread]
    private static int Main(string[] args)
    {
        Native.SetErrorMode(0x8003); // Only this owned process and children: suppress error UI.
        if (args.Length == 5 && args[0] == "--child") return Child(args[1], args[2], args[3], args[4]);
        if (args.Length is not (2 or 4) || args[0] != "--report" ||
            args.Length == 4 && (args[2] != "--baseline" || args[3] != "synthetic-lua-medium")) return 2;
        // Provenance only: this explicit diagnostic label never relaxes token checks.
        string baseline = args.Length == 4 ? args[3] : "ordinary-user";
        string report = Path.GetFullPath(args[1]);
        if (File.Exists(report)) return 2;
        string directory = Path.GetDirectoryName(report)!;
        Directory.CreateDirectory(directory);
        string stage = Path.Combine(directory, "lpac-stage-" + Guid.NewGuid().ToString("N"));
        string profile = "AutumnOS.Probe." + Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.UtcNow;
        var results = new List<CaseResult>();
        var environmentVariables = new List<EnvironmentVariableFact>();
        object? failure = null;
        bool createdProfile = false, childStillRunning = false;
        int? cleanupHresult = null;
        string? appSidText = null, capabilitySid = null;
        TokenFacts? parentToken = null;
        int exitCode = 1;
        IntPtr appSid = IntPtr.Zero;
        try
        {
            TokenFacts parent = Native.ReadOwnToken();
            parentToken = parent;
            if (parent.elevated || parent.is_appcontainer || parent.is_restricted || parent.integrity_rid != 0x2000)
                throw new InvalidOperationException("ORDINARY_MEDIUM_PARENT_REQUIRED");
            string executable = Environment.ProcessPath!;
            if (Path.GetFileName(executable).Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("COMPILED_APPHOST_REQUIRED");
            int hr = Native.CreateAppContainerProfile(profile, profile, "AutumnOS owned LPAC metadata probe", IntPtr.Zero, 0, out appSid);
            if (hr != 0) throw new COMException("UNIQUE_PROFILE_CREATE_FAILED", hr);
            createdProfile = true;
            appSidText = new SecurityIdentifier(appSid).Value;
            using var capability = Capability.Derive(ClipboardCapability);
            capabilitySid = capability.SidText;
            PrepareStage(stage, appSidText);
            string stagedExe = Path.Combine(stage, "app", Path.GetFileName(executable));
            string environment = ChildEnvironment(stage, environmentVariables);
            foreach (string label in new[] { "A-ordinary", "B-lpac-no-clipboard", "C-lpac-with-clipboard" })
            {
                var result = RunCase(label, stagedExe, stage, environment, parent, appSid, appSidText,
                    capability.Sid, capabilitySid);
                results.Add(result);
                if (!result.natural_exit && result.api_started) childStillRunning = true;
                // A must establish that the staged CLR and metadata collector actually work.
                // C remains useful when B exits with a recorded failure: it tests whether the
                // clipboard capability changes that observation. Never start it after a live child.
                if (childStillRunning || label == "A-ordinary" && !result.passed)
                    throw new InvalidOperationException("CONTROL_OR_CHILD_LIFECYCLE_FAILED");
            }
            if (results.Any(result => !result.passed)) throw new InvalidOperationException("MECHANISM_CASE_FAILED");
            exitCode = 0;
        }
        catch (Exception error) { failure = Failure(error); }
        finally
        {
            if (createdProfile && !childStillRunning)
            {
                cleanupHresult = Native.DeleteAppContainerProfile(profile);
                if (cleanupHresult != 0) exitCode = 1;
            }
            if (appSid != IntPtr.Zero) Native.FreeSid(appSid);
        }
        var document = new
        {
            schema_version = 1,
            status = exitCode == 0 ? "passed" : "failed",
            kind = "owned_LPAC_process_token_and_windowstation_handle_AB_C",
            started_utc = started,
            finished_utc = DateTimeOffset.UtcNow,
            os = Environment.OSVersion.VersionString,
            baseline_origin = baseline,
            real_user_desktop_acceptance = "not_run",
            parent_token = parentToken,
            appcontainer_sid = appSidText,
            clipboard_capability_sid = capabilitySid,
            stage_directory = stage,
            child_environment = new { source = "explicit_windows_known_folders_and_owned_stage", variables = environmentVariables,
                scope = "Names and presence only. Environment strings do not grant resource access." },
            cases = results,
            failure,
            cleanup = new { unique_profile_created = createdProfile, delete_hresult = cleanupHresult,
                status = !createdProfile ? "not_required" : childStillRunning ? "pending_live_child" : cleanupHresult == 0 ? "passed" : "failed",
                stage_files = "retained_as_evidence", force_termination = "none" },
            scope = "Actual owned child token and station handle access only; not clipboard or browser acceptance.",
            clipboard_content_calls = "none", browser = "not_run", clipboard_isolation_acceptance = "not_run",
            existing_station_desktop_acl_changes = "none", desktop_switch = "none", global_policy_changes = "none",
            network_capabilities = "none", untrusted_content = "not_run"
        };
        // CreateNew preserves earlier evidence even when a caller reuses a report path concurrently.
        using (var file = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read))
            JsonSerializer.Serialize(file, document, Json);
        Console.WriteLine(JsonSerializer.Serialize(new { report, status = exitCode == 0 ? "passed" : "failed", cases = results.Count }));
        return exitCode;
    }

    private static void PrepareStage(string stage, string appSid)
    {
        if (Directory.Exists(stage)) throw new InvalidOperationException("STAGE_MUST_BE_NEW");
        Directory.CreateDirectory(stage);
        string app = Path.Combine(stage, "app");
        Directory.CreateDirectory(app);
        foreach (string file in Directory.EnumerateFiles(AppContext.BaseDirectory))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("REPARSE_SOURCE_REJECTED");
            File.Copy(file, Path.Combine(app, Path.GetFileName(file)), overwrite: false);
        }
        string runtime = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        string version = Path.GetFileName(runtime);
        string sourceRoot = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
        string fxr = Path.Combine(sourceRoot, "host", "fxr", version);
        if (!File.Exists(Path.Combine(fxr, "hostfxr.dll"))) throw new InvalidDataException("MATCHING_HOSTFXR_MISSING");
        CopyTree(runtime, Path.Combine(stage, "runtime", "shared", "Microsoft.NETCore.App", version));
        CopyTree(fxr, Path.Combine(stage, "runtime", "host", "fxr", version));
        string user = WindowsIdentity.GetCurrent().User!.Value;
        // Access grants apply only to this newly created copy, never the SDK or worktree.
        // Trusted code stays Medium/NW: Low children can read/execute its explicit RX
        // grant, but cannot write up into code. Only their separate temp is Low/RW.
        SetDirectorySecurity(stage, $"D:P(A;OICI;FA;;;{user})(A;OICI;GRGX;;;{appSid})S:(ML;OICI;NW;;;ME)");
        foreach (string folder in Directory.EnumerateDirectories(stage, "*", SearchOption.AllDirectories))
            SetDirectorySecurity(folder, $"D:P(A;OICI;FA;;;{user})(A;OICI;GRGX;;;{appSid})S:(ML;OICI;NW;;;ME)");
        foreach (string file in Directory.EnumerateFiles(stage, "*", SearchOption.AllDirectories))
            Native.SetNewFileSecurity(file, $"D:P(A;;FA;;;{user})(A;;GRGX;;;{appSid})S:(ML;;NW;;;ME)");
        string temp = Path.Combine(stage, "temp");
        Directory.CreateDirectory(temp);
        SetDirectorySecurity(temp, $"D:P(A;OICI;FA;;;{user})(A;OICI;FA;;;{appSid})S:(ML;OICI;NW;;;LW)");
    }

    private static void SetDirectorySecurity(string path, string sddl) => Native.SetNewFileSecurity(path, sddl);
    private static void CopyTree(string source, string destination)
    {
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("REPARSE_RUNTIME_REJECTED");
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("REPARSE_RUNTIME_REJECTED");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }
        foreach (string folder in Directory.EnumerateDirectories(source)) CopyTree(folder, Path.Combine(destination, Path.GetFileName(folder)));
    }

    private static string ChildEnvironment(string stage, List<EnvironmentVariableFact> facts)
    {
        // Controlled environment: no inherited credentials, proxy settings, profiler or startup hook.
        var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string name, string value, bool required = true)
        {
            bool present = !string.IsNullOrWhiteSpace(value);
            facts.Add(new(name, present));
            if (!present)
            {
                if (required) throw new InvalidOperationException("CHILD_ENVIRONMENT_REQUIRED_VALUE_MISSING_" + name);
                return;
            }
            if (value.Contains('\0')) throw new InvalidDataException("CHILD_ENVIRONMENT_VALUE_INVALID_" + name);
            values.Add(name, value);
        }
        string Folder(Environment.SpecialFolder folder) => Environment.GetFolderPath(folder, Environment.SpecialFolderOption.DoNotVerify);
        string windows = Folder(Environment.SpecialFolder.Windows);
        Add("SystemRoot", windows);
        Add("WINDIR", windows);
        if (!Path.IsPathFullyQualified(windows)) throw new InvalidDataException("CHILD_ENVIRONMENT_WINDOWS_PATH_INVALID");
        Add("SystemDrive", (Path.GetPathRoot(windows) ?? "").TrimEnd(Path.DirectorySeparatorChar));
        Add("USERPROFILE", Folder(Environment.SpecialFolder.UserProfile));
        Add("LOCALAPPDATA", Folder(Environment.SpecialFolder.LocalApplicationData));
        Add("APPDATA", Folder(Environment.SpecialFolder.ApplicationData));
        string programData = Folder(Environment.SpecialFolder.CommonApplicationData);
        Add("ProgramData", programData);
        Add("ALLUSERSPROFILE", programData);
        string programFiles = Folder(Environment.SpecialFolder.ProgramFiles);
        string commonProgramFiles = Folder(Environment.SpecialFolder.CommonProgramFiles);
        Add("ProgramFiles", programFiles);
        Add("CommonProgramFiles", commonProgramFiles);
        if (Environment.Is64BitOperatingSystem)
        {
            Add("ProgramW6432", programFiles);
            Add("CommonProgramW6432", commonProgramFiles);
            Add("ProgramFiles(x86)", Folder(Environment.SpecialFolder.ProgramFilesX86));
            Add("CommonProgramFiles(x86)", Folder(Environment.SpecialFolder.CommonProgramFilesX86));
        }
        Add("PATH", Path.Combine(windows, "System32") + ";" + windows);
        Add("ComSpec", Path.Combine(windows, "System32", "cmd.exe"));
        Add("OS", "Windows_NT");
        Add("PROCESSOR_ARCHITECTURE", "AMD64"); // The probe project is fixed win-x64.
        Add("DOTNET_ROOT", Path.Combine(stage, "runtime"));
        Add("DOTNET_ROOT_X64", Path.Combine(stage, "runtime"));
        Add("DOTNET_MULTILEVEL_LOOKUP", "0");
        Add("DOTNET_CLI_TELEMETRY_OPTOUT", "1");
        Add("DOTNET_EnableDiagnostics", "0");
        Add("TEMP", Path.Combine(stage, "temp"));
        Add("TMP", Path.Combine(stage, "temp"));
        return string.Join('\0', values.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
    }

    private static int Child(string nonce, string label, string parentText, string expectedAppSid)
    {
        if (nonce.Length != 32 || nonce.Any(c => !char.IsAsciiHexDigit(c)) ||
            label is not ("A-ordinary" or "B-lpac-no-clipboard" or "C-lpac-with-clipboard") ||
            !int.TryParse(parentText, out int parent) || parent <= 0) return 2;
        try
        {
            var facts = Native.ReadOwnToken();
            var station = Native.StationFacts();
            Console.WriteLine(JsonSerializer.Serialize(new ChildResult("metadata_complete", nonce, label, checked((uint)Environment.ProcessId),
                parent, expectedAppSid, facts, station)));
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "failed", nonce, label, pid = Environment.ProcessId, failure = Failure(error) }));
            return 1;
        }
    }

    private static CaseResult RunCase(string label, string executable, string stage, string environment, TokenFacts parent,
        IntPtr appSid, string appSidText, IntPtr capability, string capabilitySid)
    {
        string nonce = Guid.NewGuid().ToString("N");
        bool ordinary = label == "A-ordinary", clipboard = label == "C-lpac-with-clipboard";
        Native.ProcessInfo child = default;
        bool started = false, exited = false;
        uint? exit = null;
        string phase = "startup_attributes";
        ChildResult? metadata = null;
        try
        {
            using var startup = new Startup(ordinary ? IntPtr.Zero : appSid, ordinary ? null : appSidText, clipboard ? capability : IntPtr.Zero);
            var information = startup.Information();
            var command = new StringBuilder(string.Join(' ', new[] { executable, "--child", nonce, label,
                Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), appSidText }.Select(Quote)));
            IntPtr environmentBlock = Marshal.StringToHGlobalUni(environment);
            try
            {
                phase = "native_process_creation";
                started = Native.CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, true,
                    0x08080000 | 0x400, environmentBlock, Path.Combine(stage, "app"), ref information, out child);
                if (!started) throw Native.Error("CREATE_OWNED_CHILD_FAILED");
            }
            finally { Marshal.FreeHGlobal(environmentBlock); }
            startup.CloseChildPipeEnds();
            using var reader = new StreamReader(new FileStream(startup.DetachOutput(), FileAccess.Read), Encoding.UTF8);
            Task<string> reading = ReadBounded(reader);
            phase = "waiting_for_natural_exit";
            uint wait = Native.WaitForSingleObject(child.Process, 30000);
            if (wait == 0xffffffff) throw Native.Error("WAIT_FAILED");
            if (wait != 0) throw new TimeoutException("CHILD_STILL_RUNNING_NO_FORCE_TERMINATION");
            exited = true;
            if (!Native.GetExitCodeProcess(child.Process, out uint code)) throw Native.Error("EXIT_CODE_QUERY_FAILED");
            exit = code;
            string output = reading.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            phase = "child_result";
            if (code != 0 || string.IsNullOrWhiteSpace(output))
                return new(label, false, true, null, child.ProcessId, code, true,
                    code == 0xc0000142 ? "native_initialization_failed_0xC0000142" : string.IsNullOrWhiteSpace(output) ? "child_terminated_without_metadata" : "child_failed",
                    null, new { output_bytes = Encoding.UTF8.GetByteCount(output), output_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(output))),
                        controlled_child_diagnostic = ControlledChildDiagnostic(output, stage, code) });
            metadata = JsonSerializer.Deserialize<ChildResult>(output.Trim()) ?? throw new InvalidDataException("CHILD_METADATA_MISSING");
            phase = "validate_actual_child_metadata";
            if (metadata.status != "metadata_complete" || metadata.nonce != nonce || metadata.label != label || metadata.pid != child.ProcessId ||
                metadata.parent_pid != Environment.ProcessId || metadata.expected_app_sid != appSidText)
                throw new InvalidDataException("CHILD_IDENTITY_MISMATCH");
            var token = metadata.token;
            if (token.elevated || token.token_type != 1 || token.user_sid_sha256 != parent.user_sid_sha256 || token.session_id != parent.session_id)
                throw new InvalidDataException("CHILD_TOKEN_IDENTITY_MISMATCH");
            if (ordinary)
            {
                if (token.is_appcontainer || token.is_lpac != false || token.is_restricted || token.integrity_rid != 0x2000)
                    throw new InvalidDataException("ORDINARY_CONTROL_TOKEN_MISMATCH");
            }
            else
            {
                if (token.is_lpac is null) throw new InvalidDataException("LPAC_ACTUAL_TOKEN_QUERY_UNAVAILABLE");
                if (!token.is_appcontainer || token.is_lpac != true || token.appcontainer_sid != appSidText || token.integrity_rid != 0x1000 ||
                    !token.capabilities.Order(StringComparer.Ordinal).SequenceEqual(clipboard ? new[] { capabilitySid } : Array.Empty<string>()))
                    throw new InvalidDataException("LPAC_ACTUAL_TOKEN_MISMATCH");
            }
            var current = metadata.station.current_handle;
            var requested = metadata.station.clipboard_access_handle;
            bool expectAllowed = ordinary || clipboard;
            bool actualAllowed = requested.opened && requested.query_status == 0 && (requested.granted_access & 4) == 4;
            bool actuallyDenied = !requested.opened && requested.error == 5;
            bool currentDenied = current.opened && current.query_status == 0 && (current.granted_access & 4) == 0;
            // A/C demonstrate that this process can obtain a handle with access bit 4.
            // Windows does not promise that its already-associated station handle was
            // opened with that bit. Keep its actual mask as an independent observation.
            // B remains conservative about both newly acquired and existing handles.
            if (metadata.station.station_name != "WinSta0" || (expectAllowed ? !actualAllowed : !(actuallyDenied && currentDenied)))
                throw new InvalidDataException("CLIPBOARD_HANDLE_CONTROL_OR_DENIAL_NOT_PROVEN");
            return new(label, true, true, null, child.ProcessId, code, true, "metadata_validated", metadata, null);
        }
        catch (Exception error)
        {
            return new(label, false, started, error is Win32Exception native ? native.NativeErrorCode : null,
                started ? child.ProcessId : null, exit, exited, phase, metadata, Failure(error));
        }
        finally
        {
            if (child.Thread != IntPtr.Zero) Native.CloseHandle(child.Thread);
            if (child.Process != IntPtr.Zero) Native.CloseHandle(child.Process);
        }
    }

    private static async Task<string> ReadBounded(StreamReader reader)
    {
        char[] buffer = new char[1024];
        var output = new StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
        {
            if (output.Length + count > 32768) throw new InvalidDataException("CHILD_OUTPUT_LIMIT_EXCEEDED");
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }
    private static object ControlledChildDiagnostic(string output, string stage, uint exitCode)
    {
        // This collector runs only the fixed, owned console child. It must never be
        // reused for arbitrary applications or copied into product logging.
        var prefixes = new List<(string Path, string Marker)> { (stage, "<STAGE>") };
        string? workspace = FindWorkspace(AppContext.BaseDirectory);
        if (workspace is not null) prefixes.Add((workspace, "<WORKSPACE>"));
        prefixes.Add((Environment.GetFolderPath(Environment.SpecialFolder.Windows), "<WINDOWS>"));
        prefixes.Add((Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<USERPROFILE>"));
        foreach (var prefix in prefixes.Where(value => value.Path.Length >= 3 && Path.IsPathFullyQualified(value.Path))
            .OrderByDescending(value => value.Path.Length))
        {
            string value = prefix.Path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            output = output.Replace(value, prefix.Marker, StringComparison.OrdinalIgnoreCase)
                .Replace(value.Replace('\\', '/'), prefix.Marker, StringComparison.OrdinalIgnoreCase);
        }
        output = Regex.Replace(output, @"\bS-\d+(?:-\d+){1,15}\b", "<SID>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
        string[] statuses = Regex.Matches(output, @"\b0x[0-9a-f]{8}\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))
            .Select(match => match.Value.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Take(16).ToArray();
        var text = new StringBuilder();
        int bytes = 0;
        bool truncated = false;
        foreach (Rune rune in output.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > 4096) { truncated = true; break; }
            text.Append(rune.ToString()); bytes += rune.Utf8SequenceLength;
        }
        return new
        {
            scope = "Fixed owned console child only; no third-party or account input. Not a product logging facility.",
            exit_code_hex = "0x" + exitCode.ToString("X8"), reported_hresult_formatted_tokens = statuses,
            mentions_hostfxr = output.Contains("hostfxr", StringComparison.OrdinalIgnoreCase),
            mentions_coreclr = output.Contains("coreclr", StringComparison.OrdinalIgnoreCase),
            mentions_hostpolicy = output.Contains("hostpolicy", StringComparison.OrdinalIgnoreCase),
            text = text.ToString(), text_utf8_bytes = bytes, max_text_utf8_bytes = 4096, truncated,
            redaction = "Exact known stage/workspace/Windows/user-profile prefixes and SID-shaped values."
        };
    }
    private static string? FindWorkspace(string directory)
    {
        string? current = Path.GetFullPath(directory);
        for (int depth = 0; depth < 16 && current is not null; depth++, current = Path.GetDirectoryName(current))
            if (File.Exists(Path.Combine(current, "AutumnOS.slnx"))) return current;
        return null;
    }
    private static string Quote(string value)
    {
        var b = new StringBuilder("\""); int slashes = 0;
        foreach (char c in value) { if (c == '\\') { slashes++; continue; } b.Append('\\', c == '"' ? slashes * 2 + 1 : slashes).Append(c); slashes = 0; }
        return b.Append('\\', slashes * 2).Append('"').ToString();
    }
    private static object Failure(Exception error) => new { type = error.GetType().Name, code = error.Message,
        native_error = error is Win32Exception native ? native.NativeErrorCode : (int?)null, hresult = "0x" + unchecked((uint)error.HResult).ToString("X8") };
}

internal sealed record CaseResult(string label, bool passed, bool api_started, int? native_error, uint? child_pid,
    uint? child_exit_code, bool natural_exit, string phase, ChildResult? metadata, object? failure);
internal sealed record ChildResult(string status, string nonce, string label, uint pid, int parent_pid, string expected_app_sid,
    TokenFacts token, StationFacts station);
internal sealed record TokenFacts(string user_sid_sha256, int token_type, uint session_id, uint integrity_rid, bool elevated,
    bool is_appcontainer, bool? is_lpac, bool is_restricted, string? appcontainer_sid, string[] capabilities,
    string appcontainer_sid_query, string lpac_query, LpacQueryDiagnostic? lpac_diagnostic);
internal sealed record LpacQueryDiagnostic(int win32_error, int win32_return_length,
    NtTokenQueryFact appcontainer_control, NtTokenQueryFact lpac);
internal sealed record NtTokenQueryFact(int information_class, int ntstatus, string ntstatus_hex,
    uint return_length, int? value, bool valid_boolean);
internal sealed record HandleFacts(bool opened, uint requested_access, int error, int? query_status, uint? granted_access);
internal sealed record StationFacts(string station_name, HandleFacts current_handle, HandleFacts clipboard_access_handle);
internal sealed record EnvironmentVariableFact(string name, bool present);

internal sealed class Capability : IDisposable
{
    internal IntPtr Sid { get; private set; }
    internal string SidText => new SecurityIdentifier(Sid).Value;
    private Capability(IntPtr sid) { Sid = sid; }
    internal static Capability Derive(string name)
    {
        IntPtr groups = IntPtr.Zero, capabilities = IntPtr.Zero;
        uint groupCount = 0, count = 0;
        try
        {
            if (!Native.DeriveCapabilitySidsFromName(name, out groups, out groupCount, out capabilities, out count)) throw Native.Error("CAPABILITY_DERIVATION_FAILED");
            if (count != 1 || groupCount > 16) throw new InvalidDataException("CAPABILITY_SID_COUNT_UNEXPECTED");
            IntPtr sid = Marshal.ReadIntPtr(capabilities);
            uint size = Native.GetLengthSid(sid);
            IntPtr copy = Marshal.AllocHGlobal(checked((int)size));
            if (!Native.CopySid(size, copy, sid)) { Marshal.FreeHGlobal(copy); throw Native.Error("CAPABILITY_COPY_FAILED"); }
            return new(copy);
        }
        finally
        {
            FreeArray(groups, groupCount); FreeArray(capabilities, count);
        }
    }
    private static void FreeArray(IntPtr array, uint count)
    {
        if (array == IntPtr.Zero) return;
        for (uint i = 0; i < count; i++) Native.LocalFree(Marshal.ReadIntPtr(array, checked((int)i * IntPtr.Size)));
        Native.LocalFree(array);
    }
    public void Dispose() { if (Sid != IntPtr.Zero) { Marshal.FreeHGlobal(Sid); Sid = IntPtr.Zero; } }
}

internal sealed class Startup : IDisposable
{
    private SafeFileHandle? outputRead, outputWrite, inputRead, inputWrite;
    private readonly List<IntPtr> allocated = [];
    private IntPtr attributes;
    private bool initialized;

    internal Startup(IntPtr appSid, string? appSidText, IntPtr capability)
    {
        IntPtr descriptor = IntPtr.Zero;
        try
        {
            if (appSid != IntPtr.Zero)
            {
                string user = WindowsIdentity.GetCurrent().User!.Value;
                if (!Native.ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;GA;;;{user})(A;;GA;;;{appSidText})S:(ML;;NW;;;LW)", 1, out descriptor, out _))
                    throw Native.Error("PIPE_SECURITY_DESCRIPTOR_FAILED");
            }
            var security = new Native.SecurityAttributes { Size = Marshal.SizeOf<Native.SecurityAttributes>(), Inherit = true, Descriptor = descriptor };
            if (!Native.CreatePipe(out outputRead, out outputWrite, ref security, 0) || !Native.CreatePipe(out inputRead, out inputWrite, ref security, 0)) throw Native.Error("PIPE_CREATE_FAILED");
            if (!Native.SetHandleInformation(outputRead, 1, 0) || !Native.SetHandleInformation(inputWrite, 1, 0)) throw Native.Error("PIPE_INHERITANCE_FAILED");
            inputWrite.Dispose(); inputWrite = null;
            UIntPtr size = UIntPtr.Zero;
            int count = appSid == IntPtr.Zero ? 1 : 3;
            Native.InitializeProcThreadAttributeList(IntPtr.Zero, count, 0, ref size);
            if (size == UIntPtr.Zero) throw Native.Error("ATTRIBUTE_LIST_SIZE_FAILED");
            attributes = Marshal.AllocHGlobal(checked((int)size));
            if (!Native.InitializeProcThreadAttributeList(attributes, count, 0, ref size)) throw Native.Error("ATTRIBUTE_LIST_INITIALIZE_FAILED");
            initialized = true;
            IntPtr handles = Allocate(IntPtr.Size * 2);
            Marshal.WriteIntPtr(handles, outputWrite.DangerousGetHandle()); Marshal.WriteIntPtr(handles + IntPtr.Size, inputRead.DangerousGetHandle());
            Update(0x20002, handles, IntPtr.Size * 2);
            if (appSid != IntPtr.Zero)
            {
                IntPtr capabilities = IntPtr.Zero;
                if (capability != IntPtr.Zero)
                {
                    capabilities = Allocate(Marshal.SizeOf<Native.SidAndAttributes>());
                    Marshal.StructureToPtr(new Native.SidAndAttributes { Sid = capability, Attributes = 4 }, capabilities, false);
                }
                IntPtr securityCapabilities = Allocate(Marshal.SizeOf<Native.SecurityCapabilities>());
                Marshal.StructureToPtr(new Native.SecurityCapabilities { AppContainerSid = appSid, Capabilities = capabilities, Count = capability == IntPtr.Zero ? 0u : 1u }, securityCapabilities, false);
                Update(0x20009, securityCapabilities, Marshal.SizeOf<Native.SecurityCapabilities>());
                IntPtr policy = Allocate(4); Marshal.WriteInt32(policy, 1);
                Update(0x2000f, policy, 4); // LPAC: ALL_APPLICATION_PACKAGES_OPT_OUT.
            }
        }
        catch { Dispose(); throw; }
        finally { if (descriptor != IntPtr.Zero) Native.LocalFree(descriptor); }
    }
    private IntPtr Allocate(int size) { IntPtr p = Marshal.AllocHGlobal(size); allocated.Add(p); return p; }
    private void Update(ulong key, IntPtr value, int size)
    {
        if (!Native.UpdateProcThreadAttribute(attributes, 0, (UIntPtr)key, value, (UIntPtr)size, IntPtr.Zero, IntPtr.Zero)) throw Native.Error("STARTUP_ATTRIBUTE_UPDATE_FAILED");
    }
    internal Native.StartupInfoEx Information() => new() { Startup = new() { Size = Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x100,
        StdInput = inputRead!.DangerousGetHandle(), StdOutput = outputWrite!.DangerousGetHandle(), StdError = outputWrite.DangerousGetHandle() }, Attributes = attributes };
    internal void CloseChildPipeEnds() { outputWrite?.Dispose(); outputWrite = null; inputRead?.Dispose(); inputRead = null; }
    internal SafeFileHandle DetachOutput() { var handle = outputRead ?? throw new InvalidOperationException("OUTPUT_MISSING"); outputRead = null; return handle; }
    public void Dispose()
    {
        outputRead?.Dispose(); outputWrite?.Dispose(); inputRead?.Dispose(); inputWrite?.Dispose();
        if (attributes != IntPtr.Zero) { if (initialized) Native.DeleteProcThreadAttributeList(attributes); Marshal.FreeHGlobal(attributes); attributes = IntPtr.Zero; }
        foreach (IntPtr p in allocated) Marshal.FreeHGlobal(p); allocated.Clear();
    }
}

internal static class Native
{
    internal static Win32Exception Error(string code) => new(Marshal.GetLastWin32Error(), code);
    internal static void SetNewFileSecurity(string path, string sddl)
    {
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out IntPtr descriptor, out _)) throw Error("NEW_STAGE_DESCRIPTOR_FAILED");
        try
        {
            // DACL and requested mandatory label only, under the fresh owned stage.
            if (!SetFileSecurity(path, 0x80000004 | 0x10, descriptor)) throw Error("NEW_STAGE_ACL_FAILED");
        }
        finally { LocalFree(descriptor); }
    }
    internal static TokenFacts ReadOwnToken()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 8, out var token)) throw Error("TOKEN_OPEN_FAILED");
        using (token)
        {
            string user = ""; int type = 0; uint session = 0, integrity = 0;
            bool elevated = false, app = false; bool? lpac = false; string? sid = null; string[] capabilities = [];
            string lpacQuery = "not_applicable_non_appcontainer";
            LpacQueryDiagnostic? lpacDiagnostic = null;
            // Establish applicability first. A non-AppContainer's sid/LPAC values are
            // logically absent, not successful reads of inapplicable token classes.
            foreach (int kind in new[] { 29, 1, 8, 12, 20, 25, 30, 31, 46 })
            {
                if (!app && kind is 31 or 46) continue;
                // Fixed classes are queried directly. In particular, class 46's
                // zero-length size probe is not a supported capability test.
                bool fixedClass = kind is 8 or 12 or 20 or 29 or 46;
                int size = sizeof(uint);
                if (!fixedClass)
                {
                    bool sized = GetTokenInformation(token, kind, IntPtr.Zero, 0, out size);
                    int sizeError = Marshal.GetLastWin32Error();
                    if ((!sized && sizeError is not (122 or 24)) || size <= 0 || size > 65536)
                        throw new Win32Exception(sizeError, "TOKEN_SIZE_QUERY_FAILED_KIND_" + kind);
                }
                IntPtr memory = Marshal.AllocHGlobal(size);
                try
                {
                    bool queried = GetTokenInformation(token, kind, memory, size, out int actual);
                    if (!queried)
                    {
                        int queryError = Marshal.GetLastWin32Error();
                        if (kind != 46) throw new Win32Exception(queryError, "TOKEN_QUERY_FAILED_KIND_" + kind);
                        // Preserve the Win32 failure and independently query the same
                        // real kernel token. Class 29 is a fixed-buffer positive control.
                        var control = QueryNtBoolean(token, 29);
                        var actualLpac = QueryNtBoolean(token, 46);
                        lpacDiagnostic = new(queryError, actual, control, actualLpac);
                        if (control.ntstatus == 0 && control.valid_boolean && control.value == 1 &&
                            actualLpac.ntstatus == 0 && actualLpac.valid_boolean)
                        {
                            lpac = actualLpac.value == 1;
                            lpacQuery = "NtQueryInformationToken";
                        }
                        else
                        {
                            lpac = null;
                            lpacQuery = "unavailable_win32_and_nt";
                        }
                        continue;
                    }
                    if (actual <= 0 || actual > size) throw new InvalidDataException("TOKEN_RESULT_LENGTH_INVALID_KIND_" + kind);
                    if (kind == 1) user = new SecurityIdentifier(TokenSidPointer(memory, actual, kind)).Value;
                    else if (kind == 8) type = unchecked((int)TokenDword(memory, actual, kind));
                    else if (kind == 12) session = TokenDword(memory, actual, kind);
                    else if (kind == 20) elevated = TokenBoolean(memory, actual, kind);
                    else if (kind == 25) { string value = new SecurityIdentifier(TokenSidPointer(memory, actual, kind)).Value; integrity = uint.Parse(value[(value.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture); }
                    else if (kind == 29)
                    {
                        app = TokenBoolean(memory, actual, kind);
                        if (app) { lpac = null; lpacQuery = "not_queried"; }
                    }
                    else if (kind == 31) { IntPtr value = TokenSidPointer(memory, actual, kind); sid = value == IntPtr.Zero ? null : new SecurityIdentifier(value).Value; }
                    else if (kind == 46) { lpac = TokenBoolean(memory, actual, kind); lpacQuery = "GetTokenInformation"; }
                    else if (kind == 30)
                    {
                        if (actual < sizeof(int)) throw new InvalidDataException("CAPABILITIES_BUFFER_INVALID_KIND_30");
                        int count = Marshal.ReadInt32(memory), offset = IntPtr.Size == 8 ? 8 : 4, entry = Marshal.SizeOf<SidAndAttributes>();
                        // An empty TOKEN_GROUPS can legitimately contain only its
                        // DWORD count, without padding for the first SID entry.
                        if (count < 0 || count != 0 && offset + (long)count * entry > actual)
                            throw new InvalidDataException("CAPABILITIES_BUFFER_INVALID_KIND_30");
                        capabilities = Enumerable.Range(0, count).Select(index => new SecurityIdentifier(Marshal.PtrToStructure<SidAndAttributes>(memory + offset + index * entry).Sid).Value).ToArray();
                    }
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
            return new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(user))), type, session, integrity, elevated,
                app, lpac, IsTokenRestricted(token), sid, capabilities,
                app ? "queried" : "not_applicable_non_appcontainer", lpacQuery, lpacDiagnostic);
        }
    }
    private static NtTokenQueryFact QueryNtBoolean(SafeAccessTokenHandle token, int kind)
    {
        IntPtr memory = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(memory, -1);
            int status = NtQueryInformationToken(token, kind, memory, sizeof(uint), out uint actual);
            int? value = status != 0 ? null : actual switch
            {
                1 => Marshal.ReadByte(memory),
                4 => Marshal.ReadInt32(memory),
                _ => null
            };
            return new(kind, status, "0x" + unchecked((uint)status).ToString("X8"), actual, value,
                status == 0 && value is 0 or 1);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    private static bool TokenBoolean(IntPtr memory, int length, int kind)
    {
        int value = length switch
        {
            1 => Marshal.ReadByte(memory),
            4 => Marshal.ReadInt32(memory),
            _ => throw new InvalidDataException("TOKEN_BOOLEAN_LENGTH_INVALID_KIND_" + kind)
        };
        if (value is not (0 or 1)) throw new InvalidDataException("TOKEN_BOOLEAN_VALUE_INVALID_KIND_" + kind);
        return value == 1;
    }
    private static uint TokenDword(IntPtr memory, int length, int kind)
    {
        if (length != sizeof(uint)) throw new InvalidDataException("TOKEN_DWORD_LENGTH_INVALID_KIND_" + kind);
        return unchecked((uint)Marshal.ReadInt32(memory));
    }
    private static IntPtr TokenSidPointer(IntPtr memory, int length, int kind)
    {
        if (length < IntPtr.Size) throw new InvalidDataException("TOKEN_SID_POINTER_LENGTH_INVALID_KIND_" + kind);
        return Marshal.ReadIntPtr(memory);
    }
    internal static StationFacts StationFacts()
    {
        IntPtr station = GetProcessWindowStation();
        if (station == IntPtr.Zero) throw Error("CURRENT_STATION_MISSING");
        var name = new StringBuilder(512);
        if (!GetUserObjectInformation(station, 2, name, name.Capacity * 2, out _)) throw Error("STATION_NAME_QUERY_FAILED");
        var current = QueryHandle(station, 0);
        IntPtr opened = OpenWindowStation("WinSta0", false, 4);
        var requested = opened == IntPtr.Zero ? new HandleFacts(false, 4, Marshal.GetLastWin32Error(), null, null) : QueryHandle(opened, 4);
        if (opened != IntPtr.Zero) CloseWindowStation(opened);
        return new(name.ToString(), current, requested);
    }
    private static HandleFacts QueryHandle(IntPtr handle, uint requested)
    {
        int status = NtQueryObject(handle, 0, out ObjectBasic information, (uint)Marshal.SizeOf<ObjectBasic>(), out _);
        return new(true, requested, 0, status, status == 0 ? information.GrantedAccess : null);
    }
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int Size; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] internal struct SidAndAttributes { public IntPtr Sid; public uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityCapabilities { public IntPtr AppContainerSid, Capabilities; public uint Count, Reserved; }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectBasic { public uint Attributes, GrantedAccess, HandleCount, PointerCount; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 10)] public uint[] Reserved; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo { public int Size; public string? Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags; public short ShowWindow, Reserved2; public IntPtr ReservedPointer, StdInput, StdOutput, StdError; }
    [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { public StartupInfo Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll")] internal static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, ref SecurityAttributes security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetHandleInformation(SafeFileHandle handle, uint mask, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, uint flags, ref UIntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, UIntPtr attribute, IntPtr value, UIntPtr size, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool CreateProcess(string application, StringBuilder command, IntPtr processSecurity, IntPtr threadSecurity, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfoEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(IntPtr process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetExitCodeProcess(IntPtr process, out uint exit);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr value);
    [DllImport("kernelbase.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool DeriveCapabilitySidsFromName(string name, out IntPtr groups, out uint groupCount, out IntPtr capabilities, out uint count);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern uint GetLengthSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool CopySid(uint length, IntPtr destination, IntPtr source);
    [DllImport("advapi32.dll")] internal static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll")] private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr information, int length, out int required);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint length);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool SetFileSecurity(string path, uint information, IntPtr descriptor);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] internal static extern int CreateAppContainerProfile(string name, string displayName, string description, IntPtr capabilities, uint count, out IntPtr sid);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] internal static extern int DeleteAppContainerProfile(string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int kind, StringBuilder value, int length, out int required);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenWindowStation(string name, bool inherit, uint access);
    [DllImport("user32.dll")] private static extern bool CloseWindowStation(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtQueryObject(IntPtr handle, int kind, out ObjectBasic information, uint size, out uint required);
    [DllImport("ntdll.dll")] private static extern int NtQueryInformationToken(SafeAccessTokenHandle token, int kind, IntPtr information, uint size, out uint required);
}
