using System.Text.Json;
using AutumnOS.Identity;

namespace AutumnOS.NativeIdentity.Sample;

internal sealed record ClientConfiguration(LogtoPublicOptions Identity, NativeResourceRequest Resource)
{
    internal const string Redirect = "http://127.0.0.1:17854/callback/";
    internal static string LoadHostClientId(string? configurationPath = null)
    {
        // Build copies the sole public source; no duplicated real app ID or permissive fallback.
        var result = LogtoConfigurationLoader.Load(configurationPath ?? Path.Combine(AppContext.BaseDirectory, "config", "logto.public.json"));
        return result.IsValid && result.Options is { } options ? options.ClientId
            : throw new IdentityFlowException("SAMPLE_HOST_CONFIGURATION_REQUIRED");
    }
    internal static ClientConfiguration Load(string file)
    {
        if (!File.Exists(file)) throw new IdentityFlowException("SAMPLE_CONFIGURATION_REQUIRED");
        using FileStream input = new(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is 0 or > 8192) throw new IdentityFlowException("SAMPLE_CONFIGURATION_REQUIRED");
        byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        if (input.ReadByte() != -1) throw new IdentityFlowException("SAMPLE_CONFIGURATION_REQUIRED");
        using JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        JsonElement root = json.RootElement;
        string[] fields = ["schemaVersion", "Authority", "MetadataAddress", "ClientId", "RedirectUri", "Resource", "RequiredScope"];
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != fields.Length ||
            root.EnumerateObject().Any(p => !fields.Contains(p.Name, StringComparer.Ordinal)) ||
            root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length ||
            root.GetProperty("schemaVersion").GetInt32() != 1) throw new IdentityFlowException("SAMPLE_CONFIGURATION_REQUIRED");
        string Get(string key)
        {
            var value = root.GetProperty(key);
            return value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 2048 } text &&
                !text.Any(char.IsControl) ? text : throw new IdentityFlowException("SAMPLE_CONFIGURATION_REQUIRED");
        }
        return Create(Get("Authority"), Get("MetadataAddress"), Get("ClientId"), Get("RedirectUri"), Get("Resource"), Get("RequiredScope"));
    }
    internal static ClientConfiguration Create(string authorityText, string metadataText, string clientId,
        string redirectText, string resourceText, string scope)
    {
        bool Https(string text, out Uri? uri) => Uri.TryCreate(text, UriKind.Absolute, out uri) && uri.Scheme == "https" &&
            uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
        if (!Https(authorityText, out Uri? authority) || !Https(metadataText, out Uri? metadata) ||
            metadata!.AbsoluteUri != authority!.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration" ||
            !Https(resourceText, out Uri? resource) || redirectText != Redirect ||
            string.IsNullOrWhiteSpace(clientId) || clientId.Length > 128 || clientId.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            clientId == LoadHostClientId()) throw new IdentityFlowException("SAMPLE_CONFIGURATION_REQUIRED");
        LogtoPublicOptions options = new(new Uri(authority.GetLeftPart(UriPartial.Authority) + "/"), authority, metadata, clientId,
            new Uri(Redirect), new Uri("http://127.0.0.1:17854/logout-callback/"), ["openid", "profile"], []);
        NativeResourceRequest request = new(resource!, scope);
        NativeResourceRequest.Validate(request, options);
        return new(options, request);
    }
}
