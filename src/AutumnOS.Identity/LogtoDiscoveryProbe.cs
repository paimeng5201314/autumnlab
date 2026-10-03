using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace AutumnOS.Identity;

public sealed record LogtoDiscoveryDocument(
    Uri Issuer,
    Uri AuthorizationEndpoint,
    Uri TokenEndpoint,
    Uri UserInfoEndpoint,
    Uri JwksUri,
    Uri? EndSessionEndpoint,
    Uri? RevocationEndpoint,
    IReadOnlyList<string> AllowedIdTokenSigningAlgorithms);

public sealed class LogtoDiscoveryResult
{
    internal LogtoDiscoveryResult(LogtoDiscoveryDocument? document, IEnumerable<IdentityConfigurationIssue> issues)
    {
        Document = document;
        Issues = Array.AsReadOnly(issues.ToArray());
    }

    public LogtoDiscoveryDocument? Document { get; }
    public IReadOnlyList<IdentityConfigurationIssue> Issues { get; }
    public bool IsValid => Document is not null && Issues.Count == 0;
    public string CorrelationId { get; } = Guid.NewGuid().ToString("N");
    public string SafeSummary => IsValid
        ? "公开发现文档校验通过；这不验证应用类型、回调登记或用户登录。"
        : "公开发现探测未通过；请使用关联 ID 和脱敏错误代码诊断。";
}

/// <summary>Read-only OIDC discovery. Does not launch a browser, bind a callback, send a client ID, or acquire tokens.</summary>
public static class LogtoDiscoveryProbe
{
    // Metadata may advertise additional algorithms, but only this explicit asymmetric set can reach a future OIDC adapter.
    private static readonly string[] AllowedSigningAlgorithms =
        ["RS256", "RS384", "RS512", "PS256", "PS384", "PS512", "ES256", "ES384", "ES512"];

