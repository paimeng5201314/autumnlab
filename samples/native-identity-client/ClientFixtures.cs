using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.Identity;
using AutumnOS.ServerIdentity.Sample;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AutumnOS.NativeIdentity.Sample;

/// <summary>Explicit synthetic provider/API, ephemeral signing keys, and a real IPv4 callback socket. Never real Logto evidence.</summary>
internal static class ClientFixtures
{
    internal static async Task<int> RunAsync(string report)
    {
        if (File.Exists(report)) { Console.WriteLine("REPORT_ALREADY_EXISTS"); return 2; }
        List<object> results = []; int failed = 0;
        async Task Case(string name, Func<Task> test)
        {
            try { await test(); results.Add(new { name, status = "passed", error = (string?)null }); Console.WriteLine("passed " + name); }
            catch (Exception error) { failed++; results.Add(new { name, status = "failed", error = error is IdentityFlowException flow ? flow.Code : error.GetType().Name }); Console.WriteLine("failed " + name); }
        }
        foreach (string defect in new[] { "valid", "valid-es384", "wrong-state-first", "id-nonce", "id-signature", "id-es384-signature", "denied", "invalid-grant", "resource-audience", "resource-expired", "resource-scope", "resource-es384-signature", "api-subject", "api-redirect", "api-oversize" })
        {
            await Case("independent-client." + defect, async () =>
            {
                using Fixture fixture = new(defect);
                var config = Configuration();
                var flow = new NativeOidcFlow(config.Identity, fixture.OpenBrowser, () => new Handler(fixture.Provider), config.Resource);
                using CancellationTokenSource budget = new(TimeSpan.FromSeconds(10));
                try
                {
                    ProtectedIdentitySession session = await flow.SignInAsync(false, false, budget.Token);
                    Check(defect is not ("id-nonce" or "id-signature" or "id-es384-signature" or "denied" or "invalid-grant"));
                    Check(fixture.AuthResource && fixture.TokenResource && fixture.Pkce && fixture.PublicClient && !fixture.OfflineAccess);
                    Check(fixture.UserInfoCalls == 0 && session.RefreshToken is null && session.Subject == "fixture-player");
                    using LocalResourceClient api = new(new Handler(fixture.Api));
                    await api.VerifyAndConfirmAsync(session, budget.Token);
                    Check(defect is "valid" or "valid-es384" or "wrong-state-first");
                    Check(fixture.ApiCalls == 3 && fixture.ChallengeConsumed);
                }
                catch (IdentityFlowException error)
                {
                    string expected = defect switch
                    {
                        "denied" => "USER_CANCELLED", "invalid-grant" => "SESSION_EXPIRED",
                        "resource-audience" or "resource-expired" or "resource-es384-signature" => "SAMPLE_API_UNAUTHORIZED", "resource-scope" => "SAMPLE_API_FORBIDDEN",
                        "api-subject" => "SAMPLE_API_IDENTITY_MISMATCH", "api-redirect" => "SAMPLE_API_UNAVAILABLE",
                        "api-oversize" => "SAMPLE_API_INVALID_RESPONSE", "id-nonce" or "id-signature" or "id-es384-signature" => "AUTH_INVALID_RESPONSE",
                        _ => "unexpected-success-required"
                    };
                    Check(error.Code == expected);
                }
                await fixture.Callback.WaitAsync(budget.Token);
            });
        }
        await Case("independent-client.explicit-port-conflict-keeps-owner", async () =>
        {
            TcpListener owner = new(IPAddress.Loopback, 17854); owner.Server.ExclusiveAddressUse = true; owner.Start();
            try
            {
                var config = Configuration(); bool opened = false;
                try { await new NativeOidcFlow(config.Identity, _ => opened = true, resourceRequest: config.Resource).SignInAsync(false, false, CancellationToken.None); Check(false); }
                catch (IdentityFlowException error) { Check(error.Code == "AUTH_PORT_IN_USE" && !opened && owner.Server.IsBound); }
            }
            finally { owner.Stop(); }
        });
        await Case("independent-client.cancel-releases-port", async () =>
        {
            using Fixture fixture = new("no-callback"); var config = Configuration();
            using CancellationTokenSource budget = new(TimeSpan.FromMilliseconds(300));
            try { await new NativeOidcFlow(config.Identity, fixture.OpenBrowser, () => new Handler(fixture.Provider), config.Resource).SignInAsync(false, false, budget.Token); Check(false); }
            catch (OperationCanceledException) { }
            using LoopbackCallback next = new(config.Identity.RedirectUri, 17854);
        });
        await Case("independent-client.host-registration-is-never-reused", () =>
        {
            Throws(() => ClientConfiguration.Create(Authority, Metadata, ClientConfiguration.LoadHostClientId(), ClientConfiguration.Redirect, Resource, Scope));
            Throws(() => ClientConfiguration.Create(Authority, Metadata, ClientId, "http://127.0.0.1:17853/callback/", Resource, Scope));
            Throws(() => ClientConfiguration.Create(Authority, Metadata, ClientId, ClientConfiguration.Redirect, Resource, "offline_access"));
            return Task.CompletedTask;
        });
        await Case("independent-client.strict-config-rejects-untrusted-authority-and-resource", () =>
        {
            Throws(() => ClientConfiguration.Create("http://provider.invalid/oidc", Metadata, ClientId, ClientConfiguration.Redirect, Resource, Scope));
            Throws(() => ClientConfiguration.Create(Authority, "https://other.invalid/discovery", ClientId, ClientConfiguration.Redirect, Resource, Scope));
            Throws(() => ClientConfiguration.Create(Authority, Metadata, ClientId, ClientConfiguration.Redirect, "file:///c:/secret", Scope));
            Throws(() => ClientConfiguration.Create(Authority, Metadata, ClientId, ClientConfiguration.Redirect, Resource, "read write"));
            return Task.CompletedTask;
        });
        await Case("independent-client.host-loopback-default-stays-exact", () =>
        {
            Throws(() => { using LoopbackCallback forbidden = new(new Uri(ClientConfiguration.Redirect)); });
            return Task.CompletedTask;
        });
        await Case("independent-client.missing-host-public-source-fails-closed", () =>
        {
            Throws(() => ClientConfiguration.LoadHostClientId(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "not-created.json")));
            return Task.CompletedTask;
        });
        string destination = Path.GetFullPath(report); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await JsonSerializer.SerializeAsync(output, new { schema_version = 1, task = "T03-R034-independent-native-client",
                build_id = AutumnOS.Contracts.BrandInfo.BuildId, source_snapshot_id = AutumnOS.Contracts.BrandInfo.SourceSnapshotId, executed_utc = DateTimeOffset.UtcNow,
                environment = Environment.OSVersion.ToString(), kind = "synthetic HTTPS/API handlers, ephemeral RSA, actual loopback TCP",
                real_logto = "not_run", status = failed == 0 ? "passed" : "failed", total = results.Count, failed, results },
                new JsonSerializerOptions { WriteIndented = true });
        return failed == 0 ? 0 : 1;
    }
    private const string Authority = "https://fixture.identity.invalid/oidc", Metadata = Authority + "/.well-known/openid-configuration";
    private const string ClientId = "independent-fixture-client", Resource = "https://fixture.resource.invalid/api", Scope = "read:player";
    private static ClientConfiguration Configuration() => ClientConfiguration.Create(Authority, Metadata, ClientId, ClientConfiguration.Redirect, Resource, Scope);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Fixture assertion failed."); }
    private static void Throws(Action action)
    { try { action(); } catch (IdentityFlowException) { return; } throw new InvalidOperationException("Invalid configuration accepted."); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken); }
    private sealed class Fixture(string defect) : IDisposable
    {
        private readonly RSA key = RSA.Create(2048), wrongKey = RSA.Create(2048);
        private readonly ECDsa ecKey = ECDsa.Create(ECCurve.NamedCurves.nistP384), wrongEcKey = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        private bool Es384 => defect.Contains("es384", StringComparison.Ordinal);
        private SecurityKey TrustedKey => Es384 ? new ECDsaSecurityKey(ecKey) { KeyId = "fixture-key" } : new RsaSecurityKey(key) { KeyId = "fixture-key" };
        private string state = "", nonce = "", challenge = "";
        private readonly ActionChallenges actions = new();
        internal bool AuthResource, TokenResource, Pkce, PublicClient, OfflineAccess, ChallengeConsumed;
        internal int UserInfoCalls, ApiCalls;
        internal Task Callback = Task.CompletedTask;
        internal void OpenBrowser(Uri uri)
        {
            var query = Parse(uri.Query.TrimStart('?')); state = query["state"]; nonce = query["nonce"]; challenge = query["code_challenge"];
            AuthResource = query.GetValueOrDefault("resource") == Resource;
            Pkce = query["code_challenge_method"] == "S256";
            string[] scopes = query["scope"].Split(' '); OfflineAccess = scopes.Contains("offline_access"); Check(scopes.Contains(Scope));
            if (defect == "no-callback") return;
            Callback = Task.Run(async () =>
            {
                using HttpClient http = new(new HttpClientHandler { UseProxy = false });
                if (defect == "wrong-state-first")
                { using var wrong = await http.GetAsync(query["redirect_uri"] + "?state=wrong&code=fixture-code"); Check(wrong.StatusCode == HttpStatusCode.BadRequest); }
                string callback = query["redirect_uri"] + "?state=" + Uri.EscapeDataString(state) + (defect == "denied" ? "&error=access_denied" : "&code=fixture-code");
                string page = await http.GetStringAsync(callback);
                Check(!page.Contains("fixture-code") && !page.Contains(state) && !page.Contains(nonce));
            });
        }
        internal async Task<HttpResponseMessage> Provider(HttpRequestMessage request, CancellationToken token)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("openid-configuration", StringComparison.Ordinal))
                return Json(new { issuer = Authority, authorization_endpoint = Authority + "/auth", token_endpoint = Authority + "/token",
                    userinfo_endpoint = Authority + "/me", jwks_uri = Authority + "/jwks", response_types_supported = new[] { "code" },
                    code_challenge_methods_supported = new[] { "S256" }, token_endpoint_auth_methods_supported = new[] { "none" },
                    id_token_signing_alg_values_supported = new[] { Es384 ? "ES384" : "RS256" }, scopes_supported = new[] { "openid", "profile", "offline_access", Scope } });
            if (path.EndsWith("/jwks", StringComparison.Ordinal))
            {
                if (Es384) { var ec = ecKey.ExportParameters(false); return Json(new { keys = new[] { new { kty = "EC", crv = "P-384", kid = "fixture-key", alg = "ES384", use = "sig", x = Base64UrlEncoder.Encode(ec.Q.X), y = Base64UrlEncoder.Encode(ec.Q.Y) } } }); }
                var rsa = key.ExportParameters(false); return Json(new { keys = new[] { new { kty = "RSA", kid = "fixture-key", alg = "RS256", use = "sig", n = Base64UrlEncoder.Encode(rsa.Modulus), e = Base64UrlEncoder.Encode(rsa.Exponent) } } });
            }
            if (path.EndsWith("/me", StringComparison.Ordinal)) { UserInfoCalls++; throw new InvalidOperationException("Resource token must not call UserInfo."); }
            if (path.EndsWith("/token", StringComparison.Ordinal))
            {
                var form = Parse(await request.Content!.ReadAsStringAsync(token));
                TokenResource = form.GetValueOrDefault("resource") == Resource;
                PublicClient = form.GetValueOrDefault("client_id") == ClientId && !form.ContainsKey("client_secret") && request.Headers.Authorization is null;
                Check(Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form["code_verifier"]))) == challenge);
                if (defect == "invalid-grant") return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
                string id = Jwt(ClientId, [new("sub", "fixture-player"), new("nonce", defect == "id-nonce" ? "wrong" : nonce)], false, defect is "id-signature" or "id-es384-signature");
                string access = Jwt(defect == "resource-audience" ? "https://wrong.invalid/api" : Resource,
                    [new("sub", "fixture-player"), new("client_id", ClientId), new("scope", defect == "resource-scope" ? "other" : Scope)], defect == "resource-expired", defect == "resource-es384-signature");
                return Json(new { access_token = access, id_token = id, refresh_token = "unexpected-fixture-refresh", token_type = "Bearer", expires_in = 300 });
            }
            throw new InvalidOperationException("Unexpected fixture provider endpoint.");
        }
        private string Jwt(string audience, Claim[] claims, bool expired, bool forged) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Authority, Audience = audience, Subject = new ClaimsIdentity(claims), IssuedAt = DateTime.UtcNow.AddMinutes(-10),
            NotBefore = DateTime.UtcNow.AddMinutes(-10), Expires = expired ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new(Es384 ? new ECDsaSecurityKey(forged ? wrongEcKey : ecKey) { KeyId = "fixture-key" }
                : new RsaSecurityKey(forged ? wrongKey : key) { KeyId = "fixture-key" }, Es384 ? "ES384" : "RS256")
        });
        internal async Task<HttpResponseMessage> Api(HttpRequestMessage request, CancellationToken token)
        {
            ApiCalls++;
            Check(request.RequestUri!.GetLeftPart(UriPartial.Authority) == "http://127.0.0.1:5197" && request.RequestUri.Query.Length == 0);
            ServerTokenVerifier verifier = new(new(new Uri(Authority), new Uri(Metadata), Resource, ClientId, Scope),
                _ => Task.FromResult<IReadOnlyCollection<SecurityKey>>([TrustedKey]));
            VerificationResult verified = await verifier.VerifyAsync(request.Headers.Authorization?.ToString(), token);
            if (!verified.Succeeded) return Json(new { error = verified.Error }, verified.Error == "INSUFFICIENT_SCOPE" ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized);
            VerifiedPlayer player = verified.Player!;
            if (defect == "api-redirect") return new(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://never-follow.invalid/") } };
            if (defect == "api-oversize") return Json(new { oversized = new string('x', 9000) });
            if (request.RequestUri.AbsolutePath == "/v1/me") return Json(new { subject = defect == "api-subject" ? "other" : player.Subject, issuer = player.Issuer, clientId = player.ClientId });
            if (request.RequestUri.AbsolutePath == "/v1/action-challenge") return Json(new { challenge = actions.Issue(player), expiresIn = 60 });
            if (request.RequestUri.AbsolutePath == "/v1/confirm-action")
            {
                using JsonDocument body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(token));
                string value = body.RootElement.GetProperty("challenge").GetString()!;
                ChallengeConsumed = actions.Consume(value, player) && !actions.Consume(value, player);
                return Json(new { accepted = ChallengeConsumed });
            }
            throw new InvalidOperationException("Unexpected fixture API route.");
        }
        private static Dictionary<string, string> Parse(string value) => value.Split('&').Select(p => p.Split('=', 2)).ToDictionary(
            p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
        private static HttpResponseMessage Json(object value, HttpStatusCode code = HttpStatusCode.OK) => new(code)
        { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
        public void Dispose() { key.Dispose(); wrongKey.Dispose(); ecKey.Dispose(); wrongEcKey.Dispose(); }
    }
}
