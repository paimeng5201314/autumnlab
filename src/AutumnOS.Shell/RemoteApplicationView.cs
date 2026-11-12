using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using AutumnOS.NativeHost.Windows;
using AutumnOS.Packages;
using AutumnOS.Runtime;
using AutumnOS.Storage;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.System;

namespace AutumnOS.Shell;

internal sealed partial class RemoteApplicationView(Grid container, RuntimeSession session, RuntimeSessionServices services,
    Func<string, CancellationToken, Task<bool>> permission, Func<bool> isForeground, Action<string> crashed, Action<string> audit,
    Action<string,string>? sdkTrace, Action<string>? guestTestReadDigest)
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly ContentControl surface = new() { IsTabStop = true, HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch };
    private readonly NativeInputQueue inputs = new();
    private readonly CancellationTokenSource closed = new();
    private readonly HashSet<Task> requests = [];
    private readonly HashSet<VirtualKey> held = [];
    private readonly NativeKeyboardState keyboard = new();
    private RuntimeHostStaging? staging;
    private NativeProcessOwner? owner;
    private RemoteViewDelivery? deliveries;
    private RuntimeAuthorityMutationCoordinator? authority;
    private Task? pump;
    private bool starting, disposed, displayed, pressed;
    private int lastPointerX, lastPointerY;
    private NativeViewport viewport = NativeViewport.Default;
    private NativeViewport? visibleViewport;
    private bool resizing;
    private NativeViewport? RequestedViewport => surface.ActualWidth >= 1 && surface.ActualHeight >= 1 &&
        double.IsFinite(surface.ActualWidth) && double.IsFinite(surface.ActualHeight)
        ? NativeViewport.FromSurface(surface.ActualWidth, surface.ActualHeight) : null;
    private bool ViewportInputReady => displayed && !resizing && visibleViewport == viewport && RequestedViewport == viewport;
    private Task resetInput = Task.CompletedTask;
    private Task? closeTransport;
    private Task? stagingCleanup;
    internal bool ResourcesReleased => disposed && !starting && (owner is null || owner.ResourcesRetired)
        && staging is null && pump is not { IsCompleted: false } && resetInput.IsCompleted && closeTransport is not { IsCompleted: false } && requests.Count == 0 &&
        (deliveries is null || deliveries.Registration.Evidence.WhenIdle.IsCompletedSuccessfully &&
            deliveries.Registration.Evidence.RetirementObserved.IsCompletedSuccessfully && deliveries.Registration.Evidence.RetirementObserved.Result);
    internal NativeProcessOwnerSnapshot? Snapshot => owner?.Snapshot;
    internal bool OwnsKeyboardProcess(uint pid) => !disposed && displayed && isForeground() &&
        pid == (uint)Environment.ProcessId && (surface.FocusState != FocusState.Unfocused || ime.FocusState != FocusState.Unfocused);

    internal async Task StartAsync(InstallationRoot root, InstalledPackage app, string archive, NativePayloadCatalog catalog, CancellationToken cancellationToken)
    {
        starting = true;
        var loading = new TextBlock { Text = "正在打开应用…", FontSize = 16, Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        container.Children.Add(loading);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, closed.Token);
        try
        {
            var route = services.AuthorityMutations is PreviewAuthorityRouter preview ? preview.For(session.AuthorityMutationScope) : services.AuthorityMutations;
            authority = route as RuntimeAuthorityMutationCoordinator ?? throw new PackageException("RUNTIME_AUTHORITY_UNAVAILABLE");
            services.Account.EnsureCurrent();
            var native = new NativeHostBinding(authority.IdentityAuthorityId, services.Account.AccountKey, services.Account.Epoch,
                services.Application.BindingKey, session.Instance.Id.Value, session.Instance.Epoch.Value, Guid.NewGuid(), 1, 1);
            var binding = new RuntimeDeliveryBinding(native.AuthorityId, native.Account, native.AccountEpoch, native.ApplicationBinding,
                native.InstanceId, native.InstanceEpoch, native.DocumentId, native.DocumentGeneration, native.ChannelGeneration);
            RuntimeHostStagingInput package;
            try { package = await Task.Run(() => RuntimeHostStaging.FromInstalledArchive(app.DirectoryPath, archive, app.PackageSha256), stop.Token); }
            catch (FileNotFoundException) { throw new PackageException("PACKAGE_ORIGINAL_ARCHIVE_REQUIRED"); }
            catch (DirectoryNotFoundException) { throw new PackageException("PACKAGE_ORIGINAL_ARCHIVE_REQUIRED"); }
            try
            {
                staging = await RuntimeHostStaging.CreateAsync(root.Directories["Runtime"], native, app.Manifest.Entry,
                    new(Path.Combine(AppContext.BaseDirectory, "NativeHost"), catalog.Host),
                    new(Path.Combine(AppContext.BaseDirectory, "WebView2Runtime"), catalog.Runtime), package, stop.Token);
            }
            catch (RuntimeHostStagingRollbackException error) { staging = error.PendingOwner; throw; }
            stop.Token.ThrowIfCancellationRequested();
            owner = NativeProcessOwner.Create(staging.ManifestPath, native);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(90));
            using var abort = deadline.Token.Register(static o => _ = ((NativeProcessOwner)o!).RequestCloseAsync(), owner);
            await owner.StartAsync(deadline.Token);
            owner.RequireOriginalPeer();
            var bootstrap = RuntimeIsolationPolicy.CreateParserGateChallenge(native.InstanceId, session.Origin, app.Manifest.Entry);
            var result = await owner.ExchangeAsync(NativeViewKind.PrepareDocument, JsonSerializer.SerializeToUtf8Bytes(new {
                origin = session.Origin, nonce = bootstrap.Isolation.Nonce }), deadline.Token);
            if (result.Kind != NativeViewKind.Proof) throw new PackageException("RUNTIME_NATIVE_PREPARE_FAILED");
            using var json = JsonDocument.Parse(result.Payload);
            var element = json.RootElement;
            if (element.ValueKind != JsonValueKind.Object || element.EnumerateObject().Count() != 2 ||
                !element.TryGetProperty("nativeResult", out var network) || !element.TryGetProperty("bindingResult", out var document) ||
                !RuntimeIsolationPolicy.ValidateParserGateBinding(bootstrap, document.GetString()))
                throw new PackageException("RUNTIME_NATIVE_DOCUMENT_INVALID");
            var proof = RuntimeIsolationPolicy.ValidateNativeResult(bootstrap.Isolation, network.GetString());
            if (proof is null) throw new PackageException("RUNTIME_NATIVE_PROOF_INVALID");
            if (!session.ConfirmNetworkIsolation(proof)) throw new PackageException("RUNTIME_NATIVE_SESSION_REJECTED");
            owner.RequireOriginalPeer();
            services.Account.EnsureCurrent();
            var frozen = session.CaptureDeliveryStamp();
            if (!session.WithCurrentView(frozen, () => deliveries = new(owner, binding, authority)))
                throw new PackageException("RUNTIME_AUTHORITY_UNAVAILABLE");
            audit("native_endpoint_registered");
            deadline.Token.ThrowIfCancellationRequested();
            if ((await owner.ExchangeAsync(NativeViewKind.ReleaseDocument, [], deadline.Token)).Kind != NativeViewKind.DocumentReady)
                throw new PackageException("RUNTIME_NATIVE_DOCUMENT_INVALID");
            audit("bootstrap_gate_released");
            surface.Content = image; AttachSurface();
            BindInput();
            while (!displayed)
            {
                await ResizeViewportAsync(deadline.Token);
                await PollAsync(deadline.Token);
                if (!displayed) await Task.Delay(16, deadline.Token);
            }
            deadline.Token.ThrowIfCancellationRequested(); services.Account.EnsureCurrent();
            audit("native_first_frame");
            pump = PumpAsync();
        }
        finally { container.Children.Remove(loading); starting = false; }
    }

    private async Task PollAsync(CancellationToken stop)
    {
        var frameAuthority = session.CaptureDeliveryStamp();
        var packet = await owner!.ExchangeAsync(NativeViewKind.Poll, [], stop);
        stop.ThrowIfCancellationRequested();
        switch (packet.Kind)
        {
            case NativeViewKind.Frame:
                byte[] png = packet.Payload;
                if (png.Length < 33 || !png.AsSpan(0,8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) ||
                    BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(8)) != 13 || !png.AsSpan(12,4).SequenceEqual("IHDR"u8) ||
                    BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16)) != viewport.Width || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20)) != viewport.Height)
                    throw new InvalidDataException("NATIVE_VIEW_FRAME_INVALID");
                using (var stream = new InMemoryRandomAccessStream())
                {
                    using var writer = new DataWriter(stream);
                    writer.WriteBytes(png); await writer.StoreAsync(); writer.DetachStream(); stream.Seek(0);
                    var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream);
                    if (bitmap.PixelWidth != viewport.Width || bitmap.PixelHeight != viewport.Height) throw new InvalidDataException("NATIVE_VIEW_FRAME_INVALID");
                    stop.ThrowIfCancellationRequested();
                    session.WithCurrentView(frameAuthority, () => { image.Source = bitmap; visibleViewport = viewport; displayed = true; });
                }
                break;
            case NativeViewKind.SdkRequest:
                if (requests.Count >= 8) throw new InvalidDataException("NATIVE_SDK_CAPACITY");
                Track(HandleRequestAsync(Utf8.GetString(packet.Payload))); break;
            case NativeViewKind.Idle: break;
            default: throw new InvalidDataException("NATIVE_VIEW_POLL_INVALID");
        }
    }
    private async Task PumpAsync()
    {
        try
        {
            while (!closed.IsCancellationRequested)
            {
                for (int i = 0; i < 8 && inputs.TryDequeue(out var input); i++)
                {
                    await resetInput;
                    if (!isForeground() || !inputs.IsCurrent(input)) continue;
                    NativeViewPacket receipt;
                    try { receipt = await owner!.ExchangeAsync(input.Kind, input.CopyPayload(), input.BeforeWriteCancellation); }
                    catch (OperationCanceledException) when (!closed.IsCancellationRequested && !inputs.IsClosed &&
                        input.BeforeWriteCancellation.IsCancellationRequested && !inputs.IsCurrent(input)) { continue; }
                    if (receipt.Kind == NativeViewKind.InputRejected && input.Kind == NativeViewKind.Composition) continue;
                    if (receipt.Kind != NativeViewKind.Accepted)
                        throw new InvalidDataException("NATIVE_VIEW_INPUT_REJECTED");
                    if (input.Gesture && !disposed && isForeground() && inputs.IsCurrent(input)) session.HostUserGesture();
                }
                await ResizeViewportAsync(closed.Token);
                await PollAsync(closed.Token);
                await Task.Delay(isForeground() ? 16 : 150, closed.Token);
            }
        }
        catch (OperationCanceledException) when (closed.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!disposed) crashed("native_view_disconnected"); }
    }
    private async Task ResizeViewportAsync(CancellationToken stop)
    {
        // One coalesced size per pump; no concurrent resize queue. IME finishes on
        // its existing target before we release input and change coordinates.
        if (RequestedViewport is not { } next || next == viewport || composition.BlocksPlainText || inputs.HasPending) return;
        resizing = true;
        try
        {
            await LoseForegroundAsync();
            await resetInput;
            stop.ThrowIfCancellationRequested();
            if (composition.BlocksPlainText || inputs.HasPending) return;
            next = RequestedViewport ?? next;
            if ((await owner!.ExchangeAsync(NativeViewKind.Resize, next.Encode(), stop)).Kind != NativeViewKind.Accepted)
                throw new InvalidDataException("NATIVE_VIEW_RESIZE_REJECTED");
            viewport = next;
            // Keep the last image visible, but admit no input until a new frame
            // with exactly the acknowledged dimensions has decoded successfully.
            visibleViewport = null;
        }
        finally { resizing = false; }
    }
    private async Task HandleRequestAsync(string json)
    {
        string method = "";
        try
        {
            using var request = JsonDocument.Parse(json);
            if (request.RootElement.ValueKind == JsonValueKind.Object && request.RootElement.TryGetProperty("method", out var field)
                && field.ValueKind == JsonValueKind.String) method = field.GetString() ?? "";
        }
        catch (JsonException) { }
        var prepared = await session.HandleMessageForDeliveryAsync(session.PageUri, json, permission, closed.Token);
        if (disposed || deliveries is null) return;
        var result = await session.SendRemoteResponseAsync(authority!.Deliveries, deliveries.Registration.Endpoint, method, prepared);
        if (result.State == RuntimeDeliveryState.Posted && method is "saves.write" or "saves.read" or "permissions.request")
        {
            using var response = JsonDocument.Parse(result.Response);
            bool ok = response.RootElement.GetProperty("ok").GetBoolean();
            audit(method.Replace('.', '_') + (ok ? "_ok" : "_rejected"));
            if (ok && method == "saves.read" && guestTestReadDigest is not null &&
                response.RootElement.TryGetProperty("result", out var read) &&
                read.TryGetProperty("exists", out var exists) && exists.ValueKind == JsonValueKind.True &&
                read.TryGetProperty("value", out var value))
            {
                byte[] jsonBytes = JsonSerializer.SerializeToUtf8Bytes(value);
                try { guestTestReadDigest(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(jsonBytes))); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(jsonBytes); }
            }
            sdkTrace?.Invoke(method, ok ? "OK" : response.RootElement.GetProperty("error").GetProperty("code").GetString() ?? "UNKNOWN");
            audit(method.Replace('.', '_') + "_native_receipt");
        }
    }
    internal void SendEvent(string name, RuntimeResponseStamp stamp, string json)
    {
        if (!displayed || disposed || deliveries is null || requests.Count >= 16) return;
        Track(session.SendRemoteEventAsync(authority!.Deliveries, deliveries.Registration.Endpoint, name, stamp, json));
    }
    private async void Track(Task task)
    {
        requests.Add(task);
        try { await task; }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!disposed) crashed("native_sdk_failed"); }
        finally { requests.Remove(task); }
    }
    private bool Input(NativeViewKind kind, object dto, bool gesture = false)
    {
        if (!disposed && inputs.TryEnqueue(kind, JsonSerializer.SerializeToUtf8Bytes(dto), gesture)) return true;
        if (!disposed)
        {
            audit($"native_input_rejected_{kind}_{inputs.Snapshot.PendingCount}");
            crashed("native_input_capacity");
        }
        return false;
    }
    private void BindInput()
    {
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(surface, "AutumnNativeSurface");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(surface, "应用画面与键盘输入");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(ime, "AutumnNativeComposition");
        surface.PointerPressed += (_, e) =>
        {
            if (disposed || !displayed || !isForeground() || pressed || !e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed) return;
            if (!Map(e, out var x, out var y)) return;
            if (!surface.Focus(FocusState.Pointer) || !SurfaceHasFocus())
            { crashed("native_input_focus_unavailable"); return; }
            surface.CapturePointer(e.Pointer); pressed = true;
            lastPointerX = x; lastPointerY = y;
            Input(NativeViewKind.Pointer, new { action = "down", x, y }, gesture:true); e.Handled = true;
        };
        surface.PointerReleased += (_, e) =>
        {
            if (!pressed) return; pressed = false;
            if (!Map(e, out var x, out var y, clamp:true))
            {
                surface.ReleasePointerCaptures(); _ = LoseForegroundAsync(); return;
            }
            Input(NativeViewKind.Pointer, new { action = "up", x, y }); surface.ReleasePointerCapture(e.Pointer); e.Handled = true;
        };
        surface.PointerCaptureLost += (_, _) => { if (pressed && !disposed) crashed("native_pointer_binding_lost"); };
        surface.PointerMoved += (_, e) =>
        {
            if (!pressed || !isForeground() || !e.GetCurrentPoint(surface).Properties.IsLeftButtonPressed ||
                !Map(e, out var x, out var y) || (x == lastPointerX && y == lastPointerY)) return;
            lastPointerX = x; lastPointerY = y;
            if (!disposed && !inputs.TryEnqueuePointerMove(x, y, viewport))
            {
                audit($"native_move_rejected_{inputs.Snapshot.PendingCount}");
                crashed("native_input_capacity");
            }
            e.Handled = true;
        };
        surface.PointerWheelChanged += (_, e) =>
        {
            if (disposed || !displayed || !isForeground() || HasSystemModifier() || !Map(e, out int x, out int y)) return;
            var properties = e.GetCurrentPoint(surface).Properties;
            var delta = NativePointerInput.NormalizeWheel(properties.MouseWheelDelta, properties.IsHorizontalMouseWheel);
            if (delta == (0, 0)) return;
            Input(NativeViewKind.Pointer, new { action = "wheel", x, y, deltaX = delta.DeltaX, deltaY = delta.DeltaY }, gesture: true);
            e.Handled = true;
        };
        surface.KeyDown += (_, e) => Key(e, true); surface.KeyUp += (_, e) => Key(e, false);
        surface.CharacterReceived += (_, e) =>
        {
            if (disposed || !ViewportInputReady || !isForeground() || !SurfaceHasFocus() || HasSystemModifier() || composition.BlocksPlainText) return;
            if (!keyboard.TryCharacter(e.Character, out string key)) return;
            Input(NativeViewKind.Key, new { action = "char", key, text = e.Character.ToString() }); e.Handled = true;
        };
        surface.LosingFocus += (sender, e) =>
        {
            // Revoke synchronously before focus can enter another control.
            // A delayed LostFocus must not discard a new IME transaction or
            // skip a real leave/return boundary after this surface refocuses.
            if (!disposed && !ReferenceEquals(e.NewFocusedElement, surface)) _ = LoseForegroundAsync();
        };
    }
    private bool SurfaceHasFocus() => surface.XamlRoot is not null &&
        ReferenceEquals(FocusManager.GetFocusedElement(surface.XamlRoot), surface);
    private static bool HasSystemModifier() => new[] { VirtualKey.Control, VirtualKey.Menu, VirtualKey.LeftWindows, VirtualKey.RightWindows }
        .Any(key => (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0);
    private static string? KeyName(VirtualKey key) => key switch {
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => "Digit" + ((int)key - 48).ToString(System.Globalization.CultureInfo.InvariantCulture),
        >= VirtualKey.A and <= VirtualKey.Z => "Key" + (char)(int)key,
        VirtualKey.Left => "ArrowLeft", VirtualKey.Right => "ArrowRight", VirtualKey.Up => "ArrowUp", VirtualKey.Down => "ArrowDown",
        VirtualKey.Enter => "Enter", VirtualKey.Space => "Space", VirtualKey.Tab => "Tab", VirtualKey.Back => "Backspace",
        VirtualKey.Delete => "Delete", VirtualKey.Home => "Home", VirtualKey.End => "End", _ => null };
    private void Key(KeyRoutedEventArgs e, bool down)
    {
        if (disposed || !ViewportInputReady || !isForeground() || !SurfaceHasFocus() || composition.BlocksPlainText || KeyName(e.Key) is not { } key) return;
        if (down)
        {
            if (HasSystemModifier()) return;
            bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            // Shift is admitted only for letter/space CharacterReceived input.
            // Modified navigation and shell shortcuts are not synthesized.
            if (shift && !(e.Key is >= VirtualKey.A and <= VirtualKey.Z or VirtualKey.Space)) return;
            // Focus loss clears held keys; a physical repeat after refocus
            // must not acquire a fresh key or character grant.
            if (e.KeyStatus.WasKeyDown) { e.Handled = true; return; }
            if (!held.Add(e.Key)) { e.Handled = true; return; }
            if (!keyboard.TryDown(key)) { held.Remove(e.Key); return; }
        }
        else
        {
            if (!held.Remove(e.Key)) return;
            if (!keyboard.TryUp(key)) { crashed("native_keyboard_sequence_lost"); return; }
        }
        bool queued = Input(NativeViewKind.Key, new { action = down ? "down" : "up", key }, gesture:down);
        // Handling the printable KeyDown cancels WinUI's subsequent character
        // input. Only an admitted first down may reach CharacterReceived, which
        // independently checks focus, modifiers and the one-use held-key grant.
        bool awaitsCharacter = queued && down &&
            (e.Key is >= VirtualKey.A and <= VirtualKey.Z or >= VirtualKey.Number0 and <= VirtualKey.Number9 or VirtualKey.Space);
        if (!awaitsCharacter) e.Handled = true;
    }
    private bool Map(PointerRoutedEventArgs e, out int x, out int y, bool clamp=false)
    {
        var p = e.GetCurrentPoint(surface).Position;
        x = y = 0;
        return ViewportInputReady && viewport.TryMap(surface.ActualWidth, surface.ActualHeight, p.X, p.Y, out x, out y, clamp);
    }
    internal Task LoseForegroundAsync()
    {
        if (!inputs.Invalidate())
        {
            if (!disposed) crashed("native_input_epoch_exhausted");
            return Task.CompletedTask;
        }
        held.Clear(); keyboard.Reset(); pressed = false; surface.ReleasePointerCaptures();
        if (owner is null || !displayed || disposed) return Task.CompletedTask;
        if (!resetInput.IsCompleted) return resetInput;
        return resetInput = ResetAsync();
    }
    private async Task ResetAsync()
    {
        try { if ((await owner!.ExchangeAsync(NativeViewKind.ResetInput, [], closed.Token)).Kind != NativeViewKind.Accepted)
            throw new InvalidDataException("NATIVE_INPUT_RESET_FAILED"); }
        catch (Exception e) when (e is not OutOfMemoryException) { if (!disposed) crashed("native_input_reset_failed"); }
    }
    internal void Close()
    {
        if (!disposed) { disposed = true; image.Source = null; closed.Cancel(); inputs.Dispose(); }
        if (owner is not null) closeTransport ??= CloseTransportAsync(owner);
        if (container.Children.Contains(viewPanel)) container.Children.Remove(viewPanel);
        if (!starting && (owner is null || owner.ResourcesRetired) && staging is not null)
        {
            stagingCleanup ??= Task.Run(staging.Dispose);
            if (stagingCleanup.IsCompletedSuccessfully) staging = null;
            else if (stagingCleanup.IsFaulted)
            {
                var error = stagingCleanup.Exception!; stagingCleanup = null;
                throw new IOException("NATIVE_STAGE_CLEANUP_INCOMPLETE", error);
            }
        }
    }
    private async Task CloseTransportAsync(NativeProcessOwner original)
    {
        try
        {
            if (displayed && original.Snapshot.State == NativeProcessOwnerState.Authenticated)
            {
                // Normal teardown asks the original STA to close its controller and wait for the
                // browser. A stuck or broken transport is cut only to initiate cleanup. Neither
                // a timer nor even a valid Closed reply releases the original OS-group lease.
                using var abort = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                using var watch = abort.Token.Register(static state => _ = ((NativeProcessOwner)state!).RequestCloseAsync(), original);
                var reply = await original.ExchangeAsync(NativeViewKind.Close, [], abort.Token);
                if (reply.Kind == NativeViewKind.Closed) audit("native_close_reply");
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { audit("native_close_unconfirmed"); }
        finally { await original.RequestCloseAsync(); }
    }
}
