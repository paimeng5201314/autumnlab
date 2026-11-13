using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.NativeHost.Windows;

// A QUERY|TERMINATE duplicate of the exact original host-owned Job survives
// host death. It never assigns processes, changes limits or reopens a PID.
internal sealed class SessionJobWitness
{
    private IntPtr handle;
    internal IntPtr Handle => handle;
    public bool original_job_duplicate_bound { get; private set; }
    public uint granted_access_requested => OwnedJobPolicy.QueryAndTerminateAccess;
    private readonly OwnedJobRecovery recovery = new();
    private bool recoveryPermitted;
    public bool termination_requested => recovery.Requested;
    public uint? limit_flags { get; private set; }
    public uint? active_processes { get; private set; }
    public uint[] process_ids { get; private set; } = [];
    public bool last_query_succeeded { get; private set; }
    public bool historical_query_failure { get; private set; }
    public bool closed_after_empty { get; private set; }
    public ProcessAudit audit { get; }
    public List<string> failures { get; } = [];
    public bool empty => original_job_duplicate_bound && last_query_succeeded && active_processes == 0 && process_ids.Length == 0;
    public bool groups_retired => empty && audit.AllSignaled;
    public bool fresh_admission_passed => empty && limit_flags == OwnedJobPolicy.LimitFlags && !historical_query_failure;
    internal SessionJobWitness(SafeProcessHandle originalHost, ulong originalHostJob, string hostTree, string runtimeTree, string sid)
    {
        audit = new ProcessAudit(hostTree, runtimeTree, sid);
        if (originalHostJob == 0 || SessionNative.Exited(originalHost)) throw new InvalidDataException("LIVE_ORIGINAL_HOST_JOB_HANDLE_REQUIRED");
        Native.Check(DuplicateHandle(originalHost.DangerousGetHandle(), new IntPtr(unchecked((long)originalHostJob)), Native.GetCurrentProcess(), out handle, OwnedJobPolicy.QueryAndTerminateAccess, false, 0), "duplicate_exact_original_job_QUERY_TERMINATE");
        original_job_duplicate_bound = true;
        Tick();
        recoveryPermitted = fresh_admission_passed;
    }
    internal void Tick()
    {
        if (handle == IntPtr.Zero || closed_after_empty) return;
        last_query_succeeded = false;
        IntPtr limits = Marshal.AllocHGlobal(144), accounting = Marshal.AllocHGlobal(48), pids = Marshal.AllocHGlobal(8 + 4096 * IntPtr.Size);
        try
        {
            OwnedJobPolicy.RequireKernelPolicy(handle);
            Native.Check(QueryInformationJobObject(handle, 9, limits, 144, out _), "witness_job_flags");
            limit_flags = unchecked((uint)Marshal.ReadInt32(limits, 16));
            if (limit_flags != OwnedJobPolicy.LimitFlags) throw new InvalidDataException("WITNESS_JOB_FLAGS_CHANGED");
            Native.Check(QueryInformationJobObject(handle, 1, accounting, 48, out _), "witness_job_accounting");
            active_processes = unchecked((uint)Marshal.ReadInt32(accounting, 40));
            Native.Check(QueryInformationJobObject(handle, 3, pids, (uint)(8 + 4096 * IntPtr.Size), out _), "witness_job_PID_list");
            uint assigned = unchecked((uint)Marshal.ReadInt32(pids)), count = unchecked((uint)Marshal.ReadInt32(pids, 4));
            if (assigned != count || count > 4096) throw new InvalidDataException("WITNESS_JOB_PID_LIST_INCOMPLETE");
            var ids = new uint[count];
            for (int index = 0; index < ids.Length; index++)
            {
                ulong value = unchecked((ulong)Marshal.ReadIntPtr(pids, 8 + index * IntPtr.Size).ToInt64());
                if (value is 0 or > uint.MaxValue) throw new InvalidDataException("WITNESS_JOB_PID_INVALID");
                ids[index] = (uint)value;
                HeldProcess? process = audit.Observe(ids[index], "unknown", "ordinary_CoreParent_exact_QUERY_only_job_witness");
                if (process is not null && !process.original_handle_signaled)
                {
                    Native.Check(IsProcessInJob(process.Handle, handle, out bool belongs), "witness_exact_process_job_membership");
                    if (!belongs) throw new InvalidDataException("WITNESS_EXACT_PROCESS_NOT_IN_JOB");
                }
            }
            process_ids = ids; audit.RefreshExits(); last_query_succeeded = true;
        }
        catch (Exception error) { historical_query_failure = true; if (failures.Count < 64) failures.Add(NativeFailure.Describe(error)); }
        finally { Marshal.FreeHGlobal(limits); Marshal.FreeHGlobal(accounting); Marshal.FreeHGlobal(pids); }
    }
    internal bool RequestRecovery(TimeSpan elapsed, bool shutdownRequired)
    {
        if (!recoveryPermitted || handle == IntPtr.Zero || closed_after_empty) return false;
        return recovery.TryRequest(elapsed, shutdownRequired, empty, () => OwnedJobPolicy.RequestTermination(handle));
    }
    internal void CloseAfterEmpty()
    {
        Tick();
        if (!groups_retired) throw new InvalidDataException("EXACT_WITNESS_GROUP_NOT_RETIRED");
        foreach (HeldProcess process in audit.Held.Where(p => p.original_handle_signaled && p.Handle != IntPtr.Zero))
        { Native.Check(Native.CloseHandle(process.Handle), "close_witness_original_observed_process_handle"); process.Handle = IntPtr.Zero; }
        Native.Check(Native.CloseHandle(handle), "close_QUERY_only_original_job_after_empty");
        handle = IntPtr.Zero; closed_after_empty = true;
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(IntPtr job, int kind, IntPtr data, uint bytes, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
