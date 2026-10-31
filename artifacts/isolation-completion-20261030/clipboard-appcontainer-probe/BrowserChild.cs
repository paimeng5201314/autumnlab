using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutumnOS.ClipboardAppContainerProbe;

internal sealed class BrowserChild : ProbeForm
{
    private readonly string runtime, udf, operation, origin, pageUri;
    private readonly WebView2 view = new() { Dock = DockStyle.Fill };
    private readonly List<Stream> streams = [];
    private readonly Dictionary<int, Ledger> ledger = [];
    private readonly Dictionary<uint, CoreWebView2BrowserProcessExitKind> exits = [];
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> messages = [];
    private readonly HashSet<int> pasteSamples = [];
    private readonly HashSet<int> requestedPasteSamples = [];
    private readonly List<PasteFact> pasteEvents = [];
    private bool pasteFinalReading, pasteFinalSnapshotObserved, pasteFinalReadFinished;
    private int pasteFinalSnapshotCount = -1, pasteFinalReadAttempts;
    private string? pasteFinalValueHash;
    private bool? pasteFinalValueEmpty;
    private CoreWebView2Environment? environment;
    private uint browserPid;
    private bool initialized, initializationReturned, loaded, pageReady, operationStarted, operationSucceeded;
    private bool failed, closing, disposedController, parentClose;
    private int processCount = -1;
    private string stage = "starting", closeReason = "not_requested";
    private long closeAt, lastInventory = -1000;
    internal BrowserChild(string runtime, string udf, string station, string desktop, string operation) : base(station, desktop)
    {
        if (!Path.IsPathFullyQualified(runtime) || !Path.IsPathFullyQualified(udf)) throw new InvalidDataException("ABSOLUTE_SCOPED_DIRECTORIES_REQUIRED");
        this.runtime = Path.GetFullPath(runtime); this.udf = Path.GetFullPath(udf); this.operation = operation;
        if (!File.Exists(Path.Combine(runtime, "msedgewebview2.exe")) || !Directory.Exists(udf)) throw new InvalidDataException("EXISTING_RUNTIME_UDF_REQUIRED");
        foreach (string key in Environment.GetEnvironmentVariables().Keys)
            if (key.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("WEBVIEW2_ENVIRONMENT_OVERRIDE_BLOCKED");
        origin = "https://owned-clipboard-" + Program.Nonce + ".autumn.invalid"; pageUri = origin + "/index.html";
        foreach (string key in new[] { "copy", "cut", "paste_ready", "paste_keydown" }) messages[key] = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Controls.Add(view);
    }
    protected override void Start() => _ = InitializeAsync();
    private async Task InitializeAsync()
    {
        try
        {
            RequireBinding(); stage = "create_environment";
            Program.Emit("initialization_begin", new { operation, runtime = FrozenPolicy.RequiredBrowserVersion, sdk = "1.0.4258.31", policy_id = FrozenPolicy.PolicyId });
            var options = new CoreWebView2EnvironmentOptions(FrozenPolicy.BrowserArguments)
            { ExclusiveUserDataFolderAccess = true, IsCustomCrashReportingEnabled = true };
            environment = await CoreWebView2Environment.CreateAsync(runtime, udf, options);
            if (Finished) return;
            environment.BrowserProcessExited += (_, args) =>
            {
                if (Finished) return; exits[args.BrowserProcessId] = args.BrowserProcessExitKind;
                Program.Emit("browser_group_exit", new { pid = args.BrowserProcessId, kind = args.BrowserProcessExitKind.ToString() });
            };
            environment.ProcessInfosChanged += (_, _) => Inventory("changed");
            Inventory("environment_created");
            if (environment.BrowserVersionString != FrozenPolicy.RequiredBrowserVersion ||
                !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.UserDataFolder)), Path.TrimEndingDirectorySeparator(udf), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("FIXED_RUNTIME_EXACT_UDF_REQUIRED");
            if (closing) return;
            stage = "create_controller";
            var controller = environment.CreateCoreWebView2ControllerOptions(); controller.IsInPrivateModeEnabled = true;
            await view.EnsureCoreWebView2Async(environment, controller);
            if (Finished) return;
            RequireBinding(); var core = view.CoreWebView2; browserPid = core.BrowserProcessId;
            if (!core.Profile.IsInPrivateModeEnabled) throw new InvalidDataException("INPRIVATE_REQUIRED");
            Inventory("controller_created"); if (closing) return;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false; core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false; core.Settings.IsStatusBarEnabled = false;
            core.PermissionRequested += (_, args) => { args.State = CoreWebView2PermissionState.Deny; args.Handled = true; };
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
            core.FrameNavigationStarting += (_, args) => args.Cancel = true;
            core.NavigationStarting += (_, args) => { if (!loaded || args.Uri != pageUri) { args.Cancel = true; Failure("unexpected_navigation"); } };
            core.ProcessFailed += (_, args) => { Program.Emit("process_failed", new { kind = args.ProcessFailedKind.ToString() }); Failure("process_failed"); };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += Serve; core.WebMessageReceived += Message;
            initialized = true; stage = "controller_ready";
            Program.Emit("controller_ready", new { browser_pid = browserPid, browser_version = environment.BrowserVersionString,
                policy_id = FrozenPolicy.PolicyId, udf_binding_sha256 = Program.PathHash(environment.UserDataFolder),
                udf_matches_requested = true, in_private = true, operation });
        }
        catch (Exception exception) { if (!Finished) { Program.Emit("initialization_failed", new { stage, error = Program.Error(exception) }); Failure("initialization_failed"); } }
        finally { initializationReturned = true; }
    }
    protected override void Command(string command, int? index)
    {
        if (closing) return;
        if (command == "close") { parentClose = true; RequestClose("parent_close", false); }
        else if (command == "load_owned_page" && initialized && !loaded)
        { loaded = true; stage = "page_loading"; view.CoreWebView2.Navigate(pageUri); }
        else if (command == "perform" && pageReady && !operationStarted)
        { operationStarted = true; _ = PerformAsync(); }
        else if (command == "sample_paste" && operation == "paste" && operationSucceeded && index is >= 1 and <= 10 && requestedPasteSamples.Add(index.Value))
            _ = ReportPasteAsync(index.Value);
        else throw new InvalidDataException("BROWSER_COMMAND_STATE_INVALID");
    }
    private async Task PerformAsync()
    {
        try
        {
            RequireBinding(); RequireDocument(); stage = "trusted_operation";
            Activate(); view.Focus(); await Task.Delay(100); RequireBinding(); RequireDocument();
            if (operation is "copy" or "cut")
            {
                await ClickAsync(operation);
                JsonElement result = await messages[operation].Task.WaitAsync(TimeSpan.FromSeconds(8));
                bool activation = Bool(result, "attempted") && Bool(result, "trustedClick") && Bool(result, "userActivation") &&
                    Bool(result, "documentFocused") && Bool(result, "selectionBeforeMatches");
                operationSucceeded = activation;
                Program.Emit("operation_complete", new { operation, activation_verified = activation,
                    exec_command_result = Bool(result, "execCommandResult"), cut_selection_empty = Bool(result, "selectionAfterEmpty") });
                if (!activation) Failure("trusted_copy_cut_activation_failed");
            }
            else
            {
                await ClickAsync("prepare");
                JsonElement prepared = await messages["paste_ready"].Task.WaitAsync(TimeSpan.FromSeconds(8));
                bool prepare = Bool(prepared, "trustedClick") && Bool(prepared, "userActivation") && Bool(prepared, "documentFocused") && prepared.GetProperty("activeElement").GetString() == "paste";
                await KeyAsync("rawKeyDown", "Control", "ControlLeft", 17, 2);
                await KeyAsync("rawKeyDown", "v", "KeyV", 86, 2);
                await KeyAsync("keyUp", "v", "KeyV", 86, 2);
                await KeyAsync("keyUp", "Control", "ControlLeft", 17, 0);
                JsonElement key = await messages["paste_keydown"].Task.WaitAsync(TimeSpan.FromSeconds(8));
                bool keyboard = Bool(key, "trusted") && Bool(key, "control") && Bool(key, "userActivation") && Bool(key, "documentFocused") && key.GetProperty("activeElement").GetString() == "paste";
                operationSucceeded = prepare && keyboard;
                Program.Emit("operation_complete", new { operation, activation_verified = operationSucceeded,
                    keyboard_verified = keyboard, prepare_verified = prepare });
                if (!operationSucceeded) Failure("trusted_paste_activation_failed");
                else { requestedPasteSamples.Add(0); await ReportPasteAsync(0); }
            }
        }
        catch (Exception exception) { if (!Finished) { Program.Emit("operation_failed", new { stage, error = Program.Error(exception) }); Failure("operation_failed"); } }
    }
    private static bool Bool(JsonElement value, string key) => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.True;
    private void RequireDocument(bool allowClosing = false)
    { if ((!allowClosing && closing) || disposedController || !initialized || view.CoreWebView2.Source != pageUri) throw new InvalidDataException("TRUSTED_DOCUMENT_REQUIRED"); }
    private async Task ClickAsync(string id)
    {
        RequireBinding(); RequireDocument();
        using JsonDocument result = JsonDocument.Parse(await view.CoreWebView2.ExecuteScriptAsync("(()=>{const r=document.getElementById(" + JsonSerializer.Serialize(id) + ").getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()"));
        double x = result.RootElement.GetProperty("x").GetDouble(), y = result.RootElement.GetProperty("y").GetDouble();
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= view.Width || y >= view.Height) throw new InvalidDataException("OWNED_TARGET_GEOMETRY_INVALID");
        await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mousePressed", x, y, button = "left", buttons = 1, clickCount = 1 }));
        await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent", JsonSerializer.Serialize(new { type = "mouseReleased", x, y, button = "left", buttons = 0, clickCount = 1 }));
    }
    private Task<string> KeyAsync(string type, string key, string code, int virtualKey, int modifiers)
    {
        RequireBinding(); RequireDocument();
        return view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", JsonSerializer.Serialize(new { type, key, code, windowsVirtualKeyCode = virtualKey, nativeVirtualKeyCode = virtualKey, modifiers }));
    }
    private async Task ReportPasteAsync(int index)
    {
        try { RequireBinding(); RequireDocument(); await view.CoreWebView2.ExecuteScriptAsync("window.reportPasteResult(" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")"); }
        catch (Exception exception) { if (!Finished) { Program.Emit("paste_sample_failed", Program.Error(exception)); Failure("paste_sample_failed"); } }
    }
    private void Message(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (Finished) return;
        try
        {
            string json = args.WebMessageAsJson;
            if (args.Source != pageUri || json.Length > 16384) throw new InvalidDataException("OWNED_MESSAGE_BOUNDARY");
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 5 });
            JsonElement value = document.RootElement;
            if (value.GetProperty("nonce").GetString() != Program.Nonce || value.GetProperty("operation").GetString() != operation) throw new InvalidDataException("OWNED_MESSAGE_BINDING");
            string phase = value.GetProperty("phase").GetString() ?? "";
            if (phase == "paste_event")
            {
                AppendPasteEvent(value, "native_web_message");
                if (operation != "paste" || !operationStarted) Failure("paste_event_without_trusted_operation");
                RequireBinding();
                if (disposedController) { pasteFinalSnapshotObserved = false; Failure("paste_event_after_controller_disposed"); }
                else RequireDocument(allowClosing: true);
                return;
            }
            if (closing) return;
            RequireBinding(); RequireDocument();
            if (phase == "ready")
            {
                if (pageReady || !Bool(value, "loaded")) throw new InvalidDataException("DUPLICATE_PAGE_READY");
                pageReady = true; Program.Emit("page_ready", new { operation, loaded = true, browser_pid = browserPid });
            }
            else if (messages.TryGetValue(phase, out var message))
            { if (!operationStarted || !message.TrySetResult(value.Clone())) throw new InvalidDataException("UNEXPECTED_DUPLICATE_OPERATION_MESSAGE"); }
            else if (phase == "paste_result")
            {
                int index = value.GetProperty("sample_index").GetInt32();
                if (operation != "paste" || !operationSucceeded || !requestedPasteSamples.Contains(index) || !pasteSamples.Add(index) || !Bool(value, "complete")) throw new InvalidDataException("PASTE_RESULT_BINDING");
                string text = value.GetProperty("value").GetString() ?? "";
                JsonElement events = value.GetProperty("pasteEvents");
                if (text.Length > 4096 || events.GetArrayLength() > 64) throw new InvalidDataException("PASTE_TEXT_LIMIT");
                var resultEvents = events.EnumerateArray().Select(item =>
                {
                    string content = item.GetProperty("text").GetString() ?? "";
                    if (content.Length > 4096) throw new InvalidDataException("PASTE_EVENT_LIMIT");
                    return new { trusted = Bool(item, "trusted"), text_sha256 = Program.Hash(content), text_empty = content.Length == 0 };
                }).ToArray();
                Program.Emit("paste_sample", new { sample_index = index, value_sha256 = Program.Hash(text), value_empty = text.Length == 0,
                    complete = true, paste_events = resultEvents, document_focused = Bool(value, "documentFocused"),
                    paste_target_focused = value.GetProperty("activeElement").GetString() == "paste" });
            }
            else throw new InvalidDataException("UNKNOWN_PAGE_PHASE");
        }
        catch (Exception exception) { Program.Emit("owned_message_failed", Program.Error(exception)); Failure("owned_message_failed"); }
    }
    private void AppendPasteEvent(JsonElement value, string source)
    {
        int index = value.GetProperty("event_index").GetInt32();
        string text = value.GetProperty("text").GetString() ?? throw new InvalidDataException("PASTE_EVENT_TEXT_REQUIRED");
        JsonElement trust = value.GetProperty("trusted");
        if (text.Length > 4096 || trust.ValueKind is not (JsonValueKind.True or JsonValueKind.False) || index is < 1 or > 64)
            throw new InvalidDataException("PASTE_EVENT_LIMIT_OR_TYPE");
        PasteFact fact = new(index, trust.GetBoolean(), text.Length == 0, Program.Hash(text));
        if (index <= pasteEvents.Count)
        {
            // A final snapshot can recover a real event whose native message
            // is still queued. An exact later delivery is deduplicated; a
            // changed/replayed fact is a permanent capture failure.
            if (pasteEvents[index - 1] != fact) throw new InvalidDataException("PASTE_EVENT_REPLAY_CONFLICT");
            return;
        }
        if (index != pasteEvents.Count + 1) throw new InvalidDataException("PASTE_EVENT_SEQUENCE_GAP");
        pasteEvents.Add(fact); pasteFinalSnapshotObserved = false;
        Program.Emit("paste_event_data", new { event_index = index, trusted = fact.Trusted,
            text_sha256 = fact.Hash, text_empty = fact.Empty, source, observed_while_closing = closing,
            observed_after_controller_disposed = disposedController });
    }
    private string PasteDigest() => Program.Hash(string.Concat(pasteEvents.Select(item =>
        item.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (item.Trusted ? "1" : "0") + "|" +
        (item.Empty ? "1" : "0") + "|" + item.Hash + "\n")));
    private async Task ReadFinalPasteEvidenceAsync()
    {
        pasteFinalReading = true; pasteFinalReadFinished = false; pasteFinalReadAttempts++;
        try
        {
            RequireBinding(); RequireDocument(allowClosing: true);
            string json = await view.CoreWebView2.ExecuteScriptAsync("window.collectPasteEvidence()").WaitAsync(TimeSpan.FromSeconds(3));
            RequireBinding(); RequireDocument(allowClosing: true);
            if (json.Length > 65536) throw new InvalidDataException("FINAL_PASTE_SNAPSHOT_SIZE");
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 5 });
            JsonElement value = document.RootElement;
            JsonElement entries = value.GetProperty("paste_events");
            int count = value.GetProperty("event_count").GetInt32();
            if (value.GetProperty("capture_version").GetInt32() != 1 || value.GetProperty("nonce").GetString() != Program.Nonce ||
                value.GetProperty("operation").GetString() != operation || count is < 0 or > 64 || entries.GetArrayLength() != count)
                throw new InvalidDataException("FINAL_PASTE_SNAPSHOT_BINDING");
            string finalValue = value.GetProperty("value").GetString() ?? throw new InvalidDataException("FINAL_PASTE_DOM_VALUE_REQUIRED");
            if (finalValue.Length > 4096) throw new InvalidDataException("FINAL_PASTE_DOM_VALUE_LIMIT");
            pasteFinalValueHash = Program.Hash(finalValue); pasteFinalValueEmpty = finalValue.Length == 0;
            foreach (JsonElement entry in entries.EnumerateArray()) AppendPasteEvent(entry, "final_snapshot_recovery");
            pasteFinalSnapshotCount = count;
            pasteFinalSnapshotObserved = count == pasteEvents.Count;
            Program.Emit("paste_final_snapshot", new { event_count = count, observed_event_count = pasteEvents.Count,
                matches_accumulated = pasteFinalSnapshotObserved, digest_sha256 = PasteDigest(), read_attempt = pasteFinalReadAttempts,
                value_sha256 = pasteFinalValueHash, value_empty = pasteFinalValueEmpty });
        }
        catch (Exception exception)
        {
            failed = true; pasteFinalSnapshotObserved = false;
            Program.Emit("paste_final_snapshot_failed", Program.Error(exception));
        }
        finally { pasteFinalReading = false; pasteFinalReadFinished = true; }
    }
    private void Serve(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (Finished || environment is null) return;
        byte[] bytes = []; string mime = "text/plain"; int status = 403;
        try
        {
            if (!closing && args.Request.Method == "GET" && args.Request.Uri == pageUri)
            {
                bytes = Encoding.UTF8.GetBytes("<!doctype html><html data-nonce=\"" + Program.Nonce + "\" data-operation=\"" + operation + "\"><meta charset=utf-8><title>Owned clipboard probe</title><link rel=icon type=image/svg+xml href=/owned-icon.svg><button id=copy>Copy owned marker</button><button id=cut>Cut owned marker</button><button id=prepare>Prepare paste target</button><textarea id=selected></textarea><textarea id=paste></textarea><script src=/probe.js defer></script></html>");
                mime = "text/html; charset=utf-8"; status = 200;
            }
            else if (!closing && args.Request.Method == "GET" && args.Request.Uri == origin + "/probe.js")
            {
                using Stream script = Assembly.GetExecutingAssembly().GetManifestResourceStream("AutumnOS.ClipboardAppContainerProbe.OwnedPage.js") ?? throw new InvalidDataException("OWNED_SCRIPT_MISSING");
                using MemoryStream data = new(); script.CopyTo(data); bytes = data.ToArray(); mime = "application/javascript; charset=utf-8"; status = 200;
            }
            else if (!closing && args.Request.Method == "GET" && args.Request.Uri == origin + "/owned-icon.svg")
            { bytes = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 16 16\"><path fill=\"#ad5e32\" d=\"M2 2h12v12H2z\"/></svg>"); mime = "image/svg+xml"; status = 200; }
            MemoryStream stream = new(bytes, writable: false); streams.Add(stream);
            args.Response = environment.CreateWebResourceResponse(stream, status, status == 200 ? "OK" : "Forbidden", FrozenPolicy.ResponseHeaders + "\r\nContent-Type: " + mime);
            if (status != 200) Failure("unexpected_resource");
        }
        catch (Exception exception) { Program.Emit("resource_failed", Program.Error(exception)); Failure("resource_failed"); }
    }
    private void Inventory(string cause)
    {
        if (Finished || environment is null) return;
        try
        {
            var rows = environment.GetProcessInfos().Select(item => new { pid = item.ProcessId, kind = item.Kind.ToString() }).ToArray();
            processCount = rows.Length;
            if (rows.Length > 128) { failed = true; RequestClose("inventory_limit", true); }
            foreach (var row in rows.Take(128))
            {
                if (ledger.TryGetValue(row.pid, out Ledger? entry)) entry.Last = Program.Elapsed;
                else if (ledger.Count < 128) ledger[row.pid] = new(row.pid, row.kind, Program.Elapsed);
                else { failed = true; RequestClose("ledger_limit", true); }
            }
            Program.Emit("process_inventory", new { cause, browser_pid = browserPid, rows = rows.Take(128).ToArray(), truncated = rows.Length > 128 });
        }
        catch (Exception exception) { processCount = -1; Program.Emit("inventory_failed", Program.Error(exception)); RequestClose("inventory_failed", true); }
    }
    protected override void Failure(string reason) => RequestClose(reason, true);
    private void RequestClose(string reason, bool failure)
    {
        failed |= failure;
        if (closing || Finished) return;
        closing = true; closeAt = Program.Elapsed; closeReason = reason;
        Program.Emit("normal_close_requested", new { reason, failure, stage, browser_pid = browserPid });
    }
    private bool Normal => browserPid != 0 && exits.TryGetValue(browserPid, out var kind) && kind == CoreWebView2BrowserProcessExitKind.Normal;
    protected override void Tick()
    {
        long elapsed = Program.Elapsed;
        if (elapsed - lastInventory >= 1000) { lastInventory = elapsed; Inventory(closing ? "closing" : "periodic"); }
        if (!initializationReturned && elapsed >= 25000) RequestClose("initialization_deadline", true);
        if (elapsed >= 60000) RequestClose("observation_deadline", true);
        if (!closing) return;
        if (initializationReturned && !disposedController)
        {
            if (operation == "paste" && operationStarted && pageReady && !failed)
            {
                if (!pasteFinalReading && !pasteFinalSnapshotObserved && pasteFinalReadAttempts < 3)
                { _ = ReadFinalPasteEvidenceAsync(); return; }
                if (pasteFinalReading) return;
                if (!pasteFinalReadFinished || !pasteFinalSnapshotObserved)
                { failed = true; Program.Emit("paste_final_capture_failed", new { reason = "explicit_final_snapshot_required", pasteFinalReadAttempts }); }
            }
            DisposeController();
        }
        if (initializationReturned && disposedController && environment is null) { processCount = 0; Complete("passed"); return; }
        if (initializationReturned && disposedController && Normal && processCount == 0) { Complete("passed"); return; }
        if (elapsed - closeAt >= 12000) { if (!disposedController) DisposeController(); Inventory("close_budget_final"); Complete("unproven"); }
    }
    private void DisposeController()
    {
        try { view.Dispose(); disposedController = true; foreach (Stream stream in streams) stream.Dispose(); streams.Clear(); Program.Emit("controller_disposed", new { browser_pid = browserPid }); }
        catch (Exception exception) { failed = true; Program.Emit("controller_dispose_failed", Program.Error(exception)); }
    }
    private void Complete(string cleanup)
    {
        bool pasteCapture = operation == "paste" && operationStarted && disposedController && pasteFinalSnapshotObserved &&
            pasteFinalSnapshotCount == pasteEvents.Count && pasteFinalValueHash is not null && pasteFinalValueEmpty.HasValue;
        if (operation == "paste" && operationStarted && !pasteCapture) failed = true;
        bool succeeded = initialized && pageReady && !failed && parentClose && Normal && processCount == 0 && cleanup == "passed";
        Program.Emit("host_complete", new { initialization_succeeded = initialized, page_succeeded = pageReady,
            operation_succeeded = operationSucceeded, operation_attempted = operationStarted,
            normal_browser_exit = Normal, environment_process_count = processCount, cleanup_status = cleanup,
            overall_status = succeeded ? "passed_mechanism_host_only" : "failed", failure_seen = failed,
            browser_pid = browserPid, close_reason = closeReason, controller_disposed = disposedController,
            paste_event_count = pasteEvents.Count, paste_nonempty_event_count = pasteEvents.Count(item => !item.Empty),
            paste_untrusted_event_count = pasteEvents.Count(item => !item.Trusted), paste_event_digest_sha256 = PasteDigest(),
            paste_capture_complete = pasteCapture, paste_final_snapshot_observed = pasteFinalSnapshotObserved,
            paste_final_value_sha256 = pasteFinalValueHash, paste_final_value_empty = pasteFinalValueEmpty,
            ledger = ledger.Values.Select(item => new { pid = item.Pid, kind = item.Kind, first_seen_ms = item.First, last_seen_ms = item.Last }).ToArray(),
            native_clipboard_operations = "none_browser_stimulus_only", ordinary_parent_exit_proof = "still_required" });
        Finish(succeeded);
    }
    private sealed class Ledger(int pid, string kind, long first)
    { internal int Pid { get; } = pid; internal string Kind { get; } = kind; internal long First { get; } = first; internal long Last { get; set; } = first; }
    private sealed record PasteFact(int Index, bool Trusted, bool Empty, string Hash);
}
