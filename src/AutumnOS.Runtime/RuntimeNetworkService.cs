using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Net.NetworkInformation;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutumnOS.Contracts;

namespace AutumnOS.Runtime;

internal sealed record RuntimeNetworkRequest(string Url, string Method, IReadOnlyDictionary<string, string> Headers, string? Body, int TimeoutMs);
internal sealed record RuntimeNetworkResponse([property: JsonPropertyName("status")] int Status,
    [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string> Headers,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("encoding")] string Encoding = "base64");

/// <summary>Host-owned, bounded anonymous/app-Bearer HTTPS broker. It never uses the
/// browser, host identity tokens, CookieContainer, Windows authentication or a proxy.</summary>
public sealed class RuntimeNetworkService
{
    public const int MaximumBodyBytes = 16 * 1024;
    public const int MaximumResponseHeaderBytes = 4096;
    public const int DefaultTimeoutMs = 10000;
    public const int MaximumTimeoutMs = 20000;
    public const int MinimumTimeoutMs = 1;
    public const int MaximumRedirects = 3;
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal) { "GET", "HEAD", "POST", "PUT", "PATCH", "DELETE" };
    private static readonly HashSet<string> RequestHeaders = new(StringComparer.Ordinal) { "accept", "content-type", "authorization", "if-none-match", "if-modified-since" };
    private static readonly HashSet<string> ResponseHeaders = new(StringComparer.Ordinal) { "content-type", "content-language", "content-length", "content-encoding", "etag", "last-modified", "cache-control", "expires", "retry-after", "location" };
    private readonly Func<string, CancellationToken, Task<IPAddress[]>> resolve;
    private readonly Func<Uri, IPAddress[], Func<IDisposable>, CancellationToken, HttpMessageHandler> createHandler;
    private readonly Func<bool> linkAvailable;

    public RuntimeNetworkService() : this(Dns.GetHostAddressesAsync, static (uri, ips, guard, token) => CreatePinnedHandler(uri, ips, guard, token)) { }
    // Deterministic test hooks cannot be selected through SDK messages or public composition.
    internal RuntimeNetworkService(Func<string, CancellationToken, Task<IPAddress[]>> resolve,
        Func<Uri, IPAddress[], Func<IDisposable>, CancellationToken, HttpMessageHandler> createHandler, Func<bool>? linkAvailable = null)
    { this.resolve = resolve; this.createHandler = createHandler; this.linkAvailable = linkAvailable ?? NetworkInterface.GetIsNetworkAvailable; }

    internal bool LinkAvailable
    {
        get { try { return linkAvailable(); } catch (NetworkInformationException) { return false; } }
    }

    internal static RuntimeNetworkRequest Parse(JsonElement parameters)
    {
        if (parameters.ValueKind != JsonValueKind.Object || RuntimeSession.HasDuplicateProperties(parameters)) throw Error("INVALID_PARAMS");
        foreach (var property in parameters.EnumerateObject())
            if (property.Name is not ("url" or "method" or "headers" or "body" or "timeoutMs")) throw Error("INVALID_PARAMS");
        if (!parameters.TryGetProperty("url", out var url) || url.ValueKind != JsonValueKind.String) throw Error("INVALID_PARAMS");
        string method = "GET";
        if (parameters.TryGetProperty("method", out var m)) { if (m.ValueKind != JsonValueKind.String) throw Error("INVALID_PARAMS"); method = m.GetString()!; }
        string? body = null;
        if (parameters.TryGetProperty("body", out var b)) { if (b.ValueKind != JsonValueKind.String) throw Error("INVALID_PARAMS"); body = b.GetString(); }
        int timeout = DefaultTimeoutMs;
        if (parameters.TryGetProperty("timeoutMs", out var t) && (t.ValueKind != JsonValueKind.Number || !t.TryGetInt32(out timeout))) throw Error("INVALID_PARAMS");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (parameters.TryGetProperty("headers", out var h))
        {
            if (h.ValueKind != JsonValueKind.Object) throw Error("NETWORK_HEADERS_INVALID");
            foreach (var header in h.EnumerateObject())
                if (header.Value.ValueKind != JsonValueKind.String || !headers.TryAdd(header.Name, header.Value.GetString()!)) throw Error("NETWORK_HEADERS_INVALID");
        }
        return new(url.GetString()!, method, headers, body, timeout);
    }

    internal async Task<RuntimeNetworkResponse> RequestAsync(RuntimeNetworkRequest request, IReadOnlyCollection<string> declaredDomains,
        Func<IDisposable> enterAuthority, CancellationToken cancellationToken)
    {
        string[] domains = NetworkDomainPolicy.CopyValidated(declaredDomains);
        Uri target = ValidateUrl(request.Url, domains);
        if (!Methods.Contains(request.Method) || request.TimeoutMs is < MinimumTimeoutMs or > MaximumTimeoutMs) throw Error("INVALID_PARAMS");
        var headers = ValidateHeaders(request.Headers);
        byte[]? body = null;
        if (request.Body is not null)
        {
            if (request.Method is "GET" or "HEAD" || request.Body.Length > ((MaximumBodyBytes + 2) / 3) * 4) throw Error("NETWORK_BODY_TOO_LARGE");
            try { body = Convert.FromBase64String(request.Body); } catch (FormatException) { throw Error("INVALID_PARAMS"); }
            if (body.Length > MaximumBodyBytes) throw Error("NETWORK_BODY_TOO_LARGE");
            if (Convert.ToBase64String(body) != request.Body) throw Error("INVALID_PARAMS");
        }
        if (headers.ContainsKey("content-type") && body is null) throw Error("NETWORK_HEADERS_INVALID");
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(request.TimeoutMs));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            for (int redirects = 0; ; redirects++)
            {
                Task<IPAddress[]> resolution;
                using (enterAuthority()) { linked.Token.ThrowIfCancellationRequested(); resolution = resolve(target.IdnHost, linked.Token); }
                IPAddress[] addresses = await resolution.WaitAsync(linked.Token).ConfigureAwait(false);
                using (enterAuthority()) linked.Token.ThrowIfCancellationRequested();
                if (addresses is null || addresses.Length is < 1 or > 32 || addresses.Any(ip => ip is null || !RuntimeNetworkAddressPolicy.IsPublic(ip))) throw Error("NETWORK_DESTINATION_BLOCKED");
                addresses = addresses.Distinct().ToArray();
                using var handler = createHandler(target, addresses, enterAuthority, linked.Token);
                using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
                using var message = new HttpRequestMessage(new HttpMethod(request.Method), target)
                { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
                message.Headers.UserAgent.ParseAdd("AutumnOS-Network/0.4");
                if (body is not null) message.Content = new ByteArrayContent(body);
                foreach (var header in headers)
                {
                    if (header.Key == "content-type") message.Content!.Headers.ContentType = MediaTypeHeaderValue.Parse(header.Value);
                    else if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value)) throw Error("NETWORK_HEADERS_INVALID");
                }
                Task<HttpResponseMessage> sending;
                using (enterAuthority()) { linked.Token.ThrowIfCancellationRequested(); sending = client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, linked.Token); }
                using var response = await sending.WaitAsync(linked.Token).ConfigureAwait(false);
                using (enterAuthority()) linked.Token.ThrowIfCancellationRequested();
                int status = (int)response.StatusCode;
                if (status is < 100 or > 599) throw Error("NETWORK_RESPONSE_INVALID");
                bool redirect = status is 301 or 302 or 303 or 307 or 308;
                Uri? location = null;
                if (redirect && response.Headers.Location is { } supplied)
                {
                    try { location = ValidateUrl(new Uri(target, supplied).AbsoluteUri, domains); }
                    catch (Exception error) when (error is RuntimeCapabilityException or UriFormatException) { if (request.Method is "GET" or "HEAD") throw Error("NETWORK_REDIRECT_BLOCKED"); }
                }
                if (redirect && location is not null && request.Method is "GET" or "HEAD")
                {
                    if (redirects >= MaximumRedirects) throw Error("NETWORK_REDIRECT_LIMIT");
                    headers.Remove("authorization"); // Even same-origin redirects do not inherit app credentials.
                    target = location;
                    continue;
                }
                var returnedHeaders = ReadHeaders(response, location);
                // HEAD/304 Content-Length describes a selected GET representation,
                // rather than octets transmitted in this response (RFC 9110 8.6).
                if (request.Method != "HEAD" && status != 304 && response.Content.Headers.ContentLength is { } length && length > MaximumBodyBytes) throw Error("NETWORK_RESPONSE_TOO_LARGE");
                using var output = new MemoryStream();
                await using var input = await response.Content.ReadAsStreamAsync(linked.Token).WaitAsync(linked.Token).ConfigureAwait(false);
                var buffer = new byte[4096];
                while (true)
                {
                    ValueTask<int> reading;
                    int remaining = (int)Math.Min(buffer.Length, MaximumBodyBytes - output.Length + 1);
                    using (enterAuthority()) { linked.Token.ThrowIfCancellationRequested(); reading = input.ReadAsync(buffer.AsMemory(0, remaining), linked.Token); }
                    int read = await reading.AsTask().WaitAsync(linked.Token).ConfigureAwait(false);
                    using (enterAuthority()) linked.Token.ThrowIfCancellationRequested();
                    if (read == 0) break;
                    if (output.Length + read > MaximumBodyBytes) throw Error("NETWORK_RESPONSE_TOO_LARGE");
                    output.Write(buffer, 0, read);
                }
                var result = new RuntimeNetworkResponse(status, returnedHeaders, Convert.ToBase64String(output.ToArray()));
                if (JsonSerializer.SerializeToUtf8Bytes(result).Length > RuntimeSession.MaximumMessageBytes - 512) throw Error("NETWORK_RESPONSE_TOO_LARGE");
                return result;
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested) { throw Error("NETWORK_TIMEOUT"); }
        catch (HttpRequestException error)
        {
            for (Exception? inner = error.InnerException; inner is not null; inner = inner.InnerException)
                if (inner is RuntimeCapabilityException authority) throw Error(authority.Code);
            throw Error("NETWORK_UNAVAILABLE");
        }
        catch (Exception error) when (error is IOException or SocketException or AuthenticationException) { throw Error("NETWORK_UNAVAILABLE"); }
    }

    internal static Uri ValidateUrl(string url, IReadOnlyCollection<string> domains)
    {
        if (url is null || url.Length is < 1 or > 2048 || !url.StartsWith("https://", StringComparison.Ordinal) ||
            url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)) || url.Contains('\\') ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 || uri.HostNameType != UriHostNameType.Dns || uri.Host.Any(c => c > 127) ||
            !NetworkDomainPolicy.IsValidDomain(uri.IdnHost)) throw Error("NETWORK_URL_NOT_ALLOWED");
        if (!domains.Contains(uri.IdnHost, StringComparer.Ordinal)) throw Error("NETWORK_DOMAIN_NOT_ALLOWED");
        return uri;
    }

    private static Dictionary<string, string> ValidateHeaders(IReadOnlyDictionary<string, string> supplied)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (supplied is null || supplied.Count > RequestHeaders.Count) throw Error("NETWORK_HEADERS_INVALID");
        int bytes = 0;
        foreach (var pair in supplied)
        {
            string key = pair.Key, value = pair.Value;
            if (!RequestHeaders.Contains(key) || value is null || value.Length > 4096 || value.Any(c => c < 32 || c > 126) || !result.TryAdd(key, value)) throw Error("NETWORK_HEADERS_INVALID");
            bytes = checked(bytes + key.Length + value.Length);
            if (bytes > 8192) throw Error("NETWORK_HEADERS_INVALID");
            if (key == "authorization" && !ValidBearer(value)) throw Error("NETWORK_HEADERS_INVALID");
            if (key == "content-type" && !MediaTypeHeaderValue.TryParse(value, out _)) throw Error("NETWORK_HEADERS_INVALID");
        }
        return result;
    }

    private static bool ValidBearer(string value)
    {
        if (!value.StartsWith("Bearer ", StringComparison.Ordinal) || value.Length <= 7) return false;
        ReadOnlySpan<char> token = value.AsSpan(7);
        int padding = token.IndexOf('=');
        if (padding == 0) return false;
        if (padding >= 0 && token[padding..].ContainsAnyExcept('=')) return false;
        if (padding >= 0) token = token[..padding];
        foreach (char c in token) if (!(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/')) return false;
        return !token.IsEmpty;
    }

    private static IReadOnlyDictionary<string, string> ReadHeaders(HttpResponseMessage response, Uri? location)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        int bytes = 0;
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            string key = header.Key.ToLowerInvariant();
            if (!ResponseHeaders.Contains(key) || key == "location") continue;
            string value = string.Join(", ", header.Value);
            if (value.Length > 4096 || value.Any(c => c < 32 || c > 126)) throw Error("NETWORK_RESPONSE_INVALID");
            bytes += Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(value);
            if (bytes > MaximumResponseHeaderBytes || !result.TryAdd(key, value)) throw Error("NETWORK_RESPONSE_TOO_LARGE");
        }
        if (location is not null)
        {
            bytes += "location".Length + Encoding.UTF8.GetByteCount(location.AbsoluteUri);
            if (bytes > MaximumResponseHeaderBytes) throw Error("NETWORK_RESPONSE_TOO_LARGE");
            result.Add("location", location.AbsoluteUri);
        }
        return result;
    }

    internal static SocketsHttpHandler CreatePinnedHandler(Uri uri, IPAddress[] addresses, Func<IDisposable> enterAuthority,
        CancellationToken lifetime, Func<IPAddress, CancellationToken, ValueTask<Stream>>? testConnector = null)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None, UseCookies = false, UseProxy = false,
            Credentials = null, DefaultProxyCredentials = null, PreAuthenticate = false, MaxConnectionsPerServer = 1,
            MaxResponseHeadersLength = 8, ConnectTimeout = TimeSpan.FromSeconds(10), PooledConnectionLifetime = TimeSpan.Zero,
            MaxResponseDrainSize = 0, ResponseDrainTimeout = TimeSpan.Zero,
            SslOptions = new()
            {
                // Certificate-supplied AIA/issuer URLs must not create a second
                // network path outside the declared-domain/DNS/IP broker gate.
                // Retain HttpClient's NoCheck revocation default, system roots,
                // normal host-name validation and server-authentication EKU.
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                CertificateChainPolicy = new()
                {
                    DisableCertificateDownloads = true,
                    RevocationMode = X509RevocationMode.NoCheck,
                    RevocationFlag = X509RevocationFlag.ExcludeRoot,
                    VerificationFlags = X509VerificationFlags.NoFlag,
                    TrustMode = X509ChainTrustMode.System,
                    ApplicationPolicy = { new Oid("1.3.6.1.5.5.7.3.1") }
                }
            },
            ConnectCallback = async (context, token) =>
            {
                if (context.DnsEndPoint.Host != uri.IdnHost || context.DnsEndPoint.Port != 443) throw Error("NETWORK_DESTINATION_BLOCKED");
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime);
                Exception? last = null;
                foreach (var address in addresses)
                {
                    if (!RuntimeNetworkAddressPolicy.IsPublic(address)) throw Error("NETWORK_DESTINATION_BLOCKED");
                    Socket? socket = null;
                    try
                    {
                        if (testConnector is not null)
                        {
                            ValueTask<Stream> test;
                            using (enterAuthority()) { linked.Token.ThrowIfCancellationRequested(); test = testConnector(address, linked.Token); }
                            return new RuntimeNetworkAuthorityStream(await test.ConfigureAwait(false), enterAuthority, lifetime);
                        }
                        socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        ValueTask connecting;
                        using (enterAuthority()) { linked.Token.ThrowIfCancellationRequested(); connecting = socket.ConnectAsync(new IPEndPoint(address, 443), linked.Token); }
                        await connecting.ConfigureAwait(false);
                        using (enterAuthority())
                        {
                            linked.Token.ThrowIfCancellationRequested();
                            if (socket.RemoteEndPoint is not IPEndPoint remote || !remote.Address.Equals(address) || remote.Port != 443) throw Error("NETWORK_DESTINATION_BLOCKED");
                        }
                        return new RuntimeNetworkAuthorityStream(new NetworkStream(socket, ownsSocket: true), enterAuthority, lifetime);
                    }
                    catch (Exception error) when (error is SocketException or IOException) { socket?.Dispose(); last = error; }
                    catch { socket?.Dispose(); throw; }
                }
                throw new HttpRequestException("NETWORK_UNAVAILABLE", last);
            }
        };
    }

    private static RuntimeCapabilityException Error(string code) => new(code);
}
