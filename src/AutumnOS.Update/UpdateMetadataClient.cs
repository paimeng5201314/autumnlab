using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.Store;

namespace AutumnOS.Update;

/// <summary>Host-update metadata only: conditional reads, bounded retry and persisted server cooldown.</summary>
internal sealed class UpdateMetadataClient(string directory, IGitHubTransport transport, Func<DateTimeOffset> clock,
    Func<DateTimeOffset?> blockedUntil, Action<DateTimeOffset> block)
{
    private sealed record Entry(string Url, string? ETag, string? LastModified, byte[] Bytes, string Digest);
    public async Task<byte[]> GetAsync(Uri uri, GitHubRequestKind kind, int maximum, CancellationToken token)
    {
        if (blockedUntil() is { } until && until > clock()) throw new UpdateException("UPDATE_RATE_LIMITED");
        string path = Path.Combine(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(uri.AbsoluteUri))) + ".json");
        Entry? previous = Read(path, uri, maximum);
        Dictionary<string, string> headers = new() { ["Accept"] = kind == GitHubRequestKind.Api ? "application/vnd.github+json" : "application/octet-stream", ["X-GitHub-Api-Version"] = "2022-11-28" };
        if (previous?.ETag is { } etag && EntityTagHeaderValue.TryParse(etag, out _)) headers["If-None-Match"] = etag;
        else if (previous?.LastModified is { } modified && DateTimeOffset.TryParse(modified, out _)) headers["If-Modified-Since"] = modified;
        for (int attempt = 0; ; attempt++)
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token); bounded.CancelAfter(TimeSpan.FromSeconds(20));
            try
            {
                using var response = await transport.SendAsync(uri, kind, headers, bounded.Token).ConfigureAwait(false);
                if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests || response.Headers.RetryAfter is not null && (int)response.StatusCode >= 500)
                { block(RetryAfter(response)); throw new UpdateException("UPDATE_RATE_LIMITED"); }
                if (response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining) && remaining.FirstOrDefault() == "0") block(RetryAfter(response));
                if (response.StatusCode == HttpStatusCode.NotModified && previous is not null) return previous.Bytes;
                if (response.StatusCode == HttpStatusCode.NotFound) throw new UpdateException("UPDATE_NOT_FOUND");
                if ((int)response.StatusCode >= 500 && attempt == 0) { await Task.Delay(250, token).ConfigureAwait(false); continue; }
                if ((int)response.StatusCode >= 500) throw new UpdateException("UPDATE_SERVICE_UNAVAILABLE");
                if (!response.IsSuccessStatusCode) throw new UpdateException("UPDATE_HTTP_ERROR");
                if (response.Content.Headers.ContentLength > maximum) throw new UpdateException("UPDATE_RESPONSE_TOO_LARGE");
                if (response.Content.Headers.ContentType?.MediaType is "text/html" or "application/xhtml+xml") throw new UpdateException("UPDATE_RESPONSE_INVALID");
                await using var input = await response.Content.ReadAsStreamAsync(bounded.Token).ConfigureAwait(false);
                using MemoryStream output = new(); byte[] buffer = new byte[8192]; int read;
                while ((read = await input.ReadAsync(buffer, bounded.Token).ConfigureAwait(false)) > 0)
                { if (output.Length + read > maximum) throw new UpdateException("UPDATE_RESPONSE_TOO_LARGE"); output.Write(buffer, 0, read); }
                byte[] bytes = output.ToArray();
                if (bytes.Length == 0 || response.Content.Headers.ContentLength is long length && length != bytes.Length) throw new UpdateException("UPDATE_RESPONSE_INVALID");
                Write(path, new(uri.AbsoluteUri, response.Headers.ETag?.ToString(), response.Content.Headers.LastModified?.ToString("R"), bytes, Hash(bytes)));
                return bytes;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new UpdateException("UPDATE_TIMEOUT"); }
            catch (HttpRequestException ex) { throw new UpdateException("UPDATE_OFFLINE", ex); }
        }
    }
    private DateTimeOffset RetryAfter(HttpResponseMessage response)
    {
        DateTimeOffset now = clock(), until = now.AddMinutes(1);
        if (response.Headers.RetryAfter?.Date is { } date) until = date;
        else if (response.Headers.RetryAfter?.Delta is { } delta) until = now + delta;
        else if (response.Headers.TryGetValues("X-RateLimit-Reset", out var resets) && long.TryParse(resets.FirstOrDefault(), out long unix))
            try { until = DateTimeOffset.FromUnixTimeSeconds(unix); } catch (ArgumentOutOfRangeException) { }
        return until < now.AddSeconds(1) ? now.AddSeconds(1) : until;
    }
    private Entry? Read(string path, Uri uri, int maximum)
    {
        try
        {
            UpdatePaths.EnsureNoReparsePoints(path);
            if (!File.Exists(path) || new FileInfo(path).Length > maximum * 2L + 4096) return null;
            var item = JsonSerializer.Deserialize<Entry>(File.ReadAllBytes(path));
            // The cache is disposable, including syntactically valid but incomplete JSON.
            // Never let a damaged cache bypass a fresh request or fault startup checking.
            return item is not null && item.Url == uri.AbsoluteUri && item.Bytes is { Length: > 0 } bytes &&
                bytes.Length <= maximum && item.Digest == Hash(bytes) ? item : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or UpdateException) { return null; }
    }
    private void Write(string path, Entry entry)
    {
        string? temp = null;
        try
        {
            UpdatePaths.EnsureNoReparsePoints(path); Directory.CreateDirectory(directory);
            temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(entry)); File.Move(temp, path, true); temp = null;
            long retained = 0; int count = 0;
            foreach (var file in Directory.EnumerateFiles(directory, "*.json").Where(p => Path.GetFileNameWithoutExtension(p).Length == 64).Select(p => new FileInfo(p)).OrderByDescending(f => f.LastWriteTimeUtc))
            { retained += file.Length; if (++count > 256 || retained > 64L * 1024 * 1024) { UpdatePaths.EnsureNoReparsePoints(file.FullName); file.Delete(); } }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or UpdateException) { }
        finally { if (temp is not null) try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
}
