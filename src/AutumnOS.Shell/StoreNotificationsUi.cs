using AutumnOS.Packages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, string> observedStoreInstalls = [];
    private readonly List<(string Id, string Text)> storeNotices = [];
    private void ObserveStoreInstall(InstalledApplication app)
    {
        if (!app.IsInstalled) { observedStoreInstalls.Remove(app.AppId); return; }
        if (observedStoreInstalls.GetValueOrDefault(app.AppId) == app.Package.PackageSha256) return;
        observedStoreInstalls[app.AppId] = app.Package.PackageSha256;
        // Registering the existing bundled sample during startup is not a user Store installation.
        if (app.Source.Kind == "bundled") return;
        storeNotices.RemoveAll(item => item.Id == app.AppId);
        storeNotices.Insert(0, (app.AppId, app.Package.Manifest.Name + " · " + app.Package.Manifest.Version + "\n安装已完成，已添加到桌面。"));
        if (storeNotices.Count > 20) storeNotices.RemoveRange(20, storeNotices.Count - 20);
        RefreshDesktopExtensions();
    }
    private void AddStoreNotifications()
    {
        foreach (var notice in storeNotices)
        {
            var content = new StackPanel { Spacing = 10 };
            content.Children.Add(Paragraph("Autumn Store")); content.Children.Add(Paragraph(notice.Text));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            actions.Children.Add(ActionButton("查看我的应用", "StoreNoticeOpen-" + notice.Id, (_, _) => { ShowPage(StorePanel); ShowStoreSection("installed"); }));
            actions.Children.Add(ActionButton("清除", "StoreNoticeDismiss-" + notice.Id, (_, _) => { storeNotices.RemoveAll(item => item.Id == notice.Id); RefreshDesktopExtensions(); }));
            content.Children.Add(actions);
            NotificationContent.Children.Add(new Border { Padding = new Thickness(18), CornerRadius = new CornerRadius(16), Child = content, Background = (Brush)Root.Resources["GlassFill"] });
        }
    }
}
