using System.Text.Json;
using System.Text.Json.Serialization;
using AutumnOS.Contracts;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

// Real temporary permission-store files and controlled host accounts only.
// These cases do not contact DNS/HTTP, open a browser, or certify OS isolation.
internal static class NetworkPermissionTests
{
    private const string LegacyFile = "application-permissions.v1.json";
    private const string NetworkFile = "application-network-permissions.v1.json";
    private static readonly string[] LegacyPermissions =
        ["saves", "identity.profile", "storage", "files.open", "files.save", "notifications", "shortcuts", "widgets", "links"];
    private static readonly JsonSerializerOptions StoreJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter<PermissionDecision>() }
    };

    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return Case("domain_expansion_and_contraction_require_fresh_grants_without_rebinding", DomainChanges);
        yield return Case("domain_order_preserves_the_complete_scope_across_reload", DomainOrder);
        yield return Case("unscoped_historical_network_grant_never_authorizes_current_domains", MissingScope);
        yield return Case("malformed_scope_hashes_fail_closed_without_rewriting_files", MalformedScopes);
        yield return Case("permission_families_cannot_be_mixed_between_store_files", MixedFamilies);
        yield return Case("network_grant_revoke_and_history_preserve_legacy_bytes_and_strict_reader", LegacyCompatibility);
        yield return Case("network_only_decisions_never_create_a_legacy_store", NetworkOnly);
        yield return Case("legacy_decision_does_not_rewrite_the_network_store", PreserveNetworkBytes);
        yield return Case("historical_github_network_row_revokes_its_exact_original_binding", HistoricalBinding);
        yield return Case("historical_revoke_rejects_other_accounts_stale_and_fabricated_rows", HistoricalRows);
        yield return Case("historical_revoke_cancellation_and_expired_accounts_preserve_bytes", HistoricalCancellation);
        yield return Case("combined_store_load_limit_accepts_4096_and_rejects_4097", LoadQuota);
        yield return Case("combined_store_write_limit_allows_replacement_but_no_new_family_rows", WriteQuota);
    }

    private static (string, Action) Case(string name, Func<string, Task> run) =>
        ("network.permission.files_" + name, () => InTemp(run).WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult());

    private static Task DomainChanges(string root)
    {
        var account = Account(); var app = App(["api.autumnos.dev"]);
        var expanded = app with { NetworkDomains = ["api.autumnos.dev", "assets.autumnos.dev"] };
        Check(app.BindingKey == expanded.BindingKey, "A declaration change must not create a different source identity.");
        var service = new PermissionService(root);
        service.SetDecision(account, app, "storage", PermissionDecision.Granted);
        service.SetDecision(account, app, "network", PermissionDecision.Granted);
        var reloaded = new PermissionService(root);
        Check(reloaded.Query(account, app, "network") == PermissionDecision.Granted, "The original declared scope did not persist.");
        Check(reloaded.Query(account, expanded, "network") == PermissionDecision.Prompt, "A grant silently expanded to a new domain.");
        Reject(() => reloaded.Demand(account, expanded, "network"), "PERMISSION_DENIED");
        Check(reloaded.Query(account, expanded, "storage") == PermissionDecision.Granted, "Network scope changed a legacy permission.");
        reloaded.SetDecision(account, expanded, "network", PermissionDecision.Granted);
        var final = new PermissionService(root);
        Check(final.Query(account, expanded, "network") == PermissionDecision.Granted, "Explicit approval of the expanded set was lost.");
        Check(final.Query(account, app, "network") == PermissionDecision.Prompt, "An exact-set grant was silently reused after contraction.");
        Check(final.List(account).Single(row => row.Permission == "network").NetworkScopeHash == NetworkDomainPolicy.ScopeHash(expanded.NetworkDomains),
            "The persisted record does not bind the complete approved domain set.");
        return Task.CompletedTask;
    }

    private static Task DomainOrder(string root)
    {
        var account = Account(); var app = App(["assets.autumnos.dev", "api.autumnos.dev"]);
        var reversed = app with { NetworkDomains = ["api.autumnos.dev", "assets.autumnos.dev"] };
        var service = new PermissionService(root); service.SetDecision(account, app, "network", PermissionDecision.Granted);
        Check(NetworkDomainPolicy.ScopeHash(app.NetworkDomains) == NetworkDomainPolicy.ScopeHash(reversed.NetworkDomains), "Domain order changed the approval scope.");
        Check(new PermissionService(root).Query(account, reversed, "network") == PermissionDecision.Granted, "Reordering the same set lost the approval.");
        return Task.CompletedTask;
    }

    private static Task MissingScope(string root)
    {
        var account = Account(); var app = App(["api.autumnos.dev"]);
        Seed(root, NetworkFile, [Row(account, app, "network")]);
        byte[] original = File.ReadAllBytes(Path.Combine(root, NetworkFile));
        using (var json = JsonDocument.Parse(original))
            Check(!json.RootElement.GetProperty("records")[0].TryGetProperty("networkScopeHash", out _), "The historical fixture unexpectedly contains a scope.");
        var service = new PermissionService(root);
        Check(service.List(account).Single().Decision == PermissionDecision.Granted, "Loading an old row must preserve its historical decision for settings.");
        Check(service.Query(account, app, "network") == PermissionDecision.Prompt, "A scope-free old grant authorized a new declaration.");
        Reject(() => service.Demand(account, app, "network"), "PERMISSION_DENIED");
        SameBytes(Path.Combine(root, NetworkFile), original, "Reading an old grant implicitly migrated or approved it.");
        Check(!File.Exists(Path.Combine(root, LegacyFile)), "Reading a network record created the legacy store.");
        return Task.CompletedTask;
    }

    private static Task MalformedScopes(string root)
    {
        var account = Account(); var app = App(["api.autumnos.dev"]);
        string[] malformed = ["", new('a', 63), new('a', 65), new('A', 64), new('g', 64), new string('0', 63) + "\n"];
        for (int index = 0; index < malformed.Length; index++)
        {
            string directory = Subdirectory(root, "scope-" + index);
            Seed(directory, NetworkFile, [Row(account, app, "network", malformed[index])]);
            byte[] original = File.ReadAllBytes(Path.Combine(directory, NetworkFile));
            Reject(() => _ = new PermissionService(directory), "PERMISSION_STORE_CORRUPT");
            SameBytes(Path.Combine(directory, NetworkFile), original, "Rejecting a malformed scope changed the evidence.");
        }
        return Task.CompletedTask;
    }

    private static Task MixedFamilies(string root)
    {
        var account = Account(); var app = App(["api.autumnos.dev"]);
        var network = Row(account, app, "network", NetworkDomainPolicy.ScopeHash(app.NetworkDomains));
        var legacy = Row(account, app, "storage");
        (string File, PermissionRecord[] Rows)[] fixtures =
        [
            (LegacyFile, [network]),
            (LegacyFile, [network with { NetworkScopeHash = null }]),
            (NetworkFile, [legacy]),
            (LegacyFile, [legacy with { NetworkScopeHash = network.NetworkScopeHash }]),
            (NetworkFile, [network, legacy]),
            (NetworkFile, [network, network])
        ];
        for (int index = 0; index < fixtures.Length; index++)
        {
            string directory = Subdirectory(root, "family-" + index);
            Seed(directory, fixtures[index].File, fixtures[index].Rows);
            byte[] original = File.ReadAllBytes(Path.Combine(directory, fixtures[index].File));
            Reject(() => _ = new PermissionService(directory), "PERMISSION_STORE_CORRUPT");
            SameBytes(Path.Combine(directory, fixtures[index].File), original, "A corrupt family was rewritten while loading.");
        }
        string explicitNull = Subdirectory(root, "legacy-explicit-null-scope");
        byte[] nullScopeBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            records = new[] { new { accountKey = legacy.AccountKey, bindingKey = legacy.BindingKey, appId = legacy.AppId,
                displayName = legacy.DisplayName, source = legacy.Source, permission = legacy.Permission,
                decision = "Granted", networkScopeHash = (string?)null } }
        });
        File.WriteAllBytes(Path.Combine(explicitNull, LegacyFile), nullScopeBytes);
        Reject(() => _ = new PermissionService(explicitNull), "PERMISSION_STORE_CORRUPT");
        SameBytes(Path.Combine(explicitNull, LegacyFile), nullScopeBytes, "An explicit null scope changed the old field family while rejecting it.");
        return Task.CompletedTask;
    }

    private static async Task LegacyCompatibility(string root)
    {
        var account = Account(); var app = App(["api.autumnos.dev"]);
        Seed(root, LegacyFile, LegacyPermissions.Select(permission => Row(account, app, permission)).ToArray(), indented: true);
        string path = Path.Combine(root, LegacyFile); byte[] original = File.ReadAllBytes(path);
        var service = new PermissionService(root);
        foreach (string permission in LegacyPermissions)
            Check(service.Query(account, app, permission) == PermissionDecision.Granted, "A pre-existing legacy grant became unreadable.");
        foreach (PermissionDecision decision in new[] { PermissionDecision.Granted, PermissionDecision.Revoked, PermissionDecision.Granted })
        {
            service.SetDecision(account, app, "network", decision);
            SameBytes(path, original, "A network decision rewrote the legacy store.");
            Check(ReadAsLegacyStrict(File.ReadAllBytes(path)).Count == LegacyPermissions.Length, "The strict legacy reader lost a permission.");
        }
        await service.RevokeHistoricalAsync(account, service.List(account).Single(row => row.Permission == "network"));
        SameBytes(path, original, "Historical network revocation rewrote the legacy store.");
        Check(!File.Exists(path + ".bak"), "A network-only mutation touched the legacy replacement path.");
        Check(ReadAsLegacyStrict(File.ReadAllBytes(path)).All(row => row.Decision == PermissionDecision.Granted), "Network changes affected legacy decisions.");
        Check(new PermissionService(root).Query(account, app, "network") == PermissionDecision.Revoked, "Network revocation did not persist separately.");
    }

    private static Task NetworkOnly(string root)
    {
        var account = Account(); var app = App(["api.autumnos.dev"]); var service = new PermissionService(root);
        service.SetDecision(account, app, "network", PermissionDecision.Granted);
        service.SetDecision(account, app, "network", PermissionDecision.Revoked);
        Check(File.Exists(Path.Combine(root, NetworkFile)), "No actual network permission store was written.");
        Check(!File.Exists(Path.Combine(root, LegacyFile)) && !File.Exists(Path.Combine(root, LegacyFile + ".bak")), "Network-only work created a legacy file.");
        Check(new PermissionService(root).Query(account, app, "network") == PermissionDecision.Revoked, "The separate file cannot be reloaded.");
        return Task.CompletedTask;
    }

    private static Task PreserveNetworkBytes(string root)
    {
        var account = Account(); var app = App(["api.autumnos.dev"]); var service = new PermissionService(root);
        service.SetDecision(account, app, "network", PermissionDecision.Granted);
        string path = Path.Combine(root, NetworkFile); byte[] original = File.ReadAllBytes(path);
        service.SetDecision(account, app, "saves", PermissionDecision.Granted);
        service.SetDecision(account, app, "saves", PermissionDecision.Revoked);
        SameBytes(path, original, "A legacy decision rewrote the network store.");
        Check(!File.Exists(path + ".bak"), "Legacy work replaced the network file.");
        Check(ReadAsLegacyStrict(File.ReadAllBytes(Path.Combine(root, LegacyFile))).Single().Permission == "saves", "Legacy writes emitted a network row or field.");
        return Task.CompletedTask;
    }

    private static async Task HistoricalBinding(string root)
    {
        var account = Account();
        var originalApp = App(null) with { Identity = new("cn.labchronicles.historical", 81357, null), Source = "github:repository:81357" };
        var row = Row(account, originalApp, "network");
        Seed(root, NetworkFile, [row]);
        var authority = new RecordingAuthority(); var service = new PermissionService(root, authorityMutations: authority);
        PermissionChange? notice = null; service.Changed += change => notice = change;
        await service.RevokeHistoricalAsync(account, service.List(account).Single());
        var expected = row with { Decision = PermissionDecision.Revoked };
        Check(service.List(account).Single() == expected, "Historical revoke changed identity fields or fabricated a scope.");
        Check(new PermissionService(root).List(account).Single() == expected, "The original row was not revoked on disk.");
        Check(authority.Scope == RuntimeMutationScope.ForApplication(account.AccountKey, originalApp.BindingKey) && authority.Commits == 1,
            "Revocation fenced a reconstructed binding instead of the exact historical binding.");
        Check(notice == new PermissionChange(account.AccountKey, row.BindingKey, "network", PermissionDecision.Revoked), "The notification lost the historical binding.");
        Check(!File.Exists(Path.Combine(root, LegacyFile)), "Historical network revoke created a legacy store.");
    }

    private static async Task HistoricalRows(string root)
    {
        var account = Account(); var app = App(["api.autumnos.dev"]); var service = new PermissionService(root);
        service.SetDecision(account, app, "network", PermissionDecision.Granted);
        PermissionRecord old = service.List(account).Single(); string path = Path.Combine(root, NetworkFile);
        byte[] before = File.ReadAllBytes(path);
        await RejectAsync(() => service.RevokeHistoricalAsync(Account("other-account"), old), "PERMISSION_RECORD_STALE");
        await RejectAsync(() => service.RevokeHistoricalAsync(account, old with { DisplayName = "fabricated snapshot" }), "PERMISSION_RECORD_STALE");
        await RejectAsync(() => service.RevokeHistoricalAsync(account, old with { BindingKey = new string('c', 64) }), "PERMISSION_RECORD_STALE");
        SameBytes(path, before, "A rejected historical row mutated the store.");
        await service.RevokeHistoricalAsync(account, old);
        byte[] revoked = File.ReadAllBytes(path);
        await RejectAsync(() => service.RevokeHistoricalAsync(account, old), "PERMISSION_RECORD_STALE");
        PermissionRecord current = service.List(account).Single();
        await RejectAsync(() => service.RevokeHistoricalAsync(account, current with { Decision = PermissionDecision.Granted }), "PERMISSION_RECORD_STALE");
        SameBytes(path, revoked, "A stale/fabricated grant changed the revoked row.");
        Check(new PermissionService(root).Query(account, app, "network") == PermissionDecision.Revoked, "Historical API restored a grant.");
        Check(service.List(Account("other-account")).Count == 0, "Another account can list the original permission row.");
    }

    private static async Task HistoricalCancellation(string root)
    {
        bool current = true; var account = Account() with { IsCurrent = () => current };
        var app = App(["api.autumnos.dev"]); var service = new PermissionService(root);
        service.SetDecision(account, app, "network", PermissionDecision.Granted);
        PermissionRecord row = service.List(account).Single(); string path = Path.Combine(root, NetworkFile); byte[] before = File.ReadAllBytes(path);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Cancelled(() => service.RevokeHistoricalAsync(account, row, cancelled.Token));
        }
        current = false;
        await RejectAsync(() => service.RevokeHistoricalAsync(account, row), "SESSION_EXPIRED");
        current = true;
        using (var invalidated = new CancellationTokenSource())
        {
            invalidated.Cancel();
            await RejectAsync(() => service.RevokeHistoricalAsync(account with { Invalidated = invalidated.Token }, row), "SESSION_EXPIRED");
        }
        SameBytes(path, before, "Cancellation or an expired account changed a permission file.");
        Check(service.Query(account, app, "network") == PermissionDecision.Granted, "Rejected revocation changed the in-memory decision.");
    }

    private static Task LoadQuota(string root)
    {
        var account = Account(); var app = App(["api.autumnos.dev"]);
        var network = Row(account, app, "network", NetworkDomainPolicy.ScopeHash(app.NetworkDomains));
        string accepted = Subdirectory(root, "accepted");
        Seed(accepted, LegacyFile, ManyLegacyRows(account, 4095)); Seed(accepted, NetworkFile, [network]);
        Check(new PermissionService(accepted).List(account).Count == 4096, "The shared record ceiling rejected a valid boundary.");
        string rejected = Subdirectory(root, "rejected");
        Seed(rejected, LegacyFile, ManyLegacyRows(account, 4096)); Seed(rejected, NetworkFile, [network]);
        byte[] legacy = File.ReadAllBytes(Path.Combine(rejected, LegacyFile)), separate = File.ReadAllBytes(Path.Combine(rejected, NetworkFile));
        Reject(() => _ = new PermissionService(rejected), "PERMISSION_STORE_CORRUPT");
        SameBytes(Path.Combine(rejected, LegacyFile), legacy, "Quota rejection rewrote the legacy evidence.");
        SameBytes(Path.Combine(rejected, NetworkFile), separate, "Quota rejection rewrote the network evidence.");
        return Task.CompletedTask;
    }

    private static Task WriteQuota(string root)
    {
        var account = Account(); var app = App(["api.autumnos.dev"]);
        Seed(root, LegacyFile, ManyLegacyRows(account, 4095));
        var service = new PermissionService(root);
        service.SetDecision(account, app, "network", PermissionDecision.Granted);
        service.SetDecision(account, app, "network", PermissionDecision.Revoked);
        Check(service.List(account).Count == 4096, "Updating an existing row consumed another quota slot.");
        byte[] legacy = File.ReadAllBytes(Path.Combine(root, LegacyFile)), network = File.ReadAllBytes(Path.Combine(root, NetworkFile));
        Reject(() => service.SetDecision(account, app with { Source = "local-test:another-network-source" }, "network", PermissionDecision.Granted), "QUOTA_EXCEEDED");
        Reject(() => service.SetDecision(account, app, "storage", PermissionDecision.Granted), "QUOTA_EXCEEDED");
        SameBytes(Path.Combine(root, LegacyFile), legacy, "A full-store write changed legacy bytes before rejecting.");
        SameBytes(Path.Combine(root, NetworkFile), network, "A full-store write changed network bytes before rejecting.");
        Check(new PermissionService(root).List(account).Count == 4096, "A rejected cross-family insertion persisted a 4097th row.");
        return Task.CompletedTask;
    }

    private static RuntimeAccountContext Account(string key = "network-permission-account") => new(key, 7, false, () => true, CancellationToken.None);
    private static RuntimeApplication App(IReadOnlyCollection<string>? domains) => new(new("cn.labchronicles.networkpermissions", 31415, null),
        "local-test:network-permissions", "Network permission fixture", [.. LegacyPermissions, "network"], NetworkDomains: domains);
    private static PermissionRecord Row(RuntimeAccountContext account, RuntimeApplication app, string permission, string? scope = null) =>
        new(account.AccountKey, app.BindingKey, app.Identity.AppId, app.DisplayName, app.Source, permission, PermissionDecision.Granted, scope);
    private static PermissionRecord[] ManyLegacyRows(RuntimeAccountContext account, int count) => Enumerable.Range(0, count).Select(index =>
        new PermissionRecord(account.AccountKey, RuntimeApplication.Hash("network-quota-row-" + index), "cn.labchronicles.quota.row" + index,
            "Quota fixture", "local-test:quota", "storage", PermissionDecision.Granted)).ToArray();

    private static void Seed(string root, string file, PermissionRecord[] rows, bool indented = false)
    {
        var options = new JsonSerializerOptions(StoreJson) { WriteIndented = indented };
        File.WriteAllBytes(Path.Combine(root, file), JsonSerializer.SerializeToUtf8Bytes(new StoreDocument(1, rows), options));
    }
    private sealed record StoreDocument(int SchemaVersion, PermissionRecord[] Records);

    // Compatibility oracle intentionally has the pre-network seven row fields and
    // the original nine-permission set. Unknown fields are rejected, including null.
    private static IReadOnlyList<LegacyRecord> ReadAsLegacyStrict(byte[] bytes)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            Converters = { new JsonStringEnumConverter<PermissionDecision>() }
        };
        var document = JsonSerializer.Deserialize<LegacyDocument>(bytes, options);
        if (document is not { SchemaVersion: 1, Records: not null })
            throw new InvalidOperationException("The original permission document shape is no longer readable.");
        foreach (var row in document.Records)
            Check(LegacyPermissions.Contains(row.Permission, StringComparer.Ordinal), "The legacy store contains a new permission name.");
        return document.Records;
    }
    private sealed record LegacyDocument(int SchemaVersion, LegacyRecord[] Records);
    private sealed record LegacyRecord(string AccountKey, string BindingKey, string AppId, string DisplayName, string Source, string Permission, PermissionDecision Decision);

    private sealed class RecordingAuthority : IRuntimeAuthorityMutations
    {
        internal RuntimeMutationScope? Scope;
        internal int Commits;
        public ValueTask<IRuntimeAuthorityMutation> BeginAsync(RuntimeMutationScope scope, CancellationToken beforeIntentCancellation = default)
        { beforeIntentCancellation.ThrowIfCancellationRequested(); Scope = scope; return ValueTask.FromResult<IRuntimeAuthorityMutation>(new Mutation(this)); }
        public bool TryCommitSynchronously(RuntimeMutationScope scope, Action shortCommit) => throw new InvalidOperationException("Historical revoke must use its async permit.");
        private sealed class Mutation(RecordingAuthority owner) : IRuntimeAuthorityMutation
        {
            private bool consumed;
            public Task CommitAsync(Action shortCommit)
            {
                Check(!consumed, "The historical mutation permit was replayed."); consumed = true; owner.Commits++; shortCommit(); return Task.CompletedTask;
            }
        }
    }

    private static async Task InTemp(Func<string, Task> run)
    {
        string root = Path.Combine(Path.GetTempPath(), "AutumnOS-network-permission-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { await run(root); }
        finally { Directory.Delete(root, true); }
    }
    private static string Subdirectory(string root, string name) { string path = Path.Combine(root, name); Directory.CreateDirectory(path); return path; }
    private static void SameBytes(string path, byte[] expected, string message) => Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(expected), message);
    private static void Check(bool valid, string message) { if (!valid) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string code)
    {
        try { action(); } catch (RuntimeCapabilityException error) { Check(error.Code == code, "Expected " + code + ", got " + error.Code); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static async Task RejectAsync(Func<Task> action, string code)
    {
        try { await action(); } catch (RuntimeCapabilityException error) { Check(error.Code == code, "Expected " + code + ", got " + error.Code); return; }
        throw new InvalidOperationException("Expected " + code);
    }
    private static async Task Cancelled(Func<Task> action)
    {
        try { await action(); } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation.");
    }
}
