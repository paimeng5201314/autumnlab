using System.Reflection;

namespace AutumnOS.Contracts;

/// <summary>Build-generated branding. Values come exclusively from build/Brand.props.</summary>
public static class BrandInfo
{
    private static readonly Assembly Assembly = typeof(BrandInfo).Assembly;
    public static string ProductName => Assembly.GetCustomAttribute<AssemblyProductAttribute>()!.Product;
    public static string DisplayName => Metadata("DisplayName");
    public static string ProducerCredit => Metadata("ProducerCredit");
    // Compatibility and update ordering use canonical SemVer; the release label may include a channel prefix.
    public static string Version => Metadata("SemanticVersion");
    public static string DisplayVersion => Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
    public static string BuildId => Metadata("BuildId");
    public static string SourceSnapshotId => Metadata("SourceSnapshotId");
    private static string Metadata(string key) => Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x => x.Key == key).Value!;
}
