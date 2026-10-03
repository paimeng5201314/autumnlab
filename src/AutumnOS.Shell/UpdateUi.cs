using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Storage;
using AutumnOS.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private UpdateService? updates;
    private VersionedConfigurationStore? updateSettings;
    private readonly ToggleSwitch automaticUpdates = new() { Header = "自动更新主程序", IsOn = true };
    private readonly ToggleSwitch previewUpdates = new() { Header = "接收预览版更新", IsOn = false };
    private readonly TextBlock updateIdentity = Paragraph("");
    private readonly TextBlock updateStatus = Paragraph("尚未检查");
    private readonly TextBlock updateCandidate = Paragraph("");
    private readonly TextBlock updateNotes = Paragraph("");
    private readonly TextBlock updateWait = Paragraph("");
    private readonly ProgressBar updateProgress = new() { Minimum = 0, Maximum = 100 };
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Button? checkUpdateButton, downloadUpdateButton, restartUpdateButton, cancelUpdateButton;
    private Border? restartNotice;
    private TextBlock? restartNoticeText;
    private CancellationTokenSource? updateOperation;
    private bool loadingUpdateSettings, updateBusy, updateCommitBusy, updateDeferred, updateManualRestart, updateRecoveryHold;
    private DateTimeOffset nextUpdateCheck = DateTimeOffset.UtcNow.AddSeconds(12), lastUpdateCheck;
    private long lastUserInput = Environment.TickCount64;
    private long restartCountdown;
    private readonly HashSet<WebAppHost> pendingResourceCleanup = [];
    private void RetainUnreleasedHost(WebAppHost host) => pendingResourceCleanup.Add(host);

    private void InitializeUpdateUi()
    {
        AutomationProperties.SetAutomationId(automaticUpdates, "AutomaticUpdates");
        AutomationProperties.SetAutomationId(previewUpdates, "PreviewUpdates");
        AutomationProperties.SetAutomationId(updateStatus, "UpdateStatus");
        AutomationProperties.SetAutomationId(updateCandidate, "UpdateCandidate");
        AutomationProperties.SetAutomationId(updateWait, "UpdateWaitingReason");
        UpdateContent.Children.Add(new TextBlock { Text = "AutumnOS", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        UpdateContent.Children.Add(updateIdentity);
        UpdateContent.Children.Add(automaticUpdates); UpdateContent.Children.Add(previewUpdates);
        UpdateContent.Children.Add(Paragraph("首次启用此功能：自动更新默认开启，预览默认关闭。预览关闭仅接收 plus，开启仅接收 meta。各游戏的版本设置独立保存。"));
        UpdateContent.Children.Add(updateStatus); UpdateContent.Children.Add(updateCandidate);
        UpdateContent.Children.Add(updateProgress); UpdateContent.Children.Add(updateNotes); UpdateContent.Children.Add(updateWait);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        checkUpdateButton = ActionButton("检查更新", "CheckSystemUpdate", async (_, _) => await CheckSystemUpdateAsync());
        downloadUpdateButton = ActionButton("下载并验证", "DownloadSystemUpdate", async (_, _) => await DownloadSystemUpdateAsync());
        restartUpdateButton = ActionButton("重启更新", "RestartSystemUpdate", (_, _) => { updateRecoveryHold = false; updateDeferred = false; updateManualRestart = true; restartCountdown = 0; });
        cancelUpdateButton = ActionButton("取消 / 稍后重启", "DeferSystemUpdate", (_, _) => DeferSystemUpdate());
        foreach (var button in new[] { checkUpdateButton, downloadUpdateButton, restartUpdateButton, cancelUpdateButton }) actions.Children.Add(button);
        UpdateContent.Children.Add(actions);
        UpdateContent.Children.Add(Paragraph("更新源：paimeng5201314/autumnlab。检查与账号登录无关；更新说明仅以文本显示。游戏在后台或正在结束时仍会等待。"));
        automaticUpdates.Toggled += OnUpdateSettingsChanged; previewUpdates.Toggled += OnUpdateSettingsChanged;
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => lastUserInput = Environment.TickCount64), true);
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => lastUserInput = Environment.TickCount64), true);
        var notice = new StackPanel { Spacing = 8 };
        restartNoticeText = Paragraph(""); notice.Children.Add(restartNoticeText);
        notice.Children.Add(ActionButton("稍后重启", "DeferUpdateNotice", (_, _) => DeferSystemUpdate()));
        restartNotice = new Border { Child = notice, Padding = new Thickness(20), Margin = new Thickness(20, 12, 20, 0), CornerRadius = new CornerRadius(16), Background = Brush("F0E8D6"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
        Root.Children.Add(restartNotice);
        updateTimer.Tick += async (_, _) => await TickSystemUpdateAsync();
    }

    private void InitializeUpdateServices()
    {
        if (updates is not null) return;
        loadingUpdateSettings = true;
        try
        {
            updateSettings = new(installationRoot, "system-update", 1, criticalOperations);
            var loadedSettings = updateSettings.Load();
            if (!loadedSettings.Success) throw new DataStoreException(loadedSettings.ErrorCode);
            if (loadedSettings.Value is { } persisted)
            {
                var values = persisted.Values;
                automaticUpdates.IsOn = values.GetProperty("automatic").GetBoolean();
                previewUpdates.IsOn = values.GetProperty("preview").GetBoolean();
                if (values.TryGetProperty("lastCheckUtc", out var last) && last.ValueKind == JsonValueKind.String && last.TryGetDateTimeOffset(out var date)) lastUpdateCheck = date;
            }
            updates = UpdateService.CreateDefault(installationRoot.DataDirectory, BrandInfo.Version, BrandInfo.BuildId, () => networkSettings, reason => criticalOperations.EnterWrite(reason));
            updates.SetPreviewEnabled(previewUpdates.IsOn);
            string recoveryJournal = Path.Combine(installationRoot.ProgramDirectory, ".autumnos-update", "journal.json");
            if (File.Exists(recoveryJournal))
                updateRecoveryHold = AutumnOS.UpdateProtocol.UpdateFiles.Read<AutumnOS.UpdateProtocol.UpdateJournal>(recoveryJournal).Phase == "aborted";
            updates.Changed += (_, _) => DispatcherQueue.TryEnqueue(RenderUpdateState);
            updateTimer.Start(); RenderUpdateState();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            automaticUpdates.IsEnabled = previewUpdates.IsEnabled = false;
            updateStatus.Text = SafeUpdateError(error) + " · 更新服务未能初始化，原配置保留。";
        }
        finally { loadingUpdateSettings = false; }
    }

    private void OnUpdateSettingsChanged(object sender, RoutedEventArgs args)
    {
        if (loadingUpdateSettings || updates is null || updateSettings is null) return;
        updateOperation?.Cancel(); restartCountdown = 0; updateManualRestart = false;
        if (restartNotice is not null) restartNotice.Visibility = Visibility.Collapsed;
        var result = SaveUpdateSettings();
        if (!result.Success) { updateStatus.Text = result.ErrorCode + " · 设置未保存。"; return; }
        updates.SetPreviewEnabled(previewUpdates.IsOn); updateDeferred = false; nextUpdateCheck = DateTimeOffset.UtcNow.AddSeconds(2);
        RenderUpdateState();
    }

    private DataResult<VersionedConfiguration> SaveUpdateSettings() => updateSettings!.Save(JsonSerializer.SerializeToElement(new { automatic = automaticUpdates.IsOn, preview = previewUpdates.IsOn, lastCheckUtc = lastUpdateCheck == default ? (DateTimeOffset?)null : lastUpdateCheck }));

    private async Task CheckSystemUpdateAsync()
    {
        if (updates is null || updateBusy || updateCommitBusy) return;
        updateBusy = true; updateOperation?.Dispose(); updateOperation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try
        {
            await updates.CheckAsync(previewUpdates.IsOn, updateOperation.Token);
            lastUpdateCheck = DateTimeOffset.UtcNow; SaveUpdateSettings(); updateDeferred = false;
        }
        catch (OperationCanceledException) { updateWait.Text = "本次检查已取消。"; }
        catch (Exception error) when (error is not OutOfMemoryException) { updateWait.Text = SafeUpdateError(error); }
        finally { updateBusy = false; nextUpdateCheck = DateTimeOffset.UtcNow.AddHours(6); RenderUpdateState(); }
        if (automaticUpdates.IsOn && updates.Snapshot.State == UpdateState.Available) await DownloadSystemUpdateAsync();
    }

    private async Task DownloadSystemUpdateAsync()
    {
        if (updates is null || updateBusy || updateCommitBusy) return;
        updateBusy = true; updateDeferred = false; updateOperation?.Dispose(); updateOperation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        try { await updates.DownloadAndStageAsync(updateOperation.Token); }
        catch (OperationCanceledException) { updateWait.Text = "下载 / 验证已取消，可重新准备。"; }
        catch (Exception error) when (error is not OutOfMemoryException) { updateWait.Text = SafeUpdateError(error); }
        finally { updateBusy = false; RenderUpdateState(); }
    }

    private void DeferSystemUpdate()
    {
        if (updateCommitBusy) return;
        updateOperation?.Cancel(); updateDeferred = true; updateManualRestart = false; restartCountdown = 0;
        if (restartNotice is not null) restartNotice.Visibility = Visibility.Collapsed;
        updateWait.Text = "本次重启已暂缓，已验证的候选将在下次提交前重新核验。";
    }

    private async Task TickSystemUpdateAsync()
    {
        foreach (var host in pendingResourceCleanup.ToArray())
        { host.Dispose(); if (host.ResourcesReleased) pendingResourceCleanup.Remove(host); }
        if (updates is null || updateBusy || updateCommitBusy || lifetime.IsCancellationRequested) return;
        if (automaticUpdates.IsOn && DateTimeOffset.UtcNow >= nextUpdateCheck) { await CheckSystemUpdateAsync(); return; }
        if (updates.Snapshot.Staged is null || updateDeferred || (!automaticUpdates.IsOn && !updateManualRestart)) return;
        if (updateRecoveryHold)
        {
            updateWait.Text = "上次提交因文件占用等原因停止，自动重启已暂停。关闭占用程序后可选择“重启更新”重试。";
            return;
        }
        var state = criticalOperations.GetSnapshot();
        bool systemSafe = SystemUpdateUiIsIdle;
        if (state.ActiveGames != 0 || state.ActiveWrites != 0 || !systemSafe)
        {
            restartCountdown = 0; restartNotice!.Visibility = Visibility.Collapsed;
            updateWait.Text = state.WaitingReasons.Count > 0 ? string.Join("\n", state.WaitingReasons) : "等待系统操作结束；请保存内容并返回桌面。";
            return;
        }
        if (Environment.TickCount64 - lastUserInput < 15000 && restartCountdown == 0) { updateWait.Text = "已准备，等待操作空闲后提示重启。"; return; }
        if (restartCountdown == 0) restartCountdown = Environment.TickCount64 + 15000;
        long seconds = Math.Max(0, (restartCountdown - Environment.TickCount64 + 999) / 1000);
        restartNotice!.Visibility = Visibility.Visible;
        restartNoticeText!.Text = $"更新已验证，将在 {seconds} 秒后安全重启。可选择稍后重启。";
        updateWait.Text = restartNoticeText.Text;
        if (seconds == 0) await CommitSystemUpdateAsync();
    }

    private void RenderUpdateState()
    {
        if (updates is null) return;
        var s = updates.Snapshot;
        updateIdentity.Text = $"当前版本 {BrandInfo.DisplayVersion}\n构建 {BrandInfo.BuildId}\n渠道 {s.Channel}" + (s.IsTestBuild ? "\n仅本地更新演练 · 测试信任根 / 隔离源" : "") + (lastUpdateCheck == default ? "\n尚未检查" : $"\n上次检查 {lastUpdateCheck.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        updateStatus.Text = s.State switch
        {
            UpdateState.Idle => "尚未检查", UpdateState.Checking => "正在读取 Release 列表…", UpdateState.Available => "发现符合策略的候选",
            UpdateState.NoCompatibleUpdate => "没有合规的已签名新版（当前兼容范围）",
            UpdateState.Downloading => "下载中", UpdateState.Verifying => "验证签名及完整负载…", UpdateState.Staged => "签名与完整负载验证通过 · 已准备，尚未安装",
            UpdateState.Committed => "更新成功", UpdateState.RolledBack => "已恢复旧版", UpdateState.Error => "更新未完成 · " + s.ErrorCode,
            _ => s.State.ToString()
        };
        if (!s.AutomaticInstallConfigured) updateStatus.Text += "\n自动安装尚未配置：缺少生产可信公钥，不能安装未签名更新。";
        if (s.Warnings.Count != 0) updateStatus.Text += "\n" + string.Join("\n", s.Warnings);
        try
        {
            string journalPath = Path.Combine(installationRoot.ProgramDirectory, ".autumnos-update", "journal.json");
            if (File.Exists(journalPath))
            {
                var journal = AutumnOS.UpdateProtocol.UpdateFiles.Read<AutumnOS.UpdateProtocol.UpdateJournal>(journalPath);
                updateStatus.Text += journal.Phase switch
                {
                    "committed" => "\n上次更新：新版核心初始化已确认。",
                    "rolledBack" => "\n上次更新失败，已恢复可用旧版；失败构建已隔离。",
                    "aborted" => "\n上次更新在替换前停止，原版保留。",
                    _ => "\n发现未完成的更新事务，请使用原 AutumnOS.exe 恢复入口。"
                };
                if (journal.ErrorCode is not null) updateStatus.Text += "\n" + journal.ErrorCode;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { updateStatus.Text += "\n更新恢复记录不能读取，已保留原文件。"; }
        updateCandidate.Text = s.Candidate is { } candidate ? $"候选 {candidate.Manifest.Version} · {candidate.Tag}\n{candidate.Manifest.Payload.Bytes:N0} 字节" : "没有已验证候选。";
        updateNotes.Text = s.Candidate?.Notes ?? "";
        updateProgress.Value = s.TotalBytes > 0 ? Math.Min(100, 100d * s.BytesReceived / s.TotalBytes) : 0;
        checkUpdateButton!.IsEnabled = !updateBusy && !updateCommitBusy;
        downloadUpdateButton!.IsEnabled = !updateBusy && !updateCommitBusy && s.State == UpdateState.Available;
        restartUpdateButton!.IsEnabled = !updateCommitBusy && s.Staged is not null;
        cancelUpdateButton!.IsEnabled = !updateCommitBusy;
    }

    private void CloseUpdateServices()
    {
        updateTimer.Stop(); updateOperation?.Cancel(); updates?.Dispose();
    }

    private static string SafeUpdateError(Exception error) => error switch
    { UpdateException update => update.Code, DataStoreException storage => storage.Code, TimeoutException => "UPDATE_TIMEOUT", _ => "UPDATE_OPERATION_FAILED" };
}
