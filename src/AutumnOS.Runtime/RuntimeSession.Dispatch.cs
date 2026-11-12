using AutumnOS.Contracts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutumnOS.Runtime;

/// <summary>Opaque, host-owned frozen response/event authority; never an SDK claim.</summary>
public sealed class RuntimeResponseStamp
{
    internal RuntimeSession Owner { get; }
    internal Guid InstanceId { get; }
    internal long InstanceEpoch { get; }
    internal long Lifecycle { get; }
    internal long GuestPermission { get; }
    internal long Permission { get; }
    internal IdentitySnapshot? Identity { get; }
    internal long EventRevision { get; }
    internal string? Method { get; }
    internal string? ResponseHash { get; }
    internal RuntimeResponseStamp(RuntimeSession owner, Guid instanceId, long epoch, long lifecycle, long guestPermission,
        long permission, IdentitySnapshot? identity, long eventRevision, string? method = null, string? responseHash = null)
    { Owner = owner; InstanceId = instanceId; InstanceEpoch = epoch; Lifecycle = lifecycle; GuestPermission = guestPermission;
      Permission = permission; Identity = identity; EventRevision = eventRevision; Method = method; ResponseHash = responseHash; }
    internal RuntimeResponseStamp Bind(string? method, string hash) => new(Owner, InstanceId, InstanceEpoch, Lifecycle,
        GuestPermission, Permission, Identity, EventRevision, method, hash);
}
public sealed record RuntimePreparedResponse(string Response, RuntimeResponseStamp Stamp);

public sealed partial class RuntimeSession
{
    private readonly IRuntimeAuthorityMutations? authorityMutations;
    private bool quarantined;
    private long lifecycleRevision;
    private readonly HashSet<Task> ownedAuthorityTasks = [];
    private Exception? authorityFailure;
    private Task<RuntimeCloseOperation>? closeIntent;
    private readonly AsyncLocal<RequestAuthorityCapture?> requestAuthority = new();
    private readonly Dictionary<string, RuntimeResponseStamp> preparedResponses = new(StringComparer.Ordinal);
    private readonly Queue<string> preparedResponseOrder = new();
    private sealed class RequestAuthorityCapture { internal string? Method; internal RuntimeResponseStamp? Frozen; }
    public event Action? NativeCloseRequired;

    /// <summary>Host-only scope, including the stable guest application binding.</summary>
    public RuntimeMutationScope AuthorityMutationScope => RuntimeMutationScope.ForInstance(
        services?.Account.AccountKey ?? "guest", services?.Application.BindingKey ??
        RuntimeApplication.Hash("autumnos.guest-app-binding.v1\0" + instance.Identity.AppId), instance.Id.Value);

    public Task ForegroundAsync(CancellationToken beforeIntentCancellation = default) => SetActiveStateAsync(AppLifecycleState.Foreground, beforeIntentCancellation);
    public Task BackgroundAsync(CancellationToken beforeIntentCancellation = default) => SetActiveStateAsync(AppLifecycleState.Background, beforeIntentCancellation);
    public Task SuspendAsync(CancellationToken beforeIntentCancellation = default) => SetActiveStateAsync(AppLifecycleState.Suspended, beforeIntentCancellation);
    public Task ResumeAsync(CancellationToken beforeIntentCancellation = default) => SetActiveStateAsync(AppLifecycleState.Foreground, beforeIntentCancellation);

    private async Task SetActiveStateAsync(AppLifecycleState state, CancellationToken cancellationToken)
    {
        object? value = null; long revision = 0;
        await CommitAuthorityAsync(() => (value, revision) = CommitActiveState(state), cancellationToken).ConfigureAwait(false);
        EmitSdkEvent("lifecycle.changed", value!, revision);
    }

    private (object Value, long Revision) CommitActiveState(AppLifecycleState state)
    {
        long permission = ReadPermissionRevision();
        lock (stateGate)
        {
            if (!IsActive)
            {
                if (authorityMutations is null) throw new InvalidOperationException("An ended session cannot be resumed.");
                throw new RuntimeCapabilityException("SESSION_EXPIRED");
            }
            long nextSuspension = state == AppLifecycleState.Suspended && instance.State != AppLifecycleState.Suspended ? checked(suspensionRevision + 1) : suspensionRevision;
            long nextLifecycle = checked(lifecycleRevision + 1), nextEvent = checked(eventRevision + 1);
            long publishedRevision = checked(nextEvent + permission);
            AppInstance next = instance with { State = state };
            object value = new { state = state.ToString().ToLowerInvariant(), blocksMaintenance = next.BlocksMaintenance };
            suspensionRevision = nextSuspension; lifecycleRevision = nextLifecycle; eventRevision = nextEvent; instance = next;
            return (value, publishedRevision);
        }
    }

