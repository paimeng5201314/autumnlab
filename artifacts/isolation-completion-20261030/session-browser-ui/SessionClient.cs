using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AutumnOS.OwnedBrowserParent;
using AutumnOS.SessionBrowserProtocol;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.SessionBrowserUI;

internal sealed class SessionClient(SessionInputs input, string manifestPath, NativeEndTrustControl? nativeControl = null)
{
    internal readonly UiReport Report = new();
    internal bool Ready { get; private set; }
    internal bool Finished { get; private set; }
    internal bool CloseRequested { get; private set; }
    private readonly Channel<SessionHeader> commands = Channel.CreateBounded<SessionHeader>(32);
    private SessionHeader? pointerMove;
    private Process? core;
    private SafeProcessHandle? host;
    private NamedPipeServerStream? pipe;
    private ViewerJobWitness? witness;
    private string? reportPath, directory;
    private long requestId;
    private bool coreStarted, initialReadySent;
    private SessionHeader? completion;
    internal bool Enqueue(SessionHeader command)
    {
        if (!Ready || CloseRequested || Finished) return false;
        command = command with { schema_version = 1, nonce = Report.nonce, epoch = 1, kind = "input", request_id = 0 };
        SessionProtocol.ValidateHeader(command with { request_id = 1 });
        if (command.command == "pointer_move") { pointerMove = command; return true; }
        if (commands.Writer.TryWrite(command)) return true;
        Report.input_queue_dropped++; return false;
    }
    internal void RequestClose() { CloseRequested = true; }
    internal void CompositionRange(string phase, int start, int length, int selectionStart, int selectionLength, bool valid)
    {
        if (!valid) Report.native_composition_range_errors++;
        if (Report.native_composition_ranges.Count < 256) Report.native_composition_ranges.Add(new(phase, start, length, selectionStart, selectionLength, valid));
        else Report.native_composition_range_errors++;
    }
    internal void CompositionEndedNotification(int start, int length, int selectionStart, int selectionLength, int notificationBufferLength)
    {
        Report.native_composition_end_notifications++;
        if (Report.native_composition_end_notification_ranges.Count < 256)
            Report.native_composition_end_notification_ranges.Add(new(start, length, selectionStart, selectionLength, notificationBufferLength));
        else Report.native_composition_range_errors++;
    }
    internal void CompositionPendingEnd(bool pending) => Report.native_composition_pending_end = pending;
    internal void CompositionSettled(NativeCompositionSettledFact fact)
    {
        if (Report.native_composition_settled.Count >= 256 || fact.transaction_id <= 0 ||
            Report.native_composition_settled.Any(previous => previous.transaction_id >= fact.transaction_id) ||
            fact.settled_length is < 0 or > 1024 || !ValidHash(fact.settled_sha256) || !ValidHash(fact.notification_buffer_sha256) ||
            !fact.initial_buffer_empty || fact.post_end_text_change_revision <= 0 || fact.decision is not ("commit" or "cancel") ||
            (fact.decision == "commit" ? fact.settled_length == 0 : fact.settled_length != 0 || fact.settled_sha256 != TextHash("")))
        {
            Report.native_composition_range_errors++; Fail("native_composition_settled_fact_rejected"); RequestClose(); return;
        }
        Report.native_composition_settled.Add(fact);
        if (fact.decision == "commit")
        {
            Report.native_composition_settled_commits++;
            Report.last_settled_commit_length = fact.settled_length;
            Report.last_settled_commit_sha256 = fact.settled_sha256;
        }
        else Report.native_composition_settled_cancels++;
    }
    private static string TextHash(string text) => Convert.ToHexStringLower(SHA256.HashData(new UTF8Encoding(false, true).GetBytes(text)));
    private static bool ValidHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private void RecordImeRpcEvidence(SessionHeader request, SessionHeader reply)
    {
        if (request.command is not ("ime_start" or "ime_update" or "ime_set_composition" or "ime_commit" or "ime_cancel")) return;
        if (reply.kind != "input_result" || reply.success != true) return;
        Report.ime_rpc_successful_ack_count++;
        if (Report.ime_rpc_evidence.Count >= 256)
        {
            Report.ime_rpc_evidence_overflow = true; Report.ime_rpc_unrecorded_successful_acks++;
            Fail("bounded_IME_RPC_evidence_overflow"); RequestClose();
            throw new InvalidDataException("BOUNDED_IME_RPC_EVIDENCE_OVERFLOW");
        }
        try
        {
            // Only reply evidence is copied. Never retain the request header,
            // its text, a CDP expression, or a native handle in this trace.
            SessionHeader evidence = SessionProtocol.WithEvidence(new SessionHeader(1, Report.nonce, 1, "input_result")
            { request_id = reply.request_id, success = reply.success, page_succeeded = reply.page_succeeded, operation_succeeded = reply.operation_succeeded }, reply);
            SessionProtocol.ValidateHeader(evidence);
            Report.ime_rpc_evidence.Add(new(request.command, request.request_id, request.text?.Length,
                request.text is null ? null : TextHash(request.text), evidence));
        }
        catch (Exception error)
        {
            Report.ime_rpc_unrecorded_successful_acks++; Fail("IME_RPC_evidence_recording_failed", error); RequestClose(); throw;
        }
    }
    internal void NativeEvent(string name)
    {
        switch (name)
        {
            case "pointer_down": Report.native_pointer_down++; break;
            case "key_down": Report.native_key_down++; break;
            case "key_up": Report.native_key_up++; break;
            case "ime_start": Report.native_composition_started++; break;
            case "ime_update": Report.native_composition_changed++; break;
            case "ime_commit": Report.native_composition_ended++; break;
            case "clipboard_blocked": Report.native_clipboard_default_blocked++; break;
            case "drag_blocked": Report.native_drag_default_blocked++; break;
        }
    }
    internal void NativeKeyPreview(bool down, string role, bool handledBefore, bool composing)
    {
        if (role is not ("surface" or "proxy" or "other")) throw new InvalidDataException("NATIVE_KEY_FOCUS_ROLE_REQUIRED");
        if (down) Report.native_preview_key_down_events = KeyDiagnosticCount(Report.native_preview_key_down_events);
        else Report.native_preview_key_up_events = KeyDiagnosticCount(Report.native_preview_key_up_events);
        if (role == "surface") Report.native_preview_surface_events = KeyDiagnosticCount(Report.native_preview_surface_events);
        else if (role == "proxy") Report.native_preview_proxy_events = KeyDiagnosticCount(Report.native_preview_proxy_events);
        else Report.native_preview_other_events = KeyDiagnosticCount(Report.native_preview_other_events);
        if (handledBefore) Report.native_preview_prehandled_events = KeyDiagnosticCount(Report.native_preview_prehandled_events);
        if (composing) Report.native_preview_composing_events = KeyDiagnosticCount(Report.native_preview_composing_events);
    }
    internal void SurfaceFocusObserved(bool focusReturn, bool exactSurfaceAfter)
    {
        Report.surface_focus_attempts = KeyDiagnosticCount(Report.surface_focus_attempts);
        if (focusReturn) Report.surface_focus_returned_true = KeyDiagnosticCount(Report.surface_focus_returned_true);
        if (exactSurfaceAfter) Report.surface_exact_focus_observed = KeyDiagnosticCount(Report.surface_exact_focus_observed);
    }
    private int KeyDiagnosticCount(int count)
    {
        if (count >= 1000000) { Report.native_key_diagnostics_saturated = true; return 1000000; }
        return count + 1;
    }
    private void Fail(string stage, Exception? error = null)
    {
        if (Report.failure_stage is null)
        {
            Report.failure_stage = stage; Report.failure = error is null ? "ASSERTION_FAILED" : ViewerNative.Failure(error);
            Report.failure_native_error = (error as System.ComponentModel.Win32Exception)?.NativeErrorCode;
            Report.failure_native_stage = error is ViewerNativeException own ? own.FixedStage : error is OwnedNativeException linked ? linked.FixedStage : null;
        }
        Report.status = "failed";
    }
    private void CleanupFault(string stage, Exception error)
    {
        string? nativeStage = error is ViewerNativeException own ? own.FixedStage : error is OwnedNativeException linked ? linked.FixedStage : null;
        var fact = new UiCleanupFailure(stage, error.GetType().Name, unchecked((uint)error.HResult), (error as System.ComponentModel.Win32Exception)?.NativeErrorCode, nativeStage);
        if (Report.cleanup_failures.Count < 64 && !Report.cleanup_failures.Contains(fact)) Report.cleanup_failures.Add(fact);
        Fail(stage, error);
    }
    private void ObserveWitnessForCleanup()
    {
        try
        {
            witness?.Tick();
            if (witness?.HistoricalFailure == true) CleanupFault("original_UI_job_query_history", new InvalidDataException("UI_JOB_QUERY_HISTORY_FAILED"));
        }
        catch (Exception error) { CleanupFault("original_UI_job_observation", error); }
    }
    private void Notify(Action<string> status, string text)
    { try { status(text); } catch (Exception error) { CleanupFault("UI_status_callback", error); } }
    private void Save()
    {
        if (reportPath is null) return;
        try
        {
            Report.job_witness = witness; Report.updated_utc = DateTimeOffset.UtcNow;
            OwnedReportFile.SaveExisting(reportPath, Report);
        }
        catch (Exception error)
        {
            Report.report_error ??= ViewerNative.Failure(error);
            Report.report_native_error ??= (error as System.ComponentModel.Win32Exception)?.NativeErrorCode;
            Fail("ui_report_write", error);
        }
    }
    private async Task<SessionPacket> Read()
    {
        while (true)
        {
            Task<SessionPacket?> read = SessionProtocol.ReadAsync(pipe!, Report.nonce, 1, CancellationToken.None);
            while (!read.IsCompleted)
            {
                witness?.Tick();
                if (witness?.HistoricalFailure == true) throw new InvalidDataException("UI_JOB_QUERY_HISTORY_FAILED");
                if (core is not null && core.HasExited)
                {
                    await Task.WhenAny(read, Task.Delay(200));
                    if (!read.IsCompleted) throw new EndOfStreamException("ORIGINAL_CORE_EXITED");
                }
                await Task.WhenAny(read, Task.Delay(50));
            }
            SessionPacket? packet = await read;
            witness?.Tick(); if (witness?.HistoricalFailure == true) throw new InvalidDataException("UI_JOB_QUERY_HISTORY_FAILED");
            if (packet is not null) return packet;
            Report.stale_epoch_packets_dropped++;
        }
    }
    private async Task Write(SessionHeader header) => await SessionProtocol.WriteAsync(pipe!, new(header, []), CancellationToken.None);
    private SessionHeader Header(string kind) => new(1, Report.nonce, 1, kind);

