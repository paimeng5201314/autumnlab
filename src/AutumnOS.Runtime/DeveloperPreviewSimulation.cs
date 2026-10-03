using AutumnOS.Contracts;

namespace AutumnOS.Runtime;

/// <summary>Explicit host-only test state. It cannot issue credentials or mutate the real identity service.</summary>
public sealed class DeveloperPreviewSimulation : IIdentityService, IDisposable
{
    private readonly object gate = new();
    private readonly string scope = Guid.NewGuid().ToString("N");
    private CancellationTokenSource epochLifetime = new();
    private long epoch;
    private bool disposed;
    private string account = "live";
    private bool offline;
    public string Account { get { lock (gate) return account; } }
    public bool Offline { get { lock (gate) return offline; } }
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
                    null, false, false, null);
            }
        }
    }
    public void SetOffline(bool value)
    {
        IdentitySnapshot snapshot;
        lock (gate) { EnsureActive(); if (offline == value) return; offline = value; snapshot = Snapshot; }
        Changed?.Invoke(this, snapshot);
    }
    public void SwitchAccount(string value)
    {
        if (value is not ("live" or "guest" or "account-a" or "account-b")) throw new RuntimeCapabilityException("DEVELOPER_SIMULATION_INVALID");
        CancellationTokenSource previous;
        lock (gate) { EnsureActive(); account = value; epoch++; previous = epochLifetime; epochLifetime = new(); }
        previous.Cancel(); previous.Dispose();
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
        CancellationTokenSource previous;
        lock (gate) { if (disposed) return; disposed = true; previous = epochLifetime; }
        previous.Cancel(); previous.Dispose(); Changed = null;
    }
    private sealed class GateLease(object gate) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; Monitor.Exit(gate); } }
    }
}

