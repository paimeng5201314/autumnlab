using System.Diagnostics;
using AutumnOS.Packages;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.Web.WebView2.Core;

namespace AutumnOS.Shell;

internal sealed record RuntimeViewEnvironmentOptions(string FixedRuntimeDirectory, string UserDataFolder, string BrowserArguments);
// Paths are for the parent's exact binding checks, not for persistence in logs.
internal sealed record RuntimeViewEnvironmentFacts(string BrowserVersion, string UserDataFolder);
internal sealed record RuntimeViewIdentityFacts(uint BrowserProcessId, string? BrowserExecutablePath, bool InPrivate);
internal sealed record RuntimeViewCleanupFacts(bool EnvironmentCreationAttempted, bool ControllerCreationAttempted, bool CreationInProgress,
    bool BrowserExitObserved, uint? ExitedBrowserProcessId, string? BrowserExitKind, uint? HeldBrowserProcessId,
    bool HeldBrowserNativeExitConfirmed, bool BrowserResourcesReleased, int? LastSdkProcessCount,
    bool ViewClosed, bool ViewDetached, int ResponseStreamCount, bool EnvironmentReleased);

/// <summary>
/// Native view mechanics only. RuntimeSession, parser-gate admission, final
/// permission/identity leases, package resources and maintenance leases belong
/// to WebAppHost. Events and mutating methods run on its WinUI dispatcher.
/// </summary>
internal interface IRuntimeViewBackend
{
    FrameworkElement? Element { get; }
    string? Source { get; }
    RuntimeViewEnvironmentFacts? EnvironmentFacts { get; }
    RuntimeViewIdentityFacts? IdentityFacts { get; }
    RuntimeViewCleanupFacts CleanupFacts { get; }
    int ResponseStreamCount { get; }
    bool ResourcesReleased { get; }
    bool WebMessageEnabled { get; set; }
    event EventHandler? UserGesture;
    event Action<string>? PolicyEvent;
    event EventHandler<RuntimeViewNavigationStartingEventArgs>? NavigationStarting;
    event EventHandler<RuntimeViewNavigationStartingEventArgs>? FrameNavigationStarting;
    event EventHandler<RuntimeViewSourceChangedEventArgs>? SourceChanged;
    event EventHandler<RuntimeViewContentLoadingEventArgs>? ContentLoading;
    event EventHandler<RuntimeViewNavigationCompletedEventArgs>? NavigationCompleted;
    event EventHandler<RuntimeViewResourceRequestedEventArgs>? ResourceRequested;
    event EventHandler<RuntimeViewProcessFailedEventArgs>? ProcessFailed;
    event EventHandler<RuntimeViewWebMessageEventArgs>? WebMessageReceived;
    event EventHandler<RuntimeViewBrowserExitedEventArgs>? BrowserProcessExited;
    void Attach();
    // These await the actual original native creation, not a cancellable proxy
    // that could return while creation still owns resources. Parent checks its token.
    Task CreateEnvironmentAsync(RuntimeViewEnvironmentOptions options);
    Task CreateControllerAsync();
    void ConfigureRestrictedPolicy();
    void Navigate(string uri);
    Task<string> ExecuteScriptAsync(string script, CancellationToken cancellationToken);
    /// <summary>
    /// Synchronous actual native post, under the caller's current delivery
    /// leases. Returning after remote enqueue does NOT implement this method.
    /// A future process backend must reject this operation until a reviewed
    /// actual-delivery acknowledgement/lease protocol is implemented.
    /// </summary>
    void PostWebMessageAsJson(string json);
    bool OwnsKeyboardProcess(uint processId);
    void CloseView();
    void Detach();
    void ReleaseResponseStreams();
    // False is pending, not an instruction to kill or release parent leases.
    bool TryRetire(bool startInProgress);
}

