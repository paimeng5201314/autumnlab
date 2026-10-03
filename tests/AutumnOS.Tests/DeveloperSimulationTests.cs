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
