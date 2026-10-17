using AutumnOS.Contracts;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;
using Windows.UI.Core;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private readonly HashSet<string> gameShortcutHintsShown = new(StringComparer.Ordinal);
    private readonly List<Border> gameShortcutHints = [];
    private readonly DispatcherTimer gameShortcutHintTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private bool gameShortcutPending;
    private NativeGameKeyboardBridge? nativeGameKeyboard;
    private nint gameShortcutWindow;

    private void InitializeGameShortcuts()
    {
        gameShortcutWindow = WinRT.Interop.WindowNative.GetWindowHandle(this);
        Root.PreviewKeyDown += (_, args) =>
        {
            // A held Esc that dismissed a popup must not trigger the game shortcut
            // when Windows sends its next repeat after the popup has closed.
            bool closeChord = args.Key == VirtualKey.Q &&
                InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down) &&
                InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down);
            if (args.KeyStatus.WasKeyDown && (args.Key == VirtualKey.Escape || closeChord) &&
                (AppPanel.Visibility == Visibility.Visible || ManagedAppPanel.Visibility == Visibility.Visible)) args.Handled = true;
        };
        // WebView's native focus can sit outside the XAML panel's scope. Register
        // window accelerators and gate every action against the visible exact session.
        // Web content cannot request these actions through SDK messages.
        var home = new KeyboardAccelerator { Key = VirtualKey.Escape };
        home.Invoked += (_, args) => args.Handled = TryHandleGameShortcut(VirtualKey.Escape, VirtualKeyModifiers.None);
        var close = new KeyboardAccelerator { Key = VirtualKey.Q, Modifiers = VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift };
        close.Invoked += (_, args) => args.Handled = TryHandleGameShortcut(VirtualKey.Q, VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        Root.KeyboardAccelerators.Add(home);
        Root.KeyboardAccelerators.Add(close);

        foreach (var panel in new[] { AppPanel, ManagedAppPanel })
        {

            var text = new TextBlock
            {
                Text = "Esc 返回桌面 · Ctrl+Shift+Q 结束游戏（需确认）\n返回桌面后，可用 F6 打开多任务继续管理。",
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                FontSize = 13,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 246, 247, 240))
            };
            AutomationProperties.SetAutomationId(text, panel == AppPanel ? "GameShortcutHint" : "ManagedGameShortcutHint");
            AutomationProperties.SetLiveSetting(text, AutomationLiveSetting.Polite);
            var hint = new Border
            {
                Child = text,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(241, 45, 73, 61)),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(20, 12, 20, 12),
                Margin = new Thickness(20, 18, 20, 18),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                MaxWidth = 560,
                IsHitTestVisible = false,
                Visibility = Visibility.Collapsed
            };
            Canvas.SetZIndex(hint, 20);
            panel.Children.Add(hint);
            gameShortcutHints.Add(hint);
            panel.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => OnGamePageVisibilityChanged(panel, hint, text));
        }

        // Respect already handled keys: closing a native permission dialog with Esc
        // must not subsequently background the game during the same routed event.
        Root.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, args) =>
        {
            if (args.Handled) return;
            VirtualKeyModifiers modifiers = VirtualKeyModifiers.None;
            if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down)) modifiers |= VirtualKeyModifiers.Control;
            if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(CoreVirtualKeyStates.Down)) modifiers |= VirtualKeyModifiers.Shift;
            if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu).HasFlag(CoreVirtualKeyStates.Down)) modifiers |= VirtualKeyModifiers.Menu;
            if (InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.LeftWindows).HasFlag(CoreVirtualKeyStates.Down) ||
                InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.RightWindows).HasFlag(CoreVirtualKeyStates.Down)) modifiers |= VirtualKeyModifiers.Windows;
            args.Handled = TryHandleGameShortcut(args.Key, modifiers);
        }), true);
        gameShortcutHintTimer.Tick += (_, _) => DismissGameShortcutHints(animate: true);
        Closed += (_, _) => { nativeGameKeyboard?.Dispose(); nativeGameKeyboard = null; DismissGameShortcutHints(animate: false); };
    }

    private bool TryHandleGameShortcut(VirtualKey key, VirtualKeyModifiers modifiers)
    {
        bool returning = key == VirtualKey.Escape && modifiers == VirtualKeyModifiers.None;
        bool closing = key == VirtualKey.Q && modifiers == (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift);
        if ((!returning && !closing) || aboutOpen || launching || accountOperation || lifetime.IsCancellationRequested || !InteractionRoot.IsEnabled) return false;
        WebAppHost? captured = null;
        ManagedApplication? managed = null;
        string? managedId = null;
        if (AppPanel.Visibility == Visibility.Visible && appHost is { } sample && IsGameShortcutState(sample, returning)) captured = sample;
        else if (ManagedAppPanel.Visibility == Visibility.Visible && visibleManagedApp is { } id && managedApplications.TryGetValue(id, out var application) && IsGameShortcutState(application.Host, returning))
        { captured = application.Host; managed = application; managedId = id; }
        if (captured is null) return false;
        if (gameShortcutPending) return true;
        gameShortcutPending = true;
        // Defer mutations until native keyboard dispatch has completed. Capture and
        // recheck the exact instance, so a delayed key cannot act on another game.
        if (!DispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                if (aboutOpen || launching || accountOperation || lifetime.IsCancellationRequested || !InteractionRoot.IsEnabled || !IsGameShortcutState(captured, returning)) return;
                bool stillVisible = managed is null
                    ? AppPanel.Visibility == Visibility.Visible && ReferenceEquals(appHost, captured)
                    : ManagedAppPanel.Visibility == Visibility.Visible && visibleManagedApp == managedId && managedApplications.TryGetValue(managedId!, out var current) && ReferenceEquals(current, managed);
                if (!stillVisible) return;
                if (returning)
                {
                    OnBackground(Root, new RoutedEventArgs());
                    sampleButton?.Focus(FocusState.Programmatic);
                }
                else if (managed is not null) await CloseManagedApplicationAsync(managedId!, managed);
                else await CloseCapturedGameAsync(captured);
            }
            finally { gameShortcutPending = false; }
        })) gameShortcutPending = false;
        return gameShortcutPending;
    }

    private static bool IsGameShortcutState(WebAppHost host, bool returning) =>
        host.Session.Instance.State == AppLifecycleState.Foreground ||
        (returning && host.Session.Instance.State is AppLifecycleState.Crashed or AppLifecycleState.Closed);

    private void OnGamePageVisibilityChanged(Grid panel, Border hint, TextBlock text)
    {
        if (AppPanel.Visibility == Visibility.Visible || ManagedAppPanel.Visibility == Visibility.Visible)
        {
            try { nativeGameKeyboard ??= new NativeGameKeyboardBridge(OwnsGameKeyboardForeground, TryHandleGameShortcut); }
            catch (System.ComponentModel.Win32Exception)
            {
                // Returning to an already-running game bypasses the launch try/catch.
                // Keep its session and expose a usable desktop if registration fails.
                Grid failedPanel = AppPanel.Visibility == Visibility.Visible ? AppPanel : ManagedAppPanel;
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (lifetime.IsCancellationRequested || failedPanel.Visibility != Visibility.Visible) return;
                    OnBackground(Root, new RoutedEventArgs());
                    DesktopError.Title = "无法启用游戏快捷键";
                    DesktopError.Message = "系统暂时无法启用快捷键，已返回桌面。游戏会话已保留，可从多任务管理或稍后重试。";
                    DesktopError.IsOpen = true;
                });
            }
        }
        else { nativeGameKeyboard?.Dispose(); nativeGameKeyboard = null; }
        if (panel.Visibility != Visibility.Visible)
        {
            hint.Visibility = Visibility.Collapsed;
            return;
        }
        string key = panel == AppPanel ? "cn.labchronicles.elementpairs" : visibleManagedApp ?? "managed";
        if (!gameShortcutHintsShown.Add(key)) return;
        gameShortcutHintTimer.Stop();
        hint.Opacity = 1;
        hint.Visibility = Visibility.Visible;
        gameShortcutHintTimer.Start();
        DispatcherQueue.TryEnqueue(() =>
        {
            if (hint.Visibility != Visibility.Visible || lifetime.IsCancellationRequested) return;
            var peer = FrameworkElementAutomationPeer.FromElement(text) ?? FrameworkElementAutomationPeer.CreatePeerForElement(text);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });
    }

    private bool OwnsGameKeyboardForeground(nint foreground)
    {
        if (lifetime.IsCancellationRequested || foreground == 0) return false;
        WebAppHost? host = AppPanel.Visibility == Visibility.Visible ? appHost :
            ManagedAppPanel.Visibility == Visibility.Visible && visibleManagedApp is { } id && managedApplications.TryGetValue(id, out var managed) ? managed.Host : null;
        if (host is null) return false;
        if (foreground == gameShortcutWindow) return true;
        return host.OwnsKeyboardProcess(NativeGameKeyboardBridge.ProcessForWindow(foreground));
    }

    private void DismissGameShortcutHints(bool animate)
    {
        gameShortcutHintTimer.Stop();
        foreach (var hint in gameShortcutHints)
        {
            if (!animate || !systemUiSettings.AnimationsEnabled || hint.Visibility != Visibility.Visible)
            { hint.Visibility = Visibility.Collapsed; continue; }
            var fade = new DoubleAnimation { To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(280)) };
            Storyboard.SetTarget(fade, hint);
            Storyboard.SetTargetProperty(fade, "Opacity");
            var storyboard = new Storyboard();
            storyboard.Children.Add(fade);
            storyboard.Completed += (_, _) => { hint.Visibility = Visibility.Collapsed; storyboard.Stop(); hint.Opacity = 1; };
            storyboard.Begin();
        }
    }
}
