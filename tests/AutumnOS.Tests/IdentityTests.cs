using System.Text.Json.Nodes;
using AutumnOS.Identity;

namespace AutumnOS.Tests;

public static class IdentityTests
{
    // Deliberately synthetic public values. Production values remain solely in the spec config file.
    private const string PublicJson = """
        {"SchemaVersion":1,"Logto":{
        "Endpoint":"https://identity.example.org/",
        "Authority":"https://identity.example.org/oidc",
        "MetadataAddress":"https://identity.example.org/oidc/.well-known/openid-configuration",
        "ClientId":"test-public-client",
        "RedirectUri":"http://127.0.0.1:17853/callback/",
        "PostLogoutRedirectUri":"http://127.0.0.1:17853/logout-callback/",
        "Scopes":["openid","profile"],"RememberSignInAdditionalScopes":["offline_access"],
        "ResponseType":"code","UsePkce":true,"PkceMethod":"S256","ClientAuthenticationMethod":"none"}}
        """;

    private const string DiscoveryJson = """
        {"issuer":"https://identity.example.org/oidc",
        "authorization_endpoint":"https://identity.example.org/oidc/auth",
        "token_endpoint":"https://identity.example.org/oidc/token",
        "userinfo_endpoint":"https://identity.example.org/oidc/me",
        "jwks_uri":"https://identity.example.org/oidc/jwks",
        "end_session_endpoint":"https://identity.example.org/oidc/session/end",
        "revocation_endpoint":"https://identity.example.org/oidc/token/revocation",
        "response_types_supported":["code"],"response_modes_supported":["query"],
        "code_challenge_methods_supported":["S256"],"token_endpoint_auth_methods_supported":["none"],
        "id_token_signing_alg_values_supported":["RS256"],
        "scopes_supported":["openid","profile","offline_access"],"grant_types_supported":["authorization_code"]}
        """;