    public Task RevokeSavePermissionAsync(CancellationToken beforeIntentCancellation = default) => services is { } runtime
        ? runtime.Permissions.SetDecisionAsync(runtime.Account, runtime.Application, "saves", PermissionDecision.Revoked, beforeIntentCancellation)
        : SetGuestSavePermissionAsync(PermissionDecision.Revoked, beforeIntentCancellation);

    /// <summary>Explicit trusted-host guest decision, never an SDK-supplied authority flag.</summary>
    public void SetGuestSavePermission(PermissionDecision decision)
    {
        RequireGuestDecision(decision);
        CommitAuthoritySynchronously(() => CommitGuestDecision(decision));
    }
    public Task SetGuestSavePermissionAsync(PermissionDecision decision, CancellationToken beforeIntentCancellation = default)
    {
        RequireGuestDecision(decision);
        return CommitAuthorityAsync(() => CommitGuestDecision(decision), beforeIntentCancellation);
    }
    private void RequireGuestDecision(PermissionDecision decision)
    {
        if (services is not null) throw new InvalidOperationException("Managed decisions belong to the permission service.");
        if (!Enum.IsDefined(decision)) throw new ArgumentOutOfRangeException(nameof(decision));
    }
    private void CommitGuestDecision(PermissionDecision decision)
    {
        lock (stateGate)
        {
            if (!IsActive) throw new RuntimeCapabilityException("SESSION_EXPIRED");
            long nextPermission = checked(permissionRevision + 1), nextEvent = checked(eventRevision + 1);
            savesGranted = decision == PermissionDecision.Granted;
            savesDenied = decision == PermissionDecision.Denied;
            savesRevoked = decision == PermissionDecision.Revoked;
            permissionRevision = nextPermission; eventRevision = nextEvent;
        }
    }

    private void CommitAuthoritySynchronously(Action action)
    {
        if (authorityMutations is null) { action(); return; }
        if (!authorityMutations.TryCommitSynchronously(AuthorityMutationScope, action))
            throw new RuntimeCapabilityException("RUNTIME_ASYNC_AUTHORITY_REQUIRED");
    }
    private async Task CommitAuthorityAsync(Action action, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (authorityMutations is null) { action(); return; }
        IRuntimeAuthorityMutation intent = await authorityMutations.BeginAsync(AuthorityMutationScope, cancellationToken).ConfigureAwait(false);
        await intent.CommitAsync(action).ConfigureAwait(false);
    }

    /// <summary>Registers intent, then quarantines delivery. Native cleanup may run before CompleteAsync resolves.</summary>
    public Task<RuntimeCloseOperation> BeginCloseAsync(bool crashed = false, CancellationToken beforeIntentCancellation = default)
    {
        beforeIntentCancellation.ThrowIfCancellationRequested();
        TaskCompletionSource<RuntimeCloseOperation> completion;
        lock (stateGate)
        {
            if (closeIntent is not null) return closeIntent;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            closeIntent = completion.Task;
        }
        OwnAuthorityTask(InstallCloseIntentAsync(completion, crashed, beforeIntentCancellation));
        return completion.Task;
    }

