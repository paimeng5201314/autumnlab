using System.Text.Json;
using AutumnOS.Contracts;

namespace AutumnOS.Runtime;

public sealed partial class RuntimeSession
{
    public static readonly TimeSpan UserGestureLifetime = TimeSpan.FromSeconds(2);
    private RuntimeSessionServices? services;
    private CancellationTokenRegistration accountRegistration;
    private long gestureTimestamp;
    private bool gestureAvailable;
    private bool servicesDetached;
    private readonly AsyncLocal<string?> activeDataPermission = new();
    private long eventRevision;
    public event Action<string, object>? SdkEvent;
    public long EventRevision => Interlocked.Read(ref eventRevision)
        + (services is null ? 0 : services.Permissions.GetRevision(services.Account, services.Application));

    /// <summary>Read-only eligibility check. Native hosts must use DeliverEvent for actual delivery.</summary>
    public bool CanDeliverEvent(string name, long revision) => DeliverEvent(name, revision, static () => { });

    /// <summary>UI-thread event post held inside the same permission, instance and identity leases as final data commits.</summary>
    public bool DeliverEvent(string name, long revision, Action post)
    {
        ArgumentNullException.ThrowIfNull(post);
        var runtime = services;
        if (runtime is null || EventRevision != revision
            || name is not ("identity.changed" or "links.opened" or "shortcuts.invoked" or "appearance.changed" or "permissions.changed" or "lifecycle.changed")) return false;
        IDisposable? permission = null;
        IDisposable? identity = null;
        try
        {
            string? required = name switch { "identity.changed" => "identity.profile", "links.opened" => "links", "shortcuts.invoked" => "shortcuts", _ => null };
            if (required is not null) permission = runtime.Permissions.EnterUsageLease(runtime.Account, runtime.Application, required);
            // Permission-change events contain a decision, not private data. Still serialize that decision
            // with grant mutations, using one declared permission to enter this service's decision gate.
            else if (name == "permissions.changed")
            {
                string? declaredPermission = runtime.Application.DeclaredPermissions.FirstOrDefault();
                if (declaredPermission is null) return false;
                permission = runtime.Permissions.EnterDecisionLease(runtime.Account, runtime.Application, declaredPermission);
            }
            lock (stateGate)
            {
                if (!IsActive || servicesDetached) return false;
                identity = runtime.Account.EnterCommitLease?.Invoke();
                runtime.Account.EnsureCurrent();
                if (EventRevision != revision) return false;
                if (name == "identity.changed") CheckAccount(runtime);
                post();
                return true;
            }
        }
        catch (RuntimeCapabilityException) { return false; }
        catch (ObjectDisposedException) { return false; }
        finally { identity?.Dispose(); permission?.Dispose(); }
    }

    /// <summary>Revalidate a completed response after an awaited UI dispatch, before exposing private data.</summary>
    public string RevalidateResponse(string method, long requestRevision, string response)
    {
        if (services is null) return response;
        string? requestId = null;
        try
        {
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            requestId = root.GetProperty("requestId").GetString();
            lock (stateGate)
            {
                if (!IsActive) return Error(requestId, "SESSION_EXPIRED");
                services.Account.EnsureCurrent();
            }
            if (!root.GetProperty("ok").GetBoolean()) return response;
            string? permission = ResponsePermission(method);
            if (permission is not null)
            {
                services.Permissions.Demand(services.Account, services.Application, permission);
                // requestProfile can itself commit one Prompt→Granted change. Revocation/regrant
                // takes at least two additional revisions and must never revive the old reply.
                long maximumRevision = method == "identity.requestProfile" ? requestRevision + 1 : requestRevision;
                if (EventRevision < requestRevision || EventRevision > maximumRevision) return Error(requestId, "PERMISSION_REVOKED");
                if (method.StartsWith("identity.", StringComparison.Ordinal)) CheckAccount(services);
            }
            if (method is "permissions.query" or "permissions.request")
            {
                var result = root.GetProperty("result");
                string name = result.GetProperty("name").GetString()!;
                string current = services.Permissions.Query(services.Account, services.Application, name).ToString().ToLowerInvariant();
                long maximumRevision = method == "permissions.request" ? requestRevision + 1 : requestRevision;
                if (EventRevision < requestRevision || EventRevision > maximumRevision || current != result.GetProperty("state").GetString())
                    return Error(requestId, "PERMISSION_REVOKED");
            }
            return response;
        }
        catch (RuntimeCapabilityException error) { return Error(requestId, error.Code); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or ArgumentException)
        { return Error(requestId, "INVALID_RESPONSE"); }
    }

