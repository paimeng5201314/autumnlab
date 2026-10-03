using AutumnOS.Contracts;
using AutumnOS.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private readonly Dictionary<Button, Border> notificationBadges = [];

    private void OnNotifications(object sender, RoutedEventArgs e)
    { SelectSettingsCategory("notifications"); OnSettings(sender, e); RefreshDesktopExtensions(); }

    private void RefreshDesktopExtensions()
    {
        if (lifetime.IsCancellationRequested || NotificationContent is null) return;
        NotificationContent.Children.Clear(); DesktopWidgetContainer.Children.Clear();
        var notifications = desktopExtensions?.Notifications ?? [];
        int notificationCount = notifications.Count + storeNotices.Count;
        NotificationsButton.Content = notificationCount == 0 ? "通知" : $"通知 · {notificationCount}";
        NotificationContent.Children.Add(Paragraph(notificationCount == 0 ? "暂无通知" : $"{notificationCount} 条通知"));
        AddStoreNotifications();
        if (desktopExtensions is not null && installedSample is not null)
        {
            var account = AccountContext(lifetime.Token); var app = BindApplication(installedSample);
            var mute = new ToggleSwitch { Header = "静音元素配对的通知与角标", IsOn = desktopExtensions.IsMuted(account, app) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(mute, "MuteSampleNotifications");
            mute.Toggled += (_, _) =>
            {
                try { desktopExtensions.SetMuted(account, app, mute.IsOn); }
                catch (RuntimeCapabilityException error) { permissionResult.Text = error.Code; }
            };
            NotificationContent.Children.Add(mute);
        }
        foreach (var notification in notifications)
        {
            var row = new StackPanel { Spacing = 8 };
            row.Children.Add(Paragraph(notification.DisplayName + " · " + notification.Title));
            row.Children.Add(Paragraph(notification.Body));
            if (notification.Action is not null) row.Children.Add(ActionButton("打开通知操作", "NotificationOpen-" + notification.Id, (_, _) => desktopExtensions?.ActivateNotification(notification.InstanceId, notification.Id)));
            row.Children.Add(ActionButton("清除这条通知", "NotificationDismiss-" + notification.Id, (_, _) => desktopExtensions?.DismissNotification(notification.InstanceId, notification.Id)));
            NotificationContent.Children.Add(new Border { Child = row, Padding = new Thickness(18), CornerRadius = new CornerRadius(16), Background = (Microsoft.UI.Xaml.Media.Brush)Root.Resources["GlassFill"] });
        }
        NotificationContent.Children.Add(Paragraph("此处展示经授权的应用通知。静音与撤销立即生效；应用结束后清理通知和订阅。Windows 系统通知推送未接通。"));
        var apps = desktopExtensions?.Apps ?? [];
        var sample = apps.FirstOrDefault(a => a.InstanceId == appHost?.Session.Instance.Id.Value);
        foreach (var badge in notificationBadges.Values)
        { ((TextBlock)badge.Child).Text = sample?.Badge > 0 ? sample.Badge.ToString() : ""; badge.Visibility = sample?.Badge > 0 ? Visibility.Visible : Visibility.Collapsed; }
        foreach (var widget in apps.SelectMany(a => a.Widgets).Take(4))
        {
            var content = new StackPanel { Spacing = 4 };
            content.Children.Add(Paragraph(widget.Title));
            foreach (var line in widget.Lines) content.Children.Add(Paragraph(line));
            DesktopWidgetContainer.Children.Add(new Border { Child = content, Padding = new Thickness(12), CornerRadius = new CornerRadius(14), Background = (Microsoft.UI.Xaml.Media.Brush)Root.Resources["GlassFill"] });
        }
    }

    private void AddDeclaredShortcutMenus(MenuFlyout menu, Guid instance)
    {
        var state = desktopExtensions?.Apps.FirstOrDefault(app => app.InstanceId == instance);
        if (state is null) return;
        foreach (var shortcut in state.Shortcuts)
        {
            var item = new MenuFlyoutItem { Text = shortcut.Title };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(item, "AppShortcut-" + shortcut.Id);
            item.Click += (_, _) => desktopExtensions?.ActivateShortcut(instance, shortcut.Id);
            menu.Items.Add(item);
        }
    }
}
