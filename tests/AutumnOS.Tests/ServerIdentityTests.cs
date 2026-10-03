using System.Security.Claims;
using System.Security.Cryptography;
using AutumnOS.ServerIdentity.Sample;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AutumnOS.Tests;

public static class ServerIdentityTests
{
    private static ServerIdentityConfiguration Configuration => new(new("https://fixture.example/oidc"),
        new("https://fixture.example/oidc/.well-known/openid-configuration"), "https://fixture-api.example", "fixture-independent-native", "player:read");
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        foreach (string defect in new[] { "valid", "signature", "issuer", "audience", "expired", "client", "scope", "unsigned", "bare-user", "nickname", "id-token", "missing-exp" })
            yield return ($"T03 server JWT fixture: {defect}", () => Run(async () =>
            {
                using RSA rsa = RSA.Create(2048);
                using RSA attacker = RSA.Create(2048);
                RsaSecurityKey trusted = new(rsa) { KeyId = "fixture-server-key" };
                ServerTokenVerifier verifier = new(Configuration, _ => Task.FromResult<IReadOnlyCollection<SecurityKey>>([trusted]));
                List<Claim> claims = [new("sub", "fixture-player"), new("client_id", defect == "client" ? "other-native" : Configuration.ClientId),
                    new("scope", defect == "scope" ? "other:read" : "player:read")];
                if (defect == "id-token") claims.Add(new("nonce", "fixture-nonce"));
                JsonWebTokenHandler generator = new() { SetDefaultTimesOnTokenCreation = false };
                string token = generator.CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = defect == "issuer" ? "https://attacker.example/oidc" : Configuration.Authority.AbsoluteUri,
                    Audience = defect == "audience" ? "https://other-api.example" : defect == "id-token" ? Configuration.ClientId : Configuration.Audience,
                    Subject = new ClaimsIdentity(claims), IssuedAt = DateTime.UtcNow.AddMinutes(-10), NotBefore = DateTime.UtcNow.AddMinutes(-10),
                    Expires = defect == "missing-exp" ? null : defect == "expired" ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(5),
                    SigningCredentials = defect == "unsigned" ? null : new SigningCredentials(defect == "signature" ? new RsaSecurityKey(attacker) : trusted, "RS256")
                });
                VerificationResult result = await verifier.VerifyAsync(defect == "bare-user" ? "Bearer fixture-player" : defect == "nickname" ? "派蒙" : "Bearer " + token);
                Assert(result.Succeeded == (defect == "valid"), "Server accepted/rejected incorrect credential.");
                Assert(result.Error is null || !result.Error.Contains(token) && !result.Error.Contains("fixture-player"), "Server failure exposed credential data.");
            }));
        foreach (bool forged in new[] { false, true })
            yield return ($"T03 server ES384 P-384 fixture: {(forged ? "wrong signature rejected" : "valid resource token accepted")}", () => Run(async () =>
            {
                using ECDsa signing = ECDsa.Create(ECCurve.NamedCurves.nistP384);
                using ECDsa attacker = ECDsa.Create(ECCurve.NamedCurves.nistP384);
                ECDsaSecurityKey trusted = new(signing) { KeyId = "fixture-es384-key" };
                ServerTokenVerifier verifier = new(Configuration, _ => Task.FromResult<IReadOnlyCollection<SecurityKey>>([trusted]));
                string token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = Configuration.Authority.AbsoluteUri, Audience = Configuration.Audience,
                    Subject = new ClaimsIdentity([new("sub", "fixture-player"), new("client_id", Configuration.ClientId), new("scope", Configuration.RequiredScope)]),
                    IssuedAt = DateTime.UtcNow.AddMinutes(-1), NotBefore = DateTime.UtcNow.AddMinutes(-1), Expires = DateTime.UtcNow.AddMinutes(5),
                    SigningCredentials = new SigningCredentials(forged ? new ECDsaSecurityKey(attacker) { KeyId = trusted.KeyId } : trusted, "ES384")
                });
                VerificationResult result = await verifier.VerifyAsync("Bearer " + token);
                Assert(result.Succeeded == !forged && (result.Succeeded || result.Error == "INVALID_CREDENTIAL"), "ES384 signature validation mismatch.");
            }));
        yield return ("T03 server fixture: missing resource configuration fails closed", () =>
        {
            try { (Configuration with { Audience = "" }).Validate(); throw new Exception("Missing resource accepted."); }
            catch (InvalidOperationException error) { Assert(error.Message == "SERVER_IDENTITY_CONFIGURATION_REQUIRED", "Wrong safe configuration error."); }
        });
        yield return ("T03 server fixture: challenge is account bound and consumed once", () =>
        {
            ActionChallenges challenges = new();
            VerifiedPlayer a = new("issuer", "player-a", "client");
            VerifiedPlayer b = new("issuer", "player-b", "client");
            string nonce = challenges.Issue(a)!;
            Assert(!challenges.Consume(nonce, b) && challenges.Consume(nonce, a) && !challenges.Consume(nonce, a), "Challenge cross-account/replay accepted.");
        });
        yield return ("T03 server fixture: expired challenge rejected and capacity bounded", () =>
        {
            FixtureClock clock = new(); ActionChallenges challenges = new(clock);
            VerifiedPlayer player = new("issuer", "player", "client");
            string first = challenges.Issue(player)!;
            for (int i = 1; i < 1024; i++) Assert(challenges.Issue(player) is not null, "Capacity exhausted prematurely.");
            Assert(challenges.Issue(player) is null, "Capacity unbounded.");
            clock.Now += TimeSpan.FromSeconds(61);
            Assert(!challenges.Consume(first, player) && challenges.Issue(player) is not null, "Expiry or bounded cleanup failed.");
        });
    }
    private sealed class FixtureClock : TimeProvider
    { public DateTimeOffset Now = DateTimeOffset.UtcNow; public override DateTimeOffset GetUtcNow() => Now; }
    private static void Run(Func<Task> test) => test().GetAwaiter().GetResult();
    private static void Assert(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
}
