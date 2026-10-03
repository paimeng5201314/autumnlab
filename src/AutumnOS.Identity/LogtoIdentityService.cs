using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using AutumnOS.Contracts;

namespace AutumnOS.Identity;

/// <summary>One host-owned identity coordinator. Tokens never leave this assembly's internal session objects.</summary>
[SupportedOSPlatform("windows")]
public sealed class LogtoIdentityService : IIdentityService, IDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly LogtoPublicOptions _options;
    private readonly ICredentialVault _vault;
    private readonly INativeOidcFlow _flow;
    private readonly TimeProvider _clock;
    private readonly Func<string, IDisposable>? _enterCriticalOperation;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _epochCancellation = new();
    private CancellationTokenSource? _login;
    private Task<IdentitySnapshot>? _initialization;
    private ProtectedIdentitySession? _session;
    private bool _disposed;
    private bool _persistenceFailure;
    private IdentitySnapshot _snapshot = new(IdentitySessionState.SignedOut, "guest", 0, null, null, false, false, null);
    private readonly Task _refreshLoop;
    public LogtoIdentityService(LogtoPublicOptions options, string dataRoot, Func<string, IDisposable>? enterCriticalOperation = null)
        : this(options, new WindowsCredentialVault(dataRoot, options), new NativeOidcFlow(options), enterCriticalOperation: enterCriticalOperation) { }
    internal LogtoIdentityService(LogtoPublicOptions options, ICredentialVault vault, INativeOidcFlow flow, TimeProvider? clock = null,
        Func<string, IDisposable>? enterCriticalOperation = null)
    {
        _options = options; _vault = vault; _flow = flow; _clock = clock ?? TimeProvider.System;
        _enterCriticalOperation = enterCriticalOperation;
        _refreshLoop = AutomaticRefreshAsync(_lifetime.Token);
    }
    public IdentitySnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public event EventHandler<IdentitySnapshot>? Changed;

    /// <summary>Linearizes a synchronous final data commit with account mutation. Never await while held.</summary>
    public IDisposable EnterSessionLease(string accountNamespace, long sessionEpoch)
    {
        Monitor.Enter(_gate);
        try
        {
            ThrowIfDisposed();
            if (_snapshot.AccountNamespace != accountNamespace || _snapshot.SessionEpoch != sessionEpoch)
                throw new RuntimeCapabilityException("SESSION_EXPIRED");
            return new SessionLease(_gate);
        }
        catch { Monitor.Exit(_gate); throw; }
    }
    private sealed class SessionLease(object gate) : IDisposable
    {
        private object? _held = gate;
        public void Dispose() { object? held = Interlocked.Exchange(ref _held, null); if (held is not null) Monitor.Exit(held); }
    }

    public Task<IdentitySnapshot> InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return _initialization ??= InitializeCoreAsync(cancellationToken);
        }
    }
    private async Task<IdentitySnapshot> InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        // Reading the DPAPI vault may repair its restrictive ACL; it participates in maintenance too.
        using (IDisposable? operation = TryEnterCriticalOperation("恢复身份凭据"))
        {
            if (operation is null) return MaintenanceDeferred();
            lock (_gate)
            {
                if (_disposed || _snapshot.SessionEpoch != 0) return _snapshot;
                try
                {
                    ProtectedIdentitySession? stored = _vault.Read();
                    if (stored is null) return _snapshot;
                    ValidateStored(stored);
                    _session = stored;
                    _snapshot = MakeSnapshot(stored, IdentitySessionState.OfflineCached, 1, true, "OFFLINE");
                }
                catch (Exception error) when (SafeFailure(error))
                {
                    _snapshot = new(IdentitySessionState.SessionExpired, "guest", 1, null, null, false, false,
                        error is IdentityFlowException flow ? flow.Code : "AUTH_CREDENTIALS_UNREADABLE");
                }
            }
        }
        Publish();
        return _session is null ? Snapshot : await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IdentitySnapshot> SignInAsync(bool rememberSignIn, bool reauthenticate = false, CancellationToken cancellationToken = default)
    {
        // Keep the lease through callback, token validation and durable commit. A browser login that is
        // not safely abortable defers maintenance; the updater never cancels it or discards its result.
        using IDisposable? operation = TryEnterCriticalOperation("登录回调与身份提交");
        if (operation is null) return MaintenanceDeferred();
        long epoch;
        CancellationTokenSource login;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_login is not null) return _snapshot;
            epoch = AdvanceEpoch();
            _session = null;
            try { _vault.Clear(); _persistenceFailure = false; }
            catch (Exception error) when (SafeFailure(error))
            {
                _persistenceFailure = true;
                _snapshot = new(IdentitySessionState.SignedOut, "guest", epoch, null, null, false, false, "AUTH_CREDENTIALS_WRITE_FAILED");
                return _snapshot;
            }
            login = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _epochCancellation.Token, _lifetime.Token);
            login.CancelAfter(TimeSpan.FromMinutes(3));
            _login = login;
            _cancelledByUser = false;
            _snapshot = new(IdentitySessionState.SigningIn, "guest", epoch, null, null, rememberSignIn, false, null);
        }
        Publish();
        try
        {
            ProtectedIdentitySession session = await _flow.SignInAsync(rememberSignIn, reauthenticate, login.Token).ConfigureAwait(false);
            lock (_gate)
            {
                if (_disposed || epoch != _snapshot.SessionEpoch) return _snapshot;
                login.Token.ThrowIfCancellationRequested();
                ValidateStored(session);
                // Remembering an existing verified session does not require or fabricate a refresh token.
                // Without one, restoration remains bound to the real access-token lifetime and UserInfo checks.
                _session = session;
                string? persistenceError = PersistCurrentSession(session, rememberSignIn);
                // Local disk failure cannot undo the successfully verified current-process identity.
                _snapshot = MakeSnapshot(session, IdentitySessionState.SignedIn, epoch, rememberSignIn, persistenceError);
            }
        }
        catch (Exception error) when (SafeFailure(error))
        {
            lock (_gate)
            {
                if (!_disposed && epoch == _snapshot.SessionEpoch)
                    _snapshot = new(IdentitySessionState.SignedOut, "guest", epoch, null, null, rememberSignIn, false,
                        ErrorCode(error, cancellationToken.IsCancellationRequested || _cancelledByUser));
            }
        }
        finally
        {
            lock (_gate) { if (ReferenceEquals(_login, login)) { _login = null; _cancelledByUser = false; } }
            login.Dispose();
            Publish();
        }
        return Snapshot;
    }
    private bool _cancelledByUser;
    public void CancelSignIn()
    {
        lock (_gate) { _cancelledByUser = true; _login?.Cancel(); }
    }

    /// <summary>Changes only current-session persistence; never changes account/epoch or requests new scopes.</summary>
    public Task<IdentitySnapshot> SetRememberSignInAsync(bool rememberSignIn, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable? operation = TryEnterCriticalOperation("保持登录偏好及凭据写入");
        if (operation is null) return Task.FromResult(MaintenanceDeferred());
        lock (_gate)
        {
            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();
            if (_login is not null)
                _snapshot = _snapshot with { ErrorCode = "REQUEST_BUSY" };
            else if (_session is null || _snapshot.State is not (IdentitySessionState.SignedIn or IdentitySessionState.OfflineCached))
                _snapshot = _snapshot with { ErrorCode = _snapshot.State == IdentitySessionState.SessionExpired ? "SESSION_EXPIRED" : "AUTH_REQUIRED" };
            else
            {
                try
                {
                    ValidateStored(_session);
                    if (rememberSignIn) _vault.Write(_session);
                    else
                    {
                        // Stop future persistence even if an external disk error prevents confirming durable removal.
                        // The failure code remains explicit; false alone is not proof that the old file was removed.
                        _snapshot = _snapshot with { RememberSignIn = false };
                        _vault.Clear();
                    }
                    _persistenceFailure = false;
                    _snapshot = _snapshot with
                    {
                        RememberSignIn = rememberSignIn,
                        ErrorCode = _snapshot.State == IdentitySessionState.OfflineCached
                            ? (_snapshot.ErrorCode == "AUTH_CREDENTIALS_WRITE_FAILED" ? "OFFLINE" : _snapshot.ErrorCode) : null
                    };
                }
                catch (Exception error) when (SafeFailure(error))
                {
                    _persistenceFailure = true;
                    // Enabling failed: preserve its previous state. Disabling already stopped all future writes.
                    _snapshot = _snapshot with { ErrorCode = "AUTH_CREDENTIALS_WRITE_FAILED" };
                }
            }
        }
        Publish();
        return Task.FromResult(Snapshot);
    }

    public async Task<IdentitySnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Token rotation can happen at the provider before the local write. Admit before starting
            // the request so preparation cannot split rotation from the resulting credential commit.
            using IDisposable? operation = TryEnterCriticalOperation("刷新身份及凭据轮换");
            if (operation is null) return MaintenanceDeferred();
            ProtectedIdentitySession session;
            long epoch;
            CancellationToken epochToken;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_session is null || _login is not null) return _snapshot;
                session = _session; epoch = _snapshot.SessionEpoch;
                epochToken = _epochCancellation.Token;
            }
            using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, epochToken, _lifetime.Token);
            request.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                ProtectedIdentitySession refreshed = await _flow.RefreshAsync(session, request.Token).ConfigureAwait(false);
                lock (_gate)
                {
                    if (_disposed || _snapshot.SessionEpoch != epoch) return _snapshot;
                    request.Token.ThrowIfCancellationRequested();
                    ValidateStored(refreshed);
                    if (refreshed.Subject != session.Subject || refreshed.Issuer != session.Issuer) throw new IdentityFlowException("AUTH_INVALID_TOKEN");
                    // Re-read under the commit gate: a user may have turned persistence off while HTTP was pending.
                    bool remember = _snapshot.RememberSignIn;
                    _session = refreshed;
                    string? persistenceError = PersistCurrentSession(refreshed, remember);
                    _snapshot = MakeSnapshot(refreshed, IdentitySessionState.SignedIn, epoch, remember, persistenceError);
                }
            }
            catch (Exception error) when (SafeFailure(error))
            {
                lock (_gate)
                {
                    if (_disposed || _snapshot.SessionEpoch != epoch) return _snapshot;
                    string code = ErrorCode(error, cancellationToken.IsCancellationRequested);
                    if (error is IdentityRefreshException partial)
                    {
                        // Rotation may already be committed by the provider before a temporary UserInfo failure.
                        // Preserve only the validated same-account result, never a late result from a prior epoch.
                        bool valid = true;
                        try
                        {
                            ValidateStored(partial.RefreshedSession);
                            if (partial.RefreshedSession.Subject != session.Subject || partial.RefreshedSession.Issuer != session.Issuer)
                                throw new IdentityFlowException("AUTH_INVALID_TOKEN");
                        }
                        catch (Exception validationError) when (SafeFailure(validationError)) { valid = false; code = "AUTH_INVALID_TOKEN"; }
                        if (valid)
                        {
                            _session = partial.RefreshedSession;
                            string? persistenceError = PersistCurrentSession(_session, _snapshot.RememberSignIn);
                            _snapshot = MakeSnapshot(_session, IdentitySessionState.OfflineCached, epoch,
                                _snapshot.RememberSignIn, persistenceError ?? code);
                            return _snapshot;
                        }
                    }
                    if (code == "USER_CANCELLED") return _snapshot;
                    bool offline = code is "OFFLINE" or "AUTH_TIMEOUT" or "AUTH_METADATA_UNAVAILABLE" or "AUTH_PROVIDER_UNAVAILABLE";
                    if (!offline)
                    {
                        _session = session with { AccessToken = "", RefreshToken = null, IdentityToken = "" };
                        try { _vault.Clear(); _persistenceFailure = false; }
                        catch (Exception clearError) when (SafeFailure(clearError)) { _persistenceFailure = true; }
                    }
                    _snapshot = MakeSnapshot(_session!, offline ? IdentitySessionState.OfflineCached : IdentitySessionState.SessionExpired,
                        epoch, _snapshot.RememberSignIn, _persistenceFailure ? "AUTH_CREDENTIALS_WRITE_FAILED" : code);
                }
            }
        }
        finally { _refresh.Release(); Publish(); }
        return Snapshot;
    }

    public async Task<IdentitySnapshot> SignOutAsync(bool browserSession = false, CancellationToken cancellationToken = default)
    {
        using IDisposable? operation = TryEnterCriticalOperation("账号退出与凭据清理");
        if (operation is null) return MaintenanceDeferred();
        ProtectedIdentitySession? previous;
        long epoch;
        lock (_gate)
        {
            ThrowIfDisposed();
            previous = _session;
            epoch = AdvanceEpoch();
            _session = null;
            string? failure = null;
            try { _vault.Clear(); _persistenceFailure = false; }
            catch (Exception error) when (SafeFailure(error)) { _persistenceFailure = true; failure = "AUTH_CREDENTIALS_WRITE_FAILED"; }
            _snapshot = new(failure is null ? IdentitySessionState.SignedOut : IdentitySessionState.SessionExpired,
                "guest", epoch, null, null, false, false, failure);
        }
        Publish();
        if (browserSession)
        {
            using CancellationTokenSource logout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            logout.CancelAfter(TimeSpan.FromMinutes(2));
            try
            {
                if (previous is null || string.IsNullOrEmpty(previous.IdentityToken)) throw new IdentityFlowException("AUTH_REQUIRED");
                await _flow.SignOutBrowserAsync(previous, logout.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (SafeFailure(error))
            {
                lock (_gate) if (_snapshot.SessionEpoch == epoch) _snapshot = _snapshot with
                { ErrorCode = _persistenceFailure ? "AUTH_CREDENTIALS_WRITE_FAILED" : "AUTH_BROWSER_LOGOUT_INCOMPLETE" };
            }
            Publish();
        }
        return Snapshot;
    }
    private async Task AutomaticRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            using PeriodicTimer timer = new(TimeSpan.FromSeconds(30), _clock);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                bool needed;
                lock (_gate) needed = !_disposed && _login is null && _session is not null &&
                    _snapshot.State is IdentitySessionState.SignedIn or IdentitySessionState.OfflineCached &&
                    _session.ExpiresAt <= _clock.GetUtcNow().AddMinutes(1);
                if (needed) await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }
    private long AdvanceEpoch()
    {
        _epochCancellation.Cancel(); _epochCancellation.Dispose(); _epochCancellation = new();
        _login?.Cancel();
        return checked(_snapshot.SessionEpoch + 1);
    }
    private IdentitySnapshot MakeSnapshot(ProtectedIdentitySession session, IdentitySessionState state, long epoch, bool remember, string? code) =>
        new(state, AccountNamespaceFor(session.Issuer, session.Subject), epoch, session.DisplayName, session.AvatarUrl, remember,
            !string.IsNullOrEmpty(session.RefreshToken), code);
    // Called under _gate. A failed removal remains visible across refreshes until a real vault operation succeeds.
    private string? PersistCurrentSession(ProtectedIdentitySession session, bool remember)
    {
        if (remember)
        {
            try { _vault.Write(session); _persistenceFailure = false; }
            catch (Exception error) when (SafeFailure(error)) { _persistenceFailure = true; }
        }
        return _persistenceFailure ? "AUTH_CREDENTIALS_WRITE_FAILED" : null;
    }
    internal static string AccountNamespaceFor(string issuer, string subject) => "account-" +
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(issuer + "\n" + subject)));
    private void ValidateStored(ProtectedIdentitySession session)
    {
        if (session.Format != 1 || session.Issuer != _options.Authority.AbsoluteUri || session.ClientId != _options.ClientId ||
            session.Subject is not { Length: > 0 and <= 512 } || session.Subject.Any(char.IsControl) ||
            session.AccessToken is not { Length: > 0 and <= 32768 } || session.IdentityToken is not { Length: > 0 and <= 32768 } ||
            session.RefreshToken?.Length > 32768 || session.Nonce is not { Length: >= 16 and <= 128 } ||
            session.DisplayName != NativeOidcFlow.SafeName(session.DisplayName) || session.AvatarUrl != NativeOidcFlow.SafeAvatar(session.AvatarUrl))
            throw new IdentityFlowException("AUTH_CREDENTIALS_UNREADABLE");
    }
    private static bool SafeFailure(Exception error) => error is IdentityFlowException or IdentityRefreshException or IOException or UnauthorizedAccessException or
        CryptographicException or System.Text.Json.JsonException or HttpRequestException or OperationCanceledException or InvalidOperationException or ArgumentException;
    private static string ErrorCode(Exception error, bool userCancelled) => error switch
    {
        IdentityFlowException flow => flow.Code,
        IdentityRefreshException partial => userCancelled ? "USER_CANCELLED" : partial.Code,
        OperationCanceledException => userCancelled ? "USER_CANCELLED" : "AUTH_TIMEOUT",
        HttpRequestException => "OFFLINE",
        IOException or UnauthorizedAccessException or CryptographicException => "AUTH_CREDENTIALS_WRITE_FAILED",
        _ => "AUTH_INVALID_RESPONSE"
    };
    private IDisposable? TryEnterCriticalOperation(string reason)
    {
        try { return _enterCriticalOperation?.Invoke(reason) ?? NoopLease.Instance; }
        catch (IOException error) when (error.Message == "MAINTENANCE_IN_PROGRESS") { return null; }
    }
    private IdentitySnapshot MaintenanceDeferred()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _snapshot = _snapshot with { ErrorCode = "MAINTENANCE_IN_PROGRESS" };
        }
        Publish();
        return Snapshot;
    }
    private sealed class NoopLease : IDisposable
    {
        internal static readonly NoopLease Instance = new();
        public void Dispose() { }
    }
    private void Publish()
    {
        IdentitySnapshot state = Snapshot;
        foreach (EventHandler<IdentitySnapshot> observer in Changed?.GetInvocationList().Cast<EventHandler<IdentitySnapshot>>() ?? [])
        { try { observer(this, state); } catch (Exception) { /* UI observers cannot turn verified results into uncaught authentication faults. */ } }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _lifetime.Cancel(); _epochCancellation.Cancel(); _login?.Cancel(); _session = null; Changed = null;
        }
        // Do not synchronously block the UI on callback/HTTP/refresh completion.
    }
}
