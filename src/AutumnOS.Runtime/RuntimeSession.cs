using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutumnOS.Contracts;

namespace AutumnOS.Runtime;

/// <summary>T01 host-bound guest game session. Never accept an application identity from an SDK message.</summary>
public sealed partial class RuntimeSession
{
    private static readonly JsonSerializerOptions ResponseJsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public const int MaximumMessageBytes = 32 * 1024;
    public const int MaximumRememberedRequestIds = 2048;
    public const int MaximumPendingRequests = 16;
    public const int MaximumRequestsPerMinute = 120;
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan DeduplicationWindow = TimeSpan.FromMinutes(10);
    private readonly object stateGate = new();
    private readonly SemaphoreSlim messages = new(1, 1);
    private readonly CancellationTokenSource ended = new();
    private readonly HashSet<string> declared;
    private readonly HashSet<string> seenIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> pendingIds = new(StringComparer.Ordinal);
    private readonly Queue<(string Id, long Timestamp)> completedRequests = new();
    private readonly Queue<long> recentRequests = new();
    private readonly TimeProvider timeProvider;
    private readonly GuestSaveStore saves;
    private readonly TimeSpan requestTimeout;
    private int pendingRequests;
    private bool savesGranted;
    private bool savesDenied;
    private bool savesRevoked;
    private long permissionRevision;
    private long suspensionRevision;
    private AppInstance instance;

    public RuntimeSession(string appId, IEnumerable<string> declaredPermissions, string savesRoot,
        string entry = "index.html", TimeSpan? requestTimeout = null, TimeProvider? timeProvider = null,
        RuntimeSessionServices? services = null)
    {
        if (!IsValidAppId(appId)) throw new ArgumentException("Invalid application ID.", nameof(appId));
        if (!IsValidEntry(entry)) throw new ArgumentException("Invalid application entry.", nameof(entry));
        this.requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        if (this.requestTimeout <= TimeSpan.Zero || this.requestTimeout > TimeSpan.FromMinutes(2))
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        ArgumentNullException.ThrowIfNull(declaredPermissions);
        declared = new HashSet<string>(declaredPermissions, StringComparer.Ordinal);
        saves = new GuestSaveStore(savesRoot, appId);
        Origin = $"https://{Guid.NewGuid():N}.autumn.invalid/";
        PageUri = Origin + entry;
        instance = new(new(Guid.NewGuid()), new(appId, null, null), true, AppLifecycleState.Starting, new(1));
        InitializeServices(services);
    }

    public string Origin { get; }
    public string PageUri { get; }
    public AppInstance Instance { get { lock (stateGate) return instance; } }

