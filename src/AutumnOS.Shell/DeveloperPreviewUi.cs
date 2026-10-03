using AutumnOS.Contracts;
using AutumnOS.Packages;
using AutumnOS.Runtime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private WebAppHost? developerPreviewHost;
    private string? developerPreviewSessionId;
    private string? developerPreviewPackagePath, developerPreviewSha256;
    private DeveloperPreviewSimulation? developerPreviewSimulation;
    private DeveloperPreviewPermissions? developerPreviewPermissions;
    private RuntimeSessionServices? developerPreviewServices;
    private readonly TextBlock developerSimulationStatus = Paragraph("开发预览模拟：未开启。仅作用于明确选择的独立预览。 ");
    private readonly DeveloperToolsService developerPreviewTrace = new();

    private async Task PreviewDeveloperPackageAsync(string packagePath, CancellationToken cancellationToken, string? expectedSha256 = null)
    {
        if (!DeveloperModeEnabled || accountOperation || permissionService is null || aboutOpen || CurrentIdentity.State == IdentitySessionState.SigningIn)
            throw new InvalidOperationException("DEVELOPER_MODE_REQUIRED");
        DeveloperBuild prepared;
        using (criticalOperations.EnterWrite("准备独立开发预览"))
            prepared = await Task.Run(() => developerTools!.PreparePreview(packagePath,
                Path.Combine(installationRoot.Directories["Packages"], "DeveloperPreviews"), expectedSha256, cancellationToken), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // The copy stays read-leased through confirmation, installation and resource loading. The executed hash is checked again.
        using FileStream payload = new(prepared.PackagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        PackageInspection inspection = developerTools!.InspectPackage(prepared.PackagePath, cancellationToken);
        if (inspection.Sha256 != prepared.Inspection.Sha256) throw new PackageException("DEVELOPER_PREVIEW_HASH_CHANGED");
        ContentDialog dialog = new() { XamlRoot = Root.XamlRoot, Title = "在内部运行此开发包？",
            Content = $"应用：{inspection.Manifest.Name}\nID：{inspection.Manifest.AppId}\n版本：{inspection.Manifest.Version}\n权限：{string.Join("、", inspection.Manifest.Permissions)}\nSHA-256：{inspection.Sha256}\n\n这是独立开发预览；仅此预览可被本机 CLI 调试。已有预览将结束，请先保存。",
            PrimaryButtonText = "确认此包并预览", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        aboutOpen = true;
        try { if (await dialog.ShowAsync().AsTask(cancellationToken) != ContentDialogResult.Primary) throw new PackageException("USER_CANCELLED"); }
        finally { if (cancellationToken.IsCancellationRequested) dialog.Hide(); aboutOpen = false; }
        cancellationToken.ThrowIfCancellationRequested();
        if (!DeveloperModeEnabled) throw new PackageException("DEVELOPER_MODE_DISABLED");
        CloseDeveloperPreview(); developerTools.ClearTrace(); developerPreviewTrace.SetEnabled(true);
        developerPreviewSimulation = new(); developerPreviewPermissions = new(permissionService);
        developerPreviewPackagePath = prepared.PackagePath; developerPreviewSha256 = inspection.Sha256;
        await StartDeveloperPreviewHostAsync(cancellationToken);
    }
    private RuntimeSessionServices CreateDeveloperPreviewServices(InstalledPackage installed, Func<RuntimeSession> session, CancellationToken token)
    {
        DeveloperPreviewSimulation simulation = developerPreviewSimulation ?? throw new PackageException("DEVELOPER_PREVIEW_SESSION_EXPIRED");
        bool testAccount = simulation.UsesTestAccount;
        developerPreviewServices = CreateRuntimeServicesCore(installed, session, token,
            testAccount ? simulation.BindAccount(token) : null, developerPreviewPermissions,
            testAccount ? simulation.GetProfileAsync : null, suppressIdentity: testAccount, identityOverride: testAccount ? simulation : null);
        return developerPreviewServices;
    }
    private async Task StartDeveloperPreviewHostAsync(CancellationToken cancellationToken)
    {
        var preview = new WebAppHost(installationRoot, DeveloperPreviewContent, RequestSavePermissionAsync,
            _ => { }, CreateDeveloperPreviewServices, CloseFileCapabilities, developerPreviewPackagePath, RecordDeveloperPreviewCall, maintenance: criticalOperations,
            retainForCleanup: RetainUnreleasedHost, expectedPreviewSha256: developerPreviewSha256);
        developerPreviewHost = preview;
        appHost?.Background(); ShowPage(DeveloperPanel);
        BackgroundManagedApps();
        try
        {
            using (criticalOperations.EnterWrite("安装独立开发预览")) await preview.StartAsync(cancellationToken);
            if (preview.Installed?.PackageSha256 != developerPreviewSha256) throw new PackageException("DEVELOPER_PREVIEW_HASH_CHANGED");
            using (CancellationTokenSource navigation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                navigation.CancelAfter(TimeSpan.FromSeconds(30));
                while (preview.Session.Instance.State == AppLifecycleState.Starting) await Task.Delay(50, navigation.Token);
            }
            if (preview.Session.Instance.State is AppLifecycleState.Closed or AppLifecycleState.Crashed)
                throw new PackageException("DEVELOPER_PREVIEW_START_FAILED");
            developerPreviewSessionId = Guid.NewGuid().ToString("N");
            RefreshDeveloperSimulationStatus();
            RefreshPermissionsUi();
        }
        catch { if (developerPreviewHost == preview) CloseDeveloperPreview(); else preview.Dispose(); throw; }
    }
    private void CloseDeveloperPreview()
    {
        developerPreviewTrace.SetEnabled(false); ReleaseDeveloperPreviewHost();
        developerPreviewSimulation?.Dispose(); developerPreviewSimulation = null;
        developerPreviewPermissions?.Dispose(); developerPreviewPermissions = null;
        developerPreviewPackagePath = developerPreviewSha256 = null;
        RefreshDeveloperSimulationStatus();
    }
    private void ReleaseDeveloperPreviewHost()
    { developerPreviewHost?.Dispose(); developerPreviewHost = null; developerPreviewSessionId = null; developerPreviewServices = null; }
    private void RecordDeveloperPreviewCall(string method, string code)
    { developerPreviewTrace.Record(method, code); RecordDeveloperSdkCall(method, code); }
    private void ResumeDeveloperPreview() => developerPreviewHost?.Foreground();
    private async Task<DeveloperPreviewResponse> ApplyDeveloperDebugAsync(string command, string? sessionId, CancellationToken cancellationToken)
    {
        if (!DeveloperModeEnabled) return new(false, "DEVELOPER_MODE_DISABLED");
        WebAppHost? preview = developerPreviewHost;
        if (preview is null || sessionId is null || sessionId != developerPreviewSessionId || !preview.IsDeveloperPreview)
            return new(false, "DEVELOPER_PREVIEW_SESSION_EXPIRED");
        string appId = preview.Session.Instance.Identity.AppId;
        switch (command)
        {
            case "status": break;
            case "trace": break;
            case "clear-trace": developerPreviewTrace.ClearTrace(); developerTools!.ClearTrace(); RefreshDeveloperTrace(); break;
            case "foreground": appHost?.Background(); BackgroundManagedApps(); ShowPage(DeveloperPanel); preview.Foreground(); break;
            case "background": preview.Background(); break;
            case "deny-permissions": case "restore-permissions":
                developerPreviewPermissions!.SetDenied(command == "deny-permissions", developerPreviewServices!.Account, developerPreviewServices.Application); break;
            case "offline": case "online": developerPreviewSimulation!.SetOffline(command == "offline"); break;
            case "account-a": case "account-b": case "account-guest": case "reset-simulation":
                await SwitchDeveloperSimulationAccountAsync(command, sessionId, cancellationToken);
                preview = developerPreviewHost ?? throw new PackageException("DEVELOPER_PREVIEW_SESSION_EXPIRED");
                break;
            case "close":
                CloseDeveloperPreview();
                return new(true, "OK", sessionId, appId, preview.ResourcesReleased ? "Closed" : "Closing");
            default: return new(false, "DEVELOPER_COMMAND_UNSUPPORTED");
        }
        RefreshDeveloperSimulationStatus();
        return new(true, "OK", developerPreviewSessionId, appId, preview.Session.Instance.State.ToString(),
            command == "trace" ? developerPreviewTrace.GetTrace().ToArray() : null, DeveloperSimulationStatus());
    }
    private DeveloperPreviewSimulationStatus DeveloperSimulationStatus()
    {
        DeveloperPreviewSimulation? simulation = developerPreviewSimulation;
        return new(simulation?.UsesTestAccount == true || simulation?.Offline == true || developerPreviewPermissions?.Denied == true,
            simulation?.Account ?? "live", developerPreviewPermissions?.Denied == true, simulation?.Offline == true);
    }
    private void RefreshDeveloperSimulationStatus()
    {
        DeveloperPreviewSimulationStatus state = DeveloperSimulationStatus();
        developerSimulationStatus.Text = developerPreviewHost is null ? "开发预览模拟：无运行预览。"
            : $"【独立开发预览／{(state.IsTestSimulation ? "测试模拟" : "真实身份")}】账号：{state.Account} · 权限：{(state.PermissionsDenied ? "强制拒绝" : "正常询问")} · 网络状态：{(state.Offline ? "模拟离线" : "未模拟离线")}\n模拟不改变 Logto、正式授权或其他应用数据；外部网络仍按原安全规则阻止。";
        DeveloperSimulationBanner.Text = developerSimulationStatus.Text;
    }
    private async Task SwitchDeveloperSimulationAccountAsync(string command, string sessionId, CancellationToken cancellationToken)
    {
        ContentDialog dialog = new() { XamlRoot = Root.XamlRoot, Title = "切换此开发预览的模拟账号？",
            Content = "仅此独立预览会结束并重新启动，未保存输入会丢弃，请先保存。原用户账号和正式存档不改变；测试账号 A/B 的存档分别隔离。重启后旧调试会话 ID 失效。",
            PrimaryButtonText = "已保存，确认切换", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        aboutOpen = true;
        try { if (await dialog.ShowAsync().AsTask(cancellationToken) != ContentDialogResult.Primary) throw new PackageException("USER_CANCELLED"); }
        finally { if (cancellationToken.IsCancellationRequested) dialog.Hide(); aboutOpen = false; }
        cancellationToken.ThrowIfCancellationRequested();
        if (!DeveloperModeEnabled || sessionId != developerPreviewSessionId) throw new PackageException("DEVELOPER_PREVIEW_SESSION_EXPIRED");
        WebAppHost previous = developerPreviewHost!;
        ReleaseDeveloperPreviewHost();
        try
        {
            using (CancellationTokenSource release = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                release.CancelAfter(TimeSpan.FromSeconds(10));
                while (!previous.ResourcesReleased) await Task.Delay(50, release.Token);
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (!DeveloperModeEnabled || developerPreviewSimulation is null) throw new PackageException("DEVELOPER_MODE_DISABLED");
            developerPreviewSimulation.SwitchAccount(command == "account-guest" ? "guest" : command == "reset-simulation" ? "live" : command);
            if (command == "reset-simulation")
            {
                developerPreviewSimulation.SetOffline(false);
                developerPreviewPermissions?.Dispose(); developerPreviewPermissions = new(permissionService!);
            }
            await StartDeveloperPreviewHostAsync(cancellationToken);
        }
        catch { CloseDeveloperPreview(); throw; }
    }
    private async void ShowDeveloperDebug(string command) => await DeveloperOperationAsync(async token =>
    {
        DeveloperPreviewResponse result = await ApplyDeveloperDebugAsync(command, developerPreviewSessionId, token);
        developerResult.Text = result.Ok ? $"独立预览：{result.AppId} · {result.State} · 会话 {result.SessionId}" : result.Code + " · 当前没有可调试的独立预览。";
    });
}
