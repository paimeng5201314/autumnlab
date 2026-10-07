using System.Security.Cryptography;
using System.Text.Json;

namespace AutumnOS.Storage;

/// <summary>Host-bound private storage. Every mutation stages and flushes before a guarded atomic commit.</summary>
public sealed class AccountDataStore
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly InstallationRoot root;
    private readonly StorageScope scope;
    private readonly Func<StorageScope, bool> isCurrent;
    private readonly Func<IDisposable> commitLease;
    private readonly bool legacyGuest;
    private readonly StorageLimits limits;
    private readonly StorageFaultHooks? faults;
    private readonly CriticalOperationCoordinator coordinator;
    private string SourceDirectory => Path.Combine(scope.Identity.AppId, scope.SourceKey);
    private string SavesDirectory => legacyGuest && scope.AccountKey == "guest"
        ? Path.Combine(root.Directories["Saves"], scope.Identity.AppId, "guest")
        : Path.Combine(root.Directories["Saves"], SourceDirectory, scope.AccountDirectory);
    private string PrivateDirectory => Path.Combine(root.Directories["AppData"], SourceDirectory, scope.AccountDirectory);
    private string PreferencesDirectory => Path.Combine(root.Directories["AppData"], SourceDirectory, "Preferences", scope.AccountDirectory);

    public AccountDataStore(InstallationRoot root, StorageScope scope, Func<StorageScope, bool> isCurrent,
        Func<IDisposable>? commitLease = null, bool allowLegacyGuest = false, StorageLimits? limits = null,
        StorageFaultHooks? testHooks = null, CriticalOperationCoordinator? coordinator = null)
    {
        ScopedStorageSafety.ValidateScope(scope);
        this.root = root; this.scope = scope; this.isCurrent = isCurrent;
        this.commitLease = commitLease ?? (() => new CriticalOperationCoordinator.Lease(() => { }));
        legacyGuest = allowLegacyGuest; this.limits = limits ?? new(); faults = testHooks;
        this.coordinator = coordinator ?? new();
        if (this.limits.MaximumSlots is < 1 or > 256 || this.limits.MaximumPrivateFiles is < 1 or > 4096
            || this.limits.MaximumSaveBytes is < 256 or > 8 * 1024 * 1024
            || this.limits.MaximumPrivateFileBytes is < 1 or > 8 * 1024 * 1024 || this.limits.MaximumAccountBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(limits));
    }

    public DataResult<JsonElement?> ReadSave(string slot, CancellationToken cancellationToken = default) => Execute(() =>
    {
        Check(slot, cancellationToken);
        SaveRecord? record = ReadRecord(PathForSlot(slot), slot);
        using (commitLease()) { CheckCurrent(cancellationToken); return record?.Value.Clone(); }
    });

    public DataResult<SaveSlot[]> ListSlots(CancellationToken cancellationToken = default) => Execute(() =>
    {
        CheckCurrent(cancellationToken);
        ScopedStorageSafety.Directory(SavesDirectory, false);
        if (!Directory.Exists(SavesDirectory)) return [];
        var slots = new List<SaveSlot>();
        foreach (string path in Directory.EnumerateFiles(SavesDirectory, "*.json").Take(limits.MaximumSlots + 1))
        {
            string slot = Path.GetFileNameWithoutExtension(path);
            Check(slot, cancellationToken);
            SaveRecord record = ReadRecord(path, slot)!;
            slots.Add(new(slot, record.FormatVersion, record.Revision, new FileInfo(path).Length, File.Exists(path + ".bak")));
        }
        if (slots.Count > limits.MaximumSlots) throw new DataStoreException("STORAGE_QUOTA_EXCEEDED");
        using (commitLease()) { CheckCurrent(cancellationToken); return slots.OrderBy(s => s.Slot, StringComparer.Ordinal).ToArray(); }
    });

    /// <summary>Read-only recovery availability; a damaged main record does not prevent checking a valid bound backup.</summary>
    public DataResult<SaveRecoveryInfo> InspectSaveRecovery(string slot, CancellationToken cancellationToken = default) => Execute(() =>
    {
        Check(slot, cancellationToken);
        string path = PathForSlot(slot), backup = path + ".bak", before = path + ".before-restore";
        ScopedStorageSafety.File(path); ScopedStorageSafety.File(backup); ScopedStorageSafety.File(before);
        bool exists = File.Exists(backup), valid = false;
        string error = exists ? "NONE" : "SAVE_BACKUP_NOT_FOUND";
        if (exists)
        {
            try { valid = ReadRecord(backup, slot) is not null; }
            catch (DataStoreException exception) { error = exception.Code; }
        }
        using (commitLease())
        {
            CheckCurrent(cancellationToken);
            return new SaveRecoveryInfo(slot, File.Exists(path), exists, valid, error, File.Exists(before));
        }
    });

    public DataResult<bool> WriteSave(string slot, JsonElement value, int formatVersion = 1, CancellationToken cancellationToken = default)
        => Execute(() =>
        {
            Check(slot, cancellationToken);
            if (formatVersion is < 1 or > 1_000_000 || value.ValueKind is JsonValueKind.Undefined) throw new DataStoreException("INVALID_PARAMS");
            using IDisposable critical = coordinator.EnterWrite("游戏存档写入");
            Ensure();
            using FileStream dataLock = Lock();
            string path = PathForSlot(slot);
            SaveRecord? previous = ReadRecord(path, slot);
            string hash = Hash(value);
            if (previous?.ContentHash == hash && previous.FormatVersion == formatVersion) return true;
            if (previous?.Revision == long.MaxValue) throw new DataStoreException("SAVE_CORRUPT");
            if (previous is null && Directory.EnumerateFiles(SavesDirectory, "*.json").Take(limits.MaximumSlots).Count() >= limits.MaximumSlots)
                throw new DataStoreException("STORAGE_QUOTA_EXCEEDED");
            byte[] bytes = Serialize(new(2, scope.Identity.AppId, scope.SourceKey, scope.AccountDirectory, slot,
                formatVersion, (previous?.Revision ?? 0) + 1, hash, value.Clone()));
            if (bytes.Length > limits.MaximumSaveBytes) throw new DataStoreException("SAVE_TOO_LARGE");
            Commit(path, bytes, keepBackup: true, cancellationToken);
            return true;
        });

    public DataResult<bool> RestoreSave(string slot, CancellationToken cancellationToken = default) => Execute(() =>
    {
        Check(slot, cancellationToken);
        using IDisposable critical = coordinator.EnterWrite("游戏存档恢复");
        Ensure(); using FileStream dataLock = Lock();
        string path = PathForSlot(slot);
        SaveRecord backup = ReadRecord(path + ".bak", slot) ?? throw new DataStoreException("SAVE_BACKUP_NOT_FOUND");
        // Explicit recovery keeps damaged/current bytes for examination, and retains the valid backup.
        string damaged = path + ".before-restore";
        ScopedStorageSafety.File(path); ScopedStorageSafety.File(damaged);
        using (commitLease())
        {
            CheckCurrent(cancellationToken);
            if (File.Exists(path))
            {
                if (File.Exists(damaged)) throw new DataStoreException("SAVE_RECOVERY_CONFLICT");
                CheckQuota(new FileInfo(path).Length);
                File.Copy(path, damaged, false);
            }
        }
        // A legacy v1 payload is upgraded when explicitly restored; never emit v2 fields with a v1 header.
        Commit(path, Serialize(backup with { SchemaVersion = 2 }), keepBackup: false, cancellationToken);
        return true;
    });

    /// <summary>The migration function is trusted host/app-version code, never executable SDK input.</summary>
    public DataResult<bool> MigrateSaveFormat(string slot, int fromVersion, int toVersion,
        Func<JsonElement, JsonElement> migration, CancellationToken cancellationToken = default) => Execute(() =>
    {
        Check(slot, cancellationToken);
        if (toVersion <= fromVersion || toVersion > 1_000_000) throw new DataStoreException("INVALID_PARAMS");
        using IDisposable critical = coordinator.EnterWrite("游戏存档格式迁移");
        Ensure(); using FileStream dataLock = Lock();
        string path = PathForSlot(slot);
        SaveRecord old = ReadRecord(path, slot) ?? throw new DataStoreException("SAVE_NOT_FOUND");
        if (old.FormatVersion != fromVersion) throw new DataStoreException("SAVE_FORMAT_CONFLICT");
        JsonElement next;
        try { next = migration(old.Value.Clone()).Clone(); }
        catch (Exception e) when (e is not OperationCanceledException) { throw new DataStoreException("SAVE_MIGRATION_FAILED"); }
        if (next.ValueKind == JsonValueKind.Undefined || old.Revision == long.MaxValue) throw new DataStoreException("SAVE_MIGRATION_FAILED");
        byte[] bytes = Serialize(old with { SchemaVersion = 2, FormatVersion = toVersion, Revision = old.Revision + 1, ContentHash = Hash(next), Value = next });
        if (bytes.Length > limits.MaximumSaveBytes) throw new DataStoreException("SAVE_TOO_LARGE");
        Commit(path, bytes, true, cancellationToken);
        return true;
    });

    /// <summary>Explicit copy only: the guest source and its backup remain intact; an existing account target is a conflict.</summary>
    public DataResult<bool> MigrateGuestSave(string slot, bool confirmed, CancellationToken cancellationToken = default) => Execute(() =>
    {
        Check(slot, cancellationToken);
        if (!confirmed) throw new DataStoreException("USER_CANCELLED");
        if (scope.AccountKey == "guest") throw new DataStoreException("INVALID_PARAMS");
        using IDisposable critical = coordinator.EnterWrite("游客存档迁移");
        Ensure(); using FileStream dataLock = Lock();
        string target = PathForSlot(slot);
        ScopedStorageSafety.File(target);
        if (File.Exists(target)) throw new DataStoreException("SAVE_MIGRATION_CONFLICT");
        var guest = new AccountDataStore(root, scope with { AccountKey = "guest" }, _ => true,
            allowLegacyGuest: legacyGuest, limits: limits);
        string source = guest.PathForSlot(slot);
        string guestLockPath = Path.Combine(guest.SavesDirectory, ".account-data.lock");
        ScopedStorageSafety.Directory(guest.SavesDirectory, false);
        if (!Directory.Exists(guest.SavesDirectory)) throw new DataStoreException("SAVE_NOT_FOUND");
        ScopedStorageSafety.File(guestLockPath);
        using var sourceLock = new FileStream(guestLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        SaveRecord original = guest.ReadRecord(source, slot) ?? throw new DataStoreException("SAVE_NOT_FOUND");
        SaveRecord imported = original with { SchemaVersion = 2, SourceKey = scope.SourceKey, AccountKey = scope.AccountDirectory, Revision = 1 };
        byte[] bytes = Serialize(imported);
        if (bytes.Length > limits.MaximumSaveBytes) throw new DataStoreException("SAVE_TOO_LARGE");
        // The import backup is created first, before a target becomes visible; it binds to the target account.
        string importBackup = target + ".import-backup";
        ScopedStorageSafety.File(importBackup);
        if (Directory.EnumerateFiles(SavesDirectory, "*.json").Take(limits.MaximumSlots).Count() >= limits.MaximumSlots)
            throw new DataStoreException("STORAGE_QUOTA_EXCEEDED");
        if (File.Exists(importBackup))
        {
            SaveRecord existing = ReadRecord(importBackup, slot) ?? throw new DataStoreException("SAVE_MIGRATION_CONFLICT");
            if (!Serialize(existing).SequenceEqual(bytes)) throw new DataStoreException("SAVE_MIGRATION_CONFLICT");
        }
        else Commit(importBackup, bytes, false, cancellationToken);
        Commit(target, bytes, false, cancellationToken);
        return true;
    });

    public DataResult<byte[]?> ReadPrivate(string key, CancellationToken cancellationToken = default) => Execute(() =>
    {
        Check(key, cancellationToken);
        string path = Path.Combine(PrivateDirectory, key + ".bin");
        ScopedStorageSafety.File(path);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length > limits.MaximumPrivateFileBytes) throw new DataStoreException("FILE_TOO_LARGE");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        using (commitLease()) { CheckCurrent(cancellationToken); return bytes; }
    });

    public DataResult<bool> WritePrivate(string key, byte[] bytes, CancellationToken cancellationToken = default) => Execute(() =>
    {
        Check(key, cancellationToken);
        if (bytes.Length > limits.MaximumPrivateFileBytes) throw new DataStoreException("FILE_TOO_LARGE");
        using IDisposable critical = coordinator.EnterWrite("应用私有数据写入");
        Ensure(); using FileStream dataLock = Lock();
        if (key.StartsWith("pref_", StringComparison.OrdinalIgnoreCase)) EnsurePreferencesMigrated(cancellationToken);
        string path = Path.Combine(PrivateDirectory, key + ".bin");
        if (!File.Exists(path) && Directory.EnumerateFiles(PrivateDirectory, "*.bin").Take(limits.MaximumPrivateFiles).Count() >= limits.MaximumPrivateFiles)
            throw new DataStoreException("STORAGE_QUOTA_EXCEEDED");
        Commit(path, bytes, true, cancellationToken); return true;
    });

    /// <summary>Deleting one private key does not clear saves, app installation, browser cache or account credentials.</summary>
    public DataResult<bool> DeletePrivate(string key, CancellationToken cancellationToken = default) => Execute(() =>
    {
        Check(key, cancellationToken);
        using IDisposable critical = coordinator.EnterWrite("应用私有数据删除");
        Ensure(); using FileStream dataLock = Lock();
        if (key.StartsWith("pref_", StringComparison.OrdinalIgnoreCase)) EnsurePreferencesMigrated(cancellationToken);
        string path = Path.Combine(PrivateDirectory, key + ".bin");
        using (commitLease())
        {
            CheckCurrent(cancellationToken); ScopedStorageSafety.File(path);
            if (!File.Exists(path)) return false;
            string backup = path + ".bak"; ScopedStorageSafety.File(backup);
            File.Move(path, backup, overwrite: true); return true;
        }
    });

    /// <summary>Preferences have their own namespace; legacy private files are copied once and retained verbatim.</summary>
    public DataResult<JsonElement?> ReadPreference(string key, CancellationToken cancellationToken = default) => Execute(() =>
    {
        CheckPreference(key, cancellationToken);
        using IDisposable critical = coordinator.EnterWrite("应用偏好迁移与读取");
        Ensure(); using FileStream dataLock = Lock();
        EnsurePreferencesMigrated(cancellationToken);
        JsonElement? value = ReadPreferenceValue(PreferencePath(key));
        using (commitLease()) { CheckCurrent(cancellationToken); return value; }
    });

    public DataResult<bool> WritePreference(string key, JsonElement value, CancellationToken cancellationToken = default) => Execute(() =>
    {
        CheckPreference(key, cancellationToken);
        if (value.ValueKind == JsonValueKind.Undefined) throw new DataStoreException("INVALID_PARAMS");
        byte[] bytes;
        try { bytes = JsonSerializer.SerializeToUtf8Bytes(value); }
        catch (JsonException) { throw new DataStoreException("INVALID_PARAMS"); }
        if (bytes.Length > limits.MaximumPrivateFileBytes) throw new DataStoreException("FILE_TOO_LARGE");
        // Use the same JSON depth limit for writes and reads; accepted data must remain readable.
        ParsePreference(bytes, "INVALID_PARAMS");
        using IDisposable critical = coordinator.EnterWrite("应用偏好写入");
        Ensure(); using FileStream dataLock = Lock();
        EnsurePreferencesMigrated(cancellationToken);
        string path = PreferencePath(key);
        ReadPreferenceValue(path); // Preserve a damaged record instead of silently replacing it.
        CheckPreferenceCapacity(path);
        Commit(path, bytes, true, cancellationToken);
        return true;
    });

    private string PreferencePath(string key) => Path.Combine(PreferencesDirectory, "pref_" + key + ".json");
    private void CheckPreference(string key, CancellationToken token)
    {
        if (!ScopedStorageSafety.IsPreferenceKey(key)) throw new DataStoreException("INVALID_PARAMS");
        CheckCurrent(token);
    }
    private JsonElement? ReadPreferenceValue(string path)
    {
        ScopedStorageSafety.File(path);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is <= 0 || stream.Length > limits.MaximumPrivateFileBytes) throw new DataStoreException("PREFERENCE_CORRUPT");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
        return ParsePreference(bytes);
    }
    private static JsonElement ParsePreference(byte[] bytes, string errorCode = "PREFERENCE_CORRUPT")
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
            if (!HasUniquePreferenceProperties(document.RootElement)) throw new DataStoreException(errorCode);
            return document.RootElement.Clone();
        }
        catch (JsonException) { throw new DataStoreException(errorCode); }
    }
    private static bool HasUniquePreferenceProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
                if (!names.Add(property.Name) || !HasUniquePreferenceProperties(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray())
                if (!HasUniquePreferenceProperties(item)) return false;
        }
        return true;
    }
    private void CheckPreferenceCapacity(string path)
    {
        if (!File.Exists(path) && Directory.EnumerateFiles(PreferencesDirectory, "*.json").Take(limits.MaximumPrivateFiles).Count() >= limits.MaximumPrivateFiles)
            throw new DataStoreException("STORAGE_QUOTA_EXCEEDED");
    }
    private void EnsurePreferencesMigrated(CancellationToken token)
    {
        // The account lock also guards private writes/deletes, so a new pref_ key cannot race migration.
        // Completion is durable before any new pref_ private write. Its bytes are never interpreted as a legacy preference.
        string marker = Path.Combine(PreferencesDirectory, ".legacy-migration-complete");
        ScopedStorageSafety.File(marker);
        if (File.Exists(marker))
        {
            using var stream = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length != 1 || stream.ReadByte() != 1) throw new DataStoreException("PREFERENCE_MIGRATION_CONFLICT");
            return;
        }
        int count = 0;
        foreach (string source in Directory.EnumerateFiles(PrivateDirectory, "*.bin"))
        {
            CheckCurrent(token);
            string oldKey = Path.GetFileNameWithoutExtension(source);
            if (!oldKey.StartsWith("pref_", StringComparison.OrdinalIgnoreCase) || !ScopedStorageSafety.IsPreferenceKey(oldKey[5..])) continue;
            if (++count > limits.MaximumPrivateFiles) throw new DataStoreException("STORAGE_QUOTA_EXCEEDED");
            ScopedStorageSafety.File(source);
            byte[] bytes;
            using (var stream = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            {
                if (stream.Length > limits.MaximumPrivateFileBytes) throw new DataStoreException("PREFERENCE_CORRUPT");
                bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            }
            string target = PreferencePath(oldKey[5..]);
            ScopedStorageSafety.File(target);
            if (File.Exists(target))
            {
                using var existing = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                if (existing.Length != bytes.Length) throw new DataStoreException("PREFERENCE_MIGRATION_CONFLICT");
                var copied = new byte[bytes.Length]; existing.ReadExactly(copied);
                if (!copied.AsSpan().SequenceEqual(bytes)) throw new DataStoreException("PREFERENCE_MIGRATION_CONFLICT");
            }
            else
            {
                CheckPreferenceCapacity(target);
                Commit(target, bytes, false, token);
            }
        }
        Commit(marker, [1], false, token);
    }

    private void Check(string key, CancellationToken token)
    {
        if (!ScopedStorageSafety.IsKey(key)) throw new DataStoreException("INVALID_PARAMS");
        CheckCurrent(token);
    }
    private void CheckCurrent(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!isCurrent(scope)) throw new DataStoreException("SESSION_STALE");
    }
    private void Ensure()
    {
        DataRootResult result = root.EnsureCreated();
        if (!result.Success) throw new DataStoreException(result.ErrorCode);
        ScopedStorageSafety.Directory(SavesDirectory, true); ScopedStorageSafety.Directory(PrivateDirectory, true);
        ScopedStorageSafety.Directory(PreferencesDirectory, true);
    }
    private FileStream Lock()
    {
        string path = Path.Combine(SavesDirectory, ".account-data.lock"); ScopedStorageSafety.File(path);
        return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }
    private string PathForSlot(string slot) => Path.Combine(SavesDirectory, slot + ".json");
    private static byte[] Serialize(SaveRecord record) => JsonSerializer.SerializeToUtf8Bytes(record, Json);
    private static string Hash(JsonElement value) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value)));

    private SaveRecord? ReadRecord(string path, string slot)
    {
        ScopedStorageSafety.File(path);
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is <= 0 || stream.Length > limits.MaximumSaveBytes) throw new DataStoreException("SAVE_CORRUPT");
        try
        {
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 40 });
            JsonElement data = document.RootElement;
            if (data.ValueKind != JsonValueKind.Object) throw new DataStoreException("SAVE_CORRUPT");
            string[] names = data.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length || !data.TryGetProperty("schemaVersion", out var schema)
                || !schema.TryGetInt32(out int version)) throw new DataStoreException("SAVE_CORRUPT");
            if (version > 2) throw new DataStoreException("SAVE_FUTURE_SCHEMA");
            if (version == 1 && legacyGuest && scope.AccountKey == "guest" && slot == "game"
                && names.Length == 5 && data.GetProperty("appId").GetString() == scope.Identity.AppId
                && data.GetProperty("accountMode").GetString() == "guest" && data.GetProperty("slot").GetString() == slot)
            {
                JsonElement value = data.GetProperty("value").Clone();
                return new(1, scope.Identity.AppId, scope.SourceKey, "guest", slot, 1, 1, Hash(value), value);
            }
            if (version != 2 || names.Length != 9) throw new DataStoreException("SAVE_CORRUPT");
            SaveRecord record = data.Deserialize<SaveRecord>(Json) ?? throw new DataStoreException("SAVE_CORRUPT");
            if (record.AppId != scope.Identity.AppId || record.SourceKey != scope.SourceKey || record.AccountKey != scope.AccountDirectory
                || record.Slot != slot || record.FormatVersion < 1 || record.Revision < 1 || record.Value.ValueKind == JsonValueKind.Undefined
                || !string.Equals(record.ContentHash, Hash(record.Value), StringComparison.Ordinal)) throw new DataStoreException("SAVE_CORRUPT");
            return record with { Value = record.Value.Clone() };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        { throw new DataStoreException("SAVE_CORRUPT"); }
    }

    private void Commit(string path, byte[] bytes, bool keepBackup, CancellationToken token)
    {
        ScopedStorageSafety.File(path); ScopedStorageSafety.File(path + ".bak");
        // Quota includes backups, forensic recovery files and staging headroom. Failed writes keep original bytes.
        CheckQuota(bytes.LongLength);
        string staging = Path.Combine(Path.GetDirectoryName(path)!, $".staging-{Guid.NewGuid():N}");
        try
        {
            faults?.Invoke(StorageWritePoint.BeforeWrite);
            using (var stream = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            faults?.Invoke(StorageWritePoint.AfterStagingFlush);
            using (commitLease())
            {
                CheckCurrent(token); faults?.Invoke(StorageWritePoint.BeforeCommit); CheckCurrent(token);
                ScopedStorageSafety.File(path); ScopedStorageSafety.File(staging); ScopedStorageSafety.File(path + ".bak");
                if (File.Exists(path)) File.Replace(staging, path, keepBackup ? path + ".bak" : null);
                else File.Move(staging, path, overwrite: false);
            }
        }
        finally
        {
            try { ScopedStorageSafety.File(staging); File.Delete(staging); }
            catch (Exception e) when (ScopedStorageSafety.Handled(e)) { }
        }
    }

    private void CheckQuota(long extra)
    {
        long bytes = extra;
        foreach (string directory in new[] { SavesDirectory, PrivateDirectory, PreferencesDirectory })
        {
            ScopedStorageSafety.Directory(directory, false);
            if (!Directory.Exists(directory)) continue;
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                ScopedStorageSafety.File(path);
                bytes = checked(bytes + new FileInfo(path).Length);
                if (bytes > limits.MaximumAccountBytes) throw new DataStoreException("STORAGE_QUOTA_EXCEEDED");
            }
        }
        if (bytes > limits.MaximumAccountBytes) throw new DataStoreException("STORAGE_QUOTA_EXCEEDED");
    }

    private static DataResult<T> Execute<T>(Func<T> action)
    {
        try { return DataResult<T>.Ok(action()); }
        catch (Exception e) when (ScopedStorageSafety.Handled(e)) { return DataResult<T>.Fail(ScopedStorageSafety.Error(e)); }
    }
    private sealed record SaveRecord(int SchemaVersion, string AppId, string SourceKey, string AccountKey, string Slot,
        int FormatVersion, long Revision, string ContentHash, JsonElement Value);
}
