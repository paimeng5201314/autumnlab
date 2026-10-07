using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AutumnOS.Contracts;

namespace AutumnOS.Runtime;

/// <summary>JSON message transport only. These bytes must never be embedded in HTML or executable script.</summary>
internal static class BridgeJson
{
    // WebAppHost sends this JSON through CoreWebView2.PostWebMessageAsJson, not ExecuteScriptAsync.
    // Keep the budget and the actual reply on the same serializer, including control-character escaping.
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        MaxDepth = 32
    };

    internal static string Success(string requestId, object value) =>
        JsonSerializer.Serialize(new { requestId, ok = true, result = value }, Options);

    internal static void EnsureReadable(JsonElement value)
    {
        // A later reader may use a longer ID than the writer. Reserve the entire supported ID space.
        string response = Success(new string('r', RuntimeSession.MaximumRequestIdLength), new { exists = true, value });
        if (Encoding.UTF8.GetByteCount(response) > RuntimeSession.MaximumMessageBytes)
            throw new RuntimeCapabilityException("RESPONSE_TOO_LARGE");
    }
}
