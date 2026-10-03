using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using AutumnOS.Contracts;
using AutumnOS.Identity;

namespace AutumnOS.Tests;

/// <summary>Real CurrentUser DPAPI with explicit synthetic verified-session fixtures; not a real Logto authentication report.</summary>
[SupportedOSPlatform("windows")]
public static class IdentityPersistenceTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("T03 persistence Windows NTFS: inherited Modify owner can enforce private DACL without WRITE_OWNER", () => Run(() => WithDirectory(async root =>
        {
            using WindowsIdentity currentUser = WindowsIdentity.GetCurrent();
            SecurityIdentifier sid = currentUser.User!;
            DirectorySecurity parentAcl = new();
            parentAcl.SetAccessRuleProtection(true, false);
            parentAcl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(root).SetAccessControl(parentAcl);
            string identityDirectory = Path.Combine(root, "Identity"); Directory.CreateDirectory(identityDirectory);
            DirectorySecurity inherited = new DirectoryInfo(identityDirectory).GetAccessControl();
            Check(sid.Equals(inherited.GetOwner(typeof(SecurityIdentifier))) && !inherited.AreAccessRulesProtected);
            var options = IdentitySessionTests.Options();
            using (var service = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), new PersistenceFlow(IdentitySessionTests.Session())))
            {
                IdentitySnapshot signedIn = await service.SignInAsync(true);
                Check(signedIn.State == IdentitySessionState.SignedIn && signedIn.ErrorCode is null && File.Exists(Credential(root)));
                DirectorySecurity actualDirectory = new DirectoryInfo(identityDirectory).GetAccessControl();
                FileSecurity actualFile = new FileInfo(Credential(root)).GetAccessControl();
                foreach (FileSystemSecurity acl in new FileSystemSecurity[] { actualDirectory, actualFile })
                {
                    var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
                    Check(acl.AreAccessRulesProtected && sid.Equals(acl.GetOwner(typeof(SecurityIdentifier))) && rules.Length == 1 &&
                        !rules[0].IsInherited && rules[0].IdentityReference.Equals(sid) && rules[0].AccessControlType == AccessControlType.Allow &&
                        (rules[0].FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl);
                }
            }
            using var reopened = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), new PersistenceFlow(IdentitySessionTests.Session()));
            Check((await reopened.InitializeAsync()).State == IdentitySessionState.SignedIn);
            Check((await reopened.SignOutAsync()).State == IdentitySessionState.SignedOut && !File.Exists(Credential(root)));
        })));
        foreach (bool refreshable in new[] { true, false })
            yield return ($"T03 persistence Windows DPAPI: close and new service restore {(refreshable ? "with" : "without")} refresh token", () => Run(() => WithDirectory(async root =>
            {
                var options = IdentitySessionTests.Options();
                ProtectedIdentitySession session = IdentitySessionTests.Session() with { RefreshToken = refreshable ? "fixture-persistent-refresh" : null };
                var vault = new WindowsCredentialVault(root, options);
                var firstFlow = new PersistenceFlow(session);
                string sentinel = Path.Combine(root, "game-save-sentinel.txt"); File.WriteAllText(sentinel, "keep-existing-save");
                string account;
                using (var first = new LogtoIdentityService(options, vault, firstFlow))
                {
                    IdentitySnapshot signedIn = await first.SignInAsync(true);
                    account = signedIn.AccountNamespace;
                    Check(signedIn.State == IdentitySessionState.SignedIn && signedIn.RememberSignIn && signedIn.CanRefresh == refreshable);
                    byte[] ciphertext = File.ReadAllBytes(Credential(root));
                    Check(!Encoding.UTF8.GetString(ciphertext).Contains(session.AccessToken, StringComparison.Ordinal));
                    Check(!Encoding.UTF8.GetString(ciphertext).Contains(session.IdentityToken, StringComparison.Ordinal));
                }
                Check(File.Exists(Credential(root)) && File.ReadAllText(sentinel) == "keep-existing-save");
                var restoredFlow = new PersistenceFlow(session);
                using var restored = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), restoredFlow);
                IdentitySnapshot result = await restored.InitializeAsync();
                Check(result.State == IdentitySessionState.SignedIn && result.AccountNamespace == account && result.RememberSignIn && result.CanRefresh == refreshable);
                Check(restoredFlow.LoginCount == 0 && restoredFlow.RefreshCount == 1);
                if (!refreshable) Check(vault.Read()?.RefreshToken is null && vault.Read()?.ExpiresAt == session.ExpiresAt);
            })));

        yield return ("T03 persistence Windows DPAPI: enabling after session-only login saves immediately without login or epoch change", () => Run(() => WithDirectory(async root =>
        {
            var options = IdentitySessionTests.Options();
            var session = IdentitySessionTests.Session() with { RefreshToken = null };
            var flow = new PersistenceFlow(session);
            using (var service = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), flow))
            {
                IdentitySnapshot original = await service.SignInAsync(false);
                Check(!File.Exists(Credential(root)) && !original.RememberSignIn && !flow.LastRemember);
                IdentitySnapshot enabled = await service.SetRememberSignInAsync(true);
                Check(enabled.RememberSignIn && !enabled.CanRefresh && enabled.SessionEpoch == original.SessionEpoch && enabled.AccountNamespace == original.AccountNamespace);
                Check(File.Exists(Credential(root)) && flow.LoginCount == 1 && flow.RefreshCount == 0 && !flow.LastRemember);
            }
            using var next = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), new PersistenceFlow(session));
            Check((await next.InitializeAsync()).State == IdentitySessionState.SignedIn);
        })));

        yield return ("T03 persistence Windows DPAPI: disabling persistence keeps current account but prevents restart restore", () => Run(() => WithDirectory(async root =>
        {
            var options = IdentitySessionTests.Options(); var session = IdentitySessionTests.Session();
            using (var service = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), new PersistenceFlow(session)))
            {
                IdentitySnapshot signedIn = await service.SignInAsync(true);
                IdentitySnapshot disabled = await service.SetRememberSignInAsync(false);
                Check(disabled.State == IdentitySessionState.SignedIn && !disabled.RememberSignIn && disabled.SessionEpoch == signedIn.SessionEpoch && disabled.AccountNamespace == signedIn.AccountNamespace);
                Check(!File.Exists(Credential(root)) && File.Exists(Path.Combine(root, "Identity", "signed-out.marker")));
            }
            var flow = new PersistenceFlow(session);
            using var next = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), flow);
            Check((await next.InitializeAsync()).State == IdentitySessionState.SignedOut && flow.RefreshCount == 0);
        })));

        yield return ("T03 persistence Windows DPAPI: valid non-refreshable session restores as offline cache and survives another restart", () => Run(() => WithDirectory(async root =>
        {
            var options = IdentitySessionTests.Options(); var session = IdentitySessionTests.Session() with { RefreshToken = null };
            using (var first = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), new PersistenceFlow(session)))
                await first.SignInAsync(true);
            byte[] persisted = File.ReadAllBytes(Credential(root));
            using (var offline = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), new PersistenceFlow(session) { Failure = "OFFLINE" }))
            {
                IdentitySnapshot state = await offline.InitializeAsync();
                Check(state.State == IdentitySessionState.OfflineCached && state.RememberSignIn && !state.CanRefresh && state.ErrorCode == "OFFLINE");
                Check(File.ReadAllBytes(Credential(root)).SequenceEqual(persisted));
            }
            using var online = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), new PersistenceFlow(session));
            Check((await online.InitializeAsync()).State == IdentitySessionState.SignedIn && !online.Snapshot.CanRefresh);
        })));

        yield return ("T03 persistence Windows DPAPI: explicit logout remains logged out after close and reopen", () => Run(() => WithDirectory(async root =>
        {
            var options = IdentitySessionTests.Options(); var session = IdentitySessionTests.Session() with { RefreshToken = null };
            using (var service = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), new PersistenceFlow(session)))
            {
                await service.SignInAsync(true);
                Check((await service.SignOutAsync()).State == IdentitySessionState.SignedOut && !File.Exists(Credential(root)));
            }
            using var reopened = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), new PersistenceFlow(session));
            Check((await reopened.InitializeAsync()).State == IdentitySessionState.SignedOut);
        })));

        foreach (bool enableDuringRefresh in new[] { false, true })
            yield return ($"T03 persistence Windows DPAPI: late refresh respects {(enableDuringRefresh ? "enabled" : "disabled")} persistence", () => Run(() => WithDirectory(async root =>
            {
                var options = IdentitySessionTests.Options(); var session = IdentitySessionTests.Session();
                var vault = new WindowsCredentialVault(root, options);
                var flow = new PersistenceFlow(session) { Hold = true };
                using (var service = new LogtoIdentityService(options, vault, flow))
                {
                    await service.SignInAsync(!enableDuringRefresh);
                    long epoch = service.Snapshot.SessionEpoch;
                    Task<IdentitySnapshot> refresh = service.RefreshAsync();
                    await flow.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    await service.SetRememberSignInAsync(enableDuringRefresh);
                    flow.Completed.TrySetResult(session with { RefreshToken = "fixture-rotated-after-choice" });
                    IdentitySnapshot result = await refresh;
                    Check(result.RememberSignIn == enableDuringRefresh && result.SessionEpoch == epoch && result.State == IdentitySessionState.SignedIn);
                    Check(enableDuringRefresh ? vault.Read()?.RefreshToken == "fixture-rotated-after-choice" : vault.Read() is null && !File.Exists(Credential(root)));
                }
                using var next = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), new PersistenceFlow(session));
                Check((await next.InitializeAsync()).State == (enableDuringRefresh ? IdentitySessionState.SignedIn : IdentitySessionState.SignedOut));
            })));

        yield return ("T03 persistence Windows DPAPI: late refresh after explicit logout cannot resurrect next service", () => Run(() => WithDirectory(async root =>
        {
            var options = IdentitySessionTests.Options(); var session = IdentitySessionTests.Session();
            var flow = new PersistenceFlow(session) { Hold = true };
            using (var service = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), flow))
            {
                await service.SignInAsync(true);
                Task<IdentitySnapshot> refresh = service.RefreshAsync();
                await flow.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                await service.SignOutAsync();
                flow.Completed.TrySetResult(session with { RefreshToken = "fixture-late-after-logout" });
                await refresh;
                Check(service.Snapshot.State == IdentitySessionState.SignedOut && !File.Exists(Credential(root)));
            }
            using var next = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), new PersistenceFlow(session));
            Check((await next.InitializeAsync()).State == IdentitySessionState.SignedOut);
        })));

        yield return ("T03 persistence Windows DPAPI: cancellation and write failure do not falsely change persistence", () => Run(() => WithDirectory(async root =>
        {
            var options = IdentitySessionTests.Options(); var session = IdentitySessionTests.Session();
            using var service = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), new PersistenceFlow(session));
            IdentitySnapshot signedIn = await service.SignInAsync(true);
            byte[] original = File.ReadAllBytes(Credential(root));
            using CancellationTokenSource cancelled = new(); cancelled.Cancel();
            try { await service.SetRememberSignInAsync(false, cancelled.Token); throw new InvalidOperationException("Cancelled persistence change accepted."); }
            catch (OperationCanceledException) { }
            Check(service.Snapshot.RememberSignIn && File.ReadAllBytes(Credential(root)).SequenceEqual(original));
            using (FileStream held = new(Credential(root), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                IdentitySnapshot failed = await service.SetRememberSignInAsync(true);
                Check(failed.ErrorCode == "AUTH_CREDENTIALS_WRITE_FAILED" && failed.RememberSignIn && failed.SessionEpoch == signedIn.SessionEpoch);
                Check(File.ReadAllBytes(Credential(root)).SequenceEqual(original));
            }
        })));

        yield return ("T03 persistence fixture: unauthenticated persistence does not invent session", () => Run(() => WithDirectory(async root =>
        {
            using var service = new LogtoIdentityService(IdentitySessionTests.Options(), new WindowsCredentialVault(root, IdentitySessionTests.Options()), new PersistenceFlow(IdentitySessionTests.Session()));
            IdentitySnapshot result = await service.SetRememberSignInAsync(true);
            Check(result.State == IdentitySessionState.SignedOut && result.ErrorCode == "AUTH_REQUIRED" && !result.RememberSignIn && !File.Exists(Credential(root)));
        })));

        yield return ("T03 persistence Windows DPAPI: failed deletion leaves tombstone and late refresh cannot rewrite credentials", () => Run(() => WithDirectory(async root =>
        {
            var options = IdentitySessionTests.Options(); var session = IdentitySessionTests.Session();
            var vault = new WindowsCredentialVault(root, options); var flow = new PersistenceFlow(session) { Hold = true };
            using (var service = new LogtoIdentityService(options, vault, flow))
            {
                await service.SignInAsync(true);
                Task<IdentitySnapshot> refresh = service.RefreshAsync(); await flow.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
                using (FileStream held = new(Credential(root), FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    IdentitySnapshot failed = await service.SetRememberSignInAsync(false);
                    Check(!failed.RememberSignIn && failed.ErrorCode == "AUTH_CREDENTIALS_WRITE_FAILED" && vault.Read() is null);
                    flow.Completed.TrySetResult(session with { RefreshToken = "fixture-must-not-be-written" });
                    IdentitySnapshot result = await refresh;
                    Check(result.State == IdentitySessionState.SignedIn && !result.RememberSignIn && result.ErrorCode == "AUTH_CREDENTIALS_WRITE_FAILED" && vault.Read() is null);
                }
                Check((await service.SetRememberSignInAsync(false)).ErrorCode is null && !File.Exists(Credential(root)));
            }
            using var next = new LogtoIdentityService(options, new WindowsCredentialVault(root, options), new PersistenceFlow(session));
            Check((await next.InitializeAsync()).State == IdentitySessionState.SignedOut);
        })));

        yield return ("T03 persistence Windows DPAPI with injected pre-marker failure: error remains until durable removal succeeds", () => Run(() => WithDirectory(async root =>
        {
            var options = IdentitySessionTests.Options(); var session = IdentitySessionTests.Session();
            var vault = new FailingClearVault(new WindowsCredentialVault(root, options));
            using var service = new LogtoIdentityService(options, vault, new PersistenceFlow(session));
            await service.SignInAsync(true); byte[] before = File.ReadAllBytes(Credential(root));
            vault.FailClear = true;
            IdentitySnapshot failed = await service.SetRememberSignInAsync(false);
            Check(!failed.RememberSignIn && failed.ErrorCode == "AUTH_CREDENTIALS_WRITE_FAILED" && vault.Read() is not null);
            IdentitySnapshot refreshed = await service.RefreshAsync();
            Check(!refreshed.RememberSignIn && refreshed.ErrorCode == "AUTH_CREDENTIALS_WRITE_FAILED" && File.ReadAllBytes(Credential(root)).SequenceEqual(before));
            vault.FailClear = false;
            Check((await service.SetRememberSignInAsync(false)).ErrorCode is null && vault.Read() is null);
        })));

        yield return ("T03 persistence Windows DPAPI: refresh storage failure keeps verified memory session and rotated token for retry", () => Run(() => WithDirectory(async root =>
        {
            var options = IdentitySessionTests.Options(); var session = IdentitySessionTests.Session();
            var flow = new PersistenceFlow(session); var vault = new WindowsCredentialVault(root, options);
            using var service = new LogtoIdentityService(options, vault, flow);
            IdentitySnapshot first = await service.SignInAsync(true); byte[] before = File.ReadAllBytes(Credential(root));
            using (FileStream held = new(Credential(root), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                IdentitySnapshot result = await service.RefreshAsync();
                Check(result.State == IdentitySessionState.SignedIn && result.SessionEpoch == first.SessionEpoch && result.AccountNamespace == first.AccountNamespace && result.ErrorCode == "AUTH_CREDENTIALS_WRITE_FAILED");
                Check(File.ReadAllBytes(Credential(root)).SequenceEqual(before));
            }
            Check((await service.RefreshAsync()).ErrorCode is null && flow.LastRefreshToken == session.RefreshToken + "-rotated");
            Check(vault.Read()?.RefreshToken == session.RefreshToken + "-rotated-rotated");
        })));

        yield return ("T03 persistence fixture: successful offline persistence retry clears only storage error", () => Run(() => WithDirectory(async root =>
        {
            var options = IdentitySessionTests.Options(); var session = IdentitySessionTests.Session();
            var flow = new PersistenceFlow(session); var vault = new FailingClearVault(new WindowsCredentialVault(root, options));
            using var service = new LogtoIdentityService(options, vault, flow);
            await service.SignInAsync(true); flow.Failure = "OFFLINE"; await service.RefreshAsync();
            vault.FailClear = true; await service.SetRememberSignInAsync(false);
            vault.FailClear = false;
            IdentitySnapshot result = await service.SetRememberSignInAsync(false);
            Check(result.State == IdentitySessionState.OfflineCached && !result.RememberSignIn && result.ErrorCode == "OFFLINE" && vault.Read() is null);
        })));

        yield return ("T03 persistence protocol: known expired token without refresh fails before offline discovery", () => Run(async () =>
        {
            int transports = 0;
            NativeOidcFlow flow = new(IdentitySessionTests.Options(), transport: () => { transports++; throw new HttpRequestException("Fixture offline."); });
            try { await flow.RefreshAsync(IdentitySessionTests.Session() with { RefreshToken = null, ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-5) }, CancellationToken.None); throw new InvalidOperationException("Expired session accepted."); }
            catch (IdentityFlowException error) { Check(error.Code == "SESSION_EXPIRED" && transports == 0); }
        }));
    }

    private static string Credential(string root) => Path.Combine(root, "Identity", "session.dpapi");
    private static async Task WithDirectory(Func<string, Task> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "autumnos-persistence-owned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await action(root); }
        finally { Directory.Delete(root, true); }
    }
    private static void Run(Func<Task> action) => action().GetAwaiter().GetResult();
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("Identity persistence fixture failed."); }
    private sealed class PersistenceFlow(ProtectedIdentitySession initial) : INativeOidcFlow
    {
        internal bool LastRemember, Hold;
        internal int LoginCount, RefreshCount;
        internal string? Failure;
        internal string? LastRefreshToken;
        internal TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<ProtectedIdentitySession> Completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<ProtectedIdentitySession> SignInAsync(bool remember, bool reauthenticate, CancellationToken cancellationToken)
        { LastRemember = remember; LoginCount++; return Task.FromResult(initial); }
        public async Task<ProtectedIdentitySession> RefreshAsync(ProtectedIdentitySession session, CancellationToken cancellationToken)
        {
            LastRefreshToken = session.RefreshToken;
            RefreshCount++; Started.TrySetResult();
            if (Hold) return await Completed.Task; // Deliberately late even after cancellation, exercising final commit gates.
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) throw new IdentityFlowException(Failure);
            if (session.RefreshToken is null)
            {
                if (session.ExpiresAt <= DateTimeOffset.UtcNow) throw new IdentityFlowException("SESSION_EXPIRED");
                return session; // Models real successful same-sub UserInfo validation, never extends a non-refreshable token.
            }
            return session with { RefreshToken = session.RefreshToken + "-rotated", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(10) };
        }
        public Task SignOutBrowserAsync(ProtectedIdentitySession session, CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class FailingClearVault(ICredentialVault inner) : ICredentialVault
    {
        internal bool FailClear;
        public ProtectedIdentitySession? Read() => inner.Read();
        public void Write(ProtectedIdentitySession session) => inner.Write(session);
        public void Clear() { if (FailClear) throw new IOException("Injected pre-marker failure."); inner.Clear(); }
    }
}
