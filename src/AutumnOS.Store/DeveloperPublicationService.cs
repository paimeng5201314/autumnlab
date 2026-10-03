using System.Security.Cryptography;
using System.Text.Json;
using AutumnOS.Packages;

namespace AutumnOS.Store;

public sealed record DeveloperPublicationOptions(string Developer, string Description, string Category = "sq",
    bool Offline = false, string MinimumHost = "0.3.0", string MinimumSdk = "0.3.0");
public sealed record DeveloperPublication(string OutputDirectory, string PackagePath, string StorePath, string ReleasePath,
    string AppId, string Version, string Channel, long Bytes, string Sha256, string ReleaseMetadataSha256, string StoreContentSha256);
public sealed record DeveloperPublicationCheck(string AppId, string Version, string Channel, string Sha256, long Bytes);

/// <summary>The same local three-layer publication workflow for the native developer application and CLI. Never uploads.</summary>
public sealed class DeveloperPublicationService(DeveloperToolsService tools)
{
    public DeveloperPublication Generate(string packagePath, string outputDirectory, DeveloperPublicationOptions options,
        CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource work = Begin(cancellationToken);
        string package = Absolute(packagePath), output = Absolute(outputDirectory);
        SafePath(package); SafePath(output);
        if (Directory.Exists(output) || File.Exists(output)) throw new CatalogException("CLI_OUTPUT_ALREADY_EXISTS");
        using FileStream inputLease = new(package, FileMode.Open, FileAccess.Read, FileShare.Read);
        PackageInspection inspection = tools.InspectPackage(package, work.Token);
        ReleaseManifest release = ReleaseMetadata.GenerateRelease(package, options.MinimumHost, options.MinimumSdk, work.Token);
        StoreManifest store = ReleaseMetadata.GenerateStore(inspection.Manifest, options.Developer, options.Description, options.Category, options.Offline);
        byte[] releaseBytes = JsonSerializer.SerializeToUtf8Bytes(release, ReleaseMetadata.JsonOptions);
        byte[] storeBytes = JsonSerializer.SerializeToUtf8Bytes(store, ReleaseMetadata.JsonOptions);
        work.Token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(output); SafePath(output);
        string copiedPackage = Path.Combine(output, release.Asset);
        // No pre-existing output is overwritten; partial output stays visible for diagnosis.
        using (FileStream destination = new(copiedPackage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { inputLease.Position = 0; inputLease.CopyTo(destination); destination.Flush(true); }
        work.Token.ThrowIfCancellationRequested();
        string storePath = Path.Combine(output, "autumn.store.json"), releasePath = Path.Combine(output, "autumn.release.json");
        WriteNew(storePath, storeBytes); work.Token.ThrowIfCancellationRequested(); WriteNew(releasePath, releaseBytes);
        DeveloperPublicationCheck checkedResult = Validate(storePath, releasePath, copiedPackage, work.Token);
        return new(output, copiedPackage, storePath, releasePath, checkedResult.AppId, checkedResult.Version, checkedResult.Channel,
            checkedResult.Bytes, checkedResult.Sha256, Convert.ToHexStringLower(SHA256.HashData(releaseBytes)), Convert.ToHexStringLower(SHA256.HashData(storeBytes)));
    }
    public DeveloperPublicationCheck Validate(string storePath, string releasePath, string packagePath, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource work = Begin(cancellationToken);
        string package = Absolute(packagePath); SafePath(package);
        using FileStream inputLease = new(package, FileMode.Open, FileAccess.Read, FileShare.Read);
        StoreManifest store = ReleaseMetadata.ParseStore(ReadMetadata(Absolute(storePath)));
        ReleaseManifest release = ReleaseMetadata.ParseRelease(ReadMetadata(Absolute(releasePath)));
        PackageInspection inspection = tools.InspectPackage(package, work.Token);
        if (store.AppId != release.AppId || store.AppId != inspection.Manifest.AppId) throw new CatalogException("CLI_PUBLICATION_APP_ID_MISMATCH");
        ReleaseMetadata.MatchPackage(release, inspection, package);
        _ = tools.ValidateRelease(Absolute(releasePath), package, work.Token);
        work.Token.ThrowIfCancellationRequested();
        return new(release.AppId, release.Version, release.Channel, inspection.Sha256, inputLease.Length);
    }
    private CancellationTokenSource Begin(CancellationToken cancellationToken)
    {
        if (!tools.Enabled) throw new PackageException("DEVELOPER_MODE_DISABLED");
        CancellationTokenSource work = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tools.CapabilityToken);
        try { work.Token.ThrowIfCancellationRequested(); return work; } catch { work.Dispose(); throw; }
    }
    private static byte[] ReadMetadata(string path)
    {
        SafePath(path);
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > ReleaseMetadata.MaximumMetadataBytes) throw new CatalogException("CLI_METADATA_SIZE_INVALID");
        byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }
    private static void WriteNew(string path, byte[] bytes)
    { SafePath(path); using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); output.Write(bytes); output.Flush(true); }
    private static string Absolute(string path) => Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : throw new CatalogException("CLI_PATH_INVALID");
    private static void SafePath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new CatalogException("CLI_LINK_FORBIDDEN"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
}
