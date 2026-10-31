using System.Text.Json;
using System.Text;
using AutumnOS.Contracts;
using AutumnOS.Packages;
using AutumnOS.Runtime;
using AutumnOS.Storage;
using Microsoft.UI.Xaml.Controls;

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
    private readonly Dictionary<string, byte[]> resources = new(StringComparer.Ordinal);
    private IRuntimeViewBackend? backend;
    private bool browserCleanupWaitingReported;
    private bool disposed;
    private bool cleanupInProgress;
    private bool cleanupPending;
    private bool startInProgress;
    private bool navigated;
    private bool desiredForeground = true;
    private Action? rejectBootstrap;
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
    public bool ResourcesReleased => disposed && !cleanupInProgress && !cleanupPending && !startInProgress &&
        (backend is null || backend.ResourcesReleased) && resources.Count == 0 && rejectBootstrap is null && runtimeLease is null && maintenanceGameLease is null;
    public bool IsDeveloperPreview => previewPackage is not null;

    // Capture the actual controller's browser process, with an open handle to prevent
    // PID reuse from admitting an unrelated browser into the native shortcut scope.
    internal bool OwnsKeyboardProcess(uint processId) => !disposed && navigated && desiredForeground &&
        backend?.OwnsKeyboardProcess(processId) == true;

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
            // Deferral completion can reenter native handlers. Revoke the session's epoch
            // and pending data commits before any failed bootstrap is completed.
            try { Session.Close(); }
            catch (Exception revocationError) when (revocationError is not OutOfMemoryException)
            { Audit("startup_revocation_failed", revocationError, "session_end"); }
            try { RejectBootstrap(); }
            catch (Exception rejectionError) when (rejectionError is not OutOfMemoryException)
            { Audit("bootstrap_rejection_failed", rejectionError, "bootstrap_gate"); }
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
        var sourceSession = Session;
        sourceSession.SdkEventReady += message =>
        {
            container.DispatcherQueue.TryEnqueue(() =>
            {
                if (!disposed && !stopped.IsCancellationRequested && sourceSession == Session && navigated && backend is { } core && core.Source == sourceSession.PageUri)
                    sourceSession.DeliverEvent(message.Name, message.Revision, () => core.PostWebMessageAsJson(JsonSerializer.Serialize(new { @event = message.Name, data = message.Data }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })));
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
        // Publish ownership before any native control can be created or attached.
        // A failed attach remains available to the normal cleanup/retry path.
        backend = new WinUiWebViewBackend(container);
        backend.UserGesture += (_, _) => Session.HostUserGesture();
        backend.PolicyEvent += eventName => Audit(eventName);
        backend.BrowserProcessExited += OnBrowserProcessExited;
        backend.Attach();
        var core = backend;
        string fixedRuntime = Path.Combine(AppContext.BaseDirectory, "WebView2Runtime");
        if (!File.Exists(Path.Combine(fixedRuntime, "msedgewebview2.exe"))) throw new PackageException("RUNTIME_ISOLATION_ENGINE_MISSING");
        startupStage = "environment";
        await core.CreateEnvironmentAsync(new(fixedRuntime, profileRoot, RuntimeIsolationPolicy.BrowserArguments));
        var environmentFacts = core.EnvironmentFacts ?? throw new InvalidOperationException("RUNTIME_VIEW_ENVIRONMENT_FACTS_REQUIRED");
        browserVersion = environmentFacts.BrowserVersion;
        if (!RuntimeIsolationPolicy.IsSupportedBrowserVersion(browserVersion))
            throw new PackageException("RUNTIME_ISOLATION_ENGINE_UNSUPPORTED");
        if (!string.Equals(Path.GetFullPath(environmentFacts.UserDataFolder).TrimEnd(Path.DirectorySeparatorChar),
            Path.GetFullPath(profileRoot).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
            throw new PackageException("RUNTIME_ENVIRONMENT_OVERRIDE_BLOCKED");
        Audit("environment_created");
        linked.Token.ThrowIfCancellationRequested();
        startupStage = "controller";
        await core.CreateControllerAsync();
        Audit("controller_created");
        startupStage = "settings";
        var identityFacts = core.IdentityFacts ?? throw new InvalidOperationException("RUNTIME_VIEW_IDENTITY_FACTS_REQUIRED");
        linked.Token.ThrowIfCancellationRequested();
        if (!identityFacts.InPrivate || !string.Equals(identityFacts.BrowserExecutablePath,
            Path.Combine(fixedRuntime, "msedgewebview2.exe"), StringComparison.OrdinalIgnoreCase))
            throw new PackageException("RUNTIME_ENVIRONMENT_OVERRIDE_BLOCKED");
        core.ConfigureRestrictedPolicy();
        startupStage = "bootstrap_prepare";
        var navigation = new RuntimeNavigationGuard(Session.PageUri);
        var challenge = RuntimeIsolationPolicy.CreateParserGateChallenge(Session.Instance.Id.Value, Session.Origin, installed.Manifest.Entry);
        if (!resources.TryGetValue(installed.Manifest.Entry, out byte[]? entryBytes)) throw new PackageException("PACKAGE_ENTRY_MISSING");
        string applicationHtml = new UTF8Encoding(false, true).GetString(entryBytes);
        if (applicationHtml.StartsWith('\uFEFF')) applicationHtml = applicationHtml[1..];
        byte[] applicationGate = Encoding.UTF8.GetBytes(challenge.CreateGateScript(applicationHtml));
        if (resources.ContainsKey(challenge.Isolation.ScriptPath) || resources.ContainsKey(challenge.GatePath))
            throw new PackageException("RUNTIME_BOOTSTRAP_PATH_COLLISION");
        var documentLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateRequested = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applicationLoaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        RuntimeViewResourceRequestedEventArgs? heldGateRequest = null;
        IRuntimeViewDeferral? heldGateDeferral = null;
        ulong? initialNavigationId = null;
        bool sourceCommitted = false, documentCommitted = false, packageReleased = false, bootstrapRevoked = false;
        void Respond(RuntimeViewResourceRequestedEventArgs args, byte[]? content, string mime)
        {
            args.Respond(content, content is null ? 403 : 200,
                content is null ? "Forbidden" : "OK", "Content-Type: " + mime + "\r\n" + RuntimeIsolationPolicy.ResponseHeaders);
        }
        void CompleteGate(bool authorize)
        {
            var request = heldGateRequest; var deferral = heldGateDeferral;
            heldGateRequest = null; heldGateDeferral = null; // One completion attempt, including reentrant teardown.
            if (request is null || deferral is null)
            {
                if (authorize) throw new PackageException("RUNTIME_ISOLATION_UNAVAILABLE");
                return;
            }
            List<Exception> failures = [];
            try
            {
                if (authorize)
                {
                    if (disposed || bootstrapRevoked || linked.IsCancellationRequested || !documentCommitted
                        || !Session.NetworkSandboxVerified || core.Source != Session.PageUri)
                        throw new PackageException("RUNTIME_ISOLATION_UNAVAILABLE");
                    Respond(request, applicationGate, "text/javascript; charset=utf-8");
                    core.WebMessageEnabled = true;
                    // The parser can execute immediately when Complete returns to Chromium.
                    // Publish eligibility only after the real native proof and response succeeded.
                    if (disposed || bootstrapRevoked || linked.IsCancellationRequested || core.Source != Session.PageUri)
                        throw new PackageException("RUNTIME_ISOLATION_UNAVAILABLE");
                    packageReleased = true;
                }
                else Respond(request, null, "text/javascript; charset=utf-8");
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                bootstrapRevoked = true; packageReleased = false; failures.Add(error);
                try { Session.Close(); }
                catch (Exception revocationError) when (revocationError is not OutOfMemoryException) { failures.Add(revocationError); }
                try { Respond(request, null, "text/javascript; charset=utf-8"); }
                catch (Exception denialError) when (denialError is not OutOfMemoryException) { failures.Add(denialError); }
            }
            finally
            {
                try { deferral.Complete(); }
                catch (Exception completionError) when (completionError is not OutOfMemoryException)
                {
                    bootstrapRevoked = true; packageReleased = false; failures.Add(completionError);
                    try { Session.Close(); }
                    catch (Exception revocationError) when (revocationError is not OutOfMemoryException) { failures.Add(revocationError); }
                }
            }
            if (failures.Count > 0) throw new AggregateException("RUNTIME_BOOTSTRAP_GATE_COMPLETION_FAILED", failures);
        }
        rejectBootstrap = () =>
        {
            bootstrapRevoked = true; packageReleased = false; navigated = false;
            documentLoaded.TrySetResult(false); gateRequested.TrySetResult(false); applicationLoaded.TrySetResult(false);
            CompleteGate(false);
        };
        core.NavigationStarting += (_, args) =>
        {
            if (disposed || bootstrapRevoked || initialNavigationId.HasValue || !navigation.OnStarting(args.NavigationId, args.Uri))
            { args.Cancel = true; Audit("navigation_denied"); }
            else { initialNavigationId = args.NavigationId; Audit("initial_navigation_started"); }
        };
        core.FrameNavigationStarting += (_, args) => { args.Cancel = true; Audit("frame_denied"); };
        // A canceled request can still commit an engine error document (observed with
        // Connection-Allowlist). Cancellation alone is not proof that the old document
        // survived. Revoke its session before any later response/event can be delivered.
        core.SourceChanged += (_, args) =>
        {
            if (disposed) return;
            if (!initialNavigationId.HasValue || core.Source != Session.PageUri || (args.IsNewDocument && (sourceCommitted || documentCommitted)))
            { Crash("document_binding_lost"); return; }
            if (args.IsNewDocument) sourceCommitted = true;
        };
        core.ContentLoading += (_, args) =>
        {
            if (disposed) return;
            if (args.IsErrorPage) { Crash("error_document_blocked"); return; }
            if (bootstrapRevoked || documentCommitted || !initialNavigationId.HasValue || args.NavigationId != initialNavigationId.Value || core.Source != Session.PageUri)
            { Crash("document_binding_lost"); return; }
            // The trusted bootstrap is already the final application document. Replacement
            // from this point revokes the session even while its parser is still held.
            documentCommitted = true; documentLoaded.TrySetResult(true); Audit("bootstrap_document_committed");
        };
        core.ResourceRequested += (_, args) =>
        {
            try
            {
                byte[]? content = null;
                string mime = "text/plain; charset=utf-8";
                if (!disposed && !bootstrapRevoked && !linked.IsCancellationRequested && core.ResponseStreamCount < 512
                    && args.Method == "GET" && args.Uri.StartsWith(Session.Origin, StringComparison.Ordinal))
                {
                    string relative = args.Uri[Session.Origin.Length..];
                    if (!packageReleased)
                    {
                        if (args.Uri == challenge.GateUri && args.ResourceContext == RuntimeViewResourceContext.Script
                            && heldGateDeferral is null && !gateRequested.Task.IsCompleted && initialNavigationId.HasValue)
                        {
                            heldGateRequest = args; heldGateDeferral = args.GetDeferral(); gateRequested.TrySetResult(true);
                            Audit("bootstrap_gate_held"); return;
                        }
                        if (args.Uri == Session.PageUri && args.ResourceContext == RuntimeViewResourceContext.Document)
                        { content = Encoding.UTF8.GetBytes(challenge.BootstrapHtml); mime = "text/html; charset=utf-8"; }
                        else if (args.Uri == challenge.BootstrapScriptUri && args.ResourceContext == RuntimeViewResourceContext.Script)
                        { content = Encoding.UTF8.GetBytes(challenge.BootstrapScript); mime = "text/javascript; charset=utf-8"; }
                    }
                    else if (Session.NetworkSandboxVerified && resources.TryGetValue(relative, out content)) mime = MimeType(relative);
                }
                // Use only verified package resources, never a file:// or generic filesystem/network proxy.
                Respond(args, content, mime);
            }
            catch (Exception responseError) when (responseError is not OutOfMemoryException)
            {
                Audit("resource_response_failed", responseError, "web_resource_response");
                Crash("resource_response_failed");
            }
        };
        core.NavigationCompleted += (_, args) =>
        {
            if (disposed) return;
            RuntimeNavigationCompletion completion = navigation.OnCompleted(args.NavigationId, args.IsSuccess);
            if (completion == RuntimeNavigationCompletion.Ignored)
            {
                if (documentCommitted && core.Source != Session.PageUri) Crash("document_binding_lost");
                else Audit("navigation_completion_ignored");
                return;
            }
            if (completion == RuntimeNavigationCompletion.InitialPageFailed || bootstrapRevoked || !documentCommitted
                || !packageReleased || !Session.NetworkSandboxVerified || core.Source != Session.PageUri)
            { applicationLoaded.TrySetResult(false); Crash("navigation_failed"); return; }
            navigated = true;
            if (desiredForeground) Foreground(); else Background();
            applicationLoaded.TrySetResult(true);
            Audit("navigation_completed");
        };
        core.ProcessFailed += (_, args) =>
        {
            // Fixed SDK enums and a numeric exit status only; descriptions/frames can
            // contain application-controlled URLs or text and are never logged.
            browserProcessFailure = new(args.Kind, args.Reason, args.ExitCode);
            Crash("process_failed");
        };
        core.WebMessageReceived += async (_, args) =>
        {
            try
            {
                if (disposed || stopped.IsCancellationRequested || bootstrapRevoked || !packageReleased || !documentCommitted || !Session.NetworkSandboxVerified || core.Source != Session.PageUri) return;
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
                if (disposed || stopped.IsCancellationRequested || bootstrapRevoked || !packageReleased || !documentCommitted || session != Session || core.Source != session.PageUri)
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
        core.Navigate(Session.PageUri);
        if (!await documentLoaded.Task.WaitAsync(TimeSpan.FromSeconds(8), linked.Token)
            || !await gateRequested.Task.WaitAsync(TimeSpan.FromSeconds(8), linked.Token))
            throw new PackageException("RUNTIME_ISOLATION_UNAVAILABLE");
        RuntimeIsolationProof? proof = null;
        var probeBudget = System.Diagnostics.Stopwatch.StartNew();
        while (proof is null && probeBudget.Elapsed < TimeSpan.FromSeconds(5))
        {
            linked.Token.ThrowIfCancellationRequested();
            if (disposed || bootstrapRevoked || !documentCommitted || heldGateDeferral is null || core.Source != Session.PageUri)
                throw new PackageException("RUNTIME_ISOLATION_UNAVAILABLE");
            string nativeResult = await core.ExecuteScriptAsync(challenge.Isolation.ResultExpression, linked.Token)
                .WaitAsync(TimeSpan.FromSeconds(3), linked.Token);
            if (disposed || bootstrapRevoked || core.Source != Session.PageUri) throw new PackageException("RUNTIME_ISOLATION_UNAVAILABLE");
            proof = RuntimeIsolationPolicy.ValidateNativeResult(challenge.Isolation, nativeResult);
            if (proof is null) await Task.Delay(50, linked.Token);
        }
        if (proof is null) throw new PackageException("RUNTIME_ISOLATION_UNAVAILABLE");
        string bindingResult = await core.ExecuteScriptAsync(challenge.BindingResultExpression, linked.Token)
            .WaitAsync(TimeSpan.FromSeconds(3), linked.Token);
        if (disposed || bootstrapRevoked || !documentCommitted || heldGateDeferral is null || core.Source != Session.PageUri
            || !RuntimeIsolationPolicy.ValidateParserGateBinding(challenge, bindingResult) || !Session.ConfirmNetworkIsolation(proof))
            throw new PackageException("RUNTIME_ISOLATION_UNAVAILABLE");
        Audit("bootstrap_document_binding_verified");
        Audit("isolation_verified");
        startupStage = "navigation";
        CompleteGate(true);
        Audit("bootstrap_gate_released");
        if (!await applicationLoaded.Task.WaitAsync(TimeSpan.FromSeconds(8), linked.Token))
            throw new PackageException("RUNTIME_ISOLATION_UNAVAILABLE");
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
            if (navigated && backend is { } core && core.Source == Session.PageUri)
                core.PostWebMessageAsJson(JsonSerializer.Serialize(new { @event = "lifecycle.stateChanged", state }));
        }
        catch (System.Runtime.InteropServices.COMException) { Audit("lifecycle_not_delivered"); }
        Audit(state);
    }

    private void Crash(string reason)
    {
        if (disposed) return;
        try { Session.MarkCrashed(); }
        catch (Exception revocationError) when (revocationError is not OutOfMemoryException)
        { Audit(reason, revocationError, "session_crash"); }
        try { RejectBootstrap(); }
        catch (Exception rejectionError) when (rejectionError is not OutOfMemoryException)
        { Audit("bootstrap_rejection_failed", rejectionError, "bootstrap_gate"); }
        try
        {
            stopped.Cancel();
            closeCapabilities?.Invoke(Session.Instance.Id.Value);
            status("应用运行中断 · 返回主界面后可重新打开；已提交存档保留");
            Audit(reason);
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Audit(reason, error, "crash_cancellation"); }
        finally { Dispose(); }
    }

    private void RejectBootstrap()
    {
        // Revoke before completing a held resource response. A throwing COM completion
        // must not keep the delegate alive or prevent closing the native controller.
        Action? reject = rejectBootstrap;
        rejectBootstrap = null;
        reject?.Invoke();
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
            var nativeCleanup = backend?.CleanupFacts;
            byte[] line = JsonSerializer.SerializeToUtf8Bytes(new { timestampUtc = DateTimeOffset.UtcNow,
                buildId = BrandInfo.BuildId, instanceId = Session.Instance.Id.Value, eventName,
                state = Session.Instance.State.ToString(), blocksMaintenance = Session.Instance.BlocksMaintenance,
                appId = Installed?.Manifest.AppId, packageSha256 = Installed?.PackageSha256,
                networkSandboxVerified = Session.NetworkSandboxVerified,
                isolationPolicy = RuntimeIsolationPolicy.PolicyId,
                isolationLoadingStrategy = RuntimeParserGateChallenge.LoadingStrategyId,
                browserVersion,
                browserProcessFailure,
                browserProcessId = nativeCleanup is { EnvironmentReleased: false } ? nativeCleanup.HeldBrowserProcessId : null,
                browserCleanup = new { environmentCreationAttempted = nativeCleanup?.EnvironmentCreationAttempted ?? false,
                    controllerCreationAttempted = nativeCleanup?.ControllerCreationAttempted ?? false,
                    browserExitObserved = nativeCleanup?.BrowserExitObserved ?? false,
                    exitedBrowserProcessId = nativeCleanup?.ExitedBrowserProcessId,
                    browserExitKind = nativeCleanup?.BrowserExitKind,
                    browserResourcesReleased = nativeCleanup?.BrowserResourcesReleased ?? false, cleanupPending,
                    heldBrowserProcessId = nativeCleanup?.HeldBrowserProcessId,
                    heldBrowserNativeExitConfirmed = nativeCleanup?.HeldBrowserNativeExitConfirmed ?? false,
                    holdsRuntimeLease = runtimeLease is not null, holdsMaintenanceLease = maintenanceGameLease is not null },
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

    private void OnBrowserProcessExited(object? sender, RuntimeViewBrowserExitedEventArgs args)
    {
        // The backend records the actual native event and matches its held original
        // browser object before this parent notification. It is not an app SDK claim.
        if (backend is null || !ReferenceEquals(sender, backend)) return;
        Audit("browser_group_exited");
        // Never release leases from inside a native callback or while startup is pending.
        // The normal dispatcher retry also covers an event arriving during Close().
        if (disposed) container.DispatcherQueue.TryEnqueue(Dispose);
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
            Release("bootstrap_gate", RejectBootstrap);
            Release("stopped_cancel", () => stopped.Cancel());
            Release("capabilities", () => closeCapabilities?.Invoke(Session.Instance.Id.Value));
            // A failed cancellation must not prevent trying to close the actual browser.
            Release("webview_close", () => backend?.CloseView());
            // A new instance may already occupy this shared UI container while the old
            // browser group is shutting down. Retry must only remove our own control.
            Release("container", () => backend?.Detach());
            Release("response_streams", () =>
            {
                backend?.ReleaseResponseStreams(); resources.Clear();
            });
            bool browserReleased = false;
            Release("browser_exit_check", () => browserReleased = backend?.TryRetire(startInProgress) ?? !startInProgress);
            if (!failed && !startInProgress && browserReleased)
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
            if (failed || !browserReleased || startInProgress)
            {
                // Retain before reporting UI status so a UI error cannot lose the retry.
                retainForCleanup?.Invoke(this);
                if (!browserCleanupWaitingReported)
                {
                    browserCleanupWaitingReported = true;
                    Audit("browser_cleanup_pending");
                    Release("cleanup_status", () => status("资源尚未释放，更新将继续等待；系统会重试资源清理。"));
                }
            }
        }
        finally { cleanupInProgress = false; }
    }
}
