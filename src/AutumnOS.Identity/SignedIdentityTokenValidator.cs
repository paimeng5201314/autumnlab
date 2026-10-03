using System.Security.Claims;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Results;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AutumnOS.Identity;

/// <summary>Explicit signature validator: the OIDC library's no-validation adapter is never used.</summary>
internal sealed class SignedIdentityTokenValidator(string issuer, string audience, IReadOnlyCollection<SecurityKey> keys,
    IReadOnlyCollection<string> algorithms, string nonce, bool refreshing = false, string? expectedSubject = null) : IIdentityTokenValidator
{
    public async Task<IdentityTokenValidationResult> ValidateAsync(string identityToken, OidcClientOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JsonWebTokenHandler handler = new() { MapInboundClaims = false, MaximumTokenSizeInBytes = 32768 };
        TokenValidationResult result = await handler.ValidateTokenAsync(identityToken, new TokenValidationParameters
        {
            ValidIssuer = issuer, ValidAudience = audience, IssuerSigningKeys = keys,
            ValidateIssuer = true, ValidateAudience = true, ValidateIssuerSigningKey = true,
            ValidateLifetime = true, RequireSignedTokens = true, RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromSeconds(30), ValidAlgorithms = algorithms,
            IncludeTokenOnFailedValidation = false, LogTokenId = false, LogValidationExceptions = false
        }).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.IsValid || result.SecurityToken is not JsonWebToken token || result.ClaimsIdentity is null)
            return new IdentityTokenValidationResult { Error = "AUTH_INVALID_TOKEN" };
        ClaimsIdentity identity = result.ClaimsIdentity;
        string[] subjects = identity.FindAll("sub").Select(c => c.Value).ToArray();
        string[] nonces = identity.FindAll("nonce").Select(c => c.Value).ToArray();
        string[] authorizedParties = identity.FindAll("azp").Select(c => c.Value).ToArray();
        if (subjects.Length != 1 || subjects[0].Length is 0 or > 512 ||
            (expectedSubject is not null && subjects[0] != expectedSubject) ||
            (!refreshing && (nonces.Length != 1 || !LoopbackCallback.FixedEquals(nonces[0], nonce))) ||
            (refreshing && nonces.Length > 0 && (nonces.Length != 1 || !LoopbackCallback.FixedEquals(nonces[0], nonce))) ||
            authorizedParties.Length > 1 || (authorizedParties.Length == 1 && authorizedParties[0] != audience) ||
            (token.Audiences.Count() > 1 && authorizedParties.Length != 1) ||
            !identity.HasClaim(c => c.Type == "iat") || token.IssuedAt > DateTime.UtcNow.AddSeconds(30))
            return new IdentityTokenValidationResult { Error = "AUTH_INVALID_TOKEN" };
        return new IdentityTokenValidationResult { User = new ClaimsPrincipal(identity), SignatureAlgorithm = token.Alg };
    }
}
