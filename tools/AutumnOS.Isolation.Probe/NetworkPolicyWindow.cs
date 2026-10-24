using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.Runtime;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutumnOS.Isolation.Probe;

internal sealed class NetworkPolicyWindow : NonActivatingProbeWindow
{
    private readonly string runtime;
    private readonly string reportPath;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly List<object> cases = [];
    private readonly List<object> lifecycle = [];
    private readonly System.Windows.Forms.Timer deadline = new() { Interval = 55000 };
    private WebView2? browser;
    private bool finished;
    private object? failure;
    public int ExitCode { get; private set; } = 1;

    public NetworkPolicyWindow(string runtime, string reportPath)
    {
        this.runtime = runtime; this.reportPath = reportPath;
        Text = "AutumnOS native network isolation · owned loopback observers";
        Width = 850; Height = 600;
        Shown += async (_, _) => await RunAsync();
        deadline.Tick += (_, _) => { failure = new { type = "ProbeDeadline" }; Finish(); };
        FormClosing += (_, _) => { if (!finished) { failure ??= new { type = "WindowClosed" }; Finish(); } };
    }

    private async Task RunAsync()
    {
        deadline.Start();
        try
        {
            bool first = await RunCaseAsync("A");
            bool second = await RunCaseAsync("B");
            ExitCode = first && second ? 0 : 1;
        }
        catch (Exception error) { failure = new { type = error.GetType().Name, code = error.Message.All(c => char.IsAsciiLetterUpper(c) || c == '_') ? error.Message : "PROBE_EXCEPTION", hResult = "0x" + unchecked((uint)error.HResult).ToString("X8") }; }
        Finish();
    }

    private static string FindFixtureDirectory()
    {
        for (DirectoryInfo? cursor = new(AppContext.BaseDirectory); cursor is not null; cursor = cursor.Parent)
        {
            string candidate = Path.Combine(cursor.FullName, "tests", "fixtures", "runtime-isolation");
            if (File.Exists(Path.Combine(candidate, "probe.js"))) return candidate;
        }
        throw new FileNotFoundException("CONTROLLED_NETWORK_FIXTURE_MISSING");
    }

