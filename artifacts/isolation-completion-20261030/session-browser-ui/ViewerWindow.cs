using AutumnOS.OwnedBrowserParent;
using AutumnOS.SessionBrowserProtocol;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;
using Windows.System;

namespace AutumnOS.SessionBrowserUI;

internal sealed class ViewerWindow : Window
{
    private readonly SessionClient client;
    private readonly Image image = new() { Width = 800, Height = 600, Stretch = Stretch.Fill };
    private readonly Button surface = new() { Width = 800, Height = 600, Padding = new Thickness(0), HorizontalAlignment = HorizontalAlignment.Left, IsTabStop = true };
    private readonly TextBox composition = new() { Width = 800, MaxLength = 1024, PlaceholderText = "第2步：画面键盘正控完成后，在此用输入法输入“你好”（仅当前组合缓冲）", IsSpellCheckEnabled = false, IsTextPredictionEnabled = false, IsEnabled = false };
    private readonly TextBlock status = new() { Text = "正在连接隔离会话…", TextWrapping = TextWrapping.Wrap, MaxWidth = 800 };
    private bool started, composing, suppressText, allowClose;
    private int compositionStart = -1, compositionLength;
    private string? pressedButton;
    private readonly HashSet<VirtualKey> blockedClipboardKeys = [];
    private readonly NativeKeyDispatchGate nativeKeys = new();
    private readonly NativeCompositionTransaction compositionTransaction = new();
    private TaskCompletionSource<bool>? pendingEnd;
    private long pendingEndToken;
    private string pendingEndNonce = "";
    private bool settlementScheduled, closeInitiated;
    private long paintedFrame;
    private SessionHeader? latestCompositionFrame;

    internal ViewerWindow(SessionInputs input, string manifest, UiStartupCheck? startupCheck = null, NativeEndTrustControl? nativeControl = null)
    {
        client = new SessionClient(input, manifest, nativeControl);
        Title = "AutumnOS · 原生隔离画面原型 · 派蒙";
        surface.Content = image;
        var close = new Button { Content = "关闭并核对资源退出", HorizontalAlignment = HorizontalAlignment.Left };
        var panel = new StackPanel { Padding = new Thickness(20), Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = "自有测试页 · 原生输入与内部画面", FontSize = 24 });
        panel.Children.Add(status); panel.Children.Add(surface); panel.Children.Add(composition); panel.Children.Add(close);
        var root = new ScrollViewer { Content = panel, AllowDrop = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        Content = root;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(880, 900));
        root.Loaded += async (_, _) =>
        {
            if (started) return; started = true;
            if (startupCheck is not null)
            {
                try { await startupCheck.VerifyLoadedAsync(this); }
                catch (Exception error) { startupCheck.Fail("loaded_rendering_or_own_HWND", error); }
                allowClose = true; Close(); return;
            }
            try { await client.RunAsync(DisplayFrame, message => { status.Text = message; RefreshCompositionAvailability(); }); }
            catch { status.Text = "原型发生未处理错误，资源退出尚未证实。"; Program.ExitCode = 1; }
            if (client.Finished) { allowClose = true; Close(); }
        };
        close.Click += (_, _) => BeginClose();
        AppWindow.Closing += (_, args) =>
        {
            if (!allowClose && startupCheck is not null)
            {
                startupCheck.Fail("startup_window_closed_before_verification", new InvalidOperationException("STARTUP_CHECK_INCOMPLETE"));
                allowClose = true; return;
            }
            if (!allowClose) { args.Cancel = true; BeginClose(); }
        };
        Closed += (_, _) => { startupCheck?.WindowClosed(); Application.Current.Exit(); };

