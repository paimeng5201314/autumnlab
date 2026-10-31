using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Packages;
using AutumnOS.Runtime;
using AutumnOS.Storage;

namespace AutumnOS.Tests;

internal static class DeveloperSimulationTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("T06 developer simulation: protocol permits only fixed preview-scoped simulation commands", () =>
        {
            foreach (string command in new[] { "deny-permissions", "restore-permissions", "account-a", "account-b", "account-guest", "offline", "online", "reset-simulation" })
            {
                DeveloperPreviewProtocol.Validate(new(1, command, SessionId: Guid.NewGuid().ToString("N")));
                PackageReject(() => DeveloperPreviewProtocol.Validate(new(1, command, SessionId: "normal-game")), "DEVELOPER_REQUEST_INVALID");
                PackageReject(() => DeveloperPreviewProtocol.Validate(new(1, command, PackagePath: Path.GetFullPath("fixture.autumn"), SessionId: Guid.NewGuid().ToString("N"))), "DEVELOPER_REQUEST_INVALID");
            }
            PackageReject(() => DeveloperPreviewProtocol.Validate(new(1, "account-real-user", SessionId: Guid.NewGuid().ToString("N"))), "DEVELOPER_COMMAND_UNSUPPORTED");
        });
        yield return ("T06 developer simulation: account switch invalidates epoch without reviving old requests", () =>
        {
            using DeveloperPreviewSimulation simulation = new();
            Assert(!simulation.UsesTestAccount && !simulation.Offline, "Simulation was enabled by default.");
            simulation.SwitchAccount("account-a"); RuntimeAccountContext a = simulation.BindAccount(CancellationToken.None);
            simulation.SwitchAccount("account-b"); RuntimeAccountContext b = simulation.BindAccount(CancellationToken.None);
            Reject(a.EnsureCurrent, "SESSION_EXPIRED"); Assert(a.Invalidated.IsCancellationRequested && a.AccountKey != b.AccountKey, "Old epoch or account binding stayed valid.");
            simulation.SwitchAccount("account-a"); RuntimeAccountContext returned = simulation.BindAccount(CancellationToken.None);
            Assert(returned.AccountKey == a.AccountKey && returned.Epoch > a.Epoch && returned.IsCurrent(), "Returning test account could not find its own stable data namespace.");
            Reject(a.EnsureCurrent, "SESSION_EXPIRED");
            using DeveloperPreviewSimulation separate = new(); separate.SwitchAccount("account-a");
            Assert(separate.BindAccount(CancellationToken.None).AccountKey != returned.AccountKey, "Different previews shared simulated account namespace.");
        });
        yield return ("T06 developer simulation: forced denial and preview approval never write persistent grants", () => Fixture((root, original, app) =>
        {
            RuntimeAccountContext live = new("guest", 1, true, () => true, CancellationToken.None);
            original.SetDecision(live, app, "saves", PermissionDecision.Granted);
            string path = Path.Combine(root.Directories["Config"], "application-permissions.v1.json"); byte[] before = File.ReadAllBytes(path);
            using DeveloperPreviewPermissions permissions = new(original);
            permissions.SetDenied(true, live, app);
            Reject(() => permissions.Demand(live, app, "saves"), "PERMISSION_DENIED");
            Assert(permissions.RequestAsync(live, app, "saves", true, (_, _) => throw new InvalidOperationException("Denied permission opened prompt."), CancellationToken.None).GetAwaiter().GetResult() == PermissionDecision.Denied, "Denied permission returned grant.");
            permissions.SetDenied(false, live, app); permissions.SetDecision(live, app, "saves", PermissionDecision.Revoked);
            Assert(original.Query(live, app, "saves") == PermissionDecision.Granted && File.ReadAllBytes(path).SequenceEqual(before), "Preview mutation affected persistent grants.");
        }));
        yield return ("T06 developer simulation: deny then restore still invalidates a pending approval", () => Fixture((_, original, app) =>
        {
            using DeveloperPreviewSimulation simulation = new(); simulation.SwitchAccount("account-a"); var account = simulation.BindAccount(CancellationToken.None);
            using DeveloperPreviewPermissions permissions = new(original);
            TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<PermissionDecision> pending = permissions.RequestAsync(account, app, "saves", true, (_, _) => answer.Task, CancellationToken.None);
            permissions.SetDenied(true, account, app); permissions.SetDenied(false, account, app); answer.SetResult(true);
            Reject(() => pending.GetAwaiter().GetResult(), "PERMISSION_REVOKED");
            Assert(permissions.Query(account, app, "saves") == PermissionDecision.Prompt, "Stale approval survived deny/restore.");
        }));
        yield return ("T06 developer simulation: minimum profile and offline event use explicit synthetic identity only", () => Fixture((root, original, app) =>
        {
            using DeveloperPreviewSimulation simulation = new(); simulation.SwitchAccount("account-a"); var account = simulation.BindAccount(CancellationToken.None);
            using DeveloperPreviewPermissions permissions = new(original); permissions.SetDecision(account, app, "identity.profile", PermissionDecision.Granted);
            RuntimeSession session = Session(root, app, simulation, permissions, account); session.Foreground();
            List<(string Name, string Json)> events = []; session.SdkEvent += (name, value) => events.Add((name, JsonSerializer.Serialize(value)));
            simulation.SetOffline(true);
            using JsonDocument reply = Call(session, "identity.getProfile"); JsonElement profile = reply.RootElement.GetProperty("result");
            Assert(reply.RootElement.GetProperty("ok").GetBoolean() && profile.GetProperty("displayName").GetString() == "开发测试账号 A" && profile.GetProperty("isCached").GetBoolean(), "Offline profile did not show explicit cached test identity.");
            Assert(profile.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "appScopedUserId", "avatarUrl", "displayName", "isCached" }.Order()) && profile.GetProperty("avatarUrl").ValueKind == JsonValueKind.Null, "Profile exposed fields beyond minimum schema.");
            Assert(events.Any(e => e.Name == "identity.changed" && e.Json.Contains("offline_cached", StringComparison.Ordinal)), "Offline test identity did not emit SDK event.");
            simulation.SetOffline(false);
            Assert(events.Any(e => e.Name == "identity.changed" && e.Json.Contains("signed_in", StringComparison.Ordinal)), "Online test identity did not emit SDK event.");
            Reject(() => simulation.SignInAsync(false), "CAPABILITY_UNAVAILABLE"); Reject(() => simulation.RefreshAsync(), "CAPABILITY_UNAVAILABLE");
            session.Close();
        }));
        yield return ("T06 developer simulation: guest remains auth required and mode teardown closes test runtime", () => Fixture((root, original, app) =>
        {
            DeveloperPreviewSimulation simulation = new(); simulation.SwitchAccount("guest"); var account = simulation.BindAccount(CancellationToken.None);
            using DeveloperPreviewPermissions permissions = new(original);
            RuntimeSession session = Session(root, app, simulation, permissions, account); session.Foreground();
            using JsonDocument reply = Call(session, "identity.getProfile"); Assert(reply.RootElement.GetProperty("error").GetProperty("code").GetString() == "AUTH_REQUIRED", "Test guest obtained signed-in profile.");
            simulation.Dispose(); Assert(account.Invalidated.IsCancellationRequested && session.Instance.State == AppLifecycleState.Closed, "Mode teardown retained live test requests/runtime.");
            Reject(account.EnsureCurrent, "SESSION_EXPIRED");
        }));
        yield return ("T06 developer simulation: account A B and live saves use existing store with separate ownership", () => Fixture((root, _, app) =>
        {
            using DeveloperPreviewSimulation simulation = new();
            AccountDataStore Store(RuntimeAccountContext account)
            {
                StorageScope scope = new(app.Identity, account.AccountKey, new(Guid.NewGuid()), new(account.Epoch), app.BindingKey);
                return new(root, scope, _ => account.IsCurrent(), account.EnterCommitLease);
            }
            RuntimeAccountContext live = new("guest", 1, true, () => true, CancellationToken.None);
            AccountDataStore liveStore = Store(live); Assert(liveStore.WriteSave("game", JsonSerializer.SerializeToElement("live")).Success, "Live fixture save failed.");
            simulation.SwitchAccount("account-a"); var a = simulation.BindAccount(CancellationToken.None); AccountDataStore storeA = Store(a);
            Assert(storeA.ReadSave("game").Value is null && storeA.WriteSave("game", JsonSerializer.SerializeToElement("A")).Success, "Test A read live save or failed write.");
            simulation.SwitchAccount("account-b"); var b = simulation.BindAccount(CancellationToken.None); AccountDataStore storeB = Store(b);
            Assert(storeB.ReadSave("game").Value is null && storeB.WriteSave("game", JsonSerializer.SerializeToElement("B")).Success, "Test B read A/live save or failed write.");
            Assert(!storeA.WriteSave("game", JsonSerializer.SerializeToElement("stale")).Success, "Old account writer survived switch.");
            simulation.SwitchAccount("account-a"); var returned = simulation.BindAccount(CancellationToken.None);
            Assert(Store(returned).ReadSave("game").Value!.Value.GetString() == "A" && liveStore.ReadSave("game").Value!.Value.GetString() == "live", "Account switch lost A or overwrote live data.");
        }));
        yield return ("T06 developer simulation: real grant revision changes discard completed preview response", () => Fixture((root, original, app) =>
        {
            RuntimeAccountContext live = new("guest", 1, true, () => true, CancellationToken.None); original.SetDecision(live, app, "saves", PermissionDecision.Granted);
            using DeveloperPreviewPermissions permissions = new(original);
            RuntimeSessionServices services = new() { Account = live, Application = app, Permissions = permissions, RequestPermission = (_, _) => Task.FromResult(false), DataRequest = (_, _, _) => Task.FromResult<object>(new { exists = false, value = (object?)null }) };
            RuntimeSession session = new(app.Identity.AppId, app.DeclaredPermissions, root.Directories["Saves"], services: services); session.Foreground();
            long revision = session.EventRevision; using JsonDocument reply = Call(session, "saves.read", new { slot = "game" }); string response = reply.RootElement.GetRawText();
            original.SetDecision(live, app, "saves", PermissionDecision.Revoked); original.SetDecision(live, app, "saves", PermissionDecision.Granted);
            using JsonDocument discarded = JsonDocument.Parse(session.RevalidateResponse("saves.read", revision, response));
            Assert(discarded.RootElement.GetProperty("error").GetProperty("code").GetString() == "PERMISSION_REVOKED", "Real revoke/regrant revived old preview data response."); session.Close();
        }));
        yield return ("T06 developer simulation: permission revisions include local inherited and forced denial changes independently", () => Fixture((_, original, app) =>
        {
            RuntimeAccountContext live = new("guest", 1, true, () => true, CancellationToken.None);
            using DeveloperPreviewPermissions permissions = new(original);
            long previous = permissions.GetRevision(live, app, "saves");
            foreach (PermissionDecision decision in new[] { PermissionDecision.Granted, PermissionDecision.Revoked, PermissionDecision.Granted })
            {
                permissions.SetDecision(live, app, "saves", decision);
                long current = permissions.GetRevision(live, app, "saves");
                Assert(current > previous && permissions.Query(live, app, "saves") == decision, "Local preview revoke/regrant did not advance the matching revision.");
                if (decision == PermissionDecision.Granted)
                {
                    using var lease = permissions.EnterUsageLease(live, app, "saves");
                    Assert(original.Query(live, app, "saves") == PermissionDecision.Prompt, "Local usage lease required or persisted an original grant.");
                }
                previous = current;
            }
            long binding = permissions.GetRevision(live, app);
            permissions.SetDecision(live, app, "identity.profile", PermissionDecision.Granted);
            original.SetDecision(live, app, "identity.profile", PermissionDecision.Granted);
            Assert(permissions.GetRevision(live, app, "saves") == previous && permissions.GetRevision(live, app) > binding,
                "Unrelated local/original permission changed saves revision or stopped binding revision.");
            foreach (PermissionDecision decision in new[] { PermissionDecision.Granted, PermissionDecision.Revoked, PermissionDecision.Granted })
            {
                original.SetDecision(live, app, "saves", decision);
                long current = permissions.GetRevision(live, app, "saves");
                Assert(current > previous && permissions.Query(live, app, "saves") == decision, "Inherited revoke/regrant did not advance the matching revision.");
                previous = current;
            }
            long profile = permissions.GetRevision(live, app, "identity.profile");
            permissions.SetDenied(true, live, app);
            Assert(permissions.GetRevision(live, app, "saves") > previous && permissions.GetRevision(live, app, "identity.profile") > profile,
                "Forced denial failed to invalidate each permission.");
            previous = permissions.GetRevision(live, app, "saves");
            permissions.SetDenied(false, live, app);
            Assert(permissions.GetRevision(live, app, "saves") > previous && permissions.Query(live, app, "saves") == PermissionDecision.Granted,
                "Restore revived a pre-denial revision.");
        }));
        yield return ("T06 developer simulation: unrelated local and original grants preserve pending approval", () => Fixture((_, original, app) =>
        {
            RuntimeAccountContext live = new("guest", 1, true, () => true, CancellationToken.None);
            using DeveloperPreviewPermissions permissions = new(original);
            TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<PermissionDecision> pending = permissions.RequestAsync(live, app, "saves", true, (_, _) => answer.Task, CancellationToken.None);
            permissions.SetDecision(live, app, "identity.profile", PermissionDecision.Granted);
            original.SetDecision(live, app, "identity.profile", PermissionDecision.Revoked);
            answer.SetResult(true);
            Assert(pending.GetAwaiter().GetResult() == PermissionDecision.Granted && permissions.Query(live, app, "saves") == PermissionDecision.Granted,
                "Unrelated permission cancelled a preview approval.");
        }));
        yield return ("T06 developer simulation: original revoke regrant rejects pending preview approval", () => Fixture((_, original, app) =>
        {
            RuntimeAccountContext live = new("guest", 1, true, () => true, CancellationToken.None);
            using DeveloperPreviewPermissions permissions = new(original);
            TaskCompletionSource<bool> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);
            Task<PermissionDecision> pending = permissions.RequestAsync(live, app, "saves", true, (_, _) => answer.Task, CancellationToken.None);
            original.SetDecision(live, app, "saves", PermissionDecision.Revoked);
            original.SetDecision(live, app, "saves", PermissionDecision.Granted);
            answer.SetResult(true);
            Reject(() => pending.GetAwaiter().GetResult(), "PERMISSION_REVOKED");
            Assert(permissions.List(live).Count == 0, "Stale preview approval persisted a local decision.");
        }));
        foreach (bool usage in new[] { true, false })
        {
            bool useUsageLease = usage;
            yield return ($"T06 developer simulation: {(usage ? "usage" : "decision")} lease holds original revocation through final commit", () => Fixture((root, original, app) =>
                VerifyOriginalRevocationLease(root, original, app, useUsageLease)));
        }
        yield return ("T06 developer simulation: synthetic identity revisions and leases never access original grants", () => Fixture((_, _, app) =>
        {
            using DeveloperPreviewSimulation simulation = new(); simulation.SwitchAccount("account-a");
            RuntimeAccountContext account = simulation.BindAccount(CancellationToken.None);
            using DeveloperPreviewPermissions permissions = new(new ForbiddenOriginalPermissions());
            Assert(permissions.Query(account, app, "saves") == PermissionDecision.Prompt && permissions.GetRevision(account, app, "saves") == 0,
                "Synthetic identity inherited a decision.");
            Assert(permissions.RequestAsync(account, app, "saves", true, (_, _) => Task.FromResult(true), CancellationToken.None).GetAwaiter().GetResult() == PermissionDecision.Granted,
                "Synthetic preview could not grant locally.");
            long before = permissions.GetRevision(account, app, "saves");
            permissions.SetDecision(account, app, "saves", PermissionDecision.Revoked);
            Assert(permissions.GetRevision(account, app, "saves") > before, "Synthetic revoke did not advance revision.");
            before = permissions.GetRevision(account, app, "saves");
            permissions.SetDecision(account, app, "saves", PermissionDecision.Granted);
            Assert(permissions.GetRevision(account, app, "saves") > before && permissions.GetRevision(account, app) >= permissions.GetRevision(account, app, "saves"),
                "Synthetic regrant or binding revision failed.");
            using (permissions.EnterUsageLease(account, app, "saves")) permissions.Demand(account, app, "saves");
            using (permissions.EnterDecisionLease(account, app, "saves")) Assert(permissions.GetRevision(account, app, "saves") > before, "Synthetic decision lease failed.");
        }));
    }
    private static void VerifyOriginalRevocationLease(InstallationRoot root, PermissionService original, RuntimeApplication app, bool usage)
    {
        RuntimeAccountContext live = new("guest", 1, true, () => true, CancellationToken.None);
        original.SetDecision(live, app, "saves", PermissionDecision.Granted);
        string path = Path.Combine(root.Directories["Config"], "application-permissions.v1.json");
        byte[] before = File.ReadAllBytes(path);
        long revision = original.GetRevision(live, app, "saves");
        using ManualResetEventSlim started = new(), committed = new();
        // Observe commit before the preview event callback can block on its own gate.
        original.Changed += change => { if (change.Permission == "saves" && change.Decision == PermissionDecision.Revoked) committed.Set(); };
        using DeveloperPreviewPermissions permissions = new(original);
        Exception? workerFailure = null;
        Thread revoke = new(() =>
        {
            started.Set();
            try { original.SetDecision(live, app, "saves", PermissionDecision.Revoked); }
            catch (Exception error) { workerFailure = error; }
        }) { IsBackground = true };
        IDisposable lease = usage ? permissions.EnterUsageLease(live, app, "saves") : permissions.EnterDecisionLease(live, app, "saves");
        try
        {
            revoke.Start();
            Assert(started.Wait(TimeSpan.FromSeconds(5)), "Revocation worker did not execute.");
            Assert(SpinWait.SpinUntil(() => (revoke.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0 || !revoke.IsAlive,
                TimeSpan.FromSeconds(5)), "Revocation worker did not reach the contended lease.");
            Assert(revoke.IsAlive && !committed.IsSet, "Original revocation committed while the preview lease was held.");
            Assert(original.Query(live, app, "saves") == PermissionDecision.Granted && original.GetRevision(live, app, "saves") == revision
                && File.ReadAllBytes(path).SequenceEqual(before), "Original decision changed on disk or in memory before final commit released its lease.");
        }
        finally
        {
            lease.Dispose();
            Assert(revoke.Join(TimeSpan.FromSeconds(5)), "Original revocation did not finish after releasing the lease.");
        }
        if (workerFailure is not null) throw new InvalidOperationException("Revocation worker failed.", workerFailure);
        Assert(committed.IsSet && original.Query(live, app, "saves") == PermissionDecision.Revoked && original.GetRevision(live, app, "saves") > revision,
            "Positive control failed: original revocation was not committed after lease release.");
        Reject(() => { using var rejected = permissions.EnterUsageLease(live, app, "saves"); }, "PERMISSION_REVOKED");
        using (permissions.EnterDecisionLease(live, app, "saves")) Assert(permissions.Query(live, app, "saves") == PermissionDecision.Revoked, "Decision lease rejected or revived revoked state.");
    }
    private sealed class ForbiddenOriginalPermissions : IPermissionService
    {
        public event Action<PermissionChange>? Changed { add { } remove { } }
        private static Exception Unexpected() => new InvalidOperationException("Synthetic identity accessed original permissions.");
        public PermissionDecision Query(RuntimeAccountContext account, RuntimeApplication app, string permission) => throw Unexpected();
        public IReadOnlyList<PermissionRecord> List(RuntimeAccountContext account) => throw Unexpected();
        public void Demand(RuntimeAccountContext account, RuntimeApplication app, string permission) => throw Unexpected();
        public long GetRevision(RuntimeAccountContext account, RuntimeApplication app) => throw Unexpected();
        public long GetRevision(RuntimeAccountContext account, RuntimeApplication app, string permission) => throw Unexpected();
        public IDisposable EnterUsageLease(RuntimeAccountContext account, RuntimeApplication app, string permission) => throw Unexpected();
        public IDisposable EnterDecisionLease(RuntimeAccountContext account, RuntimeApplication app, string permission) => throw Unexpected();
        public Task<PermissionDecision> RequestAsync(RuntimeAccountContext account, RuntimeApplication app, string permission, bool userGesture,
            Func<RuntimePermissionPrompt, CancellationToken, Task<bool>> prompt, CancellationToken cancellationToken) => throw Unexpected();
        public void SetDecision(RuntimeAccountContext account, RuntimeApplication app, string permission, PermissionDecision decision) => throw Unexpected();
    }
    private static RuntimeSession Session(InstallationRoot root, RuntimeApplication app, DeveloperPreviewSimulation simulation,
        DeveloperPreviewPermissions permissions, RuntimeAccountContext account) => new(app.Identity.AppId, app.DeclaredPermissions, root.Directories["Saves"],
        services: new() { Account = account, Application = app, Permissions = permissions, RequestPermission = (_, _) => Task.FromResult(false), Identity = simulation, GetProfile = simulation.GetProfileAsync });
    private static JsonDocument Call(RuntimeSession session, string method, object? parameters = null)
        => JsonDocument.Parse(session.HandleMessageAsync(session.PageUri, JsonSerializer.Serialize(new { protocolVersion = 1, requestId = Guid.NewGuid().ToString("N"), method, @params = parameters ?? new { } }), (_, _) => Task.FromResult(false)).GetAwaiter().GetResult());
    private static void Fixture(Action<InstallationRoot, PermissionService, RuntimeApplication> test)
    {
        string directory = Path.Combine(Path.GetTempPath(), "autumnos-developer-simulation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            InstallationRoot root = new(directory); Assert(root.EnsureCreated().Success, "Isolated fixture root unavailable.");
            PermissionService permissions = new(root.Directories["Config"]); RuntimeApplication app = new(new("cn.labchronicles.simfixture", null, null), "local-preview:" + new string('a', 64), "模拟测试", ["saves", "identity.profile"]);
            test(root, permissions, app);
        }
        finally { Directory.Delete(directory, true); }
    }
    private static void Reject(Action action, string code) { try { action(); throw new InvalidOperationException("Unexpected success."); } catch (RuntimeCapabilityException error) { Assert(error.Code == code, "Unexpected code: " + error.Code); } }
    private static void PackageReject(Action action, string code) { try { action(); throw new InvalidOperationException("Unexpected success."); } catch (PackageException error) { Assert(error.Code == code, "Unexpected code: " + error.Code); } }
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