    public static bool IsValidAppId(string? appId)
    {
        if (appId is null || appId.Length is < 3 or > 80
            || !Regex.IsMatch(appId, "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*(?:\\.[a-z][a-z0-9]*(?:-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant)
            || appId.Split('.').Any(part => part.Length > 63)) return false;
        string first = appId.Split('.')[0];
        return !Regex.IsMatch(first, "^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public void Foreground() => SetActiveState(AppLifecycleState.Foreground);
    public void Background() => SetActiveState(AppLifecycleState.Background);
    public void Suspend() => SetActiveState(AppLifecycleState.Suspended);
    public void Resume() => SetActiveState(AppLifecycleState.Foreground);
    public void Close() => End(AppLifecycleState.Closed);
    public void MarkCrashed() => End(AppLifecycleState.Crashed);

    /// <summary>Host-only revocation. Pending permission requests cannot restore the revoked grant.</summary>
    public void RevokeSavePermission()
    {
        if (services is not null)
        {
            services.Permissions.SetDecision(services.Account, services.Application, "saves", PermissionDecision.Revoked);
            return;
        }
        lock (stateGate)
        {
            savesGranted = false;
            savesRevoked = true;
            permissionRevision++;
        }
    }

    private static bool IsValidEntry(string? entry)
    {
        if (entry is null || entry.Length is < 1 or > 200 || !entry.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || entry.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '/' and not '.' and not '_' and not '-')) return false;
        return entry.Split('/').All(segment => segment.Length is > 0 and <= 80 && !segment.StartsWith('.')
            && !segment.EndsWith('.') && !Regex.IsMatch(segment.Split('.')[0],
                "^(?:con|prn|aux|nul|com[1-9]|lpt[1-9])$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
    }

    private void SetActiveState(AppLifecycleState state)
    {
        lock (stateGate)
        {
            if (!IsActive) throw new InvalidOperationException("An ended session cannot be resumed.");
            if (state == AppLifecycleState.Suspended && instance.State != AppLifecycleState.Suspended) suspensionRevision++;
            instance = instance with { State = state };
        }
        EmitSdkEvent("lifecycle.changed", new { state = state.ToString().ToLowerInvariant(), blocksMaintenance = Instance.BlocksMaintenance });
    }

    private void End(AppLifecycleState state)
    {
        lock (stateGate)
        {
            if (!IsActive) return;
            instance = instance with { State = state, Epoch = new(instance.Epoch.Value + 1) };
            savesGranted = false;
        }
        // Cancel outside stateGate: cancellation callbacks must not run under the lifecycle lock.
        ended.Cancel();
        DetachServices();
    }

    private bool IsActive => instance.State is not (AppLifecycleState.Closed or AppLifecycleState.Crashed or AppLifecycleState.Closing);

    public async Task<string> HandleMessageAsync(string source, string json,
        Func<string, CancellationToken, Task<bool>> requestPermission, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requestPermission);
        if (!string.Equals(source, PageUri, StringComparison.Ordinal)) return Error(null, "SOURCE_REJECTED");
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaximumMessageBytes) return Error(null, "MESSAGE_TOO_LARGE");
        string? requestId = null;
        bool acquired = false;
        bool admitted = false;
        using var timeout = new CancellationTokenSource(requestTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, ended.Token, timeout.Token,
            services?.Account.Invalidated ?? CancellationToken.None);
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement request = document.RootElement;
            if (!ExactProperties(request, "protocolVersion", "requestId", "method", "params") || HasDuplicateProperties(request))
                return Error(null, "INVALID_REQUEST");
            JsonElement id = request.GetProperty("requestId");
            if (id.ValueKind != JsonValueKind.String || id.GetString() is not { } idText
                || !Regex.IsMatch(idText, "^[A-Za-z0-9][A-Za-z0-9_-]{0,63}$", RegexOptions.CultureInvariant))
                return Error(null, "INVALID_REQUEST");
            requestId = idText;
            if (!request.GetProperty("protocolVersion").TryGetInt32(out int version) || version != 1)
                return Error(requestId, "PROTOCOL_UNSUPPORTED");
            if (request.GetProperty("method").ValueKind != JsonValueKind.String
                || request.GetProperty("params").ValueKind != JsonValueKind.Object) return Error(requestId, "INVALID_REQUEST");
            string method = request.GetProperty("method").GetString()!;
            JsonElement parameters = request.GetProperty("params");

            lock (stateGate)
            {
                if (!IsActive) return Error(requestId, "SESSION_EXPIRED");
                linked.Token.ThrowIfCancellationRequested();
                if (pendingRequests >= MaximumPendingRequests) return Error(requestId, "REQUEST_BUSY");
                long now = timeProvider.GetTimestamp();
                while (recentRequests.TryPeek(out long oldest) && timeProvider.GetElapsedTime(oldest, now) >= TimeSpan.FromMinutes(1))
                    recentRequests.Dequeue();
                if (recentRequests.Count >= MaximumRequestsPerMinute) return Error(requestId, "RATE_LIMITED");
                recentRequests.Enqueue(now);
                PruneCompletedRequests(now);
                // Reserve before queueing: concurrent duplicates cannot wait and execute again later.
                // Pending IDs never expire; completed IDs retain a bounded replay window.
                if (pendingIds.Contains(requestId) || seenIds.Contains(requestId)) return Error(requestId, "DUPLICATE_REQUEST");
                if (pendingIds.Count + seenIds.Count >= MaximumRememberedRequestIds) return Error(requestId, "REQUEST_BUSY");
                pendingIds.Add(requestId);
                pendingRequests++;
                admitted = true;
            }
            await messages.WaitAsync(linked.Token).ConfigureAwait(false);
            acquired = true;
            lock (stateGate)
            {
                if (!IsActive) return Error(requestId, "SESSION_EXPIRED");
                if (instance.State == AppLifecycleState.Suspended && method != "lifecycle.getState")
                    return Error(requestId, "SESSION_SUSPENDED");
            }

            if (services is not null)
            {
                object managedResult = await HandleManagedAsync(method, parameters, linked.Token).ConfigureAwait(false);
                lock (stateGate)
                {
                    services.Account.EnsureCurrent();
                    return SuccessIfActive(requestId, managedResult, linked.Token);
                }
            }

            if (method == "permissions.request")
            {
                if (!PermissionName(parameters)) return Error(requestId, "CAPABILITY_UNAVAILABLE");
                long revision;
                long suspendedAtRequest;
                lock (stateGate)
                {
                    if (!IsActive) return Error(requestId, "SESSION_EXPIRED");
                    linked.Token.ThrowIfCancellationRequested();
                    if (instance.State == AppLifecycleState.Suspended) return Error(requestId, "SESSION_SUSPENDED");
                    if (!declared.Contains("saves")) return Error(requestId, "PERMISSION_NOT_DECLARED");
                    if (savesRevoked) return Error(requestId, "PERMISSION_REVOKED");
                    if (savesGranted) return SuccessIfActive(requestId, new { name = "saves", state = "granted" }, linked.Token);
                    // A denied app cannot repeatedly reopen the host permission dialog in this session.
                    if (savesDenied) return SuccessIfActive(requestId, new { name = "saves", state = "denied" }, linked.Token);
                    revision = permissionRevision;
                    suspendedAtRequest = suspensionRevision;
                }
                bool accepted = await requestPermission("saves", linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
                lock (stateGate)
                {
                    if (!IsActive) return Error(requestId, "SESSION_EXPIRED");
                    linked.Token.ThrowIfCancellationRequested();
                    if (instance.State == AppLifecycleState.Suspended || suspensionRevision != suspendedAtRequest)
                        return Error(requestId, "SESSION_SUSPENDED");
                    if (revision != permissionRevision || savesRevoked) return Error(requestId, "PERMISSION_REVOKED");
                    savesGranted = accepted;
                    savesDenied = !accepted;
                    return SuccessIfActive(requestId, new { name = "saves", state = accepted ? "granted" : "denied" }, linked.Token);
                }
            }

            lock (stateGate)
            {
                if (!IsActive) return Error(requestId, "SESSION_EXPIRED");
                if (instance.State == AppLifecycleState.Suspended && method != "lifecycle.getState")
                    return Error(requestId, "SESSION_SUSPENDED");
                linked.Token.ThrowIfCancellationRequested();
                object result;
                switch (method)
                {
                    case "platform.getCapabilities":
                        if (!ExactProperties(parameters)) return Error(requestId, "INVALID_REQUEST");
                        result = new { protocolVersion = 1, capabilities = new[] { "lifecycle", "permissions", "saves" }, accountMode = "guest", networkSandboxVerified = false,
                            limits = new { maximumMessageBytes = MaximumMessageBytes, maximumPendingRequests = MaximumPendingRequests,
                                maximumRequestsPerMinute = MaximumRequestsPerMinute, requestTimeoutMs = (int)requestTimeout.TotalMilliseconds,
                                maximumRememberedRequestIds = MaximumRememberedRequestIds, deduplicationWindowMs = (int)DeduplicationWindow.TotalMilliseconds } };
                        break;
                    case "lifecycle.getState":
                        if (!ExactProperties(parameters)) return Error(requestId, "INVALID_REQUEST");
                        result = new { state = instance.State.ToString().ToLowerInvariant(), blocksMaintenance = instance.BlocksMaintenance };
                        break;
                    case "permissions.query":
                        if (!PermissionName(parameters)) return Error(requestId, "CAPABILITY_UNAVAILABLE");
                        if (!declared.Contains("saves")) return Error(requestId, "PERMISSION_NOT_DECLARED");
                        result = new { name = "saves", state = savesRevoked ? "revoked" : savesGranted ? "granted" : savesDenied ? "denied" : "prompt" };
                        break;
                    case "saves.read":
                    case "saves.write":
                        if (!declared.Contains("saves")) return Error(requestId, "PERMISSION_NOT_DECLARED");
                        if (savesRevoked) return Error(requestId, "PERMISSION_REVOKED");
                        if (!savesGranted) return Error(requestId, "PERMISSION_DENIED");
                        if (!ExactProperties(parameters, method == "saves.read" ? ["slot"] : ["slot", "value"])
                            || parameters.GetProperty("slot").ValueKind != JsonValueKind.String
                            || parameters.GetProperty("slot").GetString() != "game") return Error(requestId, "INVALID_REQUEST");
                        // The lifecycle lock makes a committed save linearize before Close; waiting/late work cannot write after Close.
                        if (method == "saves.read")
                        {
                            JsonElement? value = saves.Read();
                            result = new { exists = value.HasValue, value };
                        }
                        else
                        {
                            saves.Write(parameters.GetProperty("value"), linked.Token);
                            result = new { saved = true };
                        }
                        break;
                    default: return Error(requestId, "CAPABILITY_UNAVAILABLE");
                }
                return SuccessIfActive(requestId, result, linked.Token);
            }
        }
        catch (OperationCanceledException)
        { return Error(requestId, ended.IsCancellationRequested ? "SESSION_EXPIRED" : cancellationToken.IsCancellationRequested ? "USER_CANCELLED" : timeout.IsCancellationRequested ? "REQUEST_TIMEOUT" : "USER_CANCELLED"); }
        catch (RuntimeStorageException exception) { return Error(requestId, exception.Code); }
        catch (RuntimeCapabilityException exception) { return Error(requestId, exception.Code); }
        catch (JsonException) { return Error(requestId, "INVALID_REQUEST"); }
        catch (InvalidOperationException) { return Error(requestId, "INVALID_REQUEST"); }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        { return Error(requestId, "INVALID_REQUEST"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { return Error(requestId, "STORAGE_IO_ERROR"); }
        finally
        {
            if (acquired) messages.Release();
            if (admitted) lock (stateGate)
            {
                pendingRequests--;
                pendingIds.Remove(requestId!);
                long now = timeProvider.GetTimestamp();
                PruneCompletedRequests(now);
                seenIds.Add(requestId!);
                completedRequests.Enqueue((requestId!, now));
            }
        }
    }

    // Caller holds stateGate. Never evict an unexpired record just to admit a new request.
    private void PruneCompletedRequests(long now)
    {
        while (completedRequests.TryPeek(out var oldest) && timeProvider.GetElapsedTime(oldest.Timestamp, now) >= DeduplicationWindow)
        {
            completedRequests.Dequeue();
            seenIds.Remove(oldest.Id);
        }
    }

    private string SuccessIfActive(string requestId, object value, CancellationToken cancellationToken)
    {
        if (!IsActive) return Error(requestId, "SESSION_EXPIRED");
        services?.Account.EnsureCurrent();
        cancellationToken.ThrowIfCancellationRequested();
        string response = JsonSerializer.Serialize(new { requestId, ok = true, result = value }, ResponseJsonOptions);
        return services is not null && Encoding.UTF8.GetByteCount(response) > MaximumMessageBytes
            ? Error(requestId, "RESPONSE_TOO_LARGE") : response;
    }

    private static bool PermissionName(JsonElement parameters) => ExactProperties(parameters, "name")
        && parameters.GetProperty("name").ValueKind == JsonValueKind.String && parameters.GetProperty("name").GetString() == "saves";

    internal static bool ExactProperties(JsonElement element, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object) return false;
        string[] names = element.EnumerateObject().Select(property => property.Name).ToArray();
        return names.Length == expected.Length && names.Distinct(StringComparer.Ordinal).Count() == names.Length
            && names.All(name => expected.Contains(name, StringComparer.Ordinal));
    }

    internal static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray()) if (HasDuplicateProperties(child)) return true;
        return false;
    }

    private static string Error(string? requestId, string code) => JsonSerializer.Serialize(new
    {
        requestId, ok = false,
        error = new
        {
            code,
            message = code switch
            {
                "SOURCE_REJECTED" => "Message source is not the bound application page.",
                "SESSION_EXPIRED" => "The application session has ended.",
                "SESSION_SUSPENDED" => "The application session is suspended.",
                "PERMISSION_NOT_DECLARED" => "The application did not declare this permission.",
                "PERMISSION_DENIED" => "This capability requires the user's approval.",
                "PERMISSION_REVOKED" => "The host revoked this permission for the current session.",
                "SAVE_CORRUPT" => "The existing save is invalid and has been preserved.",
                "STORAGE_UNSAFE_PATH" => "The save path is redirected and cannot be used.",
                "CAPABILITY_UNAVAILABLE" => "This capability is unavailable in the current runtime.",
                "DUPLICATE_REQUEST" => "A request ID is pending or was completed within the replay protection window.",
                "REQUEST_BUSY" => "The application has too many pending requests.",
                "RATE_LIMITED" => "The application request rate limit was reached.",
                "REQUEST_TIMEOUT" => "The request timed out.",
                _ => "The request could not be completed."
            },
            retryable = code is "SAVE_BUSY" or "STORAGE_IO_ERROR" or "REQUEST_BUSY" or "RATE_LIMITED",
            correlationId = Guid.NewGuid()
        }
    });
}
