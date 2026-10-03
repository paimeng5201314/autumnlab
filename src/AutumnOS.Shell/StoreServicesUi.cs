using AutumnOS.Contracts;
using AutumnOS.Packages;
using AutumnOS.Store;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private ApplicationInstallService? applicationInstaller;
    private StoreNetworkSettingsService? storeNetworkSettings;
    private StoreNetworkSettings networkSettings = new();
    private GitHubTransport? githubTransport;
    private DownloadService? storeDownloads;
    private StoreInstallCoordinator? storeInstallCoordinator;
    private readonly HashSet<string> approvedDownloadInstalls = [];
    private bool installingStoreTask;
    private bool isolatedStoreSource;
    private readonly CheckBox useSystemProxy = new() { Content = "使用系统网络代理" };
    private readonly CheckBox allowDirectFallback = new() { Content = "加速服务失败时尝试直连" };
    private readonly TextBox githubApiTemplate = new() { Header = "公开元数据加速模板（留空为直连）", PlaceholderText = "https://你的服务/?url={url}", MaxLength = 2048 };
    private readonly TextBox githubAssetTemplate = new() { Header = "Release 附件加速模板（留空为直连）", PlaceholderText = "https://你的服务/?url={url}", MaxLength = 2048 };
    private readonly NumberBox downloadConcurrency = new() { Header = "同时下载任务数", Minimum = 1, Maximum = 4, SmallChange = 1, Value = 2, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
    private readonly NumberBox downloadLimit = new() { Header = "总下载限速（KiB/s，0 为不限速）", Minimum = 0, Maximum = 1048576, SmallChange = 64, Value = 0, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline };
    private readonly TextBlock networkSettingsResult = Paragraph("");
    private readonly DispatcherTimer downloadUiTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, (TextBlock Status, ProgressBar Progress, Button Pause, Button Resume, Button Cancel, Button Retry, Button Install)> downloadRows = [];

    private void InitializeStoreServices()
    {
        if (applicationInstaller is not null) return;
        try
        {
            var saves = new InstalledSaveGuard(installationRoot.Directories["Saves"], Path.Combine(installationRoot.Directories["Packages"], "SaveBackups"));
            applicationInstaller = new(installationRoot.Directories["Apps"],
                () => criticalOperations.EnterWrite("应用安装 / 恢复提交"),
                id => appHost?.Session.Instance is { } sample && sample.Identity.AppId == id && sample.BlocksMaintenance ||
                    managedApplications.TryGetValue(id, out var app) && app.Host.Session.Instance.BlocksMaintenance,
                saves.PrepareChange);
            foreach (var app in applicationInstaller.GetInstalled()) observedStoreInstalls[app.AppId] = app.Package.PackageSha256;
            applicationInstaller.Changed += changed => DispatcherQueue.TryEnqueue(() =>
            {
                if (lifetime.IsCancellationRequested) return;
                if (changed.Application is { IsInstalled: true } app) ObserveStoreInstall(app);
                else observedStoreInstalls.Remove(changed.AppId);
                installedSample = applicationInstaller.Find("cn.labchronicles.elementpairs")?.Package;
                BuildAppEntries(); RefreshPermissionsUi();
                if (storeSection == "installed") RenderInstalledApplications();
            });
            var recovery = applicationInstaller.InspectRecovery();
            if (recovery.Length > 0) storeStatus.Text = "发现安装事务记录，已保留恢复内容：" + string.Join("、", recovery.Take(4));
            storeNetworkSettings = new(installationRoot.DataDirectory);
            networkSettings = storeNetworkSettings.Load();
            githubTransport = new(() => networkSettings);
            StartStoreInfrastructure(installationRoot.DataDirectory, githubTransport, false);
            LoadNetworkSettingsUi();
            downloadUiTimer.Tick += (_, _) => { if (storeSection == "downloads" && StorePanel.Visibility == Visibility.Visible) UpdateDownloadRows(); };
            downloadUiTimer.Start();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            storeStatus.Text = SafeStoreCode(error) + " · 商店服务未能初始化，原配置与安装记录保留。";
            networkSettingsResult.Text = storeStatus.Text;
        }
    }
    private void StartStoreInfrastructure(string dataRoot, IGitHubTransport transport, bool test)
    {
        if (installingStoreTask || localInstallBusy) throw new InvalidOperationException("PACKAGE_INSTALL_BUSY");
        var nextDownloads = new DownloadService(dataRoot, transport, () => networkSettings);
        StoreInstallCoordinator nextInstaller;
        IStoreCatalog nextCatalog;
        try
        {
            nextInstaller = new(dataRoot, nextDownloads, applicationInstaller!, test);
            nextCatalog = new GitHubStoreCatalog(Path.Combine(dataRoot, "Cache", "GitHubStore"), BrandInfo.Version, "0.3.0", transport);
        }
        catch { nextDownloads.Dispose(); throw; }
        storeQuery?.Cancel(); storeRevision++; storeDownloads?.Dispose(); approvedDownloadInstalls.Clear();
        isolatedStoreSource = test;
        storeRepositories.Clear(); storeDetails.Clear(); storeVersionPages.Clear(); storeHasMore = false;
        storeDownloads = nextDownloads; storeInstallCoordinator = nextInstaller; storeCatalog = nextCatalog;
        storeSource.Text = test ? "本地集成测试数据，不是 GitHub 实时结果" : "GitHub 公开应用";
        if (nextInstaller.RecoveryWarnings.Length > 0) storeStatus.Text = string.Join("、", nextInstaller.RecoveryWarnings);
        var currentDownloads = storeDownloads;
        storeDownloads.Changed += (_, _) => DispatcherQueue.TryEnqueue(async () =>
        {
            if (lifetime.IsCancellationRequested || !ReferenceEquals(currentDownloads, storeDownloads)) return;
            if (storeSection == "downloads" && StorePanel.Visibility == Visibility.Visible) UpdateDownloadRows();
            await ProcessApprovedDownloadsAsync();
        });
    }
    private async Task ProcessApprovedDownloadsAsync()
    {
        if (installingStoreTask || lifetime.IsCancellationRequested) return;
        var ready = storeDownloads?.Snapshot().FirstOrDefault(task => task.State == DownloadState.AwaitingInstall && approvedDownloadInstalls.Contains(task.Id));
        if (ready is not null) { approvedDownloadInstalls.Remove(ready.Id); await InstallStoreTaskAsync(ready.Id); }
    }
    private void CloseStoreServices()
    {
        storeQuery?.Cancel(); storeDebounce.Stop(); downloadUiTimer.Stop();
        storeTestSource?.Dispose(); storeTestSource = null;
        storeDownloads?.Dispose(); githubTransport?.Dispose(); CloseManagedApplications();
    }
    private void InitializeNetworkSettingsUi()
    {
        AutomationProperties.SetAutomationId(useSystemProxy, "GitHubUseSystemProxy");
        AutomationProperties.SetAutomationId(allowDirectFallback, "GitHubDirectFallback");
        AutomationProperties.SetAutomationId(githubApiTemplate, "GitHubApiTemplate");
        AutomationProperties.SetAutomationId(githubAssetTemplate, "GitHubAssetTemplate");
        AutomationProperties.SetAutomationId(downloadConcurrency, "DownloadConcurrency");
        AutomationProperties.SetAutomationId(downloadLimit, "DownloadSpeedLimit");
        AutomationProperties.SetAutomationId(networkSettingsResult, "GitHubNetworkResult");
        GitHubContent.Children.Add(Paragraph("网络与下载 · GitHub 加速"));
        GitHubContent.Children.Add(Paragraph("只影响公开商店的 GitHub 请求。系统代理使用 Windows 现有设置，不修改系统配置；URL 加速由你选择，默认关闭。账号登录和应用自己的网络不经过此服务。"));
        foreach (var control in new UIElement[] { useSystemProxy, githubApiTemplate, githubAssetTemplate, allowDirectFallback, downloadConcurrency, downloadLimit }) GitHubContent.Children.Add(control);
        GitHubContent.Children.Add(Paragraph("模板须使用 HTTPS，并包含一个 {url}。原始 GitHub 地址会进行 URL 编码。不要填入密码、令牌或需要凭据的服务地址。"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        actions.Children.Add(ActionButton("保存设置", "GitHubSaveSettings", (_, _) => SaveNetworkSettings()));
        actions.Children.Add(ActionButton("测试公开接口", "GitHubTestConnection", async (_, _) =>
        {
            if (!SaveNetworkSettings() || githubTransport is null) return;
            networkSettingsResult.Text = "正在检查 GitHub 公开接口…";
            var result = await githubTransport.TestConnectionAsync(lifetime.Token);
            networkSettingsResult.Text = result.Succeeded ? "连接成功 · " + result.Route + " · HTTP " + result.StatusCode : result.Code + " · 连接未成功；TLS 验证保持启用。";
        }));
        actions.Children.Add(ActionButton("测试所选附件连接", "GitHubTestAssetConnection", async (_, _) =>
        {
            if (!SaveNetworkSettings() || githubTransport is null) return;
            if (isolatedStoreSource) { networkSettingsResult.Text = "本地测试源不验证真实 GitHub 附件加速。请先返回公开商店。"; return; }
            var asset = storeDetails.Values.SelectMany(d => d.Versions).FirstOrDefault(v => v.CanInstall)?.Asset;
            if (asset is null) { networkSettingsResult.Text = "请先在公开商店读取含合规附件的应用详情，再测试该附件连接。"; return; }
            networkSettingsResult.Text = "正在检查实际 Release 附件连接…";
            var result = await githubTransport.TestAssetConnectionAsync(asset.DownloadUri, lifetime.Token);
            networkSettingsResult.Text = result.Succeeded ? "附件连接成功 · " + result.Route + " · HTTP " + result.StatusCode : result.Code + " · 附件连接未成功。";
        }));
        GitHubContent.Children.Add(actions); GitHubContent.Children.Add(networkSettingsResult);
    }
    private void LoadNetworkSettingsUi()
    {
        useSystemProxy.IsChecked = networkSettings.UseSystemProxy; allowDirectFallback.IsChecked = networkSettings.AllowDirectFallback;
        githubApiTemplate.Text = networkSettings.ApiUrlTemplate; githubAssetTemplate.Text = networkSettings.AssetUrlTemplate;
        downloadConcurrency.Value = networkSettings.MaximumConcurrentDownloads; downloadLimit.Value = networkSettings.BytesPerSecond / 1024.0;
    }
    private bool SaveNetworkSettings()
    {
        try
        {
            if (storeNetworkSettings is null || !double.IsFinite(downloadConcurrency.Value) || !double.IsFinite(downloadLimit.Value)) throw new ArgumentException();
            var value = new StoreNetworkSettings(useSystemProxy.IsChecked == true, githubApiTemplate.Text.Trim(), githubAssetTemplate.Text.Trim(),
                allowDirectFallback.IsChecked == true, checked((int)downloadConcurrency.Value), checked((long)(downloadLimit.Value * 1024)));
            using var writeLease = criticalOperations.EnterWrite("GitHub 网络配置写入");
            storeNetworkSettings.Save(value); networkSettings = value; networkSettingsResult.Text = "已保存。重启后恢复；新的请求使用当前设置。";
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { networkSettingsResult.Text = SafeStoreCode(error) + " · 设置未保存，请检查模板及下载限制。"; return false; }
    }
    private async Task BeginStoreInstallAsync(CatalogDetails details, CatalogVersion version)
    {
        if (aboutOpen || storeInstallCoordinator is null || version.Manifest is not { } release) return;
        aboutOpen = true;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "下载并安装 " + (details.Store?.Name ?? details.Repository.Name) + "？",
            Content = $"来源：{details.Repository.FullName}\n版本：{release.Version}\n{details.Repository.CategoryLabel}\n权限：{string.Join("、", release.Permissions.Select(PermissionTitle))}\n{StorePermissionChanges(release)}\n\n安装不会自动授予敏感权限。包摘要一致不代表发布者经过审核。", PrimaryButtonText = "下载并安装", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var task = storeInstallCoordinator.Queue(details, version);
            if (task.State == DownloadState.Completed) { await InstallStoreTaskAsync(task.Id); ShowStoreSection("installed"); return; }
            approvedDownloadInstalls.Add(task.Id); ShowStoreSection("downloads");
            if (task.State == DownloadState.AwaitingInstall) { approvedDownloadInstalls.Remove(task.Id); await InstallStoreTaskAsync(task.Id); }
        }
        catch (Exception error) when (error is not OutOfMemoryException) { storeStatus.Text = StoreError(SafeStoreCode(error)); }
        finally { aboutOpen = false; }
    }
    private async Task InstallStoreTaskAsync(string taskId, bool repair = false)
    {
        if (installingStoreTask || storeInstallCoordinator is null || storeDownloads is null) return;
        installingStoreTask = true;
        try
        {
            var task = storeDownloads.Snapshot().Single(x => x.Id == taskId); bool downgrade = applicationInstaller?.Find(task.Request.AppId) is { } current && ApplicationInstallService.CompareVersions(task.Request.Version, current.Package.Manifest.Version) < 0;
            if (downgrade)
            {
                if (aboutOpen) { storeStatus.Text = "下载已完成。请在下载任务中确认降级；旧版本不会自动替换。"; return; }
                aboutOpen = true;
                try
                {
                    var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "检查存档并降级？", Content = "将检查此应用所有账号及游客的真实存档格式，并先建立备份。不兼容或备份失败时会阻止降级。", PrimaryButtonText = "检查并降级", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
                    if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                }
                finally { aboutOpen = false; }
            }
            await storeInstallCoordinator.InstallAsync(taskId, downgrade, lifetime.Token, repair);
            storeStatus.Text = "安装已提交，应用已出现在桌面。";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { storeStatus.Text = StoreError(SafeStoreCode(error)); }
        finally
        {
            installingStoreTask = false;
            if (storeSection == "downloads") UpdateDownloadRows();
            if (!lifetime.IsCancellationRequested) DispatcherQueue.TryEnqueue(async () => await ProcessApprovedDownloadsAsync());
        }
    }
    private void RenderDownloadTasks()
    {
        storeBody.Children.Clear(); downloadRows.Clear();
        storeStatus.Text = "下载、校验与安装分开记录。后台应用仍在运行，安装会等待其真正结束。";
        if (storeDownloads is null || storeDownloads.Snapshot().Count == 0) { storeBody.Children.Add(StoreEmpty("下载任务会出现在这里", "从应用详情选择版本，或选择本地 .autumn 安装包。")); return; }
        foreach (var task in storeDownloads.Snapshot())
        {
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = task.Request.AppId + " · " + task.Request.Version, FontSize = 18, TextWrapping = TextWrapping.Wrap });
            var state = Paragraph(""); AutomationProperties.SetAutomationId(state, "DownloadState-" + task.Request.Version);
            var progress = new ProgressBar { Minimum = 0, Maximum = task.TotalBytes, Height = 5 };
            content.Children.Add(state); content.Children.Add(progress);
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            Button Add(string title, string action, RoutedEventHandler click) { var button = ActionButton(title, "Download-" + action + "-" + task.Request.Version, click); controls.Children.Add(button); return button; }
            var pause = Add("暂停", "Pause", (_, _) => storeDownloads.Pause(task.Id));
            var resume = Add("继续", "Resume", (_, _) => storeDownloads.Resume(task.Id));
            var cancel = Add("取消", "Cancel", (_, _) => { approvedDownloadInstalls.Remove(task.Id); storeDownloads.Cancel(task.Id); });
            var retry = Add("重试", "Retry", (_, _) => storeDownloads.Retry(task.Id));
            var install = Add("安装", "Install", async (_, _) => await InstallStoreTaskAsync(task.Id));
            downloadRows[task.Id] = (state, progress, pause, resume, cancel, retry, install);
            content.Children.Add(controls); storeBody.Children.Add(StoreCard(content));
        }
        UpdateDownloadRows();
    }
    private void UpdateDownloadRows()
    {
        if (storeDownloads is null || storeSection != "downloads") return;
        var tasks = storeDownloads.Snapshot();
        if (tasks.Count != downloadRows.Count || tasks.Any(task => !downloadRows.ContainsKey(task.Id))) { RenderDownloadTasks(); return; }
        foreach (var task in tasks)
        {
            var row = downloadRows[task.Id];
            row.Status.Text = DownloadName(task.State) + $" · {task.BytesReceived:N0} / {task.TotalBytes:N0} 字节" + (task.State == DownloadState.Downloading ? $" · {task.BytesPerSecond / 1024:N1} KiB/s" : "") +
                (task.EstimatedRemaining is { } eta && task.State == DownloadState.Downloading ? $" · 约 {eta.TotalSeconds:N0} 秒" : "") + (task.ErrorCode is { } code ? "\n" + StoreError(code) : "") +
                "\n连接：" + (isolatedStoreSource ? "受控本地 HTTP" : task.Route) + (task.RetryAfterUtc is { } retry ? " · 可在 " + retry.ToLocalTime().ToString("HH:mm:ss") + " 后重试" : "");
            row.Progress.Value = task.BytesReceived;
            row.Pause.IsEnabled = task.State is DownloadState.Downloading or DownloadState.Queued or DownloadState.Verifying;
            row.Resume.IsEnabled = task.State == DownloadState.Paused;
            row.Cancel.IsEnabled = task.State is not (DownloadState.Completed or DownloadState.Cancelled or DownloadState.Installing);
            row.Retry.IsEnabled = task.State is DownloadState.Failed or DownloadState.Cancelled;
            row.Install.IsEnabled = !installingStoreTask && task.State == DownloadState.AwaitingInstall;
        }
    }
    private static string DownloadName(DownloadState state) => state switch { DownloadState.Queued => "排队中", DownloadState.Downloading => "下载中", DownloadState.Paused => "已暂停", DownloadState.Verifying => "校验中", DownloadState.AwaitingInstall => "等待安装", DownloadState.Installing => "安装中", DownloadState.Completed => "已安装", DownloadState.Cancelled => "已取消", _ => "失败" };
    private static string SafeStoreCode(Exception error) => error is CatalogException ce ? ce.Code : error is PackageException pe ? pe.Code : error is OperationCanceledException ? "USER_CANCELLED" :
        error.Message.Length is > 0 and < 80 && error.Message.All(c => char.IsAsciiLetterUpper(c) || char.IsAsciiDigit(c) || c == '_') ? error.Message : "STORE_OPERATION_FAILED";
}