    /// <summary>UI-thread delivery with final permission/instance/identity leases held through the native post.</summary>
    public string DeliverResponse(string method, long requestRevision, string response, Action<string> post)
    {
        ArgumentNullException.ThrowIfNull(post);
        IDisposable? permissionLease = null;
        IDisposable? identityLease = null;
        string? requestId = null;
        try
        {
            using var document = JsonDocument.Parse(response);
            requestId = document.RootElement.GetProperty("requestId").GetString();
            if (services is not null && document.RootElement.GetProperty("ok").GetBoolean() && ResponsePermission(method) is { } permission)
                permissionLease = services.Permissions.EnterUsageLease(services.Account, services.Application, permission);
            else if (services is not null && document.RootElement.GetProperty("ok").GetBoolean() && (method is "permissions.query" or "permissions.request"))
                permissionLease = services.Permissions.EnterDecisionLease(services.Account, services.Application,
                    document.RootElement.GetProperty("result").GetProperty("name").GetString()!);
            lock (stateGate)
            {
                if (services is not null && IsActive) identityLease = services.Account.EnterCommitLease?.Invoke();
                string deliver = RevalidateResponse(method, requestRevision, response);
                post(deliver);
                return deliver;
            }
        }
        catch (RuntimeCapabilityException error)
        {
            string deliver = Error(requestId, error.Code);
            post(deliver); return deliver;
        }
        finally { identityLease?.Dispose(); permissionLease?.Dispose(); }
    }

    private static string? ResponsePermission(string method) => method switch
    {
        "identity.getProfile" or "identity.requestProfile" => "identity.profile",
        "saves.list" or "saves.read" or "saves.write" or "saves.restore" => "saves",
        "storage.read" or "storage.write" or "storage.delete" or "preferences.get" or "preferences.set" => "storage",
        "files.pickOpen" or "files.read" => "files.open",
        "files.pickSave" or "files.write" => "files.save",
        _ => null
    };

    private void InitializeServices(RuntimeSessionServices? value)
    {
        if (value is null) return;
        value.Account.EnsureCurrent();
        if (value.Application.Identity.AppId != instance.Identity.AppId
            || !declared.SetEquals(value.Application.DeclaredPermissions)) throw new ArgumentException("Runtime services must bind the installed application.", nameof(value));
        services = value;
        instance = instance with { Identity = value.Application.Identity, Epoch = new(value.Account.Epoch) };
        value.Desktop?.Register(instance.Id.Value, value.Account, value.Application, value.Permissions, value.Declarations);
        value.Permissions.Changed += PermissionChanged;
        if (value.Desktop is not null) value.Desktop.Event += DesktopEvent;
        if (value.Identity is not null) value.Identity.Changed += IdentityChanged;
        accountRegistration = value.Account.Invalidated.Register(() => End(AppLifecycleState.Closed));
    }

    /// <summary>Host-only receipt of a native pointer/key interaction; an SDK flag never counts as evidence.</summary>
    public void HostUserGesture()
    {
        lock (stateGate)
        {
            if (!IsActive || instance.State != AppLifecycleState.Foreground) return;
            if (services is not null && (services.Account.Invalidated.IsCancellationRequested || !services.Account.IsCurrent())) return;
            gestureTimestamp = timeProvider.GetTimestamp();
            gestureAvailable = true;
        }
    }

    /// <summary>Storage uses this synchronous lease around its final epoch check and atomic replace.</summary>
    public IDisposable EnterCommitLease()
    {
        IDisposable? permissionLease = null;
        if (services is not null && activeDataPermission.Value is { } permission)
            permissionLease = services.Permissions.EnterUsageLease(services.Account, services.Application, permission);
        Monitor.Enter(stateGate);
        try
        {
            if (!IsActive) throw new RuntimeCapabilityException("SESSION_EXPIRED");
            IDisposable? accountLease = services?.Account.EnterCommitLease?.Invoke();
            try
            {
                services?.Account.EnsureCurrent();
                return new CommitLease(stateGate, accountLease, permissionLease);
            }
            catch { accountLease?.Dispose(); throw; }
        }
        catch { Monitor.Exit(stateGate); permissionLease?.Dispose(); throw; }
    }

