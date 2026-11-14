using System.Text.Json;
using System.Text.Json.Serialization;
using AutumnOS.Contracts;
using System.Collections.Concurrent;

namespace AutumnOS.Runtime;

/// <summary>Host-managed, persistent grants. Source identity and each permission are independently bound.</summary>
public sealed class PermissionService : IPermissionService
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    { "saves", "identity.profile", "storage", "files.open", "files.save", "notifications", "shortcuts", "widgets", "links", "network" };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<PermissionDecision>() }
    };
    private readonly object gate = new();
    private readonly string path;
    private readonly string networkPath;
    private readonly Func<IDisposable>? enterCritical;
    private readonly IRuntimeAuthorityMutations? authorityMutations;
    private readonly Dictionary<string, PermissionRecord> records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> revisions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> bindingRevisions = new(StringComparer.Ordinal);
    private readonly HashSet<string> pending = new(StringComparer.Ordinal);
    private const int MaximumRecords = 4096;
    private const int MaximumBytes = 2 * 1024 * 1024;
    public event Action<PermissionChange>? Changed;

    public PermissionService(string configurationDirectory, Func<IDisposable>? enterCritical = null,
        IRuntimeAuthorityMutations? authorityMutations = null)
    {
        this.enterCritical = enterCritical;
        this.authorityMutations = authorityMutations;
        if (!Path.IsPathFullyQualified(configurationDirectory)) throw new ArgumentException("An absolute configuration directory is required.", nameof(configurationDirectory));
        path = Path.Combine(Path.GetFullPath(configurationDirectory), "application-permissions.v1.json");
        networkPath = Path.Combine(Path.GetFullPath(configurationDirectory), "application-network-permissions.v1.json");
        Load(path, network: false);
        Load(networkPath, network: true);
    }

    private void Load(string sourcePath, bool network)
    {
        SafePath(sourcePath, false);
        if (!File.Exists(sourcePath)) return;
        try
        {
            var info = new FileInfo(sourcePath);
            if (info.Length is <= 0 or > MaximumBytes) throw new RuntimeCapabilityException("PERMISSION_STORE_CORRUPT");
            using var document = JsonDocument.Parse(File.ReadAllBytes(sourcePath), new JsonDocumentOptions { MaxDepth = 12 });
            if (RuntimeSession.HasDuplicateProperties(document.RootElement)) throw new RuntimeCapabilityException("PERMISSION_STORE_CORRUPT");
            var stored = document.RootElement.Deserialize<PermissionDocument>(JsonOptions);
            if (stored is null || stored.SchemaVersion != 1 || stored.Records is null || stored.Records.Length > MaximumRecords)
                throw new RuntimeCapabilityException("PERMISSION_STORE_CORRUPT");
            // Preserve the exact old seven-field record family, including strict
            // old-client readers that reject even a new field whose value is null.
            if (!network && document.RootElement.GetProperty("records").EnumerateArray().Any(
                row => row.ValueKind == JsonValueKind.Object && row.TryGetProperty("networkScopeHash", out _)))
                throw new RuntimeCapabilityException("PERMISSION_STORE_CORRUPT");
            foreach (var record in stored.Records)
            {
                Validate(record);
                if ((record.Permission == "network") != network || records.Count >= MaximumRecords)
                    throw new RuntimeCapabilityException("PERMISSION_STORE_CORRUPT");
                if (!records.TryAdd(Key(record.AccountKey, record.BindingKey, record.Permission), record))
                    throw new RuntimeCapabilityException("PERMISSION_STORE_CORRUPT");
            }
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        { throw new RuntimeCapabilityException("PERMISSION_STORE_CORRUPT"); }
    }

    public PermissionDecision Query(RuntimeAccountContext account, RuntimeApplication app, string permission)
    {
        ValidateRequest(account, app, permission);
        lock (gate)
        {
            if (!records.TryGetValue(Key(account.AccountKey, app.BindingKey, permission), out var item)) return PermissionDecision.Prompt;
            // A grant is for the complete declared DNS set, not just the unchanged
            // application/source binding. Old records without a scope never grant network.
            if (permission == "network" && item.NetworkScopeHash != NetworkDomainPolicy.ScopeHash(app.NetworkDomains)) return PermissionDecision.Prompt;
            return item.Decision;
        }
    }

    public IReadOnlyList<PermissionRecord> List(RuntimeAccountContext account)
    {
        account.EnsureCurrent();
        lock (gate) return records.Values.Where(item => item.AccountKey == account.AccountKey).OrderBy(item => item.AppId, StringComparer.Ordinal).ThenBy(item => item.Permission, StringComparer.Ordinal).ToArray();
    }

    public void Demand(RuntimeAccountContext account, RuntimeApplication app, string permission)
    {
        var state = Query(account, app, permission);
        if (state != PermissionDecision.Granted)
            throw new RuntimeCapabilityException(state == PermissionDecision.Revoked ? "PERMISSION_REVOKED" : "PERMISSION_DENIED");
    }

    public long GetRevision(RuntimeAccountContext account, RuntimeApplication app) =>
        bindingRevisions.GetValueOrDefault(Key(account.AccountKey, app.BindingKey, ""));

    public long GetRevision(RuntimeAccountContext account, RuntimeApplication app, string permission)
    {
        lock (gate)
        {
            ValidateRequest(account, app, permission);
            return revisions.GetValueOrDefault(Key(account.AccountKey, app.BindingKey, permission));
        }
    }

    /// <summary>Serializes the final write commit with host revocation; never hold this across an await.</summary>
    public IDisposable EnterUsageLease(RuntimeAccountContext account, RuntimeApplication app, string permission)
    {
        Monitor.Enter(gate);
        try { Demand(account, app, permission); return new UsageLease(gate); }
        catch { Monitor.Exit(gate); throw; }
    }

    /// <summary>Read-side decision lease for the final native response delivery, including denied states.</summary>
    public IDisposable EnterDecisionLease(RuntimeAccountContext account, RuntimeApplication app, string permission)
    {
        Monitor.Enter(gate);
        try { ValidateRequest(account, app, permission); return new UsageLease(gate); }
        catch { Monitor.Exit(gate); throw; }
    }

    public async Task<PermissionDecision> RequestAsync(RuntimeAccountContext account, RuntimeApplication app,
        string permission, bool userGesture, Func<RuntimePermissionPrompt, CancellationToken, Task<bool>> prompt,
        CancellationToken cancellationToken)
    {
        ValidateRequest(account, app, permission);
        string key = Key(account.AccountKey, app.BindingKey, permission);
        string[]? networkDomains = permission == "network" ? NetworkDomainPolicy.CopyValidated(app.NetworkDomains) : null;
        string? networkScope = networkDomains is null ? null : NetworkDomainPolicy.ScopeHash(networkDomains);
        long revision;
        lock (gate)
        {
            var state = Query(account, app, permission);
            // Denial/revocation persists. Only the settings application can reset or grant it.
            if (state != PermissionDecision.Prompt) return state;
            if (!userGesture) throw new RuntimeCapabilityException("USER_GESTURE_REQUIRED");
            if (!pending.Add(key)) throw new RuntimeCapabilityException("REQUEST_BUSY");
            revision = revisions.GetValueOrDefault(key);
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, account.Invalidated);
        try
        {
            var fields = permission == "identity.profile" ? new[] { "appScopedUserId", "displayName", "avatarUrl", "isCached" } : networkDomains ?? Array.Empty<string>();
            string purpose = app.PermissionPurposes is not null && app.PermissionPurposes.TryGetValue(permission, out var declaredPurpose)
                ? $"应用声明用途：{declaredPurpose}\n平台边界：{Purpose(permission)}" : Purpose(permission);
            var request = new RuntimePermissionPrompt(app.Identity.AppId, app.DisplayName, app.Source, permission, purpose, fields);
            bool accepted = await prompt(request, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            PermissionChange? change = null;
            await CommitMutationAsync(account, app, () =>
            {
                lock (gate)
                {
                    linked.Token.ThrowIfCancellationRequested();
                    account.EnsureCurrent();
                    if (revisions.GetValueOrDefault(key) != revision) throw new RuntimeCapabilityException("PERMISSION_REVOKED");
                    if (networkScope is not null && networkScope != NetworkDomainPolicy.ScopeHash(app.NetworkDomains))
                        throw new RuntimeCapabilityException("NETWORK_POLICY_CHANGED");
                    change = SaveDecision(account, app, permission, accepted ? PermissionDecision.Granted : PermissionDecision.Denied);
                }
            }, linked.Token).ConfigureAwait(false);
            Changed?.Invoke(change!);
            return change!.Decision;
        }
        finally { lock (gate) pending.Remove(key); }
    }

    /// <summary>Settings-only mutation; the application bridge never exposes this method.</summary>
    public void SetDecision(RuntimeAccountContext account, RuntimeApplication app, string permission, PermissionDecision decision)
    {
        ValidateRequest(account, app, permission);
        if (!Enum.IsDefined(decision)) throw new ArgumentOutOfRangeException(nameof(decision));
        PermissionChange? change = null;
        CommitMutation(account, app, () =>
        {
            lock (gate)
            {
                ValidateRequest(account, app, permission);
                change = SaveDecision(account, app, permission, decision);
            }
        });
        Changed?.Invoke(change!);
    }

    public async Task SetDecisionAsync(RuntimeAccountContext account, RuntimeApplication app, string permission,
        PermissionDecision decision, CancellationToken cancellationToken = default)
    {
        ValidateRequest(account, app, permission);
        if (!Enum.IsDefined(decision)) throw new ArgumentOutOfRangeException(nameof(decision));
        PermissionChange? change = null;
        await CommitMutationAsync(account, app, () =>
        {
            lock (gate)
            {
                // Settings writes are new authoritative decisions, in writer order.
                // A preceding valid write does not invalidate the next settings action.
                cancellationToken.ThrowIfCancellationRequested();
                ValidateRequest(account, app, permission);
                change = SaveDecision(account, app, permission, decision);
            }
        }, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(change!);
    }

    /// <summary>Host settings may revoke an exact historical row without reconstructing
    /// an application or inventing its former domains/repository identity. Never grants.</summary>
    public async Task RevokeHistoricalAsync(RuntimeAccountContext account, PermissionRecord record,
        CancellationToken cancellationToken = default)
    {
        account.EnsureCurrent(); Validate(record);
        if (record.AccountKey != account.AccountKey) throw new RuntimeCapabilityException("PERMISSION_RECORD_STALE");
        PermissionChange? change = null;
        await CommitMutationAsync(account, record.BindingKey, () =>
        {
            lock (gate)
            {
                cancellationToken.ThrowIfCancellationRequested(); account.EnsureCurrent();
                if (!records.TryGetValue(Key(record.AccountKey, record.BindingKey, record.Permission), out var current) || current != record)
                    throw new RuntimeCapabilityException("PERMISSION_RECORD_STALE");
                change = PersistDecision(account, record with { Decision = PermissionDecision.Revoked });
            }
        }, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(change!);
    }

    private void CommitMutation(RuntimeAccountContext account, RuntimeApplication app, Action commit)
    {
        if (authorityMutations is null) { commit(); return; }
        if (!authorityMutations.TryCommitSynchronously(RuntimeMutationScope.ForApplication(account.AccountKey, app.BindingKey), commit))
            throw new RuntimeCapabilityException("RUNTIME_ASYNC_AUTHORITY_REQUIRED");
    }

    private async Task CommitMutationAsync(RuntimeAccountContext account, RuntimeApplication app, Action commit,
        CancellationToken beforeIntentCancellation)
        => await CommitMutationAsync(account, app.BindingKey, commit, beforeIntentCancellation).ConfigureAwait(false);

    private async Task CommitMutationAsync(RuntimeAccountContext account, string binding, Action commit,
        CancellationToken beforeIntentCancellation)
    {
        if (authorityMutations is null) { beforeIntentCancellation.ThrowIfCancellationRequested(); commit(); return; }
        var mutation = await authorityMutations.BeginAsync(RuntimeMutationScope.ForApplication(account.AccountKey, binding),
            beforeIntentCancellation).ConfigureAwait(false);
        // Once intent exists, cancellation must not abandon its one-shot permit.
        await mutation.CommitAsync(commit).ConfigureAwait(false);
    }

    private PermissionChange SaveDecision(RuntimeAccountContext account, RuntimeApplication app, string permission, PermissionDecision decision)
        => PersistDecision(account, new PermissionRecord(account.AccountKey, app.BindingKey, app.Identity.AppId, app.DisplayName,
            app.Source, permission, decision, permission == "network" ? NetworkDomainPolicy.ScopeHash(app.NetworkDomains) : null));

    private PermissionChange PersistDecision(RuntimeAccountContext account, PermissionRecord item)
    {
        using var critical = enterCritical?.Invoke();
        using var lease = account.EnterCommitLease?.Invoke();
        account.EnsureCurrent();
        string key = Key(account.AccountKey, item.BindingKey, item.Permission);
        string bindingKey = Key(account.AccountKey, item.BindingKey, "");
        // Check every next stamp before staging bytes or changing any decision.
        long nextPermissionRevision = checked(revisions.GetValueOrDefault(key) + 1);
        long nextBindingRevision = checked(bindingRevisions.GetValueOrDefault(bindingKey) + 1);
        if (!records.ContainsKey(key) && records.Count >= MaximumRecords) throw new RuntimeCapabilityException("QUOTA_EXCEEDED");
        Validate(item);
        var copy = new Dictionary<string, PermissionRecord>(records, StringComparer.Ordinal) { [key] = item };
        bool network = item.Permission == "network";
        string destination = network ? networkPath : path;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new PermissionDocument(1,
            copy.Values.Where(record => (record.Permission == "network") == network).ToArray()), JsonOptions);
        if (bytes.Length > MaximumBytes) throw new RuntimeCapabilityException("QUOTA_EXCEEDED");
        SafePath(destination, true);
        string temp = Path.Combine(Path.GetDirectoryName(destination)!, $".permission-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            account.EnsureCurrent();
            SafePath(destination, false);
            if (File.Exists(destination))
            {
                string backup = destination + ".bak";
                SafePath(backup, false);
                File.Replace(temp, destination, backup);
            }
            else File.Move(temp, destination, false);
            records[key] = item;
            revisions[key] = nextPermissionRevision;
            bindingRevisions[bindingKey] = nextBindingRevision;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new(account.AccountKey, item.BindingKey, item.Permission, item.Decision);
    }

    private static void ValidateRequest(RuntimeAccountContext account, RuntimeApplication app, string permission)
    {
        account.EnsureCurrent();
        if (!Supported.Contains(permission)) throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
        if (!app.DeclaredPermissions.Contains(permission, StringComparer.Ordinal)) throw new RuntimeCapabilityException("PERMISSION_NOT_DECLARED");
        if (permission == "network") _ = NetworkDomainPolicy.CopyValidated(app.NetworkDomains);
        if (!RuntimeSession.IsValidAppId(app.Identity.AppId) || string.IsNullOrWhiteSpace(app.Source) || app.Source.Length > 512
            || app.Source.Any(char.IsControl) || string.IsNullOrWhiteSpace(app.DisplayName) || app.DisplayName.Length > 128
            || app.DisplayName.Any(char.IsControl)) throw new RuntimeCapabilityException("INVALID_REQUEST");
        if (app.PermissionPurposes is not null && (app.PermissionPurposes.Count > Supported.Count
            || app.PermissionPurposes.Any(pair => !app.DeclaredPermissions.Contains(pair.Key, StringComparer.Ordinal)
                || pair.Value is null || pair.Value.Length is < 1 or > 160 || pair.Value.Any(char.IsControl))))
            throw new RuntimeCapabilityException("INVALID_REQUEST");
    }

    private static void Validate(PermissionRecord record)
    {
        if (record is null || string.IsNullOrWhiteSpace(record.AccountKey) || record.AccountKey.Length > 256
            || record.BindingKey is null || record.BindingKey.Length != 64 || !record.BindingKey.All(char.IsAsciiHexDigit)
            || !RuntimeSession.IsValidAppId(record.AppId) || string.IsNullOrWhiteSpace(record.DisplayName) || record.DisplayName.Length > 128 || record.DisplayName.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(record.Source) || record.Source.Length > 512 || record.Source.Any(char.IsControl) || !Supported.Contains(record.Permission)
            || !Enum.IsDefined(record.Decision) || record.NetworkScopeHash is not null &&
                (record.Permission != "network" || record.NetworkScopeHash.Length != 64 || record.NetworkScopeHash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f'))))
            throw new RuntimeCapabilityException("PERMISSION_STORE_CORRUPT");
    }

    internal static void SafePath(string path, bool createDirectory)
    {
        var ancestors = new Stack<string>();
        for (string? directory = Path.GetDirectoryName(path); directory is not null; directory = Path.GetDirectoryName(directory)) ancestors.Push(directory);
        while (ancestors.TryPop(out var directory))
        {
            Check(directory, true);
            if (createDirectory) Directory.CreateDirectory(directory);
            Check(directory, true);
        }
        Check(path, false);
        static void Check(string name, bool directory)
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(name); }
            catch (FileNotFoundException) { return; }
            catch (DirectoryNotFoundException) { return; }
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new RuntimeCapabilityException("STORAGE_UNSAFE_PATH");
            if (((attributes & FileAttributes.Directory) != 0) != directory) throw new RuntimeCapabilityException("STORAGE_IO_ERROR");
        }
    }

    private static string Key(string account, string binding, string permission) => RuntimeApplication.Hash($"{account}\0{binding}\0{permission}");
    private static string Purpose(string permission) => permission switch
    {
        "identity.profile" => "在当前应用中显示应用范围用户标识、昵称和头像；不提供宿主令牌、邮箱或手机号。",
        "saves" => "在当前应用及账号的存档空间读取和保存进度。",
        "storage" => "读写当前应用及账号的私有文件。",
        "files.open" => "经系统选择器读取你明确选择的文件。",
        "files.save" => "经系统选择器写入你明确选择的文件。",
        "notifications" => "在 AutumnOS 内发送通知并显示角标。",
        "shortcuts" => "注册清单中声明的应用快捷操作。",
        "widgets" => "更新清单中声明的小组件资料。",
        "links" => "发送指向本应用声明动作的内部链接。",
        "network" => "经宿主向清单列出的完整域名集合发送有界 HTTPS 请求；不共享宿主 Cookie、系统凭据或登录令牌。",
        _ => "受限应用能力。"
    };
    private sealed record PermissionDocument(int SchemaVersion, PermissionRecord[] Records);
    private sealed class UsageLease(object synchronization) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; Monitor.Exit(synchronization); } }
    }
}
