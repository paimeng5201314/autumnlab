using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Store;

namespace AutumnOS.Update;

public sealed record UpdateSecurityLedgerState(long HighestSequence, string[] FailedBuilds,
    string ManifestSha256 = "", string? MinimumVersion = null, DateTimeOffset LastTrustedUtc = default);

/// <summary>Restrictive state belongs to the stable Data root, not an extractable payload seed.
/// Legacy records are locally bound state, not signed provenance or additional update trust.</summary>
public static class UpdateSecurityLedgerStore
{
    public const int MaximumFailedBuilds = 4096;
    public const int MaximumLegacySources = 128;
    private const string Initialized = "AutumnOS.update.security.v1\n";
    private const int MaximumLedgerBytes = 1024 * 1024;

    public static UpdateSecurityLedgerState LoadAndMerge(string programDirectory, string? dataDirectory = null) =>
        Change(programDirectory, dataDirectory, null);

    // The caller never supplies an old snapshot to write back. Every read/merge/write,
    // including the compatible current-root ledger, is owned by the same Data lock.
    public static UpdateSecurityLedgerState Record(string programDirectory, long sequence, string? manifestSha256,
        DateTimeOffset trustedUtc, string? failedBuildId = null) => Change(programDirectory, null,
            new(sequence, failedBuildId is null ? [] : [failedBuildId], manifestSha256 ?? "", null, trustedUtc), writeCurrentLedger: true);

    public static UpdateSecurityLedgerState RecordPolicy(string programDirectory, string dataDirectory, long sequence,
        string? manifestSha256, DateTimeOffset trustedUtc, IReadOnlyList<string> failedBuilds)
    {
        if (sequence < 0 || sequence > 0 && !UpdateManifestCodec.Hash(manifestSha256) || failedBuilds is null || failedBuilds.Count > 128)
            throw new UpdateException("UPDATE_STATE_INVALID");
        return Change(programDirectory, dataDirectory, new(sequence, failedBuilds.ToArray(), manifestSha256 ?? "", null, trustedUtc));
    }

    public static DateTimeOffset VerificationTime(UpdateSecurityLedgerState state, DateTimeOffset now)
    {
        if (state.LastTrustedUtc != DateTimeOffset.MinValue && now < state.LastTrustedUtc.AddMinutes(-5))
            throw new UpdateException("UPDATE_CLOCK_ROLLBACK");
        // Even an allowed small clock correction cannot make expired metadata valid again.
        return now < state.LastTrustedUtc ? state.LastTrustedUtc : now;
    }

    public static void RequireMinimumVersion(UpdateSecurityLedgerState state, string version)
    {
        if (!SemanticVersion.TryParse(version, out var current) || state.MinimumVersion is { } minimum &&
            current!.CompareTo(SemanticVersion.Parse(minimum)) < 0)
            throw new UpdateException("UPDATE_MINIMUM_SECURITY_VERSION");
    }