    private async Task InstallCloseIntentAsync(TaskCompletionSource<RuntimeCloseOperation> completion, bool crashed, CancellationToken cancellationToken)
    {
        bool registered = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IRuntimeAuthorityMutation? mutation = authorityMutations is null ? null :
                await authorityMutations.BeginAsync(AuthorityMutationScope, cancellationToken).ConfigureAwait(false);
            registered = true;
            lock (stateGate) { quarantined = true; gestureAvailable = false; }
            completion.TrySetResult(new RuntimeCloseOperation(this, mutation, crashed ? AppLifecycleState.Crashed : AppLifecycleState.Closed));
            NativeCloseRequired?.Invoke(); // Never under a state lock or inside the short authority commit.
        }
        catch (OperationCanceledException) when (!registered)
        {
            lock (stateGate) { if (ReferenceEquals(closeIntent, completion.Task)) closeIntent = null; }
            completion.TrySetCanceled(cancellationToken);
        }
        catch (Exception error)
        {
            lock (stateGate) { authorityFailure ??= error; if (!registered && ReferenceEquals(closeIntent, completion.Task)) closeIntent = null; }
            completion.TrySetException(error);
        }
    }

    public sealed class RuntimeCloseOperation
    {
        private readonly RuntimeSession owner;
        private readonly IRuntimeAuthorityMutation? mutation;
        private readonly AppLifecycleState state;
        private readonly object sync = new();
        private Task? completion;
        internal RuntimeCloseOperation(RuntimeSession owner, IRuntimeAuthorityMutation? mutation, AppLifecycleState state)
        { this.owner = owner; this.mutation = mutation; this.state = state; }
        /// <summary>Idempotent task ownership; the underlying mutation permit is consumed exactly once.</summary>
        public Task CompleteAsync()
        {
            TaskCompletionSource<bool> source;
            lock (sync)
            {
                if (completion is not null) return completion;
                source = new(TaskCreationOptions.RunContinuationsAsynchronously); completion = source.Task;
            }
            owner.OwnAuthorityTask(CompleteCoreAsync(source));
            return source.Task;
        }
        private async Task CompleteCoreAsync(TaskCompletionSource<bool> completion)
        {
            try
            {
                bool changed = false;
                if (mutation is null) changed = owner.CommitEndState(state);
                else await mutation.CommitAsync(() => changed = owner.CommitEndState(state)).ConfigureAwait(false);
                if (changed) owner.CancelAndDetachAfterEnd();
                completion.TrySetResult(true);
            }
            catch (Exception error)
            {
                lock (owner.stateGate) owner.authorityFailure ??= error;
                completion.TrySetException(error);
            }
        }
    }

    public async Task CloseAsync(CancellationToken beforeIntentCancellation = default) =>
        await (await BeginCloseAsync(false, beforeIntentCancellation).ConfigureAwait(false)).CompleteAsync().ConfigureAwait(false);
    public async Task MarkCrashedAsync(CancellationToken beforeIntentCancellation = default) =>
        await (await BeginCloseAsync(true, beforeIntentCancellation).ConfigureAwait(false)).CompleteAsync().ConfigureAwait(false);
    private bool CommitEndState(AppLifecycleState state)
    {
        lock (stateGate)
        {
            if (HasEnded) return false;
            long nextEpoch = checked(instance.Epoch.Value + 1), nextLifecycle = checked(lifecycleRevision + 1), nextEvent = checked(eventRevision + 1);
            AppInstance next = instance with { State = state, Epoch = new(nextEpoch) };
            instance = next;
            savesGranted = false; networkIsolationVerified = false; gestureAvailable = false;
            lifecycleRevision = nextLifecycle; eventRevision = nextEvent;
            return true;
        }
    }
    private void CancelAndDetachAfterEnd()
    {
        try { ended.Cancel(); }
        finally { DetachServices(); }
    }
    private void QuarantineAndCloseOwned()
    {
        lock (stateGate) { if (HasEnded) return; quarantined = true; gestureAvailable = false; }
        OwnAuthorityTask(CloseAsync());
    }
    private void OwnAuthorityTask(Task task)
    {
        lock (stateGate) ownedAuthorityTasks.Add(task);
        _ = task.ContinueWith(completed =>
        {
            lock (stateGate)
            {
                if (completed.Exception is { } error) authorityFailure ??= error.InnerException ?? error;
                ownedAuthorityTasks.Remove(completed);
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public async Task DrainAuthorityOperationsAsync()
    {
        while (true)
        {
            Task[] pending; Exception? failure;
            lock (stateGate) { pending = ownedAuthorityTasks.ToArray(); failure = authorityFailure; }
            if (pending.Length == 0) { if (failure is not null) throw new InvalidOperationException("RUNTIME_AUTHORITY_OPERATION_FAILED", failure); return; }
            try { await Task.WhenAll(pending).ConfigureAwait(false); }
            catch (Exception error) { lock (stateGate) authorityFailure ??= error; }
        }
    }
    private long ReadPermissionRevision() => services is null ? 0 : services.Permissions.GetRevision(services.Account, services.Application);

    public RuntimeResponseStamp CaptureDeliveryStamp() => CaptureInstanceDelivery().Stamp;
    public (AppInstance Instance, RuntimeResponseStamp Stamp) CaptureInstanceDelivery()
    {
        long permission = ReadPermissionRevision();
        IdentitySnapshot? identity = services?.Identity?.Snapshot;
        lock (stateGate) return (instance, StampLocked(permission, identity));
    }
    private RuntimeResponseStamp StampLocked(long permission, IdentitySnapshot? identity) => new(this, instance.Id.Value,
        instance.Epoch.Value, lifecycleRevision, permissionRevision, permission, identity, checked(eventRevision + permission));

    public async Task<string> HandleMessageAsync(string source, string json,
        Func<string, CancellationToken, Task<bool>> requestPermission, CancellationToken cancellationToken = default)
        => (await HandleMessageForDeliveryAsync(source, json, requestPermission, cancellationToken).ConfigureAwait(false)).Response;

    public async Task<RuntimePreparedResponse> HandleMessageForDeliveryAsync(string source, string json,
        Func<string, CancellationToken, Task<bool>> requestPermission, CancellationToken cancellationToken = default)
    {
        var capture = new RequestAuthorityCapture();
        RequestAuthorityCapture? previous = requestAuthority.Value;
        requestAuthority.Value = capture;
        try
        {
            string response = await HandleMessageCoreAsync(source, json, requestPermission, cancellationToken).ConfigureAwait(false);
            RuntimeResponseStamp stamp = (capture.Frozen ?? CaptureDeliveryStamp()).Bind(capture.Method, ResponseHash(response));
            lock (stateGate)
            {
                string key = stamp.ResponseHash!;
                if (!preparedResponses.ContainsKey(key)) preparedResponseOrder.Enqueue(key);
                preparedResponses[key] = stamp;
                while (preparedResponseOrder.Count > MaximumRememberedRequestIds) preparedResponses.Remove(preparedResponseOrder.Dequeue());
            }
            return new(response, stamp);
        }
        finally { requestAuthority.Value = previous; }
    }

    private void FreezePrivateResponseAuthority()
    {
        if (requestAuthority.Value is { } capture) capture.Frozen ??= CaptureDeliveryStamp();
    }
    private bool FindPreparedResponse(string response, out RuntimeResponseStamp stamp)
    { lock (stateGate) return preparedResponses.TryGetValue(ResponseHash(response), out stamp!); }
    private static string ResponseHash(string response) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(response)));

    private string? StampErrorLocked(RuntimeResponseStamp stamp, long permission, IdentitySnapshot? identity)
    {
        if (!ReferenceEquals(stamp.Owner, this)) return "INVALID_RESPONSE";
        if (!IsActive || servicesDetached || stamp.InstanceId != instance.Id.Value || stamp.InstanceEpoch != instance.Epoch.Value || stamp.Lifecycle != lifecycleRevision)
            return "SESSION_EXPIRED";
        if (stamp.GuestPermission != permissionRevision || stamp.Permission != permission) return "PERMISSION_REVOKED";
        if (stamp.Identity != identity) return "SESSION_EXPIRED";
        return null;
    }
    private string RevalidatePreparedLocked(string method, RuntimeResponseStamp stamp, string response, long permission, IdentitySnapshot? identity)
    {
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(response);
            JsonElement root = document.RootElement; id = root.GetProperty("requestId").GetString();
            if (stamp.ResponseHash != ResponseHash(response) || stamp.Method is { } bound && bound != method) return Error(id, "INVALID_RESPONSE");
            if (StampErrorLocked(stamp, permission, identity) is { } stale) return Error(id, stale);
            services?.Account.EnsureCurrent();
            if (!root.GetProperty("ok").GetBoolean()) return response;
            if (services is null)
            {
                if (ResponsePermission(method) == "saves")
                {
                    if (savesRevoked) return Error(id, "PERMISSION_REVOKED");
                    if (!savesGranted) return Error(id, "PERMISSION_DENIED");
                }
                if (method is "permissions.query" or "permissions.request")
                {
                    string current = savesRevoked ? "revoked" : savesGranted ? "granted" : savesDenied ? "denied" : "prompt";
                    if (root.GetProperty("result").GetProperty("state").GetString() != current) return Error(id, "PERMISSION_REVOKED");
                }
                return response;
            }
            if (ResponsePermission(method) is { } required) services.Permissions.Demand(services.Account, services.Application, required);
            if (method.StartsWith("identity.", StringComparison.Ordinal)) CheckAccount(services);
            if (method is "permissions.query" or "permissions.request")
            {
                JsonElement result = root.GetProperty("result");
                string current = services.Permissions.Query(services.Account, services.Application, result.GetProperty("name").GetString()!).ToString().ToLowerInvariant();
                if (current != result.GetProperty("state").GetString()) return Error(id, "PERMISSION_REVOKED");
            }
            return response;
        }
        catch (RuntimeCapabilityException error) { return Error(id, error.Code); }
        catch (Exception error) when (error is JsonException or InvalidOperationException or FormatException or ArgumentException)
        { return Error(id, "INVALID_RESPONSE"); }
    }

    public string RevalidateResponse(string method, RuntimeResponseStamp stamp, string response)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        long permission = ReadPermissionRevision(); IdentitySnapshot? identity = services?.Identity?.Snapshot;
        lock (stateGate) return RevalidatePreparedLocked(method, stamp, response, permission, identity);
    }
    public string DeliverResponse(string method, RuntimeResponseStamp stamp, string response, Action<string> post)
        => WithResponseAuthority(method, stamp, response, post);

    private string WithResponseAuthority(string method, RuntimeResponseStamp stamp, string response, Action<string> post)
    {
        ArgumentNullException.ThrowIfNull(stamp); ArgumentNullException.ThrowIfNull(post);
        IDisposable? permissionLease = null, identityLease = null;
        try
        {
            if (services is { } runtime)
            {
                using var parsed = JsonDocument.Parse(response);
                if (parsed.RootElement.GetProperty("ok").GetBoolean() && ResponsePermission(method) is { } required)
                    permissionLease = runtime.Permissions.EnterUsageLease(runtime.Account, runtime.Application, required);
                else if (runtime.Application.DeclaredPermissions.FirstOrDefault() is { } declaredPermission)
                    permissionLease = runtime.Permissions.EnterDecisionLease(runtime.Account, runtime.Application, declaredPermission);
            }
            long permission = ReadPermissionRevision();
            lock (stateGate)
            {
                if (services is not null && IsActive) identityLease = services.Account.EnterCommitLease?.Invoke();
                IdentitySnapshot? identity = services?.Identity?.Snapshot;
                string validated = RevalidatePreparedLocked(method, stamp, response, permission, identity);
                if (StampErrorLocked(stamp, permission, identity) is null) post(validated);
                return validated;
            }
        }
        catch (RuntimeCapabilityException error) { return Error(ResponseRequestId(response), error.Code); }
        finally { identityLease?.Dispose(); permissionLease?.Dispose(); }
    }

    public bool DeliverEvent(string name, RuntimeResponseStamp stamp, Action post)
        => WithEventAuthority(name, stamp, post);

    private bool WithEventAuthority(string name, RuntimeResponseStamp stamp, Action post)
    {
        ArgumentNullException.ThrowIfNull(stamp); ArgumentNullException.ThrowIfNull(post);
        if (name is not ("identity.changed" or "links.opened" or "shortcuts.invoked" or "appearance.changed" or "permissions.changed" or "lifecycle.changed")) return false;
        IDisposable? permissionLease = null, identityLease = null;
        try
        {
            if (services is { } runtime)
            {
                string? required = name switch { "identity.changed" => "identity.profile", "links.opened" => "links", "shortcuts.invoked" => "shortcuts", _ => null };
                if (required is not null) permissionLease = runtime.Permissions.EnterUsageLease(runtime.Account, runtime.Application, required);
                else if (runtime.Application.DeclaredPermissions.FirstOrDefault() is { } declaredPermission)
                    permissionLease = runtime.Permissions.EnterDecisionLease(runtime.Account, runtime.Application, declaredPermission);
            }
            else if (name != "lifecycle.changed") return false;
            long permission = ReadPermissionRevision();
            lock (stateGate)
            {
                if (services is not null && IsActive) identityLease = services.Account.EnterCommitLease?.Invoke();
                IdentitySnapshot? identity = services?.Identity?.Snapshot;
                if (StampErrorLocked(stamp, permission, identity) is not null) return false;
                services?.Account.EnsureCurrent();
                if (name == "identity.changed" && services is not null) CheckAccount(services);
                post(); return true;
            }
        }
        catch (RuntimeCapabilityException) { return false; }
        catch (ObjectDisposedException) { return false; }
        finally { identityLease?.Dispose(); permissionLease?.Dispose(); }
    }
    private static string? ResponseRequestId(string response)
    {
        try { using var value = JsonDocument.Parse(response); return value.RootElement.GetProperty("requestId").GetString(); }
        catch (JsonException) { return null; }
    }
}
