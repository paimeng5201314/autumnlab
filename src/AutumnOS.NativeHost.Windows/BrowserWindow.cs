using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using AutumnOS.Runtime;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.NativeHost.Windows;

// All controller, document and input calls stay on the STA running this private
// Form. Only the two inherited pipe streams are touched from worker threads.
internal sealed partial class BrowserWindow : Form
{
    private readonly NativeBrowserStartup startup;
    private readonly string manifestSha, udf;
    private readonly uint stationPid;
    private readonly ulong stationCreation;
    private readonly BrowserNativeScope scope;
    private readonly Stream input, output;
    private readonly CancellationTokenSource revoked = new();
    private readonly Channel<NativeViewPacket> commands = Channel.CreateBounded<NativeViewPacket>(new BoundedChannelOptions(1)
        { SingleReader = true, SingleWriter = true, AllowSynchronousContinuations = false });
    private readonly RuntimeDeliveryBinding binding;



    private readonly Queue<byte[]> pendingSdk = new();
    private readonly List<Stream> responseStreams = [];
    private readonly byte[] frameBuffer = new byte[NativeViewProtocol.MaximumFrameBytes];
    private readonly System.Windows.Forms.Timer parentMonitor = new() { Interval = 250 };
    private readonly TaskCompletionSource<bool> committed = NewSignal(), gateHeld = NewSignal(), loaded = NewSignal();
    private CoreWebView2Environment? environment;
    private CoreWebView2Controller? controller;
    private CoreWebView2? core;
    private SafeProcessHandle? browser;
    private uint browserPid;
    private CoreWebView2BrowserProcessExitKind? browserExitKind;
    private readonly NativeBrowserCloseObservation closeObservation = new();
    private Dictionary<string, byte[]>? package;
    private RuntimeParserGateChallenge? challenge;
    private byte[]? gateScript;
    private CoreWebView2WebResourceRequestedEventArgs? gateRequest;
    private CoreWebView2Deferral? gateDeferral;
    private NativeWebViewDeliveryTarget? target;
    private BrowserDeliveryChannel? deliveryChannel;
    private ulong? navigationId;
    private bool sourceCommitted, documentCommitted, proofReady, released, invalidated, finished, closing;
    private bool buttonDown, compositionActive;
    private NativeViewport viewport = NativeViewport.Default;
    private long sequence;
    private NativeBrowserFailureStage failureStage;
    private string probeSummary = "none";
    private string? revocationReason;
    internal int ExitCode { get; private set; } = 1;

    internal BrowserWindow(NativeBrowserStartup startup, string manifestSha, string udf, uint stationPid,
        ulong stationCreation, BrowserNativeScope scope, Stream input, Stream output)
    {
        this.startup = startup; this.manifestSha = manifestSha; this.udf = udf; this.stationPid = stationPid;
        this.stationCreation = stationCreation; this.scope = scope; this.input = input; this.output = output;
        var b = startup.Binding;
        binding = new(b.AuthorityId, b.Account, b.AccountEpoch, b.ApplicationBinding, b.InstanceId, b.InstanceEpoch,
            b.DocumentId, b.DocumentGeneration, b.ChannelGeneration);

        AutoScaleMode = AutoScaleMode.None; ClientSize = new Size(800, 600);
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.Manual; Location = Point.Empty;
        ShowInTaskbar = false; Text = "AutumnOS isolated application";
        Shown += async (_, _) => await RunAsync();
        FormClosing += (_, e) => { if (!finished) { e.Cancel = true; Revoke(); } };
        parentMonitor.Tick += (_, _) => { try { scope.RequireBinding(); } catch { Revoke("BROWSER_OWNER_REVOKED"); } };
    }

