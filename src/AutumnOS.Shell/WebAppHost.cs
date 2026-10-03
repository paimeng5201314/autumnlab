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

/// <summary>Hosts the bundled T01 application. General third-party launch remains gated on network isolation verification.</summary>
internal sealed class WebAppHost : IDisposable
{
    private const string SampleId = "cn.labchronicles.elementpairs";
    private const string ContentPolicy = "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'none'; frame-src 'none'; child-src 'none'; worker-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    private readonly InstallationRoot root;
    private readonly Grid container;
    private readonly Func<string, CancellationToken, Task<bool>> requestPermission;
    private readonly Action<string> status;
    private readonly CancellationTokenSource stopped = new();
    private readonly List<Stream> responseStreams = [];
    private readonly Dictionary<string, byte[]> resources = new(StringComparer.Ordinal);
    private WebView2? view;
    private bool disposed;
    private bool startInProgress;
    private bool navigated;
    private bool desiredForeground = true;
    private string startupStage = "package";
    private int? profilePathLength;
    private readonly Func<InstalledPackage, Func<RuntimeSession>, CancellationToken, RuntimeSessionServices>? serviceFactory;
    private readonly Action<Guid>? closeCapabilities;
    private readonly string? previewPackage;
    private readonly string? expectedPreviewSha256;
    private readonly Action<string, string>? sdkTrace;
    private readonly InstalledPackage? registeredPackage;
    private IDisposable? runtimeLease;
    private IDisposable? maintenanceGameLease;
    private readonly Action<WebAppHost>? retainForCleanup;
    public bool ResourcesReleased => !startInProgress && runtimeLease is null && maintenanceGameLease is null;
    public bool IsDeveloperPreview => previewPackage is not null;

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
        status("正在验证并安装内置 .autumn 应用…");
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
        // WebView2 creates additional native subdirectories below userDataFolder. The old account hash /
        // binding hash nesting exhausted MAX_PATH in ordinary deep portable directories on this machine.
        // InPrivate browser cache is not an app save: retain the old cache untouched and start a compact,
        // independently bound cache. Actual account/source data paths and migration rules are unchanged.
        string profileRoot = WebViewProfileDirectory.ForBinding(root.Directories["Runtime"], services?.Account.AccountKey ?? "guest",
            services?.Application.BindingKey ?? RuntimeApplication.Hash("autumnos.legacy-bundled-profile.v1\0" + installed.Manifest.AppId));
        profilePathLength = profileRoot.Length;
        ValidateManagedPath(Path.Combine(profileRoot, "profile-marker"));
        Directory.CreateDirectory(profileRoot);
        ValidateManagedPath(Path.Combine(profileRoot, "profile-marker"));
        Audit("profile_prepared");
        view = new WebView2();
        view.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => Session.HostUserGesture()), true);
        view.AddHandler(UIElement.KeyDownEvent, new KeyEventHandler((_, _) => Session.HostUserGesture()), true);
        container.Children.Add(view);
        var environmentOptions = new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = "--force-renderer-accessibility" };
        string fixedRuntime = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
        startupStage = "environment";
        var environment = await CoreWebView2Environment.CreateWithOptionsAsync(Directory.Exists(fixedRuntime) ? fixedRuntime : null, profileRoot, environmentOptions);
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
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDefaultScriptDialogsEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsWebMessageEnabled = true;
        core.PermissionRequested += (_, args) => { args.State = CoreWebView2PermissionState.Deny; args.Handled = true; };
        core.NewWindowRequested += (_, args) => { args.Handled = true; Audit("new_window_denied"); };
        core.DownloadStarting += (_, args) => { args.Cancel = true; Audit("download_denied"); };
        core.LaunchingExternalUriScheme += (_, args) => { args.Cancel = true; Audit("external_protocol_denied"); };
        var navigation = new RuntimeNavigationGuard(Session.PageUri);
        core.NavigationStarting += (_, args) =>
        {
            if (!navigation.OnStarting(args.NavigationId, args.Uri)) { args.Cancel = true; Audit("navigation_denied"); }
        };
        core.FrameNavigationStarting += (_, args) => { args.Cancel = true; Audit("frame_denied"); };
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        core.WebResourceRequested += (_, args) =>
        {
            byte[]? content = null;
            string mime = "text/plain; charset=utf-8";
            if (!disposed && args.Request.Method == "GET" && args.Request.Uri.StartsWith(Session.Origin, StringComparison.Ordinal))
            {
                string relative = args.Request.Uri[Session.Origin.Length..];
                if (responseStreams.Count < 512 && resources.TryGetValue(relative, out content)) mime = MimeType(relative);
            }
            // Use only verified package resources, never a file:// or generic filesystem/network proxy.
            Stream? stream = content is null ? null : new MemoryStream(content, writable: false);
            if (stream is not null) responseStreams.Add(stream);
            args.Response = environment.CreateWebResourceResponse(stream?.AsRandomAccessStream(), content is null ? 403 : 200,
                content is null ? "Forbidden" : "OK", "Content-Type: " + mime + "\r\nContent-Security-Policy: " + ContentPolicy
                + "\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer\r\nPermissions-Policy: camera=(), microphone=(), geolocation=(), usb=(), serial=(), bluetooth=(), display-capture=()");
        };
        core.NavigationCompleted += (_, args) =>
        {
            if (disposed) return;
            RuntimeNavigationCompletion completion = navigation.OnCompleted(args.NavigationId, args.IsSuccess);
            if (completion == RuntimeNavigationCompletion.Ignored) { Audit("navigation_completion_ignored"); return; }
            if (completion == RuntimeNavigationCompletion.InitialPageFailed) { Crash("navigation_failed"); return; }
            navigated = true;
            if (desiredForeground) Foreground(); else Background();
            Audit("navigation_completed");
        };
        core.ProcessFailed += (_, _) => Crash("process_failed");
        core.WebMessageReceived += async (_, args) =>
        {
            try
            {
                if (disposed || stopped.IsCancellationRequested) return;
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
                if (disposed || stopped.IsCancellationRequested || session != Session || core.Source != session.PageUri) return;
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
        startupStage = "navigation";
        core.Navigate(Session.PageUri);
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
        view?.Close();
        container.Children.Clear();
        view = null;
        Session.MarkCrashed();
        closeCapabilities?.Invoke(Session.Instance.Id.Value);
        stopped.Cancel();
        status("应用运行中断 · 返回主界面后可重新打开；已提交存档保留");
        Audit(reason);
        Dispose();
    }

    private void Audit(string eventName, Exception? startupError = null)
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
                profilePathLength,
                startupFailure = startupError is null ? null : new
                {
                    stage = startupStage,
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
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
    }

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
        if (!disposed) { disposed = true; stopped.Cancel(); }
        try
        {
            view?.Close(); view = null;
            Session.Close();
            closeCapabilities?.Invoke(Session.Instance.Id.Value);
            container.Children.Clear();
            foreach (var stream in responseStreams) stream.Dispose();
            responseStreams.Clear(); resources.Clear();
            if (startInProgress) return;
            Interlocked.Exchange(ref runtimeLease, null)?.Dispose();
            Interlocked.Exchange(ref maintenanceGameLease, null)?.Dispose();
            Audit("closed");
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Keep both leases until a later cleanup succeeds; a clicked End is not an exit.
            Audit("resource_release_failed");
            status("资源尚未释放，更新将继续等待；系统会重试资源清理。");
            retainForCleanup?.Invoke(this);
        }
    }
}
