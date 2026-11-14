using System.Net;
using System.Text;
using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

// Deterministic resolver/HTTP substitutes. These never contact DNS, sockets or
// the Internet and do not certify a live native application/permission boundary.
internal static class NetworkTransportTests
{
    private const string Url = "https://api.example.com/";
    private static readonly string[] Domains = ["api.example.com", "assets.example.com"];
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return Case("HEAD_and_304_representation_length_is_not_response_body", RepresentationLength);
        yield return Case("invalid_timeout_json_types_are_INVALID_PARAMS", TimeoutKinds);
        yield return Case("exact_HTTPS_domain_URL_admission_rejects_ambiguous_targets", UrlAdmission);
        yield return Case("private_mapped_and_mixed_DNS_results_never_start_HTTP", DnsAdmission);
        yield return Case("canonical_binary_request_body_roundtrips_and_bad_base64_is_rejected", RequestBodies);
        yield return Case("only_exact_lowercase_safe_headers_and_application_Bearer_are_accepted", RequestHeaders);
        yield return Case("GET_redirects_strip_Bearer_even_same_origin_and_bound_hops", RedirectGet);
        yield return Case("undeclared_or_private_redirect_targets_are_blocked_before_next_DNS", RedirectBlocked);
        yield return Case("POST_redirect_does_not_replay_body_or_credentials", RedirectPost);
        yield return Case("response_body_and_UTF8_header_budget_are_bounded_without_truncation", ResponseBudgets);
        yield return Case("production_handler_has_no_ambient_proxy_cookie_credentials_or_response_drain", HandlerDefaults);
    }
    private static (string, Action) Case(string name, Func<Task> run) =>
        ("network.transport.synthetic_" + name, () => run().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
    private static RuntimeNetworkRequest Request(string method = "GET", Dictionary<string, string>? headers = null, string? body = null) => new(Url, method, headers ?? [], body, 10000);
    private static RuntimeNetworkService Service(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply, IPAddress[]? ips = null) =>
        new((_, _) => Task.FromResult(ips ?? [IPAddress.Parse("93.184.216.34")]), (_, _, _, _) => new Handler(reply), () => true);
    private static Task<RuntimeNetworkResponse> Send(RuntimeNetworkService service, RuntimeNetworkRequest request) => service.RequestAsync(request, Domains, () => new Lease(), default);
    private static HttpResponseMessage Response(HttpStatusCode status = HttpStatusCode.OK, byte[]? bytes = null, long? length = null)
    {
        var response = new HttpResponseMessage(status) { Content = new ByteArrayContent(bytes ?? []) };
        if (length.HasValue) response.Content.Headers.ContentLength = length;
        return response;
    }
    private static async Task RepresentationLength()
    {
        foreach (var item in new[] { ("HEAD", HttpStatusCode.OK), ("GET", HttpStatusCode.NotModified) })
        {
            var result = await Send(Service(_ => Task.FromResult(Response(item.Item2, [], 65536))), Request(item.Item1));
            Check(result.Status == (int)item.Item2 && result.Body == "");
        }
        await Reject(() => Send(Service(_ => Task.FromResult(Response(bytes: [], length: 65536))), Request()), "NETWORK_RESPONSE_TOO_LARGE");
        await Reject(() => Send(Service(_ => Task.FromResult(Response(bytes: new byte[16385], length: 0))), Request("HEAD")), "NETWORK_RESPONSE_TOO_LARGE");
    }
    private static Task TimeoutKinds()
    {
        foreach (string value in new[] { "\"1000\"", "null", "true", "[]", "{}", "1.5", "2147483648" })
        {
            using var json = JsonDocument.Parse("{\"url\":\"" + Url + "\",\"timeoutMs\":" + value + "}");
            try { _ = RuntimeNetworkService.Parse(json.RootElement); } catch (RuntimeCapabilityException e) when (e.Code == "INVALID_PARAMS") { continue; }
            throw new InvalidOperationException("NONINTEGER_NETWORK_TIMEOUT_ACCEPTED");
        }
        Check(RuntimeNetworkService.MinimumTimeoutMs == 1); return Task.CompletedTask;
    }
    private static Task UrlAdmission()
    {
        Check(RuntimeNetworkService.ValidateUrl(Url + "v1?q=a", Domains).IdnHost == "api.example.com");
        foreach (string url in new[] { "http://api.example.com/", "HTTPS://api.example.com/", "https://api.example.com:444/", "https://api.example.com./", "https://api.example.com/#x",
            "https://user@api.example.com/", "https://127.0.0.1/", "https://[::1]/", "https://other.example.com/", "https://api.example.com\\evil", " " + Url, Url + "a b" })
        {
            try { _ = RuntimeNetworkService.ValidateUrl(url, Domains); } catch (RuntimeCapabilityException) { continue; }
            throw new InvalidOperationException("AMBIGUOUS_NETWORK_URL_ACCEPTED");
        }
        return Task.CompletedTask;
    }
    private static async Task DnsAdmission()
    {
        foreach (string text in new[] { "0.0.0.0", "10.0.0.1", "100.64.0.1", "127.0.0.1", "169.254.169.254", "172.16.0.1", "192.168.1.1", "192.0.2.1", "198.18.0.1", "198.51.100.1", "203.0.113.1", "224.0.0.1", "::1", "::ffff:8.8.8.8", "64:ff9b::a00:1", "fc00::1", "fe80::1", "2001:db8::1", "2002::1" })
        {
            int calls = 0;
            await Reject(() => Send(Service(_ => { calls++; return Task.FromResult(Response()); }, [IPAddress.Parse("8.8.8.8"), IPAddress.Parse(text)]), Request()), "NETWORK_DESTINATION_BLOCKED");
            Check(calls == 0);
        }
        Check(RuntimeNetworkAddressPolicy.IsPublic(IPAddress.Parse("8.8.8.8")) && RuntimeNetworkAddressPolicy.IsPublic(IPAddress.Parse("2606:4700:4700::1111")));
    }
    private static async Task RequestBodies()
    {
        foreach (byte[] expected in new[] { Array.Empty<byte>(), new byte[] { 0, 1, 255 }, new byte[16384] })
        {
            var result = await Send(Service(async request => { Check((await request.Content!.ReadAsByteArrayAsync()).SequenceEqual(expected)); return Response(); }),
                Request("POST", new() { ["content-type"] = "application/octet-stream" }, Convert.ToBase64String(expected)));
            Check(result.Status == 200);
        }
        foreach (string bad in new[] { "AB==", "AAB=", "AA==\n", "====", Convert.ToBase64String(new byte[16385]) })
            await Reject(() => Send(Service(_ => throw new InvalidOperationException("INVALID_BODY_REACHED_HTTP")), Request("POST", body: bad)), "INVALID_PARAMS", "NETWORK_BODY_TOO_LARGE");
        await Reject(() => Send(Service(_ => throw new InvalidOperationException()), Request(body: "")), "NETWORK_BODY_TOO_LARGE");
    }
    private static async Task RequestHeaders()
    {
        foreach (var pair in new[] { ("cookie", "synthetic"), ("host", "other.example.com"), ("proxy-authorization", "Basic synthetic"), ("user-agent", "other"), ("Accept", "text/plain"),
            ("accept", "a\r\nb"), ("accept", "中文"), ("authorization", "Basic synthetic"), ("authorization", "Bearer ab=c"), ("authorization", "Bearer ") })
            await Reject(() => Send(Service(_ => throw new InvalidOperationException("INVALID_HEADER_REACHED_HTTP")), Request(headers: new() { [pair.Item1] = pair.Item2 })), "NETWORK_HEADERS_INVALID");
        await Reject(() => Send(Service(_ => throw new InvalidOperationException()), Request(headers: new() { ["content-type"] = "application/json" })), "NETWORK_HEADERS_INVALID");
        await Send(Service(request => { Check(request.Headers.Authorization?.ToString() == "Bearer synthetic=="); return Task.FromResult(Response()); }), Request(headers: new() { ["authorization"] = "Bearer synthetic==" }));
    }
    private static async Task RedirectGet()
    {
        foreach (bool crossOrigin in new[] { false, true })
        {
            int calls = 0;
            await Send(Service(request =>
            {
                calls++;
                Check(request.Headers.Contains("Authorization") == (calls == 1));
                var response = Response();
                if (calls == 1) { response.StatusCode = HttpStatusCode.Redirect; response.Headers.Location = new Uri(crossOrigin ? "https://assets.example.com/next" : "/next", UriKind.RelativeOrAbsolute); }
                return Task.FromResult(response);
            }), Request(headers: new() { ["authorization"] = "Bearer synthetic" }));
            Check(calls == 2);
        }
        int hops = 0;
        await Reject(() => Send(Service(_ => { hops++; var r = Response(HttpStatusCode.Redirect); r.Headers.Location = new Uri("/next", UriKind.Relative); return Task.FromResult(r); }), Request()), "NETWORK_REDIRECT_LIMIT");
        Check(hops == 4);
    }
    private static async Task RedirectBlocked()
    {
        foreach (string target in new[] { "https://other.example.net/", "https://127.0.0.1/", "http://api.example.com/", "https://api.example.com/#fragment" })
        {
            int calls = 0;
            await Reject(() => Send(Service(_ => { calls++; var r = Response(HttpStatusCode.Redirect); r.Headers.Location = new Uri(target); return Task.FromResult(r); }), Request()), "NETWORK_REDIRECT_BLOCKED");
            Check(calls == 1);
        }
    }
    private static async Task RedirectPost()
    {
        int calls = 0;
        var result = await Send(Service(async request => { calls++; Check(request.Method.Method == "POST" && (await request.Content!.ReadAsByteArrayAsync()).SequenceEqual(new byte[] { 1 }));
            var r = Response(HttpStatusCode.TemporaryRedirect); r.Headers.Location = new Uri("https://assets.example.com/next"); return r; }), Request("POST", body: "AQ=="));
        Check(calls == 1 && result.Status == 307);
    }
    private static async Task ResponseBudgets()
    {
        byte[] maximum = new byte[16384]; maximum[^1] = 255;
        var value = await Send(Service(_ => Task.FromResult(Response(bytes: maximum))), Request());
        Check(Convert.FromBase64String(value.Body).SequenceEqual(maximum));
        await Reject(() => Send(Service(_ => Task.FromResult(Response(bytes: new byte[16385], length: 0))), Request()), "NETWORK_RESPONSE_TOO_LARGE");
        await Reject(() => Send(Service(_ => { var r = Response(); r.Headers.TryAddWithoutValidation("ETag", "中".PadRight(1500, '中')); return Task.FromResult(r); }), Request()), "NETWORK_RESPONSE_INVALID");
        await Reject(() => Send(Service(_ => { var r = Response(); r.Headers.TryAddWithoutValidation("ETag", new string('a', 2100));
            r.Headers.TryAddWithoutValidation("Cache-Control", new string('b', 2100)); return Task.FromResult(r); }), Request()), "NETWORK_RESPONSE_TOO_LARGE");
        var filtered = await Send(Service(_ => { var r = Response(); r.Headers.TryAddWithoutValidation("Set-Cookie", "synthetic=test"); r.Headers.TryAddWithoutValidation("Authorization", "Bearer synthetic"); return Task.FromResult(r); }), Request());
        Check(!filtered.Headers.ContainsKey("set-cookie") && !filtered.Headers.ContainsKey("authorization"));
    }
    private static Task HandlerDefaults()
    {
        using var handler = RuntimeNetworkService.CreatePinnedHandler(new Uri(Url), [IPAddress.Parse("8.8.8.8")], () => new Lease(), default);
        Check(!handler.AllowAutoRedirect && !handler.UseProxy && !handler.UseCookies && handler.Credentials is null && handler.DefaultProxyCredentials is null && !handler.PreAuthenticate &&
            handler.AutomaticDecompression == DecompressionMethods.None && handler.MaxResponseDrainSize == 0 && handler.SslOptions.RemoteCertificateValidationCallback is null);
        var chain = handler.SslOptions.CertificateChainPolicy;
        Check(chain is not null && chain.DisableCertificateDownloads &&
            chain.RevocationMode == System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck &&
            chain.TrustMode == System.Security.Cryptography.X509Certificates.X509ChainTrustMode.System &&
            chain.VerificationFlags == System.Security.Cryptography.X509Certificates.X509VerificationFlags.NoFlag &&
            chain.ApplicationPolicy.Cast<System.Security.Cryptography.Oid>().Select(p => p.Value).SequenceEqual(new[] { "1.3.6.1.5.5.7.3.1" }));
        return Task.CompletedTask;
    }
    private static async Task Reject(Func<Task<RuntimeNetworkResponse>> action, params string[] codes)
    { try { _ = await action(); } catch (RuntimeCapabilityException error) when (codes.Contains(error.Code, StringComparer.Ordinal)) { return; } throw new InvalidOperationException("NETWORK_REJECTION_NOT_OBSERVED"); }
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("NETWORK_TRANSPORT_ASSERTION"); }
    private sealed class Lease : IDisposable { public void Dispose() { } }
    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => reply(request); }
}
