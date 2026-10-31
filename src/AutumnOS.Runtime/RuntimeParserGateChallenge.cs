using System.Net;
using System.Text;
using System.Text.Json;
using AutumnOS.Contracts;

namespace AutumnOS.Runtime;

public static partial class RuntimeIsolationPolicy
{
    /// <summary>
    /// Creates a trusted bootstrap at the real package entry. The original full ICE challenge
    /// and ValidateNativeResult remain required. Before gate completion the native host must deny
    /// all package resources/SDK calls and bind Core.Source, navigation ID and lifecycle itself.
    /// </summary>
    public static RuntimeParserGateChallenge CreateParserGateChallenge(Guid instanceId, string origin, string entryPath)
    {
        if (!IsParserGateEntry(entryPath)) throw new ArgumentException("A canonical relative HTML package entry is required.", nameof(entryPath));
        return new(CreateChallenge(instanceId, origin), entryPath);
    }

    /// <summary>
    /// Validates only an ExecuteScriptAsync envelope from the still-loading trusted bootstrap.
    /// This is an auxiliary binding check, not a native-document attestation or an isolation proof.
    /// document.open can retain a Document object: the host must invalidate on native document/
    /// navigation/lifecycle changes even when Source or this JavaScript marker appears unchanged.
    /// Never call this with an application WebMessage or use it in place of ValidateNativeResult.
    /// </summary>
    public static bool ValidateParserGateBinding(RuntimeParserGateChallenge challenge, string? executeScriptAsyncJson)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        if (executeScriptAsyncJson is null || Encoding.UTF8.GetByteCount(executeScriptAsyncJson) > MaximumNativeResultBytes) return false;
        try
        {
            using JsonDocument outer = JsonDocument.Parse(executeScriptAsyncJson, new JsonDocumentOptions { MaxDepth = 4 });
            if (outer.RootElement.ValueKind != JsonValueKind.String || outer.RootElement.GetString() is not { } text
                || Encoding.UTF8.GetByteCount(text) > MaximumNativeResultBytes) return false;
            using JsonDocument inner = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 4 });
            JsonElement value = inner.RootElement;
            return ExactProperties(value, "protocolVersion", "bindingKind", "nonce", "instanceId", "origin", "entry", "sameDocument", "sameRoot", "readyState")
                && ExactInteger(value, "protocolVersion", 1) && ExactString(value, "bindingKind", "parser-gate-document-v1")
                && ExactString(value, "nonce", challenge.Isolation.Nonce) && ExactString(value, "instanceId", challenge.Isolation.InstanceId.ToString("D"))
                && ExactString(value, "origin", challenge.Isolation.Origin) && ExactString(value, "entry", challenge.Isolation.PageUri)
                && ExactTrue(value, "sameDocument") && ExactTrue(value, "sameRoot") && ExactString(value, "readyState", "loading");
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { return false; }
    }

    // Match RuntimeSession's entry domain: absolute URLs, percent escapes, traversal, alternate
    // separators, empty/dot segments and Windows device names never become bootstrap locations.
    private static bool IsParserGateEntry(string? value)
    {
        if (value is null || value.Length is < 1 or > 200 || !value.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
            || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '/' and not '.' and not '_' and not '-')) return false;
        foreach (string segment in value.Split('/'))
        {
            if (segment.Length is < 1 or > 80 || segment.StartsWith('.') || segment.EndsWith('.')) return false;
            string basename = segment.Split('.')[0].ToLowerInvariant();
            if (basename is "con" or "prn" or "aux" or "nul"
                || (basename.Length == 4 && (basename.StartsWith("com", StringComparison.Ordinal) || basename.StartsWith("lpt", StringComparison.Ordinal))
                    && basename[3] is >= '1' and <= '9')) return false;
        }
        return true;
    }
}

/// <summary>
/// Host-created same-document startup resources. This object prepares scripts; it never grants
/// execution. G1 verified seven owned cases on one fixed engine; it did not certify arbitrary HTML.
/// Existing html/head/doctype tokens follow HTML parser merge/ignore rules. Head attributes and
/// non-HTML5 document modes are not transparently preserved and need separate compatibility review.
/// </summary>
public sealed class RuntimeParserGateChallenge
{
    public const string LoadingStrategyId = "autumn-parser-gate-v1";
    public const int MaximumParserGateHtmlBytes = 8 * 1024 * 1024;
    public const string CompatibilityScope = "HTML5 parser insertion into the trusted bootstrap document; repeated html/head/doctype tokens follow merge/ignore rules; head attributes and non-HTML5 modes are not transparently preserved. Same Document does not certify arbitrary package HTML.";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly string marker;