    private async Task<bool> RunCaseAsync(string label)
    {
        string caseRoot = Path.Combine(Path.GetDirectoryName(reportPath)!, label + "-network-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(caseRoot);
        string origin = "https://network-isolation-" + Guid.NewGuid().ToString("N") + ".autumn.invalid/";
        string token = Guid.NewGuid().ToString("N");
        string dnsHost = "autumn-dns-" + token + ".invalid";
        string preconnectHost = "autumn-preconnect-" + token + ".invalid";
        string netLogPath = Path.Combine(caseRoot, "netlog.json");
        string fixtureDirectory = FindFixtureDirectory();
        string fixtureScript = File.ReadAllText(Path.Combine(fixtureDirectory, "probe.js"));
        string fixtureCss = File.ReadAllText(Path.Combine(fixtureDirectory, "probe.css"));
        string networkCss = File.ReadAllText(Path.Combine(fixtureDirectory, "network.css"));
        using OwnedNetworkObserver observer = new();
        await observer.VerifyPositiveControlAsync();
        int controlTcp = observer.TcpCount, controlUdp = observer.UdpCount;
        string httpBase = "http://127.0.0.1:" + observer.TcpPort + "/" + token;
        string configScript = "window.autumnIsolationProbeConfig=" + JsonSerializer.Serialize(new
        {
            token, httpBase, tcpPort = observer.TcpPort, udpPort = observer.UdpPort, deadlineMs = 3000, allowNetworkAttempts = true
        }) + ";";
        string hintScript = "(() => { globalThis.__ownedNetworkExtraHints={dns:" + JsonSerializer.Serialize(dnsHost) + ",preconnect:" + JsonSerializer.Serialize(preconnectHost) + ",inserted:true};" +
            "for(const [rel,host] of [['dns-prefetch',globalThis.__ownedNetworkExtraHints.dns],['preconnect',globalThis.__ownedNetworkExtraHints.preconnect]]){const hint=document.createElement('link');hint.rel=rel;hint.href='https://'+host+'/';document.head.appendChild(hint);}})();";
        Dictionary<string, (string Content, string Mime)> resources = new(StringComparer.Ordinal)
        {
            ["index.html"] = (Html, "text/html"), ["config.js"] = (configScript, "text/javascript"),
            ["probe.js"] = (fixtureScript, "text/javascript"), ["probe.css"] = (fixtureCss, "text/css"),
            ["network.css"] = (networkCss.Replace("__HTTP_BASE__", httpBase, StringComparison.Ordinal), "text/css"),
            ["hints.js"] = (hintScript, "text/javascript"),
            ["worker.js"] = (File.ReadAllText(Path.Combine(fixtureDirectory, "worker.js")), "text/javascript"),
            ["service-worker.js"] = ("self.addEventListener('install',()=>self.skipWaiting());", "text/javascript")
        };
        List<Stream> streams = [];
        bool processFailed = false;
        int interceptedAllowed = 0, interceptedBlocked = 0;
        JsonElement? observed = null;
        JsonElement? observedHints = null;
        object? localFailure = null;
        string browserVersion = "not_created";
        bool browserExited = false;
        // This diagnostic process only bypasses the machine's HTTP proxy so its random
        // .invalid DNS positive control reaches Chromium's resolver instead of a proxy CONNECT.
        // Neither system settings nor the product's browser arguments are changed.
        string args = RuntimeIsolationPolicy.BrowserArguments + " --log-net-log=\"" + netLogPath + "\" --net-log-capture-mode=Default --no-proxy-server";
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(runtime, Path.Combine(caseRoot, "profile"), new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = args });
        TaskCompletionSource<bool> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        environment.BrowserProcessExited += (_, _) => exited.TrySetResult(true);
        browserVersion = environment.BrowserVersionString;
        browser = new() { Dock = DockStyle.Fill }; Controls.Add(browser);
        try
        {
            await browser.EnsureCoreWebView2Async(environment);
            CoreWebView2 core = browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false; core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false; core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.PermissionRequested += (_, e) => {
                bool ownedNetworkControl = label == "A" && e.PermissionKind.ToString().Contains("Network", StringComparison.Ordinal);
                lifecycle.Add(new { @case = label, @event = "permission", kind = e.PermissionKind.ToString(), allowed_for_owned_network_control = ownedNetworkControl });
                e.State = ownedNetworkControl ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny; e.Handled = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.NavigationStarting += (_, e) => { if (e.Uri != origin + "index.html") e.Cancel = true; };
            core.FrameNavigationStarting += (_, e) => e.Cancel = label != "A" || !e.Uri.StartsWith(httpBase + "/", StringComparison.Ordinal);
            core.ProcessFailed += (_, e) =>
            {
                processFailed = true;
                lifecycle.Add(new { @case = label, @event = "process_failed", kind = e.ProcessFailedKind.ToString(), reason = e.Reason.ToString(), exitCode = e.ExitCode });
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) =>
            {
                // A is a deliberately reduced test policy, never a product launch mode.
                // Only this observer's exact owned loopback port/token may reach the wire.
                if (label == "A" && e.Request.Uri.StartsWith(httpBase + "/", StringComparison.Ordinal)) return;
                string relative = e.Request.Uri.StartsWith(origin, StringComparison.Ordinal) ? e.Request.Uri[origin.Length..] : "";
                bool allowed = e.Request.Method == "GET" && resources.TryGetValue(relative, out var resource);
                resource = allowed ? resources[relative] : ("Forbidden", "text/plain");
                if (allowed) interceptedAllowed++; else interceptedBlocked++;
                MemoryStream stream = new(Encoding.UTF8.GetBytes(resource.Content), writable: false); streams.Add(stream);
                string headers = "Content-Type: " + resource.Mime + "; charset=utf-8\r\n" +
                    (label == "B" ? RuntimeIsolationPolicy.ResponseHeaders : "Cache-Control: no-store");
                e.Response = environment.CreateWebResourceResponse(stream, allowed ? 200 : 403, allowed ? "OK" : "Forbidden", headers);
            };
            TaskCompletionSource<bool> navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) => navigation.TrySetResult(e.IsSuccess);
            core.Navigate(origin + "index.html");
            if (!await navigation.Task.WaitAsync(TimeSpan.FromSeconds(8))) throw new InvalidOperationException("CONTROLLED_NAVIGATION_FAILED");
            await core.ExecuteScriptAsync("document.getElementById('network').click()");
            DateTimeOffset ends = DateTimeOffset.UtcNow.AddSeconds(9);
            while (DateTimeOffset.UtcNow < ends)
            {
                if (processFailed) throw new InvalidOperationException("CONTROLLED_RENDERER_FAILED");
                string result = await core.ExecuteScriptAsync("document.getElementById('results').value");
                using JsonDocument outer = JsonDocument.Parse(result);
                if (outer.RootElement.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(outer.RootElement.GetString()))
                {
                    using JsonDocument inner = JsonDocument.Parse(outer.RootElement.GetString()!);
                    observed = inner.RootElement.Clone();
                    if (inner.RootElement.TryGetProperty("network", out var network) && network.TryGetProperty("complete", out var complete) && complete.ValueKind == JsonValueKind.True) break;
                }
                await Task.Delay(80);
            }
            await Task.Delay(250);
            string extraHints = await core.ExecuteScriptAsync("JSON.stringify(globalThis.__ownedNetworkExtraHints ?? null)");
            using (JsonDocument hintOuter = JsonDocument.Parse(extraHints))
                if (hintOuter.RootElement.ValueKind == JsonValueKind.String)
                {
                    using JsonDocument hintInner = JsonDocument.Parse(hintOuter.RootElement.GetString()!);
                    observedHints = hintInner.RootElement.Clone();
                }
        }
        catch (Exception error) { localFailure = new { type = error.GetType().Name, code = error.Message }; }
        finally
        {
            browser.Dispose(); Controls.Remove(browser); browser = null;
            foreach (Stream stream in streams) stream.Dispose();
            try { browserExited = await exited.Task.WaitAsync(TimeSpan.FromSeconds(8)); } catch (TimeoutException) { }
        }
        await Task.Delay(100);
        int extraTcp = observer.TcpCount - controlTcp, extraUdp = observer.UdpCount - controlUdp;
        int beforeAfterControlTcp = observer.TcpCount, beforeAfterControlUdp = observer.UdpCount;
        await observer.VerifyPositiveControlAsync();
        bool observerStillAlive = observer.TcpCount > beforeAfterControlTcp && observer.UdpCount > beforeAfterControlUdp;
        List<object> dnsEvents = [], preconnectEvents = [];
        bool validNetLog = false;
        if (File.Exists(netLogPath))
        {
            try
            {
                using JsonDocument log = JsonDocument.Parse(File.ReadAllText(netLogPath));
                Dictionary<int, string> eventNames = log.RootElement.GetProperty("constants").GetProperty("logEventTypes").EnumerateObject().ToDictionary(p => p.Value.GetInt32(), p => p.Name);
                foreach (JsonElement entry in log.RootElement.GetProperty("events").EnumerateArray())
                {
                    string name = eventNames.GetValueOrDefault(entry.GetProperty("type").GetInt32(), "unknown");
                    if (!name.Contains("HOST_RESOLVER", StringComparison.Ordinal) || !entry.TryGetProperty("params", out JsonElement parameters)) continue;
                    string raw = parameters.GetRawText();
                    object projection = new { type = name, source = entry.GetProperty("source").Clone(), phase = entry.GetProperty("phase").GetInt32(), parameters = parameters.Clone() };
                    if (raw.Contains(dnsHost, StringComparison.Ordinal)) dnsEvents.Add(projection);
                    if (raw.Contains(preconnectHost, StringComparison.Ordinal)) preconnectEvents.Add(projection);
                }
                validNetLog = true;
            }
            catch (JsonException) { }
        }
        int attempted = observed is { } value && value.TryGetProperty("network", out JsonElement n) ? n.GetProperty("started").GetArrayLength() : 0;
        bool completed = observed is { } value2 && value2.TryGetProperty("network", out JsonElement n2) && n2.GetProperty("complete").ValueKind == JsonValueKind.True;
        bool transportProof = label == "A" ? extraTcp + extraUdp > 0 : extraTcp == 0 && extraUdp == 0;
        bool dnsProof = label == "A" ? dnsEvents.Count + preconnectEvents.Count > 0 : dnsEvents.Count == 0 && preconnectEvents.Count == 0;
        bool hintsExecuted = observedHints is { ValueKind: JsonValueKind.Object } hints &&
            hints.GetProperty("inserted").ValueKind == JsonValueKind.True && hints.GetProperty("dns").GetString() == dnsHost && hints.GetProperty("preconnect").GetString() == preconnectHost;
        string[] receiverChannels = ["fetch", "xhr", "beacon", "websocket", "eventsource", "img", "remote-script", "module-import", "css-link", "fontface", "audio", "video", "iframe", "form-frame", "worker-fetch"];
        var channelChecks = receiverChannels.Select(channel => new { channel, receiver_observed = observer.RequestLines.Any(line => line.Contains("/probe/" + channel + " ", StringComparison.Ordinal)) }).ToArray();
        bool receiverControls = label == "A" ? channelChecks.All(check => check.receiver_observed) : channelChecks.All(check => !check.receiver_observed);
        bool workerControls = observed is {} outcomeRoot && outcomeRoot.TryGetProperty("network", out var networkOutcomes) && networkOutcomes.TryGetProperty("outcomes",out var outcomes)
            && (label == "A" ? outcomes.GetProperty("worker-local").GetProperty("state").GetString() == "worker_started" && outcomes.GetProperty("service-worker").GetProperty("state").GetString() == "registered"
                : outcomes.GetProperty("worker-local").GetProperty("state").GetString() != "worker_started" && outcomes.GetProperty("service-worker").GetProperty("state").GetString() != "registered");
        bool passed = localFailure is null && !processFailed && browserExited && validNetLog && completed && attempted >= 26 && hintsExecuted && transportProof && dnsProof && receiverControls && workerControls && observerStillAlive;
        cases.Add(new
        {
            @case = label, status = passed ? "passed" : "failed", runtime = browserVersion,
            policy = label == "B" ? "direct_production_headers" : "trusted_fixture_only_owned_loopback_forwarding_no_production_policy",
            fixture_sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(fixtureScript))),
            positive_control = new { tcp = controlTcp, udp = controlUdp, verified = controlTcp > 0 && controlUdp > 0 },
            positive_control_after = new { tcp = observer.TcpCount - beforeAfterControlTcp, udp = observer.UdpCount - beforeAfterControlUdp, verified = observerStillAlive },
            extra_tcp_connections = extraTcp, extra_udp_datagrams = extraUdp,
            transport_proof = transportProof, dns_proof = dnsProof, network_complete = completed, fixture_channel_attempt_count = attempted,
            extra_dns_and_preconnect_hint_count = hintsExecuted ? 2 : 0, total_channel_attempt_count = attempted + (hintsExecuted ? 2 : 0),
            extra_hints_executed = hintsExecuted, observed_extra_hints = observedHints,
            dns_target = dnsHost, preconnect_target = preconnectHost, dns_resolver_events = dnsEvents, preconnect_resolver_events = preconnectEvents,
            observer_events = observer.Events.ToArray(), intercepted_allowed_resources = interceptedAllowed, intercepted_blocked_resources = interceptedBlocked,
            channel_receiver_checks = channelChecks, per_channel_controls = receiverControls, native_worker_controls = workerControls,
            not_run = new[] { "trusted_certificate_WebTransport_handshake", "authoritative_external_DNS_receiver" },
            netlog = Path.GetRelativePath(Path.GetDirectoryName(reportPath)!, netLogPath), valid_netlog = validNetLog, browser_exited = browserExited,
            observed, process_failed = processFailed, failure = localFailure
        });
        return passed;
    }

    private void Finish()
    {
        if (finished) return;
        finished = true; deadline.Stop(); browser?.Dispose();
        object report = new
        {
            schema_version = 1, kind = "native_WebView2_network_fixture_owned_TCP_UDP_and_NetLog",
            status = ExitCode == 0 ? "passed" : "failed", started_utc = started, finished_utc = DateTimeOffset.UtcNow,
            additional_browser_arguments = RuntimeIsolationPolicy.BrowserArguments + " [test_only_log_net_log_Default_capture_and_no_proxy_server]", experimental_features = "none_enabled", chromium_sandbox = "default_enabled",
            content = "trusted_local_fixture_only_no_user_data_or_SDK", observer_scope = "127.0.0.1_ephemeral_owned_ports",
            case_isolation = "new_controller_environment_profile_and_observer_per_case", cases, lifecycle, failure
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { report = reportPath, status = ExitCode == 0 ? "passed" : "failed", cases = cases.Count }));
        if (!IsDisposed) Close();
    }

    private const string Html = """
        <!doctype html><html><head><meta charset="utf-8"><title>Controlled network isolation fixture</title><link rel="stylesheet" href="probe.css"></head><body><main>
        <h1>Owned network observation</h1><input id="unsaved"><textarea id="results" rows="9"></textarea><div id="probe-elements"></div>
        <button id="network">Network attempts</button><button id="sdk">SDK attempts</button><button id="permissions">Permissions</button><button id="storage">Storage</button>
        <button id="popup">Popup</button><button id="navigation">Navigation</button><button id="form-navigation">Form</button><button id="download">Download</button><button id="external">External</button><button id="queue-close">Queue</button>
        <script src="config.js"></script><script src="probe.js"></script><script src="hints.js"></script></main></body></html>
        """;
}

