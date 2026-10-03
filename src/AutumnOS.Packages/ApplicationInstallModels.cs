namespace AutumnOS.Packages;

/// <summary>Host supplied source, never an app SDK claim. Repository names are display metadata, IDs bind continuity.</summary>
public sealed record PackageSource(string Kind, long? RepositoryId = null, string? RepositoryName = null,
    string? SigningKeyFingerprint = null)
{
    public string Binding(string appId) => Kind == "github" ? $"github:{RepositoryId}" :
        Kind == "test" ? $"test:{RepositoryId}" : $"{Kind}:{appId}";
}

public sealed record PackageProvenance(long? ReleaseId = null, long? AssetId = null, string? ReleaseMetadataSha256 = null,
    string? StoreContentSha = null, string? ReleaseCanonicalSha256 = null);
public sealed record ExpectedPackageIdentity(PackageSource Source, string AppId, string Version, string Runtime,
    string Entry, string[] Permissions, string Sha256, long Bytes, int SaveFormatVersion = 1, PackageProvenance? Provenance = null);
public enum InstallIntent { InstallOrUpdate, Repair, Downgrade }
public sealed record InstalledApplication(string AppId, InstalledPackage Package, PackageSource Source,
    DateTimeOffset InstalledAt, string? PinnedVersion = null, bool AllowPreview = false, bool IsInstalled = true, PackageProvenance? Provenance = null);
public sealed record ApplicationRegistryChange(string Kind, string AppId, InstalledApplication? Application);
public enum InstallCommitPoint { BeforeContentInstall, AfterContentInstall, BeforeRegistryCommit, AfterRegistryCommit }
/// <summary>Explicit isolated-test injection; never loaded from normal configuration or SDK messages.</summary>
public sealed record InstallFaultHooks(Action<InstallCommitPoint> Invoke);
