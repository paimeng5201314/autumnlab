using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Packages;
using AutumnOS.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private bool recoveryInitialized, recoveryBusy;
    private readonly TextBlock recoveryConfigurationStatus = Paragraph("尚未读取开发者配置备份。");
    private readonly TextBlock recoveryAccountStatus = Paragraph("尚未读取当前账号的元素配对存档。");
    private readonly TextBlock recoverySaveStatus = Paragraph("");
    private readonly TextBlock recoveryResult = Paragraph("");
    private readonly ComboBox recoverySlots = new() { Header = "当前账号的存档槽位", HorizontalAlignment = HorizontalAlignment.Stretch };
    private Button recoveryRefresh = null!, recoveryConfigurationRestore = null!, recoverySaveRestore = null!;
    private ConfigurationBackupInfo? recoveryConfigurationBackup;
    private readonly List<RecoverySaveChoice> recoveryChoices = [];
    private RecoveryBinding? recoveryBinding;

    private void InitializeDataRecoveryUi()
    {
        if (recoveryInitialized) return;
        recoveryInitialized = true;
        RecoveryContent.Children.Add(new TextBlock { Text = "本地数据恢复", FontSize = 21, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        RecoveryContent.Children.Add(Paragraph("检查真实备份后再恢复。恢复前的文件会保留为 before-restore；已有恢复记录发生冲突时会停止，不删除旧档。此入口不为应用授予 SDK 权限。"));
        recoveryRefresh = ActionButton("检查配置与当前账号存档", "DataRecoveryRefresh", (_, _) => RefreshDataRecoveryUi());
        RecoveryContent.Children.Add(recoveryRefresh);
        RecoveryContent.Children.Add(new TextBlock { Text = "开发者模式配置", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        AutomationProperties.SetAutomationId(recoveryConfigurationStatus, "DeveloperConfigBackupStatus");
        RecoveryContent.Children.Add(recoveryConfigurationStatus);
        recoveryConfigurationRestore = ActionButton("恢复开发者配置备份", "RestoreDeveloperConfig", OnRestoreDeveloperConfiguration);
        recoveryConfigurationRestore.Visibility = Visibility.Collapsed;
        RecoveryContent.Children.Add(recoveryConfigurationRestore);
        RecoveryContent.Children.Add(new TextBlock { Text = "元素配对存档", FontSize = 17, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        AutomationProperties.SetAutomationId(recoveryAccountStatus, "SaveRecoveryAccount");
        AutomationProperties.SetAutomationId(recoverySlots, "SaveRecoverySlot");
        AutomationProperties.SetAutomationId(recoverySaveStatus, "SaveRecoveryStatus");
        AutomationProperties.SetAutomationId(recoveryResult, "DataRecoveryResult");
        recoverySlots.SelectionChanged += (_, _) => UpdateRecoverySelection();
        RecoveryContent.Children.Add(recoveryAccountStatus);
        RecoveryContent.Children.Add(recoverySlots);
        RecoveryContent.Children.Add(recoverySaveStatus);
        recoverySaveRestore = ActionButton("恢复所选存档备份", "RestoreSaveBackup", OnRestoreSaveBackup);
        recoverySaveRestore.Visibility = Visibility.Collapsed;
        RecoveryContent.Children.Add(recoverySaveRestore);
        RecoveryContent.Children.Add(recoveryResult);
        RefreshDataRecoveryUi();
    }

    private void RefreshDataRecoveryUi()
    {
        // Identity initialization can publish before the diagnostics controls are composed.
        if (!recoveryInitialized || recoveryBusy || lifetime.IsCancellationRequested) return;
        _ = ScanRecoveryAsync();
    }

    private async Task ScanRecoveryAsync()
    {
        if (!recoveryInitialized || recoveryBusy || lifetime.IsCancellationRequested) return;
        recoveryBusy = true; RefreshRecoveryButtons();
        try
        {
            var configurationStore = developerConfiguration ?? new VersionedConfigurationStore(installationRoot, "developer-mode", 1, criticalOperations);
            var installed = installedSample;
            var account = AccountContext(lifetime.Token);
            string accountLabel = account.IsGuest ? "游客存档" : $"当前账号：{CurrentIdentity.DisplayName ?? "已验证账号"}";
            RecoveryBinding? binding = installed is null ? null : CreateRecoveryBinding(installed, account);
            var scan = await Task.Run(() =>
            {
                var configuration = configurationStore.Load();
                var backup = configurationStore.InspectBackup();
                var choices = new List<RecoverySaveChoice>();
                string? listError = null;
                if (binding is not null)
                {
                    var slots = binding.Store.ListSlots(lifetime.Token);
                    if (!slots.Success) listError = slots.ErrorCode;
                    var names = new HashSet<string>(slots.Value?.Select(slot => slot.Slot) ?? [], StringComparer.Ordinal);
                    // A damaged primary must not prevent recovery of the sample's known game slot.
                    names.Add("game");
                    foreach (string name in names.Order(StringComparer.Ordinal))
                    {
                        var inspected = binding.Store.InspectSaveRecovery(name, lifetime.Token);
                        if (!inspected.Success) { if (name == "game" && listError is null) listError = inspected.ErrorCode; continue; }
                        var info = inspected.Value!;
                        if (!info.MainExists && !info.BackupExists) continue;
                        var slot = slots.Value?.SingleOrDefault(item => item.Slot == name);
                        choices.Add(new(name, info, slot));
                    }
                }
                return (Configuration: configuration, Backup: backup, Choices: choices, ListError: listError);
            }, lifetime.Token);
            if (lifetime.IsCancellationRequested) return;
            recoveryConfigurationBackup = scan.Backup.Success ? scan.Backup.Value : null;
            recoveryConfigurationStatus.Text = DescribeConfigurationRecovery(scan.Configuration, scan.Backup);
            if (!account.IsCurrent())
            {
                recoveryBinding = null; recoveryChoices.Clear(); recoverySlots.Items.Clear();
                recoveryAccountStatus.Text = "账号已变化，先前扫描结果未用于新账号。请重新检查。";
                recoverySaveStatus.Text = "SESSION_EXPIRED · 没有修改任何存档。";
                return;
            }
            recoveryBinding = binding;
            recoveryAccountStatus.Text = binding is null ? "元素配对尚未完成安装校验，暂不读取存档。" : accountLabel + " · 仅本目录、此应用来源的数据";
            recoveryChoices.Clear(); recoveryChoices.AddRange(scan.Choices);
            recoverySlots.Items.Clear();
            foreach (var choice in recoveryChoices)
            {
                string description = choice.Slot is { } metadata ? $"{choice.Name} · 格式 {metadata.FormatVersion} · 修订 {metadata.Revision}"
                    : choice.Info.MainExists ? choice.Name + " · 主记录需要检查" : choice.Name + " · 主记录缺失";
                recoverySlots.Items.Add(new ComboBoxItem { Content = description, Tag = choice.Name });
            }
            if (recoverySlots.Items.Count > 0) recoverySlots.SelectedIndex = 0;
            else recoverySaveStatus.Text = scan.ListError is { } error ? error + " · 列表无法安全读取，未找到可恢复的 game 备份；原数据保留。"
                : binding is null ? "等待真实安装记录。" : "此账号尚无存档或备份。游客和各登录账号的进度保持分离。";
            if (scan.ListError is { } failure && recoveryChoices.Count > 0)
                recoveryAccountStatus.Text += "\n" + failure + " · 主记录列表未通过检查；仅展示已单独核验的槽位恢复信息。";
        }
        catch (OperationCanceledException) { if (!lifetime.IsCancellationRequested) recoveryResult.Text = "USER_CANCELLED · 检查已取消，文件未修改。"; }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            recoveryConfigurationBackup = null; recoveryBinding = null; recoveryChoices.Clear(); recoverySlots.Items.Clear();
            recoveryResult.Text = SafeRecoveryCode(error) + " · 无法安全检查，原文件保留。";
        }
        finally { recoveryBusy = false; RefreshRecoveryButtons(); }
    }

    private RecoveryBinding CreateRecoveryBinding(InstalledPackage installed, RuntimeAccountContext account)
    {
        var application = BindApplication(installed);
        if (application.Identity.AppId != "cn.labchronicles.elementpairs" || application.Source != "bundled:cn.labchronicles.elementpairs")
            throw new RuntimeCapabilityException("SOURCE_REJECTED");
        var scope = new StorageScope(application.Identity, account.AccountKey, new(Guid.NewGuid()), new(account.Epoch), application.BindingKey);
        bool Current(StorageScope candidate) => candidate == scope && account.IsCurrent() && !account.Invalidated.IsCancellationRequested;
        var store = new AccountDataStore(installationRoot, scope, Current, () =>
        {
            IDisposable lease = account.EnterCommitLease?.Invoke() ?? new RecoveryEmptyLease();
            try { account.EnsureCurrent(); return lease; } catch { lease.Dispose(); throw; }
        }, allowLegacyGuest: true, coordinator: criticalOperations);
        return new(account, application, store);
    }

    private static string DescribeConfigurationRecovery(DataResult<VersionedConfiguration?> current, DataResult<ConfigurationBackupInfo> inspected)
    {
        string main = current.Success ? current.Value is null ? "当前配置尚未创建。" : $"当前配置修订 {current.Value.Revision}。"
            : current.ErrorCode + " · 当前配置未通过读取，文件保留。";
        if (!inspected.Success) return main + "\n" + inspected.ErrorCode + " · 无法检查备份。";
        var info = inspected.Value!;
        if (!info.BackupExists) return main + "\n暂无配置备份。";
        if (!info.BackupValid) return main + "\n" + info.BackupError + " · 备份不可安全恢复。";
        if (!ValidDeveloperConfiguration(info.Backup, out bool enabled)) return main + "\nCONFIG_INVALID_VALUE · 备份不是受支持的开发者模式配置。";
        return main + $"\n已验证备份：开发者模式{(enabled ? "开启" : "关闭")}。"
            + (info.MainExists && info.BeforeRestoreExists ? "已有 before-restore 文件，需先在本地保管好这份恢复记录，当前不会覆盖它。" : "恢复前会保留当前文件，并关闭开发预览及旧调试能力。普通游戏保持运行。");
    }

    private void UpdateRecoverySelection()
    {
        if (recoverySlots.SelectedItem is ComboBoxItem { Tag: string slot } && recoveryChoices.SingleOrDefault(item => item.Name == slot) is { } selected)
        {
            recoverySaveStatus.Text = selected.Info.BackupExists
                ? selected.Info.BackupValid
                    ? "备份已通过应用来源、账号和内容校验。恢复前请保存并关闭当前游戏；原主记录和有效备份都会保留。"
                        + (selected.Info.MainExists && selected.Info.BeforeRestoreExists ? "\nSAVE_RECOVERY_CONFLICT · 已存在 before-restore，当前不会覆盖。" : "")
                    : selected.Info.BackupError + " · 备份存在，但未通过校验；没有恢复操作。"
                : "此槽位暂无备份。";
        }
        RefreshRecoveryButtons();
    }

    private void RefreshRecoveryButtons()
    {
        if (!recoveryInitialized || recoveryRefresh is null || recoveryConfigurationRestore is null || recoverySaveRestore is null) return;
        recoveryRefresh.IsEnabled = !recoveryBusy;
        bool validConfiguration = recoveryConfigurationBackup is { BackupExists: true, BackupValid: true }
            && ValidDeveloperConfiguration(recoveryConfigurationBackup.Backup, out _);
        recoveryConfigurationRestore.Visibility = validConfiguration ? Visibility.Visible : Visibility.Collapsed;
        recoveryConfigurationRestore.IsEnabled = !recoveryBusy && validConfiguration
            && !(recoveryConfigurationBackup!.MainExists && recoveryConfigurationBackup.BeforeRestoreExists);
        recoverySlots.IsEnabled = !recoveryBusy && recoverySlots.Items.Count > 0;
        string? slot = (recoverySlots.SelectedItem as ComboBoxItem)?.Tag as string;
        var choice = recoveryChoices.SingleOrDefault(item => item.Name == slot);
        bool validSave = recoveryBinding is not null && choice?.Info is { BackupExists: true, BackupValid: true };
        recoverySaveRestore.Visibility = validSave ? Visibility.Visible : Visibility.Collapsed;
        recoverySaveRestore.IsEnabled = !recoveryBusy && validSave && !(choice!.Info.MainExists && choice.Info.BeforeRestoreExists);
    }

    private async void OnRestoreDeveloperConfiguration(object sender, RoutedEventArgs e)
    {
        if (recoveryBusy || aboutOpen || developerBusy || developerConfiguration is null || developerTools is null
            || recoveryConfigurationBackup is not { BackupExists: true, BackupValid: true }
            || !ValidDeveloperConfiguration(recoveryConfigurationBackup.Backup, out _)) return;
        recoveryBusy = true; RefreshRecoveryButtons();
        bool capabilitiesClosed = false;
        try
        {
            if (!await ConfirmRecoveryAsync("恢复开发者模式配置？", "请先保存开发预览中的进度。恢复会结束开发预览并撤销旧调试能力，然后读取备份中的真实开关值。普通游戏保持运行；当前配置保留为 before-restore，不删除任何游戏或存档。", "已保存预览，恢复配置"))
            { recoveryResult.Text = "USER_CANCELLED · 未恢复配置，开发预览和普通游戏保持不变。"; return; }
            var rechecked = developerConfiguration.InspectBackup();
            if (!rechecked.Success || rechecked.Value is not { BackupExists: true, BackupValid: true } backup
                || !ValidDeveloperConfiguration(backup.Backup, out _))
            { recoveryResult.Text = (rechecked.Success ? "CONFIG_BACKUP_INVALID" : rechecked.ErrorCode) + " · 备份检查未通过，没有修改配置或关闭预览。"; return; }
            if (backup.MainExists && backup.BeforeRestoreExists)
            { recoveryResult.Text = "CONFIG_RECOVERY_CONFLICT · 已有 before-restore，未覆盖任何记录，开发预览保持不变。"; return; }
            developerLoading = true; developerBusy = true; DeveloperModeToggle.IsEnabled = false;
            CloseDeveloperCapabilities();
            capabilitiesClosed = true;
            var restored = await Task.Run(() => developerConfiguration.RestoreBackup(true, lifetime.Token), lifetime.Token);
            bool valid = ReloadDeveloperConfigurationAfterRecovery();
            recoveryResult.Text = restored.Success && valid
                ? "已恢复开发者模式配置并重新读取。旧开发预览及其调试能力已关闭；普通游戏和存档保留。" + (backup.MainExists ? "恢复前配置已留为 before-restore。" : "恢复前没有主配置，备份仍保留。")
                : (restored.Success ? "CONFIG_INVALID_VALUE" : restored.ErrorCode) + " · 恢复未完整完成，原文件及已有恢复记录保留；开发能力按当前可安全读取的配置重新载入。";
            developerResult.Text = recoveryResult.Text;
        }
        catch (OperationCanceledException) { recoveryResult.Text = "USER_CANCELLED · 配置恢复已取消，已有文件和恢复记录保留。"; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { recoveryResult.Text = SafeRecoveryCode(error) + " · 配置恢复未完成；原文件和备份保留。"; }
        finally
        {
            if (capabilitiesClosed)
            {
                ReloadDeveloperConfigurationAfterRecovery();
                developerBusy = false;
                RefreshDeveloperButtons(); BuildAppEntries(); RefreshPermissionsUi();
            }
            developerLoading = false; recoveryBusy = false; RefreshRecoveryButtons(); RefreshDataRecoveryUi();
        }
    }

    private bool ReloadDeveloperConfigurationAfterRecovery()
    {
        bool enabled = false;
        var loaded = developerConfiguration!.Load();
        bool valid = loaded.Success && (loaded.Value is null || ValidDeveloperConfiguration(loaded.Value, out enabled));
        developerTools!.SetEnabled(valid && enabled);
        DeveloperModeToggle.IsOn = valid && enabled;
        DeveloperModeToggle.IsEnabled = valid;
        return valid;
    }

    private async void OnRestoreSaveBackup(object sender, RoutedEventArgs e)
    {
        if (recoveryBusy || aboutOpen || accountOperation || launching || recoveryBinding is not { } binding
            || recoverySlots.SelectedItem is not ComboBoxItem { Tag: string slot }
            || recoveryChoices.SingleOrDefault(item => item.Name == slot)?.Info is not { BackupExists: true, BackupValid: true }) return;
        recoveryBusy = true; accountOperation = true; RefreshRecoveryButtons(); RefreshAccountUi();
        try
        {
            binding.Account.EnsureCurrent();
            if (!await ConfirmRecoveryAsync("恢复元素配对存档？", $"即将从当前账号的 {slot} 备份恢复。游客与其他账号的存档不会变动。接下来需要确认已保存并关闭游戏；取消任一步都不会写入存档。当前主记录保留为 before-restore。", "继续，检查并关闭游戏"))
            { recoveryResult.Text = "USER_CANCELLED · 未恢复存档，当前游戏实例和未保存输入保持不变。"; return; }
            binding.Account.EnsureCurrent();
            var inspected = binding.Store.InspectSaveRecovery(slot, lifetime.Token);
            if (!inspected.Success || inspected.Value is not { BackupExists: true, BackupValid: true } usable)
            { recoveryResult.Text = (inspected.Success ? inspected.Value?.BackupError ?? "SAVE_BACKUP_NOT_FOUND" : inspected.ErrorCode) + " · 没有恢复或关闭游戏。"; return; }
            if (usable.MainExists && usable.BeforeRestoreExists)
            { recoveryResult.Text = "SAVE_RECOVERY_CONFLICT · 已有 before-restore，未覆盖任何记录，当前游戏保持不变。"; return; }
            if (!await PrepareAccountChangeAsync("保存并关闭游戏后恢复存档", "请先在游戏内保存需要保留的进度。此次操作不会切换账号；确认后关闭当前游戏及预览，再恢复选定备份。取消会保留原实例和未保存内容。"))
            { recoveryResult.Text = "USER_CANCELLED · 未恢复存档，当前游戏实例保持不变。"; return; }
            binding.Account.EnsureCurrent();
            var restored = await Task.Run(() => binding.Store.RestoreSave(slot, lifetime.Token), lifetime.Token);
            recoveryResult.Text = restored.Success
                ? $"已恢复当前账号的 {slot} 存档。" + (usable.MainExists ? "原主记录保留为 before-restore，" : "恢复前没有主记录，") + "有效备份仍保留。再次打开元素配对会读取恢复后的内容。"
                : restored.ErrorCode + " · 恢复未完成，原主记录、有效备份和已有恢复记录保留；没有覆盖其他账号或应用。";
        }
        catch (OperationCanceledException) { recoveryResult.Text = "USER_CANCELLED · 恢复已取消，已提交存档和备份保留。"; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { recoveryResult.Text = SafeRecoveryCode(error) + " · 恢复未完成，没有切换账号或删除原数据。"; }
        finally { accountOperation = false; recoveryBusy = false; RefreshAccountUi(); RefreshRecoveryButtons(); RefreshDataRecoveryUi(); }
    }

    private async Task<bool> ConfirmRecoveryAsync(string title, string explanation, string action)
    {
        if (aboutOpen || lifetime.IsCancellationRequested) return false;
        aboutOpen = true;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = title, Content = explanation,
            PrimaryButtonText = action, CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
        using var registration = lifetime.Token.Register(() => DispatcherQueue.TryEnqueue(() => dialog.Hide()));
        try { return await dialog.ShowAsync() == ContentDialogResult.Primary && !lifetime.IsCancellationRequested; }
        finally { aboutOpen = false; }
    }

    private static bool ValidDeveloperConfiguration(VersionedConfiguration? configuration, out bool enabled)
    {
        enabled = false;
        if (configuration is null || configuration.SchemaVersion != 1 || configuration.Values.ValueKind != JsonValueKind.Object
            || configuration.Values.EnumerateObject().Count() != 1 || !configuration.Values.TryGetProperty("enabled", out var value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        enabled = value.GetBoolean(); return true;
    }
    private static string SafeRecoveryCode(Exception error) => error switch
    {
        RuntimeCapabilityException capability => capability.Code,
        DataStoreException storage => storage.Code,
        UnauthorizedAccessException => "STORAGE_ACCESS_DENIED",
        IOException => "STORAGE_IO_ERROR",
        _ => "RECOVERY_CHECK_FAILED"
    };
    private sealed record RecoveryBinding(RuntimeAccountContext Account, RuntimeApplication Application, AccountDataStore Store);
    private sealed record RecoverySaveChoice(string Name, SaveRecoveryInfo Info, SaveSlot? Slot);
    private sealed class RecoveryEmptyLease : IDisposable { public void Dispose() { } }
}
