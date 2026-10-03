using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.Store;
using AutumnOS.Packages;

namespace AutumnOS.Tests;

internal static class StoreCatalogTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("store.release_save_format_bounds_match_install_contract", () =>
        {
            Check(ReleaseMetadata.ParseRelease(Bytes(Release() with { SaveFormatVersion = PackageInstaller.MaximumSaveFormatVersion })).SaveFormatVersion == 1_000_000);
            foreach (int invalid in new[] { 0, -1, PackageInstaller.MaximumSaveFormatVersion + 1, int.MaxValue })
                Reject(() => ReleaseMetadata.ParseRelease(Bytes(Release() with { SaveFormatVersion = invalid })), "STORE_RELEASE_INVALID");
        });
        yield return ("store.semver_numeric_preview_build_order", () =>
        {
            Check(SemanticVersion.Parse("1.10.0").CompareTo(SemanticVersion.Parse("1.9.0")) > 0);
            Check(SemanticVersion.Parse("1.0.0-preview.10").CompareTo(SemanticVersion.Parse("1.0.0-preview.2")) > 0);
            Check(SemanticVersion.Parse("1.0.0").CompareTo(SemanticVersion.Parse("1.0.0-preview.99")) > 0);
            Check(SemanticVersion.Parse("999999999999999999999999.0.0").CompareTo(SemanticVersion.Parse("9.0.0")) > 0);
            Check(SemanticVersion.Parse("1.0.0+abc").CompareTo(SemanticVersion.Parse("1.0.0+def")) == 0);
            foreach (string bad in new[] { "01.0.0", "1.0", "1.0.0-01", "v1.0.0", "1.0.0\n", "1.0.0-", "1.0.0+" }) Check(!SemanticVersion.TryParse(bad, out _));
        });
        yield return ("store.query_keeps_topic_and_rejects_operator_injection", () => Run(async root =>
        {
            Fixture f = new(); GitHubStoreCatalog catalog = Make(root, f);
            await catalog.SearchAsync("元素 配对");
            Check(Uri.UnescapeDataString(f.Requests[0].AbsoluteUri).Contains("q=topic:autumnos-app is:public 元素 配对", StringComparison.Ordinal));
            await RejectAsync(() => catalog.SearchAsync("topic:other OR is:private"), "STORE_QUERY_INVALID");
            Check(f.Requests.Count == 1);
        }));
        yield return ("store.empty_search_is_real_empty_not_error", () => Run(async root =>
        {
            CatalogPage page = await Make(root, new()).SearchAsync(); Check(page.Repositories.Count == 0 && page.TotalCount == 0 && !page.IncompleteResults);
        }));
        yield return ("store.topics_four_classifications_and_repository_id_deduplication", () => Run(async root =>
        {
            Fixture f = new() { SearchBody = new { total_count = 5, incomplete_results = false, items = new object[] {
                Repository(1, "pm", ["autumnos-app", "autumn-app-pm"]), Repository(2, "sq", ["autumnos-app", "autumn-app-sq"]),
                Repository(3, "none", ["autumnos-app"]), Repository(4, "both", ["autumnos-app", "autumn-app-pm", "autumn-app-sq"]), Repository(1, "duplicate", ["autumnos-app"]) } } };
            var page = await Make(root, f).SearchAsync(); Check(page.Repositories.Count == 4);
            Check(page.Repositories.Select(r => r.Category).SequenceEqual(new[] { CatalogCategory.Pm, CatalogCategory.Sq, CatalogCategory.Unclassified, CatalogCategory.Conflict }));
            Check(page.Repositories[0].CategoryLabel.Contains("未核验", StringComparison.Ordinal));
        }));
        yield return ("store.search_incomplete_and_1000_cap_are_explicit", () => Run(async root =>
        {
            Fixture f = new() { SearchBody = new { total_count = 1234, incomplete_results = true, items = Array.Empty<object>() } };
            var page = await Make(root, f).SearchAsync(); Check(page.IncompleteResults && page.WarningCode == "STORE_SEARCH_LIMIT");
        }));
        yield return ("store.search_pagination_bounded_and_current_page", () => Run(async root =>
        {
            Fixture f = new() { SearchBody = new { total_count = 31, incomplete_results = false, items = Enumerable.Range(1, 30).Select(i => Repository(i, "repo" + i, ["autumnos-app"])).ToArray() } };
            var page = await Make(root, f).SearchAsync(page: 1); Check(page.HasMore && page.Page == 1 && page.Repositories.Count == 30);
            await RejectAsync(() => Make(root, f).SearchAsync(page: 35), "STORE_QUERY_INVALID");
        }));
        yield return ("store.etag_304_and_offline_cache_survive_catalog_restart", () => Run(async root =>
        {
            Fixture f = new(); var first = await Make(root, f).SearchAsync(); f.Mode = "not-modified";
            var second = await Make(root, f).SearchAsync(); Check(second.IsCached && second.FetchedAt == first.FetchedAt && f.SawConditional);
            f.Mode = "offline"; var third = await Make(root, f).SearchAsync(); Check(third.IsCached && third.WarningCode == "STORE_OFFLINE_CACHE");
        }));
        yield return ("store.first_offline_is_error_not_empty_results", () => Run(async root => await RejectAsync(() => Make(root, new() { Mode = "offline" }).SearchAsync(), "STORE_OFFLINE")));
        yield return ("store.rate_limit_blocks_repeat_without_network_storm", () => Run(async root =>
        {
            Fixture f = new() { Mode = "rate" }; var catalog = Make(root, f);
            await RejectAsync(() => catalog.SearchAsync(), "STORE_RATE_LIMITED");
            await RejectAsync(() => catalog.SearchAsync(), "STORE_RATE_LIMITED"); Check(f.Requests.Count == 1);
        }));
        yield return ("store.cancelled_request_never_returns_cached_success", () => Run(async root =>
        {
            Fixture f = new(); var catalog = Make(root, f); await catalog.SearchAsync(); using CancellationTokenSource cts = new(); cts.Cancel();
            try { await catalog.SearchAsync(cancellationToken: cts.Token); throw new InvalidOperationException("Cancellation ignored."); } catch (OperationCanceledException) { }
        }));
        yield return ("store.rejects_oversize_and_html_api_response", () => Run(async root =>
        {
            await RejectAsync(() => Make(Path.Combine(root, "huge"), new() { Mode = "huge" }).SearchAsync(), "STORE_RESPONSE_TOO_LARGE");
            await RejectAsync(() => Make(Path.Combine(root, "html"), new() { Mode = "html" }).SearchAsync(), "STORE_RESPONSE_INVALID");
        }));
        yield return ("store.root_manifest_unknown_duplicate_and_future_schema_rejected", () =>
        {
            byte[] valid = Bytes(Store()); Check(ReleaseMetadata.ParseStore(valid).AppId == "fixture.catalog");
            string json = Encoding.UTF8.GetString(valid);
            Reject(() => ReleaseMetadata.ParseStore(Encoding.UTF8.GetBytes(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal))), "STORE_METADATA_DUPLICATE_FIELD");
            Reject(() => ReleaseMetadata.ParseStore(Encoding.UTF8.GetBytes(json.Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal))), "STORE_SCHEMA_UNSUPPORTED");
            Reject(() => ReleaseMetadata.ParseStore(Encoding.UTF8.GetBytes(json.Insert(1, "\"approve\":true,"))), "STORE_METADATA_INVALID");
        });
        yield return ("store.release_shared_parser_unknown_schema_permissions_hash_channel", () =>
        {
            Check(ReleaseMetadata.ParseRelease(Bytes(Release())).Version == "1.0.0");
            foreach (ReleaseManifest bad in new[] { Release() with { SchemaVersion = 2 }, Release() with { Channel = "preview" }, Release() with { Permissions = ["saves", "saves"] },
                Release() with { Sha256 = "nope" }, Release() with { Asset = "setup.exe" }, Release() with { Version = "1.0.0-01" } })
            {
                try { ReleaseMetadata.ParseRelease(Bytes(bad)); throw new InvalidOperationException("Accepted invalid release."); } catch (CatalogException) { }
                try { ReleaseManifestValidator.Parse(Bytes(bad)); throw new InvalidOperationException("Developer accepted invalid release."); } catch (PackageException) { }
            }
        });
        yield return ("store.default_branch_contents_sha_etag_and_valid_release_asset", () => Run(async root =>
        {
            Fixture f = new() { HasRelease = true }; var details = await Make(root, f).GetDetailsAsync(Repo());
            Check(details.Store?.Name == "受控目录测试" && details.StoreContentSha?.Length == 40 && details.StoreETag == "\"fixture\"");
            Check(details.Versions.Count == 1 && details.Versions[0].CanInstall && details.Versions[0].Asset!.AssetId == 102);
            Check(f.Requests.Any(u => u.AbsolutePath.EndsWith("/contents/autumn.store.json", StringComparison.Ordinal) && u.Query.Length == 0));
            Check(!f.Requests.Any(u => u.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal)));
        }));
        yield return ("store.no_releases_is_explicit", () => Run(async root =>
        {
            var details = await Make(root, new()).GetDetailsAsync(Repo()); Check(details.Versions.Count == 0 && details.Warnings.Contains("STORE_NO_RELEASES"));
        }));
        foreach ((string mode, string code) in new[] { ("no-manifest", "STORE_RELEASE_METADATA_MISSING"), ("no-package", "STORE_PACKAGE_ASSET_MISSING"),
            ("wrong-app", "STORE_APP_ID_MISMATCH"), ("wrong-url", "STORE_ASSET_SOURCE_INVALID"), ("wrong-size", "STORE_ASSET_SIZE_MISMATCH"),
            ("wrong-digest", "STORE_METADATA_DIGEST_MISMATCH"), ("ambiguous", "STORE_ASSET_AMBIGUOUS"), ("preview-mismatch", "STORE_RELEASE_VERSION_MISMATCH"),
            ("wrong-tag", "STORE_ASSET_SOURCE_INVALID"), ("future-host", "STORE_HOST_TOO_OLD"), ("unknown-runtime", "STORE_RUNTIME_UNSUPPORTED") })
            yield return ("store.release_rejects_" + mode, () => Run(async root =>
            {
                var details = await Make(root, new() { HasRelease = true, ReleaseMode = mode }).GetDetailsAsync(Repo());
                Check(details.Versions.Count == 1 && !details.Versions[0].CanInstall && details.Versions[0].UnavailableReason == code,
                    "Expected " + code + " got " + details.Versions.FirstOrDefault()?.UnavailableReason);
            }));
        yield return ("store.repository_recreation_cannot_take_selected_identity", () => Run(async root =>
            await RejectAsync(() => Make(root, new() { Mode = "recreated" }).GetDetailsAsync(Repo()), "STORE_SOURCE_CHANGED")));
        yield return ("store.repository_identity_rechecked_on_historical_page", () => Run(async root =>
            await RejectAsync(() => Make(root, new() { Mode = "recreated" }).GetVersionsAsync(Repo(), Store(), 2), "STORE_SOURCE_CHANGED")));
        yield return ("store.image_rejects_foreign_repository_and_svg_before_network", () => Run(async root =>
        {
            Fixture f = new(); var catalog = Make(root, f);
            await RejectAsync(() => catalog.GetScreenshotAsync(Repo(), "https://github.com/foreign/repo/blob/main/image.png"), "STORE_IMAGE_SOURCE_INVALID");
            await RejectAsync(() => catalog.GetScreenshotAsync(Repo(), "https://github.com/fixture/repo/blob/main/image.svg"), "STORE_IMAGE_UNSUPPORTED"); Check(f.Requests.Count == 0);
        }));
        yield return ("store.generator_and_inspection_share_all_release_fields", () => Run(async root =>
        {
            string project = Path.Combine(root, "project"), output = Path.Combine(root, "output"); Directory.CreateDirectory(project);
            File.WriteAllBytes(Path.Combine(project, "manifest.json"), Bytes(new AutumnPackageManifest(1, "fixture.catalog", "受控目录测试", "1.0.0", "web", "index.html", ["saves"])));
            File.WriteAllText(Path.Combine(project, "index.html"), "<!doctype html><title>Isolated catalog test</title>");
            using DeveloperToolsService tools = new(); tools.SetEnabled(true);
            DeveloperBuild build = tools.BuildProject(project, output);
            ReleaseManifest release = ReleaseMetadata.GenerateRelease(build.PackagePath);
            string metadata = Path.Combine(root, "autumn.release.json"); File.WriteAllBytes(metadata, Bytes(release));
            Check(tools.ValidateRelease(metadata, build.PackagePath).Sha256 == release.Sha256);
            ReleaseMetadata.MatchPackage(release, build.Inspection, build.PackagePath);
            Reject(() => ReleaseMetadata.MatchPackage(release with { SaveFormatVersion = 2 }, build.Inspection, build.PackagePath), "STORE_PACKAGE_METADATA_MISMATCH");
            await Task.CompletedTask;
        }));
        yield return ("store.isolated_fixture_real_http_catalog_download_and_hash", () => Run(async root =>
        {
            string fixtureDirectory = FixtureDirectory();
            using StoreTestSource source = new(fixtureDirectory) { PackageChunkDelayMilliseconds = 0 };
            var catalog = new GitHubStoreCatalog(Path.Combine(root, "cache"), "0.4.0", "0.3.0", source);
            var results = await catalog.SearchAsync(); Check(results.Repositories.Count == 1 && results.Repositories[0].RepositoryId == StoreTestSource.RepositoryId);
            var details = await catalog.GetDetailsAsync(results.Repositories[0]); Check(details.Versions.Count == 5 && details.Versions.All(v => v.CanInstall));
            CatalogVersion selected = details.Versions.Single(v => v.Version == "1.0.0");
            Check(source.IsControlledPackage(StoreTestSource.AppId, selected.Manifest!.Sha256));
            Check(!source.IsControlledPackage("cn.labchronicles.elementpairs", selected.Manifest.Sha256));
            var manifest = selected.Manifest;
            using DownloadService downloads = new(root, source, () => new StoreNetworkSettings());
            DownloadSnapshot task = downloads.Enqueue(new(manifest.AppId, manifest.Version, selected.Asset!.DownloadUri, manifest.Bytes, manifest.Sha256,
                StoreTestSource.RepositoryId, selected.ReleaseId, selected.Asset.AssetId));
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline && downloads.Snapshot().Single(t => t.Id == task.Id).State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Verifying) await Task.Delay(30);
            var final = downloads.Snapshot().Single(t => t.Id == task.Id);
            Check(final.State == DownloadState.AwaitingInstall && final.BytesReceived == manifest.Bytes && final.LocalPath is not null, "Fixture bytes must traverse real HTTP/download validation.");
            PackageInspection inspection = PackageInstaller.Inspect(final.LocalPath!);
            Check(inspection.Sha256 == manifest.Sha256 && inspection.Manifest.AppId == StoreTestSource.AppId);
            // Download cache paths are opaque .autumn names, while original release asset name remains metadata.
            Check((await catalog.GetVersionsAsync(results.Repositories[0], details.Store, 2)).Versions.Count == 0);
        }));
        yield return ("store.fixture_index_replacement_cannot_authorize_arbitrary_runtime", () => Run(async root =>
        {
            string directory = Path.Combine(root, "tampered"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "fixture-index.json"), "{}");
            try { using StoreTestSource invalid = new(directory); throw new InvalidOperationException("Mutable fixture index was trusted."); }
            catch (InvalidDataException ex) when (ex.Message == "STORE_TEST_FIXTURE_CHANGED") { }
            await Task.CompletedTask;
        }));
    }

    private static CatalogRepository Repo() => new(42, "fixture", "repo", "test", ["autumnos-app", "autumn-app-sq"], CatalogCategory.Sq);
    private static object Repository(long id, string name, string[] topics) => new { id, name, owner = new { login = "fixture" }, description = "Only isolated fixture", @private = false, topics };
    private static StoreManifest Store() => new(1, "fixture.catalog", "受控目录测试", "本地隔离数据，非 GitHub 实时商品", "sq", new("测试作者"), [], true);
    private static ReleaseManifest Release() => new(1, "fixture.catalog", "1.0.0", "stable", "web", "0.3.0", "0.3.0", "index.html", "fixture.autumn", 123, new string('a', 64), ["saves"], 1);
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull });
    private static GitHubStoreCatalog Make(string root, Fixture fixture) => new(Path.Combine(root, "cache"), "0.4.0", "0.3.0", fixture);
    private static string FixtureDirectory()
    {
        for (string? root = Directory.GetCurrentDirectory(); root is not null; root = Path.GetDirectoryName(root))
        {
            string candidate = Path.Combine(root, "artifacts", "store-fixtures");
            if (File.Exists(Path.Combine(candidate, "fixture-index.json"))) return candidate;
        }
        throw new InvalidOperationException("Run New-StoreFixturePackages.ps1 before the build and tests.");
    }
    private static void Check(bool value, string message = "Catalog assertion failed.") { if (!value) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string code) { try { action(); } catch (CatalogException e) when (e.Code == code) { return; } throw new InvalidOperationException("Expected " + code); }
    private static async Task RejectAsync(Func<Task> action, string code) { try { await action(); } catch (CatalogException e) when (e.Code == code) { return; } throw new InvalidOperationException("Expected " + code); }
    private static void Run(Func<string, Task> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "autumnos-catalog-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root).GetAwaiter().GetResult(); } finally { Directory.Delete(root, true); }
    }
    private sealed class Fixture : IGitHubTransport
    {
        public string Mode = "", ReleaseMode = ""; public bool HasRelease, SawConditional;
        public object SearchBody = new { total_count = 0, incomplete_results = false, items = Array.Empty<object>() };
        public List<Uri> Requests = [];
        public Task<HttpResponseMessage> SendAsync(Uri url, GitHubRequestKind kind, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(url);
            if (Mode == "offline") throw new HttpRequestException("synthetic offline");
            if (Mode == "rate") { var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests); r.Headers.RetryAfter = new(TimeSpan.FromSeconds(60)); return Task.FromResult(r); }
            if (Mode == "not-modified") { SawConditional = headers?.GetValueOrDefault("If-None-Match") == "\"fixture\""; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified)); }
            if (Mode == "huge") return Task.FromResult(Response(new byte[2 * 1024 * 1024 + 1]));
            if (Mode == "html") { var r = Response("<html>failure</html>"u8.ToArray()); r.Content.Headers.ContentType = new("text/html"); return Task.FromResult(r); }
            if (url.AbsolutePath == "/search/repositories") return Task.FromResult(Response(Bytes(SearchBody)));
            if (url.AbsolutePath == "/repos/fixture/repo") return Task.FromResult(Response(Bytes(Repository(Mode == "recreated" ? 99 : 42, "repo", ["autumnos-app", "autumn-app-sq"]))));
            if (url.AbsolutePath.EndsWith("/contents/autumn.store.json", StringComparison.Ordinal))
            {
                byte[] body = Bytes(Store()); byte[] prefix = Encoding.ASCII.GetBytes("blob " + body.Length + "\0");
                return Task.FromResult(Response(Bytes(new { type = "file", path = "autumn.store.json", encoding = "base64", content = Convert.ToBase64String(body), size = body.Length, sha = Convert.ToHexStringLower(SHA1.HashData(prefix.Concat(body).ToArray())) })));
            }
            ReleaseManifest manifest = ReleaseMode switch { "wrong-app" => Release() with { AppId = "foreign.game" }, "future-host" => Release() with { MinHostVersion = "99.0.0" },
                "unknown-runtime" => Release() with { Runtime = "native" }, _ => Release() };
            byte[] meta = Bytes(manifest);
            if (url.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal))
            {
                List<object> assets = [];
                if (ReleaseMode != "no-manifest") assets.Add(Asset(101, "autumn.release.json", meta.Length, "https://github.com/fixture/repo/releases/download/v1.0.0/autumn.release.json", "sha256:" + Convert.ToHexStringLower(SHA256.HashData(meta))));
                if (ReleaseMode != "no-package") assets.Add(Asset(102, "fixture.autumn", ReleaseMode == "wrong-size" ? 124 : 123,
                    ReleaseMode == "wrong-url" ? "https://github.com/foreign/repo/releases/download/v1.0.0/fixture.autumn" : "https://github.com/fixture/repo/releases/download/v1.0.0/fixture.autumn",
                    "sha256:" + new string(ReleaseMode == "wrong-digest" ? 'b' : 'a', 64)));
                if (ReleaseMode == "ambiguous") assets.Add(assets[^1]);
                object item = new { id = 10, tag_name = ReleaseMode == "wrong-tag" ? "v9.0.0" : "v1.0.0", draft = false, prerelease = ReleaseMode == "preview-mismatch", published_at = "2026-10-02T00:00:00Z", body = "Isolated release notes", assets };
                return Task.FromResult(Response(Bytes(HasRelease ? new[] { item } : Array.Empty<object>())));
            }
            if (url.AbsolutePath.EndsWith("autumn.release.json", StringComparison.Ordinal)) return Task.FromResult(Response(meta));
            throw new InvalidOperationException("Unexpected fixture URI.");
        }
        private static object Asset(long id, string name, int size, string url, string digest) => new { id, name, size, browser_download_url = url, digest };
        private static HttpResponseMessage Response(byte[] bytes)
        {
            HttpResponseMessage r = new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }; r.Headers.ETag = new("\"fixture\""); r.Content.Headers.ContentType = new("application/json"); return r;
        }
    }
}
