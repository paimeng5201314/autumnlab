using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.OwnedBrowserParent;

internal sealed record PasteEventFact(int event_index, bool trusted, string text_sha256, bool text_empty);

internal sealed class PipeChild(string kind, string nonce, string side, string operation, string host, string runtime,
    string udf, string temp, string executable, IntPtr packageSid, string? sidText, StationLease lease)
{
    public string kind { get; } = kind;
    public string nonce { get; } = nonce;
    public string side { get; } = side;
    public string operation { get; } = operation;
    public ProcessAudit audit { get; } = new(host, runtime, sidText);
    public JobEvidence job { get; } = new();
    public List<JsonElement> events { get; } = [];
    public List<object> launch_facts { get; } = [];
    public List<string> failures { get; } = [];
    public CleanupJournal cleanup { get; } = new();
    public string stage { get; private set; } = "not_run";
    public bool stdio_complete { get; private set; }
    public bool no_child_created_proven { get; private set; } = true;
    public bool close_requested { get; private set; }
    public uint browser_pid { get; private set; }
    public bool browser_normal_event { get; private set; }
    public bool final_sdk_empty { get; private set; }
    public bool host_complete_received { get; private set; }
    public bool retired { get; private set; }
    public bool browser_token_verified_before_load { get; private set; }
    public bool failure_cleanup_requested { get; private set; }
    public List<PasteEventFact> paste_events { get; } = [];
    public string paste_event_digest_sha256 => OwnedFiles.HashText(string.Concat(paste_events.Select(e =>
        e.event_index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (e.trusted ? "1" : "0") + "|" + (e.text_empty ? "1" : "0") + "|" + e.text_sha256 + "\n")));
    public bool paste_terminal_summary_matches { get; private set; }
    public bool paste_capture_complete { get; private set; }
    public bool paste_final_snapshot_observed { get; private set; }
    public string? paste_final_value_sha256 { get; private set; }
    public bool? paste_final_value_empty { get; private set; }
    public bool operation_command_sent { get; private set; }
    public string? expected_paste_baseline_sha256 { get; private set; }
    public bool native_loader_report_received { get; private set; }
    public bool native_loader_completed { get; private set; }
    public string? native_loader_failed_stage { get; private set; }
    public uint? native_loader_native_error { get; private set; }
    public string? native_loader_last_stage { get; private set; }
    public string? native_loader_last_result { get; private set; }
    public uint? native_loader_last_native_error { get; private set; }
    public bool output_drained_after_EOF => stdio_complete && lines.IsEmpty;
    public bool original_group_exit_proven => no_child_created_proven && (!job.created || job.Empty) ||
        audit.Held.Count > 0 && audit.AllSignaled && job.Empty &&
        (kind == "collector" || browser_pid == 0 || browser_normal_event && final_sdk_empty && host_complete_received);
    public bool kernel_group_empty_proven => no_child_created_proven && (!job.created || job.Empty) ||
        audit.Held.Count > 0 && audit.AllSignaled && stdio_complete && job.Empty &&
        job.atomic_launch_attribute_attached && !job.membership_failure_seen;
    public bool resource_cleanup_proven => no_child_created_proven && (!job.created || job.Empty) ||
        stdio_complete && (original_group_exit_proven || failure_cleanup_requested && kernel_group_empty_proven);
    public string cleanup_evidence => original_group_exit_proven ? "normal_or_no_browser_original_group_exit_proven" :
        failure_cleanup_requested && kernel_group_empty_proven ? "failed_case_kernel_group_empty" : "not_proven";
    public bool host_exit_zero => audit.Held.FirstOrDefault(p => p.pid == audit.RootPid)?.exit_code == 0;
    private readonly List<JsonElement> unread = [];
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> lines = new();
    private StreamWriter? input;
    private FileStream? inputTransport;
    private SafeFileHandle? inputTransportHandle;
    private FileStream? outputTransport;
    private SafeFileHandle? outputTransportHandle;
    private volatile bool captureDone, captureFailed;
    private bool captureStarted;
    private long outputBytes;

    internal void Start()
    {
        IntPtr read = IntPtr.Zero, write = IntPtr.Zero, stdinRead = IntPtr.Zero, stdinWrite = IntPtr.Zero;
        IntPtr list = IntPtr.Zero, handles = IntPtr.Zero, caps = IntPtr.Zero, env = IntPtr.Zero, desktop = IntPtr.Zero;
        IntPtr inheritedHolder = IntPtr.Zero;
        IntPtr jobList = IntPtr.Zero;
        bool initialized = false, attempted = false; Native.ProcessInformation child = default;
        try
        {
            lease.CheckLive();
            stage = "create_new_unnamed_child_job"; job.Create();
            var security = new Native.SecurityAttributes { Length = (uint)Marshal.SizeOf<Native.SecurityAttributes>(), Inherit = true };
            stage = "create_output_pipe"; Native.Check(Native.CreatePipe(out read, out write, ref security, 4096), stage);
            stage = "output_read_not_inherited"; Native.Check(Native.SetHandleInformation(read, 1, 0), stage);
            stage = "create_input_pipe"; Native.Check(Native.CreatePipe(out stdinRead, out stdinWrite, ref security, 4096), stage);
            stage = "input_write_not_inherited"; Native.Check(Native.SetHandleInformation(stdinWrite, 1, 0), stage);
            int count = packageSid == IntPtr.Zero ? 2 : 3; nuint bytes = 0;
            stage = "attribute_list_size";
            bool first = Native.InitializeProcThreadAttributeList(IntPtr.Zero, count, 0, ref bytes); int error = Marshal.GetLastWin32Error();
            if (first || error != 122 || bytes < 32 || bytes > 65536) throw new InvalidDataException("ATTRIBUTE_SIZE");
            list = Marshal.AllocHGlobal(checked((int)bytes)); stage = "initialize_attributes";
            Native.Check(Native.InitializeProcThreadAttributeList(list, count, 0, ref bytes), stage); initialized = true;
            stage = "duplicate_original_holder_SYNCHRONIZE_only";
            Native.Check(DuplicateHandle(Native.GetCurrentProcess(), lease.HolderHandle, Native.GetCurrentProcess(), out inheritedHolder,
                Native.Synchronize, true, 0), stage);
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size); Marshal.WriteIntPtr(handles, write); Marshal.WriteIntPtr(handles, IntPtr.Size, stdinRead);
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, inheritedHolder);
            stage = "explicit_two_owned_pipe_handles_and_original_holder_wait_only_handle";
            Native.Check(Native.UpdateProcThreadAttribute(list, 0, Native.HandleList, handles, 3 * (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero), stage);
            jobList = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobList, job.Handle);
            stage = "atomic_PROC_THREAD_ATTRIBUTE_JOB_LIST";
            Native.Check(Native.UpdateProcThreadAttribute(list, 0, JobEvidence.JobListAttribute, jobList, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero), stage);
            job.MarkAtomicLaunchAttached();
            if (packageSid != IntPtr.Zero)
            {
                caps = Marshal.AllocHGlobal(Marshal.SizeOf<Native.SecurityCapabilities>());
                Marshal.StructureToPtr(new Native.SecurityCapabilities { AppContainerSid = packageSid }, caps, false);
                stage = "zero_capability_appcontainer";
                Native.Check(Native.UpdateProcThreadAttribute(list, 0, Native.SecurityCapabilitiesAttribute, caps,
                    (nuint)Marshal.SizeOf<Native.SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero), stage);
            }
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            OwnedFiles.FixedLocal(local); OwnedFiles.PlainAncestors(local);
            var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows), ["LOCALAPPDATA"] = local,
                ["TEMP"] = temp, ["TMP"] = temp, ["DOTNET_EnableDiagnostics"] = "0", ["COMPlus_EnableDiagnostics"] = "0" };
            if (kind == "native")
            {
                values["AUTUMN_PROBE_NONCE"] = nonce; values["AUTUMN_PROBE_SIDE"] = side;
                values["AUTUMN_PROBE_STATION"] = lease.station_name; values["AUTUMN_PROBE_DESKTOP"] = lease.desktop_name;
            }
            env = Marshal.StringToHGlobalUni(string.Concat(values.Select(p => p.Key + "=" + p.Value + "\0")) + "\0");
            desktop = Marshal.StringToHGlobalUni(lease.DesktopBinding);
            var start = new Native.StartupInfoEx { Startup = new Native.StartupInfo { Size = (uint)Marshal.SizeOf<Native.StartupInfoEx>(),
                Desktop = desktop, Flags = 0x100, StdInput = stdinRead, StdOutput = write, StdError = write }, Attributes = list };
            string arguments = kind == "native" ? " --native-loader " + nonce : kind == "collector" ?
                " --collector " + nonce + " " + Quote(lease.station_name) + " " + Quote(lease.desktop_name) + " " + operation + " " + side :
                " --browser " + nonce + " " + Quote(runtime) + " " + Quote(udf) + " " + Quote(lease.station_name) + " " + Quote(lease.desktop_name) + " " + operation;
            arguments += " " + lease.holder_pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " +
                lease.holder_creation_filetime.ToString(System.Globalization.CultureInfo.InvariantCulture) + " " +
                inheritedHolder.ToInt64().ToString(System.Globalization.CultureInfo.InvariantCulture);
            stage = "CreateProcess_exact_desktop"; attempted = true; no_child_created_proven = false;
            bool launched = Native.CreateProcessW(executable, new StringBuilder(Quote(executable) + arguments), IntPtr.Zero, IntPtr.Zero,
                true, 0x00080000 | 0x00000400 | 0x08000000, env, host, ref start, out child);
            int launchError = launched ? 0 : Marshal.GetLastWin32Error();
            launch_facts.Add(new { succeeded = launched, native_error = launchError, returned_pid = child.ProcessId,
                returned_tid = child.ThreadId, process_handle_zero = child.Process == IntPtr.Zero, thread_handle_zero = child.Thread == IntPtr.Zero,
                desktop_binding = lease.DesktopBinding, explicit_inherited_pipe_count = 2,
                explicit_inherited_original_holder_wait_only_handles = 1, holder_granted_access_requested = "0x00100000", capability_count = 0,
                environment_keys = values.Keys.ToArray(), creation_flags = "0x08080400", atomic_job_list_required = true });
            if (!launched)
            {
                no_child_created_proven = child.Process == IntPtr.Zero && child.Thread == IntPtr.Zero && child.ProcessId == 0 && child.ThreadId == 0;
                if (child.Process != IntPtr.Zero) { audit.AdoptRoot(child.Process, child.ProcessId); child.Process = IntPtr.Zero; }
                throw new OwnedNativeException(launchError, stage);
            }
            audit.AdoptRoot(child.Process, child.ProcessId); child.Process = IntPtr.Zero;
            job.Observe(audit);
            Native.CloseHandle(child.Thread); child.Thread = IntPtr.Zero;
            Native.CloseHandle(write); write = IntPtr.Zero; Native.CloseHandle(stdinRead); stdinRead = IntPtr.Zero;
            if (kind == "native")
            {
                // The fixed kernel-only diagnostic does not consume commands.
                // Close this parent's unused write endpoint without a text
                // writer/final flush; the child receives only ordinary EOF.
                Native.Check(Native.CloseHandle(stdinWrite), "close_unused_native_stdin_writer"); stdinWrite = IntPtr.Zero;
            }
            else
            {
                inputTransportHandle = new SafeFileHandle(stdinWrite, true); stdinWrite = IntPtr.Zero;
                inputTransport = new FileStream(inputTransportHandle, FileAccess.Write);
                input = new StreamWriter(inputTransport, new UTF8Encoding(false), 1024, leaveOpen: true) { AutoFlush = true };
            }
            outputTransportHandle = new SafeFileHandle(read, true); read = IntPtr.Zero;
            outputTransport = new FileStream(outputTransportHandle, FileAccess.Read); FileStream pipe = outputTransport;
            _ = Task.Run(() => Capture(pipe)); captureStarted = true; stage = "running";
        }
        catch (Exception ex)
        {
            failures.Add(Program.FailureCode(ex)); if (!attempted) no_child_created_proven = true;
            if (!captureStarted) { captureFailed = true; captureDone = true; stdio_complete = true; }
            throw;
        }
        finally
        {
            // A partial failed launch with any identity is unproven. Original
            // process handles are adopted, not closed or replaced by PID.
            if (child.Process != IntPtr.Zero) { audit.AdoptRoot(child.Process, child.ProcessId); child.Process = IntPtr.Zero; }
            if (child.Thread != IntPtr.Zero) Native.CloseHandle(child.Thread);
            foreach (IntPtr handle in new[] { read, write, stdinRead, stdinWrite, inheritedHolder }) if (handle != IntPtr.Zero) Native.CloseHandle(handle);
            if (initialized) Native.DeleteProcThreadAttributeList(list);
            foreach (IntPtr memory in new[] { list, handles, caps, env, desktop, jobList }) if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
        }
    }
    internal void Send(string command, int? sample = null)
    {
        if (command == "perform") operation_command_sent = true;
        if (command == "close") { if (close_requested) return; close_requested = true; }
        if (input is null) return;
        try
        {
            string text = sample is null ? JsonSerializer.Serialize(new { schema_version = 1, nonce, command }) :
                JsonSerializer.Serialize(new { schema_version = 1, nonce, command, sample_index = sample.Value });
            if (Encoding.UTF8.GetByteCount(text) + 1 > 1024) throw new InvalidDataException("COMMAND_SIZE");
            input.WriteLine(text);
        }
        catch (Exception ex) { failures.Add("COMMAND_WRITE_" + Program.FailureCode(ex)); }
    }
    internal void BindNativePasteBaselineHash(string hash)
    {
        if (expected_paste_baseline_sha256 is not null || !ValidHash(hash)) throw new InvalidDataException("PASTE_BASELINE_HASH_BIND_ONCE");
        expected_paste_baseline_sha256 = hash;
    }
    internal void EnterFailureCleanup() => failure_cleanup_requested = true;
    internal void Tick()
    {
        if (retired) return;
        audit.Snapshot();
        job.Observe(audit);
        while (lines.TryDequeue(out byte[]? line)) ParseProtocolLine(line);
        if (captureFailed && !failures.Contains("BOUNDED_STDIO_FAILED")) failures.Add("BOUNDED_STDIO_FAILED");
        if ((job.query_failure_seen || job.membership_failure_seen) && !failures.Contains("EXACT_JOB_QUERY_OR_MEMBERSHIP_FAILED")) failures.Add("EXACT_JOB_QUERY_OR_MEMBERSHIP_FAILED");
        stdio_complete = captureDone;
    }
    internal void ParseProtocolLine(byte[] line)
    {
            try
            {
                using var document = JsonDocument.Parse(new UTF8Encoding(false, true).GetString(line)); var root = document.RootElement;
                if (root.GetProperty("schema_version").GetInt32() != 1 || root.GetProperty("nonce").GetString() != nonce ||
                    root.GetProperty("host_pid").GetUInt32() != audit.RootPid || root.GetProperty("isolation_acceptance").GetString() != "not_claimed_host_output_only")
                    throw new InvalidDataException("PEER_ENVELOPE_IDENTITY");
                string name = root.GetProperty("kind").GetString() ?? ""; JsonElement detail = root.GetProperty("detail");
                if (kind == "native")
                {
                    if (name is not ("native_loader_stage" or "native_loader_complete") || native_loader_report_received)
                        throw new InvalidDataException("NATIVE_LOADER_PROTOCOL_STATE");
                    string nativeStage = detail.GetProperty("stage").GetString() ?? "", result = detail.GetProperty("result").GetString() ?? "";
                    if (!NativeLoaderStage(nativeStage) || !NativeLoaderResult(nativeStage, result))
                        throw new InvalidDataException("NATIVE_LOADER_FIXED_STAGE_RESULT_REQUIRED");
                    uint nativeError = detail.GetProperty("native_error").GetUInt32();
                    native_loader_last_stage = nativeStage; native_loader_last_result = result; native_loader_last_native_error = nativeError;
                    if (name == "native_loader_complete")
                    {
                        string failedStage = detail.GetProperty("failed_stage").GetString() ?? "";
                        native_loader_report_received = true; native_loader_completed = detail.GetProperty("completed").GetBoolean();
                        native_loader_failed_stage = failedStage; native_loader_native_error = nativeError;
                        if (nativeStage != "complete" || native_loader_completed != (result == "passed") ||
                            (native_loader_completed ? failedStage != "none" || nativeError != 0 : !NativeLoaderStage(failedStage)))
                            throw new InvalidDataException("NATIVE_LOADER_TERMINAL_FACTS_CONTRADICT");
                    }
                }
                if (name is "process_inventory")
                {
                    var rows = detail.GetProperty("rows"); if (rows.GetArrayLength() > 128 || detail.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean()) throw new InvalidDataException("PROCESS_INVENTORY_LIMIT");
                    foreach (var row in rows.EnumerateArray()) audit.Observe(row.GetProperty("pid").GetUInt32(), row.GetProperty("kind").GetString() ?? "unknown", "SDK_inventory_plus_OS_query");
                }
                if (name == "controller_ready")
                {
                    if (browser_pid != 0) throw new InvalidDataException("DUPLICATE_BROWSER_IDENTITY");
                    browser_pid = detail.GetProperty("browser_pid").GetUInt32();
                    var held = audit.Observe(browser_pid, "Browser", "controller_ready_original_OS_handle");
                    string udfHash = "sha256:" + OwnedFiles.HashText(Path.TrimEndingDirectorySeparator(Path.GetFullPath(udf)).ToUpperInvariant());
                    bool fixedBindings = detail.GetProperty("browser_version").GetString() == "154.0.4258.53" && detail.GetProperty("policy_id").GetString() == "autumn-local-web-v2" &&
                        detail.GetProperty("udf_matches_requested").GetBoolean() && detail.GetProperty("udf_binding_sha256").GetString() == udfHash && detail.GetProperty("in_private").GetBoolean();
                    bool tokens = sidText is null ? held?.token?.is_appcontainer == 0 && audit.Held.FirstOrDefault(p => p.pid == audit.RootPid)?.token?.is_appcontainer == 0 : audit.RestrictedObservedTokensPass;
                    bool exactJob = held is not null && job.Contains(held.Handle, browser_pid, "actual_browser_before_page_load");
                    browser_token_verified_before_load = fixedBindings && tokens && exactJob && !audit.ObservationGap &&
                        !job.query_failure_seen && !job.membership_failure_seen && held?.image_relative.Equals("runtime/msedgewebview2.exe", StringComparison.OrdinalIgnoreCase) == true;
                }
                if (name == "browser_group_exit")
                    browser_normal_event |= browser_pid != 0 && detail.GetProperty("pid").GetUInt32() == browser_pid && detail.GetProperty("kind").GetString() == "Normal";
                if (name == "paste_event_data")
                {
                    if (kind != "browser" || operation != "paste" || host_complete_received) throw new InvalidDataException("PASTE_EVENT_OUTSIDE_ACTIVE_BROWSER_CAPTURE");
                    int index = detail.GetProperty("event_index").GetInt32(); string hash = detail.GetProperty("text_sha256").GetString() ?? "";
                    bool trusted = detail.GetProperty("trusted").GetBoolean(), empty = detail.GetProperty("text_empty").GetBoolean();
                    if (paste_events.Count >= 128 || index != paste_events.Count + 1 || !ValidHash(hash) || empty != (hash == OwnedFiles.HashText("")))
                        throw new InvalidDataException("PASTE_EVENT_INDEX_HASH_OR_EMPTY_PROOF");
                    paste_events.Add(new PasteEventFact(index, trusted, hash, empty));
                    if (!trusted) failures.Add("PASTE_EVENT_UNTRUSTED");
                    if (side == "B" && !empty) failures.Add("RESTRICTED_BROWSER_ACTUAL_PASTE_DATA_LEAK");
                    if (side == "A" && !empty && hash != expected_paste_baseline_sha256) failures.Add("ORDINARY_PASTE_EVENT_HASH_DOES_NOT_MATCH_NATIVE_ONLY_BASELINE");
                }
                if (name == "host_complete")
                {
                    if (host_complete_received) throw new InvalidDataException("DUPLICATE_HOST_COMPLETE");
                    host_complete_received = true; final_sdk_empty = detail.GetProperty("environment_process_count").GetInt32() == 0 && detail.GetProperty("browser_pid").GetUInt32() == browser_pid;
                    paste_terminal_summary_matches = detail.GetProperty("paste_event_count").GetInt32() == paste_events.Count &&
                        detail.GetProperty("paste_nonempty_event_count").GetInt32() == paste_events.Count(e => !e.text_empty) &&
                        detail.GetProperty("paste_untrusted_event_count").GetInt32() == paste_events.Count(e => !e.trusted) &&
                        detail.GetProperty("paste_event_digest_sha256").GetString() == paste_event_digest_sha256;
                    paste_capture_complete = detail.GetProperty("paste_capture_complete").GetBoolean();
                    paste_final_snapshot_observed = detail.GetProperty("paste_final_snapshot_observed").GetBoolean();
                    paste_final_value_sha256 = detail.GetProperty("paste_final_value_sha256").GetString();
                    var finalEmpty = detail.GetProperty("paste_final_value_empty");
                    paste_final_value_empty = finalEmpty.ValueKind == JsonValueKind.Null ? null : finalEmpty.GetBoolean();
                    if (!paste_terminal_summary_matches) failures.Add("TERMINAL_PASTE_ACCUMULATION_MISMATCH");
                    if (operation == "paste" && operation_command_sent)
                    {
                        if (!paste_capture_complete || !paste_final_snapshot_observed) failures.Add("FINAL_PASTE_CAPTURE_HANDSHAKE_NOT_PROVEN");
                        if (side == "B" && paste_events.Any(e => !e.text_empty || !e.trusted)) failures.Add("RESTRICTED_PASTE_FINAL_EVENT_BOUNDARY_FAILED");
                        if (side == "B" && (paste_final_value_empty != true || paste_final_value_sha256 != OwnedFiles.HashText("")))
                            failures.Add("RESTRICTED_PASTE_FINAL_DOM_VALUE_NOT_PROVEN_EMPTY");
                        if (side == "A" && !paste_events.Any(e => e.trusted && !e.text_empty && e.text_sha256 == expected_paste_baseline_sha256))
                            failures.Add("ORDINARY_PASTE_FINAL_TRUSTED_READ_POSITIVE_CONTROL_MISSING");
                        if (side == "A" && (paste_final_value_empty != false || paste_final_value_sha256 != expected_paste_baseline_sha256))
                            failures.Add("ORDINARY_PASTE_FINAL_DOM_VALUE_NOT_NATIVE_BASELINE");
                    }
                }
                if (name.Contains("fail", StringComparison.OrdinalIgnoreCase) || name.Contains("budget", StringComparison.OrdinalIgnoreCase) || name is "launch_refused" or "command_rejected") failures.Add("CHILD_" + name);
                if (events.Count >= 2048) throw new InvalidDataException("EVENT_COUNT_LIMIT");
                var owned = root.Clone(); events.Add(owned); unread.Add(owned);
            }
            catch (Exception ex) { if (failures.Count < 128) failures.Add("PROTOCOL_" + Program.FailureCode(ex)); }
        }
    internal JsonElement? Take(string name, int? sample = null)
    {
        int index = unread.FindIndex(e => e.GetProperty("kind").GetString() == name && (sample is null || e.GetProperty("detail").TryGetProperty("sample_index", out var value) && value.GetInt32() == sample));
        if (index < 0) return null; JsonElement result = unread[index].GetProperty("detail"); unread.RemoveAt(index); return result;
    }
    internal void ClosePipesAfterOriginalExit()
    {
        if (!audit.AllSignaled)
        { cleanup.Attempt("child_original_exit_proof", () => throw new OwnedAssertionException("ORIGINAL_PROCESSES_STILL_LIVE_NO_RELEASE")); return; }
        cleanup.Attempt("child_job_final_observation", () => job.Observe(audit));
        if (job.created && !job.original_handle_closed_after_empty) cleanup.Attempt("child_empty_job_original_handle_close", job.CloseAfterEmpty);
        // The writer does not own transport; either action may throw and the
        // other is still attempted. In particular, a broken flush never skips
        // the original process handles or the caller's profile/station cleanup.
        StreamWriter? writer = input; input = null;
        FileStream? transport = inputTransport;
        cleanup.DisposeWriterAndTransport("child_stdin", writer, transport);
        if (inputTransportHandle is not null) cleanup.Attempt("child_stdin_original_safe_handle_close", inputTransportHandle.Dispose);
        if (inputTransportHandle is null || inputTransportHandle.IsClosed) { inputTransport = null; inputTransportHandle = null; }
        if (outputTransport is not null) cleanup.Attempt("child_stdout_EOF_transport_dispose", outputTransport.Dispose);
        if (outputTransportHandle is not null) cleanup.Attempt("child_stdout_original_safe_handle_close", outputTransportHandle.Dispose);
        if (outputTransportHandle is null || outputTransportHandle.IsClosed) { outputTransport = null; outputTransportHandle = null; }
        foreach (var process in audit.Held.Where(p => p.original_handle_signaled && p.Handle != IntPtr.Zero))
            cleanup.Attempt("child_signaled_original_process_handle_close", () =>
            { Native.Check(Native.CloseHandle(process.Handle), "close_exact_signaled_owned_process_handle"); process.Handle = IntPtr.Zero; });
        retired = (!job.created || job.original_handle_closed_after_empty) && inputTransport is null && outputTransport is null && audit.Held.All(p => p.Handle == IntPtr.Zero);
        if (cleanup.any_failure && !failures.Contains("OWNED_RESOURCE_CLEANUP_FAILURE")) failures.Add("OWNED_RESOURCE_CLEANUP_FAILURE");
    }
    private void Capture(FileStream pipe)
    {
        try
        {
            using (pipe) using (MemoryStream line = new())
            {
                int value; bool overflow = false;
                while ((value = pipe.ReadByte()) >= 0)
                {
                    if (++outputBytes > 4 * 1024 * 1024) { overflow = true; captureFailed = true; }
                    if (value == 10) { if (!overflow) lines.Enqueue(line.ToArray()); line.SetLength(0); overflow = false; continue; }
                    if (line.Length >= 65536) { overflow = true; captureFailed = true; }
                    if (!overflow) line.WriteByte((byte)value);
                }
                if (line.Length != 0) captureFailed = true;
            }
        }
        catch { captureFailed = true; }
        finally { captureDone = true; }
    }
    private static string Quote(string value)
    { if (value.Contains('"') || value.Contains('\0') || value.EndsWith('\\')) throw new InvalidDataException("COMMAND_ARGUMENT"); return "\"" + value + "\""; }
    private static bool ValidHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool NativeLoaderStage(string value) => value is "native_entry" or "environment_binding" or
        "load_user32" or "load_gdi32" or "load_shell32" or "user32_metadata_exports" or "user32_binding" or "complete";
    private static bool NativeLoaderResult(string stage, string result) => stage switch
    {
        "native_entry" => result == "entered",
        "environment_binding" => result is "validated_not_identity_proof" or "invalid_nonce" or "invalid_side" or "invalid_station" or "invalid_desktop",
        "load_user32" or "load_gdi32" or "load_shell32" => result is "loading" or "loaded" or "failed",
        "user32_metadata_exports" => result is "resolved" or "missing_station_export" or "missing_desktop_export" or "missing_object_info_export",
        "user32_binding" => result is "exact_station_and_desktop_matched" or "current_station_unavailable" or "current_desktop_unavailable" or
            "station_name_query_failed" or "station_name_mismatch" or "desktop_name_query_failed" or "desktop_name_mismatch",
        "complete" => result is "passed" or "failed",
        _ => false
    };
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target,
        uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}
