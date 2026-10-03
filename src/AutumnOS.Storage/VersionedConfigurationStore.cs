using System.Text.Json;

namespace AutumnOS.Storage;

public sealed record VersionedConfiguration(int SchemaVersion, long Revision, JsonElement Values);

/// <summary>
/// Host-only configuration for new features. Existing desktop/first-run files keep their established
/// readers and are not rewritten merely because T03 is present. Migrations are explicit and atomic.
/// </summary>
public sealed class VersionedConfigurationStore
{
    private const int MaximumBytes = 64 * 1024;
    private readonly InstallationRoot root;
    private readonly string name;
    private readonly int supportedVersion;
    private readonly CriticalOperationCoordinator coordinator;
    private readonly StorageFaultHooks? faults;
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string FilePath => Path.Combine(root.Directories["Config"], name + ".json");
    public VersionedConfigurationStore(InstallationRoot root, string name, int supportedVersion,
        CriticalOperationCoordinator? coordinator = null, StorageFaultHooks? testHooks = null)
    {
        if (!ScopedStorageSafety.IsKey(name) || name is "desktop-preferences" or "first-run" || supportedVersion < 1)
            throw new ArgumentException("Choose a separate versioned host configuration key.");
        this.root = root; this.name = name; this.supportedVersion = supportedVersion;
        this.coordinator = coordinator ?? new(); faults = testHooks;
    }
    public DataResult<VersionedConfiguration?> Load() => Execute(() => Read(FilePath));

    /// <summary>Read-only host UI availability, independent of whether the current main configuration parses.</summary>
    public DataResult<ConfigurationBackupInfo> InspectBackup() => Execute(() =>
    {
        string backup = FilePath + ".bak", before = FilePath + ".before-restore";
        ScopedStorageSafety.File(FilePath); ScopedStorageSafety.File(backup); ScopedStorageSafety.File(before);
        bool exists = File.Exists(backup), valid = false; int? schema = null; VersionedConfiguration? inspected = null;
        string error = exists ? "NONE" : "CONFIG_BACKUP_NOT_FOUND";
        if (exists)
        {
            try { inspected = Read(backup); valid = inspected is not null; schema = inspected?.SchemaVersion; }
            catch (DataStoreException exception) { error = exception.Code; }
        }
        return new ConfigurationBackupInfo(File.Exists(FilePath), exists, valid, error, File.Exists(before), schema, inspected);
    });

    public DataResult<VersionedConfiguration> Save(JsonElement values, CancellationToken token = default) => Execute(() =>
    {
        if (values.ValueKind != JsonValueKind.Object) throw new DataStoreException("CONFIG_INVALID_VALUE");
        using IDisposable critical = coordinator.EnterWrite("系统配置写入"); Ensure(); using FileStream dataLock = Lock();
        VersionedConfiguration? previous = Read(FilePath);
        if (previous is not null && previous.SchemaVersion != supportedVersion) throw new DataStoreException("CONFIG_MIGRATION_REQUIRED");
        if (previous is not null && JsonElement.DeepEquals(previous.Values, values)) return previous;
        if (previous?.Revision == long.MaxValue) throw new DataStoreException("CONFIG_CORRUPT");
        var next = new VersionedConfiguration(supportedVersion, (previous?.Revision ?? 0) + 1, values.Clone());
        Commit(next, keepBackup: true, token); return next;
    });

    public DataResult<VersionedConfiguration> Migrate(int fromVersion, Func<JsonElement, JsonElement> transform,
        CancellationToken token = default) => Execute(() =>
    {
        if (fromVersion < 1 || fromVersion >= supportedVersion) throw new DataStoreException("CONFIG_INVALID_VALUE");
        using IDisposable critical = coordinator.EnterWrite("系统配置迁移"); Ensure(); using FileStream dataLock = Lock();
        VersionedConfiguration previous = Read(FilePath) ?? throw new DataStoreException("CONFIG_NOT_FOUND");
        if (previous.SchemaVersion != fromVersion || previous.Revision == long.MaxValue) throw new DataStoreException("CONFIG_VERSION_CONFLICT");
        JsonElement values;
        try { values = transform(previous.Values.Clone()).Clone(); }
        catch (Exception e) when (e is not OperationCanceledException) { throw new DataStoreException("CONFIG_MIGRATION_FAILED"); }
        if (values.ValueKind != JsonValueKind.Object) throw new DataStoreException("CONFIG_MIGRATION_FAILED");
        var next = new VersionedConfiguration(supportedVersion, previous.Revision + 1, values);
        Commit(next, keepBackup: true, token); return next;
    });

