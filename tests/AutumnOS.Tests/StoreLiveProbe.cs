using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Store;

namespace AutumnOS.Tests;

/// <summary>Real anonymous public GitHub read; never substitutes fixtures or uploads.</summary>
internal static class StoreLiveProbe
{
    internal static async Task<int> RunAsync(string reportPath, string? repositoryReference = null)
    {
        string path = Path.GetFullPath(reportPath);
        if (File.Exists(path)) throw new IOException("Probe report already exists; preserve prior evidence.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string cache = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-public-cache");
        using GitHubTransport transport = new(() => new StoreNetworkSettings());
        ObservedTransport observed = new(transport);
        GitHubStoreCatalog catalog = new(cache, BrandInfo.Version, "0.3.0", observed);
        using CancellationTokenSource budget = new(TimeSpan.FromSeconds(90));
        List<object> details = [];
        CatalogPage? page = null; string? failure = null; DateTimeOffset? retryAfter = null;
        CatalogRepository? resolvedRepository = null; bool detailFailed = false;
        DateTimeOffset started = DateTimeOffset.UtcNow;
        try
        {
            IEnumerable<CatalogRepository> repositories;
            if (repositoryReference is null)
            {
                page = await catalog.SearchAsync(cancellationToken: budget.Token);
                repositories = page.Repositories.Take(2);
            }
            else
            {
                resolvedRepository = await catalog.ResolveRepositoryAsync(repositoryReference, budget.Token);
                repositories = [resolvedRepository];
            }
            foreach (CatalogRepository repository in repositories)
            {
                try
                {
                    CatalogDetails detail = await catalog.GetDetailsAsync(repository, budget.Token);
                    details.Add(new
                    {
                        repository_id = repository.RepositoryId, repository = repository.FullName,
                        category = repository.Category.ToString(), is_cached = detail.IsCached,
                        topics = detail.Repository.Topics,
                        store_present = detail.Store is not null, store_blob_sha = detail.StoreContentSha,
                        store_etag = detail.StoreETag, fetched_at = detail.FetchedAt,
                        versions_loaded = detail.Versions.Count, has_more_versions = detail.HasMoreVersions,
                        installable_release_count = detail.Versions.Count(v => v.CanInstall),
                        versions = detail.Versions.Select(v => new { release_id = v.ReleaseId, version = v.Version, can_install = v.CanInstall, reason = v.UnavailableReason }),
                        warnings = detail.Warnings, status = "read"
                    });
                }
                catch (CatalogException ex) { detailFailed = true; details.Add(new { repository_id = repository.RepositoryId, repository = repository.FullName, status = "failed", code = ex.Code, retry_after = ex.RetryAfter }); }
                catch (OperationCanceledException) { detailFailed = true; details.Add(new { repository_id = repository.RepositoryId, status = "not_run", code = "PROBE_TIME_BUDGET" }); break; }
            }
        }
        catch (CatalogException ex) { failure = ex.Code; retryAfter = ex.RetryAfter; }
        catch (OperationCanceledException) { failure = "PROBE_TIME_BUDGET"; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        { failure = ex.GetType().Name; }
        bool passed = failure is null && (repositoryReference is null ? page is not null : resolvedRepository is not null && !detailFailed && details.Count == 1);
        var report = new
        {
            schema_version = 1, build_id = BrandInfo.BuildId, source_snapshot_id = BrandInfo.SourceSnapshotId,
            started_utc = started, finished_utc = DateTimeOffset.UtcNow, environment = Environment.OSVersion.VersionString,
            kind = "real_public_GitHub_production_adapter_not_fixture", query = repositoryReference is null ? GitHubStoreCatalog.DiscoveryQuery : null,
            method = "GET", endpoint = repositoryReference is null ? "https://api.github.com/search/repositories" : "https://api.github.com/repos/owner/name",
            repository = resolvedRepository?.FullName, authorization = "none", remote_writes = "none",
            status = passed ? "passed" : "failed", failure, retry_after = retryAfter, exchanges = observed.Exchanges,
            total_count = page?.TotalCount, loaded_count = page?.Repositories.Count, page = page?.Page,
            incomplete_results = page?.IncompleteResults, has_more = page?.HasMore, is_cached = page?.IsCached,
            fetched_at = page?.FetchedAt, warning_code = page?.WarningCode, details,
            real_release_install = "not_run", fixture_results = "separate_report",
            interpretation = repositoryReference is not null ? "Repository metadata only; missing releases do not prove a successful download, installation or game launch." :
                page is null ? "Network failure is not an empty store." : page.Repositories.Count == 0 ? "Real public query returned zero loaded repositories; no demonstration products inserted." : "Only the loaded public page and at most two details were inspected."
        };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json); Console.WriteLine(json);
        return passed ? 0 : 1;
    }
    private sealed class ObservedTransport(IGitHubTransport production) : IGitHubTransport
    {
        public List<object> Exchanges { get; } = [];
        public async Task<HttpResponseMessage> SendAsync(Uri url, GitHubRequestKind kind, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            HttpResponseMessage response = await production.SendAsync(url, kind, headers, cancellationToken);
            string? Header(string name) => response.Headers.TryGetValues(name, out var values) ? string.Join(",", values).Substring(0, Math.Min(200, string.Join(",", values).Length)) : null;
            if (Exchanges.Count < 70) Exchanges.Add(new
            {
                request_path = url.GetLeftPart(UriPartial.Path), status = (int)response.StatusCode, at = DateTimeOffset.UtcNow,
                etag = Header("ETag"), last_modified = response.Content.Headers.LastModified,
                rate_limit_remaining = Header("X-RateLimit-Remaining"), rate_limit_reset = Header("X-RateLimit-Reset"),
                retry_after = Header("Retry-After"), rate_limit_resource = Header("X-RateLimit-Resource")
            });
            return response;
        }
    }
}
