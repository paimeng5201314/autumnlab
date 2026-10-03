using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AutumnOS.Store;

namespace AutumnOS.Update;

public static class UpdateManifestCodec
{
    public const int MaximumManifestBytes = 4 * 1024 * 1024;
    public const int MaximumFiles = 20000;
    public const long MaximumPayloadBytes = 4L * 1024 * 1024 * 1024;
    public const long MaximumExpandedBytes = 12L * 1024 * 1024 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 24, WriteIndented = true,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true
    };
    public static byte[] Serialize(UpdateManifest manifest)
    { Validate(manifest); return JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions); }
    public static UpdateManifest Parse(byte[] bytes)
    { var manifest = ParseStrict<UpdateManifest>(bytes, MaximumManifestBytes); Validate(manifest); return manifest; }
    public static T ParseStrict<T>(byte[] bytes, int maximum)
    {
        if (bytes.Length == 0 || bytes.Length > maximum || bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            throw new UpdateException("UPDATE_METADATA_ENCODING_OR_SIZE");
        try
        {
            _ = new UTF8Encoding(false, true).GetString(bytes);
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
            RejectDuplicates(document.RootElement);
            return JsonSerializer.Deserialize<T>(bytes, JsonOptions) ?? throw new UpdateException("UPDATE_METADATA_INVALID");
        }
        catch (Exception ex) when (ex is JsonException or DecoderFallbackException)
        { throw new UpdateException("UPDATE_METADATA_INVALID", ex); }
    }
    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new UpdateException("UPDATE_DUPLICATE_FIELD"); RejectDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) RejectDuplicates(value);
    }
    public static void Validate(UpdateManifest m)
    {
        if (m.SchemaVersion != 1) throw new UpdateException("UPDATE_SCHEMA_UNSUPPORTED");
        if (m.ProductId != "cn.labchronicles.autumnos" || m.Repository != "paimeng5201314/autumnlab") throw new UpdateException("UPDATE_PRODUCT_SOURCE_MISMATCH");
        if (!SemanticVersion.TryParse(m.Version, out var version) || m.Channel is not ("plus" or "meta") ||
            version!.IsPrerelease != (m.Channel == "meta")) throw new UpdateException("UPDATE_CHANNEL_VERSION_MISMATCH");
        if (!SemanticVersion.TryParse(m.MinimumUpdaterVersion, out _) || m.TargetRid != "win-x64") throw new UpdateException("UPDATE_COMPATIBILITY_INVALID");
        if (!Identifier(m.BuildId) || !Identifier(m.KeyId) || m.TrustRootVersion < 1 || m.Sequence < 1 ||
            m.IssuedAtUtc.Offset != TimeSpan.Zero || m.ExpiresAtUtc.Offset != TimeSpan.Zero ||
            m.ExpiresAtUtc <= m.IssuedAtUtc || m.ExpiresAtUtc - m.IssuedAtUtc > TimeSpan.FromDays(90)) throw new UpdateException("UPDATE_METADATA_INVALID");
        if (m.Payload is null || !Regex.IsMatch(m.Payload.Asset ?? "", @"^[A-Za-z0-9][A-Za-z0-9._-]{0,150}\.zip$", RegexOptions.CultureInvariant) ||
            m.Payload.Bytes is <= 0 or > MaximumPayloadBytes || m.Payload.ReleaseId <= 0 || m.Payload.AssetId <= 0 || !Hash(m.Payload.Sha256)) throw new UpdateException("UPDATE_PAYLOAD_INVALID");
        if (m.Data is null || m.Data.SchemaVersion != 1 || m.Data.MinimumReadableVersion != 1 || !m.Data.RollbackCompatible)
            throw new UpdateException("UPDATE_MIGRATION_UNSUPPORTED");
        if (m.Files is null || m.Files.Count is < 1 or > MaximumFiles || m.RemoveFiles is null || m.RemoveFiles.Count > MaximumFiles)
            throw new UpdateException("UPDATE_FILE_TABLE_INVALID");
        HashSet<string> paths = new(StringComparer.OrdinalIgnoreCase); long expanded = 0;
        foreach (var file in m.Files)
        {
            if (file is null) throw new UpdateException("UPDATE_FILE_TABLE_INVALID");
            UpdatePaths.ValidateManagedRelativePath(file.Path);
            if (!paths.Add(file.Path) || file.Bytes < 0 || file.Bytes > MaximumPayloadBytes || !Hash(file.Sha256)) throw new UpdateException("UPDATE_FILE_TABLE_INVALID");
            expanded = checked(expanded + file.Bytes);
            if (expanded > MaximumExpandedBytes) throw new UpdateException("UPDATE_EXPANDED_LIMIT");
        }
        if (!paths.Contains("AutumnOS.Client.exe")) throw new UpdateException("UPDATE_CLIENT_ENTRY_MISSING");
        foreach (string path in m.RemoveFiles)
        { UpdatePaths.ValidateManagedRelativePath(path); if (!paths.Add(path)) throw new UpdateException("UPDATE_FILE_TABLE_INVALID"); }
        // A file cannot also be an ancestor directory (case insensitive Windows semantics).
        foreach (string path in paths)
        { for (int slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1)) if (paths.Contains(path[..slash])) throw new UpdateException("UPDATE_FILE_TABLE_INVALID"); }
    }
    internal static bool Identifier(string? value) => value is { Length: >= 1 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
