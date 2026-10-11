using AutumnOS.Contracts;
using AutumnOS.Update;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private async Task ConfirmUpdateTargetAsync(UpdateCandidate candidate, UpdateSelectionMode mode, bool startupPrompt = false)
    {
        if (updates is null || updateBusy || updateCommitBusy || updateDialogOpen || lifetime.IsCancellationRequested) return;
        bool confirmed = false;
        updateDialogOpen = true;
        RenderUpdateState();
        try
        {
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(Paragraph(UpdateTargetDescription(candidate, mode)));
            content.Children.Add(NativeReleaseNotes.Create(candidate.Notes));
            content.Children.Add(OfficialReleaseButton(candidate.Tag));
            var dialog = CreateUpdateDialog(mode == UpdateSelectionMode.Rollback ? "确认回滚 autumnOS" : "发现 autumnOS 新版本", content);
            AutomationProperties.SetAutomationId(dialog, "SystemUpdateConsentDialog");
            dialog.PrimaryButtonText = mode == UpdateSelectionMode.Rollback ? "确认回滚" : "确认更新";
            dialog.CloseButtonText = mode == UpdateSelectionMode.Rollback ? "暂不回滚" : "暂不更新";
            activeUpdateDialog = dialog;
            confirmed = await dialog.ShowAsync() == ContentDialogResult.Primary && !lifetime.IsCancellationRequested;
            if (!confirmed) updates.DismissCandidate(candidate);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { updateWait.Text = startupPrompt ? "更新提示已延后，你可以在设置中查看。" : SafeUpdateError(error); }
        finally { activeUpdateDialog = null; updateDialogOpen = false; RenderUpdateState(); }
        if (confirmed) await DownloadSystemUpdateAsync(candidate, mode, pinAfterSuccess: false);
    }

    private async Task SelectSystemVersionAsync(UpdateSelectionMode mode)
    {
        if (updates is null || updateBusy || updateCommitBusy || updateDialogOpen) return;
        updateDialogOpen = true;
        RenderUpdateState();
        using var historyCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        UpdateCandidate? accepted = null;
        bool pinAfterSuccess = false, loading = false, closed = false;
        UpdateReleaseChoice? selected = null;
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(Paragraph($"当前版本：{BrandInfo.DisplayVersion}\n" + (mode == UpdateSelectionMode.Rollback
            ? "请选择低于当前版本的目标。回滚前会核对数据兼容性并备份当前程序状态；不兼容的历史版本不能安装。"
            : "请选择高于当前版本的目标。列表中的稳定版与预览版均来自官方发行仓库。")));
        var choices = new ComboBox { Header = "目标版本（按稳定版／预览版分组）", HorizontalAlignment = HorizontalAlignment.Stretch, MaxDropDownHeight = 300 };
        AutomationProperties.SetAutomationId(choices, "SystemUpdateVersionChoices");
        var summary = Paragraph("正在加载发行记录…");
        var loadStatus = Paragraph("");
        var notes = new StackPanel { Spacing = 8 };
        var pin = new CheckBox { Content = "切换后固定此版本", IsChecked = true };
        var pinHelp = Paragraph("仅在版本切换及启动验证成功后固定；失败时保留原固定状态。固定后仍检查，但不会弹出常规升级提醒。");
        var loadMore = ActionButton("继续加载历史版本", "LoadSystemUpdateHistory", (_, _) => { });
        var enablePreview = ActionButton("开启并保存“接受预览版邀请”", "EnableSystemPreview", (_, _) => { });
        content.Children.Add(choices);
        content.Children.Add(loadStatus);
        content.Children.Add(loadMore);
        content.Children.Add(summary);
        content.Children.Add(enablePreview);
        content.Children.Add(pin);
        content.Children.Add(pinHelp);
        content.Children.Add(notes);
        var dialog = CreateUpdateDialog(mode == UpdateSelectionMode.Rollback ? "回滚到指定版本" : "更新到指定版本", content);
        AutomationProperties.SetAutomationId(dialog, "SystemUpdateVersionDialog");
        dialog.PrimaryButtonStyle = UpdateDialogButtonStyle("ConfirmSystemVersionSwitch");
        dialog.PrimaryButtonText = mode == UpdateSelectionMode.Rollback ? "确认回滚" : "确认更新";
        dialog.CloseButtonText = mode == UpdateSelectionMode.Rollback ? "暂不回滚" : "暂不更新";
        dialog.IsPrimaryButtonEnabled = false;
        activeUpdateDialog = dialog;
        dialog.Closed += (_, _) => { closed = true; historyCancellation.Cancel(); };

        void ShowSelection()
        {
            selected = (choices.SelectedItem as ComboBoxItem)?.Tag as UpdateReleaseChoice;
            notes.Children.Clear();
            if (selected is null)
            {
                summary.Text = "选择目标后可查看发布说明和不可安装原因。";
                enablePreview.Visibility = Visibility.Collapsed;
                pin.Visibility = pinHelp.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = false;
                return;
            }
            string reason = selected.CanInstall ? "此目标通过当前兼容性与可信校验；确认后才下载安装包。" : UpdateReason(selected.DisabledReason);
            summary.Text = $"{selected.Name} · {selected.Tag}\n{(selected.IsPreview ? "预览版" : "稳定版")} · 发布时间：{FormatUpdateDate(selected.PublishedAtUtc)}\n{reason}";
            if (selected.Candidate is { } candidate)
                summary.Text += $"\n安装包：{candidate.Manifest.Payload.Bytes:N0} 字节 · {candidate.Manifest.TargetRid}" +
                    (mode == UpdateSelectionMode.Rollback ? "\n回滚将备份当前配置并核对目标的数据读取能力。游戏、应用和存档不会作为主程序数据被覆盖。" : "");
            notes.Children.Add(NativeReleaseNotes.Create(selected.Notes));
            notes.Children.Add(OfficialReleaseButton(selected.Tag));
            enablePreview.Visibility = selected.IsPreview && !updates.Snapshot.PreviewEnabled ? Visibility.Visible : Visibility.Collapsed;
            bool manualTarget = selected.Candidate is { } target && target.ReleaseId != updates.Snapshot.Candidate?.ReleaseId;
            pin.Visibility = pinHelp.Visibility = manualTarget ? Visibility.Visible : Visibility.Collapsed;
            dialog.IsPrimaryButtonEnabled = !loading && selected.CanInstall && selected.Candidate is not null;
        }

        async Task LoadChoicesAsync(bool reset)
        {
            if (loading || closed) return;
            loading = true;
            loadMore.IsEnabled = false;
            choices.IsEnabled = false;
            dialog.IsPrimaryButtonEnabled = false;
            loadStatus.Text = "正在读取发行记录和可信清单…可关闭弹窗取消加载。";
            long? selectedId = selected?.ReleaseId;
            try
            {
                var page = await updates.LoadHistoryAsync(mode, reset, historyCancellation.Token);
                if (closed || historyCancellation.IsCancellationRequested) return;
                choices.Items.Clear();
                foreach (bool preview in new[] { false, true })
                {
                    choices.Items.Add(new ComboBoxItem { Content = preview ? "—— 预览版 ——" : "—— 稳定版 ——", IsEnabled = false });
                    foreach (var release in page.Items.Where(item => item.IsPreview == preview))
                    {
                        string reason = release.CanInstall ? "可安装" : UpdateReason(release.DisabledReason);
                        var row = new ComboBoxItem { Content = new TextBlock { Text = $"{release.Version ?? release.Tag} · {reason}", TextWrapping = TextWrapping.Wrap, MaxWidth = 480 }, Tag = release };
                        // Incompatible releases remain selectable for reading. The install action stays disabled.
                        AutomationProperties.SetHelpText(row, reason);
                        choices.Items.Add(row);
                    }
                }
                choices.SelectedItem = choices.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is UpdateReleaseChoice release && release.ReleaseId == selectedId)
                    ?? choices.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is UpdateReleaseChoice release && release.CanInstall)
                    ?? choices.Items.OfType<ComboBoxItem>().FirstOrDefault(item => item.Tag is UpdateReleaseChoice);
                loadStatus.Text = page.ErrorCode is { } error ? "加载尚未完成：" + UpdateReason(error) : page.Items.Count == 0 ? "官方源没有可显示的已发布版本。" : page.Items.Any(item => item.CanInstall) ? "请选择版本并查看下面的发布说明。" : "当前已加载记录没有可安装目标；可选择版本查看原因。";
                loadMore.Content = page.ErrorCode is null ? "继续加载历史版本" : "重试加载";
                loadMore.Visibility = page.HasMore || page.ErrorCode is not null ? Visibility.Visible : Visibility.Collapsed;
            }
            catch (OperationCanceledException) { if (!closed) loadStatus.Text = "历史记录加载已取消。"; }
            catch (Exception error) when (error is not OutOfMemoryException)
            { if (!closed) { loadStatus.Text = "读取发行记录失败：" + SafeUpdateError(error); loadMore.Content = "重试加载"; loadMore.Visibility = Visibility.Visible; } }
            finally
            {
                loading = false;
                if (!closed) { loadMore.IsEnabled = true; choices.IsEnabled = true; ShowSelection(); }
            }
        }

        choices.SelectionChanged += (_, _) => ShowSelection();
        loadMore.Click += async (_, _) => await LoadChoicesAsync(false);
        enablePreview.Click += async (_, _) =>
        {
            if (loading || closed) return;
            try
            {
                updates.SetPreviewEnabled(true);
                loadingUpdateSettings = true;
                previewUpdates.IsOn = updates.Snapshot.PreviewEnabled;
                loadingUpdateSettings = false;
                await LoadChoicesAsync(true);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { loadStatus.Text = SafeUpdateError(error) + " · 未保存，预览版仍不可安装。"; ShowSelection(); }
        };
        Task initialLoad = LoadChoicesAsync(true);
        try
        {
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary && !lifetime.IsCancellationRequested && selected?.CanInstall == true)
            {
                accepted = selected.Candidate;
                pinAfterSuccess = pin.Visibility == Visibility.Visible && pin.IsChecked == true;
            }
            else if (selected?.Candidate is { } dismissed) updates.DismissCandidate(dismissed);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { updateWait.Text = SafeUpdateError(error); }
        finally
        {
            closed = true;
            historyCancellation.Cancel();
            await initialLoad;
            activeUpdateDialog = null;
            updateDialogOpen = false;
            RenderUpdateState();
        }
        if (accepted is not null) await DownloadSystemUpdateAsync(accepted, mode, pinAfterSuccess);
    }

    private async Task ShowReadyToRestartAsync()
    {
        if (updates?.Snapshot.Staged is not { } staged || updateDialogOpen || lifetime.IsCancellationRequested) return;
        updateDialogOpen = true;
        bool restart = false;
        try
        {
            var dialog = CreateUpdateDialog("等待重启", Paragraph($"版本 {staged.Candidate.Manifest.Version} 已下载并验证。\n请保存内容并正常退出运行中的游戏或应用。\n选择“稍后重启”会保留待安装状态；普通退出和下次启动都不会自动安装。"));
            AutomationProperties.SetAutomationId(dialog, "SystemUpdateRestartDialog");
            dialog.PrimaryButtonStyle = UpdateDialogButtonStyle("ConfirmSystemUpdateRestart");
            dialog.CloseButtonStyle = UpdateDialogButtonStyle("PostponeSystemUpdateRestart");
            dialog.PrimaryButtonText = "重启并安装";
            dialog.CloseButtonText = "稍后重启";
            activeUpdateDialog = dialog;
            restart = await dialog.ShowAsync() == ContentDialogResult.Primary && !lifetime.IsCancellationRequested;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { updateWait.Text = SafeUpdateError(error); }
        finally { activeUpdateDialog = null; updateDialogOpen = false; RenderUpdateState(); }
        if (restart) await CommitSystemUpdateAsync();
        else updateWait.Text = "已保留经过验证的待安装状态；需要你再次选择“重启并安装”才会执行。";
    }

    private async Task ConfirmSystemRestartAsync()
    {
        if (updates?.Snapshot.Staged is null || updateBusy || updateCommitBusy || updateDialogOpen) return;
        await ShowReadyToRestartAsync();
    }

    private ContentDialog CreateUpdateDialog(string title, UIElement content) => new()
    {
        XamlRoot = Root.XamlRoot,
        Title = title,
        DefaultButton = ContentDialogButton.Close,
        PrimaryButtonStyle = UpdateDialogButtonStyle("ConfirmSystemUpdate"),
        CloseButtonStyle = UpdateDialogButtonStyle("DismissSystemUpdate"),
        Content = new ScrollViewer
        {
            Content = content,
            MaxHeight = Math.Max(64, Math.Min(460, Root.ActualHeight - 240)),
            MaxWidth = Math.Max(200, Math.Min(580, Root.ActualWidth - 120)),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        }
    };

    private static Button OfficialReleaseButton(string tag) => ActionButton("在浏览器查看官方发行页", "OpenOfficialSystemRelease", async (_, _) =>
    {
        try
        {
            var address = new Uri($"https://github.com/{UpdateService.Repository}/releases/tag/{Uri.EscapeDataString(tag)}");
            await Windows.System.Launcher.LaunchUriAsync(address);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { }
    });

    private static Style UpdateDialogButtonStyle(string automationId)
    {
        var style = new Style { TargetType = typeof(Button) };
        style.Setters.Add(new Setter(AutomationProperties.AutomationIdProperty, automationId));
        return style;
    }

    private static string UpdateTargetDescription(UpdateCandidate candidate, UpdateSelectionMode mode) =>
        $"当前版本：{BrandInfo.DisplayVersion}\n目标版本：{candidate.Manifest.Version} · {ReleaseChannel(candidate)}\n发布时间：{FormatUpdateDate(candidate.PublishedAtUtc)}\n安装包：{candidate.Manifest.Payload.Bytes:N0} 字节 · {candidate.Manifest.TargetRid}\n官方仓库：{candidate.Manifest.Repository}\n发行标记：{candidate.Tag}\n" +
        (ReleaseChannel(candidate) == "预览版" ? "预览版可能不稳定，请先保存工作。\n" : "") +
        (mode == UpdateSelectionMode.Rollback ? "回滚前会备份当前程序状态并再次核对数据兼容性。\n" : "") +
        "确认仅授权下载这个目标。验证完成后仍需你选择重启安装。\n\n发布说明";

    private static string FormatUpdateDate(DateTimeOffset? date) => date is { } value ? value.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : "发行记录未提供";
}
