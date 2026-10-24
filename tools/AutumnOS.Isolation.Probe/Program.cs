using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutumnOS.Runtime;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutumnOS.Isolation.Probe;

internal static class Program
{
    [STAThread]
    private static int Main(string[] arguments)
    {
        if (arguments.Length is not (4 or 6) || arguments[0] != "--runtime" || arguments[2] != "--report" || (arguments.Length == 6 && (arguments[4] != "--mode" || arguments[5] is not ("basic" or "offer" or "top" or "runtime" or "runtime-ab" or "network" or "resources-storage" or "same-origin"))))
        {
            Console.Error.WriteLine("Usage: --runtime <fixed-runtime-directory> --report <new-report.json> [--mode basic|top|offer|runtime|runtime-ab|network|resources-storage|same-origin]");
            return 2;
        }
        string runtime = Path.GetFullPath(arguments[1]);
        string report = Path.GetFullPath(arguments[3]);
        if (File.Exists(report)) { Console.Error.WriteLine("Report already exists; preserve earlier evidence."); return 2; }
        if (!File.Exists(Path.Combine(runtime, "msedgewebview2.exe"))) { Console.Error.WriteLine("Fixed WebView2 runtime is missing."); return 2; }
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (arguments.Length == 6 && arguments[5] == "same-origin")
        {
            using SameOriginWindow originWindow = new(runtime, report);
            Application.Run(originWindow);
            return originWindow.ExitCode;
        }
        if (arguments.Length == 6 && arguments[5] == "resources-storage")
        {
            using ResourceStorageWindow resourceWindow = new(runtime, report);
            Application.Run(resourceWindow);
            return resourceWindow.ExitCode;
        }
        if (arguments.Length == 6 && arguments[5] == "network")
        {
            using NetworkPolicyWindow networkWindow = new(runtime, report);
            Application.Run(networkWindow);
            return networkWindow.ExitCode;
        }
        if (arguments.Length == 6 && arguments[5].StartsWith("runtime", StringComparison.Ordinal))
        {
            using RuntimePolicyWindow policyWindow = new(runtime, report, arguments[5] == "runtime-ab");
            Application.Run(policyWindow);
            return policyWindow.ExitCode;
        }
        using ProbeWindow window = new(runtime, report, arguments.Length == 6 ? arguments[5] : "basic");
        Application.Run(window);
        return window.ExitCode;
    }
}

internal sealed class ProbeWindow : Form
{
    private const string Policy = "(response-origin); redirects=block; webrtc=block";
    private readonly string runtime;
    private readonly string reportPath;
    private readonly string mode;
    private readonly string origin = "https://isolation-" + Guid.NewGuid().ToString("N") + ".autumn.invalid/";
    private readonly WebView2 browser = new() { Dock = DockStyle.Fill };
    private readonly List<Stream> streams = [];
    private readonly List<object> observations = [];
    private readonly List<object> intercepted = [];
    private readonly List<object> lifecycle = [];
    private readonly System.Windows.Forms.Timer deadline = new() { Interval = 30000 };
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private TaskCompletionSource<JsonElement>? current;
    private string activeCase = "none";
    private string? browserVersion;
    private object? failure;
    private bool finished;
    public int ExitCode { get; private set; } = 1;

    public ProbeWindow(string runtime, string reportPath, string mode)
    {
        this.runtime = runtime;
        this.reportPath = reportPath;
        this.mode = mode;
        Text = "AutumnOS isolation probe · controlled test content";
        Width = 720; Height = 420;
        Controls.Add(browser);
        Shown += async (_, _) => await RunProbeAsync();
        deadline.Tick += (_, _) => { failure = new { type = "Timeout", stage = activeCase }; Finish(); };
        FormClosing += (_, _) => { if (!finished) { failure ??= new { type = "WindowClosed", stage = activeCase }; Finish(); } };
    }

