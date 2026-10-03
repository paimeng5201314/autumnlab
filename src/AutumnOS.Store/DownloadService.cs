using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutumnOS.Store;

/// <summary>Host-only downloads. Receiving bytes never registers an application or grants a runtime permission.</summary>
public sealed class DownloadService : IDownloadService, IDisposable, IAsyncDisposable
{
    public const long MaximumPackageBytes = 20L * 1024 * 1024;
    public const int MaximumTasks = 200;
    private const int MaximumPending = 32;
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly string _history;
    private readonly IGitHubTransport _transport;
    private readonly Func<StoreNetworkSettings> _settings;
    private readonly Func<string, long> _freeSpace;
    private readonly bool _hostUpdate;
    private readonly List<TaskRecord> _records = [];
    private readonly Dictionary<string, (CancellationTokenSource Cancellation, Task Task)> _running = [];
    private readonly BandwidthLimiter _limiter = new();
    private bool _disposed;
    public event EventHandler? Changed;

    public DownloadService(string dataRoot, IGitHubTransport transport, Func<StoreNetworkSettings> settings,
        Func<string, long>? availableSpaceForTest = null, bool hostUpdate = false)
    {
        _hostUpdate = hostUpdate;
        _directory = Path.Combine(Path.GetFullPath(dataRoot), "Downloads", hostUpdate ? "host-update-v1" : "store-v1");
        _history = Path.Combine(_directory, "history.json");
        _transport = transport;
        _settings = settings;
        _freeSpace = availableSpaceForTest ?? (path => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace);
        DownloadFiles.EnsureDirectory(_directory);
        LoadHistory();
    }

