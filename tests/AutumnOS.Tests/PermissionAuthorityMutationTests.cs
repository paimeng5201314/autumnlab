using System.Threading.Channels;
using System.Reflection;
using System.Collections.Concurrent;
using AutumnOS.Contracts;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

// Deterministic authority-ordering fakes. These are not OS, browser or IPC proof.
internal static class PermissionAuthorityMutationTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return Case("persistent_setting_waits_for_application_permit_and_notifies_after_release", PersistentSetting);
        yield return Case("persistent_prompt_cannot_overwrite_revoke_regrant", PersistentStalePrompt);
        yield return Case("persistent_cancellation_before_intent_creates_none_after_intent_settles", PersistentCancellation);
        yield return Case("cancelled_settings_denial_and_offline_consume_permits_without_changes", CancelledSettingsAndPreview);
        yield return Case("persistent_account_rechecked_and_failed_commit_consumes_permit", AccountRecheck);
        yield return Case("preview_decisions_and_forced_denial_are_fenced_and_memory_only", PreviewDecisions);
        yield return Case("preview_prompt_cannot_overwrite_deny_restore", PreviewStalePrompt);
        yield return Case("preview_identity_revision_changes_without_expiring_offline_account", SimulationRevision);
        yield return Case("preview_identity_cancellation_and_disposal_settle_registered_intent", SimulationCancellationAndDisposal);
        yield return Case("preview_queued_mutation_rechecks_disposed_authority", SimulationDisposedRecheck);
        yield return Case("revision_exhaustion_preserves_disk_decisions_and_consumes_permits", RevisionExhaustion);
    }

    private static (string, Action) Case(string name, Func<Task> run) =>
        ("g2b2.permission.pure_fake_" + name, () => run().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult());

    private static async Task PersistentSetting()
    {
        var gate = new ControlledGate(); using var fixture = new Fixture(gate);
        var service = fixture.Permissions; var account = Account(); var app = App();
        int notices = 0;
        service.Changed += change =>
        {
            Check(!gate.InCommit && change.Decision == PermissionDecision.Granted);
            Check(Task.Run(() => service.Query(account, app, "saves")).Wait(TimeSpan.FromSeconds(2)));
            notices++;
        };
        Reject(() => service.SetDecision(account, app, "saves", PermissionDecision.Granted), "RUNTIME_ASYNC_AUTHORITY_REQUIRED");
        Check(gate.Count == 0 && service.GetRevision(account, app, "saves") == 0);
        Task write = service.SetDecisionAsync(account, app, "saves", PermissionDecision.Granted);
        var operation = await gate.Next(); ApplicationScope(operation, account, app);
        Check(!write.IsCompleted && service.Query(account, app, "saves") == PermissionDecision.Prompt && notices == 0);
        operation.Release(); await write;
        Check(operation.Consumed && service.Query(account, app, "saves") == PermissionDecision.Granted && notices == 1);
        Check(service.GetRevision(account, app, "saves") == 1);
    }

    private static async Task PersistentStalePrompt()
    {
        var gate = new ControlledGate(); using var fixture = new Fixture(gate);
        var account = Account(); var app = App();
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<PermissionDecision> prompt = fixture.Permissions.RequestAsync(account, app, "saves", true, (_, _) => answer.Task, CancellationToken.None);
        await Set(fixture.Permissions, gate, account, app, PermissionDecision.Revoked);
        await Set(fixture.Permissions, gate, account, app, PermissionDecision.Granted);
        answer.SetResult(true); var stale = await gate.Next(); ApplicationScope(stale, account, app);
        stale.Release(); await RejectAsync(prompt, "PERMISSION_REVOKED");
        Check(stale.Consumed && fixture.Permissions.Query(account, app, "saves") == PermissionDecision.Granted);
        Check(fixture.Permissions.GetRevision(account, app, "saves") == 2);
    }

    private static async Task PersistentCancellation()
    {
        var gate = new ControlledGate(); using var fixture = new Fixture(gate);
        var account = Account(); var app = App();
        using var before = new CancellationTokenSource(); before.Cancel();
        await Cancelled(fixture.Permissions.SetDecisionAsync(account, app, "saves", PermissionDecision.Granted, before.Token));
        Check(gate.Count == 0 && fixture.Permissions.Query(account, app, "saves") == PermissionDecision.Prompt);
        using var after = new CancellationTokenSource();
        Task<PermissionDecision> prompt = fixture.Permissions.RequestAsync(account, app, "saves", true, (_, _) => Task.FromResult(true), after.Token);
        var registered = await gate.Next(); after.Cancel();
        Check(!prompt.IsCompleted); registered.Release(); await Cancelled(prompt);
        Check(registered.Consumed && fixture.Permissions.Query(account, app, "saves") == PermissionDecision.Prompt);
        Check(fixture.Permissions.GetRevision(account, app, "saves") == 0 && fixture.Permissions.List(account).Count == 0);
        Check(!File.Exists(Path.Combine(fixture.Config, "application-permissions.v1.json")));
        await Set(fixture.Permissions, gate, account, app, PermissionDecision.Granted);
        Check(fixture.Permissions.GetRevision(account, app, "saves") == 1);
    }

    private static async Task AccountRecheck()
    {
        var gate = new ControlledGate(); using var fixture = new Fixture(gate);
        bool current = true; var account = Account(() => current); var app = App();
        Task write = fixture.Permissions.SetDecisionAsync(account, app, "saves", PermissionDecision.Granted);
        var operation = await gate.Next(); current = false; operation.Release();
        await RejectAsync(write, "SESSION_EXPIRED"); Check(operation.Consumed);
        current = true; Check(fixture.Permissions.Query(account, app, "saves") == PermissionDecision.Prompt);
        Check(fixture.Permissions.GetRevision(account, app, "saves") == 0);
    }

    private static async Task CancelledSettingsAndPreview()
    {
        var gate = new ControlledGate(); using var fixture = new Fixture(gate);
        var account = Account(); var app = App();
        using (var cancel = new CancellationTokenSource())
        {
            Task write = fixture.Permissions.SetDecisionAsync(account, app, "saves", PermissionDecision.Granted, cancel.Token);
            var held = await gate.Next(); cancel.Cancel(); held.Release(); await Cancelled(write);
            Check(held.Consumed && fixture.Permissions.GetRevision(account, app, "saves") == 0 && fixture.Permissions.List(account).Count == 0);
        }
        var preview = new DeveloperPreviewPermissions(fixture.Permissions, gate);
        var simulation = new DeveloperPreviewSimulation(gate);
        try
        {
            using (var cancel = new CancellationTokenSource())
            {
                Task deny = preview.SetDeniedAsync(true, account, app, cancel.Token);
                var held = await gate.Next(); cancel.Cancel(); held.Release(); await Cancelled(deny);
                Check(held.Consumed && !preview.Denied && preview.GetRevision(account, app) == 0);
            }
            using (var cancel = new CancellationTokenSource())
            {
                Task change = preview.SetDecisionAsync(account, app, "saves", PermissionDecision.Granted, cancel.Token);
                var held = await gate.Next(); cancel.Cancel(); held.Release(); await Cancelled(change);
                Check(held.Consumed && preview.Query(account, app, "saves") == PermissionDecision.Prompt && preview.GetRevision(account, app) == 0);
            }
            using (var cancel = new CancellationTokenSource())
            {
                Task offline = simulation.SetOfflineAsync(true, cancel.Token);
                var held = await gate.Next(); cancel.Cancel(); held.Release(); await Cancelled(offline);
                Check(held.Consumed && !simulation.Offline && simulation.Snapshot.AuthorityRevision == 0);
            }
            await Set(fixture.Permissions, gate, account, app, PermissionDecision.Granted);
            Check(fixture.Permissions.Query(account, app, "saves") == PermissionDecision.Granted);
        }
        finally { gate.AllowSynchronous = true; preview.Dispose(); simulation.Dispose(); }
    }

    private static async Task PreviewDecisions()
    {
        using var fixture = new Fixture();
        var gate = new ControlledGate(); var account = Account(); var app = App();
        fixture.Permissions.SetDecision(account, app, "saves", PermissionDecision.Granted);
        string store = Path.Combine(fixture.Config, "application-permissions.v1.json"); byte[] before = File.ReadAllBytes(store);
        var preview = new DeveloperPreviewPermissions(fixture.Permissions, gate);
        try
        {
            int notices = 0;
            preview.Changed += _ =>
            {
                Check(!gate.InCommit);
                Check(Task.Run(() => preview.GetRevision(account, app)).Wait(TimeSpan.FromSeconds(2)));
                notices++;
            };
            Reject(() => preview.SetDenied(true, account, app), "RUNTIME_ASYNC_AUTHORITY_REQUIRED");
            Reject(() => preview.SetDecision(account, app, "saves", PermissionDecision.Revoked), "RUNTIME_ASYNC_AUTHORITY_REQUIRED");
            Task deny = preview.SetDeniedAsync(true, account, app); var operation = await gate.Next(); ApplicationScope(operation, account, app);
            Check(!preview.Denied && !deny.IsCompleted); operation.Release(); await deny;
            Check(preview.Denied && notices == app.DeclaredPermissions.Count);
            long deniedRevision = preview.GetRevision(account, app, "saves");
            Task restore = preview.SetDeniedAsync(false, account, app); var restored = await gate.Next(); restored.Release(); await restore;
            Check(!preview.Denied && preview.GetRevision(account, app, "saves") > deniedRevision);
            await Set(preview, gate, account, app, PermissionDecision.Revoked);
            Check(fixture.Permissions.Query(account, app, "saves") == PermissionDecision.Granted && File.ReadAllBytes(store).SequenceEqual(before));
        }
        finally { gate.AllowSynchronous = true; preview.Dispose(); }
    }

    private static async Task PreviewStalePrompt()
    {
        using var fixture = new Fixture(); var gate = new ControlledGate();
        using var simulation = new DeveloperPreviewSimulation(); simulation.SwitchAccount("account-a");
        var account = simulation.BindAccount(CancellationToken.None); var app = App();
        var preview = new DeveloperPreviewPermissions(fixture.Permissions, gate);
        try
        {
            var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<PermissionDecision> prompt = preview.RequestAsync(account, app, "saves", true, (_, _) => answer.Task, CancellationToken.None);
            Task deny = preview.SetDeniedAsync(true, account, app); var first = await gate.Next(); first.Release(); await deny;
            Task restore = preview.SetDeniedAsync(false, account, app); var second = await gate.Next(); second.Release(); await restore;
            answer.SetResult(true); var stale = await gate.Next(); stale.Release();
            await RejectAsync(prompt, "PERMISSION_REVOKED"); Check(stale.Consumed);
            Check(preview.Query(account, app, "saves") == PermissionDecision.Prompt);
        }
        finally { gate.AllowSynchronous = true; preview.Dispose(); }
    }

    private static async Task SimulationRevision()
    {
        var gate = new ControlledGate(); var simulation = new DeveloperPreviewSimulation(gate);
        try
        {
            long previousRevision = 0; int notices = 0;
            simulation.Changed += (_, snapshot) =>
            {
                Check(!gate.InCommit && snapshot.AuthorityRevision > previousRevision);
                Check(Task.Run(() => simulation.Snapshot).Wait(TimeSpan.FromSeconds(2)));
                previousRevision = snapshot.AuthorityRevision; notices++;
            };
            Reject(() => simulation.SwitchAccount("account-a"), "RUNTIME_ASYNC_AUTHORITY_REQUIRED");
            Reject(() => simulation.SetOffline(true), "RUNTIME_ASYNC_AUTHORITY_REQUIRED");
            Check(gate.Count == 0 && simulation.Account == "live");
            Task select = simulation.SwitchAccountAsync("account-a"); var operation = await gate.Next();
            Check(operation.Scope.Kind == RuntimeMutationScopeKind.Identity && simulation.Account == "live");
            operation.Release(); await select;
            var account = simulation.BindAccount(CancellationToken.None); long epoch = account.Epoch;
            bool cancelledUnderPermit = false;
            using var registration = account.Invalidated.Register(() => cancelledUnderPermit = gate.InCommit);
            Task offline = simulation.SetOfflineAsync(true); var off = await gate.Next();
            Check(!simulation.Offline); off.Release(); await offline;
            Check(simulation.Offline && simulation.Snapshot.AuthorityRevision == 2 && simulation.Snapshot.SessionEpoch == epoch);
            account.EnsureCurrent(); Check(!account.Invalidated.IsCancellationRequested);
            Task switchAgain = simulation.SwitchAccountAsync("account-b"); var change = await gate.Next(); change.Release(); await switchAgain;
            Check(account.Invalidated.IsCancellationRequested && !cancelledUnderPermit && notices == 3);
        }
        finally { gate.AllowSynchronous = true; simulation.Dispose(); }
    }

    private static async Task SimulationCancellationAndDisposal()
    {
        var gate = new ControlledGate(); var simulation = new DeveloperPreviewSimulation(gate);
        try
        {
            using var before = new CancellationTokenSource(); before.Cancel();
            await Cancelled(simulation.SwitchAccountAsync("account-a", before.Token)); Check(gate.Count == 0);
            using var after = new CancellationTokenSource();
            Task select = simulation.SwitchAccountAsync("account-a", after.Token); var selected = await gate.Next(); after.Cancel();
            selected.Release(); await Cancelled(select);
            Check(selected.Consumed && simulation.Account == "live" && simulation.Snapshot.AuthorityRevision == 0);
            Task accepted = simulation.SwitchAccountAsync("account-a"); var next = await gate.Next(); next.Release(); await accepted;
            var account = simulation.BindAccount(CancellationToken.None);
            bool cancellationWasInside = true; using var registration = account.Invalidated.Register(() => cancellationWasInside = gate.InCommit);
            Reject(simulation.Dispose, "RUNTIME_ASYNC_AUTHORITY_REQUIRED");
            Task dispose = simulation.DisposeAsync().AsTask(); var retired = await gate.Next();
            Check(retired.Scope.Kind == RuntimeMutationScopeKind.Identity && !dispose.IsCompleted && !account.Invalidated.IsCancellationRequested);
            retired.Release(); await dispose;
            Check(retired.Consumed && account.Invalidated.IsCancellationRequested && !cancellationWasInside);
            Reject(() => _ = simulation.Snapshot, "SESSION_EXPIRED");
        }
        finally { gate.AllowSynchronous = true; simulation.Dispose(); }
    }

    private static async Task SimulationDisposedRecheck()
    {
        var gate = new ControlledGate(); var simulation = new DeveloperPreviewSimulation(gate);
        try
        {
            Task disposal = simulation.DisposeAsync().AsTask(); var first = await gate.Next();
            Task queued = simulation.SetOfflineAsync(true); var later = await gate.Next();
            first.Release(); await disposal; later.Release();
            await RejectAsync(queued, "SESSION_EXPIRED"); Check(first.Consumed && later.Consumed);
        }
        finally { gate.AllowSynchronous = true; simulation.Dispose(); }
    }

    private static async Task RevisionExhaustion()
    {
        var gate = new ControlledGate(); using var fixture = new Fixture(gate);
        var account = Account(); var app = App();
        await Set(fixture.Permissions, gate, account, app, PermissionDecision.Granted);
        string store = Path.Combine(fixture.Config, "application-permissions.v1.json");
        byte[] committed = File.ReadAllBytes(store);
        var persistentPermission = Private<Dictionary<string, long>>(fixture.Permissions, "revisions");
        var persistentBinding = Private<ConcurrentDictionary<string, long>>(fixture.Permissions, "bindingRevisions");
        string PermissionKey(string permission) => (string)typeof(PermissionService).GetMethod("Key", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [account.AccountKey, app.BindingKey, permission])!;
        string permissionKey = PermissionKey("saves"), bindingKey = PermissionKey("");
        persistentPermission[permissionKey] = long.MaxValue;
        Task failed = fixture.Permissions.SetDecisionAsync(account, app, "saves", PermissionDecision.Revoked);
        var held = await gate.Next(); held.Release(); await Overflowed(failed);
        Check(held.Consumed && fixture.Permissions.Query(account, app, "saves") == PermissionDecision.Granted);
        Check(persistentPermission[permissionKey] == long.MaxValue && persistentBinding[bindingKey] == 1 && File.ReadAllBytes(store).SequenceEqual(committed));
        persistentPermission[permissionKey] = 1; persistentBinding[bindingKey] = long.MaxValue;
        failed = fixture.Permissions.SetDecisionAsync(account, app, "saves", PermissionDecision.Denied);
        held = await gate.Next(); held.Release(); await Overflowed(failed);
        Check(held.Consumed && persistentPermission[permissionKey] == 1 && persistentBinding[bindingKey] == long.MaxValue);
        Check(fixture.Permissions.Query(account, app, "saves") == PermissionDecision.Granted && File.ReadAllBytes(store).SequenceEqual(committed));

        var preview = new DeveloperPreviewPermissions(fixture.Permissions, gate);
        var synthetic = new RuntimeAccountContext("developer-preview:revision-fixture:a", 1, false, () => true, CancellationToken.None);
        try
        {
            await Set(preview, gate, synthetic, app, PermissionDecision.Granted);
            var localPermission = Private<Dictionary<(string, string, string), long>>(preview, "permissionRevisions");
            var localBinding = Private<Dictionary<(string, string), long>>(preview, "revisions");
            var localKey = (synthetic.AccountKey, app.BindingKey, "saves"); var binding = (synthetic.AccountKey, app.BindingKey);
            localPermission[localKey] = long.MaxValue;
            failed = preview.SetDecisionAsync(synthetic, app, "saves", PermissionDecision.Revoked);
            held = await gate.Next(); held.Release(); await Overflowed(failed);
            Check(held.Consumed && preview.Query(synthetic, app, "saves") == PermissionDecision.Granted && localPermission[localKey] == long.MaxValue && localBinding[binding] == 1);
            localPermission[localKey] = 1; localBinding[binding] = long.MaxValue;
            failed = preview.SetDecisionAsync(synthetic, app, "saves", PermissionDecision.Denied);
            held = await gate.Next(); held.Release(); await Overflowed(failed);
            Check(held.Consumed && localPermission[localKey] == 1 && localBinding[binding] == long.MaxValue && preview.Query(synthetic, app, "saves") == PermissionDecision.Granted);
            localBinding[binding] = 1; localPermission[localKey] = long.MaxValue - 1;
            typeof(DeveloperPreviewPermissions).GetField("denialRevision", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(preview, 1L);
            Check(preview.GetRevision(synthetic, app, "saves") == long.MaxValue);
            failed = preview.SetDecisionAsync(synthetic, app, "saves", PermissionDecision.Revoked);
            held = await gate.Next(); held.Release(); await Overflowed(failed);
            Check(held.Consumed && localPermission[localKey] == long.MaxValue - 1 && localBinding[binding] == 1);
            failed = preview.SetDeniedAsync(true, synthetic, app);
            held = await gate.Next(); held.Release(); await Overflowed(failed);
            Check(held.Consumed && !preview.Denied && preview.GetRevision(synthetic, app, "saves") == long.MaxValue);
            Check(preview.Query(synthetic, app, "saves") == PermissionDecision.Granted && File.ReadAllBytes(store).SequenceEqual(committed));
            localPermission[localKey] = long.MaxValue;
            try { _ = preview.GetRevision(synthetic, app, "saves"); throw new InvalidOperationException("COMBINED_REVISION_WRAPPED"); }
            catch (OverflowException) { }
        }
        finally { gate.AllowSynchronous = true; preview.Dispose(); }
    }

    private static T Private<T>(object target, string name) where T : class =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;
    private static async Task Overflowed(Task action)
    { try { await action; } catch (OverflowException) { return; } throw new InvalidOperationException("EXPECTED_REVISION_OVERFLOW"); }

    private static async Task Set(IPermissionService service, ControlledGate gate, RuntimeAccountContext account, RuntimeApplication app, PermissionDecision decision)
    { Task write = service.SetDecisionAsync(account, app, "saves", decision); var operation = await gate.Next(); operation.Release(); await write; }
    private static void ApplicationScope(ControlledMutation mutation, RuntimeAccountContext account, RuntimeApplication app)
    { Check(mutation.Scope.Kind == RuntimeMutationScopeKind.Application && mutation.Scope.AccountKey == account.AccountKey && mutation.Scope.ApplicationBinding == app.BindingKey); }
    private static RuntimeApplication App() => new(new("cn.labchronicles.authorityfixture", null, null), "local:authority-fixture", "权限测试", ["saves", "identity.profile"]);
    private static RuntimeAccountContext Account(Func<bool>? current = null) => new("guest", 1, true, current ?? (() => true), CancellationToken.None);
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("PERMISSION_AUTHORITY_TEST_FAILED"); }
    private static void Reject(Action action, string code)
    { try { action(); } catch (RuntimeCapabilityException error) when (error.Code == code) { return; } throw new InvalidOperationException("EXPECTED_" + code); }
    private static async Task RejectAsync(Task action, string code)
    { try { await action; } catch (RuntimeCapabilityException error) when (error.Code == code) { return; } throw new InvalidOperationException("EXPECTED_" + code); }
    private static async Task Cancelled(Task action)
    { try { await action; } catch (OperationCanceledException) { return; } throw new InvalidOperationException("EXPECTED_CANCELLATION"); }

    private sealed class ControlledGate : IRuntimeAuthorityMutations
    {
        private readonly Channel<ControlledMutation> registered = Channel.CreateUnbounded<ControlledMutation>();
        private int count, committing;
        internal bool AllowSynchronous;
        internal int Count => Volatile.Read(ref count);
        internal bool InCommit => Volatile.Read(ref committing) != 0;
        internal Task<ControlledMutation> Next() => registered.Reader.ReadAsync().AsTask();
        public ValueTask<IRuntimeAuthorityMutation> BeginAsync(RuntimeMutationScope scope, CancellationToken beforeIntentCancellation = default)
        {
            beforeIntentCancellation.ThrowIfCancellationRequested();
            var operation = new ControlledMutation(this, scope); Interlocked.Increment(ref count);
            Check(registered.Writer.TryWrite(operation)); return ValueTask.FromResult<IRuntimeAuthorityMutation>(operation);
        }
        public bool TryCommitSynchronously(RuntimeMutationScope scope, Action shortCommit)
        {
            if (!AllowSynchronous) return false;
            Interlocked.Increment(ref committing); try { shortCommit(); } finally { Interlocked.Decrement(ref committing); }
            return true;
        }
        internal void Run(Action action)
        { Interlocked.Increment(ref committing); try { action(); } finally { Interlocked.Decrement(ref committing); } }
    }
    private sealed class ControlledMutation(ControlledGate owner, RuntimeMutationScope scope) : IRuntimeAuthorityMutation
    {
        private readonly TaskCompletionSource<bool> released = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int started;
        internal RuntimeMutationScope Scope { get; } = scope;
        internal bool Consumed { get; private set; }
        internal void Release() => released.SetResult(true);
        public async Task CommitAsync(Action shortCommit)
        {
            Check(Interlocked.CompareExchange(ref started, 1, 0) == 0);
            await released.Task.ConfigureAwait(false);
            try { owner.Run(shortCommit); } finally { Consumed = true; }
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "AutumnOS-PermissionAuthority-" + Guid.NewGuid().ToString("N"));
        internal string Config => Path.Combine(root, "Config");
        internal PermissionService Permissions { get; }
        internal Fixture(IRuntimeAuthorityMutations? gate = null)
        { Directory.CreateDirectory(root); Permissions = new(Config, authorityMutations: gate); }
        public void Dispose()
        {
            string prefix = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("FIXTURE_PATH_OUTSIDE_TEMP");
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
