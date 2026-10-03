using AutumnOS.Contracts;
using AutumnOS.Identity;
using AutumnOS.Packages;
using AutumnOS.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace AutumnOS.Shell;

public sealed partial class MainWindow : Window
{
    private readonly InstallationRoot installationRoot = InstallationRoot.ForCurrentProcess();
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly Windows.UI.ViewManagement.UISettings systemUiSettings = new();
    private LogtoConfigurationResult? configuration;
    private FirstRunStateStore? stateStore;
    private DesktopPreferencesStore? preferencesStore;
    private InstalledPackage? installedSample;
    private string theme = "light", wallpaper = "warm";
    private bool preferencesWritable, loaded, preparing, aboutOpen, launching;
    private WebAppHost? appHost;
    private Button? sampleButton, dockSampleButton;

    public MainWindow()
    {
        InitializeComponent();
        if (AutumnOS.UpdateProtocol.UpdateLaunchGuard.IsHealthStart) InteractionRoot.IsEnabled = false;
        InitializeDesktopInteractions();
        InitializeAccountUi();
        InitializeStoreUi();
        InitializeUpdateUi();
        Closed += (_, _) => CloseUpdateServices();
        AboutProductName.Text = BrandInfo.ProductName;
        AboutProducer.Text = BrandInfo.ProducerCredit;
        AboutVersion.Text = BrandInfo.DisplayVersion;
        AboutBuild.Text = BrandInfo.BuildId;
        SelectSettingsCategory("appearance");
        Title = BrandInfo.ProductName;
        ProductTitle.Text = BrandInfo.DisplayName;
        ProducerText.Text = DesktopProducer.Text = WelcomeProducer.Text = BrandInfo.ProducerCredit;
        WelcomeBrand.Text = DesktopBrand.Text = BrandInfo.DisplayName;
        WelcomeFullBrand.Text = BrandInfo.ProductName;
        Root.ActualThemeChanged += (_, _) => { ApplyWallpaper(); SynchronizeAppearance(); };
        systemUiSettings.AnimationsEnabledChanged += OnSystemAnimationsChanged;
        clockTimer.Tick += (_, _) => UpdateClock();
        var display = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = Math.Min(900, work.Width);
            presenter.PreferredMinimumHeight = Math.Min(680, work.Height);
        }
        int width = Math.Min(1180, Math.Max(320, work.Width - 48));
        int height = Math.Min(880, Math.Max(320, work.Height - 48));
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(work.X + (work.Width - width) / 2,
            work.Y + (work.Height - height) / 2, width, height));
        Closed += (_, _) => { HideRunningOperations(); clockTimer.Stop(); systemUiSettings.AnimationsEnabledChanged -= OnSystemAnimationsChanged; foreach (var timer in pressTimers) timer.Stop(); lifetime.Cancel(); CloseStoreServices(); CloseDeveloperCapabilities(); appHost?.Dispose(); foreach (var broker in fileBrokers.Values) broker.Dispose(); fileBrokers.Clear(); identityService?.Dispose(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (loaded) return;
        loaded = true;
        Root.XamlRoot.Changed += (_, _) => SynchronizeAppearance();
        await LoadDesktopAsync();
    }

    private async Task LoadDesktopAsync()
    {
        configuration = LogtoConfigurationLoader.Load(Path.Combine(AppContext.BaseDirectory, "config", "logto.public.json"));
        ConfigurationText.Text = configuration.SafeSummary;
        var registration = RegistrationStatusLoader.Load(Path.Combine(AppContext.BaseDirectory, "config", "registration.json"), configuration.Options?.ClientId);
        RegistrationText.Text = registration.IsReadable
            ? $"Native 类型：{registration.Status!.ApplicationTypeObserved}；登录回调：{registration.Status.RedirectUriStatus}；退出回调：{registration.Status.PostLogoutRedirectUriStatus}。登记记录不代表当前已登录。"
            : registration.SafeSummary;
        DiscoveryButton.IsEnabled = configuration.IsValid;
        if (!CheckData()) return;
        await InitializeIdentityAsync();
        InitializeRuntimeServices();
        InitializeDeveloperTools();
        InitializeDataRecoveryUi();
        InitializeStoreServices();
        preferencesStore = new DesktopPreferencesStore(installationRoot, criticalOperations);
        LoadDesktopLayout();
        var preferences = preferencesStore.Load();
        preferencesWritable = preferences.Success;
        if (preferences.Success) { theme = preferences.State!.Theme; wallpaper = preferences.State.Wallpaper; }
        PreferencesStatus.Text = preferences.Success ? "外观已载入；更改会自动保存在这台电脑上。" : $"{preferences.ErrorCode} · {preferences.RecoveryMessage} 原文件已保留。";
        ApplyPreferences();
        UpdateClock();
        clockTimer.Start();
        var checkpoint = stateStore!.Load();
        if (!checkpoint.Success) { StartupError(checkpoint.ErrorCode + " · " + checkpoint.RecoveryMessage); return; }
        if (checkpoint.State!.IsComplete)
        {
            ShowPage(DesktopPanel);
            await PrepareInstalledAppsAsync();
        }
        else ShowWelcome(checkpoint.State.Checkpoint);
        await ConfirmUpdateHealthAsync();
        InitializeUpdateServices();
    }

    private bool CheckData()
    {
        var result = installationRoot.EnsureCreated();
        DataPath.Text = installationRoot.DataDirectory;
        DataStatus.Severity = result.Success ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        DataStatus.Title = result.Success ? "本地数据空间已就绪" : "无法准备本地数据空间";
        DataStatus.Message = result.Success ? "已有数据会保留在程序旁的 AutumnOS_Data。" : $"{result.ErrorCode} · {result.RecoveryMessage}";
        InitializeButton.IsEnabled = DiagnosticSampleButton.IsEnabled = result.Success;
        if (!result.Success) { StartupError(result.ErrorCode + " · " + result.RecoveryMessage); return false; }
        LauncherDiagnostics.UseValidatedDirectory(installationRoot.Directories["Logs"]);
        new StructuredLog(installationRoot).Write(DiagnosticEvent.StartupDataReady);
        stateStore = new FirstRunStateStore(installationRoot, criticalOperations);
        var current = stateStore.Load();
        ShowCheckpoint(current);
        if (!current.Success) { StartupError(current.ErrorCode + " · " + current.RecoveryMessage); return false; }
        WelcomeError.Text = "";
        WelcomeRetry.Visibility = Visibility.Collapsed;
        return true;
    }

    private void ShowCheckpoint(FirstRunResult result)
    {
        if (!result.Success)
        {
            new StructuredLog(installationRoot).Write(DiagnosticEvent.FirstRunStateRejected, result.ErrorCode);
            CheckpointText.Text = $"{result.ErrorCode} · {result.RecoveryMessage}";
            InitializeButton.IsEnabled = false;
            return;
        }
        CheckpointText.Text = result.State!.IsComplete ? "基础初始化已完成。再次启动会保留这份记录。" : $"初始化记录：{result.State.Checkpoint}";
        InitializeButton.IsEnabled = !result.State.IsComplete;
    }

    private void StartupError(string message)
    {
        WelcomeError.Text = message;
        WelcomeRetry.Visibility = Visibility.Visible;
        ShowPage(WelcomePanel);
    }

    private void ShowWelcome(FirstRunCheckpoint checkpoint)
    {
        HelloStage.Visibility = checkpoint == FirstRunCheckpoint.Hello ? Visibility.Visible : Visibility.Collapsed;
        BrandStage.Visibility = checkpoint == FirstRunCheckpoint.Brand ? Visibility.Visible : Visibility.Collapsed;
        InitializeStage.Visibility = checkpoint == FirstRunCheckpoint.Initializing ? Visibility.Visible : Visibility.Collapsed;
        ShowPage(WelcomePanel);
    }

    private void OnWelcomeNext(object sender, RoutedEventArgs e)
    {
        var next = (sender as Button)?.Tag as string == "brand" ? FirstRunCheckpoint.Brand : FirstRunCheckpoint.Initializing;
        var result = stateStore?.Advance(next);
        if (result?.Success != true) { StartupError(result?.RecoveryMessage ?? "本地空间尚未准备好，请重试。"); return; }
        new StructuredLog(installationRoot).Write(DiagnosticEvent.FirstRunCheckpointSaved);
        ShowCheckpoint(result);
        ShowWelcome(next);
    }

    private async void OnInitialize(object sender, RoutedEventArgs e)
    {
        if (stateStore is null || preparing) return;
        preparing = true;
        PrepareDesktopButton.IsEnabled = InitializeButton.IsEnabled = false;
        InitializeProgress.Visibility = Visibility.Visible;
        InitializeProgress.IsActive = true;
        try
        {
            foreach (var next in new[] { FirstRunCheckpoint.Brand, FirstRunCheckpoint.Initializing })
            {
                var current = stateStore.Load();
                if (!current.Success) { StartupError(current.RecoveryMessage); return; }
                if (current.State!.Checkpoint >= next) continue;
                var advance = stateStore.Advance(next);
                if (!advance.Success) { StartupError(advance.RecoveryMessage); return; }
            }
            if (!SavePreferences(theme, wallpaper)) { StartupError(PreferencesStatus.Text); return; }
            if (!await PrepareInstalledAppsAsync()) { StartupError(DesktopError.Message); return; }
            var result = stateStore.Advance(FirstRunCheckpoint.Completed);
            ShowCheckpoint(result);
            if (!result.Success) { StartupError(result.RecoveryMessage); return; }
            new StructuredLog(installationRoot).Write(DiagnosticEvent.FirstRunCheckpointSaved);
            ShowPage(DesktopPanel);
        }
        finally { preparing = false; InitializeProgress.IsActive = false; InitializeProgress.Visibility = Visibility.Collapsed; PrepareDesktopButton.IsEnabled = true; }
    }

    private async Task<bool> PrepareInstalledAppsAsync()
    {
        try
        {
            // Install returns only after a real installation record and every resource
            // have been committed/verified. Desktop entries come from that result.
            if (applicationInstaller is null) throw new PackageException("PACKAGE_REGISTRY_UNAVAILABLE");
            if (!applicationInstaller.HasRegistration("cn.labchronicles.elementpairs"))
            {
                var bundled = await Task.Run(() => PackageInstaller.Install(Path.Combine(AppContext.BaseDirectory, "Samples", "element-pairs.autumn"), installationRoot.Directories["Apps"], lifetime.Token), lifetime.Token);
                if (bundled.Manifest.AppId != "cn.labchronicles.elementpairs") throw new PackageException("PACKAGE_APP_ID_INVALID");
                applicationInstaller.RegisterExisting(bundled);
            }
            installedSample = applicationInstaller.Find("cn.labchronicles.elementpairs")?.Package;
            DesktopError.IsOpen = false;
            BuildAppEntries();
            RefreshPermissionsUi();
            RefreshDataRecoveryUi();
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            installedSample = null;
            BuildAppEntries();
            DesktopError.Title = "应用暂时无法打开";
            DesktopError.Message = (error is PackageException packageError ? packageError.Code : "APP_PREPARATION_FAILED") + " · 已保留原有文件，请到设置中的开发者诊断检查。";
            DesktopError.IsOpen = true;
            return false;
        }
    }

    private async void OnRetry(object sender, RoutedEventArgs e) { if (!preparing) await LoadDesktopAsync(); }
    private void OnSettings(object sender, RoutedEventArgs e)
    {
        if (ConsumeHeldClick(sender)) return;
        BackgroundManagedApps();
        if (appHost?.Session.Instance.State == AppLifecycleState.Foreground) appHost.Background();
        if (developerPreviewHost?.Session.Instance.State == AppLifecycleState.Foreground) developerPreviewHost.Background();
        ShowPage(SettingsPanel);
    }
    private void OnDiagnostics(object sender, RoutedEventArgs e) { SelectSettingsCategory("diagnostics"); ShowPage(SettingsPanel); }
    private void OnTheme(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is string value) SavePreferences(value, wallpaper); }
    private void OnWallpaper(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is string value) SavePreferences(theme, value); }

    private bool SavePreferences(string nextTheme, string nextWallpaper)
    {
        if (preferencesStore is null || !preferencesWritable)
        {
            if (WelcomePanel.Visibility == Visibility.Visible) WelcomeError.Text = PreferencesStatus.Text;
            return false;
        }
        var result = preferencesStore.Save(nextTheme, nextWallpaper);
        if (!result.Success)
        {
            PreferencesStatus.Text = result.ErrorCode + " · " + result.RecoveryMessage;
            if (WelcomePanel.Visibility == Visibility.Visible) WelcomeError.Text = PreferencesStatus.Text;
            return false;
        }
        WelcomeError.Text = "";
        theme = result.State!.Theme;
        wallpaper = result.State.Wallpaper;
        PreferencesStatus.Text = "已保存 · " + ThemeName(theme) + " / " + WallpaperName(wallpaper) + "。下次打开时依然如此。";
        ApplyPreferences();
        SynchronizeAppearance();
        return true;
    }

    private void ShowPage(Grid page)
    {
        CancelDesktopDrag();
        HideRunningOperations();
        foreach (var menu in appMenus) menu.Hide();
        foreach (var panel in new[] { WelcomePanel, DesktopPanel, SettingsPanel, AppPanel, DeveloperPanel, StorePanel, ManagedAppPanel }) panel.Visibility = panel == page ? Visibility.Visible : Visibility.Collapsed;
        if (systemUiSettings.AnimationsEnabled)
        {
            var animation = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(220)) };
            Storyboard.SetTarget(animation, page);
            Storyboard.SetTargetProperty(animation, "Opacity");
            var storyboard = new Storyboard(); storyboard.Children.Add(animation); storyboard.Begin();
        }
    }

    private async void OnDiscovery(object sender, RoutedEventArgs e)
    {
        if (configuration?.Options is not { } options || !configuration.IsValid) return;
        DiscoveryButton.IsEnabled = false;
        DiscoveryText.Text = "正在通过 HTTPS 检测公开发现文档…";
        try { var result = await LogtoDiscoveryProbe.ProbeAsync(options, lifetime.Token); DiscoveryText.Text = result.SafeSummary + "（此检测不证明登录或回调登记成功。）"; }
        catch (OperationCanceledException) { DiscoveryText.Text = "检测已取消。"; }
        finally { if (!lifetime.IsCancellationRequested) DiscoveryButton.IsEnabled = true; }
    }

    private async void OnAbout(object sender, RoutedEventArgs e)
    {
        if (aboutOpen) return;
        aboutOpen = true;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = BrandInfo.ProductName,
            Content = $"{BrandInfo.ProducerCredit}\n版本 {BrandInfo.DisplayVersion}\n构建 {BrandInfo.BuildId}\n\nC# / .NET 10 · WinUI 3\n本地开发版：桌面、个性化与元素配对。账户、商店、下载和更新仍在开发中。", CloseButtonText = "关闭" };
        try { await dialog.ShowAsync(); } finally { aboutOpen = false; }
    }

    private async void OnSample(object sender, RoutedEventArgs e)
    {
        if (ConsumeHeldClick(sender) || launching || accountOperation || permissionService is null || identityService?.Snapshot.State == IdentitySessionState.SigningIn || installedSample is null) return;
        if (appHost is not null && appHost.Session.Instance.State is not (AppLifecycleState.Crashed or AppLifecycleState.Closed))
        { BackgroundManagedApps(); appHost.Foreground(); ShowPage(AppPanel); return; }
        launching = true;
        try
        {
            appHost?.Dispose();
            BackgroundManagedApps();
            var lease = applicationInstaller?.EnterRuntimeLease(installedSample.Manifest.AppId) ?? throw new PackageException("PACKAGE_REGISTRY_UNAVAILABLE");
            try
            {
                installedSample = applicationInstaller!.Find("cn.labchronicles.elementpairs")?.Package ?? throw new PackageException("APP_NOT_INSTALLED");
                appHost = new WebAppHost(installationRoot, GameContent, RequestSavePermissionAsync, UpdateRuntimeStatus, CreateRuntimeServices, CloseFileCapabilities, sdkTrace: RecordDeveloperSdkCall, registeredPackage: installedSample, runtimeLease: lease, maintenance: criticalOperations, retainForCleanup: RetainUnreleasedHost);
            }
            catch { lease.Dispose(); throw; }
            GameTitle.Text = installedSample.Manifest.Name;
            ShowPage(AppPanel);
            await appHost.StartAsync(lifetime.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            appHost?.Dispose(); appHost = null; ShowPage(DesktopPanel);
            var code = error is PackageException packageError ? packageError.Code : "RUNTIME_START_FAILED";
            UpdateRuntimeStatus("无游戏会话");
            DesktopError.Title = "应用暂时无法打开"; DesktopError.Message = code + " · 请检查 WebView2 和本地数据目录后重试。"; DesktopError.IsOpen = true;
        }
        finally { launching = false; }
    }

    private void UpdateRuntimeStatus(string text)
    {
        RuntimeStatus.Text = SessionSummary.Text = text;
        UpdateRunningIndicators();
    }

    private void OnBackground(object sender, RoutedEventArgs e)
    { if (ConsumeHeldClick(sender)) return; appHost?.Background(); developerPreviewHost?.Background(); BackgroundManagedApps(); ShowPage(DesktopPanel); }

    private async void OnCloseGame(object sender, RoutedEventArgs e)
    {
        if (appHost is { } current) await CloseCapturedGameAsync(current);
    }

    private async Task CloseCapturedGameAsync(WebAppHost closingHost)
    {
        if (aboutOpen || launching || !IsCurrentLiveHost(closingHost)) return;
        var closingInstance = closingHost.Session.Instance.Id;
        HideRunningOperations();
        aboutOpen = true;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "结束元素配对？", Content = "请先在游戏中保存进度。结束后，尚未保存的更改会丢失；已保存的进度会保留。", PrimaryButtonText = "确认结束", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || !IsCurrentLiveHost(closingHost) || closingHost.Session.Instance.Id != closingInstance) return;
            closingHost.Dispose(); appHost = null; UpdateRuntimeStatus("无游戏会话"); ShowPage(DesktopPanel);
        }
        finally { aboutOpen = false; }
    }

    private Task<bool> RequestSavePermissionAsync(string name, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            if (token.IsCancellationRequested || aboutOpen || appHost?.Session.Instance.State != AppLifecycleState.Foreground) { completion.TrySetResult(false); return; }
            aboutOpen = true;
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "允许元素配对读写自己的存档？", Content = "本次游戏会话可以在游客空间保存和恢复进度，不会获得其他应用的文件或账户资料。结束游戏后需要重新授权。", PrimaryButtonText = "允许", CloseButtonText = "拒绝", DefaultButton = ContentDialogButton.Close };
            using var registration = token.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
            try { completion.TrySetResult(await dialog.ShowAsync() == ContentDialogResult.Primary && !token.IsCancellationRequested); }
            catch (Exception) { completion.TrySetResult(false); }
            finally { aboutOpen = false; }
        })) completion.TrySetResult(false);
        return completion.Task;
    }
}
