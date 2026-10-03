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
