using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.NetOnlyStationProbe;

internal static class Program
{
    internal static bool Nonce(string text) => text.Length == 32 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) return 0; // Compiled-only entry: no P/Invoke, pipe, file or process action.
        if (args.Length == 2 && args[0] == "--self-test") return ProtocolSelfTests.Run(args[1]);
        if (args.Length == 4 && args[0] == "--child" && uint.TryParse(args[3], out uint parentPid))
        { try { return await StationChild.Run(args[1], args[2], parentPid); } catch { return 22; } }
        if (args.Length != 2 || args[0] != "--execute") return 2;
        try { using PreparedInputs input = ManifestInputs.Load(args[1]); return await Execute(input); }
        catch { return 3; } // Manifest failures do not create a case or log file paths.
    }

    private static async Task<int> Execute(PreparedInputs input)
    {
        string nonce = Guid.NewGuid().ToString("N"), directory = Path.Combine(input.case_root, "netonly-case-" + nonce);
        if (Directory.Exists(directory)) return 4;
        Directory.CreateDirectory(directory);
        var report = new Report { nonce = nonce, manifest_sha256 = input.manifest_sha256, source_snapshot_id = input.source_snapshot_id };
        string reportPath = Path.Combine(directory, "report.json");
        using (new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { }
        void Fail(string stage, Exception? ex = null)
        {
            if (report.failure_stage is null)
            {
                report.failure_stage = stage;
                report.failure = ex is null ? "ASSERTION_FAILED" : ex.GetType().Name;
                report.failure_hresult = ex?.HResult;
                report.failure_native_error = ex is Win32Exception win ? win.NativeErrorCode : null;
            }
            report.status = "failed";
        }
        void Save()
        {
            try
            {
                report.updated_utc = DateTimeOffset.UtcNow;
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
                string temp = Path.Combine(directory, "report.pending.json");
                using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.Read)) { file.Write(bytes); file.Flush(true); }
                File.Move(temp, reportPath, true);
            }
            catch (Exception ex)
            {
                report.report_error ??= new("report_write", ex.GetType().Name, ex.HResult, ex is Win32Exception win ? win.NativeErrorCode : null);
                Fail("report_write", ex);
            }
        }
        void RequireReport() { if (report.report_error is not null) throw new InvalidOperationException("REPORT_WRITE_UNPROVEN"); }
        Save();
        SafeProcessHandle? child = null; SafeWaitHandle? thread = null;
        bool resumeSucceeded = false, createCalled = false, createReturned = false;
        NamedPipeServerStream? pipe = null;
        Task? exitObserver = null;
        string stage = "ordinary_parent_identity";
        try
        {
            RequireReport();
            using var current = Native.Current();
            report.parent_identity = Native.Identity(current);
            if (report.parent_identity.elevated || report.parent_identity.appcontainer || report.parent_identity.integrity_rid != 8192)
                throw new InvalidOperationException("ORDINARY_MEDIUM_PARENT_REQUIRED");
            report.parent_station_before = Native.StationName(Native.GetProcessWindowStation());
            if (report.parent_station_before != "WinSta0") throw new InvalidOperationException("PARENT_WINSTA0_REQUIRED");
            stage = "create_current_user_only_pipe";
            string pipeName = "AutumnOS.NetOnlyStation." + nonce;
            pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly, 4096, 4096);
            stage = "create_process_with_logon_once";
            Native.ProcessInfo process = default;
            IntPtr password = IntPtr.Zero, environment = IntPtr.Zero;
            byte[] random = RandomNumberGenerator.GetBytes(32);
            char[] secret = new char[random.Length * 2];
            const string digits = "0123456789ABCDEF";
            for (int index = 0; index < random.Length; index++)
            { secret[index * 2] = digits[random[index] >> 4]; secret[index * 2 + 1] = digits[random[index] & 15]; }
            CryptographicOperations.ZeroMemory(random);
            try
            {
                password = Marshal.AllocHGlobal((secret.Length + 1) * 2);
                Marshal.Copy(secret, 0, password, secret.Length); Marshal.WriteInt16(password, secret.Length * 2, 0);
                string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                if (!Path.IsPathFullyQualified(windows) || windows.Contains('\0')) throw new InvalidOperationException("SYSTEM_ROOT_REQUIRED");
                // No inherited profiler/startup hook or caller search PATH. No profile load.
                string block = "SystemRoot=" + windows + "\0WINDIR=" + windows + "\0PATH=" + Path.Combine(windows, "System32") + "\0\0";
                environment = Marshal.StringToHGlobalUni(block);
                string executable = Path.Combine(input.payload_directory, input.executable);
                var command = new StringBuilder('"' + executable + "\" --child " + nonce + " " + pipeName + " " + Environment.ProcessId);
                if (command.Length >= 1024) throw new InvalidOperationException("CPWL_COMMAND_TOO_LONG");
                var startup = new Native.StartupInfo { cb = Marshal.SizeOf<Native.StartupInfo>(), desktop = "WinSta0\\Default" };
                string syntheticUsername = "AOProbe_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(6));
                Native.ClearLastError(0); Marshal.SetLastPInvokeError(0);
                report.create_process_with_logon_calls++; createCalled = true;
                createReturned = Native.CreateProcessWithLogonW(syntheticUsername, ".", password, 2, executable, command,
                    0x08000404 /* NO_WINDOW | UNICODE_ENVIRONMENT | SUSPENDED */, environment, input.payload_directory, ref startup, out process);
                int nativeError = createReturned ? 0 : Marshal.GetLastWin32Error();
                report.create_process_result = createReturned; report.create_process_native_error = nativeError;
                report.original_child_pid = process.processId;
                if (process.process != IntPtr.Zero) child = new SafeProcessHandle(process.process, true);
                if (process.thread != IntPtr.Zero) thread = new SafeWaitHandle(process.thread, true);
                report.create_failure_zero_identity_proven = !createReturned && process.process == IntPtr.Zero && process.thread == IntPtr.Zero && process.processId == 0 && process.threadId == 0;
                if (!createReturned) throw new Win32Exception(nativeError, stage);
                if (child is null || child.IsInvalid || thread is null || thread.IsInvalid || process.processId == 0 || process.threadId == 0)
                    throw new InvalidOperationException("ORIGINAL_PROCESS_IDENTITIES_INCOMPLETE");
            }
            finally
            {
                Array.Clear(secret);
                if (password != IntPtr.Zero) { for (int i = 0; i <= secret.Length; i++) Marshal.WriteInt16(password, i * 2, 0); Marshal.FreeHGlobal(password); }
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
            }
            stage = "suspended_original_token_admission";
            if (Native.GetProcessId(child!) != report.original_child_pid || Native.Exited(child!)) throw new InvalidOperationException("ORIGINAL_CHILD_HANDLE_MISMATCH");
            report.child_identity = Native.Identity(child!);
            report.token_admission_passed = Native.TokenAdmission(report.parent_identity, report.child_identity);
            report.original_child_image_matches = string.Equals(Native.Image(child!), Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
            if (!report.token_admission_passed || !report.original_child_image_matches) throw new InvalidOperationException("OS_TOKEN_ADMISSION_FAILED");
            Save(); RequireReport();
            stage = "resume_original_thread";
            uint resume = Native.ResumeThread(thread!); report.resume_return = resume;
            report.resume_native_error = resume == uint.MaxValue ? Marshal.GetLastWin32Error() : 0;
            if (resume != 1) throw new InvalidOperationException("EXACT_SINGLE_SUSPEND_REQUIRED");
            resumeSucceeded = true;
            exitObserver = ObserveExit(child!);
            using var protocolBudget = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            async Task<T> Race<T>(Task<T> operation)
            {
                Task winner = await Task.WhenAny(operation, exitObserver, Task.Delay(Timeout.Infinite, protocolBudget.Token));
                if (winner != operation) throw new InvalidOperationException(winner == exitObserver ? "ORIGINAL_CHILD_EXITED_BEFORE_PROTOCOL" : "PROTOCOL_BUDGET_EXPIRED");
                return await operation;
            }
            stage = "pipe_connect";
            await Race(Connect(pipe, protocolBudget.Token));
            stage = "pipe_original_kernel_peer";
            if (!Native.GetNamedPipeClientProcessId(pipe.SafePipeHandle, out uint peer) || peer != report.original_child_pid || Native.Exited(child!))
                throw new InvalidOperationException("PIPE_CLIENT_ORIGINAL_PID_MISMATCH");
            report.pipe_peer_pid_verified = true;
            stage = "protocol_hello";
            await Race(Protocol.ReadAsync(pipe, nonce, "hello", protocolBudget.Token));
            RequireReport();
            await Protocol.WriteAsync(pipe, new(1, nonce, "ready"), protocolBudget.Token);
            stage = "protocol_create";
            report.create_command_sent = true; Save(); RequireReport();
            await Protocol.WriteAsync(pipe, new(1, nonce, "create"), protocolBudget.Token);
            stage = "protocol_result";
            report.child_result = await Race(Protocol.ReadAsync(pipe, nonce, "result", protocolBudget.Token));
            Save();
            stage = "protocol_ack";
            await Protocol.WriteAsync(pipe, new(1, nonce, "ack"), protocolBudget.Token);
            if (!ChildResultPass(report.child_result)) throw new InvalidOperationException("CHILD_STATION_FACTS_FAILED");
        }
        catch (Exception ex) { Fail(stage, ex); }
        finally
        {
            // Every resource is independent; cleanup failure cannot skip original exit observation.
            try { pipe?.Dispose(); } catch (Exception ex) { report.cleanup_errors.Add(new("pipe_dispose", ex.GetType().Name, ex.HResult, null)); Fail("pipe_dispose", ex); }
            if (thread is not null && !thread.IsInvalid && !resumeSucceeded)
            {
                while (!resumeSucceeded)
                {
                    try
                    {
                        if (Native.WaitOriginalThread(thread) == 0) { report.original_thread_exit_proven = true; break; }
                        uint resumed = Native.ResumeThread(thread); report.failure_resume_return = resumed;
                        report.failure_resume_attempts++;
                        report.failure_resume_native_error = resumed == uint.MaxValue ? Marshal.GetLastWin32Error() : 0;
                        if (resumed is 0 or 1) resumeSucceeded = true;
                        else Fail("failure_resume_original_thread");
                    }
                    catch (Exception ex) { Fail("failure_resume_original_thread", ex); }
                    if (!resumeSucceeded)
                    {
                        report.status = "failed_pending_original_child"; Save(); await Task.Delay(100);
                    }
                }
            }
            if (child is not null && !child.IsInvalid)
            {
                exitObserver ??= ObserveExit(child);
                DateTimeOffset pendingAt = DateTimeOffset.UtcNow.AddSeconds(45);
                while (!exitObserver.IsCompleted)
                {
                    if (DateTimeOffset.UtcNow >= pendingAt) { report.status = "failed_pending_original_child"; Fail("original_child_still_live"); report.status = "failed_pending_original_child"; }
                    Save(); await Task.WhenAny(exitObserver, Task.Delay(250));
                }
                try { await exitObserver; report.original_child_exit_proven = true; report.original_child_exit_code = Native.ExitCode(child); }
                catch (Exception ex) { Fail("original_child_exit_observation", ex); }
            }
            try
            {
                if (thread is not null && !thread.IsInvalid)
                {
                    report.original_thread_exit_proven = Native.WaitOriginalThread(thread) == 0;
                    if (!report.original_thread_exit_proven) throw new InvalidOperationException("ORIGINAL_THREAD_EXIT_UNPROVEN");
                    thread.Dispose();
                }
            }
            catch (Exception ex) { report.cleanup_errors.Add(new("original_thread_close", ex.GetType().Name, ex.HResult, ex is Win32Exception win ? win.NativeErrorCode : null)); Fail("original_thread_close", ex); }
            try { if (report.original_child_exit_proven) child?.Dispose(); }
            catch (Exception ex) { report.cleanup_errors.Add(new("original_process_close", ex.GetType().Name, ex.HResult, null)); Fail("original_process_close", ex); }
            try
            {
                if (report.child_result?.station_name is string name && report.original_child_exit_proven)
                {
                    Native.ClearLastError(0); Marshal.SetLastPInvokeError(0);
                    IntPtr probe = Native.OpenWindowStationW(name, false, 2 /* READATTRIBUTES only */);
                    int error = probe == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
                    bool closed = true;
                    if (probe != IntPtr.Zero) closed = Native.CloseWindowStation(probe);
                    report.station_postcheck = new(name, Native.Hash(name), probe != IntPtr.Zero, error, probe == IntPtr.Zero && error is 2 or 3, closed);
                    if (!report.station_postcheck.not_found_proven) Fail("owned_station_absence_unproven");
                }
                report.parent_station_after = Native.StationName(Native.GetProcessWindowStation());
                report.parent_station_unchanged = report.parent_station_before == report.parent_station_after;
                if (!report.parent_station_unchanged) Fail("parent_station_changed");
            }
            catch (Exception ex) { Fail("station_postcheck", ex); }
            bool noCreatedStation = report.child_result is { create_attempted: true, creation_succeeded: false, cleanup_confirmed: true };
            bool originalFinished = report.original_child_exit_proven || report.create_failure_zero_identity_proven || !createCalled;
            report.cleanup_proven = originalFinished && report.parent_station_unchanged && report.cleanup_errors.Count == 0 &&
                (!createCalled || report.create_failure_zero_identity_proven || noCreatedStation ||
                 report.child_result is { cleanup_confirmed: true, original_handle_closed: true } && report.station_postcheck?.not_found_proven == true);
            if (!report.cleanup_proven) Fail("cleanup_unproven");
            if (report.failure_stage is null && report.report_error is null && report.original_child_exit_code == 0 &&
                report.token_admission_passed && report.pipe_peer_pid_verified && report.cleanup_proven && ChildResultPass(report.child_result))
                report.status = "completed_netonly_station_metadata_only";
            else report.status = "failed";
            Save();
        }
        return report.status == "completed_netonly_station_metadata_only" && report.report_error is null ? 0 : 1;

        async Task ObserveExit(SafeProcessHandle handle)
        {
            while (true)
            {
                try { if (Native.Exited(handle)) return; }
                catch (Exception ex) { Fail("original_process_wait", ex); }
                await Task.Delay(100);
            }
        }
    }
    private static async Task<bool> Connect(NamedPipeServerStream pipe, CancellationToken token) { await pipe.WaitForConnectionAsync(token); return true; }
    internal static bool ChildResultPass(NetMessage? result) => result is { kind: "result", failure_stage: "none", native_error: 0,
        cleanup_confirmed: true, original_station_unchanged: true, create_attempted: true, creation_succeeded: true,
        exact_name_verified: true, noninteractive_verified: true, original_handle_closed: true, close_native_error: 0 } &&
        result.station_name is not null && result.descriptor_sha256 is not null;
}

