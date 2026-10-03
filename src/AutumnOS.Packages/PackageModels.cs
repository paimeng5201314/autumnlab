namespace AutumnOS.Packages;

public sealed record AutumnPackageManifest(int SchemaVersion, string AppId, string Name, string Version,
    string Runtime, string Entry, string[] Permissions,
    PackageDesktopDeclarations? Desktop = null, Dictionary<string, string>? PermissionPurposes = null,
    int SaveFormatVersion = 1, int? MinReadableSaveFormatVersion = null, int? MaxReadableSaveFormatVersion = null);

public sealed record PackageShortcut(string Id, string Title, string Action);
public sealed record PackageWidget(string Id, string Title);
public sealed record PackageDesktopDeclarations(PackageShortcut[] Shortcuts, string[] Links, PackageWidget[] Widgets);
public sealed record PackageInspection(AutumnPackageManifest Manifest, string Sha256, int FileCount, long ExpandedBytes);

public sealed record InstalledPackage(AutumnPackageManifest Manifest, string DirectoryPath, string PackageSha256, string? HostSource = null);

/// <summary>Fixed safe error code and message; never includes archive-controlled text or local paths.</summary>
public sealed class PackageException : Exception
{
    public PackageException(string code) : base(code) => Code = code;
    public string Code { get; }
}
