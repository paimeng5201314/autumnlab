using System.Reflection;
using System.Runtime.Versioning;
using System.Threading.Channels;
using AutumnOS.Contracts;
using AutumnOS.Identity;

namespace AutumnOS.Tests;

/// <summary>Synthetic identity/vault/permit fixtures only; no native login, credentials or OS evidence.</summary>
[SupportedOSPlatform("windows")]
internal static class IdentityAuthorityMutationTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return Case("G2-B2 identity restore waits before vault/state and publishes outside commit", RestoreHeld);
        yield return Case("G2-B2 identity login fences both initial clear and verified persistence", LoginHeld);
        yield return Case("G2-B2 identity pending login cancellation settles its intent and permits retry", PendingLoginCancellation);
        yield return Case("G2-B2 identity remember decision waits before durable removal", RememberHeld);
        yield return Case("G2-B2 identity same-epoch SessionExpired fences and invalidates commit lease", SameEpochExpired);
        yield return Case("G2-B2 identity late refresh settles no-op after logout", LateRefresh);
        yield return Case("G2-B2 identity verified rotation survives cancellation while its fence is held", AcceptedRotationSurvivesCancellation);
        yield return Case("G2-B2 identity cancellation after intent waits for and consumes permit", CancellationSettles);
        yield return Case("G2-B2 identity epoch cancellation callbacks run outside monitor and permit", CancellationOutsideLocks);
        yield return Case("G2-B2 identity shutdown fences then drains actual ignored callback", ShutdownDrains);
        yield return Case("G2-B2 identity revision exhaustion fails closed without wrap", RevisionExhaustion);
    }
    private static (string Name, Action Run) Case(string name, Func<Task> body) =>
        (name, () => body().WaitAsync(TimeSpan.FromSeconds(12)).GetAwaiter().GetResult());
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static ProtectedIdentitySession Session() => IdentitySessionTests.Session();
    private static LogtoIdentityService Service(Vault vault, Flow flow, Mutations mutations) =>
        new(IdentitySessionTests.Options(), vault, flow, authorityMutations: mutations);
    private static async Task Dispose(LogtoIdentityService identity) => await identity.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));

    private static async Task RestoreHeld()
    {
        var gate = new Mutations(); var vault = new Vault { Value = Session() }; var flow = new Flow();
        var identity = Service(vault, flow, gate);
        bool notificationsOutside = true; int changed = 0;
        identity.Changed += (_, _) => { changed++; notificationsOutside &= gate.Executing == 0 && Task.Run(() => identity.Snapshot).Wait(TimeSpan.FromSeconds(2)); };
        try
        {
            gate.HoldNext(); Task<IdentitySnapshot> restore = identity.InitializeAsync(); Pending permit = await gate.Next();
            Check(vault.Reads == 0 && identity.Snapshot.AuthorityRevision == 0 && changed == 0 && !identity.LocalSessionReady.IsCompleted,
                "Held restoration touched vault, authority or callbacks.");
            await Task.Run(() => { using var lease = identity.EnterSessionLease("guest", 0); }).WaitAsync(TimeSpan.FromSeconds(2));
            permit.Release(); IdentitySnapshot result = await restore;
            Check(result.State == IdentitySessionState.SignedIn && result.AuthorityRevision == 2 && vault.Reads == 1 && notificationsOutside,
                "Restore/refresh revisions or out-of-lock notification failed.");
        }
        finally { gate.ReleaseAll(); await Dispose(identity); }
    }
    private static async Task LoginHeld()
    {
        var gate = new Mutations(); var vault = new Vault(); var flow = new Flow { HoldLogin = true }; var identity = Service(vault, flow, gate);
        try
        {
            gate.HoldNext(); Task<IdentitySnapshot> login = identity.SignInAsync(true); Pending start = await gate.Next();
            Check(vault.Clears == 0 && !flow.LoginStarted.Task.IsCompleted && identity.Snapshot.AuthorityRevision == 0, "Login escaped its initial fence.");
            start.Release(); await flow.LoginStarted.Task;
            Check(identity.Snapshot.State == IdentitySessionState.SigningIn && identity.Snapshot.AuthorityRevision == 1, "Initial login commit missing.");
            gate.HoldNext(); flow.LoginResult.TrySetResult(Session()); Pending verified = await gate.Next();
            Check(vault.Writes == 0 && identity.Snapshot.State == IdentitySessionState.SigningIn, "Verified login persisted before permit.");
            verified.Release(); IdentitySnapshot result = await login;
            Check(result.State == IdentitySessionState.SignedIn && result.AuthorityRevision == 2 && vault.Value is not null && vault.Writes == 1, "Verified login commit missing.");
        }
        finally { gate.ReleaseAll(); flow.LoginResult.TrySetResult(Session()); await Dispose(identity); }
    }
    private static async Task PendingLoginCancellation()
    {
        var gate = new Mutations(); var vault = new Vault(); var flow = new Flow(); var identity = Service(vault, flow, gate);
        try
        {
            IdentitySnapshot before = identity.Snapshot;
            gate.HoldNext(); Task<IdentitySnapshot> login = identity.SignInAsync(true); Pending permit = await gate.Next();
            identity.CancelSignIn();
            Check(!login.IsCompleted && !permit.Settled.Task.IsCompleted && !flow.LoginStarted.Task.IsCompleted,
                "Pending login canceled by abandoning its intent or starting the flow.");
            permit.Release(); IdentitySnapshot canceled = await login; await permit.Settled.Task;
            Check(canceled == before && !flow.LoginStarted.Task.IsCompleted && vault.Clears == 0 && vault.Writes == 0,
                "Canceled initial login changed authority, credentials or opened the native flow.");
            IdentitySnapshot retried = await identity.SignInAsync(true);
            Check(retried.State == IdentitySessionState.SignedIn && flow.LoginStarted.Task.IsCompleted && vault.Writes == 1,
                "Canceled pending owner prevented a fresh login from succeeding.");
        }
        finally { gate.ReleaseAll(); await Dispose(identity); }
    }
    private static async Task RememberHeld()
    {
        var gate = new Mutations(); var vault = new Vault(); var identity = Service(vault, new Flow(), gate);
        try
        {
            await identity.SignInAsync(true); IdentitySnapshot before = identity.Snapshot; int clears = vault.Clears;
            gate.HoldNext(); Task<IdentitySnapshot> change = identity.SetRememberSignInAsync(false); Pending permit = await gate.Next();
            Check(identity.Snapshot == before && vault.Clears == clears && vault.Value is not null, "Remember preference changed before fence.");
            permit.Release(); IdentitySnapshot after = await change;
            Check(!after.RememberSignIn && after.AuthorityRevision == before.AuthorityRevision + 1 && vault.Value is null, "Remember removal not committed.");
        }
        finally { gate.ReleaseAll(); await Dispose(identity); }
    }
    private static async Task SameEpochExpired()
    {
        var gate = new Mutations(); var vault = new Vault(); var flow = new Flow(); var identity = Service(vault, flow, gate);
        try
        {
            await identity.SignInAsync(true); IdentitySnapshot before = identity.Snapshot;
            flow.RefreshError = "AUTH_INVALID_TOKEN";
            gate.HoldNext(); Task<IdentitySnapshot> refresh = identity.RefreshAsync(); Pending permit = await gate.Next();
            Check(identity.Snapshot == before && vault.Value is not null, "Refresh invalidated state or credentials before fence.");
            using (identity.EnterSessionLease(before.AccountNamespace, before.SessionEpoch)) { }
            permit.Release(); IdentitySnapshot after = await refresh;
            Check(after.State == IdentitySessionState.SessionExpired && after.SessionEpoch == before.SessionEpoch && after.AccountNamespace == before.AccountNamespace &&
                after.AuthorityRevision == before.AuthorityRevision + 1 && vault.Value is null, "Same-epoch expiry missing its revision or durable commit.");
            try { using var lease = identity.EnterSessionLease(after.AccountNamespace, after.SessionEpoch); throw new InvalidOperationException("Expired same-epoch lease survived."); }
            catch (RuntimeCapabilityException error) when (error.Code == "SESSION_EXPIRED") { }
        }
        finally { gate.ReleaseAll(); await Dispose(identity); }
    }
    private static async Task LateRefresh()
    {
        var gate = new Mutations(); var vault = new Vault(); var flow = new Flow { HoldRefresh = true }; var identity = Service(vault, flow, gate);
        try
        {
            await identity.SignInAsync(true); Task<IdentitySnapshot> refresh = identity.RefreshAsync(); await flow.RefreshStarted.Task;
            IdentitySnapshot logout = await identity.SignOutAsync(); int writes = vault.Writes;
            gate.HoldNext(); flow.RefreshResult.TrySetResult(Session() with { RefreshToken = "fixture-late-rotation", DisplayName = "Late fixture" }); Pending permit = await gate.Next();
            permit.Release(); await refresh;
            Check(identity.Snapshot == logout && vault.Value is null && vault.Writes == writes, "A stale refresh replaced logout or persisted credentials.");
        }
        finally { gate.ReleaseAll(); flow.RefreshResult.TrySetResult(Session()); await Dispose(identity); }
    }
    private static async Task AcceptedRotationSurvivesCancellation()
    {
        var gate = new Mutations(); var vault = new Vault(); var flow = new Flow { HoldRefresh = true }; var identity = Service(vault, flow, gate);
        try
        {
            await identity.SignInAsync(true); IdentitySnapshot before = identity.Snapshot;
            ProtectedIdentitySession rotated = Session() with { RefreshToken = "fixture-accepted-rotation", AccessToken = "fixture-accepted-access" };
            using var cancel = new CancellationTokenSource();
            Task<IdentitySnapshot> refresh = identity.RefreshAsync(cancel.Token); await flow.RefreshStarted.Task;
            gate.HoldNext(); flow.RefreshResult.TrySetResult(rotated); Pending permit = await gate.Next();
            cancel.Cancel();
            Check(!refresh.IsCompleted && identity.Snapshot == before && vault.Value?.RefreshToken != rotated.RefreshToken,
                "Accepted rotation changed authority before its fence or abandoned the intent.");
            permit.Release(); IdentitySnapshot after = await refresh;
            Check(after.State == IdentitySessionState.SignedIn && after.SessionEpoch == before.SessionEpoch &&
                after.AccountNamespace == before.AccountNamespace && after.AuthorityRevision == before.AuthorityRevision + 1 &&
                vault.Value?.RefreshToken == rotated.RefreshToken && vault.Value?.AccessToken == rotated.AccessToken,
                "Caller cancellation discarded verified provider rotation or changed its account scope.");
            await permit.Settled.Task;
        }
        finally { gate.ReleaseAll(); flow.RefreshResult.TrySetResult(Session()); await Dispose(identity); }
    }
    private static async Task CancellationSettles()
    {
        var gate = new Mutations(); var vault = new Vault(); var identity = Service(vault, new Flow(), gate);
        try
        {
            await identity.SignInAsync(true); IdentitySnapshot before = identity.Snapshot; int clears = vault.Clears;
            using var cancel = new CancellationTokenSource();
            gate.HoldNext(); Task<IdentitySnapshot> change = identity.SetRememberSignInAsync(false, cancel.Token); Pending permit = await gate.Next();
            cancel.Cancel();
            Check(!change.IsCompleted && !permit.Settled.Task.IsCompleted, "Intent was abandoned when its caller canceled.");
            permit.Release();
            try { await change; throw new InvalidOperationException("Canceled preference unexpectedly committed."); } catch (OperationCanceledException) { }
            await permit.Settled.Task;
            Check(identity.Snapshot == before && vault.Clears == clears, "Canceled settlement mutated current identity.");
        }
        finally { gate.ReleaseAll(); await Dispose(identity); }
    }
    private static async Task CancellationOutsideLocks()
    {
        var gate = new Mutations(); var vault = new Vault(); var flow = new Flow { HoldLogin = true }; var identity = Service(vault, flow, gate);
        bool outside = false;
        try
        {
            Task<IdentitySnapshot> login = identity.SignInAsync(true); await flow.LoginStarted.Task;
            using var observed = flow.LoginToken.Register(() => outside = gate.Executing == 0 &&
                Task.Run(() => identity.Snapshot).Wait(TimeSpan.FromSeconds(2)));
            await identity.SignOutAsync();
            Check(outside, "Epoch cancellation ran while identity monitor or mutation permit was held.");
            flow.LoginResult.TrySetResult(Session()); await login;
            Check(identity.Snapshot.State == IdentitySessionState.SignedOut && vault.Writes == 0, "Canceled late login authenticated.");
        }
        finally { gate.ReleaseAll(); flow.LoginResult.TrySetResult(Session()); await Dispose(identity); }
    }
    private static async Task ShutdownDrains()
    {
        var gate = new Mutations(); var vault = new Vault(); var flow = new Flow { HoldLogin = true }; var identity = Service(vault, flow, gate);
        try
        {
            Task<IdentitySnapshot> login = identity.SignInAsync(true); await flow.LoginStarted.Task;
            IdentitySnapshot before = identity.Snapshot;
            gate.HoldNext(); Task shutdown = identity.DisposeAsync().AsTask(); Pending permit = await gate.Next();
            Check(!shutdown.IsCompleted && !flow.LoginCancellation.Task.IsCompleted && identity.Snapshot == before, "Shutdown canceled or changed authority before its fence.");
            permit.Release(); await flow.LoginCancellation.Task;
            Check(!shutdown.IsCompleted, "Shutdown treated cancellation as actual callback completion.");
            flow.LoginResult.TrySetResult(Session()); await login; await shutdown;
            Check(vault.Writes == 0 && identity.Snapshot.State == IdentitySessionState.SignedOut && gate.Executing == 0, "Shutdown accepted a late identity or retained its permit.");
        }
        finally { gate.ReleaseAll(); flow.LoginResult.TrySetResult(Session()); await Dispose(identity); }
    }
    private static async Task RevisionExhaustion()
    {
        var gate = new Mutations(); var vault = new Vault(); var identity = Service(vault, new Flow(), gate);
        try
        {
            await identity.SignInAsync(true);
            typeof(LogtoIdentityService).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(identity,
                identity.Snapshot with { AuthorityRevision = long.MaxValue });
            try { await identity.SetRememberSignInAsync(false); throw new InvalidOperationException("Revision exhaustion silently committed."); }
            catch (RuntimeCapabilityException error) when (error.Code == "SESSION_EXPIRED") { }
            Check(identity.Snapshot.State == IdentitySessionState.SessionExpired && identity.Snapshot.AuthorityRevision == long.MaxValue &&
                identity.Snapshot.DisplayName is null, "Revision wrapped or left private identity exposed.");
            try { using var lease = identity.EnterSessionLease(identity.Snapshot.AccountNamespace, identity.Snapshot.SessionEpoch); throw new InvalidOperationException("Exhausted authority accepted a lease."); }
            catch (ObjectDisposedException) { }
        }
        finally { gate.ReleaseAll(); try { await Dispose(identity); } catch (AggregateException) { /* Exhaustion remains an explicit terminal failure; resources still drain. */ } }
    }

    private sealed class Vault : ICredentialVault
    {
        internal ProtectedIdentitySession? Value;
        internal int Reads, Writes, Clears;
        public ProtectedIdentitySession? Read() { Reads++; return Value; }
        public void Write(ProtectedIdentitySession value) { Writes++; Value = value; }
        public void Clear() { Clears++; Value = null; }
    }
    private sealed class Flow : INativeOidcFlow
    {
        internal bool HoldLogin, HoldRefresh;
        internal string? RefreshError;
        internal CancellationToken LoginToken;
        internal readonly TaskCompletionSource LoginStarted = NewCompletion(), RefreshStarted = NewCompletion(), LoginCancellation = NewCompletion();
        internal readonly TaskCompletionSource<ProtectedIdentitySession> LoginResult = NewCompletion<ProtectedIdentitySession>(), RefreshResult = NewCompletion<ProtectedIdentitySession>();
        public async Task<ProtectedIdentitySession> SignInAsync(bool remember, bool reauthenticate, CancellationToken cancellationToken)
        {
            LoginToken = cancellationToken;
            using var registration = cancellationToken.Register(() => LoginCancellation.TrySetResult());
            LoginStarted.TrySetResult();
            return HoldLogin ? await LoginResult.Task.ConfigureAwait(false) : Session();
        }
        public async Task<ProtectedIdentitySession> RefreshAsync(ProtectedIdentitySession session, CancellationToken cancellationToken)
        {
            RefreshStarted.TrySetResult();
            if (HoldRefresh) return await RefreshResult.Task.ConfigureAwait(false);
            if (RefreshError is not null) throw new IdentityFlowException(RefreshError);
            cancellationToken.ThrowIfCancellationRequested();
            return session with { RefreshToken = "fixture-rotated", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        }
        public Task SignOutBrowserAsync(ProtectedIdentitySession session, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class Mutations : IRuntimeAuthorityMutations
    {
        private int holdNext;
        private readonly Channel<Pending> pending = Channel.CreateUnbounded<Pending>();
        private readonly List<Pending> held = [];
        internal int Executing;
        internal void HoldNext() { Check(Interlocked.CompareExchange(ref holdNext, 1, 0) == 0, "A held mutation was already requested."); }
        internal Task<Pending> Next() => pending.Reader.ReadAsync().AsTask();
        internal void ReleaseAll() { Interlocked.Exchange(ref holdNext, 0); lock (held) foreach (Pending value in held) value.Release(); }
        public ValueTask<IRuntimeAuthorityMutation> BeginAsync(RuntimeMutationScope scope, CancellationToken beforeIntentCancellation = default)
        {
            beforeIntentCancellation.ThrowIfCancellationRequested();
            Check(scope.Kind == RuntimeMutationScopeKind.Identity, "Identity requested another authority scope.");
            bool hold = Interlocked.Exchange(ref holdNext, 0) != 0;
            var value = new Pending(this, hold);
            if (hold)
            {
                lock (held) held.Add(value);
                Check(pending.Writer.TryWrite(value), "Fixture could not publish owned intent.");
            }
            return ValueTask.FromResult<IRuntimeAuthorityMutation>(value);
        }
        public bool TryCommitSynchronously(RuntimeMutationScope scope, Action shortCommit) => false;
    }
    private sealed class Pending(Mutations owner, bool hold) : IRuntimeAuthorityMutation
    {
        private readonly TaskCompletionSource ready = NewCompletion();
        internal readonly TaskCompletionSource Settled = NewCompletion();
        private int claimed;
        internal void Release() => ready.TrySetResult();
        public async Task CommitAsync(Action shortCommit)
        {
            Check(Interlocked.Exchange(ref claimed, 1) == 0, "Fixture permit was reused.");
            if (hold) await ready.Task.ConfigureAwait(false);
            Interlocked.Increment(ref owner.Executing);
            try { shortCommit(); }
            finally { Interlocked.Decrement(ref owner.Executing); Settled.TrySetResult(); }
        }
    }
    private static TaskCompletionSource NewCompletion() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskCompletionSource<T> NewCompletion<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
