using AutumnOS.Contracts;

namespace AutumnOS.Shell;

// Host-created account bindings select the authority; application input never
// selects it. Live previews share the real identity/permission coordinator so
// a real revocation fences their inherited decisions before it commits.
internal sealed class PreviewAuthorityRouter(IRuntimeAuthorityMutations live,
    IRuntimeAuthorityMutations simulation) : IRuntimeAuthorityMutations
{
    internal IRuntimeAuthorityMutations For(RuntimeMutationScope scope)
        => scope.Kind == RuntimeMutationScopeKind.Identity ||
           scope.AccountKey?.StartsWith("developer-preview:", StringComparison.Ordinal) == true ? simulation : live;

    public ValueTask<IRuntimeAuthorityMutation> BeginAsync(RuntimeMutationScope scope,
        CancellationToken beforeIntentCancellation = default)
        => For(scope).BeginAsync(scope, beforeIntentCancellation);

    public bool TryCommitSynchronously(RuntimeMutationScope scope, Action shortCommit)
        => For(scope).TryCommitSynchronously(scope, shortCommit);
}
