using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AutumnOS.ServerIdentity.Sample;

public sealed record ServerIdentityConfiguration(Uri Authority, Uri MetadataAddress, string Audience, string ClientId, string RequiredScope)
{
    public void Validate()
    {
        if (Authority.Scheme != "https" || Authority.UserInfo.Length != 0 || Authority.Query.Length != 0 || Authority.Fragment.Length != 0 ||
            MetadataAddress.AbsoluteUri != Authority.AbsoluteUri.TrimEnd('/') + "/.well-known/openid-configuration" ||
            !Uri.TryCreate(Audience, UriKind.Absolute, out Uri? resource) || resource.Scheme != "https" ||
            resource.UserInfo.Length != 0 || resource.Fragment.Length != 0 || resource.Query.Length != 0 ||
            string.IsNullOrWhiteSpace(ClientId) || ClientId.Length > 128 || ClientId.Any(char.IsWhiteSpace) || Audience == ClientId ||
            string.IsNullOrWhiteSpace(RequiredScope) || RequiredScope.Length > 128 || RequiredScope.Any(char.IsWhiteSpace))
            throw new InvalidOperationException("SERVER_IDENTITY_CONFIGURATION_REQUIRED");
    }
}
public sealed record VerifiedPlayer(string Issuer, string Subject, string ClientId);
public sealed record VerificationResult(VerifiedPlayer? Player, string? Error)
{
    public bool Succeeded => Player is not null;
}

