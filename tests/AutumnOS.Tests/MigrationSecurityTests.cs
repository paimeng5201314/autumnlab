using System.Security.Cryptography;
using System.Text.Json;
using AutumnOS.Store;
using AutumnOS.Update;
using AutumnOS.UpdateProtocol;

namespace AutumnOS.Tests;

internal static class MigrationSecurityTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("migration.security_cross_seed_carries_all_floors_without_changing_legacy_data", CrossSeed);
        yield return ("migration.security_same_sequence_digest_conflict_is_not_enumeration_order", Conflict);
        yield return ("migration.security_failed_history_union_is_bounded_without_truncation", FailureLimit);
        yield return ("migration.security_legacy_optional_fields_and_required_policy_fields", LegacyFields);
        yield return ("migration.security_initialized_missing_or_corrupt_canonical_fails_closed", CanonicalDamage);
        yield return ("migration.security_old_journal_blocks_even_without_ledger", OldJournal);
        yield return ("migration.security_seed_receipt_entry_marker_and_schema_binding", Bindings);
        yield return ("migration.security_unbound_product_with_ledger_cannot_be_ignored", Unbound);
        yield return ("migration.security_current_and_independent_guards_share_minimum_and_failed_history", IndependentGuards);
        yield return ("migration.security_clock_watermark_reaches_signature_expiry_check", ClockAndSignature);
        yield return ("migration.security_concurrent_records_cannot_write_stale_lower_floor", ConcurrentRecords);
        yield return ("migration.security_lower_failure_retains_digest_floor_and_shared_clock", LowerFailure);
        yield return ("migration.security_legacy_ledger_cannot_supply_update_trust", NoLegacyTrust);
        yield return ("migration.security_portable_data_override_cannot_redirect_migration", DataRootBinding);
        yield return ("migration.security_ordinary_entry_to_new_seed_carries_independent_floor", OrdinaryEntry);
        yield return ("migration.security_relevant_old_sources_are_bounded", SourceLimit);
        yield return ("migration.security_null_arrays_and_journal_previous_fail_closed", NullRecords);
        yield return ("migration.security_first_policy_write_keeps_failure_129_before_UI_truncation", FirstPolicyWrite);
        yield return ("migration.security_two_services_keep_concurrent_failure_intents", ConcurrentServices);
        yield return ("migration.security_policy_intent_cannot_overwrite_higher_sequence", ConcurrentPolicySequences);
        yield return ("migration.security_stable_bootstrap_uses_current_receipt_despite_offline_clock_change", StableBootstrapReceipt);
        yield return ("migration.security_launch_rejects_actual_receipt_below_minimum", LaunchMinimum);
        yield return ("migration.security_launch_requires_receipt_and_blocks_failed_current_client", LaunchIdentity);
    }

    private static void CrossSeed() => WithFixture(f =>
    {
        string old = f.Seed('a', "old", "0.9.0"), next = f.Seed('b', "next", "1.0.0");
        string ledger = f.Ledger(old, 100, Hash('a'), "0.9.0", ["old-failed"]);
        byte[] before = File.ReadAllBytes(ledger);
        var now = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        f.Policy(120, Hash('b'), now, ["shared-failed"]);
        var result = UpdateSecurityLedgerStore.LoadAndMerge(next);
        Assert(result.HighestSequence == 120 && result.ManifestSha256 == Hash('b') && result.MinimumVersion == "0.9.0");
        Assert(result.FailedBuilds.SequenceEqual(new[] { "old-failed", "shared-failed" }) && result.LastTrustedUtc == now);
        Assert(File.ReadAllBytes(ledger).SequenceEqual(before));
        var repeated = UpdateSecurityLedgerStore.LoadAndMerge(next);
        Assert(JsonSerializer.Serialize(result) == JsonSerializer.Serialize(repeated));
        Assert(File.Exists(Path.Combine(f.System, "Entry-" + new string('a', 20) + ".json")));
    });

    private static void Conflict() => WithFixture(f =>
    {
        string a = f.Seed('a', "A", "1.0.0"), b = f.Seed('b', "B", "1.0.1"), next = f.Seed('c', "C", "1.1.0");
        f.Ledger(a, 10, Hash('a')); f.Ledger(b, 10, Hash('b'));
        Reject(() => UpdateSecurityLedgerStore.LoadAndMerge(next), "UPDATE_SECURITY_SEQUENCE_CONFLICT");
        Assert(!File.Exists(f.Canonical));
        f.Ledger(b, 11, Hash('b'));
        Assert(UpdateSecurityLedgerStore.LoadAndMerge(next).HighestSequence == 11);
        byte[] accepted = File.ReadAllBytes(f.Canonical);
        f.Ledger(a, 11, Hash('c'));
        Reject(() => UpdateSecurityLedgerStore.LoadAndMerge(next), "UPDATE_SECURITY_SEQUENCE_CONFLICT");
        Assert(File.ReadAllBytes(f.Canonical).SequenceEqual(accepted));
    });

    private static void FailureLimit() => WithFixture(f =>
    {
        string a = f.Seed('a', "A", "1.0.0"), b = f.Seed('b', "B", "1.0.1"), next = f.Seed('c', "C", "1.1.0");
        f.Ledger(a, 1, Hash('a'), failed: Enumerable.Range(0, 4096).Select(i => "bad-" + i).ToArray());
        f.Ledger(b, 2, Hash('b'), failed: ["one-more"]);
        Reject(() => UpdateSecurityLedgerStore.LoadAndMerge(next), "UPDATE_SECURITY_FAILED_BUILDS_LIMIT");
        Assert(!File.Exists(f.Canonical));
    });

    private static void LegacyFields() => WithFixture(f =>
    {
        string old = f.Seed('a', "A", "1.0.0"), next = f.Seed('b', "B", "1.1.0");
        string ledger = f.Ledger(old, 7, "");
        File.WriteAllText(ledger, "{\"highestSequence\":7,\"failedBuilds\":[]}");
        f.Policy(0, null, DateTimeOffset.MinValue, []);
        Assert(UpdateSecurityLedgerStore.LoadAndMerge(next).HighestSequence == 7);
        // Nullable is not optional: existing policy's six core constructor fields remain required.
        string policy = Path.Combine(f.Data, "Updates", "state.json");
        using var document = JsonDocument.Parse(File.ReadAllBytes(policy));
        var missing = document.RootElement.EnumerateObject().Where(p => p.Name != "highestDigest")
            .ToDictionary(p => p.Name, p => p.Value.Clone());
        Fixture.Write(policy, missing);
        Reject(() => UpdateSecurityLedgerStore.LoadAndMerge(next));
    });

    private static void CanonicalDamage() => WithFixture(f =>
    {
        string next = f.Seed('b', "B", "1.1.0");
        _ = UpdateSecurityLedgerStore.LoadAndMerge(next);
        byte[] good = File.ReadAllBytes(f.Canonical);
        File.WriteAllText(f.Canonical, "{\"schemaVersion\":1,\"schemaVersion\":1}");
        Reject(() => UpdateSecurityLedgerStore.LoadAndMerge(next));
        File.Delete(f.Canonical);
        Reject(() => UpdateSecurityLedgerStore.LoadAndMerge(next), "UPDATE_SECURITY_LEDGER_MISSING");
        File.WriteAllBytes(f.Canonical, good);
        Assert(UpdateSecurityLedgerStore.LoadAndMerge(next).HighestSequence == 0);
    });

    private static void OldJournal() => WithFixture(f =>
    {
        string old = f.Seed('a', "A", "1.0.0"), next = f.Seed('b', "B", "1.1.0");
        var previous = UpdateFiles.Read<InstalledBuild>(Path.Combine(old, "autumn.install.json"));
        Fixture.Write(Path.Combine(old, ".autumnos-update", "journal.json"), new UpdateJournal(1, Guid.NewGuid().ToString("N"),
            old, "applying", "A", "target", previous, previous.Files, []));
        Reject(() => UpdateSecurityLedgerStore.LoadAndMerge(next), "UPDATE_SECURITY_LEGACY_RECOVERY_REQUIRED");
        Assert(!File.Exists(f.Canonical));
    });

    private static void Bindings()
    {
        foreach (string fault in new[] { "seed", "marker", "entry-build", "entry-version", "receipt-schema", "receipt-version", "unknown-ledger" })
            WithFixture(f =>
            {
                string old = f.Seed('a', "A", "1.0.0"), next = f.Seed('b', "B", "1.1.0");
                string ledger = f.Ledger(old, 7, Hash('a'));
                string entry = Path.Combine(f.System, "Entry-" + new string('a', 20) + ".json");
                string receipt = Path.Combine(old, "autumn.install.json");
                switch (fault)
                {
                    case "seed": File.WriteAllText(Path.Combine(old, ".autumnos-single-file-seed"), Hash('c')); break;
                    case "marker": File.WriteAllText(Path.Combine(old, "autumn.portable"), "wrong"); break;
                    case "entry-build": File.WriteAllText(entry, File.ReadAllText(entry).Replace("\"A\"", "\"other\"")); break;
                    case "entry-version": File.WriteAllText(entry, File.ReadAllText(entry).Replace("1.0.0", "1.0.1")); break;
                    case "receipt-schema": File.WriteAllText(receipt, File.ReadAllText(receipt).Replace("\"dataSchemaVersion\": 1", "\"dataSchemaVersion\": 2")); break;
                    case "receipt-version": File.WriteAllText(receipt, File.ReadAllText(receipt).Replace("1.0.0", "nonsense")); break;
                    case "unknown-ledger": File.WriteAllText(ledger, File.ReadAllText(ledger).Replace("\"highestSequence\"", "\"HighestSequence\"")); break;
                }
                Reject(() => UpdateSecurityLedgerStore.LoadAndMerge(next));
                Assert(!File.Exists(f.Canonical));
            });
    }

    private static void Unbound() => WithFixture(f =>
    {
        string next = f.Seed('b', "B", "1.1.0"), unrelated = Path.Combine(f.System, "Product-unrelated");
        Directory.CreateDirectory(unrelated);
        Assert(UpdateSecurityLedgerStore.LoadAndMerge(next).HighestSequence == 0);
        f.Ledger(unrelated, 90, Hash('a'));
        byte[] before = File.ReadAllBytes(f.Canonical);
        Reject(() => UpdateSecurityLedgerStore.LoadAndMerge(next));
        Assert(File.ReadAllBytes(f.Canonical).SequenceEqual(before));
    });

    private static void IndependentGuards() => WithFixture(f =>
    {
        string old = f.Seed('a', "A", "1.0.0"), next = f.Seed('b', "B", "1.1.0");
        f.Ledger(old, 100, Hash('a'), "1.0.0", ["known-bad"]);
        var state = UpdateSecurityLedgerStore.LoadAndMerge(next);
        Assert(UpdateTransaction.HighestSequence(next) == 100 && UpdateTransaction.IsFailedBuild(next, "known-bad"));
        UpdateSecurityLedgerStore.RequireMinimumVersion(state, "1.0.0");
        Reject(() => UpdateSecurityLedgerStore.RequireMinimumVersion(state, "1.0.0-rc.3"), "UPDATE_MINIMUM_SECURITY_VERSION");
        Reject(() => UpdateSecurityLedgerStore.RequireMinimumVersion(state, "0.9.9"), "UPDATE_MINIMUM_SECURITY_VERSION");
    });

    private static void ClockAndSignature()
    {
        using var rsa = RSA.Create(3072);
        var now = new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero);
        var trust = new UpdateTrustStore(new(1, 1, "production", [new("new-key", rsa.ExportSubjectPublicKeyInfoPem())]));
        var manifest = new UpdateManifest(1, "plus", "1.1.0", "fixture", "win-x64", "1.1.0", 10, now.AddHours(-1), now.AddMinutes(2), "new-key", 1,
            new("payload.zip", 1, Hash('a'), 1, 2), [new("AutumnOS.Client.exe", 1, Hash('b'))], [], new(1, 1, true), ReleaseTag: "plus-v1.1.0");
        byte[] bytes = UpdateManifestCodec.Serialize(manifest), signature = UpdateSignature.Create(bytes, rsa, "new-key", 1);
        Assert(UpdateSignature.Verify(bytes, signature, trust, now).Sequence == 10);
        var state = new UpdateSecurityLedgerState(0, [], LastTrustedUtc: now.AddMinutes(4));
        DateTimeOffset effective = UpdateSecurityLedgerStore.VerificationTime(state, now);
        Assert(effective == now.AddMinutes(4));
        Reject(() => UpdateSignature.Verify(bytes, signature, trust, effective), "UPDATE_METADATA_EXPIRED");
        Reject(() => UpdateSecurityLedgerStore.VerificationTime(state, now.AddMinutes(-2)), "UPDATE_CLOCK_ROLLBACK");
    }

    private static void ConcurrentRecords() => WithFixture(f =>
    {
        string a = f.Seed('a', "A", "1.0.0"), b = f.Seed('b', "B", "1.0.1");
        Task.WhenAll(Enumerable.Range(1, 8).Select(i => Task.Run(() =>
            UpdateSecurityLedgerStore.Record(i % 2 == 0 ? a : b, i, i.ToString("x64"), DateTimeOffset.UtcNow, "bad-" + i))))
            .GetAwaiter().GetResult();
        var state = UpdateSecurityLedgerStore.LoadAndMerge(a);
        Assert(state.HighestSequence == 8 && state.ManifestSha256 == 8.ToString("x64") && state.FailedBuilds.Length == 8);
    });

    private static void LowerFailure() => WithFixture(f =>
    {
        string next = f.Seed('b', "B", "1.1.0");
        var trusted = DateTimeOffset.UtcNow.AddMinutes(1);
        f.Ledger(next, 100, Hash('a'), "0.9.0"); f.Policy(100, Hash('a'), trusted, []);
        _ = UpdateSecurityLedgerStore.Record(next, 12, Hash('b'), DateTimeOffset.UtcNow, "bad-lower");
        var state = UpdateSecurityLedgerStore.LoadAndMerge(next);
        Assert(state.HighestSequence == 100 && state.ManifestSha256 == Hash('a') && state.MinimumVersion == "0.9.0" &&
            state.LastTrustedUtc == trusted && state.FailedBuilds.Contains("bad-lower"));
    });

    private static void NoLegacyTrust() => WithFixture(f =>
    {
        string old = f.Seed('a', "A", "1.0.0"), next = f.Seed('b', "B", "1.1.0");
        string ledger = f.Ledger(old, 10, Hash('a'));
        File.WriteAllText(ledger, "{\"highestSequence\":10,\"failedBuilds\":[],\"keys\":[{\"keyId\":\"old-root\"}]}");
        Reject(() => UpdateSecurityLedgerStore.LoadAndMerge(next));
        Assert(!File.Exists(f.Canonical));
    });

    private static void DataRootBinding() => WithFixture(f =>
    {
        string next = f.Seed('b', "B", "1.1.0");
        Reject(() => UpdateSecurityLedgerStore.LoadAndMerge(next, Path.Combine(f.Root, "foreign-data")), "UPDATE_SECURITY_DATA_ROOT_MISMATCH");
        Assert(!Directory.Exists(Path.Combine(f.Root, "foreign-data")));
    });

    private static void OrdinaryEntry() => WithFixture(f =>
    {
        string next = f.Seed('b', "B", "1.1.0");
        File.Copy(Path.Combine(next, "autumn.install.json"), Path.Combine(f.Root, "autumn.install.json"));
        f.Ledger(f.Root, 99, Hash('a'), "1.0.0", ["ordinary-failed"]);
        var result = UpdateSecurityLedgerStore.LoadAndMerge(next);
        Assert(result.HighestSequence == 99 && result.MinimumVersion == "1.0.0" && result.FailedBuilds.Contains("ordinary-failed"));
        var previous = UpdateFiles.Read<InstalledBuild>(Path.Combine(f.Root, "autumn.install.json"));
        Fixture.Write(Path.Combine(f.Root, ".autumnos-update", "journal.json"), new UpdateJournal(1, Guid.NewGuid().ToString("N"),
            f.Root, "health", previous.BuildId, "target", previous, previous.Files, []));
        Reject(() => UpdateSecurityLedgerStore.LoadAndMerge(next), "UPDATE_SECURITY_LEGACY_RECOVERY_REQUIRED");
    });

    private static void SourceLimit() => WithFixture(f =>
    {
        string next = f.Seed('f', "current", "1.1.0");
        for (int i = 0; i <= UpdateSecurityLedgerStore.MaximumLegacySources; i++)
        {
            string seed = i.ToString("x8") + new string('0', 56), root = f.Seed(seed, "old-" + i, "1.0.0");
            f.Ledger(root, 1, Hash('a'));
        }
        Reject(() => UpdateSecurityLedgerStore.LoadAndMerge(next), "UPDATE_SECURITY_LEGACY_SOURCES_LIMIT");
        Assert(!File.Exists(f.Canonical));
    });

    private static void NullRecords()
    {
        foreach (string fault in new[] { "policy", "receipt", "previous" }) WithFixture(f =>
        {
            string old = f.Seed('a', "A", "1.0.0"), next = f.Seed('b', "B", "1.1.0"); f.Ledger(old, 1, Hash('a'));
            switch (fault)
            {
                case "policy": File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(f.Data, "Updates")).FullName, "state.json"),
                    "{\"schemaVersion\":1,\"preview\":false,\"highestSequence\":0,\"highestDigest\":null,\"lastTrustedUtc\":\"0001-01-01T00:00:00+00:00\",\"failedBuildIds\":null}"); break;
                case "receipt": File.WriteAllText(Path.Combine(old, "autumn.install.json"), "{\"schemaVersion\":1,\"version\":\"1.0.0\",\"buildId\":\"A\",\"files\":null}"); break;
                case "previous": File.WriteAllText(Path.Combine(old, ".autumnos-update", "journal.json"),
                    "{\"schemaVersion\":1,\"transactionId\":\"" + Guid.NewGuid().ToString("N") + "\",\"root\":" + JsonSerializer.Serialize(old) +
                    ",\"phase\":\"aborted\",\"fromBuildId\":\"A\",\"toBuildId\":\"target\",\"previous\":null,\"nextFiles\":[],\"touched\":[]}"); break;
            }
            Reject(() => UpdateSecurityLedgerStore.LoadAndMerge(next)); Assert(!File.Exists(f.Canonical));
        });
    }

    private static void FirstPolicyWrite() => WithFixture(f =>
    {
        string next = f.Seed('b', "B", "1.1.0");
        f.Policy(0, null, DateTimeOffset.MinValue, Enumerable.Range(0, 128).Select(i => "old-" + i).ToArray());
        var transport = new NoNetwork();
        using var downloads = new DownloadService(f.Data, transport, () => new(), hostUpdate: true);
        using var service = new UpdateService(f.Data, "1.1.0", "B", transport, downloads, programDirectory: next);
        service.RecordFailedCandidate("new-129");
        var state = UpdateSecurityLedgerStore.LoadAndMerge(next);
        Assert(state.FailedBuilds.Length == 129 && state.FailedBuilds.Contains("old-0") && state.FailedBuilds.Contains("new-129"));
    });

    private static void ConcurrentServices() => WithFixture(f =>
    {
        string next = f.Seed('b', "B", "1.1.0");
        var transport = new NoNetwork();
        using var downloads = new DownloadService(f.Data, transport, () => new(), hostUpdate: true);
        using var a = new UpdateService(f.Data, "1.1.0", "B", transport, downloads, programDirectory: next);
        using var b = new UpdateService(f.Data, "1.1.0", "B", transport, downloads, programDirectory: next);
        Task.WhenAll(Task.Run(() => a.RecordFailedCandidate("bad-a")), Task.Run(() => b.RecordFailedCandidate("bad-b")))
            .GetAwaiter().GetResult();
        var state = UpdateSecurityLedgerStore.LoadAndMerge(next);
        Assert(state.FailedBuilds.SequenceEqual(new[] { "bad-a", "bad-b" }));
    });

    private static void ConcurrentPolicySequences() => WithFixture(f =>
    {
        string next = f.Seed('b', "B", "1.1.0");
        Task.WhenAll(Task.Run(() => UpdateSecurityLedgerStore.RecordPolicy(next, f.Data, 100, Hash('a'), DateTimeOffset.UtcNow, ["bad-a"])),
            Task.Run(() => UpdateSecurityLedgerStore.RecordPolicy(next, f.Data, 99, Hash('b'), DateTimeOffset.UtcNow, ["bad-b"])))
            .GetAwaiter().GetResult();
        f.Policy(99, Hash('b'), DateTimeOffset.MinValue, ["bad-b"]); // A stale UI projection cannot lower the authority.
        var state = UpdateSecurityLedgerStore.LoadAndMerge(next);
        Assert(state.HighestSequence == 100 && state.ManifestSha256 == Hash('a') && state.FailedBuilds.Length == 2);
    });

    private static void StableBootstrapReceipt() => WithFixture(f =>
    {
        const string stableBootstrapVersion = "1.0.0";
        string current = f.Seed('b', "client-new", "1.2.0");
        f.Ledger(current, 100, Hash('a'), "1.1.0", ["stable-bootstrap-old"]);
        f.Policy(100, Hash('a'), DateTimeOffset.UtcNow.AddHours(1), []);
        Assert(SemanticVersion.Parse(stableBootstrapVersion).CompareTo(SemanticVersion.Parse("1.1.0")) < 0);
        var installed = UpdateTransaction.RequireCurrentInstallation(current);
        Assert(installed.Version == "1.2.0" && installed.BuildId == "client-new");
        // Installed/offline startup succeeds; the same clock still blocks update verification.
        Reject(() => UpdateSecurityLedgerStore.VerificationTime(UpdateSecurityLedgerStore.LoadAndMerge(current), DateTimeOffset.UtcNow), "UPDATE_CLOCK_ROLLBACK");
    });

    private static void LaunchMinimum()
    {
        foreach (string version in new[] { "1.0.0", "1.1.0-rc.3" }) WithFixture(f =>
        {
            string current = f.Seed('b', "client-current", version);
            f.Ledger(current, 100, Hash('a'), "1.1.0");
            Reject(() => UpdateTransaction.RequireCurrentInstallation(current), "UPDATE_MINIMUM_SECURITY_VERSION");
        });
    }

    private static void LaunchIdentity()
    {
        WithFixture(f => Reject(() => UpdateTransaction.RequireCurrentInstallation(f.Root), "UPDATE_INSTALL_RECEIPT_REQUIRED"));
        WithFixture(f =>
        {
            string current = f.Seed('b', "client-current", "1.2.0");
            File.Delete(Path.Combine(current, "autumn.install.json"));
            Reject(() => UpdateTransaction.RequireCurrentInstallation(current), "UPDATE_INSTALL_RECEIPT_REQUIRED");
        });
        WithFixture(f =>
        {
            string current = f.Seed('b', "client-failed", "1.2.0");
            f.Ledger(current, 100, Hash('a'), "1.1.0", ["client-failed"]);
            Reject(() => UpdateTransaction.RequireCurrentInstallation(current), "UPDATE_FAILED_BUILD_BLOCKED");
        });
        WithFixture(f =>
        {
            string current = f.Seed('b', "client-current", "1.2.0");
            string receipt = Path.Combine(current, "autumn.install.json");
            File.WriteAllText(receipt, File.ReadAllText(receipt).Replace("1.2.0", "not-a-version"));
            Reject(() => UpdateTransaction.RequireCurrentInstallation(current), "UPDATE_INSTALL_RECEIPT_INVALID");
        });
    }

    private sealed class NoNetwork : IGitHubTransport
    {
        public Task<HttpResponseMessage> SendAsync(Uri url, GitHubRequestKind kind, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Migration fixture must not make a network request.");
    }

    private static string Hash(char c) => new(c, 64);
    private static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Migration security assertion failed."); }
    private static void Reject(Action action, string? code = null)
    {
        try { action(); }
        catch (Exception error) when (error is UpdateException or IOException or JsonException)
        { if (code is not null && error.Message != code) throw new InvalidOperationException($"Expected {code}, got {error.Message}.", error); return; }
        throw new InvalidOperationException("Unsafe migration input was accepted.");
    }
    private static void WithFixture(Action<Fixture> action) { using var fixture = new Fixture(); action(fixture); }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "autumnos-migration-security-" + Guid.NewGuid().ToString("N"));
        internal string Data => Path.Combine(Root, "AutumnOS_Data");
        internal string System => Path.Combine(Data, "System");
        internal string Canonical => Path.Combine(Data, "Updates", "security-ledger.json");
        internal Fixture() => Directory.CreateDirectory(System);
        internal string Seed(char seed, string build, string version)
            => Seed(Hash(seed), build, version);
        internal string Seed(string seed, string build, string version)
        {
            string root = Path.Combine(System, "Product-" + seed[..20]);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "autumn.portable"), AutumnOS.Contracts.PortableLayout.Marker);
            File.WriteAllText(Path.Combine(root, ".autumnos-single-file-seed"), seed);
            var files = new[] { "AutumnOS.Client.exe", "runtime.dll", "sample.autumn" }.Select(name => new InstalledFile(name, 1, Hash('a'))).ToArray();
            Write(Path.Combine(root, "autumn.install.json"), new InstalledBuild(1, version, build, files));
            PortableEntryBinding.Commit(root, build, version);
            return root;
        }
        internal string Ledger(string root, long sequence, string digest, string? minimum = null, string[]? failed = null)
        {
            string path = Path.Combine(root, ".autumnos-update", "security.json");
            Write(path, new UpdateSecurityState(sequence, failed ?? [], digest, minimum)); return path;
        }
        internal void Policy(long sequence, string? digest, DateTimeOffset time, string[] failed) =>
            Write(Path.Combine(Data, "Updates", "state.json"), new { schemaVersion = 1, preview = false, highestSequence = sequence,
                highestDigest = digest, lastTrustedUtc = time, failedBuildIds = failed });
        internal static void Write<T>(string path, T value)
        { Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, UpdateManifestCodec.JsonOptions)); }
        public void Dispose()
        {
            string full = Path.GetFullPath(Root), parent = Path.GetFullPath(Path.GetTempPath());
            if (!full.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("autumnos-migration-security-", StringComparison.Ordinal))
                throw new InvalidOperationException("Fixture cleanup escaped its owned temporary directory.");
            Directory.Delete(full, true);
        }
    }
}
