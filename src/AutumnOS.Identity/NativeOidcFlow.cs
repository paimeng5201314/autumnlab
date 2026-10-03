using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using Duende.IdentityModel.Client;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Results;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace AutumnOS.Identity;

internal interface INativeOidcFlow
{
    Task<ProtectedIdentitySession> SignInAsync(bool remember, bool reauthenticate, CancellationToken cancellationToken);
    Task<ProtectedIdentitySession> RefreshAsync(ProtectedIdentitySession session, CancellationToken cancellationToken);
    Task SignOutBrowserAsync(ProtectedIdentitySession session, CancellationToken cancellationToken);
}

internal sealed class NativeOidcFlow(LogtoPublicOptions options, Action<Uri>? browser = null,
    Func<HttpMessageHandler>? transport = null, NativeResourceRequest? resourceRequest = null) : INativeOidcFlow
{
    // The independent developer sample has its own public registration, callback and process memory.
    // This internal opt-in is never exposed to third-party SDK messages or the host account service.
    private readonly NativeResourceRequest? resource = NativeResourceRequest.Validate(resourceRequest, options);
    private Action<Uri> OpenBrowser { get; } = browser ?? (uri =>
    {
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        { throw new IdentityFlowException("AUTH_BROWSER_UNAVAILABLE"); }
    });
    private HttpMessageHandler CreateTransport() => transport?.Invoke() ?? new HttpClientHandler
    { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null, AutomaticDecompression = DecompressionMethods.None };
    private HttpClient NewClient() => new(new BoundedIdentityHandler(options.Authority, CreateTransport())) { Timeout = TimeSpan.FromSeconds(15) };

    public async Task<ProtectedIdentitySession> SignInAsync(bool remember, bool reauthenticate, CancellationToken cancellationToken)
    {
        // Binding occurs before metadata work or browser launch; fixed port failures have no side effects.
        using LoopbackCallback callback = new(options.RedirectUri, resource is null ? 17853 : 17854);
        string nonce = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        (OidcClient client, LogtoDiscoveryDocument metadata) = await CreateClientAsync(remember, nonce, false, null, cancellationToken).ConfigureAwait(false);
        Parameters extra = new() { { "nonce", nonce } };
        if (resource is not null) extra.Add("resource", resource.Resource.AbsoluteUri);
        if (reauthenticate) extra.Add("prompt", "login");
        AuthorizeState state = await client.PrepareLoginAsync(extra, cancellationToken).ConfigureAwait(false);
        Uri start = ValidateBrowserUri(state.StartUrl, metadata.AuthorizationEndpoint);
        // The query normally has >8 fields; independently check the S256 challenge without echoing it.
        if (!start.Query.Contains("code_challenge_method=S256", StringComparison.Ordinal)) throw new IdentityFlowException("AUTH_PROTOCOL_ERROR");
        cancellationToken.ThrowIfCancellationRequested();
        OpenBrowser(start);
        string response = await callback.ReceiveAsync(state.State, false, cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> parameters = LoopbackCallback.ParseQuery(new Uri(response).Query.TrimStart('?'))!;
        if (parameters.TryGetValue("iss", out string? issuer) && issuer != options.Authority.AbsoluteUri)
            throw new IdentityFlowException("AUTH_INVALID_CALLBACK");
        if (parameters.TryGetValue("error", out string? error))
            throw new IdentityFlowException(error == "access_denied" ? "USER_CANCELLED" : "AUTH_PROVIDER_ERROR");
        Parameters? tokenParameters = resource is null ? null : new() { { "resource", resource.Resource.AbsoluteUri } };
        LoginResult result = await client.ProcessResponseAsync(response, state, tokenParameters, cancellationToken).ConfigureAwait(false);
        if (result.IsError) throw new IdentityFlowException(MapError(result.Error));
        string subject = result.User.FindFirst("sub")?.Value ?? throw new IdentityFlowException("AUTH_INVALID_TOKEN");
        return new(1, options.Authority.AbsoluteUri, options.ClientId, subject,
            SafeName(result.User.FindFirst("name")?.Value ?? result.User.FindFirst("username")?.Value),
            SafeAvatar(result.User.FindFirst("picture")?.Value), result.AccessToken,
            resource is null && remember && !string.IsNullOrEmpty(result.RefreshToken) ? result.RefreshToken : null,
            result.IdentityToken, result.AccessTokenExpiration, nonce);
    }

    public async Task<ProtectedIdentitySession> RefreshAsync(ProtectedIdentitySession session, CancellationToken cancellationToken)
    {
        if (resource is not null) throw new IdentityFlowException("CAPABILITY_UNAVAILABLE");
        // A known expired non-refreshable session cannot become a usable login merely because discovery is offline.
        if (string.IsNullOrEmpty(session.RefreshToken) && session.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new IdentityFlowException("SESSION_EXPIRED");
        (OidcClient client, LogtoDiscoveryDocument metadata) = await CreateClientAsync(true, session.Nonce, true, session.Subject, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(session.RefreshToken))
        {
            if (session.ExpiresAt <= DateTimeOffset.UtcNow) throw new IdentityFlowException("SESSION_EXPIRED");
            return await LoadProfileAsync(session, metadata, cancellationToken).ConfigureAwait(false);
        }
        RefreshTokenResult result = await client.RefreshTokenAsync(session.RefreshToken, cancellationToken: cancellationToken).ConfigureAwait(false);
        if (result.IsError) throw new IdentityFlowException(MapError(result.Error));
        ProtectedIdentitySession rotated = session with
        {
            AccessToken = result.AccessToken,
            RefreshToken = string.IsNullOrEmpty(result.RefreshToken) ? session.RefreshToken : result.RefreshToken,
            IdentityToken = string.IsNullOrEmpty(result.IdentityToken) ? session.IdentityToken : result.IdentityToken,
            ExpiresAt = result.AccessTokenExpiration
        };
        try { return await LoadProfileAsync(rotated, metadata, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) when (error is HttpRequestException or OperationCanceledException ||
            error is IdentityFlowException { Code: "AUTH_PROVIDER_UNAVAILABLE" })
        {
            string code = error is IdentityFlowException flow ? flow.Code : error is OperationCanceledException ? "AUTH_TIMEOUT" : "OFFLINE";
            throw new IdentityRefreshException(code, rotated);
        }
    }
    private async Task<ProtectedIdentitySession> LoadProfileAsync(ProtectedIdentitySession session, LogtoDiscoveryDocument metadata, CancellationToken cancellationToken)
    {
        using HttpClient http = NewClient();
        using HttpRequestMessage request = new(HttpMethod.Get, metadata.UserInfoEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new IdentityFlowException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "SESSION_EXPIRED",
            HttpStatusCode.TooManyRequests => "AUTH_PROVIDER_UNAVAILABLE",
            _ when (int)response.StatusCode >= 500 => "AUTH_PROVIDER_UNAVAILABLE",
            _ => "AUTH_PROVIDER_ERROR"
        });
        using JsonDocument profile = IdentityJson.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), 131072);
        if (IdentityJson.GetString(profile.RootElement, "sub") != session.Subject) throw new IdentityFlowException("AUTH_INVALID_TOKEN");
        return session with
        {
            DisplayName = SafeName(IdentityJson.GetString(profile.RootElement, "name") ?? IdentityJson.GetString(profile.RootElement, "username")),
            AvatarUrl = SafeAvatar(IdentityJson.GetString(profile.RootElement, "picture"))
        };
    }

    public async Task SignOutBrowserAsync(ProtectedIdentitySession session, CancellationToken cancellationToken)
    {
        if (resource is not null) throw new IdentityFlowException("CAPABILITY_UNAVAILABLE");
        using LoopbackCallback callback = new(options.PostLogoutRedirectUri);
        (OidcClient client, LogtoDiscoveryDocument metadata) = await CreateClientAsync(false, session.Nonce, true, session.Subject, cancellationToken).ConfigureAwait(false);
        if (metadata.EndSessionEndpoint is null) throw new IdentityFlowException("CAPABILITY_UNAVAILABLE");
        string state = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        string logout = await client.PrepareLogoutAsync(new LogoutRequest { IdTokenHint = session.IdentityToken, State = state }, cancellationToken).ConfigureAwait(false);
        OpenBrowser(ValidateBrowserUri(logout, metadata.EndSessionEndpoint));
        await callback.ReceiveAsync(state, true, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(OidcClient, LogtoDiscoveryDocument)> CreateClientAsync(bool remember, string nonce, bool refreshing,
        string? subject, CancellationToken cancellationToken)
    {
        using HttpClient http = NewClient();
        string discovery = await GetJsonAsync(http, options.MetadataAddress, cancellationToken).ConfigureAwait(false);
        LogtoDiscoveryResult check = LogtoDiscoveryProbe.ValidateJson(options, discovery);
        if (!check.IsValid) throw new IdentityFlowException("AUTH_METADATA_UNAVAILABLE");
        LogtoDiscoveryDocument metadata = check.Document!;
        using JsonDocument discoveryJson = IdentityJson.Parse(discovery, 131072);
        string[] scopes = IdentityJson.GetStrings(discoveryJson.RootElement, "scopes_supported") ?? [];
        bool supportedRemember = resource is null && remember && scopes.Contains("offline_access", StringComparer.Ordinal);
        string keyJson = await GetJsonAsync(http, metadata.JwksUri, cancellationToken).ConfigureAwait(false);
        using (JsonDocument _ = IdentityJson.Parse(keyJson, 131072)) { }
        JsonWebKeySet keySet = new(keyJson);
        SecurityKey[] keys = keySet.GetSigningKeys().Where(k => k is not SymmetricSecurityKey).ToArray();
        if (keys.Length is 0 or > 32) throw new IdentityFlowException("AUTH_INVALID_TOKEN");
        OidcClientOptions clientOptions = new()
        {
            Authority = options.Authority.AbsoluteUri, ClientId = options.ClientId,
            RedirectUri = options.RedirectUri.AbsoluteUri, PostLogoutRedirectUri = options.PostLogoutRedirectUri.AbsoluteUri,
            Scope = string.Join(' ', resource is null ? options.GetRequestedScopes(supportedRemember)
                : options.Scopes.Concat([resource.RequiredScope]).Distinct(StringComparer.Ordinal)),
            ProviderInformation = new ProviderInformation
            {
                IssuerName = metadata.Issuer.AbsoluteUri, AuthorizeEndpoint = metadata.AuthorizationEndpoint.AbsoluteUri,
                TokenEndpoint = metadata.TokenEndpoint.AbsoluteUri, UserInfoEndpoint = metadata.UserInfoEndpoint.AbsoluteUri,
                EndSessionEndpoint = metadata.EndSessionEndpoint?.AbsoluteUri,
                KeySet = new Duende.IdentityModel.Jwk.JsonWebKeySet(keyJson), TokenEndPointAuthenticationMethods = ["none"]
            },
            RefreshDiscoveryDocumentForLogin = false, RefreshDiscoveryOnSignatureFailure = false,
            // Resource JWTs are not Logto's opaque UserInfo access token. The independent
            // sample trusts only the verified ID token and does not request UserInfo.
            LoadProfile = resource is null, FilterClaims = false, ClockSkew = TimeSpan.FromSeconds(30),
            LoggerFactory = NullLoggerFactory.Instance, BackchannelTimeout = TimeSpan.FromSeconds(15),
            HttpClientFactory = _ => NewClient(),
            IdentityTokenValidator = new SignedIdentityTokenValidator(options.Authority.AbsoluteUri, options.ClientId, keys,
                metadata.AllowedIdTokenSigningAlgorithms.ToArray(), nonce, refreshing, subject),
            Policy = new Policy
            {
                RequireIdentityTokenSignature = true, ValidateTokenIssuerName = true,
                ValidSignatureAlgorithms = metadata.AllowedIdTokenSigningAlgorithms.ToList(),
                RequireIdentityTokenOnRefreshTokenResponse = false,
                Discovery = new DiscoveryPolicy { RequireHttps = true, ValidateIssuerName = true, ValidateEndpoints = true }
            }
        };
        return (new OidcClient(clientOptions), metadata);
    }
    private static async Task<string> GetJsonAsync(HttpClient http, Uri uri, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using HttpResponseMessage response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new IdentityFlowException("AUTH_METADATA_UNAVAILABLE");
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
    private static Uri ValidateBrowserUri(string text, Uri expected)
    {
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || uri.GetLeftPart(UriPartial.Path) != expected.AbsoluteUri ||
            uri.Scheme != "https" || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new IdentityFlowException("AUTH_PROTOCOL_ERROR");
        return uri;
    }
    internal static string MapError(string? error)
    {
        // Duende wraps token errors (for example "Error redeeming code: invalid_grant").
        // Only classify finite protocol identifiers; never return descriptions/remote text.
        string[] identifiers = (error ?? "").Split([' ', ':', ';', ',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (identifiers.Contains("invalid_grant", StringComparer.Ordinal)) return "SESSION_EXPIRED";
        if (identifiers.Contains("access_denied", StringComparer.Ordinal)) return "USER_CANCELLED";
        if (identifiers.Contains("server_error", StringComparer.Ordinal) || identifiers.Contains("temporarily_unavailable", StringComparer.Ordinal))
            return "AUTH_PROVIDER_UNAVAILABLE";
        if (identifiers.Contains("AUTH_PROVIDER_UNAVAILABLE", StringComparer.Ordinal)) return "AUTH_PROVIDER_UNAVAILABLE";
        return "AUTH_INVALID_RESPONSE";
    }
    internal static string? SafeName(string? value) => string.IsNullOrWhiteSpace(value) ? null : new(value.Where(c => !char.IsControl(c)).Take(128).ToArray());
    internal static string? SafeAvatar(string? value) => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) && uri.Scheme == "https" &&
        uri.UserInfo.Length == 0 && uri.AbsoluteUri.Length <= 2048 ? uri.AbsoluteUri : null;
}

/// <summary>Internal validated rotation result. Message/ToString contain only a finite error code, never credentials.</summary>
internal sealed class IdentityRefreshException(string code, ProtectedIdentitySession refreshedSession) : Exception(code)
{
    internal string Code { get; } = code;
    internal ProtectedIdentitySession RefreshedSession { get; } = refreshedSession;
}

internal sealed class BoundedIdentityHandler(Uri authority, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Uri? uri = request.RequestUri;
        if (uri is null || uri.Scheme != "https" || !IdentityJson.SameOrigin(authority, uri) || uri.UserInfo.Length != 0)
            throw new IdentityFlowException("AUTH_UNTRUSTED_ENDPOINT");
        HttpResponseMessage response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        try
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500)
                throw new IdentityFlowException("AUTH_PROVIDER_UNAVAILABLE");
            if ((int)response.StatusCode is >= 300 and <= 399) throw new IdentityFlowException("AUTH_UNTRUSTED_ENDPOINT");
            if (response.Content.Headers.ContentLength is > 131072) throw new IdentityFlowException("AUTH_RESPONSE_TOO_LARGE");
            await response.Content.LoadIntoBufferAsync(131072, cancellationToken).ConfigureAwait(false);
            return response;
        }
        catch { response.Dispose(); throw; }
    }
}