    private static TaskCompletionSource<bool> NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private async Task RunAsync()
    {
        long outstandingRequest = 0;
        bool closeRequested = false;
        bool closeReplyStarted = false;
        // Monitor the actual inherited parent pipe even while WebView is
        // creating the environment, navigating, executing the probe or posting.
        Task pipeReader = Task.Run(ReadCommandsAsync);
        try
        {
            parentMonitor.Start();
            await CreateControllerAsync();
            scope.RequireBinding(); revoked.Token.ThrowIfCancellationRequested();
            await Task.Run(() => NativeHostBootstrapProtocol.WriteAsync(output, startup.Nonce, manifestSha,
                new(NativeBootstrapKind.BrowserControllerReady, stationPid, stationCreation, browserPid)));
            while (!invalidated)
            {
                var packet = await commands.Reader.ReadAsync(revoked.Token);
                if (packet.Sequence != checked(sequence + 1)) throw new InvalidDataException("BROWSER_VIEW_SEQUENCE");
                sequence = packet.Sequence;
                outstandingRequest = sequence;
                failureStage = NativeBrowserFailureStage.Request;
                probeSummary = "none";
                scope.RequireBinding(); revoked.Token.ThrowIfCancellationRequested();
                if (packet.Kind == NativeViewKind.Close) { closeRequested = true; break; }
                var reply = await ProcessAsync(packet);
                revoked.Token.ThrowIfCancellationRequested();
                await SendAsync(reply);
                outstandingRequest = 0;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (outstandingRequest != 0)
                try { await SendAsync(new(outstandingRequest, NativeViewKind.Failed,
                    NativeBrowserFailure.Encode(failureStage, revocationReason is null ? ex : new InvalidDataException(revocationReason, ex), probeSummary))); }
                catch (Exception sendError) when (sendError is not OutOfMemoryException) { }
        }
        finally
        {
            parentMonitor.Stop();
            try
            {
                bool clean = await CloseOriginalAsync();
                if (closeRequested && clean)
                {
                    closeObservation.Step = NativeBrowserCloseStep.Reader;
                    await pipeReader;
                    closeObservation.Step = NativeBrowserCloseStep.Reply;
                    closeReplyStarted = true;
                    await SendAsync(new(outstandingRequest, NativeViewKind.Closed, []));
                    ExitCode = 0;
                }
                else if (closeRequested)
                {
                    closeReplyStarted = true;
                    await SendAsync(new(outstandingRequest, NativeViewKind.Failed, closeObservation.Failure()));
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                ExitCode = 1;
                // Never append a second reply after any possible partial write.
                if (closeRequested && !closeReplyStarted)
                {
                    closeReplyStarted = true;
                    try { await SendAsync(new(outstandingRequest, NativeViewKind.Failed, closeObservation.Failure(error))); }
                    catch (Exception sendError) when (sendError is not OutOfMemoryException) { }
                }
            }
            finished = true; Close();
        }
    }

    private Task SendAsync(NativeViewPacket reply) => Task.Run(async () =>
        await NativeViewProtocol.WriteAsync(output, reply, CancellationToken.None));

    private async Task ReadCommandsAsync()
    {
        Exception? failure = null;
        try
        {
            while (true)
            {
                var packet = await NativeViewProtocol.ReadAsync(input, revoked.Token).ConfigureAwait(false);
                await commands.Writer.WriteAsync(packet, revoked.Token).ConfigureAwait(false);
                if (packet.Kind == NativeViewKind.Close) break;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure = error;
            try { BeginInvoke(() => Revoke("BROWSER_PIPE_REVOKED")); } catch (InvalidOperationException) { }
        }
        finally { commands.Writer.TryComplete(failure); }
    }

    private async Task CreateControllerAsync()
    {
        scope.RequireBinding();
        var options = new CoreWebView2EnvironmentOptions(RuntimeIsolationPolicy.BrowserArguments)
        { ExclusiveUserDataFolderAccess = true, IsCustomCrashReportingEnabled = true };
        environment = await CoreWebView2Environment.CreateAsync(startup.RuntimeDirectory, udf, options);
        if (!RuntimeIsolationPolicy.IsSupportedBrowserVersion(environment.BrowserVersionString) ||
            !Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.UserDataFolder)).Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(udf)), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("BROWSER_FROZEN_ENVIRONMENT_MISMATCH");
        revoked.Token.ThrowIfCancellationRequested();
        environment.BrowserProcessExited += (_, e) =>
        {
            if (e.BrowserProcessId == browserPid)
            {
                browserExitKind = e.BrowserProcessExitKind;
                closeObservation.ExitKind = browserExitKind switch
                {
                    CoreWebView2BrowserProcessExitKind.Normal => NativeBrowserCloseExit.Normal,
                    CoreWebView2BrowserProcessExitKind.Failed => NativeBrowserCloseExit.Failed,
                    _ => NativeBrowserCloseExit.Other
                };
                if (!closing) Revoke();
            }
        };
        var controllerOptions = environment.CreateCoreWebView2ControllerOptions();
        controllerOptions.IsInPrivateModeEnabled = true;
        controller = await environment.CreateCoreWebView2ControllerAsync(Handle, controllerOptions);
        core = controller.CoreWebView2; browserPid = core.BrowserProcessId;
        if (!core.Profile.IsInPrivateModeEnabled || browserPid == 0) throw new InvalidDataException("BROWSER_PROFILE_REQUIRED");
        browser = SessionNative.OpenProcess(0x101000, false, browserPid);
        if (browser.IsInvalid || SessionNative.Exited(browser) || !Path.GetFullPath(SessionNative.Image(browser)).Equals(
            Path.Combine(startup.RuntimeDirectory, "msedgewebview2.exe"), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("BROWSER_ORIGINAL_IMAGE_REQUIRED");
        controller.ShouldDetectMonitorScaleChanges = false; controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
        controller.RasterizationScale = 1; controller.Bounds = new Rectangle(0, 0, 800, 600);
        controller.ZoomFactor = 1; controller.IsVisible = true; controller.AllowExternalDrop = false;
        if (controller.Bounds != new Rectangle(0, 0, 800, 600) || controller.RasterizationScale != 1 || controller.ZoomFactor != 1)
            throw new InvalidDataException("BROWSER_VIEWPORT_MISMATCH");
        ConfigurePolicy(core);
    }

    private async Task<NativeViewPacket> ProcessAsync(NativeViewPacket packet)
    {
        switch (packet.Kind)
        {
            case NativeViewKind.PrepareDocument:
                return new(sequence, NativeViewKind.Proof, await PrepareDocumentAsync(packet.Payload));
            case NativeViewKind.ReleaseDocument:
                failureStage = NativeBrowserFailureStage.Release;
                await ReleaseDocumentAsync();
                return new(sequence, NativeViewKind.DocumentReady, []);
            case NativeViewKind.Poll:
                failureStage = NativeBrowserFailureStage.Poll;
                RequireDocument(true);
                if (pendingSdk.TryDequeue(out var sdk)) return new(sequence, NativeViewKind.SdkRequest, sdk);
                using (var frame = new MemoryStream(frameBuffer, 0, frameBuffer.Length, true, true))
                {
                    frame.SetLength(0);
                    await core!.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, frame);
                    RequireDocument(true);
                    return new(sequence, NativeViewKind.Frame, frame.ToArray());
                }
            case NativeViewKind.Resize:
                RequireDocument(true);
                var next = NativeViewport.Parse(packet.Payload);
                // The original STA releases held input before changing coordinates.
                await ResetInputAsync(new(packet.Sequence, NativeViewKind.ResetInput, []));
                ClientSize = new Size(next.Width, next.Height);
                controller!.Bounds = new Rectangle(0, 0, next.Width, next.Height);
                if (controller.Bounds != new Rectangle(0, 0, next.Width, next.Height))
                    throw new InvalidDataException("BROWSER_VIEWPORT_MISMATCH");
                viewport = next;
                RequireDocument(true);
                return new(sequence, NativeViewKind.Accepted, []);
            case NativeViewKind.Delivery:
                failureStage = NativeBrowserFailureStage.Delivery;
                RequireDocument(false);
                return new(sequence, NativeViewKind.Receipt, await deliveryChannel!.DispatchAsync(packet.Payload, released && loaded.Task.IsCompletedSuccessfully && loaded.Task.Result, revoked.Token));
            case NativeViewKind.ResetInput:
                failureStage = NativeBrowserFailureStage.ResetInput;
                return await ResetInputAsync(packet);
            case NativeViewKind.Key or NativeViewKind.Pointer or NativeViewKind.Composition:
                failureStage = NativeBrowserFailureStage.Input;
                RequireDocument(true);
                bool accepted = await ForwardInputAsync(packet.Kind, packet.Payload);
                RequireDocument(true);
                return new(sequence, accepted ? NativeViewKind.Accepted : NativeViewKind.InputRejected, []);
            default:
                throw new InvalidDataException("BROWSER_VIEW_REQUEST_UNSUPPORTED");
        }
    }

    private bool SameDocument => !invalidated && !closing && proofReady && documentCommitted &&
        challenge is not null && core?.Source == challenge.Isolation.PageUri;
    private void RequireDocument(bool requireLoaded)
    {
        scope.RequireBinding();
        if (!SameDocument || target is null || !target.IsCurrentDocument(binding) ||
            requireLoaded && (!released || !loaded.Task.IsCompletedSuccessfully || !loaded.Task.Result))
            throw new InvalidDataException("BROWSER_DOCUMENT_LOST");
    }

    private void Revoke(string reason = "BROWSER_REVOKED")
    {
        if (invalidated) return;
        revocationReason = reason;
        invalidated = true; target?.InvalidateDocument();
        committed.TrySetResult(false); gateHeld.TrySetResult(false); loaded.TrySetResult(false);
        revoked.Cancel();
        if (!closing)
        {
            // A lost owner also interrupts an outstanding native capture/input,
            // independent of a potentially blocked request/response exchange.
            BeginInvoke(() =>
            {
                if (closing || finished) return;
                try { CompleteGate(false); } catch (Exception error) when (error is not OutOfMemoryException) { }
                try { controller?.Close(); controller = null; } catch (Exception error) when (error is not OutOfMemoryException) { }
            });
        }
    }

    private async Task<bool> CloseOriginalAsync()
    {
        closeObservation.Step = NativeBrowserCloseStep.Revoke;
        closing = true; Revoke(); CompleteGate(false);
        closeObservation.Step = NativeBrowserCloseStep.DeliveryDrain;
        if (deliveryChannel is not null) await deliveryChannel.CloseAsync();
        closeObservation.Step = NativeBrowserCloseStep.Controller;
        controller?.Close();
        closeObservation.Step = NativeBrowserCloseStep.Streams;
        foreach (Stream stream in responseStreams) stream.Dispose();
        responseStreams.Clear();
        // Local close status requires the original native browser handle AND SDK
        // process-set/exit event. The station independently retains its Job, OS
        // child handles and context owner even after receiving a Closed response.
        bool clean = false;
        closeObservation.Step = NativeBrowserCloseStep.Observe;
        if (environment is not null && browser is { IsInvalid: false })
        {
            var clock = Stopwatch.StartNew();
            bool observedExitSet = false;
            while (clock.Elapsed < TimeSpan.FromSeconds(12))
            {
                closeObservation.OriginalBrowserSignaled = SessionNative.Exited(browser);
                // Preserve the original short-circuit: this diagnostic does not
                // add SDK calls before the native browser has actually exited.
                // An unqueried SDK set is encoded as unknown, never as zero.
                if (closeObservation.OriginalBrowserSignaled == true)
                {
                    closeObservation.SdkProcessCount = null; closeObservation.SdkQueryHresult = null;
                    try
                    {
                        closeObservation.SdkProcessCount = environment.GetProcessInfos().Count;
                        closeObservation.SdkQueryHresult = 0;
                    }
                    catch (Exception queryError) when (queryError is not OutOfMemoryException)
                    { closeObservation.SdkQueryHresult = queryError.HResult; throw; }
                }
                if (closeObservation.OriginalBrowserSignaled == true && closeObservation.SdkProcessCount == 0 && browserExitKind.HasValue)
                { clean = closeObservation.Clean; observedExitSet = true; break; }
                await Task.Delay(75);
            }
            closeObservation.TimedOut = !observedExitSet;
        }
        closeObservation.Step = NativeBrowserCloseStep.Release;
        browser?.Dispose(); core = null; controller = null; environment = null;
        closeObservation.Step = NativeBrowserCloseStep.Observe;
        return clean;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { parentMonitor.Dispose(); deliveryChannel?.Dispose(); revoked.Dispose(); }
        base.Dispose(disposing);
    }
}