/// <summary>Resource access tokens only. No platform token exchange or client-side identity minting.</summary>
public sealed class ServerTokenVerifier
{
    private readonly ServerIdentityConfiguration _configuration;
    private readonly Func<CancellationToken, Task<IReadOnlyCollection<SecurityKey>>> _keys;
    public ServerTokenVerifier(ServerIdentityConfiguration configuration, Func<CancellationToken, Task<IReadOnlyCollection<SecurityKey>>> trustedSigningKeys)
    { configuration.Validate(); _configuration = configuration; _keys = trustedSigningKeys; }
    public async Task<VerificationResult> VerifyAsync(string? authorization, CancellationToken cancellationToken = default)
    {
        if (authorization is null || authorization.Length > 32775 || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            authorization.AsSpan(7).ContainsAny(' ', '\r', '\n')) return new(null, "INVALID_CREDENTIAL");
        string token = authorization[7..];
        if (token.Length < 16 || token.Count(c => c == '.') != 2) return new(null, "INVALID_CREDENTIAL");
        try
        {
            IReadOnlyCollection<SecurityKey> keys = await _keys(cancellationToken).ConfigureAwait(false);
            if (keys.Count is 0 or > 32) return new(null, "KEYS_UNAVAILABLE");
            TokenValidationResult result = await new JsonWebTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 32768 }
                .ValidateTokenAsync(token, new TokenValidationParameters
                {
                    ValidIssuer = _configuration.Authority.AbsoluteUri, ValidAudience = _configuration.Audience,
                    IssuerSigningKeys = keys, ValidateIssuerSigningKey = true, ValidateIssuer = true, ValidateAudience = true,
                    ValidateLifetime = true, RequireSignedTokens = true, RequireExpirationTime = true,
                    ClockSkew = TimeSpan.FromSeconds(30), ValidAlgorithms = ["RS256", "PS256", "ES256", "ES384"],
                    IncludeTokenOnFailedValidation = false, LogTokenId = false, LogValidationExceptions = false
                }).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.IsValid || result.ClaimsIdentity is null || result.SecurityToken is not JsonWebToken jwt) return new(null, "INVALID_CREDENTIAL");
            ClaimsIdentity claims = result.ClaimsIdentity;
            string[] subjects = claims.FindAll("sub").Select(c => c.Value).ToArray();
            string[] clients = claims.FindAll("client_id").Select(c => c.Value).ToArray();
            string[] scopes = claims.FindAll("scope").SelectMany(c => c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
            if (subjects.Length != 1 || subjects[0].Length is 0 or > 512 || subjects[0].Any(char.IsControl) ||
                clients.Length != 1 || clients[0] != _configuration.ClientId ||
                jwt.IssuedAt > DateTime.UtcNow.AddSeconds(30) || !claims.HasClaim(c => c.Type == "iat") || claims.HasClaim(c => c.Type == "nonce"))
                return new(null, "INVALID_CREDENTIAL");
            if (!scopes.Contains(_configuration.RequiredScope, StringComparer.Ordinal)) return new(null, "INSUFFICIENT_SCOPE");
            return new(new(_configuration.Authority.AbsoluteUri, subjects[0], clients[0]), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or SecurityTokenException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { return new(null, "KEYS_UNAVAILABLE"); }
    }
}

/// <summary>HTTPS discovery and bounded JWKS cache; no secret or authorization header is sent here.</summary>
public sealed class DiscoverySigningKeys(ServerIdentityConfiguration configuration) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1);
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false })
        { Timeout = TimeSpan.FromSeconds(10) };
    private IReadOnlyCollection<SecurityKey>? _cached;
    private DateTimeOffset _expires;
    public async Task<IReadOnlyCollection<SecurityKey>> GetAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_cached is not null && _expires > DateTimeOffset.UtcNow) return _cached;
            using JsonDocument discovery = JsonDocument.Parse(await GetJsonAsync(configuration.MetadataAddress, cancellationToken).ConfigureAwait(false));
            if (discovery.RootElement.GetProperty("issuer").GetString() != configuration.Authority.AbsoluteUri ||
                !Uri.TryCreate(discovery.RootElement.GetProperty("jwks_uri").GetString(), UriKind.Absolute, out Uri? jwks) ||
                jwks.Scheme != "https" || jwks.GetLeftPart(UriPartial.Authority) != configuration.Authority.GetLeftPart(UriPartial.Authority) ||
                jwks.UserInfo.Length != 0 || jwks.Fragment.Length != 0 || jwks.Query.Length != 0)
                throw new InvalidOperationException("UNTRUSTED_METADATA");
            JsonWebKeySet set = new(await GetJsonAsync(jwks, cancellationToken).ConfigureAwait(false));
            SecurityKey[] keys = set.GetSigningKeys().Where(key => key is not SymmetricSecurityKey).ToArray();
            if (keys.Length is 0 or > 32) throw new InvalidOperationException("INVALID_KEYS");
            _cached = keys; _expires = DateTimeOffset.UtcNow.AddSeconds(30);
            return keys;
        }
        finally { _gate.Release(); }
    }
    private async Task<string> GetJsonAsync(Uri uri, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > 131072 || response.Content.Headers.ContentType?.MediaType != "application/json")
            throw new InvalidOperationException("METADATA_UNAVAILABLE");
        await response.Content.LoadIntoBufferAsync(131072, cancellationToken).ConfigureAwait(false);
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }
    public void Dispose() { _http.Dispose(); _gate.Dispose(); }
}

/// <summary>Single-use, short-lived action confirmation, separate from reusable bearer authentication.</summary>
public sealed class ActionChallenges(TimeProvider? clock = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (VerifiedPlayer Player, DateTimeOffset Expires)> _pending = [];
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    public string? Issue(VerifiedPlayer player)
    {
        lock (_gate)
        {
            DateTimeOffset now = _clock.GetUtcNow();
            foreach (string expired in _pending.Where(pair => pair.Value.Expires <= now).Select(pair => pair.Key).ToArray()) _pending.Remove(expired);
            if (_pending.Count >= 1024) return null;
            string nonce = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
            _pending.Add(nonce, (player, now.AddSeconds(60)));
            return nonce;
        }
    }
    public bool Consume(string? nonce, VerifiedPlayer player)
    {
        if (nonce is null || nonce.Length != 43) return false;
        lock (_gate)
        {
            if (!_pending.TryGetValue(nonce, out var saved) || saved.Player != player) return false;
            _pending.Remove(nonce);
            return saved.Expires > _clock.GetUtcNow();
        }
    }
}
