using AutumnOS.Contracts;

namespace AutumnOS.Storage;

/// <summary>Optional onboarding login; owns no credentials and never substitutes progress for session validity.</summary>
public sealed class FirstRunAccountCoordinator(FirstRunStateStore store, IIdentityService? identity) : IDisposable
{
    private readonly object gate = new();
    private CancellationTokenSource? attempt;
    private Task<FirstRunResult>? login;
    private Task<FirstRunResult>? skip;
    private long generation;
    private bool disposed;

    public Task<FirstRunResult> SignInAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (login is not null) return login;
            if (skip is not null) return skip;
            var current = store.Load(cancellationToken);
            if (!current.Success || current.State!.Checkpoint != FirstRunCheckpoint.Initializing ||
                current.State.LoginStep != FirstRunLoginStep.Pending || identity is null) return Task.FromResult(current);
            attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            return login = SignInCoreAsync(++generation, attempt);
        }
    }

    private async Task<FirstRunResult> SignInCoreAsync(long captured, CancellationTokenSource cancellation)
    {
        await Task.Yield();
        try
        {
            cancellation.Token.ThrowIfCancellationRequested();
            IdentitySnapshot result = await identity!.SignInAsync(true, cancellationToken: cancellation.Token).ConfigureAwait(false);
            lock (gate)
            {
                // Includes providers returning after cancellation and a skip racing the successful callback.
                if (disposed || captured != generation || cancellation.IsCancellationRequested ||
                    result.State != IdentitySessionState.SignedIn || identity.Snapshot.State != IdentitySessionState.SignedIn ||
                    result.SessionEpoch != identity.Snapshot.SessionEpoch) return store.Load();
                return store.RecordLoginStep(FirstRunLoginStep.Completed, cancellation.Token);
            }
        }
        finally
        {
            lock (gate)
            {
                if (ReferenceEquals(attempt, cancellation)) attempt = null;
                login = null;
            }
            cancellation.Dispose();
        }
    }

    public Task<FirstRunResult> SkipAsync(CancellationToken cancellationToken = default)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (skip is not null) return skip;
            var current = store.Load(cancellationToken);
            if (!current.Success || current.State!.Checkpoint != FirstRunCheckpoint.Initializing ||
                current.State.LoginStep != FirstRunLoginStep.Pending) return Task.FromResult(current);
            bool pendingLogin = login is not null;
            generation++;
            attempt?.Cancel();
            identity?.CancelSignIn();
            return skip = SkipCoreAsync(pendingLogin, cancellationToken);
        }
    }

    private async Task<FirstRunResult> SkipCoreAsync(bool pendingLogin, CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            // If token validation finished just before the skip, clear that result too. SignOut advances
            // the identity epoch, so a provider ignoring cancellation cannot write a late credential.
            if (pendingLogin && identity is not null)
            {
                var cleared = await identity.SignOutAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
                if (cleared.State != IdentitySessionState.SignedOut || cleared.ErrorCode is not null)
                    return FirstRunResult.Failed(StorageErrors.IoError);
            }
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (disposed) throw new OperationCanceledException(cancellationToken);
                return store.RecordLoginStep(FirstRunLoginStep.Skipped, cancellationToken);
            }
        }
        finally { lock (gate) skip = null; }
    }

    public void CancelSignIn()
    {
        lock (gate) { generation++; attempt?.Cancel(); identity?.CancelSignIn(); }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            generation++;
            attempt?.Cancel();
            identity?.CancelSignIn();
        }
    }
}
