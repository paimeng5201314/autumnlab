using System.Buffers.Binary;
using AutumnOS.NativeHost.Windows;

namespace AutumnOS.Tests;

// Pure ABI/policy and request ownership checks. These are not OS retirement tests.
internal static class NativeOwnedJobPolicyTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("native.job.resource_limits_include_memory_process_cpu_and_no_breakaway", Limits);
        yield return ("native.job.readback_rejects_every_weakened_resource_limit", Weakened);
        yield return ("native.job.kernel_accounting_changes_do_not_change_resource_policy", Accounting);
        yield return ("native.job.generated_limit_buffers_cannot_mutate_global_policy", IndependentBuffers);
        yield return ("native.job.recovery_requires_shutdown_and_elapsed_grace_and_nonempty_group", RecoveryConditions);
        yield return ("native.job.recovery_request_is_single_use_and_not_exit_evidence", RecoveryOnce);
        yield return ("native.job.failed_termination_request_remains_failed_and_is_not_retried_as_success", RecoveryFailure);
        yield return ("native.close.inflight_exchange_does_not_consume_fault_recovery_grace", CloseInFlight);
        yield return ("native.close.confirmed_exchange_starts_a_separate_cleanup_grace", CloseCompleted);
        yield return ("native.close.unsettled_exchange_deadline_still_bounds_original_job_recovery", CloseDeadline);
        yield return ("native.close.explicit_eof_or_fault_supersedes_inflight_exchange_without_reset", CloseFault);
        yield return ("native.close.repeated_observation_cannot_postpone_recovery_or_assert_exit", CloseRepeated);
        yield return ("native.close.idle_owner_has_no_recovery_deadline", NoShutdown);
    }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("OWNED_JOB_POLICY_ASSERTION"); }
    private static void Reject(Action action)
    { try { action(); } catch (InvalidDataException) { return; } throw new InvalidOperationException("WEAKENED_JOB_POLICY_ACCEPTED"); }
    private static void Limits()
    {
        byte[] ext = OwnedJobPolicy.ExtendedLimits(), cpu = OwnedJobPolicy.CpuLimits();
        OwnedJobPolicy.Validate(ext, cpu);
        Check(ext.Length == 144 && cpu.Length == 8 && (OwnedJobPolicy.LimitFlags & 0x1800) == 0);
        Check((OwnedJobPolicy.LimitFlags & 0x2308) == 0x2308);
        Check(BinaryPrimitives.ReadUInt32LittleEndian(ext.AsSpan(40)) is > 0 and <= 64 && OwnedJobPolicy.ProcessMemoryBytes <= OwnedJobPolicy.JobMemoryBytes);
        Check(OwnedJobPolicy.JobMemoryBytes <= 4UL * 1024 * 1024 * 1024 && BinaryPrimitives.ReadUInt32LittleEndian(cpu.AsSpan(4)) is > 0 and < 10000);
        Check(OwnedJobPolicy.QueryAndTerminateAccess == 12 && (OwnedJobPolicy.QueryAndTerminateAccess & 3) == 0);
    }
    private static void Weakened()
    {
        foreach (int field in new[] { 16, 40, 112, 120 })
        {
            byte[] ext = OwnedJobPolicy.ExtendedLimits(); ext[field] ^= 1;
            Reject(() => OwnedJobPolicy.Validate(ext, OwnedJobPolicy.CpuLimits()));
        }
        foreach (int field in new[] { 0, 4 })
        {
            byte[] cpu = OwnedJobPolicy.CpuLimits(); cpu[field] ^= 1;
            Reject(() => OwnedJobPolicy.Validate(OwnedJobPolicy.ExtendedLimits(), cpu));
        }
        byte[] breakaway = OwnedJobPolicy.ExtendedLimits();
        BinaryPrimitives.WriteUInt32LittleEndian(breakaway.AsSpan(16), OwnedJobPolicy.LimitFlags | 0x800);
        Reject(() => OwnedJobPolicy.Validate(breakaway, OwnedJobPolicy.CpuLimits()));
        foreach (int length in new[] { 0, 64, 143, 145 }) Reject(() => OwnedJobPolicy.Validate(new byte[length], OwnedJobPolicy.CpuLimits()));
        foreach (int length in new[] { 0, 7, 9 }) Reject(() => OwnedJobPolicy.Validate(OwnedJobPolicy.ExtendedLimits(), new byte[length]));
    }
    private static void Accounting()
    {
        byte[] ext = OwnedJobPolicy.ExtendedLimits();
        ext.AsSpan(64, 48).Fill(0x7f); ext.AsSpan(128, 16).Fill(0x3a);
        OwnedJobPolicy.Validate(ext, OwnedJobPolicy.CpuLimits());
    }
    private static void IndependentBuffers()
    {
        byte[] first = OwnedJobPolicy.ExtendedLimits(); first.AsSpan().Clear();
        OwnedJobPolicy.Validate(OwnedJobPolicy.ExtendedLimits(), OwnedJobPolicy.CpuLimits());
    }
    private static void RecoveryConditions()
    {
        var owner = new OwnedJobRecovery(); int calls = 0;
        Check(!owner.TryRequest(TimeSpan.Zero, true, false, () => calls++));
        Check(!owner.TryRequest(OwnedJobPolicy.GracePeriod, false, false, () => calls++));
        Check(!owner.TryRequest(OwnedJobPolicy.GracePeriod, true, true, () => calls++));
        Check(!owner.Requested && calls == 0);
        Check(owner.TryRequest(OwnedJobPolicy.GracePeriod, true, false, () => calls++));
        Check(owner.Requested && calls == 1);
    }
    private static void RecoveryOnce()
    {
        var owner = new OwnedJobRecovery(); int calls = 0; bool originalExitObserved = false;
        Check(owner.TryRequest(OwnedJobPolicy.GracePeriod, true, false, () => calls++));
        Check(!owner.TryRequest(OwnedJobPolicy.GracePeriod + TimeSpan.FromHours(1), true, false, () => calls++));
        Check(calls == 1 && !originalExitObserved); // A request did not fabricate an original-object observation.
    }
    private static void RecoveryFailure()
    {
        var owner = new OwnedJobRecovery(); int calls = 0;
        try { owner.TryRequest(OwnedJobPolicy.GracePeriod, true, false, () => { calls++; throw new IOException("owned fake termination failure"); }); }
        catch (IOException)
        {
            Check(owner.Requested && !owner.TryRequest(OwnedJobPolicy.GracePeriod, true, false, () => calls++));
            Check(calls == 1); return;
        }
        throw new InvalidOperationException("TERMINATION_FAILURE_SWALLOWED");
    }
    private static void CloseInFlight()
    {
        var budget = new OwnedShutdownBudget(); var recovery = new OwnedJobRecovery(); int calls = 0;
        budget.BeginCloseExchange(TimeSpan.Zero);
        // This is the production race: the browser may still be observing its
        // original handle and SDK exit set at 12s, inside the valid 15s RPC.
        Check(budget.CloseExchangeStarted && !budget.TryGetRecoveryElapsed(OwnedJobPolicy.GracePeriod, out _));
        Check(!budget.TryGetRecoveryElapsed(OwnedJobPolicy.ExchangeDeadline - TimeSpan.FromTicks(1), out _));
        Check(!recovery.Requested && calls == 0);
    }
    private static void CloseCompleted()
    {
        var budget = new OwnedShutdownBudget(); var recovery = new OwnedJobRecovery(); int calls = 0;
        budget.BeginCloseExchange(TimeSpan.Zero);
        TimeSpan completed = OwnedJobPolicy.ExchangeDeadline - TimeSpan.FromSeconds(1);
        budget.CompleteCloseExchange(completed);
        Check(budget.TryGetRecoveryElapsed(completed, out var elapsed) && elapsed == TimeSpan.Zero);
        Check(!recovery.TryRequest(elapsed, true, false, () => calls++));
        Check(budget.TryGetRecoveryElapsed(completed + OwnedJobPolicy.GracePeriod - TimeSpan.FromTicks(1), out elapsed));
        Check(!recovery.TryRequest(elapsed, true, false, () => calls++));
        Check(budget.TryGetRecoveryElapsed(completed + OwnedJobPolicy.GracePeriod, out elapsed));
        Check(recovery.TryRequest(elapsed, true, false, () => calls++) && calls == 1);
    }
    private static void CloseDeadline()
    {
        var budget = new OwnedShutdownBudget(); var recovery = new OwnedJobRecovery(); int calls = 0;
        TimeSpan started = TimeSpan.FromSeconds(7);
        budget.BeginCloseExchange(started);
        Check(budget.TryGetRecoveryElapsed(started + OwnedJobPolicy.ExchangeDeadline, out var elapsed) && elapsed == TimeSpan.Zero);
        Check(!recovery.TryRequest(elapsed, true, false, () => calls++));
        Check(budget.TryGetRecoveryElapsed(started + OwnedJobPolicy.ExchangeDeadline + OwnedJobPolicy.GracePeriod, out elapsed));
        Check(recovery.TryRequest(elapsed, true, false, () => calls++) && calls == 1);
        // Delayed exchange completion must not move the already elapsed budget.
        budget.CompleteCloseExchange(started + TimeSpan.FromHours(1));
        Check(budget.TryGetRecoveryElapsed(started + TimeSpan.FromHours(1), out elapsed) && elapsed > OwnedJobPolicy.GracePeriod);
        var delayed = new OwnedShutdownBudget();
        delayed.BeginCloseExchange(started);
        delayed.CompleteCloseExchange(started + OwnedJobPolicy.ExchangeDeadline + OwnedJobPolicy.GracePeriod);
        Check(delayed.TryGetRecoveryElapsed(started + OwnedJobPolicy.ExchangeDeadline + OwnedJobPolicy.GracePeriod, out elapsed) && elapsed == OwnedJobPolicy.GracePeriod);
    }
    private static void CloseFault()
    {
        var budget = new OwnedShutdownBudget(); var recovery = new OwnedJobRecovery(); int calls = 0;
        budget.BeginCloseExchange(TimeSpan.Zero);
        TimeSpan fault = TimeSpan.FromSeconds(2);
        budget.ObserveShutdown(fault);
        Check(budget.TryGetRecoveryElapsed(fault, out var elapsed) && elapsed == TimeSpan.Zero);
        Check(!recovery.TryRequest(elapsed, true, false, () => calls++));
        budget.CompleteCloseExchange(fault + TimeSpan.FromSeconds(1));
        Check(budget.TryGetRecoveryElapsed(fault + OwnedJobPolicy.GracePeriod, out elapsed) && elapsed == OwnedJobPolicy.GracePeriod);
        Check(recovery.TryRequest(elapsed, true, false, () => calls++) && calls == 1);
    }
    private static void CloseRepeated()
    {
        var budget = new OwnedShutdownBudget(); var recovery = new OwnedJobRecovery(); int calls = 0;
        bool originalHandlesSignaled = false, originalJobEmpty = false;
        budget.ObserveShutdown(TimeSpan.Zero);
        budget.BeginCloseExchange(TimeSpan.FromSeconds(3));
        budget.ObserveShutdown(TimeSpan.FromSeconds(8));
        budget.BeginCloseExchange(TimeSpan.FromSeconds(10));
        budget.CompleteCloseExchange(OwnedJobPolicy.GracePeriod);
        Check(budget.TryGetRecoveryElapsed(OwnedJobPolicy.GracePeriod, out var elapsed) && elapsed == OwnedJobPolicy.GracePeriod);
        Check(recovery.TryRequest(elapsed, true, originalJobEmpty, () => calls++));
        Check(calls == 1 && !originalHandlesSignaled && !originalJobEmpty);
    }
    private static void NoShutdown()
    {
        var budget = new OwnedShutdownBudget();
        Check(!budget.CloseExchangeStarted && !budget.TryGetRecoveryElapsed(TimeSpan.FromDays(1), out _));
    }
}
