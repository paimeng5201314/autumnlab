using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.Packages;

namespace AutumnOS.Tests;

internal static class StoreInstallTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("store_install.confirmation_and_single_post_commit_event", Confirmation);
        yield return ("store_install.release_all_fields_must_match_real_package", ExpectedIdentity);
        yield return ("store_install.repository_id_continuity_survives_rename", Sources);
        yield return ("store_install.local_source_cannot_take_over_github", LocalSource);
        yield return ("store_install.historical_version_bytes_are_immutable", HistoricalVersion);
        yield return ("store_install.pin_and_preview_are_persistent_app_policies", VersionPolicy);
        yield return ("store_install.foreground_background_starting_leases_block_mutation", RuntimeLease);
        yield return ("store_install.launch_waits_for_commit_not_stale_resource", LaunchRace);
        yield return ("store_install.repair_uses_current_exact_archive_and_retains_old_content", Repair);
        yield return ("store_install.uninstall_retains_saves_and_source_tombstone", Uninstall);
        yield return ("store_install.interrupted_update_keeps_previous_registry", InterruptedUpdate);
        yield return ("store_install.interrupted_first_install_has_no_ghost_icon", InterruptedFirst);
        yield return ("store_install.disk_full_keeps_previous_registry_bytes", DiskFull);
        yield return ("store_install.commit_survives_missing_receipt", AfterCommit);
        yield return ("store_install.bundle_adoption_does_not_override_selection_or_uninstall", Bundle);
        yield return ("store_install.downgrade_checks_all_accounts_and_backups_exact_bytes", Downgrade);
        yield return ("store_install.downgrade_incompatible_or_corrupt_save_is_blocked", BadSave);
        yield return ("store_install.absent_save_guard_fails_closed", NoSaveGuard);
        yield return ("store_install.future_save_compatibility_is_explicit", SaveManifest);
        yield return ("store_install.cancelled_input_never_registers", Cancel);
        yield return ("store_install.registry_duplicate_fields_are_rejected", CorruptRegistry);
        yield return ("store_install.provenance_saved_without_claiming_signature_verification", Provenance);
        yield return ("store_install.failed_backup_does_not_commit_downgrade", FailedBackup);
        yield return ("store_install.semver_numeric_preview_order", Versions);
    }
    private static void Confirmation() => InTemp(root =>
    {
        var service = Service(root); string package = Package(root); int changes = 0;
        service.Changed += change => { Assert(service.Find(change.AppId) is not null, "event fired before registration"); changes++; };
        Reject(() => service.Install(package), "PACKAGE_SOURCE_CONFIRMATION_REQUIRED");
        var first = service.Install(package, sourceConfirmed: true);
        var second = service.Install(package, sourceConfirmed: true);
        Assert(first.Package.DirectoryPath == second.Package.DirectoryPath && service.GetInstalled().Length == 1 && changes == 1, "install must be idempotent");
        Assert(File.Exists(Path.Combine(first.Package.DirectoryPath, "index.html")), "actual resource missing");
    });
    private static void ExpectedIdentity() => InTemp(root =>
    {
        var service = Service(root); string path = Package(root); var expected = Expected(path);
        foreach (var bad in new[] { expected with { AppId = "other.game" }, expected with { Version = "2.0.0" },
            expected with { Runtime = "wasm" }, expected with { Entry = "wrong.html" }, expected with { Permissions = [] },
            expected with { SaveFormatVersion = 2 } })
            Reject(() => service.Install(path, bad, sourceConfirmed: true), "PACKAGE_RELEASE_MISMATCH");
        Reject(() => service.Install(path, expected with { Sha256 = new string('0', 64) }, sourceConfirmed: true), "PACKAGE_HASH_MISMATCH");
        Reject(() => service.Install(path, expected with { Bytes = expected.Bytes + 1 }, sourceConfirmed: true), "PACKAGE_HASH_MISMATCH");
        Assert(service.GetInstalled().Length == 0, "mismatch created registration");
    });
    private static void Sources() => InTemp(root =>
    {
        var service = Service(root); string one = Package(root), two = Package(root, "2.0.0");
        service.Install(one, Expected(one), sourceConfirmed: true);
        Reject(() => service.Install(two, Expected(two) with { Source = new("github", 8, "same/name") }), "PACKAGE_SOURCE_CONFLICT");
        var renamed = service.Install(two, Expected(two) with { Source = new("github", 7, "new/name") });
        Assert(renamed.Package.HostSource == "github:7", "rename split account data source");
    });
    private static void LocalSource() => InTemp(root =>
    {
        var service = Service(root); string one = Package(root); service.Install(one, Expected(one), sourceConfirmed: true);
        Reject(() => service.Install(one, sourceConfirmed: true), "PACKAGE_SOURCE_CONFLICT");
        service.Uninstall("test.store"); Reject(() => service.Install(one, sourceConfirmed: true), "PACKAGE_SOURCE_CONFLICT");
    });
    private static void HistoricalVersion() => InTemp(root =>
    {
        var service = Service(root); service.Install(Package(root), sourceConfirmed: true);
        service.Install(Package(root, "2.0.0"));
        Reject(() => service.Install(Package(root, content: "changed"), intent: InstallIntent.Downgrade, downgradeConfirmed: true), "PACKAGE_VERSION_CONFLICT");
    });
    private static void VersionPolicy() => InTemp(root =>
    {
        var service = Service(root); service.Install(Package(root), sourceConfirmed: true);
        service.SetVersionPolicy("test.store", true, false);
        service = Service(root); Assert(service.Find("test.store")!.PinnedVersion == "1.0.0", "pin lost on restart");
        string preview = Package(root, "2.0.0-preview.1");
        Reject(() => service.Install(preview), "PACKAGE_VERSION_PINNED");
        service.SetVersionPolicy("test.store", false, false); Reject(() => service.Install(preview), "PACKAGE_PREVIEW_DISABLED");
        service.SetVersionPolicy("test.store", false, true); service.Install(preview);
        Assert(Service(root).Find("test.store")!.AllowPreview, "application preview lost");
    });
    private static void RuntimeLease() => InTemp(root =>
    {
        var service = Service(root); string one = Package(root); var first = service.Install(one, sourceConfirmed: true);
        using (service.EnterRuntimeLease("test.store"))
        {
            Reject(() => service.Install(Package(root, "2.0.0")), "PACKAGE_APP_RUNNING");
            Reject(() => service.Install(one, intent: InstallIntent.Repair), "PACKAGE_APP_RUNNING");
            Reject(() => service.Uninstall("test.store"), "PACKAGE_APP_RUNNING");
        }
        service.Install(Package(root, "2.0.0")); Assert(service.Find("test.store")!.Package.DirectoryPath != first.Package.DirectoryPath, "version not switched after close");
    });
    private static void LaunchRace() => InTemp(root =>
    {
        var service = Service(root); service.Install(Package(root), sourceConfirmed: true);
        using ManualResetEventSlim staged = new(), release = new();
        var updating = Service(root, new(point => { if (point == InstallCommitPoint.AfterContentInstall) { staged.Set(); Assert(release.Wait(TimeSpan.FromSeconds(5)), "test synchronization timeout"); } }));
        Task install = Task.Run(() => updating.Install(Package(root, "2.0.0")));
        Assert(staged.Wait(TimeSpan.FromSeconds(5)), "install did not reach stage");
        Task<IDisposable> launching = Task.Run(() => updating.EnterRuntimeLease("test.store"));
        Assert(!launching.Wait(TimeSpan.FromMilliseconds(80)), "launch bypassed pending commit");
        release.Set(); install.GetAwaiter().GetResult();
        using var lease = launching.GetAwaiter().GetResult();
        Assert(updating.Find("test.store")!.Package.Manifest.Version == "2.0.0", "launch resolved stale active version");
    });
    private static void Repair() => InTemp(root =>
    {
        var service = Service(root); string package = Package(root); var original = service.Install(package, sourceConfirmed: true);
        File.WriteAllText(Path.Combine(original.Package.DirectoryPath, "index.html"), "damaged");
        Reject(() => service.Install(package), "PACKAGE_INSTALL_CORRUPT");
        Reject(() => service.Install(Package(root, "2.0.0"), intent: InstallIntent.Repair), "PACKAGE_REPAIR_VERSION_REQUIRED");
        var repaired = service.Install(package, intent: InstallIntent.Repair);
        ApplicationInstallService.VerifyInstalled(repaired.Package);
        Assert(repaired.Package.DirectoryPath != original.Package.DirectoryPath && File.ReadAllText(Path.Combine(original.Package.DirectoryPath, "index.html")) == "damaged", "repair must preserve old evidence");
    });
    private static void Uninstall() => InTemp(root =>
    {
        var service = Service(root); service.Install(Package(root), sourceConfirmed: true); string save = Save(root, "guest", 1);
        byte[] before = File.ReadAllBytes(save); service.Uninstall("test.store"); service.Uninstall("test.store");
        Assert(Service(root).Find("test.store") is null && service.HasRegistration("test.store") && File.ReadAllBytes(save).SequenceEqual(before), "uninstall lost saves or source identity");
        Reject(() => service.EnterRuntimeLease("test.store"), "APP_NOT_INSTALLED");
    });
    private static void InterruptedUpdate() => InTemp(root =>
    {
        var service = Service(root); var previous = service.Install(Package(root), sourceConfirmed: true);
        var failing = Service(root, new(point => { if (point == InstallCommitPoint.BeforeRegistryCommit) throw new IOException("isolated interruption"); }));
        Reject(() => failing.Install(Package(root, "2.0.0")), "PACKAGE_IO_ERROR");
        var restarted = Service(root); Assert(restarted.Find("test.store")!.Package.DirectoryPath == previous.Package.DirectoryPath, "interrupted update changed active pointer");
        Assert(restarted.InspectRecovery().Contains("staged_not_registered"), "interruption not observable");
    });
    private static void InterruptedFirst() => InTemp(root =>
    {
        var service = Service(root, new(point => { if (point == InstallCommitPoint.AfterContentInstall) throw new IOException(); }));
        Reject(() => service.Install(Package(root), sourceConfirmed: true), "PACKAGE_IO_ERROR");
        Assert(Service(root).GetInstalled().Length == 0 && Service(root).InspectRecovery().Single() == "staged_not_registered", "failed first install ghost icon");
    });
    private static void DiskFull() => InTemp(root =>
    {
        Service(root).Install(Package(root), sourceConfirmed: true);
        byte[] registry = File.ReadAllBytes(Path.Combine(root, "Apps", ".autumnos-registry.json"));
        var service = Service(root, new(point => { if (point == InstallCommitPoint.BeforeRegistryCommit) throw new DiskFullException(); }));
        Reject(() => service.Install(Package(root, "2.0.0")), "PACKAGE_DISK_FULL");
        Assert(File.ReadAllBytes(Path.Combine(root, "Apps", ".autumnos-registry.json")).SequenceEqual(registry), "disk failure modified old registration");
    });
    private static void AfterCommit() => InTemp(root =>
    {
        var service = Service(root, new(point => { if (point == InstallCommitPoint.AfterRegistryCommit) throw new IOException(); }));
        Reject(() => service.Install(Package(root), sourceConfirmed: true), "PACKAGE_IO_ERROR");
        Assert(Service(root).Find("test.store") is not null, "atomic commit lost after receipt interruption");
    });
    private static void Bundle() => InTemp(root =>
    {
        string path = Package(root); var old = PackageInstaller.Install(path, Path.Combine(root, "Apps"));
        var service = Service(root); var adopted = service.RegisterExisting(old)!;
        Assert(adopted.Package.HostSource == "bundled:test.store", "bundled source binding changed");
        Assert(service.RegisterExisting(old)!.Package.DirectoryPath == old.DirectoryPath, "duplicate adoption changed path");
        service.Uninstall("test.store"); Assert(service.RegisterExisting(old) is null, "bundled application resurrected after uninstall");
    });
    private static void Downgrade() => InTemp(root =>
    {
        var guard = Guard(root); var service = new ApplicationInstallService(Path.Combine(root, "Apps"), prepareSaveChange: guard.PrepareChange);
        service.Install(Package(root, "2.0.0"), sourceConfirmed: true);
        string a = Save(root, "guest", 1), b = Save(root, "account-b", 1);
        byte[] aa = File.ReadAllBytes(a), bb = File.ReadAllBytes(b);
        string old = Package(root); Reject(() => service.Install(old), "PACKAGE_DOWNGRADE_CONFIRMATION_REQUIRED");
        service.Install(old, intent: InstallIntent.Downgrade, downgradeConfirmed: true);
        Assert(guard.LastBackupPath is not null && File.Exists(Path.Combine(guard.LastBackupPath, ".backup-complete.json")), "downgrade backup incomplete");
        Assert(File.ReadAllBytes(a).SequenceEqual(aa) && File.ReadAllBytes(b).SequenceEqual(bb), "downgrade modified saves");
        Assert(File.ReadAllBytes(Path.Combine(guard.LastBackupPath!, "source", "account-b", "game.json")).SequenceEqual(bb), "account B backup missing");
    });
    private static void BadSave() => InTemp(root =>
    {
        var service = Service(root); service.Install(Package(root, "2.0.0", format: 2), sourceConfirmed: true);
        string path = Save(root, "account-other", 2);
        Reject(() => service.Install(Package(root), intent: InstallIntent.Downgrade, downgradeConfirmed: true), "SAVE_FORMAT_INCOMPATIBLE");
        File.WriteAllText(path, "{");
        Reject(() => service.Install(Package(root), intent: InstallIntent.Downgrade, downgradeConfirmed: true), "SAVE_CORRUPT");
        Assert(service.Find("test.store")!.Package.Manifest.Version == "2.0.0", "incompatible downgrade committed");
    });
    private static void NoSaveGuard() => InTemp(root =>
    {
        var service = new ApplicationInstallService(Path.Combine(root, "Apps")); service.Install(Package(root, "2.0.0"), sourceConfirmed: true);
        Reject(() => service.Install(Package(root), intent: InstallIntent.Downgrade, downgradeConfirmed: true), "SAVE_COMPATIBILITY_UNAVAILABLE");
    });
    private static void SaveManifest() => InTemp(root =>
    {
        var service = Service(root); service.Install(Package(root), sourceConfirmed: true); Save(root, "guest", 1);
        Reject(() => service.Install(Package(root, "2.0.0", format: 2)), "SAVE_FORMAT_INCOMPATIBLE");
        service.Install(Package(root, "2.0.0", format: 2, minimum: 1));
        Assert(service.Find("test.store")!.Package.Manifest.MinReadableSaveFormatVersion == 1, "readable version declaration discarded");
    });
    private static void Cancel() => InTemp(root =>
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var service = Service(root);
        Reject(() => service.Install(Package(root), sourceConfirmed: true, cancellationToken: cancellation.Token), "PACKAGE_CANCELLED");
        Assert(service.GetInstalled().Length == 0, "cancel created icon");
        Reject(() => service.Install(Path.Combine(root, "anything.exe"), sourceConfirmed: true), "PACKAGE_EXTENSION_INVALID");
    });
    private static void CorruptRegistry() => InTemp(root =>
    {
        var service = Service(root); service.Install(Package(root), sourceConfirmed: true);
        string path = Path.Combine(root, "Apps", ".autumnos-registry.json");
        string original = File.ReadAllText(path); File.WriteAllText(path, original.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 1,\"schemaVersion\": 1", StringComparison.Ordinal));
        Reject(() => service.GetInstalled(), "PACKAGE_REGISTRY_CORRUPT");
    });
    private static void Provenance() => InTemp(root =>
    {
        string path = Package(root); var expected = Expected(path) with { Provenance = new(8, 9, new string('a', 64), new string('b', 40)) };
        var service = Service(root); service.Install(path, expected, sourceConfirmed: true);
        Assert(Service(root).Find("test.store")!.Provenance?.AssetId == 9, "release asset provenance lost");
        InTemp(other => Reject(() => Service(other).Install(path, expected with { Source = expected.Source with { SigningKeyFingerprint = new string('c', 64) } }, sourceConfirmed: true), "PACKAGE_SIGNATURE_UNSUPPORTED"));
    });
    private static void FailedBackup() => InTemp(root =>
    {
        var service = Service(root); service.Install(Package(root, "2.0.0"), sourceConfirmed: true); Save(root, "guest", 1);
        File.WriteAllText(Path.Combine(root, "Backups"), "occupied");
        Reject(() => service.Install(Package(root), intent: InstallIntent.Downgrade, downgradeConfirmed: true), "PACKAGE_IO_ERROR");
        Assert(service.Find("test.store")!.Package.Manifest.Version == "2.0.0", "failed backup changed installation");
    });
    private static void Versions()
    {
        Assert(ApplicationInstallService.CompareVersions("1.0.0-preview.10", "1.0.0-preview.2") > 0, "numeric preview ordering");
        Assert(ApplicationInstallService.CompareVersions("1.0.0", "1.0.0-preview.99") > 0, "stable ordering");
        Assert(ApplicationInstallService.CompareVersions("1.0.0+first", "1.0.0+second") == 0, "build metadata precedence");
    }
    private static ApplicationInstallService Service(string root, InstallFaultHooks? faults = null) =>
        new(Path.Combine(root, "Apps"), prepareSaveChange: Guard(root).PrepareChange, testHooks: faults);
    private static InstalledSaveGuard Guard(string root) => new(Path.Combine(root, "Saves"), Path.Combine(root, "Backups"));
    private static ExpectedPackageIdentity Expected(string path)
    {
        var inspection = PackageInstaller.Inspect(path); var m = inspection.Manifest;
        return new(new("github", 7, "author/store"), m.AppId, m.Version, m.Runtime, m.Entry, m.Permissions, inspection.Sha256, new FileInfo(path).Length, m.SaveFormatVersion);
    }
    private static string Package(string root, string version = "1.0.0", string content = "actual web content", int format = 1, int? minimum = null)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".autumn");
        var manifest = new Dictionary<string, object> { ["schemaVersion"] = 1, ["appId"] = "test.store", ["name"] = "隔离安装测试",
            ["version"] = version, ["runtime"] = "web", ["entry"] = "index.html", ["permissions"] = new[] { "saves" }, ["saveFormatVersion"] = format };
        if (minimum is not null) manifest["minReadableSaveFormatVersion"] = minimum.Value;
        using ZipArchive archive = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, string value) in new[] { ("manifest.json", JsonSerializer.Serialize(manifest)), ("index.html", content) })
        {
            var entry = archive.CreateEntry(name); entry.LastWriteTime = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
            using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false)); writer.Write(value);
        }
        return path;
    }
    private static string Save(string root, string account, int format)
    {
        string path = Path.Combine(root, "Saves", "test.store", "source", account, "game.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var value = JsonDocument.Parse("{\"progress\":42}");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 2, appId = "test.store", sourceKey = "source", accountKey = account,
            slot = "game", formatVersion = format, revision = 1, contentHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value.RootElement))), value = value.RootElement }));
        return path;
    }
    private static void InTemp(Action<string> action)
    {
        string parent = Path.GetFullPath(Path.GetTempPath()), root = Path.Combine(parent, "AutumnOS-store-install-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            string resolved = Path.GetFullPath(root);
            if (!resolved.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolved).StartsWith("AutumnOS-store-install-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup");
            Directory.Delete(resolved, true);
        }
    }
    private static void Reject(Action action, string code)
    {
        try { action(); } catch (PackageException e) { Assert(e.Code == code, "expected " + code + ", got " + e.Code); return; }
        throw new InvalidOperationException("expected rejection " + code);
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class DiskFullException : IOException { public DiskFullException() => HResult = unchecked((int)0x80070070); }
}
