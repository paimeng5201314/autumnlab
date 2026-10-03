using AutumnOS.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private bool supportExportBusy;
    private async void OnExportSupportBundle(object sender, RoutedEventArgs e)
    {
        if (supportExportBusy || aboutOpen || accountOperation) return;
        supportExportBusy = true; SupportExportButton.IsEnabled = false;
        try
        {
            ContentDialog consent = new()
            {
                XamlRoot = Root.XamlRoot, Title = "导出本地脱敏诊断包",
                Content = "仅包含当前版本、构建、系统版本、更新阶段及有限的固定诊断事件。不会导出原始日志、账号、令牌、文件路径、存档、应用内容或截图。\n\n接下来选择保存文件夹；不会自动上传，也不会覆盖已有文件。",
                PrimaryButtonText = "选择保存位置", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close
            };
            aboutOpen = true;
            ContentDialogResult accepted;
            try { accepted = await consent.ShowAsync(); }
            finally { aboutOpen = false; }
            if (accepted != ContentDialogResult.Primary) { SupportExportStatus.Text = "已取消，没有生成诊断包。"; return; }
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id);
            var folder = await picker.PickSingleFolderAsync().AsTask(lifetime.Token);
            if (folder is null) { SupportExportStatus.Text = "已取消，没有生成诊断包。"; return; }
            string path = Path.Combine(folder.Path, $"AutumnOS-support-{DateTime.UtcNow:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.zip");
            SupportBundleResult result = await Task.Run(() => new SupportBundleService(installationRoot, criticalOperations).Export(path, lifetime.Token), lifetime.Token);
            SupportExportStatus.Text = $"已保存本地诊断包（{result.Events} 条脱敏事件）：\n{result.Path}\n请先自行检查，再决定是否分享。";
        }
        catch (OperationCanceledException) { SupportExportStatus.Text = "导出已取消，原文件保留。"; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { SupportExportStatus.Text = "SUPPORT_EXPORT_FAILED · 未完成导出。请检查目标目录访问权限或正在进行的维护；原日志与存档保留。"; }
        finally { supportExportBusy = false; SupportExportButton.IsEnabled = true; }
    }
}