    private async Task RunProbeAsync()
    {
        deadline.Start();
        try
        {
            string profile = Path.Combine(Path.GetDirectoryName(reportPath)!, "profile-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(profile);
            // Keep Chromium's default sandbox. Use the same auxiliary mDNS suppression
            // as the product; the A/B native response policy remains the tested boundary.
            CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(runtime, profile, new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = RuntimeIsolationPolicy.BrowserArguments });
            browserVersion = environment.BrowserVersionString;
            await browser.EnsureCoreWebView2Async(environment);
            CoreWebView2 core = browser.CoreWebView2;
            core.ProcessFailed += (_, e) => lifecycle.Add(new { @case = activeCase, @event = "process_failed", kind = e.ProcessFailedKind.ToString(), reason = e.Reason.ToString(), exitCode = e.ExitCode });
            core.NavigationStarting += (_, e) => lifecycle.Add(new { @case = activeCase, @event = "navigation_start", navigation = e.NavigationId });
            core.NavigationCompleted += (_, e) => lifecycle.Add(new { @case = activeCase, @event = "navigation_complete", navigation = e.NavigationId, success = e.IsSuccess, error = e.WebErrorStatus.ToString() });
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) =>
            {
                bool page = e.Request.Uri == origin + activeCase + "/index.html";
                bool script = e.Request.Uri == origin + activeCase + "/probe.js";
                bool allowed = e.Request.Method == "GET" && (page || script);
                if (intercepted.Count < 100) intercepted.Add(new { @case = activeCase, resource = page ? "controlled_html" : script ? "controlled_script" : "other_blocked", method = e.Request.Method });
                string content = page ? Html : script ? "const negotiate = " + (mode == "offer" ? "true" : "false") + "; const includeFrame = " + (mode == "top" || mode == "offer" ? "false" : "true") + ";\n" + Script : "Blocked";
                MemoryStream stream = new(Encoding.UTF8.GetBytes(content), writable: false);
                streams.Add(stream);
                string headers = "Content-Type: " + (page ? "text/html" : "text/javascript") + "; charset=utf-8\r\nCache-Control: no-store\r\n";
                if (activeCase == "B") headers += "Connection-Allowlist: " + Policy + "\r\n";
                e.Response = environment.CreateWebResourceResponse(stream, allowed ? 200 : 403, allowed ? "OK" : "Forbidden", headers);
            };
            core.WebMessageReceived += (_, e) =>
            {
                if (e.Source != origin + activeCase + "/index.html") return;
                try
                {
                    using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
                    if (document.RootElement.TryGetProperty("stage", out JsonElement stage)) lifecycle.Add(new { @case = activeCase, @event = "script_stage", stage = stage.GetString(), detail = document.RootElement.Clone() });
                    else current?.TrySetResult(document.RootElement.Clone());
                }
                catch (JsonException) { current?.TrySetException(new InvalidDataException("Controlled probe returned malformed JSON.")); }
            };
            // Start with the protected document. This also ensures the protected-case
            // observation cannot inherit an unprotected document's live native objects.
            foreach (string label in new[] { "B" })
            {
                activeCase = label;
                current = new(TaskCreationOptions.RunContinuationsAsynchronously);
                core.Navigate(origin + label + "/index.html");
                JsonElement observed = await current.Task.WaitAsync(TimeSpan.FromSeconds(10));
                observations.Add(new { @case = label, connection_allowlist = label == "B" ? Policy : null, observed });
            }
            ExitCode = 0;
        }
        catch (Exception error)
        {
            failure = new { type = error.GetType().Name, stage = activeCase, hResult = "0x" + unchecked((uint)error.HResult).ToString("X8") };
        }
        Finish();
    }

    private void Finish()
    {
        if (finished) return;
        finished = true;
        deadline.Stop();
        browser.Dispose();
        foreach (Stream stream in streams) stream.Dispose();
        object report = new
        {
            schema_version = 1,
            kind = "real_fixed_WebView2_Connection_Allowlist_controlled_native_probe",
            status = ExitCode == 0 ? "observed" : "failed",
            started_utc = started, finished_utc = DateTimeOffset.UtcNow,
            expected_runtime = "154.0.4258.53", actual_runtime = browserVersion,
            browser_arguments = RuntimeIsolationPolicy.BrowserArguments, experimental_feature_switches = "none_enabled", chromium_sandbox = "default_enabled",
            external_host_objects = false, content = "controlled_local_HTML_and_JavaScript_only",
            mode,
            network_offer_generation = mode == "offer" ? "controlled_relay_only_double_peer_offer_answer_no_TURN" : "none_no_offer_or_setLocalDescription",
            intended_network_target = "stun:127.0.0.1:9_configuration_only",
            observations, intercepted, lifecycle, failure,
            interpretation = "Observation only: controlled native RTC state. Relay-only configuration contains no TURN service; no external network destination is used."
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { report = reportPath, status = ExitCode == 0 ? "observed" : "failed", actual_runtime = browserVersion, cases = observations.Count }));
        if (!IsDisposed) Close();
    }

    private const string Html = """
        <!doctype html><html><head><meta charset="utf-8"><title>AutumnOS isolation probe</title></head>
        <body><h1>Controlled WebView2 isolation probe</h1><p>Two local cases; no offer or external requests are generated.</p><script src="probe.js"></script></body></html>
        """;

    private const string Script = """
        'use strict';
        async function probeRealm(realm, name) {
          window.chrome.webview.postMessage({ stage: 'realm_before_rtc', realm: name });
          const result = { realm: name, href: realm.location.href, rtcAvailable: typeof realm.RTCPeerConnection === 'function' };
          if (!result.rtcAvailable) return result;
          let peer, answerPeer;
          try {
            peer = new realm.RTCPeerConnection({ iceServers: [{ urls: ['stun:127.0.0.1:9'] }], iceTransportPolicy: 'relay' });
            result.configuration = peer.getConfiguration();
            window.chrome.webview.postMessage({ stage: 'realm_configuration', realm: name, configuration: result.configuration });
            result.states = [{ connection: peer.connectionState, gathering: peer.iceGatheringState, ice: peer.iceConnectionState }];
            result.candidates = [];
            peer.addEventListener('icecandidate', e => result.candidates.push(e.candidate ? e.candidate.candidate : null));
            peer.addEventListener('iceconnectionstatechange', () => result.states.push({ connection: peer.connectionState, gathering: peer.iceGatheringState, ice: peer.iceConnectionState }));
            if (negotiate) {
              answerPeer = new realm.RTCPeerConnection({ iceServers: [{ urls: ['stun:127.0.0.1:9'] }], iceTransportPolicy: 'relay' });
              result.answerConfiguration = answerPeer.getConfiguration();
              peer.createDataChannel('controlled-local-probe');
              window.chrome.webview.postMessage({ stage: 'before_offer', realm: name });
              await peer.setLocalDescription(await peer.createOffer());
              await answerPeer.setRemoteDescription(peer.localDescription);
              await answerPeer.setLocalDescription(await answerPeer.createAnswer());
              await peer.setRemoteDescription(answerPeer.localDescription);
              window.chrome.webview.postMessage({ stage: 'offer_answer_complete', realm: name });
            }
            await new Promise(resolve => setTimeout(resolve, 1500));
            window.chrome.webview.postMessage({ stage: 'realm_before_final_configuration', realm: name });
            result.finalConfiguration = peer.getConfiguration();
            window.chrome.webview.postMessage({ stage: 'realm_after_final_configuration', realm: name, configuration: result.finalConfiguration });
            result.finalState = { connection: peer.connectionState, gathering: peer.iceGatheringState, ice: peer.iceConnectionState };
            if (answerPeer) result.answerFinalState = { connection: answerPeer.connectionState, gathering: answerPeer.iceGatheringState, ice: answerPeer.iceConnectionState };
          } catch (error) { result.error = { name: error.name, message: String(error.message).slice(0, 300) }; }
          finally { if (peer) { window.chrome.webview.postMessage({ stage: 'realm_before_close', realm: name }); peer.close(); if (answerPeer) answerPeer.close(); window.chrome.webview.postMessage({ stage: 'realm_after_close', realm: name }); } }
          return result;
        }
        (async () => {
          window.chrome.webview.postMessage({ stage: 'script_started' });
          const realms = [await probeRealm(window, 'top')];
          if (includeFrame) {
            const frame = document.createElement('iframe');
            frame.title = 'initial about:blank realm';
            document.body.appendChild(frame);
            realms.push(await probeRealm(frame.contentWindow, 'initial-about-blank'));
          }
          window.chrome.webview.postMessage({ userAgent: navigator.userAgent, secureContext: isSecureContext, realms });
        })().catch(error => window.chrome.webview.postMessage({ fatal: { name: error.name, message: String(error.message).slice(0, 300) } }));
        """;
}
