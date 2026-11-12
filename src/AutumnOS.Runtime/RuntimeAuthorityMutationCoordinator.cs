using AutumnOS.Contracts;

namespace AutumnOS.Runtime;

/// <summary>Host-owned authority transaction adapter. This does not install a native endpoint or generate OS evidence.</summary>
public sealed class RuntimeAuthorityMutationCoordinator : IRuntimeAuthorityMutations
{
    private readonly RuntimeDeliveryCoordinator deliveries;
    public Guid IdentityAuthorityId { get; }
    internal RuntimeDeliveryCoordinator Deliveries => deliveries;

    public RuntimeAuthorityMutationCoordinator() : this(Guid.NewGuid(), new()) { }

    internal RuntimeAuthorityMutationCoordinator(Guid identityAuthorityId, RuntimeDeliveryCoordinator deliveries)
    {
        if (identityAuthorityId == Guid.Empty) throw new ArgumentException("AUTHORITY_ID_REQUIRED", nameof(identityAuthorityId));
        ArgumentNullException.ThrowIfNull(deliveries);
        IdentityAuthorityId = identityAuthorityId;
        this.deliveries = deliveries;
    }

    public async ValueTask<IRuntimeAuthorityMutation> BeginAsync(RuntimeMutationScope scope,
        CancellationToken beforeIntentCancellation = default)
    {
        RuntimeDeliveryCoordinator.FenceOperation operation = await deliveries.BeginFenceAsync(Map(scope),
            beforeIntentCancellation).ConfigureAwait(false);
        return new Mutation(operation);
    }

    public bool TryCommitSynchronously(RuntimeMutationScope scope, Action shortCommit)
        => deliveries.TryCommitSynchronously(Map(scope), shortCommit);

    // Trusted host composition only. The future native backend must retain the
    // original witness/decoder and cannot register under another authority.
    internal RuntimeDeliveryCoordinator.HostRegistration RegisterHostEndpoint(RuntimeDeliveryBinding binding,
        IOrderedRuntimeDeliveryDriver driver, IOriginalRuntimeEndpointLifetime lifetime)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.IdentityAuthorityId != IdentityAuthorityId)
            throw new ArgumentException("AUTHORITY_ENDPOINT_BINDING_MISMATCH", nameof(binding));
        return deliveries.RegisterHostEndpoint(binding, driver, lifetime);
    }

    private RuntimeAuthorityScope Map(RuntimeMutationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        RuntimeAuthorityScopeKind kind = scope.Kind switch
        {
            RuntimeMutationScopeKind.Identity => RuntimeAuthorityScopeKind.Identity,
            RuntimeMutationScopeKind.Application => RuntimeAuthorityScopeKind.Application,
            RuntimeMutationScopeKind.Instance => RuntimeAuthorityScopeKind.Instance,
            _ => throw new ArgumentException("AUTHORITY_SCOPE_INVALID", nameof(scope))
        };
        return new(kind, IdentityAuthorityId, scope.AccountKey, scope.ApplicationBinding, scope.InstanceId);
    }

    private sealed class Mutation(RuntimeDeliveryCoordinator.FenceOperation operation) : IRuntimeAuthorityMutation
    {
        private int claimed;
        public Task CommitAsync(Action shortCommit)
        {
            ArgumentNullException.ThrowIfNull(shortCommit);
            if (Interlocked.CompareExchange(ref claimed, 1, 0) != 0)
                throw new InvalidOperationException("AUTHORITY_MUTATION_ALREADY_USED");
            return CompleteAsync(shortCommit);
        }

        private async Task CompleteAsync(Action shortCommit)
        {
            // Intent has already been installed. No cancellation, timeout or
            // transport failure can replace the actual fence/retirement proof.
            RuntimeDeliveryCoordinator.MutationPermit permit = await operation.WaitCommitPermitAsync().ConfigureAwait(false);
            permit.Commit(shortCommit);
        }
    }
}
