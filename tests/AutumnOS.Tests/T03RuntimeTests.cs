using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

internal static class T03RuntimeTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("t03.permissions.persist_grants_and_denials_by_source_and_account", PermissionPersistence);
        yield return ("t03.permissions.new_permissions_require_new_approval", PermissionUpgrade);
        yield return ("t03.permissions.native_user_gesture_required", GestureRequired);
        yield return ("t03.permissions.denial_and_revocation_stop_repeated_prompts", DenialCooldown);
        yield return ("t03.permissions.revoke_invalidates_pending_approval", RevokePending);
        yield return ("t03.permissions.concurrent_prompt_is_bounded", ConcurrentPrompt);
        yield return ("t03.permissions.corrupt_records_are_preserved", CorruptPermissions);
        yield return ("t03.permissions.atomic_replacement_preserves_backup", PermissionBackup);
        yield return ("t03.identity.profile_guest_requires_login", GuestProfile);
        yield return ("t03.identity.profile_has_only_minimum_fields_and_scoped_id", MinimumProfile);
        yield return ("t03.identity.revocation_discards_delayed_profile", RevokedProfile);
        yield return ("t03.identity.epoch_discards_delayed_profile", StaleProfile);
        yield return ("t03.identity.host_snapshot_offline_profile_is_minimal", HostIdentitySnapshot);
        yield return ("t03.identity.host_epoch_change_closes_session_and_cancels_events", HostIdentityEpoch);
        yield return ("t03.runtime.forged_identity_and_epoch_rejected", ForgedIdentity);
        yield return ("t03.runtime.native_gesture_expires_and_is_single_use", GestureLifetime);
        yield return ("t03.runtime.suspend_invalidates_pending_permission", SuspendPermission);
        yield return ("t03.runtime.queued_events_revalidate_permission_revision", EventRevision);
        yield return ("t03.runtime.event_delivery_requires_current_declared_permissions", EventPermissionDelivery);
        yield return ("t03.runtime.native_event_post_linearizes_before_revocation", EventPostLease);
        yield return ("t03.runtime.native_event_post_linearizes_before_account_epoch_change", EventEpochLease);
        yield return ("t03.runtime.native_event_post_linearizes_before_close", EventCloseLease);
        yield return ("t03.runtime.managed_response_size_is_bounded", ResponseSize);
        yield return ("t03.runtime.malformed_base64_or_numeric_adapter_input_is_safe_error", AdapterParameterErrors);
        yield return ("t03.runtime.completed_private_reply_rechecks_revocation_before_delivery", ResponseDelivery);
        yield return ("t03.runtime.first_profile_approval_remains_deliverable", FirstProfileDelivery);
        yield return ("t03.runtime.native_response_post_linearizes_before_revocation", DeliveryLease);
        yield return ("t03.runtime.delayed_permission_events_cannot_revive_old_reply", DelayedPermissionEvents);
        yield return ("t03.runtime.close_prevents_data_commit_and_events", ClosedCommit);
        yield return ("t03.runtime.revocation_rechecked_after_data_request", RevokeData);
        yield return ("t03.runtime.revocation_blocks_queued_write_before_commit", RevokeQueuedCommit);
        yield return ("t03.runtime.cancellation_cleans_subscriptions", CancelSubscriptions);
        yield return ("t03.runtime.capabilities_only_advertise_wired_services", ActualCapabilities);
        yield return ("t03.desktop.notifications_permission_and_bounded_deduplication", NotificationDeduplication);
        yield return ("t03.desktop.notification_mute_persists_and_clears_badge", NotificationMute);
        yield return ("t03.desktop.revoke_removes_notification_and_click_capability", NotificationRevocation);
        yield return ("t03.desktop.notification_click_requires_declared_action", NotificationClick);
        yield return ("t03.desktop.appearance_snapshot_and_scoped_change_event", AppearanceEvents);
        yield return ("t03.desktop.shortcut_registration_and_native_invocation", Shortcuts);
        yield return ("t03.desktop.widget_data_is_bounded_plain_text", Widgets);
        yield return ("t03.desktop.internal_links_cannot_target_other_apps_or_commands", InternalLinks);
        yield return ("t03.desktop.close_removes_all_desktop_extensions", ExtensionCleanup);
        yield return ("t03.desktop.stale_account_cannot_receive_or_write_extensions", StaleDesktop);
    }

    private static void PermissionPersistence() => InTemp(root =>
    {
        var store = new PermissionService(root); var account = Account("a"); var app = App();
        store.SetDecision(account, app, "saves", PermissionDecision.Granted);
        store.SetDecision(account, app, "identity.profile", PermissionDecision.Denied);
        var reloaded = new PermissionService(root);
        Equal(reloaded.Query(account, app, "saves"), PermissionDecision.Granted);
        Equal(reloaded.Query(account, app, "identity.profile"), PermissionDecision.Denied);
        Equal(reloaded.Query(Account("b"), app, "saves"), PermissionDecision.Prompt);
        Equal(reloaded.Query(Account("guest", true), app, "saves"), PermissionDecision.Prompt);
        Equal(reloaded.Query(account, app with { Source = "other-installed-source" }, "saves"), PermissionDecision.Prompt);
        Equal(reloaded.Query(account, app with { Identity = app.Identity with { RepositoryId = 456 } }, "saves"), PermissionDecision.Prompt);
        Equal(reloaded.Query(account, app with { DisplayName = "新名字" }, "saves"), PermissionDecision.Granted);
    });
    private static void PermissionUpgrade() => InTemp(root =>
    {
        var store = new PermissionService(root); var account = Account(); var old = App() with { DeclaredPermissions = ["saves"] };
        store.SetDecision(account, old, "saves", PermissionDecision.Granted);
        Throws("PERMISSION_NOT_DECLARED", () => store.Query(account, old, "notifications"));
        Equal(store.Query(account, App(), "notifications"), PermissionDecision.Prompt);
        Throws("CAPABILITY_UNAVAILABLE", () => store.Query(account, App(), "host.execute"));
    });
    private static void GestureRequired() => InTemp(root =>
    {
        int prompts = 0; var store = new PermissionService(root);
        Throws("USER_GESTURE_REQUIRED", () => store.RequestAsync(Account(), App(), "saves", false,
            (_, _) => { prompts++; return Task.FromResult(true); }, default).GetAwaiter().GetResult());
        Equal(prompts, 0);
        var state = store.RequestAsync(Account(), App(), "identity.profile", true, (prompt, _) =>
        {
            Equal(prompt.AppId, "test.game"); Equal(prompt.Source, "local-package:test-source");
            Assert(prompt.Fields.SequenceEqual(new[] { "appScopedUserId", "displayName", "avatarUrl", "isCached" }), "Minimal prompt fields.");
            Assert(!prompt.Purpose.Contains("refresh", StringComparison.OrdinalIgnoreCase), "No refresh credential access.");
            return Task.FromResult(true);
        }, default).GetAwaiter().GetResult();
        Equal(state, PermissionDecision.Granted);
    });
    private static void DenialCooldown() => InTemp(root =>
    {
        int prompts = 0; var store = new PermissionService(root); var account = Account(); var app = App();
        Task<bool> Deny(RuntimePermissionPrompt _, CancellationToken __) { prompts++; return Task.FromResult(false); }
        for (int count = 0; count < 20; count++) Equal(store.RequestAsync(account, app, "saves", true, Deny, default).GetAwaiter().GetResult(), PermissionDecision.Denied);
        Equal(prompts, 1);
        store.SetDecision(account, app, "saves", PermissionDecision.Revoked);
        Equal(store.RequestAsync(account, app, "saves", true, Deny, default).GetAwaiter().GetResult(), PermissionDecision.Revoked);
        Equal(prompts, 1);
        store.SetDecision(account, app, "saves", PermissionDecision.Prompt);
        _ = store.RequestAsync(account, app, "saves", true, Deny, default).GetAwaiter().GetResult(); Equal(prompts, 2);
    });
    private static void RevokePending() => InTemp(root =>
    {
        var store = new PermissionService(root); var account = Account(); var app = App(); var response = Signal<bool>();
        Task<PermissionDecision> pending = store.RequestAsync(account, app, "identity.profile", true, (_, _) => response.Task, default);
        store.SetDecision(account, app, "identity.profile", PermissionDecision.Revoked); response.SetResult(true);
        Throws("PERMISSION_REVOKED", () => pending.GetAwaiter().GetResult());
        Equal(store.Query(account, app, "identity.profile"), PermissionDecision.Revoked);
    });
    private static void ConcurrentPrompt() => InTemp(root =>
    {
        var store = new PermissionService(root); var response = Signal<bool>(); var account = Account(); var app = App();
        var first = store.RequestAsync(account, app, "saves", true, (_, _) => response.Task, default);
        Throws("REQUEST_BUSY", () => store.RequestAsync(account, app, "saves", true, (_, _) => Task.FromResult(true), default).GetAwaiter().GetResult());
        response.SetResult(true); Equal(first.GetAwaiter().GetResult(), PermissionDecision.Granted);
    });
    private static void CorruptPermissions() => InTemp(root =>
    {
        string path = Path.Combine(root, "application-permissions.v1.json");
        byte[] original = "{\"schemaVersion\":1,\"schemaVersion\":1,\"records\":[]}"u8.ToArray(); File.WriteAllBytes(path, original);
        Throws("PERMISSION_STORE_CORRUPT", () => _ = new PermissionService(root));
        Assert(File.ReadAllBytes(path).SequenceEqual(original), "Corrupt grant record must be preserved.");
    });
    private static void PermissionBackup() => InTemp(root =>
    {
        var store = new PermissionService(root); store.SetDecision(Account(), App(), "saves", PermissionDecision.Granted);
        string path = Path.Combine(root, "application-permissions.v1.json"); byte[] original = File.ReadAllBytes(path);
        store.SetDecision(Account(), App(), "saves", PermissionDecision.Revoked);
        Assert(File.ReadAllBytes(path + ".bak").SequenceEqual(original), "Atomic replace keeps previous grant document.");
        Equal(new PermissionService(root).Query(Account(), App(), "saves"), PermissionDecision.Revoked);
    });
    private static void GuestProfile() => InTemp(root =>
    {
        var account = Account("guest", true); var services = Services(root, account); var session = Session(root, services);
        session.HostUserGesture(); Error(Send(session, "identity.requestProfile"), "AUTH_REQUIRED");
        Equal(services.Permissions.Query(account, services.Application, "identity.profile"), PermissionDecision.Prompt);
        session.Close();
    });
    private static void MinimumProfile() => InTemp(root =>
    {
        var first = Services(root, Account("a")); first.Permissions.SetDecision(first.Account, first.Application, "identity.profile", PermissionDecision.Granted);
        var session = Session(root, first); var result = Ok(Send(session, "identity.getProfile"));
        Assert(result.EnumerateObject().Select(item => item.Name).Order().SequenceEqual(new[] { "appScopedUserId", "avatarUrl", "displayName", "isCached" }.Order()), "Only four minimum fields.");
        string id = result.GetProperty("appScopedUserId").GetString()!; Equal(result.GetProperty("avatarUrl").ValueKind, JsonValueKind.Null);
        session.Close(); var repeat = Session(root, first); Equal(Ok(Send(repeat, "identity.getProfile")).GetProperty("appScopedUserId").GetString()!, id); repeat.Close();
        var second = Services(root, Account("b")); second.Permissions.SetDecision(second.Account, second.Application, "identity.profile", PermissionDecision.Granted);
        var other = Session(root, second); Assert(Ok(Send(other, "identity.getProfile")).GetProperty("appScopedUserId").GetString() != id, "Account IDs differ."); other.Close();
    });
    private static void RevokedProfile() => InTemp(root =>
    {
        var completion = Signal<RuntimeProfile>(); var services = Services(root, profile: (_, _) => completion.Task);
        services.Permissions.SetDecision(services.Account, services.Application, "identity.profile", PermissionDecision.Granted);
        var session = Session(root, services); Task<string> request = SendAsync(session, "identity.getProfile");
        services.Permissions.SetDecision(services.Account, services.Application, "identity.profile", PermissionDecision.Revoked);
        completion.SetResult(new("Late", null, false)); Error(request.GetAwaiter().GetResult(), "PERMISSION_REVOKED"); session.Close();
    });
    private static void StaleProfile() => InTemp(root =>
    {
        bool current = true; var account = new RuntimeAccountContext("a", 1, false, () => current, default);
        var completion = Signal<RuntimeProfile>(); var services = Services(root, account, (_, _) => completion.Task);
        services.Permissions.SetDecision(account, services.Application, "identity.profile", PermissionDecision.Granted);
        var session = Session(root, services); var pending = SendAsync(session, "identity.getProfile"); current = false;
        completion.SetResult(new("Old account", null, false)); Error(pending.GetAwaiter().GetResult(), "SESSION_EXPIRED"); session.Close();
    });
    private static void ForgedIdentity() => InTemp(root =>
    {
        var session = Session(root, Services(root));
        Error(Send(session, "identity.getProfile", new { userId = "victim", sessionEpoch = 99 }), "INVALID_REQUEST");
        Error(session.HandleMessageAsync(session.PageUri, "{\"protocolVersion\":1,\"requestId\":\"forged\",\"method\":\"permissions.query\",\"params\":{\"name\":\"saves\"},\"appId\":\"victim.game\"}", (_, _) => Task.FromResult(true)).GetAwaiter().GetResult(), "INVALID_REQUEST");
        session.Close();
    });
    private static void HostIdentitySnapshot() => InTemp(root =>
    {
        var identity = new TestIdentity(new(IdentitySessionState.OfflineCached, "a", 0, "真实验证后的缓存（测试替身）", "https://example.invalid/avatar", true, true, "OFFLINE"));
        var context = new RuntimeAccountContext("a", 0, false, () => identity.Snapshot.AccountNamespace == "a" && identity.Snapshot.SessionEpoch == 0, default);
        var services = new RuntimeSessionServices { Account = context, Application = App(), Permissions = new PermissionService(root),
            RequestPermission = (_, _) => Task.FromResult(true), Identity = identity };
        Grant(services, "identity.profile"); var session = Session(root, services);
        var result = Ok(Send(session, "identity.getProfile")); Assert(result.GetProperty("isCached").GetBoolean(), "Offline cache correctly labelled.");
        Equal(result.GetProperty("avatarUrl").GetString()!, "https://example.invalid/avatar"); Equal(result.EnumerateObject().Count(), 4); session.Close();
    });
    private static void HostIdentityEpoch() => InTemp(root =>
    {
        var identity = new TestIdentity(new(IdentitySessionState.SignedIn, "a", 3, "A", null, false, false, null));
        var context = new RuntimeAccountContext("a", 3, false, () => identity.Snapshot.AccountNamespace == "a" && identity.Snapshot.SessionEpoch == 3, default);
        var services = new RuntimeSessionServices { Account = context, Application = App(), Permissions = new PermissionService(root),
            RequestPermission = (_, _) => Task.FromResult(true), Identity = identity };
        Grant(services, "identity.profile"); var session = Session(root, services); int events = 0; session.SdkEvent += (_, _) => events++;
        identity.Update(identity.Snapshot with { State = IdentitySessionState.OfflineCached }); Equal(events, 1);
        identity.Update(identity.Snapshot with { AccountNamespace = "b", SessionEpoch = 4 }); Equal(session.Instance.State, AppLifecycleState.Closed);
        Error(Send(session, "identity.getProfile"), "SESSION_EXPIRED"); Equal(events, 1); session.HostUserGesture();
    });
    private static void SuspendPermission() => InTemp(root =>
    {
        var answer = Signal<bool>(); var services = new RuntimeSessionServices { Account = Account(), Application = App(), Permissions = new PermissionService(root), RequestPermission = (_, _) => answer.Task };
        var session = Session(root, services); session.HostUserGesture(); var pending = SendAsync(session, "permissions.request", new { name = "saves" });
        session.Suspend(); session.Resume(); answer.SetResult(true); Error(pending.GetAwaiter().GetResult(), "SESSION_SUSPENDED");
        Equal(services.Permissions.Query(services.Account, services.Application, "saves"), PermissionDecision.Prompt); session.Close();
    });
    private static void EventRevision() => InTemp(root =>
    {
        var services = Services(root); Grant(services, "identity.profile"); var session = Session(root, services); long revision = session.EventRevision;
        Assert(session.CanDeliverEvent("identity.changed", revision), "Current granted event is deliverable.");
        services.Permissions.SetDecision(services.Account, services.Application, "identity.profile", PermissionDecision.Revoked);
        Assert(!session.CanDeliverEvent("identity.changed", revision), "Queued pre-revocation event discarded.");
        Grant(services, "identity.profile"); Assert(!session.CanDeliverEvent("identity.changed", revision), "Regrant cannot revive queued event.");
        long current = session.EventRevision; session.Close(); Assert(!session.CanDeliverEvent("appearance.changed", current), "Close removes queued event.");
    });
    private static void EventPermissionDelivery() => InTemp(root =>
    {
        var services = Services(root); var session = Session(root, services); int delivered = 0;
        foreach (var (name, permission) in new[] { ("identity.changed", "identity.profile"), ("links.opened", "links"), ("shortcuts.invoked", "shortcuts") })
        {
            Assert(!session.DeliverEvent(name, session.EventRevision, () => delivered++), "Unapproved event blocked.");
            Grant(services, permission); long original = session.EventRevision;
            Assert(session.DeliverEvent(name, original, () => delivered++), "Granted event delivered.");
            services.Permissions.SetDecision(services.Account, services.Application, permission, PermissionDecision.Revoked);
            Assert(!session.DeliverEvent(name, session.EventRevision, () => delivered++), "Revoked event blocked even with newest revision.");
            Grant(services, permission);
            Assert(!session.DeliverEvent(name, original, () => delivered++), "Regrant never revives an old queued event.");
        }
        Equal(delivered, 3);
        Assert(!session.DeliverEvent("host.execute", session.EventRevision, () => delivered++), "Unknown event rejected.");
        session.Close();
    });
    private static void EventPostLease() => InTemp(root =>
    {
        foreach (var (name, permission) in new[] { ("identity.changed", "identity.profile"), ("links.opened", "links"), ("shortcuts.invoked", "shortcuts"), ("permissions.changed", "storage") })
        {
            var services = Services(root); Grant(services, permission); var session = Session(root, services);
            using var started = new ManualResetEventSlim(); using var completed = new ManualResetEventSlim(); Task? mutation = null;
            Assert(session.DeliverEvent(name, session.EventRevision, () =>
            {
                mutation = Task.Run(() => { started.Set(); services.Permissions.SetDecision(services.Account, services.Application, permission, PermissionDecision.Revoked); completed.Set(); });
                Assert(started.Wait(TimeSpan.FromSeconds(2)), "Concurrent permission mutation started.");
                Assert(!completed.Wait(TimeSpan.FromMilliseconds(50)), "Permission mutation waits until actual native event post finishes.");
            }), "Current event posted under lease.");
            Assert(completed.Wait(TimeSpan.FromSeconds(2)), "Permission mutation proceeds after post."); mutation!.GetAwaiter().GetResult(); session.Close();
        }
    });
    private static void EventEpochLease() => InTemp(root =>
    {
        object gate = new(); bool current = true;
        var account = Account() with { IsCurrent = () => current, EnterCommitLease = () => { Monitor.Enter(gate); return new TestGateLease(gate); } };
        var session = Session(root, Services(root, account)); long revision = session.EventRevision;
        using var started = new ManualResetEventSlim(); using var completed = new ManualResetEventSlim(); Task? mutation = null;
        Assert(session.DeliverEvent("appearance.changed", revision, () =>
        {
            mutation = Task.Run(() => { started.Set(); lock (gate) { current = false; completed.Set(); } });
            Assert(started.Wait(TimeSpan.FromSeconds(2)), "Concurrent epoch mutation started.");
            Assert(!completed.Wait(TimeSpan.FromMilliseconds(50)), "Epoch mutation waits until actual native post finishes.");
        }), "Snapshot delivered within account lease.");
        Assert(completed.Wait(TimeSpan.FromSeconds(2)), "Epoch mutation proceeds after post."); mutation!.GetAwaiter().GetResult();
        Assert(!session.DeliverEvent("appearance.changed", revision, () => throw new InvalidOperationException("Must not post")), "Late old-account event discarded.");
        session.Close();
    });
    private static void EventCloseLease() => InTemp(root =>
    {
        var session = Session(root, Services(root)); long revision = session.EventRevision;
        using var started = new ManualResetEventSlim(); using var completed = new ManualResetEventSlim(); Task? close = null;
        Assert(session.DeliverEvent("appearance.changed", revision, () =>
        {
            close = Task.Run(() => { started.Set(); session.Close(); completed.Set(); });
            Assert(started.Wait(TimeSpan.FromSeconds(2)), "Concurrent close started.");
            Assert(!completed.Wait(TimeSpan.FromMilliseconds(50)), "Close waits until actual native post finishes.");
        }), "Event posted before close.");
        Assert(completed.Wait(TimeSpan.FromSeconds(2)), "Close proceeds after post."); close!.GetAwaiter().GetResult();
        Assert(!session.DeliverEvent("appearance.changed", session.EventRevision, () => throw new InvalidOperationException("Must not post")), "Closed session cannot post.");
    });
    private static void ResponseSize() => InTemp(root =>
    {
        var services = Services(root, data: (_, _, _) => Task.FromResult<object>(new { data = new string('a', 32768) })); Grant(services, "storage");
        var session = Session(root, services); Error(Send(session, "storage.read", new { key = "oversize" }), "RESPONSE_TOO_LARGE"); session.Close();
    });
    private static void AdapterParameterErrors() => InTemp(root =>
    {
        foreach (Exception error in new Exception[] { new FormatException("Do not expose attacker-controlled base64 or paths"), new ArgumentException("Do not expose internal paths") })
        {
            var services = Services(root, data: (_, _, _) => throw error); Grant(services, "storage"); var session = Session(root, services);
            Error(Send(session, "storage.write", new { key = "test", data = "not base64" }), "INVALID_REQUEST"); session.Close();
        }
    });
    private static void ResponseDelivery() => InTemp(root =>
    {
        var services = Services(root); Grant(services, "identity.profile"); var session = Session(root, services); long revision = session.EventRevision;
        string response = Send(session, "identity.getProfile"); Ok(response);
        services.Permissions.SetDecision(services.Account, services.Application, "identity.profile", PermissionDecision.Revoked);
        Error(session.RevalidateResponse("identity.getProfile", revision, response), "PERMISSION_REVOKED");
        Grant(services, "identity.profile"); Error(session.RevalidateResponse("identity.getProfile", revision, response), "PERMISSION_REVOKED");
        string? delivered = null; session.DeliverResponse("identity.getProfile", revision, response, value => delivered = value);
        Error(delivered!, "PERMISSION_REVOKED"); session.Close(); Error(session.RevalidateResponse("identity.getProfile", revision, response), "SESSION_EXPIRED");
    });
    private static void FirstProfileDelivery() => InTemp(root =>
    {
        var services = Services(root); var session = Session(root, services); session.HostUserGesture(); long revision = session.EventRevision;
        string response = Send(session, "identity.requestProfile"); Ok(response); Ok(session.RevalidateResponse("identity.requestProfile", revision, response));
        services.Permissions.SetDecision(services.Account, services.Application, "identity.profile", PermissionDecision.Revoked); Grant(services, "identity.profile");
        Error(session.RevalidateResponse("identity.requestProfile", revision, response), "PERMISSION_REVOKED"); session.Close();
    });
    private static void DeliveryLease() => InTemp(root =>
    {
        var services = Services(root); Grant(services, "identity.profile"); var session = Session(root, services); long revision = session.EventRevision;
        string response = Send(session, "identity.getProfile"); using var started = new ManualResetEventSlim(); using var revoked = new ManualResetEventSlim(); Task? revocation = null;
        session.DeliverResponse("identity.getProfile", revision, response, value =>
        {
            Ok(value);
            revocation = Task.Run(() => { started.Set(); services.Permissions.SetDecision(services.Account, services.Application, "identity.profile", PermissionDecision.Revoked); revoked.Set(); });
            Assert(started.Wait(TimeSpan.FromSeconds(2)), "Concurrent revocation worker started.");
            Assert(!revoked.Wait(TimeSpan.FromMilliseconds(50)), "Native delivery holds final permission lease.");
        });
        Assert(revoked.Wait(TimeSpan.FromSeconds(2)), "Revocation continues immediately after native post."); revocation!.GetAwaiter().GetResult(); session.Close();
    });
    private static void DelayedPermissionEvents() => InTemp(root =>
    {
        var services = Services(root); Grant(services, "identity.profile"); using var releaseCallbacks = new ManualResetEventSlim();
        using var revokeCommitted = new ManualResetEventSlim(); using var grantCommitted = new ManualResetEventSlim();
        services.Permissions.Changed += change =>
        {
            if (change.Decision == PermissionDecision.Revoked) revokeCommitted.Set(); else grantCommitted.Set();
            if (!releaseCallbacks.Wait(TimeSpan.FromSeconds(5))) throw new InvalidOperationException("Test callback budget exceeded.");
        };
        var session = Session(root, services); long revision = session.EventRevision; string response = Send(session, "identity.getProfile");
        Task? revoke = null; Task? grant = null;
        try
        {
            revoke = Task.Run(() => services.Permissions.SetDecision(services.Account, services.Application, "identity.profile", PermissionDecision.Revoked));
            Assert(revokeCommitted.Wait(TimeSpan.FromSeconds(2)), "Revocation commit completed, callbacks blocked.");
            grant = Task.Run(() => Grant(services, "identity.profile"));
            Assert(grantCommitted.Wait(TimeSpan.FromSeconds(2)), "Regrant commit completed, callbacks blocked.");
            Error(session.RevalidateResponse("identity.getProfile", revision, response), "PERMISSION_REVOKED");
        }
        finally { releaseCallbacks.Set(); revoke?.GetAwaiter().GetResult(); grant?.GetAwaiter().GetResult(); session.Close(); }
    });
    private static void GestureLifetime() => InTemp(root =>
    {
        var clock = new TestClock(); var services = Services(root); var session = Session(root, services, clock);
        Error(Send(session, "permissions.request", new { name = "saves" }), "USER_GESTURE_REQUIRED");
        session.HostUserGesture(); clock.Advance(TimeSpan.FromSeconds(3));
        Error(Send(session, "permissions.request", new { name = "saves" }), "USER_GESTURE_REQUIRED");
        session.HostUserGesture(); Equal(Ok(Send(session, "permissions.request", new { name = "saves" })).GetProperty("state").GetString()!, "granted");
        Error(Send(session, "permissions.request", new { name = "notifications" }), "USER_GESTURE_REQUIRED"); session.Close();
    });
    private static void ClosedCommit() => InTemp(root =>
    {
        var session = Session(root, Services(root)); using (session.EnterCommitLease()) { }
        session.Close(); Throws("SESSION_EXPIRED", () => session.EnterCommitLease());
        Error(Send(session, "saves.write", new { slot = "game", value = 4 }), "SESSION_EXPIRED");
    });
    private static void RevokeData() => InTemp(root =>
    {
        var completion = Signal<object>(); var services = Services(root, data: (_, _, _) => completion.Task);
        services.Permissions.SetDecision(services.Account, services.Application, "storage", PermissionDecision.Granted);
        var session = Session(root, services); var pending = SendAsync(session, "storage.read", new { key = "document" });
        services.Permissions.SetDecision(services.Account, services.Application, "storage", PermissionDecision.Revoked);
        completion.SetResult(new { content = "private" }); Error(pending.GetAwaiter().GetResult(), "PERMISSION_REVOKED"); session.Close();
    });
    private static void CancelSubscriptions() => InTemp(root =>
    {
        using var cancellation = new CancellationTokenSource();
        var account = new RuntimeAccountContext("a", 1, false, () => true, cancellation.Token);
        var desktop = new DesktopExtensionService(root); var services = Services(root, account, desktop: desktop); var session = Session(root, services);
        int events = 0; session.SdkEvent += (_, _) => events++;
        desktop.SetAppearance(new("dark", "zh-CN", 1, false)); Equal(events, 1);
        cancellation.Cancel(); Equal(session.Instance.State, AppLifecycleState.Closed); Equal(desktop.Apps.Count, 0);
        desktop.SetAppearance(new("light", "en-US", 1, true)); Equal(events, 1);
    });
    private static void RevokeQueuedCommit() => InTemp(root =>
    {
        var resume = Signal<bool>(); bool written = false; RuntimeSession? session = null;
        var services = Services(root, data: async (_, _, _) =>
        {
            await resume.Task.ConfigureAwait(false);
            using (session!.EnterCommitLease()) { written = true; }
            return new { saved = true };
        });
        Grant(services, "saves"); session = Session(root, services);
        var pending = SendAsync(session, "saves.write", new { slot = "game", value = 9 });
        services.Permissions.SetDecision(services.Account, services.Application, "saves", PermissionDecision.Revoked);
        resume.SetResult(true); Error(pending.GetAwaiter().GetResult(), "PERMISSION_REVOKED"); Assert(!written, "Revoked queued write did not commit."); session.Close();
    });
    private static void ActualCapabilities() => InTemp(root =>
    {
        var session = Session(root, Services(root)); var capabilities = Ok(Send(session, "platform.getCapabilities")).GetProperty("capabilities").EnumerateArray().Select(item => item.GetString()).ToArray();
        Assert(capabilities.Contains("identity.profile") && !capabilities.Contains("saves") && !capabilities.Contains("notifications"), "Only wired capabilities advertised.");
        Error(Send(session, "identity.beginAppSession"), "CAPABILITY_UNAVAILABLE"); session.Close();
    });
    private static void NotificationDeduplication() => InTemp(root =>
    {
        var clock = new TestClock(); var desktop = new DesktopExtensionService(root, clock); var services = Services(root, desktop: desktop); var session = Session(root, services);
        var notification = new { id = "first", title = "进度", body = "真实通知", action = "resume" };
        Error(Send(session, "notifications.show", notification), "PERMISSION_DENIED"); Grant(services, "notifications");
        Equal(Ok(Send(session, "notifications.show", notification)).GetProperty("shown").GetBoolean(), true);
        Equal(Ok(Send(session, "notifications.show", notification)).GetProperty("reason").GetString()!, "duplicate");
        Equal(desktop.Notifications.Count, 1); desktop.DismissNotification(session.Instance.Id.Value, "first");
        Equal(Ok(Send(session, "notifications.show", notification)).GetProperty("reason").GetString()!, "duplicate");
        clock.Advance(TimeSpan.FromMinutes(11)); Equal(Ok(Send(session, "notifications.show", notification)).GetProperty("shown").GetBoolean(), true);
        for (int index = 1; index < 20; index++) Ok(Send(session, "notifications.show", new { id = "n" + index, title = "N", body = "B", action = (string?)null }));
        Error(Send(session, "notifications.show", new { id = "overflow", title = "N", body = "B", action = (string?)null }), "QUOTA_EXCEEDED"); session.Close();
    });
    private static void NotificationMute() => InTemp(root =>
    {
        var desktop = new DesktopExtensionService(root); var services = Services(root, desktop: desktop); var session = Session(root, services); Grant(services, "notifications");
        Ok(Send(session, "notifications.setBadge", new { count = 12 })); Equal(desktop.Apps.Single().Badge, 12);
        desktop.SetMuted(services.Account, services.Application, true); Equal(desktop.Apps.Single().Badge, 0);
        Equal(Ok(Send(session, "notifications.show", new { id = "muted", title = "N", body = "B", action = (string?)null })).GetProperty("reason").GetString()!, "muted");
        Assert(new DesktopExtensionService(root).IsMuted(services.Account, services.Application), "Mute is persisted.");
        Assert(!new DesktopExtensionService(root).IsMuted(Account("different"), services.Application), "Mute is account-bound."); session.Close();
    });
    private static void NotificationRevocation() => InTemp(root =>
    {
        var desktop = new DesktopExtensionService(root); var services = Services(root, desktop: desktop); var session = Session(root, services); Grant(services, "notifications");
        Ok(Send(session, "notifications.show", new { id = "one", title = "N", body = "B", action = "resume" }));
        services.Permissions.SetDecision(services.Account, services.Application, "notifications", PermissionDecision.Revoked);
        Equal(desktop.Notifications.Count, 0); Throws("PERMISSION_REVOKED", () => desktop.ActivateNotification(session.Instance.Id.Value, "one")); session.Close();
    });
    private static void NotificationClick() => InTemp(root =>
    {
        var desktop = new DesktopExtensionService(root); var services = Services(root, desktop: desktop); var session = Session(root, services); Grant(services, "notifications");
        Error(Send(session, "notifications.show", new { id = "bad", title = "N", body = "B", action = "powershell.exe" }), "ACTION_NOT_DECLARED");
        Ok(Send(session, "notifications.show", new { id = "good", title = "N", body = "B", action = "resume" }));
        Guid bound = Guid.Empty; string? action = null; desktop.ActionRequested += (id, value, _) => { bound = id; action = value; };
        Assert(desktop.ActivateNotification(session.Instance.Id.Value, "good"), "Declared action can be activated."); Equal(bound, session.Instance.Id.Value); Equal(action!, "resume");
        Assert(!desktop.ActivateNotification(session.Instance.Id.Value, "good"), "Click is consumed, not replayed."); session.Close();
    });
    private static void AppearanceEvents() => InTemp(root =>
    {
        var desktop = new DesktopExtensionService(root); var services = Services(root, desktop: desktop); var session = Session(root, services);
        Equal(Ok(Send(session, "appearance.get")).GetProperty("theme").GetString()!, "system");
        int events = 0; session.SdkEvent += (name, _) => { if (name == "appearance.changed") events++; };
        desktop.SetAppearance(new("dark", "en-US", 1.5, true)); desktop.SetAppearance(new("dark", "en-US", 1.5, true)); Equal(events, 1);
        session.Close(); desktop.SetAppearance(new("light", "zh-CN", 1, false)); Equal(events, 1);
    });
    private static void Shortcuts() => InTemp(root =>
    {
        var desktop = new DesktopExtensionService(root); var services = Services(root, desktop: desktop); var session = Session(root, services); Grant(services, "shortcuts");
        Error(Send(session, "shortcuts.register", new { ids = new[] { "external.exe" } }), "ACTION_NOT_DECLARED");
        Ok(Send(session, "shortcuts.register", new { ids = new[] { "resume" } })); Equal(desktop.Apps.Single().Shortcuts.Count, 1);
        Assert(desktop.ActivateShortcut(session.Instance.Id.Value, "resume"), "Actual registered shortcut.");
        services.Permissions.SetDecision(services.Account, services.Application, "shortcuts", PermissionDecision.Revoked); Equal(desktop.Apps.Single().Shortcuts.Count, 0);
        Throws("PERMISSION_REVOKED", () => desktop.ActivateShortcut(session.Instance.Id.Value, "resume")); session.Close();
    });
    private static void Widgets() => InTemp(root =>
    {
        var desktop = new DesktopExtensionService(root); var services = Services(root, desktop: desktop); var session = Session(root, services); Grant(services, "widgets");
        Error(Send(session, "widgets.update", new { id = "foreign", lines = new[] { "test" } }), "ACTION_NOT_DECLARED");
        Error(Send(session, "widgets.update", new { id = "score", lines = new[] { new string('a', 161) } }), "INVALID_REQUEST");
        Error(Send(session, "widgets.update", new { id = "score", lines = new[] { "line\nscript" } }), "INVALID_REQUEST");
        Ok(Send(session, "widgets.update", new { id = "score", lines = new[] { "配对 3 / 8", "<script>仅文本</script>" } }));
        Equal(desktop.Apps.Single().Widgets.Single().Title, "进度"); session.Close();
    });
    private static void InternalLinks() => InTemp(root =>
    {
        var desktop = new DesktopExtensionService(root); var services = Services(root, desktop: desktop); var session = Session(root, services); Grant(services, "links");
        Error(Send(session, "links.openInternal", new { action = "resume", arguments = new { slot = "game" } }), "USER_GESTURE_REQUIRED");
        session.HostUserGesture(); Error(Send(session, "links.openInternal", new { action = "resume", arguments = new { }, appId = "victim.game" }), "INVALID_REQUEST");
        session.HostUserGesture(); Error(Send(session, "links.openInternal", new { action = "file:///secret", arguments = new { } }), "INVALID_REQUEST");
        session.HostUserGesture(); Ok(Send(session, "links.openInternal", new { action = "resume", arguments = new { slot = "game" } })); session.Close();
    });
    private static void ExtensionCleanup() => InTemp(root =>
    {
        var desktop = new DesktopExtensionService(root); var services = Services(root, desktop: desktop); var session = Session(root, services);
        foreach (string permission in new[] { "notifications", "shortcuts", "widgets" }) Grant(services, permission);
        Ok(Send(session, "notifications.show", new { id = "one", title = "N", body = "B", action = "resume" }));
        Ok(Send(session, "shortcuts.register", new { ids = new[] { "resume" } })); Ok(Send(session, "widgets.update", new { id = "score", lines = new[] { "1" } }));
        session.Close(); Equal(desktop.Apps.Count, 0); Equal(desktop.Notifications.Count, 0);
        Throws("SESSION_EXPIRED", () => desktop.ActivateShortcut(session.Instance.Id.Value, "resume"));
    });
    private static void StaleDesktop() => InTemp(root =>
    {
        bool current = true; var account = new RuntimeAccountContext("a", 4, false, () => current, default);
        var desktop = new DesktopExtensionService(root); var services = Services(root, account, desktop: desktop); var session = Session(root, services); Grant(services, "notifications");
        current = false; Error(Send(session, "notifications.setBadge", new { count = 5 }), "SESSION_EXPIRED"); Equal(desktop.Apps.Count, 0);
        Throws("SESSION_EXPIRED", () => desktop.SetMuted(account, services.Application, true)); session.Close();
    });

    private static RuntimeApplication App() => new(new("test.game", 123, null), "local-package:test-source", "受控测试游戏", PermissionService.Supported.ToArray());
    private static RuntimeAccountContext Account(string key = "account-a", bool guest = false) => new(key, 1, guest, () => true, default);
    private static RuntimeSessionServices Services(string root, RuntimeAccountContext? account = null,
        Func<RuntimeAccountContext, CancellationToken, Task<RuntimeProfile>>? profile = null,
        Func<string, JsonElement, CancellationToken, Task<object>>? data = null, DesktopExtensionService? desktop = null) => new()
    {
        Account = account ?? Account(), Application = App(), Permissions = new PermissionService(root),
        RequestPermission = (_, _) => Task.FromResult(true), GetProfile = profile ?? ((_, _) => Task.FromResult(new RuntimeProfile("派蒙测试昵称", "http://unsafe.invalid/avatar", false))),
        DataRequest = data, Desktop = desktop, Declarations = new([new("resume", "继续", "resume")], ["resume"], [new("score", "进度")])
    };
    private static RuntimeSession Session(string root, RuntimeSessionServices services, TimeProvider? timeProvider = null)
    { var value = new RuntimeSession(services.Application.Identity.AppId, services.Application.DeclaredPermissions, root, timeProvider: timeProvider, services: services); value.Foreground(); return value; }
    private static void Grant(RuntimeSessionServices services, string permission) => services.Permissions.SetDecision(services.Account, services.Application, permission, PermissionDecision.Granted);
    private static string Send(RuntimeSession session, string method, object? parameters = null) => SendAsync(session, method, parameters).GetAwaiter().GetResult();
    private static Task<string> SendAsync(RuntimeSession session, string method, object? parameters = null) => session.HandleMessageAsync(session.PageUri,
        JsonSerializer.Serialize(new { protocolVersion = 1, requestId = Guid.NewGuid().ToString("N"), method, @params = parameters ?? new { } }), (_, _) => Task.FromResult(false));
    private static JsonElement Ok(string response)
    { using var document = JsonDocument.Parse(response); Assert(document.RootElement.GetProperty("ok").GetBoolean(), response); return document.RootElement.GetProperty("result").Clone(); }
    private static void Error(string response, string code)
    { using var document = JsonDocument.Parse(response); Assert(!document.RootElement.GetProperty("ok").GetBoolean(), "Expected error: " + code); Equal(document.RootElement.GetProperty("error").GetProperty("code").GetString()!, code); }
    private static void Throws(string code, Action action)
    { try { action(); } catch (RuntimeCapabilityException error) { Equal(error.Code, code); return; } throw new InvalidOperationException("Expected " + code); }
    private static void Equal<T>(T actual, T expected) => Assert(EqualityComparer<T>.Default.Equals(actual, expected), $"Expected {expected}, got {actual}.");
    private static void Assert(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static TaskCompletionSource<T> Signal<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void InTemp(Action<string> action)
    {
        string root = Path.Combine(Path.GetTempPath(), "AutumnOS-T03-runtime-tests", Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); } finally { Directory.Delete(root, true); }
    }
    private sealed class TestClock : TimeProvider
    {
        private long ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ticks;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddTicks(ticks);
        internal void Advance(TimeSpan value) => ticks += value.Ticks;
    }
    private sealed class TestGateLease(object gate) : IDisposable
    {
        private object? held = gate;
        public void Dispose() { object? value = Interlocked.Exchange(ref held, null); if (value is not null) Monitor.Exit(value); }
    }
    private sealed class TestIdentity(IdentitySnapshot snapshot) : IIdentityService
    {
        public IdentitySnapshot Snapshot { get; private set; } = snapshot;
        public event EventHandler<IdentitySnapshot>? Changed;
        internal void Update(IdentitySnapshot value) { Snapshot = value; Changed?.Invoke(this, value); }
        public Task<IdentitySnapshot> InitializeAsync(CancellationToken cancellationToken = default) => Task.FromResult(Snapshot);
        public Task<IdentitySnapshot> SignInAsync(bool rememberSignIn, bool reauthenticate = false, CancellationToken cancellationToken = default) => throw new NotSupportedException("Explicit test snapshot only.");
        public Task<IdentitySnapshot> SetRememberSignInAsync(bool rememberSignIn, CancellationToken cancellationToken = default) => throw new NotSupportedException("Explicit test snapshot only.");
        public Task<IdentitySnapshot> RefreshAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException("Explicit test snapshot only.");
        public Task<IdentitySnapshot> SignOutAsync(bool browserSession = false, CancellationToken cancellationToken = default) => throw new NotSupportedException("Explicit test snapshot only.");
        public void CancelSignIn() { }
        public IDisposable EnterSessionLease(string accountNamespace, long sessionEpoch)
        { if (Snapshot.AccountNamespace != accountNamespace || Snapshot.SessionEpoch != sessionEpoch) throw new RuntimeCapabilityException("SESSION_EXPIRED"); return new EmptyLease(); }
        private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    }
}
