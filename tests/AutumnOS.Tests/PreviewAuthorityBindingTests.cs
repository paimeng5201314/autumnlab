using AutumnOS.Contracts;
using AutumnOS.Shell;

namespace AutumnOS.Tests;

// Synthetic one-shot permits only. Real native fence ordering is tested separately.
internal static class PreviewAuthorityBindingTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return Case("identity_maps_to_bound_live_or_test_instance_without_broadening_other_scopes", Routes);
        yield return Case("old_bound_pending_permit_is_consumed_without_mutating_new_instance", Rebind);
        yield return Case("unbound_pending_begin_cannot_mutate_after_first_instance_binding", FirstBind);
        yield return Case("synchronous_identity_commit_rechecks_binding_generation", SynchronousRebind);
    }
    private static (string, Action) Case(string name, Func<Task> run) =>
        ("network.preview.synthetic_router_" + name, () => run().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult());

    private static async Task Routes()
    {
        var live = new Authority(); var simulation = new Authority(); var router = new PreviewAuthorityRouter(live, simulation);
        foreach ((string account, Authority expected) in new[] { ("account-live", live), ("developer-preview:fixture:guest", simulation) })
        {
            var scope = RuntimeMutationScope.ForInstance(account, "binding-fixture", Guid.NewGuid()); router.BindInstanceScope(scope);
            int before = expected.Items.Count;
            var mutation = await router.BeginAsync(RuntimeMutationScope.ForIdentity());
            Check(expected.Items.Count == before + 1 && expected.Items[^1].Scope == scope);
            Check(ReferenceEquals(router.For(scope), expected));
            expected.Items[^1].Release.TrySetResult(true); int commits = 0;
            await mutation.CommitAsync(() => commits++); Check(commits == 1);
        }
        var ordinary = RuntimeMutationScope.ForApplication("account-other", "ordinary-binding");
        var normal = await router.BeginAsync(ordinary); Check(live.Items[^1].Scope == ordinary);
        live.Items[^1].Release.TrySetResult(true); await normal.CommitAsync(() => { });
    }
    private static async Task Rebind()
    {
        foreach (bool sameScope in new[] { false, true })
        {
            var live = new Authority(); var simulation = new Authority(); var router = new PreviewAuthorityRouter(live, simulation);
            var old = RuntimeMutationScope.ForInstance("account-live", "binding-fixture", Guid.NewGuid()); router.BindInstanceScope(old);
            var mutation = await router.BeginAsync(RuntimeMutationScope.ForIdentity());
            var permit = live.Items.Single(); int writes = 0;
            Task commit = mutation.CommitAsync(() => writes++); await permit.CommitRequested.Task;
            router.BindInstanceScope(sameScope ? old : old with { InstanceId = Guid.NewGuid() });
            permit.Release.TrySetResult(true);
            await Expired(commit); Check(writes == 0 && permit.Commits == 1);
            // The stale permit was settled; it cannot be reused to mutate later.
            try { await mutation.CommitAsync(() => writes++); throw new InvalidOperationException("Permit replay accepted."); }
            catch (InvalidOperationException error) when (error.Message == "SYNTHETIC_PERMIT_REPLAY") { }
            var fresh = await router.BeginAsync(RuntimeMutationScope.ForIdentity());
            live.Items[^1].Release.TrySetResult(true); await fresh.CommitAsync(() => writes++); Check(writes == 1);
        }
    }
    private static async Task FirstBind()
    {
        var live = new Authority(); var simulation = new Authority { PauseBegin = true }; var router = new PreviewAuthorityRouter(live, simulation);
        Task<IRuntimeAuthorityMutation> starting = router.BeginAsync(RuntimeMutationScope.ForIdentity()).AsTask();
        await simulation.BeginRequested.Task;
        Check(simulation.Items.Single().Scope.Kind == RuntimeMutationScopeKind.Identity && live.Items.Count == 0);
        router.BindInstanceScope(RuntimeMutationScope.ForInstance("account-live", "binding-fixture", Guid.NewGuid()));
        simulation.BeginRelease.TrySetResult(true); var mutation = await starting;
        int writes = 0; simulation.Items.Single().Release.TrySetResult(true);
        await Expired(mutation.CommitAsync(() => writes++)); Check(writes == 0 && simulation.Items.Single().Commits == 1);
    }
    private static Task SynchronousRebind()
    {
        var live = new Authority(); var simulation = new Authority(); var router = new PreviewAuthorityRouter(live, simulation);
        var scope = RuntimeMutationScope.ForInstance("account-live", "binding-fixture", Guid.NewGuid()); router.BindInstanceScope(scope);
        live.BeforeSynchronousCommit = () => router.BindInstanceScope(scope with { InstanceId = Guid.NewGuid() });
        int writes = 0;
        try { router.TryCommitSynchronously(RuntimeMutationScope.ForIdentity(), () => writes++); throw new InvalidOperationException("Stale sync accepted."); }
        catch (RuntimeCapabilityException error) when (error.Code == "DEVELOPER_PREVIEW_SESSION_EXPIRED") { }
        Check(writes == 0 && live.SynchronousScope == scope);
        live.BeforeSynchronousCommit = null;
        Check(router.TryCommitSynchronously(RuntimeMutationScope.ForIdentity(), () => writes++) && writes == 1);
        return Task.CompletedTask;
    }
    private sealed class Authority : IRuntimeAuthorityMutations
    {
        internal List<Permit> Items = [];
        internal bool PauseBegin;
        internal TaskCompletionSource<bool> BeginRequested = Signal(), BeginRelease = Signal();
        internal Action? BeforeSynchronousCommit;
        internal RuntimeMutationScope? SynchronousScope;
        public async ValueTask<IRuntimeAuthorityMutation> BeginAsync(RuntimeMutationScope scope, CancellationToken beforeIntentCancellation = default)
        {
            beforeIntentCancellation.ThrowIfCancellationRequested(); var permit = new Permit(scope); Items.Add(permit); BeginRequested.TrySetResult(true);
            if (PauseBegin) await BeginRelease.Task;
            return permit;
        }
        public bool TryCommitSynchronously(RuntimeMutationScope scope, Action shortCommit)
        { SynchronousScope = scope; BeforeSynchronousCommit?.Invoke(); shortCommit(); return true; }
    }
    private sealed class Permit(RuntimeMutationScope scope) : IRuntimeAuthorityMutation
    {
        internal RuntimeMutationScope Scope = scope;
        internal int Commits;
        internal TaskCompletionSource<bool> Release = Signal(), CommitRequested = Signal();
        public async Task CommitAsync(Action shortCommit)
        {
            if (Interlocked.Increment(ref Commits) != 1) throw new InvalidOperationException("SYNTHETIC_PERMIT_REPLAY");
            CommitRequested.TrySetResult(true); await Release.Task; shortCommit();
        }
    }
    private static async Task Expired(Task task)
    {
        try { await task; } catch (RuntimeCapabilityException error) when (error.Code == "DEVELOPER_PREVIEW_SESSION_EXPIRED") { return; }
        throw new InvalidOperationException("Stale preview generation accepted.");
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Synthetic preview binding assertion failed."); }
}
