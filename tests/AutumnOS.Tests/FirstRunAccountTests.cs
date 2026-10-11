using AutumnOS.Contracts;
using AutumnOS.Identity;
using AutumnOS.Storage;

namespace AutumnOS.Tests;

// Only the injected in-memory credential vault and synthetic protocol are used here; no DPAPI,
// browser, real network, user credential, or platform-specific identity API is exercised.
#pragma warning disable CA1416
public static class FirstRunAccountTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return Case("onboarding.decision_required_before_completion", async (root, store) =>
        {
            Check(store.Load().State is { IsComplete: false, LoginStep: FirstRunLoginStep.Pending }, "New install invented a completed step.");
            Check(!store.RecordLoginStep(FirstRunLoginStep.Skipped).Success, "Decision accepted before login stage.");
            EnterLogin(store);
            Check(!store.Advance(FirstRunCheckpoint.Completed).Success, "Configuration finished before explicit decision.");
            await Task.CompletedTask;
        });
        yield return Case("onboarding.login_success_does_not_finish_configuration", async (_, store) =>
        {
            EnterLogin(store);
            var vault = new MemoryVault(); var flow = new FixtureFlow();
            using var identity = new LogtoIdentityService(Options(), vault, flow);
            using var onboarding = new FirstRunAccountCoordinator(store, identity);
            var result = await onboarding.SignInAsync();
            Check(result.State is { IsComplete: false, LoginStep: FirstRunLoginStep.Completed }, "Verified login did not record an independent step.");
            Check(identity.Snapshot.State == IdentitySessionState.SignedIn && vault.Value is not null, "Verified session was not saved in the credential vault.");
            Check(store.Advance(FirstRunCheckpoint.Completed).Success, "Final configuration completion failed.");
        });
        yield return Case("onboarding.skip_survives_restart_without_login", async (root, store) =>
        {
            EnterLogin(store);
            using var onboarding = new FirstRunAccountCoordinator(store, null);
            Check((await onboarding.SkipAsync()).State is { IsComplete: false, LoginStep: FirstRunLoginStep.Skipped }, "Skip marked all configuration complete.");
            Check(store.Advance(FirstRunCheckpoint.Completed).Success, "Skipped configuration could not finish.");
            Check(new FirstRunStateStore(root).Load().State is { IsComplete: true, LoginStep: FirstRunLoginStep.Skipped }, "Restart lost completed skip.");
        });
        yield return Case("onboarding.legacy_completed_users_do_not_repeat_welcome", async (_, store) =>
        {
            WriteLegacy(store, "Completed");
            string previous = File.ReadAllText(store.StateFilePath);
            Check(store.Load().State is { IsComplete: true, LoginStep: FirstRunLoginStep.NotOffered }, "Old completed user became first-run or signed in.");
            Check(store.Advance(FirstRunCheckpoint.Completed).Success && File.ReadAllText(store.StateFilePath) == previous,
                "Idempotent read/complete rewrote legacy durable record.");
            await Task.CompletedTask;
        });
        yield return Case("onboarding.legacy_interrupted_migrates_on_next_decision", async (_, store) =>
        {
            WriteLegacy(store, "Initializing");
            Check(store.Load().State is { IsComplete: false, LoginStep: FirstRunLoginStep.Pending }, "Interrupted legacy setup lost pending step.");
            Check(store.RecordLoginStep(FirstRunLoginStep.Skipped).Success && File.ReadAllText(store.StateFilePath).Contains("\"schemaVersion\": 2"), "Next commit did not atomically migrate schema.");
            await Task.CompletedTask;
        });
        foreach (var (checkpoint, step) in new[] { ("Completed", "Pending"), ("Initializing", "NotOffered"), ("Hello", "Skipped"), ("Initializing", "Unknown") })
            yield return Case("onboarding.rejects_inconsistent_v2." + checkpoint + "." + step, async (_, store) =>
            {
                string invalid = "{\"schemaVersion\":2,\"checkpoint\":\"" + checkpoint + "\",\"revision\":4,\"updatedUtc\":\"2026-01-01T00:00:00Z\",\"loginStep\":\"" + step + "\"}";
                File.WriteAllText(store.StateFilePath, invalid);
                Check(!store.Load().Success && !store.RecordLoginStep(FirstRunLoginStep.Skipped).Success && File.ReadAllText(store.StateFilePath) == invalid,
                    "Inconsistent onboarding record accepted or overwritten.");
                await Task.CompletedTask;
            });
        yield return Case("onboarding.duplicate_login_coalesces_and_skip_rejects_late_callback", async (_, store) =>
        {
            EnterLogin(store);
            var vault = new MemoryVault(); var flow = new FixtureFlow { HoldLogin = true };
            using var identity = new LogtoIdentityService(Options(), vault, flow);
            using var onboarding = new FirstRunAccountCoordinator(store, identity);
            var login = onboarding.SignInAsync();
            await flow.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(ReferenceEquals(login, onboarding.SignInAsync()) && flow.LoginCount == 1, "Repeated click created a second browser transaction.");
            Check((await onboarding.SkipAsync()).State?.LoginStep == FirstRunLoginStep.Skipped, "Skip did not save user choice.");
            flow.LoginCompleted.TrySetResult(Session()); // Deliberately ignores cancellation.
            await login;
            Check(store.Load().State?.LoginStep == FirstRunLoginStep.Skipped && identity.Snapshot.State == IdentitySessionState.SignedOut && vault.Value is null,
                "Late callback overwrote skip or persisted credentials.");
        });
        yield return Case("onboarding.cancel_then_skip", async (_, store) =>
        {
            EnterLogin(store);
            var flow = new FixtureFlow { HoldLogin = true };
            using var identity = new LogtoIdentityService(Options(), new MemoryVault(), flow);
            using var onboarding = new FirstRunAccountCoordinator(store, identity);
            var login = onboarding.SignInAsync(); await flow.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            onboarding.CancelSignIn(); flow.LoginCompleted.TrySetResult(Session()); await login;
            Check(store.Load().State?.LoginStep == FirstRunLoginStep.Pending && identity.Snapshot.State == IdentitySessionState.SignedOut, "Cancellation became successful login.");
            Check((await onboarding.SkipAsync()).Success, "Cancellation blocked skipping.");
        });
        foreach (var failure in new[] { "OFFLINE", "AUTH_TIMEOUT", "USER_CANCELLED", "AUTH_INVALID_TOKEN" })
            yield return Case("onboarding.login_failure_allows_skip." + failure, async (_, store) =>
            {
                EnterLogin(store);
                var flow = new FixtureFlow { LoginFailure = failure };
                using var identity = new LogtoIdentityService(Options(), new MemoryVault(), flow);
                using var onboarding = new FirstRunAccountCoordinator(store, identity);
                await onboarding.SignInAsync();
                Check(store.Load().State?.LoginStep == FirstRunLoginStep.Pending, "Failed login changed first-run decision.");
                Check((await onboarding.SkipAsync()).Success, "Failed login blocked skipping.");
            });
        yield return Case("onboarding.failed_choice_save_keeps_real_pending_state", async (_, store) =>
        {
            EnterLogin(store);
            using var onboarding = new FirstRunAccountCoordinator(store, null);
            File.WriteAllText(store.StateFilePath, "{broken");
            var result = await onboarding.SkipAsync();
            Check(!result.Success && File.ReadAllText(store.StateFilePath) == "{broken", "Save error was hidden or damaged state overwritten.");
        });
        yield return Case("onboarding.logout_preserves_completion_and_restart_stays_guest", async (root, store) =>
        {
            EnterLogin(store);
            var vault = new MemoryVault();
            using (var identity = new LogtoIdentityService(Options(), vault, new FixtureFlow()))
            using (var onboarding = new FirstRunAccountCoordinator(store, identity))
            {
                await onboarding.SignInAsync(); Check(store.Advance(FirstRunCheckpoint.Completed).Success, "Completion failed.");
                await identity.SignOutAsync();
            }
            using var next = new LogtoIdentityService(Options(), vault, new FixtureFlow());
            Check((await next.InitializeAsync()).State == IdentitySessionState.SignedOut && new FirstRunStateStore(root).Load().State!.IsComplete,
                "Logout restored an old session or reset first-run completion.");
        });
        yield return Case("identity.local_restore_ready_before_network_and_offline_keeps_vault", async (_, _) =>
        {
            var vault = new MemoryVault { Value = Session() }; var flow = new FixtureFlow { HoldRefresh = true };
            using var identity = new LogtoIdentityService(Options(), vault, flow);
            var initialization = identity.InitializeAsync();
            var local = await identity.LocalSessionReady.WaitAsync(TimeSpan.FromSeconds(5));
            await flow.RefreshStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Check(local.State == IdentitySessionState.OfflineCached && !initialization.IsCompleted, "Startup waited for network or invented verified login.");
            flow.RefreshCompleted.TrySetException(new IdentityFlowException("OFFLINE"));
            Check((await initialization).State == IdentitySessionState.OfflineCached && vault.Value is not null, "Temporary offline erased protected session.");
            using var online = new LogtoIdentityService(Options(), vault, new FixtureFlow());
            Check((await online.InitializeAsync()).State == IdentitySessionState.SignedIn, "Session did not recover when online.");
        });
        yield return Case("identity.revoked_session_clears_vault_without_affecting_onboarding", async (_, store) =>
        {
            EnterLogin(store); store.RecordLoginStep(FirstRunLoginStep.Skipped); store.Advance(FirstRunCheckpoint.Completed);
            var vault = new MemoryVault { Value = Session() };
            using var identity = new LogtoIdentityService(Options(), vault, new FixtureFlow { RefreshFailure = "SESSION_EXPIRED" });
            Check((await identity.InitializeAsync()).State == IdentitySessionState.SessionExpired && vault.Value is null && store.Load().State!.IsComplete,
                "Revocation left recoverable credentials or changed first-run.");
        });
        yield return Case("onboarding.close_rejects_late_callback_without_finish", async (_, store) =>
        {
            EnterLogin(store);
            var vault = new MemoryVault(); var flow = new FixtureFlow { HoldLogin = true };
            using var identity = new LogtoIdentityService(Options(), vault, flow);
            using var onboarding = new FirstRunAccountCoordinator(store, identity);
            var login = onboarding.SignInAsync(); await flow.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            onboarding.Dispose(); identity.Dispose(); flow.LoginCompleted.TrySetResult(Session()); await login;
            Check(vault.Value is null && store.Load().State is { IsComplete: false, LoginStep: FirstRunLoginStep.Pending }, "Closed window accepted late credentials or completed setup.");
        });
    }

    private static (string, Action) Case(string name, Func<InstallationRoot, FirstRunStateStore, Task> body) => (name, () =>
    {
        string path = Path.Combine(Path.GetTempPath(), "autumnos-first-run-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            var root = new InstallationRoot(path);
            Check(root.EnsureCreated().Success, "Fixture data root failed.");
            body(root, new FirstRunStateStore(root)).GetAwaiter().GetResult();
        }
        finally { Directory.Delete(path, true); }
    });
    private static void EnterLogin(FirstRunStateStore store)
    {
        Check(store.Advance(FirstRunCheckpoint.Brand).Success && store.Advance(FirstRunCheckpoint.Initializing).Success, "Could not reach account step.");
    }
    private static void WriteLegacy(FirstRunStateStore store, string checkpoint) => File.WriteAllText(store.StateFilePath,
        "{\"schemaVersion\":1,\"checkpoint\":\"" + checkpoint + "\",\"revision\":4,\"updatedUtc\":\"2026-01-01T00:00:00Z\"}");
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static LogtoPublicOptions Options() => new(new("https://identity.example.org/"), new("https://identity.example.org/oidc"),
        new("https://identity.example.org/oidc/.well-known/openid-configuration"), "fixture-client", new("http://127.0.0.1:17853/callback/"),
        new("http://127.0.0.1:17853/logout-callback/"), ["openid", "profile"], ["offline_access"]);
    private static ProtectedIdentitySession Session() => new(1, Options().Authority.AbsoluteUri, Options().ClientId,
        "fixture-first-run", "测试用户", null, "fixture-access", "fixture-refresh", "fixture-identity", DateTimeOffset.UtcNow.AddMinutes(10), "fixture-nonce-32-characters-long-value");
    private sealed class MemoryVault : ICredentialVault
    {
        internal ProtectedIdentitySession? Value;
        public ProtectedIdentitySession? Read() => Value;
        public void Write(ProtectedIdentitySession value) => Value = value;
        public void Clear() => Value = null;
    }
    private sealed class FixtureFlow : INativeOidcFlow
    {
        internal bool HoldLogin, HoldRefresh;
        internal string? LoginFailure, RefreshFailure;
        internal int LoginCount;
        internal readonly TaskCompletionSource LoginStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource RefreshStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<ProtectedIdentitySession> LoginCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<ProtectedIdentitySession> RefreshCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ProtectedIdentitySession> SignInAsync(bool remember, bool reauthenticate, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref LoginCount); LoginStarted.TrySetResult();
            if (LoginFailure is not null) throw new IdentityFlowException(LoginFailure);
            return HoldLogin ? await LoginCompleted.Task : Session();
        }
        public async Task<ProtectedIdentitySession> RefreshAsync(ProtectedIdentitySession session, CancellationToken cancellationToken)
        {
            RefreshStarted.TrySetResult();
            if (RefreshFailure is not null) throw new IdentityFlowException(RefreshFailure);
            return HoldRefresh ? await RefreshCompleted.Task : session with { RefreshToken = "fixture-rotated" };
        }
        public Task SignOutBrowserAsync(ProtectedIdentitySession session, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
#pragma warning restore CA1416