    internal RuntimeParserGateChallenge(RuntimeIsolationChallenge original, string entryPath)
    {
        marker = "__autumnParserGate_" + original.Nonce;
        GatePath = "__autumn_gate_" + original.Nonce + ".js";
        GateUri = original.Origin + GatePath;
        BootstrapScriptUri = original.Origin + original.ScriptPath;
        BootstrapHtml = "<!doctype html><html><head><meta charset=\"utf-8\">"
            + "<script src=\"" + WebUtility.HtmlEncode(BootstrapScriptUri) + "\"></script>"
            + "<script src=\"" + WebUtility.HtmlEncode(GateUri) + "\"></script>";
        // Only the page location/HTML are adapted. The full original ICE script and strict native
        // validator remain exactly the same; no constructor-only substitute or simulated proof.
        Isolation = new(original.InstanceId, original.Origin, original.Nonce, entryPath, original.ScriptPath, BootstrapHtml, original.Script);
        string binding = JsonSerializer.Serialize(new { protocolVersion = 1, bindingKind = "parser-gate-document-v1", nonce = original.Nonce,
            instanceId = original.InstanceId.ToString("D") });
        BootstrapScript = "(() => {'use strict'; const binding=Object.freeze(" + binding + ");"
            + "const initialDocument=document, initialRoot=document.documentElement;"
            + "Object.defineProperty(globalThis," + JsonSerializer.Serialize(marker) + ",{configurable:false,writable:false,value:Object.freeze({"
            + "capture:()=>({...binding,origin:location.origin+'/',entry:location.href,sameDocument:document===initialDocument,"
            + "sameRoot:initialRoot!==null&&document.documentElement===initialRoot,readyState:document.readyState})})});})();\n"
            + original.Script;
        BindingResultExpression = "JSON.stringify(globalThis[" + JsonSerializer.Serialize(marker) + "]?.capture() ?? null)";
    }

    public RuntimeIsolationChallenge Isolation { get; }
    public string BootstrapHtml { get; }
    public string BootstrapScript { get; }
    public string BootstrapScriptUri { get; }
    public string GatePath { get; }
    public string GateUri { get; }
    public string BindingResultExpression { get; }

    /// <summary>
    /// Prepares a synchronous parser-inserted gate script. The native host must not serve it until
    /// full ICE proof AND the current native navigation/document/session binding have passed.
    /// The supplied HTML is JSON-encoded as data, with no eval, document.open, DOMParser or API patch.
    /// This does not normalize or certify HTML compatibility; see CompatibilityScope.
    /// </summary>
    public string CreateGateScript(string applicationHtml)
    {
        ArgumentNullException.ThrowIfNull(applicationHtml);
        if (applicationHtml.Length == 0) throw new RuntimeCapabilityException("RUNTIME_ENTRY_HTML_EMPTY");
        if (applicationHtml.Length > MaximumParserGateHtmlBytes || StrictUtf8.GetByteCount(applicationHtml) > MaximumParserGateHtmlBytes)
            throw new RuntimeCapabilityException("RUNTIME_ENTRY_HTML_TOO_LARGE");
        return "(() => {'use strict'; const state=globalThis[" + JsonSerializer.Serialize(marker) + "]?.capture();"
            + "if(!state||state.sameDocument!==true||state.sameRoot!==true||state.readyState!=='loading'"
            + "||state.nonce!==" + JsonSerializer.Serialize(Isolation.Nonce)
            + "||state.instanceId!==" + JsonSerializer.Serialize(Isolation.InstanceId.ToString("D"))
            + "||state.origin!==" + JsonSerializer.Serialize(Isolation.Origin)
            + "||state.entry!==" + JsonSerializer.Serialize(Isolation.PageUri)
            + ") throw new Error('PARSER_DOCUMENT_BINDING_LOST');"
            + "document.write(" + JsonSerializer.Serialize(applicationHtml) + ");})();";
    }
}
