using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutumnOS.Runtime;

/// <summary>Browser-enforced policy and host-only, instance-bound startup challenge.</summary>
public static class RuntimeIsolationPolicy
{
    public const string PolicyId = "autumn-local-web-v2";
    public const string RequiredBrowserVersion = "154.0.4258.53";
    // This fixed engine constructs an mDNS listener during RTC initialization even when
    // the document's Connection-Allowlist blocks ICE. Suppress that optional listener;
    // this is NOT the network boundary. The native response policy, mandatory challenge
    // and receiver-side negative tests remain required. No sandbox option is disabled.
    public const string BrowserArguments = "--force-renderer-accessibility --disable-features=WebRtcHideLocalIpsWithMdns";
    // WASM compilation from package-owned JavaScript bytes is supported without JavaScript eval.
    // Do not widen connect-src to 'self': that also admits native same-origin WebSocket/WebTransport
    // paths that WebResourceRequested does not reliably intercept. Fetch/XHR, including local URLs,
    // stay unavailable until a separately enforced and validated resource/network capability exists.
    public const string ContentSecurityPolicy = "default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'none'; frame-src 'none'; child-src 'none'; worker-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    public const string ConnectionAllowlist = "(response-origin); redirects=block; webrtc=block";
    public const string DnsPrefetchControl = "off";
    // WebView2's ClipboardRead permission event does not cover async clipboard writes.
    // The native Permissions Policy check must deny both asynchronous directions before execution.
    // Legacy user-initiated copy/cut/paste is a separate browser editing surface, not certified here.
    public const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), usb=(), serial=(), bluetooth=(), display-capture=(), clipboard-read=(), clipboard-write=()";
    public const string ResponseHeaders = "Content-Security-Policy: " + ContentSecurityPolicy
        + "\r\nConnection-Allowlist: " + ConnectionAllowlist + "\r\nX-DNS-Prefetch-Control: " + DnsPrefetchControl
        + "\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer"
        + "\r\nPermissions-Policy: " + PermissionsPolicy;
    public const int MaximumNativeResultBytes = 8 * 1024;

    /// <summary>Admit only the exact engine covered by this policy's native validation evidence.</summary>
    public static bool IsSupportedBrowserVersion(string? browserVersion) =>
        string.Equals(browserVersion, RequiredBrowserVersion, StringComparison.Ordinal);

    public static RuntimeIsolationChallenge CreateChallenge(Guid instanceId, string origin)
    {
        if (instanceId == Guid.Empty) throw new ArgumentException("An active runtime instance is required.", nameof(instanceId));
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.AbsolutePath != "/" || uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0
            || !string.Equals(uri.AbsoluteUri, origin, StringComparison.Ordinal))
            throw new ArgumentException("A canonical trusted HTTPS origin with a trailing slash is required.", nameof(origin));
        string nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        string prefix = "__autumn_isolation_" + nonce;
        string scriptPath = prefix + ".js";
        string binding = JsonSerializer.Serialize(new { protocolVersion = 1, nonce, instanceId = instanceId.ToString("D"), origin });
        string script = "(() => { 'use strict'; const binding = " + binding + ";\n" + ProbeScript + "\n})();";
        string html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>AutumnOS runtime isolation check</title>"
            + "<script src=\"" + scriptPath + "\" defer></script></head><body>Checking runtime isolation.</body></html>";
        return new RuntimeIsolationChallenge(instanceId, origin, nonce, prefix + ".html", scriptPath, html, script);
    }

    /// <summary>
    /// Accepts only the exact ExecuteScriptAsync JSON string returned from the host challenge page.
    /// The Shell must verify its native Core.Source against challenge.PageUri before and after evaluation;
    /// WebMessageReceived payloads must never be passed here. This verifies native policy support, not
    /// an attacker-provided statement about network safety.
    /// </summary>
    public static RuntimeIsolationProof? ValidateNativeResult(RuntimeIsolationChallenge challenge, string? executeScriptAsyncJson)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (executeScriptAsyncJson is null || Encoding.UTF8.GetByteCount(executeScriptAsyncJson) > MaximumNativeResultBytes) return null;
        try
        {
            using var outer = JsonDocument.Parse(executeScriptAsyncJson, new JsonDocumentOptions { MaxDepth = 8 });
            if (outer.RootElement.ValueKind != JsonValueKind.String) return null;
            string? report = outer.RootElement.GetString();
            if (report is null || Encoding.UTF8.GetByteCount(report) > MaximumNativeResultBytes) return null;
            using var document = JsonDocument.Parse(report, new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (!ExactProperties(root, "protocolVersion", "nonce", "instanceId", "origin", "completed", "secureContext", "probes")
                || !ExactInteger(root, "protocolVersion", 1) || !ExactString(root, "nonce", challenge.Nonce)
                || !ExactString(root, "instanceId", challenge.InstanceId.ToString("D"))
                || !ExactString(root, "origin", challenge.Origin) || !ExactTrue(root, "completed") || !ExactTrue(root, "secureContext")) return null;
            var probes = root.GetProperty("probes");
            if (probes.ValueKind != JsonValueKind.Array || probes.GetArrayLength() != 2) return null;
            int index = 0;
            foreach (var probe in probes.EnumerateArray())
            {
                if (!ExactProperties(probe, "label", "nativeConstructor", "filteredIceServers", "relayOnly", "iceConnectionState", "connectionState", "candidates", "restartedIceConnectionState")
                    || !ExactString(probe, "label", "peer-" + (++index)) || !ExactTrue(probe, "nativeConstructor")
                    || !ExactTrue(probe, "filteredIceServers") || !ExactTrue(probe, "relayOnly")
                    || !ExactString(probe, "iceConnectionState", "failed") || !UnconnectedState(probe)
                    || !ExactInteger(probe, "candidates", 0) || !ExactString(probe, "restartedIceConnectionState", "failed")) return null;
            }
            return new RuntimeIsolationProof(challenge.InstanceId, challenge.Origin);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { return null; }
    }

    private static bool ExactProperties(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var remaining = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject()) if (!remaining.Remove(property.Name)) return false;
        return remaining.Count == 0;
    }

    private static bool ExactString(JsonElement value, string name, string expected) =>
        value.GetProperty(name).ValueKind == JsonValueKind.String && string.Equals(value.GetProperty(name).GetString(), expected, StringComparison.Ordinal);
    private static bool ExactTrue(JsonElement value, string name) => value.GetProperty(name).ValueKind == JsonValueKind.True;
    private static bool ExactInteger(JsonElement value, string name, int expected) =>
        value.GetProperty(name).ValueKind == JsonValueKind.Number && value.GetProperty(name).TryGetInt32(out int actual) && actual == expected;

    // WebView2 154 keeps aggregate connectionState=new while ICE has administratively failed.
    // A future runtime may propagate failed; connected/connecting/disconnected/closed never attest.
    private static bool UnconnectedState(JsonElement value) => value.GetProperty("connectionState").ValueKind == JsonValueKind.String
        && value.GetProperty("connectionState").GetString() is "new" or "failed";

    // Relay-only negotiation without any TURN server cannot produce a local, srflx or relay
    // candidate even on an unsupported browser. The non-routable-to-the-Internet sentinel is
    // deliberately a literal loopback STUN address, avoiding DNS during the host-owned check.
    // Native policy filters the configuration and administratively fails ICE. Exceptions/timeouts
    // never attest. No attacker code or package resource is served in this document.
    private const string ProbeScript = """
        const resultName = '__autumnIsolationProbeResult';
        let storedResult = null;
        Object.defineProperty(globalThis, resultName, { get: () => storedResult, configurable: false });
        const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
        const publish = probes => {
            storedResult = Object.freeze({ ...binding, origin: location.origin + '/', completed: true,
                secureContext: globalThis.isSecureContext === true, probes: Object.freeze(probes) });
        };
        (async () => {
            const peers = [];
            try {
                const NativePeer = globalThis.RTCPeerConnection;
                if (typeof NativePeer !== 'function' || !Function.prototype.toString.call(NativePeer).includes('[native code]')) {
                    publish([]); return;
                }
                for (let index = 0; index < 2; index++) {
                    const pc = new NativePeer({ iceTransportPolicy: 'relay', iceServers: [
                        { urls: 'stun:127.0.0.1:9' }
                    ] });
                    const item = { pc, label: 'peer-' + (index + 1), candidates: 0 };
                    pc.addEventListener('icecandidate', event => { if (event.candidate !== null) item.candidates++; });
                    peers.push(item);
                }
                peers[0].pc.createDataChannel('autumn-isolation-check');
                await peers[0].pc.setLocalDescription(await peers[0].pc.createOffer());
                await peers[1].pc.setRemoteDescription(peers[0].pc.localDescription);
                await peers[1].pc.setLocalDescription(await peers[1].pc.createAnswer());
                await peers[0].pc.setRemoteDescription(peers[1].pc.localDescription);
                const deadline = performance.now() + 2000;
                while (peers.some(item => item.pc.iceConnectionState !== 'failed') && performance.now() < deadline) await sleep(20);
                const probes = peers.map(item => {
                    const config = item.pc.getConfiguration();
                    const value = { label: item.label, nativeConstructor: true,
                        filteredIceServers: Array.isArray(config.iceServers) && config.iceServers.length === 0,
                        relayOnly: config.iceTransportPolicy === 'relay',
                        iceConnectionState: item.pc.iceConnectionState, connectionState: item.pc.connectionState,
                        candidates: item.candidates, restartedIceConnectionState: '' };
                    item.pc.restartIce();
                    return value;
                });
                await sleep(80);
                for (let index = 0; index < probes.length; index++) {
                    probes[index].restartedIceConnectionState = peers[index].pc.iceConnectionState;
                    probes[index].candidates = peers[index].candidates;
                    Object.freeze(probes[index]);
                }
                publish(probes);
            } catch (_) { publish([]); }
            finally { for (const { pc } of peers) pc.close(); }
        })();
        """;
}

public sealed class RuntimeIsolationChallenge
{
    internal RuntimeIsolationChallenge(Guid instanceId, string origin, string nonce, string documentPath, string scriptPath, string documentHtml, string script)
    {
        InstanceId = instanceId; Origin = origin; Nonce = nonce; DocumentPath = documentPath;
        ScriptPath = scriptPath; DocumentHtml = documentHtml; Script = script;
    }
    public Guid InstanceId { get; }
    public string Origin { get; }
    public string Nonce { get; }
    public string DocumentPath { get; }
    public string ScriptPath { get; }
    public string PageUri => Origin + DocumentPath;
    public string DocumentHtml { get; }
    public string Script { get; }
    public string ResultExpression => "JSON.stringify(globalThis.__autumnIsolationProbeResult ?? null)";
}

/// <summary>Host proof produced only by strict native-result validation; never constructible by the SDK.</summary>
public sealed class RuntimeIsolationProof
{
    internal RuntimeIsolationProof(Guid instanceId, string origin) { InstanceId = instanceId; Origin = origin; }
    public Guid InstanceId { get; }
    public string Origin { get; }
}
