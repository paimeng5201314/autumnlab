using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AutumnOS.Runtime;
using Microsoft.Web.WebView2.Core;

namespace AutumnOS.NativeHost.Windows;

internal sealed partial class BrowserWindow
{
    private void ConfigurePolicy(CoreWebView2 native)
    {
        var settings = native.Settings;
        settings.AreHostObjectsAllowed = false; settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false; settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false; settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false; settings.IsStatusBarEnabled = false; settings.IsZoomControlEnabled = false;
        settings.IsWebMessageEnabled = false;
        native.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
        native.NewWindowRequested += (_, e) => e.Handled = true;
        native.DownloadStarting += (_, e) => e.Cancel = true;
        native.LaunchingExternalUriScheme += (_, e) => e.Cancel = true;
        native.FrameNavigationStarting += (_, e) => e.Cancel = true;
        native.NavigationStarting += (_, e) =>
        {
            if (invalidated || challenge is null || navigationId.HasValue || e.Uri != challenge.Isolation.PageUri)
            { e.Cancel = true; Revoke("BROWSER_NAVIGATION_REVOKED"); return; }
            navigationId = e.NavigationId;
        };
        native.SourceChanged += (_, e) =>
        {
            if (challenge is null || !navigationId.HasValue || native.Source != challenge.Isolation.PageUri ||
                e.IsNewDocument && (sourceCommitted || documentCommitted)) { Revoke("BROWSER_SOURCE_REVOKED"); return; }
            if (e.IsNewDocument) sourceCommitted = true;
        };
        native.ContentLoading += (_, e) =>
        {
            if (invalidated || challenge is null || e.IsErrorPage || documentCommitted ||
                e.NavigationId != navigationId || native.Source != challenge.Isolation.PageUri) { Revoke("BROWSER_CONTENT_REVOKED"); return; }
            documentCommitted = true; committed.TrySetResult(true);
        };
        native.NavigationCompleted += (_, e) =>
        {
            if (challenge is null || e.NavigationId != navigationId || !e.IsSuccess || !SameDocument || !released)
            { Revoke("BROWSER_COMPLETION_REVOKED"); return; }
            loaded.TrySetResult(true);
        };
        native.ProcessFailed += (_, _) => Revoke("BROWSER_PROCESS_REVOKED");
        native.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
        native.WebResourceRequested += Serve;
        native.WebMessageReceived += (_, e) =>
        {
            try
            {
                // Package script may call the SDK during its initial parse.
                if (!released || !SameDocument || e.Source != challenge!.Isolation.PageUri) { Revoke("BROWSER_MESSAGE_REVOKED"); return; }
                string json = e.WebMessageAsJson;
                if (json.Length > 32768 || Encoding.UTF8.GetByteCount(json) > 32768 || pendingSdk.Count >= 32) { Revoke("BROWSER_MESSAGE_REVOKED"); return; }
                pendingSdk.Enqueue(Encoding.UTF8.GetBytes(json));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { Revoke("BROWSER_MESSAGE_REVOKED"); }
        };
    }

    private void Respond(CoreWebView2WebResourceRequestedEventArgs request, byte[]? content, string mime)
    {
        Stream stream = Stream.Null;
        if (content is not null)
        {
            if (responseStreams.Count >= 512) throw new InvalidDataException("BROWSER_RESPONSE_STREAM_LIMIT");
            stream = new MemoryStream(content, writable: false);
            responseStreams.Add(stream);
        }
        request.Response = environment!.CreateWebResourceResponse(stream, content is null ? 403 : 200,
            content is null ? "Forbidden" : "OK", "Content-Type: " + mime + "\r\n" + RuntimeIsolationPolicy.ResponseHeaders);
    }

    private void Serve(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            if (!invalidated && challenge is { } c && navigationId.HasValue && e.Request.Method == "GET")
            {
                string uri = e.Request.Uri;
                if (!released)
                {
                    if (uri == c.Isolation.PageUri && e.ResourceContext == CoreWebView2WebResourceContext.Document)
                    { Respond(e, Encoding.UTF8.GetBytes(c.BootstrapHtml), "text/html; charset=utf-8"); return; }
                    if (uri == c.BootstrapScriptUri && e.ResourceContext == CoreWebView2WebResourceContext.Script)
                    { Respond(e, Encoding.UTF8.GetBytes(c.BootstrapScript), "text/javascript; charset=utf-8"); return; }
                    if (uri == c.GateUri && e.ResourceContext == CoreWebView2WebResourceContext.Script &&
                        gateRequest is null && !gateHeld.Task.IsCompleted)
                    {
                        gateRequest = e; gateDeferral = e.GetDeferral(); gateHeld.TrySetResult(true); return;
                    }
                }
                else if (SameDocument && uri.StartsWith(c.Isolation.Origin, StringComparison.Ordinal))
                {
                    string name = uri[c.Isolation.Origin.Length..];
                    if (package!.TryGetValue(name, out var content)) { Respond(e, content, Mime(name)); return; }
                }
            }
            Respond(e, null, "text/plain");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Revoke("BROWSER_RESOURCE_REVOKED");
            // A failed interception must not silently fall through to the network.
            try { Respond(e, null, "text/plain"); }
            catch (Exception fallback) when (fallback is not OutOfMemoryException) { controller?.Close(); }
        }
    }

    private static string Mime(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8",
        ".css" => "text/css; charset=utf-8", ".json" => "application/json", ".wasm" => "application/wasm",
        ".svg" => "image/svg+xml", ".png" => "image/png", ".jpg" => "image/jpeg", ".woff2" => "font/woff2",
        _ => "application/octet-stream"
    };

