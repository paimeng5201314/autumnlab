using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using AutumnOS.Store;

namespace AutumnOS.Tests;

/// <summary>Controlled HTTP responses and actual temporary file bytes, never a claim of a public GitHub release.</summary>
public static class StoreDownloadTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("T04 transport: private, loopback, link-local and IPv4-mapped private DNS addresses rejected", () =>
        {
            foreach (string address in new[] { "127.0.0.1", "10.0.1.2", "172.16.1.2", "192.168.5.6", "169.254.169.254", "100.64.0.1", "::1", "::", "fc00::1", "fe80::1", "::ffff:192.168.1.1" })
                Check(!GitHubTransport.IsPublicAddress(IPAddress.Parse(address)), "Non-public DNS address accepted: " + address);
            Check(GitHubTransport.IsPublicAddress(IPAddress.Parse("140.82.112.6")), "Public GitHub address rejected.");
        });
        yield return ("T04 download: settings persist separately; invalid templates never replace saved settings", () =>
        {
            using var fixture = new Fixture();
            var store = new StoreNetworkSettingsService(fixture.Root);
            var settings = new StoreNetworkSettings(false, "", "https://accelerator.example/get?url={url}", true, 3, 65_536);
            store.Save(settings); Check(store.Load() == settings, "Network settings did not roundtrip.");
            Expect<ArgumentException>(() => store.Save(settings with { AssetUrlTemplate = "http://unsafe.example/{url}" }));
            Check(store.Load() == settings, "Invalid save replaced valid settings.");
        });
        yield return ("T04 download: templates reject credentials, loopback, missing/duplicate placeholders and nested raw URL", () =>
        {
            foreach (string bad in new[] { "http://a.example/{url}", "https://u:p@a.example/{url}", "https://127.0.0.1/{url}",
                "https://localhost/{url}", "https://proxy.local/{url}", "https://a.example/no-placeholder", "https://{url}.example/",
                "https://a.example/{url}/{url}", "https://a.example/?token=secret&u={url}", "https://a.example/?u={url}&opaque=secret", "https://a.example/#{url}" })
                Expect<ArgumentException>(() => GitHubTransport.ValidateTemplate(bad));
            var nested = GitHubTransport.ApplyTemplate("https://a.example/?url={url}", new("https://api.github.com/search/repositories?q=a%26b"));
            Check(nested.Query.Contains("https%3A%2F%2F", StringComparison.OrdinalIgnoreCase) && !nested.Query.Contains("&b"), "Nested URL was not encoded as one value.");
        });
        yield return ("T04 transport: Logto, local paths, EXE origin and non-GitHub source rejected before HTTP", () => Run(async () =>
        {
            int calls = 0;
            using var transport = new GitHubTransport(() => new(), new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
            foreach (var uri in new[] { "https://account.labchronicles.cn/oidc", "http://github.com/o/r/releases/download/v/a.autumn",
                "file:///C:/private.json", "https://127.0.0.1/private", "https://github.com.evil.example/o/r/releases/download/v/a.autumn" })
                await ExpectAsync<ArgumentException>(() => transport.SendAsync(new(uri), GitHubRequestKind.Asset, null, default));
            Check(calls == 0, "A forbidden URL reached HTTP.");
        }));
        yield return ("T04 transport: synthetic Authorization and Cookie cannot be forwarded", () => Run(async () =>
        {
            int calls = 0;
            using var transport = new GitHubTransport(() => new(), new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); }));
            foreach (string header in new[] { "Authorization", "Cookie", "Proxy-Authorization", "Host" })
                await ExpectAsync<ArgumentException>(() => transport.SendAsync(new("https://api.github.com/rate_limit"), GitHubRequestKind.Api,
                    new Dictionary<string, string> { [header] = "synthetic-test-credential-not-real" }, default));
            Check(calls == 0, "Secret-like header reached a transport handler.");
        }));
        yield return ("T04 transport: redirect only to fixed HTTPS asset hosts, no arbitrary host or downgrade", () => Run(async () =>
        {
            foreach (string target in new[] { "https://untrusted.example/a.autumn", "http://release-assets.githubusercontent.com/a.autumn", "https://127.0.0.1/a.autumn" })
            {
                int calls = 0;
                using var transport = new GitHubTransport(() => new(), new Handler((_, _) =>
                {
                    calls++; var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new(target); return Task.FromResult(response);
                }));
                await ExpectAsync<HttpRequestException>(() => transport.SendAsync(Source, GitHubRequestKind.Asset, null, default));
                Check(calls == 1, "Unsafe redirect target was requested.");
            }
        }));
        yield return ("T04 transport: acceleration failure explicitly falls back to original public source", () => Run(async () =>
        {
            var hosts = new List<string>();
            using var transport = new GitHubTransport(() => new(AssetUrlTemplate: "https://accelerator.example/?u={url}"), new Handler((request, _) =>
            {
                hosts.Add(request.RequestUri!.Host);
                Check(request.Headers.Authorization is null && !request.Headers.Contains("Cookie"), "Credentials appeared on transport.");
                return Task.FromResult(new HttpResponseMessage(hosts.Count == 1 ? HttpStatusCode.BadGateway : HttpStatusCode.OK));
            }));
            using var result = await transport.SendAsync(Source, GitHubRequestKind.Asset, null, default);
            Check(hosts.SequenceEqual(new[] { "accelerator.example", "github.com" }) && result.Headers.GetValues("X-AutumnOS-Route").Single() == "direct-fallback", "No explicit direct fallback.");
        }));
        yield return ("T04 transport: disabled fallback never issues a second request", () => Run(async () =>
        {
            int calls = 0;
            using var transport = new GitHubTransport(() => new(ApiUrlTemplate: "https://accelerator.example/?u={url}", AllowDirectFallback: false),
                new Handler((_, _) => { calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }));
            var test = await transport.TestConnectionAsync();
            Check(!test.Succeeded && test.StatusCode == 503 && calls == 1, "Disabled fallback issued unexpected request.");
        }));
        yield return ("T04 transport: caller cancellation never triggers direct fallback", () => Run(async () =>
        {
            int calls = 0;
            using var stop = new CancellationTokenSource();
            using var transport = new GitHubTransport(() => new(ApiUrlTemplate: "https://accelerator.example/?u={url}"),
                new Handler(async (_, token) => { calls++; stop.Cancel(); await Task.Delay(1000, token); return new HttpResponseMessage(HttpStatusCode.OK); }));
            var result = await transport.TestConnectionAsync(stop.Token);
            Check(!result.Succeeded && result.Code == "CANCELLED" && calls == 1, "Cancellation retried via fallback.");
        }));
        yield return ("T04 download: exact bytes/hash commit atomically; duplicate enqueue one task; install state separate", () => Run(async () =>
        {
            using var fixture = new Fixture(); var payload = Bytes(80_000);
            await using var queue = fixture.Queue((_, _, _) => Task.FromResult(Response(payload)));
            var request = Request(payload);
            string id = queue.Enqueue(request).Id;
            Check(queue.Enqueue(request).Id == id && queue.Snapshot().Count == 1, "Repeated action created another task.");
            var done = await WaitAsync(queue, id, DownloadState.AwaitingInstall);
            Check(done.BytesReceived == payload.Length && File.ReadAllBytes(done.LocalPath!).SequenceEqual(payload), "Committed bytes differ.");
            Check(!File.Exists(Path.ChangeExtension(done.LocalPath, ".part")), "Partial survived verified commit.");
            Check(queue.MarkInstalling(id) && queue.MarkInstallFailed(id, "PACKAGE_BUSY") && queue.Snapshot().Single().State == DownloadState.AwaitingInstall,
                "Installation failure discarded reusable download.");
            Check(queue.MarkInstalling(id) && queue.MarkInstalled(id) && queue.MarkInstalling(id), "Install/repair lifecycle unavailable.");
        }));
        yield return ("T04 download: MIME/sniffed HTML, bad digest and mismatched length never become installable", () => Run(async () =>
        {
            foreach (string scenario in new[] { "mime", "sniff", "hash", "length" })
            {
                using var fixture = new Fixture(); byte[] payload = scenario == "sniff" ? System.Text.Encoding.UTF8.GetBytes("<!doctype html><html>error</html>") : Bytes(4096);
                await using var queue = fixture.Queue((_, _, _) =>
                {
                    var response = Response(payload);
                    if (scenario == "mime") response.Content.Headers.ContentType = new("text/html");
                    if (scenario == "length") response.Content.Headers.ContentLength = payload.Length + 1;
                    return Task.FromResult(response);
                });
                var request = Request(payload); if (scenario == "hash") request = request with { Sha256 = new string('0', 64) };
                var result = await WaitAsync(queue, queue.Enqueue(request).Id, DownloadState.Failed);
                Check(result.LocalPath is null && Directory.GetFiles(Path.Combine(fixture.Root, "Downloads", "store-v1"), "*.autumn").Length == 0,
                    "An invalid payload became installable.");
            }
        }));
        yield return ("T04 download: pause then strong ETag/If-Range resumes same stable asset", () => Run(async () =>
        {
            using var fixture = new Fixture(); byte[] payload = Bytes(131_072); int ranged = 0;
            await using var queue = fixture.Queue((_, headers, _) =>
            {
                long offset = Offset(headers); if (offset > 0) { ranged++; Check(headers!["If-Range"] == "\"fixed-resource\"", "If-Range identity missing."); }
                return Task.FromResult(Response(payload, offset));
            }, new(BytesPerSecond: 262_144));
            string id = queue.Enqueue(Request(payload)).Id;
            await WaitBytesAsync(queue, id);
            Check(queue.Pause(id) && queue.Resume(id), "Pause/resume action rejected.");
            var result = await WaitAsync(queue, id, DownloadState.AwaitingInstall);
            Check(ranged >= 1 && File.ReadAllBytes(result.LocalPath!).SequenceEqual(payload), "Validated range was not resumed correctly.");
        }));
        yield return ("T04 download: process restart keeps paused task and resumes stable resource without a second task", () => Run(async () =>
        {
            using var fixture = new Fixture(); byte[] payload = Bytes(131_072); string id; int ranges = 0;
            Func<Uri, IReadOnlyDictionary<string, string>?, CancellationToken, Task<HttpResponseMessage>> send = (_, headers, _) =>
            { long offset = Offset(headers); if (offset > 0) ranges++; return Task.FromResult(Response(payload, offset)); };
            await using (var queue = fixture.Queue(send, new(BytesPerSecond: 262_144)))
            { id = queue.Enqueue(Request(payload)).Id; await WaitBytesAsync(queue, id); }
            await using (var reopened = fixture.Queue(send))
            {
                Check(reopened.Snapshot().Single().State == DownloadState.Paused && reopened.Resume(id), "Restart did not expose resumable paused history.");
                await WaitAsync(reopened, id, DownloadState.AwaitingInstall);
                Check(ranges > 0 && reopened.Snapshot().Count == 1, "Restart lost stable download identity.");
            }
        }));
        yield return ("T04 download: Range 200 or changed ETag/malformed Content-Range safely restart instead of appending", () => Run(async () =>
        {
            foreach (string scenario in new[] { "200", "etag", "range" })
            {
                using var fixture = new Fixture(); byte[] payload = Bytes(131_072); int ranged = 0, full = 0;
                await using var queue = fixture.Queue((_, headers, _) =>
                {
                    long offset = Offset(headers); if (offset == 0) full++; else ranged++;
                    var response = Response(payload, scenario == "200" ? 0 : offset);
                    if (offset > 0 && scenario == "etag") response.Headers.ETag = new("\"changed-resource\"");
                    if (offset > 0 && scenario == "range") response.Content.Headers.ContentRange = new(0, payload.Length - offset - 1, payload.Length);
                    return Task.FromResult(response);
                }, new(BytesPerSecond: 262_144));
                string id = queue.Enqueue(Request(payload)).Id; await WaitBytesAsync(queue, id); queue.Pause(id); queue.Resume(id);
                var result = await WaitAsync(queue, id, DownloadState.AwaitingInstall);
                Check(ranged >= 1 && File.ReadAllBytes(result.LocalPath!).SequenceEqual(payload) && (scenario == "200" || full >= 2), "Range mismatch concatenated incompatible content.");
            }
        }));
        yield return ("T04 download: no strong ETag means pause resumes by safe full download", () => Run(async () =>
        {
            using var fixture = new Fixture(); byte[] payload = Bytes(131_072); int calls = 0;
            await using var queue = fixture.Queue((_, headers, _) =>
            {
                calls++; Check(Offset(headers) == 0, "Weak ETag used for byte resume.");
                var response = Response(payload); response.Headers.ETag = new("\"weak\"", true); return Task.FromResult(response);
            }, new(BytesPerSecond: 262_144));
            string id = queue.Enqueue(Request(payload)).Id; await WaitBytesAsync(queue, id); queue.Pause(id); queue.Resume(id);
            await WaitAsync(queue, id, DownloadState.AwaitingInstall); Check(calls >= 2, "No restart occurred.");
        }));
        yield return ("T04 download: disk-space failure performs no HTTP and does not touch unrelated data", () => Run(async () =>
        {
            using var fixture = new Fixture(); int calls = 0; string sentinel = Path.Combine(fixture.Root, "save-sentinel.bin"); File.WriteAllBytes(sentinel, [1, 2, 3]);
            await using var queue = fixture.Queue((_, _, _) => { calls++; return Task.FromResult(Response(Bytes(4096))); }, freeSpace: _ => 0);
            var result = await WaitAsync(queue, queue.Enqueue(Request(Bytes(4096))).Id, DownloadState.Failed);
            Check(result.ErrorCode == "DOWNLOAD_DISK_FULL" && calls == 0 && File.ReadAllBytes(sentinel).SequenceEqual(new byte[] { 1, 2, 3 }), "Disk guard failed.");
        }));
        yield return ("T04 download: transient network failure retries at most three attempts then explicit retry succeeds", () => Run(async () =>
        {
            using var fixture = new Fixture(); byte[] payload = Bytes(4096); int calls = 0; bool recover = false;
            await using var queue = fixture.Queue((_, _, _) =>
            { calls++; if (!recover) throw new HttpRequestException("Synthetic disconnected response"); return Task.FromResult(Response(payload)); });
            string id = queue.Enqueue(Request(payload)).Id;
            var failed = await WaitAsync(queue, id, DownloadState.Failed);
            Check(calls == 3 && failed.ErrorCode == "DOWNLOAD_NETWORK_FAILED", "Unbounded or missing retries.");
            recover = true; Check(queue.Retry(id), "Explicit retry rejected."); await WaitAsync(queue, id, DownloadState.AwaitingInstall);
        }));
        yield return ("T04 download: long Retry-After does not trigger early automatic retry", () => Run(async () =>
        {
            using var fixture = new Fixture(); int calls = 0;
            await using var queue = fixture.Queue((_, _, _) =>
            { calls++; var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests); response.Headers.RetryAfter = new(TimeSpan.FromMinutes(1)); return Task.FromResult(response); });
            string id = queue.Enqueue(Request(Bytes(4096))).Id;
            var result = await WaitAsync(queue, id, DownloadState.Failed);
            Check(calls == 1 && result.RetryAfterUtc > DateTimeOffset.UtcNow && !queue.Retry(id), "Retry-After was ignored.");
        }));
        yield return ("T04 download: broken HTTP body retries from actual partial with strong identity", () => Run(async () =>
        {
            using var fixture = new Fixture(); byte[] payload = Bytes(65_536); int calls = 0, ranges = 0;
            await using var queue = fixture.Queue((_, headers, _) =>
            {
                calls++; long offset = Offset(headers); if (offset > 0) ranges++;
                var response = Response(payload, offset);
                if (calls == 1)
                {
                    response.Content = new StreamContent(new InterruptedBody(payload));
                    response.Content.Headers.ContentLength = payload.Length;
                }
                return Task.FromResult(response);
            });
            var result = await WaitAsync(queue, queue.Enqueue(Request(payload)).Id, DownloadState.AwaitingInstall);
            Check(calls == 2 && ranges == 1 && File.ReadAllBytes(result.LocalPath!).SequenceEqual(payload), "Interrupted HTTP body was not safely resumed.");
        }));
        yield return ("T04 download: queue concurrency and global byte limit are enforced", () => Run(async () =>
        {
            using var fixture = new Fixture(); byte[] payload = Bytes(32_768); int active = 0, maximum = 0;
            await using var queue = fixture.Queue(async (_, _, token) =>
            {
                int count = Interlocked.Increment(ref active); int old;
                do { old = maximum; } while (count > old && Interlocked.CompareExchange(ref maximum, count, old) != old);
                try { await Task.Delay(80, token); return Response(payload); } finally { Interlocked.Decrement(ref active); }
            }, new(MaximumConcurrentDownloads: 2, BytesPerSecond: 65_536));
            var timer = Stopwatch.StartNew();
            var ids = Enumerable.Range(1, 3).Select(n => queue.Enqueue(Request(payload) with { AppId = "test.download." + n, AssetId = n }).Id).ToArray();
            await Task.WhenAll(ids.Select(id => WaitAsync(queue, id, DownloadState.AwaitingInstall)));
            Check(maximum is >= 1 and <= 2 && timer.Elapsed >= TimeSpan.FromSeconds(1.3), "Concurrency or global bandwidth cap was bypassed.");
        }));
        yield return ("T04 download: cancel deletes only owned partial and cannot finish as installable", () => Run(async () =>
        {
            using var fixture = new Fixture(); byte[] payload = Bytes(131_072);
            var queue = fixture.Queue((_, _, _) => Task.FromResult(Response(payload)), new(BytesPerSecond: 262_144));
            string id = queue.Enqueue(Request(payload)).Id; await WaitBytesAsync(queue, id); Check(queue.Cancel(id), "Cancel failed.");
            await queue.DisposeAsync();
            Check(queue.Snapshot().Single().State == DownloadState.Cancelled && Directory.GetFiles(Path.Combine(fixture.Root, "Downloads", "store-v1"), "*.part").Length == 0,
                "Cancelled task leaked a partial or became complete.");
        }));
        yield return ("T04 download: persisted verified cache is rehashed and tampering is not accepted on restart", () => Run(async () =>
        {
            using var fixture = new Fixture(); byte[] payload = Bytes(4096); string path;
            await using (var queue = fixture.Queue((_, _, _) => Task.FromResult(Response(payload))))
            { path = (await WaitAsync(queue, queue.Enqueue(Request(payload)).Id, DownloadState.AwaitingInstall)).LocalPath!; }
            File.WriteAllBytes(path, Bytes(10));
            await using var reopened = fixture.Queue((_, _, _) => Task.FromResult(Response(payload)));
            Check(reopened.Snapshot().Single().State == DownloadState.Failed && reopened.Snapshot().Single().ErrorCode == "DOWNLOAD_CACHE_INVALID", "Tampered cache accepted.");
        }));
    }

    private static readonly Uri Source = new("https://github.com/fixture-owner/fixture-app/releases/download/v1.0.0/application.autumn");
    private static DownloadRequest Request(byte[] bytes) => new("test.store.download", "1.0.0", Source, bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), 101, 202, 303);
    private static byte[] Bytes(int length) { var bytes = new byte[length]; new Random(490).NextBytes(bytes); if (length > 4) { bytes[0] = 80; bytes[1] = 75; bytes[2] = 3; bytes[3] = 4; } return bytes; }
    private static long Offset(IReadOnlyDictionary<string, string>? headers) => headers?.TryGetValue("Range", out string? value) == true ? long.Parse(value[6..^1]) : 0;
    private static HttpResponseMessage Response(byte[] payload, long offset = 0)
    {
        var response = new HttpResponseMessage(offset > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
        { Content = new ByteArrayContent(payload[(int)offset..]) };
        response.Headers.ETag = new("\"fixed-resource\"");
        response.Content.Headers.ContentType = new("application/octet-stream");
        if (offset > 0) response.Content.Headers.ContentRange = new(offset, payload.Length - 1, payload.Length);
        return response;
    }
    private static async Task<DownloadSnapshot> WaitAsync(DownloadService queue, string id, DownloadState state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        while (true)
        {
            var current = queue.Snapshot().Single(r => r.Id == id);
            if (current.State == state) return current;
            if (current.State == DownloadState.Failed) throw new InvalidOperationException("Unexpected download failure: " + current.ErrorCode);
            await Task.Delay(10, timeout.Token);
        }
    }
    private static async Task WaitBytesAsync(DownloadService queue, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (queue.Snapshot().Single(r => r.Id == id).BytesReceived < 16_384) await Task.Delay(5, timeout.Token);
    }
    private static void Run(Func<Task> action) => action().GetAwaiter().GetResult();
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Expect<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => handler(request, token); }
    private sealed class InterruptedBody(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            if (Position >= 32_768) throw new IOException("Synthetic interrupted HTTP body");
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 16_384)], token);
        }
    }
    private sealed class ControlledTransport(Func<Uri, IReadOnlyDictionary<string, string>?, CancellationToken, Task<HttpResponseMessage>> handler) : IGitHubTransport
    { public Task<HttpResponseMessage> SendAsync(Uri url, GitHubRequestKind kind, IReadOnlyDictionary<string, string>? headers, CancellationToken token) => handler(url, headers, token); }
    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "AutumnOS-store-download-tests", Guid.NewGuid().ToString("N"));
        internal Fixture() => Directory.CreateDirectory(Root);
        internal DownloadService Queue(Func<Uri, IReadOnlyDictionary<string, string>?, CancellationToken, Task<HttpResponseMessage>> handler,
            StoreNetworkSettings? settings = null, Func<string, long>? freeSpace = null) => new(Root, new ControlledTransport(handler), () => settings ?? new(), freeSpace);
        public void Dispose()
        {
            string allowed = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "AutumnOS-store-download-tests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Test cleanup escaped its temporary root.");
            Directory.Delete(Root, true);
        }
    }
}
