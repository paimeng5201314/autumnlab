namespace AutumnOS.Identity;

/// <summary>Validated public-client configuration. Only the configuration loader creates instances.</summary>
public sealed class LogtoPublicOptions
{
    internal LogtoPublicOptions(Uri endpoint, Uri authority, Uri metadataAddress, string clientId,
        Uri redirectUri, Uri postLogoutRedirectUri, string[] scopes, string[] rememberScopes)
    {
        Endpoint = endpoint;
        Authority = authority;
        MetadataAddress = metadataAddress;
        ClientId = clientId;
        RedirectUri = redirectUri;
        PostLogoutRedirectUri = postLogoutRedirectUri;
        Scopes = Array.AsReadOnly(scopes);
        RememberSignInAdditionalScopes = Array.AsReadOnly(rememberScopes);
    }

    public int SchemaVersion => 1;
    public Uri Endpoint { get; }
    public Uri Authority { get; }
    public Uri MetadataAddress { get; }
    public string ClientId { get; }
    public Uri RedirectUri { get; }
    public Uri PostLogoutRedirectUri { get; }
    public IReadOnlyList<string> Scopes { get; }
    public IReadOnlyList<string> RememberSignInAdditionalScopes { get; }
    public string ResponseType => "code";
    public bool UsePkce => true;
    public string PkceMethod => "S256";
    public string ClientAuthenticationMethod => "none";

    /// <summary>offline_access is added only after an explicit user choice; it does not promise a refresh token.</summary>
    public IReadOnlyList<string> GetRequestedScopes(bool rememberSignIn) => rememberSignIn
        ? Array.AsReadOnly(Scopes.Concat(RememberSignInAdditionalScopes).ToArray())
        : Scopes;
}

/// <summary>Messages and field names are fixed safe strings; no supplied values or exception text are echoed.</summary>
public sealed record IdentityConfigurationIssue(string Code, string Field, string Message);

public sealed class LogtoConfigurationResult
{
    internal LogtoConfigurationResult(LogtoPublicOptions? options, IEnumerable<IdentityConfigurationIssue> issues)
    {
        Options = options;
        Issues = Array.AsReadOnly(issues.ToArray());
    }

    public LogtoPublicOptions? Options { get; }
    public IReadOnlyList<IdentityConfigurationIssue> Issues { get; }
    public bool IsValid => Options is not null && Issues.Count == 0;
    public string SafeSummary => IsValid
        ? "公开配置校验通过；尚未验证 Native 类型、回调登记或真实登录。"
        : "公开身份配置不可用；请查看脱敏的配置错误代码。";
}