    private static UpdateSecurityLedgerState Change(string programDirectory, string? dataDirectory, UpdateSecurityLedgerState? addition, bool writeCurrentLedger = false)
    {
        string program = Full(programDirectory);
        UpdatePaths.EnsureNoReparsePoints(program);
        string derived = Full(PortableLayout.DataDirectory(program));
        string data = dataDirectory is null ? derived : Full(dataDirectory);
        // Host-only test/ordinary layouts may supply their existing Data location.
        // A portable marker always fixes the one real entry-owned Data root.
        if (File.Exists(Path.Combine(program, PortableLayout.MarkerName)) && !Same(data, derived))
            throw new UpdateException("UPDATE_SECURITY_DATA_ROOT_MISMATCH");
        string updates = Path.Combine(data, "Updates");
        UpdatePaths.EnsureNoReparsePoints(updates);
        Directory.CreateDirectory(updates);
        string canonical = Path.Combine(updates, "security-ledger.json"), initialized = Path.Combine(updates, ".security-ledger-initialized");
        using var ownership = Lock(Path.Combine(updates, ".security-ledger.lock"));
        UpdatePaths.EnsureNoReparsePoints(canonical); UpdatePaths.EnsureNoReparsePoints(initialized);
        if (File.Exists(initialized) && ReadText(initialized, 128) != Initialized)
            throw new UpdateException("UPDATE_SECURITY_LEDGER_INVALID");
        if (File.Exists(initialized) && !File.Exists(canonical))
            throw new UpdateException("UPDATE_SECURITY_LEDGER_MISSING");
        UpdateSecurityLedgerState merged = new(0, []);
        if (File.Exists(canonical))
        {
            var saved = Read<CanonicalLedger>(canonical, MaximumLedgerBytes);
            if (saved.SchemaVersion != 1) throw new UpdateException("UPDATE_SECURITY_LEDGER_INVALID");
            merged = Merge(merged, new(saved.HighestSequence, saved.FailedBuilds, saved.ManifestSha256, saved.MinimumVersion, saved.LastTrustedUtc));
        }
        string currentLedger = Path.Combine(program, ".autumnos-update", "security.json");
        if (File.Exists(currentLedger)) merged = Merge(merged, ReadLegacy(currentLedger));
        string policyPath = Path.Combine(updates, "state.json");
        if (File.Exists(policyPath))
        {
            var policy = Read<LegacyPolicy>(policyPath, 65536);
            if (policy.SchemaVersion != 1 || policy.HighestSequence < 0 || policy.HighestSequence > 0 && !UpdateManifestCodec.Hash(policy.HighestDigest) ||
                policy.FailedBuildIds is null || policy.FailedBuildIds.Length > 128 || policy.PinnedVersion is not null && !SemanticVersion.TryParse(policy.PinnedVersion, out _) ||
                policy.PendingManifestSha256 is not null && !UpdateManifestCodec.Hash(policy.PendingManifestSha256))
                throw new UpdateException("UPDATE_STATE_INVALID");
            merged = Merge(merged, new(policy.HighestSequence, policy.FailedBuildIds, policy.HighestDigest ?? "", null, policy.LastTrustedUtc));
        }
        string system = Path.Combine(data, "System");
        UpdatePaths.EnsureNoReparsePoints(system);
        int legacySources = 0;
        // A complete single-file entry may replace a previous ordinary-directory
        // entry. Its old ledger remains at the fixed parent of AutumnOS_Data.
        string entryRoot = Path.GetDirectoryName(data)!;
        if (!Same(entryRoot, program) && Path.GetFileName(data) == "AutumnOS_Data")
        {
            string ledger = Path.Combine(entryRoot, ".autumnos-update", "security.json");
            string journal = Path.Combine(entryRoot, ".autumnos-update", "journal.json");
            UpdatePaths.EnsureNoReparsePoints(ledger); UpdatePaths.EnsureNoReparsePoints(journal);
            if (File.Exists(ledger) || File.Exists(journal))
            {
                legacySources++;
                if (File.Exists(Path.Combine(entryRoot, PortableLayout.MarkerName)) || !Same(PortableLayout.DataDirectory(entryRoot), data))
                    throw new UpdateException("UPDATE_SECURITY_LEGACY_BINDING_INVALID");
                var receipt = Read<LegacyReceipt>(Path.Combine(entryRoot, "autumn.install.json"), 8 * 1024 * 1024);
                ValidateReceipt(receipt); ValidateLegacyJournal(entryRoot, receipt, journal);
                if (File.Exists(ledger)) merged = Merge(merged, ReadLegacy(ledger));
            }
        }
        if (Directory.Exists(system))
        {
            // Enumerate direct directories only. Never follow an arbitrary path from a record.
            foreach (string candidate in Directory.EnumerateDirectories(system))
            {
                if (Same(candidate, program) || !Path.GetFileName(candidate).StartsWith("Product", StringComparison.OrdinalIgnoreCase)) continue;
                UpdatePaths.EnsureNoReparsePoints(candidate);
                string ledger = Path.Combine(candidate, ".autumnos-update", "security.json");
                string journal = Path.Combine(candidate, ".autumnos-update", "journal.json");
                UpdatePaths.EnsureNoReparsePoints(ledger); UpdatePaths.EnsureNoReparsePoints(journal);
                if (!File.Exists(ledger) && !File.Exists(journal)) continue;
                if (++legacySources > MaximumLegacySources) throw new UpdateException("UPDATE_SECURITY_LEGACY_SOURCES_LIMIT");
                ValidateLegacyRoot(candidate, data, journal);
                if (File.Exists(ledger)) merged = Merge(merged, ReadLegacy(ledger));
            }
        }
        if (addition is not null) merged = Merge(merged, addition);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(new CanonicalLedger(1, merged.HighestSequence, merged.FailedBuilds,
            merged.ManifestSha256, merged.MinimumVersion, merged.LastTrustedUtc), UpdateManifestCodec.JsonOptions);
        if (bytes.Length > MaximumLedgerBytes) throw new UpdateException("UPDATE_SECURITY_LEDGER_INVALID");
        if (!File.Exists(canonical) || !File.ReadAllBytes(canonical).AsSpan().SequenceEqual(bytes)) WriteAtomic(canonical, bytes);
        if (!File.Exists(initialized)) WriteAtomic(initialized, Encoding.UTF8.GetBytes(Initialized));
        if (writeCurrentLedger)
            WriteAtomic(currentLedger, JsonSerializer.SerializeToUtf8Bytes(new LegacyLedger(merged.HighestSequence, merged.FailedBuilds,
                merged.ManifestSha256, merged.MinimumVersion), UpdateManifestCodec.JsonOptions));
        return merged;
    }

