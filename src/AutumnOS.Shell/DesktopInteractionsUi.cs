using AutumnOS.Contracts;
using AutumnOS.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Foundation;
using Windows.System;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private const double DesktopCellWidth = 130, DesktopCellHeight = 136;
    private readonly Dictionary<Button, string> desktopIds = [];
    private DesktopLayoutStore? desktopLayoutStore;
    private List<string> desktopOrder = [];
    private bool desktopLayoutWritable, renderingSwitcher;
    private Button? draggedDesktopButton;
    private Button? desktopDropTarget;
    private uint desktopDragPointerId;
    private long suppressDesktopDoubleTapUntil;
    private const string DesktopGestureText = "拖动图标调整位置 · 双击桌面空白处查看后台";
    private void InitializeDesktopInteractions()
    {
        DesktopPanel.AddHandler(UIElement.DoubleTappedEvent, new DoubleTappedEventHandler((_, args) =>
        {
            if (DesktopPanel.Visibility != Visibility.Visible || draggedDesktopButton is not null ||
                Environment.TickCount64 < suppressDesktopDoubleTapUntil || !IsDesktopBlank(args.OriginalSource as DependencyObject)) return;
            args.Handled = true;
            ShowRunningSwitcher();
        }), true);
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Key == VirtualKey.Escape && draggedDesktopButton is not null)
            { CancelDesktopDrag(); args.Handled = true; }
            else if (args.Key == VirtualKey.Escape && RunningSwitcher.Visibility == Visibility.Visible)
            { HideRunningSwitcher(); args.Handled = true; }
            else if (args.Key == VirtualKey.F6 && DesktopPanel.Visibility == Visibility.Visible)
            { ShowRunningSwitcher(); args.Handled = true; }
        }), true);
        Root.SizeChanged += (_, _) =>
        {
            CancelDesktopDrag();
            SwitcherSurface.MaxHeight = Math.Max(180, Root.ActualHeight - 64);
        };
        Activated += (_, args) => { if (args.WindowActivationState == WindowActivationState.Deactivated) CancelDesktopDrag(); };
    }

    private bool IsDesktopBlank(DependencyObject? source)
    {
        for (var node = source; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, DesktopWidgetContainer) || ReferenceEquals(node, Dock)) return false;
            if (node is Control and not ScrollViewer) return false; // Scrolling content gaps are blank; actual controls and scrollbars are not.
            if (ReferenceEquals(node, DesktopPanel)) return true;
        }
        return false;
    }

    private void LoadDesktopLayout()
    {
        desktopLayoutStore = new DesktopLayoutStore(installationRoot, criticalOperations);
        var state = desktopLayoutStore.Load(lifetime.Token);
        desktopLayoutWritable = state.Success;
        desktopOrder = state.Success ? [.. state.State!.OrderedIds] : [];
        if (!state.Success)
        {
            DesktopError.Title = "桌面排序暂时不能保存";
            DesktopError.Message = state.ErrorCode + " · " + state.RecoveryMessage;
            DesktopError.IsOpen = true;
        }
    }

    private List<Button> OrderedDesktopButtons() => desktopIds.Keys.OrderBy(Grid.GetRow).ThenBy(Grid.GetColumn).ToList();

    private void ArrangeDesktopEntries()
    {
        var defaults = OrderedDesktopButtons().Select(button => desktopIds[button]).ToArray();
        try { desktopOrder = [.. DesktopLayoutStore.ResolveOrder(desktopOrder, defaults)]; }
        catch (ArgumentException)
        {
            desktopLayoutWritable = false;
            desktopOrder = desktopOrder.Concat(defaults).Distinct(StringComparer.Ordinal).ToList();
            DesktopError.Title = "桌面排序达到容量限制";
            DesktopError.Message = "原布局文件已保留。应用入口仍可使用，本次不能保存新的排列。";
            DesktopError.IsOpen = true;
        }
        var byId = desktopIds.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
        int index = 0;
        AppGrid.RowDefinitions.Clear();
        foreach (string id in desktopOrder)
        {
            if (!byId.TryGetValue(id, out var button)) continue;
            while (AppGrid.RowDefinitions.Count <= index / 3) AppGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(DesktopCellHeight) });
            Grid.SetRow(button, index / 3); Grid.SetColumn(button, index % 3);
            button.VerticalAlignment = VerticalAlignment.Top;
            button.TabIndex = index++;
        }
        DesktopGestureHint.Text = desktopLayoutWritable ? DesktopGestureText : "桌面排序只读 · 双击空白处查看后台";
    }

    private void AddDesktopMoveMenus(MenuFlyout menu, Button button)
    {
        if (!desktopIds.ContainsKey(button)) return;
        var current = OrderedDesktopButtons();
        int index = current.IndexOf(button);
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (int offset in new[] { -1, 1 })
        {
            int target = index + offset;
            var item = new MenuFlyoutItem { Text = offset < 0 ? "向前移动" : "向后移动", IsEnabled = desktopLayoutWritable && target >= 0 && target < current.Count };
            AutomationProperties.SetAutomationId(item, offset < 0 ? "DesktopMoveEarlier" : "DesktopMoveLater");
            item.Click += (_, _) => { heldButtons.Remove(button); MoveDesktopEntry(button, target); };
            menu.Items.Add(item);
        }
    }

    private void UpdateDesktopDrag(Button button, Point start, Point point, PointerRoutedEventArgs args)
    {
        if (!desktopLayoutWritable || !desktopIds.ContainsKey(button) || DesktopPanel.Visibility != Visibility.Visible || aboutOpen) return;
        if (draggedDesktopButton is null)
        {
            draggedDesktopButton = button;
            desktopDragPointerId = args.Pointer.PointerId;
            heldButtons.Add(button);
            foreach (var menu in appMenus) menu.Hide();
            // ButtonBase already owns mouse capture. Keep it through release so its
            // native pressed/keyboard state resets; suppress Click via heldButtons.
            if (button.PointerCaptures?.Any(pointer => pointer.PointerId == args.Pointer.PointerId) != true &&
                !button.CapturePointer(args.Pointer)) { CancelDesktopDrag(); return; }
            Canvas.SetZIndex(button, 20);
        }
        if (!ReferenceEquals(draggedDesktopButton, button)) return;
        args.Handled = true;
        if (button.RenderTransform is CompositeTransform transform)
        { transform.TranslateX = point.X - start.X; transform.TranslateY = point.Y - start.Y; transform.ScaleX = transform.ScaleY = 1.04; }
        if (desktopDropTarget is not null) desktopDropTarget.Opacity = 1;
        var entries = OrderedDesktopButtons();
        int index = DesktopDropIndex(point, entries.Count);
        desktopDropTarget = index >= 0 ? entries[index] : null;
        if (desktopDropTarget is not null && !ReferenceEquals(desktopDropTarget, button)) desktopDropTarget.Opacity = 0.45;
        DesktopGestureHint.Text = index < 0 ? "松开取消移动 · Esc 取消" : $"移动到第 {index + 1} 个位置 · 松开保存 · Esc 取消";
    }

    private int DesktopDropIndex(Point point, int count)
    {
        if (count == 0 || point.X < 0 || point.Y < 0 || point.X >= AppGrid.ActualWidth || point.Y >= AppGrid.ActualHeight) return -1;
        // Reject drops outside the visible scrolling viewport even if the full grid continues below it.
        Point viewportPoint = AppGrid.TransformToVisual(DesktopAppScroll).TransformPoint(point);
        if (viewportPoint.Y < 0 || viewportPoint.Y >= DesktopAppScroll.ActualHeight) return -1;
        return Math.Min(count - 1, (int)(point.Y / DesktopCellHeight) * 3 + Math.Min(2, (int)(point.X / DesktopCellWidth)));
    }

    private void CompleteDesktopDrag(Button button, Point point)
    {
        if (!ReferenceEquals(draggedDesktopButton, button)) return;
        int target = DesktopDropIndex(point, desktopIds.Count);
        CancelDesktopDrag();
        if (target >= 0) MoveDesktopEntry(button, target);
    }

    private void CancelDesktopDrag()
    {
        if (draggedDesktopButton is not { } button) return;
        draggedDesktopButton = null;
        desktopDragPointerId = 0;
        heldButtons.Add(button); // Keep release from becoming an application click.
        suppressDesktopDoubleTapUntil = Environment.TickCount64 + 650;
        if (button.RenderTransform is CompositeTransform transform)
        { transform.TranslateX = transform.TranslateY = 0; transform.ScaleX = transform.ScaleY = 1; }
        Canvas.SetZIndex(button, 0); button.Opacity = 1;
        if (desktopDropTarget is not null) desktopDropTarget.Opacity = 1;
        desktopDropTarget = null;
        DesktopGestureHint.Text = DesktopGestureText;
    }

    private void MoveDesktopEntry(Button button, int target)
    {
        if (!desktopLayoutWritable || desktopLayoutStore is null || !desktopIds.TryGetValue(button, out string? movingId)) return;
        var visible = OrderedDesktopButtons().Select(item => desktopIds[item]).ToList();
        int from = visible.IndexOf(movingId);
        if (from < 0 || target < 0 || target >= visible.Count || from == target) return;
        visible.RemoveAt(from); visible.Insert(target, movingId);
        var available = visible.ToHashSet(StringComparer.Ordinal);
        int cursor = 0;
        // Hidden entries retain their slots; only currently visible slots are permuted.
        var next = desktopOrder.Select(id => available.Contains(id) ? visible[cursor++] : id).ToArray();
        var saved = desktopLayoutStore.Save(next, lifetime.Token);
        if (!saved.Success)
        {
            DesktopError.Title = "位置未保存"; DesktopError.Message = saved.ErrorCode + " · " + saved.RecoveryMessage; DesktopError.IsOpen = true;
            return; // Visual order is unchanged on failed commit.
        }
        desktopOrder = [.. saved.State!.OrderedIds]; ArrangeDesktopEntries();
        DesktopGestureHint.Text = "位置已保存 · 双击桌面空白处查看后台";
        button.Focus(FocusState.Programmatic);
    }

    private void OnDismissSwitcher(object sender, RoutedEventArgs args) => HideRunningSwitcher();

    private void HideRunningSwitcher()
    {
        if (RunningSwitcher.Visibility != Visibility.Visible) return;
        RunningSwitcher.Visibility = Visibility.Collapsed;
        RunningCards.Children.Clear();
        DesktopPanel.IsHitTestVisible = true;
        sampleButton?.Focus(FocusState.Programmatic);
    }

    private void ShowRunningSwitcher()
    {
        if (DesktopPanel.Visibility != Visibility.Visible || aboutOpen || launching || accountOperation || draggedDesktopButton is not null) return;
        foreach (var menu in appMenus) menu.Hide();
        HideRunningOperations();
        RunningSwitcher.Visibility = Visibility.Visible;
        DesktopPanel.IsHitTestVisible = false;
        RenderRunningSwitcher();
        SwitcherClose.Focus(FocusState.Programmatic);
        if (systemUiSettings.AnimationsEnabled)
        {
            var transform = new TranslateTransform(); SwitcherSurface.RenderTransform = transform;
            var enter = new DoubleAnimation { From = 24, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(220)), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(enter, transform); Storyboard.SetTargetProperty(enter, "Y");
            var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(180)) };
            Storyboard.SetTarget(fade, SwitcherSurface); Storyboard.SetTargetProperty(fade, "Opacity");
            var storyboard = new Storyboard(); storyboard.Children.Add(enter); storyboard.Children.Add(fade); storyboard.Begin();
        }
    }

    private void RefreshRunningSwitcherIfOpen()
    {
        if (RunningSwitcher.Visibility == Visibility.Visible && !renderingSwitcher) RenderRunningSwitcher();
    }

    private void RenderRunningSwitcher()
    {
        if (renderingSwitcher) return;
        renderingSwitcher = true;
        try
        {
            RunningCards.Children.Clear();
            if (appHost is { } sample && IsCurrentLiveHost(sample))
            {
                var instance = sample.Session.Instance.Id;
                AddRunningCard(installedSample?.Manifest.Name ?? "元素配对", "✦", sample, "RunningPanelTitle", "RunningContinueButton", "RunningEndButton",
                    () => { if (IsCurrentLiveHost(sample) && sample.Session.Instance.Id == instance) ContinueCapturedGame(sample, instance); },
                    async () => { if (IsCurrentLiveHost(sample) && sample.Session.Instance.Id == instance) { HideRunningSwitcher(); await CloseCapturedGameAsync(sample); } });
            }
            int managedIndex = 0;
            foreach (var entry in managedApplications.OrderBy(pair => pair.Key))
            {
                string id = entry.Key; var captured = entry.Value;
                if (!captured.Host.Session.Instance.BlocksMaintenance) continue;
                var instance = captured.Host.Session.Instance.Id;
                bool StillCurrent() => managedApplications.TryGetValue(id, out var current) && ReferenceEquals(current, captured) && current.Host.Session.Instance.Id == instance && current.Host.Session.Instance.BlocksMaintenance;
                string suffix = managedIndex++ == 0 ? "" : "-" + id;
                AddRunningCard(captured.Name, "▥", captured.Host, "RunningTitle-" + id, "ManagedContinue" + suffix, "ManagedEnd" + suffix,
                    () => { if (StillCurrent()) ActivateManagedApplication(id, captured); },
                    async () => { if (StillCurrent()) { HideRunningSwitcher(); await CloseManagedApplicationAsync(id, captured); } });
            }
            if (developerPreviewHost is { } preview && DeveloperModeEnabled && preview.Session.Instance.BlocksMaintenance)
            {
                var instance = preview.Session.Instance.Id;
                bool StillCurrent() => ReferenceEquals(developerPreviewHost, preview) && preview.Session.Instance.Id == instance && preview.Session.Instance.BlocksMaintenance && DeveloperModeEnabled;
                AddRunningCard("开发者预览", "{ }", preview, "RunningPreviewTitle", "RunningPreviewContinue", "RunningPreviewEnd",
                    () => { if (StillCurrent()) OnDeveloperOpen(RunningSwitcher, new RoutedEventArgs()); },
                    async () =>
                    {
                        if (!StillCurrent() || aboutOpen) return;
                        HideRunningSwitcher(); aboutOpen = true;
                        try
                        {
                            var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = "结束开发者预览？", Content = "请先保存进度，未保存的输入会丢失。已保存的数据保留。", PrimaryButtonText = "确认结束", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Close };
                            if (await dialog.ShowAsync() == ContentDialogResult.Primary && StillCurrent()) CloseDeveloperPreview();
                        }
                        finally { aboutOpen = false; }
                    });
            }
            int count = RunningCards.Children.Count;
            SwitcherSummary.Text = count == 0 ? "桌面已就绪，开启下一段探索。" : $"{count} 个应用保留在后台，随时接着继续。";
            if (count == 0)
            {
                var empty = new StackPanel { Width = 420, Spacing = 16, Padding = new Thickness(22, 40, 22, 40) };
                empty.Children.Add(new TextBlock { Text = "✦", FontSize = 48, Foreground = Brush("7C987E") });
                var title = new TextBlock { Text = "没有正在运行的应用", FontSize = 22 }; AutomationProperties.SetAutomationId(title, "RunningSwitcherEmpty");
                empty.Children.Add(title); empty.Children.Add(new TextBlock { Text = "从桌面打开应用后，在这里切换或结束。", TextWrapping = TextWrapping.Wrap, Opacity = 0.65 });
                RunningCards.Children.Add(empty);
            }
        }
        finally { renderingSwitcher = false; }
    }

    private void AddRunningCard(string name, string glyph, WebAppHost host, string titleId, string continueId, string endId, Action resume, Func<Task> end)
    {
        var contents = new StackPanel { Width = 250, Spacing = 16 };
        var title = new TextBlock { Text = name, FontSize = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, MaxLines = 2 };
        AutomationProperties.SetAutomationId(title, titleId);
        contents.Children.Add(title);
        var artwork = new Grid { Height = 150, Background = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), GradientStops = { new GradientStop { Color = Color("DFE9D8"), Offset = 0 }, new GradientStop { Color = Color("F0DFC7"), Offset = 1 } } } };
        artwork.Children.Add(new TextBlock { Text = glyph, FontSize = 56, Foreground = Brush("466652"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        contents.Children.Add(new Border { Child = artwork, CornerRadius = new CornerRadius(20) });
        contents.Children.Add(new TextBlock { Text = host.Session.Instance.State == AppLifecycleState.Suspended ? "●  已挂起 · 会话保留" : "●  后台运行 · 会话保留", Opacity = 0.7, FontSize = 12 });
        var open = ActionButton("继续游戏", continueId, (_, _) => resume()); open.HorizontalAlignment = HorizontalAlignment.Stretch;
        open.HorizontalContentAlignment = HorizontalAlignment.Center; open.CornerRadius = new CornerRadius(14); open.Padding = new Thickness(14, 12, 14, 12); open.Background = Brush("426A53"); open.Foreground = Brush("FFFFFF");
        var close = ActionButton("结束游戏", endId, async (_, _) => await end()); close.HorizontalAlignment = HorizontalAlignment.Stretch; close.HorizontalContentAlignment = HorizontalAlignment.Center; close.CornerRadius = new CornerRadius(14); close.Padding = new Thickness(14, 10, 14, 10);
        contents.Children.Add(open); contents.Children.Add(close);
        RunningCards.Children.Add(new Border { Child = contents, Padding = new Thickness(20), CornerRadius = new CornerRadius(24), Background = (Brush)Root.Resources["StoreCardSurface"], BorderBrush = (Brush)Root.Resources["StoreCardStroke"], BorderThickness = new Thickness(1) });
    }
}
