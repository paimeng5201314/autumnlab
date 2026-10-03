using System.Text.Json;
using System.Text.Json.Serialization;
using AutumnOS.Contracts;
using System.Collections.Concurrent;

namespace AutumnOS.Runtime;

/// <summary>Host-managed, persistent grants. Source identity and each permission are independently bound.</summary>
public sealed class PermissionService : IPermissionService
{
    public static readonly IReadOnlySet<string> Supported = new HashSet<string>(StringComparer.Ordinal)
    { "saves", "identity.profile", "storage", "files.open", "files.save", "notifications", "shortcuts", "widgets", "links" };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter<PermissionDecision>() }
    };
    private readonly object gate = new();
    private readonly string path;
    private readonly Func<IDisposable>? enterCritical;
    private readonly Dictionary<string, PermissionRecord> records = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> revisions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> bindingRevisions = new(StringComparer.Ordinal);
    private readonly HashSet<string> pending = new(StringComparer.Ordinal);
    private const int MaximumRecords = 4096;
    private const int MaximumBytes = 2 * 1024 * 1024;
    public event Action<PermissionChange>? Changed;

    public PermissionService(string configurationDirectory, Func<IDisposable>? enterCritical = null)
    {
        this.enterCritical = enterCritical;
        if (!Path.IsPathFullyQualified(configurationDirectory)) throw new ArgumentException("An absolute configuration directory is required.", nameof(configurationDirectory));
        path = Path.Combine(Path.GetFullPath(configurationDirectory), "application-permissions.v1.json");
        SafePath(path, false);
        if (!File.Exists(path)) return;
        try
        {
            var info = new FileInfo(path);
            if (info.Length is <= 0 or > MaximumBytes) throw new RuntimeCapabilityException("PERMISSION_STORE_CORRUPT");
            using var document = JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 12 });
            if (RuntimeSession.HasDuplicateProperties(document.RootElement)) throw new RuntimeCapabilityException("PERMISSION_STORE_CORRUPT");
            var stored = document.RootElement.Deserialize<PermissionDocument>(JsonOptions);
            if (stored is null || stored.SchemaVersion != 1 || stored.Records is null || stored.Records.Length > MaximumRecords)
                throw new RuntimeCapabilityException("PERMISSION_STORE_CORRUPT");
            foreach (var record in stored.Records)
            {
                Validate(record);
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
        lock (gate) return records.TryGetValue(Key(account.AccountKey, app.BindingKey, permission), out var item) ? item.Decision : PermissionDecision.Prompt;
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
            var fields = permission == "identity.profile" ? new[] { "appScopedUserId", "displayName", "avatarUrl", "isCached" } : Array.Empty<string>();
            string purpose = app.PermissionPurposes is not null && app.PermissionPurposes.TryGetValue(permission, out var declaredPurpose)
                ? $"应用声明用途：{declaredPurpose}\n平台边界：{Purpose(permission)}" : Purpose(permission);
            var request = new RuntimePermissionPrompt(app.Identity.AppId, app.DisplayName, app.Source, permission, purpose, fields);
            bool accepted = await prompt(request, linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
            PermissionChange change;
            lock (gate)
            {
                linked.Token.ThrowIfCancellationRequested();
                account.EnsureCurrent();
                if (revisions.GetValueOrDefault(key) != revision) throw new RuntimeCapabilityException("PERMISSION_REVOKED");
                change = SaveDecision(account, app, permission, accepted ? PermissionDecision.Granted : PermissionDecision.Denied);
            }
            Changed?.Invoke(change);
            return change.Decision;
        }
        finally { lock (gate) pending.Remove(key); }
    }

    /// <summary>Settings-only mutation; the application bridge never exposes this method.</summary>
    public void SetDecision(RuntimeAccountContext account, RuntimeApplication app, string permission, PermissionDecision decision)
    {
        ValidateRequest(account, app, permission);
        if (!Enum.IsDefined(decision)) throw new ArgumentOutOfRangeException(nameof(decision));
        PermissionChange change;
        lock (gate)
        {
            account.EnsureCurrent();
            change = SaveDecision(account, app, permission, decision);
        }
        Changed?.Invoke(change);
    }

    private PermissionChange SaveDecision(RuntimeAccountContext account, RuntimeApplication app, string permission, PermissionDecision decision)
    {
        using var critical = enterCritical?.Invoke();
        using var lease = account.EnterCommitLease?.Invoke();
        account.EnsureCurrent();
        string key = Key(account.AccountKey, app.BindingKey, permission);
        if (!records.ContainsKey(key) && records.Count >= MaximumRecords) throw new RuntimeCapabilityException("QUOTA_EXCEEDED");
        var item = new PermissionRecord(account.AccountKey, app.BindingKey, app.Identity.AppId, app.DisplayName, app.Source, permission, decision);
        Validate(item);
        var copy = new Dictionary<string, PermissionRecord>(records, StringComparer.Ordinal) { [key] = item };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new PermissionDocument(1, copy.Values.ToArray()), JsonOptions);
        if (bytes.Length > MaximumBytes) throw new RuntimeCapabilityException("QUOTA_EXCEEDED");
        SafePath(path, true);
        string temp = Path.Combine(Path.GetDirectoryName(path)!, $".permission-{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            account.EnsureCurrent();
            SafePath(path, false);
            if (File.Exists(path))
            {
                string backup = path + ".bak";
                SafePath(backup, false);
                File.Replace(temp, path, backup);
            }
            else File.Move(temp, path, false);
            records[key] = item;
            revisions[key] = revisions.GetValueOrDefault(key) + 1;
            bindingRevisions.AddOrUpdate(Key(account.AccountKey, app.BindingKey, ""), 1, (_, previous) => previous + 1);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
        return new(account.AccountKey, app.BindingKey, permission, decision);
    }

    private static void ValidateRequest(RuntimeAccountContext account, RuntimeApplication app, string permission)
    {
        account.EnsureCurrent();
        if (!Supported.Contains(permission)) throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
        if (!app.DeclaredPermissions.Contains(permission, StringComparer.Ordinal)) throw new RuntimeCapabilityException("PERMISSION_NOT_DECLARED");
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
            || !Enum.IsDefined(record.Decision)) throw new RuntimeCapabilityException("PERMISSION_STORE_CORRUPT");
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
        _ => "受限应用能力。"
    };
    private sealed record PermissionDocument(int SchemaVersion, PermissionRecord[] Records);
    private sealed class UsageLease(object synchronization) : IDisposable
    {
        private bool disposed;
        public void Dispose() { if (!disposed) { disposed = true; Monitor.Exit(synchronization); } }
    }
}
