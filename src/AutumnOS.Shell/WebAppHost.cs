using System.Text.Json;
using System.Text;
using AutumnOS.Contracts;
using AutumnOS.Packages;
using AutumnOS.Runtime;
using AutumnOS.Storage;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace AutumnOS.Shell;

/// <summary>Hosts validated packages after this native WebView has passed its private isolation check.</summary>
internal sealed class WebAppHost : IDisposable
{
    private const string SampleId = "cn.labchronicles.elementpairs";
    private readonly InstallationRoot root;
    private readonly Grid container;
    private readonly Func<string, CancellationToken, Task<bool>> requestPermission;
    private readonly Action<string> status;
    private readonly CancellationTokenSource stopped = new();
    private readonly List<Stream> responseStreams = [];
    private readonly Dictionary<string, byte[]> resources = new(StringComparer.Ordinal);
    private WebView2? view;
    private System.Diagnostics.Process? keyboardBrowserProcess;
    private bool disposed;
    private bool cleanupInProgress;
    private bool cleanupPending;
    private bool startInProgress;
    private bool navigated;
    private bool desiredForeground = true;
    private string startupStage = "package";
    private int? profilePathLength;
    private string? profileNamespace;
    private string? browserVersion;
    private BrowserProcessFailure? browserProcessFailure;
    private sealed record BrowserProcessFailure(string Kind, string Reason, int ExitCode);
    private readonly Func<InstalledPackage, Func<RuntimeSession>, CancellationToken, RuntimeSessionServices>? serviceFactory;
    private readonly Action<Guid>? closeCapabilities;
    private readonly string? previewPackage;
    private readonly string? expectedPreviewSha256;
    private readonly Action<string, string>? sdkTrace;
    private readonly InstalledPackage? registeredPackage;
    private IDisposable? runtimeLease;
    private IDisposable? maintenanceGameLease;
    private readonly Action<WebAppHost>? retainForCleanup;
    public bool ResourcesReleased => disposed && !cleanupInProgress && !cleanupPending && !startInProgress && view is null && keyboardBrowserProcess is null &&
        responseStreams.Count == 0 && resources.Count == 0 && runtimeLease is null && maintenanceGameLease is null;
    public bool IsDeveloperPreview => previewPackage is not null;

    // Capture the actual controller's browser process, with an open handle to prevent
    // PID reuse from admitting an unrelated browser into the native shortcut scope.
    internal bool OwnsKeyboardProcess(uint processId) => !disposed && navigated && desiredForeground &&
        keyboardBrowserProcess is { } process && process.Id == processId && !process.HasExited;

