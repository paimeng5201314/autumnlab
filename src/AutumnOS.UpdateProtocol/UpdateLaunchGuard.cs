using System.Diagnostics;
using System.IO.Pipes;
using AutumnOS.Update;

namespace AutumnOS.UpdateProtocol;

public sealed class UpdateLaunchGuard : IDisposable
{
    private static UpdateLaunchGuard? current;
    private FileStream? rootLease;
    private readonly string root;
    private NamedPipeClientStream? pipe;
    private string? transaction;
    private UpdateLaunchGuard(string root) { this.root = root; }
    public static bool IsHealthStart => current?.transaction is not null;
    public static UpdateLaunchGuard Enter(string installRoot, string[] args)
    {
        string root = UpdateFiles.Root(installRoot);
        var guard = new UpdateLaunchGuard(root);
        try
        {
            if (args.Length == 2 && args[0] == "--update-health" && Guid.TryParseExact(args[1], "N", out _))
            {
                string id = args[1];
                var journal = UpdateFiles.Read<UpdateJournal>(Path.Combine(UpdateFiles.Work(root), "journal.json"));
                var updater = UpdateFiles.Read<UpdaterIdentity>(Path.Combine(UpdateFiles.Transaction(root, id), "updater.json"));
                if (journal.TransactionId != id || journal.Root != root || journal.Phase != "health") throw new IOException("UPDATE_HEALTH_UNAUTHORIZED");
                using var process = Process.GetProcessById(updater.Pid);
                if (process.StartTime.ToUniversalTime().Ticks != updater.StartUtcTicks || !string.Equals(process.MainModule?.FileName, Path.Combine(root, "AutumnOS.Updater.exe"), StringComparison.OrdinalIgnoreCase)) throw new IOException("UPDATE_HEALTH_UNAUTHORIZED");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                guard.pipe = UpdatePipe.ConnectAsync(id, "health", updater.Pid, timeout.Token).GetAwaiter().GetResult();
                UpdatePipe.SendAsync(guard.pipe, new("enter", id), timeout.Token).GetAwaiter().GetResult();
                UpdatePipe.ReceiveAsync(guard.pipe, id, "authorized", timeout.Token).GetAwaiter().GetResult();
                guard.transaction = id;
            }
            else
            {
                if (args.Length != 0 || UpdateMutexLease.MaintenanceActive()) throw new IOException("UPDATE_MAINTENANCE_ACTIVE");
                string journalPath = Path.Combine(UpdateFiles.Work(root), "journal.json");
                if (File.Exists(journalPath) && UpdateFiles.Read<UpdateJournal>(journalPath).Phase is not ("committed" or "rolledBack" or "aborted")) throw new IOException("UPDATE_RECOVERY_REQUIRED");
                guard.rootLease = UpdateRootLease.AcquireShared(root);
                if (UpdateMutexLease.MaintenanceActive()) throw new IOException("UPDATE_MAINTENANCE_ACTIVE");
            }
            current = guard; return guard;
        }
        catch { guard.Dispose(); throw; }
    }
    internal static async Task ReportAsync(string buildId, string sourceId, CancellationToken cancellationToken)
    {
        var guard = current;
        if (guard?.transaction is not { } id || guard.pipe is null) return;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); budget.CancelAfter(TimeSpan.FromSeconds(90));
        await UpdatePipe.SendAsync(guard.pipe, new("healthy", id, buildId, sourceId), budget.Token).ConfigureAwait(false);
        await UpdatePipe.ReceiveAsync(guard.pipe, id, "commit", budget.Token).ConfigureAwait(false);
        guard.rootLease = UpdateRootLease.AcquireShared(guard.root);
        await UpdatePipe.SendAsync(guard.pipe, new("lease-acquired", id), budget.Token).ConfigureAwait(false);
        await UpdatePipe.ReceiveAsync(guard.pipe, id, "interactive", budget.Token).ConfigureAwait(false);
        guard.pipe.Dispose(); guard.pipe = null; guard.transaction = null;
    }
    public void Dispose() { rootLease?.Dispose(); pipe?.Dispose(); if (current == this) current = null; }
}

public static class UpdateHealth
{
    public static Task ReportCoreReadyAsync(string buildId, string sourceSnapshotId, CancellationToken cancellationToken = default) => UpdateLaunchGuard.ReportAsync(buildId, sourceSnapshotId, cancellationToken);
}
public sealed record UpdaterIdentity(int Pid, long StartUtcTicks);
