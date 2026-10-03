using AutumnOS.Contracts;
using AutumnOS.Packages;
using AutumnOS.Store;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private sealed record ManagedApplication(WebAppHost Host, Grid Content, string Name);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ManagedApplication> managedApplications = new();
    private readonly Dictionary<Button, (string AppId, Border Badge)> managedBadges = [];
    private readonly Grid managedContent = new();
    private readonly TextBlock managedTitle = new() { FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private string? visibleManagedApp;
    private bool localInstallBusy;

    private bool StoreHasInstalledRepository(long id) => applicationInstaller?.GetInstalled().Any(a => a.Source.RepositoryId == id && a.Source.Kind == (isolatedStoreSource ? "test" : "github")) == true;
    private bool StoreHasUpdate(CatalogDetails? details) => details?.Store is { } store && applicationInstaller?.Find(store.AppId) is { } app &&
        StoreSourceMatches(app, details.Repository) && details.Versions.Any(v => v.CanInstall && (!v.Prerelease || app.AllowPreview) && ApplicationInstallService.CompareVersions(v.Version, app.Package.Manifest.Version) > 0);
    private bool StoreSourceMatches(InstalledApplication app, CatalogRepository repository) => app.Source.Kind == (isolatedStoreSource ? "test" : "github") && app.Source.RepositoryId == repository.RepositoryId;
    private string StoreInstalledLabel(CatalogDetails? details) => details?.Store is { } store && applicationInstaller?.Find(store.AppId) is { } app
        ? !StoreSourceMatches(app, details.Repository) ? "应用 ID 与已安装来源冲突" : StoreHasUpdate(details) ? "有更新" : "已安装 " + app.Package.Manifest.Version : "查看版本  ›";
    private bool StoreAllowsPreview(CatalogDetails details) => details.Store is { } store && applicationInstaller?.Find(store.AppId) is { } app && StoreSourceMatches(app, details.Repository) && app.AllowPreview;
    private string StorePermissionChanges(ReleaseManifest release)
    {
        var current = applicationInstaller?.Find(release.AppId);
        var added = release.Permissions.Except(current?.Package.Manifest.Permissions ?? []).ToArray();
        return current is null ? "敏感权限将在实际使用时单独询问。" : added.Length == 0 ? "声明权限未增加。" : "新增权限：" + string.Join("、", added.Select(PermissionTitle)) + "；旧授权不会自动放行。";
    }
    private string StoreVersionActionLabel(CatalogDetails details, CatalogVersion version) => version.Manifest is { } release && applicationInstaller?.Find(release.AppId) is { } app
        ? !StoreSourceMatches(app, details.Repository) ? "来源冲突，无法替换" : release.Version == app.Package.Manifest.Version ? "已安装 · 查看任务" : ApplicationInstallService.CompareVersions(release.Version, app.Package.Manifest.Version) < 0 ? "下载此历史版本" : "下载更新" : "下载并安装";

    private void RenderInstalledApplications()
    {
        storeBody.Children.Clear();
        storeStatus.Text = "安装文件与存档分开管理。固定版本和预览开关只作用于当前应用。";
        var installed = applicationInstaller?.GetInstalled() ?? [];
        foreach (var app in installed)
        {
            var row = new StackPanel { Spacing = 12 };
            row.Children.Add(new TextBlock { Text = app.Package.Manifest.Name, FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            row.Children.Add(Paragraph(app.Package.Manifest.Version + " · " + app.AppId + "\n" + app.Source.Kind + " · " + (app.Source.RepositoryName ?? "本地安装") + (IsManagedRunning(app.AppId) ? " · 运行中" : "")));
            var pin = new CheckBox { Content = "固定当前版本", IsChecked = app.PinnedVersion is not null };
            var preview = new CheckBox { Content = "允许此应用的预览版本", IsChecked = app.AllowPreview };
            AutomationProperties.SetAutomationId(pin, "AppPin-" + app.AppId); AutomationProperties.SetAutomationId(preview, "AppPreview-" + app.AppId);
            void SavePolicy(object sender, RoutedEventArgs args)
            {
                try { applicationInstaller!.SetVersionPolicy(app.AppId, pin.IsChecked == true, preview.IsChecked == true); storeStatus.Text = "应用版本设置已保存。"; }
                catch (Exception error) when (error is not OutOfMemoryException) { storeStatus.Text = StoreError(SafeStoreCode(error)); }
            }
            pin.Checked += SavePolicy; pin.Unchecked += SavePolicy; preview.Checked += SavePolicy; preview.Unchecked += SavePolicy;
            row.Children.Add(pin); row.Children.Add(preview);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(ActionButton("打开", "InstalledOpen-" + app.AppId, async (_, _) => await OpenInstalledApplicationAsync(app.AppId)));
            actions.Children.Add(ActionButton("修复当前版本", "InstalledRepair-" + app.AppId, async (_, _) => await RepairInstalledApplicationAsync(app)));
            actions.Children.Add(ActionButton("卸载", "InstalledUninstall-" + app.AppId, async (_, _) => await UninstallApplicationAsync(app)));
            if (app.Source.RepositoryId is { } repoId && app.Source.RepositoryName?.Split('/') is [var owner, var name])
                actions.Children.Add(ActionButton("查看版本", "InstalledVersions-" + app.AppId, async (_, _) => await OpenStoreDetailsAsync(new(repoId, owner, name, "", [], CatalogCategory.Unclassified))));
            row.Children.Add(actions);
            if (app.Source.Kind is not "bundled" && !CanRunControlledPackage(app)) row.Children.Add(Paragraph("运行受限：一般第三方 WebView 网络隔离尚未完成验收。安装和版本管理可用，当前不会执行此应用代码。"));
            storeBody.Children.Add(StoreCard(row));
        }
        if (installed.Length == 0) storeBody.Children.Add(StoreEmpty("还没有安装应用", "可以从发现页查看版本，或选择本地 .autumn。卸载不会删除你的存档。"));
    }
    private async Task UninstallApplicationAsync(InstalledApplication app)
    {
        if (aboutOpen || applicationInstaller is null) return;
        aboutOpen = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "卸载 " + app.Package.Manifest.Name + "？", Content = "移除应用的安装注册和桌面入口，保留所有账号与游客的存档。旧安装资源作为恢复缓存保留，不会立即释放全部磁盘空间。运行中的应用须先保存并结束。", PrimaryButtonText = "卸载并保留数据", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await Task.Run(() => applicationInstaller.Uninstall(app.AppId), lifetime.Token);
            storeStatus.Text = "已卸载，存档保留。";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { storeStatus.Text = StoreError(SafeStoreCode(error)); }
        finally { aboutOpen = false; }
    }
    private async Task RepairInstalledApplicationAsync(InstalledApplication app)
    {
        if (applicationInstaller is null || localInstallBusy) return;
        localInstallBusy = true;
        try
        {
            var cached = storeDownloads?.Snapshot().FirstOrDefault(task => task.Request.AppId == app.AppId && task.Request.Version == app.Package.Manifest.Version && task.Request.Sha256 == app.Package.PackageSha256 && task.LocalPath is not null);
            if (cached is not null) { await InstallStoreTaskAsync(cached.Id, repair: true); return; }
            // A repair only accepts the identical installed version and digest, never arbitrary latest bytes.
            string? path = app.Source.Kind == "bundled" ? Path.Combine(AppContext.BaseDirectory, "Samples", "element-pairs.autumn") : await PickDeveloperFileAsync(".autumn", lifetime.Token);
            if (path is null) return;
            var m = app.Package.Manifest;
            var expected = new ExpectedPackageIdentity(app.Source, app.AppId, m.Version, m.Runtime, m.Entry, m.Permissions, app.Package.PackageSha256, new FileInfo(path).Length, m.SaveFormatVersion, app.Provenance);
            await Task.Run(() => applicationInstaller.Install(path, expected, InstallIntent.Repair, sourceConfirmed: true, cancellationToken: lifetime.Token), lifetime.Token);
            storeStatus.Text = "当前版本已按原摘要修复，存档保留。";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { storeStatus.Text = StoreError(SafeStoreCode(error)); }
        finally { localInstallBusy = false; }
    }
    private async void OnLocalPackageInstall(object sender, RoutedEventArgs e)
    {
        if (localInstallBusy) return;
        try { string? path = await PickDeveloperFileAsync(".autumn", lifetime.Token); if (path is not null) await InstallLocalPackageAsync(path); }
        catch (Exception error) when (error is not OutOfMemoryException) { storeStatus.Text = StoreError(SafeStoreCode(error)); }
    }
    private void OnStoreDragOver(object sender, DragEventArgs e)
    { if (e.DataView.Contains(StandardDataFormats.StorageItems)) { e.AcceptedOperation = DataPackageOperation.Copy; e.DragUIOverride.Caption = "检查并安装 .autumn"; } }
    private async void OnStoreDrop(object sender, DragEventArgs e)
    {
        var deferral = e.GetDeferral();
        try
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            var items = await e.DataView.GetStorageItemsAsync();
            if (items.Count != 1 || items[0] is not Windows.Storage.StorageFile file || !file.Path.EndsWith(".autumn", StringComparison.OrdinalIgnoreCase))
            { storeStatus.Text = "PACKAGE_EXTENSION_INVALID · 一次选择一个 .autumn；不导入 EXE。"; return; }
            await InstallLocalPackageAsync(file.Path);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { storeStatus.Text = StoreError(SafeStoreCode(error)); }
        finally { deferral.Complete(); }
    }
    private async Task InstallLocalPackageAsync(string path)
    {
        if (localInstallBusy || aboutOpen || applicationInstaller is null) return;
        localInstallBusy = true;
        try
        {
            var package = await Task.Run(() => PackageInstaller.Inspect(path, lifetime.Token), lifetime.Token);
            var old = applicationInstaller.Find(package.Manifest.AppId);
            bool downgrade = old is not null && ApplicationInstallService.CompareVersions(package.Manifest.Version, old.Package.Manifest.Version) < 0;
            aboutOpen = true;
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "安装本地应用？", Content = $"{package.Manifest.Name} · {package.Manifest.Version}\n来源：用户选择的本地 .autumn\n权限：{string.Join("、", package.Manifest.Permissions.Select(PermissionTitle))}\n\n安装不等于授权；已存在的不同来源不能被接管。" + (downgrade ? "\n降级前将检查真实存档并备份，不兼容时阻止。" : ""), PrimaryButtonText = downgrade ? "确认检查并降级" : "确认安装", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var m = package.Manifest;
            var expected = new ExpectedPackageIdentity(new("local"), m.AppId, m.Version, m.Runtime, m.Entry, m.Permissions, package.Sha256, new FileInfo(path).Length, m.SaveFormatVersion);
            await Task.Run(() => applicationInstaller.Install(path, expected, downgrade ? InstallIntent.Downgrade : InstallIntent.InstallOrUpdate, true, downgrade, lifetime.Token), lifetime.Token);
            ShowStoreSection("installed"); storeStatus.Text = "安装已提交，桌面入口已注册；存档保留。";
        }
        catch (Exception error) when (error is not OutOfMemoryException) { storeStatus.Text = StoreError(SafeStoreCode(error)); }
        finally { localInstallBusy = false; aboutOpen = false; }
    }

    private bool IsManagedRunning(string appId) => appId == "cn.labchronicles.elementpairs" ? HasLiveGame : managedApplications.TryGetValue(appId, out var running) && running.Host.Session.Instance.BlocksMaintenance;
    private void BackgroundManagedApps() { foreach (var app in managedApplications.Values) app.Host.Background(); }
    private void CloseManagedApplications() { foreach (var app in managedApplications.Values) app.Host.Dispose(); managedApplications.Clear(); managedContent.Children.Clear(); UpdateManagedBadges(); }
    private void InitializeManagedAppUi()
    {
        ManagedAppPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); ManagedAppPanel.RowDefinitions.Add(new RowDefinition());
        var bar = new Grid { Padding = new Thickness(20, 12, 20, 12), ColumnSpacing = 18 };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); bar.ColumnDefinitions.Add(new ColumnDefinition()); bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.Children.Add(ActionButton("‹  返回桌面", "ManagedAppHome", OnBackground));
        Grid.SetColumn(managedTitle, 1); bar.Children.Add(managedTitle);
        var close = ActionButton("结束游戏", "ManagedAppClose", async (_, _) => { if (visibleManagedApp is { } id && managedApplications.TryGetValue(id, out var app)) await CloseManagedApplicationAsync(id, app); });
        Grid.SetColumn(close, 2); bar.Children.Add(close); ManagedAppPanel.Children.Add(bar);
        Grid.SetRow(managedContent, 1); ManagedAppPanel.Children.Add(managedContent);
    }
    private async Task OpenInstalledApplicationAsync(string appId)
    {
        if (appId == "cn.labchronicles.elementpairs") { OnSample(StorePanel, new RoutedEventArgs()); return; }
        if (launching || accountOperation || aboutOpen || permissionService is null || CurrentIdentity.State == IdentitySessionState.SigningIn || applicationInstaller?.Find(appId) is not { } installed) return;
        if (!CanRunControlledPackage(installed))
        {
            aboutOpen = true;
            try { await new ContentDialog { XamlRoot = Root.XamlRoot, Title = "此应用暂时不能运行", Content = "应用已安装。一般第三方 WebView 的完整网络隔离仍待 T01 验证，当前保留运行限制。PM/SQ 分类不改变此限制。", CloseButtonText = "知道了" }.ShowAsync(); }
            finally { aboutOpen = false; }
            return;
        }
        if (managedApplications.TryGetValue(appId, out var existing) && existing.Host.Session.Instance.BlocksMaintenance) { ActivateManagedApplication(appId, existing); return; }
        launching = true;
        try
        {
            existing?.Host.Dispose();
            if (existing is not null) managedContent.Children.Remove(existing.Content);
            var container = new Grid(); var lease = applicationInstaller.EnterRuntimeLease(appId);
            WebAppHost host;
            try
            {
                installed = applicationInstaller.Find(appId) ?? throw new PackageException("APP_NOT_INSTALLED");
                if (!CanRunControlledPackage(installed)) throw new PackageException("RUNTIME_COMMUNITY_NOT_VERIFIED");
                host = new WebAppHost(installationRoot, container, RequestSavePermissionAsync, _ => UpdateManagedBadges(), CreateRuntimeServices, CloseFileCapabilities,
                    sdkTrace: RecordDeveloperSdkCall, registeredPackage: installed.Package, runtimeLease: lease, maintenance: criticalOperations, retainForCleanup: RetainUnreleasedHost);
            }
            catch { lease.Dispose(); throw; }
            var app = new ManagedApplication(host, container, installed.Package.Manifest.Name); managedApplications[appId] = app; managedContent.Children.Add(container);
            ActivateManagedApplication(appId, app); await host.StartAsync(lifetime.Token); RefreshPermissionsUi();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            if (managedApplications.TryRemove(appId, out var failed)) { failed.Host.Dispose(); managedContent.Children.Remove(failed.Content); }
            ShowPage(DesktopPanel); DesktopError.Title = "应用启动未完成"; DesktopError.Message = SafeStoreCode(error); DesktopError.IsOpen = true;
        }
        finally { launching = false; UpdateManagedBadges(); }
    }
    private void ActivateManagedApplication(string id, ManagedApplication app)
    {
        appHost?.Background(); developerPreviewHost?.Background(); BackgroundManagedApps();
        foreach (var item in managedApplications.Values) item.Content.Visibility = ReferenceEquals(item, app) ? Visibility.Visible : Visibility.Collapsed;
        visibleManagedApp = id; managedTitle.Text = app.Name; app.Host.Foreground(); ShowPage(ManagedAppPanel);
    }
    private async Task CloseManagedApplicationAsync(string id, ManagedApplication captured)
    {
        if (aboutOpen || launching) return; aboutOpen = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "结束 " + captured.Name + "？", Content = "请先在游戏中保存进度。未保存的输入会丢失，已保存的内容保留。", PrimaryButtonText = "确认结束", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !managedApplications.TryGetValue(id, out var current) || !ReferenceEquals(current, captured)) return;
            captured.Host.Dispose(); managedApplications.TryRemove(id, out _); managedContent.Children.Remove(captured.Content); ShowPage(DesktopPanel); UpdateManagedBadges();
        }
        finally { aboutOpen = false; }
    }
    private void AddManagedDesktopEntries()
    {
        int cell = 6;
        foreach (var app in applicationInstaller?.GetInstalled().Where(app => app.AppId != "cn.labchronicles.elementpairs") ?? [])
        {
            while (AppGrid.RowDefinitions.Count <= cell / 3) AppGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var button = AppButton(app.Package.Manifest.Name, "managed", "InstalledApp-" + app.AppId, false, OnManagedIconClick);
            button.Tag = app.AppId; Grid.SetRow(button, cell / 3); Grid.SetColumn(button, cell++ % 3); AppGrid.Children.Add(button);
            if (button.Content is StackPanel stack && stack.Children[0] is Grid layer && layer.Children[1] is Border badge) managedBadges[button] = (app.AppId, badge);
        }
        UpdateManagedBadges();
    }
    private async void OnManagedIconClick(object sender, RoutedEventArgs e)
    { if (!ConsumeHeldClick(sender) && sender is Button { Tag: string id }) await OpenInstalledApplicationAsync(id); }
    private void UpdateManagedBadges()
    { foreach (var (button, value) in managedBadges) { bool running = IsManagedRunning(value.AppId); value.Badge.Visibility = running ? Visibility.Visible : Visibility.Collapsed; AutomationProperties.SetHelpText(button, running ? "运行中；单击继续，双击桌面空白处查看后台，长按或右键管理" : "打开已安装应用，拖动调整桌面顺序"); } RefreshRunningSwitcherIfOpen(); }
    private void FillManagedMenu(MenuFlyout menu, Button button)
    {
        if (button.Tag is not string id) return;
        var open = new MenuFlyoutItem { Text = IsManagedRunning(id) ? "继续游戏" : "打开" };
        open.Click += async (_, _) => { heldButtons.Remove(button); await OpenInstalledApplicationAsync(id); }; menu.Items.Add(open);
        if (managedApplications.TryGetValue(id, out var app) && app.Host.Session.Instance.BlocksMaintenance)
        { var end = new MenuFlyoutItem { Text = "结束游戏" }; end.Click += async (_, _) => await CloseManagedApplicationAsync(id, app); menu.Items.Add(end); }
    }
}
