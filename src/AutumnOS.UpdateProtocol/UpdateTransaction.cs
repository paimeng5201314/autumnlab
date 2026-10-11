using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using AutumnOS.Store;
using AutumnOS.Update;

namespace AutumnOS.UpdateProtocol;

public sealed record UpdateSecurityState(long HighestSequence, string[] FailedBuilds, string ManifestSha256 = "", string? MinimumVersion = null);
public sealed record UpdateFailureDiagnostic(string TransactionId, string Phase, string? File, string ErrorType, string HResult, DateTimeOffset AtUtc);

public static class UpdateTransaction
{
    public const string UpdaterVersion = "1.1.0";
    public static UpdateFailureDiagnostic? LastFailure { get; private set; }
    private sealed class ApplyContext { internal string Phase = "inventory"; internal string? File; internal bool Recorded; }
    private static string JournalPath(string root) => Path.Combine(UpdateFiles.Work(root), "journal.json");
    private static UpdateSecurityState Security(string root)
    {
        string path = Path.Combine(UpdateFiles.Work(root), "security.json");
        return File.Exists(path) ? UpdateFiles.Read<UpdateSecurityState>(path) : new(0, []);
    }
    public static bool IsFailedBuild(string root, string buildId) => Security(root).FailedBuilds.Contains(buildId, StringComparer.Ordinal);
    public static long HighestSequence(string root) => Security(root).HighestSequence;
    public static string? GetCompatibility(string root, UpdateManifest manifest, bool rollback)
    {
        try
        {
            if (!SemanticVersion.TryParse(manifest.MinimumUpdaterVersion, out var minimum) || !SemanticVersion.TryParse(UpdaterVersion, out var updater) || minimum!.CompareTo(updater) > 0)
                return "UPDATE_UPDATER_TOO_OLD";
            // Protocol 1.1 is the first release profile that can read first-run v2.
            // Restoring an old onboarding file would incorrectly repeat setup/logout.
            if (rollback && minimum!.CompareTo(updater) < 0) return "UPDATE_ROLLBACK_PROTOCOL_UNSUPPORTED";
            if (minimum!.CompareTo(updater) < 0)
            {
                string firstRun = Path.Combine(UpdateFiles.DataRoot(root), "Config", "first-run.json");
                UpdatePaths.EnsureNoReparsePoints(firstRun);
                if (File.Exists(firstRun))
                {
                    if (new FileInfo(firstRun).Length > 16384) return "UPDATE_ONBOARDING_SCHEMA_UNSUPPORTED";
                    using var state = JsonDocument.Parse(File.ReadAllBytes(firstRun));
                    if (!state.RootElement.TryGetProperty("schemaVersion", out var schema) || !schema.TryGetInt32(out int version) || version > 1)
                        return "UPDATE_ONBOARDING_SCHEMA_UNSUPPORTED";
                }
            }
            var security = Security(root);
            if (security.MinimumVersion is { } floor && (!SemanticVersion.TryParse(floor, out var lower) ||
                !SemanticVersion.TryParse(manifest.Version, out var target) || target!.CompareTo(lower) < 0)) return "UPDATE_MINIMUM_SECURITY_VERSION";
            if (security.FailedBuilds.Contains(manifest.BuildId, StringComparer.Ordinal)) return "UPDATE_FAILED_BUILD_BLOCKED";
            UpdateDataSnapshot.CheckCompatibility(root, manifest);
            return null;
        }
        catch (IOException error) when (error.Message.StartsWith("UPDATE_", StringComparison.Ordinal)) { return error.Message; }
        catch (Exception error) when (error is IOException or UpdateException or UnauthorizedAccessException or JsonException) { return "UPDATE_DATA_COMPATIBILITY_UNAVAILABLE"; }
    }
    public static bool NeedsRecovery(string root) => File.Exists(JournalPath(root)) && UpdateFiles.Read<UpdateJournal>(JournalPath(root)).Phase is not ("committed" or "rolledBack" or "aborted");

