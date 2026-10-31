using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutumnOS.AppContainerBrowserHost;

internal static class Program
{
    private const int MaximumOutputBytes = 64 * 1024;
    private static readonly object OutputGate = new();
    private static readonly Stopwatch Elapsed = Stopwatch.StartNew();
    private static StreamWriter? output;
    internal static string Nonce { get; private set; } = "";
    internal static long ElapsedMilliseconds => Elapsed.ElapsedMilliseconds;

    internal static void Emit(string kind, object detail)
    {
        lock (OutputGate)
        {
            string json = JsonSerializer.Serialize(new { schema_version = 1, nonce = Nonce,
                host_pid = Environment.ProcessId, elapsed_ms = ElapsedMilliseconds, kind, detail,
                isolation_acceptance = "not_claimed_host_output_only" });
            if (Encoding.UTF8.GetByteCount(json) + 1 > MaximumOutputBytes)
                throw new InvalidDataException("HOST_OUTPUT_LIMIT_EXCEEDED");
            output!.WriteLine(json);
            output.Flush();
        }
    }

    internal static object Error(Exception exception) => new { exception_type = exception.GetType().Name,
        hresult = unchecked((uint)exception.HResult), native_error = (exception as Win32Exception)?.NativeErrorCode };

    internal static string PathBinding(string path) => "sha256:" + Convert.ToHexStringLower(SHA256.HashData(
        Encoding.UTF8.GetBytes(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToUpperInvariant())));

    [STAThread]
    private static int Main(string[] args)
    {
        using StreamWriter writer = new(Console.OpenStandardOutput(), new UTF8Encoding(false), 8192, leaveOpen: true);
        output = writer;
        if (args.Length != 4 || args[0] != "--execute" || !Regex.IsMatch(args[1], "^[a-f0-9]{32}$", RegexOptions.CultureInvariant))
        {
            Emit("launch_refused", new { reason = "EXPLICIT_NONCE_RUNTIME_UDF_REQUIRED", execution = "not_run" });
            return 64;
        }
        Nonce = args[1];
        try
        {
            string runtime = Path.GetFullPath(args[2]), udf = Path.GetFullPath(args[3]);
            if (!Path.IsPathFullyQualified(args[2]) || !Path.IsPathFullyQualified(args[3]) ||
                !Directory.Exists(runtime) || !Directory.Exists(udf) ||
                !File.Exists(Path.Combine(runtime, "msedgewebview2.exe")))
                throw new InvalidDataException("EXISTING_SCOPED_RUNTIME_UDF_REQUIRED");
            // Environment values are never printed. The parent must independently
            // audit applicable registry policy and actual executable identities.
            foreach (string name in Environment.GetEnvironmentVariables().Keys)
                if (name.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("WEBVIEW2_ENVIRONMENT_OVERRIDE_BLOCKED");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using OwnedForm form = new(runtime, udf);
            Application.Run(form);
            return form.Succeeded ? 0 : 1;
        }
        catch (Exception exception)
        {
            Emit("host_failure", Error(exception));
            return 1;
        }
    }
}

internal sealed class OwnedForm : Form
{
    private const int MaximumCommandBytes = 1024;
    private const long InitializationBudgetMilliseconds = 25_000;
    private const long ObservationBudgetMilliseconds = 60_000;
    private const long CloseBudgetMilliseconds = 12_000;
    private readonly string runtime, udf, origin;
    private readonly WebView2 view = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 100 };
    private readonly Dictionary<int, LedgerEntry> ledger = [];
    private readonly Dictionary<uint, CoreWebView2BrowserProcessExitKind> exitEvents = [];
    private readonly List<Stream> responseStreams = [];
    private CoreWebView2Environment? environment;
    private bool initialized, initializationReturned, pageRequested, pageSucceeded;
    private bool closing, controlDisposed, finalClose, failed, finished, parentClose;
    private bool inventoryTruncated;
    private long closeRequestedMilliseconds, lastInventoryMilliseconds = -1000;
    private string closeReason = "not_requested", stage = "starting";
    private uint browserPid;
    private int environmentProcessCount = -1;
    internal bool Succeeded { get; private set; }

