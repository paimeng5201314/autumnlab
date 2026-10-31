using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text.Json;
using AutumnOS.SessionBrowserProtocol;

namespace AutumnOS.OwnedBrowserParent;

internal static class SessionParent
{
    internal static async Task<int> Run(SessionInputs input, string manifestPath, string nonce, string viewerPipe, uint viewerPid)
    {
        if (viewerPipe != "AutumnOS.SessionViewer." + nonce || viewerPid == 0) return 2;
        string caseDirectory = Path.Combine(input.case_root, "session-browser-case-" + nonce);
        OwnedFiles.PlainAncestors(caseDirectory);
        if (!Directory.Exists(caseDirectory)) throw new DirectoryNotFoundException("UI_MUST_CREATE_ITS_EXACT_FRESH_CASE");
        string reportPath = Path.Combine(caseDirectory, "core-report.json");
        using (new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
        var launch = new NetOnlyLaunch();
        var report = new CoreReport { nonce = nonce, manifest_sha256 = input.manifest_sha256, source_snapshot_id = input.source_snapshot_id, netonly_launch = launch };
        SessionJobWitness? witness = null;
        SessionHeader? hostComplete = null;
        string? stationName = null;
        string stage = "original_viewer_authentication";
        using var viewer = SessionNative.OpenProcess(0x101000, false, viewerPid);
        using var ui = new NamedPipeClientStream(".", viewerPipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        using var hostPipe = new NamedPipeServerStream("AutumnOS.SessionHost." + nonce, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
        void Fail(string failedStage, Exception? error = null)
        { report.failure_stage ??= failedStage; report.failure ??= error is null ? "ASSERTION_FAILED" : Program.FailureCode(error); report.status = "failed"; }
        void Save()
        { try { Program.SaveOwnedReport(reportPath, report); } catch (Exception error) { report.report_error ??= Program.FailureCode(error); Fail("core_report_write", error); } }
        void RequireReport() { if (report.report_error is not null) throw new IOException("DURABLE_CORE_REPORT_REQUIRED"); }
        void Tick()
        {
            witness?.Tick();
            if (witness is { historical_query_failure: true } || witness?.audit.ObservationGap == true || witness?.audit.Held.Any(p => !witness.audit.BoundRestricted(p)) == true)
                throw new InvalidDataException("CORE_INDEPENDENT_JOB_OR_OS_TOKEN_HISTORY_FAILED");
        }
        async Task<SessionPacket> Read(Stream stream, bool fromHost)
        {
            while (true)
            {
                Task<SessionPacket?> read = SessionProtocol.ReadAsync(stream, nonce, 1, CancellationToken.None);
                while (!read.IsCompleted)
                {
                    Tick();
                    bool peerExited = fromHost ? launch.Process is not null && launch.Exited : viewer.IsInvalid || SessionNative.Exited(viewer);
                    if (peerExited)
                    {
                        // Buffered final packets may complete after the original peer exits.
                        await Task.WhenAny(read, Task.Delay(200));
                        if (!read.IsCompleted) throw new EndOfStreamException("ORIGINAL_PEER_EXITED_BEFORE_COMPLETE_PACKET");
                    }
                    await Task.WhenAny(read, Task.Delay(50));
                }
                SessionPacket? packet = await read;
                if (packet is not null) { Tick(); return packet; }
            }
        }
        async Task Write(Stream stream, SessionHeader header) => await SessionProtocol.WriteAsync(stream, new(header, []), CancellationToken.None);
        Save();
        try
        {
            RequireReport();
            using var current = SessionNative.Current();
            SessionIdentity self = SessionNative.Identity(current);
            if (viewer.IsInvalid || SessionNative.Exited(viewer) || SessionNative.GetProcessId(viewer) != viewerPid ||
                !string.Equals(SessionNative.Image(viewer), Path.Combine(input.ui_directory, input.ui_executable), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("FROZEN_ORIGINAL_WINUI_VIEWER_REQUIRED");
            SessionIdentity peer = SessionNative.Identity(viewer);
            if (peer.user_sid_sha256 != self.user_sid_sha256 || peer.session_id != self.session_id || peer.integrity_rid != self.integrity_rid ||
                peer.elevated != self.elevated || peer.appcontainer != self.appcontainer || peer.authentication_id != self.authentication_id ||
                self.elevated || self.appcontainer || self.integrity_rid != 8192) throw new InvalidDataException("ORDINARY_SAME_SESSION_VIEWER_TOKEN_REQUIRED");
            report.original_viewer_identity_verified = true;
            report.parent_station_before = SessionNative.StationName(SessionNative.GetProcessWindowStation());
            if (report.parent_station_before != "WinSta0") throw new InvalidDataException("CORE_ORIGINAL_WINSTA0_REQUIRED");
            using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25))) await ui.ConnectAsync(budget.Token);
            if (!SessionNative.GetNamedPipeServerProcessId(ui.SafePipeHandle, out uint uiKernelPid) || uiKernelPid != viewerPid || SessionNative.Exited(viewer))
                throw new InvalidDataException("ORIGINAL_VIEWER_KERNEL_PIPE_PID_REQUIRED");
            report.original_viewer_kernel_pipe_verified = true;
            await Write(ui, new(1, nonce, 1, "hello"));
            if ((await Read(ui, false)).header.kind != "ready") throw new InvalidDataException("VIEWER_HELLO_READY_ORDER");
            string sid = DerivedSid(nonce);
            // Only this UI-created, nonce-bound, guarded new case is modified.
            new DirectoryInfo(caseDirectory).SetAccessControl(OwnedFiles.Descriptor(sid, false));
            stage = "one_NETONLY_station_host"; Save(); RequireReport();
            string arguments = "--station-host " + NetOnlyLaunch.Quote(manifestPath) + " " + nonce + " AutumnOS.SessionHost." + nonce + " " + Environment.ProcessId + " " + NetOnlyLaunch.Quote(caseDirectory);
            launch.Start(Path.Combine(input.core_directory, input.core_executable), arguments, input.core_directory);
            stage = "connect_original_station_host_pipe";
            using (var budget = new CancellationTokenSource(TimeSpan.FromSeconds(25)))
            {
                Task connection = hostPipe.WaitForConnectionAsync(budget.Token);
                while (!connection.IsCompleted)
                { if (launch.Exited) throw new EndOfStreamException("ORIGINAL_STATION_HOST_EXITED_BEFORE_PIPE"); await Task.WhenAny(connection, Task.Delay(50)); }
                await connection;
            }
            stage = "bind_original_station_host_kernel_pipe_PID";
            if (!SessionNative.GetNamedPipeClientProcessId(hostPipe.SafePipeHandle, out uint hostPid) || hostPid != launch.original_pid || launch.Exited)
                throw new InvalidDataException("ORIGINAL_STATION_HOST_KERNEL_PIPE_PID_REQUIRED");
            report.original_station_host_kernel_pipe_verified = true;
            if ((await Read(hostPipe, true)).header.kind != "hello") throw new InvalidDataException("HOST_HELLO_ORDER");
            await Write(hostPipe, new(1, nonce, 1, "ready"));
            stage = "read_original_host_job_ready_before_any_profile";
            SessionHeader jobReady = (await Read(hostPipe, true)).header;
            if (jobReady.kind != "job_ready" || jobReady.job_handle is null) throw new InvalidDataException("EXACT_ORIGINAL_HOST_JOB_REQUIRED");
            stage = "duplicate_exact_original_job_QUERY_only_to_CoreParent";
            witness = new SessionJobWitness(launch.Process!, jobReady.job_handle.Value, Path.Combine(caseDirectory, "host"), Path.Combine(caseDirectory, "runtime"), sid);
            report.job_witness = witness;
            if (!witness.fresh_admission_passed) throw new InvalidDataException("FRESH_QUERY_ONLY_JOB_WITNESS_FAILED");
            await Write(ui, jobReady with { job_handle = unchecked((ulong)witness.Handle.ToInt64()),
                host_process_handle = unchecked((ulong)launch.Process!.DangerousGetHandle().ToInt64()), host_pid = launch.original_pid,
                host_creation_filetime = launch.original_creation_filetime });
            stage = "wait_WINUI_QUERY_only_original_job_witness";
            SessionHeader uiJobBound = (await Read(ui, false)).header;
            if (uiJobBound.kind != "job_bound" || uiJobBound.success != true) throw new InvalidDataException("WINUI_ORIGINAL_JOB_QUERY_WITNESS_REQUIRED");
            report.UI_job_witness_acknowledged = true; Save(); RequireReport();
            await Write(hostPipe, new SessionHeader(1, nonce, 1, "start") { success = true });
            stage = "await_exact_owned_station_desktop_profile_metadata";
            SessionHeader owned = (await Read(hostPipe, true)).header;
            if (owned.kind != "owned_ready" || owned.station_name is null || owned.desktop_name is null || owned.package_sid_sha256 != OwnedFiles.HashText(sid))
                throw new InvalidDataException("EXACT_OWNED_STATION_PACKAGE_BINDING_REQUIRED");
            stationName = owned.station_name; report.station_name = stationName; report.desktop_name = owned.desktop_name;
            stage = "independent_station_security_readback";
            IntPtr query = SessionNative.OpenWindowStationW(stationName, false, 0x20002);
            Native.Check(query != IntPtr.Zero, "ordinary_CoreParent_query_exact_automatic_station");
            try { report.independent_station_descriptor_sha256 = OwnedStation.VerifyStationHandle(query, stationName, self.UserSid, sid); }
            finally { Native.Check(SessionNative.CloseWindowStation(query), "close_independent_station_query_before_browser_start"); }
            report.independent_station_security_verified = true;
            await Write(ui, owned);
            stage = "wait_viewer_owned_context_positive_ack";
            SessionHeader ownedAck = (await Read(ui, false)).header;
            if (ownedAck.kind != "ack" || ownedAck.success != true) throw new InvalidDataException("VIEWER_OWNED_CONTEXT_ACK");
            Save(); RequireReport(); await Write(hostPipe, new SessionHeader(1, nonce, 1, "ack") { success = true });
            stage = "await_owned_browser_presentation_ready";
            SessionHeader ready = (await Read(hostPipe, true)).header;
            if (ready.kind != "ready" || ready.success != true || ready.page_succeeded != true) throw new InvalidDataException("HOST_OWNED_PAGE_PRESENTATION_READY_REQUIRED");
            await Write(ui, ready);
            var gate = new SessionRequestGate(nonce, 1);
            stage = "presentation_proxy";
            while (true)
            {
                SessionPacket request = await Read(ui, false); gate.Begin(request.header); Tick(); RequireReport();
                await SessionProtocol.WriteAsync(hostPipe, request, CancellationToken.None);
                SessionPacket reply = await Read(hostPipe, true); gate.Complete(reply.header); Tick();
                if (reply.header.kind == "frame") report.frames_forwarded++; else if (request.header.command != "close") report.input_actions_forwarded++;
                await SessionProtocol.WriteAsync(ui, reply, CancellationToken.None); Save();
                if (request.header.command == "close")
                {
                    hostComplete = (await Read(hostPipe, true)).header;
                    if (hostComplete.kind != "host_complete") throw new InvalidDataException("HOST_COMPLETE_REQUIRED_AFTER_NORMAL_CLOSE");
                    break;
                }
                if (reply.header.success == false) throw new InvalidDataException("PRESENTATION_REPLY_FAILED");
            }
        }
        catch (Exception error) { Fail(stage, error); }
        finally
        {
            // Closing this endpoint is a normal EOF request to the frozen Host.
            try { hostPipe.Dispose(); } catch (Exception error) { Fail("host_pipe_dispose", error); }
            try { await launch.ObserveNaturalExit(() => { report.status = "failed_pending_original_station_host"; Save(); }); }
            catch (Exception error) { Fail("original_host_exit_observation", error); }
            if (witness is not null)
            {
                while (true)
                {
                    witness.Tick();
                    if (witness.groups_retired) break;
                    report.status = "failed_pending_original_browser_job"; Save(); await Task.Delay(100);
                }
            }
            string hostReport = Path.Combine(caseDirectory, "station-host-report.json");
            try
            {
                if (File.Exists(hostReport) && launch.original_process_signaled)
                {
                    report.station_host_report_sha256 = Program.HashFile(hostReport);
                    using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(hostReport)); JsonElement facts = document.RootElement;
                    report.station_host_final_report_bound = facts.GetProperty("nonce").GetString() == nonce && facts.GetProperty("manifest_sha256").GetString() == input.manifest_sha256 &&
                        facts.GetProperty("source_snapshot_id").GetString() == input.source_snapshot_id && hostComplete?.report_sha256 == report.station_host_report_sha256;
                    // A desktop/profile failure can occur after station creation
                    // but before owned_ready. Recover only the frozen original
                    // Host's final exact name for read-only absence checking.
                    bool originalReportBound = facts.GetProperty("nonce").GetString() == nonce &&
                        facts.GetProperty("manifest_sha256").GetString() == input.manifest_sha256 &&
                        facts.GetProperty("source_snapshot_id").GetString() == input.source_snapshot_id;
                    if (!originalReportBound) throw new InvalidDataException("ORIGINAL_HOST_FINAL_REPORT_IDENTITY_REQUIRED");
                    report.station_host_cleanup_reported = facts.GetProperty("cleanup_proven").GetBoolean();
                    report.station_host_success_reported = facts.GetProperty("status").GetString() == "completed_session_station_host_mechanism_only";
                    report.no_owned_station_created_reported = !facts.GetProperty("owned_context").GetProperty("station_created").GetBoolean();
                    if (stationName is null && !report.no_owned_station_created_reported)
                    {
                        string exact = facts.GetProperty("owned_context").GetProperty("station_name").GetString() ?? "";
                        if (exact.Length is < 1 or > 256 || exact.Equals("WinSta0", StringComparison.OrdinalIgnoreCase) ||
                            exact.Any(c => char.IsControl(c) || c is '\\' or '/' or ':' || char.IsWhiteSpace(c)))
                            throw new InvalidDataException("ORIGINAL_HOST_EXACT_FRESH_STATION_NAME_REQUIRED");
                        stationName = exact; report.station_name = exact;
                    }
                }
                if (stationName is not null && launch.original_process_signaled)
                {
                    SessionNative.ClearLastError(0); Marshal.SetLastPInvokeError(0);
                    IntPtr probe = SessionNative.OpenWindowStationW(stationName, false, 2);
                    report.station_postcheck_native_error = probe == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                    report.station_postcheck_returned_handle = probe != IntPtr.Zero;
                    if (probe != IntPtr.Zero) Native.Check(SessionNative.CloseWindowStation(probe), "close_owned_station_absence_probe_handle");
                    report.exact_station_absent = probe == IntPtr.Zero && report.station_postcheck_native_error is 2 or 3;
                }
                report.parent_station_after = SessionNative.StationName(SessionNative.GetProcessWindowStation());
                report.parent_station_unchanged = report.parent_station_after == report.parent_station_before;
            }
            catch (Exception error) { Fail("independent_final_facts", error); }
            bool originalExited = launch.no_child_created_proven || launch.original_process_signaled && launch.original_thread_signaled;
            report.cleanup_proven = originalExited && (witness is null || witness.groups_retired) && report.parent_station_unchanged &&
                (launch.no_child_created_proven || report.station_host_cleanup_reported && (report.exact_station_absent || report.no_owned_station_created_reported));
            if (witness is not null)
            { try { witness.CloseAfterEmpty(); } catch (Exception error) { report.cleanup_proven = false; Fail("witness_close", error); } }
            try { launch.CloseOriginalAfterExit(); } catch (Exception error) { report.cleanup_proven = false; Fail("original_host_handle_close", error); }
            bool passed = report.failure_stage is null && report.report_error is null && launch.create_process_with_logon_calls == 1 && launch.exit_code == 0 &&
                launch.token_admission_passed && launch.original_image_verified && report.UI_job_witness_acknowledged && report.independent_station_security_verified &&
                witness is { historical_query_failure: false } && !witness.audit.ObservationGap && witness.audit.Held.Count > 1 && witness.audit.Held.All(witness.audit.BoundRestricted) &&
                report.station_host_final_report_bound && report.station_host_success_reported && hostComplete?.success == true && report.frames_forwarded > 0 && report.cleanup_proven;
            report.status = passed ? "completed_owned_session_browser_prototype_mechanism_only" : "failed"; Save();
            try
            {
                if (ui.IsConnected) await Write(ui, new SessionHeader(1, nonce, 1, "host_complete") { success = passed && report.report_error is null,
                    cleanup_confirmed = report.cleanup_proven, report_sha256 = Program.HashFile(reportPath), station_name = stationName });
            }
            catch (Exception error) { Fail("final_viewer_protocol", error); Save(); }
        }
        return report.status == "completed_owned_session_browser_prototype_mechanism_only" && report.report_error is null ? 0 : 1;
    }
    private static string DerivedSid(string nonce)
    {
        int hr = DeriveAppContainerSidFromAppContainerName("AutumnOS.Paimon.SessionBrowser." + nonce, out IntPtr sid);
        if (hr != 0 || sid == IntPtr.Zero) throw new COMException("DERIVE_FIXED_SESSION_PROFILE_SID", hr);
        try { return Native.SidString(sid); } finally { Native.FreeSid(sid); }
    }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
}

internal sealed class CoreReport
{
    public int schema_version => 1;
    public string producer => "派蒙";
    public string nonce { get; init; } = "";
    public string manifest_sha256 { get; init; } = "";
    public string source_snapshot_id { get; init; } = "";
    public string status { get; set; } = "running";
    public string? failure_stage { get; set; }
    public string? failure { get; set; }
    public string? report_error { get; set; }
    public NetOnlyLaunch? netonly_launch { get; init; }
    public SessionJobWitness? job_witness { get; set; }
    public bool original_viewer_identity_verified { get; set; }
    public bool original_viewer_kernel_pipe_verified { get; set; }
    public bool original_station_host_kernel_pipe_verified { get; set; }
    public bool UI_job_witness_acknowledged { get; set; }
    public string? station_name { get; set; }
    public string? desktop_name { get; set; }
    public string? independent_station_descriptor_sha256 { get; set; }
    public bool independent_station_security_verified { get; set; }
    public string? station_host_report_sha256 { get; set; }
    public bool station_host_final_report_bound { get; set; }
    public bool station_host_cleanup_reported { get; set; }
    public bool station_host_success_reported { get; set; }
    public bool no_owned_station_created_reported { get; set; }
    public int? station_postcheck_native_error { get; set; }
    public bool station_postcheck_returned_handle { get; set; }
    public bool exact_station_absent { get; set; }
    public string? parent_station_before { get; set; }
    public string? parent_station_after { get; set; }
    public bool parent_station_unchanged { get; set; }
    public int frames_forwarded { get; set; }
    public int input_actions_forwarded { get; set; }
    public bool cleanup_proven { get; set; }
    public string production_gate => "blocked_pending_G1_and_product_integration";
    public string isolation_acceptance => "not_claimed_owned_page_presentation_prototype_only";
    public string lsa_cleanup => "not_claimed";
    public int UAC_requests => 0;
    public int force_termination_calls => 0;
    public string existing_object_ACL_changes => "none";
}
