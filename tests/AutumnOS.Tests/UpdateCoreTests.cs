using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.Store;
using AutumnOS.Update;

namespace AutumnOS.Tests;

internal static class UpdateCoreTests
{
    private static readonly Lazy<RSA> SigningKey = new(() => RSA.Create(3072));
    private static readonly DateTimeOffset Now = new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("update.signature_raw_bytes_pss_roundtrip", () => Run(async f =>
        {
            var pair = f.Add("1.1.0"); Check(UpdateSignature.Verify(pair.Bytes, pair.Signature, Trust(), Now).Version == "1.1.0");
            Reject(() => UpdateSignature.Verify(pair.Bytes.Concat(" "u8.ToArray()).ToArray(), pair.Signature, Trust(), Now), "UPDATE_SIGNATURE_INVALID");
            await Task.CompletedTask;
        }));
        yield return ("update.sha256_and_feed_key_never_replace_trust", () => Run(async f =>
        {
            var p = f.Add("1.1.0"); Reject(() => UpdateSignature.Verify(p.Bytes, p.Signature, new(new(1, 1, "production", [])), Now), "UPDATE_PRODUCTION_TRUST_NOT_CONFIGURED");
            if (!UpdateTrustStore.IsTestBuild) Check(!UpdateTrustStore.FromEmbedded().IsConfigured);
            await Task.CompletedTask;
        }));
        yield return ("update.unknown_revoked_and_old_root_rejected", () => Run(async f =>
        {
            var p = f.Add("1.1.0");
            Reject(() => UpdateSignature.Verify(p.Bytes, p.Signature, new(new(1, 1, "local-test", [new("other", SigningKey.Value.ExportSubjectPublicKeyInfoPem())])), Now), "UPDATE_UNKNOWN_KEY");
            Reject(() => UpdateSignature.Verify(p.Bytes, p.Signature, new(new(1, 1, "local-test", [new("test-key", SigningKey.Value.ExportSubjectPublicKeyInfoPem(), true), new("other", SigningKey.Value.ExportSubjectPublicKeyInfoPem())])), Now), "UPDATE_REVOKED_KEY");
            Reject(() => UpdateSignature.Verify(p.Bytes, p.Signature, new(new(1, 2, "local-test", [new("test-key", SigningKey.Value.ExportSubjectPublicKeyInfoPem())])), Now), "UPDATE_TRUST_ROOT_VERSION");
            await Task.CompletedTask;
        }));
        yield return ("update.expiry_future_clock_sequence_checks", () => Run(async f =>
        {
            var p = f.Add("1.1.0", 7);
            Reject(() => UpdateSignature.Verify(p.Bytes, p.Signature, Trust(), Now.AddDays(10)), "UPDATE_METADATA_EXPIRED");
            Reject(() => UpdateSignature.Verify(p.Bytes, p.Signature, Trust(), Now.AddDays(-1)), "UPDATE_CLOCK_OR_FUTURE_METADATA");
            Reject(() => UpdateSignature.Verify(p.Bytes, p.Signature, Trust(), Now, 8), "UPDATE_METADATA_REPLAY");
            Check(UpdateSignature.Verify(p.Bytes, p.Signature, Trust(), Now, 7).Sequence == 7); await Task.CompletedTask;
        }));
        yield return ("update.strict_json_duplicates_unknown_bom_missing", () => Run(async f =>
        {
            var p = f.Add("1.1.0"); string json = Encoding.UTF8.GetString(p.Bytes);
            Reject(() => UpdateManifestCodec.Parse(Encoding.UTF8.GetBytes(json.Insert(1, "\"schemaVersion\":1,"))), "UPDATE_DUPLICATE_FIELD");
            Reject(() => UpdateManifestCodec.Parse(Encoding.UTF8.GetBytes(json.Insert(1, "\"arbitraryCommand\":\"run\","))), "UPDATE_METADATA_INVALID");
            Reject(() => UpdateManifestCodec.Parse(new byte[] { 239, 187, 191 }.Concat(p.Bytes).ToArray()), "UPDATE_METADATA_ENCODING_OR_SIZE");
            Reject(() => UpdateManifestCodec.Parse("{}"u8.ToArray()), "UPDATE_METADATA_INVALID"); await Task.CompletedTask;
        }));
        yield return ("update.product_channel_architecture_migration_binding", () => Run(async f =>
        {
            var m = f.Add("1.1.0").Manifest;
            foreach (var invalid in new[] { m with { ProductId = "foreign" }, m with { Repository = "evil/repo" }, m with { Channel = "meta" }, m with { TargetRid = "win-arm64" },
                m with { Data = new(2, 1, false) }, m with { Payload = m.Payload with { ReleaseId = 0 } } }) Reject(() => UpdateManifestCodec.Serialize(invalid));
            await Task.CompletedTask;
        }));
        yield return ("update.paths_reject_traversal_ads_reserved_data_stable", () =>
        {
            foreach (string path in new[] { "../evil.dll", "a/../../evil", "C:/evil", "a\\evil", "a:stream", "a/CON.txt", "a./x", "a/../b", "a//b", "/x", "AutumnOS_Data/Saves/x", "AUTUMNOS.EXE", "AutumnOS.Updater.exe", ".autumnos-update/security.json", "secret.key" })
                Reject(() => UpdatePaths.ValidateManagedRelativePath(path), "UPDATE_PATH_NOT_MANAGED");
        });
        yield return ("update.inventory_excludes_private_keys_credentials_and_uninstaller", () => Run(async f =>
        {
            string root = Path.Combine(f.Root, "inventory"); Directory.CreateDirectory(Path.Combine(root, "nested"));
            await File.WriteAllTextAsync(Path.Combine(root, "AutumnOS.Client.exe"), "unit fixture; not executable");
            await File.WriteAllTextAsync(Path.Combine(root, "nested", "public.dll"), "unit fixture; not executable");
            string[] privateNames = ["private.pk8", "nested/certificate.P12", "nested/credentials.dpapi", "nested/signing.SNK", "AutumnOS.Uninstall.exe"];
            foreach (string name in privateNames)
            {
                await File.WriteAllTextAsync(Path.Combine(root, name), "synthetic private-file sentinel; contains no key or credentials");
                Reject(() => UpdatePaths.ValidateManagedRelativePath(name), "UPDATE_PATH_NOT_MANAGED");
            }
            var files = UpdatePayload.CreateFileList(root);
            Check(files.Count == 2 && files.Any(file => file.Path == "AutumnOS.Client.exe") && files.Any(file => file.Path == "nested/public.dll"));
            Check(privateNames.All(name => File.Exists(Path.Combine(root, name))), "Inventory must preserve excluded files.");
        }));
        yield return ("update.file_table_case_collision_parent_and_stable_delete_rejected", () => Run(async f =>
        {
            var m = f.Add("1.1.0").Manifest;
            Reject(() => UpdateManifestCodec.Validate(m with { Files = m.Files.Concat([m.Files[0] with { Path = "autumnos.client.exe" }]).ToArray() }));
            Reject(() => UpdateManifestCodec.Validate(m with { Files = m.Files.Concat([new UpdateFile("a", 0, Hash([])), new UpdateFile("a/b", 0, Hash([]))]).ToArray() }));
            Reject(() => UpdateManifestCodec.Validate(m with { RemoveFiles = ["AutumnOS_Data/save"] })); await Task.CompletedTask;
        }));
        yield return ("update.payload_roundtrip_and_file_tampering", () => Run(async f =>
        {
            var p = f.Add("1.1.0"); string payload = f.SavePayload(p);
            await UpdatePayload.VerifyAsync(payload, p.Manifest); string extract = Path.Combine(f.Root, "extract"); await UpdatePayload.ExtractVerifiedAsync(payload, extract, p.Manifest);
            Check(File.ReadAllText(Path.Combine(extract, "AutumnOS.Client.exe")) == "isolated unit bytes; never executable");
            await File.AppendAllTextAsync(payload, "x"); await RejectAsync(() => UpdatePayload.VerifyAsync(payload, p.Manifest), "UPDATE_PAYLOAD_SIZE_MISMATCH");
        }));
        yield return ("update.payload_unlisted_and_symlink_entries_rejected", () => Run(async f =>
        {
            var p = f.Add("1.1.0"); byte[] zip;
            using (MemoryStream stream = new()) { using (ZipArchive archive = new(stream, ZipArchiveMode.Create, true)) { var entry = archive.CreateEntry("AutumnOS.Client.exe"); entry.ExternalAttributes = (0xa1ff << 16); using var writer = new StreamWriter(entry.Open()); writer.Write("isolated unit bytes; never executable"); } zip = stream.ToArray(); }
            var m = p.Manifest with { Payload = p.Manifest.Payload with { Bytes = zip.Length, Sha256 = Hash(zip) } };
            string path = Path.Combine(f.Root, "symlink.zip"); await File.WriteAllBytesAsync(path, zip); await RejectAsync(() => UpdatePayload.VerifyAsync(path, m), "UPDATE_PAYLOAD_ENTRY_INVALID");
        }));
        yield return ("update.production_no_root_still_reads_release_list", () => Run(async f =>
        {
            using var service = f.Service(new(new(1, 1, "production", []))); await service.CheckAsync(false);
            Check(f.Requests.Any(u => u.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal)) && service.Snapshot.ErrorCode == "UPDATE_PRODUCTION_TRUST_NOT_CONFIGURED" && service.Snapshot.Candidate is null);
        }));
        yield return ("update.semver_numeric_order_not_release_date_or_latest", () => Run(async f =>
        {
            f.Add("1.9.0", 9); f.Add("1.10.0", 10); f.Add("1.8.0", 8);
            using var service = f.Service(); await service.CheckAsync(false); Check(service.Snapshot.Candidate?.Manifest.Version == "1.10.0");
            Check(f.Requests.All(u => !u.AbsolutePath.EndsWith("latest", StringComparison.Ordinal)));
        }));
        yield return ("update.strict_plus_meta_and_no_downgrade", () => Run(async f =>
        {
            f.Add("0.9.0", 1); f.Add("1.1.0", 2); f.Add("2.0.0-preview.1", 3);
            using var service = f.Service(); await service.CheckAsync(false); Check(service.Snapshot.Candidate?.Manifest.Channel == "plus");
            await service.CheckAsync(true); Check(service.Snapshot.Candidate?.Manifest.Channel == "meta");
            await service.CheckAsync(false); Check(service.Snapshot.Candidate is null && service.Snapshot.Warnings.Contains("UPDATE_METADATA_REPLAY"));
        }));
        yield return ("update.paginates_to_later_release_page", () => Run(async f =>
        {
            for (int i = 0; i < 100; i++) f.Releases.Add(new { draft = true }); f.Add("1.2.0");
            using var service = f.Service(); await service.CheckAsync(false); Check(service.Snapshot.Candidate?.Manifest.Version == "1.2.0"); Check(f.Requests.Any(u => u.Query.Contains("page=2", StringComparison.Ordinal)));
        }));
        yield return ("update.prerelease_release_id_size_source_missing_assets_rejected", () => Run(async f =>
        {
            foreach (string fault in new[] { "prerelease", "release-id", "size", "source", "missing" })
            {
                f.Clear(); f.Add("1.1.0", fault: fault);
                using var service = f.Service(dataSuffix: fault); await service.CheckAsync(false); Check(service.Snapshot.Candidate is null, fault);
            }
        }));
        yield return ("update.download_verification_stage_channel_switch_blocks_commit", () => Run(async f =>
        {
            f.Add("1.1.0"); using var service = f.Service(); await service.CheckAsync(false); await service.DownloadAndStageAsync();
            Check(service.Snapshot.State == UpdateState.Staged, service.Snapshot.ErrorCode ?? "no staged payload"); Check(File.Exists(service.ValidateStagedForCommit().PayloadPath));
            service.SetPreviewEnabled(true); Reject(() => service.ValidateStagedForCommit(), "UPDATE_NOT_STAGED");
        }));
        yield return ("update.persisted_sequence_digest_replay_and_failed_build", () => Run(async f =>
        {
            var first = f.Add("1.2.0", 5); using (var service = f.Service()) { await service.CheckAsync(false); service.RecordFailedCandidate(first.Manifest.BuildId); }
            using (var service = f.Service()) { await service.CheckAsync(false); Check(service.Snapshot.Candidate is null); }
            f.Clear(); f.Add("1.3.0", 5); using var next = f.Service(); await next.CheckAsync(false); Check(next.Snapshot.Candidate is null && next.Snapshot.Warnings.Contains("UPDATE_SEQUENCE_REUSED"));
        }));
        yield return ("update.unknown_signature_bad_bytes_no_candidate", () => Run(async f =>
        {
            var p = f.Add("1.1.0"); f.Assets[p.Manifest.Channel + "-v" + p.Manifest.Version + "/autumn.update.sig"][100] ^= 1;
            using var service = f.Service(); await service.CheckAsync(false); Check(service.Snapshot.Candidate is null);
        }));
        yield return ("update.updater_ledger_survives_data_state_reset", () => Run(async f =>
        {
            var candidate = f.Add("1.1.0", 8); string work = Path.Combine(f.Root, ".autumnos-update"); Directory.CreateDirectory(work);
            await File.WriteAllBytesAsync(Path.Combine(work, "security.json"), JsonSerializer.SerializeToUtf8Bytes(new { highestSequence = 8, failedBuilds = new[] { candidate.Manifest.BuildId }, manifestSha256 = Hash(candidate.Bytes) }));
            using var service = f.Service(); await service.CheckAsync(false); Check(service.Snapshot.Candidate is null && service.Snapshot.Warnings.Contains("UPDATE_CANDIDATE_PREVIOUSLY_FAILED_OR_CURRENT"));
            await File.WriteAllBytesAsync(Path.Combine(work, "security.json"), JsonSerializer.SerializeToUtf8Bytes(new { highestSequence = 9, failedBuilds = Array.Empty<string>(), manifestSha256 = new string('a', 64) }));
            await service.CheckAsync(false); Check(service.Snapshot.Candidate is null && service.Snapshot.Warnings.Contains("UPDATE_METADATA_REPLAY"));
        }));
        yield return ("update.failed_critical_write_does_not_change_preview_in_memory", () => Run(async f =>
        {
            using var service = new UpdateService(Path.Combine(f.Root, "lease-fail"), "1.0.0", "build-a", f, f.Downloads, Trust(), () => Now, _ => throw new InvalidOperationException("busy"));
            try { service.SetPreviewEnabled(true); throw new Exception("Write should be blocked"); } catch (InvalidOperationException) { }
            Check(service.Snapshot.Channel == "plus"); await Task.CompletedTask;
        }));
        yield return ("update.critical_state_save_uses_common_lease", () => Run(async f =>
        {
            int writes = 0; using var service = new UpdateService(Path.Combine(f.Root, "lease"), "1.0.0", "build-a", f, f.Downloads, Trust(), () => Now, _ => { writes++; return new Lease(); });
            service.SetPreviewEnabled(true); Check(writes == 1); await Task.CompletedTask;
        }));
        yield return ("update.host_download_profile_does_not_relax_community_autumn_limit", () =>
        {
            var request = new DownloadRequest("cn.labchronicles.autumnos", "1.1.0", new("https://github.com/paimeng5201314/autumnlab/releases/download/plus-v1.1.0/payload.zip"), 21L * 1024 * 1024, new string('a', 64), 1, 1, 1);
            try { DownloadService.ValidateRequest(request); throw new InvalidOperationException("Community profile accepted host payload."); } catch (ArgumentException) { }
        });
    }
    private sealed class Lease : IDisposable { public void Dispose() { } }
    private static UpdateTrustStore Trust() => new(new(1, 1, "local-test", [new("test-key", SigningKey.Value.ExportSubjectPublicKeyInfoPem())]));
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Check(bool condition, string message = "Update assertion failed.") { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action, string? code = null) { try { action(); } catch (UpdateException ex) when (code is null || code == ex.Code) { return; } throw new InvalidOperationException("Expected update rejection " + code); }
    private static async Task RejectAsync(Func<Task> action, string? code = null) { try { await action(); } catch (UpdateException ex) when (code is null || code == ex.Code) { return; } throw new InvalidOperationException("Expected update rejection " + code); }
    private static void Run(Func<Fixture, Task> run) { using Fixture fixture = new(); run(fixture).GetAwaiter().GetResult(); }
    private sealed record Signed(UpdateManifest Manifest, byte[] Bytes, byte[] Signature, byte[] Zip);
    private sealed class Fixture : IGitHubTransport, IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "autumnos-update-core-tests-" + Guid.NewGuid().ToString("N"));
        public Dictionary<string, byte[]> Assets { get; } = new(StringComparer.Ordinal);
        public List<object> Releases { get; } = [];
        public List<Uri> Requests { get; } = [];
        public DownloadService Downloads { get; }
        public Fixture() { Directory.CreateDirectory(Root); Downloads = new(Root, this, () => new(), hostUpdate: true); }
        public void Clear() { Releases.Clear(); Assets.Clear(); }
        public UpdateService Service(UpdateTrustStore? trust = null, string dataSuffix = "data") => new(Path.Combine(Root, dataSuffix), "1.0.0", "build-a", this, Downloads, trust ?? Trust(), () => Now);
        public Signed Add(string version, long sequence = 1, string fault = "")
        {
            string channel = version.Contains('-') ? "meta" : "plus", tag = channel + "-v" + version;
            byte[] file = Encoding.UTF8.GetBytes("isolated unit bytes; never executable"), zip;
            using (MemoryStream memory = new()) { using (ZipArchive archive = new(memory, ZipArchiveMode.Create, true)) { var entry = archive.CreateEntry("AutumnOS.Client.exe"); using var output = entry.Open(); output.Write(file); } zip = memory.ToArray(); }
            var manifest = new UpdateManifest(1, channel, version, "unit-build-" + version, "win-x64", "1.0.0", sequence, Now.AddMinutes(-5), Now.AddDays(7), "test-key", 1,
                new("payload.zip", zip.Length, Hash(zip), sequence, sequence * 10 + 3), [new("AutumnOS.Client.exe", file.Length, Hash(file))], [], new(1, 1, true));
            byte[] bytes = UpdateManifestCodec.Serialize(manifest), signature = UpdateSignature.Create(bytes, SigningKey.Value, "test-key", 1);
            Assets[tag + "/autumn.update.json"] = bytes; Assets[tag + "/autumn.update.sig"] = signature; Assets[tag + "/payload.zip"] = zip;
            object Asset(string name, byte[] content, long id) => new { id, name, size = content.Length + (fault == "size" && name == "payload.zip" ? 1 : 0), browser_download_url = "https://github.com/" + (fault == "source" ? "foreign/repo" : UpdateService.Repository) + "/releases/download/" + tag + "/" + name };
            var assets = new[] { Asset("autumn.update.json", bytes, sequence * 10 + 1), Asset("autumn.update.sig", signature, sequence * 10 + 2), Asset("payload.zip", zip, sequence * 10 + 3) };
            Releases.Add(new { id = sequence + (fault == "release-id" ? 1 : 0), draft = false, tag_name = tag, prerelease = fault == "prerelease" || channel == "meta", published_at = "2020-01-01T00:00:00Z", body = "unit metadata", assets = fault == "missing" ? assets[..1] : assets });
            return new(manifest, bytes, signature, zip);
        }
        public string SavePayload(Signed signed) { string path = Path.Combine(Root, "payload.zip"); File.WriteAllBytes(path, signed.Zip); return path; }
        public Task<HttpResponseMessage> SendAsync(Uri url, GitHubRequestKind kind, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Requests.Add(url); byte[] bytes;
            if (kind == GitHubRequestKind.Api)
            {
                int page = int.Parse(url.Query.Split("page=", StringSplitOptions.None)[^1]);
                bytes = JsonSerializer.SerializeToUtf8Bytes(Releases.Skip((page - 1) * 100).Take(100).ToArray());
            }
            else bytes = Assets[Uri.UnescapeDataString(url.AbsolutePath.Split("/releases/download/", StringSplitOptions.None)[1])];
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
            response.Headers.ETag = new('"' + Hash(bytes) + '"'); return Task.FromResult(response);
        }
        public void Dispose()
        {
            Downloads.DisposeAsync().AsTask().GetAwaiter().GetResult();
            string resolved = Path.GetFullPath(Root), prefix = Path.Combine(Path.GetTempPath(), "autumnos-update-core-tests-");
            if (resolved.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) Directory.Delete(resolved, true);
        }
    }
}
