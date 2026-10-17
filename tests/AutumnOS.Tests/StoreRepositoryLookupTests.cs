using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.Store;

namespace AutumnOS.Tests;

internal static class StoreRepositoryLookupTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        foreach ((string name, string input) in new[]
        {
            ("short_name", "fixture/repo"),
            ("trimmed_short_name", " \tfixture/repo\r\n"),
            ("https_url", "https://github.com/fixture/repo"),
            ("trailing_slash", "https://github.com/fixture/repo/"),
            ("host_case", "https://GITHUB.COM/fixture/repo"),
            ("trimmed_https_url", " \r\nhttps://github.com/fixture/repo/\t ")
        })
            yield return ("store.repository_lookup_accepts_" + name, () => Run(async root =>
            {
                Fixture fixture = new();
                IStoreCatalog catalog = Make(root, fixture);
                CatalogRepository result = await catalog.ResolveRepositoryAsync(input);
                Check(result.RepositoryId == 42 && result.FullName == "fixture/repo");
                Check(fixture.Requests.Count == 1 && fixture.Requests[0].Url.AbsoluteUri == "https://api.github.com/repos/fixture/repo");
                Check(fixture.Requests[0].Kind == GitHubRequestKind.Api);
            }));

        yield return ("store.repository_lookup_preserves_returned_id_and_canonical_name", () => Run(async root =>
        {
            Fixture fixture = new() { RepositoryBody = Repository(9876543210, "Canonical-Owner", "Renamed.Repo", ["autumn-app-pm"]) };
            CatalogRepository result = await Make(root, fixture).ResolveRepositoryAsync("Requested-Owner/Old.Repo");
            Check(result.RepositoryId == 9876543210 && result.FullName == "Canonical-Owner/Renamed.Repo");
            Check(result.Category == CatalogCategory.Pm && result.Description == "Synthetic public repository");
            Check(fixture.Requests.Single().Url.AbsoluteUri == "https://api.github.com/repos/Requested-Owner/Old.Repo");
        }));
        yield return ("store.repository_lookup_can_inspect_public_repository_without_topic", () => Run(async root =>
        {
            Fixture fixture = new() { Topics = [] };
            CatalogRepository result = await Make(root, fixture).ResolveRepositoryAsync("fixture/repo");
            Check(result.RepositoryId == 42 && result.Topics.Length == 0 && result.Category == CatalogCategory.Unclassified);
            Check(fixture.Requests.Count == 1);
        }));

        foreach ((string name, string input) in new[]
        {
            ("null", (string)null!), ("empty", ""), ("whitespace", " \r\n\t"),
            ("missing_owner", "/repo"), ("missing_name", "fixture/"), ("missing_separator", "fixture"),
            ("http", "http://github.com/fixture/repo"), ("ftp", "ftp://github.com/fixture/repo"),
            ("scheme_relative", "//github.com/fixture/repo"), ("foreign_host", "https://example.com/fixture/repo"),
            ("host_suffix", "https://github.com.example.com/fixture/repo"), ("api_host", "https://api.github.com/fixture/repo"),
            ("credentials", "https://user:password@github.com/fixture/repo"), ("username", "https://user@github.com/fixture/repo"),
            ("explicit_default_port", "https://github.com:443/fixture/repo"), ("nondefault_port", "https://github.com:8443/fixture/repo"),
            ("query", "https://github.com/fixture/repo?tab=readme"), ("empty_query", "https://github.com/fixture/repo?"),
            ("fragment", "https://github.com/fixture/repo#readme"), ("empty_fragment", "https://github.com/fixture/repo#"),
            ("encoded_owner", "https://github.com/%66ixture/repo"), ("encoded_separator", "https://github.com/fixture%2frepo"),
            ("encoded_dot", "https://github.com/fixture/%2e%2e/repo"), ("encoded_backslash", "https://github.com/fixture/repo%5c"),
            ("backslash", "https://github.com/fixture\\repo"), ("short_backslash", "fixture\\repo"),
            ("parent_segment", "https://github.com/fixture/../repo"), ("current_segment", "https://github.com/fixture/./repo"),
            ("dot_name", "fixture/."), ("parent_name", "fixture/.."), ("double_separator", "fixture//repo"),
            ("extra_path", "https://github.com/fixture/repo/releases"), ("double_trailing_slash", "https://github.com/fixture/repo//"),
            ("git_suffix", "fixture/repo.git"), ("git_suffix_case", "https://github.com/fixture/repo.GIT/"),
            ("invalid_owner", "_fixture/repo"), ("owner_leading_hyphen", "-fixture/repo"),
            ("invalid_name", "fixture/repo+name"), ("space", "fixture/repo name"), ("unicode", "fixture/仓库"),
            ("embedded_control", "fixture/re\npo"), ("owner_too_long", new string('a', 40) + "/repo"),
            ("unicode_casefold_owner", "fixtuKe/repo"), ("unicode_casefold_name", "fixture/reKo"),
            ("unicode_casefold_url", "https://github.com/fixtuKe/repo"), ("unicode_casefold_scheme", "httpſ://github.com/fixture/repo"),
            ("name_too_long", "fixture/" + new string('a', 101)), ("input_too_long", new string('a', 201))
        })
            yield return ("store.repository_lookup_rejects_" + name + "_before_network", () => Run(async root =>
            {
                Fixture fixture = new();
                await RejectAsync(() => Make(root, fixture).ResolveRepositoryAsync(input), "STORE_REPOSITORY_INVALID");
                Check(fixture.Requests.Count == 0, "Invalid repository reference reached HTTP.");
            }));

        yield return ("store.repository_lookup_rejects_private_response", () => Run(async root =>
        {
            Fixture fixture = new() { RepositoryBody = Repository(42, "fixture", "repo", [], isPrivate: true) };
            await RejectAsync(() => Make(root, fixture).ResolveRepositoryAsync("fixture/repo"), "STORE_SOURCE_INVALID");
            Check(fixture.Requests.Count == 1);
        }));
        yield return ("store.repository_lookup_rejects_invalid_response_identity", () => Run(async root =>
        {
            Fixture fixture = new() { RepositoryBody = Repository(0, "fixture", "repo", []) };
            await RejectAsync(() => Make(root, fixture).ResolveRepositoryAsync("fixture/repo"), "STORE_SOURCE_INVALID");
            Check(fixture.Requests.Count == 1);
        }));
        yield return ("store.repository_lookup_rejects_malformed_response", () => Run(async root =>
        {
            Fixture fixture = new() { RepositoryBody = new { message = "Missing required GitHub repository fields" } };
            await RejectAsync(() => Make(root, fixture).ResolveRepositoryAsync("fixture/repo"), "STORE_RESPONSE_INVALID");
            Check(fixture.Requests.Count == 1);
        }));
        yield return ("store.repository_lookup_not_found_is_not_an_empty_result", () => Run(async root =>
        {
            Fixture fixture = new() { RepositoryStatus = HttpStatusCode.NotFound };
            await RejectAsync(() => Make(root, fixture).ResolveRepositoryAsync("fixture/repo"), "STORE_NOT_FOUND");
            Check(fixture.Requests.Count == 1);
        }));
        yield return ("store.repository_lookup_first_offline_is_explicit", () => Run(async root =>
        {
            Fixture fixture = new() { Offline = true };
            await RejectAsync(() => Make(root, fixture).ResolveRepositoryAsync("fixture/repo"), "STORE_OFFLINE");
            Check(fixture.Requests.Count == 1);
        }));
        yield return ("store.repository_lookup_cancelled_request_never_returns_cached_success", () => Run(async root =>
        {
            Fixture fixture = new(); GitHubStoreCatalog catalog = Make(root, fixture);
            await catalog.ResolveRepositoryAsync("fixture/repo");
            using CancellationTokenSource cancellation = new(); cancellation.Cancel();
            await CancelledAsync(() => catalog.ResolveRepositoryAsync("fixture/repo", cancellation.Token));
            Check(fixture.Requests.Count == 1, "An already cancelled lookup reached HTTP.");
        }));
        yield return ("store.repository_lookup_cancellation_reaches_transport", () => Run(async root =>
        {
            using CancellationTokenSource cancellation = new();
            Fixture fixture = new() { OnRequest = cancellation.Cancel };
            await CancelledAsync(() => Make(root, fixture).ResolveRepositoryAsync("fixture/repo", cancellation.Token));
            Check(fixture.Requests.Count == 1);
        }));
        yield return ("store.repository_lookup_details_report_each_missing_requirement", () => Run(async root =>
        {
            Fixture fixture = new() { Topics = [], HasStore = false };
            GitHubStoreCatalog catalog = Make(root, fixture);
            CatalogRepository repository = await catalog.ResolveRepositoryAsync("fixture/repo");
            CatalogDetails details = await catalog.GetDetailsAsync(repository);
            Check(details.Repository.RepositoryId == 42 && details.Store is null && details.Versions.Count == 0);
            Check(details.Warnings.Contains("STORE_TOPIC_MISSING") && details.Warnings.Contains("STORE_METADATA_MISSING") && details.Warnings.Contains("STORE_NO_RELEASES"));
            Check(!fixture.Requests.Any(r => r.Url.AbsolutePath.StartsWith("/search/", StringComparison.Ordinal)));
        }));
        yield return ("store.repository_lookup_missing_topic_disables_otherwise_valid_release", () => Run(async root =>
        {
            Fixture fixture = new() { Topics = ["autumn-app-sq"], HasRelease = true };
            GitHubStoreCatalog catalog = Make(root, fixture);
            CatalogDetails details = await catalog.GetDetailsAsync(await catalog.ResolveRepositoryAsync("fixture/repo"));
            CatalogVersion version = details.Versions.Single();
            Check(details.Store?.AppId == Store().AppId && details.Warnings.Contains("STORE_TOPIC_MISSING"));
            Check(version.Manifest is not null && version.Asset is not null && !version.CanInstall && version.UnavailableReason == "STORE_TOPIC_MISSING");
        }));
        yield return ("store.repository_lookup_historical_page_rechecks_removed_topic", () => Run(async root =>
        {
            Fixture fixture = new() { HasRelease = true }; GitHubStoreCatalog catalog = Make(root, fixture);
            CatalogRepository repository = await catalog.ResolveRepositoryAsync("fixture/repo");
            CatalogDetails details = await catalog.GetDetailsAsync(repository);
            Check(details.Versions.Single().CanInstall, "The control release must be installable before the topic is removed.");
            int previousIdentityRequests = fixture.IdentityRequests;
            fixture.Topics = ["autumn-app-sq"];
            CatalogVersionPage historical = await catalog.GetVersionsAsync(repository, details.Store, 2);
            Check(fixture.IdentityRequests == previousIdentityRequests + 1, "Version lookup reused the selected card's stale topic.");
            Check(historical.Page == 2 && historical.Versions.Single().UnavailableReason == "STORE_TOPIC_MISSING" && !historical.Versions.Single().CanInstall);
            Check(historical.Repository?.RepositoryId == repository.RepositoryId && !historical.Repository.Topics.Contains("autumnos-app"), "Historical page must expose the revalidated source for previously loaded versions.");
            Check(fixture.Requests.Any(r => r.Url.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal) && r.Url.Query == "?per_page=30&page=2"));
        }));
    }

    private static object Repository(long id, string owner, string name, string[] topics, bool isPrivate = false) =>
        new { id, name, owner = new { login = owner }, description = "Synthetic public repository", @private = isPrivate, topics };
    private static StoreManifest Store() => new(1, "fixture.lookup", "仓库直达测试", "Only a local test fixture", "sq", new("测试作者"), [], true);
    private static ReleaseManifest Release() => new(1, "fixture.lookup", "1.0.0", "stable", "web", "0.3.0", "0.3.0", "index.html", "fixture.autumn", 123, new string('a', 64), ["saves"], 1);
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    private static GitHubStoreCatalog Make(string root, Fixture fixture) => new(Path.Combine(root, "cache"), "0.4.0", "0.3.0", fixture);
    private static void Check(bool condition, string message = "Repository lookup assertion failed.")
    {
        if (!condition) throw new InvalidOperationException(message);
    }
    private static async Task RejectAsync(Func<Task> action, string code)
    {
        try { await action(); }
        catch (CatalogException exception) when (exception.Code == code) { return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static async Task CancelledAsync(Func<Task> action)
    {
        try { await action(); }
        catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Repository lookup ignored cancellation.");
    }
    private static void Run(Func<string, Task> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "autumnos-repository-lookup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root).GetAwaiter().GetResult(); }
        finally { Directory.Delete(root, true); }
    }

    private sealed class Fixture : IGitHubTransport
    {
        public string[] Topics = ["autumnos-app", "autumn-app-sq"];
        public object? RepositoryBody;
        public HttpStatusCode RepositoryStatus = HttpStatusCode.OK;
        public bool HasStore = true, HasRelease, Offline;
        public Action? OnRequest;
        public int IdentityRequests;
        public List<(Uri Url, GitHubRequestKind Kind)> Requests = [];

        public Task<HttpResponseMessage> SendAsync(Uri url, GitHubRequestKind kind, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add((url, kind)); OnRequest?.Invoke(); cancellationToken.ThrowIfCancellationRequested();
            if (Offline) throw new HttpRequestException("Synthetic offline fixture");
            if (url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length != 0 || url.Fragment.Length != 0)
                throw new InvalidOperationException("Unexpected fixture request origin.");
            if (url.Host == "api.github.com" && url.AbsolutePath.StartsWith("/repos/", StringComparison.Ordinal) && url.AbsolutePath.Split('/').Length == 4)
            {
                IdentityRequests++;
                Check(url.Query.Length == 0 && kind == GitHubRequestKind.Api);
                return Task.FromResult(Response(Bytes(RepositoryBody ?? Repository(42, "fixture", "repo", Topics)), RepositoryStatus));
            }
            if (url.Host == "api.github.com" && url.AbsolutePath == "/repos/fixture/repo/contents/autumn.store.json")
            {
                if (!HasStore) return Task.FromResult(Response("{}"u8.ToArray(), HttpStatusCode.NotFound));
                byte[] body = Bytes(Store());
                byte[] header = Encoding.ASCII.GetBytes("blob " + body.Length + "\0");
                return Task.FromResult(Response(Bytes(new { type = "file", path = "autumn.store.json", encoding = "base64", content = Convert.ToBase64String(body), size = body.Length, sha = Convert.ToHexStringLower(SHA1.HashData(header.Concat(body).ToArray())) })));
            }
            byte[] metadata = Bytes(Release());
            if (url.Host == "api.github.com" && url.AbsolutePath == "/repos/fixture/repo/releases")
            {
                object release = new
                {
                    id = 10, tag_name = "v1.0.0", draft = false, prerelease = false, published_at = "2026-10-17T00:00:00Z", body = "Local fixture release",
                    assets = new[]
                    {
                        Asset(101, "autumn.release.json", metadata.Length, "sha256:" + Convert.ToHexStringLower(SHA256.HashData(metadata))),
                        Asset(102, "fixture.autumn", 123, "sha256:" + new string('a', 64))
                    }
                };
                return Task.FromResult(Response(Bytes(HasRelease ? new[] { release } : Array.Empty<object>())));
            }
            if (url.AbsoluteUri == "https://github.com/fixture/repo/releases/download/v1.0.0/autumn.release.json" && kind == GitHubRequestKind.Asset)
                return Task.FromResult(Response(metadata));
            throw new InvalidOperationException("Unexpected fixture URI.");
        }
        private static object Asset(long id, string name, int size, string digest) => new { id, name, size, digest, browser_download_url = "https://github.com/fixture/repo/releases/download/v1.0.0/" + name };
        private static HttpResponseMessage Response(byte[] bytes, HttpStatusCode status = HttpStatusCode.OK)
        {
            HttpResponseMessage response = new(status) { Content = new ByteArrayContent(bytes) };
            response.Headers.ETag = new("\"lookup-fixture\"");
            response.Content.Headers.ContentType = new("application/json");
            return response;
        }
    }
}
