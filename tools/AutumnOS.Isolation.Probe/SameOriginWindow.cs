using System.Text;
using System.Text.Json;
using AutumnOS.Runtime;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutumnOS.Isolation.Probe;

internal sealed class SameOriginWindow(string runtime, string reportPath) : NonActivatingProbeWindow
{
    private readonly List<object> cases = [];
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    public int ExitCode { get; private set; } = 1;
    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e); object? failure = null;
        try
        {
            bool all = true;
            foreach (string channel in new[] { "websocket", "webtransport" })
                foreach (bool production in new[] { false, true }) all &= await RunCaseAsync(channel, production);
            ExitCode = all ? 0 : 1;
        }
        catch (Exception error) { failure = new { type = error.GetType().Name, error.Message }; }
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { schema_version = 1, status = ExitCode == 0 ? "passed" : "failed", kind = "same_origin_native_network_CSP_regression_probe", started_utc = started,
            finished_utc = DateTimeOffset.UtcNow, cases, failure, scope = "Random .invalid DNS targets only, no external service or user data. A is a deliberately weakened local test control: production headers with connect-src self. B uses exact production headers. Exact /owned network-service request creation is checked per channel; initial host-generated navigation can speculatively resolve its random synthetic origin before response headers, so total origin DNS is retained and is not claimed zero. WebTransport positive handshake remains not_run.",
            browser_arguments = RuntimeIsolationPolicy.BrowserArguments + " [test_only_netlog_Default_and_no_proxy_server]", system_policy_changes = "none" }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { status = ExitCode == 0 ? "passed" : "failed", report = reportPath })); Close();
    }
    private async Task<bool> RunCaseAsync(string channel, bool production)
    {
        string label = (production ? "B-production-" : "A-weakened-self-") + channel;
        string root = Path.Combine(Path.GetDirectoryName(reportPath)!, label + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        string host = "owned-same-origin-" + Guid.NewGuid().ToString("N") + ".invalid", origin = "https://" + host + "/";
        string netlog = Path.Combine(root, "netlog.json");
        var environment = await CoreWebView2Environment.CreateAsync(runtime, Path.Combine(root, "profile"), new() { AdditionalBrowserArguments = RuntimeIsolationPolicy.BrowserArguments + " --log-net-log=\"" + netlog + "\" --net-log-capture-mode=Default --no-proxy-server" });
        var exited = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); environment.BrowserProcessExited += (_, _) => exited.TrySetResult(true);
        using var view = new WebView2 { Dock = DockStyle.Fill }; Controls.Add(view);
        var streams = new List<Stream>(); var intercepted = new List<object>(); bool processFailed = false, browserExited = false;
        JsonElement? observed = null; string? failure = null;
        string headers = production ? RuntimeIsolationPolicy.ResponseHeaders : RuntimeIsolationPolicy.ResponseHeaders.Replace("connect-src 'none'", "connect-src 'self'", StringComparison.Ordinal);
        try
        {
            await view.EnsureCoreWebView2Async(environment);
            var core = view.CoreWebView2; core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDevToolsEnabled = false;
            core.PermissionRequested += (_, e) => {e.State = CoreWebView2PermissionState.Deny; e.Handled = true;};
            core.ProcessFailed += (_, _) => processFailed = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) => {
                bool page = e.Request.Uri == origin + "index.html", script = e.Request.Uri == origin + "probe.js", allowed = e.Request.Method == "GET" && (page || script);
                intercepted.Add(new { uri = e.Request.Uri, method = e.Request.Method, context = e.ResourceContext.ToString(), allowed });
                string text = page ? "<!doctype html><script src=probe.js defer></script>" : script ? Script.Replace("__CHANNEL__", JsonSerializer.Serialize(channel), StringComparison.Ordinal) : "blocked";
                var stream = new MemoryStream(Encoding.UTF8.GetBytes(text), false); streams.Add(stream);
                e.Response = environment.CreateWebResourceResponse(stream, allowed ? 200 : 403, allowed ? "OK" : "Forbidden", "Content-Type: " + (page ? "text/html" : "text/javascript") + "\r\n" + headers);
            };
            var report = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.WebMessageReceived += (_, e) => {if(e.Source == origin + "index.html") {using var doc=JsonDocument.Parse(e.WebMessageAsJson);report.TrySetResult(doc.RootElement.Clone());}};
            core.Navigate(origin + "index.html"); observed = await report.Task.WaitAsync(TimeSpan.FromSeconds(12)); await Task.Delay(200);
        }
        catch (Exception error) { failure = error.GetType().Name + ":" + error.Message; }
        finally
        {
            view.Dispose(); Controls.Remove(view); foreach(var stream in streams) stream.Dispose();
            try {browserExited = await exited.Task.WaitAsync(TimeSpan.FromSeconds(8));} catch(TimeoutException){}
        }
        var dns = new List<object>(); var matches = new List<object>(); var attempts = new List<object>(); bool valid = false;
        if(File.Exists(netlog)) try {
            using var doc = JsonDocument.Parse(File.ReadAllText(netlog));
            var names = doc.RootElement.GetProperty("constants").GetProperty("logEventTypes").EnumerateObject().ToDictionary(p=>p.Value.GetInt32(),p=>p.Name);
            foreach(var item in doc.RootElement.GetProperty("events").EnumerateArray()) {
                if(!item.TryGetProperty("params",out var args) || !args.GetRawText().Contains(host,StringComparison.Ordinal)) continue;
                string name = names.GetValueOrDefault(item.GetProperty("type").GetInt32(),"unknown");
                object entry = new { type=name, parameters=args.Clone(), source=item.GetProperty("source").Clone(), phase=item.GetProperty("phase").GetInt32() }; matches.Add(entry);
                if(name.Contains("HOST_RESOLVER",StringComparison.Ordinal)) dns.Add(entry);
                if(args.TryGetProperty("url",out var url) && Uri.TryCreate(url.GetString(),UriKind.Absolute,out var target) && target.Host == host && target.AbsolutePath == "/owned") attempts.Add(entry);
            } valid=true;
        } catch(JsonException){}
        bool executed = observed is {ValueKind:JsonValueKind.Object} value && value.GetProperty("attempted").GetBoolean();
        bool passed = failure is null && !processFailed && browserExited && valid && executed && (production ? attempts.Count == 0 : attempts.Count > 0 && dns.Count > 0);
        cases.Add(new { label, status=passed?"passed":"failed", channel, runtime=environment.BrowserVersionString, production_policy=production, headers, observed, intercepted,
            resolver_events=dns, target_events=matches, exact_channel_network_requests=attempts, channel_network_request_started=attempts.Count>0, origin_resolver_started_including_navigation=dns.Count>0, valid_netlog=valid, netlog=Path.GetRelativePath(Path.GetDirectoryName(reportPath)!,netlog), browser_exited=browserExited, process_failed=processFailed, failure });
        return passed;
    }
    private const string Script = """
        'use strict';
        (async()=>{const channel=__CHANNEL__,result={channel,attempted:true,available:typeof(channel==='websocket'?WebSocket:WebTransport)==='function'};let socket;
          try {
            if(channel==='websocket') await new Promise(resolve=>{socket=new WebSocket(location.origin.replace('https:','wss:')+'/owned');socket.onopen=()=>{result.state='open';resolve();};socket.onerror=()=>{result.state='error';resolve();};setTimeout(()=>{result.state??='timeout';resolve();},3500);});
            else {socket=new WebTransport(location.origin+'/owned');await Promise.race([socket.ready,new Promise((_,reject)=>setTimeout(()=>reject(new DOMException('deadline','TimeoutError')),3500))]);result.state='ready';}
          }catch(e){result.state=e.name;}finally{if(socket)socket.close();}chrome.webview.postMessage(result);
        })();
        """;
}
