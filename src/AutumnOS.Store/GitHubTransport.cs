using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Collections.Concurrent;

namespace AutumnOS.Store;

public enum GitHubRequestKind { Api, Asset }

public interface IGitHubTransport
{
    Task<HttpResponseMessage> SendAsync(Uri url, GitHubRequestKind kind,
        IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken);
}

public sealed record GitHubConnectionTest(bool Succeeded, string Code, string Route, int? StatusCode);

/// <summary>Restricted, cookie-free, credential-free transport. Test handlers are injected in code, never configuration.</summary>
public sealed class GitHubTransport : IGitHubTransport, IDisposable
{
    private readonly Func<StoreNetworkSettings> _settings;
    private readonly HttpClient _system;
    private readonly HttpClient _direct;
    private readonly bool _testHandler;
    private readonly ConcurrentDictionary<string, byte> _protectedHosts = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> AssetHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "github.com", "api.github.com", "release-assets.githubusercontent.com", "objects.githubusercontent.com",
        "github-releases.githubusercontent.com", "github-production-release-asset-2e65be.s3.amazonaws.com"
    };
    private static readonly HashSet<string> HeaderNames = new(StringComparer.OrdinalIgnoreCase)
        { "Accept", "If-None-Match", "If-Modified-Since", "Range", "If-Range", "X-GitHub-Api-Version" };

    public GitHubTransport(Func<StoreNetworkSettings> settings, HttpMessageHandler? testHandler = null)
    {
        _settings = settings;
        _testHandler = testHandler is not null;
        foreach (string host in AssetHosts) _protectedHosts.TryAdd(host, 0);
        if (testHandler is not null) _system = _direct = new(testHandler) { Timeout = TimeSpan.FromSeconds(30) };
        else
        {
            _system = MakeClient(true);
            _direct = MakeClient(false);
        }
    }
    private HttpClient MakeClient(bool proxy) => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, UseProxy = proxy, Credentials = null,
        AutomaticDecompression = DecompressionMethods.None, ConnectTimeout = TimeSpan.FromSeconds(15),
        ConnectCallback = async (context, token) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token).ConfigureAwait(false);
            // An explicitly configured Windows proxy may itself be local; only origin/accelerator hosts are protected.
            if (_protectedHosts.ContainsKey(context.DnsEndPoint.Host) && (addresses.Length == 0 || addresses.Any(a => !IsPublicAddress(a))))
                throw new HttpRequestException("GITHUB_PRIVATE_ADDRESS_NOT_ALLOWED");
            foreach (var address in addresses)
            {
                var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token).ConfigureAwait(false);
                    return new NetworkStream(socket, ownsSocket: true);
                }
                catch (SocketException) { socket.Dispose(); }
                catch { socket.Dispose(); throw; }
            }
            throw new HttpRequestException("GITHUB_CONNECTION_FAILED");
        }
    }) { Timeout = TimeSpan.FromSeconds(30) };

    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        byte[] bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
            return bytes[0] is not (0 or 10 or 127) && bytes[0] < 224 &&
                !(bytes[0] == 100 && bytes[1] is >= 64 and <= 127) &&
                !(bytes[0] == 169 && bytes[1] == 254) && !(bytes[0] == 172 && bytes[1] is >= 16 and <= 31) &&
                !(bytes[0] == 192 && bytes[1] is 168 or 0) && !(bytes[0] == 198 && bytes[1] is 18 or 19);
        return bytes.Length == 16 && !address.Equals(IPAddress.IPv6Any) && !address.IsIPv6LinkLocal && !address.IsIPv6SiteLocal &&
            !address.IsIPv6Multicast && (bytes[0] & 0xfe) != 0xfc && bytes[0] != 0;
    }

    public static void ValidateSource(Uri url, GitHubRequestKind kind)
    {
        if (!url.IsAbsoluteUri || url.Scheme != "https" || !url.IsDefaultPort || url.UserInfo.Length != 0 || url.Fragment.Length != 0)
            throw new ArgumentException("GITHUB_URL_NOT_ALLOWED");
        bool allowed = kind == GitHubRequestKind.Api ? url.Host.Equals("api.github.com", StringComparison.OrdinalIgnoreCase)
            : url.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
                System.Text.RegularExpressions.Regex.IsMatch(url.AbsolutePath, @"^/[^/]+/[^/]+/releases/download/[^/]+/[^/]+$")
                && url.Query.Length == 0;
        if (!allowed) throw new ArgumentException("GITHUB_URL_NOT_ALLOWED");
    }

    public static void ValidateTemplate(string template)
    {
        if (string.IsNullOrEmpty(template)) return;
        if (template.Length > 2048 || template.Count(c => c == '{') != 1 || template.Count(c => c == '}') != 1 ||
            !template.Contains("{url}", StringComparison.Ordinal) || template.Any(char.IsControl))
            throw new ArgumentException("GITHUB_TEMPLATE_INVALID");
        const string marker = "autumnos-encoded-url-marker";
        if (!Uri.TryCreate(template.Replace("{url}", marker, StringComparison.Ordinal), UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Host.Contains(marker, StringComparison.Ordinal) || uri.HostNameType != UriHostNameType.Dns ||
            !uri.Host.Contains('.') || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("account.labchronicles.cn", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("GITHUB_TEMPLATE_INVALID");
        if (uri.Query.Contains("token=", StringComparison.OrdinalIgnoreCase) || uri.Query.Contains("secret=", StringComparison.OrdinalIgnoreCase) ||
            uri.Query.Contains("key=", StringComparison.OrdinalIgnoreCase) || uri.Query.Contains("password=", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("GITHUB_TEMPLATE_CREDENTIALS_NOT_ALLOWED");
        // Only a single URL-valued query parameter is supported. Arbitrary static query values may contain secrets.
        if (uri.Query.Length != 0)
        {
            string query = Uri.UnescapeDataString(uri.Query[1..]);
            int separator = query.IndexOf('=');
            if (separator <= 0 || query.Contains('&') || query[(separator + 1)..] != marker ||
                query[..separator].Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))
                throw new ArgumentException("GITHUB_TEMPLATE_INVALID");
        }
    }

    public static Uri ApplyTemplate(string template, Uri original)
    {
        ValidateTemplate(template);
        return string.IsNullOrEmpty(template) ? original : new(template.Replace("{url}", Uri.EscapeDataString(original.AbsoluteUri), StringComparison.Ordinal));
    }

    public async Task<HttpResponseMessage> SendAsync(Uri url, GitHubRequestKind kind,
        IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        ValidateSource(url, kind);
        if (headers?.Any(x => !HeaderNames.Contains(x.Key) || x.Value.Length > 2048 || x.Value.Any(char.IsControl)) == true)
            throw new ArgumentException("GITHUB_HEADER_NOT_ALLOWED");
        var settings = _settings(); StoreNetworkSettingsService.Validate(settings);
        string template = kind == GitHubRequestKind.Api ? settings.ApiUrlTemplate : settings.AssetUrlTemplate;
        Uri target = ApplyTemplate(template, url);
        bool accelerated = target != url;
        if (accelerated) _protectedHosts.TryAdd(target.Host, 0);
        try
        {
            var response = await SendCoreAsync(target, kind, headers, settings.UseSystemProxy, accelerated ? target.Host : null, cancellationToken).ConfigureAwait(false);
            if (!(accelerated && settings.AllowDirectFallback && ((int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.RequestTimeout)))
            {
                response.Headers.Remove("X-AutumnOS-Route");
                response.Headers.TryAddWithoutValidation("X-AutumnOS-Route", accelerated ? "accelerated" : "direct");
                return response;
            }
            response.Dispose();
        }
        catch (HttpRequestException) when (accelerated && settings.AllowDirectFallback) { }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && accelerated && settings.AllowDirectFallback) { }
        var fallback = await SendCoreAsync(url, kind, headers, settings.UseSystemProxy, null, cancellationToken).ConfigureAwait(false);
        fallback.Headers.Remove("X-AutumnOS-Route");
        fallback.Headers.TryAddWithoutValidation("X-AutumnOS-Route", "direct-fallback");
        return fallback;
    }

    private async Task<HttpResponseMessage> SendCoreAsync(Uri target, GitHubRequestKind kind,
        IReadOnlyDictionary<string, string>? headers, bool systemProxy, string? acceleratorHost, CancellationToken cancellationToken)
    {
        for (int redirects = 0; redirects <= 5; redirects++)
        {
            bool knownHost = kind == GitHubRequestKind.Api ? target.Host == "api.github.com" : AssetHosts.Contains(target.Host);
            if (target.Scheme != "https" || !target.IsDefaultPort || target.UserInfo.Length != 0 || target.Fragment.Length != 0 ||
                !(knownHost || string.Equals(target.Host, acceleratorHost, StringComparison.OrdinalIgnoreCase)))
                throw new HttpRequestException("GITHUB_REDIRECT_NOT_ALLOWED");
            if (!_testHandler && string.Equals(target.Host, acceleratorHost, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var addresses = await Dns.GetHostAddressesAsync(target.Host, cancellationToken).ConfigureAwait(false);
                    if (addresses.Length == 0 || addresses.Any(a => !IsPublicAddress(a))) throw new HttpRequestException("GITHUB_PRIVATE_ADDRESS_NOT_ALLOWED");
                }
                catch (SocketException error) { throw new HttpRequestException("GITHUB_DNS_FAILED", error); }
            }
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.UserAgent.ParseAdd("AutumnOS-PublicStore/1.0");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(kind == GitHubRequestKind.Api ? "application/vnd.github+json" : "application/octet-stream"));
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            if (headers is not null) foreach (var (key, value) in headers)
            {
                request.Headers.Remove(key);
                request.Headers.TryAddWithoutValidation(key, value);
            }
            var response = await (systemProxy ? _system : _direct).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var location = response.Headers.Location;
            response.Dispose();
            if (location is null || redirects == 5) throw new HttpRequestException("GITHUB_REDIRECT_INVALID");
            target = location.IsAbsoluteUri ? location : new(target, location);
        }
        throw new HttpRequestException("GITHUB_REDIRECT_LIMIT");
    }

    public async Task<GitHubConnectionTest> TestConnectionAsync(CancellationToken cancellationToken = default)
        => await TestAsync(new("https://api.github.com/rate_limit"), GitHubRequestKind.Api, null, cancellationToken).ConfigureAwait(false);

    public async Task<GitHubConnectionTest> TestAssetConnectionAsync(Uri selectedPublicReleaseAsset, CancellationToken cancellationToken = default)
        => await TestAsync(selectedPublicReleaseAsset, GitHubRequestKind.Asset,
            new Dictionary<string, string> { ["Range"] = "bytes=0-0" }, cancellationToken).ConfigureAwait(false);

    private async Task<GitHubConnectionTest> TestAsync(Uri url, GitHubRequestKind kind,
        IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(url, kind, headers, cancellationToken).ConfigureAwait(false);
            string route = response.Headers.TryGetValues("X-AutumnOS-Route", out var values) ? values.First() : "direct";
            return new(response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "OK" : "GITHUB_HTTP_" + (int)response.StatusCode, route, (int)response.StatusCode);
        }
        catch (OperationCanceledException) { return new(false, cancellationToken.IsCancellationRequested ? "CANCELLED" : "TIMEOUT", "unknown", null); }
        catch (Exception e) when (e is HttpRequestException or ArgumentException) { return new(false, "GITHUB_CONNECTION_FAILED", "unknown", null); }
    }
    public void Dispose() { _system.Dispose(); if (!ReferenceEquals(_system, _direct)) _direct.Dispose(); }
}
