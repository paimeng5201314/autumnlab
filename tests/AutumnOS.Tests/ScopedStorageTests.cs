using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Storage;

namespace AutumnOS.Tests;

public static class ScopedStorageTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("storage.t03.guest_accounts_and_sources_are_distinct", Isolated);
        yield return ("storage.t03.slots_private_keys_and_quota", Quotas);
        yield return ("storage.t03.rejects_paths_and_reserved_names", UnsafeKeys);
        yield return ("storage.t03.atomic_backup_and_explicit_restore", BackupRestore);
        yield return ("storage.t03.duplicate_save_keeps_bytes_revision_backup", Duplicate);
        yield return ("storage.t03.cancel_after_flush_preserves_old", CancelAfterFlush);
        yield return ("storage.t03.interrupted_staging_preserves_old", InterruptAfterFlush);
        yield return ("storage.t03.injected_disk_full_preserves_old", DiskFull);
        yield return ("storage.t03.corrupt_record_not_overwritten", Corrupt);
        yield return ("storage.t03.future_record_not_overwritten", Future);
        yield return ("storage.t03.legacy_guest_read_then_backed_up_upgrade", Legacy);
        yield return ("storage.t03.explicit_guest_migration_preserves_source_and_conflicts", GuestMigration);
        yield return ("storage.t03.guest_import_interruption_retry_preserves_backup", GuestMigrationInterrupted);
        yield return ("storage.t03.format_migration_failure_then_success", FormatMigration);
        yield return ("storage.t03.account_switch_at_commit_rejects_old_write", StaleAtCommit);
        yield return ("storage.t03.session_guard_linearizes_before_switch", CommitGuard);
        yield return ("storage.t03.critical_operations_block_maintenance", Maintenance);
        yield return ("storage.t03.private_delete_only_changes_own_key", PrivateDelete);
        yield return ("storage.preferences.private_keys_are_independent", PreferenceIsolation);
        yield return ("storage.preferences.account_and_source_bindings", PreferenceBindings);
        yield return ("storage.preferences.legacy_migration_preserves_bytes_and_backup", PreferenceLegacy);
        yield return ("storage.preferences.corrupt_legacy_and_current_records_are_preserved", PreferenceCorruption);
        yield return ("storage.preferences.duplicate_keys_and_excessive_depth_are_rejected", PreferenceJsonValidation);
        yield return ("storage.preferences.migration_conflict_preserves_both_records", PreferenceMigrationConflict);
        yield return ("storage.preferences.interrupted_migration_retries_without_losing_source", PreferenceMigrationInterrupted);
        yield return ("storage.preferences.cancelled_write_and_stale_migration_preserve_bytes", PreferenceFailureGuards);
        yield return ("storage.preferences.file_and_aggregate_quotas_include_migration", PreferenceQuotas);
        yield return ("storage.preferences.logical_key_boundaries_and_device_names", PreferenceKeys);
        yield return ("storage.preferences.account_lock_serializes_storage_and_migration", PreferenceConcurrent);
        yield return ("storage.preferences.unsafe_paths_are_rejected", PreferenceUnsafePaths);
        yield return ("storage.t03.capability_owner_account_epoch_instance_bound", HandleOwnership);
        yield return ("storage.t03.capability_expiry_and_revocation", HandleExpiry);
        yield return ("storage.t03.capability_capacity_and_cleanup", HandleCapacity);
        yield return ("storage.t03.capability_size_and_readonly_pinning", HandleBounds);
        yield return ("storage.t03.export_is_one_shot_atomic_and_keeps_backup", ExportBackup);
        yield return ("storage.t03.export_rejects_changed_target_and_cross_access", ExportChanged);
        yield return ("storage.t03.export_cancel_preserves_original", ExportCancelled);
        yield return ("storage.t03.configuration_old_version_requires_migration", ConfigVersion);
        yield return ("storage.t03.configuration_migration_failure_preserves_old", ConfigMigrationFailure);
        yield return ("storage.t03.configuration_corrupt_recovery_keeps_damaged", ConfigRecovery);
        yield return ("storage.t03.configuration_future_schema_is_preserved", ConfigFuture);
        yield return ("storage.t03.save_recovery_inspection_validates_backup_without_main", InspectSaveRecovery);
        yield return ("storage.t03.config_recovery_inspection_validates_backup_without_main", InspectConfigurationRecovery);
    }
    private static StorageScope Scope(string account = "guest", string app = "test.storage", long source = 123, long epoch = 1) =>
        new(new(app, source, null), account, new(Guid.NewGuid()), new(epoch));
    private static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);
    private static void Assert(bool value, string description) { if (!value) throw new InvalidOperationException(description); }
    private static void InTemp(Action<InstallationRoot> action)
    {
        string directory = Path.Combine(Path.GetTempPath(), "AutumnOS-storage-t03-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try { var root = new InstallationRoot(directory); Assert(root.EnsureCreated().Success, "Temporary root failed."); action(root); }
        finally { Directory.Delete(directory, recursive: true); }
    }
    private static string SavePath(InstallationRoot root, StorageScope scope, string slot = "game") =>
        Path.Combine(root.Directories["Saves"], scope.Identity.AppId, scope.SourceKey, scope.AccountDirectory, slot + ".json");
    private static string PrivatePath(InstallationRoot root, StorageScope scope, string key) =>
        Path.Combine(root.Directories["AppData"], scope.Identity.AppId, scope.SourceKey, scope.AccountDirectory, key + ".bin");
    private static string PreferencePath(InstallationRoot root, StorageScope scope, string key) =>
        Path.Combine(root.Directories["AppData"], scope.Identity.AppId, scope.SourceKey, "Preferences", scope.AccountDirectory, "pref_" + key + ".json");
    private static void SeedLegacyPreference(InstallationRoot root, StorageScope scope, string key, byte[] bytes)
    {
        string path = PrivatePath(root, scope, "pref_" + key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }
    private static void PreferenceIsolation() => InTemp(root =>
    {
        StorageScope scope = Scope(); var store = new AccountDataStore(root, scope, _ => true);
        Assert(store.WritePreference("theme", Json("dark")).Success, "Preference write failed.");
        Assert(store.WritePrivate("theme", [1]).Success && store.WritePrivate("pref_theme", "not-json"u8.ToArray()).Success, "Independent private keys failed.");
        Assert(store.ReadPreference("theme").Value!.Value.GetString() == "dark", "Private write overwrote preference.");
        Assert(store.ReadPrivate("theme").Value!.SequenceEqual(new byte[] { 1 }) && store.ReadPrivate("pref_theme").Value!.SequenceEqual("not-json"u8.ToArray()), "Preference consumed private data.");
        Assert(store.DeletePrivate("pref_theme").Success && store.DeletePrivate("theme").Success, "Private delete failed.");
        Assert(store.ReadPreference("theme").Value!.Value.GetString() == "dark", "Private delete removed preference.");
        StorageScope freshScope = Scope("fresh"); var fresh = new AccountDataStore(root, freshScope, _ => true);
        Assert(fresh.WritePrivate("pref_theme", "\"storage\""u8.ToArray()).Success, "Fresh private write failed.");
        var restarted = new AccountDataStore(root, freshScope, _ => true);
        Assert(restarted.ReadPreference("theme") is { Success: true, Value: null }, "New private data was mistaken for a legacy preference after restart.");
        Assert(restarted.WritePreference("theme", Json("preference")).Success && restarted.ReadPrivate("pref_theme").Value!.SequenceEqual("\"storage\""u8.ToArray()), "Private-first ordering collided.");
    });
    private static void PreferenceBindings() => InTemp(root =>
    {
        var a = new AccountDataStore(root, Scope("account-A"), _ => true);
        Assert(a.WritePreference("theme", Json("dark")).Success, "Initial preference failed.");
        foreach (StorageScope scope in new[] { Scope("account-B"), Scope(), Scope("account-A", source: 999), Scope("account-A", app: "other.app") })
            Assert(new AccountDataStore(root, scope, _ => true).ReadPreference("theme") is { Success: true, Value: null }, "Preference crossed a host binding.");
    });
    private static void PreferenceLegacy() => InTemp(root =>
    {
        StorageScope scope = Scope(); byte[] old = "  \"旧主题 🌙\"  "u8.ToArray();
        SeedLegacyPreference(root, scope, "theme", old);
        SeedLegacyPreference(root, scope, "nullable", "null"u8.ToArray());
        string source = PrivatePath(root, scope, "pref_theme"); File.WriteAllText(source + ".bak", "old-backup");
        var store = new AccountDataStore(root, scope, _ => true);
        Assert(store.ReadPreference("theme").Value!.Value.GetString() == "旧主题 🌙", "Legacy preference was unreadable.");
        Assert(store.ReadPreference("nullable").Value!.Value.ValueKind == JsonValueKind.Null, "Stored JSON null became an absent preference.");
        Assert(File.ReadAllBytes(source).SequenceEqual(old) && File.ReadAllBytes(PreferencePath(root, scope, "theme")).SequenceEqual(old)
            && File.ReadAllText(source + ".bak") == "old-backup", "Migration did not preserve exact source and backup bytes.");
        Assert(store.WritePrivate("pref_theme", "new raw bytes"u8.ToArray()).Success && store.ReadPreference("theme").Value!.Value.GetString() == "旧主题 🌙", "Legacy source retained authority after migration.");
        Assert(store.WritePreference("theme", Json("light")).Success && File.ReadAllBytes(PreferencePath(root, scope, "theme") + ".bak").SequenceEqual(old), "Preference update lost its exact migrated backup.");
        Assert(store.ReadPrivate("pref_theme").Value!.SequenceEqual("new raw bytes"u8.ToArray()), "Preference update changed raw storage.");
    });
    private static void PreferenceCorruption() => InTemp(root =>
    {
        StorageScope scope = Scope(); byte[] broken = "not-json"u8.ToArray();
        SeedLegacyPreference(root, scope, "broken", broken); SeedLegacyPreference(root, scope, "good", "true"u8.ToArray());
        var store = new AccountDataStore(root, scope, _ => true);
        Assert(store.ReadPreference("broken").ErrorCode == "PREFERENCE_CORRUPT" && store.WritePreference("broken", Json(1)).ErrorCode == "PREFERENCE_CORRUPT", "Legacy corruption was not protected.");
        Assert(File.ReadAllBytes(PrivatePath(root, scope, "pref_broken")).SequenceEqual(broken)
            && File.ReadAllBytes(PreferencePath(root, scope, "broken")).SequenceEqual(broken), "Corrupt legacy bytes were lost.");
        Assert(store.ReadPreference("good").Value!.Value.GetBoolean() && store.WritePrivate("pref_other", [1]).Success, "Unrelated storage was blocked by corrupt preference.");
        Assert(store.WritePreference("current", Json(2)).Success, "Current preference setup failed.");
        string current = PreferencePath(root, scope, "current"); File.WriteAllBytes(current, [0xff, 0xfe]);
        Assert(store.ReadPreference("current").ErrorCode == "PREFERENCE_CORRUPT" && store.WritePreference("current", Json(3)).ErrorCode == "PREFERENCE_CORRUPT"
            && File.ReadAllBytes(current).SequenceEqual(new byte[] { 0xff, 0xfe }), "Damaged current preference was overwritten.");
    });
    private static void PreferenceMigrationConflict() => InTemp(root =>
    {
        StorageScope scope = Scope(); SeedLegacyPreference(root, scope, "theme", "\"legacy\""u8.ToArray());
        string target = PreferencePath(root, scope, "theme"); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.WriteAllText(target, "\"existing\"");
        var store = new AccountDataStore(root, scope, _ => true);
        Assert(store.ReadPreference("theme").ErrorCode == "PREFERENCE_MIGRATION_CONFLICT" && store.WritePrivate("pref_theme", [1]).ErrorCode == "PREFERENCE_MIGRATION_CONFLICT", "Conflicting migration silently chose a winner.");
        Assert(File.ReadAllText(target) == "\"existing\"" && File.ReadAllText(PrivatePath(root, scope, "pref_theme")) == "\"legacy\"", "Migration conflict lost either record.");
    });
    private static void PreferenceJsonValidation() => InTemp(root =>
    {
        int index = 0;
        foreach (string json in new[] { "{\"theme\":1,\"theme\":2}", "{\"nested\":[{\"x\":1,\"x\":2}]}" })
        {
            StorageScope scope = Scope("duplicate-" + index++); byte[] old = System.Text.Encoding.UTF8.GetBytes(json);
            SeedLegacyPreference(root, scope, "legacy", old);
            var store = new AccountDataStore(root, scope, _ => true);
            Assert(store.ReadPreference("legacy").ErrorCode == "PREFERENCE_CORRUPT" && store.WritePreference("legacy", Json(1)).ErrorCode == "PREFERENCE_CORRUPT", "Duplicate-key legacy preference was accepted or overwritten.");
            Assert(File.ReadAllBytes(PrivatePath(root, scope, "pref_legacy")).SequenceEqual(old) && File.ReadAllBytes(PreferencePath(root, scope, "legacy")).SequenceEqual(old), "Duplicate-key evidence was lost.");
            using JsonDocument duplicate = JsonDocument.Parse(json);
            Assert(store.WritePreference("new", duplicate.RootElement).ErrorCode == "INVALID_PARAMS" && !File.Exists(PreferencePath(root, scope, "new")), "Duplicate-key input was committed.");
        }
        var valid = new AccountDataStore(root, Scope("depth"), _ => true);
        Assert(valid.WritePreference("existing", Json(1)).Success, "Depth test setup failed.");
        using JsonDocument deep = JsonDocument.Parse(new string('[', 65) + "0" + new string(']', 65), new JsonDocumentOptions { MaxDepth = 128 });
        Assert(valid.WritePreference("existing", deep.RootElement).ErrorCode == "INVALID_PARAMS" && valid.ReadPreference("existing").Value!.Value.GetInt32() == 1, "Over-depth preference replaced readable data.");
    });
    private static void PreferenceMigrationInterrupted() => InTemp(root =>
    {
        StorageScope scope = Scope(); byte[] old = "\"legacy\""u8.ToArray(); SeedLegacyPreference(root, scope, "theme", old); int commits = 0;
        var interrupted = new AccountDataStore(root, scope, _ => true, testHooks: new(point =>
        { if (point == StorageWritePoint.BeforeCommit && ++commits == 2) throw new IOException("injected migration completion failure"); }));
        Assert(interrupted.ReadPreference("theme").ErrorCode == "STORAGE_IO_ERROR", "Interrupted migration appeared successful.");
        Assert(File.ReadAllBytes(PrivatePath(root, scope, "pref_theme")).SequenceEqual(old) && File.ReadAllBytes(PreferencePath(root, scope, "theme")).SequenceEqual(old), "Partial migration lost data.");
        string directory = Path.GetDirectoryName(PreferencePath(root, scope, "theme"))!;
        Assert(!File.Exists(Path.Combine(directory, ".legacy-migration-complete")) && !Directory.EnumerateFiles(directory, ".staging-*").Any(), "Unfinished migration was marked complete or leaked staging.");
        var retry = new AccountDataStore(root, scope, _ => true);
        Assert(retry.ReadPreference("theme").Value!.Value.GetString() == "legacy" && retry.WritePrivate("pref_theme", [1]).Success, "Interrupted migration was not safely retryable.");
        Assert(retry.ReadPreference("theme").Value!.Value.GetString() == "legacy", "Retry retained old namespace collision.");
    });
    private static void PreferenceFailureGuards() => InTemp(root =>
    {
        StorageScope scope = Scope(); var original = new AccountDataStore(root, scope, _ => true); Assert(original.WritePreference("theme", Json("old")).Success, "Initial preference failed.");
        byte[] before = File.ReadAllBytes(PreferencePath(root, scope, "theme")); using var cancellation = new CancellationTokenSource();
        var cancelled = new AccountDataStore(root, scope, _ => true, testHooks: new(point => { if (point == StorageWritePoint.AfterStagingFlush) cancellation.Cancel(); }));
        Assert(cancelled.WritePreference("theme", Json("new"), cancellation.Token).ErrorCode == "USER_CANCELLED"
            && File.ReadAllBytes(PreferencePath(root, scope, "theme")).SequenceEqual(before), "Cancelled preference replaced committed bytes.");
        StorageScope legacyScope = Scope("legacy"); SeedLegacyPreference(root, legacyScope, "theme", "1"u8.ToArray()); long epoch = legacyScope.Epoch.Value;
        var stale = new AccountDataStore(root, legacyScope, s => s.Epoch.Value == epoch, testHooks: new(point => { if (point == StorageWritePoint.BeforeCommit) epoch++; }));
        Assert(stale.ReadPreference("theme").ErrorCode == "SESSION_STALE" && !File.Exists(PreferencePath(root, legacyScope, "theme"))
            && File.ReadAllText(PrivatePath(root, legacyScope, "pref_theme")) == "1", "Migration crossed an account epoch.");
    });
    private static void PreferenceQuotas() => InTemp(root =>
    {
        var store = new AccountDataStore(root, Scope(), _ => true, limits: new(MaximumPrivateFiles: 1, MaximumPrivateFileBytes: 4));
        Assert(store.WritePreference("one", Json(1)).Success && store.WritePreference("two", Json(2)).ErrorCode == "STORAGE_QUOTA_EXCEEDED", "Preference file quota missing.");
        Assert(store.WritePrivate("raw", [1]).Success && store.WritePrivate("second", [2]).ErrorCode == "STORAGE_QUOTA_EXCEEDED", "Private file quota changed.");
        Assert(store.WritePreference("one", Json("long")).ErrorCode == "FILE_TOO_LARGE", "Preference byte quota missing.");
        StorageScope smallScope = Scope("small"); var small = new AccountDataStore(root, smallScope, _ => true, limits: new(MaximumAccountBytes: 7));
        Assert(small.WritePreference("one", Json("ab")).Success && small.WritePrivate("raw", [1, 2, 3]).ErrorCode == "STORAGE_QUOTA_EXCEEDED", "Aggregate quota omitted preferences.");
        Assert(small.WritePreference("one", Json("cd")).ErrorCode == "STORAGE_QUOTA_EXCEEDED" && small.ReadPreference("one").Value!.Value.GetString() == "ab", "Staging and backup headroom was unaccounted.");
        StorageScope legacyScope = Scope("legacy-quota"); SeedLegacyPreference(root, legacyScope, "one", "1234"u8.ToArray());
        var legacy = new AccountDataStore(root, legacyScope, _ => true, limits: new(MaximumAccountBytes: 7));
        Assert(legacy.ReadPreference("one").ErrorCode == "STORAGE_QUOTA_EXCEEDED" && File.ReadAllText(PrivatePath(root, legacyScope, "pref_one")) == "1234"
            && !File.Exists(PreferencePath(root, legacyScope, "one")), "Migration exceeded quota or lost the source.");
    });
    private static void PreferenceKeys() => InTemp(root =>
    {
        var store = new AccountDataStore(root, Scope(), _ => true);
        Assert(store.WritePreference(new string('k', 59), Json(true)).Success, "59-character preference key was rejected.");
        foreach (string key in new[] { new string('k', 60), new string('k', 64), "", "a/b", "..", "主题", "a\n", "a:stream" })
            Assert(store.ReadPreference(key).ErrorCode == "INVALID_PARAMS" && store.WritePreference(key, Json(1)).ErrorCode == "INVALID_PARAMS", "Invalid logical preference key accepted.");
        foreach (string key in new[] { "CON", "prn", "AUX", "nul", "COM1", "LPT9" })
            Assert(store.WritePreference(key, Json(key)).Success && store.ReadPreference(key).Value!.Value.GetString() == key
                && store.WritePrivate(key, [1]).ErrorCode == "INVALID_PARAMS", "Safe encoded preference/device-name contract changed.");
    });
    private static void PreferenceConcurrent() => InTemp(root =>
    {
        StorageScope scope = Scope(); SeedLegacyPreference(root, scope, "theme", "\"old\""u8.ToArray());
        using var entered = new ManualResetEventSlim(); using var resume = new ManualResetEventSlim();
        var migrating = new AccountDataStore(root, scope, _ => true, testHooks: new(point =>
        {
            if (point != StorageWritePoint.AfterStagingFlush) return;
            entered.Set(); if (!resume.Wait(TimeSpan.FromSeconds(10))) throw new IOException("Concurrent test timeout.");
        }));
        Task<DataResult<JsonElement?>> reading = Task.Run(() => migrating.ReadPreference("theme"));
        var writer = new AccountDataStore(root, scope, _ => true);
        try
        {
            Assert(entered.Wait(TimeSpan.FromSeconds(10)), "Migration did not enter its critical section.");
            Assert(!writer.WritePrivate("pref_theme", "\"new\""u8.ToArray()).Success, "Private writer bypassed migration's account lock.");
        }
        finally { resume.Set(); }
        Assert(reading.GetAwaiter().GetResult().Value!.Value.GetString() == "old", "Concurrent migration lost old preference.");
        Assert(writer.WritePrivate("pref_theme", "\"new\""u8.ToArray()).Success && writer.ReadPreference("theme").Value!.Value.GetString() == "old", "Retried private writer crossed namespaces.");
    });
    private static void PreferenceUnsafePaths() => InTemp(root =>
    {
        StorageScope scope = Scope(); var store = new AccountDataStore(root, scope, _ => true); Assert(store.WritePreference("theme", Json(1)).Success, "Preference setup failed.");
        string path = PreferencePath(root, scope, "theme"); File.Delete(path); Directory.CreateDirectory(path);
        Assert(store.ReadPreference("theme").ErrorCode == "STORAGE_UNSAFE_PATH" && store.WritePreference("theme", Json(2)).ErrorCode == "STORAGE_UNSAFE_PATH", "Directory masqueraded as preference data.");
        if (!OperatingSystem.IsWindows())
        {
            Directory.Delete(path); string outside = Path.Combine(root.InstallationDirectory, "external.json"); File.WriteAllText(outside, "3"); File.CreateSymbolicLink(path, outside);
            Assert(store.ReadPreference("theme").ErrorCode == "STORAGE_UNSAFE_PATH" && store.WritePreference("theme", Json(4)).ErrorCode == "STORAGE_UNSAFE_PATH"
                && File.ReadAllText(outside) == "3", "Preference symlink escaped the managed directory.");
        }
    });
    private static void Isolated() => InTemp(root =>
    {
        var a = new AccountDataStore(root, Scope("verified-issuer:subject-A"), _ => true);
        var b = new AccountDataStore(root, Scope("verified-issuer:subject-B"), _ => true);
        var guest = new AccountDataStore(root, Scope(), _ => true);
        var otherSource = new AccountDataStore(root, Scope("verified-issuer:subject-A", source: 999), _ => true);
        Assert(a.WriteSave("game", Json(new { score = 8 })).Success, "Save A.");
        foreach (var other in new[] { b, guest, otherSource }) Assert(other.ReadSave("game").Value is null, "Another identity saw A.");
        Assert(a.WritePrivate("notes", [7]).Success && b.ReadPrivate("notes").Value is null, "Private files cross account.");
        Assert(a.ReadSave("game").Value!.Value.GetProperty("score").GetInt32() == 8, "Original changed.");
    });
    private static void Quotas() => InTemp(root =>
    {
        var store = new AccountDataStore(root, Scope(), _ => true, limits: new(MaximumSlots: 1, MaximumPrivateFiles: 1, MaximumPrivateFileBytes: 4));
        Assert(store.WriteSave("one", Json(1)).Success, "First slot.");
        Assert(store.WriteSave("two", Json(2)).ErrorCode == "STORAGE_QUOTA_EXCEEDED", "Slot capacity.");
        Assert(store.WritePrivate("one", [1, 2]).Success, "First private.");
        Assert(store.WritePrivate("two", [2]).ErrorCode == "STORAGE_QUOTA_EXCEEDED", "File capacity.");
        Assert(store.WritePrivate("one", new byte[5]).ErrorCode == "FILE_TOO_LARGE", "File size.");
        var small = new AccountDataStore(root, Scope("small"), _ => true, limits: new(MaximumAccountBytes: 5));
        Assert(small.WritePrivate("file", new byte[6]).ErrorCode == "STORAGE_QUOTA_EXCEEDED", "Aggregate capacity.");
    });
    private static void UnsafeKeys() => InTemp(root =>
    {
        var store = new AccountDataStore(root, Scope(), _ => true);
        foreach (string key in new[] { "../other", "a/b", "a\\b", "C:\\secret", "a:stream", ".", "con", "LPT1", "a.", "" })
            Assert(store.WriteSave(key, Json(1)).ErrorCode == "INVALID_PARAMS" && store.WritePrivate(key, [1]).ErrorCode == "INVALID_PARAMS", "Unsafe key accepted.");
    });
    private static void BackupRestore() => InTemp(root =>
    {
        StorageScope scope = Scope(); var store = new AccountDataStore(root, scope, _ => true);
        Assert(store.WriteSave("game", Json(1)).Success && store.WriteSave("game", Json(2)).Success, "Writes failed.");
        string path = SavePath(root, scope); byte[] second = File.ReadAllBytes(path);
        Assert(store.ListSlots().Value![0].HasBackup, "Backup absent.");
        Assert(store.RestoreSave("game").Success && store.ReadSave("game").Value!.Value.GetInt32() == 1, "Restore failed.");
        Assert(File.ReadAllBytes(path + ".before-restore").SequenceEqual(second), "Pre-restore bytes lost.");
        Assert(store.RestoreSave("game").ErrorCode == "SAVE_RECOVERY_CONFLICT", "Recovery must not overwrite old forensic bytes.");
    });
    private static void Duplicate() => InTemp(root =>
    {
        StorageScope scope = Scope(); var store = new AccountDataStore(root, scope, _ => true);
        Assert(store.WriteSave("game", Json(1)).Success && store.WriteSave("game", Json(2)).Success, "Writes.");
        string path = SavePath(root, scope); byte[] old = File.ReadAllBytes(path), backup = File.ReadAllBytes(path + ".bak");
        for (int i = 0; i < 10; i++) Assert(store.WriteSave("game", Json(2)).Success, "Duplicate.");
        Assert(File.ReadAllBytes(path).SequenceEqual(old) && File.ReadAllBytes(path + ".bak").SequenceEqual(backup), "Duplicate advanced record.");
    });
    private static void CancelAfterFlush() => FaultPreserves((point, token) => { if (point == StorageWritePoint.AfterStagingFlush) token.Cancel(); }, "USER_CANCELLED");
    private static void InterruptAfterFlush() => FaultPreserves((point, _) => { if (point == StorageWritePoint.AfterStagingFlush) throw new IOException("injected interrupted write"); }, "STORAGE_IO_ERROR");
    private static void DiskFull() => FaultPreserves((point, _) => { if (point == StorageWritePoint.BeforeWrite) throw new FullDiskException(); }, "STORAGE_DISK_FULL");
    private sealed class FullDiskException : IOException { internal FullDiskException() { HResult = unchecked((int)0x80070070); } }
    private static void FaultPreserves(Action<StorageWritePoint, CancellationTokenSource> action, string expected) => InTemp(root =>
    {
        StorageScope scope = Scope(); var original = new AccountDataStore(root, scope, _ => true);
        Assert(original.WriteSave("game", Json(11)).Success, "Initial write.");
        string path = SavePath(root, scope); byte[] before = File.ReadAllBytes(path);
        using var cancelled = new CancellationTokenSource();
        var broken = new AccountDataStore(root, scope, _ => true, testHooks: new(point => action(point, cancelled)));
        Assert(broken.WriteSave("game", Json(12), cancellationToken: cancelled.Token).ErrorCode == expected, "Failure not surfaced.");
        Assert(File.ReadAllBytes(path).SequenceEqual(before), "Committed bytes changed.");
        Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, ".staging-*").Any(), "Call-owned staging leaked.");
    });
    private static void Corrupt() => InTemp(root =>
    {
        StorageScope scope = Scope(); var store = new AccountDataStore(root, scope, _ => true);
        Assert(store.WriteSave("game", Json(1)).Success && store.WriteSave("game", Json(2)).Success, "Writes.");
        string path = SavePath(root, scope); File.WriteAllText(path, "{broken");
        Assert(store.ReadSave("game").ErrorCode == "SAVE_CORRUPT" && store.WriteSave("game", Json(3)).ErrorCode == "SAVE_CORRUPT", "Corruption not protected.");
        Assert(File.ReadAllText(path) == "{broken", "Corruption erased.");
        Assert(store.RestoreSave("game").Success && store.ReadSave("game").Value!.Value.GetInt32() == 1, "Valid backup not restorable.");
    });
    private static void Future() => InTemp(root =>
    {
        StorageScope scope = Scope(); var store = new AccountDataStore(root, scope, _ => true);
        Assert(store.WriteSave("game", Json(1)).Success, "Initial.");
        string path = SavePath(root, scope); File.WriteAllText(path, "{\"schemaVersion\":999}");
        Assert(store.WriteSave("game", Json(2)).ErrorCode == "SAVE_FUTURE_SCHEMA", "Future schema overwritten.");
        Assert(File.ReadAllText(path) == "{\"schemaVersion\":999}", "Future bytes changed.");
    });
    private static void Legacy() => InTemp(root =>
    {
        StorageScope scope = Scope(); string path = Path.Combine(root.Directories["Saves"], scope.Identity.AppId, "guest", "game.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] old = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, appId = scope.Identity.AppId, accountMode = "guest", slot = "game", value = new { score = 7 } });
        File.WriteAllBytes(path, old);
        var store = new AccountDataStore(root, scope, _ => true, allowLegacyGuest: true);
        Assert(store.ReadSave("game").Value!.Value.GetProperty("score").GetInt32() == 7, "Legacy unreadable.");
        Assert(File.ReadAllBytes(path).SequenceEqual(old), "Read migrated silently.");
        Assert(store.WriteSave("game", Json(new { score = 8 })).Success, "Legacy write.");
        Assert(File.ReadAllBytes(path + ".bak").SequenceEqual(old), "Legacy backup not exact.");
        Assert(store.RestoreSave("game").Success && store.ReadSave("game").Value!.Value.GetProperty("score").GetInt32() == 7, "Legacy recovery.");
    });
    private static void GuestMigration() => InTemp(root =>
    {
        StorageScope guestScope = Scope(); var guest = new AccountDataStore(root, guestScope, _ => true);
        Assert(guest.WriteSave("game", Json(88)).Success, "Guest write."); byte[] source = File.ReadAllBytes(SavePath(root, guestScope));
        StorageScope aScope = Scope("account-A"); var account = new AccountDataStore(root, aScope, _ => true);
        Assert(account.ReadSave("game").Value is null, "Silent guest binding.");
        Assert(account.MigrateGuestSave("game", false).ErrorCode == "USER_CANCELLED", "Confirmation ignored.");
        Assert(account.MigrateGuestSave("game", true).Success && account.ReadSave("game").Value!.Value.GetInt32() == 88, "Import failed.");
        Assert(File.ReadAllBytes(SavePath(root, guestScope)).SequenceEqual(source), "Source changed.");
        Assert(File.Exists(SavePath(root, aScope) + ".import-backup"), "Import backup absent.");
        Assert(account.MigrateGuestSave("game", true).ErrorCode == "SAVE_MIGRATION_CONFLICT", "Target overwritten.");
    });
    private static void GuestMigrationInterrupted() => InTemp(root =>
    {
        StorageScope guestScope = Scope(); var guest = new AccountDataStore(root, guestScope, _ => true);
        Assert(guest.WriteSave("game", Json(42)).Success, "Guest write.");
        StorageScope aScope = Scope("account-A"); int commits = 0;
        var broken = new AccountDataStore(root, aScope, _ => true, testHooks: new(point => { if (point == StorageWritePoint.BeforeCommit && ++commits == 2) throw new IOException("injected target interruption"); }));
        Assert(!broken.MigrateGuestSave("game", true).Success && !File.Exists(SavePath(root, aScope)), "Failed migration became committed.");
        var good = new AccountDataStore(root, aScope, _ => true);
        Assert(good.MigrateGuestSave("game", true).Success && guest.ReadSave("game").Value!.Value.GetInt32() == 42, "Retriable import lost source.");
    });
    private static void FormatMigration() => InTemp(root =>
    {
        StorageScope scope = Scope(); var store = new AccountDataStore(root, scope, _ => true); Assert(store.WriteSave("game", Json(3)).Success, "Write.");
        byte[] before = File.ReadAllBytes(SavePath(root, scope));
        Assert(store.MigrateSaveFormat("game", 1, 2, _ => throw new InvalidOperationException()).ErrorCode == "SAVE_MIGRATION_FAILED", "Failure missing.");
        Assert(File.ReadAllBytes(SavePath(root, scope)).SequenceEqual(before), "Migration corrupted old.");
        Assert(store.MigrateSaveFormat("game", 1, 2, value => Json(new { score = value.GetInt32() })).Success, "Valid migration.");
        Assert(store.ListSlots().Value![0].FormatVersion == 2 && File.ReadAllBytes(SavePath(root, scope) + ".bak").SequenceEqual(before), "Migration evidence.");
    });
    private static void StaleAtCommit() => InTemp(root =>
    {
        StorageScope scope = Scope(); long epoch = 1;
        var store = new AccountDataStore(root, scope, s => s.Epoch.Value == epoch, testHooks: new(point => { if (point == StorageWritePoint.BeforeCommit) epoch++; }));
        Assert(store.WriteSave("game", Json(1)).ErrorCode == "SESSION_STALE" && !File.Exists(SavePath(root, scope)), "Late write crossed epoch.");
        Assert(store.ReadSave("game").ErrorCode == "SESSION_STALE", "Late read returned data.");
    });
    private static void CommitGuard() => InTemp(root =>
    {
        StorageScope scope = Scope(); bool leased = false; int checks = 0;
        var store = new AccountDataStore(root, scope, _ => { checks++; return true; },
            commitLease: () => { Assert(!leased, "Nested lease."); leased = true; return new TestLease(() => leased = false); },
            testHooks: new(point => { if (point == StorageWritePoint.BeforeCommit) Assert(leased, "Commit has no session lock."); }));
        Assert(store.WriteSave("game", Json(1)).Success && checks >= 3 && !leased, "Guard missing or leaked.");
    });
    private sealed class TestLease(Action release) : IDisposable { public void Dispose() => release(); }
    private static void Maintenance() => InTemp(root =>
    {
        var coordinator = new CriticalOperationCoordinator();
        using (coordinator.EnterWrite()) Assert(coordinator.ActiveWrites == 1 && coordinator.TryEnterMaintenance() is null, "Maintenance raced write.");
        using (coordinator.TryEnterMaintenance())
        {
            var store = new AccountDataStore(root, Scope(), _ => true, coordinator: coordinator);
            Assert(store.WriteSave("game", Json(1)).ErrorCode == "MAINTENANCE_IN_PROGRESS", "Write entered maintenance.");
        }
        Assert(coordinator.ActiveWrites == 0 && !coordinator.IsMaintenance, "Leases leaked.");
    });
    private static void PrivateDelete() => InTemp(root =>
    {
        var store = new AccountDataStore(root, Scope(), _ => true);
        Assert(store.WritePrivate("one", [1]).Success && store.WritePrivate("two", [2]).Success && store.WriteSave("game", Json(4)).Success, "Setup.");
        Assert(store.DeletePrivate("one").Value && store.ReadPrivate("one").Value is null && store.ReadPrivate("two").Value!.SequenceEqual(new byte[] { 2 })
            && store.ReadSave("game").Value!.Value.GetInt32() == 4, "Delete crossed data kinds.");
    });
    private static void HandleOwnership() => InTemp(root =>
    {
        string file = Path.Combine(root.InstallationDirectory, "picked.txt"); File.WriteAllText(file, "picked");
        StorageScope scope = Scope("A"); using var broker = new FileCapabilityBroker(_ => true);
        FileCapability cap = broker.RegisterPickedFile(file, scope).Value!;
        Assert(cap.Name == "picked.txt" && cap.Handle.Length == 64 && broker.Read(cap.Handle, scope).Success, "Valid handle failed.");
        foreach (StorageScope other in new[] { scope with { AccountKey = "B" }, scope with { Epoch = new(2) }, scope with { InstanceId = new(Guid.NewGuid()) }, Scope("A", app: "other.app") })
            Assert(broker.Read(cap.Handle, other).ErrorCode == "FILE_HANDLE_WRONG_OWNER", "Cross-scope handle access.");
        Assert(broker.Read(file, scope).ErrorCode == "FILE_HANDLE_INVALID", "Path masqueraded as handle.");
        Assert(broker.Close(cap.Handle, scope).Success && broker.Read(cap.Handle, scope).ErrorCode == "FILE_HANDLE_INVALID", "Close not effective.");
    });
    private sealed class FakeTime : TimeProvider { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private static void HandleExpiry() => InTemp(root =>
    {
        string file = Path.Combine(root.InstallationDirectory, "picked.txt"); File.WriteAllText(file, "picked");
        StorageScope scope = Scope(); var time = new FakeTime(); bool allowed = true;
        using var broker = new FileCapabilityBroker(_ => allowed, time);
        var cap = broker.RegisterPickedFile(file, scope, TimeSpan.FromSeconds(1)).Value!; time.Now += TimeSpan.FromSeconds(2);
        Assert(broker.Read(cap.Handle, scope).ErrorCode == "FILE_HANDLE_EXPIRED", "Expired handle accepted.");
        cap = broker.RegisterPickedFile(file, scope).Value!; allowed = false;
        Assert(broker.Read(cap.Handle, scope).ErrorCode == "PERMISSION_REVOKED", "Revoked read accepted.");
        allowed = true; cap = broker.RegisterPickedFile(file, scope).Value!; broker.RevokeInstance(scope.InstanceId);
        Assert(broker.Read(cap.Handle, scope).ErrorCode == "FILE_HANDLE_INVALID", "Instance close retained handle.");
        cap = broker.RegisterPickedFile(file, scope, TimeSpan.FromSeconds(1)).Value!; time.Now += TimeSpan.FromSeconds(2);
        Assert(broker.Close(cap.Handle, scope).ErrorCode == "FILE_HANDLE_EXPIRED", "Close accepted expired capability.");
        cap = broker.RegisterPickedFile(file, scope).Value!; allowed = false;
        Assert(broker.Close(cap.Handle, scope).ErrorCode == "PERMISSION_REVOKED", "Close ignored permission revocation.");
    });
    private static void HandleCapacity() => InTemp(root =>
    {
        string file = Path.Combine(root.InstallationDirectory, "picked.txt"); File.WriteAllText(file, "picked"); StorageScope scope = Scope();
        using var broker = new FileCapabilityBroker(_ => true);
        for (int i = 0; i < FileCapabilityBroker.MaximumHandles; i++) Assert(broker.RegisterPickedFile(file, scope).Success, "Early limit.");
        Assert(broker.RegisterPickedFile(file, scope).ErrorCode == "FILE_HANDLE_LIMIT", "Unbounded handles.");
        broker.RevokeAll(); Assert(broker.RegisterPickedFile(file, scope).Success, "Cleanup did not reclaim capacity.");
    });
    private static void HandleBounds() => InTemp(root =>
    {
        string file = Path.Combine(root.InstallationDirectory, "large.bin"); using (var stream = File.Create(file)) stream.SetLength(FileCapabilityBroker.MaximumFileBytes + 1L);
        StorageScope scope = Scope(); using var broker = new FileCapabilityBroker(_ => true);
        Assert(broker.RegisterPickedFile(file, scope).ErrorCode == "FILE_TOO_LARGE", "Oversized file.");
        Assert(broker.RegisterPickedFile("relative.txt", scope).ErrorCode == "INVALID_PARAMS", "Relative host path.");
        File.WriteAllText(file, "retained"); FileCapability cap = broker.RegisterPickedFile(file, scope).Value!;
        if (OperatingSystem.IsWindows())
        {
            try { File.WriteAllText(file, "swapped"); throw new InvalidOperationException("Selected read handle allowed replacement."); }
            catch (IOException) { }
        }
        Assert(System.Text.Encoding.UTF8.GetString(broker.Read(cap.Handle, scope).Value!) == "retained", "Pinned data changed.");
    });
    private static void ConfigVersion() => InTemp(root =>
    {
        var old = new VersionedConfigurationStore(root, "host-options", 1); Assert(old.Save(Json(new { theme = "dark" })).Success, "Old config.");
        var newer = new VersionedConfigurationStore(root, "host-options", 2);
        Assert(newer.Load().Value!.SchemaVersion == 1 && newer.Save(Json(new { theme = "light" })).ErrorCode == "CONFIG_MIGRATION_REQUIRED", "Old config silently replaced.");
        Assert(newer.Migrate(1, value => Json(new { theme = value.GetProperty("theme").GetString(), reducedMotion = false })).Success, "Migration.");
        Assert(newer.Load().Value!.Values.GetProperty("theme").GetString() == "dark" && File.Exists(newer.FilePath + ".bak"), "User choice lost.");
    });
    private static void ExportBackup() => InTemp(root =>
    {
        string file = Path.Combine(root.InstallationDirectory, "export.txt"); File.WriteAllText(file, "original");
        StorageScope scope = Scope(); using var broker = new FileCapabilityBroker(_ => true);
        FileCapability capability = broker.RegisterPickedSaveFile(file, scope).Value!;
        Assert(File.ReadAllText(file) == "original", "Picker registration truncated file.");
        DataResult<FileExportResult> result = broker.Write(capability.Handle, scope, System.Text.Encoding.UTF8.GetBytes("new"));
        Assert(result.Success && result.Value!.BackupRetained && File.ReadAllText(file) == "new", "Atomic export failed.");
        string backup = Directory.EnumerateFiles(root.InstallationDirectory, "export.txt.AutumnOS-backup-*").Single();
        Assert(File.ReadAllText(backup) == "original" && broker.Write(capability.Handle, scope, [0]).ErrorCode == "FILE_HANDLE_INVALID", "Backup/one-shot constraint.");
        string newFile = Path.Combine(root.InstallationDirectory, "new-export.txt");
        capability = broker.RegisterPickedSaveFile(newFile, scope).Value!;
        Assert(!File.Exists(newFile) && broker.Write(capability.Handle, scope, [1]).Success && File.ReadAllBytes(newFile).SequenceEqual(new byte[] { 1 }), "New export.");
    });
    private static void ExportChanged() => InTemp(root =>
    {
        string file = Path.Combine(root.InstallationDirectory, "export.txt"); File.WriteAllText(file, "original");
        StorageScope scope = Scope(); using var broker = new FileCapabilityBroker(_ => true);
        var write = broker.RegisterPickedSaveFile(file, scope).Value!;
        Assert(broker.Read(write.Handle, scope).ErrorCode == "FILE_HANDLE_ACCESS_DENIED", "Write capability became read grant.");
        Assert(broker.Write(write.Handle, scope with { AccountKey = "other" }, [1]).ErrorCode == "FILE_HANDLE_WRONG_OWNER", "Cross-account export.");
        string replacement = Path.Combine(root.InstallationDirectory, "replacement.txt"); File.WriteAllText(replacement, "changed");
        File.Replace(replacement, file, destinationBackupFileName: null);
        Assert(broker.Write(write.Handle, scope, [1]).ErrorCode == "FILE_CHANGED_SINCE_PICK" && File.ReadAllText(file) == "changed", "Changed file overwritten.");
        broker.Close(write.Handle, scope);
        var read = broker.RegisterPickedFile(file, scope).Value!;
        Assert(broker.Write(read.Handle, scope, [1]).ErrorCode == "FILE_HANDLE_ACCESS_DENIED", "Read handle upgraded to write.");
    });
    private static void ExportCancelled() => InTemp(root =>
    {
        string file = Path.Combine(root.InstallationDirectory, "export.txt"); File.WriteAllText(file, "original");
        StorageScope scope = Scope(); using var broker = new FileCapabilityBroker(_ => true);
        var capability = broker.RegisterPickedSaveFile(file, scope).Value!;
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert(broker.Write(capability.Handle, scope, [1], cancellation.Token).ErrorCode == "USER_CANCELLED" && File.ReadAllText(file) == "original", "Cancelled export changed file.");
        Assert(!Directory.EnumerateFiles(root.InstallationDirectory, ".AutumnOS-export-*").Any(), "Staging leaked.");
    });
    private static void ConfigMigrationFailure() => InTemp(root =>
    {
        var old = new VersionedConfigurationStore(root, "host-options", 1); Assert(old.Save(Json(new { x = 3 })).Success, "Old config."); byte[] bytes = File.ReadAllBytes(old.FilePath);
        var newer = new VersionedConfigurationStore(root, "host-options", 2, testHooks: new(point => { if (point == StorageWritePoint.AfterStagingFlush) throw new IOException("injected migration interruption"); }));
        Assert(!newer.Migrate(1, value => value).Success && File.ReadAllBytes(old.FilePath).SequenceEqual(bytes), "Migration replaced old on failure.");
    });
    private static void ConfigRecovery() => InTemp(root =>
    {
        var store = new VersionedConfigurationStore(root, "host-options", 1); Assert(store.Save(Json(new { x = 1 })).Success && store.Save(Json(new { x = 2 })).Success, "Config setup.");
        File.WriteAllText(store.FilePath, "corrupt"); Assert(store.Save(Json(new { x = 3 })).ErrorCode == "CONFIG_CORRUPT", "Overwrote corrupt config.");
        Assert(store.RestoreBackup(false).ErrorCode == "USER_CANCELLED", "Restore confirmation bypassed.");
        Assert(store.RestoreBackup(true).Success && store.Load().Value!.Values.GetProperty("x").GetInt32() == 1 && File.ReadAllText(store.FilePath + ".before-restore") == "corrupt", "Recovery lost data.");
    });
    private static void ConfigFuture() => InTemp(root =>
    {
        var future = new VersionedConfigurationStore(root, "host-options", 9); Assert(future.Save(Json(new { x = 1 })).Success, "Future setup."); byte[] bytes = File.ReadAllBytes(future.FilePath);
        var older = new VersionedConfigurationStore(root, "host-options", 1);
        Assert(older.Load().ErrorCode == "CONFIG_FUTURE_SCHEMA" && older.Save(Json(new { x = 2 })).ErrorCode == "CONFIG_FUTURE_SCHEMA"
            && File.ReadAllBytes(future.FilePath).SequenceEqual(bytes), "Future overwritten.");
    });
    private static void InspectSaveRecovery() => InTemp(root =>
    {
        StorageScope scope = Scope(); var store = new AccountDataStore(root, scope, _ => true);
        Assert(store.InspectSaveRecovery("game").Value is { MainExists: false, BackupExists: false, BackupValid: false }, "Missing backup was offered.");
        Assert(store.WriteSave("game", Json(1)).Success && store.WriteSave("game", Json(2)).Success, "Setup failed.");
        string path = SavePath(root, scope); File.WriteAllText(path, "corrupt-main");
        var result = store.InspectSaveRecovery("game");
        Assert(result.Value is { MainExists: true, BackupExists: true, BackupValid: true } && File.ReadAllText(path) == "corrupt-main", "Main corruption hid valid backup or changed data.");
        File.WriteAllText(path + ".bak", "corrupt-backup");
        Assert(store.InspectSaveRecovery("game").Value is { BackupValid: false, BackupError: "SAVE_CORRUPT" }, "Corrupt backup offered as restorable.");
    });
    private static void InspectConfigurationRecovery() => InTemp(root =>
    {
        var store = new VersionedConfigurationStore(root, "host-options", 1);
        Assert(store.InspectBackup().Value is { MainExists: false, BackupExists: false, BackupValid: false }, "Missing config backup offered.");
        Assert(store.Save(Json(new { x = 1 })).Success && store.Save(Json(new { x = 2 })).Success, "Setup failed.");
        File.WriteAllText(store.FilePath, "corrupt-main");
        Assert(store.InspectBackup().Value is { MainExists: true, BackupValid: true, BackupSchemaVersion: 1 } && File.ReadAllText(store.FilePath) == "corrupt-main", "Inspection changed damaged main.");
        File.WriteAllText(store.FilePath + ".bak", "{\"schemaVersion\":9,\"revision\":1,\"values\":{}}");
        Assert(store.InspectBackup().Value is { BackupValid: false, BackupError: "CONFIG_FUTURE_SCHEMA" }, "Future backup offered as valid.");
    });
}
