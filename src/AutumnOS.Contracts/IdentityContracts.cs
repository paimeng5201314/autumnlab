namespace AutumnOS.Contracts;

/// <summary>Host-only account state. This interface never exposes bearer credentials or raw claims.</summary>
public enum IdentitySessionState { SignedOut, SigningIn, SignedIn, SessionExpired, OfflineCached }

public sealed record IdentitySnapshot(IdentitySessionState State, string AccountNamespace, long SessionEpoch,
    string? DisplayName, string? AvatarUrl, bool RememberSignIn, bool CanRefresh, string? ErrorCode);

/// <summary>Trusted host contract. Application SDK receives only separately authorized, app-scoped profile fields.</summary>
public interface IIdentityService
{
    IdentitySnapshot Snapshot { get; }
    event EventHandler<IdentitySnapshot>? Changed;
    Task<IdentitySnapshot> InitializeAsync(CancellationToken cancellationToken = default);
    Task<IdentitySnapshot> SignInAsync(bool rememberSignIn, bool reauthenticate = false, CancellationToken cancellationToken = default);
    /// <summary>Persists or forgets the current verified session without changing accounts or requesting new scopes.</summary>
    Task<IdentitySnapshot> SetRememberSignInAsync(bool rememberSignIn, CancellationToken cancellationToken = default);
    Task<IdentitySnapshot> RefreshAsync(CancellationToken cancellationToken = default);
    Task<IdentitySnapshot> SignOutAsync(bool browserSession = false, CancellationToken cancellationToken = default);
    void CancelSignIn();
    /// <summary>Synchronous host commit guard. Dispose on the owning thread; never hold across await.</summary>
    IDisposable EnterSessionLease(string accountNamespace, long sessionEpoch);
}
