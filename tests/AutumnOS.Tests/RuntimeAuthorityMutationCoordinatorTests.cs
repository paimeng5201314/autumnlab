using AutumnOS.Contracts;
using AutumnOS.Runtime;
using AutumnOS.Shell;

namespace AutumnOS.Tests;

// Synthetic driver and lifetime only; none of these Tasks proves a native Post
// or an operating-system process exit.
internal static class RuntimeAuthorityMutationCoordinatorTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return Case("begin_installs_writer_before_return_and_commit_is_once", WriterOwnership);
        yield return Case("caller_cancellation_after_intent_does_not_abandon_writer", CancellationOwnership);
        yield return Case("action_failure_consumes_permit_and_releases_next_writer", FailedCommit);
        yield return Case("native_fence_receipt_required_before_real_mutation", HeldReceipt);
        yield return Case("close_can_start_cleanup_after_intent_before_commit", CloseRetirement);
        yield return Case("sync_fast_path_never_installs_intent_when_unavailable", SynchronousCompatibility);
        yield return Case("live_preview_inherits_real_authority_fence", LivePreviewAuthority);
        yield return Case("test_preview_identity_has_its_own_authority", TestPreviewAuthority);
    }

    private static (string, Action) Case(string name, Func<Task> run) =>
        ("g2b2.authority.pure_fake_" + name, () => run().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult());

    private static async Task WriterOwnership()
    {
        var core = new RuntimeDeliveryCoordinator();
        var gate = new RuntimeAuthorityMutationCoordinator(Guid.NewGuid(), core);
        var foreign = new RuntimeDeliveryBinding(Guid.NewGuid(), "synthetic-account", 1, new string('a', 64),
            Guid.NewGuid(), 1, Guid.NewGuid(), 1, 1);
        Throws<ArgumentException>(() => gate.RegisterHostEndpoint(foreign, new Driver(), new Lifetime(foreign)));
        Check(core.SnapshotCounts().Endpoints == 0);
        IRuntimeAuthorityMutation first = await gate.BeginAsync(RuntimeMutationScope.ForIdentity());
        IRuntimeAuthorityMutation second = await gate.BeginAsync(RuntimeMutationScope.ForIdentity());
        int commits = 0;
        Task queued = second.CommitAsync(() => commits++);
        Check(!queued.IsCompleted && core.SnapshotCounts().Fences == 2 && commits == 0);
        Throws<InvalidOperationException>(() => second.CommitAsync(() => commits++));
        await first.CommitAsync(() => commits++); await queued;
        Check(commits == 2 && core.SnapshotCounts().Fences == 0);
        Throws<InvalidOperationException>(() => first.CommitAsync(() => commits++));
    }

    private static async Task CancellationOwnership()
    {
        var core = new RuntimeDeliveryCoordinator();
        var gate = new RuntimeAuthorityMutationCoordinator(Guid.NewGuid(), core);
        using var preCancelled = new CancellationTokenSource(); preCancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () =>
            _ = await gate.BeginAsync(RuntimeMutationScope.ForIdentity(), preCancelled.Token));
        Check(core.SnapshotCounts().Fences == 0);
        using var afterIntent = new CancellationTokenSource();
        IRuntimeAuthorityMutation operation = await gate.BeginAsync(RuntimeMutationScope.ForIdentity(), afterIntent.Token);
        afterIntent.Cancel(); int commits = 0;
        await operation.CommitAsync(() => commits++);
        Check(commits == 1 && core.SnapshotCounts().Fences == 0);
    }

    private static async Task FailedCommit()
    {
        var core = new RuntimeDeliveryCoordinator();
        var gate = new RuntimeAuthorityMutationCoordinator(Guid.NewGuid(), core);
        IRuntimeAuthorityMutation first = await gate.BeginAsync(RuntimeMutationScope.ForIdentity());
        IRuntimeAuthorityMutation next = await gate.BeginAsync(RuntimeMutationScope.ForIdentity());
        await ThrowsAsync<ArgumentException>(() => first.CommitAsync(() => throw new ArgumentException("synthetic")));
        int commits = 0; await next.CommitAsync(() => commits++);
        Check(commits == 1 && core.SnapshotCounts().Fences == 0);
        Throws<InvalidOperationException>(() => first.CommitAsync(() => commits++));
    }

    private static async Task HeldReceipt()
    {
        var endpoint = new Endpoint(); int commits = 0;
        IRuntimeAuthorityMutation operation = await endpoint.Gate.BeginAsync(
            RuntimeMutationScope.ForApplication(endpoint.Binding.AccountKey, endpoint.Binding.ApplicationBinding));
        Task commit = operation.CommitAsync(() => commits++);
        RuntimeFenceWrite barrier = await endpoint.Driver.Received.Task;
        Check(!commit.IsCompleted && commits == 0);
        Check(!endpoint.Registration.Evidence.AcceptNativeFence(Receipt(barrier) with { NextChannelGeneration = 99 }));
        Check(!commit.IsCompleted && commits == 0);
        Check(endpoint.Registration.Evidence.AcceptNativeFence(Receipt(barrier)));
        await commit; Check(commits == 1);
        await endpoint.Retire();
    }

    private static async Task CloseRetirement()
    {
        var endpoint = new Endpoint(); int commits = 0;
        IRuntimeAuthorityMutation operation = await endpoint.Gate.BeginAsync(RuntimeMutationScope.ForInstance(
            endpoint.Binding.AccountKey, endpoint.Binding.ApplicationBinding, endpoint.Binding.InstanceId));
        await endpoint.Driver.Received.Task;
        endpoint.Registration.Evidence.MarkTransportUnknown();
        Task commit = operation.CommitAsync(() => commits++);
        Check(!commit.IsCompleted && commits == 0);
        // This explicit synthetic lifetime signal stands in for a future owner
        // observing its original handle group. Disconnect alone did not commit.
        await endpoint.Retire(); await commit;
        Check(commits == 1 && endpoint.Core.SnapshotCounts().Fences == 0);
    }

    private static async Task SynchronousCompatibility()
    {
        var core = new RuntimeDeliveryCoordinator();
        var gate = new RuntimeAuthorityMutationCoordinator(Guid.NewGuid(), core);
        int commits = 0;
        Check(gate.TryCommitSynchronously(RuntimeMutationScope.ForIdentity(), () => commits++));
        IRuntimeAuthorityMutation pending = await gate.BeginAsync(RuntimeMutationScope.ForIdentity());
        Check(!gate.TryCommitSynchronously(RuntimeMutationScope.ForIdentity(), () => commits++));
        Check(commits == 1 && core.SnapshotCounts().Fences == 1);
        await pending.CommitAsync(() => { });
        Throws<ArgumentException>(() => gate.TryCommitSynchronously(RuntimeMutationScope.ForIdentity(),
            () => throw new ArgumentException("synthetic")));
        Check(core.SnapshotCounts().Fences == 0);

        var endpoint = new Endpoint();
        Check(!endpoint.Gate.TryCommitSynchronously(RuntimeMutationScope.ForIdentity(), () => commits++));
        Check(endpoint.Core.SnapshotCounts().Fences == 0 && commits == 1);
        await endpoint.Retire();
        Check(endpoint.Gate.TryCommitSynchronously(RuntimeMutationScope.ForIdentity(), () => commits++));
        Check(commits == 2);
    }

    private static async Task LivePreviewAuthority()
    {
        var real = new Endpoint();
        var testCore = new RuntimeDeliveryCoordinator();
        var testGate = new RuntimeAuthorityMutationCoordinator(Guid.NewGuid(), testCore);
        var router = new PreviewAuthorityRouter(real.Gate, testGate);
        IRuntimeAuthorityMutation operation = await router.BeginAsync(RuntimeMutationScope.ForApplication(
            real.Binding.AccountKey, real.Binding.ApplicationBinding));
        int changes = 0; Task commit = operation.CommitAsync(() => changes++);
        RuntimeFenceWrite barrier = await real.Driver.Received.Task;
        Check(changes == 0 && !commit.IsCompleted && testCore.SnapshotCounts().Fences == 0);
        Check(real.Registration.Evidence.AcceptNativeFence(Receipt(barrier)));
        await commit; Check(changes == 1); await real.Retire();
    }

    private static async Task TestPreviewAuthority()
    {
        var realCore = new RuntimeDeliveryCoordinator();
        var realGate = new RuntimeAuthorityMutationCoordinator(Guid.NewGuid(), realCore);
        var test = new Endpoint("developer-preview:controlled:account-a");
        var router = new PreviewAuthorityRouter(realGate, test.Gate);
        IRuntimeAuthorityMutation operation = await router.BeginAsync(RuntimeMutationScope.ForIdentity());
        int changes = 0; Task commit = operation.CommitAsync(() => changes++);
        RuntimeFenceWrite barrier = await test.Driver.Received.Task;
        Check(changes == 0 && !commit.IsCompleted && realCore.SnapshotCounts().Fences == 0);
        Check(test.Registration.Evidence.AcceptNativeFence(Receipt(barrier)));
        await commit; Check(changes == 1); await test.Retire();
    }

    private sealed class Driver : IOrderedRuntimeDeliveryDriver
    {
        internal TaskCompletionSource<RuntimeFenceWrite> Received { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask WriteDeliveryAsync(RuntimeDeliveryWrite delivery) => throw new InvalidOperationException("UNEXPECTED_DELIVERY");
        public ValueTask WriteFenceAsync(RuntimeFenceWrite fence) { Check(Received.TrySetResult(fence)); return ValueTask.CompletedTask; }
    }
    private sealed class Lifetime(RuntimeDeliveryBinding binding) : IOriginalRuntimeEndpointLifetime
    {
        internal TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private RuntimeEndpointLifetimeRegistration? registration;
        public RuntimeEndpointLifetimeRegistration OriginalLifetime => registration ??= RuntimeEndpointLifetimeRegistration.BindForHost(binding, Completion.Task);
    }
    private sealed class Endpoint
    {
        internal RuntimeDeliveryCoordinator Core { get; } = new();
        internal RuntimeAuthorityMutationCoordinator Gate { get; }
        internal RuntimeDeliveryBinding Binding { get; }
        internal RuntimeDeliveryCoordinator.HostRegistration Registration { get; }
        internal Driver Driver { get; } = new();
        private readonly Lifetime lifetime;
        internal Endpoint(string account = "synthetic-account")
        {
            Gate = new(Guid.NewGuid(), Core);
            Binding = new(Gate.IdentityAuthorityId, account, 1, new string('a', 64), Guid.NewGuid(), 1, Guid.NewGuid(), 1, 1);
            lifetime = new(Binding); Registration = Gate.RegisterHostEndpoint(Binding, Driver, lifetime);
        }
        internal async Task Retire()
        { lifetime.Completion.TrySetResult(); Check(await Registration.Evidence.RetirementObserved); await Registration.Evidence.WhenIdle; }
    }
    private static RuntimeNativeFenceReceipt Receipt(RuntimeFenceWrite write) =>
        new(write.FenceId, write.Binding, write.CutoffDeliveryId, write.NextChannelGeneration);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("AUTHORITY_MUTATION_ASSERTION"); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("EXPECTED_" + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("EXPECTED_" + typeof(T).Name); }
}
