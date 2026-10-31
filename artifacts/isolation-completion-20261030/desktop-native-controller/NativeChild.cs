using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.OwnedBrowserParent;

internal sealed class NativeChild(string directory, string entry, string nonce, string side, string desktop, string temp, IntPtr package, string? packageText, Action save, string station = "WinSta0")
{
    public ProcessAudit audit { get; } = new(directory, directory, packageText);
    public JobEvidence job { get; } = new();
    public CleanupJournal cleanup { get; } = new();
    public List<JsonElement> events { get; } = [];
    public List<object> launch_facts { get; } = [];
    public List<string> failures { get; } = [];
    public string stage { get; private set; } = "not_run";
    public bool no_child_created_proven { get; private set; } = true;
    public bool stdout_eof { get; private set; }
    public bool stdout_capture_unavailable { get; private set; }
    public bool retired { get; private set; }
    public bool observation_budget_exceeded { get; private set; }
    public bool terminal_received { get; private set; }
    public string expected_station => station;
    public string expected_desktop => desktop;
    public bool initial_suspended_identity_job_verified { get; private set; }
    public List<object> original_thread_resume_attempts { get; } = [];
    public bool loader_completed { get; private set; }
    public string? failed_stage { get; private set; }
    public uint? native_error { get; private set; }
    public uint? original_exit_code => audit.Held.FirstOrDefault(p => p.pid == audit.RootPid)?.exit_code;
    public bool group_empty_proven => no_child_created_proven && (!job.created || job.Empty) || job.Empty && audit.Held.Count > 0 && audit.AllSignaled && (stdout_eof || stdout_capture_unavailable);
    public bool OS_identity_proven => audit.Held.Count > 0 && !audit.ObservationGap && (packageText is null ?
        audit.Held.All(p => p.token is { is_appcontainer: 0, integrity_rid: 8192, capability_count: 0 }) : audit.Held.All(audit.BoundRestricted));
    public bool diagnostic_execution_completed => retired && group_empty_proven && stdout_eof && !stdout_capture_unavailable && OS_identity_proven && initial_suspended_identity_job_verified && terminal_received &&
        !observation_budget_exceeded && failures.Count == 0 && !job.query_failure_seen && !job.membership_failure_seen &&
        (loader_completed ? original_exit_code == 0 : original_exit_code == 20);
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> lines = new();
    private FileStream? output;
    private SafeFileHandle? outputHandle;
    private volatile bool captureDone, captureFailed;
    private long outputBytes;
    private int protocolStep;
    private string? pendingFailureStage;
    private uint pendingFailureCode;

