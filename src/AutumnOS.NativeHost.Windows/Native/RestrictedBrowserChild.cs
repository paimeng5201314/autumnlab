using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.NativeHost.Windows;

internal sealed class RestrictedBrowserChild(NativeHostLaunchManifest manifest, OwnedStation station, JobEvidence job, string udf, string temp)
{
    private string hostTree => manifest.HostDirectory;
    private string executable => Path.Combine(hostTree, manifest.HostExecutable);
    private string nonce => manifest.Nonce;
    private readonly ProcessAudit audit = new(manifest.HostDirectory, manifest.RuntimeDirectory, station.package_sid);
    private IntPtr originalThread;
    private FileStream? input, output;
    private SafeFileHandle? inputHandle, outputHandle;
    private bool no_child_created_proven = true;
    internal uint RootPid => audit.RootPid;
    internal Stream Input => input ?? throw new InvalidOperationException("CHILD_INPUT_UNAVAILABLE");
    internal Stream Output => output ?? throw new InvalidOperationException("CHILD_OUTPUT_UNAVAILABLE");
    internal void Start()
    {
        IntPtr read = IntPtr.Zero, write = IntPtr.Zero, stdinRead = IntPtr.Zero, stdinWrite = IntPtr.Zero, hostWait = IntPtr.Zero;
        IntPtr list = IntPtr.Zero, handles = IntPtr.Zero, caps = IntPtr.Zero, env = IntPtr.Zero, desktop = IntPtr.Zero, jobs = IntPtr.Zero;
        Native.ProcessInformation process = default;
        bool initialized = false, attempted = false;
        string stage;
        try
        {
            station.VerifyRuntime();
            if (!job.created || job.limit_flags != OwnedJobPolicy.LimitFlags || !job.Empty) throw new InvalidDataException("PREBOUND_FRESH_RESOURCE_LIMITED_JOB_REQUIRED");
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
            string command = NetOnlyLaunch.Quote(executable) + " --browser-host " + nonce + " " + manifest.ManifestSha256 + " " +
                NetOnlyLaunch.Quote(udf) + " " + NetOnlyLaunch.Quote(station.station_name) + " " + NetOnlyLaunch.Quote(station.desktop_name) +
                " " + Environment.ProcessId + " " + created.Value + " " + hostWait.ToInt64();
            stage = "CreateProcess_suspended_exact_private_desktop"; attempted = true; no_child_created_proven = false;
            bool ok = Native.CreateProcessW(executable, new StringBuilder(command), IntPtr.Zero, IntPtr.Zero, true,
                0x08080404 /* NO_WINDOW | EXTENDED_STARTUPINFO | UNICODE | SUSPENDED */, env, hostTree, ref startup, out process);
            int error = ok ? 0 : Marshal.GetLastWin32Error();
            no_child_created_proven = !ok && process.Process == IntPtr.Zero && process.Thread == IntPtr.Zero && process.ProcessId == 0 && process.ThreadId == 0;
            if (process.Process != IntPtr.Zero) { audit.AdoptRoot(process.Process, process.ProcessId); process.Process = IntPtr.Zero; }
            if (!ok) throw new OwnedNativeException(error, stage);
            bool root_token_verified_before_resume = audit.Held.Count == 1 && audit.BoundRestricted(audit.Held[0]) && !audit.ObservationGap;
            bool original_root_job_membership = job.Contains(audit.Held[0].Handle, process.ProcessId, "suspended_original_browser_host");
            if (!root_token_verified_before_resume || !original_root_job_membership) throw new InvalidDataException("SUSPENDED_OS_TOKEN_OR_JOB_ADMISSION_FAILED");
            inputHandle = new SafeFileHandle(stdinWrite, true); stdinWrite = IntPtr.Zero; input = new FileStream(inputHandle, FileAccess.Write);
            outputHandle = new SafeFileHandle(read, true); read = IntPtr.Zero; output = new FileStream(outputHandle, FileAccess.Read);

            Native.CloseHandle(write); write = IntPtr.Zero; Native.CloseHandle(stdinRead); stdinRead = IntPtr.Zero;
            stage = "resume_exact_original_browser_host_thread";
            uint resume_return = ResumeThread(process.Thread);
            if (resume_return == uint.MaxValue) throw new OwnedNativeException(Marshal.GetLastWin32Error(), stage);
            if (resume_return != 1) throw new InvalidDataException("SINGLE_ORIGINAL_SUSPEND_EXPECTED");

        }
        catch { if (!attempted) no_child_created_proven = true; throw; }
        finally
        {
            if (process.Process != IntPtr.Zero) { audit.AdoptRoot(process.Process, process.ProcessId); process.Process = IntPtr.Zero; }
            if (process.Thread != IntPtr.Zero)
            {
                originalThread = process.Thread; process.Thread = IntPtr.Zero;
            }
            foreach (IntPtr handle in new[] { read, write, stdinRead, stdinWrite, hostWait }) if (handle != IntPtr.Zero) Native.CloseHandle(handle);
            if (initialized) Native.DeleteProcThreadAttributeList(list);
            foreach (IntPtr memory in new[] { list, handles, caps, env, desktop, jobs }) if (memory != IntPtr.Zero) Marshal.FreeHGlobal(memory);
        }
    }

    internal void RequireLive(uint browserPid = 0)
    {
        job.Observe(audit);
        audit.RefreshExits();
        if (!job.last_query_succeeded || job.query_failure_seen || job.membership_failure_seen || audit.ObservationGap ||
            audit.Held.Count == 0 || audit.Held.Any(p => !audit.BoundRestricted(p) || !p.image_in_new_copied_trees) ||
            !AliveImage(RootPid, executable) || browserPid != 0 && !AliveImage(browserPid, Path.Combine(manifest.RuntimeDirectory, "msedgewebview2.exe")))
            throw new InvalidDataException("LIVE_RESTRICTED_ORIGINAL_GROUP_REQUIRED");
    }

    private bool AliveImage(uint pid, string image)
    {
        var process = audit.Held.SingleOrDefault(p => p.pid == pid && !p.original_handle_signaled);
        return process is not null && job.last_process_ids.Contains(pid) &&
            process.image_path_binding_sha256 == "sha256:" + OwnedFiles.HashText(Path.GetFullPath(image).ToUpperInvariant()) &&
            job.Contains(process.Handle, pid, "original_live_image");
    }

    internal void SignalInputEof() => Interlocked.Exchange(ref input, null)?.Dispose();

    internal async Task DrainAsync()
    {
        SignalInputEof();
        output?.Dispose();
        var grace = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            job.Observe(audit); audit.RefreshExits();
            if (job.Empty && audit.AllSignaled && (no_child_created_proven || audit.Held.Count > 0 && originalThread != IntPtr.Zero &&
                Native.WaitForSingleObject(originalThread, 0) == Native.WaitObject0)) break;
            // EOF is cooperative. An unresponsive original child group is
            // terminated through its private Job after grace, never by PID.
            // Termination success alone cannot satisfy the exit predicate above.
            job.RequestRecovery(grace.Elapsed);
            await Task.Delay(100).ConfigureAwait(false);
        }
        if (originalThread != IntPtr.Zero)
        {
            Native.Check(Native.CloseHandle(originalThread), "close_exited_original_restricted_thread");
            originalThread = IntPtr.Zero;
        }
        audit.ReleaseSignaledHandles();
        inputHandle?.Dispose(); outputHandle?.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}