    private static UpdateSecurityLedgerState Merge(UpdateSecurityLedgerState a, UpdateSecurityLedgerState b)
    {
        Validate(a); Validate(b);
        if (a.HighestSequence == b.HighestSequence && a.ManifestSha256.Length != 0 && b.ManifestSha256.Length != 0 && a.ManifestSha256 != b.ManifestSha256)
            throw new UpdateException("UPDATE_SECURITY_SEQUENCE_CONFLICT");
        long sequence = Math.Max(a.HighestSequence, b.HighestSequence);
        string digest = a.HighestSequence > b.HighestSequence ? a.ManifestSha256 : b.HighestSequence > a.HighestSequence ? b.ManifestSha256 :
            a.ManifestSha256.Length != 0 ? a.ManifestSha256 : b.ManifestSha256;
        string? minimum = a.MinimumVersion;
        if (b.MinimumVersion is not null && (minimum is null || SemanticVersion.Parse(b.MinimumVersion).CompareTo(SemanticVersion.Parse(minimum)) > 0)) minimum = b.MinimumVersion;
        string[] failed = a.FailedBuilds.Concat(b.FailedBuilds).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (failed.Length > MaximumFailedBuilds) throw new UpdateException("UPDATE_SECURITY_FAILED_BUILDS_LIMIT");
        return new(sequence, failed, digest, minimum, a.LastTrustedUtc > b.LastTrustedUtc ? a.LastTrustedUtc : b.LastTrustedUtc);
    }

    private static void Validate(UpdateSecurityLedgerState state)
    {
        if (state.HighestSequence < 0 || state.FailedBuilds is null || state.FailedBuilds.Length > MaximumFailedBuilds ||
            state.FailedBuilds.Any(id => !UpdateManifestCodec.Identifier(id)) || state.ManifestSha256 is null ||
            state.ManifestSha256.Length != 0 && !UpdateManifestCodec.Hash(state.ManifestSha256) ||
            state.HighestSequence == 0 && state.ManifestSha256.Length != 0 ||
            state.MinimumVersion is not null && !SemanticVersion.TryParse(state.MinimumVersion, out _) || state.LastTrustedUtc.Offset != TimeSpan.Zero)
            throw new UpdateException("UPDATE_SECURITY_LEDGER_INVALID");
    }

    private static UpdateSecurityLedgerState ReadLegacy(string path)
    {
        var old = Read<LegacyLedger>(path, MaximumLedgerBytes);
        return new(old.HighestSequence, old.FailedBuilds, old.ManifestSha256, old.MinimumVersion);
    }

    private static void ValidateLegacyRoot(string root, string data, string journalPath)
    {
        string seed = ReadText(Path.Combine(root, ".autumnos-single-file-seed"), 64);
        if (!UpdateManifestCodec.Hash(seed) || Path.GetFileName(root) != "Product-" + seed[..20] ||
            !File.Exists(Path.Combine(root, PortableLayout.MarkerName)) || !Same(PortableLayout.DataDirectory(root), data))
            throw new UpdateException("UPDATE_SECURITY_LEGACY_BINDING_INVALID");
        var receipt = Read<LegacyReceipt>(Path.Combine(root, "autumn.install.json"), 8 * 1024 * 1024);
        ValidateReceipt(receipt);
        string binding = Path.Combine(Path.GetDirectoryName(root)!, "Entry-" + seed[..20] + ".json");
        if (File.Exists(binding))
        {
            var entry = Read<LegacyEntry>(binding, 8192);
            if (entry.SchemaVersion != 1 || entry.SeedSha256 != seed || entry.ProductDirectory != Path.GetFileName(root) ||
                entry.BuildId != receipt.BuildId || entry.Version != receipt.Version)
                throw new UpdateException("UPDATE_SECURITY_LEGACY_BINDING_INVALID");
        }
        ValidateLegacyJournal(root, receipt, journalPath);
    }

    private static void ValidateLegacyJournal(string root, LegacyReceipt receipt, string journalPath)
    {
        if (!File.Exists(journalPath)) return;
        var journal = Read<LegacyJournal>(journalPath, 16 * 1024 * 1024);
        if (journal.SchemaVersion != 1 || string.IsNullOrWhiteSpace(journal.Root) || !Same(journal.Root, root) || !Guid.TryParseExact(journal.TransactionId, "N", out _) ||
            journal.Previous is null || journal.Previous.BuildId != journal.FromBuildId || !UpdateManifestCodec.Identifier(journal.ToBuildId) ||
            journal.NextFiles is null || journal.Touched is null)
            throw new UpdateException("UPDATE_SECURITY_LEGACY_BINDING_INVALID");
        ValidateReceipt(journal.Previous);
        if (journal.Phase is not ("committed" or "rolledBack" or "aborted"))
            throw new UpdateException("UPDATE_SECURITY_LEGACY_RECOVERY_REQUIRED");
        if (receipt.BuildId != (journal.Phase == "committed" ? journal.ToBuildId : journal.FromBuildId) ||
            journal.Phase == "committed" && !File.Exists(Path.Combine(root, ".autumnos-update", "security.json")))
            throw new UpdateException("UPDATE_SECURITY_LEGACY_BINDING_INVALID");
    }

