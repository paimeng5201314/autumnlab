using System.Net;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.Identity;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AutumnOS.Tests;

/// <summary>Duende + Microsoft validators through synthetic HTTPS handlers and a real loopback TCP callback.</summary>
[SupportedOSPlatform("windows")]
public static class IdentityProtocolTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        foreach (string defect in new[] { "valid", "nonce", "userinfo-sub", "issuer", "audience", "signature", "expired", "denied", "token-invalid-grant" })
            yield return ($"T03 OIDC protocol fixture: {defect}", () => Run(async () =>
            {
                using ProtocolFixture fixture = new(defect);
                NativeOidcFlow flow = new(IdentitySessionTests.Options(), fixture.OpenBrowser, () => new FixtureHandler(fixture));
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                try
                {
                    ProtectedIdentitySession session = await flow.SignInAsync(true, false, timeout.Token);
                    Assert(defect == "valid", "Malformed provider response was accepted.");
                    Assert(session.Subject == "fixture-user" && session.RefreshToken == "fixture-refresh" && fixture.TokenRequests == 1, "Protocol result incorrect.");
                    Assert(fixture.SawPkce && fixture.SawOfflineAccess && fixture.SawPublicClient, "PKCE/scopes/public client missing.");
                    ProtectedIdentitySession refreshed = await flow.RefreshAsync(session, timeout.Token);
                    Assert(refreshed.RefreshToken == "fixture-rotated", "Refresh rotation not used.");
                    await flow.SignOutBrowserAsync(refreshed, timeout.Token);
                    Assert(fixture.LogoutCalls == 1, "Browser logout not dispatched.");
                }
                catch (IdentityFlowException error)
                {
                    Assert(defect != "valid", "Valid OIDC flow failed with " + error.Code);
                    if (defect == "denied") Assert(error.Code == "USER_CANCELLED" && fixture.TokenRequests == 0, "Denial became authentication.");
                    if (defect == "token-invalid-grant") Assert(error.Code == "SESSION_EXPIRED", "Invalid grant not distinguished.");
                }
                await fixture.LastCallback.WaitAsync(timeout.Token);
            }));
        yield return ("T03 OIDC protocol fixture: login parameters are independent and no remember omits offline scope", () => Run(async () =>
        {
            using ProtocolFixture fixture = new("valid");
            NativeOidcFlow flow = new(IdentitySessionTests.Options(), fixture.OpenBrowser, () => new FixtureHandler(fixture));
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            ProtectedIdentitySession one = await flow.SignInAsync(false, false, timeout.Token);
            await fixture.LastCallback;
            (string state, string nonce, string challenge) first = (fixture.State, fixture.Nonce, fixture.Challenge);
            Assert(!fixture.SawOfflineAccess && one.RefreshToken is null, "No-remember retained refresh token.");
            ProtectedIdentitySession stillValid = await flow.RefreshAsync(one, timeout.Token);
            Assert(stillValid.Subject == one.Subject && stillValid.RefreshToken is null && fixture.TokenRequests == 1,
                "Valid session without refresh token expired or fabricated refresh credentials.");
            await flow.SignInAsync(false, true, timeout.Token);
            await fixture.LastCallback;
            Assert(first.state != fixture.State && first.nonce != fixture.Nonce && first.challenge != fixture.Challenge && fixture.Reauthentication,
                "Login reused protocol parameters or omitted fresh authentication.");
        }));
        foreach (string failure in new[] { "profile-503", "profile-429", "profile-offline", "profile-invalid-sub", "profile-401",
            "token-503", "token-429", "token-server-error", "token-temporarily-unavailable" })
            yield return ($"T03 OIDC restart Windows DPAPI fixture: refresh {failure}", () => Run(async () =>
            {
                string root = Path.Combine(Path.GetTempPath(), "autumnos-protocol-restart-owned-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(root);
                try
                {
                    using ProtocolFixture fixture = new("valid");
                    var options = IdentitySessionTests.Options();
                    var vault = new WindowsCredentialVault(root, options);
                    NativeOidcFlow flow = new(options, fixture.OpenBrowser, () => new FixtureHandler(fixture));
                    using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(15));
                    bool expired = failure is "profile-invalid-sub" or "profile-401";
                    bool rotated = failure.StartsWith("profile-", StringComparison.Ordinal);
                    using (var service = new LogtoIdentityService(options, vault, flow))
                    {
                        Assert((await service.SignInAsync(true, false, timeout.Token)).State == AutumnOS.Contracts.IdentitySessionState.SignedIn, "Initial fixture login failed.");
                        await fixture.LastCallback.WaitAsync(timeout.Token);
                        fixture.RefreshDefect = failure;
                        var result = await service.RefreshAsync(timeout.Token);
                        Assert(result.State == (expired ? AutumnOS.Contracts.IdentitySessionState.SessionExpired : AutumnOS.Contracts.IdentitySessionState.OfflineCached), "Transient provider failure expired identity, or invalid identity survived.");
                        if (expired) Assert(vault.Read() is null, "Revoked/mismatched identity retained persisted credentials.");
                        else
                        {
                            Assert(vault.Read()?.RefreshToken == (rotated ? "fixture-rotated" : "fixture-refresh"), "Refresh rotation was lost during temporary profile failure.");
                            Assert(result.ErrorCode == (failure == "profile-offline" ? "OFFLINE" : "AUTH_PROVIDER_UNAVAILABLE"), "Temporary failure classification incorrect.");
                        }
                    }
                    fixture.RefreshDefect = null;
                    using var reopened = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), flow);
                    var restored = await reopened.InitializeAsync(timeout.Token);
                    Assert(restored.State == (expired ? AutumnOS.Contracts.IdentitySessionState.SignedOut : AutumnOS.Contracts.IdentitySessionState.SignedIn), "Close/reopen restored incorrect identity.");
                    if (!expired) Assert(fixture.LastRefreshToken == (rotated ? "fixture-rotated" : "fixture-refresh"), "Reopen used stale instead of retained refresh token.");
                }
                finally { Directory.Delete(root, true); }
            }));
    }
    private static void Run(Func<Task> action) => action().GetAwaiter().GetResult();
    private static void Assert(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
    private sealed class FixtureHandler(ProtocolFixture fixture) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => fixture.ReplyAsync(request, cancellationToken);
    }
    private sealed class ProtocolFixture(string defect) : IDisposable
    {
        private readonly RSA _rsa = RSA.Create(2048);
        private readonly RSA _wrong = RSA.Create(2048);
        public string State = "", Nonce = "", Challenge = "";
        public bool SawPkce, SawOfflineAccess, SawPublicClient, Reauthentication;
        public int TokenRequests, LogoutCalls;
        public string? RefreshDefect, LastRefreshToken;
        public Task LastCallback = Task.CompletedTask;
        public void OpenBrowser(Uri uri)
        {
            Dictionary<string, string> query = Parse(uri.Query.TrimStart('?'));
            bool logout = uri.AbsolutePath.EndsWith("/end", StringComparison.Ordinal);
            if (!logout)
            {
                State = query["state"]; Nonce = query["nonce"]; Challenge = query["code_challenge"];
                SawPkce = query["code_challenge_method"] == "S256";
                SawOfflineAccess = query["scope"].Split(' ').Contains("offline_access");
                Reauthentication = query.GetValueOrDefault("prompt") == "login";
            }
            else LogoutCalls++;
            string callback = query[logout ? "post_logout_redirect_uri" : "redirect_uri"] + "?state=" + Uri.EscapeDataString(query["state"]);
            if (!logout) callback += defect == "denied" ? "&error=access_denied" : "&code=fixture-authorization-code";
            LastCallback = Task.Run(async () =>
            {
                using HttpClient http = new(new HttpClientHandler { UseProxy = false });
                string page = await http.GetStringAsync(callback);
                Assert(!page.Contains("fixture-authorization-code") && !page.Contains(State), "Callback page leaked secrets.");
            });
        }
        public async Task<HttpResponseMessage> ReplyAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("openid-configuration", StringComparison.Ordinal))
                return Json(new
                {
                    issuer = "https://identity.example.org/oidc", authorization_endpoint = "https://identity.example.org/oidc/auth",
                    token_endpoint = "https://identity.example.org/oidc/token", userinfo_endpoint = "https://identity.example.org/oidc/me",
                    jwks_uri = "https://identity.example.org/oidc/jwks", end_session_endpoint = "https://identity.example.org/oidc/end",
                    response_types_supported = new[] { "code" }, response_modes_supported = new[] { "query" },
                    code_challenge_methods_supported = new[] { "S256" }, token_endpoint_auth_methods_supported = new[] { "none" },
                    id_token_signing_alg_values_supported = new[] { "RS256" }, grant_types_supported = new[] { "authorization_code", "refresh_token" },
                    scopes_supported = new[] { "openid", "profile", "offline_access" }
                });
            if (path.EndsWith("/jwks", StringComparison.Ordinal))
            {
                RSAParameters key = _rsa.ExportParameters(false);
                return Json(new { keys = new[] { new { kty = "RSA", kid = "ephemeral-fixture", use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(key.Modulus), e = Base64UrlEncoder.Encode(key.Exponent) } } });
            }
            if (path.EndsWith("/me", StringComparison.Ordinal))
            {
                if (RefreshDefect == "profile-503") return Json(new { error = "temporarily_unavailable" }, HttpStatusCode.ServiceUnavailable);
                if (RefreshDefect == "profile-429") return Json(new { error = "temporarily_unavailable" }, HttpStatusCode.TooManyRequests);
                if (RefreshDefect == "profile-offline") throw new HttpRequestException("Synthetic provider unavailable.");
                if (RefreshDefect == "profile-401") return Json(new { error = "invalid_token" }, HttpStatusCode.Unauthorized);
                return Json(new { sub = defect == "userinfo-sub" || RefreshDefect == "profile-invalid-sub" ? "attacker-user" : "fixture-user", name = "Fixture 派蒙", email = "must-not-leave-host@example.invalid" });
            }
            if (path.EndsWith("/token", StringComparison.Ordinal))
            {
                TokenRequests++;
                string form = await request.Content!.ReadAsStringAsync(cancellationToken);
                Dictionary<string, string> values = Parse(form);
                SawPublicClient = values.GetValueOrDefault("client_id") == "fixture-client" && !values.ContainsKey("client_secret") && request.Headers.Authorization is null;
                bool refresh = values["grant_type"] == "refresh_token";
                if (refresh)
                {
                    LastRefreshToken = values["refresh_token"];
                    if (RefreshDefect == "token-503") return Json(new { error = "server_error" }, HttpStatusCode.ServiceUnavailable);
                    if (RefreshDefect == "token-429") return Json(new { error = "temporarily_unavailable" }, HttpStatusCode.TooManyRequests);
                    if (RefreshDefect == "token-server-error") return Json(new { error = "server_error" }, HttpStatusCode.BadRequest);
                    if (RefreshDefect == "token-temporarily-unavailable") return Json(new { error = "temporarily_unavailable" }, HttpStatusCode.BadRequest);
                }
                if (!refresh)
                    Assert(Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(values["code_verifier"]))) == Challenge, "PKCE verifier did not match challenge.");
                if (defect == "token-invalid-grant") return Json(new { error = "invalid_grant", error_description = "fixture secret not returned" }, HttpStatusCode.BadRequest);
                string token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = defect == "issuer" ? "https://wrong.example/oidc" : "https://identity.example.org/oidc",
                    Audience = defect == "audience" ? "wrong-client" : "fixture-client",
                    Subject = new ClaimsIdentity([new("sub", "fixture-user"), new("nonce", defect == "nonce" ? "wrong-nonce" : Nonce)]),
                    IssuedAt = DateTime.UtcNow.AddMinutes(-10), NotBefore = DateTime.UtcNow.AddMinutes(-10),
                    Expires = defect == "expired" ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(5),
                    SigningCredentials = new SigningCredentials(new RsaSecurityKey(defect == "signature" ? _wrong : _rsa) { KeyId = "ephemeral-fixture" }, "RS256")
                });
                return Json(new { access_token = "fixture-access", refresh_token = refresh ? "fixture-rotated" : "fixture-refresh", id_token = token, token_type = "Bearer", expires_in = 300 });
            }
            throw new InvalidOperationException("Unexpected fixture endpoint.");
        }
        private static Dictionary<string, string> Parse(string text) => text.Split('&').Select(p => p.Split('=', 2))
            .ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        private static HttpResponseMessage Json(object value, HttpStatusCode code = HttpStatusCode.OK) => new(code)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        public void Dispose() { _rsa.Dispose(); _wrong.Dispose(); }
    }
}
