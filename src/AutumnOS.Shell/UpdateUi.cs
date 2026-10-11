using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Storage;
using AutumnOS.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private UpdateService? updates;
    private readonly ToggleSwitch previewUpdates = new() { Header = "接受预览版邀请", IsOn = false };
    private readonly TextBlock updateIdentity = Paragraph("");
    private readonly TextBlock updateStatus = Paragraph("尚未检查");
    private readonly TextBlock updateCandidate = Paragraph("");
    private readonly TextBlock updatePin = Paragraph("");
    private readonly TextBlock updateWait = Paragraph("");
    private readonly ProgressBar updateProgress = new() { Minimum = 0, Maximum = 100 };
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Button? checkUpdateButton, downloadUpdateButton, restartUpdateButton, cancelUpdateButton;
    private Button? selectUpgradeButton, selectRollbackButton, restoreUpdateChannelButton;
    private CancellationTokenSource? updateOperation;
    private ContentDialog? activeUpdateDialog;
    private bool loadingUpdateSettings, updateBusy, updateCommitBusy, updateDialogOpen, startupUpdateCheckFinished;
    private string installedUpdateChannel = "渠道未验证（工程版本）";
    private bool installedUpdateChannelVerified;
    private DateTimeOffset nextInstalledChannelCheck;
    private readonly HashSet<WebAppHost> pendingResourceCleanup = [];
    private void RetainUnreleasedHost(WebAppHost host) => pendingResourceCleanup.Add(host);

    private void InitializeUpdateUi()
    {
        AutomationProperties.SetAutomationId(previewUpdates, "PreviewUpdates");
        AutomationProperties.SetAutomationId(updateStatus, "UpdateStatus");
        AutomationProperties.SetAutomationId(updateCandidate, "UpdateCandidate");
        AutomationProperties.SetAutomationId(updateWait, "UpdateWaitingReason");
        AutomationProperties.SetAutomationId(updatePin, "UpdatePinnedVersion");
        UpdateContent.Children.Add(new TextBlock { Text = "autumnOS", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        UpdateContent.Children.Add(updateIdentity);
        UpdateContent.Children.Add(previewUpdates);
        UpdateContent.Children.Add(Paragraph("每次启动检查主程序更新。查看目标版本和发布说明并确认后，才会下载安装包；安装还需你选择“重启并安装”。此设置与商店应用各自的版本设置独立。"));
        UpdateContent.Children.Add(updateStatus);
        UpdateContent.Children.Add(updateCandidate);
        UpdateContent.Children.Add(updateProgress);
        UpdateContent.Children.Add(updatePin);
        UpdateContent.Children.Add(updateWait);
        // Vertical actions stay reachable in narrow windows and at larger display scaling.
        var actions = new StackPanel { Spacing = 8, HorizontalAlignment = HorizontalAlignment.Left };
        checkUpdateButton = ActionButton("检查更新", "CheckSystemUpdate", async (_, _) => await CheckSystemUpdateAsync());
        downloadUpdateButton = ActionButton("查看更新并确认", "DownloadSystemUpdate", async (_, _) => await ConfirmAvailableUpdateAsync());
        selectUpgradeButton = ActionButton("更新到指定版本", "SelectSystemUpgrade", async (_, _) => await SelectSystemVersionAsync(UpdateSelectionMode.Upgrade));
        selectRollbackButton = ActionButton("回滚到指定版本", "SelectSystemRollback", async (_, _) => await SelectSystemVersionAsync(UpdateSelectionMode.Rollback));
        restoreUpdateChannelButton = ActionButton("恢复跟随更新渠道", "RestoreSystemUpdateChannel", async (_, _) => await RestoreUpdateChannelAsync());
        restartUpdateButton = ActionButton("重启并安装", "RestartSystemUpdate", async (_, _) => await ConfirmSystemRestartAsync());
        cancelUpdateButton = ActionButton("取消当前操作", "DeferSystemUpdate", (_, _) => CancelSystemUpdateOperation());
        foreach (var button in new[] { checkUpdateButton, downloadUpdateButton, selectUpgradeButton, selectRollbackButton, restoreUpdateChannelButton, restartUpdateButton, cancelUpdateButton }) actions.Children.Add(button);
        UpdateContent.Children.Add(actions);
        UpdateContent.Children.Add(Paragraph("官方更新源：paimeng5201314/autumnlab。游客也可检查、选择和安装允许的版本。运行中的游戏或应用需要先保存并正常退出。"));
        previewUpdates.Toggled += OnUpdateSettingsChanged;
        updateTimer.Tick += async (_, _) => await TickSystemUpdateAsync();
    }

    private void InitializeUpdateServices()
    {
        if (updates is not null) return;
        loadingUpdateSettings = true;
        try
        {
            updates = UpdateService.CreateDefault(installationRoot.DataDirectory, BrandInfo.Version, BrandInfo.BuildId, () => networkSettings, reason => criticalOperations.EnterWrite(reason), programDirectory: installationRoot.ProgramDirectory,
                compatibilityCheck: (manifest, rollback) => AutumnOS.UpdateProtocol.UpdateTransaction.GetCompatibility(installationRoot.ProgramDirectory, manifest, rollback));
            // The old shell setting is imported once; the update service is the sole policy writer.
            var legacy = new VersionedConfigurationStore(installationRoot, "system-update", 1, criticalOperations).Load();
            if (!legacy.Success)
                updateWait.Text = legacy.ErrorCode + " · 旧版更新设置无法读取，已保留原文件；当前使用更新服务已保存的策略。";
            else if (legacy.Value is { } persisted && persisted.Values.TryGetProperty("preview", out var preview) && preview.ValueKind is JsonValueKind.True or JsonValueKind.False)
                updates.ImportLegacyPreviewPreference(preview.GetBoolean());
            previewUpdates.IsOn = updates.Snapshot.PreviewEnabled;
            RefreshInstalledUpdateChannel();
            updates.Changed += (_, _) => DispatcherQueue.TryEnqueue(RenderUpdateState);
            updateTimer.Start();
            RenderUpdateState();
            _ = RunStartupUpdateCheckAsync();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            updateTimer.Stop();
            updates?.Dispose();
            updates = null;
            previewUpdates.IsEnabled = false;
            foreach (var button in new[] { checkUpdateButton, downloadUpdateButton, selectUpgradeButton, selectRollbackButton, restoreUpdateChannelButton, restartUpdateButton, cancelUpdateButton })
                if (button is not null) button.IsEnabled = false;
            updateStatus.Text = SafeUpdateError(error) + " · 更新服务未能初始化，原配置保留。";
        }
        finally { loadingUpdateSettings = false; }
    }

    private async Task RunStartupUpdateCheckAsync()
    {
        if (updates is null) return;
        try
        {
            updates.ReconcileCommittedPolicy();
            await updates.CheckOnStartupAsync(lifetime.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException) { updateWait.Text = SafeUpdateError(error); }
        finally { startupUpdateCheckFinished = true; RenderUpdateState(); }
        await TryShowStartupUpdatePromptAsync();
    }

    // Called after first-run is completed/skipped, and by the idle UI timer when an account dialog closes.
    private async Task TryShowStartupUpdatePromptAsync()
    {
        if (updates is null || !startupUpdateCheckFinished || lifetime.IsCancellationRequested || updateBusy || updateCommitBusy || updateDialogOpen || !SystemUpdateUiIsIdle) return;
        if (AutumnOS.UpdateProtocol.UpdateLaunchGuard.IsHealthStart)
        {
            // The updater records a requested pin only after the health acknowledgement is committed.
            // Defer this first prompt until that durable result has been reconciled.
            try
            {
                string journal = Path.Combine(installationRoot.ProgramDirectory, ".autumnos-update", "journal.json");
                if (!File.Exists(journal) || AutumnOS.UpdateProtocol.UpdateFiles.Read<AutumnOS.UpdateProtocol.UpdateJournal>(journal).Phase != "committed") return;
                updates.ReconcileCommittedPolicy();
            }
            catch (Exception error) when (error is not OutOfMemoryException) { return; }
        }
        var firstRun = stateStore?.Load();
        if (firstRun?.Success != true || firstRun.State?.IsComplete != true) return;
        var snapshot = updates.Snapshot;
        if (snapshot.ShouldPrompt && snapshot.Candidate is { } candidate)
            await ConfirmUpdateTargetAsync(candidate, UpdateSelectionMode.Upgrade, startupPrompt: true);
    }

    private void OnUpdateSettingsChanged(object sender, RoutedEventArgs args)
    {
        if (loadingUpdateSettings || updates is null) return;
        bool requested = previewUpdates.IsOn;
        try
        {
            updates.SetPreviewEnabled(requested);
            updateWait.Text = requested ? "已接受预览版邀请；检查时也会保留稳定版候选。" : "已停止接收预览更新。尚未安装的预览版计划不能继续执行；当前版本保留。";
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { updateWait.Text = SafeUpdateError(error) + " · 设置未保存，已恢复实际保存的选择。"; }
        finally
        {
            loadingUpdateSettings = true;
            previewUpdates.IsOn = updates.Snapshot.PreviewEnabled;
            loadingUpdateSettings = false;
            RenderUpdateState();
        }
    }

    private async Task CheckSystemUpdateAsync()
    {
        if (updates is null || updateCommitBusy || updateDialogOpen) return;
        updateBusy = true;
        ResetUpdateCancellation();
        RenderUpdateState();
        try { await updates.CheckAsync(updates.Snapshot.PreviewEnabled, updateOperation!.Token); }
        catch (OperationCanceledException) { updateWait.Text = "本次检查已取消。"; }
        catch (Exception error) when (error is not OutOfMemoryException) { updateWait.Text = SafeUpdateError(error); }
        finally { updateBusy = false; RenderUpdateState(); }
    }

    private async Task ConfirmAvailableUpdateAsync()
    {
        if (updates?.Snapshot.Candidate is { } candidate)
            await ConfirmUpdateTargetAsync(candidate, UpdateSelectionMode.Upgrade);
    }

    private async Task DownloadSystemUpdateAsync(UpdateCandidate candidate, UpdateSelectionMode mode, bool pinAfterSuccess)
    {
        if (updates is null || updateBusy || updateCommitBusy) return;
        updateBusy = true;
        ResetUpdateCancellation();
        RenderUpdateState();
        try
        {
            // Consent binds this exact object/identity. No later lookup of /latest/download is permitted.
            updates.ConfirmTarget(candidate, mode, pinAfterSuccess);
            await updates.DownloadAndStageAsync(updateOperation!.Token);
        }
        catch (OperationCanceledException) { updateWait.Text = "下载或验证已取消。再次下载需要重新确认目标版本。"; }
        catch (Exception error) when (error is not OutOfMemoryException) { updateWait.Text = SafeUpdateError(error); }
        finally { updateBusy = false; RenderUpdateState(); }
        if (updates.Snapshot.Staged is not null) await ShowReadyToRestartAsync();
    }

    private void ResetUpdateCancellation()
    {
        updateOperation?.Dispose();
        updateOperation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
    }

    private void CancelSystemUpdateOperation()
    {
        if (updateCommitBusy) return;
        try
        {
            if (updateBusy)
            {
                updateOperation?.Cancel();
                updateWait.Text = "已请求取消当前操作；已验证的待安装版本只会在你确认重启后安装。";
            }
            else if (updates?.Snapshot.Staged is not null)
            {
                updates.CancelConsent();
                updateWait.Text = "已取消待安装计划。重新选择目标时需要再次确认。";
                RenderUpdateState();
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { updateWait.Text = SafeUpdateError(error); }
    }

    private async Task TickSystemUpdateAsync()
    {
        foreach (var host in pendingResourceCleanup.ToArray())
        { host.Dispose(); if (host.ResourcesReleased) pendingResourceCleanup.Remove(host); }
        if (lifetime.IsCancellationRequested) return;
        if (!updateBusy && !updateCommitBusy)
        {
            try { updates?.ReconcileCommittedPolicy(); RefreshInstalledUpdateChannel(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { updateWait.Text = SafeUpdateError(error) + " · 切换后的固定策略未能保存，将保留成功安装记录以便重试。"; }
        }
        await TryShowStartupUpdatePromptAsync();
    }

    private async Task RestoreUpdateChannelAsync()
    {
        if (updates is null || updateBusy || updateCommitBusy || updateDialogOpen) return;
        try
        {
            updates.RestoreFollowingChannel();
            updateWait.Text = "已恢复跟随更新渠道。重新检查只更新候选信息，安装仍需确认。";
            await CheckSystemUpdateAsync();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { updateWait.Text = SafeUpdateError(error) + " · 未能保存，固定状态保持不变。"; }
        finally { RenderUpdateState(); }
    }

    private void RenderUpdateState()
    {
        if (updates is null || lifetime.IsCancellationRequested) return;
        var s = updates.Snapshot;
        updateIdentity.Text = $"当前版本 {BrandInfo.DisplayVersion} · {installedUpdateChannel}\n可比较工程版本 {BrandInfo.Version}\n构建 {BrandInfo.BuildId}\n更新渠道：{(s.PreviewEnabled ? "稳定版及预览版" : "稳定版")}" +
            (s.IsTestBuild ? "\n仅本地更新演练 · 测试信任根 / 隔离源" : "") +
            (s.LastCheckUtc is { } checkedAt ? $"\n上次检查 {checkedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "\n尚未检查") +
            (s.LastSuccessfulCheckUtc is { } succeededAt ? $"\n上次成功 {succeededAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}" : "\n尚无成功检查记录");
        updateStatus.Text = s.State switch
        {
            UpdateState.Idle => "尚未检查",
            UpdateState.Checking => "正在读取发行版信息…",
            UpdateState.Available => "发现版本 · 等待你查看说明并确认",
            UpdateState.NoCompatibleUpdate => s.CheckComplete ? "当前渠道没有可用新版本" : "检查信息不完整，尚不能确定是否有新版本",
            UpdateState.Downloading => "正在下载",
            UpdateState.Verifying => "正在验证签名与安装包完整性…",
            UpdateState.Staged => "等待重启 · 安装包已验证，尚未安装",
            UpdateState.Committed => "更新成功",
            UpdateState.RolledBack => "已恢复旧版",
            UpdateState.Error => "检查或更新未完成 · " + UpdateReason(s.ErrorCode),
            _ => "更新处理中 · " + s.State
        };
        if (!s.AutomaticInstallConfigured) updateStatus.Text += "\n缺少正式更新可信公钥，暂不能验证或安装正式更新。";
        if (s.RetryAfterUtc is { } retry) updateStatus.Text += $"\n服务暂缓检查，可重试时间：{retry.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        if (!s.PreviewEnabled && installedUpdateChannel == "预览版") updateStatus.Text += "\n已停止接收预览更新，可继续使用当前版本或手动回滚到稳定版。";
        var visibleWarnings = s.Warnings.Where(code => code != "UPDATE_PREVIEW_STOPPED_KEEP_CURRENT" || installedUpdateChannel == "预览版").Select(UpdateReason).Distinct().ToArray();
        if (visibleWarnings.Length != 0) updateStatus.Text += "\n" + string.Join("\n", visibleWarnings);
        AppendUpdateRecoveryStatus();
        updateCandidate.Text = s.Candidate is { } candidate ? $"可用版本 {candidate.Manifest.Version} · {ReleaseChannel(candidate)}\n{candidate.Tag} · {candidate.Manifest.Payload.Bytes:N0} 字节" : "没有已验证的可安装候选。";
        updatePin.Text = s.PinnedVersion is { } pinned ? $"已固定版本：{pinned}。每次启动仍检查，但不弹出常规升级提醒。" : "正在跟随更新渠道。";
        updateProgress.Value = s.TotalBytes > 0 ? Math.Min(100, 100d * s.BytesReceived / s.TotalBytes) : 0;
        bool available = !updateBusy && !updateCommitBusy && !updateDialogOpen;
        checkUpdateButton!.IsEnabled = available && s.State is not (UpdateState.Downloading or UpdateState.Verifying or UpdateState.Checking);
        downloadUpdateButton!.IsEnabled = available && s.Candidate is not null && s.Staged is null;
        selectUpgradeButton!.IsEnabled = selectRollbackButton!.IsEnabled = available && s.Staged is null;
        restoreUpdateChannelButton!.IsEnabled = available && s.PinnedVersion is not null;
        restartUpdateButton!.IsEnabled = available && s.Staged is not null;
        cancelUpdateButton!.Content = s.Staged is not null && !updateBusy ? "取消待安装计划" : "取消当前操作";
        cancelUpdateButton.IsEnabled = !updateCommitBusy && !updateDialogOpen && (updateBusy || s.Staged is not null);
        previewUpdates.IsEnabled = !updateCommitBusy && !updateDialogOpen;
    }

    private void RefreshInstalledUpdateChannel()
    {
        if (installedUpdateChannelVerified || DateTimeOffset.UtcNow < nextInstalledChannelCheck) return;
        nextInstalledChannelCheck = DateTimeOffset.UtcNow.AddSeconds(10);
        try
        {
            string root = installationRoot.ProgramDirectory;
            string journalPath = Path.Combine(root, ".autumnos-update", "journal.json");
            if (!File.Exists(journalPath)) return;
            var journal = AutumnOS.UpdateProtocol.UpdateFiles.Read<AutumnOS.UpdateProtocol.UpdateJournal>(journalPath);
            if (journal.Phase != "committed" || journal.ToBuildId != BrandInfo.BuildId || journal.TargetVersion != BrandInfo.Version) return;
            var installed = AutumnOS.UpdateProtocol.UpdateFiles.ReadInstalled(root);
            if (installed.BuildId != BrandInfo.BuildId || installed.Version != BrandInfo.Version) return;
            string transaction = AutumnOS.UpdateProtocol.UpdateFiles.Transaction(root, journal.TransactionId);
            string manifestPath = Path.Combine(transaction, "autumn.update.json"), signaturePath = Path.Combine(transaction, "autumn.update.sig");
            UpdatePaths.EnsureNoReparsePoints(manifestPath);
            UpdatePaths.EnsureNoReparsePoints(signaturePath);
            if (!File.Exists(manifestPath) || !File.Exists(signaturePath) || new FileInfo(manifestPath).Length > UpdateManifestCodec.MaximumManifestBytes || new FileInfo(signaturePath).Length > 16384) return;
            if (AutumnOS.UpdateProtocol.UpdateFiles.Hash(manifestPath) != journal.ManifestSha256) return;
            var manifest = UpdateSignature.Verify(File.ReadAllBytes(manifestPath), File.ReadAllBytes(signaturePath), UpdateTrustStore.FromEmbedded(), DateTimeOffset.UtcNow);
            if (manifest.BuildId != BrandInfo.BuildId || manifest.Version != BrandInfo.Version) return;
            installedUpdateChannel = manifest.Channel == "meta" ? "预览版" : "稳定版";
            installedUpdateChannelVerified = true;
            RenderUpdateState();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { /* A display label never invents GitHub channel identity from engineering versions. */ }
    }

    private void AppendUpdateRecoveryStatus()
    {
        try
        {
            string journalPath = Path.Combine(installationRoot.ProgramDirectory, ".autumnos-update", "journal.json");
            if (!File.Exists(journalPath)) return;
            var journal = AutumnOS.UpdateProtocol.UpdateFiles.Read<AutumnOS.UpdateProtocol.UpdateJournal>(journalPath);
            updateStatus.Text += journal.Phase switch
            {
                "committed" => "\n上次更新：新版核心初始化已确认。",
                "rolledBack" => "\n上次更新失败，已恢复可用旧版；失败构建已隔离。",
                "aborted" => "\n上次更新在替换前停止，原版保留。",
                _ => "\n发现未完成的更新事务，请使用原 AutumnOS.exe 恢复入口。"
            };
            if (journal.ErrorCode is not null) updateStatus.Text += "\n" + UpdateReason(journal.ErrorCode);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { updateStatus.Text += "\n更新恢复记录不能读取，已保留原文件。"; }
    }

    private void CloseUpdateServices()
    {
        updateTimer.Stop();
        updateOperation?.Cancel();
        activeUpdateDialog?.Hide();
        updates?.Dispose();
    }

    private static string ReleaseChannel(UpdateCandidate candidate) => candidate.Manifest.Channel == "meta" ? "预览版" : "稳定版";
    private static string SafeUpdateError(Exception error) => error switch
    {
        UpdateException update => UpdateReason(update.Code), DataStoreException storage => storage.Code,
        IOException io when io.Message.StartsWith("UPDATE_", StringComparison.Ordinal) => UpdateReason(io.Message),
        TimeoutException => "检查超时，请稍后重试。", _ => "更新操作未完成，请稍后重试。"
    };

    private static string UpdateReason(string? code) => code switch
    {
        null => "原因未知，请重试。",
        "UPDATE_PRODUCTION_TRUST_NOT_CONFIGURED" => "缺少正式更新可信公钥。",
        "UPDATE_PREVIEW_DISABLED" or "UPDATE_PREVIEW_NOT_ALLOWED" => "需要先开启并保存“接受预览版邀请”。",
        "UPDATE_VERSION_NOT_COMPARABLE" or "UPDATE_VERSION_INVALID" => "此历史版本没有可靠的可比较版本，暂不能安装。",
        "UPDATE_CURRENT_VERSION" or "UPDATE_CANDIDATE_CURRENT" => "当前已安装，不能重复安装同一版本。",
        "UPDATE_NOT_UPGRADE" or "UPDATE_UPGRADE_REQUIRES_NEWER" or "UPDATE_DOWNGRADE_REJECTED" => "更新目标必须高于当前版本。低版本请使用回滚入口。",
        "UPDATE_NOT_ROLLBACK" or "UPDATE_ROLLBACK_REQUIRES_OLDER" => "回滚目标必须低于当前版本。",
        "UPDATE_ASSET_MISSING_OR_AMBIGUOUS" => "此版本缺少唯一、完整的安装资产。",
        "UPDATE_TARGET_RID_INVALID" or "UPDATE_PLATFORM_NOT_SUPPORTED" => "此版本不支持当前系统或架构。",
        "UPDATE_UPDATER_TOO_OLD" or "UPDATE_ROLLBACK_PROTOCOL_UNSUPPORTED" => "此版本不支持当前所需的更新或回滚协议。",
        "UPDATE_COMPATIBILITY_INVALID" => "目标平台或更新协议不支持当前安装环境。",
        "UPDATE_MIGRATION_UNSUPPORTED" => "此版本不支持安全读取当前数据，暂不能切换。",
        "UPDATE_ONBOARDING_SCHEMA_UNSUPPORTED" => "此历史版本无法读取现有首次配置记录，需要支持新协议的版本。",
        "UPDATE_DATA_COMPATIBILITY_UNAVAILABLE" => "无法核实当前数据与目标版本的兼容性，暂不能安装。",
        "UPDATE_DATA_MIGRATION_UNSUPPORTED" => "目标不支持当前数据格式，不能安全升级或回滚。",
        "UPDATE_DATA_SNAPSHOT_INVALID" => "当前数据快照未通过完整性校验，已停止切换。",
        "UPDATE_APPLY_RECHECK_ABORTED" => "安装前复核未通过，已重新打开原版本。",
        "UPDATE_TARGET_WITHDRAWN" => "目标发行版已撤回或改为草稿，已停止安装。",
        "UPDATE_TARGET_CHANGED" or "UPDATE_TARGET_ASSET_CHANGED" => "目标发行版或安装资产发生变化，需要重新选择并确认。",
        "UPDATE_FAILED_BUILD_BLOCKED" or "UPDATE_CANDIDATE_PREVIOUSLY_FAILED_OR_CURRENT" => "此构建已安装或曾启动失败，已阻止重复安装。",
        "UPDATE_MINIMUM_SECURITY_VERSION" => "目标版本低于当前要求的最低安全版本。",
        "UPDATE_PREVIEW_PLAN_CANCELLED" => "预览版邀请已关闭，尚未应用的预览版计划已取消。",
        "UPDATE_PREVIEW_STOPPED_KEEP_CURRENT" => "已停止接收预览更新，可继续使用当前版本或手动回滚到稳定版。",
        "UPDATE_CHANNEL_CHANGED" => "更新策略发生变化，请重新检查并确认目标。",
        "UPDATE_OPERATION_IN_PROGRESS" => "已有更新操作正在进行，请等待完成。",
        "UPDATE_CONSENT_REQUIRED" or "UPDATE_CONSENT_TARGET_CHANGED" => "目标授权缺失或目标发生变化，请重新查看发布说明并确认。",
        "UPDATE_RELEASE_WITHDRAWN" => "目标发行版已撤回，不能继续安装。",
        "UPDATE_TIMEOUT" => "请求超时，请稍后重试。",
        "UPDATE_OFFLINE" => "无法连接更新源，请检查网络后重试。",
        "UPDATE_RATE_LIMITED" => "更新源限制了请求频率，已暂缓检查。",
        "UPDATE_SERVICE_UNAVAILABLE" => "更新源暂时不可用，请稍后重试。",
        "UPDATE_RELEASE_MANIFEST_MISMATCH" => "发行渠道或版本与可信清单不一致，请等待维护者修正。",
        "UPDATE_RELEASE_REMOVED" or "UPDATE_RELEASE_UNAVAILABLE" => "目标版本已撤回或不可用，请重新选择。",
        "UPDATE_ASSET_IDENTITY_MISMATCH" or "UPDATE_STAGED_METADATA_CHANGED" => "目标安装资产发生变化，需要重新检查并确认。",
        "UPDATE_METADATA_REPLAY" => "此版本未通过最低安全序列检查。",
        "UPDATE_NO_COMPATIBLE_SIGNED_CANDIDATE" => "没有兼容且可信的候选；详情请查看版本列表。",
        "UPDATE_DATA_INCOMPATIBLE" or "UPDATE_ROLLBACK_DATA_INCOMPATIBLE" => "目标无法读取当前数据，且没有受支持的恢复路径。",
        "UPDATE_LATEST_NOT_FOUND" => "官方仓库尚未提供最新稳定发行版。",
        "UPDATE_CANCELLED" => "操作已取消。",
        "UPDATE_NOT_IDLE" => "请先保存并正常退出运行中的游戏或应用，再重试安装。",
        _ => "暂不能完成此操作（" + code + "）。"
    };
}