internal sealed record ErrorFact(string stage, string exception_type, int hresult, int? native_error);
internal sealed record StationPostcheck(string name, string name_sha256, bool handle_returned, int native_error, bool not_found_proven, bool probe_handle_closed);
internal sealed class Report
{
    public int schema_version => 1;
    public string producer => "派蒙";
    public DateTimeOffset started_utc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset updated_utc { get; set; }
    public string status { get; set; } = "running";
    public string nonce { get; set; } = "";
    public string manifest_sha256 { get; set; } = "";
    public string source_snapshot_id { get; set; } = "";
    public string? failure_stage { get; set; }
    public string? failure { get; set; }
    public int? failure_hresult { get; set; }
    public int? failure_native_error { get; set; }
    public ErrorFact? report_error { get; set; }
    public List<ErrorFact> cleanup_errors { get; } = [];
    public int create_process_with_logon_calls { get; set; }
    public uint logon_flags => 2;
    public string credentials => "synthetic_generated_not_logged_not_validated";
    public bool create_process_result { get; set; }
    public int? create_process_native_error { get; set; }
    public bool create_failure_zero_identity_proven { get; set; }
    public uint original_child_pid { get; set; }
    public TokenFacts? parent_identity { get; set; }
    public TokenFacts? child_identity { get; set; }
    public bool token_admission_passed { get; set; }
    public bool original_child_image_matches { get; set; }
    public uint? resume_return { get; set; }
    public int? resume_native_error { get; set; }
    public uint? failure_resume_return { get; set; }
    public int? failure_resume_native_error { get; set; }
    public int failure_resume_attempts { get; set; }
    public bool original_thread_exit_proven { get; set; }
    public bool pipe_peer_pid_verified { get; set; }
    public bool create_command_sent { get; set; }
    public NetMessage? child_result { get; set; }
    public bool original_child_exit_proven { get; set; }
    public uint? original_child_exit_code { get; set; }
    public string? parent_station_before { get; set; }
    public string? parent_station_after { get; set; }
    public bool parent_station_unchanged { get; set; }
    public StationPostcheck? station_postcheck { get; set; }
    public bool cleanup_proven { get; set; }
    public string cleanup_scope => "original_child_and_its_new_window_station_only";
    public string lsa_cleanup => "not_claimed";
    public string logon_scope => "new_lsa_netonly_context_not_credential_authentication";
    public string isolation_acceptance => "not_claimed_metadata_only";
    public string existing_object_ACL_changes => "none";
    public int profiles_created => 0;
    public int desktops_created => 0;
    public int clipboard_calls => 0;
    public int browser_starts => 0;
    public int window_creation_calls => 0;
    public int application_network_calls => 0;
    public string network_provider_side_effects => "possible_os_logon_notification_not_measured";
    public int UAC_requests => 0;
    public int policy_or_service_changes => 0;
    public int force_termination_calls => 0;
}
