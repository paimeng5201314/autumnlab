namespace AutumnOS.Store;

public enum CatalogCategory { Unclassified, Pm, Sq, Conflict }
public sealed record CatalogRepository(long RepositoryId, string Owner, string Name, string Description,
    string[] Topics, CatalogCategory Category)
{
    public string FullName => Owner + "/" + Name;
    public string Url => "https://github.com/" + FullName;
    public string CategoryLabel => Category switch
    {
        CatalogCategory.Pm => "PM · 受认可分类（仓库自标，未核验）",
        CatalogCategory.Sq => "SQ · 社区分类（仓库自标）",
        CatalogCategory.Conflict => "PM / SQ 标签冲突（未核验）",
        _ => "未分类"
    };
}
public sealed record CatalogPage(IReadOnlyList<CatalogRepository> Repositories, int Page, bool HasMore,
    int TotalCount, bool IncompleteResults, bool IsCached, DateTimeOffset FetchedAt, string? WarningCode);
public sealed record StoreDeveloper(string Name, string? Url = null);
public sealed record StoreManifest(int SchemaVersion, string AppId, string Name, string Description,
    string Category, StoreDeveloper Developer, string[] Screenshots, bool OfflineCapable);
public sealed record ReleaseManifest(int SchemaVersion, string AppId, string Version, string Channel,
    string Runtime, string MinHostVersion, string MinSdkVersion, string Entry, string Asset, long Bytes,
    string Sha256, string[] Permissions, int SaveFormatVersion);
public sealed record CatalogAsset(long AssetId, string Name, long Size, Uri DownloadUri, string? Digest = null);
public sealed record CatalogVersion(long ReleaseId, string Tag, DateTimeOffset PublishedAt, string Notes,
    bool Prerelease, ReleaseManifest? Manifest, CatalogAsset? Asset, bool Compatible, string? UnavailableReason)
{
    public bool CanInstall => Manifest is not null && Asset is not null && Compatible && UnavailableReason is null;
    public string Version => Manifest?.Version ?? Tag;
}
public sealed record CatalogDetails(CatalogRepository Repository, StoreManifest? Store,
    IReadOnlyList<CatalogVersion> Versions, IReadOnlyList<string> Warnings, DateTimeOffset FetchedAt,
    string? StoreContentSha = null, string? StoreETag = null, bool IsCached = false, bool HasMoreVersions = false);
public sealed record CatalogVersionPage(IReadOnlyList<CatalogVersion> Versions, int Page, bool HasMore, bool IsCached, DateTimeOffset FetchedAt);
public sealed record CatalogImage(byte[] Bytes, string MediaType);
public interface IStoreCatalog
{
    Task<CatalogPage> SearchAsync(string? search = null, int page = 1, CancellationToken cancellationToken = default);
    Task<CatalogDetails> GetDetailsAsync(CatalogRepository repository, CancellationToken cancellationToken = default);
    Task<CatalogVersionPage> GetVersionsAsync(CatalogRepository repository, StoreManifest? store, int page = 1, CancellationToken cancellationToken = default);
    Task<CatalogImage> GetScreenshotAsync(CatalogRepository repository, string imageUrl, CancellationToken cancellationToken = default);
}
public sealed class CatalogException(string code, DateTimeOffset? retryAfter = null) : Exception(code)
{
    public string Code { get; } = code;
    public DateTimeOffset? RetryAfter { get; } = retryAfter;
}