        surface.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler(OnPointerDown), true);
        surface.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler(OnPointerUp), true);
        surface.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnPointerMove), true);
        surface.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnWheel), true);
        surface.PointerCanceled += OnPointerCanceled;
        surface.PointerCaptureLost += OnPointerCanceled;
        root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler((_, args) => PreviewNativeKey(args, true)), true);
        root.AddHandler(UIElement.PreviewKeyUpEvent, new KeyEventHandler((_, args) => PreviewNativeKey(args, false)), true);
        // These are cancellation events, not clipboard data requests. No data
        // object, format, text or sequence is queried from WinSta0.
        composition.Paste += (_, args) => { args.Handled = true; client.NativeEvent("clipboard_blocked"); };
        composition.CopyingToClipboard += (_, args) => { args.Handled = true; client.NativeEvent("clipboard_blocked"); };
        composition.CuttingToClipboard += (_, args) => { args.Handled = true; client.NativeEvent("clipboard_blocked"); };
        composition.ContextFlyout = new MenuFlyout();
        root.ContextRequested += (_, args) => { args.Handled = true; client.NativeEvent("clipboard_blocked"); };
        root.DragOver += (_, args) => { args.AcceptedOperation = DataPackageOperation.None; args.Handled = true; client.NativeEvent("drag_blocked"); };
        root.Drop += (_, args) => { args.Handled = true; client.NativeEvent("drag_blocked"); };
        composition.AllowDrop = false; composition.CanDrag = false; surface.AllowDrop = false; surface.CanDrag = false;

        composition.TextCompositionStarted += (sender, args) =>
        {
            try
            {
                if (!ReferenceEquals(sender, composition)) throw new InvalidDataException("NATIVE_COMPOSITION_SENDER_MISMATCH");
                if (closeInitiated || client.CloseRequested) throw new InvalidDataException("NATIVE_COMPOSITION_START_AFTER_CLOSE_REQUEST");
                if (!KeyboardPositive()) throw new InvalidDataException("NATIVE_COMPOSITION_KEYBOARD_CONTROL_REQUIRED");
                if (!CompositionEffectGate.FrameAllowsNewComposition(latestCompositionFrame, client.Report.nonce))
                    throw new InvalidDataException("NATIVE_COMPOSITION_LATEST_FRAME_TARGET_FOCUS_REQUIRED");
                compositionTransaction.Begin(composition.Text); composing = true;
                if (Composition("ime_start", args.StartIndex, args.Length)) client.NativeEvent("ime_start");
            }
            catch { CompositionBoundaryFailed("ime_start_boundary_unverified"); }
        };
        composition.TextCompositionChanged += (sender, args) =>
        {
            try { if (!ReferenceEquals(sender, composition)) throw new InvalidDataException("NATIVE_COMPOSITION_SENDER_MISMATCH"); compositionTransaction.RequireActive(); if (Composition("ime_update", args.StartIndex, args.Length)) client.NativeEvent("ime_update"); }
            catch { CompositionBoundaryFailed("ime_update_boundary_unverified"); }
        };
        composition.TextCompositionEnded += (sender, args) =>
        {
            try
            {
                if (!ReferenceEquals(sender, composition)) throw new InvalidDataException("NATIVE_COMPOSITION_SENDER_MISMATCH");
                string nativeBuffer = composition.Text;
                pendingEndToken = compositionTransaction.End(args.StartIndex, args.Length, composition.SelectionStart, composition.SelectionLength, nativeBuffer);
                client.CompositionEndedNotification(args.StartIndex, args.Length, composition.SelectionStart, composition.SelectionLength, nativeBuffer.Length);
                client.NativeEvent("ime_commit"); client.CompositionPendingEnd(true);
                composing = false; compositionStart = -1; compositionLength = 0;
                pendingEndNonce = client.Report.nonce; pendingEnd = new(TaskCreationOptions.RunContinuationsAsynchronously);
                SchedulePendingEnd(); _ = FailUnsettledEnd(pendingEndToken, pendingEnd);
            }
            catch { CompositionBoundaryFailed("ime_end_boundary_unverified"); }
        };
        composition.TextChanging += (sender, args) =>
        {
            if (!compositionTransaction.HasPendingEnd || suppressText) return;
            try
            {
                if (!ReferenceEquals(sender, composition)) throw new InvalidDataException("NATIVE_COMPOSITION_SENDER_MISMATCH");
                // WinUI TextChanging is synchronous after the public Text value
                // has changed. End itself does not provide that final value.
                compositionTransaction.ObservePostEndNativeChange(composition.Text); SchedulePendingEnd();
            }
            catch { CompositionBoundaryFailed("ime_final_native_text_change_unverified"); }
        };
        composition.SelectionChanged += (_, _) =>
        {
            if (!composing || suppressText) return;
            // Selection may change before TextCompositionChanged updates its
            // native range. Observe the settled native event range/caret.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            { if (composing && compositionStart >= 0) Composition("ime_update", compositionStart, compositionLength); });
        };
        composition.TextChanged += (_, _) =>
        {
            if (suppressText) return;
            try
            {
                if (!compositionTransaction.ShouldInsertPlainText(composition.Text) || closeInitiated) return;
                Queue("insert_text", composition.Text); ClearCurrentBuffer();
            }
            catch { CompositionBoundaryFailed("native_text_change_boundary_unverified"); }
        };
    }
    private async void BeginClose()
    {
        if (closeInitiated) return;
        closeInitiated = true; surface.IsEnabled = false; composition.IsHitTestVisible = false;
        status.Text = "正在结算原生组合并请求正常关闭；等待原句柄和 Job 归零。";
        if (pendingEnd is not null && !pendingEnd.Task.IsCompleted) await pendingEnd.Task;
        if (compositionTransaction.IsActive)
        {
            // Closing an active transaction without an End is an explicit
            // failed/cancelled input boundary, never successful composition.
            client.CompositionRange("ime_active_close_without_end", compositionStart, compositionLength, composition.SelectionStart, composition.SelectionLength, false);
            TryQueue(new SessionHeader(1, client.Report.nonce, 1, "input") { command = "ime_cancel" });
            compositionTransaction.InvalidateBoundary();
        }
        composition.IsEnabled = false; client.RequestClose();
    }
    private void SchedulePendingEnd()
    {
        if (settlementScheduled || pendingEnd is null || !compositionTransaction.IsCurrentPending(pendingEndToken)) return;
        settlementScheduled = true; long token = pendingEndToken; var completion = pendingEnd;
        if (!DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            settlementScheduled = false;
            if (!compositionTransaction.IsCurrentPending(token)) { completion.TrySetResult(false); return; }
            if (!compositionTransaction.CanSettle(token)) return; // Await real post-End TextChanging, not time alone.
            try
            {
                if (!client.Ready || client.CloseRequested || client.Report.nonce != pendingEndNonce) throw new InvalidDataException("NATIVE_COMPOSITION_SESSION_CHANGED");
                NativeCompositionDispatch dispatch = compositionTransaction.Settle(token, composition.Text);
                if (!TryQueue(new SessionHeader(1, client.Report.nonce, 1, "input") { command = dispatch.command, text = dispatch.text }))
                    throw new InvalidDataException("NATIVE_COMPOSITION_DISPATCH_NOT_QUEUED");
                compositionTransaction.ConfirmDispatchQueued(dispatch); client.CompositionSettled(dispatch.fact);
                ClearCurrentBuffer(); compositionTransaction.ConfirmOwnBufferCleared(composition.Text);
                client.CompositionPendingEnd(false); RefreshCompositionAvailability(); completion.TrySetResult(true);
            }
            catch { CompositionBoundaryFailed("ime_settlement_boundary_unverified"); completion.TrySetResult(false); }
        })) { settlementScheduled = false; CompositionBoundaryFailed("ime_settlement_dispatcher_unavailable"); }
    }
    private async Task FailUnsettledEnd(long token, TaskCompletionSource<bool> completion)
    {
        if (await Task.WhenAny(completion.Task, Task.Delay(5000)) == completion.Task) return;
        if (compositionTransaction.IsCurrentPending(token)) CompositionBoundaryFailed("ime_final_native_change_not_observed");
    }
    private void CompositionBoundaryFailed(string phase)
    {
        client.CompositionRange(phase, compositionStart, compositionLength, composition.SelectionStart, composition.SelectionLength, false);
        // Do not inspect or clear a buffer belonging to a new/unknown native
        // transaction. Cancel only the private page's outstanding composition.
        TryQueue(new SessionHeader(1, client.Report.nonce, 1, "input") { command = "ime_cancel" });
        compositionTransaction.InvalidateBoundary(); composing = false; pendingEnd?.TrySetResult(false);
        status.Text = "原生组合事务边界尚未得到验证，已停止本次输入验证。"; BeginClose();
    }
    private async Task DisplayFrame(SessionPacket packet)
    {
        if (packet.header.nonce != client.Report.nonce || packet.header.epoch != 1 || packet.header.frame_id <= paintedFrame)
            throw new InvalidDataException("UI_STALE_FRAME_NOT_DISPLAYED");
        // Display is awaited by the one-RPC pump; a later frame cannot decode
        // concurrently and replace the image while this frame is still pending.
        SessionProtocol.Validate(packet);
        using var random = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(random.GetOutputStreamAt(0))) { writer.WriteBytes(packet.data); await writer.StoreAsync(); await writer.FlushAsync(); }
        random.Seek(0); var bitmap = new BitmapImage { DecodePixelWidth = 800, DecodePixelHeight = 600 };
        await bitmap.SetSourceAsync(random);
        if (packet.header.nonce != client.Report.nonce || packet.header.epoch != 1 || packet.header.frame_id <= paintedFrame)
            throw new InvalidDataException("UI_STALE_DECODE_NOT_DISPLAYED");
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object> rendering = null!;
        rendering = (_, _) => { CompositionTarget.Rendering -= rendering; rendered.TrySetResult(); };
        try
        {
            image.Source = bitmap; CompositionTarget.Rendering += rendering;
            if (await Task.WhenAny(rendered.Task, Task.Delay(5000)) != rendered.Task) throw new TimeoutException("UI_RENDERING_CALLBACK_NOT_OBSERVED");
            await rendered.Task;
        }
        finally { CompositionTarget.Rendering -= rendering; }
        paintedFrame = packet.header.frame_id; latestCompositionFrame = packet.header; RefreshCompositionAvailability();
    }
    private void RefreshCompositionAvailability()
    {
        // Updating a frame must not disable a live native transaction and
        // thereby cause a new End/cancel event during preedit settlement.
        composition.IsEnabled = CompositionEffectGate.NativeControlEnabled(client.Ready, closeInitiated || client.CloseRequested, compositionTransaction.IsActive,
            compositionTransaction.HasPendingEnd, latestCompositionFrame, client.Report.nonce);
        if (!compositionTransaction.IsActive && !compositionTransaction.HasPendingEnd && !KeyboardPositive()) composition.IsEnabled = false;
        if (client.Ready && !closeInitiated && !client.CloseRequested && !compositionTransaction.IsActive && !compositionTransaction.HasPendingEnd)
            status.Text = !CompositionEffectGate.FrameAllowsNewComposition(latestCompositionFrame, client.Report.nonce)
                ? "第1步：点击上方画面白框，再在画面区域按左右方向键并松开。"
                : !KeyboardPositive() ? "第1步：画面目标已聚焦；请在画面区域按左右方向键并松开，等待键盘回执。"
                : "第2步：原生按下、松开与画面键盘回执已收到；请在下方输入框用输入法提交“你好”。";
    }
    private bool KeyboardPositive() => client.Report.native_key_down > 0 && client.Report.native_key_up > 0 && client.Report.forwarded_key_acks >= 2 &&
        latestCompositionFrame?.dom_key_down_count > 0;
    private bool Range(string phase, int start, int length, out string text, out int relativeStart, out int relativeEnd)
    {
        string buffer = composition.Text; int selectionStart = composition.SelectionStart, selectionLength = composition.SelectionLength;
        bool valid = start >= 0 && length >= 0 && start <= buffer.Length && length <= buffer.Length - start && selectionStart >= start &&
            selectionLength >= 0 && selectionStart <= start + length && selectionLength <= start + length - selectionStart;
        client.CompositionRange(phase, start, length, selectionStart, selectionLength, valid);
        if (!valid)
        {
            text = ""; relativeStart = relativeEnd = 0; status.Text = "原生组合范围尚未得到有效证据，停止本次输入验证。"; client.RequestClose(); return false;
        }
        text = buffer.Substring(start, length); relativeStart = selectionStart - start; relativeEnd = relativeStart + selectionLength; return true;
    }
    private bool Composition(string command, int start, int length)
    {
        if (!Range(command, start, length, out string text, out int selectedStart, out int selectedEnd)) return false;
        compositionStart = start; compositionLength = length;
        Queue(new SessionHeader(1, client.Report.nonce, 1, "input") { command = command, text = text, selection_start = selectedStart, selection_end = selectedEnd });
        return true;
    }
    private void ClearCurrentBuffer()
    { suppressText = true; try { composition.Text = ""; } finally { suppressText = false; } }
    private void Queue(string command, string? text = null) => Queue(new SessionHeader(1, client.Report.nonce, 1, "input") { command = command, text = text });
    private void Queue(SessionHeader action)
        => TryQueue(action);
    private bool TryQueue(SessionHeader action)
    {
        if (!client.Ready) return false;
        try { bool accepted = client.Enqueue(action); if (!accepted) status.Text = "输入队列已满，此次输入证据不完整。"; return accepted; }
        catch { status.Text = "输入超出本原型允许范围。"; client.RequestClose(); return false; }
    }
    private static int Modifiers()
    {
        bool Down(VirtualKey key) => ((int)InputKeyboardSource.GetKeyStateForCurrentThread(key) & 1) != 0;
        return (Down(VirtualKey.Menu) ? 1 : 0) | (Down(VirtualKey.Control) ? 2 : 0) |
            (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows) ? 4 : 0) | (Down(VirtualKey.Shift) ? 8 : 0);
    }
    private static bool ClipboardChord(VirtualKey key)
    {
        int modifiers = Modifiers();
        return (modifiers & 2) != 0 && (key is VirtualKey.C or VirtualKey.V or VirtualKey.X or VirtualKey.Insert) ||
            (modifiers & 8) != 0 && (key is VirtualKey.Insert or VirtualKey.Delete) || (modifiers & 4) != 0 && key == VirtualKey.V;
    }
    private void PreviewKey(object sender, KeyRoutedEventArgs args)
    {
        if (!ClipboardChord(args.Key)) return;
        args.Handled = true; blockedClipboardKeys.Add(args.Key); client.NativeEvent("clipboard_blocked");
        // Only this private CDP action is forwarded; the native TextBox's
        // clipboard default never runs and no system text is copied into IPC.
        if (!closeInitiated && !client.CloseRequested && (Modifiers() & 4) == 0 &&
            (args.Key == VirtualKey.V || args.Key == VirtualKey.Insert && (Modifiers() & 8) != 0)) Queue("private_paste");
    }
    private string ActualFocusRole()
    {
        object? focused = surface.XamlRoot is XamlRoot xamlRoot ? FocusManager.GetFocusedElement(xamlRoot) : null;
        return ReferenceEquals(focused, surface) ? "surface" : ReferenceEquals(focused, composition) ? "proxy" : "other";
    }
    private void PreviewNativeKey(KeyRoutedEventArgs args, bool down)
    {
        string role = ActualFocusRole();
        client.NativeKeyPreview(down, role, args.Handled, compositionTransaction.BlocksPlainText);
        bool clipboardBlocked;
        if (down)
        {
            PreviewKey(this, args); clipboardBlocked = blockedClipboardKeys.Contains(args.Key);
        }
        else
        {
            clipboardBlocked = blockedClipboardKeys.Remove(args.Key);
            if (clipboardBlocked) args.Handled = true;
        }
        bool closing = closeInitiated || client.CloseRequested;
        if (closing) args.Handled = true;
        nativeKeys.TryDispatch(down, role, MappedKey(args.Key), client.Ready, closing, compositionTransaction.BlocksPlainText,
            clipboardBlocked, () => ForwardKey(args, down));
        RefreshCompositionAvailability();
    }
    private static string? MappedKey(VirtualKey nativeKey)
    {
        string? key = nativeKey switch
        {
            VirtualKey.Enter => "Enter", VirtualKey.Back => "Backspace", VirtualKey.Tab => "Tab", VirtualKey.Escape => "Escape", VirtualKey.Delete => "Delete",
            VirtualKey.Home => "Home", VirtualKey.End => "End", VirtualKey.PageUp => "PageUp", VirtualKey.PageDown => "PageDown", VirtualKey.Left => "ArrowLeft",
            VirtualKey.Right => "ArrowRight", VirtualKey.Up => "ArrowUp", VirtualKey.Down => "ArrowDown", VirtualKey.Space => "Space",
            VirtualKey.Control => "Control", VirtualKey.Shift => "Shift", VirtualKey.Menu => "Alt", _ => null
        };
        int code = (int)nativeKey;
        if (key is null && (code is >= 65 and <= 90 or >= 48 and <= 57)) key = ((char)code).ToString();
        return key;
    }
    private bool ForwardKey(KeyRoutedEventArgs args, bool down)
    {
        if (!client.Ready) return false;
        string? key = MappedKey(args.Key); if (key is null) return false;
        args.Handled = true; client.NativeEvent(down ? "key_down" : "key_up");
        return TryQueue(new SessionHeader(1, client.Report.nonce, 1, "input") { command = down ? "key_down" : "key_up", key = key, modifiers = Modifiers() });
    }
    private (int x, int y) Point(PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(surface).Position;
        return (Math.Clamp((int)(point.X * 800 / Math.Max(1, surface.ActualWidth)), 0, 799), Math.Clamp((int)(point.Y * 600 / Math.Max(1, surface.ActualHeight)), 0, 599));
    }
    private string MouseButton(PointerRoutedEventArgs args)
    { var p = args.GetCurrentPoint(surface).Properties; return p.IsRightButtonPressed ? "right" : p.IsMiddleButtonPressed ? "middle" : "left"; }
    private void OnPointerDown(object sender, PointerRoutedEventArgs args)
    {
        args.Handled = true; bool focused = surface.Focus(FocusState.Pointer);
        client.SurfaceFocusObserved(focused, ActualFocusRole() == "surface"); client.NativeEvent("pointer_down"); var p = Point(args);
        pressedButton = MouseButton(args); surface.CapturePointer(args.Pointer);
        Queue(new SessionHeader(1, client.Report.nonce, 1, "input") { command = "pointer_down", x = p.x, y = p.y, button = pressedButton, modifiers = Modifiers() });
    }
    private void OnPointerUp(object sender, PointerRoutedEventArgs args)
    {
        args.Handled = true; var p = Point(args); string? button = pressedButton; pressedButton = null;
        if (button is not null) Queue(new SessionHeader(1, client.Report.nonce, 1, "input") { command = "pointer_up", x = p.x, y = p.y, button = button, modifiers = Modifiers() });
        surface.ReleasePointerCapture(args.Pointer);
    }
    private void OnPointerCanceled(object sender, PointerRoutedEventArgs args)
    {
        string? button = pressedButton; pressedButton = null;
        if (button is not null)
        { var p = Point(args); Queue(new SessionHeader(1, client.Report.nonce, 1, "input") { command = "pointer_up", x = p.x, y = p.y, button = button, modifiers = Modifiers() }); }
    }
    private void OnPointerMove(object sender, PointerRoutedEventArgs args)
    { var p = Point(args); Queue(new SessionHeader(1, client.Report.nonce, 1, "input") { command = "pointer_move", x = p.x, y = p.y, modifiers = Modifiers() }); }
    private void OnWheel(object sender, PointerRoutedEventArgs args)
    {
        args.Handled = true; var p = Point(args); var properties = args.GetCurrentPoint(surface).Properties; int delta = Math.Clamp(properties.MouseWheelDelta, -1200, 1200);
        Queue(new SessionHeader(1, client.Report.nonce, 1, "input") { command = "wheel", x = p.x, y = p.y,
            wheel_x = properties.IsHorizontalMouseWheel ? delta : 0, wheel_y = properties.IsHorizontalMouseWheel ? 0 : delta, modifiers = Modifiers() });
    }
}
