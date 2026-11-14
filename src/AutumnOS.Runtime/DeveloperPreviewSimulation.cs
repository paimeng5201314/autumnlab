using AutumnOS.Contracts;

namespace AutumnOS.Runtime;

/// <summary>Explicit host-only test state. It cannot issue credentials or mutate the real identity service.</summary>
public sealed class DeveloperPreviewSimulation : IIdentityService, IDisposable, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IRuntimeAuthorityMutations? authorityMutations;
    private readonly string scope = Guid.NewGuid().ToString("N");
    private CancellationTokenSource epochLifetime = new();
    private CancellationTokenSource networkLifetime = new();
    private long epoch;
    private long authorityRevision;
    private bool disposed;
    private string account = "live";
    private bool offline;
    public DeveloperPreviewSimulation(IRuntimeAuthorityMutations? authorityMutations = null)
    { this.authorityMutations = authorityMutations; }
    public string Account { get { lock (gate) return account; } }
    public bool Offline { get { lock (gate) return offline; } }
    /// <summary>Every live offline-policy transition invalidates the previous network
    /// generation, including an off/on cycle without an account epoch change.</summary>
    public CancellationToken NetworkPolicyInvalidated { get { lock (gate) { EnsureActive(); return networkLifetime.Token; } } }
    public IDisposable EnterNetworkPolicyLease(CancellationToken expectedGeneration)
    {
        Monitor.Enter(gate);
        try
        {
            EnsureActive();
            if (expectedGeneration.IsCancellationRequested || expectedGeneration != networkLifetime.Token)
                throw new RuntimeCapabilityException("NETWORK_POLICY_CHANGED");
            return new GateLease(gate);
        }
        catch { Monitor.Exit(gate); throw; }
    }
    public bool UsesTestAccount => Account != "live";
    public event EventHandler<IdentitySnapshot>? Changed;
    public IdentitySnapshot Snapshot
    {
        get
        {
            lock (gate)
            {
                EnsureActive();
                bool guest = account is "guest" or "live";
                return new(guest ? IdentitySessionState.SignedOut : offline ? IdentitySessionState.OfflineCached : IdentitySessionState.SignedIn,
                    $"developer-preview:{scope}:{account}", epoch, guest ? null : account == "account-a" ? "开发测试账号 A" : "开发测试账号 B",
                    null, false, false, null, authorityRevision);
            }
        }
    }
    public void SetOffline(bool value)
    {
        CancellationTokenSource? previous = null; IdentitySnapshot? snapshot = null;
        CommitIdentity(() => (previous, snapshot) = SetOfflineCore(value));
        PublishSwitch(previous, snapshot);
    }
    public async Task SetOfflineAsync(bool value, CancellationToken cancellationToken = default)
    {
        lock (gate) EnsureActive();
        CancellationTokenSource? previous = null; IdentitySnapshot? snapshot = null;
        await CommitIdentityAsync(() => (previous, snapshot) = SetOfflineCore(value, cancellationToken), cancellationToken).ConfigureAwait(false);
        PublishSwitch(previous, snapshot);
    }
    private (CancellationTokenSource?, IdentitySnapshot?) SetOfflineCore(bool value, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            if (offline == value) return (null, null);
            long nextRevision = checked(authorityRevision + 1);
            var previous = networkLifetime;
            networkLifetime = new CancellationTokenSource();
            offline = value; authorityRevision = nextRevision;
            return (previous, Snapshot);
        }
    }
    public void SwitchAccount(string value)
    {
        ValidateAccount(value);
        CancellationTokenSource? previous = null; IdentitySnapshot? snapshot = null;
        CommitIdentity(() => (previous, snapshot) = SwitchAccountCore(value));
        PublishSwitch(previous, snapshot);
    }
    public async Task SwitchAccountAsync(string value, CancellationToken cancellationToken = default)
    {
        ValidateAccount(value); lock (gate) EnsureActive();
        CancellationTokenSource? previous = null; IdentitySnapshot? snapshot = null;
        await CommitIdentityAsync(() => (previous, snapshot) = SwitchAccountCore(value, cancellationToken), cancellationToken).ConfigureAwait(false);
        PublishSwitch(previous, snapshot);
    }
    private static void ValidateAccount(string value)
    { if (value is not ("live" or "guest" or "account-a" or "account-b")) throw new RuntimeCapabilityException("DEVELOPER_SIMULATION_INVALID"); }
    private (CancellationTokenSource, IdentitySnapshot) SwitchAccountCore(string value, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            EnsureActive();
            cancellationToken.ThrowIfCancellationRequested();
            long nextEpoch = checked(epoch + 1), nextRevision = checked(authorityRevision + 1);
            var nextLifetime = new CancellationTokenSource();
            var previous = epochLifetime;
            account = value; epoch = nextEpoch; authorityRevision = nextRevision; epochLifetime = nextLifetime;
            return (previous, Snapshot);
        }
    }
    private void PublishSwitch(CancellationTokenSource? previous, IdentitySnapshot? snapshot)
    {
        try { previous?.Cancel(); }
        finally
        {
            previous?.Dispose();
            if (snapshot is not null) Changed?.Invoke(this, snapshot);
        }
    }
    public RuntimeAccountContext BindAccount(CancellationToken hostLifetime)
    {
        lock (gate)
        {
            EnsureActive();
            if (account == "live") throw new RuntimeCapabilityException("DEVELOPER_SIMULATION_INVALID");
            string selected = account;
            long captured = epoch;
            CancellationToken invalidated = epochLifetime.Token;
            bool Current() { lock (gate) return !disposed && epoch == captured && !hostLifetime.IsCancellationRequested; }
            IDisposable Lease()
            {
                Monitor.Enter(gate);
                try
                {
                    if (!Current() || invalidated.IsCancellationRequested) throw new RuntimeCapabilityException("SESSION_EXPIRED");
                    return new GateLease(gate);
                }
                catch { Monitor.Exit(gate); throw; }
            }
            return new($"developer-preview:{scope}:{selected}", captured, selected == "guest", Current, invalidated, Lease);
        }
    }
    public Task<RuntimeProfile> GetProfileAsync(RuntimeAccountContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); context.EnsureCurrent();
        lock (gate)
        {
            EnsureActive();
            if (context.IsGuest) throw new RuntimeCapabilityException("AUTH_REQUIRED");
            if (context.AccountKey != $"developer-preview:{scope}:{account}" || context.Epoch != epoch)
                throw new RuntimeCapabilityException("SESSION_EXPIRED");
            return Task.FromResult(new RuntimeProfile(account == "account-a" ? "开发测试账号 A" : "开发测试账号 B", null, offline));
        }
    }
    private void EnsureActive() { if (disposed) throw new RuntimeCapabilityException("SESSION_EXPIRED"); }
    public IDisposable EnterSessionLease(string accountNamespace, long sessionEpoch)
    {
        Monitor.Enter(gate);
        try
        {
            EnsureActive();
            if (Snapshot.AccountNamespace != accountNamespace || epoch != sessionEpoch) throw new RuntimeCapabilityException("SESSION_EXPIRED");
            return new GateLease(gate);
        }
        catch { Monitor.Exit(gate); throw; }
    }
    // Explicit test account selection above is the only supported mutation; no fake authentication/refresh methods.
    public Task<IdentitySnapshot> InitializeAsync(CancellationToken cancellationToken = default) => throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
    public Task<IdentitySnapshot> SignInAsync(bool rememberSignIn, bool reauthenticate = false, CancellationToken cancellationToken = default) => throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
    public Task<IdentitySnapshot> SetRememberSignInAsync(bool rememberSignIn, CancellationToken cancellationToken = default) => throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
    public Task<IdentitySnapshot> RefreshAsync(CancellationToken cancellationToken = default) => throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
    public Task<IdentitySnapshot> SignOutAsync(bool browserSession = false, CancellationToken cancellationToken = default) => throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
    public void CancelSignIn() { }
    public void Dispose()
    {
        lock (gate) if (disposed) return;
        CancellationTokenSource? previous = null;
        CommitIdentity(() => previous = DisposeCore());
        CompleteDisposal(previous);
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) if (disposed) return;
        CancellationTokenSource? previous = null;
        await CommitIdentityAsync(() => previous = DisposeCore(), CancellationToken.None).ConfigureAwait(false);
        CompleteDisposal(previous);
    }
    private CancellationTokenSource? DisposeCore()
    {
        lock (gate)
        {
            if (disposed) return null;
            long nextRevision = checked(authorityRevision + 1);
            disposed = true; authorityRevision = nextRevision;
            return epochLifetime;
        }
    }
    private void CompleteDisposal(CancellationTokenSource? previous)
    {
        if (previous is null) return; // Another concurrent disposal already owns both lifetimes.
        try { previous?.Cancel(); }
        finally
        {
            previous?.Dispose();
            try { networkLifetime.Cancel(); }
            finally { networkLifetime.Dispose(); Changed = null; }
        }
    }
    private void CommitIdentity(Action commit)
    {
        if (authorityMutations is null) { commit(); return; }
        if (!authorityMutations.TryCommitSynchronously(RuntimeMutationScope.ForIdentity(), commit))
            throw new RuntimeCapabilityException("RUNTIME_ASYNC_AUTHORITY_REQUIRED");
    }
    private async Task CommitIdentityAsync(Action commit, CancellationToken beforeIntentCancellation)
    {
        if (authorityMutations is null) { beforeIntentCancellation.ThrowIfCancellationRequested(); commit(); return; }
        var mutation = await authorityMutations.BeginAsync(RuntimeMutationScope.ForIdentity(), beforeIntentCancellation).ConfigureAwait(false);
        await mutation.CommitAsync(commit).ConfigureAwait(false);
    }
    private sealed class GateLease(object gate) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; Monitor.Exit(gate); } }
    }
}

