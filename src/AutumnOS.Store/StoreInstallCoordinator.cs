using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AutumnOS.Packages;

namespace AutumnOS.Store;

/// <summary>Host-only bridge. Downloaded bytes are never considered installed before registry commit.</summary>
public sealed class StoreInstallCoordinator
{
    private readonly IDownloadService downloads;
    private readonly ApplicationInstallService installer;
    private readonly string contextsPath;
    private readonly object gate = new();
    private readonly SemaphoreSlim installGate = new(1, 1);
    private readonly string sourceKind;
    private readonly List<string> recoveryWarnings = [];
    public string[] RecoveryWarnings { get { lock (gate) return recoveryWarnings.ToArray(); } }
    private static readonly JsonSerializerOptions IntentJson = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 20 };
    private sealed record PendingInstall(string TaskId, CatalogRepository Repository, ReleaseManifest Release, long ReleaseId, long AssetId, string? StoreContentSha);

    public StoreInstallCoordinator(string dataRoot, IDownloadService downloads, ApplicationInstallService installer, bool isolatedTestSource = false)
    {
        this.downloads = downloads; this.installer = installer; sourceKind = isolatedTestSource ? "test" : "github";
        contextsPath = Path.Combine(Path.GetFullPath(dataRoot), "Downloads", "store-install-intents.json");
        DownloadFiles.EnsureDirectory(Path.GetDirectoryName(contextsPath)!);
        ReconcileCommittedReceipts();
    }
    public DownloadSnapshot Queue(CatalogDetails details, CatalogVersion version)
    {
        if (details.Store is null || !version.CanInstall || version.Manifest is not { } release || version.Asset is not { } asset ||
            details.Store.AppId != release.AppId || !details.Versions.Contains(version)) throw new CatalogException("STORE_VERSION_NOT_INSTALLABLE");
        // Preserve the exact parsed schema, never infer package identity from release text or filename.
        _ = ReleaseMetadata.ParseRelease(JsonSerializer.SerializeToUtf8Bytes(release, ReleaseMetadata.JsonOptions));
        var url = asset.DownloadUri;
        if (details.Repository.RepositoryId <= 0 || version.ReleaseId <= 0 || asset.AssetId <= 0 || asset.Name != release.Asset || asset.Size != release.Bytes ||
            !url.IsAbsoluteUri || url.Scheme != "https" || url.Host != "github.com" || !url.IsDefaultPort || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0 ||
            Uri.UnescapeDataString(url.AbsolutePath) != "/" + details.Repository.FullName + "/releases/download/" + version.Tag + "/" + asset.Name ||
            asset.Digest is not null && asset.Digest != "sha256:" + release.Sha256)
            throw new CatalogException("STORE_ASSET_SOURCE_INVALID");
        var task = downloads.Enqueue(new(release.AppId, release.Version, asset.DownloadUri, release.Bytes, release.Sha256,
            details.Repository.RepositoryId, version.ReleaseId, asset.AssetId));
        lock (gate)
        {
            var existingIds = downloads.Snapshot().Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            var pending = Read().Where(item => item.TaskId != task.Id && existingIds.Contains(item.TaskId)).ToList();
            pending.Add(new(task.Id, details.Repository, release, version.ReleaseId, asset.AssetId, details.StoreContentSha));
            if (pending.Count > 200) throw new CatalogException("STORE_INSTALL_HISTORY_FULL");
            try { DownloadFiles.WriteJson(contextsPath, pending); }
            catch { downloads.Pause(task.Id); throw; }
        }
        return task;
    }
    public async Task<InstalledApplication> InstallAsync(string taskId, bool downgradeConfirmed, CancellationToken cancellationToken, bool repair = false)
    {
        await installGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            PendingInstall intent;
            lock (gate) intent = Read().SingleOrDefault(item => item.TaskId == taskId) ?? throw new CatalogException("STORE_INSTALL_INTENT_MISSING");
            var task = downloads.Snapshot().SingleOrDefault(item => item.Id == taskId) ?? throw new CatalogException("DOWNLOAD_NOT_FOUND");
            if (task.LocalPath is null || task.Request.RepositoryId != intent.Repository.RepositoryId || task.Request.ReleaseId != intent.ReleaseId ||
                task.Request.AssetId != intent.AssetId || task.Request.Sha256 != intent.Release.Sha256 || task.Request.AppId != intent.Release.AppId ||
                task.Request.Version != intent.Release.Version || task.Request.ExpectedBytes != intent.Release.Bytes) throw new CatalogException("STORE_INSTALL_IDENTITY_MISMATCH");
            if (!repair && task.State == DownloadState.Completed && installer.Find(intent.Release.AppId) is { } existing &&
                existing.Package.Manifest.Version == intent.Release.Version && existing.Package.PackageSha256 == intent.Release.Sha256 &&
                existing.Source.Binding(existing.AppId) == new PackageSource(sourceKind, intent.Repository.RepositoryId).Binding(existing.AppId))
            {
                DemandManifestMatch(intent.Release, existing.Package.Manifest);
                ApplicationInstallService.VerifyInstalled(existing.Package); return existing;
            }
            if (!downloads.MarkInstalling(taskId)) throw new CatalogException("DOWNLOAD_NOT_READY");
            try
            {
                return await Task.Run(() =>
                {
                    var release = intent.Release;
                    var inspection = PackageInstaller.Inspect(task.LocalPath, cancellationToken);
                    // A downloader uses opaque cache names; compare package bytes and fields, not that cache filename.
                    if (inspection.Sha256 != release.Sha256 || inspection.Manifest.AppId != release.AppId || inspection.Manifest.Version != release.Version)
                        throw new PackageException("PACKAGE_RELEASE_MISMATCH");
                    DemandManifestMatch(release, inspection.Manifest);
                    var expected = new ExpectedPackageIdentity(new(sourceKind, intent.Repository.RepositoryId, intent.Repository.FullName), release.AppId,
                        release.Version, release.Runtime, release.Entry, release.Permissions, release.Sha256, release.Bytes, release.SaveFormatVersion,
                        new(intent.ReleaseId, intent.AssetId, null, intent.StoreContentSha,
                            ReleaseCanonicalSha256: Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(release, ReleaseMetadata.JsonOptions)))));
                    var current = installer.Find(release.AppId);
                    var action = current is null ? InstallIntent.InstallOrUpdate : ApplicationInstallService.CompareVersions(release.Version, current.Package.Manifest.Version) < 0
                        ? InstallIntent.Downgrade : repair ? InstallIntent.Repair : InstallIntent.InstallOrUpdate;
                    var result = installer.Install(task.LocalPath, expected, action, sourceConfirmed: true, downgradeConfirmed, cancellationToken);
                    MarkCommittedReceipt(taskId);
                    return result;
                }, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                try { downloads.MarkInstallFailed(taskId, error is PackageException pe ? pe.Code : error is CatalogException ce ? ce.Code : "PACKAGE_INSTALL_FAILED"); }
                catch (Exception receiptError) when (receiptError is IOException or UnauthorizedAccessException or ObjectDisposedException)
                { RecordWarning("INSTALL_RECEIPT_RECONCILIATION_REQUIRED"); }
                throw;
            }
        }
        finally { installGate.Release(); }
    }
    private void MarkCommittedReceipt(string taskId)
    {
        // The registry is already committed. A closed UI/queue or failed receipt write cannot undo it.
        try { if (!downloads.MarkInstalled(taskId)) RecordWarning("INSTALL_RECEIPT_RECONCILIATION_REQUIRED"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ObjectDisposedException or OperationCanceledException)
        { RecordWarning("INSTALL_RECEIPT_RECONCILIATION_REQUIRED"); }
    }
    private void RecordWarning(string code)
    { lock (gate) { if (!recoveryWarnings.Contains(code)) recoveryWarnings.Add(code); } }

    private void ReconcileCommittedReceipts()
    {
        var awaiting = downloads.Snapshot().Where(task => task.State is DownloadState.AwaitingInstall or DownloadState.Installing).ToArray();
        if (awaiting.Length == 0) return;
        PendingInstall[] intents = Read();
        foreach (var task in awaiting)
        {
            var intent = intents.SingleOrDefault(item => item.TaskId == task.Id);
            if (intent is null || task.Request.AppId != intent.Release.AppId || task.Request.Version != intent.Release.Version ||
                task.Request.Sha256 != intent.Release.Sha256 || task.Request.ExpectedBytes != intent.Release.Bytes ||
                task.Request.RepositoryId != intent.Repository.RepositoryId || task.Request.ReleaseId != intent.ReleaseId || task.Request.AssetId != intent.AssetId) continue;
            var installed = installer.Find(intent.Release.AppId);
            if (installed is null || installed.Package.PackageSha256 != intent.Release.Sha256 || installed.Package.Manifest.Version != intent.Release.Version ||
                installed.Source.Binding(installed.AppId) != new PackageSource(sourceKind, intent.Repository.RepositoryId).Binding(installed.AppId)) continue;
            try
            {
                DemandManifestMatch(intent.Release, installed.Package.Manifest);
                ApplicationInstallService.VerifyInstalled(installed.Package);
                if (task.State == DownloadState.Installing || downloads.MarkInstalling(task.Id)) MarkCommittedReceipt(task.Id);
            }
            catch (Exception error) when (error is PackageException or IOException or UnauthorizedAccessException or ObjectDisposedException)
            { RecordWarning("INSTALL_RECONCILIATION_REJECTED"); }
        }
    }
    private PendingInstall[] Read()
    {
        DownloadFiles.Validate(contextsPath);
        if (!File.Exists(contextsPath)) return [];
        if (new FileInfo(contextsPath).Length > 1024 * 1024) throw new CatalogException("STORE_INSTALL_HISTORY_INVALID");
        try
        {
            using JsonDocument document = ReleaseMetadata.Document(File.ReadAllBytes(contextsPath), 1024 * 1024);
            var value = document.RootElement.Deserialize<PendingInstall[]>(IntentJson) ?? throw new CatalogException("STORE_INSTALL_HISTORY_INVALID");
            if (value.Length > 200 || value.Any(v => v is null || v.Repository is null || v.Release is null || v.TaskId is null) ||
                value.Select(v => v.TaskId).Distinct(StringComparer.Ordinal).Count() != value.Length) throw new CatalogException("STORE_INSTALL_HISTORY_INVALID");
            foreach (var item in value)
            {
                if (!Regex.IsMatch(item.TaskId, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant) ||
                    item.Repository.RepositoryId <= 0 || item.ReleaseId <= 0 || item.AssetId <= 0 ||
                    item.Repository.Owner is null || !Regex.IsMatch(item.Repository.Owner, "^[A-Za-z0-9][A-Za-z0-9-]{0,38}$", RegexOptions.CultureInvariant) ||
                    item.Repository.Name is null || item.Repository.Name is "." or ".." || !Regex.IsMatch(item.Repository.Name, "^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant) ||
                    item.StoreContentSha is not null && !Regex.IsMatch(item.StoreContentSha, "^(?:[a-f0-9]{40}|[a-f0-9]{64})$", RegexOptions.CultureInvariant))
                    throw new CatalogException("STORE_INSTALL_HISTORY_INVALID");
                _ = ReleaseMetadata.ParseRelease(JsonSerializer.SerializeToUtf8Bytes(item.Release, ReleaseMetadata.JsonOptions));
            }
            return value;
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or CatalogException)
        { throw new CatalogException("STORE_INSTALL_HISTORY_INVALID"); }
    }
    private static void DemandManifestMatch(ReleaseManifest release, AutumnPackageManifest manifest)
    {
        if (release.AppId != manifest.AppId || release.Version != manifest.Version || release.Runtime != manifest.Runtime ||
            release.Entry != manifest.Entry || release.SaveFormatVersion != manifest.SaveFormatVersion ||
            !release.Permissions.Order(StringComparer.Ordinal).SequenceEqual(manifest.Permissions.Order(StringComparer.Ordinal)))
            throw new PackageException("PACKAGE_RELEASE_MISMATCH");
    }
}
