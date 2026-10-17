using AutumnOS.Contracts;
using AutumnOS.Identity;
using AutumnOS.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private FirstRunAccountCoordinator? firstRunAccount;
    private readonly TextBlock firstRunAccountStatus = Paragraph("账号登录是可选步骤，游客也能检查和安装公开更新。");
    private Button firstRunLogin = null!, firstRunSkip = null!, firstRunCancel = null!;
    private bool firstRunLoginPending, firstRunSkipPending;

    private void InitializeFirstRunAccountUi()
    {
        var section = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        section.Children.Add(new TextBlock { Text = "你的账号，你的选择", FontSize = 19, Foreground = Brush("355748") });
        AutomationProperties.SetAutomationId(firstRunAccountStatus, "FirstRunAccountStatus");
        section.Children.Add(firstRunAccountStatus);
        section.Children.Add(Paragraph("在系统浏览器中安全登录。选择登录后，会话将加密保存在这台电脑上；也可先跳过，之后在设置中登录。"));
        foreach (var text in section.Children.OfType<TextBlock>())
            text.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 96, 117, 104));
        firstRunLogin = ActionButton("登录并保持登录  ↗", "FirstRunLogin", OnFirstRunLogin);
        firstRunLogin.Background = Brush("355748"); firstRunLogin.Foreground = Brush("FFFFFF");
        firstRunLogin.HorizontalAlignment = HorizontalAlignment.Stretch;
        firstRunLogin.HorizontalContentAlignment = HorizontalAlignment.Center;
        firstRunSkip = ActionButton("暂时跳过", "FirstRunSkip", OnFirstRunSkip);
        firstRunCancel = ActionButton("取消登录", "FirstRunCancel", (_, _) =>
        {
            firstRunAccount?.CancelSignIn();
            firstRunAccountStatus.Text = "登录已取消，可以重试或暂时跳过。";
        });
        section.Children.Add(firstRunLogin);
        firstRunSkip.HorizontalAlignment = HorizontalAlignment.Center;
        firstRunSkip.Background = Brush("FAF8F2"); firstRunSkip.Foreground = Brush("355748");
        section.Children.Add(firstRunSkip);
        section.Children.Add(firstRunCancel);
        WelcomeAccountContent.Children.Add(section);
    }

    private void RefreshFirstRunAccountUi()
    {
        if (firstRunLogin is null || lifetime.IsCancellationRequested) return;
        var progress = stateStore?.Load();
        var step = progress?.State?.LoginStep ?? FirstRunLoginStep.Pending;
        var session = identityService?.Snapshot;
        bool decided = step != FirstRunLoginStep.Pending;
        firstRunLogin.IsEnabled = !decided && !firstRunLoginPending && !firstRunSkipPending && identityService is not null && !accountOperation;
        firstRunSkip.IsEnabled = !decided && !firstRunSkipPending;
        firstRunCancel.Visibility = firstRunLoginPending ? Visibility.Visible : Visibility.Collapsed;
        firstRunCancel.IsEnabled = firstRunLoginPending && !firstRunSkipPending;
        PrepareDesktopButton.IsEnabled = progress?.Success == true && decided && !preparing && !firstRunSkipPending;
        PrepareDesktopButton.Visibility = decided || preparing ? Visibility.Visible : Visibility.Collapsed;
        firstRunSkip.Visibility = decided ? Visibility.Collapsed : Visibility.Visible;
        firstRunLogin.Visibility = decided ? Visibility.Collapsed : Visibility.Visible;
        WelcomeAccountBack.IsEnabled = !firstRunLoginPending && !firstRunSkipPending && !preparing;
        if (step == FirstRunLoginStep.Skipped) firstRunAccountStatus.Text = "已暂时跳过登录。完成桌面配置后即可使用，之后可到设置中登录。";
        else if (step == FirstRunLoginStep.Completed)
            firstRunAccountStatus.Text = session?.State == IdentitySessionState.SignedIn
                ? $"已登录：{session.DisplayName ?? "Logto 用户"}。请继续完成桌面配置。" +
                    (session.ErrorCode == "AUTH_CREDENTIALS_WRITE_FAILED" ? "本机未能保存会话，可进入桌面后在设置中重试保持登录。" : "")
                : "登录步骤已完成；当前会话状态以设置中的账号页面为准。请继续完成桌面配置。";
        else if (identityService is null) firstRunAccountStatus.Text = "账号服务暂不可用，可以暂时跳过；公开更新无需登录。";
        else if (firstRunLoginPending) firstRunAccountStatus.Text = "正在等待系统浏览器登录，可以取消或暂时跳过。";
    }

    private async void OnFirstRunLogin(object sender, RoutedEventArgs e)
    {
        if (firstRunAccount is null || firstRunLoginPending || firstRunSkipPending || accountOperation) return;
        firstRunLoginPending = true;
        RefreshFirstRunAccountUi();
        try
        {
            var result = await firstRunAccount.SignInAsync(lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            if (!result.Success) firstRunAccountStatus.Text = result.RecoveryMessage;
            else if (result.State?.LoginStep == FirstRunLoginStep.Pending)
                firstRunAccountStatus.Text = IdentityUserMessage.ForError(identityService?.Snapshot.ErrorCode);
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) firstRunAccountStatus.Text = "登录已取消，可以重试或暂时跳过。"; }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!lifetime.IsCancellationRequested) firstRunAccountStatus.Text = "登录未完成，可以重试或暂时跳过。"; }
        finally
        {
            firstRunLoginPending = false;
            if (!lifetime.IsCancellationRequested) { RefreshFirstRunAccountUi(); RefreshAccountUi(); }
        }
    }

    private async void OnFirstRunSkip(object sender, RoutedEventArgs e)
    {
        if (firstRunAccount is null || firstRunSkipPending) return;
        firstRunSkipPending = true;
        RefreshFirstRunAccountUi();
        try
        {
            var result = await firstRunAccount.SkipAsync(lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            if (!result.Success) WelcomeError.Text = result.RecoveryMessage;
            else WelcomeError.Text = "";
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!lifetime.IsCancellationRequested) WelcomeError.Text = "未能保存跳过选择，请重试。"; }
        finally
        {
            firstRunSkipPending = false;
            if (!lifetime.IsCancellationRequested) { RefreshFirstRunAccountUi(); RefreshAccountUi(); }
        }
    }
}