/// <summary>The existing in-process WinUI WebView implementation; grants no new execution eligibility.</summary>
internal sealed class WinUiWebViewBackend : IRuntimeViewBackend
{
    private readonly Grid container;
    private readonly List<Stream> responseStreams = [];
    private WebView2? view, pendingDetach;
    private CoreWebView2? core;
    private CoreWebView2Environment? environment;
    private Process? browser;
    private bool environmentCreationAttempted, controllerCreationAttempted, closeRequested, policyConfigured;
    private bool browserExitObserved, heldBrowserNativeExitConfirmed, browserResourcesReleased;
    private bool viewClosed, viewDetached, retired, attachAttempted, attached;
    private uint? exitedBrowserProcessId, heldBrowserProcessId;
    private string? browserExitKind;
    private int creating;
    private int? lastSdkProcessCount;
    public RuntimeViewEnvironmentFacts? EnvironmentFacts { get; private set; }
    public RuntimeViewIdentityFacts? IdentityFacts { get; private set; }
    public FrameworkElement? Element => view ?? pendingDetach;
    public string? Source => core?.Source;
    public int ResponseStreamCount => responseStreams.Count;
    public bool ResourcesReleased => retired && closeRequested && creating == 0 && view is null && pendingDetach is null &&
        browser is null && environment is null && responseStreams.Count == 0;
    public RuntimeViewCleanupFacts CleanupFacts => new(environmentCreationAttempted, controllerCreationAttempted, creating != 0,
        browserExitObserved, exitedBrowserProcessId, browserExitKind, heldBrowserProcessId, heldBrowserNativeExitConfirmed,
        browserResourcesReleased, lastSdkProcessCount, viewClosed, viewDetached, responseStreams.Count, retired && environment is null);
    public bool WebMessageEnabled { get => RequireCore().Settings.IsWebMessageEnabled; set => RequireCore().Settings.IsWebMessageEnabled = value; }
    public event EventHandler? UserGesture;
    public event Action<string>? PolicyEvent;
    public event EventHandler<RuntimeViewNavigationStartingEventArgs>? NavigationStarting;
    public event EventHandler<RuntimeViewNavigationStartingEventArgs>? FrameNavigationStarting;
    public event EventHandler<RuntimeViewSourceChangedEventArgs>? SourceChanged;
    public event EventHandler<RuntimeViewContentLoadingEventArgs>? ContentLoading;
    public event EventHandler<RuntimeViewNavigationCompletedEventArgs>? NavigationCompleted;
    public event EventHandler<RuntimeViewResourceRequestedEventArgs>? ResourceRequested;
    public event EventHandler<RuntimeViewProcessFailedEventArgs>? ProcessFailed;
    public event EventHandler<RuntimeViewWebMessageEventArgs>? WebMessageReceived;
    public event EventHandler<RuntimeViewBrowserExitedEventArgs>? BrowserProcessExited;

    internal WinUiWebViewBackend(Grid container)
    { this.container = container; RequireDispatcher(); }

