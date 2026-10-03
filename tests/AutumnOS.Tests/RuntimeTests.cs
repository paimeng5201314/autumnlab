using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

internal static class RuntimeTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("runtime.rejects_invalid_identity_and_entry", InvalidIdentity);
        yield return ("runtime.webview_profile_compact_path_preserves_complete_binding", CompactWebViewProfile);
        yield return ("runtime.webview_profile_isolates_account_application_source_and_signer", WebViewProfileIsolation);
        yield return ("runtime.webview_profile_rejects_relative_or_forged_binding", InvalidWebViewProfile);
        yield return ("runtime.navigation_initial_success_binds_only_one_trusted_native_id", InitialNavigationSuccess);
        yield return ("runtime.navigation_initial_failure_remains_failure_and_cannot_revive", InitialNavigationFailure);
        yield return ("runtime.navigation_denied_completion_preserves_original_session_and_input", DeniedNavigationPreservesSession);
        yield return ("runtime.navigation_foreign_completion_races_cannot_complete_initial_page", NavigationCompletionRace);
        yield return ("runtime.entry_and_origin_bound_to_host", SourceBinding);
        yield return ("runtime.rejects_forged_identity_and_duplicate_fields", InvalidMessages);
        yield return ("runtime.message_limit_counts_utf8_bytes", MessageLimit);
        yield return ("runtime.capabilities_report_actual_limits", Capabilities);
        yield return ("runtime.lifecycle_preserves_live_background_and_suspend", Lifecycle);
        yield return ("runtime.ended_epoch_cannot_resume", EndedSession);
        yield return ("runtime.permission_requires_manifest_declaration", UndeclaredPermission);
        yield return ("runtime.denial_does_not_reopen_prompt", DeniedPermission);
        yield return ("runtime.replayed_request_is_rejected", Replay);
        yield return ("runtime.save_persists_across_session_restart", SavePersistence);
        yield return ("runtime.save_namespaces_are_app_specific", SaveIsolation);
        yield return ("runtime.rejects_arbitrary_save_paths", InvalidSaveParameters);
        yield return ("runtime.corrupt_and_foreign_saves_preserve_bytes", CorruptSaves);
        yield return ("runtime.busy_save_is_retryable", BusySave);
        yield return ("runtime.readonly_save_preserves_commit", ReadOnlySave);
        yield return ("runtime.cancelled_request_cannot_grant_or_write", CancelledRequest);
        yield return ("runtime.close_cancels_pending_permission", ClosePendingPermission);
        yield return ("runtime.permission_request_has_finite_timeout", RequestTimeout);
        yield return ("runtime.pending_queue_is_bounded", BoundedQueue);
        yield return ("runtime.request_rate_is_bounded", RateLimit);
        yield return ("runtime.long_session_12000_requests_keeps_bounded_replay_memory", LongSession);
        yield return ("runtime.replay_window_expires_without_ending_session", ReplayExpiry);
        yield return ("runtime.pending_duplicate_never_expires_or_reexecutes", PendingReplay);
        yield return ("runtime.revoke_denies_future_save_access", RevokedPermission);
        yield return ("runtime.revoke_invalidates_pending_permission", RevokePendingPermission);
        yield return ("runtime.suspend_rejects_pending_grant", SuspendPendingPermission);
        yield return ("runtime.path_conflict_has_safe_error", SavePathConflict);
        if (OperatingSystem.IsWindows()) yield return ("runtime.save_junction_cannot_escape_namespace", SaveJunction);
    }

    private static void InitialNavigationSuccess()
    {
        var guard = new RuntimeNavigationGuard("https://bound.autumn.invalid/index.html");
        Assert(!guard.OnStarting(1, "https://foreign.invalid/"), "A foreign first navigation must not bind the initial page.");
        Assert(guard.OnCompleted(1, false) == RuntimeNavigationCompletion.Ignored && !guard.IsReady, "Unbound cancellation cannot complete or fail the initial page.");
        Assert(guard.OnStarting(2, "https://bound.autumn.invalid/index.html"), "The trusted initial native ID should be admitted.");
        Assert(guard.OnStarting(2, "https://bound.autumn.invalid/index.html"), "A repeated trusted starting event for the same ID remains bound.");
        Assert(!guard.OnStarting(3, "https://bound.autumn.invalid/index.html"), "A competing native ID cannot replace the pending initial page.");
        Assert(guard.OnCompleted(2, true) == RuntimeNavigationCompletion.InitialPageReady && guard.IsReady, "Actual initial success must be published once.");
        Assert(guard.OnCompleted(2, false) == RuntimeNavigationCompletion.Ignored && guard.IsReady, "Duplicate initial completion must not crash an already ready page.");
        Assert(!guard.OnStarting(4, "https://bound.autumn.invalid/index.html"), "Even a same-URI later navigation must not reload unsaved input.");
    }

    private static void InitialNavigationFailure()
    {
        var guard = new RuntimeNavigationGuard("https://bound.autumn.invalid/index.html");
        Assert(guard.OnStarting(5, "https://bound.autumn.invalid/index.html"), "Trusted initial ID should start.");
        Assert(!guard.OnStarting(5, "https://redirect.invalid/"), "A foreign redirect with the initial native ID must be denied.");
        Assert(guard.OnCompleted(5, false) == RuntimeNavigationCompletion.InitialPageFailed && !guard.IsReady, "A denied initial redirect/real initial failure must still fail, not be ignored.");
        Assert(guard.OnCompleted(5, true) == RuntimeNavigationCompletion.Ignored && !guard.IsReady, "Late success cannot revive a failed initial page.");
        Assert(!guard.OnStarting(6, "https://bound.autumn.invalid/index.html"), "Failed host requires a new instance, not an implicit restart.");
    }

    private static void DeniedNavigationPreservesSession() => InTemp(root =>
    {
        var session = NewSession(root); session.Foreground();
        var guard = new RuntimeNavigationGuard(session.PageUri);
        Assert(guard.OnStarting(7, session.PageUri) && guard.OnCompleted(7, true) == RuntimeNavigationCompletion.InitialPageReady, "Fixture initial page must actually become ready.");
        var original = session.Instance;
        string unsavedInput = "unchanged input " + Guid.NewGuid().ToString("N");
        foreach (ulong laterId in new ulong[] { 8, 9, 10 })
        {
            Assert(!guard.OnStarting(laterId, "https://foreign.invalid/"), "Foreign navigation must be canceled.");
            RuntimeNavigationCompletion decision = guard.OnCompleted(laterId, false);
            if (decision == RuntimeNavigationCompletion.InitialPageFailed) { session.MarkCrashed(); unsavedInput = "lost"; }
            Assert(decision == RuntimeNavigationCompletion.Ignored && guard.IsReady && session.Instance.Id == original.Id && session.Instance.State == AppLifecycleState.Foreground
                && session.Instance.BlocksMaintenance && unsavedInput.StartsWith("unchanged input ", StringComparison.Ordinal), "Denied completion must preserve running instance/input/maintenance blocker.");
        }
        session.MarkCrashed();
        Assert(session.Instance.State == AppLifecycleState.Crashed, "Real process failure remains independently observable; navigation guard must not suppress it.");
    });

    private static void NavigationCompletionRace()
    {
        var guard = new RuntimeNavigationGuard("https://bound.autumn.invalid/index.html");
        Assert(guard.OnStarting(11, "https://bound.autumn.invalid/index.html"), "Initial navigation must bind before racers.");
        int ignored = 0;
        Parallel.For(12, 44, id =>
        {
            Assert(!guard.OnStarting((ulong)id, "https://foreign.invalid/"), "Concurrent foreign start was admitted.");
            if (guard.OnCompleted((ulong)id, id % 2 == 0) == RuntimeNavigationCompletion.Ignored) Interlocked.Increment(ref ignored);
        });
        Assert(ignored == 32 && !guard.IsReady, "Foreign completions of either status cannot complete the pending initial page.");
        Assert(guard.OnCompleted(11, false) == RuntimeNavigationCompletion.InitialPageFailed && !guard.IsReady, "A real failure must survive preceding canceled-completion races.");
    }

    private static void CompactWebViewProfile() => InTemp(root =>
    {
        string runtime = Path.Combine(root, "AutumnOS_Data", "Runtime");
        string binding = RuntimeApplication.Hash("complete-binding");
        string path = WebViewProfileDirectory.ForBinding(runtime, "guest", binding);
        string expected = RuntimeApplication.Hash("autumnos.webview-profile.v1\0" + RuntimeApplication.Hash("guest") + "\0" + binding);
        Assert(path == Path.Combine(runtime, "wv", expected), "Profile identity must retain full SHA-256 and stay beneath Runtime.");
        Assert(path.Length == runtime.Length + 68, "Only one compact identity directory may be appended.");
        Assert(path == WebViewProfileDirectory.ForBinding(runtime, "guest", binding), "Restart must use the same bound cache.");
        Assert(!Directory.Exists(runtime), "Computing a cache path must not write data or migrate old files.");
    });

    private static void WebViewProfileIsolation() => InTemp(root =>
    {
        RuntimeApplication app = new(new AppIdentity("test.game", 41, "signer-a"), "github:41", "Name", []);
        string baseline = WebViewProfileDirectory.ForBinding(root, "account-a", app.BindingKey);
        string[] other =
        [
            WebViewProfileDirectory.ForBinding(root, "account-b", app.BindingKey),
            WebViewProfileDirectory.ForBinding(root, "account-a", (app with { Identity = app.Identity with { AppId = "test.other" } }).BindingKey),
            WebViewProfileDirectory.ForBinding(root, "account-a", (app with { Identity = app.Identity with { RepositoryId = 42 } }).BindingKey),
            WebViewProfileDirectory.ForBinding(root, "account-a", (app with { Identity = app.Identity with { SigningKeyFingerprint = "signer-b" } }).BindingKey),
            WebViewProfileDirectory.ForBinding(root, "account-a", (app with { Source = "local-preview:payload-a" }).BindingKey)
        ];
        Assert(other.All(path => path != baseline) && other.Distinct(StringComparer.Ordinal).Count() == other.Length,
            "Every complete account, application, source and signer binding must retain an isolated native profile.");
        Assert(baseline == WebViewProfileDirectory.ForBinding(root, "account-a", (app with { DisplayName = "Rename", DeclaredPermissions = ["saves"] }).BindingKey),
            "Display metadata must not change the bound account cache.");
    });

    private static void InvalidWebViewProfile() => InTemp(root =>
    {
        string binding = RuntimeApplication.Hash("binding");
        Throws<ArgumentException>(() => WebViewProfileDirectory.ForBinding("relative", "guest", binding));
        foreach (string invalid in new[] { "", "../other", new string('a', 63), new string('A', 64), new string('g', 64), new string('a', 65) })
            Throws<ArgumentException>(() => WebViewProfileDirectory.ForBinding(root, "guest", invalid));
        Throws<ArgumentException>(() => WebViewProfileDirectory.ForBinding(root, "", binding));
        Throws<ArgumentException>(() => WebViewProfileDirectory.ForBinding(root, new string('a', 257), binding));
        Assert(!Directory.EnumerateFileSystemEntries(root).Any(), "Rejected host bindings must create no directories.");
    });

    private static void InvalidIdentity() => InTemp(root =>
    {
        foreach (string appId in new[] { "game", "../game", "demo.1game", "con.game", "demo..game", "Demo.game", "game-demo", new string('a', 64) + ".game" })
        {
            Assert(!RuntimeSession.IsValidAppId(appId), "Invalid application identity must be rejected.");
            Throws<ArgumentException>(() => _ = new RuntimeSession(appId, [], root));
        }
        foreach (string entry in new[] { "../index.html", "/index.html", "app\\index.html", "app/con.html", "index.html?fake", "script.js", "https://evil/index.html" })
            Throws<ArgumentException>(() => _ = new RuntimeSession("test.game", [], root, entry));
        Throws<ArgumentException>(() => _ = new RuntimeSession("test.game", [], "relative"));
        Throws<ArgumentOutOfRangeException>(() => _ = new RuntimeSession("test.game", [], root, requestTimeout: TimeSpan.Zero));
    });

    private static void SourceBinding() => InTemp(root =>
    {
        var first = new RuntimeSession("test.game", [], root, "pages/play.html");
        var second = new RuntimeSession("test.game", [], root);
        Assert(first.Origin != second.Origin, "Each instance must have an independent origin.");
        Assert(first.PageUri == first.Origin + "pages/play.html", "The installed manifest entry must bind the gateway source.");
        Success(Send(first, "platform.getCapabilities"));
        foreach (string source in new[] { second.PageUri, first.Origin + "index.html", first.PageUri + "?fake", first.PageUri + "#fake", "https://evil.invalid/index.html", "file:///index.html" })
            Error(first.HandleMessageAsync(source, Request("platform.getCapabilities"), Approve).GetAwaiter().GetResult(), "SOURCE_REJECTED");
    });

    private static void InvalidMessages() => InTemp(root =>
    {
        var session = NewSession(root);
        string[] malformed =
        [
            "null", "[]", "{}", "{", "{\"protocolVersion\":1,\"requestId\":\"a\",\"method\":\"platform.getCapabilities\",\"params\":{},\"appId\":\"victim.game\"}",
            "{\"protocolVersion\":1,\"requestId\":\"a\",\"requestId\":\"b\",\"method\":\"platform.getCapabilities\",\"params\":{}}",
            "{\"protocolVersion\":1,\"requestId\":\"a\",\"method\":\"saves.write\",\"params\":{\"slot\":\"game\",\"value\":{\"score\":1,\"score\":2}}}",
            "{\"protocolVersion\":1,\"requestId\":\"bad id\",\"method\":\"platform.getCapabilities\",\"params\":{}}",
            "{\"protocolVersion\":1,\"requestId\":\"a\",\"method\":2,\"params\":{}}",
            "{\"protocolVersion\":1,\"requestId\":\"a\",\"method\":\"platform.getCapabilities\",\"params\":[]}",
            "{\"protocolVersion\":1,\"requestId\":\"a\",\"method\":\"platform.getCapabilities\",\"params\":{\"sessionEpoch\":9}}"
        ];
        foreach (string json in malformed) Error(session.HandleMessageAsync(session.PageUri, json, Approve).GetAwaiter().GetResult(), "INVALID_REQUEST");
        Error(session.HandleMessageAsync(session.PageUri, "{\"protocolVersion\":2,\"requestId\":\"other\",\"method\":\"platform.getCapabilities\",\"params\":{}}", Approve).GetAwaiter().GetResult(), "PROTOCOL_UNSUPPORTED");
        Error(Send(session, "identity.beginAppSession"), "CAPABILITY_UNAVAILABLE");
        Assert(!Directory.EnumerateFileSystemEntries(root).Any(), "Invalid messages must not create save data.");
    });

    private static void MessageLimit() => InTemp(root =>
    {
        var session = NewSession(root);
        string json = "{\"protocolVersion\":1,\"requestId\":\"bytes\",\"method\":\"saves.write\",\"params\":{\"slot\":\"game\",\"value\":\"" + new string('中', 12000) + "\"}}";
        Assert(json.Length < RuntimeSession.MaximumMessageBytes && Encoding.UTF8.GetByteCount(json) > RuntimeSession.MaximumMessageBytes, "The fixture must distinguish bytes from characters.");
        Error(session.HandleMessageAsync(session.PageUri, json, Approve).GetAwaiter().GetResult(), "MESSAGE_TOO_LARGE");
    });

    private static void Capabilities() => InTemp(root =>
    {
        JsonElement result = Success(Send(NewSession(root), "platform.getCapabilities"));
        Assert(result.GetProperty("accountMode").GetString() == "guest" && !result.GetProperty("networkSandboxVerified").GetBoolean(), "T01 must not advertise identity or a verified sandbox.");
        Assert(result.GetProperty("limits").GetProperty("maximumPendingRequests").GetInt32() == RuntimeSession.MaximumPendingRequests, "Advertised limits must match the gateway.");
        Assert(result.GetProperty("limits").GetProperty("maximumRememberedRequestIds").GetInt32() == RuntimeSession.MaximumRememberedRequestIds, "The bounded replay capacity must be advertised.");
        Assert(result.GetProperty("limits").GetProperty("deduplicationWindowMs").GetInt32() == (int)RuntimeSession.DeduplicationWindow.TotalMilliseconds, "The replay retention window must be advertised.");
    });

    private static void Lifecycle() => InTemp(root =>
    {
        var session = NewSession(root);
        AppInstanceId id = session.Instance.Id;
        Assert(session.Instance.State == AppLifecycleState.Starting && session.Instance.BlocksMaintenance, "A starting game must block maintenance.");
        session.Foreground();
        session.Background();
        Assert(session.Instance.State == AppLifecycleState.Background && session.Instance.BlocksMaintenance, "Returning home must retain the live game.");
        session.Suspend();
        Assert(session.Instance.State == AppLifecycleState.Suspended && session.Instance.BlocksMaintenance, "Suspended games still block maintenance.");
        Assert(Success(Send(session, "lifecycle.getState")).GetProperty("state").GetString() == "suspended", "Suspension must be queryable.");
        Error(Send(session, "saves.read", new { slot = "game" }), "SESSION_SUSPENDED");
        session.Resume();
        Assert(session.Instance.State == AppLifecycleState.Foreground && session.Instance.Id == id, "Resume must retain the instance identity.");
    });

    private static void EndedSession() => InTemp(root =>
    {
        foreach (bool crash in new[] { false, true })
        {
            var session = NewSession(root);
            if (crash) session.MarkCrashed(); else session.Close();
            Assert(!session.Instance.BlocksMaintenance && session.Instance.Epoch.Value == 2, "Ending must invalidate the epoch and release live-game state.");
            session.Close(); session.MarkCrashed();
            Assert(session.Instance.Epoch.Value == 2, "Ending again must be idempotent.");
            Throws<InvalidOperationException>(session.Resume);
            Error(Send(session, "platform.getCapabilities"), "SESSION_EXPIRED");
        }
    });

    private static void UndeclaredPermission() => InTemp(root =>
    {
        var session = new RuntimeSession("test.game", [], root);
        int prompts = 0;
        Error(Send(session, "permissions.request", new { name = "saves" }, permission: (_, _) => { prompts++; return Task.FromResult(true); }), "PERMISSION_NOT_DECLARED");
        Error(Send(session, "saves.read", new { slot = "game" }), "PERMISSION_NOT_DECLARED");
        Assert(prompts == 0 && !Directory.EnumerateFileSystemEntries(root).Any(), "Undeclared permissions must never prompt or create data.");
    });

    private static void DeniedPermission() => InTemp(root =>
    {
        var session = NewSession(root);
        int prompts = 0;
        Task<bool> Deny(string _, CancellationToken token) { prompts++; return Task.FromResult(false); }
        for (int i = 0; i < 3; i++) Assert(Success(Send(session, "permissions.request", new { name = "saves" }, permission: Deny)).GetProperty("state").GetString() == "denied", "Denied permission must remain denied.");
        Error(Send(session, "saves.write", new { slot = "game", value = 7 }), "PERMISSION_DENIED");
        Assert(prompts == 1, "Repeated requests cannot harass the user with new prompts.");
    });

    private static void Replay() => InTemp(root =>
    {
        var session = NewSession(root);
        Success(Send(session, "permissions.request", new { name = "saves" }, id: "same"));
        Error(Send(session, "saves.write", new { slot = "game", value = 10 }, id: "same"), "DUPLICATE_REQUEST");
        Assert(!File.Exists(SavePath(root)), "Replayed IDs must not perform new side effects.");
    });

    private static void SavePersistence() => InTemp(root =>
    {
        var first = NewSession(root); Grant(first);
        Success(Send(first, "saves.write", new { slot = "game", value = new { matches = 5, title = "元素 配对" } }));
        byte[] before = File.ReadAllBytes(SavePath(root));
        first.Close();
        var second = NewSession(root);
        Error(Send(second, "saves.read", new { slot = "game" }), "PERMISSION_DENIED");
        Grant(second);
        JsonElement result = Success(Send(second, "saves.read", new { slot = "game" }));
        Assert(result.GetProperty("exists").GetBoolean() && result.GetProperty("value").GetProperty("matches").GetInt32() == 5, "The guest save must survive a new runtime instance.");
        Assert(File.ReadAllBytes(SavePath(root)).SequenceEqual(before), "Reading or closing must preserve save bytes.");
        Assert(!Directory.EnumerateFiles(Path.GetDirectoryName(SavePath(root))!, "*.tmp").Any(), "Successful atomic writes must leave no temporary files.");
    });

    private static void SaveIsolation() => InTemp(root =>
    {
        var first = NewSession(root); Grant(first);
        Success(Send(first, "saves.write", new { slot = "game", value = 123 }));
        var second = new RuntimeSession("other.game", ["saves"], root); Grant(second);
        Assert(!Success(Send(second, "saves.read", new { slot = "game" })).GetProperty("exists").GetBoolean(), "Other apps cannot read an existing app's save.");
        Success(Send(second, "saves.write", new { slot = "game", value = 456 }));
        Assert(Success(Send(first, "saves.read", new { slot = "game" })).GetProperty("value").GetInt32() == 123, "Other app writes must not modify this app's namespace.");
    });

    private static void InvalidSaveParameters() => InTemp(root =>
    {
        var session = NewSession(root); Grant(session);
        foreach (object parameters in new object[] { new { slot = "../other", value = 1 }, new { slot = "C:/game", value = 1 }, new { slot = "game", value = 1, accountMode = "other" }, new { slot = 1, value = 1 }, new { slot = "game" } })
            Error(Send(session, "saves.write", parameters), "INVALID_REQUEST");
        Assert(!File.Exists(SavePath(root)), "Invalid logical slots must not write files.");
    });

    private static void CorruptSaves() => InTemp(root =>
    {
        var session = NewSession(root); Grant(session);
        string[] corrupt = ["{", "null", "[]", "{}", new string('x', 129 * 1024),
            "{\"schemaVersion\":2,\"appId\":\"test.game\",\"accountMode\":\"guest\",\"slot\":\"game\",\"value\":1}",
            "{\"schemaVersion\":1,\"appId\":\"other.game\",\"accountMode\":\"guest\",\"slot\":\"game\",\"value\":1}",
            "{\"schemaVersion\":1,\"appId\":\"test.game\",\"accountMode\":\"account\",\"slot\":\"game\",\"value\":1}",
            "{\"schemaVersion\":1,\"appId\":\"test.game\",\"accountMode\":\"guest\",\"slot\":\"game\",\"value\":{\"score\":1,\"score\":2}}"];
        Directory.CreateDirectory(Path.GetDirectoryName(SavePath(root))!);
        foreach (string content in corrupt)
        {
            File.WriteAllText(SavePath(root), content);
            byte[] before = File.ReadAllBytes(SavePath(root));
            Error(Send(session, "saves.read", new { slot = "game" }), "SAVE_CORRUPT");
            Error(Send(session, "saves.write", new { slot = "game", value = 0 }), "SAVE_CORRUPT");
            Assert(File.ReadAllBytes(SavePath(root)).SequenceEqual(before), "Invalid saves must retain every byte.");
        }
    });

    private static void BusySave() => InTemp(root =>
    {
        var session = NewSession(root); Grant(session);
        Success(Send(session, "saves.write", new { slot = "game", value = 1 }));
        using (new FileStream(Path.Combine(Path.GetDirectoryName(SavePath(root))!, ".game.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert(Error(Send(session, "saves.write", new { slot = "game", value = 2 }), "SAVE_BUSY").GetProperty("retryable").GetBoolean(), "Lock contention should allow a later retry.");
        Assert(Success(Send(session, "saves.read", new { slot = "game" })).GetProperty("value").GetInt32() == 1, "A busy save must retain the last commit.");
        Success(Send(session, "saves.write", new { slot = "game", value = 3 }));
    });

    private static void ReadOnlySave() => InTemp(root =>
    {
        var session = NewSession(root); Grant(session);
        Success(Send(session, "saves.write", new { slot = "game", value = 1 }));
        byte[] before = File.ReadAllBytes(SavePath(root));
        File.SetAttributes(SavePath(root), FileAttributes.ReadOnly);
        try
        {
            Error(Send(session, "saves.write", new { slot = "game", value = 2 }), "STORAGE_IO_ERROR");
            Assert(File.ReadAllBytes(SavePath(root)).SequenceEqual(before), "Read-only failure must preserve the durable save.");
        }
        finally { File.SetAttributes(SavePath(root), FileAttributes.Normal); }
    });

    private static void CancelledRequest() => InTemp(root =>
    {
        var session = NewSession(root);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        int prompts = 0;
        Error(Send(session, "permissions.request", new { name = "saves" }, permission: (_, _) => { prompts++; return Task.FromResult(true); }, cancellationToken: cancelled.Token), "USER_CANCELLED");
        Assert(prompts == 0, "A cancelled request must not open a permission prompt.");
        Grant(session);
        Error(Send(session, "saves.write", new { slot = "game", value = 1 }, cancellationToken: cancelled.Token), "USER_CANCELLED");
        Assert(!File.Exists(SavePath(root)), "Cancelled writes cannot produce a save.");
    });

    private static void ClosePendingPermission() => InTemp(root =>
    {
        var session = NewSession(root);
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> request = session.HandleMessageAsync(session.PageUri, Request("permissions.request", new { name = "saves" }), (_, _) => approval.Task);
        session.Close();
        Error(request.GetAwaiter().GetResult(), "SESSION_EXPIRED");
        approval.SetResult(true);
        Error(Send(session, "saves.write", new { slot = "game", value = 1 }), "SESSION_EXPIRED");
        Assert(!File.Exists(SavePath(root)), "Late permission results cannot revive an ended session.");
    });

    private static void RequestTimeout() => InTemp(root =>
    {
        var session = new RuntimeSession("test.game", ["saves"], root, requestTimeout: TimeSpan.FromMilliseconds(100));
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken callbackToken = default;
        Task<string> request = session.HandleMessageAsync(session.PageUri, Request("permissions.request", new { name = "saves" }), (_, token) => { callbackToken = token; return approval.Task; });
        Error(request.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(), "REQUEST_TIMEOUT");
        Assert(callbackToken.IsCancellationRequested, "The host prompt must receive timeout cancellation.");
        approval.SetResult(true);
        Assert(Success(Send(session, "permissions.query", new { name = "saves" })).GetProperty("state").GetString() == "prompt", "A late approval cannot grant a timed-out permission request.");
    });

    private static void BoundedQueue() => InTemp(root =>
    {
        var session = NewSession(root);
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        List<Task<string>> requests = [session.HandleMessageAsync(session.PageUri, Request("permissions.request", new { name = "saves" }), (_, _) => approval.Task)];
        for (int i = 1; i < RuntimeSession.MaximumPendingRequests; i++) requests.Add(session.HandleMessageAsync(session.PageUri, Request("lifecycle.getState"), Approve));
        Error(Send(session, "lifecycle.getState"), "REQUEST_BUSY");
        session.Close();
        foreach (Task<string> request in requests) Error(request.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult(), "SESSION_EXPIRED");
        approval.SetResult(true);
    });

    private static void RateLimit() => InTemp(root =>
    {
        var clock = new ManualTimestampProvider();
        var session = new RuntimeSession("test.game", [], root, timeProvider: clock);
        for (int i = 0; i < RuntimeSession.MaximumRequestsPerMinute; i++) Success(Send(session, "lifecycle.getState"));
        Error(Send(session, "lifecycle.getState"), "RATE_LIMITED");
        clock.Advance(TimeSpan.FromMinutes(1) - TimeSpan.FromTicks(1));
        Error(Send(session, "lifecycle.getState"), "RATE_LIMITED");
        clock.Advance(TimeSpan.FromTicks(1));
        Success(Send(session, "lifecycle.getState"));
    });

    private static void LongSession() => InTemp(root =>
    {
        var clock = new ManualTimestampProvider();
        var session = new RuntimeSession("test.game", ["saves"], root, timeProvider: clock);
        AppInstanceId instanceId = session.Instance.Id;
        for (int index = 0; index < 12000; index++)
        {
            Success(Send(session, "lifecycle.getState", id: "long_" + index));
            clock.Advance(TimeSpan.FromMilliseconds(600));
        }
        Assert(session.Instance.Id == instanceId && session.Instance.BlocksMaintenance, "Long sessions must not be replaced or ended to recover request capacity.");
        // Inspect only retained collection counts: successful calls alone do not prove bounded memory.
        var seen = (HashSet<string>)typeof(RuntimeSession).GetField("seenIds", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(session)!;
        Assert(seen.Count > 900 && seen.Count <= 1000 && seen.Count <= RuntimeSession.MaximumRememberedRequestIds,
            "The rolling replay cache must retain the recent window while retiring historical IDs.");
        Error(Send(session, "lifecycle.getState", id: "long_11999"), "DUPLICATE_REQUEST");
        Grant(session);
        Success(Send(session, "saves.write", new { slot = "game", value = new { calls = 12000 } }));
        Assert(Success(Send(session, "saves.read", new { slot = "game" })).GetProperty("value").GetProperty("calls").GetInt32() == 12000,
            "Permissions and real save operations must still work after the previous lifetime ceiling.");
    });

    private static void ReplayExpiry() => InTemp(root =>
    {
        var clock = new ManualTimestampProvider();
        var session = new RuntimeSession("test.game", [], root, timeProvider: clock);
        Success(Send(session, "lifecycle.getState", id: "remembered"));
        clock.Advance(RuntimeSession.DeduplicationWindow - TimeSpan.FromTicks(1));
        Error(Send(session, "lifecycle.getState", id: "remembered"), "DUPLICATE_REQUEST");
        clock.Advance(TimeSpan.FromTicks(1));
        // Protocol callers must still use fresh IDs: the bounded cache is not permanent exactly-once delivery.
        Success(Send(session, "lifecycle.getState", id: "remembered"));
        Error(Send(session, "lifecycle.getState", id: "remembered"), "DUPLICATE_REQUEST");
    });

    private static void PendingReplay() => InTemp(root =>
    {
        var clock = new ManualTimestampProvider();
        var session = new RuntimeSession("test.game", ["saves"], root, timeProvider: clock);
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int prompts = 0;
        string request = Request("permissions.request", new { name = "saves" }, "pending");
        Task<string> original = session.HandleMessageAsync(session.PageUri, request, (_, _) => { prompts++; return approval.Task; });
        clock.Advance(RuntimeSession.DeduplicationWindow + TimeSpan.FromMinutes(1));
        Task<string> replay = session.HandleMessageAsync(session.PageUri, request, (_, _) => { prompts++; return Task.FromResult(true); });
        Error(replay.WaitAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult(), "DUPLICATE_REQUEST");
        approval.SetResult(true); Success(original.GetAwaiter().GetResult());
        Assert(prompts == 1, "Concurrent replay must not queue a second permission operation.");
        clock.Advance(RuntimeSession.DeduplicationWindow - TimeSpan.FromTicks(1));
        Error(Send(session, "lifecycle.getState", id: "pending"), "DUPLICATE_REQUEST");
        clock.Advance(TimeSpan.FromTicks(1));
        Success(Send(session, "lifecycle.getState", id: "pending"));
    });

    private static void RevokedPermission() => InTemp(root =>
    {
        var session = NewSession(root); Grant(session);
        Success(Send(session, "saves.write", new { slot = "game", value = 1 }));
        byte[] before = File.ReadAllBytes(SavePath(root));
        session.RevokeSavePermission();
        Assert(Success(Send(session, "permissions.query", new { name = "saves" })).GetProperty("state").GetString() == "revoked", "Revocation must be visible to the app.");
        Error(Send(session, "saves.read", new { slot = "game" }), "PERMISSION_REVOKED");
        Error(Send(session, "saves.write", new { slot = "game", value = 2 }), "PERMISSION_REVOKED");
        Error(Send(session, "permissions.request", new { name = "saves" }), "PERMISSION_REVOKED");
        Assert(File.ReadAllBytes(SavePath(root)).SequenceEqual(before), "Revocation must not delete or overwrite user data.");
    });

    private static void RevokePendingPermission() => InTemp(root =>
    {
        var session = NewSession(root);
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> request = session.HandleMessageAsync(session.PageUri, Request("permissions.request", new { name = "saves" }), (_, _) => approval.Task);
        session.RevokeSavePermission(); approval.SetResult(true);
        Error(request.GetAwaiter().GetResult(), "PERMISSION_REVOKED");
        Error(Send(session, "saves.read", new { slot = "game" }), "PERMISSION_REVOKED");
    });

    private static void SuspendPendingPermission() => InTemp(root =>
    {
        var session = NewSession(root);
        var approval = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<string> request = session.HandleMessageAsync(session.PageUri, Request("permissions.request", new { name = "saves" }), (_, _) => approval.Task);
        session.Suspend(); session.Resume(); approval.SetResult(true);
        Error(request.GetAwaiter().GetResult(), "SESSION_SUSPENDED");
        Assert(Success(Send(session, "permissions.query", new { name = "saves" })).GetProperty("state").GetString() == "prompt", "Suspending then resuming must still discard the earlier pending grant.");
    });

    private static void SavePathConflict() => InTemp(root =>
    {
        File.WriteAllText(Path.Combine(root, "test.game"), "preserve");
        var session = NewSession(root); Grant(session);
        string response = Send(session, "saves.read", new { slot = "game" });
        Error(response, "STORAGE_IO_ERROR");
        Assert(!response.Contains(root, StringComparison.OrdinalIgnoreCase), "SDK errors must not expose host paths.");
        Assert(File.ReadAllText(Path.Combine(root, "test.game")) == "preserve", "Conflicting files must not be deleted.");
    });

    private static void SaveJunction() => InTemp(root =>
    {
        string outside = Path.Combine(root, "outside"); Directory.CreateDirectory(outside);
        string link = Path.Combine(root, "test.game"); CreateJunction(link, outside);
        try
        {
            var session = NewSession(root); Grant(session);
            Error(Send(session, "saves.write", new { slot = "game", value = 1 }), "STORAGE_UNSAFE_PATH");
            Assert(!Directory.EnumerateFileSystemEntries(outside).Any(), "A redirected save directory cannot receive writes.");
        }
        finally { Directory.Delete(link, recursive: false); }
    });

    private static RuntimeSession NewSession(string root) => new("test.game", ["saves"], root);
    private sealed class ManualTimestampProvider : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref timestamp, elapsed.Ticks);
    }
    private static Task<bool> Approve(string _, CancellationToken cancellationToken) => Task.FromResult(true);
    private static void Grant(RuntimeSession session) => Success(Send(session, "permissions.request", new { name = "saves" }));
    private static string SavePath(string root) => Path.Combine(root, "test.game", "guest", "game.json");
    private static string Request(string method, object? parameters = null, string? id = null) => JsonSerializer.Serialize(new { protocolVersion = 1, requestId = id ?? Guid.NewGuid().ToString("N"), method, @params = parameters ?? new { } });
    private static string Send(RuntimeSession session, string method, object? parameters = null, string? id = null,
        Func<string, CancellationToken, Task<bool>>? permission = null, CancellationToken cancellationToken = default) =>
        session.HandleMessageAsync(session.PageUri, Request(method, parameters, id), permission ?? Approve, cancellationToken).GetAwaiter().GetResult();

    private static JsonElement Success(string response)
    {
        using JsonDocument json = JsonDocument.Parse(response);
        Assert(json.RootElement.GetProperty("ok").GetBoolean(), "Expected success: " + response);
        return json.RootElement.GetProperty("result").Clone();
    }

    private static JsonElement Error(string response, string code)
    {
        using JsonDocument json = JsonDocument.Parse(response);
        Assert(!json.RootElement.GetProperty("ok").GetBoolean(), "Expected error " + code);
        JsonElement error = json.RootElement.GetProperty("error");
        Assert(error.GetProperty("code").GetString() == code, "Expected " + code + ": " + response);
        Assert(error.GetProperty("correlationId").TryGetGuid(out _), "Errors must have a correlation ID.");
        return error.Clone();
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    private static void CreateJunction(string link, string target)
    {
        if (link.IndexOfAny(['"', '%', '\r', '\n']) >= 0 || target.IndexOfAny(['"', '%', '\r', '\n']) >= 0)
            throw new InvalidOperationException("Unsafe temporary path.");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        { Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Cannot create test junction.");
        process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert(process.ExitCode == 0, "Test junction creation must succeed.");
    }

    private static void InTemp(Action<string> action)
    {
        string parent = Path.GetFullPath(Path.GetTempPath());
        string root = Path.Combine(parent, "AutumnOS-runtime-tests-中文 空格-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            string canonical = Path.GetFullPath(root);
            if (!canonical.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(canonical).StartsWith("AutumnOS-runtime-tests-中文 空格-", StringComparison.Ordinal)) throw new InvalidOperationException("Unverified test cleanup path.");
            Directory.Delete(canonical, recursive: true);
        }
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
