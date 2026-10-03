using AutumnOS.Packages;
using AutumnOS.Store;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private async void OnDeveloperCreateProject(object sender, RoutedEventArgs e) => await DeveloperOperationAsync(async token =>
    {
        if (aboutOpen) return;
        ComboBox templates = new() { Header = "模板", ItemsSource = new[] { "hello-app", "identity-app", "save-game", "desktop-extension" }, SelectedIndex = 0 };
        TextBox appId = new() { Header = "应用 ID", Text = "dev.example.hello", MaxLength = 80 };
        TextBox name = new() { Header = "应用名称", Text = "我的第一个应用", MaxLength = 128 };
        AutomationProperties.SetAutomationId(templates, "DeveloperTemplate");
        AutomationProperties.SetAutomationId(appId, "DeveloperProjectAppId");
        AutomationProperties.SetAutomationId(name, "DeveloperProjectName");
        StackPanel fields = new() { Spacing = 12 }; fields.Children.Add(templates); fields.Children.Add(appId); fields.Children.Add(name);
        ContentDialog dialog = new() { XamlRoot = Root.XamlRoot, Title = "创建本地应用项目", Content = fields,
            PrimaryButtonText = "选择父文件夹", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        aboutOpen = true;
        try { if (await dialog.ShowAsync().AsTask(token) != ContentDialogResult.Primary) return; }
        finally { if (token.IsCancellationRequested) dialog.Hide(); aboutOpen = false; }
        var selected = await new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id).PickSingleFolderAsync().AsTask(token);
        token.ThrowIfCancellationRequested(); if (selected is null) return;
        string resources = Path.Combine(AppContext.BaseDirectory, "Developer");
        using IDisposable operation = criticalOperations.EnterWrite("创建开发者项目");
        string template = (string)templates.SelectedItem, id = appId.Text, title = name.Text;
        // Destination identity is validated by the shared engine before any project write.
        string destination = Path.Combine(selected.Path, id);
        DeveloperProject result = await Task.Run(() => developerTools!.CreateProject(Path.Combine(resources, "Templates"),
            Path.Combine(resources, "SDK", "autumn-sdk.js"), template, destination, id, title, token), token);
        token.ThrowIfCancellationRequested(); developerProject = result.ProjectDirectory; developerPackage = null;
        developerSelection.Text = "项目：" + result.ProjectDirectory;
        developerResult.Text = PackageSummary(result.Inspection) + "\n模板与 SDK 已复制，可以在此项目目录继续开发。";
    });
    private async void OnDeveloperValidateProject(object sender, RoutedEventArgs e) => await DeveloperOperationAsync(async token =>
    {
        if (developerProject is null) { developerResult.Text = "请先选择或创建项目。"; return; }
        PackageInspection result = await Task.Run(() => developerTools!.ValidateProject(developerProject, token), token);
        developerResult.Text = PackageSummary(result) + "\n只校验，没有提交交付包或运行项目。";
    });
    private async void OnDeveloperGeneratePublication(object sender, RoutedEventArgs e) => await DeveloperOperationAsync(async token =>
    {
        if (developerPackage is null) { developerResult.Text = "请先选择并检查 .autumn 包。"; return; }
        if (aboutOpen) return;
        TextBox author = new() { Header = "开发者名称", MaxLength = 128 };
        TextBox description = new() { Header = "应用简介", MaxLength = 4000, AcceptsReturn = true, Height = 100 };
        CheckBox offline = new() { Content = "应用真实支持离线使用" };
        StackPanel fields = new() { Spacing = 12 }; fields.Children.Add(author); fields.Children.Add(description); fields.Children.Add(offline);
        ContentDialog dialog = new() { XamlRoot = Root.XamlRoot, Title = "生成本地发布材料", Content = fields,
            PrimaryButtonText = "选择输出父文件夹", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        aboutOpen = true;
        try { if (await dialog.ShowAsync().AsTask(token) != ContentDialogResult.Primary) return; }
        finally { if (token.IsCancellationRequested) dialog.Hide(); aboutOpen = false; }
        var selected = await new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id).PickSingleFolderAsync().AsTask(token);
        token.ThrowIfCancellationRequested(); if (selected is null) return;
        var options = new DeveloperPublicationOptions(author.Text, description.Text, Offline: offline.IsChecked == true);
        string output = Path.Combine(selected.Path, "publication-" + Guid.NewGuid().ToString("N"));
        using IDisposable operation = criticalOperations.EnterWrite("生成开发者发布材料");
        DeveloperPublication result = await Task.Run(() => new DeveloperPublicationService(developerTools!).Generate(developerPackage, output, options, token), token);
        developerResult.Text = $"三层校验通过：{result.AppId} · {result.Version}\n{result.OutputDirectory}\n仅本地生成，没有上传或创建 Release。";
    });
    private async void OnDeveloperValidatePublication(object sender, RoutedEventArgs e) => await DeveloperOperationAsync(async token =>
    {
        if (developerPackage is null) { developerResult.Text = "请先选择并检查 .autumn 包。"; return; }
        developerResult.Text = "请选择 autumn.store.json。";
        string? store = await PickDeveloperFileAsync(".json", token); if (store is null) return;
        developerResult.Text = "请选择同版本的 autumn.release.json。";
        string? release = await PickDeveloperFileAsync(".json", token); if (release is null) return;
        DeveloperPublicationCheck result = await Task.Run(() => new DeveloperPublicationService(developerTools!).Validate(store, release, developerPackage, token), token);
        developerResult.Text = $"商店、发布清单与实际包一致：{result.AppId} · {result.Version}\nSHA-256：{result.Sha256}\n公开仓库及发布状态尚未由本地检查证明。";
    });
}
