namespace AutumnOS.Contracts;

// Trusted host composition only. Never deserialize these scopes or operations
// from an application message, or expose them through the application SDK.
public enum RuntimeMutationScopeKind { Identity, Application, Instance }

public sealed record RuntimeMutationScope(RuntimeMutationScopeKind Kind,
    string? AccountKey = null, string? ApplicationBinding = null, Guid? InstanceId = null)
{
    public static RuntimeMutationScope ForIdentity() => new(RuntimeMutationScopeKind.Identity);
    public static RuntimeMutationScope ForApplication(string accountKey, string applicationBinding)
        => new(RuntimeMutationScopeKind.Application, accountKey, applicationBinding);
    public static RuntimeMutationScope ForInstance(string accountKey, string applicationBinding, Guid instanceId)
        => new(RuntimeMutationScopeKind.Instance, accountKey, applicationBinding, instanceId);
}

public interface IRuntimeAuthorityMutations
{
    // Cancellation applies only before writer intent is registered. Once this
    // returns, the owner must retain and settle the operation, including no-op
    // settlement when the original action is no longer applicable.
    ValueTask<IRuntimeAuthorityMutation> BeginAsync(RuntimeMutationScope scope,
        CancellationToken beforeIntentCancellation = default);

    // Legacy local-only path. False means no intent or mutation was performed.
    // Never wait for a native ACK here; callers needing it must use BeginAsync.
    bool TryCommitSynchronously(RuntimeMutationScope scope, Action shortCommit);
}

public interface IRuntimeAuthorityMutation
{
    // One-shot, including when shortCommit throws. Waiting after intent is not
    // cancellable. Recheck authority prerequisites inside shortCommit under its
    // existing lock; never await or publish/cancel callbacks inside that action.
    // A close owner may start native cleanup between BeginAsync and this call.
    Task CommitAsync(Action shortCommit);
}
