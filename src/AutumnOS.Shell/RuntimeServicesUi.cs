using System.Text;
using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Packages;
using AutumnOS.Runtime;
using AutumnOS.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private PermissionService? permissionService;
    private DesktopExtensionService? desktopExtensions;
    private readonly CriticalOperationCoordinator criticalOperations = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<(Guid Instance, bool Write), FileCapabilityBroker> fileBrokers = [];
    private readonly TextBlock permissionResult = Paragraph("");

    private IdentitySnapshot CurrentIdentity => identityService?.Snapshot ?? new(IdentitySessionState.SignedOut, "guest", 0, null, null, false, false, "AUTH_NOT_CONFIGURED");
    private RuntimeAccountContext AccountContext(CancellationToken token, Func<IDisposable>? lease = null)
    {
        var identity = CurrentIdentity;
        return new(identity.AccountNamespace, identity.SessionEpoch, identity.AccountNamespace == "guest",
            () => !token.IsCancellationRequested && CurrentIdentity.AccountNamespace == identity.AccountNamespace && CurrentIdentity.SessionEpoch == identity.SessionEpoch,
            token, lease ?? (() => identityService?.EnterSessionLease(identity.AccountNamespace, identity.SessionEpoch) ?? new EmptyLease()));
    }
    private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    private static RuntimeApplication BindApplication(InstalledPackage installed) => new(
        new(installed.Manifest.AppId, installed.HostSource?.StartsWith("github:", StringComparison.Ordinal) == true && long.TryParse(installed.HostSource[7..], out var repositoryId) ? repositoryId : null, null), installed.HostSource ?? (installed.Manifest.AppId == "cn.labchronicles.elementpairs"
            ? "bundled:cn.labchronicles.elementpairs" : "local-preview:" + installed.PackageSha256),
        installed.Manifest.Name, installed.Manifest.Permissions, installed.Manifest.PermissionPurposes);

    private void InitializeRuntimeServices()
    {
        if (permissionService is not null) return;
        try
        {
            permissionService = new PermissionService(installationRoot.Directories["Config"], () => criticalOperations.EnterWrite("应用权限写入"));
            desktopExtensions = new DesktopExtensionService(installationRoot.Directories["Config"], enterCritical: () => criticalOperations.EnterWrite("通知设置写入"));
            permissionService.Changed += _ => DispatcherQueue.TryEnqueue(RefreshPermissionsUi);
            desktopExtensions.Changed += () => DispatcherQueue.TryEnqueue(RefreshDesktopExtensions);
            desktopExtensions.ActionRequested += (id, _, _) => DispatcherQueue.TryEnqueue(() =>
            {
                if (appHost?.Session.Instance.Id.Value == id && HasLiveGame) { appHost.Foreground(); ShowPage(AppPanel); }
                else if (developerPreviewHost?.Session.Instance.Id.Value == id && DeveloperModeEnabled)
                { appHost?.Background(); developerPreviewHost.Foreground(); ShowPage(DeveloperPanel); }
            });
            SynchronizeAppearance();
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            permissionService = null; desktopExtensions = null;
            permissionResult.Text = "PERMISSION_STORE_UNAVAILABLE · 权限或通知配置无法安全读取，原文件保留；应用启动已停用。";
        }
        RefreshPermissionsUi();
    }

    private RuntimeSessionServices CreateRuntimeServices(InstalledPackage installed, Func<RuntimeSession> session, CancellationToken token)
        => CreateRuntimeServicesCore(installed, session, token);

    private RuntimeSessionServices CreateRuntimeServicesCore(InstalledPackage installed, Func<RuntimeSession> session, CancellationToken token,
        RuntimeAccountContext? accountOverride = null, IPermissionService? permissionsOverride = null,
        Func<RuntimeAccountContext, CancellationToken, Task<RuntimeProfile>>? profileOverride = null, bool suppressIdentity = false,
        IIdentityService? identityOverride = null)
    {
        if (permissionService is null) throw new RuntimeCapabilityException("PERMISSION_STORE_UNAVAILABLE");
        var app = BindApplication(installed);
        var account = accountOverride ?? AccountContext(token);
        IPermissionService permissions = permissionsOverride ?? permissionService;
        var declarations = installed.Manifest.Desktop;
        AccountDataStore? store = null;
        StorageScope Scope() => new(app.Identity, account.AccountKey, session().Instance.Id, new(account.Epoch), app.BindingKey);
        bool Current(StorageScope scope) => account.IsCurrent() && !token.IsCancellationRequested && session().Instance.Id == scope.InstanceId && session().Instance.BlocksMaintenance;
        AccountDataStore Data() => store ??= new(installationRoot, Scope(), Current,
            () => session().EnterCommitLease(), allowLegacyGuest: app.Source == "bundled:cn.labchronicles.elementpairs", coordinator: criticalOperations);
        FileCapabilityBroker Files(bool write = false)
        {
            var key = (session().Instance.Id.Value, write);
            if (fileBrokers.TryGetValue(key, out var existing)) return existing;
            var broker = new FileCapabilityBroker(scope => Current(scope) &&
                permissions.Query(account, app, write ? "files.save" : "files.open") == PermissionDecision.Granted,
                commitLease: _ => session().EnterCommitLease(), coordinator: criticalOperations);
            fileBrokers[key] = broker;
            return broker;
        }
        return new RuntimeSessionServices
        {
            Account = account, Application = app, Permissions = permissions, Identity = identityOverride ?? (suppressIdentity ? null : identityService),
            GetProfile = profileOverride,
            RequestPermission = RequestApplicationPermissionAsync, Desktop = desktopExtensions,
            Declarations = declarations is null ? new([], [], []) : new(
                declarations.Shortcuts.Select(s => new DesktopShortcut(s.Id, s.Title, s.Action)).ToArray(), declarations.Links,
                declarations.Widgets.Select(w => new DesktopWidget(w.Id, w.Title)).ToArray()),
            DataCapabilities = ["saves", "storage", "preferences", "files.open", "files.save"],
            DataRequest = async (method, p, ct) =>
            {
                if (method == "files.pickOpen")
                {
                    Exact(p);
                    string? selected = await PickOpenFileAsync(ct);
                    if (selected is null) throw new RuntimeCapabilityException("USER_CANCELLED");
                    return Require(Files().RegisterPickedFile(selected, Scope()))!;
                }
                if (method == "files.read")
                {
                    Exact(p, "handle"); var bytes = Require(Files().Read(Text(p, "handle"), Scope(), ct))!;
                    if (bytes.Length > 20 * 1024) throw new RuntimeCapabilityException("FILE_TOO_LARGE");
                    return new { data = Convert.ToBase64String(bytes), encoding = "base64" };
                }
                if (method == "files.pickSave")
                {
                    Exact(p); string? selected = await PickSaveFileAsync(ct);
                    if (selected is null) throw new RuntimeCapabilityException("USER_CANCELLED");
                    return Require(Files(true).RegisterPickedSaveFile(selected, Scope()))!;
                }
                if (method == "files.write")
                {
                    Exact(p, "handle", "data");
                    return Require(Files(true).Write(Text(p, "handle"), Scope(), Convert.FromBase64String(Text(p, "data")), ct))!;
                }
                if (method == "files.close")
                {
                    Exact(p, "handle");
                    foreach (bool write in new[] { false, true })
                        if (fileBrokers.TryGetValue((session().Instance.Id.Value, write), out var broker))
                        {
                            var result = broker.Close(Text(p, "handle"), Scope());
                            if (result.ErrorCode != "FILE_HANDLE_INVALID") return new { closed = Require(result) };
                        }
                    throw new RuntimeCapabilityException("FILE_HANDLE_INVALID");
                }
                return await Task.Run<object>(() =>
                {
                    switch (method)
                    {
                        case "saves.list": Exact(p); return new { slots = Require(Data().ListSlots(ct)) };
                        case "saves.read": Exact(p, "slot"); var value = Require(Data().ReadSave(Text(p, "slot"), ct)); return new { exists = value.HasValue, value };
                        case "saves.write":
                            if (p.TryGetProperty("formatVersion", out var version)) { Exact(p, "slot", "value", "formatVersion"); Require(Data().WriteSave(Text(p, "slot"), p.GetProperty("value"), version.GetInt32(), ct)); }
                            else { Exact(p, "slot", "value"); Require(Data().WriteSave(Text(p, "slot"), p.GetProperty("value"), 1, ct)); }
                            return new { saved = true };
                        case "saves.restore": Exact(p, "slot"); Require(Data().RestoreSave(Text(p, "slot"), ct)); return new { restored = true };
                        case "storage.read":
                            Exact(p, "key"); var raw = Require(Data().ReadPrivate(Text(p, "key"), ct));
                            if (raw?.Length > 20 * 1024) throw new RuntimeCapabilityException("FILE_TOO_LARGE");
                            return new { exists = raw is not null, data = raw is null ? null : Convert.ToBase64String(raw), encoding = "base64" };
                        case "storage.write": Exact(p, "key", "data"); Require(Data().WritePrivate(Text(p, "key"), Convert.FromBase64String(Text(p, "data")), ct)); return new { written = true };
                        case "storage.delete": Exact(p, "key"); return new { deleted = Require(Data().DeletePrivate(Text(p, "key"), ct)) };
                        case "preferences.get":
                            Exact(p, "key"); var saved = Require(Data().ReadPrivate("pref_" + Text(p, "key"), ct));
                            return new { exists = saved is not null, value = saved is null ? (JsonElement?)null : JsonSerializer.Deserialize<JsonElement>(saved) };
                        case "preferences.set": Exact(p, "key", "value"); Require(Data().WritePrivate("pref_" + Text(p, "key"), JsonSerializer.SerializeToUtf8Bytes(p.GetProperty("value")), ct)); return new { saved = true };
                        default: throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
                    }
                }, ct);
            }
        };
    }
    private void CloseFileCapabilities(Guid instance)
    {
        foreach (var key in fileBrokers.Keys.Where(key => key.Instance == instance).ToArray())
        { if (fileBrokers.TryRemove(key, out var broker)) broker.Dispose(); }
    }
    private static T? Require<T>(DataResult<T> result)
    { if (!result.Success) throw new RuntimeCapabilityException(result.ErrorCode == "SESSION_STALE" ? "SESSION_EXPIRED" : result.ErrorCode); return result.Value; }
    private static string Text(JsonElement p, string key)
    { if (p.GetProperty(key).ValueKind != JsonValueKind.String) throw new RuntimeCapabilityException("INVALID_REQUEST"); return p.GetProperty(key).GetString()!; }
    private static void Exact(JsonElement p, params string[] keys)
    { if (p.ValueKind != JsonValueKind.Object || p.EnumerateObject().Count() != keys.Length || keys.Any(k => !p.TryGetProperty(k, out _))) throw new RuntimeCapabilityException("INVALID_REQUEST"); }

    private Task<string?> PickOpenFileAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id);
                var file = await picker.PickSingleFileAsync().AsTask(token);
                token.ThrowIfCancellationRequested(); completion.TrySetResult(file?.Path);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception) { completion.TrySetException(new RuntimeCapabilityException("FILE_PICKER_FAILED")); }
        })) completion.TrySetException(new RuntimeCapabilityException("SESSION_EXPIRED"));
        return completion.Task;
    }

    private Task<string?> PickSaveFileAsync(CancellationToken token)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(AppWindow.Id) { SuggestedFileName = "AutumnOS-export" };
                picker.FileTypeChoices.Add("JSON 数据", new List<string> { ".json" });
                var file = await picker.PickSaveFileAsync().AsTask(token);
                token.ThrowIfCancellationRequested(); completion.TrySetResult(file?.Path);
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(token); }
            catch (Exception) { completion.TrySetException(new RuntimeCapabilityException("FILE_PICKER_FAILED")); }
        })) completion.TrySetException(new RuntimeCapabilityException("SESSION_EXPIRED"));
        return completion.Task;
    }

    private Task<bool> RequestApplicationPermissionAsync(RuntimePermissionPrompt prompt, CancellationToken token)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            bool foreground = appHost?.Session.Instance.State == AppLifecycleState.Foreground && appHost.Session.Instance.Identity.AppId == prompt.AppId ||
                developerPreviewHost?.Session.Instance.State == AppLifecycleState.Foreground && developerPreviewHost.Session.Instance.Identity.AppId == prompt.AppId ||
                managedApplications.TryGetValue(prompt.AppId, out var managed) && managed.Host.Session.Instance.State == AppLifecycleState.Foreground;
            if (token.IsCancellationRequested || aboutOpen || !foreground) { completion.TrySetResult(false); return; }
            aboutOpen = true;
            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = $"允许 {prompt.DisplayName} 使用{PermissionTitle(prompt.Permission)}？", Content = $"来源：{prompt.Source}\n用途：{prompt.Purpose}\n请求字段：{string.Join("、", prompt.Fields)}\n\n仅对当前账号与此应用来源授权。你可以在设置中撤销；已经交给应用的资料无法收回。", PrimaryButtonText = "允许", CloseButtonText = "拒绝", DefaultButton = ContentDialogButton.Close };
            using var cancellation = token.Register(() => DispatcherQueue.TryEnqueue(dialog.Hide));
            try { completion.TrySetResult(await dialog.ShowAsync() == ContentDialogResult.Primary && !token.IsCancellationRequested); }
            catch (Exception) { completion.TrySetResult(false); }
            finally { aboutOpen = false; }
        })) completion.TrySetResult(false);
        return completion.Task;
    }
    private static string PermissionTitle(string permission) => permission switch
    { "identity.profile" => "基本资料", "saves" => "私有存档", "storage" => "私有文件", "files.open" => "选择文件", "files.save" => "导出文件", "notifications" => "通知和角标", "shortcuts" => "长按快捷操作", "widgets" => "小组件", "links" => "内部链接", _ => permission };

    private void RefreshPermissionsUi()
    {
        PermissionsContent.Children.Clear();
        PermissionsContent.Children.Add(Paragraph("授权绑定当前账号、应用来源和具体权限。登录账号不会自动授权应用；新增权限需要单独允许。"));
        PermissionsContent.Children.Add(permissionResult);
        if (permissionService is null) return;
        var account = AccountContext(lifetime.Token);
        var applications = new Dictionary<string, (RuntimeApplication App, bool Current)>();
        foreach (var installed in new[] { installedSample, developerPreviewHost?.Installed }.Concat(applicationInstaller?.GetInstalled().Select(a => a.Package) ?? []).Where(item => item is not null))
        { var app = BindApplication(installed!); applications[app.BindingKey] = (app, true); }
        foreach (var group in permissionService.List(account).GroupBy(record => record.BindingKey))
        {
            if (applications.ContainsKey(group.Key)) continue;
            var record = group.First();
            var app = new RuntimeApplication(new(record.AppId, null, null), record.Source, record.DisplayName, group.Select(item => item.Permission).Distinct().ToArray());
            if (app.BindingKey == group.Key) applications.Add(group.Key, (app, false));
        }
        foreach (var (app, current) in applications.Values)
        {
          PermissionsContent.Children.Add(Paragraph(app.DisplayName + " · " + app.Source + (current ? "" : " · 历史授权（允许需重新打开已验证应用）")));
          foreach (string permission in app.DeclaredPermissions)
          {
            var row = new StackPanel { Spacing = 8 };
            PermissionDecision decision;
            try { decision = permissionService.Query(account, app, permission); } catch (RuntimeCapabilityException) { continue; }
            row.Children.Add(Paragraph(PermissionTitle(permission) + " · " + decision));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (var action in new[] { ("允许", PermissionDecision.Granted), ("拒绝", PermissionDecision.Denied), ("撤销", PermissionDecision.Revoked) })
            {
                if (!current && action.Item2 == PermissionDecision.Granted) continue;
                actions.Children.Add(ActionButton(action.Item1, "Permission-" + permission + "-" + action.Item2, (_, _) =>
                {
                    try { permissionService.SetDecision(account, app, permission, action.Item2); permissionResult.Text = "已保存权限决定。"; }
                    catch (RuntimeCapabilityException error) { permissionResult.Text = error.Code + " · 权限未改变。"; }
                    RefreshPermissionsUi();
                }));
            }
            row.Children.Add(actions); PermissionsContent.Children.Add(row);
          }
        }
        if (installedSample is null) return;
        PermissionsContent.Children.Add(Paragraph("存档默认按游客和账号分开，退出不会删除。将游客存档复制到当前账号需明确确认，目标已有存档时不会覆盖。"));
        var import = ActionButton("复制游客存档到当前账号", "ImportGuestSave", OnImportGuestSave);
        import.IsEnabled = !account.IsGuest && !accountOperation;
        PermissionsContent.Children.Add(import);
    }

    private async void OnImportGuestSave(object sender, RoutedEventArgs e)
    {
        if (installedSample is null || CurrentIdentity.AccountNamespace == "guest" || aboutOpen || accountOperation) return;
        if (!await PrepareAccountChangeAsync()) return;
        aboutOpen = true;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "复制游客存档？", Content = "保留游客原档和备份，仅复制到当前账号。目标存在存档时将拒绝，不会覆盖。", PrimaryButtonText = "确认复制", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var account = AccountContext(lifetime.Token); var app = BindApplication(installedSample);
            var scope = new StorageScope(app.Identity, account.AccountKey, new(Guid.NewGuid()), new(account.Epoch), app.BindingKey);
            var store = new AccountDataStore(installationRoot, scope, _ => account.IsCurrent(), account.EnterCommitLease, allowLegacyGuest: true, coordinator: criticalOperations);
            var result = store.MigrateGuestSave("game", true, lifetime.Token);
            permissionResult.Text = result.Success ? "已复制并保留备份，游客原档未改变。" : result.ErrorCode + " · 原存档保留。";
        }
        finally { aboutOpen = false; }
    }

    private void OnSystemAnimationsChanged(Windows.UI.ViewManagement.UISettings sender, Windows.UI.ViewManagement.UISettingsAnimationsEnabledChangedEventArgs args)
        => DispatcherQueue.TryEnqueue(() => { if (!lifetime.IsCancellationRequested) SynchronizeAppearance(); });
    private void SynchronizeAppearance() => desktopExtensions?.SetAppearance(new(Root.ActualTheme == ElementTheme.Dark ? "dark" : "light", "zh-CN", Root.XamlRoot?.RasterizationScale ?? 1, !systemUiSettings.AnimationsEnabled));
}
