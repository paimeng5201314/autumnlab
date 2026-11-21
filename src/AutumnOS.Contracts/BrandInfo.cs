using System.Reflection;
using System.Globalization;

namespace AutumnOS.Contracts;

/// <summary>Build-generated branding. Values come exclusively from build/Brand.props.</summary>
public static class BrandInfo
{
    public const string UpdateRepository = "paimeng5201314/autumnOS";
    private static readonly Assembly Assembly = typeof(BrandInfo).Assembly;
    public static string ProductName => Assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product;
    public static string DisplayName => Metadata("DisplayName");
    public static string ProducerCredit => Metadata("ProducerCredit");
    // Compatibility and update ordering use canonical SemVer; the release label may include a channel prefix.
    public static string Version => Metadata("SemanticVersion");
    public static string DisplayVersion => Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    public static string BuildId => Metadata("BuildId");
    public static string ReleaseChannel => Metadata("ReleaseChannel");
    public static long UpdateSequence => Sequence("UpdateSequence") ?? 0;
    public static long SecuritySequence => Sequence("SecuritySequence") ?? UpdateSequence;
    private static long? Sequence(string key)
    {
        string? value = Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().SingleOrDefault(x => x.Key == key)?.Value;
        if (value is null) return null;
        return long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var sequence)
            ? sequence : throw new InvalidDataException("UPDATE_BUILD_SEQUENCE_INVALID");
    }
    public static string SourceSnapshotId => Metadata("SourceSnapshotId");
    private static string Metadata(string key) => Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x => x.Key == key).Value!;
}