    private static void ValidateReceipt(LegacyReceipt receipt)
    {
        if (receipt is null || receipt.SchemaVersion != 1 || receipt.Sequence < 0 || receipt.SecuritySequence is <= 0 || receipt.DataSchemaVersion != 1 || !UpdateManifestCodec.Identifier(receipt.BuildId) ||
            !SemanticVersion.TryParse(receipt.Version, out _) || receipt.Files is null || receipt.Files.Length is < 3 or > 20000)
            throw new UpdateException("UPDATE_SECURITY_LEGACY_BINDING_INVALID");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in receipt.Files)
        {
            if (file is null) throw new UpdateException("UPDATE_SECURITY_LEGACY_BINDING_INVALID");
            UpdatePaths.ValidateManagedRelativePath(file.Path);
            if (!seen.Add(file.Path) || file.Bytes < 0 || !UpdateManifestCodec.Hash(file.Sha256))
                throw new UpdateException("UPDATE_SECURITY_LEGACY_BINDING_INVALID");
        }
    }

    private static T Read<T>(string path, int maximum)
    {
        UpdatePaths.EnsureNoReparsePoints(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 2 || input.Length > maximum) throw new UpdateException("UPDATE_SECURITY_LEDGER_INVALID");
        byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
        return UpdateManifestCodec.ParseStrict<T>(bytes, maximum);
    }

    private static string ReadText(string path, int maximum)
    {
        UpdatePaths.EnsureNoReparsePoints(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > maximum) throw new UpdateException("UPDATE_SECURITY_LEGACY_BINDING_INVALID");
        using var reader = new StreamReader(input, new UTF8Encoding(false, true), false);
        return reader.ReadToEnd();
    }

    private static FileStream Lock(string path)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            UpdatePaths.EnsureNoReparsePoints(path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33)
            {
                if (clock.Elapsed >= TimeSpan.FromSeconds(2)) throw new UpdateException("UPDATE_SECURITY_LEDGER_BUSY", error);
                Thread.Sleep(25);
            }
        }
    }

    private static void WriteAtomic(string path, byte[] bytes)
    {
        UpdatePaths.EnsureNoReparsePoints(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
            { file.Write(bytes); file.Flush(true); }
            UpdatePaths.EnsureNoReparsePoints(path); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    private static bool Same(string a, string b) => string.Equals(Full(a), Full(b), StringComparison.OrdinalIgnoreCase);
    private sealed record CanonicalLedger(int SchemaVersion, long HighestSequence, string[] FailedBuilds, string ManifestSha256, string? MinimumVersion, DateTimeOffset LastTrustedUtc);
    private sealed record LegacyLedger(long HighestSequence, string[] FailedBuilds, string ManifestSha256 = "", string? MinimumVersion = null);
    private sealed record LegacyPolicy(int SchemaVersion, bool Preview, long HighestSequence, string? HighestDigest, DateTimeOffset LastTrustedUtc, string[] FailedBuildIds,
        string? PinnedVersion = null, DateTimeOffset? LastSuccessfulCheckUtc = null, DateTimeOffset? RetryAfterUtc = null, string? ReconciledTransactionId = null, string? PendingManifestSha256 = null, string? PendingFromBuildId = null);
    private sealed record LegacyFile(string Path, long Bytes, string Sha256);
    private sealed record LegacyReceipt(int SchemaVersion, string Version, string BuildId, LegacyFile[] Files, int DataSchemaVersion = 1, long Sequence = 0, long? SecuritySequence = null);
    private sealed record LegacyEntry(int SchemaVersion, string SeedSha256, string ProductDirectory, string BuildId, string Version);
    private sealed record LegacyJournal(int SchemaVersion, string TransactionId, string Root, string Phase, string FromBuildId, string ToBuildId,
        LegacyReceipt Previous, LegacyFile[] NextFiles, string[] Touched, string? ErrorCode = null, int ChildPid = 0, long ChildStartUtcTicks = 0,
        string? DataSnapshotId = null, bool PinAfterSuccess = false, string? TargetVersion = null, string? ManifestSha256 = null);
}