internal sealed class OwnedNetworkObserver : IDisposable
{
    private readonly TcpListener tcp = new(IPAddress.Loopback, 0);
    private readonly UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, 0));
    private readonly CancellationTokenSource ended = new();
    private int tcpCount, udpCount;
    public ConcurrentQueue<object> Events { get; } = new();
    public ConcurrentQueue<string> RequestLines { get; } = new();
    private readonly ConcurrentBag<Task> clients = [];
    public int TcpPort => ((IPEndPoint)tcp.LocalEndpoint).Port;
    public int UdpPort => ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    public int TcpCount => Volatile.Read(ref tcpCount);
    public int UdpCount => Volatile.Read(ref udpCount);

    public OwnedNetworkObserver()
    {
        tcp.Start();
        _ = Task.Run(ObserveTcpAsync);
        _ = Task.Run(ObserveUdpAsync);
    }

    private async Task ObserveTcpAsync()
    {
        try
        {
            while (!ended.IsCancellationRequested)
            {
                TcpClient client = await tcp.AcceptTcpClientAsync(ended.Token);
                Interlocked.Increment(ref tcpCount);
                clients.Add(ObserveClientAsync(client));
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task ObserveClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using CancellationTokenSource readBudget = CancellationTokenSource.CreateLinkedTokenSource(ended.Token);
                readBudget.CancelAfter(TimeSpan.FromSeconds(2));
                byte[] buffer = new byte[4096]; int bytes = 0;
                while(bytes < buffer.Length) {
                    int count = await client.GetStream().ReadAsync(buffer.AsMemory(bytes), readBudget.Token); if(count == 0) break; bytes += count;
                    if(Encoding.ASCII.GetString(buffer,0,bytes).Contains("\r\n\r\n",StringComparison.Ordinal) || buffer[0] == 0) break;
                }
                string request = Encoding.ASCII.GetString(buffer, 0, bytes);
                string requestLine = request.Split("\r\n",StringSplitOptions.None)[0];
                bool http = requestLine.EndsWith(" HTTP/1.1",StringComparison.Ordinal);
                if(http) RequestLines.Enqueue(requestLine);
                if (Events.Count < 300) Events.Enqueue(new { transport = "tcp", at = DateTimeOffset.UtcNow, bytes, protocol = http ? "http" : "other_native_transport", request_line = http ? requestLine : null });
                if (http)
                {
                    string? keyLine = request.Split("\r\n",StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(line=>line.StartsWith("Sec-WebSocket-Key:",StringComparison.OrdinalIgnoreCase));
                    if(keyLine is not null) {
                        string key = keyLine[(keyLine.IndexOf(':')+1)..].Trim();
                        string accept=Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key+"258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
                        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: "+accept+"\r\n\r\n"),ended.Token);
                        await Task.Delay(100, ended.Token);
                    } else {
                        string mime = requestLine.Contains("/eventsource ",StringComparison.Ordinal)?"text/event-stream":requestLine.Contains("/module-import ",StringComparison.Ordinal)||requestLine.Contains("/remote-script ",StringComparison.Ordinal)?"text/javascript":requestLine.Contains("/css-link ",StringComparison.Ordinal)?"text/css":"text/plain";
                        string body = mime=="text/event-stream"?"data: owned-probe\n\n":mime=="text/javascript"?"globalThis.ownedNetworkScriptExecuted=true;":mime=="text/css"?".owned-probe { color: rgb(1,2,3) }":"owned-isolation-observer";
                        byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
                        string headers="HTTP/1.1 200 OK\r\nContent-Type: "+mime+"\r\nContent-Length: "+bodyBytes.Length+"\r\nAccess-Control-Allow-Origin: *\r\nAccess-Control-Allow-Methods: GET,POST,OPTIONS\r\nAccess-Control-Allow-Headers: *\r\nConnection: close\r\n\r\n";
                        await client.GetStream().WriteAsync(Encoding.UTF8.GetBytes(headers),ended.Token); await client.GetStream().WriteAsync(bodyBytes,ended.Token);
                    }
                }
            }
            catch(Exception error) when(error is OperationCanceledException or SocketException or ObjectDisposedException or IOException) { }
        }
    }

    private async Task ObserveUdpAsync()
    {
        try
        {
            while (!ended.IsCancellationRequested)
            {
                UdpReceiveResult result = await udp.ReceiveAsync(ended.Token);
                Interlocked.Increment(ref udpCount);
                bool stunMagicCookie = result.Buffer.Length >= 8 && result.Buffer[4] == 0x21 && result.Buffer[5] == 0x12 && result.Buffer[6] == 0xa4 && result.Buffer[7] == 0x42;
                if (Events.Count < 300) Events.Enqueue(new { transport = "udp", at = DateTimeOffset.UtcNow, bytes = result.Buffer.Length, protocol = "owned_datagram", stun_magic_cookie = stunMagicCookie, stun_message_type = stunMagicCookie ? (int?)((result.Buffer[0] << 8) | result.Buffer[1]) : null });
            }
        }
        catch (Exception error) when (error is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    public async Task VerifyPositiveControlAsync()
    {
        int initialTcp = TcpCount, initialUdp = UdpCount;
        using (TcpClient client = new())
        {
            await client.ConnectAsync(IPAddress.Loopback, TcpPort);
            await client.GetStream().WriteAsync("GET /owned-positive-control HTTP/1.1\r\nHost: 127.0.0.1\r\n\r\n"u8.ToArray());
            byte[] response = new byte[256];
            using CancellationTokenSource budget = new(TimeSpan.FromSeconds(2));
            _ = await client.GetStream().ReadAsync(response, budget.Token);
        }
        using UdpClient sender = new();
        await sender.SendAsync("owned-positive-control"u8.ToArray(), new IPEndPoint(IPAddress.Loopback, UdpPort));
        DateTimeOffset waitEnds = DateTimeOffset.UtcNow.AddSeconds(2);
        while ((TcpCount <= initialTcp || UdpCount <= initialUdp) && DateTimeOffset.UtcNow < waitEnds) await Task.Delay(20);
        if (TcpCount <= initialTcp || UdpCount <= initialUdp) throw new InvalidOperationException("OWNED_OBSERVER_POSITIVE_CONTROL_FAILED");
    }

    public void Dispose() { ended.Cancel(); tcp.Stop(); udp.Dispose(); try {Task.WhenAll(clients).GetAwaiter().GetResult();} catch(OperationCanceledException) {} ended.Dispose(); }
}