    private const string RegistrationJson = """
        {"schema_version":1,"pack_version":"0.2","application_id":"test-public-client",
        "public_values_source":"test fixture","application_type_required":"Native/public client",
        "application_type_observed":"unverified","redirect_uri_status":"proposed_not_confirmed_registered",
        "post_logout_redirect_uri_status":"proposed_not_confirmed_registered",
        "discovery_probe":{"status":"not_verified","attempted_read_only":true,"reason":"test only",
        "product_login_test_executed":false},"console_changes_performed":false,"contains_secrets":false}
        """;

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Identity: valid public configuration is typed and not login proof", () =>
        {
            LogtoConfigurationResult result = LogtoConfigurationLoader.ValidateJson(PublicJson);
            Assert(result.IsValid && result.Options is not null, "Valid configuration rejected.");
            Assert(result.Options!.Authority.AbsolutePath == "/oidc", "Authority incorrectly composed.");
            Assert(result.SafeSummary.Contains("尚未验证"), "Configuration must not claim authentication.");
        });
        yield return ("Identity: offline_access requires explicit remember choice", () =>
        {
            LogtoPublicOptions options = Options();
            Assert(options.GetRequestedScopes(false).SequenceEqual(["openid", "profile"]), "Default scopes grew.");
            Assert(options.GetRequestedScopes(true).SequenceEqual(["openid", "profile", "offline_access"]), "Remember scopes incorrect.");
        });
        foreach ((string field, string value) in new (string, string)[]
        {
            ("Endpoint", "http://identity.example.org/"),
            ("Endpoint", "https://identity.example.org/tenant/"),
            ("Endpoint", "https://localhost/"),
            ("Endpoint", "https://127.0.0.1/"),
            ("Authority", "https://other.example.org/oidc"),
            ("Authority", "https://identity.example.org/oidc/oidc"),
            ("Authority", "https://identity.example.org/oidc/"),
            ("MetadataAddress", "https://attacker.example.org/oidc/.well-known/openid-configuration"),
            ("MetadataAddress", "https://identity.example.org/oidc/.well-known/openid-configuration?token=DO_NOT_ECHO"),
            ("MetadataAddress", "https://user:DO_NOT_ECHO@identity.example.org/oidc/.well-known/openid-configuration"),
            ("MetadataAddress", "https://identity.example.org:8443/oidc/.well-known/openid-configuration"),
            ("MetadataAddress", "https://identity.example.org/oidc/.well-known/openid-configuration#fragment"),
            ("RedirectUri", "http://localhost:17853/callback/"),
            ("RedirectUri", "http://0.0.0.0:17853/callback/"),
            ("RedirectUri", "http://127.0.0.1:17854/callback/"),
            ("RedirectUri", "http://127.0.0.1:17853/callback"),
            ("RedirectUri", "http://127.0.0.1:17853/callback/?code=DO_NOT_ECHO"),
            ("PostLogoutRedirectUri", "http://127.0.0.1:17853/logout-callback"),
            ("ClientId", "client\nDO_NOT_ECHO"),
            ("ResponseType", "id_token"),
            ("PkceMethod", "plain"),
            ("ClientAuthenticationMethod", "client_secret_post")
        })
        {
            string caseField = field;
            string caseValue = value;
            yield return ($"Identity: rejects unsafe {field} variant {StableLabel(value)}", () =>
                InvalidConfiguration(MutatePublic(logto => logto[caseField] = caseValue)));
        }
        yield return ("Identity: PKCE cannot be disabled", () =>
            InvalidConfiguration(MutatePublic(logto => logto["UsePkce"] = false)));
        yield return ("Identity: secrets and unknown properties are rejected", () =>
            InvalidConfiguration(MutatePublic(logto => logto["ClientSecret"] = "DO_NOT_ECHO")));
        yield return ("Identity: root secret fields are rejected", () =>
        {
            JsonObject json = JsonNode.Parse(PublicJson)!.AsObject();
            json["ClientSecret"] = "DO_NOT_ECHO";
            InvalidConfiguration(json.ToJsonString());
        });
        yield return ("Identity: duplicate properties cannot override trusted authority", () =>
            InvalidConfiguration(PublicJson.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":1,\"SchemaVersion\":1", StringComparison.Ordinal)));
        yield return ("Identity: nested duplicate properties are rejected", () =>
            InvalidConfiguration(PublicJson.Replace("\"UsePkce\":true", "\"UsePkce\":false,\"UsePkce\":true", StringComparison.Ordinal)));
        yield return ("Identity: unsupported schema is rejected", () =>
            InvalidConfiguration(PublicJson.Replace("\"SchemaVersion\":1", "\"SchemaVersion\":2", StringComparison.Ordinal)));
        yield return ("Identity: missing required fields are rejected", () =>
            InvalidConfiguration(MutatePublic(logto => logto.Remove("ClientId"))));
        yield return ("Identity: default email scope is rejected", () =>
            InvalidConfiguration(MutatePublic(logto => logto["Scopes"] = new JsonArray("openid", "profile", "email"))));
        yield return ("Identity: default refresh scope is rejected", () =>
            InvalidConfiguration(MutatePublic(logto => logto["Scopes"] = new JsonArray("openid", "profile", "offline_access"))));
        yield return ("Identity: duplicate scopes are rejected", () =>
            InvalidConfiguration(MutatePublic(logto => logto["Scopes"] = new JsonArray("openid", "openid"))));
        yield return ("Identity: malformed non-object JSON and excessive content are rejected", () =>
        {
            foreach (string json in new[] { "null", "[]", "{", "{\"SchemaVersion\":\"1\"}", PublicJson + new string(' ', 65536) })
                InvalidConfiguration(json);
        });
        yield return ("Identity: local file loading and absent file errors are safe", () =>
        {
            string path = Path.Combine(Path.GetTempPath(), "autumnos-identity-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                Assert(LogtoConfigurationLoader.Load(path).Issues[0].Code == "AUTH_NOT_CONFIGURED", "Missing file error incorrect.");
                File.WriteAllText(path, PublicJson);
                Assert(LogtoConfigurationLoader.Load(path).IsValid, "File loader failed.");
                File.WriteAllBytes(path, [0xFF, 0xFE, 0xFF]);
                Assert(!LogtoConfigurationLoader.Load(path).IsValid, "Invalid UTF-8 accepted.");
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        });
        yield return ("Discovery: valid metadata retains exact trusted endpoints", () =>
        {
            LogtoDiscoveryResult result = LogtoDiscoveryProbe.ValidateJson(Options(), DiscoveryJson);
            Assert(result.IsValid && result.Document?.Issuer == Options().Authority, "Valid discovery rejected.");
            Assert(result.Document!.EndSessionEndpoint is not null && result.CorrelationId.Length == 32, "Discovery result incomplete.");
            Assert(result.SafeSummary.Contains("不验证"), "Discovery claimed login proof.");
        });
        foreach ((string key, string value) in new (string, string)[]
        {
            ("issuer", "https://identity.example.org/oidc/"),
            ("issuer", "https://attacker.example.org/oidc"),
            ("token_endpoint", "http://identity.example.org/oidc/token"),
            ("authorization_endpoint", "https://attacker.example.org/auth"),
            ("jwks_uri", "https://identity.example.org.evil.example.org/jwks"),
            ("userinfo_endpoint", "https://DO_NOT_ECHO@identity.example.org/oidc/me"),
            ("end_session_endpoint", "https://identity.example.org/logout?token=DO_NOT_ECHO"),
            ("revocation_endpoint", "https://identity.example.org:8443/revoke"),
            ("device_authorization_endpoint", "https://attacker.example.org/device")
        })
        {
            string field = key;
            string replacement = value;
            yield return ($"Discovery: rejects unsafe {key} variant {StableLabel(value)}", () =>
                InvalidDiscovery(MutateDiscovery(json => json[field] = replacement)));
        }
        foreach (string key in new[] { "response_types_supported", "code_challenge_methods_supported",
            "token_endpoint_auth_methods_supported", "id_token_signing_alg_values_supported" })
        {
            string field = key;
            yield return ($"Discovery: requires {key}", () =>
            {
                InvalidDiscovery(MutateDiscovery(json => json.Remove(field)));
                InvalidDiscovery(MutateDiscovery(json => json[field] = new JsonArray("unsupported")));
            });
        }
        yield return ("Discovery: duplicate issuer cannot replace trusted value", () =>
            InvalidDiscovery(DiscoveryJson.Replace("\"issuer\":", "\"issuer\":\"https://attacker.example.org/\",\"issuer\":", StringComparison.Ordinal)));
        yield return ("Discovery: ES384 deployment is accepted without permitting weak algorithms", () =>
        {
            string json = MutateDiscovery(value => value["id_token_signing_alg_values_supported"] = new JsonArray("ES384", "none", "HS256"));
            LogtoDiscoveryResult result = LogtoDiscoveryProbe.ValidateJson(Options(), json);
            Assert(result.IsValid && result.Document!.AllowedIdTokenSigningAlgorithms.SequenceEqual(["ES384"]),
                "Safe asymmetric algorithm selection failed.");
            InvalidDiscovery(MutateDiscovery(value => value["id_token_signing_alg_values_supported"] = new JsonArray("none", "HS256")));
        });
        yield return ("Discovery: missing endpoint fails closed", () =>
            InvalidDiscovery(MutateDiscovery(json => json.Remove("token_endpoint"))));
        yield return ("Discovery: unsupported scope fails clearly", () =>
            InvalidDiscovery(MutateDiscovery(json => json["scopes_supported"] = new JsonArray("openid"))));
        yield return ("Discovery: default sign-in does not require offline access", () =>
        {
            string json = MutateDiscovery(value => value["scopes_supported"] = new JsonArray("openid", "profile"));
            LogtoDiscoveryResult result = LogtoDiscoveryProbe.ValidateJson(Options(), json);
            Assert(result.IsValid, "Missing optional offline access must not block the default public-client flow.");
            Assert(!Options().GetRequestedScopes(false).Contains("offline_access"), "Discovery must not opt the user into persistent sign-in.");
        });
        yield return ("Discovery: omitted optional scope advertisement remains usable", () =>
        {
            string json = MutateDiscovery(value => value.Remove("scopes_supported"));
            Assert(LogtoDiscoveryProbe.ValidateJson(Options(), json).IsValid,
                "An omitted optional scopes_supported field must not be treated as a rejected user grant.");
        });
        yield return ("Discovery: malformed and oversize documents fail safely", () =>
        {
            InvalidDiscovery("[]");
            InvalidDiscovery("DO_NOT_ECHO");
            InvalidDiscovery(DiscoveryJson + new string(' ', 131072));
        });
        yield return ("Discovery: pre-cancelled probe cannot become authentication", () =>
        {
            using CancellationTokenSource cancelled = new();
            cancelled.Cancel();
            LogtoDiscoveryResult result = LogtoDiscoveryProbe.ProbeAsync(Options(), cancelled.Token).GetAwaiter().GetResult();
            Assert(!result.IsValid && result.Issues.Single().Code == "USER_CANCELLED", "Cancellation incorrectly handled.");
        });
        yield return ("Registration: notes never establish authentication", () =>
        {
            RegistrationStatusResult result = RegistrationStatusLoader.Parse(RegistrationJson, "test-public-client");
            Assert(result.IsReadable && result.Status?.ApplicationTypeObserved == "unverified", "Notes not loaded.");
            Assert(!result.Status!.IsAuthenticationEvidence && !result.Status.ProductLoginTestExecuted, "Notes granted authentication.");
            string claimed = RegistrationJson.Replace("\"product_login_test_executed\":false", "\"product_login_test_executed\":true", StringComparison.Ordinal);
            Assert(!RegistrationStatusLoader.Parse(claimed).Status!.IsAuthenticationEvidence, "Local test claim trusted as login.");
        });
        yield return ("Registration: mismatched app and unsafe content are not trusted", () =>
        {
            Assert(!RegistrationStatusLoader.Parse(RegistrationJson, "wrong-client").IsReadable, "Mismatched notes accepted.");
            Assert(!RegistrationStatusLoader.Parse(RegistrationJson.Replace("\"contains_secrets\":false", "\"contains_secrets\":true", StringComparison.Ordinal)).IsReadable, "Secret-bearing notes accepted.");
            Assert(!RegistrationStatusLoader.Parse("DO_NOT_ECHO").IsReadable, "Malformed notes accepted.");
        });
    }

    private static LogtoPublicOptions Options() => LogtoConfigurationLoader.ValidateJson(PublicJson).Options!;

    private static string MutatePublic(Action<JsonObject> mutate)
    {
        JsonObject root = JsonNode.Parse(PublicJson)!.AsObject();
        mutate(root["Logto"]!.AsObject());
        return root.ToJsonString();
    }

    private static string MutateDiscovery(Action<JsonObject> mutate)
    {
        JsonObject root = JsonNode.Parse(DiscoveryJson)!.AsObject();
        mutate(root);
        return root.ToJsonString();
    }

    private static string StableLabel(string text) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text)))[..8];

    private static void InvalidConfiguration(string json)
    {
        LogtoConfigurationResult result = LogtoConfigurationLoader.ValidateJson(json);
        Assert(!result.IsValid && result.Options is null && result.Issues.Count > 0, "Unsafe configuration accepted.");
        Assert(!result.SafeSummary.Contains("DO_NOT_ECHO") && !result.Issues.Any(i => (i.Message + i.Field).Contains("DO_NOT_ECHO")),
            "Configuration echoed sensitive input.");
    }

    private static void InvalidDiscovery(string json)
    {
        LogtoDiscoveryResult result = LogtoDiscoveryProbe.ValidateJson(Options(), json);
        Assert(!result.IsValid && result.Document is null && result.Issues.Count > 0, "Unsafe discovery accepted.");
        Assert(!result.SafeSummary.Contains("DO_NOT_ECHO") && !result.Issues.Any(i => (i.Message + i.Field).Contains("DO_NOT_ECHO")),
            "Discovery echoed sensitive input.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
