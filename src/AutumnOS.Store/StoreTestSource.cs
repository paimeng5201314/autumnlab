using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.Packages;

namespace AutumnOS.Store;

/// <summary>Explicit developer-only loopback fixture. Production catalog never constructs this class.</summary>
public sealed class StoreTestSource : IGitHubTransport, IDisposable
{
    public const string AppId = "cn.labchronicles.storeprobe";
    public const long RepositoryId = 90004001;
    public const string Owner = "autumnos-isolated-test", RepositoryName = "store-probe";
    public const string Label = "本地集成测试数据，不是 GitHub 实时结果";
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource shutdown = new();
    private readonly HttpClient client = new(new HttpClientHandler { UseProxy = false, UseCookies = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };
    private readonly Dictionary<string, Payload> payloads = new(StringComparer.Ordinal);
    private readonly HashSet<string> controlledHashes = new(StringComparer.Ordinal);
    private readonly string capability = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    private readonly SemaphoreSlim connections = new(4, 4);
    private readonly Task server;
    private bool disposed;
    private readonly byte[] repositoryBytes, searchBytes, releaseBytes;
    public Uri LocalEndpoint { get; }
    public int PackageChunkDelayMilliseconds { get; set; } = 65;

    public StoreTestSource(string fixtureDirectory)
    {
        string directory = Path.GetFullPath(fixtureDirectory);
        DownloadFiles.Validate(directory);
        byte[] indexBytes = Read(directory, "fixture-index.json", 65536);
        using Stream trusted = typeof(StoreTestSource).Assembly.GetManifestResourceStream("AutumnOS.Store.FixtureIndex.json")
            ?? throw new InvalidDataException("STORE_TEST_FIXTURE_NOT_EMBEDDED");
        using MemoryStream embedded = new(); trusted.CopyTo(embedded);
        if (!SHA256.HashData(indexBytes).SequenceEqual(SHA256.HashData(embedded.ToArray()))) throw new InvalidDataException("STORE_TEST_FIXTURE_CHANGED");
        using JsonDocument index = JsonDocument.Parse(indexBytes);
        if (index.RootElement.GetProperty("appId").GetString() != AppId || index.RootElement.GetProperty("repositoryId").GetInt64() != RepositoryId)
            throw new InvalidDataException("STORE_TEST_FIXTURE_CHANGED");
        List<object> releases = []; int releaseIndex = 0;
        foreach (JsonElement entry in index.RootElement.GetProperty("versions").EnumerateArray())
        {
            string version = entry.GetProperty("version").GetString()!, name = entry.GetProperty("file").GetString()!;
            byte[] bytes = Read(directory, name, (int)ReleaseMetadata.MaximumPackageBytes);
            string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (sha != entry.GetProperty("sha256").GetString() || bytes.LongLength != entry.GetProperty("bytes").GetInt64()) throw new InvalidDataException("STORE_TEST_FIXTURE_CHANGED");
            PackageInspection inspection = PackageInstaller.Inspect(Path.Combine(directory, name));
            if (inspection.Manifest.AppId != AppId || inspection.Manifest.Version != version) throw new InvalidDataException("STORE_TEST_FIXTURE_CHANGED");
            byte[] metadata = Read(directory, entry.GetProperty("metadata").GetString()!, 65536);
            ReleaseManifest release = ReleaseMetadata.ParseRelease(metadata);
            bool bad = entry.GetProperty("badHash").GetBoolean();
            if (!bad)
            {
                ReleaseMetadata.MatchPackage(release, inspection, Path.Combine(directory, name));
                controlledHashes.Add(sha);
            }
            else if (release.Sha256 == sha) throw new InvalidDataException("STORE_TEST_BAD_HASH_FIXTURE_INVALID");
            long releaseId = 40001 + releaseIndex++, metadataId = releaseId * 10, assetId = metadataId + 1;
            string prefix = "https://github.com/" + Owner + "/" + RepositoryName + "/releases/download/v" + version + "/";
            string metaUrl = prefix + "autumn.release.json", assetUrl = prefix + name;
            Add(metaUrl, metadata, false); Add(assetUrl, bytes, true);
            object[] assets =
            [
                new { id = metadataId, name = "autumn.release.json", size = metadata.LongLength, browser_download_url = metaUrl, digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(metadata)) },
                // No optional GitHub digest for the bad-hash fixture: failure must occur on downloaded bytes.
                new { id = assetId, name, size = bytes.LongLength, browser_download_url = assetUrl, digest = bad ? (string?)null : "sha256:" + sha }
            ];
            releases.Add(new { id = releaseId, tag_name = "v" + version, draft = false, prerelease = release.Channel == "preview", published_at = "2026-10-02T00:00:00Z",
                body = Label + (bad ? "；此版本故意提供错误 SHA-256，必须下载失败且不安装。" : "；验证独立样例的下载、安装、后台与存档流程。"), assets });
        }
        object repo = new { id = RepositoryId, name = RepositoryName, owner = new { login = Owner }, description = Label, @private = false, topics = new[] { "autumnos-app", "autumn-app-sq" } };
        repositoryBytes = JsonSerializer.SerializeToUtf8Bytes(repo);
        searchBytes = JsonSerializer.SerializeToUtf8Bytes(new { total_count = 1, incomplete_results = false, items = new[] { repo } });
        releaseBytes = JsonSerializer.SerializeToUtf8Bytes(releases);
        byte[] store = Read(directory, "autumn.store.json", 65536); _ = ReleaseMetadata.ParseStore(store);
        byte[] contents = JsonSerializer.SerializeToUtf8Bytes(new { type = "file", path = "autumn.store.json", encoding = "base64",
            content = Convert.ToBase64String(store), size = store.LongLength, sha = Convert.ToHexStringLower(SHA1.HashData(Encoding.ASCII.GetBytes("blob " + store.Length + "\0").Concat(store).ToArray())) });
        string api = "https://api.github.com/repos/" + Owner + "/" + RepositoryName;
        Add(api, repositoryBytes, false); Add(api + "/contents/autumn.store.json", contents, false);
        Add("fixture-search", searchBytes, false); Add("fixture-releases", releaseBytes, false);
        Add("fixture-empty-releases", "[]"u8.ToArray(), false);
        Add("fixture-empty-search", "{\"total_count\":0,\"incomplete_results\":false,\"items\":[]}"u8.ToArray(), false);
        listener.Start(4);
        LocalEndpoint = new("http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/");
        server = ServeAsync();
    }

