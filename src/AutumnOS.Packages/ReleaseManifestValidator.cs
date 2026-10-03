using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AutumnOS.Packages;

public sealed record PackageReleaseManifest(int SchemaVersion, string AppId, string Version, string Channel,
    string Runtime, string MinHostVersion, string MinSdkVersion, string Entry, string Asset, long Bytes,
    string Sha256, string[] Permissions, int SaveFormatVersion);

/// <summary>Shared by the developer tools, catalog and installation preparation. Version 1 retains the T03 release fields.</summary>
public static class ReleaseManifestValidator
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true, MaxDepth = 12
    };
    public const string VersionPattern = @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z-][0-9A-Za-z-]*))*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z";
    private static readonly HashSet<string> Permissions = new(StringComparer.Ordinal)
        { "saves", "identity.profile", "storage", "files.open", "files.save", "notifications", "shortcuts", "widgets", "links" };

    public static PackageReleaseManifest Parse(byte[] bytes)
    {
        if (bytes.Length is 0 or > 65536) throw new PackageException("RELEASE_METADATA_INVALID");
        try
        {
            using JsonDocument doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            string[] fields = ["schemaVersion", "appId", "version", "channel", "runtime", "minHostVersion", "minSdkVersion", "entry", "asset", "bytes", "sha256", "permissions", "saveFormatVersion"];
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != fields.Length || fields.Any(f => !root.TryGetProperty(f, out _)))
                throw new PackageException("RELEASE_METADATA_INVALID");
            PackageReleaseManifest value = JsonSerializer.Deserialize<PackageReleaseManifest>(bytes, JsonOptions) ?? throw new PackageException("RELEASE_METADATA_INVALID");
            if (value.SchemaVersion != 1) throw new PackageException("RELEASE_SCHEMA_UNSUPPORTED");
            bool Match(string? text, string pattern, int min, int max) => text is not null && text.Length >= min && text.Length <= max && Regex.IsMatch(text, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
            bool Version(string? text) => Match(text, VersionPattern, 5, 128);
            if (!Match(value.AppId, @"^(?!(?:con|prn|aux|nul|com[1-9]|lpt[1-9])\.)(?=[^.]{1,63}(?:\.[^.]{1,63})+$)[a-z][a-z0-9]*(?:-[a-z0-9]+)*(?:\.[a-z][a-z0-9]*(?:-[a-z0-9]+)*)+\z", 3, 80) ||
                !Version(value.Version) || !Version(value.MinHostVersion) || !Version(value.MinSdkVersion) || value.Channel is not ("stable" or "preview") ||
                value.Version.Split('+')[0].Contains('-') != (value.Channel == "preview") ||
                !Match(value.Runtime, @"^[a-z][a-z0-9-]*\z", 1, 32) ||
                !Match(value.Entry, @"^[A-Za-z0-9_-][A-Za-z0-9_./-]*\.[hH][tT][mM][lL]\z", 1, 128) || value.Entry.Contains("..", StringComparison.Ordinal) ||
                !Match(value.Asset, @"^[A-Za-z0-9][A-Za-z0-9._+-]*\.autumn\z", 8, 160) || value.Bytes is <= 0 or > 20L * 1024 * 1024 ||
                !Match(value.Sha256, @"^[0-9a-f]{64}\z", 64, 64) || value.SaveFormatVersion is < 1 or > PackageInstaller.MaximumSaveFormatVersion || value.Permissions is null ||
                value.Permissions.Length > Permissions.Count || value.Permissions.Any(p => !Permissions.Contains(p)) || value.Permissions.Distinct(StringComparer.Ordinal).Count() != value.Permissions.Length)
                throw new PackageException("RELEASE_METADATA_INVALID");
            return value;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException or RegexMatchTimeoutException)
        { throw new PackageException("RELEASE_METADATA_INVALID"); }
    }
    public static void ValidatePackage(PackageReleaseManifest release, PackageInspection inspection, string packagePath)
    {
        AutumnPackageManifest manifest = inspection.Manifest;
        if (release.AppId != manifest.AppId || release.Version != manifest.Version || release.Runtime != manifest.Runtime || release.Entry != manifest.Entry ||
            release.Sha256 != inspection.Sha256 || release.Asset != Path.GetFileName(packagePath) || release.Bytes != new FileInfo(packagePath).Length ||
            !release.Permissions.Order(StringComparer.Ordinal).SequenceEqual(manifest.Permissions.Order(StringComparer.Ordinal)) ||
            release.SaveFormatVersion != manifest.SaveFormatVersion) throw new PackageException("RELEASE_METADATA_MISMATCH");
    }
}