    public void Attach()
    {
        RequireDispatcher();
        if (closeRequested || attachAttempted || retired) throw new InvalidOperationException("RUNTIME_VIEW_ATTACH_UNAVAILABLE");
        attachAttempted = true;
        view = new WebView2();
        view.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => { if (!closeRequested) UserGesture?.Invoke(this, EventArgs.Empty); }), true);
        view.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => { if (!closeRequested) UserGesture?.Invoke(this, EventArgs.Empty); }), true);
        container.Children.Add(view); attached = true;
    }

    public async Task CreateEnvironmentAsync(RuntimeViewEnvironmentOptions options)
    {
        RequireDispatcher(); ArgumentNullException.ThrowIfNull(options);
        if (closeRequested || !attached || view is null || environmentCreationAttempted || creating != 0) throw new InvalidOperationException("RUNTIME_VIEW_ENVIRONMENT_UNAVAILABLE");
        var nativeOptions = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = options.BrowserArguments, ExclusiveUserDataFolderAccess = true, IsCustomCrashReportingEnabled = true
        };
        if (!nativeOptions.IsCustomCrashReportingEnabled) throw new InvalidOperationException("RUNTIME_LOCAL_CRASH_REPORTING_UNAVAILABLE");
        environmentCreationAttempted = true; creating++;
        try
        {
            var created = await CoreWebView2Environment.CreateWithOptionsAsync(options.FixedRuntimeDirectory, options.UserDataFolder, nativeOptions);
            environment = created; created.BrowserProcessExited += OnBrowserProcessExited;
            EnvironmentFacts = new(created.BrowserVersionString, created.UserDataFolder);
            if (closeRequested) throw new ObjectDisposedException(nameof(WinUiWebViewBackend));
        }
        finally { creating--; }
    }

    public async Task CreateControllerAsync()
    {
        RequireDispatcher();
        if (closeRequested || environment is null || view is null || controllerCreationAttempted || creating != 0)
            throw new InvalidOperationException("RUNTIME_VIEW_CONTROLLER_UNAVAILABLE");
        var owned = view; var options = environment.CreateCoreWebView2ControllerOptions(); options.IsInPrivateModeEnabled = true;
        controllerCreationAttempted = true; creating++;
        try
        {
            await owned.EnsureCoreWebView2Async(environment, options);
            if (closeRequested) throw new ObjectDisposedException(nameof(WinUiWebViewBackend));
            core = owned.CoreWebView2;
            var captured = Process.GetProcessById(checked((int)core.BrowserProcessId));
            try { _ = captured.Handle; } catch { captured.Dispose(); throw; }
            browser = captured; heldBrowserProcessId = (uint)captured.Id;
            browserExitObserved = exitedBrowserProcessId == heldBrowserProcessId;
            if (!browserExitObserved) browserExitKind = null;
            IdentityFacts = new(core.BrowserProcessId, captured.MainModule?.FileName, core.Profile.IsInPrivateModeEnabled);
        }
        finally { creating--; }
    }

    public void ConfigureRestrictedPolicy()
    {
        RequireDispatcher(); var native = RequireCore();
        if (policyConfigured) throw new InvalidOperationException("RUNTIME_VIEW_POLICY_ALREADY_CONFIGURED");
        native.Settings.AreHostObjectsAllowed = false; native.Settings.AreDevToolsEnabled = false;
        native.Settings.AreDefaultContextMenusEnabled = false; native.Settings.AreDefaultScriptDialogsEnabled = false;
        native.Settings.AreBrowserAcceleratorKeysEnabled = false; native.Settings.IsGeneralAutofillEnabled = false;
        native.Settings.IsPasswordAutosaveEnabled = false; native.Settings.IsStatusBarEnabled = false;
        native.Settings.IsWebMessageEnabled = false;
        native.PermissionRequested += OnPermissionRequested; native.NewWindowRequested += OnNewWindowRequested;
        native.DownloadStarting += OnDownloadStarting; native.LaunchingExternalUriScheme += OnLaunchingExternalUriScheme;
        native.NavigationStarting += OnNavigationStarting; native.FrameNavigationStarting += OnFrameNavigationStarting;
        native.SourceChanged += OnSourceChanged; native.ContentLoading += OnContentLoading;
        native.NavigationCompleted += OnNavigationCompleted; native.ProcessFailed += OnProcessFailed;
        native.WebMessageReceived += OnWebMessageReceived;
        native.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        native.WebResourceRequested += OnWebResourceRequested;
        policyConfigured = true;
    }

    public void Navigate(string uri)
    {
        RequireDispatcher(); if (!policyConfigured) throw new InvalidOperationException("RUNTIME_VIEW_POLICY_REQUIRED");
        RequireCore().Navigate(uri);
    }
    public Task<string> ExecuteScriptAsync(string script, CancellationToken cancellationToken)
    { RequireDispatcher(); return RequireCore().ExecuteScriptAsync(script).AsTask(cancellationToken); }
    public void PostWebMessageAsJson(string json)
    { RequireDispatcher(); RequireCore().PostWebMessageAsJson(json); }
    public bool OwnsKeyboardProcess(uint processId) => !closeRequested && browser is { } held && held.Id == processId && !held.HasExited;

    public void CloseView()
    {
        RequireDispatcher(); closeRequested = true; WebView2? owned = view; pendingDetach ??= owned;
        if (owned is not null)
        {
            owned.Close();
            if (ReferenceEquals(view, owned)) { view = null; core = null; }
        }
        viewClosed = view is null;
    }
    public void Detach()
    {
        RequireDispatcher(); var owned = pendingDetach ?? view;
        if (owned is not null) container.Children.Remove(owned);
        pendingDetach = null; attached = false; viewDetached = true;
    }
    public void ReleaseResponseStreams()
    {
        RequireDispatcher(); List<Exception>? failures = null;
        foreach (Stream stream in responseStreams)
        {
            try { stream.Dispose(); }
            catch (Exception error) when (error is not OutOfMemoryException) { (failures ??= []).Add(error); }
        }
        if (failures is not null) throw new AggregateException("RUNTIME_RESPONSE_STREAM_RELEASE_FAILED", failures);
        responseStreams.Clear();
    }
    public bool TryRetire(bool startInProgress)
    {
        RequireDispatcher();
        if (startInProgress || creating != 0 || !closeRequested || view is not null || pendingDetach is not null || responseStreams.Count != 0) return false;
        if (!HasReleasedBrowserProcesses()) return false;
        if (browser is { } held) { held.Dispose(); browser = null; }
        if (environment is { } owned) { owned.BrowserProcessExited -= OnBrowserProcessExited; environment = null; }
        retired = true;
        return true;
    }
    private bool HasReleasedBrowserProcesses()
    {
        if (browserResourcesReleased) return true;
        if (!environmentCreationAttempted) return true;
        if (environment is null || creating != 0) return false;
        if (browser is { } held)
        {
            if (!browserExitObserved || exitedBrowserProcessId != (uint)held.Id || !held.HasExited) return false;
            heldBrowserNativeExitConfirmed = true;
        }
        else if (controllerCreationAttempted && !browserExitObserved) return false;
        lastSdkProcessCount = environment.GetProcessInfos().Count;
        browserResourcesReleased = lastSdkProcessCount == 0;
        // Abnormal exit may still have proven empty resources. Normal is an
        // acceptance observation, never an added prerequisite for cleanup.
        return browserResourcesReleased;
    }
    private CoreWebView2 RequireCore()
    {
        if (closeRequested || core is null) throw new ObjectDisposedException(nameof(WinUiWebViewBackend));
        return core;
    }
    private void RequireDispatcher()
    { if (!container.DispatcherQueue.HasThreadAccess) throw new InvalidOperationException("RUNTIME_VIEW_DISPATCHER_REQUIRED"); }

    private void OnBrowserProcessExited(CoreWebView2Environment sender, CoreWebView2BrowserProcessExitedEventArgs args)
    {
        if (environment is null || browser is { } held && args.BrowserProcessId != (uint)held.Id) return;
        exitedBrowserProcessId = args.BrowserProcessId; browserExitObserved = true; browserExitKind = args.BrowserProcessExitKind.ToString();
        BrowserProcessExited?.Invoke(this, new(args.BrowserProcessId, browserExitKind));
    }
    private void OnPermissionRequested(CoreWebView2 sender, CoreWebView2PermissionRequestedEventArgs args)
    { args.State = CoreWebView2PermissionState.Deny; args.Handled = true; PolicyEvent?.Invoke("browser_permission_denied"); }
    private void OnNewWindowRequested(CoreWebView2 sender, CoreWebView2NewWindowRequestedEventArgs args)
    { args.Handled = true; PolicyEvent?.Invoke("new_window_denied"); }
    private void OnDownloadStarting(CoreWebView2 sender, CoreWebView2DownloadStartingEventArgs args)
    { args.Cancel = true; PolicyEvent?.Invoke("download_denied"); }
    private void OnLaunchingExternalUriScheme(CoreWebView2 sender, CoreWebView2LaunchingExternalUriSchemeEventArgs args)
    { args.Cancel = true; PolicyEvent?.Invoke("external_protocol_denied"); }
    private void OnNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        => NavigationStarting?.Invoke(this, new(() => args.Uri, () => args.NavigationId, () => args.Cancel, value => args.Cancel = value));
    private void OnFrameNavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs args)
        => FrameNavigationStarting?.Invoke(this, new(() => args.Uri, () => args.NavigationId, () => args.Cancel, value => args.Cancel = value));
    private void OnSourceChanged(CoreWebView2 sender, CoreWebView2SourceChangedEventArgs args)
        => SourceChanged?.Invoke(this, new(() => sender.Source, () => args.IsNewDocument));
    private void OnContentLoading(CoreWebView2 sender, CoreWebView2ContentLoadingEventArgs args)
        => ContentLoading?.Invoke(this, new(() => args.NavigationId, () => args.IsErrorPage));
    private void OnNavigationCompleted(CoreWebView2 sender, CoreWebView2NavigationCompletedEventArgs args)
        => NavigationCompleted?.Invoke(this, new(() => args.NavigationId, () => args.IsSuccess));
    private void OnProcessFailed(CoreWebView2 sender, CoreWebView2ProcessFailedEventArgs args)
        => ProcessFailed?.Invoke(this, new(args.ProcessFailedKind.ToString(), args.Reason.ToString(), args.ExitCode));
    private void OnWebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
        => WebMessageReceived?.Invoke(this, new(() => args.Source, () => args.WebMessageAsJson));
    private void OnWebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (environment is null || retired) return; // A late callback cannot recreate retired response streams.
        var request = new NativeResourceRequest(this, args);
        if (!closeRequested && ResourceRequested is { } handler) handler(this, request);
        else request.Respond(null, 403, "Forbidden", "Content-Type: text/plain; charset=utf-8\r\n" + AutumnOS.Runtime.RuntimeIsolationPolicy.ResponseHeaders);
    }
    private sealed class NativeResourceRequest(WinUiWebViewBackend owner, CoreWebView2WebResourceRequestedEventArgs native) : RuntimeViewResourceRequestedEventArgs
    {
        public override string Uri => native.Request.Uri;
        public override string Method => native.Request.Method;
        public override RuntimeViewResourceContext ResourceContext => native.ResourceContext switch
        { CoreWebView2WebResourceContext.Document => RuntimeViewResourceContext.Document, CoreWebView2WebResourceContext.Script => RuntimeViewResourceContext.Script, _ => RuntimeViewResourceContext.Other };
        public override IRuntimeViewDeferral GetDeferral() => new NativeDeferral(native.GetDeferral());
        public override void Respond(byte[]? body, int status, string reason, string headers)
        {
            owner.RequireDispatcher();
            var ownedEnvironment = owner.environment ?? throw new InvalidOperationException("RUNTIME_VIEW_ENVIRONMENT_REQUIRED");
            if (owner.retired || body is not null && owner.closeRequested) throw new ObjectDisposedException(nameof(WinUiWebViewBackend));
            if (body is not null && owner.responseStreams.Count >= 512) throw new PackageException("RUNTIME_RESOURCE_LIMIT");
            Stream? stream = body is null ? null : new MemoryStream(body, writable: false);
            if (stream is not null) owner.responseStreams.Add(stream);
            native.Response = ownedEnvironment.CreateWebResourceResponse(stream?.AsRandomAccessStream(), status, reason, headers);
        }
    }
    private sealed class NativeDeferral(Windows.Foundation.Deferral native) : IRuntimeViewDeferral
    { public void Complete() => native.Complete(); }
}
