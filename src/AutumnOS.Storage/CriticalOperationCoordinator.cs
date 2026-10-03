using System.Diagnostics;
using AutumnOS.Contracts;

namespace AutumnOS.Storage;

public sealed record MaintenanceSnapshot(bool IsPreparing, bool IsMaintenance, int ActiveWrites, int ActiveGames,
    IReadOnlyList<string> WaitingReasons)
{
    public bool IsIdle => !IsPreparing && !IsMaintenance && ActiveWrites == 0 && ActiveGames == 0;
}

/// <summary>
/// One host-owned admission gate for game lifetimes and real critical operations. Leases count lifetime,
/// not thread ownership. This gate does not replace the cross-process launcher/update handoff protocol.
/// </summary>
public sealed class CriticalOperationCoordinator
{
    private readonly object sync = new();
    private readonly Dictionary<Guid, string> writes = [];
    private readonly Dictionary<Guid, GameOperation> games = [];
    private TaskCompletionSource changed = NewSignal();
    private bool preparing;
    private bool maintenance;
    public int ActiveWrites { get { lock (sync) return writes.Count; } }
    public int ActiveGames { get { lock (sync) return games.Count; } }
    public bool IsMaintenance { get { lock (sync) return maintenance; } }
    public bool IsPreparing { get { lock (sync) return preparing; } }

    public IDisposable EnterWrite(string reason = "数据写入")
    {
        ValidateLabel(reason);
        lock (sync)
        {
            RequireAdmission();
            Guid id = Guid.NewGuid();
            writes.Add(id, reason);
            Signal();
            return new Lease(() => { lock (sync) { writes.Remove(id); Signal(); } });
        }
    }

    /// <summary>
    /// Acquire before Starting/creating a WebView. Release only after WebView, streams, SDK capabilities
    /// and other runtime resources have actually been released. A Closed/Crashed label alone never releases it.
    /// The optional snapshot is diagnostic only, and is never called with this coordinator's gate held.
    /// </summary>
    public IDisposable EnterGame(string appId, Func<AppInstance?>? snapshot = null)
    {
        ValidateLabel(appId);
        lock (sync)
        {
            RequireAdmission();
            Guid id = Guid.NewGuid();
            games.Add(id, new(appId, snapshot));
            Signal();
            return new Lease(() => { lock (sync) { games.Remove(id); Signal(); } });
        }
    }

    public MaintenanceSnapshot GetSnapshot()
    {
        string[] operations;
        GameOperation[] running;
        bool isPreparing, isMaintenance;
        lock (sync)
        {
            operations = writes.Values.ToArray(); running = games.Values.ToArray();
            isPreparing = preparing; isMaintenance = maintenance;
        }
        var reasons = operations.Select(reason => "正在完成：" + reason).ToList();
        foreach (GameOperation game in running)
        {
            string state = "资源尚未释放";
            try
            {
                if (game.Snapshot?.Invoke() is { } app)
                    state = app.State switch
                    {
                        AppLifecycleState.Starting => "正在启动",
                        AppLifecycleState.Foreground => "前台运行",
                        AppLifecycleState.Background => "后台运行",
                        AppLifecycleState.Suspended => "已挂起",
                        AppLifecycleState.Closing => "正在结束，等待资源释放",
                        _ => "等待资源释放"
                    };
            }
            catch (Exception) { /* Diagnostic failure cannot release the actual game lease. */ }
            reasons.Add($"游戏 {game.AppId}：{state}");
        }
        if (isMaintenance) reasons.Add("系统维护已取得排他资格");
        else if (isPreparing) reasons.Add("正在准备维护，暂不接受新的游戏或关键操作");
        return new(isPreparing, isMaintenance, operations.Length, running.Length, reasons.AsReadOnly());
    }

    /// <summary>Immediate atomic attempt; games still releasing resources also prevent entry.</summary>
    public IDisposable? TryEnterMaintenance()
    {
        lock (sync)
        {
            if (writes.Count != 0 || games.Count != 0 || preparing || maintenance) return null;
            return AcquireMaintenance();
        }
    }

    /// <summary>
    /// Atomically closes admission, waits for admitted critical work, and rechecks before exclusive entry.
    /// Live games defer without closing admission, so their user-initiated saves can still complete.
    /// Cancellation/timeout reopens admission; neither cancels work already holding a lease.
    /// </summary>
    public async Task<IDisposable> PrepareMaintenanceAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(timeout));
        long started = Stopwatch.GetTimestamp();
        lock (sync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (maintenance || preparing) throw new DataStoreException("MAINTENANCE_IN_PROGRESS");
            if (games.Count != 0) throw new DataStoreException("MAINTENANCE_GAME_ACTIVE");
            preparing = true;
            Signal();
        }
        bool acquired = false;
        try
        {
            while (true)
            {
                Task pending;
                lock (sync)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (writes.Count == 0 && games.Count == 0)
                    {
                        IDisposable lease = AcquireMaintenance();
                        preparing = false;
                        acquired = true;
                        return lease;
                    }
                    pending = changed.Task;
                }
                TimeSpan remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("MAINTENANCE_WAIT_TIMEOUT");
                await pending.WaitAsync(remaining, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (!acquired) lock (sync) { preparing = false; Signal(); }
        }
    }

    private IDisposable AcquireMaintenance()
    {
        maintenance = true;
        Signal();
        return new Lease(() => { lock (sync) { maintenance = false; Signal(); } });
    }
    private void RequireAdmission()
    {
        if (preparing || maintenance) throw new DataStoreException("MAINTENANCE_IN_PROGRESS");
    }
    private static void ValidateLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label) || label.Length > 160 || label.Any(char.IsControl))
            throw new ArgumentException("A bounded non-sensitive operation label is required.", nameof(label));
    }
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Signal()
    {
        TaskCompletionSource previous = changed;
        changed = NewSignal();
        previous.TrySetResult();
    }
    private sealed record GameOperation(string AppId, Func<AppInstance?>? Snapshot);
    internal sealed class Lease(Action release) : IDisposable
    {
        private Action? current = release;
        public void Dispose() => Interlocked.Exchange(ref current, null)?.Invoke();
    }
}