    internal async Task RunAsync(Func<SessionPacket, Task> display, Action<string> status)
    {
        string stage = "fresh_ui_case";
        try
        {
            if (nativeControl is not { ProofPinned: true, ProtectedPreparedDirectoryVerified: true, ProtectedReportAndBindingVerified: true, OriginalControlExitBound: true })
                throw new InvalidDataException("VERIFIED_SAME_RUNTIME_NATIVE_CONTROL_REQUIRED");
            Report.native_end_control = nativeControl;
            Report.nonce = Guid.NewGuid().ToString("N"); Report.source_snapshot_id = input.source_snapshot_id; Report.manifest_sha256 = input.manifest_sha256;
            directory = Path.Combine(input.case_root, "session-browser-case-" + Report.nonce);
            OwnedFiles.CreatePrivateDirectory(directory, null, false); reportPath = Path.Combine(directory, "ui-report.json");
            using (new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
            Save(); if (Report.report_error is not null) throw new IOException("DURABLE_UI_REPORT_REQUIRED");
            string name = "AutumnOS.SessionViewer." + Report.nonce;
            pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
            var start = new ProcessStartInfo(Path.Combine(input.core_directory, input.core_executable)) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = input.core_directory };
            foreach (string value in new[] { "--execute", manifestPath, Report.nonce, name, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(value);
            stage = "start_fixed_original_CoreParent";
            core = new Process { StartInfo = start }; if (!core.Start()) throw new InvalidOperationException("CORE_START_FAILED"); coreStarted = true;
            Report.original_core_pid = (uint)core.Id;
            Report.core_identity = ViewerNative.RequireCore(core.SafeHandle, start.FileName);
            stage = "authenticate_CoreParent_pipe";
            using (var connection = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
            {
                Task wait = pipe.WaitForConnectionAsync(connection.Token);
                while (!wait.IsCompleted) { if (core.HasExited) throw new EndOfStreamException("CORE_EXITED_BEFORE_PIPE"); await Task.WhenAny(wait, Task.Delay(50)); }
                await wait;
            }
            if (!ViewerNative.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peer) || peer != Report.original_core_pid || core.HasExited)
                throw new InvalidDataException("ORIGINAL_CORE_KERNEL_PIPE_PID_REQUIRED");
            Report.original_core_kernel_pipe_verified = true;
            if ((await Read()).header.kind != "hello") throw new InvalidDataException("UI_HELLO_ORDER");
            initialReadySent = true; await Write(Header("ready") with { success = true });
            stage = "bind_independent_original_Host_and_QUERY_only_job";
            SessionHeader job = (await Read()).header;
            if (job.kind != "job_ready" || job.job_handle is null || job.host_process_handle is null || job.host_pid is null || job.host_creation_filetime is null)
                throw new InvalidDataException("ORIGINAL_HOST_AND_JOB_METADATA_REQUIRED");
            host = ViewerNative.BindStationHost(core.SafeHandle, job.host_process_handle.Value, job.host_pid.Value, job.host_creation_filetime.Value, start.FileName);
            Report.original_host_pid = job.host_pid; Report.original_host_creation_filetime = job.host_creation_filetime; Report.original_host_identity_verified = true;
            witness = new ViewerJobWitness(core.SafeHandle, job.job_handle.Value);
            if (!witness.InitialEmptyVerified) throw new InvalidDataException("UI_FRESH_QUERY_ONLY_JOB_REQUIRED");
            Report.UI_job_witness_acknowledged = true; Save();
            if (Report.report_error is not null) throw new IOException("DURABLE_UI_REPORT_REQUIRED");
            await Write(Header("job_bound") with { success = true });
            stage = "owned_context_metadata";
            SessionHeader owned = (await Read()).header;
            if (owned.kind != "owned_ready" || owned.station_name is null || owned.station_name.Equals("WinSta0", StringComparison.OrdinalIgnoreCase) ||
                owned.desktop_name is null || !owned.desktop_name.StartsWith("AutumnOS_SessionDesktop_", StringComparison.Ordinal) || owned.package_sid_sha256 is null)
                throw new InvalidDataException("FIXED_PRIVATE_CONTEXT_REQUIRED");
            Report.station_name = owned.station_name; Report.desktop_name = owned.desktop_name; Report.package_sid_sha256 = owned.package_sid_sha256;
            await Write(Header("ack") with { success = true });
            SessionHeader ready = (await Read()).header;
            if (ready.kind != "ready" || ready.success != true || ready.page_succeeded != true || ready.initialization_succeeded != true || ready.browser_pid is null)
                throw new InvalidDataException("REAL_PRIVATE_BROWSER_PRESENTATION_READY_REQUIRED");
            witness.Tick(); if (witness.HistoricalFailure || !witness.process_ids.Contains(ready.browser_pid.Value)) throw new InvalidDataException("ACTUAL_BROWSER_IN_EXACT_UI_JOB_REQUIRED");
            Ready = true; Report.status = "interactive_owned_page_prototype"; Notify(status, "已连接。点击画面中的文本框，再用下方输入法框输入“你好”；测试鼠标和方向键后关闭。"); Save();
            var gate = new SessionRequestGate(Report.nonce, 1); var captureClock = Stopwatch.StartNew(); long capturedAt = -100;
            stage = "native_presentation_and_bounded_input";
            while (true)
            {
                SessionHeader? action = null;
                if (!CloseRequested && Report.initial_frames.Count < 2 && captureClock.ElapsedMilliseconds - capturedAt >= 100)
                { capturedAt = captureClock.ElapsedMilliseconds; action = Header("input") with { command = "capture" }; }
                else if (!CloseRequested && Report.initial_frames.Count < 2) { await Task.Delay(10); continue; }
                else if (commands.Reader.TryRead(out SessionHeader? queued)) action = queued;
                else if (pointerMove is not null) { action = pointerMove; pointerMove = null; }
                else if (CloseRequested) action = Header("input") with { command = "close" };
                else if (captureClock.ElapsedMilliseconds - capturedAt >= 100) { capturedAt = captureClock.ElapsedMilliseconds; action = Header("input") with { command = "capture" }; }
                if (action is null) { await Task.Delay(10); continue; }
                SessionHeader request = action with { schema_version = 1, nonce = Report.nonce, epoch = 1, kind = "input", request_id = ++requestId };
                gate.Begin(request); await Write(request); SessionPacket reply = await Read(); gate.Complete(reply.header);
                if (reply.header.success == false) throw new InvalidDataException("PRIVATE_BROWSER_ACTION_REJECTED");
                if (reply.header.wasm_result is not null) Report.last_DOM_evidence = reply.header;
                if (reply.header.kind == "frame")
                {
                    if (Report.initial_frames.Count < 2 && Report.forwarded_input_acks == 0 && reply.header.dom_text_length == 0 &&
                        reply.header.dom_composition_start == 0 && reply.header.dom_composition_update == 0 && reply.header.dom_composition_end == 0)
                    {
                        SessionProtocol.Validate(reply);
                        string hash = Convert.ToHexStringLower(SHA256.HashData(reply.data));
                        if (Report.initial_frames.All(f => f.sha256 != hash))
                        {
                            string frameName = "frame-initial-" + (Report.initial_frames.Count + 1) + ".png";
                            using (var file = new FileStream(Path.Combine(directory!, frameName), FileMode.CreateNew, FileAccess.Write, FileShare.Read))
                            { file.Write(reply.data); file.Flush(true); }
                            Report.initial_frames.Add(new InitialFrameFact(frameName, reply.data.Length, hash, reply.header.frame_id));
                        }
                    }
                    await display(reply); Report.displayed_frames++; Report.last_displayed_frame_id = reply.header.frame_id;
                }
                else if (request.command != "close")
                {
                    Report.forwarded_input_acks++;
                    if (request.command == "pointer_down") Report.forwarded_pointer_acks++;
                    if (request.command is "key_down" or "key_up") Report.forwarded_key_acks++;
                    if (request.command is "ime_start" or "ime_update" or "ime_commit") Report.forwarded_composition_acks++;
                    if (request.command == "ime_commit")
                    {
                        Report.forwarded_ime_commit_acks++;
                        Report.last_ime_commit_ack_length = request.text!.Length;
                        Report.last_ime_commit_ack_sha256 = TextHash(request.text!);
                    }
                }
                RecordImeRpcEvidence(request, reply.header);
                Save(); if (Report.report_error is not null) throw new IOException("DURABLE_UI_REPORT_REQUIRED");
                if (request.command == "close")
                {
                    if (reply.header.wasm_result is not null) Report.final_DOM_evidence = reply.header;
                    completion = (await Read()).header;
                    Report.completion_report_sha256 = completion.report_sha256;
                    if (completion.kind != "host_complete") throw new InvalidDataException("CORE_FINAL_COMPLETION_REQUIRED");
                    break;
                }
            }
        }
        catch (Exception error) { Fail(stage, error); Notify(status, "验证失败，正在等待原进程和 Job 退出证据。"); }
        finally
        {
            Ready = false; CloseRequested = true; commands.Writer.TryComplete(); pointerMove = null;
            Report.cleanup_stage = "close_UI_protocol_endpoint";
            try { pipe?.Dispose(); } catch (Exception error) { CleanupFault("ui_pipe_close", error); }
            // Core death never substitutes for StationHost death: that host
            // could still be finishing a copy before it starts the browser.
            if (coreStarted && core is not null)
            {
                while (!Report.original_core_exit_proven)
                {
                    Report.cleanup_stage = "wait_original_CoreParent_process";
                    try { Report.original_core_exit_proven = ViewerNative.OriginalExited(core.SafeHandle); } catch (Exception error) { CleanupFault("original_Core_wait", error); }
                    if (!Report.original_core_exit_proven) { ObserveWitnessForCleanup(); Report.status = "pending_original_CoreParent_exit"; Save(); await Task.Delay(100); }
                }
                try { Report.original_core_exit_code = unchecked((int)ViewerNative.ExitCode(core.SafeHandle)); } catch (Exception error) { CleanupFault("original_Core_exit_code", error); }
            }
            if (Report.original_core_exit_proven && directory is not null)
            {
                try
                {
                    string exactReport = Path.Combine(directory, "core-report.json");
                    using var file = new FileStream(exactReport, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (file.Length is < 2 or > 16777216) throw new InvalidDataException("BOUNDED_EXACT_CORE_REPORT_REQUIRED");
                    byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
                    Report.core_report_sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
                    using JsonDocument json = JsonDocument.Parse(bytes); JsonElement facts = json.RootElement;
                    bool bound = facts.GetProperty("nonce").GetString() == Report.nonce && facts.GetProperty("source_snapshot_id").GetString() == input.source_snapshot_id &&
                        facts.GetProperty("manifest_sha256").GetString() == input.manifest_sha256;
                    Report.core_report_identity_bound = bound;
                    Report.core_report_bound = bound && completion?.report_sha256 == Report.core_report_sha256;
                    Report.core_cleanup_confirmed = bound && facts.GetProperty("cleanup_proven").GetBoolean();
                    Report.core_status = facts.GetProperty("status").GetString();
                    Report.core_final_report_bound = Report.core_report_bound && Report.core_cleanup_confirmed &&
                        Report.core_status == "completed_owned_session_browser_prototype_mechanism_only";
                    JsonElement launch = facts.GetProperty("netonly_launch");
                    Report.no_Host_created_failure_proven = bound && Report.core_status == "failed" && Report.core_cleanup_confirmed && !Report.UI_job_witness_acknowledged &&
                        !facts.GetProperty("UI_job_witness_acknowledged").GetBoolean() && launch.GetProperty("no_child_created_proven").GetBoolean() &&
                        !launch.GetProperty("create_succeeded").GetBoolean() && launch.GetProperty("create_process_with_logon_calls").GetInt32() is >= 0 and <= 1 &&
                        facts.GetProperty("station_name").ValueKind == JsonValueKind.Null && host is null && witness is null;
                }
                catch (Exception error) { Fail("exact_final_Core_report_binding", error); }
            }
            if (host is not null)
            {
                while (!Report.original_host_exit_proven)
                {
                    Report.cleanup_stage = "wait_original_StationHost_process";
                    try { Report.original_host_exit_proven = ViewerNative.OriginalExited(host); } catch (Exception error) { CleanupFault("original_Host_wait", error); }
                    if (!Report.original_host_exit_proven) { ObserveWitnessForCleanup(); Report.status = "pending_original_StationHost_exit"; Save(); await Task.Delay(100); }
                }
                try { Report.original_host_exit_code = ViewerNative.ExitCode(host); } catch (Exception error) { CleanupFault("original_Host_exit_code", error); }
            }
            else if (initialReadySent && !Report.no_Host_created_failure_proven)
            {
                // No PID reopening or guessed host-death proof after a crash
                // before transfer of the original handle.
                while (true) { Report.cleanup_stage = "original_Host_identity_unproven"; Report.status = "failed_pending_original_Host_identity_unproven"; Save(); await Task.Delay(500); }
            }
            if (witness is not null)
            {
                while (!witness.closed_after_empty)
                {
                    Report.cleanup_stage = "query_and_close_original_browser_job_after_empty";
                    try { witness.Tick(); if (witness.Empty) witness.CloseAfterEmpty(); } catch (Exception error) { CleanupFault("original_UI_job_close_after_empty", error); }
                    if (!witness.closed_after_empty) { Report.status = "pending_original_browser_job_zero"; Save(); await Task.Delay(100); }
                }
                Report.independent_job_closed_after_empty = witness.closed_after_empty;
            }
            Report.failed_case_terminal_cleanup_bound = Report.original_core_exit_proven && Report.original_host_exit_proven && witness is { Empty: true, closed_after_empty: true } &&
                Report.core_report_identity_bound && Report.core_cleanup_confirmed && Report.core_status is "failed" or "completed_owned_session_browser_prototype_mechanism_only";
            Report.cleanup_proven = (!coreStarted || Report.original_core_exit_proven) && (!initialReadySent || Report.original_host_exit_proven || Report.no_Host_created_failure_proven) &&
                (witness is null || witness.Empty && witness.closed_after_empty) && (!initialReadySent ||
                    Report.no_Host_created_failure_proven || Report.failed_case_terminal_cleanup_bound || completion?.cleanup_confirmed == true && Report.core_report_bound && Report.core_cleanup_confirmed);
            Report.native_input_evidence_complete = NativeEvidence();
            bool passed = Report.failure_stage is null && Report.report_error is null && Report.cleanup_proven && Report.original_core_exit_code == 0 &&
                Report.original_host_exit_code == 0 && completion?.success == true && Report.core_final_report_bound && Report.UI_job_witness_acknowledged && witness?.HistoricalFailure == false && Report.native_input_evidence_complete;
            Report.status = passed ? "completed_native_WinUI_session_browser_prototype_mechanism_only" : Report.cleanup_proven && Report.failure_stage is null ? "partial_native_input_evidence_incomplete" : Report.cleanup_proven ? "failed_resources_cleaned" : "failed_cleanup_unproven";
            Report.cleanup_stage = "close_original_process_handles_after_exit";
            try { host?.Dispose(); } catch (Exception error) { Report.cleanup_proven = false; CleanupFault("original_Host_handle_dispose", error); }
            try { core?.Dispose(); } catch (Exception error) { Report.cleanup_proven = false; CleanupFault("original_Core_handle_dispose", error); }
            if (!Report.cleanup_proven) { passed = false; Report.status = "failed_cleanup_unproven"; }
            Report.cleanup_stage = "finished_owned_resource_observation";
            Save();
            bool accepted = passed && Report.failure_stage is null && Report.report_error is null && Report.cleanup_proven && Report.status == "completed_native_WinUI_session_browser_prototype_mechanism_only";
            Program.ExitCode = accepted ? 0 : 1; Finished = Report.cleanup_proven;
            Notify(status, accepted ? "原生输入和隔离画面原型验证完成。" : "证据未齐，原型未通过。已保存报告。");
            if (Report.failure_stage is not null || Report.report_error is not null) { Program.ExitCode = 1; Save(); }
        }
    }
    private bool NativeEndMatchesControl() => nativeControl is { ProofPinned: true, OriginalControlExitBound: true } &&
        Report.final_DOM_evidence is { dom_composition_end: int total, dom_trusted_composition_end: int trusted } &&
        NativeEndTrustComparison.Matches(nativeControl.ExpectedEndIsTrusted, total, trusted);
    private bool NativeEvidence() => NativeEndMatchesControl() && Report.displayed_frames >= 2 && Report.input_queue_dropped == 0 && Report.native_composition_range_errors == 0 &&
        Report.native_composition_end_notifications > 0 && Report.native_composition_settled_commits > 0 && Report.forwarded_ime_commit_acks > 0 && !Report.native_composition_pending_end &&
        Report.native_composition_settled.LastOrDefault(fact => fact.decision == "commit") is { initial_buffer_empty: true, post_end_text_change_revision: > 0 } &&
        Report.last_settled_commit_length == Report.last_ime_commit_ack_length && Report.last_settled_commit_length == Report.final_DOM_evidence?.dom_text_length &&
        Report.last_settled_commit_sha256 == Report.last_ime_commit_ack_sha256 && Report.last_settled_commit_sha256 == Report.final_DOM_evidence?.dom_text_sha256 &&
        Report.native_pointer_down > 0 && Report.forwarded_pointer_acks > 0 &&
        Report.native_key_down > 0 && Report.native_key_up > 0 && Report.forwarded_key_acks > 0 && Report.native_composition_started > 0 && Report.native_composition_changed > 0 &&
        Report.native_composition_ended > 0 && Report.forwarded_composition_acks >= 3 && Report.final_DOM_evidence is { wasm_result: 42, raf_count: >= 60, dom_composing: false,
            frame_pixel_sentinel: true, different_frame_observed: true, captured_frame_count: >= 2, dom_expected_hello: true,
            dom_composition_start: > 0, dom_composition_update: > 0, dom_composition_end: > 0, dom_trusted_composition_start: > 0,
            dom_trusted_composition_update: > 0, dom_trusted_input_count: > 0 };
}

internal sealed class UiReport
{
    public int schema_version => 1;
    public string producer => "派蒙";
    public string nonce { get; set; } = "";
    public string manifest_sha256 { get; set; } = "";
    public string source_snapshot_id { get; set; } = "";
    public NativeEndTrustControl? native_end_control { get; set; }
    public string status { get; set; } = "not_run";
    public DateTimeOffset updated_utc { get; set; }
    public string? failure_stage { get; set; }
    public string? failure { get; set; }
    public int? failure_native_error { get; set; }
    public string? failure_native_stage { get; set; }
    public string? report_error { get; set; }
    public int? report_native_error { get; set; }
    public string? cleanup_stage { get; set; }
    public List<UiCleanupFailure> cleanup_failures { get; } = [];
    public uint original_core_pid { get; set; }
    public CoreIdentityBinding? core_identity { get; set; }
    public bool original_core_kernel_pipe_verified { get; set; }
    public uint? original_host_pid { get; set; }
    public ulong? original_host_creation_filetime { get; set; }
    public bool original_host_identity_verified { get; set; }
    public bool UI_job_witness_acknowledged { get; set; }
    public ViewerJobWitness? job_witness { get; set; }
    public string? station_name { get; set; }
    public string? desktop_name { get; set; }
    public string? package_sid_sha256 { get; set; }
    public long last_displayed_frame_id { get; set; }
    public int displayed_frames { get; set; }
    public int forwarded_input_acks { get; set; }
    public int forwarded_pointer_acks { get; set; }
    public int forwarded_key_acks { get; set; }
    public int forwarded_composition_acks { get; set; }
    public int forwarded_ime_commit_acks { get; set; }
    public int? last_ime_commit_ack_length { get; set; }
    public string? last_ime_commit_ack_sha256 { get; set; }
    public int ime_rpc_successful_ack_count { get; set; }
    public List<ImeRpcEvidence> ime_rpc_evidence { get; } = [];
    public bool ime_rpc_evidence_overflow { get; set; }
    public int ime_rpc_unrecorded_successful_acks { get; set; }
    public int native_pointer_down { get; set; }
    public int native_key_down { get; set; }
    public int native_key_up { get; set; }
    public int native_preview_key_down_events { get; set; }
    public int native_preview_key_up_events { get; set; }
    public int native_preview_surface_events { get; set; }
    public int native_preview_proxy_events { get; set; }
    public int native_preview_other_events { get; set; }
    public int native_preview_prehandled_events { get; set; }
    public int native_preview_composing_events { get; set; }
    public int surface_focus_attempts { get; set; }
    public int surface_focus_returned_true { get; set; }
    public int surface_exact_focus_observed { get; set; }
    public bool native_key_diagnostics_saturated { get; set; }
    public int native_composition_started { get; set; }
    public int native_composition_changed { get; set; }
    public int native_composition_ended { get; set; }
    public int native_composition_end_notifications { get; set; }
    public List<NativeCompositionEndNotificationFact> native_composition_end_notification_ranges { get; } = [];
    public int native_composition_settled_commits { get; set; }
    public int native_composition_settled_cancels { get; set; }
    public List<NativeCompositionSettledFact> native_composition_settled { get; } = [];
    public int? last_settled_commit_length { get; set; }
    public string? last_settled_commit_sha256 { get; set; }
    public bool native_composition_pending_end { get; set; }
    public int native_composition_range_errors { get; set; }
    public List<NativeCompositionRangeFact> native_composition_ranges { get; } = [];
    public int native_clipboard_default_blocked { get; set; }
    public int native_drag_default_blocked { get; set; }
    public int input_queue_dropped { get; set; }
    public List<InitialFrameFact> initial_frames { get; } = [];
    public int stale_epoch_packets_dropped { get; set; }
    public SessionHeader? last_DOM_evidence { get; set; }
    public SessionHeader? final_DOM_evidence { get; set; }
    public bool original_core_exit_proven { get; set; }
    public int? original_core_exit_code { get; set; }
    public bool original_host_exit_proven { get; set; }
    public uint? original_host_exit_code { get; set; }
    public string? core_report_sha256 { get; set; }
    public string? completion_report_sha256 { get; set; }
    public string? core_status { get; set; }
    public bool core_report_bound { get; set; }
    public bool core_report_identity_bound { get; set; }
    public bool core_final_report_bound { get; set; }
    public bool core_cleanup_confirmed { get; set; }
    public bool no_Host_created_failure_proven { get; set; }
    public bool failed_case_terminal_cleanup_bound { get; set; }
    public bool independent_job_closed_after_empty { get; set; }
    public bool native_input_evidence_complete { get; set; }
    public bool cleanup_proven { get; set; }
    public string clipboard_scope => "project_clipboard_defaults_blocked_WinSta0_clipboard_APIs_not_called";
    public string input_scope => "owned_frame_mouse_allowlisted_key_and_current_native_composition_buffer_only_not_full_keyboard_or_TSF_acceptance";
    public string unmeasured_input_scope => "system_input_providers_UIA_global_IME_clipboard_and_full_TSF_not_measured";
    public string production_gate => "blocked_pending_G1_and_product_integration";
    public string isolation_acceptance => "not_claimed_prototype_only";
    public string lsa_cleanup => "not_claimed";
    public int UAC_requests => 0;
    public int force_termination_calls => 0;
}
internal sealed record InitialFrameFact(string file, int bytes, string sha256, long frame_id);
internal sealed record NativeCompositionRangeFact(string phase, int start_index, int length, int selection_start, int selection_length, bool valid);
internal sealed record NativeCompositionEndNotificationFact(int start_index, int length, int selection_start, int selection_length, int notification_buffer_length);
internal sealed record ImeRpcEvidence(string command, long request_id, int? request_text_length, string? request_text_sha256, SessionHeader evidence);
internal sealed record UiCleanupFailure(string stage, string exception_type, uint hresult, int? native_error, string? fixed_native_stage);
