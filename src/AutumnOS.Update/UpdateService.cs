using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using AutumnOS.Store;

namespace AutumnOS.Update;

public sealed class UpdateService : IDisposable
{
    public const string Repository = "paimeng5201314/autumnlab";
    public const string UpdaterVersion = "1.0.0";
    private readonly object gate = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly string dataRoot, statePath, currentBuildId;
    private readonly CatalogResponseCache cache;
    private readonly IDownloadService downloads;
    private readonly UpdateTrustStore trust;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<string, IDisposable>? enterCriticalOperation;
    private PersistentState persistent;
    private UpdateSnapshot snapshot;
    private IDisposable? ownedTransport, ownedDownloads;
    private int generation;
    public event EventHandler? Changed;
    public UpdateSnapshot Snapshot { get { lock (gate) return snapshot; } }

    public static UpdateService CreateDefault(string dataRoot, string currentVersion, string currentBuildId, Func<StoreNetworkSettings> settings, Func<string, IDisposable>? enterCriticalOperation = null)
    {
        IGitHubTransport transport = UpdateTestFeed.TryCreateEmbedded() ?? (IGitHubTransport)new GitHubTransport(settings);
        var downloads = new DownloadService(dataRoot, transport, settings, hostUpdate: true);
        return new(dataRoot, currentVersion, currentBuildId, transport, downloads, enterCriticalOperation: enterCriticalOperation)
        { ownedTransport = transport as IDisposable, ownedDownloads = downloads };
    }
    public UpdateService(string dataRoot, string currentVersion, string currentBuildId, IGitHubTransport transport,
        IDownloadService downloads, UpdateTrustStore? trust = null, Func<DateTimeOffset>? clock = null, Func<string, IDisposable>? enterCriticalOperation = null)
    {
        _ = SemanticVersion.Parse(currentVersion);
        this.dataRoot = Path.GetFullPath(dataRoot); this.currentBuildId = currentBuildId;
        this.downloads = downloads; this.trust = trust ?? UpdateTrustStore.FromEmbedded(); this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.enterCriticalOperation = enterCriticalOperation;
        statePath = Path.Combine(this.dataRoot, "Updates", "state.json");
        cache = new CatalogResponseCache(Path.Combine(this.dataRoot, "Updates", "metadata-cache"), transport);
        persistent = ReadState();
        snapshot = new(UpdateState.Idle, persistent.Preview ? "meta" : "plus", currentVersion, null, 0, 0, null, [], null, this.trust.IsConfigured, UpdateTrustStore.IsTestBuild);
    }
    public void SetPreviewEnabled(bool enabled)
    {
        lock (gate)
        {
            if (persistent.Preview == enabled) return;
            SaveState(persistent with { Preview = enabled }); generation++;
            snapshot = snapshot with { Channel = enabled ? "meta" : "plus", State = UpdateState.Idle, Candidate = null, Staged = null, BytesReceived = 0, TotalBytes = 0, ErrorCode = null };
        }
        Notify();
    }
    public async Task CheckAsync(bool previewEnabled, CancellationToken cancellationToken = default)
    {
        SetPreviewEnabled(previewEnabled);
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        int captured; string channel; lock (gate) { captured = generation; channel = snapshot.Channel; }
        try
        {
            Publish(UpdateState.Checking, null, []);
            lock (gate) snapshot = snapshot with { Candidate = null, Staged = null, BytesReceived = 0, TotalBytes = 0 };
            CheckClock(); List<UpdateCandidate> candidates = []; List<string> warnings = [];
            if (!trust.IsConfigured) warnings.Add("UPDATE_PRODUCTION_TRUST_NOT_CONFIGURED");
            bool ended = false;
            for (int page = 1; page <= 20; page++)
            {
                var response = await cache.GetAsync(new($"https://api.github.com/repos/{Repository}/releases?per_page=100&page={page}"), GitHubRequestKind.Api, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                if (response.WarningCode is { } warning) warnings.Add(warning);
                using JsonDocument json = JsonDocument.Parse(response.Bytes);
                if (json.RootElement.ValueKind != JsonValueKind.Array || json.RootElement.GetArrayLength() > 100) throw new UpdateException("UPDATE_RELEASE_LIST_INVALID");
                foreach (var release in json.RootElement.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        if (release.GetProperty("draft").GetBoolean()) continue;
                        string tag = release.GetProperty("tag_name").GetString() ?? "";
                        if (!tag.StartsWith(channel + "-v", StringComparison.Ordinal)) continue;
                        string version = tag[(channel.Length + 2)..];
                        if (!SemanticVersion.TryParse(version, out var semantic) || semantic!.IsPrerelease != (channel == "meta") ||
                            release.GetProperty("prerelease").GetBoolean() != semantic.IsPrerelease || semantic.CompareTo(SemanticVersion.Parse(snapshot.CurrentVersion)) <= 0) continue;
                        var assets = release.GetProperty("assets").EnumerateArray().ToArray();
                        var metadata = Asset(assets, "autumn.update.json", tag);
                        var signature = Asset(assets, "autumn.update.sig", tag);
                        var manifestResponse = await cache.GetAsync(metadata.Url, GitHubRequestKind.Asset, UpdateManifestCodec.MaximumManifestBytes, cancellationToken).ConfigureAwait(false);
                        var signatureResponse = await cache.GetAsync(signature.Url, GitHubRequestKind.Asset, 16 * 1024, cancellationToken).ConfigureAwait(false);
                        if (metadata.Bytes != manifestResponse.Bytes.Length || signature.Bytes != signatureResponse.Bytes.Length) throw new UpdateException("UPDATE_ASSET_SIZE_MISMATCH");
                        var manifest = UpdateSignature.Verify(manifestResponse.Bytes, signatureResponse.Bytes, trust, clock(), persistent.HighestSequence);
                        CheckAccepted(manifest, manifestResponse.Bytes);
                        if (manifest.Channel != channel || manifest.Version != version || manifest.Payload.ReleaseId != release.GetProperty("id").GetInt64()) throw new UpdateException("UPDATE_RELEASE_MANIFEST_MISMATCH");
                        if (SemanticVersion.Parse(UpdaterVersion).CompareTo(SemanticVersion.Parse(manifest.MinimumUpdaterVersion)) < 0) throw new UpdateException("UPDATE_UPDATER_TOO_OLD");
                        var payload = Asset(assets, manifest.Payload.Asset, tag);
                        if (payload.Id != manifest.Payload.AssetId || payload.Bytes != manifest.Payload.Bytes) throw new UpdateException("UPDATE_ASSET_IDENTITY_MISMATCH");
                        if (manifest.BuildId == currentBuildId || persistent.FailedBuildIds.Contains(manifest.BuildId)) throw new UpdateException("UPDATE_CANDIDATE_PREVIOUSLY_FAILED_OR_CURRENT");
                        candidates.Add(new(manifest.Payload.ReleaseId, tag, release.TryGetProperty("body", out var body) ? (body.GetString() ?? "")[..Math.Min(10000, body.GetString()?.Length ?? 0)] : "",
                            manifest, payload.Url, payload.Id, manifestResponse.Bytes, signatureResponse.Bytes));
                    }
                    catch (UpdateException ex) { warnings.Add(ex.Code); }
                    catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException) { warnings.Add("UPDATE_RELEASE_INVALID"); }
                }
                if (json.RootElement.GetArrayLength() < 100) { ended = true; break; }
            }
            if (!ended) throw new UpdateException("UPDATE_RELEASE_LIST_INCOMPLETE");
            UpdateCandidate? selected = candidates.OrderByDescending(c => SemanticVersion.Parse(c.Manifest.Version)).FirstOrDefault();
            if (selected is null) warnings.Add("UPDATE_NO_COMPATIBLE_SIGNED_CANDIDATE");
            lock (gate)
            {
                if (captured != generation) throw new UpdateException("UPDATE_CHANNEL_CHANGED");
                if (selected is not null)
                {
                    SaveState(persistent with { HighestSequence = selected.Manifest.Sequence, HighestDigest = Digest(selected.ManifestBytes), LastTrustedUtc = clock() });
                }
                snapshot = snapshot with { State = !trust.IsConfigured ? UpdateState.Error : selected is null ? UpdateState.NoCompatibleUpdate : UpdateState.Available, Candidate = selected, Staged = null, BytesReceived = 0,
                    TotalBytes = selected?.Manifest.Payload.Bytes ?? 0, ErrorCode = trust.IsConfigured ? null : "UPDATE_PRODUCTION_TRUST_NOT_CONFIGURED", Warnings = warnings.Distinct().Take(50).ToArray() };
            }
            Notify();
        }
        catch (OperationCanceledException) { Publish(UpdateState.Idle, "UPDATE_CANCELLED"); throw; }
        catch (Exception ex) when (ex is UpdateException or CatalogException or IOException or UnauthorizedAccessException or JsonException)
        { Publish(UpdateState.Error, Error(ex)); }
        finally { operation.Release(); }
    }

    public async Task DownloadAndStageAsync(CancellationToken cancellationToken = default)
    {
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        string? downloadId = null;
        try
        {
            UpdateCandidate candidate; int captured;
            lock (gate) { candidate = snapshot.Candidate ?? throw new UpdateException("UPDATE_CANDIDATE_MISSING"); captured = generation; }
            CheckCandidate(candidate); Publish(UpdateState.Downloading, null);
            var manifest = candidate.Manifest;
            var task = downloads.Enqueue(new("cn.labchronicles.autumnos", manifest.Version, candidate.PayloadUrl, manifest.Payload.Bytes, manifest.Payload.Sha256,
                1, candidate.ReleaseId, candidate.AssetId)); downloadId = task.Id;
            if (task.State == DownloadState.Paused) downloads.Resume(task.Id);
            if (task.State is DownloadState.Failed or DownloadState.Cancelled) downloads.Retry(task.Id);
            DownloadSnapshot finished;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                lock (gate) { if (captured != generation) throw new UpdateException("UPDATE_CHANNEL_CHANGED"); }
                finished = downloads.Snapshot().Single(d => d.Id == task.Id);
                lock (gate) snapshot = snapshot with { BytesReceived = finished.BytesReceived, TotalBytes = finished.TotalBytes };
                Notify();
                if (finished.State is DownloadState.AwaitingInstall or DownloadState.Completed) break;
                if (finished.State is DownloadState.Failed or DownloadState.Cancelled or DownloadState.Paused) throw new UpdateException(finished.ErrorCode ?? "UPDATE_DOWNLOAD_STOPPED");
                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
            Publish(UpdateState.Verifying, null); CheckCandidate(candidate);
            string directory = Path.Combine(dataRoot, "Updates", "staging", Guid.NewGuid().ToString("N"));
            UpdatePaths.EnsureNoReparsePoints(directory); Directory.CreateDirectory(directory);
            string manifestPath = Path.Combine(directory, "autumn.update.json"), signaturePath = Path.Combine(directory, "autumn.update.sig"), payloadPath = Path.Combine(directory, "payload.zip");
            await File.WriteAllBytesAsync(manifestPath, candidate.ManifestBytes, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(signaturePath, candidate.SignatureBytes, cancellationToken).ConfigureAwait(false);
            UpdatePaths.EnsureNoReparsePoints(finished.LocalPath!);
            await using (var input = new FileStream(finished.LocalPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous))
            await using (var output = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            { await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false); await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(true); }
            await UpdatePayload.VerifyAsync(payloadPath, manifest, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                if (captured != generation) throw new UpdateException("UPDATE_CHANNEL_CHANGED");
                snapshot = snapshot with { State = UpdateState.Staged, Staged = new(candidate, manifestPath, signaturePath, payloadPath), ErrorCode = null };
            }
            Notify();
        }
        catch (OperationCanceledException) { if (downloadId is not null) downloads.Pause(downloadId); Publish(UpdateState.Idle, "UPDATE_CANCELLED"); throw; }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        { if (downloadId is not null) downloads.Pause(downloadId); Publish(UpdateState.Error, Error(ex)); }
        finally { operation.Release(); }
    }
    public StagedUpdate ValidateStagedForCommit()
    {
        lock (gate)
        {
            var staged = snapshot.Staged ?? throw new UpdateException("UPDATE_NOT_STAGED"); CheckCandidate(staged.Candidate);
            UpdatePaths.EnsureNoReparsePoints(staged.ManifestPath); UpdatePaths.EnsureNoReparsePoints(staged.SignaturePath); UpdatePaths.EnsureNoReparsePoints(staged.PayloadPath);
            if (!File.ReadAllBytes(staged.ManifestPath).AsSpan().SequenceEqual(staged.Candidate.ManifestBytes) || !File.ReadAllBytes(staged.SignaturePath).AsSpan().SequenceEqual(staged.Candidate.SignatureBytes))
                throw new UpdateException("UPDATE_STAGED_METADATA_CHANGED");
            return staged;
        }
    }
    public void RecordFailedCandidate(string buildId)
    {
        if (!UpdateManifestCodec.Identifier(buildId)) throw new UpdateException("UPDATE_BUILD_ID_INVALID");
        lock (gate) { SaveState(persistent with { FailedBuildIds = persistent.FailedBuildIds.Append(buildId).Distinct().TakeLast(128).ToArray() }); }
    }
    private void CheckCandidate(UpdateCandidate candidate)
    {
        CheckClock(); var manifest = UpdateSignature.Verify(candidate.ManifestBytes, candidate.SignatureBytes, trust, clock(), persistent.HighestSequence); CheckAccepted(manifest, candidate.ManifestBytes);
        if (manifest.Channel != snapshot.Channel || candidate.Tag != manifest.Channel + "-v" + manifest.Version) throw new UpdateException("UPDATE_CHANNEL_CHANGED");
        if (SemanticVersion.Parse(manifest.Version).CompareTo(SemanticVersion.Parse(snapshot.CurrentVersion)) <= 0) throw new UpdateException("UPDATE_DOWNGRADE_REJECTED");
        if (persistent.FailedBuildIds.Contains(manifest.BuildId)) throw new UpdateException("UPDATE_CANDIDATE_PREVIOUSLY_FAILED_OR_CURRENT");
    }
    private void CheckClock() { if (persistent.LastTrustedUtc != DateTimeOffset.MinValue && clock() < persistent.LastTrustedUtc.AddMinutes(-5)) throw new UpdateException("UPDATE_CLOCK_ROLLBACK"); }
    private void CheckAccepted(UpdateManifest manifest, byte[] bytes)
    {
        if (manifest.Sequence == persistent.HighestSequence && persistent.HighestDigest is not null && Digest(bytes) != persistent.HighestDigest) throw new UpdateException("UPDATE_SEQUENCE_REUSED");
        // The independent updater owns this ledger; rollback must not reset its security state.
        string installRoot = Path.GetDirectoryName(dataRoot) ?? throw new UpdateException("UPDATE_DATA_ROOT_INVALID");
        string ledgerPath = Path.Combine(installRoot, ".autumnos-update", "security.json");
        UpdatePaths.EnsureNoReparsePoints(ledgerPath);
        if (!File.Exists(ledgerPath)) return;
        if (new FileInfo(ledgerPath).Length > 65536) throw new UpdateException("UPDATE_SECURITY_LEDGER_INVALID");
        var ledger = UpdateManifestCodec.ParseStrict<SecurityLedger>(File.ReadAllBytes(ledgerPath), 65536);
        if (ledger.HighestSequence < 0 || ledger.FailedBuilds is null || ledger.FailedBuilds.Count > 4096 || ledger.FailedBuilds.Any(b => !UpdateManifestCodec.Identifier(b)) ||
            ledger.ManifestSha256.Length != 0 && !UpdateManifestCodec.Hash(ledger.ManifestSha256)) throw new UpdateException("UPDATE_SECURITY_LEDGER_INVALID");
        if (manifest.Sequence < ledger.HighestSequence) throw new UpdateException("UPDATE_METADATA_REPLAY");
        if (manifest.Sequence == ledger.HighestSequence && ledger.ManifestSha256.Length != 0 && ledger.ManifestSha256 != Digest(bytes)) throw new UpdateException("UPDATE_SEQUENCE_REUSED");
        if (ledger.FailedBuilds.Contains(manifest.BuildId)) throw new UpdateException("UPDATE_CANDIDATE_PREVIOUSLY_FAILED_OR_CURRENT");
    }
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static (long Id, long Bytes, Uri Url) Asset(JsonElement[] assets, string name, string tag)
    {
        var matching = assets.Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (matching.Length != 1) throw new UpdateException("UPDATE_ASSET_MISSING_OR_AMBIGUOUS");
        var asset = matching[0]; string expected = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";
        if (asset.GetProperty("browser_download_url").GetString() != expected || asset.GetProperty("id").GetInt64() <= 0 || asset.GetProperty("size").GetInt64() <= 0)
            throw new UpdateException("UPDATE_ASSET_SOURCE_INVALID");
        return (asset.GetProperty("id").GetInt64(), asset.GetProperty("size").GetInt64(), new(expected));
    }
    private PersistentState ReadState()
    {
        UpdatePaths.EnsureNoReparsePoints(statePath);
        if (!File.Exists(statePath)) return new(1, false, 0, null, DateTimeOffset.MinValue, []);
        if (new FileInfo(statePath).Length > 65536) throw new UpdateException("UPDATE_STATE_INVALID");
        var saved = UpdateManifestCodec.ParseStrict<PersistentState>(File.ReadAllBytes(statePath), 65536);
        if (saved.SchemaVersion != 1 || saved.HighestSequence < 0 || saved.HighestSequence > 0 && !UpdateManifestCodec.Hash(saved.HighestDigest) ||
            saved.FailedBuildIds is null || saved.FailedBuildIds.Count > 128 || saved.FailedBuildIds.Any(id => !UpdateManifestCodec.Identifier(id))) throw new UpdateException("UPDATE_STATE_INVALID");
        return saved;
    }
    private void SaveState(PersistentState value)
    {
        using var lease = enterCriticalOperation?.Invoke("update-state-write");
        UpdatePaths.EnsureNoReparsePoints(statePath); Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
        string temp = statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(JsonSerializer.SerializeToUtf8Bytes(value, UpdateManifestCodec.JsonOptions)); file.Flush(true); }
        UpdatePaths.EnsureNoReparsePoints(statePath); File.Move(temp, statePath, true);
        persistent = value;
    }
    private void Publish(UpdateState state, string? error, IReadOnlyList<string>? warnings = null)
    { lock (gate) snapshot = snapshot with { State = state, ErrorCode = error, Warnings = warnings ?? snapshot.Warnings }; Notify(); }
    private static string Error(Exception ex) => ex switch { UpdateException u => u.Code, CatalogException c => c.Code, UnauthorizedAccessException => "UPDATE_ACCESS_DENIED", IOException => "UPDATE_IO_ERROR", _ => "UPDATE_OPERATION_FAILED" };
    private void Notify() { if (Changed is not null) foreach (EventHandler handler in Changed.GetInvocationList()) try { handler(this, EventArgs.Empty); } catch (Exception) { } }
    public void Dispose() { ownedDownloads?.Dispose(); ownedTransport?.Dispose(); }
    private sealed record PersistentState(int SchemaVersion, bool Preview, long HighestSequence, string? HighestDigest, DateTimeOffset LastTrustedUtc, IReadOnlyList<string> FailedBuildIds);
    private sealed record SecurityLedger(long HighestSequence, IReadOnlyList<string> FailedBuilds, string ManifestSha256 = "");
}
