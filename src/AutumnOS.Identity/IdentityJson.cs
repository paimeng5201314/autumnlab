using System.Text;
using System.Text.Json;

namespace AutumnOS.Identity;

internal static class IdentityJson
{
    internal const int ConfigurationLimitBytes = 64 * 1024;
    internal const int MetadataLimitBytes = 128 * 1024;

    internal static JsonDocument Parse(string json, int limit)
    {
        if (Encoding.UTF8.GetByteCount(json) > limit)
            throw new JsonException("Document size limit exceeded.");
        JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
        try
        {
            CheckDuplicates(document.RootElement);
            return document;
        }
        catch
        {
            document.Dispose();
            throw;
        }
    }

    internal static string ReadFile(string path)
    {
        using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > ConfigurationLimitBytes)
            throw new JsonException("Document size limit exceeded.");
        using MemoryStream output = new();
        byte[] buffer = new byte[4096];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            if (output.Length + count > ConfigurationLimitBytes)
                throw new JsonException("Document size limit exceeded.");
            output.Write(buffer, 0, count);
        }
        ReadOnlySpan<byte> bytes = output.GetBuffer().AsSpan(0, checked((int)output.Length));
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }))
            bytes = bytes[3..];
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    internal static bool HasOnly(JsonElement element, params string[] keys) =>
        element.ValueKind == JsonValueKind.Object &&
        element.EnumerateObject().All(property => keys.Contains(property.Name, StringComparer.Ordinal));

    internal static string? GetString(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out JsonElement value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    internal static string[]? GetStrings(JsonElement element, string key)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(key, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 64)
            return null;
        List<string> values = [];
        foreach (JsonElement item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 128 } text)
                return null;
            values.Add(text);
        }
        return values.Distinct(StringComparer.Ordinal).Count() == values.Count ? values.ToArray() : null;
    }

    internal static bool IsHttpsUri(string? text, out Uri? uri)
    {
        uri = null;
        if (text is null || text.Length > 2048 || !Uri.TryCreate(text, UriKind.Absolute, out Uri? candidate) ||
            candidate.Scheme != Uri.UriSchemeHttps || candidate.HostNameType != UriHostNameType.Dns ||
            candidate.IsLoopback || candidate.Port != 443 || candidate.UserInfo.Length != 0 ||
            candidate.Query.Length != 0 || candidate.Fragment.Length != 0 ||
            !string.Equals(text, candidate.AbsoluteUri, StringComparison.Ordinal) ||
            text.Contains('\\') || text.Any(char.IsControl))
            return false;
        uri = candidate;
        return true;
    }

    internal static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme && left.Port == right.Port &&
        string.Equals(left.IdnHost, right.IdnHost, StringComparison.OrdinalIgnoreCase);

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> seen = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name))
                    throw new JsonException("Duplicate property.");
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                CheckDuplicates(item);
        }
    }
}
