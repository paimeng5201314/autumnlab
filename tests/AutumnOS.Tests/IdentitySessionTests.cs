using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Identity;
using Duende.IdentityModel.OidcClient;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace AutumnOS.Tests;

/// <summary>All identities, signing keys and tokens in this file are ephemeral test fixtures, never Logto evidence.</summary>
[SupportedOSPlatform("windows")]
public static class IdentitySessionTests
{
    internal static LogtoPublicOptions Options() => new(new("https://identity.example.org/"), new("https://identity.example.org/oidc"),
        new("https://identity.example.org/oidc/.well-known/openid-configuration"), "fixture-client",
        new("http://127.0.0.1:17853/callback/"), new("http://127.0.0.1:17853/logout-callback/"), ["openid", "profile"], ["offline_access"]);
    private const string State = "independent-transaction-state-32-bytes";
    private const string Nonce = "independent-transaction-nonce-32-bytes";
    private static string Request(string target) => $"GET {target} HTTP/1.1\r\nHost: 127.0.0.1:17853\r\n\r\n";
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("T03 identity fixture: strict callback accepts transaction code only", () =>
        {
            string valid = "/callback/?code=fixture-code&state=" + State;
            Assert(LoopbackCallback.ValidateRequest(Request(valid), State, Options().RedirectUri, false) == valid, "Valid callback rejected.");
        });
        foreach ((string name, string request) in new[]
        {
            ("wrong state", Request("/callback/?code=fixture&state=other")),
            ("wrong path", Request("/callback?code=fixture&state=" + State)),
            ("traversal path", Request("/other/../callback/?code=fixture&state=" + State)),
            ("duplicate state", Request("/callback/?code=fixture&state=" + State + "&state=" + State)),
            ("code plus error", Request("/callback/?code=fixture&error=access_denied&state=" + State)),
            ("arbitrary parameter", Request("/callback/?code=fixture&state=" + State + "&command=run")),
            ("bad escaping", Request("/callback/?code=%GG&state=" + State)),
            ("control character", Request("/callback/?code=%0A&state=" + State)),
            ("POST method", Request("/callback/?code=fixture&state=" + State).Replace("GET ", "POST ")),
            ("foreign host", Request("/callback/?code=fixture&state=" + State).Replace("Host: 127.0.0.1", "Host: attacker.example")),
            ("HTTP body", Request("/callback/?code=fixture&state=" + State).Replace("\r\n\r\n", "\r\nContent-Length: 0\r\n\r\n")),
            ("overlarge request", Request("/callback/?code=" + new string('a', 8192) + "&state=" + State))
        })
            yield return ($"T03 identity fixture: rejects {name}", () => Assert(LoopbackCallback.ValidateRequest(request, State, Options().RedirectUri, false) is null, "Invalid callback accepted."));
        yield return ("T03 identity fixture: real loopback rejects wrong state then consumes valid callback with secret-free page", () => Run(async () =>
        {
            using LoopbackCallback callback = new(Options().RedirectUri);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task<string> receive = callback.ReceiveAsync(State, false, timeout.Token);
            using HttpClient client = new(new HttpClientHandler { UseProxy = false });
            HttpResponseMessage wrong = await client.GetAsync("http://127.0.0.1:17853/callback/?code=fixture-secret&state=wrong", timeout.Token);
            Assert(wrong.StatusCode == HttpStatusCode.BadRequest && !receive.IsCompleted, "Wrong state consumed transaction.");
            string page = await client.GetStringAsync("http://127.0.0.1:17853/callback/?code=fixture-secret&state=" + State, timeout.Token);
            Assert(!page.Contains("fixture-secret") && !page.Contains(State) && page.Contains("查看结果"), "Callback page leaked material or claimed login.");
            Assert((await receive).Contains("code=fixture-secret"), "Callback not delivered internally.");
        }));
        yield return ("T03 identity fixture: occupied port is not killed or replaced", () =>
        {
            using LoopbackCallback owner = new(Options().RedirectUri);
            try { using LoopbackCallback second = new(Options().RedirectUri); throw new Exception("Second listener opened."); }
            catch (IdentityFlowException error) { Assert(error.Code == "AUTH_PORT_IN_USE", "Port failure not distinguished."); }
        });
        yield return ("T03 identity fixture: cancellation and timeout release real loopback port", () => Run(async () =>
        {
            using (LoopbackCallback callback = new(Options().RedirectUri))
            using (CancellationTokenSource timeout = new(TimeSpan.FromMilliseconds(50)))
            { try { await callback.ReceiveAsync(State, false, timeout.Token); throw new Exception("Timeout ignored."); } catch (OperationCanceledException) { } }
            using LoopbackCallback next = new(Options().RedirectUri);
        }));
        foreach (string defect in new[] { "valid", "signature", "issuer", "audience", "nonce", "expired", "subject", "azp", "unsigned" })
            yield return ($"T03 identity JWT fixture: {defect}", () => Run(async () =>
            {
                using RSA signing = RSA.Create(2048);
                using RSA wrong = RSA.Create(2048);
                RsaSecurityKey trusted = new(signing) { KeyId = "ephemeral-test-key" };
                SecurityTokenDescriptor descriptor = new()
                {
                    Issuer = defect == "issuer" ? "https://wrong.example/oidc" : Options().Authority.AbsoluteUri,
                    Audience = defect == "audience" ? "other-client" : Options().ClientId,
                    Subject = new ClaimsIdentity([new("sub", defect == "subject" ? "other-user" : "fixture-user"),
                        new("nonce", defect == "nonce" ? "wrong" : Nonce), new("azp", defect == "azp" ? "other-client" : Options().ClientId)]),
                    IssuedAt = DateTime.UtcNow.AddMinutes(-10), NotBefore = DateTime.UtcNow.AddMinutes(-10),
                    Expires = defect == "expired" ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(5),
                    SigningCredentials = defect == "unsigned" ? null : new SigningCredentials(defect == "signature" ? new RsaSecurityKey(wrong) : trusted, SecurityAlgorithms.RsaSha256)
                };
                string token = new JsonWebTokenHandler().CreateToken(descriptor);
                SignedIdentityTokenValidator validator = new(Options().Authority.AbsoluteUri, Options().ClientId, [trusted], ["RS256"], Nonce, false, "fixture-user");
                var result = await validator.ValidateAsync(token, new OidcClientOptions());
                Assert(result.IsError == (defect != "valid"), "Signature/claim validation result mismatch.");
            }));
        foreach (bool forged in new[] { false, true })
            yield return ($"T03 identity ES384 P-384 fixture: {(forged ? "wrong signature rejected" : "valid ID token accepted")}", () => Run(async () =>
            {
                using ECDsa signing = ECDsa.Create(ECCurve.NamedCurves.nistP384);
                using ECDsa attacker = ECDsa.Create(ECCurve.NamedCurves.nistP384);
                ECDsaSecurityKey trusted = new(signing) { KeyId = "ephemeral-es384-key" };
                string token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
                {
                    Issuer = Options().Authority.AbsoluteUri, Audience = Options().ClientId,
                    Subject = new ClaimsIdentity([new("sub", "fixture-user"), new("nonce", Nonce)]),
                    IssuedAt = DateTime.UtcNow.AddMinutes(-1), NotBefore = DateTime.UtcNow.AddMinutes(-1), Expires = DateTime.UtcNow.AddMinutes(5),
                    SigningCredentials = new SigningCredentials(forged ? new ECDsaSecurityKey(attacker) { KeyId = trusted.KeyId } : trusted, "ES384")
                });
                SignedIdentityTokenValidator validator = new(Options().Authority.AbsoluteUri, Options().ClientId, [trusted], ["ES384"], Nonce, false, "fixture-user");
                var result = await validator.ValidateAsync(token, new OidcClientOptions());
                Assert(result.IsError == forged, "ES384 ID token signature validation mismatch.");
            }));
        yield return ("T03 identity fixture: login cancellation does not authenticate and concurrent calls reuse transaction", () => Run(async () =>
        {
            MemoryVault vault = new(); FixtureFlow flow = new() { HoldSignIn = true };
            using LogtoIdentityService service = new(Options(), vault, flow);
            Task<IdentitySnapshot> first = service.SignInAsync(false);
            await flow.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            IdentitySnapshot second = await service.SignInAsync(true);
            Assert(flow.LoginCount == 1 && second.State == IdentitySessionState.SigningIn, "Second transaction started.");
            service.CancelSignIn();
            IdentitySnapshot result = await first;
            Assert(result.State == IdentitySessionState.SignedOut && result.ErrorCode == "USER_CANCELLED" && vault.Value is null, "Cancellation authenticated/persisted.");
        }));
        yield return ("T03 identity fixture: remember persists verified session without inventing refresh credentials", () => Run(async () =>
        {
            MemoryVault vault = new(); FixtureFlow flow = new() { Next = Session() with { RefreshToken = null } };
            using LogtoIdentityService service = new(Options(), vault, flow);
            IdentitySnapshot result = await service.SignInAsync(true);
            Assert(result.State == IdentitySessionState.SignedIn && !result.CanRefresh && vault.Value?.RefreshToken is null &&
                vault.Value?.AccessToken == flow.Next.AccessToken, "Remembered session missing or refresh credential invented.");
        }));
        yield return ("T03 identity fixture: ignored login cancellation cannot leave SigningIn or authenticate late", () => Run(async () =>
        {
            MemoryVault vault = new(); FixtureFlow flow = new() { IgnoreSignInCancellation = true };
            using LogtoIdentityService service = new(Options(), vault, flow);
            using CancellationTokenSource cancellation = new();
            Task<IdentitySnapshot> login = service.SignInAsync(true, false, cancellation.Token);
            await flow.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            cancellation.Cancel();
            flow.SignInCompleted.SetResult(Session());
            IdentitySnapshot result = await login;
            Assert(result.State == IdentitySessionState.SignedOut && result.ErrorCode == "USER_CANCELLED" && vault.Value is null,
                "Ignored cancellation authenticated or left transaction waiting forever.");
        }));
        foreach (string failure in new[] { "none", "OFFLINE", "SESSION_EXPIRED" })
            yield return ($"T03 identity fixture: encrypted session restoration refresh {failure}", () => Run(async () =>
            {
                MemoryVault vault = new() { Value = Session() with { ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) } };
                FixtureFlow flow = new() { RefreshFailure = failure == "none" ? null : failure };
                using LogtoIdentityService service = new(Options(), vault, flow);
                IdentitySnapshot restored = await service.InitializeAsync();
                IdentitySessionState expected = failure == "none" ? IdentitySessionState.SignedIn : failure == "OFFLINE" ? IdentitySessionState.OfflineCached : IdentitySessionState.SessionExpired;
                Assert(restored.State == expected && flow.RefreshCount == 1 && restored.SessionEpoch == 1,
                    "Restoration did not distinguish actual refresh, offline and invalid grant.");
                Assert((vault.Value is null) == (failure == "SESSION_EXPIRED"), "Wrong restored credential retention.");
                await service.InitializeAsync();
                Assert(flow.RefreshCount == 1, "Initialization was not idempotent.");
            }));
        yield return ("T03 identity fixture: malformed restored fields fail closed without null crash", () => Run(async () =>
        {
            foreach (ProtectedIdentitySession malformed in new[] { Session() with { Subject = null! }, Session() with { AccessToken = null! },
                Session() with { IdentityToken = null! }, Session() with { Nonce = null! }, Session() with { AvatarUrl = "file:///private" },
                Session() with { DisplayName = "unsafe\nname" }, Session() with { Issuer = "https://other.example/oidc" } })
            {
                MemoryVault vault = new() { Value = malformed }; FixtureFlow flow = new();
                using LogtoIdentityService service = new(Options(), vault, flow);
                IdentitySnapshot result = await service.InitializeAsync();
                Assert(result.State == IdentitySessionState.SessionExpired && result.ErrorCode == "AUTH_CREDENTIALS_UNREADABLE" && flow.RefreshCount == 0,
                    "Malformed credential became an identity or crashed validation.");
            }
        }));
        yield return ("T03 identity fixture: late refresh after logout cannot restore session or credentials", () => Run(async () =>
        {
            MemoryVault vault = new(); FixtureFlow flow = new() { HoldRefresh = true };
            using LogtoIdentityService service = new(Options(), vault, flow);
            await service.SignInAsync(true);
            long epoch = service.Snapshot.SessionEpoch;
            Task<IdentitySnapshot> refreshing = service.RefreshAsync();
            await flow.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await service.SignOutAsync();
            flow.RefreshCompleted.SetResult(Session() with { RefreshToken = "fixture-late-refresh" });
            await refreshing;
            Assert(service.Snapshot.SessionEpoch > epoch && service.Snapshot.State == IdentitySessionState.SignedOut && vault.Value is null, "Late refresh restored old login.");
        }));
        yield return ("T03 identity fixture: invalid grant expires session while offline retains marked cached profile", () => Run(async () =>
        {
            foreach (string failure in new[] { "SESSION_EXPIRED", "OFFLINE" })
            {
                MemoryVault vault = new(); FixtureFlow flow = new() { RefreshFailure = failure };
                using LogtoIdentityService service = new(Options(), vault, flow);
                await service.SignInAsync(true);
                IdentitySnapshot refreshed = await service.RefreshAsync();
                Assert(refreshed.State == (failure == "OFFLINE" ? IdentitySessionState.OfflineCached : IdentitySessionState.SessionExpired), "Failure kinds conflated.");
                Assert((vault.Value is null) == (failure != "OFFLINE"), "Refresh credentials not appropriately retained/cleared.");
            }
        }));
        yield return ("T03 identity fixture: late A refresh cannot contaminate newly signed in B", () => Run(async () =>
        {
            MemoryVault vault = new(); FixtureFlow flow = new() { HoldRefresh = true };
            using LogtoIdentityService service = new(Options(), vault, flow);
            await service.SignInAsync(true);
            Task<IdentitySnapshot> refreshA = service.RefreshAsync();
            await flow.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
            flow.Next = Session() with { Subject = "fixture-user-b", DisplayName = "Fixture B", RefreshToken = "fixture-refresh-b" };
            IdentitySnapshot b = await service.SignInAsync(true, true);
            flow.RefreshCompleted.SetResult(Session() with { RefreshToken = "fixture-late-a" });
            await refreshA;
            Assert(service.Snapshot.AccountNamespace == b.AccountNamespace && service.Snapshot.SessionEpoch == b.SessionEpoch && vault.Value?.Subject == "fixture-user-b" &&
                vault.Value?.RefreshToken == "fixture-refresh-b", "Late A refresh polluted B snapshot or credentials.");
        }));
        yield return ("T03 identity fixture: concurrent refreshes are serialized", () => Run(async () =>
        {
            FixtureFlow flow = new(); using LogtoIdentityService service = new(Options(), new MemoryVault(), flow);
            await service.SignInAsync(true);
            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => service.RefreshAsync()));
            Assert(flow.PeakRefresh == 1 && flow.RefreshCount == 8, "Refresh calls raced.");
        }));
        yield return ("T03 identity fixture: browser logout failure still leaves local logout and preserves saves", () => Run(async () =>
        {
            MemoryVault vault = new(); FixtureFlow flow = new() { LogoutFailure = true };
            using LogtoIdentityService service = new(Options(), vault, flow);
            await service.SignInAsync(true);
            IdentitySnapshot result = await service.SignOutAsync(true);
            Assert(result.State == IdentitySessionState.SignedOut && result.ErrorCode == "AUTH_BROWSER_LOGOUT_INCOMPLETE" && vault.Value is null, "Remote logout failure restored local credentials.");
        }));
        yield return ("T03 identity fixture: account namespace stable by issuer and subject without nickname", () =>
        {
            string a = LogtoIdentityService.AccountNamespaceFor("issuer-a", "user-a");
            Assert(a == LogtoIdentityService.AccountNamespaceFor("issuer-a", "user-a") && a != LogtoIdentityService.AccountNamespaceFor("issuer-a", "user-b") &&
                a != LogtoIdentityService.AccountNamespaceFor("issuer-b", "user-a") && !a.Contains("user-a"), "Namespace isolation failed.");
        });
        yield return ("T03 identity fixture: final commit lease linearizes account logout and rejects stale epoch", () =>
        {
            using LogtoIdentityService service = new(Options(), new MemoryVault(), new FixtureFlow());
            service.SignInAsync(false).GetAwaiter().GetResult();
            IdentitySnapshot initial = service.Snapshot;
            using ManualResetEventSlim started = new();
            Task<IdentitySnapshot> logout;
            using (service.EnterSessionLease(initial.AccountNamespace, initial.SessionEpoch))
            {
                logout = Task.Run(() => { started.Set(); return service.SignOutAsync(); });
                Assert(started.Wait(TimeSpan.FromSeconds(2)), "Logout worker did not start.");
                Assert(!logout.Wait(TimeSpan.FromMilliseconds(50)), "Logout changed account during final commit.");
            }
            logout.GetAwaiter().GetResult();
            try { using IDisposable stale = service.EnterSessionLease(initial.AccountNamespace, initial.SessionEpoch); throw new Exception("Stale lease accepted."); }
            catch (RuntimeCapabilityException error) { Assert(error.Code == "SESSION_EXPIRED", "Wrong lease mismatch result."); }
        });
        yield return ("T03 identity Windows fixture: DPAPI encrypted atomic replacement and logout leaves unrelated saves", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "autumnos-identity-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                File.WriteAllText(Path.Combine(root, "unrelated-save.txt"), "preserve-fixture-save");
                WindowsCredentialVault vault = new(root, Options());
                vault.Write(Session());
                byte[] encrypted = File.ReadAllBytes(Path.Combine(root, "Identity", "session.dpapi"));
                Assert(!Encoding.UTF8.GetString(encrypted).Contains("fixture-refresh"), "Plaintext credential reached disk.");
                Assert(vault.Read()?.RefreshToken == "fixture-refresh", "DPAPI roundtrip failed.");
                vault.Write(Session() with { RefreshToken = "fixture-rotated" });
                Assert(vault.Read()?.RefreshToken == "fixture-rotated", "Atomic credential rotation failed.");
                vault.Clear();
                Assert(vault.Read() is null && File.ReadAllText(Path.Combine(root, "unrelated-save.txt")) == "preserve-fixture-save", "Logout deleted unrelated save.");
            }
            finally { Directory.Delete(root, true); }
        });
        yield return ("T03 identity Windows fixture: damaged credential fails closed without deleting saves", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "autumnos-identity-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "Identity"));
            try
            {
                File.WriteAllBytes(Path.Combine(root, "Identity", "session.dpapi"), RandomNumberGenerator.GetBytes(64));
                WindowsCredentialVault vault = new(root, Options());
                try { vault.Read(); throw new Exception("Damaged DPAPI data accepted."); }
                catch (IdentityFlowException error) { Assert(error.Code == "AUTH_CREDENTIALS_UNREADABLE", "Wrong unreadable state."); }
                Assert(File.Exists(Path.Combine(root, "Identity", "session.dpapi")), "Unreadable credential silently deleted.");
            }
            finally { Directory.Delete(root, true); }
        });
        yield return ("T03 identity Windows fixture: logout tombstone prevents restore if encrypted file deletion is interrupted", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "autumnos-identity-fixture-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                WindowsCredentialVault vault = new(root, Options());
                vault.Write(Session());
                using (FileStream held = new(Path.Combine(root, "Identity", "session.dpapi"), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { vault.Clear(); throw new Exception("Fixture lock did not block encrypted file deletion."); }
                    catch (IOException) { }
                    Assert(vault.Read() is null, "Surviving encrypted file restored after logout marker.");
                }
                vault.Write(Session() with { RefreshToken = "fixture-new-login" });
                Assert(vault.Read()?.RefreshToken == "fixture-new-login", "New successful login did not replace tombstone.");
                vault.Clear();
            }
            finally { Directory.Delete(root, true); }
        });
        foreach (var test in IdentityPersistenceTests.Cases()) yield return test;
        foreach (var test in CallbackResponseTests.Cases()) yield return test;
    }
    internal static ProtectedIdentitySession Session() => new(1, Options().Authority.AbsoluteUri, Options().ClientId, "fixture-user", "Fixture 派蒙", null,
        "fixture-access", "fixture-refresh", "fixture-identity-token", DateTimeOffset.UtcNow.AddMinutes(10), Nonce);
    private static void Run(Func<Task> test) => test().GetAwaiter().GetResult();
    private static void Assert(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
    private sealed class MemoryVault : ICredentialVault
    {
        public ProtectedIdentitySession? Value;
        public ProtectedIdentitySession? Read() => Value;
        public void Write(ProtectedIdentitySession session) => Value = session;
        public void Clear() => Value = null;
    }
    private sealed class FixtureFlow : INativeOidcFlow
    {
        public ProtectedIdentitySession Next = Session();
        public bool HoldSignIn, HoldRefresh, LogoutFailure, IgnoreSignInCancellation;
        public string? RefreshFailure;
        public int LoginCount, RefreshCount, PeakRefresh, ActiveRefresh;
        public TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProtectedIdentitySession> SignInCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource RefreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ProtectedIdentitySession> RefreshCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProtectedIdentitySession> SignInAsync(bool remember, bool reauthenticate, CancellationToken cancellationToken)
        { LoginCount++; Started.TrySetResult(); if (IgnoreSignInCancellation) return await SignInCompleted.Task; if (HoldSignIn) await Task.Delay(Timeout.Infinite, cancellationToken); return Next; }
        public async Task<ProtectedIdentitySession> RefreshAsync(ProtectedIdentitySession session, CancellationToken cancellationToken)
        {
            RefreshCount++; PeakRefresh = Math.Max(PeakRefresh, Interlocked.Increment(ref ActiveRefresh));
            RefreshStarted.TrySetResult();
            try
            {
                if (HoldRefresh) return await RefreshCompleted.Task; // Deliberately ignores cancellation to challenge commit-time epoch validation.
                await Task.Delay(10, cancellationToken);
                if (RefreshFailure is not null) throw new IdentityFlowException(RefreshFailure);
                return session with { RefreshToken = session.RefreshToken is null ? null : session.RefreshToken + "-rotated",
                    ExpiresAt = session.RefreshToken is null ? session.ExpiresAt : DateTimeOffset.UtcNow.AddMinutes(10) };
            }
            finally { Interlocked.Decrement(ref ActiveRefresh); }
        }
        public Task SignOutBrowserAsync(ProtectedIdentitySession session, CancellationToken cancellationToken) =>
            LogoutFailure ? Task.FromException(new IdentityFlowException("OFFLINE")) : Task.CompletedTask;
    }
}