    public WebAppHost(InstallationRoot root, Grid container,
        Func<string, CancellationToken, Task<bool>> requestPermission, Action<string> status,
        Func<InstalledPackage, Func<RuntimeSession>, CancellationToken, RuntimeSessionServices>? serviceFactory = null,
        Action<Guid>? closeCapabilities = null, string? previewPackage = null, Action<string, string>? sdkTrace = null,
        InstalledPackage? registeredPackage = null, IDisposable? runtimeLease = null, CriticalOperationCoordinator? maintenance = null,
        Action<WebAppHost>? retainForCleanup = null, string? expectedPreviewSha256 = null)
    {
        this.root = root;
        this.container = container;
        this.requestPermission = requestPermission;
        this.status = status;
        this.serviceFactory = serviceFactory;
        this.closeCapabilities = closeCapabilities;
        this.previewPackage = previewPackage;
        this.expectedPreviewSha256 = expectedPreviewSha256;
        if (previewPackage is not null && (serviceFactory is null || expectedPreviewSha256 is not { Length: 64 } ||
            expectedPreviewSha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))))
            throw new PackageException("DEVELOPER_PREVIEW_BINDING_REQUIRED");
        this.sdkTrace = sdkTrace;
        this.registeredPackage = registeredPackage;
        this.runtimeLease = runtimeLease;
        this.retainForCleanup = retainForCleanup;
        Session = new RuntimeSession(SampleId, ["saves"], root.Directories["Saves"]);
        maintenanceGameLease = maintenance?.EnterGame(registeredPackage?.Manifest.AppId ?? (previewPackage is null ? SampleId : "developer.preview"), () => Session.Instance);
    }

    public RuntimeSession Session { get; private set; }
    public InstalledPackage? Installed { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (disposed || startInProgress) throw new InvalidOperationException("RUNTIME_START_UNAVAILABLE");
        startInProgress = true;
        try { await StartCoreAsync(cancellationToken); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // The UI may dispose this host immediately after a failed start. Retain the failing native
            // stage and numeric HRESULT first; exception messages can contain paths and user content.
            Audit(error is OperationCanceledException ? "startup_cancelled" : "startup_failed", error);
            throw;
        }
        finally
        {
            startInProgress = false;
            if (disposed) Dispose(); // Cancellation does not mean native creation or package work has returned.
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, stopped.Token);
        status("正在验证应用并准备运行环境…");
        string packagePath = previewPackage ?? Path.Combine(AppContext.BaseDirectory, "Samples", "element-pairs.autumn");
        string installRoot = previewPackage is null ? root.Directories["Apps"] : Path.Combine(root.Directories["Runtime"], "DeveloperPackages");
        ValidateManagedPath(Path.Combine(installRoot, "package-marker"));
        var installed = await Task.Run(() =>
        {
            if (registeredPackage is not null) { ApplicationInstallService.VerifyInstalled(registeredPackage); return registeredPackage; }
            if (previewPackage is not null)
            {
                // A developer commonly rebuilds the same app/version. Each confirmed payload gets its own
                // immutable cache root; it never conflicts with or replaces an earlier preview or installed app.
                return DeveloperToolsService.InstallPreviewPackage(packagePath, installRoot, expectedPreviewSha256!, linked.Token);
            }
            return PackageInstaller.Install(packagePath, installRoot, linked.Token);
        }, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (registeredPackage is null && previewPackage is null && installed.Manifest.AppId != SampleId) throw new PackageException("PACKAGE_APP_ID_INVALID");
        if (registeredPackage is null) installed = installed with { HostSource = previewPackage is null ? "bundled:" + SampleId : "local-preview:" + installed.PackageSha256 };
        Installed = installed;
        startupStage = "required_boundaries";
        // The current native Job restriction still lets legacy browser copy/cut clear
        // the clipboard. No clipboard-confined external-package host is implemented.
        // This program's own installed content must match its fixed distribution asset
        // as well as its host-created source binding. An app ID, bundled-looking manifest,
        // report, developer mode, or GitHub/local package with the same bytes is no grant.
        // Preserve the selected install/version and its runtime lease; never silently
        // reinstall a different asset to make this check pass.
        bool programContent = previewPackage is null && installed.HostSource == "bundled:" + SampleId &&
            await Task.Run(() =>
            {
                string asset = Path.Combine(AppContext.BaseDirectory, "Samples", "element-pairs.autumn");
                using FileStream assetLease = new(asset, FileMode.Open, FileAccess.Read, FileShare.Read);
                var distribution = PackageInstaller.Inspect(asset, linked.Token);
                return installed.PackageSha256 == distribution.Sha256 &&
                    installed.Manifest.AppId == distribution.Manifest.AppId && installed.Manifest.Version == distribution.Manifest.Version &&
                    installed.Manifest.Entry == distribution.Manifest.Entry;
            }, linked.Token);
        linked.Token.ThrowIfCancellationRequested();
        if (!programContent)
            throw new PackageException("RUNTIME_CLIPBOARD_BOUNDARY_UNAVAILABLE");
        Session.Close();
        startupStage = "session";
        var services = serviceFactory?.Invoke(installed, () => Session, stopped.Token);
        if (previewPackage is not null && services is null) throw new PackageException("DEVELOPER_PREVIEW_BINDING_REQUIRED");
        Session = new RuntimeSession(installed.Manifest.AppId, installed.Manifest.Permissions,
            root.Directories["Saves"], installed.Manifest.Entry, services: services);
        Session.SdkEvent += (name, value) =>
        {
            var sourceSession = Session; long revision = sourceSession.EventRevision;
            container.DispatcherQueue.TryEnqueue(() =>
            {
                if (!disposed && !stopped.IsCancellationRequested && sourceSession == Session && navigated && view?.CoreWebView2 is { } core && core.Source == sourceSession.PageUri)
                    sourceSession.DeliverEvent(name, revision, () => core.PostWebMessageAsJson(JsonSerializer.Serialize(new { @event = name, data = value }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));
            });
        };
        startupStage = "resources";
        foreach (var file in Directory.EnumerateFiles(installed.DirectoryPath, "*", SearchOption.AllDirectories))
        {
            ValidateManagedPath(file);
            string relative = Path.GetRelativePath(installed.DirectoryPath, file).Replace('\\', '/');
            if (relative.StartsWith('.')) continue;
            resources.Add(relative, File.ReadAllBytes(file));
        }
        Audit("installed");
        startupStage = "profile";
        // WebView environment overrides can replace our fixed browser, profile or security options.
        // Do not silently admit a differently configured controller into an isolated application.
        foreach (string name in new[] { "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", "WEBVIEW2_BROWSER_EXECUTABLE_FOLDER", "WEBVIEW2_USER_DATA_FOLDER" })
            if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(name)))
                throw new PackageException("RUNTIME_ENVIRONMENT_OVERRIDE_BLOCKED");
        RejectBrowserPolicyOverrides();
        // WebView2 creates additional native subdirectories below userDataFolder. The old account hash /
        // binding hash nesting exhausted MAX_PATH in ordinary deep portable directories on this machine.
        // InPrivate browser cache is not an app save: retain the old cache untouched and start a compact,
        // independently bound cache. Actual account/source data paths and migration rules are unchanged.
        string profileRoot = WebViewProfileDirectory.ForBinding(root.Directories["Runtime"], services?.Account.AccountKey ?? "guest",
            services?.Application.BindingKey ?? RuntimeApplication.Hash("autumnos.legacy-bundled-profile.v1\0" + installed.Manifest.AppId));
        profilePathLength = profileRoot.Length;
        profileNamespace = Path.GetFileName(profileRoot);
        ValidateManagedPath(Path.Combine(profileRoot, "profile-marker"));
        Directory.CreateDirectory(profileRoot);
        ValidateManagedPath(Path.Combine(profileRoot, "profile-marker"));
        Audit("profile_prepared");
        view = new WebView2();
        view.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Session.HostUserGesture()), true);
        view.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => Session.HostUserGesture()), true);
        container.Children.Add(view);
        var environmentOptions = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = RuntimeIsolationPolicy.BrowserArguments,
            ExclusiveUserDataFolderAccess = true
        };
        string fixedRuntime = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
        if (!File.Exists(Path.Combine(fixedRuntime, "msedgewebview2.exe"))) throw new PackageException("RUNTIME_ISOLATION_ENGINE_MISSING");
        startupStage = "environment";
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(fixedRuntime, profileRoot, environmentOptions);
        browserVersion = environment.BrowserVersionString;
        if (!RuntimeIsolationPolicy.IsSupportedBrowserVersion(browserVersion))
            throw new PackageException("RUNTIME_ISOLATION_ENGINE_UNSUPPORTED");
        if (!string.Equals(Path.GetFullPath(environment.UserDataFolder).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(profileRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new PackageException("RUNTIME_ENVIRONMENT_OVERRIDE_BLOCKED");
        Audit("environment_created");
        linked.Token.ThrowIfCancellationRequested();
        startupStage = "controller";
        var options = environment.CreateCoreWebView2ControllerOptions();
        options.IsInPrivateModeEnabled = true;
        await view.EnsureCoreWebView2Async(environment, options);
        Audit("controller_created");
        linked.Token.ThrowIfCancellationRequested();
        startupStage = "settings";
        var core = view.CoreWebView2;
        keyboardBrowserProcess = System.Diagnostics.Process.GetProcessById(checked((int)core.BrowserProcessId));
        _ = keyboardBrowserProcess.Handle;
        if (!core.Profile.IsInPrivateModeEnabled || !string.Equals(keyboardBrowserProcess.MainModule?.FileName,
            Path.Combine(fixedRuntime, "msedgewebview2.exe"), StringComparison.OrdinalIgnoreCase))
            throw new PackageException("RUNTIME_ENVIRONMENT_OVERRIDE_BLOCKED");
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        core.PermissionRequested += (_, args) => { args.State = CoreWebView2PermissionState.Deny; args.Handled = true; Audit("browser_permission_denied"); };
        core.NewWindowRequested += (_, args) => { args.Handled = true; Audit("new_window_denied"); };
        core.DownloadStarting += (_, args) => { args.Cancel = true; Audit("download_denied"); };
        core.LaunchingExternalUriScheme += (_, args) => { args.Cancel = true; Audit("external_protocol_denied"); };
        var navigation = new RuntimeNavigationGuard(Session.PageUri);
        var challenge = RuntimeIsolationPolicy.CreateChallenge(Session.Instance.Id.Value, Session.Origin);
        var challengeNavigation = new RuntimeNavigationGuard(challenge.PageUri);
        var challengeLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool checkingIsolation = true;
        bool applicationDocumentCommitted = false;
        core.NavigationStarting += (_, args) =>
        {
            var guard = checkingIsolation ? challengeNavigation : navigation;
            if (!guard.OnStarting(args.NavigationId, args.Uri)) { args.Cancel = true; Audit("navigation_denied"); }
        };
        core.FrameNavigationStarting += (_, args) => { args.Cancel = true; Audit("frame_denied"); };
        // A canceled request can still commit an engine error document (observed with
        // Connection-Allowlist). Cancellation alone is not proof that the old document
        // survived. Revoke its session before any later response/event can be delivered.
        core.SourceChanged += (_, args) =>
        {
            if (!disposed && applicationDocumentCommitted && (args.IsNewDocument || core.Source != Session.PageUri))
                Crash("document_binding_lost");
        };
        core.ContentLoading += (_, args) =>
        {
            if (disposed) return;
            if (args.IsErrorPage) { Crash("error_document_blocked"); return; }
            if (checkingIsolation) return;
            if (applicationDocumentCommitted || core.Source != Session.PageUri)
            { Crash("document_binding_lost"); return; }
            applicationDocumentCommitted = true;
        };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, args) =>
        {
            byte[]? content = null;
            string mime = "text/plain; charset=utf-8";
            if (!disposed && responseStreams.Count < 512 && args.Request.Method == "GET" && args.Request.Uri.StartsWith(Session.Origin, StringComparison.Ordinal))
            {
                string relative = args.Request.Uri[Session.Origin.Length..];
                if (checkingIsolation)
                {
                    if (relative == challenge.DocumentPath) { content = Encoding.UTF8.GetBytes(challenge.DocumentHtml); mime = "text/html; charset=utf-8"; }
                    else if (relative == challenge.ScriptPath) { content = Encoding.UTF8.GetBytes(challenge.Script); mime = "text/javascript; charset=utf-8"; }
                }
                else if (Session.NetworkSandboxVerified && resources.TryGetValue(relative, out content)) mime = MimeType(relative);
            }
            // Use only verified package resources, never a file:// or generic filesystem/network proxy.
            Stream? stream = content is null ? null : new MemoryStream(content, writable: false);
            if (stream is not null) responseStreams.Add(stream);
            args.Response = environment.CreateWebResourceResponse(stream?.AsRandomAccessStream(), content is null ? 403 : 200,
                content is null ? "Forbidden" : "OK", "Content-Type: " + mime + "\r\n" + RuntimeIsolationPolicy.ResponseHeaders);
        };
        core.NavigationCompleted += (_, args) =>
        {
            if (disposed) return;
            if (checkingIsolation)
            {
                RuntimeNavigationCompletion probeCompletion = challengeNavigation.OnCompleted(args.NavigationId, args.IsSuccess);
                if (probeCompletion != RuntimeNavigationCompletion.Ignored)
                    challengeLoaded.TrySetResult(probeCompletion != RuntimeNavigationCompletion.InitialPageFailed && core.Source == challenge.PageUri);
                return;
            }
            RuntimeNavigationCompletion completion = navigation.OnCompleted(args.NavigationId, args.IsSuccess);
            if (completion == RuntimeNavigationCompletion.Ignored)
            {
                if (applicationDocumentCommitted && core.Source != Session.PageUri) Crash("document_binding_lost");
                else Audit("navigation_completion_ignored");
                return;
            }
            if (completion == RuntimeNavigationCompletion.InitialPageFailed) { Crash("navigation_failed"); return; }
            navigated = true;
            if (desiredForeground) Foreground(); else Background();
            Audit("navigation_completed");
        };
        core.ProcessFailed += (_, args) =>
        {
            // Fixed SDK enums and a numeric exit status only; descriptions/frames can
            // contain application-controlled URLs or text and are never logged.
            browserProcessFailure = new(args.ProcessFailedKind.ToString(), args.Reason.ToString(), args.ExitCode);
            Crash("process_failed");
        };
        core.WebMessageReceived += async (_, args) =>
        {
            try
            {
                if (disposed || stopped.IsCancellationRequested || checkingIsolation || !applicationDocumentCommitted || !Session.NetworkSandboxVerified || core.Source != Session.PageUri) return;
                string source = args.Source;
                string request = args.WebMessageAsJson;
                if (Encoding.UTF8.GetByteCount(request) > RuntimeSession.MaximumMessageBytes) { Audit("message_too_large"); return; }
                var session = Session;
                string methodName = "";
                try
                {
                    using var envelope = JsonDocument.Parse(request);
                    if (envelope.RootElement.ValueKind == JsonValueKind.Object && envelope.RootElement.TryGetProperty("method", out var requestedMethod) && requestedMethod.ValueKind == JsonValueKind.String)
                        methodName = requestedMethod.GetString() ?? "";
                }
                catch (JsonException) { /* The gateway returns the finite invalid-request envelope. */ }
                long revision = session.EventRevision;
                string result = await session.HandleMessageAsync(source, request, requestPermission, stopped.Token);
                if (disposed || stopped.IsCancellationRequested || session != Session || core.Source != session.PageUri)
                { Audit("response_invalidated"); return; }
                result = session.DeliverResponse(methodName, revision, result, core.PostWebMessageAsJson);
                using var response = JsonDocument.Parse(result);
                if (methodName.Length > 0)
                {
                    var known = methodName;
                    string[] traceable = ["platform.getCapabilities", "lifecycle.getState", "permissions.query", "permissions.request", "identity.getProfile", "identity.requestProfile", "saves.list", "saves.read", "saves.write", "saves.restore", "storage.read", "storage.write", "storage.delete", "preferences.get", "preferences.set", "files.pickOpen", "files.pickSave", "files.read", "files.write", "appearance.get", "notifications.show", "notifications.setBadge", "shortcuts.register", "widgets.update", "links.openInternal"];
                    if (known is not null && traceable.Contains(known, StringComparer.Ordinal))
                    {
                        bool ok = response.RootElement.GetProperty("ok").GetBoolean();
                        string code = ok ? "OK" : response.RootElement.GetProperty("error").GetProperty("code").GetString() ?? "UNKNOWN";
                        sdkTrace?.Invoke(known, code);
                    }
                    if (known is "saves.write" or "saves.read" or "permissions.request")
                        Audit(known.Replace('.', '_') + (response.RootElement.GetProperty("ok").GetBoolean() ? "_ok" : "_rejected"));
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException) { Audit("response_not_delivered"); }
        };
        startupStage = "network_isolation";
        Audit("isolation_check_started");
        core.Navigate(challenge.PageUri);
        if (!await challengeLoaded.Task.WaitAsync(TimeSpan.FromSeconds(8), linked.Token)) throw new PackageException("RUNTIME_ISOLATION_UNAVAILABLE");
        RuntimeIsolationProof? proof = null;
        var probeBudget = System.Diagnostics.Stopwatch.StartNew();
        while (proof is null && probeBudget.Elapsed < TimeSpan.FromSeconds(5))
        {
            linked.Token.ThrowIfCancellationRequested();
            if (disposed || core.Source != challenge.PageUri) throw new PackageException("RUNTIME_ISOLATION_UNAVAILABLE");
            string nativeResult = await core.ExecuteScriptAsync(challenge.ResultExpression)
                .AsTask(linked.Token).WaitAsync(TimeSpan.FromSeconds(3), linked.Token);
            if (disposed || core.Source != challenge.PageUri) throw new PackageException("RUNTIME_ISOLATION_UNAVAILABLE");
            proof = RuntimeIsolationPolicy.ValidateNativeResult(challenge, nativeResult);
            if (proof is null) await Task.Delay(50, linked.Token);
        }
        if (proof is null || !Session.ConfirmNetworkIsolation(proof)) throw new PackageException("RUNTIME_ISOLATION_UNAVAILABLE");
        Audit("isolation_verified");
        checkingIsolation = false;
        startupStage = "navigation";
        core.Navigate(Session.PageUri);
    }

    private static void RejectBrowserPolicyOverrides()
    {
        // WebView2 also accepts registry overrides for unpackaged hosts. Read only the
        // documented current executable / wildcard selectors; never alter system policy.
        string executable = Environment.ProcessPath ?? throw new PackageException("RUNTIME_ENVIRONMENT_OVERRIDE_BLOCKED");
        foreach (var hive in new[] { Microsoft.Win32.RegistryHive.CurrentUser, Microsoft.Win32.RegistryHive.LocalMachine })
        foreach (var registryView in new[] { Microsoft.Win32.RegistryView.Registry64, Microsoft.Win32.RegistryView.Registry32 })
        {
            using var rootKey = Microsoft.Win32.RegistryKey.OpenBaseKey(hive, registryView);
            foreach (string setting in new[] { "AdditionalBrowserArguments", "BrowserExecutableFolder", "UserDataFolder" })
            {
                using var key = rootKey.OpenSubKey(@"Software\Policies\Microsoft\Edge\WebView2\" + setting, writable: false);
                if (key is null) continue;
                foreach (string selector in new[] { "*", Path.GetFileName(executable), executable })
                    if (key.GetValue(selector, null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is not null)
                        throw new PackageException("RUNTIME_ENVIRONMENT_OVERRIDE_BLOCKED");
            }
        }
    }

    public void Foreground()
    {
        if (disposed) return;
        desiredForeground = true;
        if (!navigated) return;
        Session.Foreground();
        PublishState("前台运行 · 隔离存档 · 游戏会话阻止更新");
    }

    public void Background()
    {
        if (disposed || !Session.Instance.BlocksMaintenance) return;
        desiredForeground = false;
        if (!navigated) return;
        Session.Background();
        PublishState("后台运行 · 游戏会话仍阻止更新");
    }

    private void PublishState(string text)
    {
        status(text);
        string state = Session.Instance.State.ToString().ToLowerInvariant();
        try
        {
            if (navigated && view?.CoreWebView2 is { } core && core.Source == Session.PageUri)
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { @event = "lifecycle.stateChanged", state }));
        }
        catch (System.Runtime.InteropServices.COMException) { Audit("lifecycle_not_delivered"); }
        Audit(state);
    }

    private void Crash(string reason)
    {
        if (disposed) return;
        try
        {
            Session.MarkCrashed();
            stopped.Cancel();
            closeCapabilities?.Invoke(Session.Instance.Id.Value);
            status("应用运行中断 · 返回主界面后可重新打开；已提交存档保留");
            Audit(reason);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Audit(reason, error, "crash_cancellation"); }
        finally { Dispose(); }
    }

    private void Audit(string eventName, Exception? startupError = null, string? cleanupStage = null)
    {
        // Called only with fixed host events; no SDK payload, user content, URLs, or exception text.
        try
        {
            string path = Path.Combine(root.Directories["Logs"], "runtime-events.jsonl");
            ValidateManagedPath(path);
            using var stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            if (stream.Length >= 1024 * 1024) return;
            stream.Seek(0, SeekOrigin.End);
            byte[] line = JsonSerializer.SerializeToUtf8Bytes(new { timestampUtc = DateTimeOffset.UtcNow,
                buildId = BrandInfo.BuildId, instanceId = Session.Instance.Id.Value, eventName,
                state = Session.Instance.State.ToString(), blocksMaintenance = Session.Instance.BlocksMaintenance,
                appId = Installed?.Manifest.AppId, packageSha256 = Installed?.PackageSha256,
                networkSandboxVerified = Session.NetworkSandboxVerified,
                isolationPolicy = RuntimeIsolationPolicy.PolicyId,
                browserVersion,
                browserProcessFailure,
                browserProcessId = keyboardBrowserProcess?.Id,
                profileNamespace,
                profilePathLength,
                cleanupFailure = cleanupStage is null || startupError is null ? null : new
                {
                    stage = cleanupStage,
                    outer = ErrorMetadata(startupError),
                    inner = startupError is AggregateException aggregate
                        ? aggregate.Flatten().InnerExceptions.Take(8).Select(ErrorMetadata).ToArray() : Array.Empty<object>()
                },
                startupFailure = startupError is null || cleanupStage is not null ? null : new
                {
                    stage = startupStage,
                    missingBoundary = startupError is PackageException { Code: "RUNTIME_CLIPBOARD_BOUNDARY_UNAVAILABLE" } ? "clipboard" : null,
                    errorType = startupError switch
                    {
                        OperationCanceledException => "OperationCanceledException",
                        System.Runtime.InteropServices.COMException => "COMException",
                        UnauthorizedAccessException => "UnauthorizedAccessException",
                        IOException => "IOException",
                        ArgumentException => "ArgumentException",
                        _ => "Other"
                    },
                    hResult = "0x" + unchecked((uint)startupError.HResult).ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
                }
            });
            stream.Write(line);
            stream.WriteByte((byte)'\n');
        }
        // Diagnostic failures must not interrupt revocation or browser/resource cleanup.
        catch (Exception error) when (error is not OutOfMemoryException) { }
    }

    private static object ErrorMetadata(Exception error) => new
    {
        errorType = error.GetType().FullName,
        hResult = "0x" + unchecked((uint)error.HResult).ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
    };

    private static string MimeType(string name) => Path.GetExtension(name) switch
    {
        ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8", ".json" => "application/json", ".wasm" => "application/wasm",
        ".svg" => "image/svg+xml", ".png" => "image/png", ".jpg" => "image/jpeg", ".woff2" => "font/woff2",
        _ => "application/octet-stream"
    };

    private void ValidateManagedPath(string path)
    {
        path = Path.GetFullPath(path);
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root.DataDirectory) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase)) throw new IOException("RUNTIME_UNSAFE_PATH");
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("RUNTIME_UNSAFE_PATH");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }

    public void Dispose()
    {
        if (ResourcesReleased || cleanupInProgress) return;
        cleanupInProgress = true;
        cleanupPending = true;
        disposed = true;
        bool failed = false;
        void Release(string stage, Action release)
        {
            try { release(); }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failed = true;
                Audit("resource_release_failed", error, stage);
            }
        }
        try
        {
            // End revokes the epoch/proof before invoking cancellation callbacks.
            // Do this explicitly even when no account service is bound to stopped.
            Release("session_end", Session.Close);
            Release("stopped_cancel", () => stopped.Cancel());
            Release("capabilities", () => closeCapabilities?.Invoke(Session.Instance.Id.Value));
            // A failed cancellation must not prevent trying to close the actual browser.
            Release("webview_close", () => { view?.Close(); view = null; });
            Release("browser_handle", () => { keyboardBrowserProcess?.Dispose(); keyboardBrowserProcess = null; });
            Release("container", () => container.Children.Clear());
            Release("response_streams", () =>
            {
                foreach (var stream in responseStreams) stream.Dispose();
                responseStreams.Clear(); resources.Clear();
            });
            if (!failed && !startInProgress)
            {
                Release("runtime_lease", () =>
                {
                    var current = runtimeLease;
                    current?.Dispose();
                    Interlocked.CompareExchange(ref runtimeLease, null, current);
                });
                if (!failed) Release("maintenance_lease", () =>
                {
                    var current = maintenanceGameLease;
                    current?.Dispose();
                    Interlocked.CompareExchange(ref maintenanceGameLease, null, current);
                });
                cleanupPending = failed;
                if (!failed) Audit("closed");
            }
            if (failed)
            {
                // Retain before reporting UI status so a UI error cannot lose the retry.
                retainForCleanup?.Invoke(this);
                Release("cleanup_status", () => status("资源尚未释放，更新将继续等待；系统会重试资源清理。"));
            }
        }
        finally { cleanupInProgress = false; }
    }
}