    private bool ConsumeGesture()
    {
        lock (stateGate)
        {
            bool valid = gestureAvailable && IsActive && instance.State == AppLifecycleState.Foreground
                && timeProvider.GetElapsedTime(gestureTimestamp, timeProvider.GetTimestamp()) <= UserGestureLifetime;
            gestureAvailable = false;
            return valid;
        }
    }

    private async Task<object> HandleManagedAsync(string method, JsonElement parameters, CancellationToken cancellationToken)
    {
        var runtime = services!;
        runtime.Account.EnsureCurrent();
        switch (method)
        {
            case "platform.getCapabilities":
                ExactManaged(parameters);
                var capabilities = new List<string> { "lifecycle", "permissions" };
                if (runtime.Identity is not null || runtime.GetProfile is not null) capabilities.Add("identity.profile");
                if (runtime.DataRequest is not null) capabilities.AddRange(runtime.DataCapabilities);
                if (runtime.Desktop is not null) capabilities.AddRange(["appearance", "notifications", "shortcuts", "widgets", "links.internal"]);
                return new { protocolVersion = 1, capabilities, accountMode = runtime.Account.IsGuest ? "guest" : "account", networkSandboxVerified = false,
                    limits = new { maximumMessageBytes = MaximumMessageBytes, maximumPendingRequests = MaximumPendingRequests,
                        maximumRequestsPerMinute = MaximumRequestsPerMinute, requestTimeoutMs = (int)requestTimeout.TotalMilliseconds,
                        maximumRememberedRequestIds = MaximumRememberedRequestIds, deduplicationWindowMs = (int)DeduplicationWindow.TotalMilliseconds } };
            case "lifecycle.getState":
                ExactManaged(parameters);
                var current = Instance;
                return new { state = current.State.ToString().ToLowerInvariant(), blocksMaintenance = current.BlocksMaintenance };
            case "permissions.query":
                ExactManaged(parameters, "name");
                string queried = PermissionParameter(parameters);
                return new { name = queried, state = runtime.Permissions.Query(runtime.Account, runtime.Application, queried).ToString().ToLowerInvariant() };
            case "permissions.request":
                ExactManaged(parameters, "name");
                string requested = PermissionParameter(parameters);
                var decision = await runtime.Permissions.RequestAsync(runtime.Account, runtime.Application, requested, ConsumeGesture(), RequestPermissionGuardedAsync, cancellationToken).ConfigureAwait(false);
                runtime.Account.EnsureCurrent();
                return new { name = requested, state = decision.ToString().ToLowerInvariant() };
            case "identity.requestProfile":
                ExactManaged(parameters);
                CheckAccount(runtime);
                var profileDecision = await runtime.Permissions.RequestAsync(runtime.Account, runtime.Application, "identity.profile", ConsumeGesture(), RequestPermissionGuardedAsync, cancellationToken).ConfigureAwait(false);
                if (profileDecision != PermissionDecision.Granted) throw new RuntimeCapabilityException(profileDecision == PermissionDecision.Revoked ? "PERMISSION_REVOKED" : "PERMISSION_DENIED");
                return await GetProfileAsync(runtime, cancellationToken).ConfigureAwait(false);
            case "identity.getProfile":
                ExactManaged(parameters);
                CheckAccount(runtime);
                return await GetProfileAsync(runtime, cancellationToken).ConfigureAwait(false);
            case "saves.list":
            case "saves.read":
            case "saves.write":
            case "saves.restore":
            case "storage.read":
            case "storage.write":
            case "storage.delete":
            case "preferences.get":
            case "preferences.set":
            case "files.pickOpen":
            case "files.pickSave":
            case "files.read":
            case "files.close":
            case "files.write":
                string permission = method.StartsWith("saves.", StringComparison.Ordinal) ? "saves"
                    : method is "files.pickOpen" or "files.read" or "files.close" ? "files.open"
                    : method is "files.pickSave" or "files.write" ? "files.save" : "storage";
                if (method == "files.close")
                {
                    string[] declaredFiles = new[] { "files.open", "files.save" }.Where(runtime.Application.DeclaredPermissions.Contains).ToArray();
                    if (declaredFiles.Length == 0) throw new RuntimeCapabilityException("PERMISSION_NOT_DECLARED");
                    permission = declaredFiles.FirstOrDefault(name => runtime.Permissions.Query(runtime.Account, runtime.Application, name) == PermissionDecision.Granted)
                        ?? declaredFiles.FirstOrDefault(name => runtime.Permissions.Query(runtime.Account, runtime.Application, name) == PermissionDecision.Revoked) ?? declaredFiles[0];
                }
                runtime.Permissions.Demand(runtime.Account, runtime.Application, permission);
                if (runtime.DataRequest is null) throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
                if ((method is "files.pickOpen" or "files.pickSave") && !ConsumeGesture()) throw new RuntimeCapabilityException("USER_GESTURE_REQUIRED");
                object result;
                string? previousPermission = activeDataPermission.Value;
                activeDataPermission.Value = permission;
                try { result = await runtime.DataRequest(method, parameters.Clone(), cancellationToken).ConfigureAwait(false); }
                finally { activeDataPermission.Value = previousPermission; }
                runtime.Account.EnsureCurrent();
                runtime.Permissions.Demand(runtime.Account, runtime.Application, permission);
                return result;
            case "appearance.get":
            case "notifications.show":
            case "notifications.setBadge":
            case "shortcuts.register":
            case "widgets.update":
            case "links.openInternal":
                if (runtime.Desktop is null) throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
                if (method == "links.openInternal" && !ConsumeGesture()) throw new RuntimeCapabilityException("USER_GESTURE_REQUIRED");
                return runtime.Desktop.Handle(Instance.Id.Value, method, parameters);
            default: throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
        }
    }

