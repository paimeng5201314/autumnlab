using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Packages;
using AutumnOS.Runtime;
using AutumnOS.Storage;
using AutumnOS.Store;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private DeveloperToolsService? developerTools;
    private VersionedConfigurationStore? developerConfiguration;
    private bool developerLoading, developerBusy;
    private string? developerProject, developerPackage;
    private readonly TextBlock developerSelection = Paragraph("选择含 manifest.json 的 Web/WASM 项目；只在本机校验与打包。" );
    private readonly TextBlock developerResult = Paragraph("");
    private readonly TextBox developerTrace = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 190 };
    private readonly List<Button> developerActions = [];
    private bool DeveloperModeEnabled => developerTools?.Enabled == true;

    private void InitializeDeveloperTools()
    {
        if (developerTools is not null) return;
        developerTools = new();
        developerConfiguration = new(installationRoot, "developer-mode", 1, criticalOperations);
        developerLoading = true;
        var loaded = developerConfiguration.Load();
        bool enabled = false;
        if (loaded.Success && loaded.Value is { } persisted && persisted.Values.ValueKind == JsonValueKind.Object &&
            persisted.Values.TryGetProperty("enabled", out JsonElement value)) enabled = value.ValueKind == JsonValueKind.True;
        developerTools.SetEnabled(enabled);
        DeveloperModeToggle.IsOn = enabled;
        DeveloperModeToggle.IsEnabled = loaded.Success;
        developerLoading = false;
        if (!loaded.Success) developerResult.Text = loaded.ErrorCode + " · 原配置保留；开发能力默认关闭。";
        DeveloperContent.Children.Add(Paragraph("开发者工具 · 本地预览"));
        DeveloperContent.Children.Add(Paragraph("项目内容按 .autumn 安全校验；不导入 EXE，不执行任意宿主命令。预览不是任意第三方应用完整隔离的验收。"));
        AutomationProperties.SetAutomationId(developerSelection, "DeveloperSelection");
        AutomationProperties.SetAutomationId(developerResult, "DeveloperResult");
        AutomationProperties.SetAutomationId(developerTrace, "DeveloperSdkTrace");
        AutomationProperties.SetAutomationId(developerSimulationStatus, "DeveloperSimulationStatus");
        DeveloperContent.Children.Add(developerSelection);
        void Add(string label, string id, RoutedEventHandler handler)
        {
            Button button = ActionButton(label, id, handler); developerActions.Add(button); DeveloperContent.Children.Add(button);
        }
        Add("选择项目文件夹", "DeveloperPickProject", OnDeveloperPickProject);
        Add("从交付模板创建项目", "DeveloperCreateProject", OnDeveloperCreateProject);
        Add("仅校验项目（不生成交付包）", "DeveloperValidateProject", OnDeveloperValidateProject);
        Add("校验清单、权限并打包 .autumn", "DeveloperBuildProject", OnDeveloperBuild);
        Add("选择并检查 .autumn", "DeveloperInspectPackage", OnDeveloperInspectPackage);
        Add("在内部预览已检查的包", "DeveloperPreview", OnDeveloperPreview);
        Add("校验 autumn.release.json", "DeveloperValidateRelease", OnDeveloperValidateRelease);
        Add("生成商店与发布材料", "DeveloperGeneratePublication", OnDeveloperGeneratePublication);
        Add("核对商店、发布清单和包", "DeveloperValidatePublication", OnDeveloperValidatePublication);
        Add("预览切到后台", "DeveloperPreviewBackground", (_, _) => ShowDeveloperDebug("background"));
        Add("预览回到前台", "DeveloperPreviewForeground", (_, _) => ShowDeveloperDebug("foreground"));
        Add("结束独立预览", "DeveloperPreviewClose", (_, _) => ShowDeveloperDebug("close"));
        DeveloperContent.Children.Add(developerSimulationStatus);
        Add("模拟拒绝此预览全部声明权限", "DeveloperSimulateDeny", (_, _) => ShowDeveloperDebug("deny-permissions"));
        Add("解除模拟权限拒绝", "DeveloperSimulateRestorePermissions", (_, _) => ShowDeveloperDebug("restore-permissions"));
        Add("切到开发测试账号 A", "DeveloperSimulateAccountA", (_, _) => ShowDeveloperDebug("account-a"));
        Add("切到开发测试账号 B", "DeveloperSimulateAccountB", (_, _) => ShowDeveloperDebug("account-b"));
        Add("切到开发测试游客", "DeveloperSimulateGuest", (_, _) => ShowDeveloperDebug("account-guest"));
        Add("模拟此预览离线", "DeveloperSimulateOffline", (_, _) => ShowDeveloperDebug("offline"));
        Add("解除模拟离线", "DeveloperSimulateOnline", (_, _) => ShowDeveloperDebug("online"));
        Add("清除模拟并重启此预览", "DeveloperResetSimulation", (_, _) => ShowDeveloperDebug("reset-simulation"));
        Add("运行隔离的生命周期与权限检查", "DeveloperRunChecks", OnDeveloperRunChecks);
        Add("本地商店集成测试", "DeveloperStoreTest", OnDeveloperStoreTest);
        Add("关闭测试源，返回公开商店", "DeveloperStoreTestStop", OnDeveloperStoreTestStop);
        Add("刷新脱敏 SDK 记录", "DeveloperRefreshTrace", (_, _) => RefreshDeveloperTrace());
        Add("清空本次调试记录", "DeveloperClearTrace", (_, _) => { developerTools?.ClearTrace(); RefreshDeveloperTrace(); });
        DeveloperContent.Children.Add(developerResult); DeveloperContent.Children.Add(developerTrace);
        RefreshDeveloperButtons();
        RefreshDeveloperPipe();
    }
    private async void OnDeveloperModeChanged(object sender, RoutedEventArgs e)
    {
        if (developerLoading || developerTools is null || developerConfiguration is null) return;
        bool enabled = DeveloperModeToggle.IsOn;
        if (!enabled && isolatedStoreSource && !await ConfirmStopStoreTestAsync())
        { developerLoading = true; DeveloperModeToggle.IsOn = true; developerLoading = false; return; }
        if (!enabled)
        {
            try { StopStoreTestSource(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            { developerResult.Text = SafeStoreCode(error) + " · 原配置保留，请检查商店数据。"; developerLoading = true; DeveloperModeToggle.IsOn = true; developerLoading = false; return; }
        }
        var saved = developerConfiguration.Save(JsonSerializer.SerializeToElement(new { enabled }), lifetime.Token);
        if (!saved.Success)
        {
            developerLoading = true; DeveloperModeToggle.IsOn = DeveloperModeEnabled; developerLoading = false;
            developerResult.Text = saved.ErrorCode + " · 未改变开发模式，原设置保留。"; return;
        }
        developerTools.SetEnabled(enabled);
        if (!enabled) CloseDeveloperCapabilities();
        RefreshDeveloperPipe();
        RefreshDeveloperButtons(); BuildAppEntries();
    }
    private void OnDeveloperOpen(object sender, RoutedEventArgs e)
    {
        if (ConsumeHeldClick(sender) || !DeveloperModeEnabled) return;
        appHost?.Background();
        BackgroundManagedApps();
        ResumeDeveloperPreview();
        ShowPage(DeveloperPanel);
        RefreshDeveloperTrace();
    }
    private void CloseDeveloperCapabilities()
    {
        StopDeveloperPipe();
        StopStoreTestSource();
        developerTools?.SetEnabled(false);
        CloseDeveloperPreview();
        developerProject = developerPackage = null;
        developerSelection.Text = "开发模式已关闭；预览、调试记录与未完成操作已撤销。普通游戏和存档保留。";
        developerTrace.Text = "";
        RefreshDeveloperButtons();
    }
    private void RecordDeveloperSdkCall(string method, string code)
    {
        developerTools?.Record(method, code);
        DispatcherQueue.TryEnqueue(() => { if (!lifetime.IsCancellationRequested && DeveloperPanel.Visibility == Visibility.Visible) RefreshDeveloperTrace(); });
    }
    private void RefreshDeveloperTrace() => developerTrace.Text = string.Join('\n', developerTools?.GetTrace().Select(call => $"{call.Timestamp:HH:mm:ss}  {call.Method}  {call.ResultCode}") ?? []);
    private void RefreshDeveloperButtons()
    { foreach (Button button in developerActions) button.IsEnabled = DeveloperModeEnabled && !developerBusy; }
    private async Task DeveloperOperationAsync(Func<CancellationToken, Task> action)
    {
        if (!DeveloperModeEnabled || developerBusy) return;
        developerBusy = true; RefreshDeveloperButtons();
        using CancellationTokenSource work = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, developerTools!.CapabilityToken);
        work.CancelAfter(TimeSpan.FromMinutes(5));
        try { await action(work.Token); }
        catch (OperationCanceledException) { developerResult.Text = "USER_CANCELLED · 操作已取消，已有包和数据保留。"; }
        catch (PackageException error) { developerResult.Text = error.Code + " · 校验未通过，没有执行项目代码。"; }
        catch (CatalogException error) { developerResult.Text = error.Code + " · 发布资料未通过，原文件保留。"; }
        catch (Exception error) when (error is not OutOfMemoryException) { developerResult.Text = "DEVELOPER_OPERATION_FAILED · 检查所选文件与访问权限；原文件保留。"; }
        finally { developerBusy = false; RefreshDeveloperButtons(); }
    }
    private async void OnDeveloperPickProject(object sender, RoutedEventArgs e) => await DeveloperOperationAsync(async token =>
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(AppWindow.Id);
        var result = await picker.PickSingleFolderAsync().AsTask(token);
        token.ThrowIfCancellationRequested();
        if (result is null) return;
        developerProject = result.Path; developerPackage = null;
        developerSelection.Text = "项目：" + result.Path;
        developerResult.Text = "尚未校验；点击打包会执行清单、权限、资源与包结构检查。";
    });
    private async void OnDeveloperBuild(object sender, RoutedEventArgs e) => await DeveloperOperationAsync(async token =>
    {
        if (developerProject is null) { developerResult.Text = "请先选择项目文件夹。"; return; }
        string output = Path.Combine(installationRoot.Directories["Packages"], "DeveloperOutputs");
        using IDisposable critical = criticalOperations.EnterWrite();
        DeveloperBuild result = await Task.Run(() => developerTools!.BuildProject(developerProject, output, token), token);
        token.ThrowIfCancellationRequested(); developerPackage = result.PackagePath;
        developerResult.Text = PackageSummary(result.Inspection) + "\n本地新包：" + result.PackagePath;
    });
    private async Task<string?> PickDeveloperFileAsync(string extension, CancellationToken token)
    {
        var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
        picker.FileTypeFilter.Add(extension);
        var result = await picker.PickSingleFileAsync().AsTask(token);
        token.ThrowIfCancellationRequested(); return result?.Path;
    }
    private async void OnDeveloperInspectPackage(object sender, RoutedEventArgs e) => await DeveloperOperationAsync(async token =>
    {
        string? selected = await PickDeveloperFileAsync(".autumn", token);
        if (selected is null) return;
        PackageInspection result = await Task.Run(() => developerTools!.InspectPackage(selected, token), token);
        token.ThrowIfCancellationRequested(); developerPackage = selected;
        developerSelection.Text = "已检查的本地包：" + selected; developerResult.Text = PackageSummary(result);
    });
    private async void OnDeveloperPreview(object sender, RoutedEventArgs e) => await DeveloperOperationAsync(async token =>
    {
        if (developerPackage is null) { developerResult.Text = "请先打包项目或选择并检查 .autumn。"; return; }
        token.ThrowIfCancellationRequested(); await PreviewDeveloperPackageAsync(developerPackage, token);
        developerResult.Text = "已在内部创建开发预览；授权与数据属于独立来源，关闭开发模式会结束此预览。";
    });
    private async void OnDeveloperValidateRelease(object sender, RoutedEventArgs e) => await DeveloperOperationAsync(async token =>
    {
        if (developerPackage is null) { developerResult.Text = "请先选择并检查要匹配的 .autumn 包。"; return; }
        string? path = await PickDeveloperFileAsync(".json", token); if (path is null) return;
        DeveloperReleaseCheck result = await Task.Run(() => developerTools!.ValidateRelease(path, developerPackage, token), token);
        developerResult.Text = $"发布资料与本地包一致：{result.AppId} · {result.Version} · {result.Channel}\nSHA-256：{result.Sha256}\n仅本地资料检查，没有上传或创建 Release。";
    });
    private async void OnDeveloperRunChecks(object sender, RoutedEventArgs e) => await DeveloperOperationAsync(async token =>
    {
        string testDirectory = Path.Combine(installationRoot.Directories["Temp"], "developer-check-" + Guid.NewGuid().ToString("N"));
        RuntimeSession session = new("cn.labchronicles.developerfixture", ["saves"], testDirectory);
        int checks = 0;
        try
        {
            void Require(bool condition) { if (!condition) throw new InvalidOperationException("DEVELOPER_CHECK_FAILED"); checks++; }
            session.Foreground(); Require(session.Instance.State == AppLifecycleState.Foreground);
            session.Background(); Require(session.Instance.State == AppLifecycleState.Background && session.Instance.BlocksMaintenance);
            session.Suspend(); Require(session.Instance.State == AppLifecycleState.Suspended);
            session.Resume(); Require(session.Instance.State == AppLifecycleState.Foreground);
            string Message(string method, object parameters) => JsonSerializer.Serialize(new { protocolVersion = 1, requestId = Guid.NewGuid().ToString("N"), method, @params = parameters });
            string denied = await session.HandleMessageAsync(session.PageUri, Message("permissions.request", new { name = "saves" }), (_, _) => Task.FromResult(false), token);
            using (JsonDocument denial = JsonDocument.Parse(denied)) Require(denial.RootElement.GetProperty("result").GetProperty("state").GetString() == "denied");
            string forged = await session.HandleMessageAsync("https://wrong.invalid/", Message("platform.getCapabilities", new { }), (_, _) => Task.FromResult(false), token);
            Require(forged.Contains("SOURCE_REJECTED", StringComparison.Ordinal));
            session.Close(); Require(session.Instance.State == AppLifecycleState.Closed && !session.Instance.BlocksMaintenance);
            developerResult.Text = $"隔离测试：{checks} 项通过。使用合成应用、游客测试命名空间和拒绝授权回调；未使用真实身份，没有给正常游戏授权。完整第三方安全隔离仍需阶段验收。";
        }
        finally { session.Close(); }
    });
    private static string PackageSummary(PackageInspection result) =>
        $"校验通过：{result.Manifest.Name} · {result.Manifest.Version}\n应用：{result.Manifest.AppId}\n声明权限：{string.Join("、", result.Manifest.Permissions)}\n资源：{result.FileCount} 个 / {result.ExpandedBytes} 字节\nSHA-256：{result.Sha256}";
}
