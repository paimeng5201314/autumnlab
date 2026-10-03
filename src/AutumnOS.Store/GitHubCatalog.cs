using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutumnOS.Store;

/// <summary>Public read-only discovery. A search result is neither an installation nor a trust decision.</summary>
public sealed class GitHubStoreCatalog : IStoreCatalog
{
    public const string DiscoveryQuery = "topic:autumnos-app is:public";
    public const int PageSize = 30;
    private readonly string hostVersion, sdkVersion;
    private readonly CatalogResponseCache cache;

    public GitHubStoreCatalog(string cacheDirectory, string hostVersion, string sdkVersion, IGitHubTransport transport)
    {
        _ = SemanticVersion.Parse(hostVersion); _ = SemanticVersion.Parse(sdkVersion);
        this.hostVersion = hostVersion; this.sdkVersion = sdkVersion;
        cache = new(cacheDirectory, transport);
    }

    public async Task<CatalogPage> SearchAsync(string? search = null, int page = 1, CancellationToken cancellationToken = default)
    {
        search = search?.Trim() ?? "";
        if (search.Length > 100 || search.Any(c => !(char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')) || page is < 1 or > 34)
            throw new CatalogException("STORE_QUERY_INVALID");
        string query = DiscoveryQuery + (search.Length == 0 ? "" : " " + search);
        Uri uri = new("https://api.github.com/search/repositories?q=" + Uri.EscapeDataString(query) + "&sort=updated&order=desc&per_page=30&page=" + page);
        CatalogResponse response = await cache.GetAsync(uri, GitHubRequestKind.Api, 2 * 1024 * 1024, cancellationToken);
        try
        {
            using JsonDocument doc = ReleaseMetadata.Document(response.Bytes, 2 * 1024 * 1024);
            JsonElement root = doc.RootElement;
            int total = root.GetProperty("total_count").GetInt32();
            bool incomplete = root.GetProperty("incomplete_results").GetBoolean();
            if (total < 0) throw new CatalogException("STORE_RESPONSE_INVALID");
            JsonElement items = root.GetProperty("items");
            if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > PageSize) throw new CatalogException("STORE_RESPONSE_INVALID");
            List<CatalogRepository> repos = []; HashSet<long> ids = [];
            foreach (JsonElement item in items.EnumerateArray())
            {
                CatalogRepository repo = Repository(item);
                if (repo.Topics.Contains("autumnos-app", StringComparer.Ordinal) && ids.Add(repo.RepositoryId)) repos.Add(repo);
            }
            bool capped = total > 1000;
            return new(repos, page, page * PageSize < Math.Min(total, 1000) && items.GetArrayLength() == PageSize, total,
                incomplete || capped, response.IsCached, response.FetchedAt, response.WarningCode ?? (capped ? "STORE_SEARCH_LIMIT" : incomplete ? "STORE_INCOMPLETE_RESULTS" : null));
        }
        catch (Exception ex) when (Malformed(ex)) { throw new CatalogException("STORE_RESPONSE_INVALID"); }
    }

    public async Task<CatalogDetails> GetDetailsAsync(CatalogRepository repository, CancellationToken cancellationToken = default)
    {
        CheckRepository(repository);
        List<string> warnings = []; StoreManifest? store = null; string? sha = null, etag = null;
        // Re-resolve the immutable ID: a recreated owner/name must never silently inherit a selected card.
        CatalogResponse identity = await cache.GetAsync(Api(repository), GitHubRequestKind.Api, 256 * 1024, cancellationToken);
        try
        {
            using JsonDocument current = ReleaseMetadata.Document(identity.Bytes, 256 * 1024);
            CatalogRepository resolved = Repository(current.RootElement);
            if (resolved.RepositoryId != repository.RepositoryId) throw new CatalogException("STORE_SOURCE_CHANGED");
            repository = resolved;
        }
        catch (Exception ex) when (Malformed(ex)) { throw new CatalogException("STORE_RESPONSE_INVALID"); }
        CatalogResponse? metadata = null;
        try
        {
            metadata = await cache.GetAsync(Api(repository, "/contents/autumn.store.json"), GitHubRequestKind.Api, 128 * 1024, cancellationToken);
            using JsonDocument contents = ReleaseMetadata.Document(metadata.Bytes, 128 * 1024);
            JsonElement root = contents.RootElement;
            if (root.GetProperty("type").GetString() != "file" || root.GetProperty("path").GetString() != "autumn.store.json" || root.GetProperty("encoding").GetString() != "base64")
                throw new CatalogException("STORE_METADATA_INVALID");
            byte[] bytes = Convert.FromBase64String(root.GetProperty("content").GetString() ?? "");
            if (bytes.Length > ReleaseMetadata.MaximumMetadataBytes || root.GetProperty("size").GetInt64() != bytes.Length) throw new CatalogException("STORE_METADATA_INVALID");
            sha = root.GetProperty("sha").GetString();
            // Git's content-object ID provides an additional consistency check, not a publisher signature.
            byte[] header = Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0");
            string contentHash = Convert.ToHexStringLower(SHA1.HashData(header.Concat(bytes).ToArray()));
            if (sha != contentHash) throw new CatalogException("STORE_METADATA_DIGEST_MISMATCH");
            store = ReleaseMetadata.ParseStore(bytes); etag = metadata.ETag;
            if (store.Category != CategoryKey(repository.Category)) warnings.Add("STORE_CATEGORY_DIFFERS_FROM_TOPICS");
            if (metadata.WarningCode is not null) warnings.Add(metadata.WarningCode);
        }
        catch (CatalogException ex) { warnings.Add(ex.Code == "STORE_NOT_FOUND" ? "STORE_METADATA_MISSING" : ex.Code); }
        catch (Exception ex) when (Malformed(ex)) { warnings.Add("STORE_METADATA_INVALID"); }
        CatalogVersionPage versions = await GetVersionsAsync(repository, store, 1, cancellationToken);
        if (identity.WarningCode is not null) warnings.Add(identity.WarningCode);
        if (versions.HasMore) warnings.Add("STORE_MORE_VERSIONS_AVAILABLE");
        if (versions.Versions.Count == 0) warnings.Add("STORE_NO_RELEASES");
        return new(repository, store, versions.Versions, warnings.Distinct().ToArray(), metadata?.FetchedAt ?? identity.FetchedAt, sha, etag,
            identity.IsCached || metadata?.IsCached == true || versions.IsCached, versions.HasMore);
    }

    public async Task<CatalogVersionPage> GetVersionsAsync(CatalogRepository repository, StoreManifest? store, int page = 1, CancellationToken cancellationToken = default)
    {
        CheckRepository(repository);
        if (page is < 1 or > 34) throw new CatalogException("STORE_QUERY_INVALID");
        CatalogResponse identity = await cache.GetAsync(Api(repository), GitHubRequestKind.Api, 256 * 1024, cancellationToken);
        try
        {
            using JsonDocument current = ReleaseMetadata.Document(identity.Bytes, 256 * 1024);
            CatalogRepository resolved = Repository(current.RootElement);
            if (resolved.RepositoryId != repository.RepositoryId) throw new CatalogException("STORE_SOURCE_CHANGED");
        }
        catch (Exception ex) when (Malformed(ex)) { throw new CatalogException("STORE_RESPONSE_INVALID"); }
        CatalogResponse response = await cache.GetAsync(Api(repository, "/releases?per_page=30&page=" + page), GitHubRequestKind.Api, 2 * 1024 * 1024, cancellationToken);
        List<CatalogVersion> versions = []; bool fromCache = response.IsCached;
        try
        {
            using JsonDocument document = ReleaseMetadata.Document(response.Bytes, 2 * 1024 * 1024);
            JsonElement releases = document.RootElement;
            if (releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() > PageSize) throw new CatalogException("STORE_RESPONSE_INVALID");
            HashSet<long> ids = [];
            foreach (JsonElement release in releases.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                long id = release.GetProperty("id").GetInt64(); string tag = release.GetProperty("tag_name").GetString() ?? "";
                if (id <= 0 || !ReleaseMetadata.Text(tag, 128)) throw new CatalogException("STORE_RESPONSE_INVALID");
                if (!ids.Add(id)) continue;
                bool preview = release.GetProperty("prerelease").GetBoolean();
                bool draft = release.GetProperty("draft").GetBoolean();
                DateTimeOffset published = release.GetProperty("published_at").ValueKind == JsonValueKind.String ? release.GetProperty("published_at").GetDateTimeOffset() : DateTimeOffset.MinValue;
                string notes = SafeDescription(release.TryGetProperty("body", out var body) ? body.GetString() : null, 12000);
                ReleaseManifest? manifest = null; CatalogAsset? asset = null; string? error = draft ? "STORE_RELEASE_DRAFT" : null;
                try
                {
                    if (error is not null) throw new CatalogException(error);
                    JsonElement assets = release.GetProperty("assets");
                    if (assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 128) throw new CatalogException("STORE_ASSET_INVALID");
                    JsonElement[] metas = assets.EnumerateArray().Where(a => a.GetProperty("name").GetString() == "autumn.release.json").ToArray();
                    if (metas.Length != 1) throw new CatalogException(metas.Length == 0 ? "STORE_RELEASE_METADATA_MISSING" : "STORE_ASSET_AMBIGUOUS");
                    CatalogAsset meta = ReadAsset(metas[0], repository, tag);
                    if (meta.Size is <= 0 or > ReleaseMetadata.MaximumMetadataBytes) throw new CatalogException("STORE_RELEASE_INVALID");
                    CatalogResponse metadata = await cache.GetAsync(meta.DownloadUri, GitHubRequestKind.Asset, ReleaseMetadata.MaximumMetadataBytes, cancellationToken);
                    fromCache |= metadata.IsCached;
                    if (metadata.Bytes.LongLength != meta.Size) throw new CatalogException("STORE_METADATA_DIGEST_MISMATCH");
                    Digest(meta.Digest, Convert.ToHexStringLower(SHA256.HashData(metadata.Bytes)));
                    manifest = ReleaseMetadata.ParseRelease(metadata.Bytes);
                    if (store is null) throw new CatalogException("STORE_METADATA_REQUIRED");
                    if (manifest.AppId != store.AppId) throw new CatalogException("STORE_APP_ID_MISMATCH");
                    if (preview != (manifest.Channel == "preview") || tag != manifest.Version && tag != "v" + manifest.Version) throw new CatalogException("STORE_RELEASE_VERSION_MISMATCH");
                    JsonElement[] matched = assets.EnumerateArray().Where(a => a.GetProperty("name").GetString() == manifest.Asset).ToArray();
                    if (matched.Length != 1) throw new CatalogException(matched.Length == 0 ? "STORE_PACKAGE_ASSET_MISSING" : "STORE_ASSET_AMBIGUOUS");
                    asset = ReadAsset(matched[0], repository, tag);
                    if (!ReleaseMetadata.AssetName(asset.Name) || asset.Size != manifest.Bytes) throw new CatalogException("STORE_ASSET_SIZE_MISMATCH");
                    Digest(asset.Digest, manifest.Sha256);
                    error = ReleaseMetadata.Compatibility(manifest, hostVersion, sdkVersion);
                }
                catch (CatalogException ex) { error = ex.Code; }
                catch (Exception ex) when (Malformed(ex)) { error = "STORE_RELEASE_INVALID"; }
                versions.Add(new(id, tag, published, notes, preview, manifest, asset, error is null, error));
            }
            return new(versions.OrderByDescending(v => SemanticVersion.TryParse(v.Manifest?.Version, out var version) ? version : null).ThenByDescending(v => v.PublishedAt).ToArray(),
                page, releases.GetArrayLength() == PageSize && page < 34, fromCache, response.FetchedAt);
        }
        catch (Exception ex) when (Malformed(ex)) { throw new CatalogException("STORE_RESPONSE_INVALID"); }
    }

    public async Task<CatalogImage> GetScreenshotAsync(CatalogRepository repository, string imageUrl, CancellationToken cancellationToken = default)
    {
        CheckRepository(repository);
        if (!ReleaseMetadata.PublicHttps(imageUrl)) throw new CatalogException("STORE_IMAGE_SOURCE_INVALID");
        Uri url = new(imageUrl);
        string[] parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        int offset = url.Host == "github.com" ? 4 : url.Host == "raw.githubusercontent.com" ? 3 : -1;
        if (offset < 0 || parts.Length <= offset || parts[0] != repository.Owner || parts[1] != repository.Name ||
            offset == 4 && parts[2] != "blob" || parts.Any(p => p is "." or ".." || p.Contains('/') || p.Contains('\\') || p.Contains('?') || p.Contains('#')))
            throw new CatalogException("STORE_IMAGE_SOURCE_INVALID");
        string reference = parts[offset - 1], path = string.Join('/', parts.Skip(offset));
        string extension = Path.GetExtension(path).ToLowerInvariant();
        if (extension is not (".png" or ".jpg" or ".jpeg")) throw new CatalogException("STORE_IMAGE_UNSUPPORTED");
        Uri api = Api(repository, "/contents/" + string.Join('/', parts.Skip(offset).Select(Uri.EscapeDataString)) + "?ref=" + Uri.EscapeDataString(reference));
        CatalogResponse response = await cache.GetAsync(api, GitHubRequestKind.Api, 3 * 1024 * 1024, cancellationToken);
        try
        {
            using JsonDocument doc = ReleaseMetadata.Document(response.Bytes, 3 * 1024 * 1024);
            JsonElement root = doc.RootElement;
            if (root.GetProperty("type").GetString() != "file" || root.GetProperty("path").GetString() != path || root.GetProperty("encoding").GetString() != "base64")
                throw new CatalogException("STORE_IMAGE_INVALID");
            byte[] bytes = Convert.FromBase64String(root.GetProperty("content").GetString() ?? "");
            if (bytes.Length is < 24 or > 2 * 1024 * 1024 || root.GetProperty("size").GetInt64() != bytes.Length) throw new CatalogException("STORE_IMAGE_TOO_LARGE");
            string blobSha = Convert.ToHexStringLower(SHA1.HashData(Encoding.ASCII.GetBytes("blob " + bytes.Length + "\0").Concat(bytes).ToArray()));
            if (root.GetProperty("sha").GetString() != blobSha) throw new CatalogException("STORE_IMAGE_INVALID");
            if (bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) && bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8))
            {
                int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16, 4)), height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(20, 4));
                ImageDimensions(width, height); return new(bytes, "image/png");
            }
            if (bytes[0] == 255 && bytes[1] == 216)
            {
                int cursor = 2;
                while (cursor + 9 < bytes.Length)
                {
                    if (bytes[cursor++] != 255) break;
                    while (cursor < bytes.Length && bytes[cursor] == 255) cursor++;
                    if (cursor + 2 >= bytes.Length) break;
                    int marker = bytes[cursor++];
                    if (marker is 0xd8 or 0x01) continue;
                    int length = System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(cursor, 2));
                    if (length < 2 || cursor + length > bytes.Length) break;
                    if (marker is 0xc0 or 0xc1 or 0xc2)
                    {
                        ImageDimensions(System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(cursor + 5, 2)), System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(cursor + 3, 2)));
                        return new(bytes, "image/jpeg");
                    }
                    cursor += length;
                }
            }
            throw new CatalogException("STORE_IMAGE_INVALID");
        }
        catch (Exception ex) when (Malformed(ex)) { throw new CatalogException("STORE_IMAGE_INVALID"); }
    }
    private static void ImageDimensions(int width, int height)
    {
        if (width is < 1 or > 4096 || height is < 1 or > 4096 || (long)width * height > 12 * 1024 * 1024) throw new CatalogException("STORE_IMAGE_TOO_LARGE");
    }
    private static void Digest(string? digest, string sha256)
    {
        if (digest is not null && digest != "sha256:" + sha256) throw new CatalogException("STORE_METADATA_DIGEST_MISMATCH");
    }
    private static CatalogAsset ReadAsset(JsonElement element, CatalogRepository repo, string tag)
    {
        long id = element.GetProperty("id").GetInt64(), size = element.GetProperty("size").GetInt64();
        string name = element.GetProperty("name").GetString() ?? "";
        if (id <= 0 || size < 0 || !ReleaseMetadata.Text(name, 160) || name.Contains('/') || name.Contains('\\') ||
            !Uri.TryCreate(element.GetProperty("browser_download_url").GetString(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            Uri.UnescapeDataString(uri.AbsolutePath) != "/" + repo.FullName + "/releases/download/" + tag + "/" + name)
            throw new CatalogException("STORE_ASSET_SOURCE_INVALID");
        string? digest = element.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
        return new(id, name, size, uri, digest);
    }
    private static CatalogRepository Repository(JsonElement item)
    {
        long id = item.GetProperty("id").GetInt64();
        string owner = item.GetProperty("owner").GetProperty("login").GetString() ?? "", name = item.GetProperty("name").GetString() ?? "";
        if (item.GetProperty("private").GetBoolean()) throw new CatalogException("STORE_SOURCE_INVALID");
        string[] topics = item.TryGetProperty("topics", out JsonElement list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() <= 100
            ? list.EnumerateArray().Select(v => v.GetString() ?? "").Where(v => v.Length <= 100).Distinct(StringComparer.Ordinal).ToArray() : [];
        bool pm = topics.Contains("autumn-app-pm", StringComparer.Ordinal), sq = topics.Contains("autumn-app-sq", StringComparer.Ordinal);
        CatalogRepository result = new(id, owner, name, SafeDescription(item.TryGetProperty("description", out var d) ? d.GetString() : null, 1000), topics,
            pm && sq ? CatalogCategory.Conflict : pm ? CatalogCategory.Pm : sq ? CatalogCategory.Sq : CatalogCategory.Unclassified);
        CheckRepository(result); return result;
    }
    private static void CheckRepository(CatalogRepository repository)
    {
        if (repository.RepositoryId <= 0 || !Regex.IsMatch(repository.Owner, "^[A-Za-z0-9][A-Za-z0-9-]{0,38}$", RegexOptions.CultureInvariant) ||
            !Regex.IsMatch(repository.Name, "^[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}$", RegexOptions.CultureInvariant) || repository.Name is "." or "..")
            throw new CatalogException("STORE_SOURCE_INVALID");
    }
    private static string SafeDescription(string? value, int limit) => value is null ? "" : new string(value.Take(limit).Where(c => !char.IsControl(c) || c is '\r' or '\n' or '\t').ToArray());
    private static string CategoryKey(CatalogCategory category) => category switch { CatalogCategory.Pm => "pm", CatalogCategory.Sq => "sq", _ => "unclassified" };
    private static Uri Api(CatalogRepository repo, string suffix = "") => new("https://api.github.com/repos/" + repo.FullName + suffix);
    private static bool Malformed(Exception ex) => ex is JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException or ArgumentException;
}