    public DataResult<VersionedConfiguration> RestoreBackup(bool confirmed, CancellationToken token = default) => Execute(() =>
    {
        if (!confirmed) throw new DataStoreException("USER_CANCELLED");
        using IDisposable critical = coordinator.EnterWrite("系统配置恢复"); Ensure(); using FileStream dataLock = Lock();
        VersionedConfiguration restored = Read(FilePath + ".bak") ?? throw new DataStoreException("CONFIG_BACKUP_NOT_FOUND");
        string original = FilePath + ".before-restore";
        ScopedStorageSafety.File(FilePath); ScopedStorageSafety.File(original); token.ThrowIfCancellationRequested();
        if (File.Exists(FilePath))
        {
            if (File.Exists(original)) throw new DataStoreException("CONFIG_RECOVERY_CONFLICT");
            File.Copy(FilePath, original, overwrite: false);
        }
        Commit(restored, keepBackup: false, token); return restored;
    });

    private void Ensure()
    {
        DataRootResult result = root.EnsureCreated();
        if (!result.Success) throw new DataStoreException(result.ErrorCode);
    }
    private FileStream Lock()
    {
        string path = Path.Combine(root.Directories["Config"], $".{name}.lock");
        ScopedStorageSafety.File(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private VersionedConfiguration? Read(string path)
    {
        ScopedStorageSafety.File(path);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is <= 0 or > MaximumBytes) throw new DataStoreException("CONFIG_CORRUPT");
        try
        {
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement item = document.RootElement;
            if (item.ValueKind != JsonValueKind.Object) throw new DataStoreException("CONFIG_CORRUPT");
            string[] keys = item.EnumerateObject().Select(p => p.Name).ToArray();
            if (keys.Length != 3 || keys.Distinct(StringComparer.Ordinal).Count() != 3
                || !item.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out int schema)
                || !item.TryGetProperty("revision", out var revision) || !revision.TryGetInt64(out long rev)
                || !item.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object
                || schema < 1 || rev < 1) throw new DataStoreException("CONFIG_CORRUPT");
            if (schema > supportedVersion) throw new DataStoreException("CONFIG_FUTURE_SCHEMA");
            return new(schema, rev, values.Clone());
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        { throw new DataStoreException("CONFIG_CORRUPT"); }
    }
    private void Commit(VersionedConfiguration next, bool keepBackup, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
        if (bytes.Length > MaximumBytes) throw new DataStoreException("CONFIG_TOO_LARGE");
        string temporary = Path.Combine(root.Directories["Config"], $".{name}-{Guid.NewGuid():N}.tmp");
        try
        {
            faults?.Invoke(StorageWritePoint.BeforeWrite);
            ScopedStorageSafety.File(temporary);
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(true); }
            faults?.Invoke(StorageWritePoint.AfterStagingFlush); token.ThrowIfCancellationRequested();
            faults?.Invoke(StorageWritePoint.BeforeCommit); token.ThrowIfCancellationRequested();
            ScopedStorageSafety.File(FilePath); ScopedStorageSafety.File(FilePath + ".bak"); ScopedStorageSafety.File(temporary);
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, keepBackup ? FilePath + ".bak" : null);
            else File.Move(temporary, FilePath, false);
        }
        finally
        {
            try { ScopedStorageSafety.File(temporary); File.Delete(temporary); }
            catch (Exception e) when (ScopedStorageSafety.Handled(e)) { }
        }
    }
    private static DataResult<T> Execute<T>(Func<T> action)
    {
        try { return DataResult<T>.Ok(action()); }
        catch (Exception e) when (ScopedStorageSafety.Handled(e)) { return DataResult<T>.Fail(ScopedStorageSafety.Error(e)); }
    }
}
