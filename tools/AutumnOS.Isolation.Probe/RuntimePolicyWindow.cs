using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.Runtime;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutumnOS.Isolation.Probe;

/// <summary>Runs the referenced production policy and its actual auxiliary browser arguments.</summary>
internal sealed class RuntimePolicyWindow : Form
{
    private readonly string runtime;
    private readonly string reportPath;
    private readonly bool includeNegative;
    private readonly DateTimeOffset started = DateTimeOffset.UtcNow;
    private readonly List<object> cases = [];
    private readonly List<object> lifecycle = [];
    private readonly System.Windows.Forms.Timer deadline = new() { Interval = 45000 };
    private WebView2? browser;
    private bool finished;
    private object? failure;
    public int ExitCode { get; private set; } = 1;

    public RuntimePolicyWindow(string runtime, string reportPath, bool includeNegative)
    {
        this.runtime = runtime; this.reportPath = reportPath; this.includeNegative = includeNegative;
        Text = "AutumnOS production isolation policy · controlled native check";
        Width = 720; Height = 420;
        Shown += async (_, _) => await RunAsync();
        deadline.Tick += (_, _) => { failure = new { type = "ProbeDeadline" }; Finish(); };
        FormClosing += (_, _) => { if (!finished) { failure ??= new { type = "WindowClosed" }; Finish(); } };
    }

    private async Task RunAsync()
    {
        deadline.Start();
        try
        {
            foreach (string label in includeNegative ? new[] { "A", "B" } : new[] { "B" })
                await RunCaseAsync(label);
            ExitCode = 0;
        }
        catch (Exception error) { failure = new { type = error.GetType().Name, hResult = "0x" + unchecked((uint)error.HResult).ToString("X8") }; }
        Finish();
    }

