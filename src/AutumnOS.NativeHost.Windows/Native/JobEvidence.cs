using System.Runtime.InteropServices;

namespace AutumnOS.NativeHost.Windows;

// One new unnamed resource-limited Job per owned child. No breakaway, named
// object adoption, arbitrary PID assignment or Shell/broker assignment exists.
internal sealed class JobEvidence
{
    internal const nuint JobListAttribute = 0x0002000D;
    private IntPtr handle;
    internal IntPtr Handle => handle;
    public bool created { get; private set; }
    public bool atomic_launch_attribute_attached { get; private set; }
    public bool original_handle_closed_after_empty { get; private set; }
    public uint? limit_flags { get; private set; }
    public uint? total_processes { get; private set; }
    public uint? active_processes { get; private set; }
    public uint[] last_process_ids { get; private set; } = [];
    public bool last_query_succeeded { get; private set; }
    public bool query_failure_seen { get; private set; }
    public bool membership_failure_seen { get; private set; }
    public List<object> membership_evidence { get; } = [];
    public List<string> failures { get; } = [];
    public DateTimeOffset? last_query_utc { get; private set; }
    public string limits_policy => "new_unnamed_2GiB_job_1GiB_process_32_processes_cpu25_kill_on_close_no_breakaway";
    private readonly OwnedJobRecovery recovery = new();
    public bool termination_requested => recovery.Requested;
    public bool Empty => created && last_query_succeeded && active_processes == 0 && last_process_ids.Length == 0;

    internal void Create()
    {
        if (handle != IntPtr.Zero || created) throw new InvalidOperationException("FRESH_UNNAMED_JOB_ONLY");
        handle = CreateJobObjectW(IntPtr.Zero, null);
        Native.Check(handle != IntPtr.Zero, "create_one_unnamed_accounting_job"); created = true;
        OwnedJobPolicy.ApplyToFreshJob(handle);
        ReadLimits();
    }
    internal void MarkAtomicLaunchAttached() => atomic_launch_attribute_attached = true;
    private void ReadLimits()
    {
        OwnedJobPolicy.RequireKernelPolicy(handle);
        // Public x64 JOBOBJECT_EXTENDED_LIMIT_INFORMATION: basic limits64,
        // IO_COUNTERS48 and four SIZE_T fields32 = 144; LimitFlags at16.
        IntPtr buffer = Marshal.AllocHGlobal(144);
        try
        {
            Native.Check(QueryInformationJobObject(handle, 9, buffer, 144, out _), "query_new_job_default_extended_limits");
            limit_flags = unchecked((uint)Marshal.ReadInt32(buffer, 16));
            if (limit_flags != OwnedJobPolicy.LimitFlags) throw new InvalidDataException("OWNED_JOB_RESOURCE_FLAGS_MISMATCH");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal bool RequestRecovery(TimeSpan elapsed) => recovery.TryRequest(elapsed, true, Empty, () => OwnedJobPolicy.RequestTermination(handle));
    internal bool Contains(IntPtr originalProcess, uint pid, string stage)
    {
        try
        {
            Native.Check(IsProcessInJob(originalProcess, handle, out bool result), "exact_original_process_IsProcessInJob");
            if (membership_evidence.Count < 256) membership_evidence.Add(new { pid, stage, belongs_to_exact_new_job = result, queried_original_process_handle = true });
            if (!result) membership_failure_seen = true;
            return result;
        }
        catch (Exception ex) { membership_failure_seen = true; AddFailure(ex); return false; }
    }
    internal void Observe(ProcessAudit audit)
    {
        if (!created || original_handle_closed_after_empty) return;
        last_query_succeeded = false;
        try
        {
            ReadLimits();
            IntPtr accounting = Marshal.AllocHGlobal(48), list = Marshal.AllocHGlobal(8 + 4096 * IntPtr.Size);
            try
            {
                Native.Check(QueryInformationJobObject(handle, 1, accounting, 48, out _), "query_job_basic_accounting_ActiveProcesses");
                total_processes = unchecked((uint)Marshal.ReadInt32(accounting, 36));
                active_processes = unchecked((uint)Marshal.ReadInt32(accounting, 40));
                Native.Check(QueryInformationJobObject(handle, 3, list, (uint)(8 + 4096 * IntPtr.Size), out _), "query_exact_job_process_id_list");
                uint assigned = unchecked((uint)Marshal.ReadInt32(list)), returned = unchecked((uint)Marshal.ReadInt32(list, 4));
                if (assigned != returned || returned > 4096) throw new InvalidDataException("JOB_PID_LIST_INCOMPLETE_NO_TRUNCATION_FALLBACK");
                uint[] ids = new uint[returned];
                for (int index = 0; index < ids.Length; index++)
                {
                    ulong value = unchecked((ulong)Marshal.ReadIntPtr(list, 8 + index * IntPtr.Size).ToInt64());
                    if (value == 0 || value > uint.MaxValue) throw new InvalidDataException("JOB_PID_INVALID");
                    ids[index] = (uint)value;
                }
                last_process_ids = ids;
                foreach (uint pid in ids)
                {
                    HeldProcess? held = audit.Observe(pid, "unknown", "exact_new_job_PID_list_plus_OS_original_handle");
                    if (held is not null && !held.original_handle_signaled) Contains(held.Handle, pid, "job_PID_list_observer");
                }
                audit.RefreshExits(); last_query_succeeded = true; last_query_utc = DateTimeOffset.UtcNow;
            }
            finally { Marshal.FreeHGlobal(accounting); Marshal.FreeHGlobal(list); }
        }
        catch (Exception ex) { query_failure_seen = true; AddFailure(ex); }
    }
    private void AddFailure(Exception ex)
    {
        if (failures.Count < 64) failures.Add(ex.GetType().Name + ":0x" + unchecked((uint)ex.HResult).ToString("x8") +
            (ex is OwnedNativeException native ? ":stage=" + native.FixedStage + ":win32=" + native.NativeErrorCode : ""));
    }
    internal void CloseAfterEmpty()
    {
        if (!Empty) throw new InvalidOperationException("JOB_ORIGINAL_ACTIVE_ZERO_PROOF_REQUIRED_NO_CLOSE");
        if (handle != IntPtr.Zero) Native.Check(Native.CloseHandle(handle), "close_only_empty_new_job_original_handle");
        handle = IntPtr.Zero; original_handle_closed_after_empty = true;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(IntPtr job, int kind, IntPtr value, uint bytes, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool result);
}
