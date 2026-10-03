using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using AutumnOS.Update;
using AutumnOS.UpdateProtocol;

namespace AutumnOS.Tests;

[SupportedOSPlatform("windows")]
internal static class UpdateProtocolTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("updater.physical_root_shared_leases_block_exclusive_across_handles", RootLease);
        yield return ("updater.exclusive_physical_root_blocks_business_start", ExclusiveLease);
        yield return ("updater.receipt_rejects_stable_bootstrap_and_data_paths", ProtectedPaths);
        yield return ("updater.receipt_rejects_case_collisions", ReceiptCollision);
        yield return ("updater.handoff_expiry_rejected_before_process_or_file_mutation", ExpiredHandoff);
        yield return ("updater.handoff_replay_rejected_before_mutation", ReplayedHandoff);
        yield return ("updater.handoff_wrong_root_rejected_without_touching_target", WrongRoot);
        yield return ("updater.interrupted_file_replacement_recovers_verified_backup_and_preserves_data", Recovery);
        yield return ("updater.recovery_retains_unknown_changed_new_file_and_failure_report", PreserveUnknown);
        yield return ("updater.corrupt_backup_stops_recovery_before_restoration", CorruptBackup);
        yield return ("updater.duplicate_or_unknown_record_fields_rejected", MalformedRecord);
        yield return ("updater.unauthorized_health_argument_never_creates_data", UnauthorizedHealth);
        yield return ("updater.recovery_handles_backed_up_applying_health_and_partial_rollback_boundaries", RecoveryPhases);
        yield return ("updater.committed_and_aborted_journals_are_not_replayed", TerminalJournal);
        yield return ("updater.missing_managed_resource_is_restored_from_prior_build", MissingResource);
        yield return ("updater.active_maintenance_excludes_concurrent_recovery_and_double_launch", MaintenanceGate);
        yield return ("updater.live_exact_child_identity_delays_recovery_without_kill", LiveChild);
        yield return ("updater.journal_target_root_mismatch_cannot_write_other_directory", WrongRecoveryRoot);
        yield return ("updater.actual_apply_replaces_windows_files_while_occupancy_guards_are_held", ActualApply);
        yield return ("updater.actual_apply_failure_restores_A_and_reports_relative_safe_diagnostics", ActualApplyFailure);
        yield return ("updater.actual_apply_external_read_handle_aborts_before_first_replacement", ActualApplyBusy);
        yield return ("updater.new_file_created_after_preflight_is_neither_replaced_nor_retired", NewFileRace);
        yield return ("updater.content_changed_before_guard_acquisition_is_rechecked_on_exact_held_stream", GuardBytes);
    }

    private static void RootLease() => Temp(root =>
    {
        using var first = UpdateRootLease.AcquireShared(root);
        using var second = UpdateRootLease.AcquireShared(root);
        Reject(() => { using var exclusive = UpdateRootLease.AcquireExclusive(root); });
    });
    private static void ExclusiveLease() => Temp(root =>
    {
        using (var exclusive = UpdateRootLease.AcquireExclusive(root)) Reject(() => { using var shared = UpdateRootLease.AcquireShared(root); });
        using var nowAvailable = UpdateRootLease.AcquireShared(root);
    });
    private static void ProtectedPaths() => Temp(root =>
    {
        foreach (string path in new[] { "AutumnOS.exe", "AutumnOS.Updater.exe", "AutumnOS_Data/save.json", "../escape.dll", "C:/escape.dll", "file.dll:stream", ".autumnos-update/journal.json", "autumn.install.json" })
            Reject(() => UpdateFiles.Managed(root, path));
    });
    private static void ReceiptCollision() => Temp(root =>
    {
        UpdateFiles.Write(Path.Combine(root, UpdateFiles.ReceiptName), new InstalledBuild(1, "0.5.0", "fixture-A", [Entry("a.dll", "a"), Entry("A.dll", "a"), Entry("b.dll", "b")]));
        Reject(() => UpdateFiles.ReadInstalled(root));
    });
    private static void ExpiredHandoff() => Temp(root =>
    {
        var record = Offer(root) with { ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1) };
        WriteOffer(root, record); Reject(() => UpdateTransaction.RunHandoff(root, record.TransactionId));
        Assert(!File.Exists(Path.Combine(root, "AutumnOS.Client.exe")), "Expired handoff created a business file.");
    });
    private static void ReplayedHandoff() => Temp(root =>
    {
        var record = Offer(root); WriteOffer(root, record);
        UpdateFiles.Write(Path.Combine(UpdateFiles.Transaction(root, record.TransactionId), "consumed.json"), new { consumed = true });
        Reject(() => UpdateTransaction.RunHandoff(root, record.TransactionId));
    });
    private static void WrongRoot() => Temp(root =>
    {
        var record = Offer(root) with { Root = Path.GetTempPath() }; WriteOffer(root, record);
        Reject(() => UpdateTransaction.RunHandoff(root, record.TransactionId));
    });
    private static void Recovery() => Temp(root =>
    {
        var journal = Interrupted(root);
        UpdateTransaction.Recover(root);
        Assert(File.ReadAllText(Path.Combine(root, "AutumnOS.Client.exe")) == "old-client", "Original program bytes were not restored.");
        Assert(File.ReadAllText(Path.Combine(root, "AutumnOS_Data", "sentinel.txt")) == "user-data", "Recovery changed data.");
        Assert(File.ReadAllText(Path.Combine(root, "unknown.txt")) == "unknown", "Recovery changed unknown files.");
        Assert(!File.Exists(Path.Combine(root, "new.dll")), "Owned added file survived rollback.");
        Assert(UpdateFiles.Read<UpdateJournal>(Path.Combine(UpdateFiles.Work(root), "journal.json")).Phase == "rolledBack", "Recovery not persisted.");
        Assert(UpdateTransaction.IsFailedBuild(root, "fixture-B"), "Bad build was not blocked.");
        UpdateTransaction.Recover(root); // Completed recovery is idempotent.
    });
    private static void PreserveUnknown() => Temp(root =>
    {
        Interrupted(root); File.WriteAllText(Path.Combine(root, "new.dll"), "changed-by-owner");
        Reject(() => UpdateTransaction.Recover(root));
        Assert(File.ReadAllText(Path.Combine(root, "new.dll")) == "changed-by-owner", "Recovery deleted changed bytes it did not own.");
        Assert(UpdateFiles.Read<UpdateJournal>(Path.Combine(UpdateFiles.Work(root), "journal.json")).Phase == "rollingBack", "Failure evidence was lost.");
    });
    private static void CorruptBackup() => Temp(root =>
    {
        var journal = Interrupted(root);
        File.WriteAllText(Path.Combine(UpdateFiles.Transaction(root, journal.TransactionId), "backup", "AutumnOS.Client.exe"), "corrupt");
        Reject(() => UpdateTransaction.Recover(root));
        Assert(File.ReadAllText(Path.Combine(root, "AutumnOS.Client.exe")) == "new-client", "Unverified backup was restored.");
    });
    private static void MalformedRecord() => Temp(root =>
    {
        string path = Path.Combine(root, "record.json");
        File.WriteAllText(path, "{\"pid\":1,\"pid\":2,\"startUtcTicks\":3}"); Reject(() => UpdateFiles.Read<UpdaterIdentity>(path));
        File.WriteAllText(path, "{\"pid\":1,\"startUtcTicks\":3,\"command\":\"run\"}"); Reject(() => UpdateFiles.Read<UpdaterIdentity>(path));
    });
    private static void UnauthorizedHealth() => Temp(root =>
    {
        Reject(() => { using var guard = UpdateLaunchGuard.Enter(root, ["--update-health", Guid.NewGuid().ToString("N")]); });
        Assert(!Directory.Exists(Path.Combine(root, "AutumnOS_Data")), "Unauthorized startup initialized business data.");
    });
    private static void RecoveryPhases() => Temp(root =>
    {
        foreach (string phase in new[] { "backedUp", "applying", "health", "rollingBack" })
        {
            string scenario = Path.Combine(root, phase); Directory.CreateDirectory(scenario);
            var journal = Interrupted(scenario);
            if (phase == "backedUp")
            {
                foreach (var file in journal.Previous.Files) File.Copy(Path.Combine(UpdateFiles.Transaction(scenario, journal.TransactionId), "backup", file.Path), Path.Combine(scenario, file.Path), true);
                File.Delete(Path.Combine(scenario, "new.dll"));
                UpdateFiles.Write(Path.Combine(scenario, UpdateFiles.ReceiptName), journal.Previous);
            }
            if (phase is "applying" or "rollingBack") File.Copy(Path.Combine(UpdateFiles.Transaction(scenario, journal.TransactionId), "backup", "core.dll"), Path.Combine(scenario, "core.dll"), true);
            UpdateFiles.Write(Path.Combine(UpdateFiles.Work(scenario), "journal.json"), journal with { Phase = phase });
            UpdateTransaction.Recover(scenario);
            Assert(journal.Previous.Files.All(f => UpdateFiles.Matches(Path.Combine(scenario, f.Path), f)), "An interruption boundary failed to restore all recorded original bytes.");
            Assert(!File.Exists(Path.Combine(scenario, "new.dll")), "An added file remained after rollback.");
        }
    });
    private static void TerminalJournal() => Temp(root =>
    {
        foreach (string phase in new[] { "committed", "aborted", "rolledBack" })
        {
            string scenario = Path.Combine(root, phase); Directory.CreateDirectory(scenario);
            var journal = Interrupted(scenario); UpdateFiles.Write(Path.Combine(UpdateFiles.Work(scenario), "journal.json"), journal with { Phase = phase });
            UpdateTransaction.Recover(scenario);
            Assert(File.ReadAllText(Path.Combine(scenario, "AutumnOS.Client.exe")) == "new-client", "Terminal journal replay changed program files.");
            Assert(!UpdateTransaction.NeedsRecovery(scenario), "Terminal journal reported active recovery.");
        }
    });
    private static void MissingResource() => Temp(root =>
    {
        Interrupted(root); File.Delete(Path.Combine(root, "resources.pri"));
        UpdateTransaction.Recover(root);
        Assert(File.ReadAllText(Path.Combine(root, "resources.pri")) == "old-resource", "Missing resource was not recovered.");
    });
    private static void MaintenanceGate() => Temp(root =>
    {
        Interrupted(root);
        using (var gate = UpdateMutexLease.AcquireMaintenance())
        {
            Assert(UpdateMutexLease.MaintenanceActive(), "Maintenance ownership not visible.");
            Reject(() => { using var second = UpdateMutexLease.AcquireMaintenance(); });
            Reject(() => UpdateTransaction.Recover(root));
            Reject(() => { using var business = UpdateLaunchGuard.Enter(root, []); });
            Assert(File.ReadAllText(Path.Combine(root, "AutumnOS.Client.exe")) == "new-client", "A concurrent recovery changed active files.");
        }
        Assert(!UpdateMutexLease.MaintenanceActive(), "Released gate remained busy.");
    });
    private static void LiveChild() => Temp(root =>
    {
        var journal = Interrupted(root);
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        UpdateFiles.Write(Path.Combine(UpdateFiles.Work(root), "journal.json"), journal with { ChildPid = self.Id, ChildStartUtcTicks = self.StartTime.ToUniversalTime().Ticks });
        Reject(() => UpdateTransaction.Recover(root));
        Assert(File.ReadAllText(Path.Combine(root, "AutumnOS.Client.exe")) == "new-client", "Live child did not defer restoration.");
    });
    private static void WrongRecoveryRoot() => Temp(root =>
    {
        var journal = Interrupted(root);
        UpdateFiles.Write(Path.Combine(UpdateFiles.Work(root), "journal.json"), journal with { Root = Path.GetTempPath() });
        Reject(() => UpdateTransaction.Recover(root));
        Assert(File.ReadAllText(Path.Combine(root, "AutumnOS.Client.exe")) == "new-client", "Wrong-root journal modified program files.");
    });
    private static void ActualApply() => Temp(root =>
    {
        var fixture = ApplyFixture(root);
        using var rootLease = UpdateRootLease.AcquireExclusive(root);
        InvokeApplyFiles(root, fixture.Tx, fixture.Offer, fixture.Manifest);
        Assert(fixture.Manifest.Files.All(f => UpdateFiles.Matches(Path.Combine(root, f.Path), new(f.Path, f.Bytes, f.Sha256))), "Actual Windows Apply did not install all target bytes.");
        Assert(fixture.Old.Files.All(f => UpdateFiles.Matches(Path.Combine(fixture.Tx, "backup", f.Path), f)), "Actual Apply did not produce valid complete backups.");
        Assert(File.ReadAllText(Path.Combine(fixture.Tx, "retired", "App.xbf")) == "old-xbf", "Protected original file was not renamed into the private transaction.");
        Assert(!File.Exists(Path.Combine(root, "obsolete.dll")), "Declared managed removal did not run.");
        Assert(UpdateFiles.Read<UpdateJournal>(Path.Combine(UpdateFiles.Work(root), "journal.json")).Phase == "health", "Apply incorrectly claimed health success or stopped before health.");
        Assert(File.ReadAllText(Path.Combine(root, "AutumnOS_Data", "saved.txt")) == "saved" && File.ReadAllText(Path.Combine(root, "unknown.txt")) == "unknown", "Actual Apply touched protected data or unknown files.");
    });
    private static void ActualApplyFailure() => Temp(root =>
    {
        var fixture = ApplyFixture(root);
        Directory.CreateDirectory(Path.Combine(fixture.Tx, "retired")); File.WriteAllText(Path.Combine(fixture.Tx, "retired", "App.xbf"), "collision-owned-by-test");
        using var rootLease = UpdateRootLease.AcquireExclusive(root);
        try { InvokeApplyFiles(root, fixture.Tx, fixture.Offer, fixture.Manifest); throw new Exception("Apply failure was not injected."); }
        catch (IOException error) { Assert(error.Message == "UPDATE_ROLLED_BACK", "Completed rollback did not request original-entry relaunch."); }
        Assert(fixture.Old.Files.All(f => UpdateFiles.Matches(Path.Combine(root, f.Path), f)), "Actual Apply error failed to restore every original file.");
        Assert(UpdateFiles.Read<UpdateJournal>(Path.Combine(UpdateFiles.Work(root), "journal.json")).Phase == "rolledBack", "Actual Apply error did not persist rollback.");
        var diagnostic = UpdateFiles.Read<UpdateFailureDiagnostic>(Path.Combine(fixture.Tx, "failure.json"));
        Assert(diagnostic.Phase == "replace" && diagnostic.File == "App.xbf" && diagnostic.ErrorType == "IOException" && diagnostic.HResult.StartsWith("0x"), "Failure diagnostic lacks exact safe operation identity.");
        Assert(!File.ReadAllText(Path.Combine(fixture.Tx, "failure.json")).Contains(root, StringComparison.Ordinal), "Diagnostic exposed an absolute user path.");
        Assert(UpdateTransaction.IsFailedBuild(root, fixture.Manifest.BuildId), "Failed candidate was not blocked.");
    });
    private static void ActualApplyBusy() => Temp(root =>
    {
        var fixture = ApplyFixture(root);
        using var occupied = new FileStream(Path.Combine(root, "core.dll"), FileMode.Open, FileAccess.Read, FileShare.Read);
        using var rootLease = UpdateRootLease.AcquireExclusive(root);
        try { InvokeApplyFiles(root, fixture.Tx, fixture.Offer, fixture.Manifest); throw new Exception("Existing reader did not block mutation."); }
        catch (IOException error) { Assert(error.Message == "UPDATE_APPLY_ABORTED_FILES_BUSY", "File occupancy did not produce the safe pre-mutation abort."); }
        Assert(fixture.Old.Files.All(f => UpdateFiles.Matches(Path.Combine(root, f.Path), f)), "Busy-file preflight modified original program bytes.");
        var journal = UpdateFiles.Read<UpdateJournal>(Path.Combine(UpdateFiles.Work(root), "journal.json"));
        Assert(journal.Phase == "aborted" && journal.ErrorCode == "UPDATE_FILES_BUSY", "File-busy journal was not safe to reopen.");
        Assert(UpdateFiles.Read<UpdateFailureDiagnostic>(Path.Combine(fixture.Tx, "failure.json")).Phase == "occupancy", "File-busy failure phase was lost.");
    });
    private static void NewFileRace() => Temp(root =>
    {
        string source = Path.Combine(root, "stage.dll"), target = Path.Combine(root, "new.dll");
        File.WriteAllText(source, "signed-new"); File.WriteAllText(target, "unknown-created-after-preflight");
        var method = typeof(UpdateTransaction).GetMethod("Replace", BindingFlags.NonPublic | BindingFlags.Static)!;
        try { method.Invoke(null, [source, target, Entry("new.dll", "signed-new"), null, false]); throw new Exception("A new-path collision was silently replaced."); }
        catch (TargetInvocationException error) when (error.InnerException is IOException cause) { Assert(cause.Message == "UPDATE_UNKNOWN_FILE_COLLISION", "New-file collision did not reject explicitly."); }
        Assert(File.ReadAllText(target) == "unknown-created-after-preflight", "Unknown file was moved or overwritten.");
        Assert(File.ReadAllText(source) == "signed-new", "Source staged bytes were changed.");
    });
    private static void GuardBytes() => Temp(root =>
    {
        string path = Path.Combine(root, "App.xbf"); var expected = Entry("App.xbf", "original");
        File.WriteAllText(path, "changed-after-backup");
        var method = typeof(UpdateTransaction).GetMethod("ProtectOwnedFile", BindingFlags.NonPublic | BindingFlags.Static)!;
        try { using var unexpected = (FileStream)method.Invoke(null, [path, expected])!; throw new Exception("Modified original bytes were admitted by the occupancy guard."); }
        catch (TargetInvocationException error) when (error.InnerException is IOException cause) { Assert(cause.Message == "UPDATE_MANAGED_FILE_CHANGED", "Guard byte mismatch was not reported."); }
        Assert(File.ReadAllText(path) == "changed-after-backup", "Guard mismatch restored over user-modified bytes.");
        File.WriteAllText(path, "original");
        using var guard = (FileStream)method.Invoke(null, [path, expected])!;
        Assert(guard.Length == expected.Bytes, "Correct bytes did not retain their actual guard.");
        Reject(() => File.WriteAllText(path, "concurrent-write"));
    });
    private static (string Tx, HandoffRecord Offer, UpdateManifest Manifest, InstalledBuild Old) ApplyFixture(string root)
    {
        string id = Guid.NewGuid().ToString("N"), tx = UpdateFiles.Transaction(root, id);
        UpdateFiles.PrivateDirectory(UpdateFiles.Work(root)); UpdateFiles.PrivateDirectory(tx);
        var originals = new Dictionary<string, string> { ["App.xbf"] = "old-xbf", ["AutumnOS.Client.exe"] = "old-client", ["core.dll"] = "old-core", ["obsolete.dll"] = "old-obsolete" };
        var target = new Dictionary<string, string> { ["App.xbf"] = "new-xbf", ["AutumnOS.Client.exe"] = "new-client", ["core.dll"] = "new-core", ["new.dll"] = "new-library" };
        foreach (var file in originals) File.WriteAllText(Path.Combine(root, file.Key), file.Value);
        var old = new InstalledBuild(1, "0.5.0", "actual-apply-A", originals.Select(f => Entry(f.Key, f.Value)).ToArray());
        UpdateFiles.Write(Path.Combine(root, UpdateFiles.ReceiptName), old);
        Directory.CreateDirectory(Path.Combine(root, "AutumnOS_Data")); File.WriteAllText(Path.Combine(root, "AutumnOS_Data", "saved.txt"), "saved"); File.WriteAllText(Path.Combine(root, "unknown.txt"), "unknown");
        string payload = Path.Combine(tx, "payload.zip");
        using (var zip = ZipFile.Open(payload, ZipArchiveMode.Create)) foreach (var file in target)
        { var item = zip.CreateEntry(file.Key); using var output = new StreamWriter(item.Open(), new System.Text.UTF8Encoding(false)); output.Write(file.Value); }
        var manifest = new UpdateManifest(1, "plus", "0.5.1", "actual-apply-B", "win-x64", "1.0.0", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1), "fixture-key", 1,
            new("payload.zip", new FileInfo(payload).Length, UpdateFiles.Hash(payload)), target.Select(f => { var e = Entry(f.Key, f.Value); return new UpdateFile(e.Path, e.Bytes, e.Sha256); }).ToArray(), ["obsolete.dll"], new(1, 1, true));
        File.WriteAllBytes(Path.Combine(tx, "autumn.update.json"), UpdateManifestCodec.Serialize(manifest));
        var offer = new HandoffRecord(1, id, root, Path.Combine(root, "AutumnOS_Data"), Environment.ProcessId, 0, old.BuildId, old.Version, UpdateFiles.Hash(Path.Combine(tx, "autumn.update.json")), DateTimeOffset.UtcNow.AddMinutes(1));
        return (tx, offer, manifest, old);
    }
    private static void InvokeApplyFiles(string root, string tx, HandoffRecord offer, UpdateManifest manifest)
    {
        // This is the actual private transaction boundary after cryptographic admission;
        // the fixture does not claim signed process handoff or WinUI health coverage.
        var method = typeof(UpdateTransaction).GetMethod("ApplyFiles", BindingFlags.NonPublic | BindingFlags.Static) ?? throw new Exception("Actual file transaction boundary missing.");
        try { method.Invoke(null, [root, tx, offer, manifest]); }
        catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); }
    }
    private static UpdateJournal Interrupted(string root)
    {
        string id = Guid.NewGuid().ToString("N"), tx = UpdateFiles.Transaction(root, id);
        UpdateFiles.PrivateDirectory(UpdateFiles.Work(root)); Directory.CreateDirectory(Path.Combine(tx, "backup"));
        var files = new[] { Entry("AutumnOS.Client.exe", "old-client"), Entry("core.dll", "old-core"), Entry("resources.pri", "old-resource") };
        foreach (var file in files) File.WriteAllText(Path.Combine(tx, "backup", file.Path), file.Path == "AutumnOS.Client.exe" ? "old-client" : file.Path == "core.dll" ? "old-core" : "old-resource");
        var previous = new InstalledBuild(1, "0.5.0", "fixture-A", files);
        var next = new[] { Entry("AutumnOS.Client.exe", "new-client"), Entry("core.dll", "new-core"), Entry("resources.pri", "new-resource"), Entry("new.dll", "new") };
        foreach (var file in next) File.WriteAllText(Path.Combine(root, file.Path), file.Path == "AutumnOS.Client.exe" ? "new-client" : file.Path == "core.dll" ? "new-core" : file.Path == "resources.pri" ? "new-resource" : "new");
        Directory.CreateDirectory(Path.Combine(root, "AutumnOS_Data")); File.WriteAllText(Path.Combine(root, "AutumnOS_Data", "sentinel.txt"), "user-data"); File.WriteAllText(Path.Combine(root, "unknown.txt"), "unknown");
        UpdateFiles.Write(Path.Combine(root, UpdateFiles.ReceiptName), new InstalledBuild(1, "0.5.1", "fixture-B", next));
        var journal = new UpdateJournal(1, id, root, "applying", "fixture-A", "fixture-B", previous, next, next.Select(f => f.Path).ToArray());
        UpdateFiles.Write(Path.Combine(UpdateFiles.Work(root), "journal.json"), journal); return journal;
    }
    private static HandoffRecord Offer(string root) => new(1, Guid.NewGuid().ToString("N"), root, Path.Combine(root, "AutumnOS_Data"), Environment.ProcessId, 0, "fixture-A", "0.5.0", new string('a', 64), DateTimeOffset.UtcNow.AddMinutes(1));
    private static void WriteOffer(string root, HandoffRecord record) => UpdateFiles.Write(Path.Combine(UpdateFiles.Transaction(root, record.TransactionId), "handoff.json"), record);
    private static InstalledFile Entry(string path, string text) { byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text); return new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))); }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception e) when (e is IOException or UpdateException or UnauthorizedAccessException) { return; } throw new Exception("Unsafe input unexpectedly accepted."); }
    private static void Temp(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "AutumnOS-updater-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, true); }
    }
}
