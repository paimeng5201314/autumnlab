using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AutumnOS.Contracts;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

// Controlled in-process authority permits and synthetic identity snapshots only.
// No native peer, ACK decoder, process retirement or OS isolation proof is supplied.
internal static class RuntimeAuthorityMutationTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return Case("lifecycle_changes_wait_for_permit_and_old_stamps_cannot_post", LifecyclePermits);
        yield return Case("guest_prompt_decision_and_regrant_are_instance_fenced", GuestDecisions);
        yield return Case("guest_prompt_cancel_after_intent_settles_without_grant", GuestPromptCancellation);
        yield return Case("close_intent_quarantines_then_commits_once_after_fake_release", PhasedClose);
        yield return Case("failed_close_is_owned_drained_and_never_formal_retirement", FailedClose);
        yield return Case("same_epoch_expiry_and_profile_changes_reject_old_private_replies", SameEpochIdentity);
        yield return Case("request_profile_own_grant_and_refresh_freeze_the_result_snapshot", OwnGrantAndRefresh);
        yield return Case("account_invalidation_owns_close_and_waits_for_host_completion", AccountInvalidation);
    }
    private static (string, Action) Case(string name, Func<Task> run) =>
        ("g2b2.runtime.pure_fake_" + name, () => run().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult());

    private static Task LifecyclePermits() => InTemp(async root =>
    {
        var gate = new Gate { AllowSync = true };
        var session = Guest(root, gate); gate.AllowSync = false;
        try
        {
            ExpectCode("RUNTIME_ASYNC_AUTHORITY_REQUIRED", session.Background);
            Check(session.Instance.State == AppLifecycleState.Foreground);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Cancelled(session.BackgroundAsync(cancelled.Token)); Check(gate.Begins == 0);
            foreach ((AppLifecycleState desired, Func<Task> change) in new (AppLifecycleState, Func<Task>)[]
            {
                (AppLifecycleState.Background, () => session.BackgroundAsync()),
                (AppLifecycleState.Foreground, () => session.ForegroundAsync()),
                (AppLifecycleState.Suspended, () => session.SuspendAsync()),
                (AppLifecycleState.Foreground, () => session.ResumeAsync())
            })
            {
                var before = session.CaptureInstanceDelivery();
                Task work = change(); Pending intent = await gate.Next(); await intent.CommitRequested.Task;
                Check(session.Instance == before.Instance && !work.IsCompleted);
                Check(intent.Scope.Kind == RuntimeMutationScopeKind.Instance && intent.Scope.InstanceId == session.Instance.Id.Value);
                Check(intent.Scope.AccountKey == "guest" && intent.Scope.ApplicationBinding == session.AuthorityMutationScope.ApplicationBinding);
                intent.Release.TrySetResult(true); await work;
                Check(session.Instance.State == desired && intent.Commits == 1);
                Check(!session.DeliverEvent("lifecycle.changed", before.Stamp, () => throw new InvalidOperationException("stale post")));
                int posted = 0; var current = session.CaptureInstanceDelivery();
                Check(session.DeliverEvent("lifecycle.changed", current.Stamp, () => posted++)); Check(posted == 1);
            }
        }
        finally { gate.AllowSync = true; session.Close(); }
    });

    private static Task GuestDecisions() => InTemp(async root =>
    {
        var gate = new Gate { AllowSync = true }; var session = Guest(root, gate); gate.AllowSync = false;
        try
        {
            RuntimeResponseStamp before = session.CaptureDeliveryStamp();
            Task<RuntimePreparedResponse> denial = Send(session, "permissions.request", new { name = "saves" }, (_, _) => Task.FromResult(false));
            Pending deny = await gate.Next(); await deny.CommitRequested.Task;
            Check(!denial.IsCompleted && session.CaptureDeliveryStamp().GuestPermission == before.GuestPermission);
            deny.Release.TrySetResult(true); RuntimePreparedResponse denied = await denial;
            Check(Ok(denied.Response).GetProperty("state").GetString() == "denied");
            Task grant = session.SetGuestSavePermissionAsync(PermissionDecision.Granted);
            Pending grantIntent = await gate.Next(); await grantIntent.CommitRequested.Task;
            Check(!grant.IsCompleted && session.RevalidateResponse("permissions.request", denied.Stamp, denied.Response) == denied.Response);
            grantIntent.Release.TrySetResult(true); await grant;
            Error(session.RevalidateResponse("permissions.request", denied.Stamp, denied.Response), "PERMISSION_REVOKED");
            RuntimePreparedResponse read = await Send(session, "saves.read", new { slot = "game" }); Ok(read.Response);
            Task revoke = session.RevokeSavePermissionAsync(); Pending revokeIntent = await gate.Next();
            revokeIntent.Release.TrySetResult(true); await revoke;
            Task regrant = session.SetGuestSavePermissionAsync(PermissionDecision.Granted); Pending regrantIntent = await gate.Next();
            regrantIntent.Release.TrySetResult(true); await regrant;
            int posted = 0;
            Error(session.DeliverResponse("saves.read", read.Stamp, read.Response, _ => posted++), "PERMISSION_REVOKED"); Check(posted == 0);
        }
        finally { gate.AllowSync = true; session.Close(); }
    });

    private static Task GuestPromptCancellation() => InTemp(async root =>
    {
        var gate = new Gate { AllowSync = true }; var session = Guest(root, gate); gate.AllowSync = false;
        try
        {
            using var cancel = new CancellationTokenSource();
            RuntimeResponseStamp before = session.CaptureDeliveryStamp();
            Task<RuntimePreparedResponse> pending = Send(session, "permissions.request", new { name = "saves" }, (_, _) => Task.FromResult(true), cancel.Token);
            Pending intent = await gate.Next(); await intent.CommitRequested.Task;
            cancel.Cancel(); Check(!pending.IsCompleted);
            intent.Release.TrySetResult(true);
            Error((await pending).Response, "USER_CANCELLED"); Check(intent.Commits == 1);
            Check(session.CaptureDeliveryStamp().GuestPermission == before.GuestPermission);
            Error((await Send(session, "saves.read", new { slot = "game" })).Response, "PERMISSION_DENIED");
        }
        finally { gate.AllowSync = true; session.Close(); }
    });

    private static Task PhasedClose() => InTemp(async root =>
    {
        var gate = new Gate { AllowSync = true }; var session = Guest(root, gate); gate.AllowSync = false;
        var before = session.CaptureInstanceDelivery(); int closeNotifications = 0; session.NativeCloseRequired += () => closeNotifications++;
        RuntimeSession.RuntimeCloseOperation operation = await session.BeginCloseAsync(); Pending intent = await gate.Next();
        Check(ReferenceEquals(operation, await session.BeginCloseAsync()));
        Task completed = operation.CompleteAsync(); await intent.CommitRequested.Task;
        Check(ReferenceEquals(completed, operation.CompleteAsync()) && !completed.IsCompleted);
        Check(session.Instance == before.Instance && closeNotifications == 1);
        Check(!session.DeliverEvent("lifecycle.changed", before.Stamp, () => throw new InvalidOperationException("quarantined post")));
        Task drain = session.DrainAuthorityOperationsAsync(); Check(!drain.IsCompleted);
        intent.Release.TrySetResult(true); await completed; await drain;
        Check(session.Instance.State == AppLifecycleState.Closed && session.Instance.Epoch.Value == before.Instance.Epoch.Value + 1);
        Check(intent.Commits == 1 && !intent.Release.TrySetResult(true));
        Check(ReferenceEquals(completed, operation.CompleteAsync()));
    });

    private static Task FailedClose() => InTemp(async root =>
    {
        var gate = new Gate { AllowSync = true }; var session = Guest(root, gate); gate.AllowSync = false;
        RuntimeSession.RuntimeCloseOperation operation = await session.BeginCloseAsync(); Pending intent = await gate.Next();
        Task completion = operation.CompleteAsync(); await intent.CommitRequested.Task;
        intent.Release.TrySetException(new InvalidOperationException("FAKE_NATIVE_FENCE_FAILURE"));
        await Fails<InvalidOperationException>(completion);
        Check(session.Instance.State == AppLifecycleState.Foreground);
        await Fails<InvalidOperationException>(session.DrainAuthorityOperationsAsync());
        Check(!session.DeliverEvent("lifecycle.changed", session.CaptureDeliveryStamp(), () => throw new InvalidOperationException("failed close post")));
        // No successful retirement is invented to clean up this deliberately faulted fake.
    });

    private static Task SameEpochIdentity() => InTemp(async root =>
    {
        var identity = new Identity(Snapshot("first", 1)); var permission = new PermissionService(root);
        RuntimeSessionServices services = Services(permission, identity);
        permission.SetDecision(services.Account, services.Application, "identity.profile", PermissionDecision.Granted);
        var session = new RuntimeSession("test.authority", ["identity.profile"], root, services: services); session.Foreground();
        try
        {
            RuntimePreparedResponse reply = await Send(session, "identity.getProfile"); Ok(reply.Response);
            var notifications = new List<RuntimeSdkEvent>(); session.SdkEventReady += notifications.Add;
            IdentitySnapshot second = Snapshot("second", 2); identity.Update(second);
            RuntimeSdkEvent oldEvent = notifications.Single(); Check(oldEvent.Stamp is not null);
            identity.Update(Snapshot("third", 3)); RuntimeSdkEvent currentEvent = notifications.Last();
            int posts = 0;
            Error(session.DeliverResponse("identity.getProfile", reply.Stamp, reply.Response, _ => posts++), "SESSION_EXPIRED");
            Check(!session.DeliverEvent(oldEvent.Name, oldEvent.Stamp!, () => posts++));
            Check(session.DeliverEvent(currentEvent.Name, currentEvent.Stamp!, () => posts++)); Check(posts == 1);
            long revision = session.EventRevision; identity.EmitObsolete(second); Check(session.EventRevision == revision);
            identity.Update(Snapshot("third", 4) with { State = IdentitySessionState.SessionExpired });
            Check(session.Instance.State == AppLifecycleState.Foreground);
            Check(!session.DeliverEvent(currentEvent.Name, currentEvent.Stamp!, () => posts++));
            Error((await Send(session, "identity.getProfile")).Response, "SESSION_EXPIRED");
        }
        finally { session.Close(); }
    });

    private static Task OwnGrantAndRefresh() => InTemp(async root =>
    {
        var identity = new Identity(Snapshot("before-prompt", 1)); var permission = new PermissionService(root);
        RuntimeSessionServices services = Services(permission, identity, prompt: (_, _) =>
        { identity.Update(Snapshot("fresh-after-prompt", 2)); return Task.FromResult(true); });
        var session = new RuntimeSession("test.authority", ["identity.profile"], root, services: services); session.Foreground();
        try
        {
            session.HostUserGesture(); RuntimePreparedResponse reply = await Send(session, "identity.requestProfile");
            Check(Ok(reply.Response).GetProperty("displayName").GetString() == "fresh-after-prompt");
            int posts = 0; Ok(session.DeliverResponse("identity.requestProfile", reply.Stamp, reply.Response, _ => posts++)); Check(posts == 1);
            identity.Update(Snapshot("newer", 3));
            Error(session.DeliverResponse("identity.requestProfile", reply.Stamp, reply.Response, _ => posts++), "SESSION_EXPIRED"); Check(posts == 1);
        }
        finally { session.Close(); }
    });

    private static Task AccountInvalidation() => InTemp(async root =>
    {
        using var invalidated = new CancellationTokenSource(); var gate = new Gate { AllowSync = true };
        var identity = new Identity(Snapshot("owned", 1)); var permission = new PermissionService(root);
        RuntimeSessionServices services = Services(permission, identity, gate, invalidated.Token);
        var session = new RuntimeSession("test.authority", ["identity.profile"], root, services: services); session.Foreground(); gate.AllowSync = false;
        int requests = 0; session.NativeCloseRequired += () => requests++;
        RuntimeResponseStamp before = session.CaptureDeliveryStamp(); invalidated.Cancel();
        Pending intent = await gate.Next(); await intent.CommitRequested.Task;
        Check(requests == 1 && session.Instance.State == AppLifecycleState.Foreground);
        Check(!session.DeliverEvent("lifecycle.changed", before, () => throw new InvalidOperationException("invalidated post")));
        Task drain = session.DrainAuthorityOperationsAsync(); Check(!drain.IsCompleted);
        intent.Release.TrySetResult(true); await drain;
        Check(session.Instance.State == AppLifecycleState.Closed && intent.Commits == 1);
    });

    private static RuntimeSession Guest(string root, Gate gate)
    { var session = new RuntimeSession("test.authority", ["saves"], root, authorityMutations: gate); session.Foreground(); return session; }
    private static RuntimeSessionServices Services(IPermissionService permission, Identity identity, Gate? gate = null,
        CancellationToken invalidated = default, Func<RuntimePermissionPrompt, CancellationToken, Task<bool>>? prompt = null) => new()
    {
        Account = new("account-a", 7, false, () => true, invalidated),
        Application = new(new("test.authority", 42, null), "controlled-package", "Pure fake authority app", ["identity.profile"]),
        Permissions = permission, Identity = identity, AuthorityMutations = gate,
        RequestPermission = prompt ?? ((_, _) => Task.FromResult(true))
    };
    private static IdentitySnapshot Snapshot(string display, long revision) => new(IdentitySessionState.SignedIn,
        "account-a", 7, display, null, false, false, null, revision);
    private static Task<RuntimePreparedResponse> Send(RuntimeSession session, string method, object? parameters = null,
        Func<string, CancellationToken, Task<bool>>? prompt = null, CancellationToken cancellation = default) => session.HandleMessageForDeliveryAsync(session.PageUri,
        JsonSerializer.Serialize(new { protocolVersion = 1, requestId = Guid.NewGuid().ToString("N"), method, @params = parameters ?? new { } }),
        prompt ?? ((_, _) => Task.FromResult(false)), cancellation);

    private sealed class Gate : IRuntimeAuthorityMutations
    {
        private readonly Channel<Pending> pending = Channel.CreateUnbounded<Pending>();
        internal bool AllowSync;
        internal int Begins;
        public ValueTask<IRuntimeAuthorityMutation> BeginAsync(RuntimeMutationScope scope, CancellationToken beforeIntentCancellation = default)
        {
            beforeIntentCancellation.ThrowIfCancellationRequested(); Interlocked.Increment(ref Begins);
            var operation = new Pending(scope); Check(pending.Writer.TryWrite(operation));
            return ValueTask.FromResult<IRuntimeAuthorityMutation>(operation);
        }
        public bool TryCommitSynchronously(RuntimeMutationScope scope, Action shortCommit)
        { if (!AllowSync) return false; shortCommit(); return true; }
        internal Task<Pending> Next() => pending.Reader.ReadAsync().AsTask();
    }
    private sealed class Pending(RuntimeMutationScope scope) : IRuntimeAuthorityMutation
    {
        internal RuntimeMutationScope Scope { get; } = scope;
        internal TaskCompletionSource<bool> Release { get; } = Signal();
        internal TaskCompletionSource<bool> CommitRequested { get; } = Signal();
        internal int Commits;
        public async Task CommitAsync(Action shortCommit)
        {
            if (Interlocked.Increment(ref Commits) != 1) throw new InvalidOperationException("FAKE_PERMIT_REPLAY");
            CommitRequested.TrySetResult(true); await Release.Task; shortCommit();
        }
    }
    private sealed class Identity(IdentitySnapshot snapshot) : IIdentityService
    {
        public IdentitySnapshot Snapshot { get; private set; } = snapshot;
        public event EventHandler<IdentitySnapshot>? Changed;
        internal void Update(IdentitySnapshot value) { Snapshot = value; Changed?.Invoke(this, value); }
        internal void EmitObsolete(IdentitySnapshot value) => Changed?.Invoke(this, value);
        public Task<IdentitySnapshot> InitializeAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task<IdentitySnapshot> SignInAsync(bool rememberSignIn, bool reauthenticate = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IdentitySnapshot> SetRememberSignInAsync(bool rememberSignIn, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IdentitySnapshot> RefreshAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IdentitySnapshot> SignOutAsync(bool browserSession = false, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void CancelSignIn() { }
        public IDisposable EnterSessionLease(string accountNamespace, long sessionEpoch)
        { if (Snapshot.AccountNamespace != accountNamespace || Snapshot.SessionEpoch != sessionEpoch) throw new RuntimeCapabilityException("SESSION_EXPIRED"); return new Lease(); }
        private sealed class Lease : IDisposable { public void Dispose() { } }
    }
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task InTemp(Func<string, Task> run)
    {
        string directory = Path.Combine(Path.GetTempPath(), "AutumnOS-authority-fakes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { await run(directory); }
        finally { Directory.Delete(directory, true); }
    }
    private static JsonElement Ok(string response)
    { using var value = JsonDocument.Parse(response); Check(value.RootElement.GetProperty("ok").GetBoolean()); return value.RootElement.GetProperty("result").Clone(); }
    private static void Error(string response, string code)
    { using var value = JsonDocument.Parse(response); Check(!value.RootElement.GetProperty("ok").GetBoolean() && value.RootElement.GetProperty("error").GetProperty("code").GetString() == code); }
    private static void ExpectCode(string code, Action action)
    { try { action(); } catch (RuntimeCapabilityException error) { Check(error.Code == code); return; } throw new InvalidOperationException("EXPECTED_" + code); }
    private static async Task Cancelled(Task task)
    { try { await task; } catch (OperationCanceledException) { return; } throw new InvalidOperationException("EXPECTED_CANCELLED"); }
    private static async Task Fails<T>(Task task) where T : Exception
    { try { await task; } catch (T) { return; } throw new InvalidOperationException("EXPECTED_" + typeof(T).Name); }
    private static void Check(bool value, [CallerArgumentExpression(nameof(value))] string? expression = null)
    { if (!value) throw new InvalidOperationException("RUNTIME_AUTHORITY_FAKE_ASSERTION: " + expression); }
}
