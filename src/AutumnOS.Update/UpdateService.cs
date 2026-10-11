using System.Security.Cryptography;
using System.Text.Json;
using AutumnOS.Store;

namespace AutumnOS.Update;

/// <summary>Public main-program updates. Discovery never grants download or restart consent.</summary>
public sealed class UpdateService : IDisposable
{
    public const string Repository = "paimeng5201314/autumnlab";
    public const string UpdaterVersion = "1.1.0";
    private readonly object gate = new();
    private readonly SemaphoreSlim operation = new(1, 1);
    private readonly string dataRoot, statePath, stagedPath, currentBuildId, programDirectory;
    private readonly UpdateMetadataClient metadata;
    private readonly IDownloadService downloads;
    private readonly UpdateTrustStore trust;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<string, IDisposable>? enterCriticalOperation;
    private readonly Func<UpdateManifest, bool, string?>? compatibilityCheck;
    private readonly CancellationTokenSource lifetime = new();
    private PersistentState persistent;
    private UpdateSnapshot snapshot;
    private IDisposable? ownedTransport, ownedDownloads;
    private Task? checkTask, startupTask;
    private UpdateConsent? consent;
    private UpdateCandidate? consentCandidate;
    private UpdateRollbackAuthorization? rollbackAuthorization;
    private readonly HashSet<string> dismissed = new(StringComparer.Ordinal);
    private readonly Dictionary<long, UpdateReleaseChoice> history = [];
    private int historyPage = 1, generation;
    private bool historyHasMore = true, policyExists, downloadActive;
    public event EventHandler? Changed;
    public UpdateSnapshot Snapshot { get { lock (gate) return snapshot; } }

