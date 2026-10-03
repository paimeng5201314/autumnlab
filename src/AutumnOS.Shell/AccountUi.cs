using AutumnOS.Contracts;
using AutumnOS.Identity;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private LogtoIdentityService? identityService;
    private bool identityInitialized, accountOperation, updatingRememberAccount;
    private readonly TextBlock accountState = new() { FontSize = 25, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock accountSummary = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 25 };
    private readonly TextBlock accountSessionIssue = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly TextBlock accountResult = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
    private readonly CheckBox rememberAccount = new() { Content = "保持登录（关闭后自动恢复）", IsChecked = false };
    private Button loginButton = null!, sessionLoginButton = null!, cancelLoginButton = null!, refreshAccountButton = null!, logoutButton = null!, browserLogoutButton = null!, switchAccountButton = null!;

    private static TextBlock Paragraph(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, LineHeight = 25 };
    private static Button ActionButton(string text, string id, RoutedEventHandler action)
    {
        var button = new Button { Content = text, CornerRadius = new CornerRadius(12), Padding = new Thickness(16, 10, 16, 10) };
        AutomationProperties.SetAutomationId(button, id);
        button.Click += action;
        return button;
    }

    private void InitializeAccountUi()
    {
        AutomationProperties.SetAutomationId(accountState, "AccountState");
        AutomationProperties.SetAutomationId(accountSummary, "AccountSummary");
        AutomationProperties.SetAutomationId(accountSessionIssue, "AccountSessionIssue");
        AutomationProperties.SetAutomationId(accountResult, "AccountResult");
        AutomationProperties.SetAutomationId(rememberAccount, "RememberAccount");
        rememberAccount.Checked += OnRememberAccountChanged;
        rememberAccount.Unchecked += OnRememberAccountChanged;
        AccountContent.Children.Add(accountState);
        AccountContent.Children.Add(accountSummary);
        AccountContent.Children.Add(accountSessionIssue);
        AccountContent.Children.Add(rememberAccount);
        AccountContent.Children.Add(Paragraph("选择“登录并保持登录”，关闭 AutumnOS 后再打开会恢复已保存的会话。关闭窗口不会主动退出账号；凭据过期或被服务端撤销时需要重新认证。"));
        var loginActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        loginButton = ActionButton("登录并保持登录", "AccountLogin", OnAccountLogin);
        sessionLoginButton = ActionButton("仅本次登录", "AccountSessionLogin", OnAccountSessionLogin);
        cancelLoginButton = ActionButton("取消登录", "AccountCancel", (_, _) => identityService?.CancelSignIn());
        loginActions.Children.Add(loginButton); loginActions.Children.Add(sessionLoginButton); loginActions.Children.Add(cancelLoginButton);
        AccountContent.Children.Add(loginActions);
        AccountContent.Children.Add(Paragraph("保持登录使用此 Windows 用户的加密存储，并按服务端支持请求离线访问。未获得刷新凭据时，只能在现有会话有效期内恢复；“仅本次登录”不会保存登录凭据。"));
        var sessionActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        refreshAccountButton = ActionButton("检查会话", "AccountRefresh", OnAccountRefresh);
        switchAccountButton = ActionButton("切换账号 / 重新认证", "AccountSwitch", OnAccountSwitch);
        sessionActions.Children.Add(refreshAccountButton); sessionActions.Children.Add(switchAccountButton);
        AccountContent.Children.Add(sessionActions);
        logoutButton = ActionButton("退出账号（本客户端）", "AccountLogout", OnAccountLogout);
        browserLogoutButton = ActionButton("退出客户端及 Logto 浏览器会话", "AccountBrowserLogout", OnBrowserLogout);
        AccountContent.Children.Add(logoutButton); AccountContent.Children.Add(browserLogoutButton);
        AccountContent.Children.Add(Paragraph("浏览器退出使用约定的退出回调；该回调仍待控制台登记确认。失败时会说明结果，不影响本客户端清理凭据。"));
        AccountContent.Children.Add(accountResult);
        AccountContent.Children.Add(Paragraph("游客也能继续使用桌面和元素配对。登录不等于授权游戏读取你的资料；游客和各账号的存档默认分开，退出不会删除存档。"));
        AccountContent.Children.Add(Paragraph("登录与切换账号前请保存并关闭当前游戏。取消关闭会中止本次账号操作，保留当前页面和进度。"));
        RefreshAccountUi();
    }

    private async Task InitializeIdentityAsync()
    {
        if (identityInitialized) return;
        identityInitialized = true;
        if (configuration?.Options is not { } options) { accountResult.Text = "AUTH_NOT_CONFIGURED · 公开配置不可用，仍可使用游客功能。"; RefreshAccountUi(); return; }
        try
        {
            identityService = new LogtoIdentityService(options, installationRoot.DataDirectory, enterCriticalOperation: reason => criticalOperations.EnterWrite(reason));
            identityService.Changed += OnIdentityChanged;
            await identityService.InitializeAsync(lifetime.Token);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            accountResult.Text = "AUTH_INITIALIZATION_FAILED · 凭据未能安全恢复，请重新认证；游戏和存档已保留。";
        }
        RefreshAccountUi();
    }

    private void OnIdentityChanged(object? sender, IdentitySnapshot state)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (lifetime.IsCancellationRequested) return;
            RefreshAccountUi();
            RefreshPermissionsUi();
            RefreshDataRecoveryUi();
        });
    }

    private void RefreshAccountUi()
    {
        var state = identityService?.Snapshot;
        accountState.Text = state?.State switch
        {
            IdentitySessionState.SigningIn => "等待浏览器登录",
            IdentitySessionState.SignedIn => "已登录",
            IdentitySessionState.SessionExpired => "会话已失效",
            IdentitySessionState.OfflineCached => "离线 · 缓存资料",
            _ => "未登录 · 游客"
        };
        string persistenceSummary = state?.ErrorCode == "AUTH_CREDENTIALS_WRITE_FAILED" ? "保持登录设置未能安全保存，请重试"
            : state?.RememberSignIn == true ? "已启用保持登录" : "仅本次登录，关闭后不保留会话";
        accountSummary.Text = state?.State is IdentitySessionState.SignedIn or IdentitySessionState.OfflineCached or IdentitySessionState.SessionExpired
            ? $"{state.DisplayName ?? "Logto 用户"}\n{persistenceSummary}\n{(state.CanRefresh ? "会话支持刷新" : "无刷新凭据，超过有效期需要重新认证")}" : "使用系统浏览器完成 Logto 登录，密码只在登录网站中输入。";
        accountSessionIssue.Text = state?.ErrorCode is { } code ? code + " · 请检查会话状态；已有存档保留。" : "";
        accountSessionIssue.Visibility = state?.ErrorCode is null ? Visibility.Collapsed : Visibility.Visible;
        bool busy = accountOperation || state?.State == IdentitySessionState.SigningIn;
        bool available = identityService is not null;
        loginButton.IsEnabled = sessionLoginButton.IsEnabled = available && !busy;
        updatingRememberAccount = true;
        try { rememberAccount.IsChecked = state?.RememberSignIn == true; }
        finally { updatingRememberAccount = false; }
        rememberAccount.Visibility = state?.State is IdentitySessionState.SignedIn or IdentitySessionState.OfflineCached ? Visibility.Visible : Visibility.Collapsed;
        rememberAccount.IsEnabled = available && !busy;
        cancelLoginButton.IsEnabled = state?.State == IdentitySessionState.SigningIn;
        refreshAccountButton.IsEnabled = available && !busy && state?.State is not IdentitySessionState.SignedOut;
        switchAccountButton.IsEnabled = available && !busy;
        logoutButton.IsEnabled = browserLogoutButton.IsEnabled = available && !busy && state?.State is not IdentitySessionState.SignedOut;
    }

    private void OnAccount(object sender, RoutedEventArgs e)
    {
        if (ConsumeHeldClick(sender)) return;
        SelectSettingsCategory("account");
        OnSettings(sender, e);
    }

    private async Task<bool> PrepareAccountChangeAsync(string? operationTitle = null, string? operationDescription = null)
    {
        if (launching || aboutOpen || isolatedStoreSource && (installingStoreTask || localInstallBusy)) return false;
        var running = appHost;
        var preview = developerPreviewHost;
        var managed = managedApplications.ToArray();
        // A revoked logical session can still own a WebView with unsaved input.
        // Explicit account changes require consent for that physical instance too.
        if (running is null && preview is null && managed.Length == 0) { StopStoreTestSource(); return true; }
        var captured = running?.Session.Instance.Id;
        var capturedPreview = preview?.Session.Instance.Id;
        aboutOpen = true;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = operationTitle ?? "保存并关闭游戏后更改账号", Content = operationDescription ?? "账号变化需要关闭当前游戏和它的浏览器数据空间。请先在游戏内保存。选择取消可返回原实例继续游戏；已提交的存档不会删除。", PrimaryButtonText = "我已保存，关闭游戏", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary || appHost != running || running?.Session.Instance.Id != captured ||
                developerPreviewHost != preview || preview?.Session.Instance.Id != capturedPreview ||
                managed.Length != managedApplications.Count || managed.Any(pair => !managedApplications.TryGetValue(pair.Key, out var current) || !ReferenceEquals(pair.Value, current))) return false;
            running?.Dispose(); appHost = null; CloseDeveloperPreview(); CloseManagedApplications(); UpdateRuntimeStatus("无游戏会话");
            StopStoreTestSource();
            return true;
        }
        finally { aboutOpen = false; }
    }

    private async Task ChangeAccountAsync(bool login, bool reauthenticate, bool browserLogout, bool? remember = null)
    {
        if (identityService is null || accountOperation) return;
        accountOperation = true; RefreshAccountUi();
        try
        {
            if (!await PrepareAccountChangeAsync()) { accountResult.Text = "USER_CANCELLED · 当前账号和游戏保持不变。"; return; }
            accountResult.Text = login ? "即将打开系统浏览器。完成后返回此窗口；也可在这里取消。" : "正在退出本客户端；已保存的游戏进度保留。";
            var result = login
                ? await identityService.SignInAsync(remember ?? rememberAccount.IsChecked == true, reauthenticate, lifetime.Token)
                : await identityService.SignOutAsync(browserLogout, lifetime.Token);
            accountResult.Text = result.ErrorCode is { } code ? code + " · 操作未完整完成，请查看账号状态。" : login ? "认证已结束，请以上方实际会话状态为准。" : browserLogout ? "退出操作已结束；不代表其他设备的会话已退出。" : "已退出本客户端。Logto 浏览器会话可能仍然有效。";
        }
        catch (OperationCanceledException) { accountResult.Text = "USER_CANCELLED · 操作已取消。"; }
        catch (Exception error) when (error is not OutOfMemoryException) { accountResult.Text = "AUTH_OPERATION_FAILED · 操作未完成，未删除游戏存档。"; }
        finally { accountOperation = false; RefreshAccountUi(); }
    }
    private async void OnAccountLogin(object sender, RoutedEventArgs e) => await ChangeAccountAsync(true, false, false, true);
    private async void OnAccountSessionLogin(object sender, RoutedEventArgs e) => await ChangeAccountAsync(true, false, false, false);
    private async void OnAccountSwitch(object sender, RoutedEventArgs e) => await ChangeAccountAsync(true, true, false);
    private async void OnAccountLogout(object sender, RoutedEventArgs e) => await ChangeAccountAsync(false, false, false);
    private async void OnBrowserLogout(object sender, RoutedEventArgs e) => await ChangeAccountAsync(false, false, true);
    private async void OnRememberAccountChanged(object sender, RoutedEventArgs e)
    {
        if (updatingRememberAccount || identityService is null || accountOperation) return;
        bool remember = rememberAccount.IsChecked == true;
        accountOperation = true;
        try
        {
            RefreshAccountUi();
            var result = await identityService.SetRememberSignInAsync(remember, lifetime.Token);
            bool saved = result.RememberSignIn == remember &&
                result.State is (IdentitySessionState.SignedIn or IdentitySessionState.OfflineCached) &&
                result.ErrorCode is (null or "OFFLINE" or "AUTH_TIMEOUT" or "AUTH_METADATA_UNAVAILABLE" or "AUTH_PROVIDER_UNAVAILABLE");
            accountResult.Text = !saved ? (result.ErrorCode ?? "AUTH_CREDENTIALS_WRITE_FAILED") + " · 保持登录设置未能完整保存，请重试。"
                : remember ? "已加密保存当前会话，关闭后可以恢复。未获得刷新凭据时，恢复受现有有效期限制。"
                : "已清除本机保存的登录凭据；当前会话可继续使用，关闭后不会自动登录。";
        }
        catch (OperationCanceledException) { accountResult.Text = "USER_CANCELLED · 设置未完成。"; }
        catch (Exception error) when (error is not OutOfMemoryException) { accountResult.Text = "AUTH_CREDENTIALS_WRITE_FAILED · 未能安全保存登录设置，请重试。"; }
        finally { accountOperation = false; RefreshAccountUi(); }
    }
    private async void OnAccountRefresh(object sender, RoutedEventArgs e)
    {
        if (identityService is null || accountOperation) return;
        accountOperation = true; RefreshAccountUi();
        try { await identityService.RefreshAsync(lifetime.Token); }
        catch (OperationCanceledException) { accountResult.Text = "USER_CANCELLED · 会话检查已取消。"; }
        catch (Exception error) when (error is not OutOfMemoryException) { accountResult.Text = "AUTH_OPERATION_FAILED · 会话检查未完成。"; }
        finally { accountOperation = false; RefreshAccountUi(); }
    }
}
