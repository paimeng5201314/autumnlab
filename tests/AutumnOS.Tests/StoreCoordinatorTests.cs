using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AutumnOS.Packages;
using AutumnOS.Store;

namespace AutumnOS.Tests;

internal static class StoreCoordinatorTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("store_pipeline.real_archive_download_verify_register_event_order", () => Run(Install));
        yield return ("store_pipeline.queue_rejects_cross_repository_asset", () => Run(CrossRepository));
        yield return ("store_pipeline.queue_rejects_asset_digest_size_name_disagreement", () => Run(AssetMetadata));
        yield return ("store_pipeline.package_permission_mismatch_preserves_existing_save", () => Run(PermissionMismatch));
        yield return ("store_pipeline.bad_download_hash_never_installs", () => Run(BadHash));
        yield return ("store_pipeline.persisted_intent_resumes_after_service_restart", () => Run(Restart));
        yield return ("store_pipeline.tampered_repository_intent_is_rejected", () => Run(TamperedIntent));
        yield return ("store_pipeline.repeated_commit_is_idempotent", () => Run(RepeatedCommit));
        yield return ("store_pipeline.completed_task_still_rejects_changed_permissions", () => Run(CompletedMetadataMismatch));
        yield return ("store_pipeline.duplicate_persisted_source_fields_are_rejected", () => Run(DuplicateIntent));
        yield return ("store_pipeline.running_instance_blocks_update_and_retry_preserves_source", () => Run(RunningUpdate));
        yield return ("store_pipeline.interrupted_registry_commit_retries_without_data_loss", () => Run(InterruptedInstall));
        yield return ("store_pipeline.shutdown_after_registry_commit_keeps_success", () => Run(ShutdownAfterCommit));
        yield return ("store_pipeline.restart_reconciles_only_verified_committed_install", () => Run(ReconcileCommitted));
    }

    private static async Task Install()
    {
        using Fixture fixture = new();
        await using var queue = fixture.Queue(); var registry = fixture.Registry(); var coordinator = fixture.Coordinator(queue, registry);
        int commits = 0; registry.Changed += change => { Check(registry.Find(change.AppId) is not null, "event before registration"); commits++; };
        var details = fixture.Details(); string id = coordinator.Queue(details, details.Versions[0]).Id;
        var downloaded = await Wait(queue, id, DownloadState.AwaitingInstall);
        Check(downloaded.BytesReceived == fixture.Payload.Length && registry.GetInstalled().Length == 0 && commits == 0, "download incorrectly counted as install");
        var installed = await coordinator.InstallAsync(id, false, default);
        Check(installed.Source.RepositoryId == 73 && installed.Provenance?.ReleaseId == 81 && installed.Provenance.AssetId == 91 &&
            installed.Package.HostSource == "github:73" && queue.Snapshot().Single().State == DownloadState.Completed && commits == 1, "identity/status/event lost");
        ApplicationInstallService.VerifyInstalled(installed.Package);
        Check(File.ReadAllText(Path.Combine(installed.Package.DirectoryPath, "index.html")).Contains("fixture"), "real package bytes not installed");
    }

    private static async Task CrossRepository()
    {
        using Fixture fixture = new(); await using var queue = fixture.Queue(); var registry = fixture.Registry(); var coordinator = fixture.Coordinator(queue, registry);
        var details = fixture.Details(); var bad = details.Versions[0] with { Asset = details.Versions[0].Asset! with { DownloadUri = new("https://github.com/foreign/repo/releases/download/v1.0.0/fixture.autumn") } };
        Reject(() => coordinator.Queue(details with { Versions = [bad] }, bad));
        Check(queue.Snapshot().Count == 0 && registry.GetInstalled().Length == 0, "cross repository produced download/registration");
    }

    private static async Task AssetMetadata()
    {
        using Fixture fixture = new(); await using var queue = fixture.Queue(); var coordinator = fixture.Coordinator(queue, fixture.Registry());
        var details = fixture.Details(); var version = details.Versions[0]; var asset = version.Asset!;
        foreach (var badAsset in new[] { asset with { Digest = "sha256:" + new string('b', 64) }, asset with { Size = asset.Size + 1 }, asset with { Name = "different.autumn" } })
        {
            var bad = version with { Asset = badAsset };
            Reject(() => coordinator.Queue(details with { Versions = [bad] }, bad));
        }
        Check(queue.Snapshot().Count == 0, "bad metadata created background task");
    }

    private static async Task PermissionMismatch()
    {
        using Fixture fixture = new(); await using var queue = fixture.Queue(); var registry = fixture.Registry(); var coordinator = fixture.Coordinator(queue, registry);
        string sentinel = fixture.SaveSentinel(); byte[] before = File.ReadAllBytes(sentinel);
        var details = fixture.Details(); var version = details.Versions[0] with { Manifest = details.Versions[0].Manifest! with { Permissions = ["identity.profile"] } };
        string id = coordinator.Queue(details with { Versions = [version] }, version).Id;
        await Wait(queue, id, DownloadState.AwaitingInstall);
        await RejectAsync(() => coordinator.InstallAsync(id, false, default), "PACKAGE_RELEASE_MISMATCH");
        Check(registry.GetInstalled().Length == 0 && File.ReadAllBytes(sentinel).SequenceEqual(before), "failed mismatch damaged source saves");
        Check(queue.Snapshot().Single().State == DownloadState.AwaitingInstall && queue.Snapshot().Single().ErrorCode == "PACKAGE_RELEASE_MISMATCH", "failed install masked as completed");
    }

    private static async Task BadHash()
    {
        using Fixture fixture = new(); await using var queue = fixture.Queue(); var registry = fixture.Registry(); var coordinator = fixture.Coordinator(queue, registry);
        var details = fixture.Details(); var version = details.Versions[0] with
        { Manifest = details.Versions[0].Manifest! with { Sha256 = new string('a', 64) }, Asset = details.Versions[0].Asset! with { Digest = "sha256:" + new string('a', 64) } };
        string id = coordinator.Queue(details with { Versions = [version] }, version).Id;
        await Wait(queue, id, DownloadState.Failed);
        await RejectAsync(() => coordinator.InstallAsync(id, false, default));
        Check(registry.GetInstalled().Length == 0, "hash failure created app");
    }

    private static async Task Restart()
    {
        using Fixture fixture = new(); string id;
        await using (var queue = fixture.Queue())
        {
            var coordinator = fixture.Coordinator(queue, fixture.Registry()); var details = fixture.Details();
            id = coordinator.Queue(details, details.Versions[0]).Id; await Wait(queue, id, DownloadState.AwaitingInstall);
        }
        await using (var queue = fixture.Queue())
        {
            var registry = fixture.Registry(); var installed = await fixture.Coordinator(queue, registry).InstallAsync(id, false, default);
            Check(installed.Package.Manifest.AppId == "fixture.pipeline" && queue.Snapshot().Single().State == DownloadState.Completed, "restarted intent could not install verified cache");
        }
    }

    private static async Task TamperedIntent()
    {
        using Fixture fixture = new(); await using var queue = fixture.Queue(); var registry = fixture.Registry(); var coordinator = fixture.Coordinator(queue, registry);
        var details = fixture.Details(); string id = coordinator.Queue(details, details.Versions[0]).Id; await Wait(queue, id, DownloadState.AwaitingInstall);
        string path = Path.Combine(fixture.Root, "Downloads", "store-install-intents.json");
        JsonNode data = JsonNode.Parse(File.ReadAllText(path))!; data[0]!["Repository"]!["RepositoryId"] = 999;
        File.WriteAllText(path, data.ToJsonString());
        await RejectAsync(() => fixture.Coordinator(queue, registry).InstallAsync(id, false, default), "STORE_INSTALL_IDENTITY_MISMATCH");
        Check(registry.GetInstalled().Length == 0, "tampered intent rebound repository");
    }

    private static async Task RepeatedCommit()
    {
        using Fixture fixture = new(); await using var queue = fixture.Queue(); var registry = fixture.Registry(); var coordinator = fixture.Coordinator(queue, registry);
        int events = 0; registry.Changed += _ => events++;
        var details = fixture.Details(); string id = coordinator.Queue(details, details.Versions[0]).Id; await Wait(queue, id, DownloadState.AwaitingInstall);
        var first = await coordinator.InstallAsync(id, false, default); var second = await coordinator.InstallAsync(id, false, default);
        Check(first.Package.DirectoryPath == second.Package.DirectoryPath && events == 1 && registry.GetInstalled().Length == 1, "duplicate install produced repair/new generation/event");
    }

    private static async Task RunningUpdate()
    {
        using Fixture fixture = new(); await using var queue = fixture.Queue(); var registry = fixture.Registry(); var coordinator = fixture.Coordinator(queue, registry);
        var firstDetails = fixture.Details(); string firstId = coordinator.Queue(firstDetails, firstDetails.Versions[0]).Id; await Wait(queue, firstId, DownloadState.AwaitingInstall);
        var first = await coordinator.InstallAsync(firstId, false, default);
        fixture.SetVersion("2.0.0"); var secondDetails = fixture.Details(); string secondId = coordinator.Queue(secondDetails, secondDetails.Versions[0]).Id; await Wait(queue, secondId, DownloadState.AwaitingInstall);
        using (registry.EnterRuntimeLease("fixture.pipeline"))
        {
            await RejectAsync(() => coordinator.InstallAsync(secondId, false, default), "PACKAGE_APP_RUNNING");
            Check(registry.Find("fixture.pipeline")!.Package.DirectoryPath == first.Package.DirectoryPath, "live resource replaced");
        }
        var second = await coordinator.InstallAsync(secondId, false, default);
        Check(second.Package.Manifest.Version == "2.0.0" && second.Package.HostSource == first.Package.HostSource, "retry failed/split source");
    }

    private static async Task CompletedMetadataMismatch()
    {
        using Fixture fixture = new(); await using var queue = fixture.Queue(); var registry = fixture.Registry(); var coordinator = fixture.Coordinator(queue, registry);
        var details = fixture.Details(); string id = coordinator.Queue(details, details.Versions[0]).Id; await Wait(queue, id, DownloadState.AwaitingInstall);
        var old = await coordinator.InstallAsync(id, false, default);
        var changed = details.Versions[0] with { Manifest = details.Versions[0].Manifest! with { Permissions = ["identity.profile"] } };
        Check(coordinator.Queue(details with { Versions = [changed] }, changed).Id == id, "fixture did not address completed task");
        await RejectAsync(() => coordinator.InstallAsync(id, false, default), "PACKAGE_RELEASE_MISMATCH");
        Check(registry.Find(old.AppId)!.Package.DirectoryPath == old.Package.DirectoryPath, "changed metadata rewrote installation");
    }

    private static async Task DuplicateIntent()
    {
        using Fixture fixture = new(); await using var queue = fixture.Queue(); var registry = fixture.Registry(); var coordinator = fixture.Coordinator(queue, registry);
        var details = fixture.Details(); string id = coordinator.Queue(details, details.Versions[0]).Id; await Wait(queue, id, DownloadState.AwaitingInstall);
        string path = Path.Combine(fixture.Root, "Downloads", "store-install-intents.json");
        string original = File.ReadAllText(path);
        string altered = original.Replace("\"RepositoryId\":73", "\"RepositoryId\":73,\"RepositoryId\":73", StringComparison.Ordinal);
        Check(altered != original, "fixture source field not located"); File.WriteAllText(path, altered);
        await RejectAsync(() => fixture.Coordinator(queue, registry).InstallAsync(id, false, default), "STORE_INSTALL_HISTORY_INVALID");
        Check(registry.GetInstalled().Length == 0, "ambiguous intent installed");
    }

    private static async Task InterruptedInstall()
    {
        using Fixture fixture = new(); await using var queue = fixture.Queue(); var registry = fixture.Registry(); var coordinator = fixture.Coordinator(queue, registry);
        var details = fixture.Details(); string id = coordinator.Queue(details, details.Versions[0]).Id; await Wait(queue, id, DownloadState.AwaitingInstall);
        var failing = fixture.Registry(new(point => { if (point == InstallCommitPoint.BeforeRegistryCommit) throw new IOException("isolated interruption"); }));
        await RejectAsync(() => fixture.Coordinator(queue, failing).InstallAsync(id, false, default), "PACKAGE_IO_ERROR");
        Check(registry.GetInstalled().Length == 0 && queue.Snapshot().Single().State == DownloadState.AwaitingInstall, "interrupted install falsely complete");
        var installed = await coordinator.InstallAsync(id, false, default);
        Check(installed.Package.Manifest.AppId == "fixture.pipeline" && registry.GetInstalled().Length == 1, "retry failed after staged content");
    }

    private static async Task ShutdownAfterCommit()
    {
        using Fixture fixture = new(); await using var queue = fixture.Queue(); using var cancellation = new CancellationTokenSource();
        var registry = fixture.Registry(new(point =>
        {
            if (point == InstallCommitPoint.AfterRegistryCommit) { queue.Dispose(); cancellation.Cancel(); }
        }));
        var coordinator = fixture.Coordinator(queue, registry); var details = fixture.Details();
        string id = coordinator.Queue(details, details.Versions[0]).Id; await Wait(queue, id, DownloadState.AwaitingInstall);
        var installed = await coordinator.InstallAsync(id, false, cancellation.Token);
        Check(installed.Package.Manifest.AppId == "fixture.pipeline" && fixture.Registry().Find(installed.AppId) is not null, "shutdown lost committed result");
    }

    private static async Task ReconcileCommitted()
    {
        using Fixture fixture = new(); string id;
        await using (var queue = fixture.Queue())
        {
            var registry = fixture.Registry(); var coordinator = fixture.Coordinator(queue, registry); var details = fixture.Details();
            id = coordinator.Queue(details, details.Versions[0]).Id; await Wait(queue, id, DownloadState.AwaitingInstall);
            await coordinator.InstallAsync(id, false, default);
            Check(queue.MarkInstalling(id), "fixture could not represent interrupted receipt");
        }
        await using (var reopened = fixture.Queue())
        {
            Check(reopened.Snapshot().Single().State == DownloadState.AwaitingInstall, "fixture must start with ambiguous receipt");
            _ = fixture.Coordinator(reopened, fixture.Registry());
            Check(reopened.Snapshot().Single().State == DownloadState.Completed, "committed registry not reconciled on startup");
            Check(reopened.MarkInstalling(id), "fixture could not arm negative recovery");
        }
        var package = fixture.Registry().Find("fixture.pipeline")!.Package; File.WriteAllText(Path.Combine(package.DirectoryPath, "index.html"), "corrupt fixture");
        await using (var reopened = fixture.Queue())
        {
            _ = fixture.Coordinator(reopened, fixture.Registry());
            Check(reopened.Snapshot().Single().State != DownloadState.Completed, "corrupt content falsely reconciled as success");
        }
    }

    private static async Task<DownloadSnapshot> Wait(DownloadService queue, string id, DownloadState state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            var current = queue.Snapshot().Single(task => task.Id == id);
            if (current.State == state) return current;
            if (current.State == DownloadState.Failed) throw new InvalidOperationException("unexpected download failure " + current.ErrorCode);
            await Task.Delay(10, timeout.Token);
        }
    }
    private static void Run(Func<Task> work) => work().GetAwaiter().GetResult();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Reject(Action action)
    { try { action(); } catch (Exception e) when (e is CatalogException or PackageException or ArgumentException) { return; } throw new InvalidOperationException("unsafe queue input accepted"); }
    private static async Task RejectAsync(Func<Task> action, string? code = null)
    {
        try { await action(); }
        catch (Exception e) when (e is CatalogException or PackageException)
        {
            if (code is not null) Check((e is CatalogException c ? c.Code : ((PackageException)e).Code) == code, "wrong error: " + e.Message);
            return;
        }
        throw new InvalidOperationException("unsafe installation accepted");
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "AutumnOS-store-pipeline-" + Guid.NewGuid().ToString("N"));
        internal byte[] Payload { get; private set; } = [];
        private string version = "1.0.0";
        private readonly GitHubTransport transport;
        internal Fixture()
        {
            Directory.CreateDirectory(Root); SetVersion(version);
            transport = new(() => new(), new Handler(() => Payload));
        }
        internal void SetVersion(string next)
        {
            version = next;
            using MemoryStream memory = new();
            using (ZipArchive archive = new(memory, ZipArchiveMode.Create, true))
            {
                var manifest = new { schemaVersion = 1, appId = "fixture.pipeline", name = "隔离安装链", version, runtime = "web", entry = "index.html", permissions = new[] { "saves" } };
                foreach (var item in new[] { ("manifest.json", JsonSerializer.Serialize(manifest)), ("index.html", "<!doctype html><title>fixture</title>") })
                {
                    var entry = archive.CreateEntry(item.Item1); entry.LastWriteTime = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                    using StreamWriter writer = new(entry.Open(), new UTF8Encoding(false)); writer.Write(item.Item2);
                }
            }
            Payload = memory.ToArray();
        }
        internal CatalogDetails Details()
        {
            string hash = Convert.ToHexStringLower(SHA256.HashData(Payload));
            var repository = new CatalogRepository(73, "fixture", "pipeline", "controlled metadata", ["autumnos-app", "autumn-app-sq"], CatalogCategory.Sq);
            var release = new ReleaseManifest(1, "fixture.pipeline", version, "stable", "web", "0.3.0", "0.3.0", "index.html", "fixture.autumn", Payload.Length, hash, ["saves"], 1);
            var item = new CatalogVersion(version == "1.0.0" ? 81 : 82, "v" + version, DateTimeOffset.UtcNow, "controlled release", false, release,
                new(version == "1.0.0" ? 91 : 92, release.Asset, Payload.Length, new("https://github.com/fixture/pipeline/releases/download/v" + version + "/fixture.autumn"), "sha256:" + hash), true, null);
            return new(repository, new(1, release.AppId, "隔离安装链", "not a live GitHub result", "sq", new("fixture"), [], true), [item], [], DateTimeOffset.UtcNow, new string('a', 40));
        }
        internal DownloadService Queue() => new(Root, transport, () => new());
        internal ApplicationInstallService Registry(InstallFaultHooks? faults = null) => new(Path.Combine(Root, "Apps"),
            prepareSaveChange: new InstalledSaveGuard(Path.Combine(Root, "Saves"), Path.Combine(Root, "Backups")).PrepareChange, testHooks: faults);
        internal StoreInstallCoordinator Coordinator(DownloadService queue, ApplicationInstallService registry) => new(Root, queue, registry);
        internal string SaveSentinel()
        {
            string path = Path.Combine(Root, "Saves", "unrelated.app", "guest", "game.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "preserved user data fixture"); return path;
        }
        public void Dispose()
        {
            transport.Dispose(); string parent = Path.GetFullPath(Path.GetTempPath()) + (Path.EndsInDirectorySeparator(Path.GetTempPath()) ? "" : Path.DirectorySeparatorChar);
            string path = Path.GetFullPath(Root);
            if (!path.StartsWith(parent, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("AutumnOS-store-pipeline-", StringComparison.Ordinal)) throw new InvalidOperationException("unsafe test cleanup");
            Directory.Delete(path, true);
        }
    }
    private sealed class Handler(Func<byte[]> bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes()), RequestMessage = request };
            response.Headers.ETag = new("\"controlled-package\""); response.Content.Headers.ContentType = new("application/octet-stream");
            return Task.FromResult(response);
        }
    }
}
