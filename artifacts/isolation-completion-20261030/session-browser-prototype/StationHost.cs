using System.IO.Pipes;
using System.Text.Json;
using AutumnOS.SessionBrowserProtocol;

namespace AutumnOS.OwnedBrowserParent;

internal static class StationHost
{
    internal static async Task<int> Run(string manifestPath, string nonce, string pipeName, uint corePid, string caseDirectory)
    {
        using SessionInputs input = ManifestInputs.Load(manifestPath);
        string expectedCase = Path.Combine(input.case_root, "session-browser-case-" + nonce);
        if (!Program.Nonce(nonce) || pipeName != "AutumnOS.SessionHost." + nonce || !Path.GetFullPath(caseDirectory).Equals(expectedCase, StringComparison.OrdinalIgnoreCase)) return 2;
        OwnedFiles.PlainAncestors(caseDirectory);
        using var parent = SessionNative.OpenProcess(0x101000, false, corePid);
        using var current = SessionNative.Current();
        if (parent.IsInvalid || SessionNative.Exited(parent) || SessionNative.GetProcessId(parent) != corePid ||
            !string.Equals(SessionNative.Image(parent), Path.Combine(input.core_directory, input.core_executable), StringComparison.OrdinalIgnoreCase) ||
            !SessionNative.TokenAdmission(SessionNative.Identity(parent), SessionNative.Identity(current))) return 3;
        var report = new HostReport { nonce = nonce, manifest_sha256 = input.manifest_sha256, source_snapshot_id = input.source_snapshot_id };
        string reportPath = Path.Combine(caseDirectory, "station-host-report.json");
        using (new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
        void Fail(string stage, Exception? error = null)
        { report.failure_stage ??= stage; report.failure ??= error is null ? "ASSERTION_FAILED" : Program.FailureCode(error); report.status = "failed"; }
        void Save()
        {
            try { Program.SaveOwnedReport(reportPath, report); }
            catch (Exception error) { report.report_error ??= Program.FailureCode(error); Fail("report_write", error); }
        }
        void RequireReport() { if (report.report_error is not null) throw new IOException("DURABLE_HOST_REPORT_REQUIRED"); }
        void RequireParentLive() { if (SessionNative.Exited(parent)) throw new EndOfStreamException("ORIGINAL_CORE_PARENT_EXITED"); }
        var station = new OwnedStation(nonce); report.owned_context = station;
        var job = new JobEvidence(); report.job = job;
        BrowserChildProcess? browser = null;
        SessionHeader? closeRequest = null;
        using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        string stage = "connect_original_CoreParent";
        Save();
        async Task<SessionPacket> ReadCore()
        {
            while (true)
            {
                Task<SessionPacket?> read = SessionProtocol.ReadAsync(pipe, nonce, 1, CancellationToken.None);
                while (!read.IsCompleted)
                {
                    browser?.Tick();
                    if (SessionNative.Exited(parent)) throw new EndOfStreamException("ORIGINAL_CORE_PARENT_EXITED");
                    await Task.WhenAny(read, Task.Delay(100));
                }
                SessionPacket? packet = await read;
                if (packet is not null) { RequireParentLive(); return packet; }
            }
        }
        async Task Write(SessionHeader header) => await SessionProtocol.WriteAsync(pipe, new(header, []), CancellationToken.None);
        try
        {
            RequireReport();
            using (var connectionBudget = new CancellationTokenSource(TimeSpan.FromSeconds(25))) await pipe.ConnectAsync(connectionBudget.Token);
            if (!SessionNative.GetNamedPipeServerProcessId(pipe.SafePipeHandle, out uint server) || server != corePid || SessionNative.Exited(parent)) throw new InvalidDataException("ORIGINAL_CORE_PIPE_SERVER_IDENTITY");
            report.original_parent_kernel_pipe_verified = true;
            await Write(new(1, nonce, 1, "hello"));
            if ((await ReadCore()).header.kind != "ready") throw new InvalidDataException("HOST_READY_HANDSHAKE");
            stage = "create_job_before_any_profile_or_browser";
            job.Create();
            job.Observe(new ProcessAudit(Path.Combine(caseDirectory, "host"), Path.Combine(caseDirectory, "runtime"), null));
            if (!job.Empty || job.limit_flags != 0) throw new InvalidDataException("INITIAL_EMPTY_ZERO_FLAGS_JOB");
            Save(); RequireReport();
            await Write(new SessionHeader(1, nonce, 1, "job_ready") { job_handle = unchecked((ulong)job.Handle.ToInt64()) });
            SessionHeader startAck = (await ReadCore()).header;
            if (startAck.kind != "start" || startAck.success != true) throw new InvalidDataException("PARENT_AND_UI_QUERY_JOB_WITNESSES_REQUIRED_BEFORE_OWNED_OBJECTS");
            report.parent_and_UI_witness_acknowledged = true;
            stage = "create_one_NETONLY_automatic_station_desktop_profile";
            Save(); RequireReport(); RequireParentLive(); station.Create(); Save(); RequireReport();
            await Write(new SessionHeader(1, nonce, 1, "owned_ready") { station_name = station.station_name, desktop_name = station.desktop_name, package_sid_sha256 = station.package_sid_sha256 });
            SessionHeader ownedAck = (await ReadCore()).header;
            if (ownedAck.kind != "ack" || ownedAck.success != true) throw new InvalidDataException("CORE_PARENT_INDEPENDENT_STATION_READBACK_REQUIRED");
            stage = "copy_new_frozen_host_and_runtime";
            string hostTree = Path.Combine(caseDirectory, "host"), runtimeTree = Path.Combine(caseDirectory, "runtime");
            RequireParentLive(); OwnedFiles.CopyFrozen(input.core_directory, hostTree, input.core_files, station.package_sid);
            OwnedFiles.CopyFrozen(input.runtime_directory, runtimeTree, input.runtime_files, station.package_sid);
            string udf = Path.Combine(station.ProfileFolder, "session-udf-" + nonce), temp = Path.Combine(station.ProfileFolder, "session-temp-" + nonce);
            OwnedFiles.CreatePrivateDirectory(udf, station.package_sid, true); OwnedFiles.CreatePrivateDirectory(temp, station.package_sid, true);
            browser = new BrowserChildProcess(nonce, hostTree, runtimeTree, udf, temp, Path.Combine(hostTree, input.core_executable), station, job);
            report.browser_child = browser;
            stage = "launch_atomic_job_zero_cap_browser"; Save(); RequireReport(); RequireParentLive(); browser.Start(Save);
            SessionPacket controller = await browser.Receive("controller_ready");
            browser.BindBrowserBeforeLoad(controller.header.browser_pid ?? 0);
            var browserGate = new SessionRequestGate(nonce, 1); var uiGate = new SessionRequestGate(nonce, 1);
            var load = new SessionHeader(1, nonce, 1, "input") { request_id = 1, command = "load_owned_page" };
            browserGate.Begin(load); await browser.Send(load);
            SessionPacket loaded = await browser.Receive("input_result", 1); browserGate.Complete(loaded.header);
            if (loaded.header.success != true || loaded.header.page_succeeded != true) throw new InvalidDataException("OWNED_SYNTHETIC_PAGE_NOT_LOADED");
            stage = "presentation_ready"; Save(); RequireReport();
            await Write(new SessionHeader(1, nonce, 1, "ready") { success = true, initialization_succeeded = true, page_succeeded = true, browser_pid = browser.browser_pid });
            while (true)
            {
                SessionPacket request = await ReadCore();
                if (request.header.kind != "input") throw new InvalidDataException("FIXED_PRESENTATION_INPUT_ONLY");
                uiGate.Begin(request.header); browser.RequireHealthy(); station.VerifyRuntime(); RequireReport();
                if (request.header.command == "close") { closeRequest = request.header; break; }
                SessionHeader mapped = request.header with { request_id = request.header.request_id + 1 };
                browserGate.Begin(mapped); await browser.Send(mapped);
                string responseKind = mapped.command == "capture" ? "frame" : "input_result";
                SessionPacket reply = await browser.Receive(responseKind, mapped.request_id); browserGate.Complete(reply.header);
                SessionHeader forwarded = reply.header with { request_id = request.header.request_id };
                uiGate.Complete(forwarded); browser.RequireHealthy();
                if (forwarded.success == false) throw new InvalidDataException("BROWSER_PRESENTATION_ACTION_FAILED");
                if (responseKind == "frame") report.frames_forwarded++; else report.input_actions_forwarded++;
                await SessionProtocol.WriteAsync(pipe, new(forwarded, reply.data), CancellationToken.None);
                Save();
            }
        }
        catch (Exception error) { Fail(stage, error); }
        finally
        {
            // Do not use healthy/acceptance gates to block the failure cleanup path.
            if (browser is not null)
            {
                try { await browser.CloseAndRetire(Save); } catch (Exception error) { Fail("browser_cleanup_barrier", error); }
                while (!browser.resource_cleanup_proven)
                {
                    report.status = "failed_pending_original_browser_group"; Save();
                    try { await browser.CloseAndRetire(Save); } catch (Exception error) { Fail("browser_cleanup_retry_observation", error); }
                    await Task.Delay(100);
                }
            }
            bool resourcesRetired = browser?.resource_cleanup_proven ?? (!job.created || job.Empty);
            try { station.CleanupAfterGroupEmpty(resourcesRetired); } catch (Exception error) { Fail("owned_context_cleanup_barrier", error); }
            while (!station.cleanup_proven)
            { report.status = "failed_pending_owned_context_cleanup"; Save(); station.CleanupAfterGroupEmpty(resourcesRetired); await Task.Delay(250); }
            if (job.created) { try { job.CloseAfterEmpty(); } catch (Exception error) { Fail("close_original_empty_job", error); } }
            report.cleanup_proven = resourcesRetired && station.cleanup_proven && (!job.created || job.original_handle_closed_after_empty);
            bool passed = report.failure_stage is null && report.report_error is null && closeRequest is not null && report.frames_forwarded > 0 && browser?.normal_acceptance == true && report.cleanup_proven && !station.cleanup.any_failure;
            report.status = passed ? "completed_session_station_host_mechanism_only" : "failed";
            Save();
            bool accepted = passed && report.report_error is null && report.status == "completed_session_station_host_mechanism_only";
            try
            {
                if (pipe.IsConnected)
                {
                    if (closeRequest is not null)
                    {
                        SessionHeader terminal = new(1, nonce, 1, "input_result") { request_id = closeRequest.request_id, success = accepted };
                        if (browser?.complete_header is SessionHeader actualFinalDOM) terminal = SessionProtocol.WithEvidence(terminal, actualFinalDOM);
                        await Write(terminal);
                    }
                    await Write(new SessionHeader(1, nonce, 1, "host_complete") { success = accepted, cleanup_confirmed = report.cleanup_proven,
                        report_sha256 = Program.HashFile(reportPath), station_name = station.station_name.Length == 0 ? null : station.station_name,
                        desktop_name = station.desktop_name, package_sid_sha256 = station.package_sid_sha256 });
                }
            }
            catch (Exception error) { Fail("final_parent_protocol", error); Save(); }
        }
        return report.status == "completed_session_station_host_mechanism_only" && report.report_error is null ? 0 : 1;
    }
}

internal sealed class HostReport
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
    public OwnedStation? owned_context { get; set; }
    public BrowserChildProcess? browser_child { get; set; }
    public JobEvidence? job { get; set; }
    public bool original_parent_kernel_pipe_verified { get; set; }
    public bool parent_and_UI_witness_acknowledged { get; set; }
    public int frames_forwarded { get; set; }
    public int input_actions_forwarded { get; set; }
    public bool cleanup_proven { get; set; }
    public string isolation_acceptance => "not_claimed_owned_page_presentation_prototype_only";
    public string production_gate => "blocked_pending_G1_and_product_integration";
    public string lsa_cleanup => "not_claimed";
    public int UAC_requests => 0;
    public string existing_object_ACL_changes => "none";
    public int application_network_calls => 0;
    public int clipboard_calls => 0;
    public int force_termination_calls => 0;
}