    public static async Task<LogtoDiscoveryResult> ProbeAsync(LogtoPublicOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        using HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseDefaultCredentials = false,
            Credentials = null,
            AutomaticDecompression = DecompressionMethods.None
        };
        using HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using HttpRequestMessage request = new(HttpMethod.Get, options.MetadataAddress);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is >= 300 and <= 399)
                return Failure("AUTH_METADATA_UNAVAILABLE", "Http", "发现地址返回重定向；未跟随到任何新地址。");
            if (!response.IsSuccessStatusCode)
                return Failure("AUTH_METADATA_UNAVAILABLE", "Http", $"发现地址返回非成功 HTTP 状态 {(int)response.StatusCode}。");
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
                return Failure("AUTH_METADATA_UNAVAILABLE", "ContentType", "发现响应不是 application/json。");
            if (response.Content.Headers.ContentLength is > IdentityJson.MetadataLimitBytes)
                return Failure("AUTH_METADATA_UNAVAILABLE", "Size", "发现响应超过大小限制。");
            await using Stream input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using MemoryStream output = new();
            byte[] buffer = new byte[4096];
            while (true)
            {
                int count = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (count == 0) break;
                if (output.Length + count > IdentityJson.MetadataLimitBytes)
                    return Failure("AUTH_METADATA_UNAVAILABLE", "Size", "发现响应超过大小限制。");
                output.Write(buffer, 0, count);
            }
            string json = new UTF8Encoding(false, true).GetString(output.GetBuffer(), 0, checked((int)output.Length));
            return ValidateJson(options, json);
        }
        catch (OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? Failure("USER_CANCELLED", "Transport", "公开发现探测已取消。")
                : Failure("AUTH_TIMEOUT", "Transport", "公开发现探测超时。");
        }
        catch (HttpRequestException error)
        {
            return error.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => Failure("AUTH_METADATA_UNAVAILABLE", "Transport.Dns", "发现主机 DNS 解析失败。"),
                HttpRequestError.SecureConnectionError => Failure("AUTH_METADATA_UNAVAILABLE", "Transport.Tls", "发现请求 TLS 连接验证失败。"),
                HttpRequestError.ConnectionError => Failure("AUTH_METADATA_UNAVAILABLE", "Transport.Connection", "无法建立发现请求网络连接。"),
                HttpRequestError.ProxyTunnelError => Failure("AUTH_METADATA_UNAVAILABLE", "Transport.Proxy", "系统网络代理无法建立发现请求隧道。"),
                _ => Failure("AUTH_METADATA_UNAVAILABLE", "Transport.Http", "发现请求发生 HTTP 传输错误。")
            };
        }
        catch (Exception error) when (error is IOException or DecoderFallbackException)
        {
            // Do not return exception text: proxy URLs, certificate details, or remote content may be sensitive.
            return Failure("AUTH_METADATA_UNAVAILABLE", "Transport", "无法安全读取公开发现文档；请检查网络和 TLS。");
        }
    }

    public static LogtoDiscoveryResult ValidateJson(LogtoPublicOptions options, string json)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            using JsonDocument parsed = IdentityJson.Parse(json, IdentityJson.MetadataLimitBytes);
            JsonElement root = parsed.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return Failure("AUTH_METADATA_UNAVAILABLE", "Json", "发现文档必须是 JSON 对象。");
            List<IdentityConfigurationIssue> issues = [];
            void Add(string field, string message) => issues.Add(new("AUTH_METADATA_UNAVAILABLE", field, message));

            string? issuerText = IdentityJson.GetString(root, "issuer");
            if (issuerText != options.Authority.AbsoluteUri)
                Add("issuer", "发现 issuer 与预期 Authority 不完全一致。");

            Uri? Endpoint(string key, bool required = true)
            {
                if (!root.TryGetProperty(key, out _) && !required) return null;
                if (!IdentityJson.IsHttpsUri(IdentityJson.GetString(root, key), out Uri? value) ||
                    value is null || !IdentityJson.SameOrigin(options.Authority, value))
                {
                    Add(key, "发现端点必须使用 HTTPS，并与预设 Authority 同可信源。");
                    return null;
                }
                return value;
            }

            Uri? authorization = Endpoint("authorization_endpoint");
            Uri? token = Endpoint("token_endpoint");
            Uri? userInfo = Endpoint("userinfo_endpoint");
            Uri? jwks = Endpoint("jwks_uri");
            Uri? endSession = Endpoint("end_session_endpoint", false);
            Uri? revocation = Endpoint("revocation_endpoint", false);
            string[] knownEndpoints = ["authorization_endpoint", "token_endpoint", "userinfo_endpoint",
                "end_session_endpoint", "revocation_endpoint"];
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.Name.EndsWith("_endpoint", StringComparison.Ordinal) && !knownEndpoints.Contains(property.Name))
                {
                    if (!IdentityJson.IsHttpsUri(IdentityJson.GetString(root, property.Name), out Uri? extra) ||
                        extra is null || !IdentityJson.SameOrigin(options.Authority, extra))
                        Add("additional_endpoint", "附加发现端点超出可信 HTTPS 源；已拒绝该发现文档。");
                }
            }

            void RequireValue(string key, string expected, bool optional = false)
            {
                if (optional && !root.TryGetProperty(key, out _)) return;
                string[]? values = IdentityJson.GetStrings(root, key);
                if (values is null || !values.Contains(expected, StringComparer.Ordinal))
                    Add(key, "发现文档未声明本客户端所需的协议能力。");
            }
            RequireValue("response_types_supported", "code");
            RequireValue("code_challenge_methods_supported", "S256");
            RequireValue("token_endpoint_auth_methods_supported", "none");
            string[]? advertisedAlgorithms = IdentityJson.GetStrings(root, "id_token_signing_alg_values_supported");
            string[] acceptedAlgorithms = advertisedAlgorithms?
                .Where(algorithm => AllowedSigningAlgorithms.Contains(algorithm, StringComparer.Ordinal)).ToArray() ?? [];
            if (acceptedAlgorithms.Length == 0)
                Add("id_token_signing_alg_values_supported", "发现文档未声明允许的非对称签名算法；拒绝无签名和共享密钥算法。");
            RequireValue("grant_types_supported", "authorization_code", optional: true);
            RequireValue("response_modes_supported", "query", optional: true);
            // Discovery is a read-only baseline check, not the user's choice to retain a session.
            // A deployment without offline_access can still support the default sign-in flow.
            // A future OIDC adapter must request the additional scope only after explicit consent,
            // and handle the provider's actual grant without promising a refresh token.
            foreach (string scope in options.Scopes)
                RequireValue("scopes_supported", scope, optional: true);

            return issues.Count == 0
                ? new(new(options.Authority, authorization!, token!, userInfo!, jwks!, endSession, revocation,
                    Array.AsReadOnly(acceptedAlgorithms)), [])
                : new(null, issues);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            return Failure("AUTH_METADATA_UNAVAILABLE", "Json", "发现文档不是有效的严格 JSON，或包含重复字段。");
        }
    }

    private static LogtoDiscoveryResult Failure(string code, string field, string message) =>
        new(null, [new(code, field, message)]);
}
