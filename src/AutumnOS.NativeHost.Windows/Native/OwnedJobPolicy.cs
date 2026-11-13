using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AutumnOS.NativeHost.Windows;

// One policy for every untrusted application group. The Shell and its ordinary
// station broker are never assigned to this Job. No breakaway flag is allowed.
internal static class OwnedJobPolicy
{
    internal const uint LimitFlags = 0x2308; // active process, process/job commit, kill on last Job handle close
    internal const uint MaximumProcesses = 32;
    internal const ulong ProcessMemoryBytes = 1024UL * 1024 * 1024;
    internal const ulong JobMemoryBytes = 2UL * 1024 * 1024 * 1024;
    internal const uint CpuFlags = 5, CpuRate = 2500; // ENABLE | HARD_CAP, hundredths of a percent
    internal const uint QueryAndTerminateAccess = 0x000c;
    internal const uint RecoveryExitCode = 0xe0a70001;
    internal static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(12);
    internal static readonly TimeSpan ExchangeDeadline = TimeSpan.FromSeconds(15);

    internal static byte[] ExtendedLimits()
    {
        var bytes = new byte[144];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(16), LimitFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(40), MaximumProcesses);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(112), ProcessMemoryBytes);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(120), JobMemoryBytes);
        return bytes;
    }
    internal static byte[] CpuLimits()
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, CpuFlags);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), CpuRate);
        return bytes;
    }
    internal static void Validate(ReadOnlySpan<byte> extended, ReadOnlySpan<byte> cpu)
    {
        if (extended.Length != 144 || cpu.Length != 8 ||
            BinaryPrimitives.ReadUInt32LittleEndian(extended[16..]) != LimitFlags ||
            BinaryPrimitives.ReadUInt32LittleEndian(extended[40..]) != MaximumProcesses ||
            BinaryPrimitives.ReadUInt64LittleEndian(extended[112..]) != ProcessMemoryBytes ||
            BinaryPrimitives.ReadUInt64LittleEndian(extended[120..]) != JobMemoryBytes ||
            BinaryPrimitives.ReadUInt32LittleEndian(cpu) != CpuFlags ||
            BinaryPrimitives.ReadUInt32LittleEndian(cpu[4..]) != CpuRate)
            throw new InvalidDataException("NATIVE_OWNED_JOB_RESOURCE_POLICY_MISMATCH");
    }
    internal static void ApplyToFreshJob(IntPtr originalJob)
    {
        if (!Environment.Is64BitProcess || originalJob == IntPtr.Zero) throw new InvalidOperationException("NATIVE_OWNED_X64_JOB_REQUIRED");
        byte[] accounting = Query(originalJob, 1, 48), original = Query(originalJob, 9, 144);
        if (BinaryPrimitives.ReadUInt32LittleEndian(accounting.AsSpan(36)) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(accounting.AsSpan(40)) != 0 ||
            BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(16)) != 0)
            throw new InvalidDataException("NATIVE_FRESH_UNASSIGNED_JOB_REQUIRED");
        Set(originalJob, 9, ExtendedLimits()); Set(originalJob, 15, CpuLimits());
        RequireKernelPolicy(originalJob);
    }
    internal static void RequireKernelPolicy(IntPtr originalJob) => Validate(Query(originalJob, 9, 144), Query(originalJob, 15, 8));
    internal static void RequestTermination(IntPtr originalJob)
    {
        if (originalJob == IntPtr.Zero) throw new InvalidOperationException("NATIVE_ORIGINAL_JOB_REQUIRED");
        // This is only a request. Callers must independently observe original
        // process/thread handles, Job active=0/PID-list empty and context cleanup.
        if (!TerminateJobObject(originalJob, RecoveryExitCode)) throw new Win32Exception(Marshal.GetLastWin32Error(), "NATIVE_ORIGINAL_JOB_RECOVERY_FAILED");
    }
    private static byte[] Query(IntPtr job, int kind, int length)
    {
        var bytes = new byte[length];
        if (!QueryInformationJobObject(job, kind, bytes, (uint)length, out uint returned) || returned != length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "NATIVE_JOB_RESOURCE_QUERY_FAILED");
        return bytes;
    }
    private static void Set(IntPtr job, int kind, byte[] bytes)
    { if (!SetInformationJobObject(job, kind, bytes, (uint)bytes.Length)) throw new Win32Exception(Marshal.GetLastWin32Error(), "NATIVE_JOB_RESOURCE_SET_FAILED"); }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(IntPtr job, int kind, [Out] byte[] bytes, uint length, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int kind, byte[] bytes, uint length);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
}

// Pure deadline/request state, deliberately unable to assert retirement.
internal sealed class OwnedJobRecovery
{
    private bool requested;
    internal bool Requested => requested;
    internal bool TryRequest(TimeSpan elapsed, bool shutdownRequired, bool originalGroupAlreadyEmpty, Action terminateOriginalJob)
    {
        ArgumentNullException.ThrowIfNull(terminateOriginalJob);
        if (requested || !shutdownRequired || originalGroupAlreadyEmpty || elapsed < OwnedJobPolicy.GracePeriod) return false;
        requested = true;
        terminateOriginalJob();
        return true;
    }
}

// Pure monotonic phase accounting. A graceful Close owns the same bounded I/O
// deadline as every exchange; its in-flight time is not also fault/EOF grace.
// Neither reaching either deadline nor completing Close is exit evidence.
// The owner serializes calls with its state lock and supplies Stopwatch time.
internal sealed class OwnedShutdownBudget
{
    private TimeSpan? closeStartedAt, recoveryStartedAt;
    internal bool CloseExchangeStarted => closeStartedAt.HasValue;

    internal void BeginCloseExchange(TimeSpan now) => closeStartedAt ??= now;

    internal void CompleteCloseExchange(TimeSpan now)
    {
        if (!closeStartedAt.HasValue) throw new InvalidOperationException("NATIVE_CLOSE_EXCHANGE_NOT_STARTED");
        ObserveShutdown(now);
    }

    // A real transport fault, explicit EOF request or observed station exit may
    // supersede a pending graceful exchange. Later observations cannot reset it.
    internal void ObserveShutdown(TimeSpan now)
    {
        // A late continuation/EOF observation cannot extend a Close deadline
        // which already elapsed while the observer was awaiting a kernel query.
        TimeSpan started = closeStartedAt is { } close && now - close >= OwnedJobPolicy.ExchangeDeadline
            ? close + OwnedJobPolicy.ExchangeDeadline : now;
        recoveryStartedAt ??= started;
    }

    internal bool TryGetRecoveryElapsed(TimeSpan now, out TimeSpan elapsed)
    {
        // I/O cancellation normally settles ExchangeAsync at this boundary.
        // Keep a bounded fallback if its continuation has not yet run; do not
        // wait indefinitely for a managed task before rescuing the original Job.
        if (!recoveryStartedAt.HasValue && closeStartedAt is { } started && now - started >= OwnedJobPolicy.ExchangeDeadline)
            recoveryStartedAt = started + OwnedJobPolicy.ExchangeDeadline;
        elapsed = recoveryStartedAt is { } recovery ? now - recovery : TimeSpan.Zero;
        if (elapsed < TimeSpan.Zero) throw new InvalidOperationException("NATIVE_MONOTONIC_SHUTDOWN_TIME_REQUIRED");
        return recoveryStartedAt.HasValue;
    }
}
