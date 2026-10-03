using System.Globalization;
using AutumnOS.Contracts;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    internal const int LongPressMilliseconds = 600;
    internal const double LongPressMovementThreshold = 8;
    private readonly List<DispatcherTimer> pressTimers = [];
    private readonly HashSet<Button> heldButtons = [];
    private static string ThemeName(string value) => value switch { "dark" => "深色", "system" => "跟随系统", _ => "浅色" };
    private static string WallpaperName(string value) => value switch { "mist" => "山雾", "night" => "暮色", _ => "晨光" };
    private static Windows.UI.Color Color(string hex)
    {
        uint value = Convert.ToUInt32(hex.TrimStart('#'), 16);
        return ColorHelper.FromArgb(hex.TrimStart('#').Length == 8 ? (byte)(value >> 24) : (byte)255, (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
    private static SolidColorBrush Brush(string value) => new(Color(value));

    private void UpdateClock()
    {
        var now = DateTime.Now;
        StatusClock.Text = now.ToString("HH:mm");
        DesktopTime.Text = now.ToString("HH:mm");
        DesktopDate.Text = now.ToString("M月d日 · dddd", CultureInfo.GetCultureInfo("zh-CN"));
        SynchronizeAppearance();
    }

    private void ApplyPreferences()
    {
        Root.RequestedTheme = theme switch { "dark" => ElementTheme.Dark, "system" => ElementTheme.Default, _ => ElementTheme.Light };
        ApplyWallpaper();
        foreach (var button in new[] { LightButton, DarkButton, SystemButton })
        {
            bool selected = (string)button.Tag == theme;
            button.BorderThickness = new Thickness(selected ? 2 : 1);
            button.BorderBrush = selected ? Brush("6B8B73") : (Brush)Root.Resources["GlassStroke"];
            button.IsEnabled = preferencesWritable;
            AutomationProperties.SetHelpText(button, selected ? "当前主题" : "切换并保存主题");
        }
        foreach (var button in new[] { WarmButton, MistButton, NightButton })
        {
            bool selected = (string)button.Tag == wallpaper;
            button.BorderThickness = new Thickness(selected ? 3 : 1);
            button.BorderBrush = selected ? Brush("6B8B73") : (Brush)Root.Resources["GlassStroke"];
            button.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            button.IsEnabled = preferencesWritable;
            AutomationProperties.SetHelpText(button, selected ? "当前壁纸" : "切换并保存壁纸");
        }
        WelcomeTheme.Text = "已选择：" + ThemeName(theme);
        WelcomeLightButton.IsEnabled = WelcomeDarkButton.IsEnabled = preferencesWritable;
        DesktopAppearance.Text = ThemeName(theme) + " · " + WallpaperName(wallpaper);
    }

    private void ApplyWallpaper()
    {
        if (Sky is null) return;
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        string[] palette = (wallpaper, dark) switch
        {
            ("mist", false) => ["E1EDE5","BDD9D3","F5F9E8","FAFFE8","B4CAC1","90B4A6","628F84"],
            ("night", false) => ["E8E4F0","C3CBDF","F9ECDB","FFF5D6","B4B8D4","979FBC","6D849D"],
            ("mist", true) => ["172E2C","244542","709087","9DB6A4","284940","31594D","426B5B"],
            ("night", true) => ["1A2337","333650","686284","AF9BAC","333A59","41486A","58627D"],
            (_, true) => ["252D2C","38433A","9D7654","C99968","66584A","4B6658","38564E"],
            _ => ["F0E6D5","EAC9AF","FAF0D4","FFF3D3","D7AC89","B7B896","75917A"]
        };
        Sky.Fill = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), GradientStops = { new GradientStop { Color = Color(palette[0]), Offset = 0 }, new GradientStop { Color = Color(palette[1]), Offset = 1 } } };
        SunHalo.Fill = Brush(palette[2]); Sun.Fill = Brush(palette[3]); FarHill.Fill = Brush(palette[4]); MiddleHill.Fill = Brush(palette[5]); NearHill.Fill = Brush(palette[6]);
        ((SolidColorBrush)Root.Resources["DesktopInk"]).Color = Color(dark ? "EDF0E8" : "2C4038");
        ((SolidColorBrush)Root.Resources["DesktopMuted"]).Color = Color(dark ? "BCCBC0" : "556A5B");
        ((SolidColorBrush)Root.Resources["GlassFill"]).Color = Color(dark ? "A831413F" : "A8FFFFFF");
        ((SolidColorBrush)Root.Resources["GlassStroke"]).Color = Color(dark ? "447E9587" : "C4FFFFFF");
        ((SolidColorBrush)Root.Resources["SidebarSurface"]).Color = Color(dark ? "21242C" : "EEF0F4");
        ((SolidColorBrush)Root.Resources["SettingsSurface"]).Color = Color(dark ? "181B21" : "F7F8FA");
        ((SolidColorBrush)Root.Resources["StoreCardSurface"]).Color = Color(dark ? "272C33" : "FFFFFF");
        ((SolidColorBrush)Root.Resources["StoreCardStroke"]).Color = Color(dark ? "3A424A" : "E3E7E4");
        SelectSettingsCategory(selectedSettingsCategory);
        if (AppWindow.TitleBar is { } bar)
        {
            bar.BackgroundColor = Color(palette[0]); bar.ForegroundColor = Color(dark ? "EDF0E8" : "2C4038");
            bar.ButtonBackgroundColor = Color(palette[0]); bar.ButtonForegroundColor = bar.ForegroundColor;
            bar.InactiveBackgroundColor = bar.BackgroundColor; bar.ButtonInactiveBackgroundColor = bar.BackgroundColor;
        }
    }

    private void BuildAppEntries()
    {
        CancelDesktopDrag();
        HideRunningOperations();
        foreach (var menu in appMenus) menu.Hide();
        appMenus.Clear(); runningBadges.Clear(); managedBadges.Clear();
        notificationBadges.Clear();
        foreach (var timer in pressTimers) timer.Stop(); pressTimers.Clear(); heldButtons.Clear();
        AppGrid.Children.Clear(); desktopIds.Clear(); Dock.Children.Clear(); sampleButton = dockSampleButton = null;
        if (installedSample is not null)
        {
            sampleButton = AppButton(installedSample.Manifest.Name, "sample", "SampleButton", false, OnSampleIconClick);
            AppGrid.Children.Add(sampleButton);
            dockSampleButton = AppButton(installedSample.Manifest.Name, "sample", "DockSampleButton", true, OnSampleIconClick);
        }
        var settings = AppButton("设置", "settings", "SettingsButton", false, OnSettings);
        Grid.SetColumn(settings, 1); AppGrid.Children.Add(settings);
        var account = AppButton("账号", "account", "AccountButton", false, OnAccount);
        Grid.SetRow(account, 1); AppGrid.Children.Add(account);
        var store = AppButton("Autumn Store", "store", "StoreButton", false, OnStore);
        Grid.SetColumn(store, 2); AppGrid.Children.Add(store);
        var downloads = AppButton("下载", "downloads", "DownloadsButton", false, OnDownloads);
        Grid.SetColumn(downloads, 2); Grid.SetRow(downloads, 1); AppGrid.Children.Add(downloads);
        if (DeveloperModeEnabled)
        {
            var developer = AppButton("开发者工具", "developer", "DeveloperButton", false, OnDeveloperOpen);
            Grid.SetColumn(developer, 1); Grid.SetRow(developer, 1); AppGrid.Children.Add(developer);
        }
        Dock.Children.Add(AppButton("桌面", "home", "DockHomeButton", true, OnBackground));
        Dock.Children.Add(new Border { Width = 1, Height = 38, Background = (Brush)Root.Resources["GlassStroke"], VerticalAlignment = VerticalAlignment.Center });
        if (dockSampleButton is not null) Dock.Children.Add(dockSampleButton);
        Dock.Children.Add(AppButton("设置", "settings", "DockSettingsButton", true, OnSettings));
        Dock.Children.Add(AppButton("商店", "store", "DockStoreButton", true, OnStore));
        AddManagedDesktopEntries();
        ArrangeDesktopEntries();
        DiagnosticSampleButton.IsEnabled = installedSample is not null;
        UpdateRuntimeStatus(appHost?.Session.Instance.BlocksMaintenance == true ? RuntimeStatus.Text : "无游戏会话");
    }

    private Button AppButton(string name, string kind, string id, bool dock, RoutedEventHandler click)
    {
        double size = dock ? 56 : 82;
        var icon = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(dock ? 18 : 25), Background = Brush(kind == "sample" ? "F4EBCD" : kind == "settings" ? "DFE5EA" : "E0E9DB"), BorderBrush = Brush("B3FFFFFF"), BorderThickness = new Thickness(1) };
        if (kind == "sample")
        {
            var symbols = new Grid { Width = size * 0.55, Height = size * 0.55, RowSpacing = 3, ColumnSpacing = 3 };
            symbols.RowDefinitions.Add(new RowDefinition()); symbols.RowDefinitions.Add(new RowDefinition());
            symbols.ColumnDefinitions.Add(new ColumnDefinition()); symbols.ColumnDefinitions.Add(new ColumnDefinition());
            for (int i = 0; i < 4; i++)
            {
                var symbol = new TextBlock { Text = i is 0 or 3 ? "✦" : "●", FontSize = dock ? 16 : 23, Foreground = Brush(i is 0 or 3 ? "557459" : "B99254"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(symbol, i / 2); Grid.SetColumn(symbol, i % 2); symbols.Children.Add(symbol);
            }
            icon.Child = symbols;
        }
        else icon.Child = new FontIcon { Glyph = kind switch { "home" => "\uE80F", "account" => "\uE77B", "developer" => "\uE943", "store" => "\uE719", "downloads" => "\uE896", "managed" => "\uE8F1", _ => "\uE713" }, FontSize = dock ? 27 : 38, Foreground = Brush(kind == "home" ? "4C6C54" : "536674") };
        var content = new StackPanel { Spacing = dock ? 0 : 12, HorizontalAlignment = HorizontalAlignment.Center };
        Border? badge = null;
        if (kind is "sample" or "managed")
        {
            var iconLayer = new Grid { Width = size, Height = size };
            iconLayer.Children.Add(icon);
            var badgeText = new TextBlock { Text = "运行中", FontSize = dock ? 10 : 11, Foreground = Brush("FFFFFF") };
            AutomationProperties.SetAutomationId(badgeText, kind == "managed" ? id + "-RunningBadge" : dock ? "DockSampleRunningBadge" : "SampleRunningBadge");
            badge = new Border { Child = badgeText, Background = Brush("426A53"), CornerRadius = new CornerRadius(7), Padding = new Thickness(6, 2, 6, 2), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, -6), Visibility = Visibility.Collapsed };
            iconLayer.Children.Add(badge);
            var notice = new TextBlock { Text = "", FontSize = 12, Foreground = Brush("FFFFFF"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            var noticeBadge = new Border { Child = notice, Visibility = Visibility.Collapsed, Background = Brush("A34646"), CornerRadius = new CornerRadius(10), Padding = new Thickness(5, 1, 5, 1), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
            iconLayer.Children.Add(noticeBadge);
            icon.Tag = noticeBadge;
            content.Children.Add(iconLayer);
        }
        else content.Children.Add(icon);
        if (!dock) content.Children.Add(new TextBlock { Text = name, FontSize = 13, MaxWidth = 110, MaxLines = 2, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = (Brush)Root.Resources["DesktopInk"], HorizontalAlignment = HorizontalAlignment.Center });
        var button = new Button { Content = content, Padding = new Thickness(dock ? 3 : 9), Background = Brush("00FFFFFF"), BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(20), HorizontalAlignment = HorizontalAlignment.Center };
        AutomationProperties.SetAutomationId(button, id); AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += click;
        if (!dock) desktopIds[button] = kind switch
        {
            "sample" => "app.cn.labchronicles.elementpairs",
            "managed" => "app." + id["InstalledApp-".Length..],
            _ => "system." + kind
        };
        if (badge is not null && kind == "sample")
        {
            runningBadges.Add(button, badge);
            if (icon.Tag is Border notice) notificationBadges.Add(button, notice);
        }
        if (kind != "home") AttachAppMenu(button, kind);
        return button;
    }

    private bool ConsumeHeldClick(object sender) => sender is Button button && (ReferenceEquals(draggedDesktopButton, button) || heldButtons.Contains(button));

    private void AttachAppMenu(Button button, string kind)
    {
        var menu = new MenuFlyout();
        appMenus.Add(menu);
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            if (kind == "managed") { FillManagedMenu(menu, button); AddDesktopMoveMenus(menu, button); return; }
            var target = kind == "sample" && HasLiveGame ? appHost : null;
            var instanceId = target?.Session.Instance.Id;
            var open = new MenuFlyoutItem { Text = target is not null ? "继续游戏" : "打开" };
            AutomationProperties.SetAutomationId(open, kind == "sample" ? "SampleMenuOpen" : "SettingsMenuOpen");
            open.Click += (_, e) =>
            {
                heldButtons.Remove(button);
                if (target is not null && instanceId is { } capturedId) ContinueCapturedGame(target, capturedId);
                else if (kind == "sample") { if (!HasLiveGame) OnSample(menu, e); }
                else if (kind == "account") OnAccount(menu, e);
                else if (kind == "developer") OnDeveloperOpen(menu, e);
                else if (kind == "store") OnStore(menu, e);
                else if (kind == "downloads") OnDownloads(menu, e);
                else OnSettings(menu, e);
            };
            menu.Items.Add(open);
            if (kind == "sample")
            {
                var info = new MenuFlyoutItem { Text = "应用信息" }; AutomationProperties.SetAutomationId(info, "SampleMenuInfo");
                info.Click += OnApplicationInfo; menu.Items.Add(info);
                if (target is not null)
                {
                    var close = new MenuFlyoutItem { Text = "结束游戏" };
                    AutomationProperties.SetAutomationId(close, "SampleMenuClose");
                    close.Click += async (_, _) =>
                    {
                        if (IsCurrentLiveHost(target) && target.Session.Instance.Id == instanceId) await CloseCapturedGameAsync(target);
                    };
                    menu.Items.Add(close);
                    AddDeclaredShortcutMenus(menu, target.Session.Instance.Id.Value);
                }
            }
            else { var about = new MenuFlyoutItem { Text = "关于 AutumnOS" }; about.Click += OnAbout; menu.Items.Add(about); }
            AddDesktopMoveMenus(menu, button);
        };
        button.ContextFlyout = menu; // Native right-click / Shift+F10 / Escape equivalents.
        var pressScale = new CompositeTransform();
        button.RenderTransform = pressScale;
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LongPressMilliseconds) };
        pressTimers.Add(timer);
        Point start = default;
        bool pressed = false;
        uint pointerId = 0;
        menu.Closed += (_, _) => { if (!pressed) heldButtons.Remove(button); };
        timer.Tick += (_, _) =>
        {
            timer.Stop(); if (!pressed || draggedDesktopButton is not null || lifetime.IsCancellationRequested) return;
            heldButtons.Add(button); button.Opacity = 1; pressScale.ScaleX = pressScale.ScaleY = 1;
            menu.ShowAt(button); // Only this application button receives the context menu.
        };
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            var point = e.GetCurrentPoint(button);
            if (pressed || !point.Properties.IsLeftButtonPressed) return;
            pointerId = e.Pointer.PointerId;
            heldButtons.Remove(button); pressed = true; start = e.GetCurrentPoint(AppGrid).Position; button.Opacity = 0.8;
            pressScale.ScaleX = pressScale.ScaleY = 0.96; timer.Start();
        }), true);
        button.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
        {
            if (!pressed || e.Pointer.PointerId != pointerId) return;
            var point = e.GetCurrentPoint(AppGrid).Position;
            if (Math.Abs(point.X - start.X) > LongPressMovementThreshold || Math.Abs(point.Y - start.Y) > LongPressMovementThreshold)
            {
                timer.Stop(); button.Opacity = 1; pressScale.ScaleX = pressScale.ScaleY = 1;
                if (!heldButtons.Contains(button) || ReferenceEquals(draggedDesktopButton, button)) UpdateDesktopDrag(button, start, point, e);
            }
        }), true);
        void EndPress()
        {
            pressed = false; timer.Stop(); button.Opacity = 1; pressScale.ScaleX = pressScale.ScaleY = 1;
            // Keep suppression through the current ButtonBase release/click dispatch,
            // then let the next keyboard activation work after the flyout closes.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => heldButtons.Remove(button));
        }
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, e) => { if (e.Pointer.PointerId != pointerId) return; CompleteDesktopDrag(button, e.GetCurrentPoint(AppGrid).Position); EndPress(); }), true);
        button.AddHandler(UIElement.PointerCanceledEvent, new PointerEventHandler((_, e) => { if (e.Pointer.PointerId != pointerId) return; if (ReferenceEquals(draggedDesktopButton, button) && e.Pointer.PointerId == desktopDragPointerId) CancelDesktopDrag(); EndPress(); }), true);
        button.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, e) =>
        {
            if (e.Pointer.PointerId != pointerId) return;
            if (!e.GetCurrentPoint(button).Properties.IsLeftButtonPressed) CompleteDesktopDrag(button, e.GetCurrentPoint(AppGrid).Position);
            else if (ReferenceEquals(draggedDesktopButton, button) && e.Pointer.PointerId == desktopDragPointerId) CancelDesktopDrag();
            EndPress();
        }), true);
    }

    private async void OnApplicationInfo(object sender, RoutedEventArgs e)
    {
        if (aboutOpen || installedSample is null) return;
        aboutOpen = true;
        var app = installedSample;
        var dialog = new ContentDialog { XamlRoot = Root.XamlRoot, Title = app.Manifest.Name, Content = $"版本 {app.Manifest.Version}\n{app.Manifest.AppId}\n\n已安装 · 内部应用\n权限：自己的游客存档\n状态：{(appHost?.Session.Instance.BlocksMaintenance == true ? "运行中" : "未运行")}", CloseButtonText = "关闭" };
        try { await dialog.ShowAsync(); } finally { aboutOpen = false; }
    }
}
