using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.Store;
using AutumnOS.Update;

namespace AutumnOS.Tests;

internal static class UpdateShutdownTests
{
    private static readonly TimeSpan TestDeadline = TimeSpan.FromSeconds(10);
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 0, 0, 0, TimeSpan.Zero);
    private static readonly Lazy<RSA> Key = new(() => RSA.Create(3072));

    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("update.shutdown_shared_check_and_queued_calls_drain_before_owned_transport", () => Run(async f =>
        {
            var service = f.Create(owned: true);
            f.Transport.BlockNext();
            Task check = service.CheckAsync(false);
            await Ready(f.Transport.Entered.Task);
            // Both calls are admitted while the check owns the operation semaphore.
            Task history = service.LoadHistoryAsync(UpdateSelectionMode.Upgrade);
            Task validation = service.ValidateStagedForCommitAsync();
            service.Dispose();
            Task shutdown = service.DisposeAsync().AsTask();
            Check(ReferenceEquals(shutdown, service.DisposeAsync().AsTask()), "Concurrent disposal must share one task.");
            Task[] concurrentShutdowns = new Task[16];
            Parallel.For(0, concurrentShutdowns.Length, i => { service.Dispose(); concurrentShutdowns[i] = service.DisposeAsync().AsTask(); });
            Check(concurrentShutdowns.All(task => ReferenceEquals(task, shutdown)));
            await Ready(f.Transport.CancellationObserved.Task);
            await Cancelled(history); await Cancelled(validation);
            Check(!shutdown.IsCompleted && !check.IsCompleted, "Cancellation cannot stand in for actual transport completion.");
            Check(f.Transport.DisposeCount == 0 && f.Downloads.AsyncDisposeCount == 0);
            await RejectClosed(() => service.CheckAsync(false));
            await RejectClosed(() => service.CheckOnStartupAsync());
            await RejectClosed(() => service.LoadHistoryAsync(UpdateSelectionMode.Upgrade));
            await RejectClosed(() => service.DownloadAndStageAsync());
            await RejectClosed(() => service.ValidateStagedForCommitAsync());
            RejectClosed(service.CancelConsent);
            RejectClosed(() => service.SetPreviewEnabled(true));
            f.Transport.Release.TrySetResult();
            await Cancelled(check); await Ready(shutdown);
            Check(f.Transport.DisposeCount == 1 && f.Downloads.AsyncDisposeCount == 1);
            Check(f.Downloads.SyncDisposeCount == 0 && f.Transport.RequestCount == 1);
            await Ready(service.DisposeAsync().AsTask());
            Check(f.Transport.DisposeCount == 1);
        }));

        yield return ("update.shutdown_history_waits_for_ignoring_cancel_transport_without_owning_dependencies", () => Run(async f =>
        {
            var service = f.Create(owned: false);
            f.Transport.BlockNext();
            Task history = service.LoadHistoryAsync(UpdateSelectionMode.Upgrade);
            await Ready(f.Transport.Entered.Task);
            Task shutdown = service.DisposeAsync().AsTask();
            await Ready(f.Transport.CancellationObserved.Task);
            Check(!shutdown.IsCompleted && !history.IsCompleted);
            f.Transport.Release.TrySetResult();
            await Cancelled(history); await Ready(shutdown);
            Check(f.Transport.DisposeCount == 0 && f.Downloads.AsyncDisposeCount == 0 && f.Downloads.SyncDisposeCount == 0,
                "The injected transport and download service remain owned by the caller.");
        }));

        yield return ("update.history_caller_cancellation_reaches_transport_and_shutdown_still_waits", () => Run(async f =>
        {
            var service = f.Create(owned: true);
            using CancellationTokenSource caller = new();
            f.Transport.BlockNext();
            Task history = service.LoadHistoryAsync(UpdateSelectionMode.Upgrade, cancellationToken: caller.Token);
            await Ready(f.Transport.Entered.Task);
            caller.Cancel(); await Ready(f.Transport.CancellationObserved.Task);
            Check(!history.IsCompleted, "An uncooperative transport is still active after caller cancellation.");
            Task shutdown = service.DisposeAsync().AsTask();
            Check(!shutdown.IsCompleted && f.Transport.DisposeCount == 0);
            f.Transport.Release.TrySetResult();
            await Cancelled(history); await Ready(shutdown);
            Check(f.Transport.DisposeCount == 1);
        }));

        yield return ("update.shutdown_download_revalidation_is_drained_before_owned_release", () => Run(async f =>
        {
            var service = f.Create(owned: true);
            service.ConfirmTarget(f.Candidate);
            f.Transport.BlockNext();
            Task download = service.DownloadAndStageAsync();
            await Ready(f.Transport.Entered.Task);
            Task shutdown = service.DisposeAsync().AsTask();
            await Ready(f.Transport.CancellationObserved.Task);
            Check(!shutdown.IsCompleted && !download.IsCompleted && f.Transport.DisposeCount == 0);
            f.Transport.Release.TrySetResult();
            await Cancelled(download); await Ready(shutdown);
            Check(f.Downloads.EnqueueCount == 0, "Cancelled revalidation cannot enqueue a payload.");
            Check(f.Transport.DisposeCount == 1 && f.Downloads.AsyncDisposeCount == 1);
        }));

        yield return ("update.shutdown_commit_revalidation_drains_and_preserves_staged_bytes", () => Run(async f =>
        {
            var service = f.Create(owned: true);
            service.ConfirmTarget(f.Candidate);
            await Ready(service.DownloadAndStageAsync());
            Check(service.Snapshot.State == UpdateState.Staged && f.Downloads.EnqueueCount == 1,
                "The positive control must really verify and stage the signed synthetic payload.");
            var staged = service.Snapshot.Staged!;
            string pending = Path.Combine(f.DataRoot, "Updates", "pending.json");
            string[] paths = [staged.ManifestPath, staged.SignaturePath, staged.PayloadPath, pending];
            var before = paths.ToDictionary(path => path, path => File.ReadAllBytes(path));
            f.Transport.BlockNext();
            Task validation = service.ValidateStagedForCommitAsync();
            await Ready(f.Transport.Entered.Task);
            Task shutdown = service.DisposeAsync().AsTask();
            await Ready(f.Transport.CancellationObserved.Task);
            Check(!shutdown.IsCompleted && !validation.IsCompleted && f.Transport.DisposeCount == 0);
            f.Transport.Release.TrySetResult();
            await Cancelled(validation); await Ready(shutdown);
            foreach (string path in paths) Check(File.ReadAllBytes(path).AsSpan().SequenceEqual(before[path]),
                "Shutdown must preserve the already committed staging transaction.");
        }));

        yield return ("update.shutdown_awaits_owned_download_workers_before_transport", () => Run(async f =>
        {
            var service = f.Create(owned: true);
            f.Downloads.BlockDispose = true;
            service.Dispose();
            Task shutdown = service.DisposeAsync().AsTask();
            await Ready(f.Downloads.DisposeEntered.Task);
            Check(!shutdown.IsCompleted && f.Transport.DisposeCount == 0 && f.Downloads.SyncDisposeCount == 0);
            await RejectClosed(() => service.LoadHistoryAsync(UpdateSelectionMode.Upgrade));
            f.Downloads.DisposeRelease.TrySetResult();
            await Ready(shutdown);
            Check(f.Downloads.WorkersExited && f.Transport.DisposeCount == 1);
            Check(!f.Transport.DisposedBeforeDownloadsExited);
        }));

        yield return ("update.shutdown_owned_download_drain_failure_is_not_success_or_early_transport_release", () => Run(async f =>
        {
            var service = f.Create(owned: true);
            f.Downloads.BlockDispose = true; f.Downloads.FailDispose = true;
            Task shutdown = service.DisposeAsync().AsTask();
            await Ready(f.Downloads.DisposeEntered.Task);
            Check(!shutdown.IsCompleted && f.Transport.DisposeCount == 0);
            f.Downloads.DisposeRelease.TrySetResult();
            await Failed(shutdown, "synthetic download drain failure");
            Check(f.Transport.DisposeCount == 0, "A failed worker drain cannot authorize transport release.");
            Check(ReferenceEquals(shutdown, service.DisposeAsync().AsTask()));
            await Failed(service.DisposeAsync().AsTask(), "synthetic download drain failure");
            Check(f.Downloads.AsyncDisposeCount == 1);
        }));

        yield return ("update.shutdown_transport_dispose_failure_is_retained_once", () => Run(async f =>
        {
            var service = f.Create(owned: true); f.Transport.FailDispose = true;
            Task shutdown = service.DisposeAsync().AsTask();
            await Failed(shutdown, "synthetic transport disposal failure");
            await Failed(service.DisposeAsync().AsTask(), "synthetic transport disposal failure");
            Check(f.Downloads.AsyncDisposeCount == 1 && f.Transport.DisposeCount == 1);
        }));

        yield return ("update.shared_check_caller_cancel_does_not_cancel_other_waiter", () => Run(async f =>
        {
            var service = f.Create(owned: false);
            using CancellationTokenSource caller = new();
            f.Transport.BlockNext();
            Task abandoned = service.CheckAsync(false, caller.Token);
            await Ready(f.Transport.Entered.Task);
            Task other = service.CheckAsync(false);
            caller.Cancel(); await Cancelled(abandoned);
            Check(!other.IsCompleted && !f.Transport.CancellationObserved.Task.IsCompleted && f.Transport.RequestCount == 1);
            f.Transport.Release.TrySetResult();
            await Ready(other);
            Check(service.Snapshot.Candidate?.Manifest.BuildId == f.Candidate.Manifest.BuildId && service.Snapshot.CheckComplete,
                "The second waiter must complete a real signed candidate check.");
            await Ready(service.DisposeAsync().AsTask());
        }));
    }

    private static async Task Ready(Task task) => await task.WaitAsync(TestDeadline);
    private static async Task Cancelled(Task task)
    {
        try { await Ready(task); } catch (OperationCanceledException) { return; }
        throw new InvalidOperationException("Expected cancellation after actual operation completion.");
    }
    private static async Task Failed(Task task, string expected)
    {
        try { await Ready(task); }
        catch (AggregateException ex) when (ex.Flatten().InnerExceptions.Any(error => error.Message == expected)) { return; }
        throw new InvalidOperationException("Expected retained shutdown failure: " + expected);
    }
    private static async Task RejectClosed(Func<Task> action)
    {
        try { await action(); } catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("Shutdown admitted a new operation.");
    }
    private static void RejectClosed(Action action)
    {
        try { action(); } catch (ObjectDisposedException) { return; }
        throw new InvalidOperationException("Shutdown admitted a new mutation.");
    }
    private static void Check(bool value, string message = "Update shutdown assertion failed.")
    { if (!value) throw new InvalidOperationException(message); }
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Run(Func<Fixture, Task> test)
    {
        using Fixture fixture = new();
        test(fixture).GetAwaiter().GetResult();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "autumnos-update-shutdown-" + Guid.NewGuid().ToString("N"));
        private UpdateService? service;
        internal string DataRoot => Path.Combine(root, "data");
        internal BlockingTransport Transport { get; }
        internal ControlledDownloads Downloads { get; }
        internal UpdateCandidate Candidate { get; }
        internal Fixture()
        {
            Directory.CreateDirectory(root);
            byte[] file = Encoding.UTF8.GetBytes("Synthetic shutdown fixture; not executable."), zip;
            using (MemoryStream memory = new())
            {
                using (ZipArchive archive = new(memory, ZipArchiveMode.Create, true))
                { using var stream = archive.CreateEntry("AutumnOS.Client.exe").Open(); stream.Write(file); }
                zip = memory.ToArray();
            }
            var manifest = new UpdateManifest(1, "plus", "1.1.0", "shutdown-test-build", "win-x64", "1.1.0", 1,
                Now.AddMinutes(-1), Now.AddDays(7), "shutdown-test", 1,
                new("payload.zip", zip.Length, Hash(zip), 1, 13), [new("AutumnOS.Client.exe", file.Length, Hash(file))], [], new(1, 1, true));
            byte[] bytes = UpdateManifestCodec.Serialize(manifest), signature = UpdateSignature.Create(bytes, Key.Value, "shutdown-test", 1);
            const string tag = "plus-v1.1.0";
            const string prefix = "https://github.com/paimeng5201314/autumnlab/releases/download/" + tag + "/";
            Candidate = new(1, tag, "Synthetic fixture", manifest, new(prefix + "payload.zip"), 13, bytes, signature);
            object Asset(string name, long id, byte[] content) => new { id, name, size = content.Length, browser_download_url = prefix + name };
            var release = new { id = 1L, tag_name = tag, name = "Fixture", body = "Synthetic", draft = false, prerelease = false,
                assets = new[] { Asset("autumn.update.json", 11, bytes), Asset("autumn.update.sig", 12, signature), Asset("payload.zip", 13, zip) } };
            string payload = Path.Combine(root, "synthetic-payload.zip"); File.WriteAllBytes(payload, zip);
            Downloads = new(payload);
            Transport = new(JsonSerializer.SerializeToUtf8Bytes(release), bytes, signature)
            { DownloadsExited = () => Downloads.WorkersExited };
        }
        internal UpdateService Create(bool owned)
        {
            Check(service is null);
            service = new(DataRoot, "1.0.0", "shutdown-current", Transport, Downloads,
                new(new(1, 1, "local-test", [new("shutdown-test", Key.Value.ExportSubjectPublicKeyInfoPem())])), () => Now);
            if (owned)
            {
                // Reproduce CreateDefault's ownership only in this synthetic fixture, without a public test factory.
                typeof(UpdateService).GetField("ownedTransport", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, Transport);
                typeof(UpdateService).GetField("ownedDownloads", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(service, Downloads);
            }
            return service;
        }
        public void Dispose()
        {
            Transport.Release.TrySetResult(); Downloads.DisposeRelease.TrySetResult();
            if (service is not null)
            {
                try { service.DisposeAsync().AsTask().WaitAsync(TestDeadline).GetAwaiter().GetResult(); }
                catch (AggregateException) when (Downloads.FailDispose || Transport.FailDispose) { }
            }
            string resolved = Path.GetFullPath(root), parent = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            Check(Path.GetDirectoryName(resolved) == parent && Path.GetFileName(resolved).StartsWith("autumnos-update-shutdown-", StringComparison.Ordinal));
            Directory.Delete(resolved, true);
        }
    }

    private sealed class BlockingTransport(byte[] release, byte[] manifest, byte[] signature) : IGitHubTransport, IDisposable
    {
        private int blockNext, requestCount, disposeCount;
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int RequestCount => Volatile.Read(ref requestCount);
        internal int DisposeCount => Volatile.Read(ref disposeCount);
        internal bool FailDispose, DisposedBeforeDownloadsExited;
        internal Func<bool> DownloadsExited { get; init; } = () => true;
        internal void BlockNext() => Interlocked.Exchange(ref blockNext, 1);
        public async Task<HttpResponseMessage> SendAsync(Uri url, GitHubRequestKind kind, IReadOnlyDictionary<string, string>? headers, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref requestCount);
            if (Interlocked.Exchange(ref blockNext, 0) == 1)
            {
                using var registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
                Entered.TrySetResult();
                // Deliberately ignores cancellation until explicitly released: cancellation alone is not completion.
                await Release.Task.ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            cancellationToken.ThrowIfCancellationRequested();
            byte[] bytes = kind == GitHubRequestKind.Api
                ? url.AbsolutePath.EndsWith("/latest", StringComparison.Ordinal) || url.AbsolutePath.EndsWith("/1", StringComparison.Ordinal) ? release : "[]"u8.ToArray()
                : url.AbsolutePath.EndsWith("/autumn.update.json", StringComparison.Ordinal) ? manifest : signature;
            return new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
        }
        public void Dispose()
        {
            Interlocked.Increment(ref disposeCount);
            DisposedBeforeDownloadsExited = !DownloadsExited();
            if (FailDispose) throw new InvalidOperationException("synthetic transport disposal failure");
        }
    }

    private sealed class ControlledDownloads(string payload) : IDownloadService, IDisposable, IAsyncDisposable
    {
        private DownloadSnapshot? item;
        private int enqueueCount, asyncDisposeCount, syncDisposeCount;
        internal int EnqueueCount => Volatile.Read(ref enqueueCount);
        internal int AsyncDisposeCount => Volatile.Read(ref asyncDisposeCount);
        internal int SyncDisposeCount => Volatile.Read(ref syncDisposeCount);
        internal bool BlockDispose, FailDispose, WorkersExited;
        internal TaskCompletionSource DisposeEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public event EventHandler? Changed { add { } remove { } }
        public DownloadSnapshot Enqueue(DownloadRequest request)
        {
            Interlocked.Increment(ref enqueueCount);
            return item = new("synthetic-download", request, DownloadState.AwaitingInstall, request.ExpectedBytes, request.ExpectedBytes,
                0, null, payload, null, 1, Now, Now);
        }
        public IReadOnlyList<DownloadSnapshot> Snapshot() => item is null ? [] : [item];
        public bool Pause(string id) => item?.Id == id;
        public bool Resume(string id) => item?.Id == id;
        public bool Cancel(string id) => item?.Id == id;
        public bool Retry(string id) => item?.Id == id;
        public bool MarkInstalling(string id) => item?.Id == id;
        public bool MarkInstalled(string id) => item?.Id == id;
        public bool MarkInstallFailed(string id, string errorCode) => item?.Id == id;
        public void Dispose() => Interlocked.Increment(ref syncDisposeCount);
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref asyncDisposeCount); DisposeEntered.TrySetResult();
            if (BlockDispose) await DisposeRelease.Task.ConfigureAwait(false);
            if (FailDispose) throw new InvalidOperationException("synthetic download drain failure");
            WorkersExited = true;
        }
    }
}