/// <summary>Preview decisions and forced denial exist only in memory. No writes reach the persistent grant store.</summary>
public sealed class DeveloperPreviewPermissions : IPermissionService, IDisposable, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IPermissionService original;
    private readonly IRuntimeAuthorityMutations? authorityMutations;
    private readonly Dictionary<(string Account, string Binding, string Permission), PermissionRecord> decisions = [];
    private readonly Dictionary<(string Account, string Binding), long> revisions = [];
    private readonly Dictionary<(string Account, string Binding, string Permission), long> permissionRevisions = [];
    private readonly HashSet<(string Account, string Binding, string Permission)> pending = [];
    private bool denied;
    private long denialRevision;
    private bool disposed;
    public DeveloperPreviewPermissions(IPermissionService original, IRuntimeAuthorityMutations? authorityMutations = null)
    { this.original = original; this.authorityMutations = authorityMutations; original.Changed += OriginalChanged; }
    private void OriginalChanged(PermissionChange change)
    {
        lock (gate)
        {
            if (disposed) return;
            if (denied) change = change with { Decision = PermissionDecision.Denied };
            else if (change.Decision is not (PermissionDecision.Denied or PermissionDecision.Revoked)
                && decisions.TryGetValue((change.AccountKey, change.BindingKey, change.Permission), out var local))
                change = change with { Decision = local.Decision };
        }
        Changed?.Invoke(change);
    }
    public event Action<PermissionChange>? Changed;
    public bool Denied { get { lock (gate) return denied; } }
    private static void Validate(RuntimeAccountContext account, RuntimeApplication app, string permission)
    {
        account.EnsureCurrent();
        if (!PermissionService.Supported.Contains(permission)) throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
        if (!app.DeclaredPermissions.Contains(permission)) throw new RuntimeCapabilityException("PERMISSION_NOT_DECLARED");
        if (permission == "network") _ = NetworkDomainPolicy.CopyValidated(app.NetworkDomains);
    }
    public PermissionDecision Query(RuntimeAccountContext account, RuntimeApplication app, string permission)
    {
        Validate(account, app, permission);
        lock (gate)
        {
            EnsureActive();
            if (denied) return PermissionDecision.Denied;
            bool testAccount = account.AccountKey.StartsWith("developer-preview:", StringComparison.Ordinal);
            PermissionDecision inherited = testAccount ? PermissionDecision.Prompt : original.Query(account, app, permission);
            if (inherited is PermissionDecision.Denied or PermissionDecision.Revoked) return inherited;
            if (decisions.TryGetValue((account.AccountKey, app.BindingKey, permission), out var record))
                return permission == "network" && record.NetworkScopeHash != NetworkDomainPolicy.ScopeHash(app.NetworkDomains)
                    ? PermissionDecision.Prompt : record.Decision;
            return inherited;
        }
    }
    public IReadOnlyList<PermissionRecord> List(RuntimeAccountContext account)
    { account.EnsureCurrent(); lock (gate) { EnsureActive(); return decisions.Values.Where(r => r.AccountKey == account.AccountKey).ToArray(); } }
    public void Demand(RuntimeAccountContext account, RuntimeApplication app, string permission)
    {
        PermissionDecision decision = Query(account, app, permission);
        if (decision != PermissionDecision.Granted) throw new RuntimeCapabilityException(decision == PermissionDecision.Revoked ? "PERMISSION_REVOKED" : "PERMISSION_DENIED");
    }
    public long GetRevision(RuntimeAccountContext account, RuntimeApplication app)
    { lock (gate) { EnsureActive(); return checked(denialRevision + revisions.GetValueOrDefault((account.AccountKey, app.BindingKey))
        + (account.AccountKey.StartsWith("developer-preview:", StringComparison.Ordinal) ? 0 : original.GetRevision(account, app))); } }
    public long GetRevision(RuntimeAccountContext account, RuntimeApplication app, string permission)
    {
        lock (gate)
        {
            EnsureActive();
            Validate(account, app, permission);
            return checked(denialRevision + permissionRevisions.GetValueOrDefault((account.AccountKey, app.BindingKey, permission))
                + (IsTestAccount(account) ? 0 : original.GetRevision(account, app, permission)));
        }
    }
    public IDisposable EnterUsageLease(RuntimeAccountContext account, RuntimeApplication app, string permission)
        => EnterLease(account, app, permission, requireGranted: true);
    public IDisposable EnterDecisionLease(RuntimeAccountContext account, RuntimeApplication app, string permission)
        => EnterLease(account, app, permission, requireGranted: false);
    private IDisposable EnterLease(RuntimeAccountContext account, RuntimeApplication app, string permission, bool requireGranted)
    {
        Monitor.Enter(gate);
        IDisposable? inherited = null;
        try
        {
            EnsureActive();
            Validate(account, app, permission);
            // A local preview grant may override Prompt, so retain the original decision lease,
            // not its usage lease. Both locks remain held through the caller's final commit.
            inherited = EnterOriginalDecisionLease(account, app, permission);
            if (requireGranted) Demand(account, app, permission);
            return new GateLease(gate, inherited);
        }
        catch
        {
            try { inherited?.Dispose(); }
            finally { Monitor.Exit(gate); }
            throw;
        }
    }
    private IDisposable? EnterOriginalDecisionLease(RuntimeAccountContext account, RuntimeApplication app, string permission)
        => IsTestAccount(account) ? null : original.EnterDecisionLease(account, app, permission);
    private static bool IsTestAccount(RuntimeAccountContext account)
        => account.AccountKey.StartsWith("developer-preview:", StringComparison.Ordinal);
    public void SetDenied(bool value, RuntimeAccountContext account, RuntimeApplication app)
    {
        account.EnsureCurrent();
        PermissionChange[] changes = [];
        CommitMutation(RuntimeMutationScope.ForApplication(account.AccountKey, app.BindingKey),
            () => changes = SetDeniedCore(value, account, app));
        foreach (var change in changes) Changed?.Invoke(change);
    }
    public async Task SetDeniedAsync(bool value, RuntimeAccountContext account, RuntimeApplication app,
        CancellationToken cancellationToken = default)
    {
        account.EnsureCurrent(); lock (gate) EnsureActive();
        PermissionChange[] changes = [];
        await CommitMutationAsync(RuntimeMutationScope.ForApplication(account.AccountKey, app.BindingKey),
            () => changes = SetDeniedCore(value, account, app, cancellationToken), cancellationToken).ConfigureAwait(false);
        foreach (var change in changes) Changed?.Invoke(change);
    }
    private PermissionChange[] SetDeniedCore(bool value, RuntimeAccountContext account, RuntimeApplication app, CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            EnsureActive(); cancellationToken.ThrowIfCancellationRequested();
            foreach (string permission in app.DeclaredPermissions) Validate(account, app, permission);
            using var inherited = app.DeclaredPermissions.FirstOrDefault() is { } firstPermission
                ? EnterOriginalDecisionLease(account, app, firstPermission) : null;
            using var lease = account.EnterCommitLease?.Invoke(); account.EnsureCurrent();
            if (denied == value) return [];
            long nextRevision = checked(denialRevision + 1);
            // Forced denial affects all local counters. Existing/current inherited
            // stamps must also fit before publishing the new denial generation.
            foreach (long local in revisions.Values) _ = checked(nextRevision + local);
            foreach (long local in permissionRevisions.Values) _ = checked(nextRevision + local);
            _ = checked(nextRevision + revisions.GetValueOrDefault((account.AccountKey, app.BindingKey))
                + (IsTestAccount(account) ? 0 : original.GetRevision(account, app)));
            foreach (string permission in app.DeclaredPermissions)
                _ = checked(nextRevision + permissionRevisions.GetValueOrDefault((account.AccountKey, app.BindingKey, permission))
                    + (IsTestAccount(account) ? 0 : original.GetRevision(account, app, permission)));
            denied = value; denialRevision = nextRevision;
            return app.DeclaredPermissions.Select(permission => new PermissionChange(account.AccountKey, app.BindingKey,
                permission, Query(account, app, permission))).ToArray();
        }
    }
    public async Task<PermissionDecision> RequestAsync(RuntimeAccountContext account, RuntimeApplication app, string permission,
        bool userGesture, Func<RuntimePermissionPrompt, CancellationToken, Task<bool>> prompt, CancellationToken cancellationToken)
    {
        Validate(account, app, permission);
        var key = (account.AccountKey, app.BindingKey, permission);
        string[]? networkDomains = permission == "network" ? NetworkDomainPolicy.CopyValidated(app.NetworkDomains) : null;
        string? networkScope = networkDomains is null ? null : NetworkDomainPolicy.ScopeHash(networkDomains);
        long revision;
        lock (gate)
        {
            EnsureActive();
            using var inherited = EnterOriginalDecisionLease(account, app, permission);
            PermissionDecision existing = Query(account, app, permission);
            if (existing != PermissionDecision.Prompt) return existing;
            if (!userGesture) throw new RuntimeCapabilityException("USER_GESTURE_REQUIRED");
            revision = GetRevision(account, app, permission);
            if (!pending.Add(key)) throw new RuntimeCapabilityException("REQUEST_BUSY");
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, account.Invalidated);
        try
        {
            string[] fields = permission == "identity.profile" ? ["appScopedUserId", "displayName", "avatarUrl", "isCached"] : networkDomains ?? [];
            var request = new RuntimePermissionPrompt(app.Identity.AppId, app.DisplayName, app.Source, permission,
                "独立开发预览：决定仅保留在本次预览内存中，不修改正式用户授权。", fields);
            bool accepted = await prompt(request, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            PermissionChange? change = null;
            await CommitMutationAsync(RuntimeMutationScope.ForApplication(account.AccountKey, app.BindingKey), () =>
            {
                lock (gate)
                {
                    EnsureActive(); using var inherited = EnterOriginalDecisionLease(account, app, permission);
                    using var lease = account.EnterCommitLease?.Invoke(); linked.Token.ThrowIfCancellationRequested(); account.EnsureCurrent();
                    if (revision != GetRevision(account, app, permission)) throw new RuntimeCapabilityException("PERMISSION_REVOKED");
                    if (networkScope is not null && networkScope != NetworkDomainPolicy.ScopeHash(app.NetworkDomains))
                        throw new RuntimeCapabilityException("NETWORK_POLICY_CHANGED");
                    change = Save(account, app, permission, accepted ? PermissionDecision.Granted : PermissionDecision.Denied);
                }
            }, linked.Token).ConfigureAwait(false);
            Changed?.Invoke(change!); return change!.Decision;
        }
        finally { lock (gate) pending.Remove(key); }
    }
    public void SetDecision(RuntimeAccountContext account, RuntimeApplication app, string permission, PermissionDecision decision)
    {
        Validate(account, app, permission);
        if (!Enum.IsDefined(decision)) throw new ArgumentOutOfRangeException(nameof(decision));
        PermissionChange? change = null;
        CommitMutation(RuntimeMutationScope.ForApplication(account.AccountKey, app.BindingKey), () =>
        {
            lock (gate)
            { EnsureActive(); using var inherited = EnterOriginalDecisionLease(account, app, permission);
                using var lease = account.EnterCommitLease?.Invoke(); Validate(account, app, permission); change = Save(account, app, permission, decision); }
        });
        Changed?.Invoke(change!);
    }
    public async Task SetDecisionAsync(RuntimeAccountContext account, RuntimeApplication app, string permission,
        PermissionDecision decision, CancellationToken cancellationToken = default)
    {
        Validate(account, app, permission);
        if (!Enum.IsDefined(decision)) throw new ArgumentOutOfRangeException(nameof(decision));
        lock (gate) EnsureActive();
        PermissionChange? change = null;
        await CommitMutationAsync(RuntimeMutationScope.ForApplication(account.AccountKey, app.BindingKey), () =>
        {
            lock (gate)
            { EnsureActive(); using var inherited = EnterOriginalDecisionLease(account, app, permission);
                using var lease = account.EnterCommitLease?.Invoke(); cancellationToken.ThrowIfCancellationRequested(); Validate(account, app, permission); change = Save(account, app, permission, decision); }
        }, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(change!);
    }
    private PermissionChange Save(RuntimeAccountContext account, RuntimeApplication app, string permission, PermissionDecision decision)
    {
        if (decisions.Count >= 4096 && !decisions.ContainsKey((account.AccountKey, app.BindingKey, permission)))
            throw new RuntimeCapabilityException("QUOTA_EXCEEDED");
        var key = (account.AccountKey, app.BindingKey, permission);
        var binding = (account.AccountKey, app.BindingKey);
        // Callers retain the original decision lease before the account lease;
        // preserve that lock order while checking the combined inherited stamp.
        long nextPermissionRevision = checked(permissionRevisions.GetValueOrDefault(key) + 1);
        long nextBindingRevision = checked(revisions.GetValueOrDefault(binding) + 1);
        _ = checked(denialRevision + nextPermissionRevision + (IsTestAccount(account) ? 0 : original.GetRevision(account, app, permission)));
        _ = checked(denialRevision + nextBindingRevision + (IsTestAccount(account) ? 0 : original.GetRevision(account, app)));
        decisions[key] = new(account.AccountKey, app.BindingKey, app.Identity.AppId, app.DisplayName, app.Source, permission, decision,
            permission == "network" ? NetworkDomainPolicy.ScopeHash(app.NetworkDomains) : null);
        permissionRevisions[key] = nextPermissionRevision;
        revisions[binding] = nextBindingRevision;
        return new(account.AccountKey, app.BindingKey, permission, decision);
    }
    private void EnsureActive() { if (disposed) throw new RuntimeCapabilityException("SESSION_EXPIRED"); }
    public void Dispose()
    {
        lock (gate) if (disposed) return;
        CommitMutation(RuntimeMutationScope.ForIdentity(), () => { lock (gate) disposed = true; });
        original.Changed -= OriginalChanged; Changed = null;
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) if (disposed) return;
        await CommitMutationAsync(RuntimeMutationScope.ForIdentity(), () => { lock (gate) disposed = true; }, CancellationToken.None).ConfigureAwait(false);
        original.Changed -= OriginalChanged; Changed = null;
    }
    private void CommitMutation(RuntimeMutationScope scope, Action commit)
    {
        if (authorityMutations is null) { commit(); return; }
        if (!authorityMutations.TryCommitSynchronously(scope, commit))
            throw new RuntimeCapabilityException("RUNTIME_ASYNC_AUTHORITY_REQUIRED");
    }
    private async Task CommitMutationAsync(RuntimeMutationScope scope, Action commit, CancellationToken beforeIntentCancellation)
    {
        if (authorityMutations is null) { beforeIntentCancellation.ThrowIfCancellationRequested(); commit(); return; }
        var mutation = await authorityMutations.BeginAsync(scope, beforeIntentCancellation).ConfigureAwait(false);
        await mutation.CommitAsync(commit).ConfigureAwait(false);
    }
    private sealed class GateLease(object gate, IDisposable? inherited = null) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { inherited?.Dispose(); }
            finally { Monitor.Exit(gate); }
        }
    }
}
