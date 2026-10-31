using System.Text.Json;
using AutumnOS.Contracts;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private readonly DispatcherTimer shutdownTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly HashSet<WebAppHost> shutdownHosts = [];
    private readonly HashSet<string> shutdownCompletedSteps = [];
    private readonly Guid shutdownId = Guid.NewGuid();
    private Task? shutdownDownloadsDrain, shutdownUpdatesDrain, shutdownIdentityDrain;
    private bool shutdownRequested, shutdownReady, shutdownTickRunning;
    private int shutdownClearObservations;
    private string? lastShutdownAudit, lastShutdownWait;

    private void InitializeGracefulShutdown()
    {
        AppWindow.Closing += OnNormalWindowClosing;
        shutdownTimer.Tick += (_, _) => DrainNormalShutdown();
        Closed += (_, _) => FinalizeClosedWindow();
    }

    private void OnNormalWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (shutdownReady) return;
        args.Cancel = true;
        shutdownRequested = true;
        // No timeout converts unknown cleanup state into success. Keep the message
        // loop, launcher mutex and installation lease until the native drain completes.
        shutdownTimer.Start();
        DrainNormalShutdown();
    }

    private void CaptureShutdownHosts()
    {
        if (appHost is { } sample) shutdownHosts.Add(sample);
        if (developerPreviewHost is { } preview) shutdownHosts.Add(preview);
        foreach (var app in managedApplications.Values) shutdownHosts.Add(app.Host);
        foreach (WebAppHost pending in pendingResourceCleanup) shutdownHosts.Add(pending);
    }

    private void DrainNormalShutdown()
    {
        if (!shutdownRequested || shutdownReady || shutdownTickRunning) return;
        shutdownTickRunning = true;
        bool failed = false;
        void Step(string key, Action action)
        {
            if (shutdownCompletedSteps.Contains(key)) return;
            try { action(); shutdownCompletedSteps.Add(key); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { failed = true; ReportShutdownWait(key, error.GetType().Name); }
        }
        bool Drained(Task? task, string key)
        {
            if (task is null || !task.IsCompleted) return false;
            if (task.IsCompletedSuccessfully) return true;
            // Observe the exception; do not retry an opaque failed disposal and
            // mistake an already-disposed no-op for successful resource release.
            ReportShutdownWait(key, task.Exception?.GetBaseException().GetType().Name ?? "Canceled");
            return false;
        }
        try
        {
            // Stop admission before invoking callbacks, cancellation, or native COM.
            Step("admission", criticalOperations.BeginShutdownAdmission);
            if (!shutdownCompletedSteps.Contains("admission")) return;
            Step("recall", () => Program.Launcher?.BeginStop());
            try { InteractionRoot.IsEnabled = false; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { failed = true; ReportShutdownWait("input", error.GetType().Name); }
            CaptureShutdownHosts();
            foreach (WebAppHost host in shutdownHosts.ToArray())
            {
                try { host.Dispose(); }
                catch (Exception error) when (error is not OutOfMemoryException)
                { failed = true; ReportShutdownWait("host_cleanup", error.GetType().Name); }
            }
            // Host.Dispose invalidates its epoch first. Only then cancel native UI
            // operations, preventing a late selection from committing into old scope.
            Step("lifetime", () => lifetime.Cancel());
            CancelOwnedUiOperations();
            Step("developer_pipe", StopDeveloperPipe);
            Step("update_operation", () => updateOperation?.Cancel());
            Step("store_query", () => storeQuery?.Cancel());
            Step("store_debounce", () => storeDebounce.Stop());
            Step("download_timer", () => downloadUiTimer.Stop());
            Step("first_run_account", () => firstRunAccount?.Dispose());
            Step("identity_begin", () => shutdownIdentityDrain = identityService?.DisposeAsync().AsTask() ?? Task.CompletedTask);
            Step("downloads_begin", () => shutdownDownloadsDrain = storeDownloads?.DisposeAsync().AsTask() ?? Task.CompletedTask);
            Step("updates_begin", () => shutdownUpdatesDrain = updates?.DisposeAsync().AsTask() ?? Task.CompletedTask);
            bool downloadsStopped = Drained(shutdownDownloadsDrain, "downloads_drain");
            foreach (Task retired in retiredDownloadDrains) downloadsStopped &= Drained(retired, "retired_downloads_drain");
            bool updatesStopped = Drained(shutdownUpdatesDrain, "updates_drain");
            bool identityStopped = Drained(shutdownIdentityDrain, "identity_drain");
            if (downloadsStopped && ownedNetworkTasks.Count == 0)
            {
                Step("github_transport", () => githubTransport?.Dispose());
                Step("store_test_source", () => storeTestSource?.Dispose());
            }

            CaptureShutdownHosts();
            bool workStillUnwinding = aboutOpen || preparing || launching || accountOperation || firstRunLoginPending || firstRunSkipPending ||
                developerBusy || supportExportBusy || localInstallBusy || installingStoreTask || updateBusy || updateCommitBusy;
            workStillUnwinding |= ownedNetworkTasks.Count != 0 || developerPipeTask is { IsCompleted: false };
            if (developerPipeTask is { IsFaulted: true }) _ = developerPipeTask.Exception;
            int hostsPending = shutdownHosts.Count(host => !host.ResourcesReleased);
            var modal = OwnedModalWindows.Observe(WinRT.Interop.WindowNative.GetWindowHandle(this));
            bool clear = !failed && !workStillUnwinding && hostsPending == 0 && ownedDialogs.Count == 0 && ownedPickers.Count == 0 &&
                modal.Complete && modal.Count == 0 && downloadsStopped && updatesStopped && identityStopped &&
                criticalOperations.ActiveGames == 0 && criticalOperations.ActiveWrites == 0 &&
                !criticalOperations.IsPreparing && !criticalOperations.IsMaintenance;
            WriteShutdownAudit(clear ? "drain_clear" : "drain_pending", hostsPending, modal.Complete, modal.Count,
                downloadsStopped, updatesStopped, identityStopped, workStillUnwinding, failed);
            // A second dispatcher turn observes native dialogs after async completion
            // and lets canceled UI continuations unwind; it is not a timeout escape.
            shutdownClearObservations = clear ? shutdownClearObservations + 1 : 0;
            if (shutdownClearObservations < 2) { ReportShutdownWait("resources_pending", null); return; }
            shutdownReady = true;
            shutdownTimer.Stop();
            WriteShutdownAudit("shutdown_ready", 0, true, 0, true, true, true, false, false);
            try { Close(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                shutdownReady = false; shutdownClearObservations = 0; shutdownTimer.Start();
                ReportShutdownWait("window_close", error.GetType().Name);
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { shutdownClearObservations = 0; ReportShutdownWait("drain_retry", error.GetType().Name); }
        finally { shutdownTickRunning = false; }
    }

    private void ReportShutdownWait(string stage, string? type)
    {
        if (!shutdownRequested) return;
        try { Title = BrandInfo.ProductName + " · 正在退出，等待应用资源释放"; }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        try { RuntimeStatus.Text = "正在退出，正在结束应用和后台任务。请稍候。"; }
        catch (Exception error) when (error is not OutOfMemoryException) { }
        if (type is null) return;
        try
        {
            string key = stage + ":" + type;
            if (key == lastShutdownWait) return;
            string directory = installationRoot.Directories["Logs"];
            if (!Directory.Exists(directory)) return;
            string line = JsonSerializer.Serialize(new { timestampUtc = DateTimeOffset.UtcNow, buildId = BrandInfo.BuildId,
                shutdownId, processId = Environment.ProcessId, facts = new { stage = "cleanup_wait", reason = stage, failureType = type } });
            File.AppendAllText(Path.Combine(directory, "shutdown-events.jsonl"), line + Environment.NewLine);
            lastShutdownWait = key;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    private void WriteShutdownAudit(string stage, int hostsPending, bool modalObservationComplete, int modalCount,
        bool downloadsStopped, bool updatesStopped, bool identityStopped, bool workPending, bool cleanupFailed)
    {
        try
        {
            var facts = new { stage, hosts = shutdownHosts.Count, hostsPending, dialogs = ownedDialogs.Count, pickers = ownedPickers.Count,
                networkTasks = ownedNetworkTasks.Count, developerPipePending = developerPipeTask is { IsCompleted: false },
                modalObservationComplete, modalCount, downloadsStopped, updatesStopped, identityStopped, workPending, cleanupFailed,
                activeWrites = criticalOperations.ActiveWrites, activeGames = criticalOperations.ActiveGames,
                isPreparing = criticalOperations.IsPreparing, isMaintenance = criticalOperations.IsMaintenance,
                admissionClosed = criticalOperations.IsShuttingDown, launcherStopping = Program.Launcher?.IsStopping == true };
            string fingerprint = JsonSerializer.Serialize(facts);
            if (fingerprint == lastShutdownAudit) return;
            string directory = installationRoot.Directories["Logs"];
            if (!Directory.Exists(directory)) return;
            string line = JsonSerializer.Serialize(new { timestampUtc = DateTimeOffset.UtcNow, buildId = BrandInfo.BuildId,
                shutdownId, processId = Environment.ProcessId, facts });
            File.AppendAllText(Path.Combine(directory, "shutdown-events.jsonl"), line + Environment.NewLine);
            lastShutdownAudit = fingerprint;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { ReportShutdownWait("audit_unavailable", error.GetType().Name); }
    }

    private void FinalizeClosedWindow()
    {
        // Normal close reaches here only after drain. Health/update Application.Exit
        // uses its separate admission protocol. One failing service must not skip others.
        void Release(Action action)
        { try { action(); } catch (Exception error) when (error is not OutOfMemoryException) { } }
        Release(() => shutdownTimer.Stop());
        Release(() => Program.Launcher?.BeginStop());
        Release(CloseUpdateServices);
        Release(HideRunningOperations);
        Release(() => clockTimer.Stop());
        Release(() => systemUiSettings.AnimationsEnabledChanged -= OnSystemAnimationsChanged);
        foreach (DispatcherTimer timer in pressTimers) Release(() => timer.Stop());
        Release(() => lifetime.Cancel());
        Release(CancelOwnedUiOperations);
        Release(() => firstRunAccount?.Dispose());
        Release(CloseStoreServices);
        Release(CloseDeveloperCapabilities);
        Release(() => appHost?.Dispose());
        foreach (var broker in fileBrokers.Values) Release(broker.Dispose);
        fileBrokers.Clear();
        Release(() => identityService?.Dispose());
    }
}