    private async Task<byte[]> PrepareDocumentAsync(byte[] payload)
    {
        failureStage = NativeBrowserFailureStage.ReadPackage;
        if (challenge is not null) throw new InvalidDataException("BROWSER_DOCUMENT_ONCE");
        using var message = ParseObject(payload, "origin", "nonce");
        string origin = message.RootElement.GetProperty("origin").GetString()!;
        string nonce = message.RootElement.GetProperty("nonce").GetString()!;
        if (!Regex.IsMatch(origin, @"\Ahttps://[0-9a-f]{32}\.autumn\.invalid/\z", RegexOptions.CultureInvariant) ||
            !NativeHostBinding.IsLowerSha256(nonce))
            throw new InvalidDataException("BROWSER_ORIGIN_REQUIRED");
        package = await Task.Run(startup.ReadPackage);
        revoked.Token.ThrowIfCancellationRequested();
        challenge = RuntimeIsolationPolicy.CreateParserGateChallenge(binding.InstanceId, origin, startup.PackageEntry, nonce);
        if (package.ContainsKey(challenge.Isolation.ScriptPath) || package.ContainsKey(challenge.GatePath))
            throw new InvalidDataException("BROWSER_CHALLENGE_PATH_COLLISION");
        string html = new UTF8Encoding(false, true).GetString(package[startup.PackageEntry]);
        gateScript = Encoding.UTF8.GetBytes(challenge.CreateGateScript(html.TrimStart('\uFEFF')));
        failureStage = NativeBrowserFailureStage.Navigate;
        scope.RequireBinding();
        core!.Navigate(challenge.Isolation.PageUri);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(revoked.Token);
        budget.CancelAfter(TimeSpan.FromSeconds(12));
        failureStage = NativeBrowserFailureStage.WaitCommit;
        if (!await committed.Task.WaitAsync(budget.Token))
            throw new InvalidDataException("BROWSER_BOOTSTRAP_NOT_HELD");
        failureStage = NativeBrowserFailureStage.WaitGate;
        if (!await gateHeld.Task.WaitAsync(budget.Token))
            throw new InvalidDataException("BROWSER_BOOTSTRAP_NOT_HELD");

        failureStage = NativeBrowserFailureStage.Probe;
        string nativeResult;
        do
        {
            RequireHeldGate();
            nativeResult = await core.ExecuteScriptAsync(challenge.Isolation.ResultExpression).WaitAsync(budget.Token);
            RequireHeldGate();
            if (RuntimeIsolationPolicy.ValidateNativeResult(challenge.Isolation, nativeResult) is not null)
            { probeSummary = "valid"; break; }
            probeSummary = nativeResult == "\"null\"" ? "pending" : "invalid";
            await Task.Delay(50, budget.Token);
        } while (true);
        failureStage = NativeBrowserFailureStage.Binding;
        string bindingResult = await core.ExecuteScriptAsync(challenge.BindingResultExpression).WaitAsync(budget.Token);
        RequireHeldGate();
        if (!RuntimeIsolationPolicy.ValidateParserGateBinding(challenge, bindingResult))
            throw new InvalidDataException("BROWSER_DOCUMENT_BINDING_INVALID");
        proofReady = true;
        target = new NativeWebViewDeliveryTarget(this, core, binding, challenge.Isolation.PageUri, () => SameDocument);
        target.AttachDocumentGuards();
        deliveryChannel = new BrowserDeliveryChannel(binding, target, startup.Nonce);
        return JsonSerializer.SerializeToUtf8Bytes(new { nativeResult, bindingResult });
    }

    private void RequireHeldGate()
    {
        scope.RequireBinding();
        if (invalidated || closing || !documentCommitted || released || gateDeferral is null ||
            challenge is null || core?.Source != challenge.Isolation.PageUri)
            throw new InvalidDataException("BROWSER_GATE_BINDING_LOST");
    }

    private async Task ReleaseDocumentAsync()
    {
        RequireDocument(false); RequireHeldGate();
        CompleteGate(true);
        if (!await loaded.Task.WaitAsync(TimeSpan.FromSeconds(12), revoked.Token))
            throw new InvalidDataException("BROWSER_APPLICATION_NAVIGATION_FAILED");
        RequireDocument(true);
    }

    private void CompleteGate(bool authorize)
    {
        var request = gateRequest; var deferral = gateDeferral;
        if (request is null || deferral is null)
        { if (authorize) throw new InvalidDataException("BROWSER_GATE_ALREADY_COMPLETED"); return; }
        gateRequest = null; gateDeferral = null;
        try
        {
            Respond(request, authorize ? gateScript : null, "text/javascript; charset=utf-8");
            if (authorize)
            {
                core!.Settings.IsWebMessageEnabled = true; RequireDocument(false);
                released = true;
            }
        }
        catch
        {
            released = false; Revoke();
            try { Respond(request, null, "text/plain"); }
            catch { controller?.Close(); throw; }
            throw;
        }
        finally { deferral.Complete(); }
    }

    private static JsonDocument ParseObject(byte[] bytes, params string[] names)
    {
        var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 2 });
        try
        {
            var required = new HashSet<string>(names, StringComparer.Ordinal);
            if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("BROWSER_MESSAGE_FIELDS");
            foreach (var field in json.RootElement.EnumerateObject())
                if (!required.Remove(field.Name)) throw new InvalidDataException("BROWSER_MESSAGE_FIELDS");
            if (required.Count != 0) throw new InvalidDataException("BROWSER_MESSAGE_FIELDS");
            return json;
        }
        catch { json.Dispose(); throw; }
    }
}
