using System.Runtime.Versioning;
using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Identity;
using AutumnOS.Storage;

namespace AutumnOS.Tests;

/// <summary>Local coordination/credential fixtures; no real identity or Windows updater result is implied.</summary>
public static class MaintenanceTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("maintenance.t05.all_game_states_and_closed_resources_block", GameLifetimes);
        yield return ("maintenance.t05.live_game_defers_without_blocking_its_saves", LiveGameDefers);
        yield return ("maintenance.t05.preparation_blocks_new_launch_and_write", Preparation);
        yield return ("maintenance.t05.cancel_reopens_without_releasing_existing_write", CancelPreparation);
        yield return ("maintenance.t05.timeout_reopens_without_aborting_work", TimeoutPreparation);
        yield return ("maintenance.t05.launch_versus_maintenance_atomic_race", AdmissionRace);
        yield return ("maintenance.t05.disposal_is_idempotent_and_diagnostics_fail_closed", Idempotent);
        yield return ("maintenance.t05.desktop_first_run_layout_refuse_writes_preserve_bytes", ConfigurationProtection);
        yield return ("maintenance.t05.actual_save_commit_drains_before_maintenance", ActualSaveDrain);
        yield return ("maintenance.t05.configuration_migration_restore_named_leases", ConfigurationMigrations);
        if (OperatingSystem.IsWindows())
        {
            yield return ("maintenance.t05.identity_fixture_login_includes_callback_and_commit", IdentityLogin);
            yield return ("maintenance.t05.identity_fixture_refresh_rotation_drains_before_maintenance", IdentityRefresh);
            yield return ("maintenance.t05.identity_fixture_new_actions_defer_preserve_credentials", IdentityDeferred);
            yield return ("maintenance.t05.real_dpapi_fixture_vault_write_clear_denied_during_maintenance", CredentialVaultProtection);
        }
    }

    private static void Assert(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void Reject(Action action, string expected)
    {
        try { action(); }
        catch (DataStoreException error) when (error.Code == expected) { return; }
        throw new InvalidOperationException("Expected rejection: " + expected);
    }
    private static void GameLifetimes()
    {
        var coordinator = new CriticalOperationCoordinator();
        var instance = new AppInstance(new(Guid.NewGuid()), new("test.game", null, null), true, AppLifecycleState.Starting, new(1));
        IDisposable game = coordinator.EnterGame("test.game", () => instance);
        foreach (AppLifecycleState state in Enum.GetValues<AppLifecycleState>())
        {
            instance = instance with { State = state };
            Assert(coordinator.TryEnterMaintenance() is null, $"Lease released from label {state} before resources.");
            Assert(coordinator.GetSnapshot().ActiveGames == 1 && coordinator.GetSnapshot().WaitingReasons.Any(r => r.Contains("test.game")), "Game wait missing.");
        }
        game.Dispose();
        using IDisposable? maintenance = coordinator.TryEnterMaintenance();
        Assert(maintenance is not null, "Released resources still block.");
    }
    private static void LiveGameDefers()
    {
        var coordinator = new CriticalOperationCoordinator();
        using IDisposable game = coordinator.EnterGame("test.game");
        Reject(() => coordinator.PrepareMaintenanceAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult(), "MAINTENANCE_GAME_ACTIVE");
        Assert(!coordinator.IsPreparing, "Game wait froze its save entry.");
        using IDisposable save = coordinator.EnterWrite("游戏存档写入");
        Assert(coordinator.ActiveWrites == 1, "Game cannot save while update waits.");
    }
    private static void Preparation()
    {
        var coordinator = new CriticalOperationCoordinator();
        IDisposable write = coordinator.EnterWrite("存档恢复");
        Task<IDisposable> pending = coordinator.PrepareMaintenanceAsync(TimeSpan.FromSeconds(3));
        Assert(coordinator.IsPreparing && !pending.IsCompleted, "Did not wait for actual operation.");
        Reject(() => coordinator.EnterGame("test.game"), "MAINTENANCE_IN_PROGRESS");
        Reject(() => coordinator.EnterWrite("配置写入"), "MAINTENANCE_IN_PROGRESS");
        Assert(coordinator.GetSnapshot().WaitingReasons.Any(r => r.Contains("存档恢复")), "Specific waiting reason missing.");
        Reject(() => coordinator.PrepareMaintenanceAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult(), "MAINTENANCE_IN_PROGRESS");
        Assert(coordinator.IsPreparing, "Second preparation cleared the first owner's state.");
        write.Dispose();
        using IDisposable lease = pending.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        Assert(coordinator.IsMaintenance && !coordinator.IsPreparing && coordinator.ActiveWrites == 0, "No exclusive recheck.");
        Reject(() => coordinator.EnterGame("test.game"), "MAINTENANCE_IN_PROGRESS");
    }
    private static void CancelPreparation()
    {
        var coordinator = new CriticalOperationCoordinator();
        using IDisposable write = coordinator.EnterWrite("凭据轮换");
        using var cancelled = new CancellationTokenSource();
        Task<IDisposable> pending = coordinator.PrepareMaintenanceAsync(TimeSpan.FromSeconds(3), cancelled.Token);
        cancelled.Cancel();
        try { pending.GetAwaiter().GetResult(); throw new InvalidOperationException("Cancellation ignored."); }
        catch (OperationCanceledException) { }
        Assert(!coordinator.IsPreparing && !coordinator.IsMaintenance && coordinator.ActiveWrites == 1, "Cancellation altered an admitted operation.");
        using IDisposable game = coordinator.EnterGame("test.game");
    }
    private static void TimeoutPreparation()
    {
        var coordinator = new CriticalOperationCoordinator();
        using IDisposable write = coordinator.EnterWrite("长操作");
        try { coordinator.PrepareMaintenanceAsync(TimeSpan.FromMilliseconds(30)).GetAwaiter().GetResult(); throw new InvalidOperationException("Timeout ignored."); }
        catch (TimeoutException) { }
        Assert(!coordinator.IsPreparing && coordinator.ActiveWrites == 1, "Timeout cancelled work or kept admission closed.");
        using IDisposable another = coordinator.EnterWrite("后续操作");
    }
    private static void AdmissionRace()
    {
        for (int i = 0; i < 160; i++)
        {
            var coordinator = new CriticalOperationCoordinator();
            using var start = new ManualResetEventSlim();
            Task<IDisposable?> launch = Task.Run(() =>
            {
                start.Wait();
                try { return coordinator.EnterGame("test.race"); }
                catch (DataStoreException error) when (error.Code == "MAINTENANCE_IN_PROGRESS") { return null; }
            });
            Task<IDisposable?> update = Task.Run(() => { start.Wait(); return coordinator.TryEnterMaintenance(); });
            start.Set();
            Task.WhenAll(launch, update).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            try { Assert((launch.Result is not null) != (update.Result is not null), "Launch and maintenance admission were not exclusive."); }
            finally { launch.Result?.Dispose(); update.Result?.Dispose(); }
        }
    }
    private static void Idempotent()
    {
        var coordinator = new CriticalOperationCoordinator();
        IDisposable game = coordinator.EnterGame("test.failure", () => throw new InvalidOperationException("diagnostic fixture"));
        Assert(coordinator.GetSnapshot().ActiveGames == 1 && coordinator.TryEnterMaintenance() is null, "Snapshot failure opened gate.");
        game.Dispose(); game.Dispose();
        IDisposable write = coordinator.EnterWrite(); write.Dispose(); write.Dispose();
        IDisposable lease = coordinator.TryEnterMaintenance()!; lease.Dispose(); lease.Dispose();
        Assert(coordinator.GetSnapshot().IsIdle, "Double dispose corrupted counts.");
    }
    private static void InRoot(Action<InstallationRoot> action)
    {
        string path = Path.Combine(Path.GetTempPath(), "AutumnOS-maintenance-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try { var root = new InstallationRoot(path); Assert(root.EnsureCreated().Success, "Fixture root unavailable."); action(root); }
        finally { Directory.Delete(path, true); }
    }
    private static void ConfigurationProtection() => InRoot(root =>
    {
        var coordinator = new CriticalOperationCoordinator();
        var preferences = new DesktopPreferencesStore(root, coordinator);
        var layout = new DesktopLayoutStore(root, coordinator);
        var firstRun = new FirstRunStateStore(root, coordinator);
        Assert(preferences.Save("light", "warm").Success && layout.Save(["app.one"]).Success && firstRun.Advance(FirstRunCheckpoint.Hello).Success, "Initial fixture save failed.");
        string[] paths = [preferences.StateFilePath, layout.StateFilePath, firstRun.StateFilePath];
        byte[][] before = paths.Select(File.ReadAllBytes).ToArray();
        using (IDisposable lease = coordinator.TryEnterMaintenance()!)
        {
            Assert(preferences.Save("dark", "warm").ErrorCode == "MAINTENANCE_IN_PROGRESS", "Appearance escaped gate.");
            Assert(layout.Save(["app.two"]).ErrorCode == "MAINTENANCE_IN_PROGRESS", "Layout escaped gate.");
            Assert(firstRun.Advance(FirstRunCheckpoint.Brand).ErrorCode == "MAINTENANCE_IN_PROGRESS", "First run escaped gate.");
            for (int i = 0; i < paths.Length; i++) Assert(before[i].SequenceEqual(File.ReadAllBytes(paths[i])), "Maintenance rewrote configuration.");
        }
        Assert(preferences.Save("dark", "warm").Success, "Configuration did not recover after lease release.");
    });
    private static void ActualSaveDrain() => InRoot(root =>
    {
        var coordinator = new CriticalOperationCoordinator();
        Task<IDisposable>? maintenance = null;
        var scope = new StorageScope(new("test.drain", 1, null), "guest", new(Guid.NewGuid()), new(1));
        var data = new AccountDataStore(root, scope, _ => true, coordinator: coordinator, testHooks: new(point =>
        {
            if (point != StorageWritePoint.BeforeCommit) return;
            maintenance = coordinator.PrepareMaintenanceAsync(TimeSpan.FromSeconds(3));
            Assert(!maintenance.IsCompleted && coordinator.ActiveWrites == 1, "Maintenance crossed an actual precommit.");
        }));
        Assert(data.WriteSave("game", JsonSerializer.SerializeToElement(new { score = 42 })).Success, "Admitted save was aborted.");
        using IDisposable lease = maintenance!.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        Assert(data.ReadSave("game").Value!.Value.GetProperty("score").GetInt32() == 42, "Committed save not durable.");
        Assert(data.WriteSave("game", JsonSerializer.SerializeToElement(43)).ErrorCode == "MAINTENANCE_IN_PROGRESS", "Post-maintenance save admitted.");
    });
    private static void ConfigurationMigrations() => InRoot(root =>
    {
        var coordinator = new CriticalOperationCoordinator();
        var previous = new VersionedConfigurationStore(root, "maintenance-fixture", 1, coordinator);
        Assert(previous.Save(JsonSerializer.SerializeToElement(new { value = 1 })).Success, "Fixture config missing.");
        var next = new VersionedConfigurationStore(root, "maintenance-fixture", 2, coordinator, new(point =>
        {
            Assert(coordinator.ActiveWrites == 1 && coordinator.TryEnterMaintenance() is null, "Configuration operation has no lease.");
        }));
        Assert(next.Migrate(1, value =>
        {
            Assert(coordinator.GetSnapshot().WaitingReasons.Any(r => r.Contains("配置迁移")), "Migration reason missing.");
            return value;
        }).Success, "Configuration migration failed.");
        using (IDisposable lease = coordinator.TryEnterMaintenance()!) Assert(next.RestoreBackup(true).ErrorCode == "MAINTENANCE_IN_PROGRESS", "Restore escaped maintenance.");
        Assert(next.RestoreBackup(true).Success, "Restore could not run when idle.");
    });

    [SupportedOSPlatform("windows")]
    private static LogtoPublicOptions Options() => new(new("https://identity.example.org/"), new("https://identity.example.org/oidc"),
        new("https://identity.example.org/oidc/.well-known/openid-configuration"), "maintenance-fixture",
        new("http://127.0.0.1:17853/callback/"), new("http://127.0.0.1:17853/logout-callback/"), ["openid", "profile"], ["offline_access"]);
    private static ProtectedIdentitySession Session() => new(1, "https://identity.example.org/oidc", "maintenance-fixture", "fixture-subject",
        "测试身份", null, "ephemeral-fixture-access", "ephemeral-fixture-refresh", "ephemeral-fixture-identity", DateTimeOffset.UtcNow.AddHours(1), "maintenance-fixture-nonce-12345678");
    [SupportedOSPlatform("windows")]
    private static void IdentityLogin()
    {
        var coordinator = new CriticalOperationCoordinator();
        var flow = new FixtureFlow { PendingLogin = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        var vault = new FixtureVault { OnWrite = () => Assert(coordinator.IsPreparing && coordinator.ActiveWrites == 1, "Login commit detached from protocol lease.") };
        using var identity = new LogtoIdentityService(Options(), vault, flow, enterCriticalOperation: reason => coordinator.EnterWrite(reason));
        Task<IdentitySnapshot> login = identity.SignInAsync(true);
        Assert(flow.LoginCalls == 1 && coordinator.ActiveWrites == 1, "Callback transaction not admitted.");
        Task<IDisposable> pending = coordinator.PrepareMaintenanceAsync(TimeSpan.FromSeconds(3));
        Assert(!pending.IsCompleted, "Maintenance did not wait for callback.");
        flow.PendingLogin.SetResult(Session());
        Assert(login.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult().State == IdentitySessionState.SignedIn, "Fixture login lost after maintenance preparation.");
        using IDisposable lease = pending.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        Assert(vault.Writes == 1 && vault.Value is not null, "Verified fixture session not committed before maintenance.");
    }
    [SupportedOSPlatform("windows")]
    private static void IdentityRefresh()
    {
        var coordinator = new CriticalOperationCoordinator();
        var flow = new FixtureFlow(); var vault = new FixtureVault();
        using var identity = new LogtoIdentityService(Options(), vault, flow, enterCriticalOperation: reason => coordinator.EnterWrite(reason));
        Assert(identity.SignInAsync(true).GetAwaiter().GetResult().State == IdentitySessionState.SignedIn, "Fixture initial sign-in failed.");
        flow.PendingRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<IdentitySnapshot> refresh = identity.RefreshAsync();
        Task<IDisposable> pending = coordinator.PrepareMaintenanceAsync(TimeSpan.FromSeconds(3));
        Assert(!pending.IsCompleted && coordinator.GetSnapshot().WaitingReasons.Any(r => r.Contains("凭据轮换")), "Refresh network phase not protected.");
        vault.OnWrite = () => Assert(coordinator.IsPreparing && coordinator.ActiveWrites == 1, "Rotated commit lost lease.");
        flow.PendingRefresh.SetResult(Session() with { RefreshToken = "rotated-ephemeral-fixture-refresh" });
        refresh.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        using IDisposable lease = pending.WaitAsync(TimeSpan.FromSeconds(3)).GetAwaiter().GetResult();
        Assert(vault.Value?.RefreshToken == "rotated-ephemeral-fixture-refresh", "Rotation was not durable before maintenance.");
    }
    [SupportedOSPlatform("windows")]
    private static void IdentityDeferred()
    {
        var coordinator = new CriticalOperationCoordinator(); var vault = new FixtureVault(); var flow = new FixtureFlow();
        using var identity = new LogtoIdentityService(Options(), vault, flow, enterCriticalOperation: reason => coordinator.EnterWrite(reason));
        IdentitySnapshot original = identity.SignInAsync(true).GetAwaiter().GetResult();
        using IDisposable lease = coordinator.TryEnterMaintenance()!;
        int writes = vault.Writes, clears = vault.Clears;
        foreach (IdentitySnapshot deferred in new[] { identity.SignInAsync(true).GetAwaiter().GetResult(), identity.RefreshAsync().GetAwaiter().GetResult(),
            identity.SetRememberSignInAsync(false).GetAwaiter().GetResult(), identity.SignOutAsync().GetAwaiter().GetResult() })
            Assert(deferred.ErrorCode == "MAINTENANCE_IN_PROGRESS" && deferred.State == original.State && deferred.SessionEpoch == original.SessionEpoch, "Refusal mutated account or silently succeeded.");
        Assert(vault.Writes == writes && vault.Clears == clears && flow.LoginCalls == 1 && flow.RefreshCalls == 0, "Deferred action changed credentials or contacted provider.");
    }
    [SupportedOSPlatform("windows")]
    private static void CredentialVaultProtection() => InRoot(root =>
    {
        var coordinator = new CriticalOperationCoordinator();
        var vault = new WindowsCredentialVault(root.DataDirectory, Options(), reason => coordinator.EnterWrite(reason));
        vault.Write(Session());
        string path = Path.Combine(root.DataDirectory, "Identity", "session.dpapi");
        byte[] before = File.ReadAllBytes(path);
        using (IDisposable lease = coordinator.TryEnterMaintenance()!)
        {
            Reject(() => vault.Write(Session() with { RefreshToken = "new-fixture" }), "MAINTENANCE_IN_PROGRESS");
            Reject(vault.Clear, "MAINTENANCE_IN_PROGRESS");
            Reject(() => vault.Read(), "MAINTENANCE_IN_PROGRESS");
            Assert(before.SequenceEqual(File.ReadAllBytes(path)), "Maintenance changed DPAPI fixture.");
        }
        Assert(vault.Read()?.Subject == "fixture-subject", "Original encrypted session unreadable.");
        vault.Clear(); Assert(vault.Read() is null, "Explicit fixture cleanup failed.");
    });
    private sealed class FixtureVault : ICredentialVault
    {
        internal ProtectedIdentitySession? Value;
        internal Action? OnWrite;
        internal int Writes, Clears;
        public ProtectedIdentitySession? Read() => Value;
        public void Write(ProtectedIdentitySession session) { OnWrite?.Invoke(); Value = session; Writes++; }
        public void Clear() { Value = null; Clears++; }
    }
    private sealed class FixtureFlow : INativeOidcFlow
    {
        internal TaskCompletionSource<ProtectedIdentitySession>? PendingLogin, PendingRefresh;
        internal int LoginCalls, RefreshCalls;
        public Task<ProtectedIdentitySession> SignInAsync(bool remember, bool reauthenticate, CancellationToken cancellationToken)
        { LoginCalls++; return PendingLogin?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(Session()); }
        public Task<ProtectedIdentitySession> RefreshAsync(ProtectedIdentitySession session, CancellationToken cancellationToken)
        { RefreshCalls++; return PendingRefresh?.Task.WaitAsync(cancellationToken) ?? Task.FromResult(session); }
        public Task SignOutBrowserAsync(ProtectedIdentitySession session, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