    public bool IsControlledPackage(string appId, string sha256) => !disposed && appId == AppId && controlledHashes.Contains(sha256);

    public async Task<HttpResponseMessage> SendAsync(Uri url, GitHubRequestKind kind, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        GitHubTransport.ValidateSource(url, kind);
        string key = url.AbsoluteUri;
        if (url.Host == "api.github.com" && url.AbsolutePath == "/search/repositories")
        {
            string query = Uri.UnescapeDataString(url.Query);
            if (!query.Contains(GitHubStoreCatalog.DiscoveryQuery, StringComparison.Ordinal)) throw new HttpRequestException("STORE_TEST_QUERY_INVALID");
            key = IsFirstPage(url) ? "fixture-search" : "fixture-empty-search";
        }
        else if (url.AbsolutePath == "/repos/" + Owner + "/" + RepositoryName + "/releases")
            key = IsFirstPage(url) ? "fixture-releases" : "fixture-empty-releases";
        string id = Hash(key);
        if (!payloads.ContainsKey(id)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        using HttpRequestMessage request = new(HttpMethod.Get, new Uri(LocalEndpoint, capability + "/" + id));
        if (headers is not null)
            foreach (var header in headers)
                if (header.Key is "Range" or "If-Range" or "If-None-Match" or "If-Modified-Since" or "Accept")
                    request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    private void Add(string key, byte[] bytes, bool package) => payloads.Add(Hash(key), new(bytes, "\"" + Convert.ToHexStringLower(SHA256.HashData(bytes)) + "\"", package));
    private static bool IsFirstPage(Uri uri) => uri.Query.TrimStart('?').Split('&').Contains("page=1", StringComparer.Ordinal);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static byte[] Read(string directory, string name, int maximum)
    {
        if (name != Path.GetFileName(name) || name.Contains('/') || name.Contains('\\')) throw new InvalidDataException("STORE_TEST_FIXTURE_PATH_INVALID");
        string path = Path.Combine(directory, name); DownloadFiles.Validate(path);
        if (new FileInfo(path).Length > maximum) throw new InvalidDataException("STORE_TEST_FIXTURE_TOO_LARGE");
        return File.ReadAllBytes(path);
    }
    private async Task ServeAsync()
    {
        try
        {
            while (!shutdown.IsCancellationRequested)
            {
                await connections.WaitAsync(shutdown.Token);
                TcpClient connection;
                try { connection = await listener.AcceptTcpClientAsync(shutdown.Token); }
                catch { connections.Release(); throw; }
                _ = HandleAsync(connection);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private async Task HandleAsync(TcpClient connection)
    {
        using (connection)
        using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            try
            {
                NetworkStream stream = connection.GetStream(); List<byte> header = [];
                byte[] one = new byte[1];
                while (header.Count < 8192)
                {
                    if (await stream.ReadAsync(one, timeout.Token) != 1) return;
                    header.Add(one[0]);
                    if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
                }
                string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n");
                string[] request = lines[0].Split(' ');
                if (request.Length != 3 || request[0] != "GET" || !request[1].StartsWith("/" + capability + "/", StringComparison.Ordinal)) return;
                string id = request[1][(capability.Length + 2)..];
                if (!payloads.TryGetValue(id, out var payload)) return;
                Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase);
                foreach (string line in lines.Skip(1).Where(l => l.Length > 0))
                {
                    int split = line.IndexOf(':'); if (split <= 0 || !headers.TryAdd(line[..split], line[(split + 1)..].Trim())) return;
                }
                if (headers.GetValueOrDefault("Host") != "127.0.0.1:" + LocalEndpoint.Port || headers.ContainsKey("Content-Length") || headers.ContainsKey("Transfer-Encoding")) return;
                if (headers.GetValueOrDefault("If-None-Match") == payload.ETag)
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 304 Not Modified\r\nConnection: close\r\nETag: " + payload.ETag + "\r\n\r\n"), timeout.Token); return;
                }
                long start = 0;
                if (payload.Package && headers.TryGetValue("Range", out string? range) && (headers.GetValueOrDefault("If-Range") is null || headers.GetValueOrDefault("If-Range") == payload.ETag))
                {
                    if (!range.StartsWith("bytes=", StringComparison.Ordinal) || !range.EndsWith('-') || !long.TryParse(range[6..^1], out start) || start < 0 || start >= payload.Bytes.LongLength) return;
                }
                string status = start == 0 ? "200 OK" : "206 Partial Content";
                string rangeHeader = start == 0 ? "" : "Content-Range: bytes " + start + "-" + (payload.Bytes.Length - 1) + "/" + payload.Bytes.Length + "\r\n";
                string response = "HTTP/1.1 " + status + "\r\nConnection: close\r\nContent-Type: " + (payload.Package ? "application/octet-stream" : "application/json") +
                    "\r\nContent-Length: " + (payload.Bytes.Length - start) + "\r\nETag: " + payload.ETag + "\r\nAccept-Ranges: bytes\r\n" + rangeHeader + "\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token);
                for (int position = (int)start; position < payload.Bytes.Length; position += 8192)
                {
                    await stream.WriteAsync(payload.Bytes.AsMemory(position, Math.Min(8192, payload.Bytes.Length - position)), timeout.Token);
                    if (payload.Package) await Task.Delay(Math.Clamp(PackageChunkDelayMilliseconds, 0, 200), timeout.Token);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException or ObjectDisposedException) { }
            finally { connections.Release(); }
        }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; shutdown.Cancel(); listener.Stop(); client.Dispose();
        // In-flight handlers observe cancellation; no synchronous UI wait or forced process termination.
    }
    private sealed record Payload(byte[] Bytes, string ETag, bool Package);
}
