using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using FrozenPolicy = AutumnOS.ClipboardAppContainerProbe.FrozenPolicy;

namespace AutumnOS.OwnedBrowserParent;

// A separately invoked, visible native-input control. This class never sends
// CDP Input commands, changes input desktops, creates a Windows profile, or
// claims that an ordinary WebView is an isolation boundary.
internal static class NativeImeControl
{
    internal static int Run(SessionInputs input, string manifestPath, string freshReportAbs)
    {
        string manifest = Path.GetFullPath(manifestPath), report = Path.GetFullPath(freshReportAbs);
        if (!Path.IsPathFullyQualified(manifestPath) || !Path.IsPathFullyQualified(freshReportAbs) ||
            Path.GetFileName(report) != "native-ime-control.json" ||
            !string.Equals(Path.GetDirectoryName(report), Path.GetDirectoryName(manifest), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("NATIVE_CONTROL_PREPARED_REPORT_REQUIRED");
        OwnedFiles.PlainAncestors(manifest); OwnedFiles.PlainAncestors(Path.GetDirectoryName(report)!);
        if (Program.HashFile(manifest) != input.manifest_sha256 || input.runtime_version != FrozenPolicy.RequiredBrowserVersion ||
            input.core_executable != "AutumnOS.SessionBrowserPrototype.exe")
            throw new InvalidDataException("NATIVE_CONTROL_FROZEN_BINDING_REQUIRED");
        // The driver creates the protected prepared directory. CreateNew both
        // refuses an existing report and retains its inherited file ACL.
        using var reportFile = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        string nonce = Guid.NewGuid().ToString("N");
        string caseDirectory = Path.Combine(input.case_root, "native-ime-control-case-" + nonce);
        string udf = Path.Combine(caseDirectory, "udf");
        NativeControlWindow? window = null;
        try
        {
            RequireContext();
            TokenFacts identity = ProcessAudit.ReadToken(Native.GetCurrentProcess(), null);
            if (identity.is_appcontainer != 0 || identity.integrity_rid != 8192 || identity.capability_count != 0)
                throw new InvalidDataException("NATIVE_CONTROL_ORDINARY_MEDIUM_TOKEN_REQUIRED");
            using (var process = SessionNative.Current())
                if (SessionNative.Identity(process).elevated) throw new InvalidDataException("NATIVE_CONTROL_ELEVATION_REJECTED");
            foreach (string key in Environment.GetEnvironmentVariables().Keys)
                if (key.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("WEBVIEW2_OVERRIDE_REJECTED");
            OwnedFiles.FixedLocal(caseDirectory); OwnedFiles.PlainAncestors(input.case_root);
            CreateFreshDirectory(caseDirectory); CreateFreshDirectory(udf);
            Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
            using (window = new NativeControlWindow(input, nonce, udf, reportFile))
            {
                Application.Run(window);
                window.SaveAfterWindowClosed();
                return window.ExitCode;
            }
        }
        catch (Exception error)
        {
            // No arbitrary exception messages, environment values, document
            // text, user paths, credentials, or token SIDs enter this report.
            if (window is null)
            {
                WriteReport(reportFile, new
                {
                    schema_version = 1, status = "failed_before_native_window", native_control = true,
                    input.source_snapshot_id, input.manifest_sha256, input.runtime_version,
                    original_control_pid = Environment.ProcessId, nonce,
                    cdp_input_calls = 0, netonly_logon_calls = 0, new_appcontainer_profiles = 0,
                    new_window_stations = 0, new_desktops = 0, clipboard_api_calls = 0,
                    initialization_succeeded = false, page_succeeded = false, final_dom_observed = false,
                    window_closed_naturally = false, cleanup_confirmed = false,
                    error_stage = "before_native_window", error_code = Program.FailureCode(error),
                    g1_isolation_acceptance = "not_claimed_native_control_only", udf_retained = true
                });
            }
            return 1;
        }
    }

    private static void CreateFreshDirectory(string path)
    {
        OwnedFiles.PlainAncestors(Path.GetDirectoryName(path)!);
        // Null SD means ordinary inheritance from this new artifact's parent;
        // no existing directory, station, desktop, or global ACL is modified.
        var attributes = new Native.SecurityAttributes { Length = (uint)Marshal.SizeOf<Native.SecurityAttributes>() };
        Native.Check(Native.CreateDirectoryW(path, ref attributes), "native_control_fresh_artifact_directory");
        OwnedFiles.PlainAncestors(path);
    }

    private static void RequireContext()
    {
        if (SessionNative.StationName(SessionNative.GetProcessWindowStation()) != "WinSta0" ||
            SessionNative.StationName(GetThreadDesktop(GetCurrentThreadId())) != "Default")
            throw new InvalidDataException("NATIVE_CONTROL_WINSTA0_DEFAULT_REQUIRED");
    }

    private static void WriteReport(FileStream file, object value)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { WriteIndented = true });
        if (bytes.Length > 1048576) throw new InvalidDataException("NATIVE_CONTROL_REPORT_LIMIT");
        file.Position = 0; file.SetLength(0); file.Write(bytes); file.Flush(true);
    }

    private sealed class NativeControlWindow : Form
    {
        private readonly SessionInputs input;
        private readonly string nonce, udf, origin, pageUri, runtimeExecutableSha, controlExecutableSha;
        private readonly FileStream report;
        private readonly ProcessAudit audit;
        private readonly List<Stream> resourceStreams = [];
        private readonly List<object> errors = [];
        private readonly Dictionary<uint, bool> browserExitKinds = [];
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
        private readonly Stopwatch clock = Stopwatch.StartNew();
        private readonly TaskCompletionSource<bool> pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CoreWebView2Environment? environment;
        private CoreWebView2Controller? controller;
        private CoreWebView2? core;
        private HeldProcess? originalBrowser;
        private JsonElement? baselineEvidence, finalEvidence;
        private ulong controlCreation;
        private uint browserPid;
        private string stage = "native_control_start", primaryErrorStage = "none", browserVersion = "unobserved";
        private int environmentProcessCount = -1, navigationStarts, blockedClipboardShortcuts;
        private long closeAt, lastInventory = -1000, lastPendingReport;
        private bool initializationReturned, initialized, environmentCreationStarted, navigationRequested, pageSucceeded;
        private bool closeRequested, closeSnapshotReturned, finalDomObserved, controllerDisposed, normalBrowserExit;
        private bool allowClose, windowClosedNaturally, failed, inventoryGap, reportWriteFailed;
        private bool originalBrowserHandleSignaled, cleanupConfirmed;
        internal int ExitCode { get; private set; } = 1;

        internal NativeControlWindow(SessionInputs input, string nonce, string udf, FileStream report)
        {
            this.input = input; this.nonce = nonce; this.udf = udf; this.report = report;
            origin = "https://owned-session-" + nonce + ".autumn.invalid"; pageUri = origin + "/index.html";
            audit = new ProcessAudit(input.core_directory, input.runtime_directory, null);
            runtimeExecutableSha = input.runtime_files.Single(f => f.path == "msedgewebview2.exe").sha256;
            controlExecutableSha = input.core_files.Single(f => f.path == input.core_executable).sha256;
            Native.Check(Native.GetProcessTimes(Native.GetCurrentProcess(), out var created, out _, out _, out _), "native_control_original_creation");
            controlCreation = created.Value;
            AutoScaleMode = AutoScaleMode.None; ClientSize = new Size(800, 600);
            FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false; StartPosition = FormStartPosition.CenterScreen;
            Text = "AutumnOS 原生 IME 对照 · 正在准备，请暂勿输入";
            AllowDrop = false;
            Shown += (_, _) => { timer.Start(); _ = InitializeAsync(); };
            timer.Tick += (_, _) => Tick();
            FormClosing += (_, args) =>
            {
                if (allowClose) return;
                args.Cancel = true; RequestClose("user_close_requested", false);
            };
            FormClosed += (_, _) => windowClosedNaturally = allowClose;
        }

        private async Task InitializeAsync()
        {
            try
            {
                RequireContext(); stage = "create_environment";
                var options = new CoreWebView2EnvironmentOptions(FrozenPolicy.BrowserArguments)
                { ExclusiveUserDataFolderAccess = true, IsCustomCrashReportingEnabled = true };
                environmentCreationStarted = true;
                environment = await CoreWebView2Environment.CreateAsync(input.runtime_directory, udf, options);
                environment.BrowserProcessExited += (_, args) =>
                {
                    if (browserExitKinds.Count >= 128 && !browserExitKinds.ContainsKey(args.BrowserProcessId))
                    { inventoryGap = true; Fail("browser_exit_event_limit"); return; }
                    browserExitKinds[args.BrowserProcessId] = args.BrowserProcessExitKind == CoreWebView2BrowserProcessExitKind.Normal;
                    if (args.BrowserProcessId == browserPid) normalBrowserExit = browserExitKinds[args.BrowserProcessId];
                };
                environment.ProcessInfosChanged += (_, _) => Inventory();
                Inventory(); browserVersion = environment.BrowserVersionString;
                if (browserVersion != input.runtime_version || browserVersion != FrozenPolicy.RequiredBrowserVersion ||
                    !Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.UserDataFolder)).Equals(Path.TrimEndingDirectorySeparator(udf), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("NATIVE_CONTROL_RUNTIME_AND_UDF_READBACK");
                if (closeRequested) return;
                stage = "create_controller";
                var controllerOptions = environment.CreateCoreWebView2ControllerOptions(); controllerOptions.IsInPrivateModeEnabled = true;
                controller = await environment.CreateCoreWebView2ControllerAsync(Handle, controllerOptions);
                RequireContext();
                controller.ShouldDetectMonitorScaleChanges = false; controller.RasterizationScale = 1;
                controller.BoundsMode = CoreWebView2BoundsMode.UseRawPixels;
                controller.Bounds = new Rectangle(0, 0, 800, 600); controller.ZoomFactor = 1; controller.AllowExternalDrop = false;
                if (controller.Bounds != new Rectangle(0, 0, 800, 600) || controller.RasterizationScale != 1 || controller.ZoomFactor != 1)
                    throw new InvalidDataException("NATIVE_CONTROL_VIEWPORT_READBACK");
                core = controller.CoreWebView2; browserPid = core.BrowserProcessId;
                originalBrowser = audit.Observe(browserPid, "Browser", "SDK_CoreWebView2_BrowserProcessId_original_handle");
                if (browserExitKinds.TryGetValue(browserPid, out bool normal)) normalBrowserExit = normal;
                if (!core.Profile.IsInPrivateModeEnabled) throw new InvalidDataException("NATIVE_CONTROL_INPRIVATE_REQUIRED");
                ConfigurePolicy();
                initialized = true;
                Inventory();
                if (closeRequested) return;
                stage = "load_owned_page"; navigationRequested = true; controller.IsVisible = true; core.Navigate(pageUri);
                await pageReady.Task.WaitAsync(TimeSpan.FromSeconds(15));
                if (closeRequested) return;
                baselineEvidence = await ReadEvidenceAsync();
                if (closeRequested) return;
                if (baselineEvidence.Value.GetProperty("value_length").GetInt32() != 0 ||
                    new[] { "composition_start", "composition_update", "composition_end", "input" }.Any(k => baselineEvidence.Value.GetProperty(k).GetInt32() != 0))
                    throw new InvalidDataException("NATIVE_CONTROL_EMPTY_BASELINE_REQUIRED");
                pageSucceeded = true; stage = "awaiting_physical_ime";
                Text = "AutumnOS 原生 IME 对照 · 点击文本框，用输入法输入“你好”，再关闭窗口";
                TrySave("awaiting_physical_ime");
            }
            catch (Exception error) { Fail(stage, error); RequestClose(stage, true); }
            finally { initializationReturned = true; }
        }

        private void ConfigurePolicy()
        {
            CoreWebView2 web = core ?? throw new InvalidDataException("NATIVE_CONTROL_CORE_REQUIRED");
            var settings = web.Settings;
            settings.AreHostObjectsAllowed = false; settings.AreDevToolsEnabled = false;
            settings.AreDefaultContextMenusEnabled = false; settings.AreDefaultScriptDialogsEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false; settings.IsPasswordAutosaveEnabled = false;
            settings.IsGeneralAutofillEnabled = false; settings.IsStatusBarEnabled = false; settings.IsZoomControlEnabled = false;
            controller!.AcceleratorKeyPressed += (_, args) =>
            {
                bool ctrl = (GetKeyState(0x11) & 0x8000) != 0, shift = (GetKeyState(0x10) & 0x8000) != 0;
                bool win = (GetKeyState(0x5b) & 0x8000) != 0 || (GetKeyState(0x5c) & 0x8000) != 0;
                uint key = args.VirtualKey;
                if (ctrl && (key is 0x43 or 0x58 or 0x56 or 0x2d) || shift && (key is 0x2d or 0x2e) || win && key == 0x56)
                { args.Handled = true; blockedClipboardShortcuts = Math.Min(1000000, blockedClipboardShortcuts + 1); }
            };
            web.PermissionRequested += (_, args) => { args.State = CoreWebView2PermissionState.Deny; args.Handled = true; };
            web.NewWindowRequested += (_, args) => args.Handled = true;
            web.DownloadStarting += (_, args) => args.Cancel = true;
            web.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
            web.FrameNavigationStarting += (_, args) => { args.Cancel = true; RequestClose("frame_navigation_rejected", true); };
            web.NavigationStarting += (_, args) =>
            {
                navigationStarts = checked(navigationStarts + 1);
                if (!navigationRequested || args.Uri != pageUri || navigationStarts != 1)
                { args.Cancel = true; RequestClose("unexpected_navigation", true); }
            };
            web.ProcessFailed += (_, _) => RequestClose("browser_process_failed", true);
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            web.WebResourceRequested += Serve;
            web.WebMessageReceived += (_, args) =>
            {
                try
                {
                    if (args.Source != pageUri || args.WebMessageAsJson.Length > 1024) throw new InvalidDataException("NATIVE_CONTROL_MESSAGE_BOUNDS");
                    using JsonDocument doc = JsonDocument.Parse(args.WebMessageAsJson); JsonElement value = doc.RootElement;
                    if (value.GetProperty("nonce").GetString() != nonce || value.GetProperty("phase").GetString() != "page_ready" ||
                        value.GetProperty("wasm").GetInt32() != 42 || value.GetProperty("raf").GetInt32() < 60)
                        throw new InvalidDataException("NATIVE_CONTROL_REAL_PAGE_READY_REQUIRED");
                    pageReady.TrySetResult(true);
                }
                catch (Exception error)
                { pageReady.TrySetException(new InvalidDataException("NATIVE_CONTROL_PAGE_MESSAGE_FAILED")); Fail("owned_page_message", error); RequestClose("owned_page_message", true); }
            };
        }

        private void Serve(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
        {
            try
            {
                byte[] bytes = []; string mime = "text/plain; charset=utf-8"; int status = 403;
                if (!closeRequested && args.Request.Method == "GET" && args.Request.Uri == pageUri && args.ResourceContext == CoreWebView2WebResourceContext.Document)
                {
                    bytes = Encoding.UTF8.GetBytes("<!doctype html><html data-nonce=\"" + nonce + "\"><meta charset=utf-8><title>AutumnOS owned presentation</title><link rel=icon href=/owned-icon.svg><link rel=stylesheet href=/probe.css><canvas id=owned-canvas width=800 height=320></canvas><p id=instruction>仅输入合成测试文字：你好</p><textarea id=owned-text maxlength=1024 aria-label=Owned-test-input></textarea><script src=/probe.js defer></script></html>");
                    mime = "text/html; charset=utf-8"; status = 200;
                }
                else if (!closeRequested && args.Request.Method == "GET" && args.Request.Uri == origin + "/probe.js" && args.ResourceContext == CoreWebView2WebResourceContext.Script)
                {
                    string name = Assembly.GetExecutingAssembly().GetManifestResourceNames().Single(n => n.EndsWith(".OwnedPage.js", StringComparison.Ordinal));
                    using Stream script = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)!;
                    using MemoryStream copy = new(); script.CopyTo(copy);
                    if (copy.Length is < 1 or > 65536) throw new InvalidDataException("NATIVE_CONTROL_EMBEDDED_SCRIPT_LIMIT");
                    bytes = copy.ToArray(); mime = "application/javascript; charset=utf-8"; status = 200;
                }
                else if (!closeRequested && args.Request.Method == "GET" && args.Request.Uri == origin + "/probe.css" && args.ResourceContext == CoreWebView2WebResourceContext.Stylesheet)
                {
                    bytes = Encoding.UTF8.GetBytes("html,body{margin:0;width:800px;height:600px;overflow:hidden;background:#152536;color:#f0eee7}#owned-canvas{position:absolute;left:0;top:0;width:800px;height:320px}#instruction{position:absolute;left:32px;top:315px;font:20px sans-serif}#owned-text{box-sizing:border-box;position:absolute;left:32px;top:360px;width:736px;height:120px;font:24px sans-serif;resize:none}");
                    mime = "text/css; charset=utf-8"; status = 200;
                }
                else if (!closeRequested && args.Request.Method == "GET" && args.Request.Uri == origin + "/owned-icon.svg")
                { bytes = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 16 16\"><path fill=\"#eb9734\" d=\"M2 2h12v12H2z\"/></svg>"); mime = "image/svg+xml"; status = 200; }
                if (resourceStreams.Count >= 64) throw new InvalidDataException("NATIVE_CONTROL_RESOURCE_LIMIT");
                var data = new MemoryStream(bytes, false); resourceStreams.Add(data);
                args.Response = environment!.CreateWebResourceResponse(data, status, status == 200 ? "OK" : "Forbidden", FrozenPolicy.ResponseHeaders + "\r\nContent-Type: " + mime);
                if (status != 200 && !closeRequested) RequestClose("resource_outside_owned_package", true);
            }
            catch (Exception error) { Fail("owned_resource", error); RequestClose("owned_resource", true); }
        }

        private async Task<JsonElement> ReadEvidenceAsync()
        {
            RequireContext();
            if (core is null || controllerDisposed || core.Source != pageUri || navigationStarts != 1)
                throw new InvalidDataException("NATIVE_CONTROL_CURRENT_OWNED_DOCUMENT_REQUIRED");
            // The sole CDP callsite is this fixed read-only expression. It
            // neither synthesizes events nor changes focus, selection or text.
            string json = await core.CallDevToolsProtocolMethodAsync("Runtime.evaluate", JsonSerializer.Serialize(new
            { expression = "window.collectPresentationEvidence()", awaitPromise = true, returnByValue = true })).WaitAsync(TimeSpan.FromSeconds(5));
            if (json.Length > 8192) throw new InvalidDataException("NATIVE_CONTROL_EVIDENCE_LIMIT");
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("exceptionDetails", out _)) throw new InvalidDataException("NATIVE_CONTROL_READONLY_EVIDENCE_FAILED");
            JsonElement value = doc.RootElement.GetProperty("result").GetProperty("value");
            if (value.GetProperty("nonce").GetString() != nonce || value.GetProperty("width").GetInt32() != 800 || value.GetProperty("height").GetInt32() != 600 ||
                value.GetProperty("device_pixel_ratio").GetDouble() != 1 || value.GetProperty("wasm").GetInt32() != 42 || value.GetProperty("raf").GetInt32() < 60 ||
                !value.GetProperty("ready").GetBoolean() || value.GetProperty("failed").GetBoolean())
                throw new InvalidDataException("NATIVE_CONTROL_REAL_WASM_VIEWPORT_RAF_REQUIRED");
            foreach (string key in new[] { "composition_start", "composition_update", "composition_end", "input", "trusted_composition_start", "trusted_composition_update", "trusted_composition_end", "trusted_input" })
                if (value.GetProperty(key).GetInt32() is < 0 or > 1024) throw new InvalidDataException("NATIVE_CONTROL_PHASE_LIMIT");
            foreach (string key in new[] { "value_sha256", "phase_digest_sha256", "document_instance_sha256" })
            {
                string hash = value.GetProperty(key).GetString() ?? "";
                if (hash.Length != 64 || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                    throw new InvalidDataException("NATIVE_CONTROL_DOM_HASH_REQUIRED");
            }
            int length = value.GetProperty("value_length").GetInt32(), start = value.GetProperty("selection_start").GetInt32(), end = value.GetProperty("selection_end").GetInt32();
            if (length is < 0 or > 1024 || start < 0 || end < start || end > length) throw new InvalidDataException("NATIVE_CONTROL_TEXT_AND_SELECTION_BOUNDS");
            foreach (string key in new[] { "pointer_down", "key_down" })
                if (value.GetProperty(key).GetInt32() is < 0 or > 1000000) throw new InvalidDataException("NATIVE_CONTROL_INPUT_COUNTER_LIMIT");
            foreach (string key in new[] { "composing", "expected_hello", "active_owned_text", "document_has_focus" }) _ = value.GetProperty(key).GetBoolean();
            foreach (string phase in new[] { "composition_update", "composition_end" })
            {
                JsonElement dataLength = value.GetProperty(phase + "_data_length"), dataHash = value.GetProperty(phase + "_data_sha256");
                bool missingLength = dataLength.ValueKind == JsonValueKind.Null, missingHash = dataHash.ValueKind == JsonValueKind.Null;
                int eventCount = value.GetProperty(phase).GetInt32();
                if (missingLength != missingHash || missingLength != (eventCount == 0))
                    throw new InvalidDataException("NATIVE_CONTROL_COMPOSITION_DATA_PAIR_REQUIRED");
                if (missingLength) continue;
                string hash = dataHash.GetString() ?? "";
                if (dataLength.GetInt32() is < 0 or > 1024 || hash.Length != 64 ||
                    hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                    throw new InvalidDataException("NATIVE_CONTROL_COMPOSITION_DATA_BOUNDS");
            }
            // Only explicitly allowlisted scalars are persisted. A future
            // change to the page cannot smuggle event.data or text into JSON.
            var safe = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (string key in new[] { "nonce", "width", "height", "device_pixel_ratio", "wasm", "raf", "ready", "failed", "composition_start", "composition_update", "composition_end", "input", "trusted_composition_start", "trusted_composition_update", "trusted_composition_end", "trusted_input", "pointer_down", "key_down", "composing", "value_length", "value_sha256", "expected_hello", "active_owned_text", "document_has_focus", "selection_start", "selection_end", "phase_digest_sha256", "document_instance_sha256", "composition_update_data_length", "composition_update_data_sha256", "composition_end_data_length", "composition_end_data_sha256" })
                safe.Add(key, value.GetProperty(key).Clone());
            return JsonSerializer.SerializeToElement(safe);
        }

        private void RequestClose(string reason, bool failure)
        {
            if (failure) Fail(reason);
            if (closeRequested) return;
            closeRequested = true; closeAt = clock.ElapsedMilliseconds;
            Text = "AutumnOS 原生 IME 对照 · 正在保存 DOM 并等待原浏览器自然退出";
            _ = SaveFinalSnapshotAsync();
        }

        private async Task SaveFinalSnapshotAsync()
        {
            try
            {
                if (pageSucceeded)
                {
                    finalEvidence = await ReadEvidenceAsync();
                    if (baselineEvidence is not JsonElement baseline ||
                        baseline.GetProperty("document_instance_sha256").GetString() != finalEvidence.Value.GetProperty("document_instance_sha256").GetString())
                        throw new InvalidDataException("NATIVE_CONTROL_SAME_DOCUMENT_REQUIRED");
                    finalDomObserved = true;
                }
            }
            catch (Exception error) { Fail("close_final_dom", error); }
            finally { closeSnapshotReturned = true; TrySave("closing_native_control"); }
        }

        private void Inventory()
        {
            if (environment is null) return;
            try
            {
                var processes = environment.GetProcessInfos();
                environmentProcessCount = processes.Count;
                if (processes.Count > 128) throw new InvalidDataException("NATIVE_CONTROL_SDK_PROCESS_LIMIT");
                foreach (var process in processes)
                {
                    if (audit.Held.Count >= 128 && !audit.Held.Any(p => p.pid == process.ProcessId && !p.original_handle_signaled))
                        throw new InvalidDataException("NATIVE_CONTROL_HELD_PROCESS_LIMIT");
                    audit.Observe(checked((uint)process.ProcessId), process.Kind.ToString(), "SDK_process_inventory_original_handle");
                }
                audit.RefreshExits();
            }
            catch (Exception error) { inventoryGap = true; environmentProcessCount = -1; Fail("sdk_process_inventory", error); }
        }

        private void Tick()
        {
            try
            {
                RequireContext();
                if (clock.ElapsedMilliseconds - lastInventory >= 500) { lastInventory = clock.ElapsedMilliseconds; Inventory(); }
                if (!initializationReturned && clock.ElapsedMilliseconds >= 25000) RequestClose("initialization_budget_unproven", true);
                if (!closeRequested || !closeSnapshotReturned) return;
                if (!initializationReturned) { SavePendingIfDue(); return; }
                if (!controllerDisposed)
                {
                    try { controller?.Close(); controller = null; core = null; controllerDisposed = true; }
                    catch (Exception error) { Fail("controller_close", error); }
                }
                Inventory(); audit.RefreshExits();
                // Keep this exact object returned when CoreWebView2 first
                // bound its browser PID. Later PID discovery never replaces it.
                originalBrowserHandleSignaled = originalBrowser is { original_handle_signaled: true, exit_code: 0, creation_filetime: > 0 };
                cleanupConfirmed = initializationReturned && controllerDisposed && environment is not null && browserPid > 0 &&
                    normalBrowserExit && environmentProcessCount == 0 && originalBrowserHandleSignaled && audit.Held.Count > 0 &&
                    audit.AllSignaled && !audit.ObservationGap && !inventoryGap;
                if (cleanupConfirmed)
                {
                    foreach (Stream stream in resourceStreams) stream.Dispose(); resourceStreams.Clear();
                    allowClose = true; timer.Stop(); Close(); return;
                }
                SavePendingIfDue();
            }
            catch (Exception error) { Fail("native_control_tick", error); RequestClose("native_control_tick", true); }
        }

        private void SavePendingIfDue()
        {
            if (clock.ElapsedMilliseconds - closeAt < 12000 || clock.ElapsedMilliseconds - lastPendingReport < 1000) return;
            lastPendingReport = clock.ElapsedMilliseconds;
            Text = "AutumnOS 原生 IME 对照 · 清理待证，请保留此窗口与本次产物";
            // Timeout changes only the report. It never kills a process,
            // marks cleanup true, deletes UDF, or exits the message loop.
            TrySave("pending_cleanup");
        }

        private bool PhysicalCompositionObserved()
        {
            if (!pageSucceeded || !finalDomObserved || finalEvidence is not JsonElement value || navigationStarts != 1) return false;
            return value.GetProperty("composition_start").GetInt32() > 0 && value.GetProperty("composition_update").GetInt32() > 0 &&
                value.GetProperty("composition_end").GetInt32() > 0 && value.GetProperty("trusted_composition_start").GetInt32() > 0 &&
                value.GetProperty("trusted_composition_update").GetInt32() > 0 && value.GetProperty("trusted_input").GetInt32() > 0 &&
                !value.GetProperty("composing").GetBoolean() && value.GetProperty("expected_hello").GetBoolean() &&
                value.GetProperty("value_length").GetInt32() == 2 && value.GetProperty("value_sha256").GetString() == OwnedFiles.HashText("你好") &&
                value.GetProperty("composition_end_data_length").GetInt32() == 2 &&
                value.GetProperty("composition_end_data_sha256").GetString() == OwnedFiles.HashText("你好");
            // Deliberately no trusted_composition_end threshold: this control
            // measures the native engine's actual bit, including false.
        }

        private void Fail(string errorStage, Exception? error = null)
        {
            failed = true; if (primaryErrorStage == "none") primaryErrorStage = errorStage;
            if (errors.Count < 32 && !errors.Any(e => JsonSerializer.Serialize(e).Contains("\"" + errorStage + "\"", StringComparison.Ordinal)))
                errors.Add(new { stage = errorStage, code = error is null ? null : Program.FailureCode(error) });
        }

        private object Report(string status) => new
        {
            schema_version = 1, status, native_control = true, input.source_snapshot_id, input.manifest_sha256, input.runtime_version,
            runtime_executable_sha256 = runtimeExecutableSha, control_executable_sha256 = controlExecutableSha,
            browser_version = browserVersion, original_control_pid = Environment.ProcessId, control_creation_filetime = controlCreation,
            nonce, station_name = "WinSta0", desktop_name = "Default", browser_pid = browserPid,
            cdp_input_calls = 0, netonly_logon_calls = 0, new_appcontainer_profiles = 0, new_window_stations = 0, new_desktops = 0, clipboard_api_calls = 0,
            readonly_evidence_method = "Runtime.evaluate:window.collectPresentationEvidence()", blocked_clipboard_shortcuts = blockedClipboardShortcuts,
            environment_creation_started = environmentCreationStarted,
            initialization_succeeded = initialized, page_succeeded = pageSucceeded, navigation_start_count = navigationStarts,
            baseline_document_instance_sha256 = baselineEvidence?.GetProperty("document_instance_sha256").GetString(),
            baseline_dom_evidence = baselineEvidence, dom_evidence = finalEvidence, final_dom_observed = finalDomObserved,
            native_composition_observed = PhysicalCompositionObserved(),
            normal_browser_exit = normalBrowserExit, normal_browser_exit_pid = normalBrowserExit ? (uint?)browserPid : null,
            final_environment_process_count = environmentProcessCount, original_browser_handle_signaled = originalBrowserHandleSignaled,
            all_observed_process_handles_signaled = audit.Held.Count > 0 && audit.AllSignaled,
            process_observation_gap = audit.ObservationGap || inventoryGap, observed_processes = audit.Held, process_failures = audit.Failures,
            observation_scope = "SDK_enumerated_PIDs_and_held_OS_handles_only_not_all_OS_isolation",
            controller_disposed = controllerDisposed, window_closed_naturally = windowClosedNaturally, cleanup_confirmed = cleanupConfirmed,
            udf_retained = true, udf_binding_sha256 = OwnedFiles.HashText(Path.GetFullPath(udf).ToUpperInvariant()),
            case_directory_leaf = "native-ime-control-case-" + nonce, windows_profile_cleanup = "not_applicable_no_profile_created",
            g1_isolation_acceptance = "not_claimed_native_control_only", error_stage = failed ? primaryErrorStage : null, errors,
            report_write_failed = reportWriteFailed
        };

        private void TrySave(string status)
        {
            try { WriteReport(report, Report(status)); }
            catch (Exception) { reportWriteFailed = true; failed = true; if (primaryErrorStage == "none") primaryErrorStage = "native_control_report_write"; }
        }

        internal void SaveAfterWindowClosed()
        {
            bool completed = windowClosedNaturally && cleanupConfirmed && PhysicalCompositionObserved() && !failed && !reportWriteFailed;
            ExitCode = completed ? 0 : 1;
            TrySave(completed ? "completed_native_ime_control" : "failed_native_ime_control");
            if (reportWriteFailed) ExitCode = 1;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                timer.Dispose(); foreach (Stream stream in resourceStreams) stream.Dispose(); resourceStreams.Clear();
                audit.ReleaseSignaledHandles();
            }
            base.Dispose(disposing);
        }
    }

    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll")] private static extern short GetKeyState(int virtualKey);
}