    public DownloadSnapshot Enqueue(DownloadRequest request)
    {
        ValidateRequestForProfile(request);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var prior = _records.LastOrDefault(r => r.Request == request && r.State != DownloadState.Cancelled);
            if (prior is not null) return View(prior);
            if (_records.Count(r => !Terminal(r.State)) >= MaximumPending) throw new InvalidOperationException("DOWNLOAD_QUEUE_FULL");
            while (_records.Count >= MaximumTasks)
            {
                var oldest = _records.FirstOrDefault(r => Terminal(r.State) && !_running.ContainsKey(r.Id));
                if (oldest is null) throw new InvalidOperationException("DOWNLOAD_HISTORY_FULL");
                // Prune only this service's bounded cache. Application installation/data is elsewhere.
                DeleteTemporary(oldest);
                DownloadFiles.DeleteOwned(FinalPath(oldest));
                _records.Remove(oldest);
            }
            var record = new TaskRecord { Id = Guid.NewGuid().ToString("N"), Request = request,
                State = DownloadState.Queued, CreatedUtc = DateTimeOffset.UtcNow, UpdatedUtc = DateTimeOffset.UtcNow };
            _records.Add(record);
            Persist(); Pump(); Notify();
            return View(record);
        }
    }

    public IReadOnlyList<DownloadSnapshot> Snapshot()
    {
        lock (_gate) return _records.OrderByDescending(r => r.CreatedUtc).Select(View).ToArray();
    }

    public bool Pause(string id) => SetUserState(id, DownloadState.Paused, DownloadState.Queued, DownloadState.Downloading, DownloadState.Verifying);
    public bool Resume(string id) => SetUserState(id, DownloadState.Queued, DownloadState.Paused);
    public bool Retry(string id) => SetUserState(id, DownloadState.Queued, DownloadState.Failed, DownloadState.Cancelled);
    public bool Cancel(string id) => SetUserState(id, DownloadState.Cancelled, DownloadState.Queued, DownloadState.Downloading,
        DownloadState.Paused, DownloadState.Verifying, DownloadState.AwaitingInstall, DownloadState.Failed);
    public bool MarkInstalling(string id) => SetUserState(id, DownloadState.Installing, DownloadState.AwaitingInstall, DownloadState.Completed);
    public bool MarkInstalled(string id) => SetUserState(id, DownloadState.Completed, DownloadState.Installing);
    public bool MarkInstallFailed(string id, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(errorCode) || errorCode.Length > 80 || errorCode.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '_')))
            throw new ArgumentException("DOWNLOAD_ERROR_CODE_INVALID");
        lock (_gate)
        {
            var record = _records.FirstOrDefault(r => r.Id == id);
            if (record?.State != DownloadState.Installing) return false;
            record.State = DownloadState.AwaitingInstall; record.ErrorCode = errorCode; record.UpdatedUtc = DateTimeOffset.UtcNow;
            Persist(); Notify(); return true;
        }
    }

    private bool SetUserState(string id, DownloadState state, params DownloadState[] allowed)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var record = _records.FirstOrDefault(r => r.Id == id);
            if (record is null || !allowed.Contains(record.State)) return false;
            if (state == DownloadState.Queued && record.RetryAfterUtc > DateTimeOffset.UtcNow) return false;
            if (state == DownloadState.Queued && record.ErrorCode == "DOWNLOAD_CACHE_INVALID")
                DownloadFiles.DeleteOwned(FinalPath(record));
            if (state == DownloadState.Installing && !File.Exists(FinalPath(record))) return false;
            record.State = state; record.ErrorCode = null; record.RetryAfterUtc = null; record.Speed = 0; record.UpdatedUtc = DateTimeOffset.UtcNow;
            if (state is DownloadState.Paused or DownloadState.Cancelled && _running.TryGetValue(id, out var run)) run.Cancellation.Cancel();
            if (state == DownloadState.Cancelled && !_running.ContainsKey(id)) DeleteTemporary(record);
            Persist(); Pump(); Notify(); return true;
        }
    }

    private void Pump()
    {
        if (_disposed) return;
        var settings = _settings(); StoreNetworkSettingsService.Validate(settings);
        foreach (var record in _records.Where(r => r.State == DownloadState.Queued && !_running.ContainsKey(r.Id)).ToArray())
        {
            if (_running.Count >= settings.MaximumConcurrentDownloads) break;
            var cancellation = new CancellationTokenSource();
            record.State = DownloadState.Downloading; record.ErrorCode = null;
            // Task.Run cannot enter the locked body until this registration is complete.
            Task task = Task.Run(() => RunAsync(record, cancellation.Token));
            _running.Add(record.Id, (cancellation, task));
        }
    }

    private async Task RunAsync(TaskRecord record, CancellationToken cancellationToken)
    {
        try
        {
            if (File.Exists(FinalPath(record)))
            {
                await VerifyAsync(record, FinalPath(record), cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    record.BytesReceived = record.Request.ExpectedBytes; record.State = DownloadState.AwaitingInstall;
                    record.ErrorCode = null; record.UpdatedUtc = DateTimeOffset.UtcNow; Persist();
                }
                return;
            }
            for (int retry = 0; ; retry++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (_gate) { record.Attempts++; record.UpdatedUtc = DateTimeOffset.UtcNow; Persist(); }
                try { await DownloadAsync(record, cancellationToken).ConfigureAwait(false); break; }
                catch (DownloadFailure e) when (e.Transient && retry < 2)
                {
                    var delay = e.RetryAfter ?? TimeSpan.FromMilliseconds(400 * Math.Pow(2, retry));
                    // Do not bypass a long Retry-After. Leave a visible failure for a later user retry.
                    if (delay > TimeSpan.FromSeconds(30)) throw;
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException) when (retry < 2)
                { await Task.Delay(TimeSpan.FromMilliseconds(400 * Math.Pow(2, retry)), cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && retry < 2)
                { await Task.Delay(TimeSpan.FromMilliseconds(400 * Math.Pow(2, retry)), cancellationToken).ConfigureAwait(false); }
            }
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                record.State = DownloadState.Verifying; record.Speed = 0; Persist(); Notify();
            }
            await VerifyAsync(record, PartialPath(record), cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DownloadFiles.Validate(FinalPath(record)); DownloadFiles.Validate(PartialPath(record));
                File.Move(PartialPath(record), FinalPath(record), overwrite: false);
                record.State = DownloadState.AwaitingInstall; record.ErrorCode = null; record.UpdatedUtc = DateTimeOffset.UtcNow; Persist();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException or UnauthorizedAccessException or DownloadFailure)
        {
            lock (_gate)
            {
                if (record.State is not (DownloadState.Paused or DownloadState.Cancelled or DownloadState.Queued))
                {
                    record.State = DownloadState.Failed;
                    record.ErrorCode = exception switch
                    {
                        DownloadFailure d => d.Code,
                        OperationCanceledException => "DOWNLOAD_TIMEOUT",
                        HttpRequestException => "DOWNLOAD_NETWORK_FAILED",
                        UnauthorizedAccessException => "DOWNLOAD_ACCESS_DENIED",
                        IOException io when (io.HResult & 0xffff) is 112 or 39 => "DOWNLOAD_DISK_FULL",
                        _ => "DOWNLOAD_IO_ERROR"
                    };
                    if (exception is DownloadFailure { RetryAfter: { } retryAfter } && retryAfter > TimeSpan.Zero)
                        record.RetryAfterUtc = DateTimeOffset.UtcNow + retryAfter;
                    record.Speed = 0;
                    if (exception is DownloadFailure { Transient: false })
                    {
                        DeleteTemporary(record);
                        DownloadFiles.DeleteOwned(FinalPath(record));
                    }
                }
                try { Persist(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { /* Preserve original history on disk; in-memory error remains visible. */ }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (_running.Remove(record.Id, out var running)) running.Cancellation.Dispose();
                if (record.State == DownloadState.Cancelled) DeleteTemporary(record);
                record.Speed = 0;
                Pump(); Notify();
            }
        }
    }

    private async Task DownloadAsync(TaskRecord record, CancellationToken cancellationToken)
    {
        string path = PartialPath(record); DownloadFiles.Validate(path);
        long offset = File.Exists(path) ? new FileInfo(path).Length : 0;
        if (offset > record.Request.ExpectedBytes || string.IsNullOrEmpty(record.ETag) || record.ETag.StartsWith("W/", StringComparison.Ordinal))
        { DeleteTemporary(record); offset = 0; }
        if (offset == record.Request.ExpectedBytes) return;
        if (_freeSpace(_directory) < record.Request.ExpectedBytes - offset + 1_048_576)
            throw new DownloadFailure("DOWNLOAD_DISK_FULL");
        for (int rangeAttempt = 0; rangeAttempt < 2; rangeAttempt++)
        {
            Dictionary<string, string>? headers = offset > 0 ? new() { ["Range"] = $"bytes={offset}-", ["If-Range"] = record.ETag! } : null;
            using var response = await _transport.SendAsync(record.Request.Url, GitHubRequestKind.Asset, headers, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.RequestTimeout || (int)response.StatusCode >= 500)
            {
                TimeSpan? after = response.Headers.RetryAfter?.Delta ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow);
                throw new DownloadFailure("DOWNLOAD_HTTP_" + (int)response.StatusCode, true, after > TimeSpan.Zero ? after : null);
            }
            if (offset > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
            { DeleteTemporary(record); offset = 0; continue; }
            if (!response.IsSuccessStatusCode) throw new DownloadFailure("DOWNLOAD_HTTP_" + (int)response.StatusCode);
            string media = response.Content.Headers.ContentType?.MediaType ?? "";
            if (media.Contains("html", StringComparison.OrdinalIgnoreCase)) throw new DownloadFailure("DOWNLOAD_HTML_RESPONSE");
            if (response.Content.Headers.ContentEncoding.Count != 0) throw new DownloadFailure("DOWNLOAD_ENCODING_NOT_ALLOWED");
            if (offset > 0 && response.StatusCode == HttpStatusCode.OK)
            { DeleteTemporary(record); offset = 0; }
            else if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (offset == 0 || range?.Unit != "bytes" || range.From != offset || range.To != record.Request.ExpectedBytes - 1 ||
                    range.Length != record.Request.ExpectedBytes || response.Headers.ETag?.ToString() != record.ETag || response.Headers.ETag?.IsWeak != false)
                {
                    if (offset > 0 && rangeAttempt == 0) { DeleteTemporary(record); offset = 0; continue; }
                    throw new DownloadFailure("DOWNLOAD_RANGE_INVALID");
                }
            }
            else if (response.StatusCode != HttpStatusCode.OK) throw new DownloadFailure("DOWNLOAD_RESPONSE_INVALID");
            if (response.Content.Headers.ContentLength is long contentLength && contentLength != record.Request.ExpectedBytes - offset)
                throw new DownloadFailure("DOWNLOAD_LENGTH_MISMATCH");
            lock (_gate)
            {
                record.ETag = response.Headers.ETag is { IsWeak: false } etag ? etag.ToString() : null;
                record.BytesReceived = offset;
                record.Route = response.Headers.TryGetValues("X-AutumnOS-Route", out var route) ? route.First() : "direct";
                Persist();
            }
            DownloadFiles.Validate(path);
            await using var output = new FileStream(path, offset == 0 ? FileMode.Create : FileMode.Append,
                FileAccess.Write, FileShare.Read, 16_384, FileOptions.Asynchronous);
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var buffer = new byte[16_384];
            var clock = Stopwatch.StartNew();
            long downloaded = 0, checkpoint = 0;
            while (true)
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                int count;
                try { count = await input.ReadAsync(buffer, readTimeout.Token).ConfigureAwait(false); }
                catch (IOException) { throw new DownloadFailure("DOWNLOAD_INTERRUPTED", true); }
                if (count == 0) break;
                if (offset + downloaded + count > record.Request.ExpectedBytes) throw new DownloadFailure("DOWNLOAD_LENGTH_MISMATCH");
                if (offset == 0 && downloaded == 0)
                {
                    string beginning = Encoding.UTF8.GetString(buffer.AsSpan(0, Math.Min(count, 256))).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
                    if (beginning.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || beginning.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                        throw new DownloadFailure("DOWNLOAD_HTML_RESPONSE");
                }
                await _limiter.WaitAsync(count, _settings().BytesPerSecond, cancellationToken).ConfigureAwait(false);
                await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
                downloaded += count;
                lock (_gate)
                {
                    record.BytesReceived = offset + downloaded;
                    record.Speed = downloaded / Math.Max(clock.Elapsed.TotalSeconds, .001);
                    record.UpdatedUtc = DateTimeOffset.UtcNow;
                    if (clock.ElapsedMilliseconds - checkpoint >= 250) { checkpoint = clock.ElapsedMilliseconds; Persist(); Notify(); }
                }
            }
            await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(true);
            if (offset + downloaded != record.Request.ExpectedBytes) throw new DownloadFailure("DOWNLOAD_INTERRUPTED", true);
            return;
        }
        throw new DownloadFailure("DOWNLOAD_RANGE_INVALID");
    }

    private static async Task VerifyAsync(TaskRecord record, string path, CancellationToken cancellationToken)
    {
        DownloadFiles.Validate(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65_536, FileOptions.Asynchronous);
        if (file.Length != record.Request.ExpectedBytes) throw new DownloadFailure("DOWNLOAD_LENGTH_MISMATCH");
        byte[] prefix = new byte[Math.Min(256L, file.Length)];
        await file.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        string beginning = Encoding.UTF8.GetString(prefix).TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        if (beginning.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || beginning.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
            throw new DownloadFailure("DOWNLOAD_HTML_RESPONSE");
        file.Position = 0;
        byte[] hash = await SHA256.HashDataAsync(file, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromHexString(record.Request.Sha256)))
            throw new DownloadFailure("DOWNLOAD_HASH_MISMATCH");
    }

    public static void ValidateRequest(DownloadRequest request)
        => ValidateRequestCore(request, false);

    private void ValidateRequestForProfile(DownloadRequest request) => ValidateRequestCore(request, _hostUpdate);

    private static void ValidateRequestCore(DownloadRequest request, bool hostUpdate)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.AppId) || request.AppId.Length > 128 || request.AppId.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(request.Version) || request.Version.Length > 80 || request.Version.Any(char.IsControl) ||
            request.ExpectedBytes <= 0 || request.ExpectedBytes > (hostUpdate ? 4L * 1024 * 1024 * 1024 : MaximumPackageBytes) || request.Sha256 is null || request.Sha256.Length != 64 || !request.Sha256.All(Uri.IsHexDigit) ||
            request.RepositoryId <= 0 || request.ReleaseId <= 0 || request.AssetId <= 0 ||
            request.Url is null || !request.Url.IsAbsoluteUri || !request.Url.AbsolutePath.EndsWith(hostUpdate ? ".zip" : ".autumn", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("DOWNLOAD_REQUEST_INVALID");
        GitHubTransport.ValidateSource(request.Url, GitHubRequestKind.Asset);
        if (hostUpdate && (request.AppId != "cn.labchronicles.autumnos" || !request.Url.AbsolutePath.StartsWith("/paimeng5201314/autumnlab/releases/download/", StringComparison.Ordinal)))
            throw new ArgumentException("DOWNLOAD_UPDATE_SOURCE_INVALID");
    }

    private void LoadHistory()
    {
        DownloadFiles.Validate(_history);
        if (!File.Exists(_history)) return;
        if (new FileInfo(_history).Length > 1_048_576) throw new InvalidDataException("DOWNLOAD_HISTORY_INVALID");
        var saved = JsonSerializer.Deserialize<History>(File.ReadAllText(_history)) ?? throw new InvalidDataException("DOWNLOAD_HISTORY_INVALID");
        if (saved.SchemaVersion != 1 || saved.Tasks is null || saved.Tasks.Count > MaximumTasks) throw new InvalidDataException("DOWNLOAD_HISTORY_INVALID");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var record in saved.Tasks)
        {
            if (record is null) throw new InvalidDataException("DOWNLOAD_HISTORY_INVALID");
            ValidateRequestForProfile(record.Request);
            if (!Guid.TryParseExact(record.Id, "N", out _) || !ids.Add(record.Id) || !Enum.IsDefined(record.State) ||
                record.ETag?.Length > 1024 || record.ETag is not null && !System.Net.Http.Headers.EntityTagHeaderValue.TryParse(record.ETag, out _) ||
                record.BytesReceived < 0 || record.BytesReceived > record.Request.ExpectedBytes)
                throw new InvalidDataException("DOWNLOAD_HISTORY_INVALID");
            if (record.State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Verifying) record.State = DownloadState.Paused;
            if (record.State == DownloadState.Installing) { record.State = DownloadState.AwaitingInstall; record.ErrorCode = "INSTALL_RECONCILIATION_REQUIRED"; }
            if (record.State == DownloadState.AwaitingInstall)
            {
                try { VerifyAsync(record, FinalPath(record), CancellationToken.None).GetAwaiter().GetResult(); }
                catch (Exception e) when (e is IOException or DownloadFailure)
                { record.State = DownloadState.Failed; record.ErrorCode = "DOWNLOAD_CACHE_INVALID"; }
            }
            record.Speed = 0; _records.Add(record);
        }
        Persist();
    }

    private DownloadSnapshot View(TaskRecord r) => new(r.Id, r.Request, r.State, r.BytesReceived, r.Request.ExpectedBytes, r.Speed,
        r.Speed > 0 ? TimeSpan.FromSeconds(Math.Max(0, r.Request.ExpectedBytes - r.BytesReceived) / r.Speed) : null,
        r.State is DownloadState.AwaitingInstall or DownloadState.Installing or DownloadState.Completed ? FinalPath(r) : null,
        r.ErrorCode, r.Attempts, r.CreatedUtc, r.UpdatedUtc, r.Route, r.RetryAfterUtc);
    private string PartialPath(TaskRecord r) => Path.Combine(_directory, r.Id + ".part");
    private string FinalPath(TaskRecord r) => Path.Combine(_directory, r.Id + (_hostUpdate ? ".zip" : ".autumn"));
    private static bool Terminal(DownloadState state) => state is DownloadState.Completed or DownloadState.Failed or DownloadState.Cancelled;
    private void DeleteTemporary(TaskRecord r) { DownloadFiles.DeleteOwned(PartialPath(r)); r.BytesReceived = 0; r.ETag = null; }
    private void Persist() => DownloadFiles.WriteJson(_history, new History(1, _records));
    private void Notify()
    {
        if (Changed is null) return;
        foreach (EventHandler handler in Changed.GetInvocationList())
            try { handler(this, EventArgs.Empty); } catch (Exception) { /* UI observers cannot alter the transfer transaction. */ }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var record in _records.Where(r => r.State is DownloadState.Queued or DownloadState.Downloading or DownloadState.Verifying))
            { record.State = DownloadState.Paused; record.Speed = 0; }
            foreach (var item in _running.Values) item.Cancellation.Cancel();
            Persist();
        }
    }
    public async ValueTask DisposeAsync()
    {
        Dispose();
        Task[] tasks; lock (_gate) tasks = _running.Values.Select(v => v.Task).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
    private sealed record History(int SchemaVersion, List<TaskRecord> Tasks);
    private sealed class TaskRecord
    {
        public string Id { get; set; } = "";
        public DownloadRequest Request { get; set; } = null!;
        public DownloadState State { get; set; }
        public long BytesReceived { get; set; }
        public string? ETag { get; set; }
        public string? ErrorCode { get; set; }
        public int Attempts { get; set; }
        public DateTimeOffset CreatedUtc { get; set; }
        public DateTimeOffset UpdatedUtc { get; set; }
        public string Route { get; set; } = "direct";
        public DateTimeOffset? RetryAfterUtc { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public double Speed { get; set; }
    }
    private sealed class DownloadFailure(string code, bool transient = false, TimeSpan? retryAfter = null) : Exception(code)
    {
        internal string Code { get; } = code;
        internal bool Transient { get; } = transient;
        internal TimeSpan? RetryAfter { get; } = retryAfter;
    }
    private sealed class BandwidthLimiter
    {
        private readonly object _gate = new();
        private long _nextTicks;
        internal async Task WaitAsync(int bytes, long limit, CancellationToken cancellationToken)
        {
            if (limit <= 0) { lock (_gate) _nextTicks = 0; return; }
            TimeSpan wait;
            lock (_gate)
            {
                long now = Stopwatch.GetTimestamp();
                _nextTicks = Math.Max(now, _nextTicks) + (long)((double)bytes * Stopwatch.Frequency / limit);
                wait = TimeSpan.FromSeconds((double)(_nextTicks - now) / Stopwatch.Frequency);
            }
            await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
        }
    }
}
