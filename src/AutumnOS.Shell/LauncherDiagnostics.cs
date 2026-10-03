using AutumnOS.Contracts;
using AutumnOS.Launcher;
using System.Text.Json;
using System.Text;

namespace AutumnOS.Shell;

internal static class LauncherDiagnostics
{
    private static readonly object gate = new();
    private static readonly Queue<LauncherEvent> pending = new();
    private static string? log;
    internal static void Attach(LauncherCoordinator launcher)
    {
        launcher.Observed += Record;
        Record(new("primary_started", 0, default));
    }
    internal static void UseValidatedDirectory(string logsDirectory)
    {
        lock (gate)
        {
            log = Path.Combine(logsDirectory, "launcher-events.jsonl");
            while (pending.TryDequeue(out var item)) Write(item);
        }
    }
    private static void Record(LauncherEvent item)
    {
        lock (gate)
        {
            if (log is null) { if (pending.Count < 64) pending.Enqueue(item); }
            else Write(item);
        }
    }
    private static void Write(LauncherEvent item)
    {
        try
        {
            if (log is null) return;
            foreach (string directory in new[] { AppContext.BaseDirectory, AutumnOS.Storage.InstallationRoot.ForCurrentProcess().DataDirectory, Path.GetDirectoryName(log)! })
            {
                var attributes = File.GetAttributes(directory);
                if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0) return;
            }
            try { if ((File.GetAttributes(log) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return; }
            catch (FileNotFoundException) { }
            string line = JsonSerializer.Serialize(new
            {
                protocol = 1, timestamp = DateTimeOffset.UtcNow, eventName = item.EventName,
                primaryPid = Environment.ProcessId, requestPid = item.RequestPid, windowHandle = item.Result.WindowHandle,
                outcome = item.Result.Outcome switch { RecallOutcome.Foreground => "foreground", RecallOutcome.AttentionRequested => "attention_requested", RecallOutcome.Closing => "closing", RecallOutcome.Timeout => "timeout", RecallOutcome.Rejected => "rejected", _ => "none" },
                buildId = BrandInfo.BuildId, version = BrandInfo.Version, sourceSnapshotId = BrandInfo.SourceSnapshotId
            }) + Environment.NewLine;
            if ((File.Exists(log) ? new FileInfo(log).Length : 0) + Encoding.UTF8.GetByteCount(line) > 1024 * 1024) return;
            File.AppendAllText(log, line);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