    public static UpdateService CreateDefault(string dataRoot, string currentVersion, string currentBuildId, Func<StoreNetworkSettings> settings, Func<string, IDisposable>? enterCriticalOperation = null, string? programDirectory = null, Func<UpdateManifest, bool, string?>? compatibilityCheck = null)
    {
        IGitHubTransport transport = UpdateTestFeed.TryCreateEmbedded() ?? (IGitHubTransport)new GitHubTransport(settings);
        var downloads = new DownloadService(dataRoot, transport, settings, hostUpdate: true);
        return new(dataRoot, currentVersion, currentBuildId, transport, downloads, enterCriticalOperation: enterCriticalOperation, programDirectory: programDirectory, compatibilityCheck: compatibilityCheck)
        { ownedTransport = transport as IDisposable, ownedDownloads = downloads };
    }
    public UpdateService(string dataRoot, string currentVersion, string currentBuildId, IGitHubTransport transport,
        IDownloadService downloads, UpdateTrustStore? trust = null, Func<DateTimeOffset>? clock = null, Func<string, IDisposable>? enterCriticalOperation = null, string? programDirectory = null, Func<UpdateManifest, bool, string?>? compatibilityCheck = null)
    {
        _ = SemanticVersion.Parse(currentVersion);
        this.dataRoot = Path.GetFullPath(dataRoot); this.currentBuildId = currentBuildId;
        this.downloads = downloads; this.trust = trust ?? UpdateTrustStore.FromEmbedded(); this.clock = clock ?? (() => DateTimeOffset.UtcNow);
        this.enterCriticalOperation = enterCriticalOperation; this.compatibilityCheck = compatibilityCheck;
        this.programDirectory = Path.GetFullPath(programDirectory ?? Path.GetDirectoryName(this.dataRoot)!);
        statePath = Path.Combine(this.dataRoot, "Updates", "state.json"); stagedPath = Path.Combine(this.dataRoot, "Updates", "pending.json");
        persistent = ReadState();
        snapshot = new(UpdateState.Idle, persistent.Preview ? "meta" : "plus", currentVersion, null, 0, 0, null, [], null, this.trust.IsConfigured, UpdateTrustStore.IsTestBuild,
            persistent.Preview, persistent.PinnedVersion, LastSuccessfulCheckUtc: persistent.LastSuccessfulCheckUtc, RetryAfterUtc: persistent.RetryAfterUtc);
        metadata = new(Path.Combine(this.dataRoot, "Updates", "metadata-cache-v2"), transport, this.clock,
            () => { lock (gate) return persistent.RetryAfterUtc; }, until => { lock (gate) { SaveState(persistent with { RetryAfterUtc = until }); snapshot = snapshot with { RetryAfterUtc = until }; } });
        RestoreStaged(); ReconcileCommittedPolicy();
    }
    public void ImportLegacyPreviewPreference(bool enabled)
    { lock (gate) { if (policyExists) return; } SetPreviewEnabled(enabled); }
    public void SetPreviewEnabled(bool enabled)
    {
        lock (gate)
        {
            if (policyExists && persistent.Preview == enabled) return;
            bool blockPending = !enabled && (consentCandidate?.Manifest.Channel == "meta" || snapshot.Staged?.Candidate.Manifest.Channel == "meta" ||
                persistent.PendingManifestSha256 is not null && snapshot.Candidate?.Manifest.Channel == "meta");
            SaveState(persistent with { Preview = enabled, PendingManifestSha256 = blockPending ? null : persistent.PendingManifestSha256 }); generation++;
            if (blockPending) { consent = null; consentCandidate = null; rollbackAuthorization = null; }
            snapshot = snapshot with { Channel = enabled ? "meta" : "plus", PreviewEnabled = enabled,
                State = blockPending ? UpdateState.Idle : snapshot.State, Staged = blockPending ? null : snapshot.Staged,
                Candidate = !enabled && snapshot.Candidate?.Manifest.Channel == "meta" ? null : snapshot.Candidate,
                ErrorCode = blockPending ? "UPDATE_PREVIEW_PLAN_CANCELLED" : null, CheckComplete = false, ShouldPrompt = false };
            if (blockPending) try { DeletePending(); } catch (Exception ex) when (Handled(ex)) { snapshot = snapshot with { ErrorCode = "UPDATE_PREVIEW_PLAN_CANCELLED_PENDING_CLEANUP" }; }
        }
        Notify();
    }
    public void RestoreFollowingChannel()
    { lock (gate) { SaveState(persistent with { PinnedVersion = null }); snapshot = snapshot with { PinnedVersion = null, ShouldPrompt = ShouldPrompt(snapshot.Candidate) }; } Notify(); }
    public void DismissCandidate(UpdateCandidate candidate)
    { lock (gate) { dismissed.Add(Identity(candidate).ManifestSha256); snapshot = snapshot with { ShouldPrompt = ShouldPrompt(snapshot.Candidate) }; } Notify(); }
    public void CancelConsent()
    {
        lock (gate)
        {
            SaveState(persistent with { PendingManifestSha256 = null }); consent = null; consentCandidate = null; rollbackAuthorization = null; generation++;
            snapshot = snapshot with { Staged = null, State = snapshot.Candidate is null ? UpdateState.Idle : UpdateState.Available, ErrorCode = null };
            try { DeletePending(); } catch (Exception ex) when (Handled(ex)) { snapshot = snapshot with { ErrorCode = "UPDATE_CANCELLED_PENDING_CLEANUP" }; }
        }
        Notify();
    }
    public Task CheckOnStartupAsync(CancellationToken cancellationToken = default)
    { lock (gate) return (startupTask ??= CheckAsync(persistent.Preview)).WaitAsync(cancellationToken); }
    public Task CheckAsync(bool previewEnabled, CancellationToken cancellationToken = default)
    {
        SetPreviewEnabled(previewEnabled);
        lock (gate)
        {
            // Each caller can abandon waiting without cancelling another caller's shared network operation.
            if (checkTask is null || checkTask.IsCompleted) { var token = lifetime.Token; checkTask = Task.Run(() => CheckLoopAsync(token)); }
            return checkTask.WaitAsync(cancellationToken);
        }
    }
    private async Task CheckLoopAsync(CancellationToken token)
    {
        do
        {
            await CheckCoreAsync(token).ConfigureAwait(false);
            lock (gate) { if (snapshot.ErrorCode != "UPDATE_CHANNEL_CHANGED") return; }
        } while (!token.IsCancellationRequested);
    }
    private async Task CheckCoreAsync(CancellationToken token)
    {
        await operation.WaitAsync(token).ConfigureAwait(false);
        int captured; bool preview; StagedUpdate? staged;
        lock (gate) { captured = generation; preview = persistent.Preview; staged = snapshot.Staged; snapshot = snapshot with { LastCheckUtc = clock(), CheckComplete = false }; }
        try
        {
            Publish(UpdateState.Checking, null, []); CheckClock();
            List<UpdateCandidate> candidates = []; List<string> warnings = []; bool complete = true;
            if (!trust.IsConfigured) warnings.Add("UPDATE_PRODUCTION_TRUST_NOT_CONFIGURED");
            try
            {
                using var latest = JsonDocument.Parse(await metadata.GetAsync(Api("/latest"), GitHubRequestKind.Api, 4 * 1024 * 1024, token).ConfigureAwait(false));
                var choice = await ReadChoiceAsync(latest.RootElement, UpdateSelectionMode.Upgrade, token).ConfigureAwait(false);
                if (latest.RootElement.GetProperty("draft").GetBoolean() || choice.IsPreview) throw new UpdateException("UPDATE_LATEST_NOT_STABLE");
                if (choice.CanInstall) candidates.Add(choice.Candidate!);
                else if (choice.DisabledReason is not ("UPDATE_CURRENT_VERSION" or "UPDATE_UPGRADE_REQUIRES_NEWER")) { warnings.Add(choice.DisabledReason ?? "UPDATE_RELEASE_INVALID"); complete = false; }
            }
            catch (UpdateException ex) when (ex.Code == "UPDATE_NOT_FOUND") { warnings.Add("UPDATE_LATEST_NOT_FOUND"); complete = false; }
            if (preview)
            {
                HashSet<long> seen = []; bool ended = false;
                for (int page = 1; page <= 100; page++)
                {
                    using var json = JsonDocument.Parse(await metadata.GetAsync(Api($"?per_page=100&page={page}"), GitHubRequestKind.Api, 4 * 1024 * 1024, token).ConfigureAwait(false));
                    ValidateList(json.RootElement);
                    foreach (var release in json.RootElement.EnumerateArray())
                    {
                        if (release.GetProperty("draft").GetBoolean() || !release.GetProperty("prerelease").GetBoolean()) continue;
                        if (!seen.Add(release.GetProperty("id").GetInt64())) continue;
                        var choice = await ReadChoiceAsync(release, UpdateSelectionMode.Upgrade, token).ConfigureAwait(false);
                        if (choice.CanInstall) candidates.Add(choice.Candidate!);
                        else if (choice.DisabledReason is not ("UPDATE_CURRENT_VERSION" or "UPDATE_UPGRADE_REQUIRES_NEWER")) { warnings.Add(choice.DisabledReason ?? "UPDATE_RELEASE_INVALID"); complete = false; }
                    }
                    if (json.RootElement.GetArrayLength() < 100) { ended = true; break; }
                }
                if (!ended) throw new UpdateException("UPDATE_RELEASE_LIST_INCOMPLETE");
            }
            var selected = candidates.OrderByDescending(c => SemanticVersion.Parse(c.Manifest.Version)).FirstOrDefault();
            if (!preview && SemanticVersion.Parse(snapshot.CurrentVersion).IsPrerelease && selected is null) warnings.Add("UPDATE_PREVIEW_STOPPED_KEEP_CURRENT");
            lock (gate)
            {
                if (captured != generation) throw new UpdateException("UPDATE_CHANNEL_CHANGED");
                if (complete) SaveState(persistent with { LastSuccessfulCheckUtc = clock() });
                snapshot = snapshot with { State = staged is not null ? UpdateState.Staged : selected is not null ? UpdateState.Available : complete ? UpdateState.NoCompatibleUpdate : UpdateState.Error,
                    Candidate = staged?.Candidate ?? selected, Staged = staged, BytesReceived = 0, TotalBytes = selected?.Manifest.Payload.Bytes ?? 0,
                    ErrorCode = !trust.IsConfigured ? "UPDATE_PRODUCTION_TRUST_NOT_CONFIGURED" : complete ? null : warnings.FirstOrDefault(), Warnings = warnings.Distinct().Take(50).ToArray(),
                    CheckComplete = complete, LastSuccessfulCheckUtc = persistent.LastSuccessfulCheckUtc, RetryAfterUtc = persistent.RetryAfterUtc, ShouldPrompt = staged is null && ShouldPrompt(selected) };
            }
            Notify();
        }
        catch (OperationCanceledException) { Publish(UpdateState.Idle, "UPDATE_CANCELLED"); throw; }
        catch (Exception ex) when (Handled(ex)) { Publish(staged is null ? UpdateState.Error : UpdateState.Staged, Error(ex)); }
        finally { operation.Release(); }
    }
    public async Task<UpdateHistoryPage> LoadHistoryAsync(UpdateSelectionMode mode, bool reset = false, CancellationToken cancellationToken = default)
    {
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (reset) { history.Clear(); historyPage = 1; historyHasMore = true; }
            if (historyHasMore)
            {
                using var json = JsonDocument.Parse(await metadata.GetAsync(Api($"?per_page=100&page={historyPage}"), GitHubRequestKind.Api, 4 * 1024 * 1024, cancellationToken).ConfigureAwait(false));
                ValidateList(json.RootElement); List<UpdateReleaseChoice> page = []; HashSet<long> seen = [];
                foreach (var release in json.RootElement.EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (release.GetProperty("draft").GetBoolean() || !seen.Add(release.GetProperty("id").GetInt64())) continue;
                    page.Add(await ReadChoiceAsync(release, mode, cancellationToken).ConfigureAwait(false));
                }
                foreach (var choice in page) history[choice.ReleaseId] = choice;
                historyPage++; historyHasMore = json.RootElement.GetArrayLength() == 100;
            }
            return HistoryResult(mode);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (Handled(ex)) { return HistoryResult(mode, Error(ex)); }
        finally { operation.Release(); }
    }
    private UpdateHistoryPage HistoryResult(UpdateSelectionMode mode, string? error = null)
    {
        var choices = history.Values.Select(c => c.Candidate is null ? c : c with { DisabledReason = Ineligible(c.Candidate, mode) })
            .OrderBy(c => c.IsPreview).ThenByDescending(c => c.Version is not null && SemanticVersion.TryParse(c.Version, out var v) ? v : null).ThenBy(c => c.Tag, StringComparer.Ordinal).ToArray();
        return new(choices, historyHasMore, error);
    }
    private async Task<UpdateReleaseChoice> ReadChoiceAsync(JsonElement release, UpdateSelectionMode mode, CancellationToken token)
    {
        long id = release.GetProperty("id").GetInt64(); string tag = release.GetProperty("tag_name").GetString() ?? "";
        string name = release.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? tag : tag;
        string notes = release.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
        notes = notes[..Math.Min(64 * 1024 + 1, notes.Length)];
        bool preview = release.GetProperty("prerelease").GetBoolean(); DateTimeOffset? published = release.TryGetProperty("published_at", out var p) && p.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(p.GetString(), out var date) ? date : null;
        var choice = new UpdateReleaseChoice(id, tag, name, notes, preview, published, null, null, null);
        try
        {
            if (release.GetProperty("draft").GetBoolean()) throw new UpdateException("UPDATE_RELEASE_WITHDRAWN");
            if (id <= 0 || tag.Length is < 1 or > 200 || tag.Any(char.IsControl)) throw new UpdateException("UPDATE_RELEASE_INVALID");
            var assets = release.GetProperty("assets").EnumerateArray().ToArray();
            var descriptor = Asset(assets, "autumn.update.json", tag); var signatureAsset = Asset(assets, "autumn.update.sig", tag);
            byte[] bytes = await metadata.GetAsync(descriptor.Url, GitHubRequestKind.Asset, UpdateManifestCodec.MaximumManifestBytes, token).ConfigureAwait(false);
            byte[] signature = await metadata.GetAsync(signatureAsset.Url, GitHubRequestKind.Asset, 16 * 1024, token).ConfigureAwait(false);
            if (descriptor.Bytes != bytes.Length || signatureAsset.Bytes != signature.Length) throw new UpdateException("UPDATE_ASSET_SIZE_MISMATCH");
            // Historical discovery verifies identity but grants no replay exemption. Consent and install enforce the ledger.
            var manifest = UpdateSignature.Verify(bytes, signature, trust, clock());
            if (manifest.Payload.ReleaseId != id || (manifest.Channel == "meta") != preview || !TagMatches(manifest, tag)) throw new UpdateException("UPDATE_RELEASE_MANIFEST_MISMATCH");
            var payload = Asset(assets, manifest.Payload.Asset, tag);
            if (payload.Id != manifest.Payload.AssetId || payload.Bytes != manifest.Payload.Bytes) throw new UpdateException("UPDATE_ASSET_IDENTITY_MISMATCH");
            var candidate = new UpdateCandidate(id, tag, notes, manifest, payload.Url, payload.Id, bytes, signature, published, name);
            return choice with { Version = manifest.Version, Candidate = candidate, DisabledReason = Ineligible(candidate, mode) };
        }
        catch (Exception ex) when (Handled(ex)) { return choice with { DisabledReason = Error(ex) }; }
    }
    private string? Ineligible(UpdateCandidate candidate, UpdateSelectionMode mode)
    {
        try { CheckCandidate(candidate, mode, discovery: true); return null; }
        catch (UpdateException ex) { return ex.Code; }
    }
    public void ConfirmTarget(UpdateCandidate candidate, UpdateSelectionMode mode = UpdateSelectionMode.Upgrade, bool pinAfterSuccess = false)
    {
        lock (gate)
        {
            if (snapshot.State is UpdateState.Downloading or UpdateState.Verifying || consent is not null) throw new UpdateException("UPDATE_OPERATION_IN_PROGRESS");
            CheckCandidate(candidate, mode, discovery: true);
            rollbackAuthorization = mode == UpdateSelectionMode.Rollback ? UpdateRollbackAuthorization.Create(candidate, currentBuildId, snapshot.CurrentVersion, clock()) : null;
            consent = new(Identity(candidate), mode, pinAfterSuccess); consentCandidate = candidate;
            snapshot = snapshot with { Candidate = candidate, State = UpdateState.Available, ErrorCode = null, ShouldPrompt = false };
        }
        Notify();
    }
    public async Task DownloadAndStageAsync(CancellationToken cancellationToken = default)
    {
        // Acquire and mark in one critical section so a duplicate cannot revoke an active download.
        lock (gate)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                if (!downloadActive && snapshot.Staged is null) ClearFailedConsent();
                cancellationToken.ThrowIfCancellationRequested();
            }
            if (!operation.Wait(0))
            {
                if (!downloadActive && snapshot.Staged is null) ClearFailedConsent();
                throw new UpdateException("UPDATE_OPERATION_IN_PROGRESS");
            }
            downloadActive = true;
        }
        string? downloadId = null;
        try
        {
            UpdateCandidate candidate; UpdateConsent approved; int captured;
            lock (gate) { candidate = consentCandidate ?? throw new UpdateException("UPDATE_CONSENT_REQUIRED"); approved = consent ?? throw new UpdateException("UPDATE_CONSENT_REQUIRED"); captured = generation;
                if (snapshot.Staged is not null) throw new UpdateException("UPDATE_ALREADY_STAGED"); }
            CheckApprovedTarget(candidate, approved); await RevalidateAsync(candidate, approved, cancellationToken).ConfigureAwait(false);
            Publish(UpdateState.Downloading, null); var manifest = candidate.Manifest;
            var task = downloads.Enqueue(new("cn.labchronicles.autumnos", manifest.Version, candidate.PayloadUrl, manifest.Payload.Bytes, manifest.Payload.Sha256, 1, candidate.ReleaseId, candidate.AssetId)); downloadId = task.Id;
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
            Publish(UpdateState.Verifying, null); CheckApprovedTarget(candidate, approved);
            string directory = Path.Combine(dataRoot, "Updates", "staging", Guid.NewGuid().ToString("N")); UpdatePaths.EnsureNoReparsePoints(directory); Directory.CreateDirectory(directory);
            string manifestPath = Path.Combine(directory, "autumn.update.json"), signaturePath = Path.Combine(directory, "autumn.update.sig"), payloadPath = Path.Combine(directory, "payload.zip");
            await File.WriteAllBytesAsync(manifestPath, candidate.ManifestBytes, cancellationToken).ConfigureAwait(false);
            await File.WriteAllBytesAsync(signaturePath, candidate.SignatureBytes, cancellationToken).ConfigureAwait(false);
            UpdatePaths.EnsureNoReparsePoints(finished.LocalPath!);
            await using (var input = new FileStream(finished.LocalPath!, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous))
            await using (var output = new FileStream(payloadPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous))
            { await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false); await output.FlushAsync(cancellationToken).ConfigureAwait(false); output.Flush(true); }
            await UpdatePayload.VerifyAsync(payloadPath, manifest, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (captured != generation || consent != approved) throw new UpdateException("UPDATE_CHANNEL_CHANGED");
                StagedUpdate staged = new(candidate, manifestPath, signaturePath, payloadPath, approved, rollbackAuthorization);
                SaveState(persistent with { HighestSequence = Math.Max(persistent.HighestSequence, manifest.Sequence),
                    HighestDigest = manifest.Sequence >= persistent.HighestSequence ? Digest(candidate.ManifestBytes) : persistent.HighestDigest,
                    LastTrustedUtc = clock() > persistent.LastTrustedUtc ? clock() : persistent.LastTrustedUtc, PendingManifestSha256 = Digest(candidate.ManifestBytes) });
                WriteAtomic(stagedPath, new PendingState(currentBuildId, snapshot.CurrentVersion, staged));
                snapshot = snapshot with { State = UpdateState.Staged, Staged = staged, ErrorCode = null };
            }
            Notify();
        }
        catch (OperationCanceledException) { if (downloadId is not null) downloads.Pause(downloadId); ClearFailedConsent(); Publish(UpdateState.Idle, "UPDATE_CANCELLED"); throw; }
        catch (Exception ex) when (Handled(ex)) { if (downloadId is not null) downloads.Pause(downloadId); ClearFailedConsent(); Publish(UpdateState.Error, Error(ex)); }
        finally { lock (gate) { downloadActive = false; operation.Release(); } }
    }
    public async Task<StagedUpdate> ValidateStagedForCommitAsync(CancellationToken cancellationToken = default)
    {
        await operation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var staged = ValidateStagedForCommit();
            await RevalidateAsync(staged.Candidate, staged.Consent ?? throw new UpdateException("UPDATE_CONSENT_REQUIRED"), cancellationToken).ConfigureAwait(false);
            await UpdatePayload.VerifyAsync(staged.PayloadPath, staged.Candidate.Manifest, cancellationToken).ConfigureAwait(false);
            lock (gate)
            {
                if (staged.Consent!.Mode == UpdateSelectionMode.Rollback)
                {
                    rollbackAuthorization = UpdateRollbackAuthorization.Create(staged.Candidate, currentBuildId, snapshot.CurrentVersion, clock());
                    staged = staged with { RollbackAuthorization = rollbackAuthorization };
                    WriteAtomic(stagedPath, new PendingState(currentBuildId, snapshot.CurrentVersion, staged));
                    snapshot = snapshot with { Staged = staged };
                }
            }
            return ValidateStagedForCommit();
        }
        finally { operation.Release(); }
    }
    // Local recovery validation only. Actual handoff must call the async fresh-validation method after explicit restart consent.
    public StagedUpdate ValidateStagedForCommit()
    {
        lock (gate)
        {
            var staged = snapshot.Staged ?? throw new UpdateException("UPDATE_NOT_STAGED");
            var approved = staged.Consent ?? throw new UpdateException("UPDATE_CONSENT_REQUIRED");
            if (approved.Target != Identity(staged.Candidate)) throw new UpdateException("UPDATE_CONSENT_TARGET_CHANGED");
            CheckApprovedTarget(staged.Candidate, approved);
            foreach (string path in new[] { staged.ManifestPath, staged.SignaturePath, staged.PayloadPath }) ValidateStagingPath(path);
            if (!File.ReadAllBytes(staged.ManifestPath).AsSpan().SequenceEqual(staged.Candidate.ManifestBytes) || !File.ReadAllBytes(staged.SignaturePath).AsSpan().SequenceEqual(staged.Candidate.SignatureBytes)) throw new UpdateException("UPDATE_STAGED_METADATA_CHANGED");
            return staged;
        }
    }
    private async Task RevalidateAsync(UpdateCandidate candidate, UpdateConsent approved, CancellationToken token)
    {
        if (Identity(candidate) != approved.Target) throw new UpdateException("UPDATE_CONSENT_TARGET_CHANGED");
        using var json = JsonDocument.Parse(await metadata.GetAsync(Api("/" + candidate.ReleaseId), GitHubRequestKind.Api, 4 * 1024 * 1024, token).ConfigureAwait(false));
        var fresh = await ReadChoiceAsync(json.RootElement, approved.Mode, token).ConfigureAwait(false);
        if (!fresh.CanInstall) throw new UpdateException(fresh.DisabledReason ?? "UPDATE_RELEASE_WITHDRAWN");
        if (Identity(fresh.Candidate!) != approved.Target) throw new UpdateException("UPDATE_CONSENT_TARGET_CHANGED");
        CheckApprovedTarget(candidate, approved);
    }
    public void ReconcileCommittedPolicy()
    {
        lock (gate)
        {
            string journalPath = Path.Combine(programDirectory, ".autumnos-update", "journal.json");
            string receiptPath = Path.Combine(programDirectory, "autumn.install.json");
            UpdatePaths.EnsureNoReparsePoints(journalPath); UpdatePaths.EnsureNoReparsePoints(receiptPath);
            if (!File.Exists(journalPath) || !File.Exists(receiptPath)) return;
            if (new FileInfo(journalPath).Length > 16 * 1024 * 1024 || new FileInfo(receiptPath).Length > 16 * 1024 * 1024) throw new UpdateException("UPDATE_RECEIPT_INVALID");
            using var journal = JsonDocument.Parse(File.ReadAllBytes(journalPath));
            var value = journal.RootElement;
            if (!value.TryGetProperty("phase", out var phase) || phase.GetString() != "committed" ||
                !value.TryGetProperty("toBuildId", out var build) || build.GetString() != currentBuildId ||
                !value.TryGetProperty("targetVersion", out var version) || version.GetString() != snapshot.CurrentVersion ||
                !value.TryGetProperty("transactionId", out var transaction) || transaction.GetString() == persistent.ReconciledTransactionId) return;
            using var receipt = JsonDocument.Parse(File.ReadAllBytes(receiptPath));
            if (receipt.RootElement.GetProperty("buildId").GetString() != currentBuildId || receipt.RootElement.GetProperty("version").GetString() != snapshot.CurrentVersion) return;
            bool pin = value.TryGetProperty("pinAfterSuccess", out var pinned) && pinned.GetBoolean();
            SaveState(persistent with { PinnedVersion = pin ? snapshot.CurrentVersion : null, ReconciledTransactionId = transaction.GetString() });
            snapshot = snapshot with { PinnedVersion = persistent.PinnedVersion, ShouldPrompt = ShouldPrompt(snapshot.Candidate) };
        }
        Notify();
    }
    public void RecordFailedCandidate(string buildId)
    {
        if (!UpdateManifestCodec.Identifier(buildId)) throw new UpdateException("UPDATE_BUILD_ID_INVALID");
        lock (gate) SaveState(persistent with { FailedBuildIds = persistent.FailedBuildIds.Append(buildId).Distinct().TakeLast(128).ToArray() });
    }
    private void CheckApprovedTarget(UpdateCandidate candidate, UpdateConsent approved)
    {
        if (Identity(candidate) != approved.Target) throw new UpdateException("UPDATE_CONSENT_TARGET_CHANGED");
        // Durable consent authorizes download/staging of these bytes. The short-lived installer capability
        // is minted again only by an explicit restart action, so "later" and long downloads remain usable.
        CheckCandidate(candidate, approved.Mode, discovery: approved.Mode == UpdateSelectionMode.Rollback);
    }
    private void CheckCandidate(UpdateCandidate candidate, UpdateSelectionMode mode, bool discovery = false)
    {
        CheckClock();
        var manifest = mode == UpdateSelectionMode.Rollback && !discovery
            ? UpdateSignature.VerifyForRollback(candidate.ManifestBytes, candidate.SignatureBytes, trust, clock(), persistent.HighestSequence,
                rollbackAuthorization ?? throw new UpdateException("UPDATE_ROLLBACK_CONSENT_REQUIRED"), currentBuildId, snapshot.CurrentVersion)
            : UpdateSignature.Verify(candidate.ManifestBytes, candidate.SignatureBytes, trust, clock(), mode == UpdateSelectionMode.Rollback ? 0 : persistent.HighestSequence);
        if (!TagMatches(manifest, candidate.Tag) || !UpdateManifestCodec.Serialize(manifest).AsSpan().SequenceEqual(UpdateManifestCodec.Serialize(candidate.Manifest))) throw new UpdateException("UPDATE_RELEASE_MANIFEST_MISMATCH");
        if (candidate.ReleaseId != manifest.Payload.ReleaseId || candidate.AssetId != manifest.Payload.AssetId ||
            !IsOfficialAssetUrl(candidate.PayloadUrl.AbsoluteUri, candidate.Tag, manifest.Payload.Asset)) throw new UpdateException("UPDATE_ASSET_IDENTITY_MISMATCH");
        if (manifest.Channel == "meta" && !persistent.Preview) throw new UpdateException("UPDATE_PREVIEW_DISABLED");
        if (SemanticVersion.Parse(UpdaterVersion).CompareTo(SemanticVersion.Parse(manifest.MinimumUpdaterVersion)) < 0) throw new UpdateException("UPDATE_UPDATER_TOO_OLD");
        int compare = SemanticVersion.Parse(manifest.Version).CompareTo(SemanticVersion.Parse(snapshot.CurrentVersion));
        if (compare == 0) throw new UpdateException("UPDATE_CURRENT_VERSION");
        if (mode == UpdateSelectionMode.Upgrade && compare < 0) throw new UpdateException("UPDATE_UPGRADE_REQUIRES_NEWER");
        if (mode == UpdateSelectionMode.Rollback && SemanticVersion.Parse(manifest.MinimumUpdaterVersion).CompareTo(SemanticVersion.Parse("1.1.0")) < 0) throw new UpdateException("UPDATE_ROLLBACK_PROTOCOL_UNSUPPORTED");
        if (mode == UpdateSelectionMode.Rollback && compare > 0) throw new UpdateException("UPDATE_ROLLBACK_REQUIRES_OLDER");
        if (manifest.BuildId == currentBuildId || persistent.FailedBuildIds.Contains(manifest.BuildId)) throw new UpdateException("UPDATE_CANDIDATE_PREVIOUSLY_FAILED_OR_CURRENT");
        CheckAccepted(manifest, candidate.ManifestBytes, mode == UpdateSelectionMode.Rollback);
        if (compatibilityCheck?.Invoke(manifest, mode == UpdateSelectionMode.Rollback) is { } compatibilityError) throw new UpdateException(compatibilityError);
    }
    private void CheckClock() { if (persistent.LastTrustedUtc != DateTimeOffset.MinValue && clock() < persistent.LastTrustedUtc.AddMinutes(-5)) throw new UpdateException("UPDATE_CLOCK_ROLLBACK"); }
    private void CheckAccepted(UpdateManifest manifest, byte[] bytes, bool rollback)
    {
        if (manifest.Sequence == persistent.HighestSequence && persistent.HighestDigest is not null && Digest(bytes) != persistent.HighestDigest) throw new UpdateException("UPDATE_SEQUENCE_REUSED");
        string ledgerPath = Path.Combine(programDirectory, ".autumnos-update", "security.json"); UpdatePaths.EnsureNoReparsePoints(ledgerPath);
        if (!File.Exists(ledgerPath)) return;
        if (new FileInfo(ledgerPath).Length > 65536) throw new UpdateException("UPDATE_SECURITY_LEDGER_INVALID");
        var ledger = UpdateManifestCodec.ParseStrict<SecurityLedger>(File.ReadAllBytes(ledgerPath), 65536);
        if (ledger.HighestSequence < 0 || ledger.FailedBuilds is null || ledger.FailedBuilds.Count > 4096 || ledger.FailedBuilds.Any(b => !UpdateManifestCodec.Identifier(b)) ||
            ledger.ManifestSha256.Length != 0 && !UpdateManifestCodec.Hash(ledger.ManifestSha256)) throw new UpdateException("UPDATE_SECURITY_LEDGER_INVALID");
        if (!rollback && manifest.Sequence < ledger.HighestSequence) throw new UpdateException("UPDATE_METADATA_REPLAY");
        if (manifest.Sequence == ledger.HighestSequence && ledger.ManifestSha256.Length != 0 && ledger.ManifestSha256 != Digest(bytes)) throw new UpdateException("UPDATE_SEQUENCE_REUSED");
        if (ledger.MinimumVersion is { } minimum && SemanticVersion.Parse(manifest.Version).CompareTo(SemanticVersion.Parse(minimum)) < 0) throw new UpdateException("UPDATE_MINIMUM_SECURITY_VERSION");
        if (ledger.FailedBuilds.Contains(manifest.BuildId)) throw new UpdateException("UPDATE_CANDIDATE_PREVIOUSLY_FAILED_OR_CURRENT");
    }
    private static bool TagMatches(UpdateManifest manifest, string tag)
    {
        if (manifest.ReleaseTag is not null) return manifest.ReleaseTag == tag;
        if (tag == manifest.Channel + "-v" + manifest.Version) return true;
        string normalized = tag.StartsWith('v') ? tag[1..] : tag;
        return normalized == manifest.Version; // Never infer comparable identity from an opaque historical display name.
    }
    public static UpdateTargetIdentity Identity(UpdateCandidate candidate) => new(Repository, candidate.ReleaseId, candidate.Tag, candidate.Manifest.Version,
        candidate.Manifest.TargetRid, candidate.AssetId, candidate.Manifest.Payload.Asset, candidate.Manifest.Payload.Bytes, candidate.Manifest.Payload.Sha256, Digest(candidate.ManifestBytes));
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static Uri Api(string suffix) => new($"https://api.github.com/repos/{Repository}/releases{suffix}");
    private static void ValidateList(JsonElement value) { if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() > 100) throw new UpdateException("UPDATE_RELEASE_LIST_INVALID"); }
    private static (long Id, long Bytes, Uri Url) Asset(JsonElement[] assets, string name, string tag)
    {
        var matching = assets.Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (matching.Length != 1) throw new UpdateException("UPDATE_ASSET_MISSING_OR_AMBIGUOUS");
        var asset = matching[0]; string expected = $"https://github.com/{Repository}/releases/download/{Uri.EscapeDataString(tag)}/{Uri.EscapeDataString(name)}";
        string source = asset.GetProperty("browser_download_url").GetString() ?? "";
        if (!IsOfficialAssetUrl(source, tag, name) || asset.GetProperty("id").GetInt64() <= 0 || asset.GetProperty("size").GetInt64() <= 0) throw new UpdateException("UPDATE_ASSET_SOURCE_INVALID");
        return (asset.GetProperty("id").GetInt64(), asset.GetProperty("size").GetInt64(), new(expected));
    }
    internal static bool IsOfficialAssetUrl(string source, string tag, string name)
    {
        if (source.Any(char.IsControl) || source != source.Trim() || !Uri.TryCreate(source, UriKind.Absolute, out var uri) ||
            uri.Scheme != "https" || uri.Host != "github.com" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0) return false;
        string prefix = $"/{Repository}/releases/download/";
        if (!uri.AbsolutePath.StartsWith(prefix, StringComparison.Ordinal)) return false;
        string[] segments = uri.AbsolutePath[prefix.Length..].Split('/');
        return segments.Length == 2 && Uri.UnescapeDataString(segments[0]) == tag && Uri.UnescapeDataString(segments[1]) == name;
    }
    private PersistentState ReadState()
    {
        UpdatePaths.EnsureNoReparsePoints(statePath); policyExists = File.Exists(statePath);
        if (!policyExists) return new(1, false, 0, null, DateTimeOffset.MinValue, []);
        if (new FileInfo(statePath).Length > 65536) throw new UpdateException("UPDATE_STATE_INVALID");
        var saved = UpdateManifestCodec.ParseStrict<PersistentState>(File.ReadAllBytes(statePath), 65536);
        if (saved.SchemaVersion != 1 || saved.HighestSequence < 0 || saved.HighestSequence > 0 && !UpdateManifestCodec.Hash(saved.HighestDigest) || saved.FailedBuildIds is null || saved.FailedBuildIds.Count > 128 ||
            saved.FailedBuildIds.Any(id => !UpdateManifestCodec.Identifier(id)) || saved.PinnedVersion is not null && !SemanticVersion.TryParse(saved.PinnedVersion, out _) || saved.PendingManifestSha256 is not null && !UpdateManifestCodec.Hash(saved.PendingManifestSha256)) throw new UpdateException("UPDATE_STATE_INVALID");
        return saved;
    }
    private void SaveState(PersistentState value)
    { WriteAtomic(statePath, value); persistent = value; policyExists = true; }
    private void WriteAtomic<T>(string path, T value)
    {
        using var lease = enterCriticalOperation?.Invoke("update-state-write"); UpdatePaths.EnsureNoReparsePoints(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { file.Write(JsonSerializer.SerializeToUtf8Bytes(value, UpdateManifestCodec.JsonOptions)); file.Flush(true); }
            UpdatePaths.EnsureNoReparsePoints(path); File.Move(temp, path, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private void RestoreStaged()
    {
        try
        {
            UpdatePaths.EnsureNoReparsePoints(stagedPath); if (!File.Exists(stagedPath)) return;
            if (new FileInfo(stagedPath).Length > 12 * 1024 * 1024) throw new UpdateException("UPDATE_PENDING_INVALID");
            var pending = UpdateManifestCodec.ParseStrict<PendingState>(File.ReadAllBytes(stagedPath), 12 * 1024 * 1024);
            if (persistent.PendingManifestSha256 != Digest(pending.Staged.Candidate.ManifestBytes)) return;
            if (pending.FromBuildId != currentBuildId || pending.FromVersion != snapshot.CurrentVersion) return;
            var staged = pending.Staged; consent = staged.Consent; consentCandidate = staged.Candidate; rollbackAuthorization = staged.RollbackAuthorization;
            snapshot = snapshot with { Staged = staged, Candidate = staged.Candidate }; _ = ValidateStagedForCommit();
            snapshot = snapshot with { State = UpdateState.Staged, TotalBytes = staged.Candidate.Manifest.Payload.Bytes, BytesReceived = staged.Candidate.Manifest.Payload.Bytes };
        }
        catch (Exception ex) when (Handled(ex)) { consent = null; consentCandidate = null; rollbackAuthorization = null; snapshot = snapshot with { Staged = null, State = UpdateState.Error, ErrorCode = Error(ex) }; }
    }
    private void ValidateStagingPath(string path)
    {
        string root = Path.GetFullPath(Path.Combine(dataRoot, "Updates", "staging")) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(path).StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new UpdateException("UPDATE_PENDING_PATH_INVALID"); UpdatePaths.EnsureNoReparsePoints(path);
    }
    private void DeletePending() { UpdatePaths.EnsureNoReparsePoints(stagedPath); if (File.Exists(stagedPath)) File.Delete(stagedPath); }
    private void ClearFailedConsent() { lock (gate) { consent = null; consentCandidate = null; rollbackAuthorization = null; } }
    private bool ShouldPrompt(UpdateCandidate? candidate) => candidate is not null && snapshot.Staged is null && consent is null && persistent.PinnedVersion is null && !dismissed.Contains(Identity(candidate).ManifestSha256);
    private void Publish(UpdateState state, string? error, IReadOnlyList<string>? warnings = null)
    { lock (gate) snapshot = snapshot with { State = state, ErrorCode = error, Warnings = warnings ?? snapshot.Warnings }; Notify(); }
    private static bool Handled(Exception ex) => ex is UpdateException or CatalogException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException or FormatException;
    private static string Error(Exception ex) => ex switch { UpdateException u => u.Code, CatalogException c => c.Code, UnauthorizedAccessException => "UPDATE_ACCESS_DENIED", IOException => "UPDATE_IO_ERROR", JsonException or KeyNotFoundException or FormatException => "UPDATE_RELEASE_INVALID", _ => "UPDATE_OPERATION_FAILED" };
    private void Notify() { if (Changed is not null) foreach (EventHandler handler in Changed.GetInvocationList()) try { handler(this, EventArgs.Empty); } catch (Exception) { } }
    public void Dispose() { lifetime.Cancel(); ownedDownloads?.Dispose(); ownedTransport?.Dispose(); if (checkTask is { IsCompleted: false } running) _ = running.ContinueWith(_ => lifetime.Dispose(), TaskScheduler.Default); else lifetime.Dispose(); }
    private sealed record PersistentState(int SchemaVersion, bool Preview, long HighestSequence, string? HighestDigest, DateTimeOffset LastTrustedUtc, IReadOnlyList<string> FailedBuildIds,
        string? PinnedVersion = null, DateTimeOffset? LastSuccessfulCheckUtc = null, DateTimeOffset? RetryAfterUtc = null, string? ReconciledTransactionId = null, string? PendingManifestSha256 = null);
    private sealed record SecurityLedger(long HighestSequence, IReadOnlyList<string> FailedBuilds, string ManifestSha256 = "", string? MinimumVersion = null);
    private sealed record PendingState(string FromBuildId, string FromVersion, StagedUpdate Staged);
}
