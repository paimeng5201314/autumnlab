using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Runtime;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutumnOS.Isolation.Probe;

// The owned browser needs a window handle, but never activates or uses user input.
internal abstract class NonActivatingProbeWindow : Form
{
    protected NonActivatingProbeWindow()
    {
        ShowInTaskbar = false; StartPosition = FormStartPosition.Manual;
        Location = new(-20000, -20000); Size = new(800, 600);
    }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var value = base.CreateParams; value.ExStyle |= 0x08000000; return value; } }
}

internal sealed class ResourceStorageWindow : NonActivatingProbeWindow
{
    private readonly string runtime, reportPath;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly string origin = "https://controlled-storage-" + Guid.NewGuid().ToString("N") + ".autumn.invalid/";
    private readonly string marker = "owned-marker-" + Guid.NewGuid().ToString("N");
    private readonly List<object> checks = [], observations = [];
    private readonly List<Context> contexts = [];
    private object? failure;
    public int ExitCode { get; private set; } = 1;
    public ResourceStorageWindow(string runtime, string reportPath)
    {
        this.runtime = runtime; this.reportPath = reportPath;
        Shown += async (_, _) => await RunAsync();
    }
    private void Check(string id, bool passed)
    {
        checks.Add(new { id, status = passed ? "passed" : "failed" });
        if (!passed) throw new InvalidOperationException(id);
    }
    private async Task RunAsync()
    {
        try
        {
            var a = await OpenAsync("account-A_app-A_source-A", "controlled-account-A", "dev.autumnos.storageprobeA", "local:controlled-source-A");
            JsonElement resources = await a.RunAsync("resources", marker);
            observations.Add(new { label = a.Label, operation = "resources", result = resources });
            Check("external_script_executed_under_production_headers", resources.GetProperty("scriptExecuted").GetBoolean());
            Check("local_fetch_explicitly_blocked_by_current_profile", resources.GetProperty("fetchError").GetString() == "TypeError" && !resources.TryGetProperty("fetch", out _));
            Check("inline_WASM_bytes_compile_and_execute_allowed", resources.GetProperty("wasm").GetInt32() == 42);
            Check("arbitrary_javascript_eval_remains_blocked", resources.GetProperty("evalBlocked").GetBoolean());
            Check("async_clipboard_features_known_to_actual_engine", resources.GetProperty("clipboardFeaturesKnown").GetBoolean());
            Check("async_clipboard_document_policy_denies_both_directions", !resources.GetProperty("clipboardReadPolicy").GetBoolean() && !resources.GetProperty("clipboardWritePolicy").GetBoolean());
            Check("async_clipboard_permission_query_reports_denied", resources.GetProperty("clipboardReadPermission").GetString() == "denied" && resources.GetProperty("clipboardWritePermission").GetString() == "denied");
            Check("only_exact_owned_package_resources_served", a.ServedPaths.Order(StringComparer.Ordinal).SequenceEqual(new[] { "index.html", "probe.js" }, StringComparer.Ordinal));

            var seeded = await a.RunAsync("seed", marker);
            observations.Add(new { label = a.Label, operation = "seed", result = seeded });
            Check("storage_positive_control_all_stores_write_and_read", AllStores(seeded, marker));
            await a.NavigateAsync();
            var reread = await a.RunAsync("read", marker);
            observations.Add(new { label = a.Label, operation = "same_controller_navigation_readback", result = reread });
            Check("same_origin_same_profile_navigation_positive_control", AllStores(reread, marker));

            foreach (var spec in new[] {
                ("account-B_app-A_source-A", "controlled-account-B", "dev.autumnos.storageprobeA", "local:controlled-source-A"),
                ("account-A_app-B_source-A", "controlled-account-A", "dev.autumnos.storageprobeB", "local:controlled-source-A"),
                ("account-A_app-A_source-B", "controlled-account-A", "dev.autumnos.storageprobeA", "local:controlled-source-B") })
            {
                var other = await OpenAsync(spec.Item1, spec.Item2, spec.Item3, spec.Item4);
                var read = await other.RunAsync("read", marker);
                observations.Add(new { label = other.Label, operation = "same_origin_distinct_binding_read", result = read });
                Check(other.Label + "_all_stores_empty", AllStores(read, null));
                Check(other.Label + "_browser_process_not_shared", other.BrowserPid != a.BrowserPid);
                var ownMarker = marker + "-" + spec.Item1;
                Check(other.Label + "_own_positive_control", AllStores(await other.RunAsync("seed", ownMarker), ownMarker));
                Check(other.Label + "_original_remains_unchanged", AllStores(await a.RunAsync("read", marker), marker));
            }
            Check("distinct_profiles_generated_by_production_binding_function", contexts.Select(c => c.Profile).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 4);
            foreach (var context in contexts) await context.CloseAsync();
            Check("all_owned_browser_processes_exited", contexts.All(c => c.Exited));
            var reopened = await OpenAsync("account-A_reopened_InPrivate", "controlled-account-A", "dev.autumnos.storageprobeA", "local:controlled-source-A");
            var fresh = await reopened.RunAsync("read", marker);
            observations.Add(new { label = reopened.Label, operation = "after_all_controllers_closed_same_profile_read", result = fresh });
            Check("same_profile_InPrivate_restart_all_stores_empty", AllStores(fresh, null));
            ExitCode = 0;
        }
        catch (Exception error) { failure = new { type = error.GetType().Name, message = error.Message }; }
        finally
        {
            foreach (var context in contexts) await context.CloseAsync();
            if (contexts.Any(c => !c.Exited)) ExitCode = 1;
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new {
                schema_version = 1, status = ExitCode == 0 ? "passed" : "failed", kind = "real_fixed_WebView2_production_policy_resource_and_controlled_identity_profile_probe",
                started_utc = started, finished_utc = DateTimeOffset.UtcNow, os = Environment.OSVersion.VersionString,
                runtime_sdk = typeof(CoreWebView2).Assembly.GetName().Version?.ToString(), response_headers = RuntimeIsolationPolicy.ResponseHeaders,
                policy_headers_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(RuntimeIsolationPolicy.ResponseHeaders))),
                scope = "Controlled test identities only; no Logto accounts, SDK save authorization or product UI validation. Same origin deliberately reused across isolated bindings to test profile boundary independently of origin partitioning.",
                additional_browser_arguments = RuntimeIsolationPolicy.BrowserArguments, system_policy_changes = "none", checks, observations, failure,
                contexts = contexts.Select(c => new { c.Label, profile = Path.GetRelativePath(Path.GetDirectoryName(reportPath)!, c.Profile), browser_pid = c.BrowserPid, runtime = c.Runtime, in_private = c.InPrivate, browser_exited = c.Exited, c.BlockedRequests, c.ServedPaths, c.ProcessFailed })
            }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(new { report = reportPath, status = ExitCode == 0 ? "passed" : "failed", checks = checks.Count }));
            Close();
        }
    }
    private static bool AllStores(JsonElement value, string? expected) => new[] { "localStorage", "sessionStorage", "cookie", "indexedDB", "cacheStorage" }.All(name =>
        value.TryGetProperty(name, out var found) && (expected is null ? found.ValueKind == JsonValueKind.Null : found.ValueKind == JsonValueKind.String && found.GetString() == expected));
    private async Task<Context> OpenAsync(string label, string account, string appId, string source)
    {
        var app = new RuntimeApplication(new(appId, null, null), source, "Owned storage probe", []);
        string profile = WebViewProfileDirectory.ForBinding(Path.Combine(Path.GetDirectoryName(reportPath)!, "owned-runtime"), account, app.BindingKey);
        var context = new Context(label, profile, runtime, origin, this); contexts.Add(context);
        await context.OpenAsync(); return context;
    }

    private sealed class Context(string label, string profile, string runtime, string origin, ResourceStorageWindow owner)
    {
        private readonly WebView2 view = new() { Dock = DockStyle.Fill };
        private readonly List<Stream> streams = [];
        private readonly TaskCompletionSource<bool> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<JsonElement>? response;
        private bool closed;
        public string Label => label;
        public string Profile => profile;
        public uint BrowserPid { get; private set; }
        public string? Runtime { get; private set; }
        public bool InPrivate { get; private set; }
        public bool Exited { get; private set; }
        public int BlockedRequests { get; private set; }
        public List<string> ServedPaths { get; } = [];
        public bool ProcessFailed { get; private set; }
        public async Task OpenAsync()
        {
            Directory.CreateDirectory(profile);
            var environment = await CoreWebView2Environment.CreateAsync(runtime, profile, new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = RuntimeIsolationPolicy.BrowserArguments });
            environment.BrowserProcessExited += (_, _) => exited.TrySetResult(true);
            Runtime = environment.BrowserVersionString;
            var controller = environment.CreateCoreWebView2ControllerOptions(); controller.IsInPrivateModeEnabled = true;
            owner.Controls.Add(view); await view.EnsureCoreWebView2Async(environment, controller);
            var core = view.CoreWebView2; BrowserPid = core.BrowserProcessId; InPrivate = core.Profile.IsInPrivateModeEnabled;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false; core.Settings.IsGeneralAutofillEnabled = false; core.Settings.IsPasswordAutosaveEnabled = false;
            core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true; core.DownloadStarting += (_, e) => e.Cancel = true;
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.NavigationStarting += (_, e) => { if (e.Uri != origin + "index.html") e.Cancel = true; };
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            core.ProcessFailed += (_, _) => { ProcessFailed = true; response?.TrySetException(new InvalidOperationException("OWNED_BROWSER_PROCESS_FAILED")); };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) => {
                string path = e.Request.Uri.StartsWith(origin, StringComparison.Ordinal) ? e.Request.Uri[origin.Length..] : "";
                byte[]? bytes = null; string mime = "text/plain";
                if (e.Request.Method == "GET") switch (path) {
                    case "index.html": bytes = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><title>Owned resource/storage probe</title><script src=probe.js defer></script>"); mime = "text/html"; break;
                    case "probe.js": bytes = Encoding.UTF8.GetBytes(Script); mime = "text/javascript"; break;
                    case "local.json": bytes = Encoding.UTF8.GetBytes("{\"value\":\"owned-local-resource\"}"); mime = "application/json"; break;
                    case "add.wasm": bytes = Convert.FromHexString("0061736d0100000001070160027f7f017f030201000707010361646400000a09010700200020016a0b"); mime = "application/wasm"; break;
                }
                bool allowed = bytes is not null; if (!allowed) BlockedRequests++; else ServedPaths.Add(path);
                var stream = new MemoryStream(bytes ?? "blocked"u8.ToArray(), false); streams.Add(stream);
                e.Response = environment.CreateWebResourceResponse(stream, allowed ? 200 : 403, allowed ? "OK" : "Forbidden", "Content-Type: " + mime + "\r\n" + RuntimeIsolationPolicy.ResponseHeaders);
            };
            core.WebMessageReceived += (_, e) => { if (e.Source == origin + "index.html") { using var json = JsonDocument.Parse(e.WebMessageAsJson); response?.TrySetResult(json.RootElement.Clone()); } };
            await NavigateAsync();
        }
        public async Task NavigateAsync()
        {
            var complete = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void Handler(object? sender, CoreWebView2NavigationCompletedEventArgs args) => complete.TrySetResult(args.IsSuccess);
            view.CoreWebView2.NavigationCompleted += Handler;
            try { view.CoreWebView2.Navigate(origin + "index.html"); if (!await complete.Task.WaitAsync(TimeSpan.FromSeconds(12))) throw new InvalidOperationException("OWNED_NAVIGATION_FAILED"); }
            finally { view.CoreWebView2.NavigationCompleted -= Handler; }
        }
        public async Task<JsonElement> RunAsync(string mode, string marker)
        {
            response = new(TaskCreationOptions.RunContinuationsAsynchronously);
            await view.CoreWebView2.ExecuteScriptAsync("globalThis.ownedRun(" + JsonSerializer.Serialize(mode) + "," + JsonSerializer.Serialize(marker) + ")");
            var result = await response.Task.WaitAsync(TimeSpan.FromSeconds(12));
            if (result.TryGetProperty("error", out var error)) throw new InvalidOperationException("OWNED_SCRIPT_ERROR:" + error.GetString());
            return result;
        }
        public async Task CloseAsync()
        {
            if (closed) return; closed = true; view.Dispose(); owner.Controls.Remove(view);
            foreach (var stream in streams) stream.Dispose();
            try { Exited = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10)); } catch (TimeoutException) { }
        }
    }
    private const string Script = """
        'use strict';
        globalThis.ownedRun = async (mode, marker) => {
          try {
            if (mode === 'resources') {
              const result = {scriptExecuted:true};
              try { result.fetch = (await (await fetch('local.json')).json()).value; } catch(e) { result.fetchError=e.name; }
              try { const hex='0061736d0100000001070160027f7f017f030201000707010361646400000a09010700200020016a0b'; const bytes=Uint8Array.from(hex.match(/../g),v=>parseInt(v,16)); const module=await WebAssembly.instantiate(bytes); result.wasm=module.instance.exports.add(20,22); } catch(e) { result.wasmError=e.name; }
              try { eval('1+1'); result.evalBlocked=false; } catch(e) { result.evalBlocked=e.name==='EvalError'; }
              const policy=document.permissionsPolicy||document.featurePolicy;
              result.clipboardFeaturesKnown=!!policy&&policy.features().includes('clipboard-read')&&policy.features().includes('clipboard-write');
              result.clipboardReadPolicy=!!policy&&policy.allowsFeature('clipboard-read'); result.clipboardWritePolicy=!!policy&&policy.allowsFeature('clipboard-write');
              result.clipboardReadPermission=(await navigator.permissions.query({name:'clipboard-read'})).state;
              result.clipboardWritePermission=(await navigator.permissions.query({name:'clipboard-write'})).state;
              chrome.webview.postMessage(result); return;
            }
            const req = request => new Promise((resolve,reject)=> {request.onsuccess=()=>resolve(request.result);request.onerror=()=>reject(request.error);});
            const opening=indexedDB.open('owned-isolation-storage',1); opening.onupgradeneeded=()=>opening.result.createObjectStore('values');
            const db=await req(opening), cache=await caches.open('owned-isolation-storage');
            try {
              if(mode==='seed') {
                localStorage.setItem('owned-marker',marker); sessionStorage.setItem('owned-marker',marker);
                document.cookie='owned_marker='+marker+'; Secure; SameSite=Strict; Path=/';
                const transaction=db.transaction('values','readwrite');
                const committed=new Promise((resolve,reject)=> {transaction.oncomplete=resolve;transaction.onerror=()=>reject(transaction.error);transaction.onabort=()=>reject(transaction.error);});
                transaction.objectStore('values').put(marker,'owned-marker'); await committed;
                await cache.put(new Request(new URL('owned-cache-marker',location.href)),new Response(marker));
              }
              const idb=await req(db.transaction('values','readonly').objectStore('values').get('owned-marker'));
              const cached=await cache.match(new URL('owned-cache-marker',location.href));
              const cookie=document.cookie.split('; ').find(x=>x.startsWith('owned_marker='));
              chrome.webview.postMessage({localStorage:localStorage.getItem('owned-marker'),sessionStorage:sessionStorage.getItem('owned-marker'),cookie:cookie?cookie.substring('owned_marker='.length):null,indexedDB:idb??null,cacheStorage:cached?await cached.text():null});
            } finally {db.close();}
          } catch(e) {chrome.webview.postMessage({error:e.name+':'+e.message});}
        };
        """;
}