    public static void RunHandoff(string root, string id)
    {
        LastFailure = null;
        root = UpdateFiles.Root(root);
        string tx = UpdateFiles.Transaction(root, id);
        var offer = UpdateFiles.Read<HandoffRecord>(Path.Combine(tx, "handoff.json"), 16384);
        if (offer.SchemaVersion != 1 || offer.TransactionId != id || offer.Root != root || offer.DataRoot != UpdateFiles.DataRoot(root) ||
            offer.ExpiresUtc < DateTimeOffset.UtcNow || offer.ExpiresUtc > DateTimeOffset.UtcNow.AddMinutes(4) || File.Exists(Path.Combine(tx, "consumed.json")))
            throw new IOException("UPDATE_HANDOFF_INVALID_OR_REPLAYED");
        using var parent = Process.GetProcessById(offer.ParentPid);
        if (parent.StartTime.ToUniversalTime().Ticks != offer.ParentStartUtcTicks || !string.Equals(parent.MainModule?.FileName, Path.Combine(root, "AutumnOS.Client.exe"), StringComparison.OrdinalIgnoreCase))
            throw new IOException("UPDATE_PARENT_IDENTITY_INVALID");
        using var maintenance = UpdateMutexLease.AcquireMaintenance();
        var manifest = Verify(root, tx, offer);
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40)))
        using (var pipe = UpdatePipe.ConnectAsync(id, "handoff", parent.Id, timeout.Token).GetAwaiter().GetResult())
        {
            UpdatePipe.SendAsync(pipe, new("offer", id, Digest: offer.ManifestSha256), timeout.Token).GetAwaiter().GetResult();
            var auth = UpdatePipe.ReceiveAsync(pipe, id, "authorize", timeout.Token).GetAwaiter().GetResult();
            if (auth.BuildId != offer.CurrentBuildId || auth.Digest != offer.ManifestSha256) throw new IOException("UPDATE_HANDOFF_AUTHORIZATION_INVALID");
            UpdateFiles.Write(Path.Combine(tx, "consumed.json"), new { transactionId = id, at = DateTimeOffset.UtcNow });
            UpdatePipe.SendAsync(pipe, new("exit-ready", id), timeout.Token).GetAwaiter().GetResult();
            UpdatePipe.ReceiveAsync(pipe, id, "exiting", timeout.Token).GetAwaiter().GetResult();
        }
        if (!parent.WaitForExit(45000)) throw new IOException("UPDATE_PARENT_EXIT_TIMEOUT");
        // A legacy v1 launcher may race this acquisition. Failure leaves all old files intact.
        using var business = UpdateMutexLease.AcquireBusiness(TimeSpan.FromSeconds(15));
        using var rootLease = UpdateRootLease.AcquireExclusive(root);
        Apply(root, tx, offer, manifest);
        business.Dispose();
        try { StartAndConfirm(root, id, manifest, rootLease); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Core initialization and file authenticity have already been confirmed at
            // this durable boundary. A lost final IPC acknowledgement is not authority
            // to kill a possibly interactive child or roll its program back underneath it.
            if (UpdateFiles.Read<UpdateJournal>(JournalPath(root)).Phase == "committed")
            {
                UpdateFiles.Write(Path.Combine(tx, "result.json"), new { outcome = "committed", buildId = manifest.BuildId, coreHealthVerified = true, warning = "UPDATE_FINAL_ACK_UNCERTAIN" });
                return;
            }
            RecordFailure(root, manifest.BuildId, manifest.Sequence, offer.ManifestSha256);
            using var rollbackBusiness = UpdateMutexLease.AcquireBusiness(TimeSpan.FromSeconds(15));
            // rootLease may have been released after a core health acknowledgement.
            rootLease.Dispose();
            using var rollbackRoot = UpdateRootLease.AcquireExclusive(root);
            Restore(root, "UPDATE_HEALTH_FAILED");
            throw new IOException("UPDATE_ROLLED_BACK", e);
        }
    }

    private static UpdateManifest Verify(string root, string tx, HandoffRecord offer)
    {
        string metadata = Path.Combine(tx, "autumn.update.json"), signature = Path.Combine(tx, "autumn.update.sig");
        if (new FileInfo(metadata).Length > UpdateManifestCodec.MaximumManifestBytes || new FileInfo(signature).Length > 16384 || UpdateFiles.Hash(metadata) != offer.ManifestSha256) throw new IOException("UPDATE_HANDOFF_DIGEST_INVALID");
        var security = Security(root);
        byte[] bytes = File.ReadAllBytes(metadata), signed = File.ReadAllBytes(signature);
        var manifest = offer.RollbackAuthorization is { } authorization
            ? UpdateSignature.VerifyForRollback(bytes, signed, UpdateTrustStore.FromEmbedded(), DateTimeOffset.UtcNow, security.HighestSequence, authorization, offer.CurrentBuildId, offer.CurrentVersion)
            : UpdateSignature.Verify(bytes, signed, UpdateTrustStore.FromEmbedded(), DateTimeOffset.UtcNow, security.HighestSequence);
        if (manifest.Sequence == security.HighestSequence && security.ManifestSha256.Length != 0 && security.ManifestSha256 != offer.ManifestSha256) throw new IOException("UPDATE_SEQUENCE_CONTENT_CONFLICT");
        if (security.FailedBuilds.Contains(manifest.BuildId, StringComparer.Ordinal)) throw new IOException("UPDATE_FAILED_BUILD_BLOCKED");
        var installed = UpdateFiles.ReadInstalled(root);
        if (installed.BuildId != offer.CurrentBuildId || installed.Version != offer.CurrentVersion) throw new IOException("UPDATE_CURRENT_BUILD_MISMATCH");
        if (!SemanticVersion.TryParse(manifest.Version, out var target) || !SemanticVersion.TryParse(installed.Version, out var current) ||
            (offer.RollbackAuthorization is null ? target!.CompareTo(current) <= 0 : target!.CompareTo(current) >= 0)) throw new IOException("UPDATE_DOWNGRADE_FORBIDDEN");
        if (!SemanticVersion.TryParse(manifest.MinimumUpdaterVersion, out var minimum) || !SemanticVersion.TryParse(UpdaterVersion, out var updater) || minimum!.CompareTo(updater) > 0) throw new IOException("UPDATE_UPDATER_TOO_OLD");
        if (GetCompatibility(root, manifest, offer.RollbackAuthorization is not null) is { } compatibilityError) throw new IOException(compatibilityError);
        if (offer.TargetIdentity is { } identity && (identity.Repository != manifest.Repository || identity.ReleaseId != manifest.Payload.ReleaseId ||
            identity.Version != manifest.Version || identity.TargetRid != manifest.TargetRid || identity.AssetId != manifest.Payload.AssetId ||
            identity.Asset != manifest.Payload.Asset || identity.Bytes != manifest.Payload.Bytes || identity.Sha256 != manifest.Payload.Sha256 || identity.ManifestSha256 != offer.ManifestSha256))
            throw new IOException("UPDATE_HANDOFF_TARGET_CHANGED");
        string policyPath = Path.Combine(UpdateFiles.DataRoot(root), "Updates", "state.json");
        UpdatePaths.EnsureNoReparsePoints(policyPath);
        if (File.Exists(policyPath))
        {
            if (new FileInfo(policyPath).Length > 128 * 1024) throw new IOException("UPDATE_POLICY_INVALID");
            using var policy = JsonDocument.Parse(File.ReadAllBytes(policyPath));
            if (manifest.Channel == "meta" && (!policy.RootElement.TryGetProperty("preview", out var enabled) || enabled.ValueKind != JsonValueKind.True)) throw new IOException("UPDATE_PREVIEW_DISABLED");
            if (policy.RootElement.TryGetProperty("retryAfterUtc", out var retry) && retry.ValueKind == JsonValueKind.String &&
                retry.TryGetDateTimeOffset(out var after) && after > DateTimeOffset.UtcNow) throw new IOException("UPDATE_RATE_LIMITED");
        }
        else if (manifest.Channel == "meta") throw new IOException("UPDATE_PREVIEW_DISABLED");
        UpdateApplySourceGuard.VerifyDefaultAsync(manifest, offer.TargetIdentity?.Tag ?? offer.RollbackAuthorization?.ReleaseTag ?? manifest.ReleaseTag ?? manifest.Channel + "-v" + manifest.Version,
            CancellationToken.None, offer.ManifestSha256, new StoreNetworkSettingsService(UpdateFiles.DataRoot(root)).Load()).GetAwaiter().GetResult();
        foreach (var file in manifest.Files) _ = UpdateFiles.Managed(root, file.Path);
        foreach (var path in manifest.RemoveFiles) _ = UpdateFiles.Managed(root, path);
        UpdatePayload.VerifyAsync(Path.Combine(tx, "payload.zip"), manifest, CancellationToken.None).GetAwaiter().GetResult();
        return manifest;
    }

    private static void Apply(string root, string tx, HandoffRecord offer, UpdateManifest manifest)
    {
        // Recheck after both the business lock and cross-session physical root lock exist.
        try { manifest = Verify(root, tx, offer); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Parent already exited. No bytes changed, so the independent entry must
            // reopen the intact original rather than leave an unexplained closed app.
            throw new IOException("UPDATE_APPLY_RECHECK_ABORTED", error);
        }
        ApplyFiles(root, tx, offer, manifest);
    }

    // A distinct private file-transaction boundary lets tests exercise this exact Windows
    // implementation with real ZIP bytes after the signature-verification boundary.
    private static void ApplyFiles(string root, string tx, HandoffRecord offer, UpdateManifest manifest)
    {
        var context = new ApplyContext();
        try { ApplyFilesCore(root, tx, offer, manifest, context); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (!context.Recorded) RecordApplyFailure(root, tx, offer.TransactionId, context, error);
            throw;
        }
    }

    private static void ApplyFilesCore(string root, string tx, HandoffRecord offer, UpdateManifest manifest, ApplyContext context)
    {
        var old = UpdateFiles.ReadInstalled(root);
        var next = manifest.Files.Select(f => new InstalledFile(f.Path, f.Bytes, f.Sha256)).ToArray();
        var oldMap = old.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var nextMap = next.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        // A historical signed inventory cannot name files first added by a later build.
        // Explicit rollback removes only currently receipt-owned bytes absent from the
        // exact authorized target; unknown files are never part of this derived set.
        var removals = offer.RollbackAuthorization is null
            ? new HashSet<string>(manifest.RemoveFiles, StringComparer.OrdinalIgnoreCase)
            : old.Files.Where(f => !nextMap.ContainsKey(f.Path)).Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in old.Files)
        {
            context.File = file.Path;
            if (!UpdateFiles.Matches(UpdateFiles.Managed(root, file.Path), file)) throw new IOException("UPDATE_MANAGED_FILE_CHANGED");
            if (!nextMap.ContainsKey(file.Path) && !removals.Contains(file.Path)) throw new IOException("UPDATE_REMOVAL_NOT_DECLARED");
        }
        foreach (var file in next)
            if (!oldMap.ContainsKey(file.Path) && File.Exists(UpdateFiles.Managed(root, file.Path))) throw new IOException("UPDATE_UNKNOWN_FILE_COLLISION");
        if (removals.Any(p => !oldMap.ContainsKey(p) || nextMap.ContainsKey(p))) throw new IOException("UPDATE_REMOVAL_INVALID");
        long needed = checked(old.Files.Sum(f => f.Bytes) + next.Sum(f => f.Bytes) * 2 + 64L * 1024 * 1024);
        if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < needed) throw new IOException("UPDATE_DISK_SPACE_INSUFFICIENT");
        string stage = Path.Combine(tx, "stage"), backup = Path.Combine(tx, "backup"), retired = Path.Combine(tx, "retired");
        context.Phase = "extract"; context.File = null;
        UpdatePayload.ExtractVerifiedAsync(Path.Combine(tx, "payload.zip"), stage, manifest, CancellationToken.None).GetAwaiter().GetResult();
        Directory.CreateDirectory(backup);
        foreach (var file in old.Files)
        {
            context.Phase = "backup"; context.File = file.Path;
            string destination = UpdateFiles.Managed(backup, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            using (var original = new FileStream(UpdateFiles.Managed(root, file.Path), FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var saved = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            { original.CopyTo(saved); saved.Flush(true); }
            if (!UpdateFiles.Matches(destination, file)) throw new IOException("UPDATE_BACKUP_INVALID");
        }
        // All supported host configuration is snapshotted after business exit, before
        // the first program replacement or possible new-client migration.
        context.Phase = "data-backup"; context.File = null;
        var dataSnapshot = UpdateDataSnapshot.Capture(root, offer.TransactionId, old);
        var journal = new UpdateJournal(1, offer.TransactionId, root, "backedUp", old.BuildId, manifest.BuildId, old, next,
            next.Select(f => f.Path).Concat(removals).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            DataSnapshotId: dataSnapshot.SnapshotId, PinAfterSuccess: offer.PinAfterSuccess, TargetVersion: manifest.Version, ManifestSha256: offer.ManifestSha256);
        context.Phase = "journal-backedUp"; context.File = null;
        UpdateFiles.Write(JournalPath(root), journal);
        var occupiedFiles = new List<FileStream>();
        try
        {
            // This also detects older/non-cooperating clients in another login session:
            // loaded images or external handles which disallow writing stop the whole
            // transaction before its first replacement. Keep the exact handles open.
            foreach (var file in old.Files)
            {
                context.Phase = "occupancy"; context.File = file.Path;
                occupiedFiles.Add(ProtectOwnedFile(UpdateFiles.Managed(root, file.Path), file));
            }
            context.Phase = "journal-applying"; context.File = null;
            journal = journal with { Phase = "applying" }; UpdateFiles.Write(JournalPath(root), journal);
            foreach (var file in next)
            {
                context.Phase = "replace"; context.File = file.Path;
                string source = UpdateFiles.Managed(stage, file.Path), target = UpdateFiles.Managed(root, file.Path);
                if (!UpdateFiles.Matches(source, file)) throw new IOException("UPDATE_STAGE_CHANGED");
                bool existingOwned = oldMap.ContainsKey(file.Path);
                Replace(source, target, file, existingOwned ? UpdateFiles.Managed(retired, file.Path) : null, existingOwned);
            }
            foreach (var path in removals) { context.Phase = "remove"; context.File = path; File.Delete(UpdateFiles.Managed(root, path)); }
            context.Phase = "receipt"; context.File = UpdateFiles.ReceiptName;
            UpdateFiles.Write(Path.Combine(root, UpdateFiles.ReceiptName), new InstalledBuild(1, manifest.Version, manifest.BuildId, next, manifest.Data.SchemaVersion));
            context.Phase = "journal-health"; context.File = null;
            UpdateFiles.Write(JournalPath(root), journal with { Phase = "health" });
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            RecordApplyFailure(root, tx, offer.TransactionId, context, error);
            foreach (var handle in occupiedFiles) handle.Dispose();
            if (journal.Phase == "backedUp")
            {
                UpdateFiles.Write(JournalPath(root), journal with { Phase = "aborted", ErrorCode = "UPDATE_FILES_BUSY" });
                throw new IOException("UPDATE_APPLY_ABORTED_FILES_BUSY", error);
            }
            RecordFailure(root, manifest.BuildId, manifest.Sequence, offer.ManifestSha256);
            Restore(root, "UPDATE_APPLY_FAILED");
            // Tell the independent entry to reopen the restored old version from its
            // original stable entry; a completed rollback must not leave a silent void.
            throw new IOException("UPDATE_ROLLED_BACK", error);
        }
        finally { foreach (var handle in occupiedFiles) handle.Dispose(); }
    }

    private static FileStream ProtectOwnedFile(string path, InstalledFile expected)
    {
        var handle = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Delete);
        try
        {
            // Re-read through the exact exclusive-content handle, after backup and
            // acquisition. Opening a second reader would be blocked by our own guard.
            if (handle.Length != expected.Bytes || Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(handle)) != expected.Sha256)
                throw new IOException("UPDATE_MANAGED_FILE_CHANGED");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static void Replace(string source, string target, InstalledFile expected, string? retiredPath = null, bool existingOwned = true)
    {
        UpdatePaths.EnsureNoReparsePoints(target);
        if (!existingOwned && (File.Exists(target) || Directory.Exists(target))) throw new IOException("UPDATE_UNKNOWN_FILE_COLLISION");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temp = target + ".aos-" + Guid.NewGuid().ToString("N") + ".tmp";
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
        { input.CopyTo(output); output.Flush(true); }
        if (!UpdateFiles.Matches(temp, expected)) throw new IOException("UPDATE_COPY_INVALID");
        UpdatePaths.EnsureNoReparsePoints(target);
        if (retiredPath is null) File.Move(temp, target, existingOwned);
        else
        {
            // MoveFileEx(REPLACE_EXISTING) fails with ACCESS_DENIED on Windows while
            // the old image's protective ReadWrite/FileShare.Delete handle is held.
            // Rename that exact old file into our private same-volume transaction first.
            // The durable applying journal and full verified backup cover the gap; the
            // stable Bootstrap never loads the incomplete business directory.
            UpdatePaths.EnsureNoReparsePoints(retiredPath);
            Directory.CreateDirectory(Path.GetDirectoryName(retiredPath)!);
            if (File.Exists(target)) File.Move(target, retiredPath, false);
            File.Move(temp, target, false);
        }
    }

    private static void RecordApplyFailure(string root, string tx, string id, ApplyContext context, Exception error)
    {
        var cause = error.GetBaseException();
        LastFailure = new(id, context.Phase, context.File, cause.GetType().Name, $"0x{cause.HResult:X8}", DateTimeOffset.UtcNow);
        context.Recorded = true;
        // No arbitrary exception message, absolute user path, command line or data bytes.
        try { UpdateFiles.Write(Path.Combine(tx, "failure.json"), LastFailure); }
        catch (Exception diagnosticError) when (diagnosticError is IOException or UnauthorizedAccessException) { }
    }

    private static void StartAndConfirm(string root, string id, UpdateManifest manifest, FileStream rootLease)
    {
        string tx = UpdateFiles.Transaction(root, id);
        using var self = Process.GetCurrentProcess();
        UpdateFiles.Write(Path.Combine(tx, "updater.json"), new UpdaterIdentity(self.Id, self.StartTime.ToUniversalTime().Ticks));
        using var pipe = UpdatePipe.Server(id, "health");
        var start = new ProcessStartInfo(Path.Combine(root, "AutumnOS.Client.exe")) { UseShellExecute = false, WorkingDirectory = root };
        start.ArgumentList.Add("--update-health"); start.ArgumentList.Add(id);
        using var child = Process.Start(start) ?? throw new IOException("UPDATE_HEALTH_START_FAILED");
        long childStart = child.StartTime.ToUniversalTime().Ticks;
        var journal = UpdateFiles.Read<UpdateJournal>(JournalPath(root)) with { ChildPid = child.Id, ChildStartUtcTicks = childStart };
        UpdateFiles.Write(JournalPath(root), journal);
        bool committed = false;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            pipe.WaitForConnectionAsync(timeout.Token).GetAwaiter().GetResult(); UpdatePipe.VerifyClient(pipe, child.Id);
            UpdatePipe.ReceiveAsync(pipe, id, "enter", timeout.Token).GetAwaiter().GetResult();
            UpdatePipe.SendAsync(pipe, new("authorized", id), timeout.Token).GetAwaiter().GetResult();
            var health = UpdatePipe.ReceiveAsync(pipe, id, "healthy", timeout.Token).GetAwaiter().GetResult();
            if (health.BuildId != manifest.BuildId || string.IsNullOrWhiteSpace(health.SourceId) || health.SourceId == "not_recorded" || child.HasExited)
                throw new IOException("UPDATE_HEALTH_BUILD_MISMATCH");
            foreach (var file in manifest.Files)
                if (!UpdateFiles.Matches(UpdateFiles.Managed(root, file.Path), new(file.Path, file.Bytes, file.Sha256))) throw new IOException("UPDATE_HEALTH_FILES_CHANGED");
            var state = Security(root);
            UpdateFiles.Write(Path.Combine(UpdateFiles.Work(root), "security.json"), state with { HighestSequence = Math.Max(state.HighestSequence, manifest.Sequence),
                ManifestSha256 = manifest.Sequence >= state.HighestSequence ? UpdateFiles.Hash(Path.Combine(tx, "autumn.update.json")) : state.ManifestSha256 });
            PortableEntryBinding.Commit(root, manifest.BuildId, manifest.Version);
            UpdateFiles.Write(JournalPath(root), journal with { Phase = "committed" });
            committed = true;
            rootLease.Dispose();
            UpdatePipe.SendAsync(pipe, new("commit", id), timeout.Token).GetAwaiter().GetResult();
            UpdatePipe.ReceiveAsync(pipe, id, "lease-acquired", timeout.Token).GetAwaiter().GetResult();
            UpdateFiles.Write(Path.Combine(tx, "result.json"), new { outcome = "committed", buildId = manifest.BuildId, version = manifest.Version, pinAfterSuccess = journal.PinAfterSuccess, coreHealthVerified = true });
            UpdatePipe.SendAsync(pipe, new("interactive", id), timeout.Token).GetAwaiter().GetResult();
            // Keep the server allocated until the client consumed the final grant and
            // disconnected; a transport failure here must never trigger process killing.
            byte[] end = new byte[1];
            using var drain = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            _ = pipe.ReadAsync(end, drain.Token).AsTask().GetAwaiter().GetResult();
        }
        finally
        {
            if (!committed && !child.HasExited && child.StartTime.ToUniversalTime().Ticks == childStart)
            {
                // This is our exact pre-interactive child, never a name lookup or an existing user's game.
                child.Kill(false);
                if (!child.WaitForExit(15000)) throw new IOException("UPDATE_HEALTH_CHILD_STILL_RUNNING");
            }
        }
    }

    public static void Recover(string root)
    {
        root = UpdateFiles.Root(root);
        using var maintenance = UpdateMutexLease.AcquireMaintenance();
        using var business = UpdateMutexLease.AcquireBusiness(TimeSpan.FromSeconds(15));
        using var rootLease = UpdateRootLease.AcquireExclusive(root);
        if (!NeedsRecovery(root)) return;
        var journal = UpdateFiles.Read<UpdateJournal>(JournalPath(root));
        if (journal.ChildPid != 0)
        {
            try
            {
                using var child = Process.GetProcessById(journal.ChildPid);
                if (!child.HasExited && child.StartTime.ToUniversalTime().Ticks == journal.ChildStartUtcTicks) throw new IOException("UPDATE_RECOVERY_WAIT_FOR_CLIENT_EXIT");
            }
            catch (ArgumentException) { }
        }
        RecordFailure(root, journal.ToBuildId, Security(root).HighestSequence);
        Restore(root, "UPDATE_INTERRUPTED");
    }

    private static void Restore(string root, string error)
    {
        var journal = UpdateFiles.Read<UpdateJournal>(JournalPath(root));
        if (journal.SchemaVersion != 1 || journal.Root != root || journal.Previous.BuildId != journal.FromBuildId) throw new IOException("UPDATE_RECOVERY_JOURNAL_INVALID");
        UpdateFiles.ValidateInstalled(root, journal.Previous);
        UpdateFiles.ValidateInstalled(root, new InstalledBuild(1, "recovery-target", journal.ToBuildId, journal.NextFiles));
        var touched = journal.NextFiles.Select(f => f.Path).Concat(journal.Previous.Files.Select(f => f.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (journal.Touched.Any(p => !touched.Contains(p)) || journal.Touched.Length > 40000) throw new IOException("UPDATE_RECOVERY_JOURNAL_INVALID");
        string backup = Path.Combine(UpdateFiles.Transaction(root, journal.TransactionId), "backup");
        var previous = journal.Previous.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in journal.Previous.Files)
            if (!UpdateFiles.Matches(UpdateFiles.Managed(backup, file.Path), file)) throw new IOException("UPDATE_RECOVERY_BACKUP_INVALID");
        if (journal.DataSnapshotId is { } snapshotId) _ = UpdateDataSnapshot.Validate(root, snapshotId, journal.Previous);
        UpdateFiles.Write(JournalPath(root), journal with { Phase = "rollingBack", ErrorCode = error });
        foreach (var file in journal.Previous.Files) Replace(UpdateFiles.Managed(backup, file.Path), UpdateFiles.Managed(root, file.Path), file);
        foreach (var file in journal.NextFiles.Where(f => !previous.ContainsKey(f.Path)))
        {
            string path = UpdateFiles.Managed(root, file.Path);
            if (File.Exists(path))
            {
                if (!UpdateFiles.Matches(path, file)) throw new IOException("UPDATE_RECOVERY_UNKNOWN_FILE_PRESERVED");
                File.Delete(path);
            }
        }
        UpdateFiles.Write(Path.Combine(root, UpdateFiles.ReceiptName), journal.Previous);
        if (journal.DataSnapshotId is { } restoreId) UpdateDataSnapshot.Restore(root, restoreId, journal.Previous);
        PortableEntryBinding.Commit(root, journal.Previous.BuildId, journal.Previous.Version);
        UpdateFiles.Write(JournalPath(root), journal with { Phase = "rolledBack", ErrorCode = error });
    }
    private static void RecordFailure(string root, string buildId, long sequence, string? manifestSha256 = null)
    {
        var state = Security(root);
        UpdateFiles.Write(Path.Combine(UpdateFiles.Work(root), "security.json"), state with { HighestSequence = Math.Max(state.HighestSequence, sequence),
            FailedBuilds = state.FailedBuilds.Append(buildId).Distinct(StringComparer.Ordinal).ToArray(),
            ManifestSha256 = sequence >= state.HighestSequence ? manifestSha256 ?? state.ManifestSha256 : state.ManifestSha256 });
    }
}