    private async Task<bool> RequestPermissionGuardedAsync(RuntimePermissionPrompt prompt, CancellationToken cancellationToken)
    {
        long suspensionAtStart;
        lock (stateGate)
        {
            if (!IsActive) throw new RuntimeCapabilityException("SESSION_EXPIRED");
            if (instance.State == AppLifecycleState.Suspended) throw new RuntimeCapabilityException("SESSION_SUSPENDED");
            suspensionAtStart = suspensionRevision;
        }
        bool accepted = await services!.RequestPermission(prompt, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        lock (stateGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsActive) throw new RuntimeCapabilityException("SESSION_EXPIRED");
            if (instance.State == AppLifecycleState.Suspended || suspensionRevision != suspensionAtStart) throw new RuntimeCapabilityException("SESSION_SUSPENDED");
        }
        return accepted;
    }

    private static void CheckAccount(RuntimeSessionServices runtime)
    {
        if (runtime.Identity?.Snapshot.ErrorCode == "AUTH_NOT_CONFIGURED") throw new RuntimeCapabilityException("AUTH_NOT_CONFIGURED");
        if (runtime.Account.IsGuest) throw new RuntimeCapabilityException("AUTH_REQUIRED");
        if (runtime.Identity is not null)
        {
            var current = runtime.Identity.Snapshot;
            if (current.AccountNamespace != runtime.Account.AccountKey || current.SessionEpoch != runtime.Account.Epoch)
                throw new RuntimeCapabilityException("SESSION_EXPIRED");
            if (current.State == IdentitySessionState.SessionExpired) throw new RuntimeCapabilityException("SESSION_EXPIRED");
            if (current.State is not (IdentitySessionState.SignedIn or IdentitySessionState.OfflineCached)) throw new RuntimeCapabilityException("AUTH_REQUIRED");
        }
    }