    // This trusted probe never requests keyboard/mouse focus or injects input.
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { CreateParams parameters = base.CreateParams; parameters.ExStyle |= 0x08000000; return parameters; }
    }

    internal OwnedForm(string runtime, string udf)
    {
        this.runtime = runtime; this.udf = udf;
        origin = "https://owned-" + Program.Nonce + ".autumn.invalid";
        Text = "AutumnOS · 自有浏览器机制验证 · 派蒙";
        Width = 540; Height = 340; ShowInTaskbar = false;
        Controls.Add(view);
        Shown += async (_, _) =>
        {
            Program.Emit("host_started", new { runtime_binding_sha256 = Program.PathBinding(runtime),
                udf_binding_sha256 = Program.PathBinding(udf), policy_id = FrozenPolicy.PolicyId,
                command_bytes_limit = MaximumCommandBytes, parent_token_audit = "required_not_implied",
                production_admission = "not_claimed", clipboard_operations = "none", network_probe_operations = "none" });
            _ = Task.Run(ReadCommands);
            timer.Start();
            await InitializeAsync();
        };
        FormClosing += (_, args) =>
        {
            if (!finalClose) { args.Cancel = true; RequestClose("native_window_close", failure: true); }
        };
        timer.Tick += (_, _) => Observe();
    }

    private async Task InitializeAsync()
    {
        try
        {
            stage = "create_environment";
            Program.Emit("initialization_begin", new { sdk = "1.0.4258.31", runtime = FrozenPolicy.RequiredBrowserVersion });
            var options = new CoreWebView2EnvironmentOptions(FrozenPolicy.BrowserArguments)
            { ExclusiveUserDataFolderAccess = true, IsCustomCrashReportingEnabled = true };
            environment = await CoreWebView2Environment.CreateAsync(runtime, udf, options);
            if (finished) return;
            environment.BrowserProcessExited += (_, args) =>
            {
                if (finished) return;
                exitEvents[args.BrowserProcessId] = args.BrowserProcessExitKind;
                Program.Emit("browser_group_exit", new { pid = args.BrowserProcessId, kind = args.BrowserProcessExitKind.ToString() });
            };
            environment.ProcessInfosChanged += (_, _) => Inventory("changed");
            Inventory("environment_created");
            if (environment.BrowserVersionString != FrozenPolicy.RequiredBrowserVersion ||
                !string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(environment.UserDataFolder)),
                    Path.TrimEndingDirectorySeparator(udf), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("FIXED_RUNTIME_AND_EXACT_UDF_REQUIRED");
            if (closing) return;
            stage = "create_controller";
            CoreWebView2ControllerOptions controller = environment.CreateCoreWebView2ControllerOptions();
            controller.IsInPrivateModeEnabled = true;
            await view.EnsureCoreWebView2Async(environment, controller);
            if (finished) return;
            CoreWebView2 core = view.CoreWebView2;
            browserPid = core.BrowserProcessId;
            if (!core.Profile.IsInPrivateModeEnabled) throw new InvalidDataException("INPRIVATE_REQUIRED");
            Inventory("controller_created");
            if (closing) return;
            stage = "configure_controller";
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
            core.NewWindowRequested += (_, args) => args.Handled = true;
            core.DownloadStarting += (_, args) => args.Cancel = true;
            core.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
            core.FrameNavigationStarting += (_, args) => args.Cancel = true;
            core.NavigationStarting += (_, args) =>
            {
                if (!pageRequested || args.Uri != origin + "/")
                { args.Cancel = true; RequestClose("unexpected_navigation", failure: true); }
            };
            core.ProcessFailed += (_, args) =>
            {
                Program.Emit("process_failed", new { kind = args.ProcessFailedKind.ToString() });
                RequestClose("browser_process_failed", failure: true);
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += Serve;
            core.WebMessageReceived += OnMessage;
            initialized = true; stage = "controller_ready";
            Program.Emit("controller_ready", new { browser_pid = browserPid,
                browser_version = environment.BrowserVersionString, policy_id = FrozenPolicy.PolicyId,
                udf_binding_sha256 = Program.PathBinding(environment.UserDataFolder),
                udf_matches_requested = true, in_private = core.Profile.IsInPrivateModeEnabled,
                parent_token_audit = "required_before_load_command" });
        }
        catch (Exception exception)
        {
            if (!finished) { Program.Emit("initialization_failed", new { stage, error = Program.Error(exception) }); RequestClose("initialization_failed", failure: true); }
        }
        finally { initializationReturned = true; }
    }

    private void ReadCommands()
    {
        try
        {
            using Stream input = Console.OpenStandardInput();
            byte[] bytes = new byte[MaximumCommandBytes]; int count = 0;
            while (true)
            {
                int value = input.ReadByte();
                if (value < 0) { Queue(() => { if (!closing) RequestClose(count == 0 ? "stdin_eof" : "partial_command_eof", failure: true); }); return; }
                if (value == '\n')
                {
                    int length = count > 0 && bytes[count - 1] == '\r' ? count - 1 : count;
                    string command = new UTF8Encoding(false, true).GetString(bytes, 0, length);
                    count = 0; Queue(() => Command(command));
                    continue;
                }
                if (count == bytes.Length) { Queue(() => RequestClose("command_too_large", failure: true)); return; }
                bytes[count++] = checked((byte)value);
            }
        }
        catch (Exception exception)
        {
            Queue(() => { Program.Emit("command_reader_failed", Program.Error(exception)); RequestClose("command_reader_failed", failure: true); });
        }
    }

    private void Queue(Action action)
    {
        try { if (!IsDisposed && IsHandleCreated) BeginInvoke(() => { if (!finished) action(); }); }
        catch (InvalidOperationException) { /* UI has already completed; no success is manufactured. */ }
    }

    private void Command(string json)
    {
        if (closing) return;
        try
        {
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 3 });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("COMMAND_OBJECT_REQUIRED");
            JsonProperty[] properties = root.EnumerateObject().ToArray();
            if (properties.Length != 3 || properties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != 3 ||
                root.GetProperty("schema_version").GetInt32() != 1 || root.GetProperty("nonce").GetString() != Program.Nonce)
                throw new InvalidDataException("COMMAND_BINDING_INVALID");
            string? command = root.GetProperty("command").GetString();
            if (command == "close") { parentClose = true; RequestClose("parent_close", failure: false); }
            else if (command == "load_owned_page" && initialized && !pageRequested)
            {
                pageRequested = true; stage = "owned_page";
                Program.Emit("owned_navigation_requested", new { browser_pid = browserPid, trusted_content_only = true });
                view.CoreWebView2.Navigate(origin + "/");
            }
            else throw new InvalidDataException("COMMAND_NOT_ALLOWED_IN_CURRENT_STATE");
        }
        catch (Exception exception)
        {
            Program.Emit("command_rejected", Program.Error(exception));
            RequestClose("invalid_command", failure: true);
        }
    }

    private void Serve(object? sender, CoreWebView2WebResourceRequestedEventArgs args)
    {
        if (finished || environment is null) return;
        byte[] bytes = []; string mime = "text/plain"; int status = 403; string resource = "rejected";
        try
        {
            if (!closing && pageRequested && args.Request.Method == "GET" && args.Request.Uri == origin + "/")
            {
                bytes = Encoding.UTF8.GetBytes("<!doctype html><html data-nonce=\"" + Program.Nonce + "\"><meta charset=utf-8><title>Owned probe</title><link rel=icon type=image/svg+xml href=/owned-icon.svg><canvas id=owned-canvas width=240 height=100></canvas><script src=/owned-page.js></script></html>");
                mime = "text/html; charset=utf-8"; status = 200; resource = "owned_entry";
            }
            else if (!closing && pageRequested && args.Request.Method == "GET" && args.Request.Uri == origin + "/owned-page.js")
            {
                using Stream script = Assembly.GetExecutingAssembly().GetManifestResourceStream("AutumnOS.AppContainerBrowserHost.owned-page.js") ?? throw new InvalidDataException("OWNED_SCRIPT_MISSING");
                using MemoryStream buffer = new(); script.CopyTo(buffer);
                bytes = buffer.ToArray(); mime = "application/javascript; charset=utf-8"; status = 200; resource = "owned_script";
            }
            else if (!closing && pageRequested && args.Request.Method == "GET" && args.Request.Uri == origin + "/owned-icon.svg")
            {
                bytes = Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 16 16\"><path fill=\"#ad5e32\" d=\"M2 2h12v12H2z\"/></svg>");
                mime = "image/svg+xml"; status = 200; resource = "owned_icon";
            }
            MemoryStream stream = new(bytes, writable: false); responseStreams.Add(stream);
            args.Response = environment.CreateWebResourceResponse(stream, status, status == 200 ? "OK" : "Forbidden", FrozenPolicy.ResponseHeaders + "\r\nContent-Type: " + mime);
            Program.Emit("owned_resource_served", new { resource, status, bytes = bytes.Length });
            if (status != 200) RequestClose("unexpected_resource", failure: true);
        }
        catch (Exception exception)
        {
            Program.Emit("resource_failure", Program.Error(exception));
            RequestClose("resource_failure", failure: true);
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (closing || finished) return;
        try
        {
            string json = args.WebMessageAsJson;
            if (!pageRequested || pageSucceeded || json.Length > 2048 || args.Source != origin + "/" || view.CoreWebView2.Source != origin + "/")
                throw new InvalidDataException("OWNED_MESSAGE_SOURCE_INVALID");
            using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 3 });
            JsonElement root = document.RootElement;
            JsonProperty[] properties = root.EnumerateObject().ToArray();
            if (properties.Length != 4 || properties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != 4 ||
                root.GetProperty("nonce").GetString() != Program.Nonce || root.GetProperty("kind").GetString() != "owned_page_complete" ||
                root.GetProperty("wasm").GetInt32() != 42 || root.GetProperty("frames").GetInt32() != 60)
                throw new InvalidDataException("OWNED_MESSAGE_INVALID");
            pageSucceeded = true;
            Inventory("owned_complete");
            Program.Emit("owned_page_complete", new { wasm = 42, frames = 60, browser_pid = browserPid,
                actual_native_token_audit = "ordinary_parent_required" });
        }
        catch (Exception exception)
        {
            Program.Emit("owned_result_failure", Program.Error(exception));
            RequestClose("owned_result_failure", failure: true);
        }
    }

    private void Inventory(string cause)
    {
        if (environment is null || finished) return;
        try
        {
            var rows = environment.GetProcessInfos().Select(item => new { pid = item.ProcessId, kind = item.Kind.ToString() }).ToArray();
            environmentProcessCount = rows.Length;
            foreach (var row in rows)
            {
                if (ledger.TryGetValue(row.pid, out LedgerEntry? entry)) entry.LastSeen = Program.ElapsedMilliseconds;
                else if (ledger.Count < 128) ledger.Add(row.pid, new LedgerEntry(row.pid, row.kind, Program.ElapsedMilliseconds));
                else { inventoryTruncated = true; failed = true; }
            }
            Program.Emit("process_inventory", new { cause, browser_pid = browserPid,
                rows = rows.Take(128).ToArray(), truncated = rows.Length > 128 || inventoryTruncated,
                crashpad = "excluded_by_sdk_ordinary_parent_os_observer_required" });
        }
        catch (Exception exception)
        {
            environmentProcessCount = -1; failed = true;
            Program.Emit("inventory_failed", Program.Error(exception));
        }
    }

    private void RequestClose(string reason, bool failure)
    {
        failed |= failure;
        if (closing || finished) return;
        closing = true; closeReason = reason; closeRequestedMilliseconds = Program.ElapsedMilliseconds;
        Program.Emit("normal_close_requested", new { reason, failure, stage, browser_pid = browserPid });
    }

    private bool NormalBrowserExit => browserPid != 0 && exitEvents.TryGetValue(browserPid, out CoreWebView2BrowserProcessExitKind kind) && kind == CoreWebView2BrowserProcessExitKind.Normal;

    private void Observe()
    {
        if (finished) return;
        long elapsed = Program.ElapsedMilliseconds;
        if (elapsed - lastInventoryMilliseconds >= 1000)
        { lastInventoryMilliseconds = elapsed; Inventory(closing ? "closing" : "periodic"); }
        if (!initializationReturned && elapsed >= InitializationBudgetMilliseconds) RequestClose("initialization_deadline", failure: true);
        if (elapsed >= ObservationBudgetMilliseconds) RequestClose("observation_deadline", failure: true);
        if (!closing) return;
        if (initializationReturned && !controlDisposed) DisposeController();
        if (controlDisposed && initializationReturned && environment is null)
        { environmentProcessCount = 0; Finish("passed"); return; }
        if (controlDisposed && initializationReturned && NormalBrowserExit && environmentProcessCount == 0)
        { Finish("passed"); return; }
        if (elapsed - closeRequestedMilliseconds >= CloseBudgetMilliseconds)
        {
            // A bounded ordinary host exit is not evidence of browser termination.
            // The parent must retain the profile/directories until OS-held handles
            // prove the complete group exited. No child process is killed here.
            if (!controlDisposed) DisposeController();
            Inventory("close_budget_final");
            Finish("unproven");
        }
    }

    private void DisposeController()
    {
        try { view.Dispose(); controlDisposed = true; Program.Emit("controller_disposed", new { browser_pid = browserPid, initialization_returned = initializationReturned }); }
        catch (Exception exception) { failed = true; Program.Emit("controller_dispose_failed", Program.Error(exception)); }
        if (controlDisposed)
        {
            foreach (Stream stream in responseStreams) stream.Dispose();
            responseStreams.Clear();
        }
    }

    private void Finish(string cleanupStatus)
    {
        if (finished) return;
        bool normal = NormalBrowserExit;
        Succeeded = initialized && pageSucceeded && parentClose && !failed && normal && environmentProcessCount == 0 && cleanupStatus == "passed";
        finished = true; timer.Stop();
        Program.Emit("host_complete", new { initialization_succeeded = initialized, page_succeeded = pageSucceeded,
            normal_browser_exit = normal, environment_process_count = environmentProcessCount, cleanup_status = cleanupStatus,
            overall_status = Succeeded ? "passed_mechanism_host_only" : "failed", close_reason = closeReason,
            failure_seen = failed, browser_pid = browserPid, controller_disposed = controlDisposed,
            initialization_returned = initializationReturned, parent_close_received = parentClose,
            ledger = ledger.Values.Select(item => new { pid = item.Pid, kind = item.Kind, first_seen_ms = item.FirstSeen, last_seen_ms = item.LastSeen }).ToArray(),
            inventory_truncated = inventoryTruncated, ordinary_parent_held_handle_exit_proof = "still_required" });
        finalClose = true; Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) timer.Dispose();
        base.Dispose(disposing);
    }

    private sealed class LedgerEntry(int pid, string kind, long firstSeen)
    {
        internal int Pid { get; } = pid;
        internal string Kind { get; } = kind;
        internal long FirstSeen { get; } = firstSeen;
        internal long LastSeen { get; set; } = firstSeen;
    }
}
