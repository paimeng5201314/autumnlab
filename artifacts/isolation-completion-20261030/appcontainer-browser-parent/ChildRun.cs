using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.OwnedBrowserParent;

internal sealed class ChildRun(string mode, string nonce, string hostRoot, string runtimeRoot, string udf,
    string executable, IntPtr packageSid, string? expectedSid, string localAppData, string temp, Action save)
{
    public string mode { get; } = mode;
    public string status { get; private set; } = "not_run";
    public string last_native_stage { get; private set; } = "not_started";
    public string? failure_native_stage { get; private set; }
    public List<string> failures { get; } = [];
    public List<JsonElement> events { get; } = [];
    public List<object> launch_records { get; } = [];
    public List<object> unparsed_output_evidence { get; } = [];
    public ProcessAudit audit { get; } = new(hostRoot, runtimeRoot, expectedSid);
    public bool cleanup_proven { get; private set; }
    public string cleanup_evidence { get; private set; } = "not_proven";
    public bool sdk_normal_event { get; private set; }
    public bool sdk_final_environment_empty { get; private set; }
    public bool host_complete_valid { get; private set; }
    public bool owned_page_complete { get; private set; }
    public bool controller_ready { get; private set; }
    public bool browser_os_token_verified_before_page { get; private set; }
    public string process_observation_coverage => "sampled_not_exhaustive";
    public string independent_process_role_identification => "not_run_SDK_role_hints_bound_to_OS_process_image_and_token_only";
    public string isolation_acceptance => "not_claimed_mechanism_only";
    private StreamWriter? input;
    private bool sentClose, sentLoad;
    private volatile bool captureFailed, captureComplete;
    private uint browserPid;
    private int inventoryAfterClose = -1;
    private readonly Stopwatch watch = Stopwatch.StartNew();
    internal bool Passed => status == "passed_sampled_browser_mechanism";

    internal async Task RunAsync()
    {
        IntPtr outputRead = IntPtr.Zero, outputWrite = IntPtr.Zero, inputRead = IntPtr.Zero, inputWrite = IntPtr.Zero;
        IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero, caps = IntPtr.Zero, environment = IntPtr.Zero;
        bool initialized = false; Native.ProcessInformation child = default;
        bool createProcessAttempted = false;
        Task? capture = null;
        try
        {
            status = "running"; save();
            var security = new Native.SecurityAttributes { Length = (uint)Marshal.SizeOf<Native.SecurityAttributes>(), Inherit = true };
            last_native_stage = "create_output_pipe";
            Native.Check(Native.CreatePipe(out outputRead, out outputWrite, ref security, 4096), "create_output_pipe");
            last_native_stage = "output_reader_not_inherited";
            Native.Check(Native.SetHandleInformation(outputRead, 1, 0), "output_reader_not_inherited");
            last_native_stage = "create_input_pipe";
            Native.Check(Native.CreatePipe(out inputRead, out inputWrite, ref security, 4096), "create_input_pipe");
            last_native_stage = "input_writer_not_inherited";
            Native.Check(Native.SetHandleInformation(inputWrite, 1, 0), "input_writer_not_inherited");
            int count = packageSid == IntPtr.Zero ? 1 : 2; nuint bytes = 0;
            last_native_stage = "attribute_size_query";
            bool first = Native.InitializeProcThreadAttributeList(IntPtr.Zero, count, 0, ref bytes); int firstError = Marshal.GetLastWin32Error();
            if (first || firstError != 122 || bytes < 32 || bytes > 65536) throw new InvalidDataException("ATTRIBUTE_SIZE_QUERY_INVALID");
            attributes = Marshal.AllocHGlobal(checked((int)bytes));
            last_native_stage = "initialize_attributes";
            Native.Check(Native.InitializeProcThreadAttributeList(attributes, count, 0, ref bytes), "initialize_attributes"); initialized = true;
            handles = Marshal.AllocHGlobal(2 * IntPtr.Size); Marshal.WriteIntPtr(handles, outputWrite); Marshal.WriteIntPtr(handles, IntPtr.Size, inputRead);
            last_native_stage = "explicit_two_owned_pipes";
            Native.Check(Native.UpdateProcThreadAttribute(attributes, 0, Native.HandleList, handles, 2 * (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero), "explicit_two_owned_pipes");
            if (packageSid != IntPtr.Zero)
            {
                caps = Marshal.AllocHGlobal(Marshal.SizeOf<Native.SecurityCapabilities>());
                Marshal.StructureToPtr(new Native.SecurityCapabilities { AppContainerSid = packageSid }, caps, false);
                last_native_stage = "zero_capability_appcontainer_attribute";
                Native.Check(Native.UpdateProcThreadAttribute(attributes, 0, Native.SecurityCapabilitiesAttribute, caps,
                    (nuint)Marshal.SizeOf<Native.SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero), "zero_capability_appcontainer_attribute");
            }
            // LOCALAPPDATA is the trusted Windows Known Folder required by the
            // v3 native positive control. No ambient user/proxy/credential env.
            if (!Directory.Exists(temp)) throw new DirectoryNotFoundException("NEW_SCOPED_TEMP_REQUIRED");
            var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["COMPlus_EnableDiagnostics"] = "0", ["DOTNET_EnableDiagnostics"] = "0", ["LOCALAPPDATA"] = localAppData,
                ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows), ["TEMP"] = temp, ["TMP"] = temp
            };
            last_native_stage = "minimal_unicode_environment";
            string block = string.Concat(values.Select(p => p.Key + "=" + p.Value + "\0")) + "\0";
            environment = Marshal.StringToHGlobalUni(block);
            var startup = new Native.StartupInfoEx
            {
                Startup = new Native.StartupInfo { Size = (uint)Marshal.SizeOf<Native.StartupInfoEx>(), Flags = 0x100,
                    StdInput = inputRead, StdOutput = outputWrite, StdError = outputWrite }, Attributes = attributes
            };
            string command = Quote(executable) + " --execute " + nonce + " " + Quote(runtimeRoot) + " " + Quote(udf);
            last_native_stage = "create_owned_browser_host"; createProcessAttempted = true;
            bool launched = Native.CreateProcessW(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true,
                0x00080000 | 0x00000400 | 0x08000000, environment, hostRoot, ref startup, out child);
            int launchError = launched ? 0 : Marshal.GetLastWin32Error();
            launch_records.Add(new { succeeded = launched, native_error = launchError, process_handle_zero = child.Process == IntPtr.Zero,
                thread_handle_zero = child.Thread == IntPtr.Zero, returned_pid = child.ProcessId, returned_tid = child.ThreadId,
                startup_info_size = startup.Startup.Size, capabilities_size = Marshal.SizeOf<Native.SecurityCapabilities>(),
                attribute_count = count, capability_count = 0, explicit_owned_inherited_pipe_handles = 2,
                creation_flags = "0x08080400", unicode_environment_keys = values.Keys.ToArray(), environment_values = "redacted_not_recorded" });
            save();
            if (!launched)
            {
                if (child.Process == IntPtr.Zero && child.Thread == IntPtr.Zero && child.ProcessId == 0 && child.ThreadId == 0)
                { cleanup_proven = true; cleanup_evidence = "CreateProcess_failed_all_returned_handles_and_IDs_zero_no_group_created"; }
                if (child.Process != IntPtr.Zero) audit.AdoptRoot(child.Process, child.ProcessId);
                if (child.Process != IntPtr.Zero || child.Thread != IntPtr.Zero || child.ProcessId != 0 || child.ThreadId != 0)
                    Fail("FAILED_CREATEPROCESS_RETURNED_PARTIAL_IDENTITY_PROFILE_RETAINED");
                throw new OwnedNativeException(launchError, "create_owned_browser_host");
            }
            // Transfer ownership of the exact CreateProcess handle even if a
            // later token, image, IO or report check fails.
            audit.AdoptRoot(child.Process, child.ProcessId);
            Native.CloseHandle(child.Thread); child.Thread = IntPtr.Zero;
            Native.CloseHandle(outputWrite); outputWrite = IntPtr.Zero; Native.CloseHandle(inputRead); inputRead = IntPtr.Zero;
            input = new StreamWriter(new FileStream(new SafeFileHandle(inputWrite, true), FileAccess.Write), new UTF8Encoding(false), 1024) { AutoFlush = true };
            inputWrite = IntPtr.Zero;
            var pipe = new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read); outputRead = IntPtr.Zero;
            capture = Task.Run(() => Capture(pipe));
            // Everything mutating audit/events occurs on this async loop;
            // capture merely queues bounded owned output lines.
            while (!audit.AllSignaled || !captureComplete)
            {
                DrainOutput(); audit.Snapshot();
                if (watch.Elapsed > TimeSpan.FromSeconds(45) && !sentClose)
                { Fail("OBSERVATION_BUDGET_EXCEEDED_NORMAL_CLOSE_ONLY"); Send("close"); }
                if (Program.CancelRequested && !sentClose) { Fail("CANCEL_REQUESTED"); Send("close"); }
                if (failures.Count > 0 && !sentClose) Send("close");
                if (failures.Count > 0 && (!audit.AllSignaled || !captureComplete)) status = "failed_pending_observed_original_handles_or_stdio";
                if (captureComplete && !sentClose) { Fail("STDOUT_CLOSED_BEFORE_EXPECTED_COMPLETION"); Send("close"); }
                save(); await Task.Delay(100);
            }
            await capture; DrainOutput(); audit.Snapshot();
            HeldProcess? browser = audit.Held.FirstOrDefault(p => p.pid == browserPid);
            // A timeout/functional failure remains failed even if this precise
            // process group later closes normally. Cleanup has separate truth.
            cleanup_proven = browserPid != 0 && browser?.original_handle_signaled == true && audit.AllSignaled &&
                sdk_normal_event && sdk_final_environment_empty && inventoryAfterClose == 0 && !audit.process_identity_gap;
            if (cleanup_proven) cleanup_evidence = "bound_Normal_event_all_original_observed_handles_signaled_final_SDK_empty";
            if (!cleanup_proven) Fail("NORMAL_GROUP_ORIGINAL_HANDLE_AND_FINAL_EMPTY_CLEANUP_UNPROVEN");
            if (!host_complete_valid || !owned_page_complete || !browser_os_token_verified_before_page) Fail("HOST_MECHANISM_ACCEPTANCE_INCOMPLETE");
            if (expectedSid is not null && !audit.RestrictedObservedTokensPass) Fail("OBSERVED_RESTRICTED_TOKEN_BOUNDARY_FAILED");
            if (audit.Held.FirstOrDefault(p => p.pid == child.ProcessId)?.exit_code != 0) Fail("ORIGINAL_HOST_NONZERO_EXIT");
            if (captureFailed) Fail("BOUNDED_STDIO_CAPTURE_FAILED");
            status = failures.Count == 0 ? "passed_sampled_browser_mechanism" : "failed";
        }
        catch (Exception ex)
        {
            failure_native_stage = ex is OwnedNativeException native ? native.FixedStage : last_native_stage;
            Fail(Program.FailureCode(ex)); status = "failed";
            if (!createProcessAttempted)
            { cleanup_proven = true; cleanup_evidence = "failed_before_any_CreateProcess_call_no_child_or_browser_created"; }
        }
        finally
        {
            // No timeout grants a live-handle release or profile deletion.
            // Keep the parent/report and exact observed handles until signaled.
            if (child.Process != IntPtr.Zero && !audit.Held.Any(p => p.Handle == child.Process))
            {
                // Adoption can fail before facts are available. Keep original
                // process identity; do not reopen the PID as substitute proof.
                while (Native.WaitForSingleObject(child.Process, 0) != Native.WaitObject0)
                { status = "failed_pending_original_host_handle"; save(); await Task.Delay(500); }
                Native.CloseHandle(child.Process);
            }
            if (input is not null) { if (!sentClose) Send("close"); input.Dispose(); input = null; }
            while (!audit.AllSignaled)
            { status = "failed_pending_observed_original_handles"; audit.Snapshot(); save(); await Task.Delay(500); }
            audit.ReleaseSignaledHandles();
            if (child.Thread != IntPtr.Zero) Native.CloseHandle(child.Thread);
            foreach (IntPtr handle in new[] { outputRead, outputWrite, inputRead, inputWrite }) if (handle != IntPtr.Zero) Native.CloseHandle(handle);
            if (initialized) Native.DeleteProcThreadAttributeList(attributes);
            foreach (IntPtr pointer in new[] { attributes, handles, caps, environment }) if (pointer != IntPtr.Zero) Marshal.FreeHGlobal(pointer);
            if (status.StartsWith("failed_pending", StringComparison.Ordinal)) status = "failed_after_late_observed_exit";
            save();
        }
    }
    private static string Quote(string text)
    { if (text.Contains('"') || text.Contains('\0') || text.EndsWith('\\')) throw new InvalidDataException("UNSUPPORTED_COMMAND_ARGUMENT"); return "\"" + text + "\""; }
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> outputLines = new();
    private long outputTotal;
    private void Capture(FileStream stream)
    {
        try
        {
            using (stream)
            using (MemoryStream line = new())
            {
                byte[] buffer = new byte[4096]; int count; bool oversized = false;
                while ((count = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    foreach (byte value in buffer.AsSpan(0, count))
                    {
                        if (++outputTotal > 4 * 1024 * 1024) { captureFailed = true; oversized = true; }
                        if (value == 10)
                        { if (!oversized) outputLines.Enqueue(line.ToArray()); line.SetLength(0); oversized = false; continue; }
                        if (line.Length >= 65536) { oversized = true; captureFailed = true; }
                        if (!oversized) line.WriteByte(value);
                    }
                }
                if (line.Length > 0) captureFailed = true;
            }
        }
        catch { captureFailed = true; }
        finally { captureComplete = true; }
    }
    private void DrainOutput()
    {
        while (outputLines.TryDequeue(out byte[]? line))
        {
            try
            {
                string text = new UTF8Encoding(false, true).GetString(line).TrimEnd('\r');
                using var doc = JsonDocument.Parse(text); var root = doc.RootElement;
                if (root.GetProperty("schema_version").GetInt32() != 1 || root.GetProperty("nonce").GetString() != nonce ||
                    root.GetProperty("host_pid").GetUInt32() != audit.RootPid ||
                    root.GetProperty("isolation_acceptance").GetString() != "not_claimed_host_output_only") throw new InvalidDataException("STDOUT_ENVELOPE_BINDING");
                // Do not preserve full UDF or exception text from child output.
                string kind = root.GetProperty("kind").GetString() ?? "";
                JsonElement detail = root.GetProperty("detail");
                if (kind == "controller_ready")
                {
                    if (controller_ready) throw new InvalidDataException("DUPLICATE_CONTROLLER_READY");
                    string expectedUdf = "sha256:" + OwnedFiles.HashText(Path.TrimEndingDirectorySeparator(Path.GetFullPath(udf)).ToUpperInvariant());
                    if (detail.GetProperty("browser_version").GetString() != "154.0.4258.53" ||
                        detail.GetProperty("policy_id").GetString() != "autumn-local-web-v2" ||
                        !detail.GetProperty("udf_matches_requested").GetBoolean() ||
                        detail.GetProperty("udf_binding_sha256").GetString() != expectedUdf ||
                        !detail.GetProperty("in_private").GetBoolean()) throw new InvalidDataException("CONTROLLER_FIXED_RUNTIME_POLICY_UDF_INPRIVATE_BINDING");
                    browserPid = detail.GetProperty("browser_pid").GetUInt32(); controller_ready = true;
                    HeldProcess? browser = audit.Observe(browserPid, "Browser", "controller_ready_bound_OS_handle");
                    bool rootToken = expectedSid is null ? audit.Held.FirstOrDefault(p => p.pid == audit.RootPid)?.token?.is_appcontainer == 0 : audit.RestrictedObservedTokensPass;
                    bool browserToken = expectedSid is null ? browser?.token?.is_appcontainer == 0 : audit.BoundRestricted(browser);
                    browser_os_token_verified_before_page = rootToken && browserToken &&
                        browser?.image_relative.Equals("runtime/msedgewebview2.exe", StringComparison.OrdinalIgnoreCase) == true && !audit.ObservationGap;
                    if (!browser_os_token_verified_before_page) Fail("ORIGINAL_BROWSER_OS_TOKEN_REJECTED_BEFORE_PAGE");
                    else if (!captureFailed && failures.Count == 0) { Send("load_owned_page"); sentLoad = true; }
                    events.Add(JsonSerializer.SerializeToElement(new { kind, browser_pid = browserPid, os_token_verified = browser_os_token_verified_before_page }));
                }
                else if (kind == "process_inventory")
                {
                    var rows = detail.GetProperty("rows"); if (rows.GetArrayLength() > 128) throw new InvalidDataException("SDK_INVENTORY_TOO_LARGE");
                    if (detail.TryGetProperty("truncated", out var truncated) && truncated.GetBoolean()) throw new InvalidDataException("SDK_INVENTORY_TRUNCATED");
                    foreach (var row in rows.EnumerateArray())
                    { uint pid = row.GetProperty("pid").GetUInt32(); string role = row.GetProperty("kind").GetString() ?? "unknown"; if (pid == 0 || role.Length > 40) throw new InvalidDataException("SDK_ROW_INVALID"); audit.Observe(pid, role, "SDK_inventory_plus_OS_query"); }
                    if (sentClose) inventoryAfterClose = rows.GetArrayLength();
                    events.Add(JsonSerializer.SerializeToElement(new { kind, count = rows.GetArrayLength(), cause = detail.GetProperty("cause").GetString() }));
                }
                else if (kind == "owned_page_complete")
                {
                    if (!sentLoad || owned_page_complete || detail.GetProperty("wasm").GetInt32() != 42 || detail.GetProperty("frames").GetInt32() != 60)
                        throw new InvalidDataException("OWNED_PAGE_RESULT_INVALID");
                    owned_page_complete = true; audit.Snapshot(); Send("close"); events.Add(JsonSerializer.SerializeToElement(new { kind, wasm = 42, frames = 60 }));
                }
                else if (kind == "browser_group_exit")
                {
                    if (detail.GetProperty("pid").GetUInt32() == browserPid && browserPid != 0 && detail.GetProperty("kind").GetString() == "Normal") sdk_normal_event = true;
                    else Fail("SDK_GROUP_EXIT_NOT_BOUND_NORMAL");
                    events.Add(JsonSerializer.SerializeToElement(new { kind, bound_normal = sdk_normal_event }));
                }
                else if (kind == "host_complete")
                {
                    sdk_final_environment_empty = detail.GetProperty("environment_process_count").GetInt32() == 0;
                    inventoryAfterClose = detail.GetProperty("environment_process_count").GetInt32();
                    host_complete_valid = detail.GetProperty("initialization_succeeded").GetBoolean() && detail.GetProperty("page_succeeded").GetBoolean() &&
                        detail.GetProperty("normal_browser_exit").GetBoolean() && detail.GetProperty("cleanup_status").GetString() == "passed" && sdk_final_environment_empty &&
                        detail.GetProperty("browser_pid").GetUInt32() == browserPid && !detail.GetProperty("failure_seen").GetBoolean() &&
                        detail.GetProperty("overall_status").GetString() == "passed_mechanism_host_only";
                    events.Add(JsonSerializer.SerializeToElement(new { kind, host_complete_valid, sdk_final_environment_empty }));
                }
                else
                {
                    if (kind.Contains("fail", StringComparison.OrdinalIgnoreCase) || kind.Contains("budget", StringComparison.OrdinalIgnoreCase) || kind is "process_failed" or "command_rejected" or "launch_refused") Fail("HOST_EVENT_" + kind);
                    if (events.Count < 2048) events.Add(JsonSerializer.SerializeToElement(new { kind, detail = SafeDetail(detail) }));
                }
            }
            catch (Exception ex)
            {
                if (unparsed_output_evidence.Count < 32) unparsed_output_evidence.Add(new { byte_count = line.Length,
                    sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(line)), error = Program.FailureCode(ex) });
                Fail("CHILD_PROTOCOL_" + Program.FailureCode(ex));
            }
        }
        if (captureFailed && !sentClose) { Fail("STDOUT_SIZE_ENCODING_OR_IO_BOUNDARY_FAILED"); Send("close"); }
    }
    private void Send(string command)
    {
        if (command == "close" && sentClose) return;
        if (command == "close") sentClose = true;
        if (input is null) return;
        try { input.WriteLine(JsonSerializer.Serialize(new { schema_version = 1, nonce, command })); }
        catch (Exception ex) { Fail("PARENT_COMMAND_WRITE_" + Program.FailureCode(ex)); }
    }
    private void Fail(string reason) { if (failures.Count < 128 && !failures.Contains(reason)) failures.Add(reason); }
    private static Dictionary<string, object?> SafeDetail(JsonElement detail)
    {
        Dictionary<string, object?> result = [];
        if (detail.ValueKind != JsonValueKind.Object) return result;
        foreach (string field in new[] { "stage", "exception_type", "kind", "reason", "resource" })
            if (detail.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String)
            {
                string text = value.GetString() ?? "";
                if (text.Length is > 0 and <= 80 && text.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.')) result[field] = text;
            }
        foreach (string field in new[] { "hresult", "native_error", "status", "bytes", "browser_pid" })
            if (detail.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long number)) result[field] = number;
        foreach (string field in new[] { "failure", "trusted_content_only" })
            if (detail.TryGetProperty(field, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False) result[field] = value.GetBoolean();
        if (detail.TryGetProperty("error", out var error)) result["error"] = SafeDetail(error);
        return result;
    }
}
