using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutumnOS.Store;

public sealed record CatalogResponse(byte[] Bytes, DateTimeOffset FetchedAt, string? ETag, string? LastModified, bool IsCached, string? WarningCode = null);
internal sealed record CatalogCacheEntry(string Url, DateTimeOffset FetchedAt, string? ETag, string? LastModified, string Sha256, byte[] Bytes);

/// <summary>Bounded public bytes only. Conditional requests and rate-limit backoff never store auth headers.</summary>
public sealed class CatalogResponseCache
{
    private readonly string directory;
    private readonly IGitHubTransport transport;
    private readonly SemaphoreSlim gate = new(1, 1);
    private DateTimeOffset blockedUntil;
    private DateTimeOffset lastRequest;
    public CatalogResponseCache(string directory, IGitHubTransport transport)
    {
        if (!Path.IsPathFullyQualified(directory)) throw new CatalogException("STORE_CACHE_PATH_INVALID");
        this.directory = Path.GetFullPath(directory); this.transport = transport;
    }

    public async Task<CatalogResponse> GetAsync(Uri uri, GitHubRequestKind kind, int maximumBytes, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            CatalogCacheEntry? previous = Read(uri, maximumBytes);
            if (DateTimeOffset.UtcNow < blockedUntil)
                return previous is not null ? Cached(previous, "STORE_RATE_LIMITED_CACHE") : throw new CatalogException("STORE_RATE_LIMITED", blockedUntil);
            // A small same-client pacing interval avoids opening dozens of concurrent metadata connections.
            TimeSpan pause = lastRequest.AddMilliseconds(120) - DateTimeOffset.UtcNow;
            if (pause > TimeSpan.Zero) await Task.Delay(pause, token);
            Dictionary<string, string> headers = new(StringComparer.OrdinalIgnoreCase) { ["Accept"] = kind == GitHubRequestKind.Api ? "application/vnd.github+json" : "application/octet-stream" };
            if (previous?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out _)) headers["If-None-Match"] = etag;
            else if (previous?.LastModified is { } modified && DateTimeOffset.TryParse(modified, out _)) headers["If-Modified-Since"] = modified;
            using CancellationTokenSource bounded = CancellationTokenSource.CreateLinkedTokenSource(token); bounded.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                lastRequest = DateTimeOffset.UtcNow;
                using HttpResponseMessage response = await transport.SendAsync(uri, kind, headers, bounded.Token);
                if (response.StatusCode == HttpStatusCode.NotModified && previous is not null)
                    return Cached(previous, null); // Keep actual origin-read timestamp, not misleadingly the current time.
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                {
                    blockedUntil = RetryAfter(response);
                    return previous is not null ? Cached(previous, "STORE_RATE_LIMITED_CACHE") : throw new CatalogException("STORE_RATE_LIMITED", blockedUntil);
                }
                if (response.StatusCode == HttpStatusCode.NotFound) throw new CatalogException("STORE_NOT_FOUND");
                if ((int)response.StatusCode >= 500) return previous is not null ? Cached(previous, "STORE_OFFLINE_CACHE") : throw new CatalogException("STORE_SERVICE_UNAVAILABLE");
                if (!response.IsSuccessStatusCode) throw new CatalogException("STORE_HTTP_ERROR");
                if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > maximumBytes) throw new CatalogException("STORE_RESPONSE_TOO_LARGE");
                string? media = response.Content.Headers.ContentType?.MediaType;
                if (media is "text/html" or "application/xhtml+xml") throw new CatalogException("STORE_RESPONSE_INVALID");
                await using Stream input = await response.Content.ReadAsStreamAsync(bounded.Token);
                using MemoryStream output = new(); byte[] buffer = new byte[8192];
                int read;
                while ((read = await input.ReadAsync(buffer, bounded.Token)) != 0)
                {
                    if (output.Length + read > maximumBytes) throw new CatalogException("STORE_RESPONSE_TOO_LARGE");
                    output.Write(buffer, 0, read);
                }
                byte[] bytes = output.ToArray();
                if (bytes.Length == 0 || response.Content.Headers.ContentLength is long length && length != bytes.LongLength) throw new CatalogException("STORE_RESPONSE_INVALID");
                CatalogCacheEntry entry = new(uri.AbsoluteUri, DateTimeOffset.UtcNow, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified?.ToString("R"),
                    Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes);
                Write(entry);
                return new(bytes, entry.FetchedAt, entry.ETag, entry.LastModified, false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { return previous is not null ? Cached(previous, "STORE_OFFLINE_CACHE") : throw new CatalogException("STORE_TIMEOUT"); }
            catch (HttpRequestException)
            { return previous is not null ? Cached(previous, "STORE_OFFLINE_CACHE") : throw new CatalogException("STORE_OFFLINE"); }
        }
        finally { gate.Release(); }
    }

    private CatalogCacheEntry? Read(Uri uri, int maximum)
    {
        try
        {
            string path = PathFor(uri.AbsoluteUri); Safe(path);
            if (!File.Exists(path) || new FileInfo(path).Length > maximum * 2L + 4096) return null;
            CatalogCacheEntry? entry = JsonSerializer.Deserialize<CatalogCacheEntry>(File.ReadAllBytes(path));
            return entry is not null && entry.Url == uri.AbsoluteUri && entry.Bytes is { Length: > 0 } && entry.Bytes.Length <= maximum &&
                entry.FetchedAt <= DateTimeOffset.UtcNow.AddMinutes(1) && entry.FetchedAt >= DateTimeOffset.UtcNow.AddDays(-7) &&
                entry.ETag?.Length is not > 300 && entry.LastModified?.Length is not > 100 &&
                entry.Sha256 == Convert.ToHexStringLower(SHA256.HashData(entry.Bytes)) ? entry : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or CatalogException) { return null; }
    }
    private void Write(CatalogCacheEntry entry)
    {
        string? temp = null;
        try
        {
            Safe(directory); Directory.CreateDirectory(directory); Safe(directory);
            string target = PathFor(entry.Url); Safe(target);
            temp = target + ".tmp-" + Guid.NewGuid().ToString("N");
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(entry);
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { stream.Write(bytes); stream.Flush(true); }
            Safe(target); File.Move(temp, target, overwrite: true); temp = null;
            // Only exact owned cache names are eligible. Never delete a user's other cache/report files.
            var owned = Directory.EnumerateFiles(directory, "catalog-*.json").Where(f => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(f), "^catalog-[0-9a-f]{64}\\.json$"))
                .Select(f => new FileInfo(f)).OrderByDescending(f => f.LastWriteTimeUtc).ToArray();
            long retained = 0;
            for (int i = 0; i < owned.Length; i++)
            {
                retained += owned[i].Length;
                if (i >= 64 || retained > 32L * 1024 * 1024) { Safe(owned[i].FullName); File.Delete(owned[i].FullName); }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CatalogException) { /* Browse works without writable cache; never claim it was persisted. */ }
        finally
        {
            if (temp is not null) try { Safe(temp); File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CatalogException) { }
        }
    }
    private static DateTimeOffset RetryAfter(HttpResponseMessage response)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow, until = now.AddSeconds(60);
        if (response.Headers.RetryAfter?.Date is { } date) until = date;
        else if (response.Headers.RetryAfter?.Delta is { } delta) until = now + delta;
        else if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) && long.TryParse(resets.FirstOrDefault(), out long unix))
            try { until = DateTimeOffset.FromUnixTimeSeconds(unix); } catch (ArgumentOutOfRangeException) { }
        return until < now.AddSeconds(1) ? now.AddSeconds(1) : until;
    }
    private static CatalogResponse Cached(CatalogCacheEntry entry, string? warning) => new(entry.Bytes, entry.FetchedAt, entry.ETag, entry.LastModified, true, warning);
    private string PathFor(string uri) => Path.Combine(directory, "catalog-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri))) + ".json");
    private static void Safe(string path)
    {
        for (string? p = path; p is not null; p = Path.GetDirectoryName(p))
            if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new CatalogException("STORE_CACHE_PATH_INVALID");
    }
}
