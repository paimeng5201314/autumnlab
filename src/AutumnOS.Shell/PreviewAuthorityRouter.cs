using AutumnOS.Contracts;

namespace AutumnOS.Shell;

// Host-created account bindings select the authority; application input never
// selects it. Live previews share the real identity/permission coordinator so
// a real revocation fences their inherited decisions before it commits.
internal sealed class PreviewAuthorityRouter(IRuntimeAuthorityMutations live,
    IRuntimeAuthorityMutations simulation) : IRuntimeAuthorityMutations
{
    private readonly object gate = new();
    private RuntimeMutationScope? previewInstance;
    private object generation = new();

    // Set by the native composition root before endpoint registration. The UI
    // retires the previous preview before starting/rebinding a new instance.
    // Simulation identity changes must fence this endpoint's actual coordinator:
    // a local response lease cannot cover the later asynchronous IPC/Post.
    internal void BindInstanceScope(RuntimeMutationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.Kind != RuntimeMutationScopeKind.Instance || string.IsNullOrWhiteSpace(scope.AccountKey) ||
            string.IsNullOrWhiteSpace(scope.ApplicationBinding) || scope.InstanceId is null || scope.InstanceId == Guid.Empty)
            throw new ArgumentException("PREVIEW_INSTANCE_SCOPE_REQUIRED", nameof(scope));
        lock (gate) { previewInstance = scope; generation = new(); }
    }

    private (RuntimeMutationScope Scope, object? Generation) Resolve(RuntimeMutationScope scope)
    {
        lock (gate) return scope.Kind == RuntimeMutationScopeKind.Identity
            ? (previewInstance ?? scope, generation) : (scope, null);
    }

    internal IRuntimeAuthorityMutations For(RuntimeMutationScope scope)
        => Select(Resolve(scope).Scope);

    private IRuntimeAuthorityMutations Select(RuntimeMutationScope scope)
        => scope.Kind == RuntimeMutationScopeKind.Identity ||
           scope.AccountKey?.StartsWith("developer-preview:", StringComparison.Ordinal) == true ? simulation : live;

    public async ValueTask<IRuntimeAuthorityMutation> BeginAsync(RuntimeMutationScope scope,
        CancellationToken beforeIntentCancellation = default)
    {
        var resolved = Resolve(scope);
        var mutation = await Select(resolved.Scope).BeginAsync(resolved.Scope, beforeIntentCancellation).ConfigureAwait(false);
        return resolved.Generation is null ? mutation : new BoundMutation(this, mutation, resolved.Generation);
    }

    public bool TryCommitSynchronously(RuntimeMutationScope scope, Action shortCommit)
    {
        var resolved = Resolve(scope);
        return Select(resolved.Scope).TryCommitSynchronously(resolved.Scope,
            resolved.Generation is null ? shortCommit : () => CommitBound(resolved.Generation, shortCommit));
    }

    private void CommitBound(object expected, Action commit)
    {
        lock (gate)
        {
            if (!ReferenceEquals(generation, expected)) throw new RuntimeCapabilityException("DEVELOPER_PREVIEW_SESSION_EXPIRED");
            commit(); // The underlying permit is consumed even when this generation is stale.
        }
    }

    private sealed class BoundMutation(PreviewAuthorityRouter owner, IRuntimeAuthorityMutation inner, object expected) : IRuntimeAuthorityMutation
    {
        public Task CommitAsync(Action shortCommit) => inner.CommitAsync(() => owner.CommitBound(expected, shortCommit));
    }
}
