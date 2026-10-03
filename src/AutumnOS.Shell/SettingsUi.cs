using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private string selectedSettingsCategory = "appearance";

    private void OnAppearance(object sender, RoutedEventArgs e)
    {
        SelectSettingsCategory("appearance");
        OnSettings(sender, e);
    }

    private void OnSettingsCategory(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string category }) SelectSettingsCategory(category);
    }

    private void SelectSettingsCategory(string category)
    {
        selectedSettingsCategory = category;
        var pages = new (string Key, string Title, FrameworkElement Page, ToggleButton Item)[]
        {
            ("appearance", "外观", AppearanceDetails, NavAppearance),
            ("desktop", "桌面与 Dock", DesktopDetails, NavDesktop),
            ("notifications", "通知与控制中心", NotificationsDetails, NavNotifications),
            ("account", "账号", AccountDetails, NavAccount),
            ("permissions", "应用权限与数据", PermissionsDetails, NavPermissions),
            ("github", "GitHub 加速", GitHubDetails, NavGitHub),
            ("updates", "系统更新", UpdateDetails, NavUpdates),
            ("diagnostics", "开发者诊断", DiagnosticsPanel, NavDiagnostics),
            ("about", "关于", AboutDetails, NavAbout)
        };
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        foreach (var (key, title, page, item) in pages)
        {
            bool selected = key == category;
            page.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            item.IsChecked = selected;
            item.Background = Brush(selected ? dark ? "34485F" : "DAE6F4" : "00FFFFFF");
            item.Foreground = Brush(dark ? "EFF0F5" : "243246");
            AutomationProperties.SetHelpText(item, selected ? "当前分类" : "切换右侧详情");
            if (selected) SettingsDetailTitle.Text = title;
        }
        RefreshSettingsState();
    }

    private void RefreshSettingsState()
    {
        DesktopApplicationsSummary.Text = installedSample is { } installed
            ? $"设置 · 系统应用\n{installed.Manifest.Name} · {installed.Manifest.Version} · 已安装\n游戏状态：{(HasLiveGame ? "运行中" : "未运行")}"
            : "设置 · 系统应用\n当前没有已验证的游戏入口。";
        DockApplicationsSummary.Text = installedSample is { } sample
            ? $"桌面  ·  {sample.Manifest.Name}  ·  商店  ·  设置"
            : "桌面  ·  商店  ·  设置";
    }
}