    private static async Task<object> GetProfileAsync(RuntimeSessionServices runtime, CancellationToken cancellationToken)
    {
        runtime.Permissions.Demand(runtime.Account, runtime.Application, "identity.profile");
        RuntimeProfile profile;
        if (runtime.Identity is not null)
        {
            var snapshot = runtime.Identity.Snapshot;
            profile = new(snapshot.DisplayName ?? "用户", snapshot.AvatarUrl, snapshot.State == IdentitySessionState.OfflineCached);
        }
        else if (runtime.GetProfile is not null)
            profile = await runtime.GetProfile(runtime.Account, cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
        else throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
        cancellationToken.ThrowIfCancellationRequested();
        runtime.Account.EnsureCurrent();
        CheckAccount(runtime);
        runtime.Permissions.Demand(runtime.Account, runtime.Application, "identity.profile");
        if (profile.DisplayName.Length > 256 || profile.DisplayName.Any(char.IsControl)) throw new RuntimeCapabilityException("PROFILE_INVALID");
        string? avatar = profile.AvatarUrl;
        if (avatar is not null && (avatar.Length > 2048 || !Uri.TryCreate(avatar, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length != 0)) avatar = null;
        return new { appScopedUserId = "au1_" + RuntimeApplication.Hash($"autumnos.app-user.v1\0{runtime.Account.AccountKey}\0{runtime.Application.BindingKey}"),
            displayName = profile.DisplayName, avatarUrl = avatar, isCached = profile.IsCached };
    }

    private void PermissionChanged(PermissionChange change)
    {
        var runtime = services;
        if (runtime is null || runtime.Account.AccountKey != change.AccountKey || runtime.Application.BindingKey != change.BindingKey) return;
        runtime.Desktop?.PermissionChanged(Instance.Id.Value, change);
        EmitSdkEvent("permissions.changed", new { name = change.Permission, state = change.Decision.ToString().ToLowerInvariant() });
    }
    private void DesktopEvent(Guid instanceId, string name, object value)
    { if (Instance.Id.Value == instanceId) EmitSdkEvent(name, value); }
    private void IdentityChanged(object? sender, IdentitySnapshot snapshot)
    {
        var runtime = services;
        if (runtime is null) return;
        if (snapshot.AccountNamespace != runtime.Account.AccountKey || snapshot.SessionEpoch != runtime.Account.Epoch
            || !runtime.Account.IsCurrent()) { End(AppLifecycleState.Closed); return; }
        if (snapshot.State is IdentitySessionState.SignedIn or IdentitySessionState.OfflineCached)
        {
            try
            {
                runtime.Permissions.Demand(runtime.Account, runtime.Application, "identity.profile");
                EmitSdkEvent("identity.changed", new { state = snapshot.State == IdentitySessionState.OfflineCached ? "offline_cached" : "signed_in" });
            }
            catch (RuntimeCapabilityException) { }
        }
    }
    private void EmitSdkEvent(string name, object value)
    {
        lock (stateGate)
        {
            if (!IsActive || servicesDetached || services is null || services.Account.Invalidated.IsCancellationRequested || !services.Account.IsCurrent()) return;
            SdkEvent?.Invoke(name, value);
        }
    }
    private void DetachServices()
    {
        var runtime = services;
        if (runtime is null) return;
        lock (stateGate) { if (servicesDetached) return; servicesDetached = true; gestureAvailable = false; }
        Interlocked.Increment(ref eventRevision);
        runtime.Permissions.Changed -= PermissionChanged;
        if (runtime.Identity is not null) runtime.Identity.Changed -= IdentityChanged;
        if (runtime.Desktop is not null) { runtime.Desktop.Event -= DesktopEvent; runtime.Desktop.Unregister(Instance.Id.Value); }
        accountRegistration.Unregister();
        SdkEvent = null;
    }
    private static string PermissionParameter(JsonElement parameters)
    {
        var name = parameters.GetProperty("name");
        if (name.ValueKind != JsonValueKind.String || name.GetString() is not { } text || text.Length is < 1 or > 80)
            throw new RuntimeCapabilityException("INVALID_REQUEST");
        return text;
    }
    private static void ExactManaged(JsonElement value, params string[] fields)
    { if (!ExactProperties(value, fields)) throw new RuntimeCapabilityException("INVALID_REQUEST"); }
    private sealed class CommitLease(object synchronization, IDisposable? accountLease, IDisposable? permissionLease) : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            try { accountLease?.Dispose(); }
            finally { Monitor.Exit(synchronization); permissionLease?.Dispose(); }
        }
    }
}