/// <summary>Preview decisions and forced denial exist only in memory. No writes reach the persistent grant store.</summary>
public sealed class DeveloperPreviewPermissions : IPermissionService, IDisposable
{
    private readonly object gate = new();
    private readonly IPermissionService original;
    private readonly Dictionary<(string Account, string Binding, string Permission), PermissionRecord> decisions = [];
    private readonly Dictionary<(string Account, string Binding), long> revisions = [];
    private readonly HashSet<(string Account, string Binding, string Permission)> pending = [];
    private bool denied;
    private long denialRevision;
    public DeveloperPreviewPermissions(IPermissionService original) { this.original = original; original.Changed += OriginalChanged; }
    private void OriginalChanged(PermissionChange change)
    {
        lock (gate)
        {
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
    }
    public PermissionDecision Query(RuntimeAccountContext account, RuntimeApplication app, string permission)
    {
        Validate(account, app, permission);
        lock (gate)
        {
            if (denied) return PermissionDecision.Denied;
            bool testAccount = account.AccountKey.StartsWith("developer-preview:", StringComparison.Ordinal);
            PermissionDecision inherited = testAccount ? PermissionDecision.Prompt : original.Query(account, app, permission);
            if (inherited is PermissionDecision.Denied or PermissionDecision.Revoked) return inherited;
            if (decisions.TryGetValue((account.AccountKey, app.BindingKey, permission), out var record)) return record.Decision;
            return inherited;
        }
    }
    public IReadOnlyList<PermissionRecord> List(RuntimeAccountContext account)
    { account.EnsureCurrent(); lock (gate) return decisions.Values.Where(r => r.AccountKey == account.AccountKey).ToArray(); }
    public void Demand(RuntimeAccountContext account, RuntimeApplication app, string permission)
    {
        PermissionDecision decision = Query(account, app, permission);
        if (decision != PermissionDecision.Granted) throw new RuntimeCapabilityException(decision == PermissionDecision.Revoked ? "PERMISSION_REVOKED" : "PERMISSION_DENIED");
    }
    public long GetRevision(RuntimeAccountContext account, RuntimeApplication app)
    { lock (gate) return denialRevision + revisions.GetValueOrDefault((account.AccountKey, app.BindingKey))
        + (account.AccountKey.StartsWith("developer-preview:", StringComparison.Ordinal) ? 0 : original.GetRevision(account, app)); }
    public IDisposable EnterUsageLease(RuntimeAccountContext account, RuntimeApplication app, string permission)
    { Monitor.Enter(gate); try { Demand(account, app, permission); return new GateLease(gate); } catch { Monitor.Exit(gate); throw; } }
    public IDisposable EnterDecisionLease(RuntimeAccountContext account, RuntimeApplication app, string permission)
    { Monitor.Enter(gate); try { Validate(account, app, permission); return new GateLease(gate); } catch { Monitor.Exit(gate); throw; } }
    public void SetDenied(bool value, RuntimeAccountContext account, RuntimeApplication app)
    {
        account.EnsureCurrent();
        lock (gate) { if (denied == value) return; denied = value; denialRevision++; }
        foreach (string permission in app.DeclaredPermissions)
            Changed?.Invoke(new(account.AccountKey, app.BindingKey, permission, Query(account, app, permission)));
    }
    public async Task<PermissionDecision> RequestAsync(RuntimeAccountContext account, RuntimeApplication app, string permission,
        bool userGesture, Func<RuntimePermissionPrompt, CancellationToken, Task<bool>> prompt, CancellationToken cancellationToken)
    {
        Validate(account, app, permission);
        var key = (account.AccountKey, app.BindingKey, permission);
        long revision;
        lock (gate)
        {
            PermissionDecision existing = Query(account, app, permission);
            if (existing != PermissionDecision.Prompt) return existing;
            if (!userGesture) throw new RuntimeCapabilityException("USER_GESTURE_REQUIRED");
            if (!pending.Add(key)) throw new RuntimeCapabilityException("REQUEST_BUSY");
            revision = GetRevision(account, app);
        }
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, account.Invalidated);
        try
        {
            string[] fields = permission == "identity.profile" ? ["appScopedUserId", "displayName", "avatarUrl", "isCached"] : [];
            var request = new RuntimePermissionPrompt(app.Identity.AppId, app.DisplayName, app.Source, permission,
                "独立开发预览：决定仅保留在本次预览内存中，不修改正式用户授权。", fields);
            bool accepted = await prompt(request, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            PermissionChange change;
            lock (gate)
            {
                linked.Token.ThrowIfCancellationRequested(); account.EnsureCurrent();
                if (revision != GetRevision(account, app)) throw new RuntimeCapabilityException("PERMISSION_REVOKED");
                change = Save(account, app, permission, accepted ? PermissionDecision.Granted : PermissionDecision.Denied);
            }
            Changed?.Invoke(change); return change.Decision;
        }
        finally { lock (gate) pending.Remove(key); }
    }
    public void SetDecision(RuntimeAccountContext account, RuntimeApplication app, string permission, PermissionDecision decision)
    {
        Validate(account, app, permission);
        if (!Enum.IsDefined(decision)) throw new ArgumentOutOfRangeException(nameof(decision));
        PermissionChange change;
        lock (gate) { using var lease = account.EnterCommitLease?.Invoke(); account.EnsureCurrent(); change = Save(account, app, permission, decision); }
        Changed?.Invoke(change);
    }
    private PermissionChange Save(RuntimeAccountContext account, RuntimeApplication app, string permission, PermissionDecision decision)
    {
        if (decisions.Count >= 4096 && !decisions.ContainsKey((account.AccountKey, app.BindingKey, permission)))
            throw new RuntimeCapabilityException("QUOTA_EXCEEDED");
        decisions[(account.AccountKey, app.BindingKey, permission)] = new(account.AccountKey, app.BindingKey, app.Identity.AppId, app.DisplayName, app.Source, permission, decision);
        var binding = (account.AccountKey, app.BindingKey); revisions[binding] = revisions.GetValueOrDefault(binding) + 1;
        return new(account.AccountKey, app.BindingKey, permission, decision);
    }
    public void Dispose() { original.Changed -= OriginalChanged; Changed = null; }
    private sealed class GateLease(object gate) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; Monitor.Exit(gate); } }
    }
}
