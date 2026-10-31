using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutumnOS.Contracts;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

internal static class RuntimeParserGateTests
{
    private const string Origin = "https://bound.autumn.invalid/";
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("runtime.parser_gate_keeps_full_challenge_and_real_nested_entry", FullChallengeAndEntry);
        yield return ("runtime.parser_gate_validates_bootstrap_binding_without_issuing_proof", NativeBinding);
        yield return ("runtime.parser_gate_rejects_old_challenge_instance_origin_and_entry", BindingReplay);
        yield return ("runtime.parser_gate_host_HTML_and_gate_do_not_contain_unencoded_package_text", GateEncoding);
        yield return ("runtime.parser_gate_HTML_limits_count_strict_UTF8_bytes", HtmlLimits);
        yield return ("runtime.parser_gate_object_is_host_owned_and_immutable", Ownership);
        yield return ("runtime.parser_gate_rejects_raw_webmessage_duplicate_unknown_and_oversize_binding", UntrustedBinding);
        foreach (string entry in new[] { "", "/index.html", "../index.html", "ui/../index.html", "ui//index.html", "ui\\index.html",
            "https://other.invalid/index.html", "index.html?x=1", "index.html#fragment", "%69ndex.html", "NUL.html", "ui/COM1.html", "ui/.entry.html", "index.js", new string('a', 81) + ".html" })
        {
            string captured = entry;
            yield return ("runtime.parser_gate_invalid_entry_" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(entry)))[..12],
                () => Throws<ArgumentException>(() => RuntimeIsolationPolicy.CreateParserGateChallenge(Guid.NewGuid(), Origin, captured)));
        }
        foreach (string field in new[] { "protocolVersion", "bindingKind", "nonce", "instanceId", "origin", "entry", "sameDocument", "sameRoot", "readyState" })
        {
            string selected = field;
            yield return ("runtime.parser_gate_rejects_missing_" + field, () => RejectMutation(root => root.Remove(selected)));
        }
        foreach ((string name, Action<JsonObject> edit) in new (string, Action<JsonObject>)[]
        {
            ("protocol_string", root => root["protocolVersion"] = "1"),
            ("binding_kind", root => root["bindingKind"] = "network-safe"),
            ("nonce_case", root => root["nonce"] = root["nonce"]!.GetValue<string>().ToUpperInvariant()),
            ("instance_other", root => root["instanceId"] = Guid.NewGuid().ToString("D")),
            ("origin_other", root => root["origin"] = "https://other.autumn.invalid/"),
            ("entry_other", root => root["entry"] = Origin + "other.html"),
            ("entry_fragment", root => root["entry"] = root["entry"]!.GetValue<string>() + "#next"),
            ("document_replaced", root => root["sameDocument"] = false),
            ("root_replaced_even_when_document_object_is_same", root => root["sameRoot"] = false),
            ("truthy_identity", root => root["sameDocument"] = 1),
            ("already_interactive", root => root["readyState"] = "interactive"),
            ("already_complete", root => root["readyState"] = "complete"),
            ("unknown_property", root => root["sandboxVerified"] = true)
        }) yield return ("runtime.parser_gate_rejects_" + name, () => RejectMutation(edit));
    }

    private static RuntimeParserGateChallenge Challenge(string entry = "ui/game/index.html") => RuntimeIsolationPolicy.CreateParserGateChallenge(Guid.NewGuid(), Origin, entry);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected rejection: " + typeof(T).Name);
    }
    private static JsonObject Binding(RuntimeParserGateChallenge gate) => new()
    {
        ["protocolVersion"] = 1, ["bindingKind"] = "parser-gate-document-v1", ["nonce"] = gate.Isolation.Nonce,
        ["instanceId"] = gate.Isolation.InstanceId.ToString("D"), ["origin"] = gate.Isolation.Origin,
        ["entry"] = gate.Isolation.PageUri, ["sameDocument"] = true, ["sameRoot"] = true, ["readyState"] = "loading"
    };
    private static string Native(JsonNode value) => JsonSerializer.Serialize(value.ToJsonString());
    private static void RejectMutation(Action<JsonObject> edit)
    {
        var gate = Challenge(); JsonObject value = Binding(gate); edit(value);
        Check(!RuntimeIsolationPolicy.ValidateParserGateBinding(gate, Native(value)), "Changed or incomplete bootstrap binding was accepted.");
    }
    private static void FullChallengeAndEntry()
    {
        var gate = Challenge(); var old = RuntimeIsolationPolicy.CreateChallenge(gate.Isolation.InstanceId, Origin);
        static string Body(string script) => script[(script.IndexOf(";\n", StringComparison.Ordinal) + 2)..];
        Check(Body(gate.Isolation.Script) == Body(old.Script), "Parser gate changed the complete native ICE algorithm.");
        Check(gate.BootstrapScript.EndsWith(gate.Isolation.Script, StringComparison.Ordinal), "Full challenge must execute after the host binding prefix.");
        Check(gate.Isolation.PageUri == Origin + "ui/game/index.html" && gate.Isolation.DocumentHtml == gate.BootstrapHtml, "Bootstrap must retain the actual application URL.");
        Check(gate.BootstrapScriptUri == Origin + gate.Isolation.ScriptPath && gate.GateUri == Origin + gate.GatePath
            && gate.BootstrapHtml.Contains("src=\"" + gate.BootstrapScriptUri + "\"", StringComparison.Ordinal)
            && gate.BootstrapHtml.Contains("src=\"" + gate.GateUri + "\"", StringComparison.Ordinal), "Nested entries cannot resolve trusted scripts against the package subdirectory.");
        Check(!gate.BootstrapHtml.Contains("defer", StringComparison.Ordinal) && !gate.BootstrapHtml.Contains("async", StringComparison.Ordinal)
            && gate.BootstrapHtml.Split("<script", StringSplitOptions.None).Length == 3, "Only two synchronous trusted external scripts may precede the gate.");
        Check(gate.BootstrapScript.Contains("document.documentElement===initialRoot", StringComparison.Ordinal), "Binding must notice root replacement even when document.open retains the Document object.");
        Check(!gate.BootstrapScript.Contains("document.open=", StringComparison.Ordinal) && !gate.BootstrapScript.Contains("RTCPeerConnection=", StringComparison.Ordinal), "Native APIs must not be patched for the gate.");
    }
    private static void NativeBinding()
    {
        var gate = Challenge(); string native = Native(Binding(gate));
        Check(RuntimeIsolationPolicy.ValidateParserGateBinding(gate, native), "Current still-loading host binding must validate.");
        Check(RuntimeIsolationPolicy.ValidateNativeResult(gate.Isolation, native) is null, "Document identity alone cannot yield an isolation proof.");
        string proof = JsonSerializer.Serialize(JsonSerializer.Serialize(new { protocolVersion = 1, nonce = gate.Isolation.Nonce,
            instanceId = gate.Isolation.InstanceId.ToString("D"), origin = Origin, completed = true, secureContext = true,
            probes = new[] { 1, 2 }.Select(i => new { label = "peer-" + i, nativeConstructor = true, filteredIceServers = true,
                relayOnly = true, iceConnectionState = "failed", connectionState = "new", candidates = 0, restartedIceConnectionState = "failed" }) }));
        Check(RuntimeIsolationPolicy.ValidateNativeResult(gate.Isolation, proof) is not null, "The original full native validator must remain usable with the real entry challenge.");
        Check(!RuntimeIsolationPolicy.ValidateParserGateBinding(gate, proof), "A full ICE result is not a current-document binding result.");
    }
    private static void BindingReplay()
    {
        var first = Challenge(); string raw = Native(Binding(first));
        foreach (var next in new[] { RuntimeIsolationPolicy.CreateParserGateChallenge(first.Isolation.InstanceId, Origin, first.Isolation.DocumentPath),
            RuntimeIsolationPolicy.CreateParserGateChallenge(Guid.NewGuid(), Origin, first.Isolation.DocumentPath),
            RuntimeIsolationPolicy.CreateParserGateChallenge(first.Isolation.InstanceId, "https://other.autumn.invalid/", first.Isolation.DocumentPath),
            RuntimeIsolationPolicy.CreateParserGateChallenge(first.Isolation.InstanceId, Origin, "different.html") })
        {
            Check(!RuntimeIsolationPolicy.ValidateParserGateBinding(next, raw), "Old document data revived another gate.");
            Check(next.BindingResultExpression != first.BindingResultExpression, "The host marker must be unique to each challenge.");
        }
    }
    private static void GateEncoding()
    {
        var gate = Challenge();
        const string html = "<!doctype html><html><head data-test=\"preserved-as-input\"></head><body>派蒙\\\"\r\n\u2028\u2029<script>/* ) ; globalThis.bad=true; // */</script></body></html>";
        string script = gate.CreateGateScript(html);
        const string begin = "document.write(";
        int start = script.IndexOf(begin, StringComparison.Ordinal) + begin.Length;
        string literal = script[start..script.LastIndexOf(");})();", StringComparison.Ordinal)];
        Check(JsonSerializer.Deserialize<string>(literal) == html, "Gate must round-trip the exact complete package HTML as JSON data.");
        Check(!literal.Contains("<script", StringComparison.Ordinal) && !literal.Contains("\u2028", StringComparison.Ordinal) && !literal.Contains("\u2029", StringComparison.Ordinal), "HTML-sensitive tokens and JS line separators must remain escaped inside the JSON string.");
        Check(!gate.BootstrapHtml.Contains("preserved-as-input", StringComparison.Ordinal) && !gate.BootstrapScript.Contains("preserved-as-input", StringComparison.Ordinal), "Untrusted HTML cannot leak into bootstrap resources before native proof.");
        Check(script.Contains("state.sameDocument!==true", StringComparison.Ordinal) && script.Contains("state.sameRoot!==true", StringComparison.Ordinal)
            && script.Contains("state.readyState!=='loading'", StringComparison.Ordinal), "Synchronous gate must reject replaced or completed parser state.");
        Check(RuntimeParserGateChallenge.CompatibilityScope.Contains("head attributes", StringComparison.Ordinal), "The public API must disclose parser compatibility limitations.");
    }
    private static void HtmlLimits()
    {
        var gate = Challenge(); int maximum = RuntimeParserGateChallenge.MaximumParserGateHtmlBytes;
        Throws<ArgumentNullException>(() => gate.CreateGateScript(null!));
        try { gate.CreateGateScript(""); throw new InvalidOperationException("Empty HTML accepted."); }
        catch (RuntimeCapabilityException error) { Check(error.Code == "RUNTIME_ENTRY_HTML_EMPTY", "Empty HTML rejection code."); }
        foreach (string oversized in new[] { new string('a', maximum + 1), new string('\u4e2d', maximum / 3 + 1) })
        {
            try { gate.CreateGateScript(oversized); throw new InvalidOperationException("Oversized HTML accepted."); }
            catch (RuntimeCapabilityException error) { Check(error.Code == "RUNTIME_ENTRY_HTML_TOO_LARGE", "HTML byte budget rejection code."); }
        }
        Check(gate.CreateGateScript(new string('a', maximum)).Contains("document.write(", StringComparison.Ordinal), "Exact package single-file size must remain supported.");
        Throws<EncoderFallbackException>(() => gate.CreateGateScript("\ud800"));
    }
    private static void Ownership()
    {
        Type type = typeof(RuntimeParserGateChallenge);
        Check(type.GetConstructors(BindingFlags.Instance | BindingFlags.Public).Length == 0, "Application callers must not construct a host gate.");
        Check(type.GetProperties(BindingFlags.Public | BindingFlags.Instance).All(property => property.SetMethod is null), "Gate identity/resources must remain immutable.");
    }
    private static void UntrustedBinding()
    {
        var gate = Challenge(); string inner = Binding(gate).ToJsonString();
        foreach (string? raw in new[] { null, "null", inner, "{}", "[]", "\"pending\"", Native(Binding(gate)) + " true",
            JsonSerializer.Serialize(inner.Insert(1, "\"sameDocument\":true,")), JsonSerializer.Serialize(inner.Insert(1, "\"SameDocument\":true,")),
            new string(' ', RuntimeIsolationPolicy.MaximumNativeResultBytes + 1), "\"" + new string('\u4e2d', RuntimeIsolationPolicy.MaximumNativeResultBytes / 2) + "\"" })
            Check(!RuntimeIsolationPolicy.ValidateParserGateBinding(gate, raw), "Malformed, raw-message, duplicate, aliased or excessive native binding was accepted.");
    }
}