    private async Task RunCaseAsync(string label)
    {
        string profile = Path.Combine(Path.GetDirectoryName(reportPath)!, label + "-profile-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(profile);
        string origin = "https://runtime-isolation-" + Guid.NewGuid().ToString("N") + ".autumn.invalid/";
        RuntimeIsolationChallenge challenge = RuntimeIsolationPolicy.CreateChallenge(Guid.NewGuid(), origin);
        string scriptSha = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(challenge.Script)));
        List<Stream> streams = [];
        bool processFailed = false;
        int packageRequests = 0, otherRequests = 0;
        CoreWebView2Environment environment = await CoreWebView2Environment.CreateAsync(runtime, profile, new CoreWebView2EnvironmentOptions { AdditionalBrowserArguments = RuntimeIsolationPolicy.BrowserArguments });
        browser = new() { Dock = DockStyle.Fill };
        Controls.Add(browser);
        try
        {
            await browser.EnsureCoreWebView2Async(environment);
            CoreWebView2 core = browser.CoreWebView2;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
            core.ProcessFailed += (_, e) =>
            {
                processFailed = true;
                lifecycle.Add(new { @case = label, @event = "process_failed", kind = e.ProcessFailedKind.ToString(), reason = e.Reason.ToString(), exitCode = e.ExitCode });
            };
            core.NavigationStarting += (_, e) => { if (e.Uri != challenge.PageUri) e.Cancel = true; };
            core.FrameNavigationStarting += (_, e) => e.Cancel = true;
            TaskCompletionSource<bool> navigation = new(TaskCreationOptions.RunContinuationsAsynchronously);
            core.NavigationCompleted += (_, e) =>
            {
                lifecycle.Add(new { @case = label, @event = "navigation_complete", success = e.IsSuccess, error = e.WebErrorStatus.ToString() });
                navigation.TrySetResult(e.IsSuccess);
            };
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested += (_, e) =>
            {
                bool page = e.Request.Uri == challenge.PageUri;
                bool script = e.Request.Uri == origin + challenge.ScriptPath;
                bool allowed = e.Request.Method == "GET" && (page || script);
                if (allowed) packageRequests++; else otherRequests++;
                MemoryStream stream = new(Encoding.UTF8.GetBytes(page ? challenge.DocumentHtml : script ? challenge.Script : "Forbidden"), writable: false);
                streams.Add(stream);
                string headers = "Content-Type: " + (page ? "text/html" : "text/javascript") + "; charset=utf-8\r\n" +
                    (label == "B" ? RuntimeIsolationPolicy.ResponseHeaders : "Content-Security-Policy: " + RuntimeIsolationPolicy.ContentSecurityPolicy + "\r\nCache-Control: no-store");
                e.Response = environment.CreateWebResourceResponse(stream, allowed ? 200 : 403, allowed ? "OK" : "Forbidden", headers);
            };
            core.Navigate(challenge.PageUri);
            if (!await navigation.Task.WaitAsync(TimeSpan.FromSeconds(10))) throw new InvalidOperationException("CONTROLLED_NAVIGATION_FAILED");
            DateTimeOffset resultDeadline = DateTimeOffset.UtcNow.AddSeconds(8);
            string? nativeJson = null;
            JsonElement? nativeResult = null;
            RuntimeIsolationProof? proof = null;
            while (DateTimeOffset.UtcNow < resultDeadline)
            {
                if (processFailed) throw new InvalidOperationException("CONTROLLED_RENDERER_FAILED");
                if (core.Source != challenge.PageUri) throw new InvalidOperationException("CONTROLLED_PAGE_SOURCE_MISMATCH");
                nativeJson = await core.ExecuteScriptAsync(challenge.ResultExpression);
                if (core.Source != challenge.PageUri || processFailed) throw new InvalidOperationException("CONTROLLED_PAGE_SOURCE_MISMATCH");
                using JsonDocument outer = JsonDocument.Parse(nativeJson);
                if (outer.RootElement.ValueKind == JsonValueKind.String)
                {
                    using JsonDocument inner = JsonDocument.Parse(outer.RootElement.GetString()!);
                    if (inner.RootElement.ValueKind == JsonValueKind.Object)
                    {
                        nativeResult = inner.RootElement.Clone();
                        proof = RuntimeIsolationPolicy.ValidateNativeResult(challenge, nativeJson);
                        break;
                    }
                }
                await Task.Delay(40);
            }
            bool bound = proof is not null && proof.InstanceId == challenge.InstanceId && proof.Origin == challenge.Origin;
            bool negativeObserved = nativeResult is { } actual && actual.TryGetProperty("probes", out JsonElement probes) && probes.ValueKind == JsonValueKind.Array && probes.GetArrayLength() == 2;
            bool passed = nativeResult is not null && !processFailed && (label == "B" ? bound : proof is null && negativeObserved);
            cases.Add(new
            {
                @case = label, status = passed ? "passed" : "failed", runtime = environment.BrowserVersionString,
                policy = label == "B" ? "direct_RuntimeIsolationPolicy_ResponseHeaders" : "original_CSP_without_Connection_Allowlist",
                challenge_instance = challenge.InstanceId, script_sha256 = scriptSha,
                native_execute_script_result = nativeJson, observed = nativeResult,
                validator_produced_proof = proof is not null, proof_bound_to_instance_and_origin = bound,
                source_checked_before_and_after = true, process_failed = processFailed,
                controlled_resources = packageRequests, blocked_other_resources = otherRequests,
                web_message_result_trust = "never_used"
            });
            if (!passed) throw new InvalidOperationException(label == "B" ? "PRODUCTION_POLICY_PROOF_NOT_PRODUCED" : "NEGATIVE_CONTROL_DID_NOT_COMPLETE_OR_WAS_ACCEPTED");
        }
        finally
        {
            browser.Dispose(); Controls.Remove(browser); browser = null;
            foreach (Stream stream in streams) stream.Dispose();
        }
    }

    private void Finish()
    {
        if (finished) return;
        finished = true; deadline.Stop(); browser?.Dispose();
        object report = new
        {
            schema_version = 1, kind = "production_RuntimeIsolationPolicy_direct_source_native_proof",
            status = ExitCode == 0 ? "passed" : "failed", started_utc = started, finished_utc = DateTimeOffset.UtcNow,
            linked_source = "src/AutumnOS.Runtime/RuntimeIsolationPolicy.cs", protocol = "host_ExecuteScriptAsync_only",
            browser_arguments = RuntimeIsolationPolicy.BrowserArguments, experimental_features = "none_enabled", chromium_sandbox = "default_enabled",
            controller_environment_profile = "independent_new_for_each_case", iframe_rtc = "never_created",
            packages_or_SDK = "never_loaded", cases, lifecycle, failure
        };
        File.WriteAllText(reportPath, JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine(JsonSerializer.Serialize(new { report = reportPath, status = ExitCode == 0 ? "passed" : "failed", cases = cases.Count }));
        if (!IsDisposed) Close();
    }
}
