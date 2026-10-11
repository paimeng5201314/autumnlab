using System.IO.Compression;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using AutumnOS.Store;
using AutumnOS.Update;
using AutumnOS.UpdateProtocol;

namespace AutumnOS.Tests;

internal static class UpdateRollbackTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("rollback.exact_signed_target_consent_does_not_disable_sequence_floor", ScopedAuthorization);
        yield return ("rollback.supported_schema_pair_upgrade_use_and_manual_rollback_preserves_data", UpgradeUseRollback);
        yield return ("rollback.snapshot_excludes_account_onboarding_policy_games_and_rejects_corruption", SnapshotScope);
        yield return ("rollback.failed_lower_sequence_keeps_highest_digest_and_security_floor", SecurityWatermark);
        yield return ("rollback.portable_original_entry_has_durable_binding_outside_runtime", PortableBinding);
        yield return ("rollback.old_protocol_and_unknown_data_schema_remain_disabled", Compatibility);
        yield return ("rollback.independent_apply_guard_rejects_withdrawn_or_replaced_release", SourceGuard);
        yield return ("rollback.independent_apply_guard_revalidates_build_metadata_urls_and_exact_manifest", SourceGuardBuildMetadata);
    }

    private static void ScopedAuthorization() => Temp(root =>
    {
        using var rsa = RSA.Create(3072); var trust = new UpdateTrustStore(new(1, 1, "local-test", [new("fixture", rsa.ExportSubjectPublicKeyInfoPem())]));
        var m = Manifest("1.0.0", "A", 10, [Entry("AutumnOS.Client.exe", "A")]);
        byte[] bytes = UpdateManifestCodec.Serialize(m), signature = UpdateSignature.Create(bytes, rsa, "fixture", 1);
        var candidate = new UpdateCandidate(1, "plus-v1.0.0", "", m, new Uri("https://github.com/paimeng5201314/autumnlab/releases/download/plus-v1.0.0/payload.zip"), 1, bytes, signature);
        var now = DateTimeOffset.UtcNow; var consent = UpdateRollbackAuthorization.Create(candidate, "B", "1.1.0", now);
        Reject(() => UpdateSignature.Verify(bytes, signature, trust, now, 20));
        Assert(UpdateSignature.VerifyForRollback(bytes, signature, trust, now, 20, consent, "B", "1.1.0").BuildId == "A");
        foreach (var invalid in new[] { consent with { AssetId = 2 }, consent with { ReleaseId = 2 }, consent with { ReleaseTag = "other" }, consent with { ManifestSha256 = new string('0', 64) }, consent with { ExpiresUtc = now.AddSeconds(-1) } })
            Reject(() => UpdateSignature.VerifyForRollback(bytes, signature, trust, now, 20, invalid, "B", "1.1.0"));
        Reject(() => UpdateSignature.VerifyForRollback(bytes, signature, trust, now, 20, consent, "C", "1.1.0"));
        Reject(() => UpdateSignature.VerifyForRollback(bytes, signature, trust, now, 20, consent, "B", "1.2.0"));
        var replaced = UpdateManifestCodec.Serialize(m with { BuildId = "replacement" });
        Reject(() => UpdateSignature.VerifyForRollback(replaced, UpdateSignature.Create(replaced, rsa, "fixture", 1), trust, now, 20, consent, "B", "1.1.0"));
        Reject(() => UpdateSignature.Verify(bytes, signature, trust, now, 20));
    });

    private static void UpgradeUseRollback() => Temp(root =>
    {
        var a = Install(root, "1.0.0", "A"); WriteAppearance(root, "light", 1);
        WriteSentinels(root);
        var upgrade = Stage(root, "1.1.0", "B", 20);
        Apply(root, upgrade.Tx, upgrade.Offer, upgrade.Manifest);
        Assert(UpdateFiles.ReadInstalled(root).BuildId == "B");
        Assert(File.Exists(Path.Combine(root, "new-in-B.dll")));
        Assert(File.ReadAllText(Path.Combine(upgrade.Tx, "data-snapshot", "desktop-preferences.json")).Contains("light", StringComparison.Ordinal));
        // This is a real same-schema use after upgrade, not just changing an installed version label.
        WriteAppearance(root, "dark", 2);
        var rollback = Stage(root, a.Version, a.BuildId, 10);
        using var rsa = RSA.Create(3072);
        byte[] bytes = UpdateManifestCodec.Serialize(rollback.Manifest), signature = UpdateSignature.Create(bytes, rsa, "fixture", 1);
        var candidate = new UpdateCandidate(1, "plus-v" + a.Version, "", rollback.Manifest, new Uri("https://github.com/paimeng5201314/autumnlab/releases/download/A/payload.zip"), 1, bytes, signature);
        var consent = UpdateRollbackAuthorization.Create(candidate, "B", "1.1.0", DateTimeOffset.UtcNow);
        UpdateSignature.VerifyForRollback(bytes, signature, new(new(1, 1, "local-test", [new("fixture", rsa.ExportSubjectPublicKeyInfoPem())])), DateTimeOffset.UtcNow, 20, consent, "B", "1.1.0");
        Assert(UpdateTransaction.GetCompatibility(root, rollback.Manifest, true) is null);
        Apply(root, rollback.Tx, rollback.Offer with { RollbackAuthorization = consent }, rollback.Manifest);
        Assert(UpdateFiles.ReadInstalled(root).BuildId == "A");
        Assert(File.ReadAllText(Path.Combine(root, "AutumnOS.Client.exe")) == "A-client");
        Assert(!File.Exists(Path.Combine(root, "new-in-B.dll")));
        Assert(File.ReadAllText(Config(root, "desktop-preferences.json")).Contains("dark", StringComparison.Ordinal));
        AssertSentinels(root);
        // Health failure of A restores B and the exact matching desktop snapshot.
        WriteAppearance(root, "light", 3);
        Invoke("Restore", root, "UPDATE_HEALTH_FAILED");
        Assert(UpdateFiles.ReadInstalled(root).BuildId == "B");
        Assert(File.ReadAllText(Config(root, "desktop-preferences.json")).Contains("dark", StringComparison.Ordinal));
        AssertSentinels(root);
    });

    private static void SnapshotScope() => Temp(root =>
    {
        var installed = Install(root, "1.0.0", "A"); WriteAppearance(root, "light", 1); WriteSentinels(root);
        string id = Guid.NewGuid().ToString("N");
        var snapshot = UpdateDataSnapshot.Capture(root, id, installed);
        string directory = Path.Combine(UpdateFiles.Transaction(root, id), "data-snapshot");
        Assert(snapshot.Files.Length == 1 && snapshot.Files[0].Path == "desktop-preferences.json");
        Assert(!string.Join("", Directory.GetFiles(directory).Select(File.ReadAllText)).Contains("credential-sentinel", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(directory, "desktop-preferences.json"), "corrupted");
        Reject(() => UpdateDataSnapshot.Restore(root, id, installed));
        AssertSentinels(root);
        File.WriteAllText(Config(root, "desktop-preferences.json"), "{\"schemaVersion\":1,\"token\":\"credential-sentinel\"}");
        Reject(() => UpdateDataSnapshot.Capture(root, Guid.NewGuid().ToString("N"), installed));
    });

    private static void SecurityWatermark() => Temp(root =>
    {
        string path = Path.Combine(UpdateFiles.Work(root), "security.json");
        UpdateFiles.Write(path, new UpdateSecurityState(100, [], new string('a', 64), "0.9.0"));
        Invoke("RecordFailure", root, "bad-lower-version", 12L, new string('b', 64));
        var state = UpdateFiles.Read<UpdateSecurityState>(path);
        Assert(state.HighestSequence == 100 && state.ManifestSha256 == new string('a', 64) && state.MinimumVersion == "0.9.0");
    });

    private static void PortableBinding() => Temp(entry =>
    {
        string seed = new string('a', 64), root = Path.Combine(entry, "AutumnOS_Data", "System", "Product-" + seed[..20]);
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "autumn.portable"), AutumnOS.Contracts.PortableLayout.Marker);
        File.WriteAllText(Path.Combine(root, ".autumnos-single-file-seed"), seed);
        PortableEntryBinding.Commit(root, "B", "1.1.0");
        string binding = Path.Combine(entry, "AutumnOS_Data", "System", "Entry-" + seed[..20] + ".json");
        Assert(UpdateFiles.Read<PortableEntryRecord>(binding).BuildId == "B");
        Directory.Delete(root, true);
        Assert(File.Exists(binding)); // The original outer EXE now refuses to reseed A from its own payload.
    });

    private static void Compatibility() => Temp(root =>
    {
        Install(root, "1.1.0", "B");
        var target = Manifest("1.0.0", "A", 10, [Entry("AutumnOS.Client.exe", "A")]);
        Assert(UpdateTransaction.GetCompatibility(root, target, true) is null);
        Assert(UpdateTransaction.GetCompatibility(root, target with { MinimumUpdaterVersion = "1.0.0" }, true) == "UPDATE_ROLLBACK_PROTOCOL_UNSUPPORTED");
        Assert(UpdateTransaction.GetCompatibility(root, target with { Data = new(2, 1, false) }, true) == "UPDATE_DATA_MIGRATION_UNSUPPORTED");
        UpdateFiles.Write(Path.Combine(UpdateFiles.Work(root), "security.json"), new UpdateSecurityState(30, [], "", "1.0.1"));
        Assert(UpdateTransaction.GetCompatibility(root, target, true) == "UPDATE_MINIMUM_SECURITY_VERSION");
    });

    private static void SourceGuard()
    {
        var manifest = Manifest("1.0.0", "A", 10, [Entry("AutumnOS.Client.exe", "A")]);
        var feed = new GuardFeed();
        void Verify() => UpdateApplySourceGuard.VerifyAsync(manifest, "plus-v1.0.0", feed, CancellationToken.None).GetAwaiter().GetResult();
        Verify(); Assert(feed.Calls == 1);
        feed.Draft = true; Reject(Verify); feed.Draft = false;
        feed.Tag = "other"; Reject(Verify); feed.Tag = "plus-v1.0.0";
        feed.AssetId = 2; Reject(Verify); feed.AssetId = 1;
        feed.Size = 2; Reject(Verify); feed.Size = 1;
        feed.Preview = true; Reject(Verify); feed.Preview = false;
        feed.Status = HttpStatusCode.NotFound; Reject(Verify);
        feed.Status = HttpStatusCode.TooManyRequests; Reject(Verify);
    }

    private static void SourceGuardBuildMetadata()
    {
        var manifest = Manifest("1.0.0+build.2", "A-build-2", 10, [Entry("AutumnOS.Client.exe", "A")]);
        byte[] approved = UpdateManifestCodec.Serialize(manifest);
        string digest = Convert.ToHexStringLower(SHA256.HashData(approved));
        var feed = new GuardFeed { Tag = "plus-v1.0.0+build.2", MetadataBytes = approved };
        void Verify() => UpdateApplySourceGuard.VerifyAsync(manifest, feed.Tag, feed, CancellationToken.None, digest).GetAwaiter().GetResult();
        // GitHub can return a literal '+' while the request uses the equivalent '%2B'.
        Verify(); Assert(feed.Calls == 2 && feed.MetadataCalls == 1);
        feed.EscapeTag = true;
        Verify(); Assert(feed.Calls == 4 && feed.MetadataCalls == 2);
        feed.MetadataBytes = UpdateManifestCodec.Serialize(manifest with { BuildId = "replaced" });
        Reject(Verify); Assert(feed.MetadataCalls == 3);
        feed.MetadataBytes = approved;
        string official = "https://github.com/paimeng5201314/autumnlab/releases/download/plus-v1.0.0%2Bbuild.2/autumn.update.json";
        foreach (string invalid in new[] { official.Replace("/autumnlab/", "/fork/"), official + "?download=1", official + "#fragment",
            official.Replace("github.com/", "github.com.evil.invalid/"), official.Replace("%2B", "%252B"),
            official.Replace("autumn.update.json", "payload.zip"), official.Replace("https://", "https://user@") })
        {
            feed.MetadataUrlOverride = invalid;
            Reject(Verify);
            Assert(feed.MetadataCalls == 3); // Invalid source is rejected before any asset request.
        }
    }

    private sealed class GuardFeed : IGitHubTransport
    {
        internal bool Draft, Preview; internal string Tag = "plus-v1.0.0"; internal long AssetId = 1, Size = 1;
        internal int Calls, MetadataCalls; internal HttpStatusCode Status = HttpStatusCode.OK;
        internal bool EscapeTag; internal byte[]? MetadataBytes; internal string? MetadataUrlOverride;
        public Task<HttpResponseMessage> SendAsync(Uri url, GitHubRequestKind kind, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            Calls++;
            if (kind == GitHubRequestKind.Asset)
            {
                Assert(MetadataBytes is not null && url.AbsoluteUri == "https://github.com/paimeng5201314/autumnlab/releases/download/" + Uri.EscapeDataString(Tag) + "/autumn.update.json");
                MetadataCalls++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(MetadataBytes!) });
            }
            Assert(url.AbsoluteUri == "https://api.github.com/repos/paimeng5201314/autumnlab/releases/1" && kind == GitHubRequestKind.Api);
            string prefix = "https://github.com/paimeng5201314/autumnlab/releases/download/" + (EscapeTag ? Uri.EscapeDataString(Tag) : Tag) + "/";
            var assets = new List<object> { new { id = AssetId, name = "payload.zip", size = Size, digest = "sha256:" + new string('a', 64), browser_download_url = prefix + "payload.zip" } };
            if (MetadataBytes is not null)
            {
                assets.Add(new { id = 2, name = "autumn.update.json", size = MetadataBytes.Length, browser_download_url = MetadataUrlOverride ?? prefix + "autumn.update.json" });
                assets.Add(new { id = 3, name = "autumn.update.sig", size = 1, browser_download_url = prefix + "autumn.update.sig" });
            }
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(JsonSerializer.Serialize(new { id = 1, tag_name = Tag, draft = Draft, prerelease = Preview,
                assets })) });
        }
    }

    private static InstalledBuild Install(string root, string version, string build)
    {
        var files = new[] { Entry("AutumnOS.Client.exe", build + "-client"), Entry("core.dll", build + "-core"), Entry("resources.pri", build + "-resources") };
        foreach (var file in files) File.WriteAllText(Path.Combine(root, file.Path), file.Path == "AutumnOS.Client.exe" ? build + "-client" : file.Path == "core.dll" ? build + "-core" : build + "-resources");
        var installed = new InstalledBuild(1, version, build, files); UpdateFiles.Write(Path.Combine(root, UpdateFiles.ReceiptName), installed); return installed;
    }
    private static (string Tx, HandoffRecord Offer, UpdateManifest Manifest) Stage(string root, string version, string build, long sequence)
    {
        string id = Guid.NewGuid().ToString("N"), tx = UpdateFiles.Transaction(root, id); Directory.CreateDirectory(tx);
        var files = new Dictionary<string, string> { ["AutumnOS.Client.exe"] = build + "-client", ["core.dll"] = build + "-core", ["resources.pri"] = build + "-resources" };
        if (build == "B") files.Add("new-in-B.dll", "B-only-library");
        string payload = Path.Combine(tx, "payload.zip");
        using (var zip = ZipFile.Open(payload, ZipArchiveMode.Create)) foreach (var file in files)
        { using var output = new StreamWriter(zip.CreateEntry(file.Key).Open(), new UTF8Encoding(false)); output.Write(file.Value); }
        var manifest = Manifest(version, build, sequence, files.Select(f => Entry(f.Key, f.Value)).ToArray()) with { Payload = new("payload.zip", new FileInfo(payload).Length, UpdateFiles.Hash(payload)) };
        File.WriteAllBytes(Path.Combine(tx, "autumn.update.json"), UpdateManifestCodec.Serialize(manifest));
        var current = UpdateFiles.ReadInstalled(root);
        var offer = new HandoffRecord(1, id, root, UpdateFiles.DataRoot(root), Environment.ProcessId, 0, current.BuildId, current.Version, UpdateFiles.Hash(Path.Combine(tx, "autumn.update.json")), DateTimeOffset.UtcNow.AddMinutes(1));
        return (tx, offer, manifest);
    }
    private static UpdateManifest Manifest(string version, string build, long sequence, InstalledFile[] files) => new(1, "plus", version, build, "win-x64", "1.1.0", sequence,
        DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), "fixture", 1, new("payload.zip", 1, new string('a', 64)), files.Select(f => new UpdateFile(f.Path, f.Bytes, f.Sha256)).ToArray(), [], new(1, 1, true));
    private static void WriteAppearance(string root, string theme, int revision) => UpdateFiles.Write(Config(root, "desktop-preferences.json"), new { schemaVersion = 1, theme, wallpaper = "warm", revision, updatedUtc = DateTimeOffset.UtcNow });
    private static string Config(string root, string name) => Path.Combine(root, "AutumnOS_Data", "Config", name);
    private static void WriteSentinels(string root)
    {
        foreach (string path in Sentinels)
        { string full = Path.Combine(root, "AutumnOS_Data", path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, "credential-sentinel:" + path); }
    }
    private static readonly string[] Sentinels = ["Config/first-run.json", "Config/system-update.json", "Identity/session.dpapi", "Saves/game/save.json", "Apps/app/data.json"];
    private static void AssertSentinels(string root) { foreach (string path in Sentinels) Assert(File.ReadAllText(Path.Combine(root, "AutumnOS_Data", path)) == "credential-sentinel:" + path); }
    private static InstalledFile Entry(string path, string content) { byte[] bytes = Encoding.UTF8.GetBytes(content); return new(path, bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes))); }
    private static void Apply(string root, string tx, HandoffRecord offer, UpdateManifest manifest) => Invoke("ApplyFiles", root, tx, offer, manifest);
    private static void Invoke(string method, params object?[] args)
    {
        try { typeof(UpdateTransaction).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args); }
        catch (TargetInvocationException e) when (e.InnerException is not null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); }
    }
    private static void Assert(bool condition) { if (!condition) throw new Exception("Rollback protection assertion failed."); }
    private static void Reject(Action action) { try { action(); } catch (Exception e) when (e is IOException or UpdateException) { return; } throw new Exception("Unsafe rollback unexpectedly accepted."); }
    private static void Temp(Action<string> action)
    { string root = Path.Combine(Path.GetTempPath(), "AutumnOS-rollback-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root); try { action(root); } finally { Directory.Delete(root, true); } }
}
