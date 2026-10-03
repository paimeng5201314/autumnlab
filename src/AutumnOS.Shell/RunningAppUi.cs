using AutumnOS.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private readonly Dictionary<Button, Border> runningBadges = [];
    private readonly List<MenuFlyout> appMenus = [];

    private bool HasLiveGame => appHost?.Session.Instance.BlocksMaintenance == true;
    private bool IsCurrentLiveHost(WebAppHost host) => ReferenceEquals(appHost, host) && host.Session.Instance.BlocksMaintenance;

    private void OnSampleIconClick(object sender, RoutedEventArgs e)
    {
        if (ConsumeHeldClick(sender)) return;
        OnSample(sender, e);
    }

    private void UpdateRunningIndicators()
    {
        bool live = HasLiveGame;
        foreach (var (button, badge) in runningBadges)
        {
            badge.Visibility = live ? Visibility.Visible : Visibility.Collapsed;
            AutomationProperties.SetHelpText(button, live ? "运行中；单击继续，双击桌面空白处查看后台，长按或右键管理。" : "单击打开应用，拖动调整桌面顺序");
            ToolTipService.SetToolTip(button, live ? "元素配对 · 运行中 · 单击继续" : "元素配对");
        }
        if (!live)
        {
            HideRunningOperations();
            foreach (var menu in appMenus) menu.Hide();
        }
        RefreshSettingsState();
    }

    private void HideRunningOperations() => HideRunningSwitcher();

    private void ContinueCapturedGame(WebAppHost target, AppInstanceId instanceId)
    {
        if (!IsCurrentLiveHost(target) || target.Session.Instance.Id != instanceId || launching) return;
        HideRunningOperations();
        BackgroundManagedApps();
        target.Foreground();
        ShowPage(AppPanel);
    }

}
