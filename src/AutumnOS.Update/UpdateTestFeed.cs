using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AutumnOS.Store;

namespace AutumnOS.Update;

/// <summary>Compile-time isolated feed. No setting, SDK call, URI or command-line argument can enable it in ordinary builds.</summary>
public sealed class UpdateTestFeed : IGitHubTransport
{
    private readonly string root;
    private UpdateTestFeed(string root) { this.root = Path.GetFullPath(root); UpdatePaths.EnsureNoReparsePoints(this.root); }
    internal static UpdateTestFeed? TryCreateEmbedded()
    {
#if AUTUMNOS_UPDATE_TEST_BUILD
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("AutumnOS.Update.TestFeed.txt");
        if (stream is null) throw new UpdateException("UPDATE_TEST_FEED_NOT_CONFIGURED");
        if (stream.Length > 4096) throw new UpdateException("UPDATE_TEST_FEED_INVALID");
        using StreamReader reader = new(stream); string directory = reader.ReadToEnd().Trim();
        if (!Path.IsPathFullyQualified(directory)) throw new UpdateException("UPDATE_TEST_FEED_INVALID");
        return new(directory);
#else
        return null;
#endif
    }
    public async Task<HttpResponseMessage> SendAsync(Uri url, GitHubRequestKind kind, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        GitHubTransport.ValidateSource(url, kind); cancellationToken.ThrowIfCancellationRequested();
        if (kind == GitHubRequestKind.Api)
        {
            if (url.AbsolutePath != $"/repos/{UpdateService.Repository}/releases") throw new UpdateException("UPDATE_TEST_FEED_URL_REJECTED");
            int page = 1;
            foreach (string pair in url.Query.TrimStart('?').Split('&')) if (pair.StartsWith("page=", StringComparison.Ordinal) && !int.TryParse(pair[5..], out page)) throw new UpdateException("UPDATE_TEST_FEED_URL_REJECTED");
            if (page < 1) throw new UpdateException("UPDATE_TEST_FEED_URL_REJECTED");
            string path = Path.Combine(root, "releases.json"); UpdatePaths.EnsureNoReparsePoints(path);
            if (!File.Exists(path)) return new(HttpStatusCode.NotFound);
            if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new UpdateException("UPDATE_TEST_FEED_TOO_LARGE");
            using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false));
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(json.RootElement.EnumerateArray().Skip((page - 1) * 100).Take(100).ToArray());
            return Respond(new ByteArrayContent(bytes), Convert.ToHexStringLower(SHA256.HashData(bytes)), headers);
        }
        string prefix = $"/{UpdateService.Repository}/releases/download/";
        if (!url.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) throw new UpdateException("UPDATE_TEST_FEED_URL_REJECTED");
        string[] parts = url.AbsolutePath[prefix.Length..].Split('/').Select(Uri.UnescapeDataString).ToArray();
        if (parts.Length != 2 || parts.Any(p => p.Length is < 1 or > 200 || p is "." or ".." || p.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_' or '+'))))
            throw new UpdateException("UPDATE_TEST_FEED_URL_REJECTED");
        string file = Path.GetFullPath(Path.Combine(root, parts[0], parts[1])); UpdatePaths.EnsureNoReparsePoints(file);
        if (!file.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new UpdateException("UPDATE_TEST_FEED_URL_REJECTED");
        if (!File.Exists(file)) return new(HttpStatusCode.NotFound);
        FileStream content = new(file, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
        try
        {
            string digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(content, cancellationToken).ConfigureAwait(false)); content.Position = 0;
            string etag = '"' + digest + '"'; long offset = 0;
            if (headers?.GetValueOrDefault("If-Range") == etag && headers.GetValueOrDefault("Range") is { } range &&
                range.StartsWith("bytes=", StringComparison.Ordinal) && range.EndsWith('-') && long.TryParse(range[6..^1], out long parsed)) offset = parsed;
            if (offset < 0 || offset >= content.Length) { await content.DisposeAsync(); return new(HttpStatusCode.RequestedRangeNotSatisfiable); }
            content.Position = offset;
            var response = Respond(new StreamContent(content), digest, headers);
            if (offset > 0) { response.StatusCode = HttpStatusCode.PartialContent; response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset, content.Length - 1, content.Length); }
            return response;
        }
        catch { await content.DisposeAsync(); throw; }
    }
    private static HttpResponseMessage Respond(HttpContent content, string digest, IReadOnlyDictionary<string, string>? headers)
    {
        string etag = '"' + digest + '"';
        if (headers?.GetValueOrDefault("If-None-Match") == etag) { content.Dispose(); return new(HttpStatusCode.NotModified); }
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        response.Headers.ETag = new(etag); response.Headers.TryAddWithoutValidation("X-AutumnOS-Route", "compiled-local-test-feed");
        response.Content.Headers.ContentType = new("application/octet-stream"); return response;
    }
}
