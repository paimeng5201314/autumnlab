using System.Text;
using System.Text.Json;

namespace AutumnOS.Identity;

public static class LogtoConfigurationLoader
{
    public static LogtoConfigurationResult Load(string configurationPath)
    {
        try { return ValidateJson(IdentityJson.ReadFile(configurationPath)); }
        catch (FileNotFoundException) { return Failure("AUTH_NOT_CONFIGURED", "File", "公开配置文件不存在。"); }
        catch (DirectoryNotFoundException) { return Failure("AUTH_NOT_CONFIGURED", "File", "公开配置目录不存在。"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or JsonException or DecoderFallbackException)
        {
            return Failure("AUTH_CONFIGURATION_ERROR", "File", "公开配置无法读取或超过大小限制。");
        }
    }

    public static LogtoConfigurationResult ValidateJson(string json)
    {
        try
        {
            using JsonDocument document = IdentityJson.Parse(json, IdentityJson.ConfigurationLimitBytes);
            JsonElement root = document.RootElement;
            if (!IdentityJson.HasOnly(root, "SchemaVersion", "Logto") ||
                !root.TryGetProperty("SchemaVersion", out JsonElement version) ||
                !version.TryGetInt32(out int schema) || schema != 1 ||
                !root.TryGetProperty("Logto", out JsonElement logto) ||
                !IdentityJson.HasOnly(logto, "Endpoint", "Authority", "MetadataAddress", "ClientId", "RedirectUri",
                    "PostLogoutRedirectUri", "Scopes", "RememberSignInAdditionalScopes", "ResponseType", "UsePkce",
                    "PkceMethod", "ClientAuthenticationMethod"))
                return Failure("AUTH_CONFIGURATION_ERROR", "Schema", "配置结构、版本或字段不符合公开配置合同。");

            List<IdentityConfigurationIssue> issues = [];
            void Add(string field, string message) => issues.Add(new("AUTH_CONFIGURATION_ERROR", field, message));

            IdentityJson.IsHttpsUri(IdentityJson.GetString(logto, "Endpoint"), out Uri? endpoint);
            IdentityJson.IsHttpsUri(IdentityJson.GetString(logto, "Authority"), out Uri? authority);
            IdentityJson.IsHttpsUri(IdentityJson.GetString(logto, "MetadataAddress"), out Uri? metadata);
            if (endpoint is null || endpoint.AbsolutePath != "/")
                Add("Endpoint", "Endpoint 必须是规范 HTTPS 源地址，路径为根目录。");
            if (authority is null || endpoint is null || !IdentityJson.SameOrigin(endpoint, authority) ||
                authority.AbsoluteUri != endpoint.AbsoluteUri + "oidc")
                Add("Authority", "Authority 必须与 Endpoint 同源，且只包含一次 /oidc。");
            if (metadata is null || authority is null || !IdentityJson.SameOrigin(authority, metadata) ||
                metadata.AbsoluteUri != authority.AbsoluteUri + "/.well-known/openid-configuration")
                Add("MetadataAddress", "发现地址必须为可信 Authority 下的标准发现路径。");

            string? clientId = IdentityJson.GetString(logto, "ClientId");
            if (clientId is null || clientId.Length is < 1 or > 128 ||
                clientId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
                Add("ClientId", "Client ID 缺失或格式无效。");

            // These callback literals are the local protocol contract, not issuer/client secrets.
            string? redirect = IdentityJson.GetString(logto, "RedirectUri");
            string? logoutRedirect = IdentityJson.GetString(logto, "PostLogoutRedirectUri");
            if (redirect != "http://127.0.0.1:17853/callback/")
                Add("RedirectUri", "登录回调必须精确匹配约定的 IPv4 回环地址、端口和路径末尾斜杠。");
            if (logoutRedirect != "http://127.0.0.1:17853/logout-callback/")
                Add("PostLogoutRedirectUri", "退出回调必须精确匹配约定的 IPv4 回环地址、端口和路径末尾斜杠。");

            string[]? scopes = IdentityJson.GetStrings(logto, "Scopes");
            string[]? rememberScopes = IdentityJson.GetStrings(logto, "RememberSignInAdditionalScopes");
            if (scopes is null || scopes.Length != 2 || !scopes.Contains("openid") || !scopes.Contains("profile"))
                Add("Scopes", "默认范围必须且只能是 openid 和 profile，不可重复。");
            if (rememberScopes is null || !rememberScopes.SequenceEqual(["offline_access"]))
                Add("RememberSignInAdditionalScopes", "保持登录的额外范围必须且只能是 offline_access。");
            if (IdentityJson.GetString(logto, "ResponseType") != "code")
                Add("ResponseType", "公共桌面客户端仅允许授权码响应。");
            if (!logto.TryGetProperty("UsePkce", out JsonElement pkce) || pkce.ValueKind != JsonValueKind.True ||
                IdentityJson.GetString(logto, "PkceMethod") != "S256")
                Add("Pkce", "必须启用 PKCE S256。");
            if (IdentityJson.GetString(logto, "ClientAuthenticationMethod") != "none")
                Add("ClientAuthenticationMethod", "公共客户端不得使用共享 client secret。");

            return issues.Count == 0
                ? new(new(endpoint!, authority!, metadata!, clientId!, new Uri(redirect!), new Uri(logoutRedirect!), scopes!, rememberScopes!), [])
                : new(null, issues);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            return Failure("AUTH_CONFIGURATION_ERROR", "Json", "公开配置不是有效的严格 JSON，或包含重复字段。");
        }
    }

    private static LogtoConfigurationResult Failure(string code, string field, string message) =>
        new(null, [new(code, field, message)]);
}