    internal async Task RunAsync()
    {
        try { Start(); }
        catch (Exception ex) { failures.Add(Program.FailureCode(ex)); stage = "launch_or_capture_failed"; }
        await EnsureRetiredAsync();
    }
    internal async Task EnsureRetiredAsync()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        // No time budget closes a live handle, frees a profile or starts the
        // next case. Pending remains visible until exact kernel proof exists.
        while (!group_empty_proven)
        {
            Tick();
            if (clock.Elapsed > TimeSpan.FromSeconds(30)) { observation_budget_exceeded = true; stage = "pending_exact_job_and_original_process_exit"; }
            save(); await Task.Delay(100);
        }
        Tick();
        while (!retired)
        {
            Retire(); if (!retired) { stage = "pending_owned_resource_release"; save(); await Task.Delay(250); }
        }
        stage = "complete"; save();
    }
    private void Start()
    {
        IntPtr read = IntPtr.Zero, write = IntPtr.Zero, list = IntPtr.Zero, handleList = IntPtr.Zero, jobList = IntPtr.Zero, caps = IntPtr.Zero, env = IntPtr.Zero, desk = IntPtr.Zero;
        bool initialized = false, launchAttempted = false, captureStarted = false, launched = false, resumed = false;
        Native.ProcessInformation process = default;
        try
        {
            bool desktopOnly = station == "WinSta0" && ExactGuidName(desktop, "AutumnOS_DesktopLoader_");
            bool privateOnly = ExactGuidName(station, "AutumnOS_ClipboardProbe_") && ExactGuidName(desktop, "AutumnOS_ClipboardDesktop_");
            if (!desktopOnly && !privateOnly) throw new OwnedAssertionException("FIXED_NATIVE_STATION_DESKTOP_CONTEXT_REQUIRED");
            stage = "create_new_default_job"; job.Create();
            var attributes = new Native.SecurityAttributes { Length = (uint)Marshal.SizeOf<Native.SecurityAttributes>(), Inherit = true };
            Native.Check(Native.CreatePipe(out read, out write, ref attributes, 4096), "create_owned_stdout_pipe");
            Native.Check(Native.SetHandleInformation(read, 1, 0), "stdout_read_not_inherited");
            int count = package == IntPtr.Zero ? 2 : 3; nuint bytes = 0;
            bool first = Native.InitializeProcThreadAttributeList(IntPtr.Zero, count, 0, ref bytes); int firstError = Marshal.GetLastWin32Error();
            if (first || firstError != 122 || bytes < 32 || bytes > 65536) throw new InvalidDataException("ATTRIBUTE_SIZE_QUERY");
            list = Marshal.AllocHGlobal(checked((int)bytes)); Native.Check(Native.InitializeProcThreadAttributeList(list, count, 0, ref bytes), "initialize_native_attributes"); initialized = true;
            handleList = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(handleList, write);
            Native.Check(Native.UpdateProcThreadAttribute(list, 0, Native.HandleList, handleList, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero), "only_owned_stdout_handle_inherited");
            jobList = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobList, job.Handle);
            Native.Check(Native.UpdateProcThreadAttribute(list, 0, JobEvidence.JobListAttribute, jobList, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero), "atomic_new_job_assignment"); job.MarkAtomicLaunchAttached();
            if (package != IntPtr.Zero)
            {
                caps = Marshal.AllocHGlobal(Marshal.SizeOf<Native.SecurityCapabilities>()); Marshal.StructureToPtr(new Native.SecurityCapabilities { AppContainerSid = package }, caps, false);
                Native.Check(Native.UpdateProcThreadAttribute(list, 0, Native.SecurityCapabilitiesAttribute, caps, (nuint)Marshal.SizeOf<Native.SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero), "zero_capability_exact_package");
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            OwnedFiles.FixedLocal(local); OwnedFiles.PlainAncestors(local);
            var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows), ["LOCALAPPDATA"] = local, ["TEMP"] = temp, ["TMP"] = temp,
                ["AUTUMN_PROBE_NONCE"] = nonce, ["AUTUMN_PROBE_SIDE"] = side, ["AUTUMN_PROBE_STATION"] = station, ["AUTUMN_PROBE_DESKTOP"] = desktop };
            env = Marshal.StringToHGlobalUni(string.Concat(values.Select(p => p.Key + "=" + p.Value + "\0")) + "\0"); desk = Marshal.StringToHGlobalUni(station + "\\" + desktop);
            var start = new Native.StartupInfoEx { Startup = new Native.StartupInfo { Size = (uint)Marshal.SizeOf<Native.StartupInfoEx>(), Desktop = desk,
                Flags = 0x100, StdInput = IntPtr.Zero, StdOutput = write, StdError = write }, Attributes = list };
            string executable = Path.Combine(directory, entry); stage = "CreateProcess_native_only"; no_child_created_proven = false; launchAttempted = true;
            bool success = Native.CreateProcessW(executable, new StringBuilder("\"" + executable + "\""), IntPtr.Zero, IntPtr.Zero, true,
                0x08000000 | 0x00080000 | 0x00000400 | 0x00000004, env, directory, ref start, out process);
            int error = success ? 0 : Marshal.GetLastWin32Error();
            launch_facts.Add(new { succeeded = success, native_error = error, pid = process.ProcessId, tid = process.ThreadId,
                process_handle_zero = process.Process == IntPtr.Zero, thread_handle_zero = process.Thread == IntPtr.Zero,
                inherited_stdout_handles = 1, inherited_stdin_handles = 0, inherited_job_or_desktop_handles = 0,
                capability_count = 0, atomic_job = true, create_suspended = true, creation_flags = "0x08080404",
                exact_desktop = desktop, station, environment_keys = values.Keys.ToArray() });
            if (!success)
            {
                no_child_created_proven = process.Process == IntPtr.Zero && process.Thread == IntPtr.Zero && process.ProcessId == 0 && process.ThreadId == 0;
                throw new OwnedNativeException(error, "CreateProcess_native_only");
            }
            launched = true;
            audit.AdoptRoot(process.Process, process.ProcessId); process.Process = IntPtr.Zero;
            job.Observe(audit);
            HeldProcess root = audit.Held.Single(p => p.pid == audit.RootPid);
            initial_suspended_identity_job_verified = OS_identity_proven && job.Contains(root.Handle, root.pid, "original_suspended_native_before_resume") &&
                !job.query_failure_seen && !job.membership_failure_seen;
            if (!initial_suspended_identity_job_verified) throw new OwnedAssertionException("SUSPENDED_NATIVE_OS_TOKEN_JOB_BINDING_FAILED");
            Native.CloseHandle(write); write = IntPtr.Zero;
            outputHandle = new SafeFileHandle(read, true); read = IntPtr.Zero; output = new FileStream(outputHandle, FileAccess.Read);
            FileStream pipe = output; _ = Task.Run(() => Capture(pipe)); captureStarted = true;
            resumed = ResumeOriginal(process.Thread);
        }
        catch
        { if (!launchAttempted) no_child_created_proven = true; if (!captureStarted) { captureFailed = true; captureDone = true; stdout_capture_unavailable = true; } throw; }
        finally
        {
            if (process.Process != IntPtr.Zero) { audit.AdoptRoot(process.Process, process.ProcessId); process.Process = IntPtr.Zero; }
            // Even a pre-resume validation/capture error must let this exact
            // fixed metadata child finish naturally. No live thread handle is
            // abandoned and no process is killed on an observation budget.
            while (launched && !resumed && process.Thread != IntPtr.Zero)
            { resumed = ResumeOriginal(process.Thread); if (!resumed) { stage = "pending_original_suspended_thread_resume"; save(); Thread.Sleep(250); } }
            if (process.Thread != IntPtr.Zero) Native.CloseHandle(process.Thread);
            foreach (IntPtr h in new[] { read, write }) if (h != IntPtr.Zero) Native.CloseHandle(h);
            if (initialized) Native.DeleteProcThreadAttributeList(list);
            foreach (IntPtr memory in new[] { list, handleList, jobList, caps, env, desk }) if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
        }
    }
    private void Tick()
    {
        if (retired) return; audit.Snapshot(); job.Observe(audit);
        while (lines.TryDequeue(out byte[]? line)) ParseProtocolLine(line);
        stdout_eof = captureDone && !stdout_capture_unavailable && lines.IsEmpty;
        if (captureFailed && !failures.Contains("STDOUT_IO_ENCODING_OR_SIZE_FAILURE")) failures.Add("STDOUT_IO_ENCODING_OR_SIZE_FAILURE");
    }
    internal void ParseProtocolLine(byte[] line)
        {
            try
            {
                using var document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(line)); var root = document.RootElement;
                if (root.GetProperty("schema_version").GetInt32() != 1 || root.GetProperty("nonce").GetString() != nonce ||
                    root.GetProperty("host_pid").GetUInt32() != audit.RootPid || root.GetProperty("isolation_acceptance").GetString() != "not_claimed_host_output_only") throw new InvalidDataException("NATIVE_ENVELOPE_BINDING");
                string kind = root.GetProperty("kind").GetString() ?? ""; var detail = root.GetProperty("detail");
                if (terminal_received || kind is not ("native_loader_stage" or "native_loader_complete")) throw new InvalidDataException("NATIVE_EVENT_ORDER");
                if (events.Count >= 32) throw new InvalidDataException("NATIVE_EVENT_LIMIT");
                string nativeStage = detail.GetProperty("stage").GetString() ?? "", result = detail.GetProperty("result").GetString() ?? "";
                uint error = detail.GetProperty("native_error").GetUInt32();
                if (kind == "native_loader_complete")
                {
                    terminal_received = true; loader_completed = detail.GetProperty("completed").GetBoolean(); failed_stage = detail.GetProperty("failed_stage").GetString(); native_error = detail.GetProperty("native_error").GetUInt32();
                    if (detail.GetProperty("stage").GetString() != "complete" || detail.GetProperty("result").GetString() != (loader_completed ? "passed" : "failed") ||
                        (loader_completed ? failed_stage != "none" || native_error != 0 || protocolStep != SuccessfulStages.Length || pendingFailureStage is not null :
                            pendingFailureStage is null || failed_stage != pendingFailureStage || native_error != pendingFailureCode)) throw new InvalidDataException("NATIVE_TERMINAL_CONTRADICTION");
                }
                else
                {
                    if (pendingFailureStage is not null || protocolStep >= SuccessfulStages.Length) throw new InvalidDataException("NATIVE_STAGE_AFTER_FAILURE_OR_COMPLETION");
                    var expected = SuccessfulStages[protocolStep];
                    if (nativeStage != expected.stage) throw new InvalidDataException("NATIVE_REQUIRED_STAGE_PREFIX_MISSING");
                    if (result == expected.result)
                    { if (error != 0) throw new InvalidDataException("SUCCESS_NATIVE_ERROR_MUST_BE_ZERO"); protocolStep++; }
                    else if (AllowedFailure(nativeStage, result) && (!nativeStage.StartsWith("load_", StringComparison.Ordinal) || expected.result == "loaded"))
                    { pendingFailureStage = nativeStage; pendingFailureCode = error; }
                    else throw new InvalidDataException("NATIVE_UNEXPECTED_STAGE_RESULT");
                }
                events.Add(root.Clone());
            }
            catch (Exception ex) { if (failures.Count < 64) failures.Add(Program.FailureCode(ex)); }
        }
    private static readonly (string stage, string result)[] SuccessfulStages =
    [ ("native_entry", "entered"), ("environment_binding", "validated_not_identity_proof"),
      ("load_user32", "loading"), ("load_user32", "loaded"), ("user32_metadata_exports", "resolved"),
      ("user32_binding", "exact_station_and_desktop_matched"), ("load_gdi32", "loading"), ("load_gdi32", "loaded"),
      ("load_shell32", "loading"), ("load_shell32", "loaded") ];
    private static bool ExactGuidName(string value, string prefix) => value.StartsWith(prefix, StringComparison.Ordinal) &&
        value.Length == prefix.Length + 32 && value[prefix.Length..].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool AllowedFailure(string stage, string result) => stage switch
    {
        "environment_binding" => result is "invalid_nonce" or "invalid_side" or "invalid_station" or "invalid_desktop",
        "load_user32" or "load_gdi32" or "load_shell32" => result == "failed",
        "user32_metadata_exports" => result is "missing_station_export" or "missing_desktop_export" or "missing_object_info_export",
        "user32_binding" => result is "current_station_unavailable" or "current_desktop_unavailable" or "station_name_query_failed" or
            "station_name_mismatch" or "desktop_name_query_failed" or "desktop_name_mismatch",
        _ => false
    };
    private bool ResumeOriginal(IntPtr thread)
    {
        uint previous = ResumeThread(thread); int error = previous == uint.MaxValue ? Marshal.GetLastWin32Error() : 0;
        original_thread_resume_attempts.Add(new { previous_suspend_count = previous, native_error = error, original_CreateProcess_thread_handle = true });
        if (previous == uint.MaxValue) { if (failures.Count < 64) failures.Add("ORIGINAL_THREAD_RESUME_FAILED:" + error); return false; }
        if (previous != 1 && !failures.Contains("UNEXPECTED_ORIGINAL_THREAD_SUSPEND_COUNT")) failures.Add("UNEXPECTED_ORIGINAL_THREAD_SUSPEND_COUNT");
        return previous <= 1;
    }
    private void Capture(FileStream pipe)
    {
        try
        {
            using (pipe) using (MemoryStream line = new())
            {
                int value;
                while ((value = pipe.ReadByte()) >= 0)
                {
                    if (++outputBytes > 65536) { captureFailed = true; continue; }
                    if (value == 10) { lines.Enqueue(line.ToArray()); line.SetLength(0); continue; }
                    if (line.Length >= 4096) { captureFailed = true; continue; } line.WriteByte((byte)value);
                }
                if (line.Length != 0) captureFailed = true;
            }
        }
        catch { captureFailed = true; }
        finally { captureDone = true; }
    }
    private void Retire()
    {
        if (!group_empty_proven) return;
        if (job.created && !job.original_handle_closed_after_empty) cleanup.Attempt("empty_job_original_handle_close", job.CloseAfterEmpty);
        if (output is not null) cleanup.Attempt("stdout_EOF_transport_dispose", output.Dispose);
        if (outputHandle is not null) cleanup.Attempt("stdout_original_safe_handle_close", outputHandle.Dispose);
        foreach (var p in audit.Held.Where(p => p.original_handle_signaled && p.Handle != IntPtr.Zero)) cleanup.Attempt("signaled_original_process_handle_close", () =>
        { Native.Check(Native.CloseHandle(p.Handle), "close_original_signaled_native_process"); p.Handle = IntPtr.Zero; });
        retired = (!job.created || job.original_handle_closed_after_empty) && (outputHandle is null || outputHandle.IsClosed) && audit.Held.All(p => p.Handle == IntPtr.Zero);
        if (cleanup.any_failure && !failures.Contains("OWNED_CHILD_CLEANUP_ERROR")) failures.Add("OWNED_CHILD_CLEANUP_ERROR");
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
}
