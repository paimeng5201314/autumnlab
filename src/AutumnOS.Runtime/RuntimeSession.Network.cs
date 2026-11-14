using AutumnOS.Contracts;

namespace AutumnOS.Runtime;

public sealed partial class RuntimeSession
{
    private int networkRequests;
    public const int MaximumNetworkRequests = 2;

    private async Task<object> RequestNetworkAsync(RuntimeNetworkRequest request, CancellationToken cancellationToken)
    {
        var runtime = services!;
        runtime.Permissions.Demand(runtime.Account, runtime.Application, "network");
        if (runtime.Network is null) throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
        long revision, suspended;
        Guid instanceId;
        long epoch;
        string scope;
        IdentitySnapshot? identity;
        CancellationToken policy;
        using (EnterCommitLease("network"))
        {
            policy = runtime.NetworkPolicyInvalidated?.Invoke() ?? default;
            using var policyLease = runtime.EnterNetworkPolicyLease?.Invoke(policy);
            if (!networkIsolationVerified) throw new RuntimeCapabilityException("NETWORK_ISOLATION_REQUIRED");
            if (instance.State == AppLifecycleState.Suspended) throw new RuntimeCapabilityException("SESSION_SUSPENDED");
            if (runtime.NetworkDisabled?.Invoke() == true) throw new RuntimeCapabilityException("NETWORK_OFFLINE");
            if (networkRequests >= MaximumNetworkRequests) throw new RuntimeCapabilityException("REQUEST_BUSY");
            identity = runtime.Identity?.Snapshot;
            if (identity?.State == IdentitySessionState.SessionExpired) throw new RuntimeCapabilityException("SESSION_EXPIRED");
            revision = runtime.Permissions.GetRevision(runtime.Account, runtime.Application, "network");
            scope = NetworkDomainPolicy.ScopeHash(runtime.Application.NetworkDomains);
            suspended = suspensionRevision; instanceId = instance.Id.Value; epoch = instance.Epoch.Value;
            policy.ThrowIfCancellationRequested();
            networkRequests++;
        }
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, runtime.Account.Invalidated, policy);
        int completed = 0;
        IDisposable EnterAuthority()
        {
            var lease = EnterCommitLease("network");
            IDisposable? policyLease = null;
            try
            {
                policyLease = runtime.EnterNetworkPolicyLease?.Invoke(policy);
                if (Volatile.Read(ref completed) != 0 || instance.Id.Value != instanceId || instance.Epoch.Value != epoch)
                    throw new RuntimeCapabilityException("SESSION_EXPIRED");
                if (instance.State == AppLifecycleState.Suspended || suspensionRevision != suspended)
                    throw new RuntimeCapabilityException("SESSION_SUSPENDED");
                if (runtime.Identity?.Snapshot != identity || runtime.Identity?.Snapshot.State == IdentitySessionState.SessionExpired)
                    throw new RuntimeCapabilityException("SESSION_EXPIRED");
                if (runtime.Permissions.GetRevision(runtime.Account, runtime.Application, "network") != revision)
                    throw new RuntimeCapabilityException("PERMISSION_REVOKED");
                if (NetworkDomainPolicy.ScopeHash(runtime.Application.NetworkDomains) != scope)
                    throw new RuntimeCapabilityException("NETWORK_POLICY_CHANGED");
                if (runtime.NetworkDisabled?.Invoke() == true) throw new RuntimeCapabilityException("NETWORK_OFFLINE");
                if (policy.IsCancellationRequested || policy != (runtime.NetworkPolicyInvalidated?.Invoke() ?? default))
                    throw new RuntimeCapabilityException("NETWORK_POLICY_CHANGED");
                lifetime.Token.ThrowIfCancellationRequested();
                return new NetworkIoLease(lease, policyLease);
            }
            catch { policyLease?.Dispose(); lease.Dispose(); throw; }
        }
        void Cancel()
        {
            try { lifetime.Cancel(); }
            catch (ObjectDisposedException) { }
            catch (AggregateException) { } // An untrusted transport callback cannot fail host revocation.
        }
        void PermissionChangedDuringRequest(PermissionChange change)
        {
            if (change.AccountKey == runtime.Account.AccountKey && change.BindingKey == runtime.Application.BindingKey && change.Permission == "network")
            {
                try { if (runtime.Permissions.GetRevision(runtime.Account, runtime.Application, "network") != revision) Cancel(); }
                catch (RuntimeCapabilityException) { Cancel(); }
            }
        }
        void IdentityChangedDuringRequest(object? sender, IdentitySnapshot ignored)
        {
            try { if (runtime.Identity?.Snapshot != identity) Cancel(); }
            catch (RuntimeCapabilityException) { Cancel(); }
        }
        runtime.Permissions.Changed += PermissionChangedDuringRequest;
        if (runtime.Identity is not null) runtime.Identity.Changed += IdentityChangedDuringRequest;
        try
        {
            using (EnterAuthority()) FreezePrivateResponseAuthority();
            object result = await runtime.Network.RequestAsync(request, NetworkDomainPolicy.CopyValidated(runtime.Application.NetworkDomains),
                EnterAuthority, lifetime.Token).ConfigureAwait(false);
            using (EnterAuthority()) return result;
        }
        catch
        {
            // Give authority changes their actual error, including revoke/regrant and
            // same-epoch SessionExpired, rather than a transport cancellation/success.
            using (EnterAuthority()) { }
            throw;
        }
        finally
        {
            Interlocked.Exchange(ref completed, 1);
            runtime.Permissions.Changed -= PermissionChangedDuringRequest;
            if (runtime.Identity is not null) runtime.Identity.Changed -= IdentityChangedDuringRequest;
            Cancel();
            lock (stateGate) networkRequests--;
        }
    }
    private sealed class NetworkIoLease(IDisposable authority, IDisposable? policy) : IDisposable
    {
        public void Dispose() { try { policy?.Dispose(); } finally { authority.Dispose(); } }
    }
}
