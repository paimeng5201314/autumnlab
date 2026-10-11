using AutumnOS.Contracts;
using AutumnOS.Packages;
using AutumnOS.UpdateProtocol;
using Microsoft.UI.Xaml;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private IDisposable? committedMaintenanceLease;
    private bool SystemUpdateUiIsIdle => !aboutOpen && !preparing && !launching && !accountOperation && !firstRunLoginPending && !firstRunSkipPending && !developerBusy &&
        !supportExportBusy && !localInstallBusy && !installingStoreTask &&
        (DesktopPanel.Visibility == Visibility.Visible || SettingsPanel.Visibility == Visibility.Visible && selectedSettingsCategory == "updates");

    private async Task ConfirmUpdateHealthAsync()
    {
        if (!UpdateLaunchGuard.IsHealthStart) return;
        InteractionRoot.IsEnabled = false;
        try
        {
            if (!configuration!.IsValid || !preferencesWritable || !desktopLayoutWritable || applicationInstaller is null ||
                stateStore!.Load().Success != true || permissionService is null) throw new IOException("UPDATE_CORE_INITIALIZATION_FAILED");
            foreach (var app in applicationInstaller.GetInstalled()) ApplicationInstallService.VerifyInstalled(app.Package);
            // Ordinary startup already validated PRI/XBF and opened configuration/storage/registry.
            // Network success and interactive authentication are deliberately not health prerequisites.
#if AUTUMNOS_TEST_HEALTH_FAILURE
            throw new IOException("UPDATE_TEST_CORE_INITIALIZATION_FAILURE");
#else
            await UpdateHealth.ReportCoreReadyAsync(BrandInfo.BuildId, BrandInfo.SourceSnapshotId, lifetime.Token);
            InteractionRoot.IsEnabled = true;
#endif
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Application.Current.Exit(); // Only this pre-health instance; updater owns bounded recovery.
        }
    }

    private async Task CommitSystemUpdateAsync()
    {
        if (updates is null || updateCommitBusy) return;
        if (!SystemUpdateUiIsIdle || criticalOperations.ActiveGames != 0 || criticalOperations.ActiveWrites != 0)
        {
            updateWait.Text = "请先保存并正常退出运行中的游戏或应用，等待当前操作结束后，再选择“重启并安装”。";
            return;
        }
        updateCommitBusy = true;
        RenderUpdateState();
        IDisposable? lease = null;
        try
        {
            updateWait.Text = "正在重新核对目标是否仍可用，以及当前安装策略…";
            updates.SetPreviewEnabled(previewUpdates.IsOn);
            await updates.ValidateStagedForCommitAsync(lifetime.Token);
            updateWait.Text = "正在取得维护资格，等待已开始的关键操作结束…";
            lease = await criticalOperations.PrepareMaintenanceAsync(TimeSpan.FromSeconds(30), lifetime.Token);
            var staged = updates.ValidateStagedForCommit();
            if (criticalOperations.ActiveGames != 0 || criticalOperations.ActiveWrites != 0 || !SystemUpdateUiIsIdle) throw new IOException("UPDATE_NOT_IDLE");
            InteractionRoot.IsEnabled = false;
            updateWait.Text = "交给独立更新器重新验证。进入安全退出阶段，此时不能取消。";
            await UpdaterHandoff.PrepareAsync(installationRoot.ProgramDirectory, staged.ManifestPath, staged.SignaturePath, staged.PayloadPath,
                BrandInfo.BuildId, BrandInfo.Version, lifetime.Token, staged.RollbackAuthorization, staged.Consent?.PinAfterSuccess == true, staged.Consent?.Target);
            committedMaintenanceLease = lease; lease = null;
            Program.Launcher?.BeginStop();
            Application.Current.Exit();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            updateWait.Text = SafeUpdateError(error) + " · 更新尚未提交，当前版本与数据保留。";
            InteractionRoot.IsEnabled = true;
        }
        finally { lease?.Dispose(); updateCommitBusy = false; RenderUpdateState(); }
    }
}
