using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AutumnOS.Packages;

namespace AutumnOS.Store;

/// <summary>One versioned parser used by catalog, local publication tools and pre-install comparison.</summary>
public static class ReleaseMetadata
{
    public const int MaximumMetadataBytes = 65536;
    public const long MaximumPackageBytes = 20L * 1024 * 1024;
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true, MaxDepth = 12
    };
    private static readonly HashSet<string> Permissions = new(StringComparer.Ordinal)
        { "saves", "identity.profile", "storage", "files.open", "files.save", "notifications", "shortcuts", "widgets", "links" };

    public static StoreManifest ParseStore(byte[] bytes)
    {
        try
        {
            using JsonDocument document = Document(bytes);
            Fields(document.RootElement, ["schemaVersion", "appId", "name", "description", "category", "developer", "screenshots", "offlineCapable"]);
            StoreManifest value = JsonSerializer.Deserialize<StoreManifest>(bytes, JsonOptions) ?? throw new CatalogException("STORE_METADATA_INVALID");
            if (value.SchemaVersion != 1) throw new CatalogException("STORE_SCHEMA_UNSUPPORTED");
            if (!AppId(value.AppId) || !Text(value.Name, 128) || !Text(value.Description, 4000, multiline: true) ||
                value.Category is not ("pm" or "sq" or "unclassified") || value.Developer is null || !Text(value.Developer.Name, 128) ||
                value.Screenshots is null || value.Screenshots.Length > 6 || value.Screenshots.Any(s => !PublicHttps(s)) ||
                value.Developer.Url is not null && !PublicHttps(value.Developer.Url)) throw new CatalogException("STORE_METADATA_INVALID");
            JsonElement developer = document.RootElement.GetProperty("developer");
            Fields(developer, ["name"], ["url"]);
            return value;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException) { throw new CatalogException("STORE_METADATA_INVALID"); }
    }

    public static ReleaseManifest ParseRelease(byte[] bytes)
    {
        try
        {
            PackageReleaseManifest value = ReleaseManifestValidator.Parse(bytes);
            return new(value.SchemaVersion, value.AppId, value.Version, value.Channel, value.Runtime, value.MinHostVersion,
                value.MinSdkVersion, value.Entry, value.Asset, value.Bytes, value.Sha256, value.Permissions, value.SaveFormatVersion);
        }
        catch (PackageException ex) { throw new CatalogException(ex.Code == "RELEASE_SCHEMA_UNSUPPORTED" ? "STORE_SCHEMA_UNSUPPORTED" : "STORE_RELEASE_INVALID"); }
    }

    public static void MatchPackage(ReleaseManifest release, PackageInspection inspection, string packagePath)
    {
        try { ReleaseManifestValidator.ValidatePackage(ReleaseManifestValidator.Parse(JsonSerializer.SerializeToUtf8Bytes(release, JsonOptions)), inspection, packagePath); }
        catch (PackageException) { throw new CatalogException("STORE_PACKAGE_METADATA_MISMATCH"); }
    }
    public static ReleaseManifest GenerateRelease(string packagePath, string minimumHostVersion = "0.3.0", string minimumSdkVersion = "0.3.0", CancellationToken cancellationToken = default)
    {
        PackageInspection inspection = PackageInstaller.Inspect(packagePath, cancellationToken);
        AutumnPackageManifest m = inspection.Manifest;
        ReleaseManifest release = new(1, m.AppId, m.Version, SemanticVersion.Parse(m.Version).IsPrerelease ? "preview" : "stable", m.Runtime,
            minimumHostVersion, minimumSdkVersion, m.Entry, Path.GetFileName(packagePath), new FileInfo(packagePath).Length, inspection.Sha256, m.Permissions, m.SaveFormatVersion);
        return ParseRelease(JsonSerializer.SerializeToUtf8Bytes(release, JsonOptions));
    }

    public static StoreManifest GenerateStore(AutumnPackageManifest manifest, string developerName, string description, string category = "sq", bool offlineCapable = false)
    {
        StoreManifest store = new(1, manifest.AppId, manifest.Name, description, category, new(developerName), [], offlineCapable);
        return ParseStore(JsonSerializer.SerializeToUtf8Bytes(store, JsonOptions));
    }

    public static string? Compatibility(ReleaseManifest value, string hostVersion, string sdkVersion)
    {
        if (value.Runtime != "web") return "STORE_RUNTIME_UNSUPPORTED";
        if (SemanticVersion.Parse(hostVersion).CompareTo(SemanticVersion.Parse(value.MinHostVersion)) < 0) return "STORE_HOST_TOO_OLD";
        if (SemanticVersion.Parse(sdkVersion).CompareTo(SemanticVersion.Parse(value.MinSdkVersion)) < 0) return "STORE_SDK_TOO_OLD";
        return null;
    }

    internal static JsonDocument Document(byte[] bytes, int maximum = MaximumMetadataBytes)
    {
        if (bytes.Length is 0 || bytes.Length > maximum) throw new CatalogException("STORE_RESPONSE_TOO_LARGE");
        JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        try { Unique(document.RootElement); return document; } catch { document.Dispose(); throw; }
    }
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (JsonProperty p in element.EnumerateObject()) { if (!keys.Add(p.Name)) throw new CatalogException("STORE_METADATA_DUPLICATE_FIELD"); Unique(p.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (JsonElement item in element.EnumerateArray()) Unique(item);
    }
    private static void Fields(JsonElement value, string[] required, string[]? optional = null)
    {
        if (value.ValueKind != JsonValueKind.Object || required.Any(key => !value.TryGetProperty(key, out _)) ||
            value.EnumerateObject().Any(p => !required.Contains(p.Name, StringComparer.Ordinal) && !(optional?.Contains(p.Name, StringComparer.Ordinal) ?? false)))
            throw new CatalogException("STORE_METADATA_INVALID");
    }
    internal static bool Text(string? value, int limit, bool multiline = false) => !string.IsNullOrWhiteSpace(value) && value.Length <= limit &&
        !value.Any(c => char.IsControl(c) && !(multiline && c is '\n' or '\r' or '\t'));
    internal static bool AppId(string? value) => value is { Length: >= 3 and <= 80 } && Regex.IsMatch(value,
        @"^(?!(?:con|prn|aux|nul|com[1-9]|lpt[1-9])\.)(?=[^.]{1,63}(?:\.[^.]{1,63})+$)[a-z][a-z0-9]*(?:-[a-z0-9]+)*(?:\.[a-z][a-z0-9]*(?:-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant);
    internal static bool AssetName(string? value) => value is { Length: > 7 and <= 160 } && Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9._+-]*\.autumn$", RegexOptions.CultureInvariant);
    private static bool Resource(string? value) => value is { Length: > 0 and <= 128 } && !value.Contains("..", StringComparison.Ordinal) &&
        Regex.IsMatch(value, @"^[A-Za-z0-9_-][A-Za-z0-9_./-]*\.[hH][tT][mM][lL]$", RegexOptions.CultureInvariant);
    internal static bool PublicHttps(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.IsDefaultPort &&
        uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 && uri.Query.Length == 0 && uri.HostNameType == UriHostNameType.Dns &&
        uri.Host.Contains('.') && !uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) && !uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}
