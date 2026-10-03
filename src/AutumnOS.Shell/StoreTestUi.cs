using AutumnOS.Packages;
using AutumnOS.Store;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private StoreTestSource? storeTestSource;
    private bool CanRunControlledPackage(InstalledApplication app) => DeveloperModeEnabled && isolatedStoreSource &&
        CurrentIdentity.AccountNamespace == "guest" && app.Source.Kind == "test" && app.Source.RepositoryId == StoreTestSource.RepositoryId &&
        storeTestSource?.IsControlledPackage(app.AppId, app.Package.PackageSha256) == true;

    private async void OnDeveloperStoreTest(object sender, RoutedEventArgs e)
    {
        if (!DeveloperModeEnabled || aboutOpen || applicationInstaller is null) return;
        if (isolatedStoreSource) { ShowPage(StorePanel); ShowStoreSection("discover"); return; }
        if (CurrentIdentity.AccountNamespace != "guest") { developerResult.Text = "请先保存并退出账号；本地集成测试仅在游客环境使用。"; return; }
        aboutOpen = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "开启本地商店集成测试？",
                Content = "仅本次开发模式有效。展示随本包校验的独立样例，通过 127.0.0.1 测试服务器验证真实下载与安装。样例有单独的应用 ID 和存档，不会冒充 GitHub 商品。关闭开发模式会停止测试源。",
                PrimaryButtonText = "开启测试源", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            storeTestSource ??= new StoreTestSource(Path.Combine(AppContext.BaseDirectory, "Samples", "StoreFixture"));
            StartStoreInfrastructure(Path.Combine(installationRoot.Directories["Runtime"], "StoreIntegration"), storeTestSource, true);
            developerResult.Text = StoreTestSource.Label + "。测试源不会在重启后自动开启。";
            appHost?.Background(); developerPreviewHost?.Background(); BackgroundManagedApps(); ShowPage(StorePanel); ShowStoreSection("discover");
        }
        catch (Exception error) when (error is not OutOfMemoryException) { developerResult.Text = SafeStoreCode(error) + " · 测试源未开启。"; }
        finally { aboutOpen = false; }
    }
    private async void OnDeveloperStoreTestStop(object sender, RoutedEventArgs e)
    {
        if (!isolatedStoreSource) { developerResult.Text = "当前使用 GitHub 公开商店。"; return; }
        if (!await ConfirmStopStoreTestAsync()) return;
        try { StopStoreTestSource(); developerResult.Text = "测试源已关闭，已恢复公开 GitHub 商店。测试安装与存档保留。"; }
        catch (Exception error) when (error is not OutOfMemoryException) { developerResult.Text = SafeStoreCode(error) + " · 无法恢复公开商店，现有状态保留。"; }
    }
    private async Task<bool> ConfirmStopStoreTestAsync()
    {
        if (installingStoreTask || localInstallBusy) { developerResult.Text = "安装提交中，请稍后再关闭测试源。"; return false; }
        if (!managedApplications.Values.Any(app => app.Host.Session.Instance.BlocksMaintenance)) return true;
        if (aboutOpen) return false;
        aboutOpen = true;
        try
        {
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "关闭测试源与测试应用？",
                Content = "请先在测试应用内保存进度。关闭开发能力会结束测试实例，已保存数据保留。", PrimaryButtonText = "已保存，关闭", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { aboutOpen = false; }
    }
    private void StopStoreTestSource()
    {
        if (storeTestSource is null) return;
        if (!isolatedStoreSource) { storeTestSource.Dispose(); storeTestSource = null; return; }
        if (!lifetime.IsCancellationRequested && githubTransport is not null)
            StartStoreInfrastructure(installationRoot.DataDirectory, githubTransport, false);
        else { storeDownloads?.Dispose(); isolatedStoreSource = false; }
        CloseManagedApplications(); storeTestSource.Dispose(); storeTestSource = null;
    }
}
