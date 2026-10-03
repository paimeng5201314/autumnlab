using System.Diagnostics;
using AutumnOS.Update;

namespace AutumnOS.UpdateProtocol;

public static class UpdaterHandoff
{
    /// <summary>Call only while holding the in-process exclusive maintenance lease. On success immediately exit normally.</summary>
    public static async Task<string> PrepareAsync(string root, string manifestPath, string signaturePath, string payloadPath,
        string currentBuildId, string currentVersion, CancellationToken cancellationToken = default)
    {
        root = UpdateFiles.Root(root);
        var installed = UpdateFiles.ReadInstalled(root);
        if (installed.BuildId != currentBuildId || installed.Version != currentVersion) throw new IOException("UPDATE_CURRENT_BUILD_MISMATCH");
        string work = UpdateFiles.Work(root), journalPath = Path.Combine(work, "journal.json");
        if (File.Exists(journalPath))
        {
            var journal = UpdateFiles.Read<UpdateJournal>(journalPath);
            if (journal.Phase is not ("committed" or "rolledBack" or "aborted")) throw new IOException("UPDATE_RECOVERY_REQUIRED");
        }
        UpdateFiles.PrivateDirectory(work);
        string id = Guid.NewGuid().ToString("N"), tx = UpdateFiles.Transaction(root, id);
        UpdateFiles.PrivateDirectory(tx);
        foreach (var pair in new[] { (manifestPath, "autumn.update.json"), (signaturePath, "autumn.update.sig"), (payloadPath, "payload.zip") })
        {
            UpdatePaths.EnsureNoReparsePoints(pair.Item1);
            File.Copy(pair.Item1, Path.Combine(tx, pair.Item2), false);
        }
        using var self = Process.GetCurrentProcess();
        var record = new HandoffRecord(1, id, root, UpdateFiles.DataRoot(root), self.Id, self.StartTime.ToUniversalTime().Ticks,
            currentBuildId, currentVersion, UpdateFiles.Hash(Path.Combine(tx, "autumn.update.json")), DateTimeOffset.UtcNow.AddMinutes(3));
        UpdateFiles.Write(Path.Combine(tx, "handoff.json"), record);
        using var pipe = UpdatePipe.Server(id, "handoff");
        string updaterPath = Path.Combine(root, "AutumnOS.Updater.exe");
        UpdatePaths.EnsureNoReparsePoints(updaterPath);
        var start = new ProcessStartInfo(updaterPath) { UseShellExecute = false, WorkingDirectory = root, CreateNoWindow = true };
        start.ArgumentList.Add("--handoff"); start.ArgumentList.Add(id);
        using var updater = Process.Start(start) ?? throw new IOException("UPDATE_UPDATER_START_FAILED");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); budget.CancelAfter(TimeSpan.FromSeconds(45));
        await pipe.WaitForConnectionAsync(budget.Token).ConfigureAwait(false);
        UpdatePipe.VerifyClient(pipe, updater.Id);
        var hello = await UpdatePipe.ReceiveAsync(pipe, id, "offer", budget.Token).ConfigureAwait(false);
        if (hello.Digest != record.ManifestSha256) throw new IOException("UPDATE_HANDOFF_DIGEST_INVALID");
        await UpdatePipe.SendAsync(pipe, new("authorize", id, currentBuildId, Digest: record.ManifestSha256), budget.Token).ConfigureAwait(false);
        await UpdatePipe.ReceiveAsync(pipe, id, "exit-ready", budget.Token).ConfigureAwait(false);
        // Wait for an explicit final ACK so the updater knows this response was consumed.
        await UpdatePipe.SendAsync(pipe, new("exiting", id), budget.Token).ConfigureAwait(false);
        return id;
    }
}
