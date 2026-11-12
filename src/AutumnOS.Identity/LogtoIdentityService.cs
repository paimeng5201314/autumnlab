using System.Runtime.Versioning;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using AutumnOS.Contracts;

namespace AutumnOS.Identity;

/// <summary>One host-owned identity coordinator. Tokens never leave this assembly's internal session objects.</summary>
[SupportedOSPlatform("windows")]
public sealed class LogtoIdentityService : IIdentityService, IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly LogtoPublicOptions _options;
    private readonly ICredentialVault _vault;
    private readonly INativeOidcFlow _flow;
    private readonly TimeProvider _clock;
    private readonly Func<string, IDisposable>? _enterCriticalOperation;
    private readonly IRuntimeAuthorityMutations? _authorityMutations;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _epochCancellation = new();
    private CancellationTokenSource? _login;
    private PendingSignIn? _pendingSignIn;
    private Task<IdentitySnapshot>? _initialization;
    private readonly TaskCompletionSource<IdentitySnapshot> _localSessionReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ProtectedIdentitySession? _session;
    private bool _disposed, _revisionExhausted, _persistenceFailure, _cancelledByUser;
    private IdentitySnapshot _snapshot = new(IdentitySessionState.SignedOut, "guest", 0, null, null, false, false, null);
    private readonly Task _refreshLoop;
    private int _activeOperations;
    private TaskCompletionSource _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _shutdownTask;

    public LogtoIdentityService(LogtoPublicOptions options, string dataRoot, Func<string, IDisposable>? enterCriticalOperation = null,
        IRuntimeAuthorityMutations? authorityMutations = null)
        : this(options, new WindowsCredentialVault(dataRoot, options), new NativeOidcFlow(options),
            enterCriticalOperation: enterCriticalOperation, authorityMutations: authorityMutations) { }
    internal LogtoIdentityService(LogtoPublicOptions options, ICredentialVault vault, INativeOidcFlow flow, TimeProvider? clock = null,
        Func<string, IDisposable>? enterCriticalOperation = null, IRuntimeAuthorityMutations? authorityMutations = null)
    {
        _options = options; _vault = vault; _flow = flow; _clock = clock ?? TimeProvider.System;
        _enterCriticalOperation = enterCriticalOperation; _authorityMutations = authorityMutations;
        _refreshLoop = AutomaticRefreshAsync(_lifetime.Token);
    }
    public IdentitySnapshot Snapshot { get { lock (_gate) return _snapshot; } }
    public event EventHandler<IdentitySnapshot>? Changed;
    public Task<IdentitySnapshot> LocalSessionReady => _localSessionReady.Task;

    /// <summary>Final synchronous data commit only. Never await while held.</summary>
    public IDisposable EnterSessionLease(string accountNamespace, long sessionEpoch)
    {
        Monitor.Enter(_gate);
        try
        {
            ThrowIfDisposed();
            if (_shutdownTask is not null || _snapshot.State == IdentitySessionState.SessionExpired ||
                _snapshot.AccountNamespace != accountNamespace || _snapshot.SessionEpoch != sessionEpoch)
                throw new RuntimeCapabilityException("SESSION_EXPIRED");
            return new SessionLease(_gate);
        }
        catch { Monitor.Exit(_gate); throw; }
    }
    private sealed class SessionLease(object gate) : IDisposable
    {
        private object? held = gate;
        public void Dispose() { object? value = Interlocked.Exchange(ref held, null); if (value is not null) Monitor.Exit(value); }
    }

    public Task<IdentitySnapshot> InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ObjectDisposedException.ThrowIf(_shutdownTask is not null, this);
            return _initialization ??= InitializeCoreAsync(cancellationToken);
        }
    }
    private async Task<IdentitySnapshot> InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        try
        {
            lock (_gate) if (_disposed) return _snapshot;
            using IDisposable? operation = TryEnterCriticalOperation("恢复身份凭据");
            if (operation is null) return await MaintenanceDeferredAsync(cancellationToken).ConfigureAwait(false);
            await CommitAuthorityAsync(after =>
            {
                lock (_gate)
                {
                    if (_disposed || _shutdownTask is not null || _snapshot.SessionEpoch != 0) return;
                    cancellationToken.ThrowIfCancellationRequested();
                    long revision = NextAuthorityRevision(after);
                    try
                    {
                        ProtectedIdentitySession? stored = _vault.Read();
                        if (stored is null) return;
                        ValidateStored(stored);
                        _session = stored;
                        SetSnapshot(MakeSnapshot(stored, IdentitySessionState.OfflineCached, 1, true, "OFFLINE"), revision, after);
                    }
                    catch (Exception error) when (SafeFailure(error))
                    {
                        SetSnapshot(new(IdentitySessionState.SessionExpired, "guest", 1, null, null, false, false,
                            error is IdentityFlowException flow ? flow.Code : "AUTH_CREDENTIALS_UNREADABLE"), revision, after);
                    }
                }
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { _localSessionReady.TrySetResult(Snapshot); }
        lock (_gate) if (_session is null || _disposed || _shutdownTask is not null) return _snapshot;
        return await RefreshAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IdentitySnapshot> SignInAsync(bool rememberSignIn, bool reauthenticate = false, CancellationToken cancellationToken = default)
    {
        using var active = BeginActiveOperation(cancellationToken);
        cancellationToken = active.Token;
        using IDisposable? operation = TryEnterCriticalOperation("登录回调与身份提交");
        if (operation is null) return await MaintenanceDeferredAsync(cancellationToken).ConfigureAwait(false);
        long epoch = 0;
        CancellationTokenSource? login = null;
        PendingSignIn pending;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_shutdownTask is not null || _login is not null || _pendingSignIn is not null) return _snapshot;
            // Reservation is operation ownership only, not an identity commit. It
            // lets CancelSignIn address a login before its initial fence opens.
            _pendingSignIn = pending = new(_snapshot.SessionEpoch);
        }
        try
        {
            await CommitAuthorityAsync(after =>
            {
                lock (_gate)
                {
                    ThrowIfDisposed();
                    if (_shutdownTask is not null || _login is not null || !ReferenceEquals(_pendingSignIn, pending) ||
                        pending.Cancelled || _snapshot.SessionEpoch != pending.Epoch) return;
                    cancellationToken.ThrowIfCancellationRequested();
                    long revision = NextAuthorityRevision(after);
                    epoch = AdvanceEpoch(after);
                    _session = null;
                    try { _vault.Clear(); _persistenceFailure = false; }
                    catch (Exception error) when (SafeFailure(error))
                    {
                        _persistenceFailure = true;
                        SetSnapshot(new(IdentitySessionState.SignedOut, "guest", epoch, null, null, false, false, "AUTH_CREDENTIALS_WRITE_FAILED"), revision, after);
                        return;
                    }
                    login = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _epochCancellation.Token, _lifetime.Token);
                    login.CancelAfter(TimeSpan.FromMinutes(3));
                    _login = login; _pendingSignIn = null; _cancelledByUser = false;
                    SetSnapshot(new(IdentitySessionState.SigningIn, "guest", epoch, null, null, rememberSignIn, false, null), revision, after);
                }
            }, cancellationToken).ConfigureAwait(false);
            if (login is null) return Snapshot;
            try
            {
                ProtectedIdentitySession session = await _flow.SignInAsync(rememberSignIn, reauthenticate, login.Token).ConfigureAwait(false);
                await CommitAuthorityAsync(after =>
                {
                    lock (_gate)
                    {
                        if (_disposed || _shutdownTask is not null || epoch != _snapshot.SessionEpoch || !ReferenceEquals(_login, login)) return;
                        login.Token.ThrowIfCancellationRequested();
                        if (_cancelledByUser) throw new OperationCanceledException(login.Token);
                        ValidateStored(session);
                        long revision = NextAuthorityRevision(after);
                        _session = session;
                        string? persistenceError = PersistCurrentSession(session, rememberSignIn);
                        _login = null; _cancelledByUser = false;
                        SetSnapshot(MakeSnapshot(session, IdentitySessionState.SignedIn, epoch, rememberSignIn, persistenceError), revision, after);
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception error) when (SafeFailure(error))
            {
                await CommitAuthorityAsync(after =>
                {
                    lock (_gate)
                    {
                        if (_disposed || _shutdownTask is not null || epoch != _snapshot.SessionEpoch || !ReferenceEquals(_login, login)) return;
                        long revision = NextAuthorityRevision(after);
                        string code = ErrorCode(error, cancellationToken.IsCancellationRequested || _cancelledByUser);
                        _login = null; _cancelledByUser = false;
                        SetSnapshot(new(IdentitySessionState.SignedOut, "guest", epoch, null, null, rememberSignIn, false, code), revision, after);
                    }
                }).ConfigureAwait(false);
            }
            return Snapshot;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_pendingSignIn, pending)) _pendingSignIn = null;
                if (login is not null && ReferenceEquals(_login, login)) { _login = null; _cancelledByUser = false; }
            }
            login?.Dispose();
        }
    }
    private sealed class PendingSignIn(long epoch)
    {
        internal readonly long Epoch = epoch;
        internal bool Cancelled;
    }
    public void CancelSignIn()
    {
        CancellationTokenSource? login;
        lock (_gate)
        {
            if (_pendingSignIn is { } pending) pending.Cancelled = true;
            login = _login;
            if (login is not null) _cancelledByUser = true;
        }
        // This is interruption intent, not a successful identity commit. A late
        // sign-in also checks _cancelledByUser under the final authority lock.
        try { login?.Cancel(); }
        catch (ObjectDisposedException) { /* Its owning operation completed. */ }
    }

    public async Task<IdentitySnapshot> SetRememberSignInAsync(bool rememberSignIn, CancellationToken cancellationToken = default)
    {
        using var active = BeginActiveOperation(cancellationToken);
        cancellationToken = active.Token;
        using IDisposable? operation = TryEnterCriticalOperation("保持登录偏好及凭据写入");
        if (operation is null) return await MaintenanceDeferredAsync(cancellationToken).ConfigureAwait(false);
        await CommitAuthorityAsync(after =>
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_shutdownTask is not null) return;
                cancellationToken.ThrowIfCancellationRequested();
                long revision = NextAuthorityRevision(after);
                IdentitySnapshot next = _snapshot;
                if (_login is not null) next = next with { ErrorCode = "REQUEST_BUSY" };
                else if (_session is null || next.State is not (IdentitySessionState.SignedIn or IdentitySessionState.OfflineCached))
                    next = next with { ErrorCode = next.State == IdentitySessionState.SessionExpired ? "SESSION_EXPIRED" : "AUTH_REQUIRED" };
                else
                {
                    try
                    {
                        ValidateStored(_session);
                        if (rememberSignIn) _vault.Write(_session);
                        else { next = next with { RememberSignIn = false }; _vault.Clear(); }
                        _persistenceFailure = false;
                        next = next with { RememberSignIn = rememberSignIn,
                            ErrorCode = next.State == IdentitySessionState.OfflineCached
                                ? (next.ErrorCode == "AUTH_CREDENTIALS_WRITE_FAILED" ? "OFFLINE" : next.ErrorCode) : null };
                    }
                    catch (Exception error) when (SafeFailure(error))
                    { _persistenceFailure = true; next = next with { ErrorCode = "AUTH_CREDENTIALS_WRITE_FAILED" }; }
                }
                SetSnapshot(next, revision, after);
            }
        }, cancellationToken).ConfigureAwait(false);
        return Snapshot;
    }

    public async Task<IdentitySnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        using var active = BeginActiveOperation(cancellationToken);
        cancellationToken = active.Token;
        await _refresh.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using IDisposable? operation = TryEnterCriticalOperation("刷新身份及凭据轮换");
            if (operation is null) return await MaintenanceDeferredAsync(cancellationToken).ConfigureAwait(false);
            ProtectedIdentitySession session;
            long epoch;
            CancellationToken epochToken;
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_shutdownTask is not null || _session is null || _login is not null) return _snapshot;
                session = _session; epoch = _snapshot.SessionEpoch; epochToken = _epochCancellation.Token;
            }
            using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, epochToken, _lifetime.Token);
            request.CancelAfter(TimeSpan.FromSeconds(45));
            try
            {
                ProtectedIdentitySession refreshed = await _flow.RefreshAsync(session, request.Token).ConfigureAwait(false);
                ValidateStored(refreshed);
                if (refreshed.Subject != session.Subject || refreshed.Issuer != session.Issuer) throw new IdentityFlowException("AUTH_INVALID_TOKEN");
                // The provider may already have invalidated the previous refresh token.
                // The HTTP deadline ends at a verified response, not at a later native
                // fence. Caller cancellation must not discard accepted rotation data;
                // RefreshCurrent still prevents writing into an ended or replaced scope.
                request.CancelAfter(Timeout.InfiniteTimeSpan);
                await CommitAuthorityAsync(after =>
                {
                    lock (_gate)
                    {
                        if (!RefreshCurrent(session, epoch)) return;
                        long revision = NextAuthorityRevision(after);
                        bool remember = _snapshot.RememberSignIn;
                        _session = refreshed;
                        string? persistenceError = PersistCurrentSession(refreshed, remember);
                        SetSnapshot(MakeSnapshot(refreshed, IdentitySessionState.SignedIn, epoch, remember, persistenceError), revision, after);
                    }
                }).ConfigureAwait(false);
            }
            catch (Exception error) when (SafeFailure(error))
            {
                string code = ErrorCode(error, cancellationToken.IsCancellationRequested);
                ProtectedIdentitySession? partialSession = null;
                if (error is IdentityRefreshException partial)
                {
                    try
                    {
                        ValidateStored(partial.RefreshedSession);
                        if (partial.RefreshedSession.Subject != session.Subject || partial.RefreshedSession.Issuer != session.Issuer)
                            throw new IdentityFlowException("AUTH_INVALID_TOKEN");
                        partialSession = partial.RefreshedSession;
                    }
                    catch (Exception invalid) when (SafeFailure(invalid)) { code = "AUTH_INVALID_TOKEN"; }
                }
                if (code == "USER_CANCELLED" && partialSession is null) return Snapshot;
                await CommitAuthorityAsync(after =>
                {
                    lock (_gate)
                    {
                        if (!RefreshCurrent(session, epoch)) return;
                        long revision = NextAuthorityRevision(after);
                        bool remember = _snapshot.RememberSignIn;
                        if (partialSession is not null)
                        {
                            _session = partialSession;
                            string? persistenceError = PersistCurrentSession(partialSession, remember);
                            SetSnapshot(MakeSnapshot(partialSession, IdentitySessionState.OfflineCached, epoch, remember, persistenceError ?? code), revision, after);
                            return;
                        }
                        bool offline = code is "OFFLINE" or "AUTH_TIMEOUT" or "AUTH_METADATA_UNAVAILABLE" or "AUTH_PROVIDER_UNAVAILABLE";
                        if (!offline)
                        {
                            _session = session with { AccessToken = "", RefreshToken = null, IdentityToken = "" };
                            try { _vault.Clear(); _persistenceFailure = false; }
                            catch (Exception clearError) when (SafeFailure(clearError)) { _persistenceFailure = true; }
                        }
                        SetSnapshot(MakeSnapshot(_session!, offline ? IdentitySessionState.OfflineCached : IdentitySessionState.SessionExpired,
                            epoch, remember, _persistenceFailure ? "AUTH_CREDENTIALS_WRITE_FAILED" : code), revision, after);
                    }
                }).ConfigureAwait(false);
            }
        }
        finally { _refresh.Release(); }
        return Snapshot;
    }
    private bool RefreshCurrent(ProtectedIdentitySession original, long epoch) => !_disposed && !_revisionExhausted && _shutdownTask is null &&
        _snapshot.SessionEpoch == epoch && ReferenceEquals(_session, original) && _login is null;

    public async Task<IdentitySnapshot> SignOutAsync(bool browserSession = false, CancellationToken cancellationToken = default)
    {
        using var active = BeginActiveOperation(cancellationToken);
        cancellationToken = active.Token;
        using IDisposable? operation = TryEnterCriticalOperation("账号退出与凭据清理");
        if (operation is null) return await MaintenanceDeferredAsync(cancellationToken).ConfigureAwait(false);
        ProtectedIdentitySession? previous = null;
        long epoch = -1;
        await CommitAuthorityAsync(after =>
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_shutdownTask is not null) return;
                cancellationToken.ThrowIfCancellationRequested();
                long revision = NextAuthorityRevision(after);
                previous = _session; epoch = AdvanceEpoch(after); _session = null;
                string? failure = null;
                try { _vault.Clear(); _persistenceFailure = false; }
                catch (Exception error) when (SafeFailure(error)) { _persistenceFailure = true; failure = "AUTH_CREDENTIALS_WRITE_FAILED"; }
                SetSnapshot(new(failure is null ? IdentitySessionState.SignedOut : IdentitySessionState.SessionExpired,
                    "guest", epoch, null, null, false, false, failure), revision, after);
            }
        }, cancellationToken).ConfigureAwait(false);
        if (browserSession && epoch >= 0)
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
                await CommitAuthorityAsync(after =>
                {
                    lock (_gate)
                    {
                        if (_disposed || _shutdownTask is not null || _snapshot.SessionEpoch != epoch) return;
                        long revision = NextAuthorityRevision(after);
                        SetSnapshot(_snapshot with { ErrorCode = _persistenceFailure ? "AUTH_CREDENTIALS_WRITE_FAILED" : "AUTH_BROWSER_LOGOUT_INCOMPLETE" }, revision, after);
                    }
                }).ConfigureAwait(false);
            }
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
                lock (_gate) needed = !_disposed && !_revisionExhausted && _shutdownTask is null && _login is null && _session is not null &&
                    _snapshot.State is IdentitySessionState.SignedIn or IdentitySessionState.OfflineCached &&
                    _session.ExpiresAt <= _clock.GetUtcNow().AddMinutes(1);
                if (needed) await RefreshAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
    }

    // Called under _gate. The returned notifications are run only after the
    // mutation permit is released. Overflow is checked before exchanging CTSs.
    private long AdvanceEpoch(DeferredActions after)
    {
        long epoch = checked(_snapshot.SessionEpoch + 1);
        CancellationTokenSource previous = _epochCancellation;
        CancellationTokenSource? login = _login;
        _epochCancellation = new();
        after.Actions.Add(() => previous.Cancel());
        after.Actions.Add(previous.Dispose);
        if (login is not null) after.Actions.Add(() => { try { login.Cancel(); } catch (ObjectDisposedException) { } });
        return epoch;
    }
    private long NextAuthorityRevision(DeferredActions after)
    {
        if (_revisionExhausted || _snapshot.AuthorityRevision == long.MaxValue)
        {
            _revisionExhausted = true; _session = null;
            _snapshot = _snapshot with { State = IdentitySessionState.SessionExpired, DisplayName = null, AvatarUrl = null,
                CanRefresh = false, ErrorCode = "AUTHORITY_REVISION_EXHAUSTED" };
            after.Publish = true;
            throw new RuntimeCapabilityException("SESSION_EXPIRED");
        }
        return checked(_snapshot.AuthorityRevision + 1);
    }
    private void SetSnapshot(IdentitySnapshot value, long revision, DeferredActions after)
    { _snapshot = value with { AuthorityRevision = revision }; after.Publish = true; }
    private async Task CommitAuthorityAsync(Action<DeferredActions> shortCommit, CancellationToken beforeIntent = default)
    {
        var after = new DeferredActions();
        Exception? failure = null;
        try
        {
            if (_authorityMutations is null) { beforeIntent.ThrowIfCancellationRequested(); shortCommit(after); }
            else
            {
                IRuntimeAuthorityMutation mutation = await _authorityMutations.BeginAsync(RuntimeMutationScope.ForIdentity(), beforeIntent).ConfigureAwait(false);
                // No cancellation or external callbacks between ownership and settlement.
                await mutation.CommitAsync(() => shortCommit(after)).ConfigureAwait(false);
            }
        }
        catch (Exception error) { failure = error; }
        List<Exception> deferredFailures = [];
        foreach (Action action in after.Actions) { try { action(); } catch (Exception error) { deferredFailures.Add(error); } }
        if (after.Publish) Publish();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        if (deferredFailures.Count != 0) throw new AggregateException("Identity deferred cancellation did not finish cleanly.", deferredFailures);
    }
    private sealed class DeferredActions { internal readonly List<Action> Actions = []; internal bool Publish; }

    private IdentitySnapshot MakeSnapshot(ProtectedIdentitySession session, IdentitySessionState state, long epoch, bool remember, string? code) =>
        new(state, AccountNamespaceFor(session.Issuer, session.Subject), epoch, session.DisplayName, session.AvatarUrl, remember,
            !string.IsNullOrEmpty(session.RefreshToken), code);
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
        catch (IOException error) when (error.Message == "CLIENT_SHUTTING_DOWN")
        { throw new OperationCanceledException("The client has stopped accepting identity operations.", error, _lifetime.Token); }
    }
    private async Task<IdentitySnapshot> MaintenanceDeferredAsync(CancellationToken beforeIntent)
    {
        await CommitAuthorityAsync(after =>
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_shutdownTask is not null) return;
                beforeIntent.ThrowIfCancellationRequested();
                long revision = NextAuthorityRevision(after);
                SetSnapshot(_snapshot with { ErrorCode = "MAINTENANCE_IN_PROGRESS" }, revision, after);
            }
        }, beforeIntent).ConfigureAwait(false);
        return Snapshot;
    }
    private sealed class NoopLease : IDisposable { internal static readonly NoopLease Instance = new(); public void Dispose() { } }
    private void Publish()
    {
        IdentitySnapshot state = Snapshot;
        foreach (EventHandler<IdentitySnapshot> observer in Changed?.GetInvocationList().Cast<EventHandler<IdentitySnapshot>>() ?? [])
        { try { observer(this, state); } catch (Exception) { /* UI observers cannot turn verified results into authentication faults. */ } }
    }
    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed || _revisionExhausted, this);
    private ActiveOperation BeginActiveOperation(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ObjectDisposedException.ThrowIf(_shutdownTask is not null, this);
            var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            if (_activeOperations++ == 0) _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            return new(linked, () => { lock (_gate) if (--_activeOperations == 0) _operationsDrained.TrySetResult(); });
        }
    }
    private sealed class ActiveOperation(CancellationTokenSource cancellation, Action completed) : IDisposable
    {
        private Action? finish = completed;
        internal CancellationToken Token => cancellation.Token;
        public void Dispose()
        {
            Action? current = Interlocked.Exchange(ref finish, null);
            if (current is null) return;
            try { cancellation.Dispose(); } finally { current(); }
        }
    }
    private Task BeginShutdown()
    {
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_shutdownTask is not null) return _shutdownTask;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _shutdownTask = completion.Task; // Admission stops before the asynchronous fence.
        }
        _ = CompleteShutdownAsync(completion);
        return completion.Task;
    }
    private async Task CompleteShutdownAsync(TaskCompletionSource completion)
    {
        List<Exception> failures = [];
        try
        {
            await CommitAuthorityAsync(after =>
            {
                lock (_gate)
                {
                    if (_disposed) return;
                    long revision = NextAuthorityRevision(after);
                    long epoch = AdvanceEpoch(after);
                    _disposed = true; _session = null; Changed = null;
                    SetSnapshot(new(IdentitySessionState.SignedOut, "guest", epoch, null, null, false, false, null), revision, after);
                }
            }).ConfigureAwait(false);
        }
        catch (Exception error) { failures.Add(error); }
        Task initialization, operations;
        CancellationTokenSource? login;
        lock (_gate)
        {
            initialization = _initialization ?? Task.CompletedTask;
            operations = _activeOperations == 0 ? Task.CompletedTask : _operationsDrained.Task;
            login = _login;
        }
        try { await DrainAsync(initialization, operations, login).ConfigureAwait(false); }
        catch (Exception error) { failures.Add(error); }
        if (failures.Count == 0) completion.TrySetResult();
        else completion.TrySetException(new AggregateException("Identity shutdown did not finish cleanly.", failures));
    }
    private async Task DrainAsync(Task initialization, Task operations, CancellationTokenSource? login)
    {
        List<Exception> failures = [];
        foreach (var source in new[] { _lifetime, _epochCancellation, login })
        {
            try { source?.Cancel(); }
            catch (ObjectDisposedException) when (ReferenceEquals(source, login)) { }
            catch (Exception error) { failures.Add(error); }
        }
        async Task Observe(Task task)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_disposed) { }
            catch (Exception error) { lock (failures) failures.Add(error); }
        }
        await Task.WhenAll(Observe(initialization), Observe(_refreshLoop), Observe(operations)).ConfigureAwait(false);
        _refresh.Dispose(); _epochCancellation.Dispose(); _lifetime.Dispose();
        if (failures.Count != 0) throw new AggregateException("Identity drain did not finish cleanly.", failures);
    }
    public void Dispose() { _ = BeginShutdown(); }
    public ValueTask DisposeAsync() => new(BeginShutdown());
}
