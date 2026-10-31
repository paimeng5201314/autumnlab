using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using AutumnOS.SessionBrowserProtocol;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.OwnedBrowserParent;

internal sealed class BrowserChildProcess(string nonce, string hostTree, string runtimeTree, string udf, string temp,
    string executable, OwnedStation station, JobEvidence exactJob)
{
    public ProcessAudit audit { get; } = new(hostTree, runtimeTree, station.package_sid);
    public JobEvidence job => exactJob;
    public System.Collections.Concurrent.ConcurrentQueue<string> failures { get; } = new();
    public CleanupJournal cleanup { get; } = new();
    public string stage { get; private set; } = "not_run";
    public bool no_child_created_proven { get; private set; } = true;
    public bool stdout_eof { get; private set; }
    public bool stdout_capture_unavailable { get; private set; }
    public bool normal_browser_exit { get; private set; }
    public bool final_sdk_empty { get; private set; }
    public bool host_complete_received { get; private set; }
    public bool browser_token_verified_before_load { get; private set; }
    public uint browser_pid { get; private set; }
    public bool retired { get; private set; }
    public bool close_requested { get; private set; }
    public bool pending_live_group { get; private set; }
    public bool original_root_job_membership { get; private set; }
    public bool root_token_verified_before_resume { get; private set; }
    public uint? resume_return { get; private set; }
    public int? resume_native_error { get; private set; }
    public int failure_resume_attempts { get; private set; }
    public bool original_thread_signaled { get; private set; }
    public SessionHeader? complete_header { get; private set; }
    public bool kernel_group_empty_proven => no_child_created_proven && (!job.created || job.Empty) ||
        job.atomic_launch_attribute_attached && job.Empty && audit.Held.Count > 0 && audit.AllSignaled;
    public bool resource_cleanup_proven => retired && kernel_group_empty_proven;
    public bool normal_acceptance => retired && stdout_eof && !stdout_capture_unavailable && failures.Count == 0 &&
        !audit.ObservationGap && !job.query_failure_seen && !job.membership_failure_seen && audit.RestrictedObservedTokensPass &&
        root_token_verified_before_resume && original_root_job_membership && browser_token_verified_before_load &&
        !cleanup.any_failure && normal_browser_exit && final_sdk_empty && host_complete_received && complete_header is { success: true, initialization_succeeded: true, page_succeeded: true, controller_disposed: true } &&
        audit.Held.FirstOrDefault(p => p.pid == audit.RootPid)?.exit_code == 0;
    private FileStream? input, output;
    private SafeFileHandle? inputHandle, outputHandle;
    private readonly Channel<SessionPacket> packets = Channel.CreateBounded<SessionPacket>(4);
    private volatile bool captureFinished;
    private Task? captureTask;
    private long lastRequest;
    private IntPtr originalThread;

    internal void Start(Action save)
    {
        IntPtr read = IntPtr.Zero, write = IntPtr.Zero, stdinRead = IntPtr.Zero, stdinWrite = IntPtr.Zero, hostWait = IntPtr.Zero;
        IntPtr list = IntPtr.Zero, handles = IntPtr.Zero, caps = IntPtr.Zero, env = IntPtr.Zero, desktop = IntPtr.Zero, jobs = IntPtr.Zero;
        Native.ProcessInformation process = default;
        bool initialized = false, attempted = false, resumed = false;
        try
        {
            station.VerifyRuntime();
            if (!job.created || job.limit_flags != 0 || !job.Empty) throw new InvalidDataException("PREBOUND_FRESH_EMPTY_ZERO_FLAGS_JOB_REQUIRED");
            var security = new Native.SecurityAttributes { Length = (uint)Marshal.SizeOf<Native.SecurityAttributes>(), Inherit = true };
            stage = "owned_stdout_pipe"; Native.Check(Native.CreatePipe(out read, out write, ref security, 4096), stage);
            Native.Check(Native.SetHandleInformation(read, 1, 0), "stdout_read_not_inherited");
            stage = "owned_stdin_pipe"; Native.Check(Native.CreatePipe(out stdinRead, out stdinWrite, ref security, 4096), stage);
            Native.Check(Native.SetHandleInformation(stdinWrite, 1, 0), "stdin_write_not_inherited");
            Native.Check(DuplicateHandle(Native.GetCurrentProcess(), Native.GetCurrentProcess(), Native.GetCurrentProcess(), out hostWait,
                Native.Synchronize, true, 0), "inherit_original_station_host_SYNCHRONIZE_only");
            nuint size = 0;
            bool initial = Native.InitializeProcThreadAttributeList(IntPtr.Zero, 3, 0, ref size);
            if (initial || Marshal.GetLastWin32Error() != 122 || size is < 32 or > 65536) throw new InvalidDataException("ATTRIBUTE_SIZE");
            list = Marshal.AllocHGlobal(checked((int)size));
            Native.Check(Native.InitializeProcThreadAttributeList(list, 3, 0, ref size), "initialize_three_required_attributes"); initialized = true;
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size); Marshal.WriteIntPtr(handles, write); Marshal.WriteIntPtr(handles, IntPtr.Size, stdinRead); Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, hostWait);
            Native.Check(Native.UpdateProcThreadAttribute(list, 0, Native.HandleList, handles, 3 * (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero), "explicit_two_pipes_and_SYNC_only_host");
            jobs = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobs, job.Handle);
            Native.Check(Native.UpdateProcThreadAttribute(list, 0, JobEvidence.JobListAttribute, jobs, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero), "atomic_exact_unnamed_job"); job.MarkAtomicLaunchAttached();
            caps = Marshal.AllocHGlobal(Marshal.SizeOf<Native.SecurityCapabilities>());
            Marshal.StructureToPtr(new Native.SecurityCapabilities { AppContainerSid = station.PackageSid }, caps, false);
            Native.Check(Native.UpdateProcThreadAttribute(list, 0, Native.SecurityCapabilitiesAttribute, caps, (nuint)Marshal.SizeOf<Native.SecurityCapabilities>(), IntPtr.Zero, IntPtr.Zero), "zero_capability_AppContainer");
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            OwnedFiles.FixedLocal(local); OwnedFiles.PlainAncestors(local);
            var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            { ["SystemRoot"] = Environment.GetFolderPath(Environment.SpecialFolder.Windows), ["LOCALAPPDATA"] = local,
                ["TEMP"] = temp, ["TMP"] = temp, ["DOTNET_EnableDiagnostics"] = "0", ["COMPlus_EnableDiagnostics"] = "0" };
            env = Marshal.StringToHGlobalUni(string.Concat(values.Select(p => p.Key + "=" + p.Value + "\0")) + "\0");
            desktop = Marshal.StringToHGlobalUni(station.DesktopBinding);
            Native.Check(Native.GetProcessTimes(Native.GetCurrentProcess(), out var created, out _, out _, out _), "original_station_host_creation_time");
            var startup = new Native.StartupInfoEx { Startup = new Native.StartupInfo { Size = (uint)Marshal.SizeOf<Native.StartupInfoEx>(), Desktop = desktop,
                Flags = 0x100, StdInput = stdinRead, StdOutput = write, StdError = write }, Attributes = list };
            string command = Quote(executable) + " --browser-host " + nonce + " " + Quote(runtimeTree) + " " + Quote(udf) + " " + Quote(station.station_name) + " " + Quote(station.desktop_name) +
                " " + Environment.ProcessId + " " + created.Value + " " + hostWait.ToInt64();
            stage = "CreateProcess_suspended_exact_private_desktop"; attempted = true; no_child_created_proven = false;
            bool ok = Native.CreateProcessW(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true,
                0x08080404 /* NO_WINDOW | EXTENDED_STARTUPINFO | UNICODE | SUSPENDED */, env, hostTree, ref startup, out process);
            int error = ok ? 0 : Marshal.GetLastWin32Error();
            no_child_created_proven = !ok && process.Process == IntPtr.Zero && process.Thread == IntPtr.Zero && process.ProcessId == 0 && process.ThreadId == 0;
            if (process.Process != IntPtr.Zero) { audit.AdoptRoot(process.Process, process.ProcessId); process.Process = IntPtr.Zero; }
            if (!ok) throw new OwnedNativeException(error, stage);
            root_token_verified_before_resume = audit.Held.Count == 1 && audit.BoundRestricted(audit.Held[0]) && !audit.ObservationGap;
            original_root_job_membership = job.Contains(audit.Held[0].Handle, process.ProcessId, "suspended_original_browser_host");
            if (!root_token_verified_before_resume || !original_root_job_membership) throw new InvalidDataException("SUSPENDED_OS_TOKEN_OR_JOB_ADMISSION_FAILED");
            inputHandle = new SafeFileHandle(stdinWrite, true); stdinWrite = IntPtr.Zero; input = new FileStream(inputHandle, FileAccess.Write);
            outputHandle = new SafeFileHandle(read, true); read = IntPtr.Zero; output = new FileStream(outputHandle, FileAccess.Read);
            captureTask = Task.Run(Capture);
            Native.CloseHandle(write); write = IntPtr.Zero; Native.CloseHandle(stdinRead); stdinRead = IntPtr.Zero;
            stage = "resume_exact_original_browser_host_thread";
            resume_return = ResumeThread(process.Thread); resume_native_error = resume_return == uint.MaxValue ? Marshal.GetLastWin32Error() : 0;
            if (resume_return != 1) throw new InvalidDataException("SINGLE_ORIGINAL_SUSPEND_EXPECTED");
            resumed = true; stage = "running";
        }
        catch (Exception error) { Fail(error); if (!attempted) no_child_created_proven = true; if (captureTask is null) { stdout_capture_unavailable = true; captureFinished = true; } throw; }
        finally
        {
            if (process.Process != IntPtr.Zero) { audit.AdoptRoot(process.Process, process.ProcessId); process.Process = IntPtr.Zero; }
            if (process.Thread != IntPtr.Zero)
            {
                while (!resumed && WaitForSingleObjectThread(process.Thread, 0) != 0)
                {
                    uint value = ResumeThread(process.Thread); failure_resume_attempts++;
                    if (value is 0 or 1) resumed = true;
                    else { pending_live_group = true; save(); Thread.Sleep(100); }
                }
                originalThread = process.Thread; process.Thread = IntPtr.Zero;
            }
            foreach (IntPtr handle in new[] { read, write, stdinRead, stdinWrite, hostWait }) if (handle != IntPtr.Zero) Native.CloseHandle(handle);
            if (initialized) Native.DeleteProcThreadAttributeList(list);
            foreach (IntPtr memory in new[] { list, handles, caps, env, desktop, jobs }) if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
        }
    }
    private async Task Capture()
    {
        try
        {
            while (true)
            {
                SessionPacket? packet = await SessionProtocol.ReadAsync(output!, nonce, 1, CancellationToken.None);
                if (packet is not null) await packets.Writer.WriteAsync(packet);
            }
        }
        catch (EndOfStreamException) { stdout_eof = true; }
        catch (Exception error)
        {
            stdout_capture_unavailable = true; lock (failures) Fail(error);
            try { await output!.CopyToAsync(Stream.Null); stdout_eof = true; } catch { }
        }
        finally { captureFinished = true; packets.Writer.TryComplete(); }
    }
    internal void Tick()
    {
        if (retired) return;
        audit.Snapshot(); job.Observe(audit); audit.RefreshExits();
        if (job.query_failure_seen || job.membership_failure_seen) AddFailure("EXACT_JOB_HISTORICAL_FAILURE");
        if (audit.ObservationGap) AddFailure("OS_PROCESS_AUDIT_GAP");
    }
    internal void RequireHealthy()
    {
        Tick();
        if (failures.Count != 0 || audit.ObservationGap || job.query_failure_seen || job.membership_failure_seen) throw new InvalidDataException("BROWSER_GROUP_HISTORICAL_FAILURE");
        if (audit.Held.Any(p => !audit.BoundRestricted(p))) throw new InvalidDataException("ALL_OBSERVED_TOKENS_MUST_MATCH_ZERO_CAP_PACKAGE");
    }
    internal async Task<SessionPacket> Receive(string expectedKind, long? request = null)
    {
        var expires = DateTimeOffset.UtcNow.AddSeconds(30);
        while (true)
        {
            RequireHealthy();
            if (packets.Reader.TryRead(out SessionPacket? packet))
            {
                Inspect(packet.header);
                if (packet.header.kind != expectedKind || request is not null && packet.header.request_id != request) throw new InvalidDataException("BROWSER_REPLY_STATE_OR_REQUEST_MISMATCH");
                return packet;
            }
            if (captureFinished || DateTimeOffset.UtcNow >= expires) throw new InvalidDataException("BROWSER_REPLY_MISSING_NOT_TIMEOUT_SUCCESS");
            await Task.Delay(25);
        }
    }
    internal async Task Send(SessionHeader header)
    {
        RequireHealthy(); station.VerifyRuntime();
        if (header.kind != "input" || header.request_id <= lastRequest) throw new InvalidDataException("BROWSER_REQUEST_MONOTONIC");
        lastRequest = header.request_id;
        if (header.command == "close") close_requested = true;
        await SessionProtocol.WriteAsync(input!, new(header, []), CancellationToken.None);
    }
    internal void BindBrowserBeforeLoad(uint pid)
    {
        Tick(); HeldProcess? process = audit.Observe(pid, "browser", "controller_browser_pid_plus_independent_OS_handle");
        browser_pid = pid;
        browser_token_verified_before_load = audit.BoundRestricted(process) && process is not null && !process.original_handle_signaled && job.Contains(process.Handle, pid, "actual_browser_before_owned_page");
        if (!browser_token_verified_before_load) throw new InvalidDataException("ACTUAL_BROWSER_TOKEN_AND_EXACT_JOB_REQUIRED");
        RequireHealthy();
    }
    private void Inspect(SessionHeader header)
    {
        if (host_complete_received) { AddFailure("PACKET_AFTER_TERMINAL_HOST_COMPLETE"); return; }
        if (header.kind == "host_complete")
        {
            complete_header = header; host_complete_received = true;
            normal_browser_exit = header.browser_pid == browser_pid && header.normal_browser_exit == true;
            final_sdk_empty = header.final_environment_process_count == 0;
        }
    }
    internal async Task CloseAndRetire(Action save)
    {
        if (retired) return;
        if (!close_requested && input is not null)
        {
            close_requested = true;
            try { await SessionProtocol.WriteAsync(input, new(new SessionHeader(1, nonce, 1, "input") { request_id = ++lastRequest, command = "close" }, []), CancellationToken.None); }
            catch (Exception error) { Fail(error); }
        }
        DateTimeOffset budget = DateTimeOffset.UtcNow.AddSeconds(45);
        while (true)
        {
            Tick();
            while (packets.Reader.TryRead(out SessionPacket? packet))
            { if (packet.header.kind != "host_complete") AddFailure("UNEXPECTED_PACKET_WHILE_CLOSING"); Inspect(packet.header); }
            if (kernel_group_empty_proven && (captureFinished || no_child_created_proven)) break;
            if (DateTimeOffset.UtcNow >= budget) { pending_live_group = true; AddFailure("FAILED_PENDING_ORIGINAL_JOB_OR_STDOUT"); }
            save(); await Task.Delay(100);
        }
        if (captureTask is not null) { try { await captureTask; } catch (Exception error) { Fail(error); } }
        Tick();
        if (audit.Held.Any(p => !audit.BoundRestricted(p))) AddFailure("FINAL_ALL_OBSERVED_TOKEN_MISMATCH");
        cleanup.Attempt("input_transport_close", () => input?.Dispose());
        cleanup.Attempt("input_original_handle_close", () => inputHandle?.Dispose());
        cleanup.Attempt("output_transport_close", () => output?.Dispose());
        cleanup.Attempt("output_original_handle_close", () => outputHandle?.Dispose());
        if (originalThread != IntPtr.Zero) cleanup.Attempt("close_exact_original_browser_host_thread_after_exit", () =>
        {
            original_thread_signaled = WaitForSingleObjectThread(originalThread, 0) == 0;
            if (!original_thread_signaled) throw new InvalidDataException("ORIGINAL_BROWSER_HOST_THREAD_EXIT_UNPROVEN");
            Native.Check(Native.CloseHandle(originalThread), "close_exact_original_browser_host_thread"); originalThread = IntPtr.Zero;
        });
        foreach (HeldProcess process in audit.Held.Where(p => p.original_handle_signaled && p.Handle != IntPtr.Zero))
            cleanup.Attempt("close_exact_original_observed_process_handle", () =>
            { Native.Check(Native.CloseHandle(process.Handle), "close_exact_original_observed_process_handle"); process.Handle = IntPtr.Zero; });
        retired = originalThread == IntPtr.Zero && (inputHandle is null || inputHandle.IsClosed) && (outputHandle is null || outputHandle.IsClosed) && audit.Held.All(p => p.Handle == IntPtr.Zero);
        pending_live_group = !retired;
    }
    private void Fail(Exception error) => AddFailure(Program.FailureCode(error));
    private void AddFailure(string failure) { if (failures.Count < 128 && !failures.Contains(failure)) failures.Enqueue(failure); }
    private static string Quote(string value)
    { if (value.Contains('"') || value.Contains('\0') || value.EndsWith('\\')) throw new InvalidDataException("PLAIN_QUOTED_ARGUMENT"); return '"' + value + '"'; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)] private static extern uint WaitForSingleObjectThread(IntPtr thread, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}
