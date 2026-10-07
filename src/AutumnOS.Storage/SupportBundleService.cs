using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AutumnOS.Contracts;

namespace AutumnOS.Storage;

public sealed record SupportBundleResult(string Path, int Events, int OmittedRecords);

/// <summary>Host-only, allowlisted diagnostic projection. Never archives directories or original logs.</summary>
public sealed class SupportBundleService(InstallationRoot root, CriticalOperationCoordinator coordinator)
{
    public const int MaximumInputBytes = 1024 * 1024;
    public const int MaximumRecordsPerLog = 200;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly HashSet<string> LauncherEvents = new(StringComparer.Ordinal)
    {
        "primary_started", "window_ready", "forwarded", "recall_received", "recall_completed",
        "transport_unavailable", "closing", "primary_stopping", "request_rejected", "ready", "primary_acquired", "recall_handled", "stopping"
    };
    private static readonly HashSet<string> RuntimeEvents = new(StringComparer.Ordinal)
    { "starting", "started", "foreground", "background", "suspended", "resumed", "closing", "closed", "crashed", "process_failed", "resource_cleanup_pending", "resources_released",
      "installed", "navigation_completed", "navigation_failed", "navigation_completion_ignored", "new_window_denied", "download_denied", "external_protocol_denied", "navigation_denied", "frame_denied", "message_too_large", "response_not_delivered", "lifecycle_not_delivered", "resource_release_failed",
      "saves_write_ok", "saves_write_rejected", "saves_read_ok", "saves_read_rejected", "permissions_request_ok", "permissions_request_rejected",
      "profile_prepared", "environment_created", "controller_created", "startup_failed", "startup_cancelled" };
    private static readonly HashSet<string> Phases = new(StringComparer.Ordinal)
    { "prepared", "backedUp", "applying", "health", "committed", "rollingBack", "rolledBack", "aborted" };

    public SupportBundleResult Export(string outputPath, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(outputPath) || !outputPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("An absolute new ZIP path is required.", nameof(outputPath));
        string destination = Path.GetFullPath(outputPath);
        SafePath(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new IOException("SUPPORT_OUTPUT_EXISTS");
        if (!Directory.Exists(Path.GetDirectoryName(destination))) throw new DirectoryNotFoundException();
        cancellationToken.ThrowIfCancellationRequested();
        using IDisposable write = coordinator.EnterWrite("脱敏诊断导出");
        root.ValidateDirectories();
        List<Dictionary<string, object>> events = [];
        List<object> sources = [];
        int omitted = 0;
        foreach (string name in new[] { "diagnostics.jsonl", "launcher-events.jsonl", "runtime-events.jsonl" })
        {
            // The diagnostic writer and reader share a gate so a rotation cannot duplicate or skip a segment.
            lock (StructuredLog.SynchronizationGate) CollectSource(name);
        }

        void CollectSource(string name)
        {
            Queue<Dictionary<string, object>> recent = new();
            List<object> segments = [];
            int rejected = 0;
            bool projected = false;
            IEnumerable<int> generations = name == "diagnostics.jsonl"
                ? Enumerable.Range(0, StructuredLog.RetainedRotations + 1).Reverse() : [0];
            foreach (int generation in generations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string file = name == "diagnostics.jsonl" ? StructuredLog.FileName(generation) : name;
                string path = Path.Combine(root.Directories["Logs"], file);
                root.ValidateManagedFile(path); SafePath(path);
                if (!File.Exists(path)) { segments.Add(new { file, generation, status = "absent" }); continue; }
                try
                {
                    using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    if (input.Length > MaximumInputBytes) { segments.Add(new { file, generation, status = "omitted_size" }); continue; }
                    // Snapshot bounded bytes; never follow a growing stream or copy a raw source into the archive.
                    byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
                    using StringReader lines = new(Encoding.UTF8.GetString(bytes));
                    int valid = 0, invalid = 0;
                    for (string? line; (line = lines.ReadLine()) is not null;)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        Dictionary<string, object>? item = line.Length > 8192 ? null : Project(name, line);
                        if (item is null) { invalid++; rejected++; continue; }
                        valid++;
                        recent.Enqueue(item);
                        if (recent.Count > MaximumRecordsPerLog) { recent.Dequeue(); rejected++; }
                    }
                    projected = true;
                    segments.Add(new { file, generation, status = "projected", records = valid, omitted = invalid });
                }
                catch (IOException) { segments.Add(new { file, generation, status = "omitted_busy_or_changed" }); }
                catch (UnauthorizedAccessException) { segments.Add(new { file, generation, status = "omitted_access" }); }
            }
            events.AddRange(recent); omitted += rejected;
            sources.Add(new { source = name, status = projected ? "projected" : "unavailable", records = recent.Count, omitted = rejected, segments });
        }
        string updatePhase = "absent";
        string journal = Path.Combine(root.InstallationDirectory, ".autumnos-update", "journal.json");
        SafePath(journal);
        if (File.Exists(journal))
        {
            try
            {
                using FileStream input = new(journal, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (input.Length > 65536) updatePhase = "omitted_size";
                else
                {
                    byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
                    using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
                    string? phase = Text(document.RootElement, "phase");
                    updatePhase = phase is not null && Phases.Contains(phase) ? phase : "unknown";
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { updatePhase = "unreadable"; }
        }
        var manifest = new
        {
            schemaVersion = 1, product = BrandInfo.ProductName, producer = BrandInfo.ProducerCredit,
            version = BrandInfo.Version, buildId = BrandInfo.BuildId, sourceSnapshotId = BrandInfo.SourceSnapshotId,
            exportedUtc = DateTimeOffset.UtcNow, operatingSystemVersion = Environment.OSVersion.Version.ToString(),
            processArchitecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            updatePhase, sources,
            privacy = "Allowlisted event projection only. No original logs, paths, accounts, credentials, URLs, saves, app content or screenshots.",
            diagnosticLogStatus = new StructuredLog(root).Status,
            logPolicy = "Diagnostics retain the current file and two rotations of at most 1 MiB each. Launcher and runtime logs stop at 1 MiB. Export retains at most 200 valid events per source across all retained files. Diagnostic failure counts cover this process and installation root."
        };
        // Build the complete bounded archive before creating the destination; cancellation never overwrites another file.
        using MemoryStream buffer = new();
        using (ZipArchive zip = new(buffer, ZipArchiveMode.Create, true))
        {
            Write(zip, "support.json", JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions));
            Write(zip, "events.json", JsonSerializer.SerializeToUtf8Bytes(events, JsonOptions));
        }
        cancellationToken.ThrowIfCancellationRequested(); SafePath(destination);
        using (FileStream output = new(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        { buffer.Position = 0; buffer.CopyTo(output); output.Flush(true); }
        return new(destination, events.Count, omitted);
    }

    private static Dictionary<string, object>? Project(string source, string line)
    {
        try
        {
            using JsonDocument parsed = JsonDocument.Parse(line, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement value = parsed.RootElement;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1)) return null;
            string? text = Text(value, source == "diagnostics.jsonl" ? "eventId" : "eventName");
            bool known = source == "diagnostics.jsonl"
                ? Enum.TryParse(text, out DiagnosticEvent diagnostic) && Enum.IsDefined(diagnostic) && diagnostic.ToString() == text
                : text is not null && (source == "launcher-events.jsonl" ? LauncherEvents : RuntimeEvents).Contains(text);
            if (!known) return null;
            Dictionary<string, object> item = new() { ["source"] = source, ["event"] = text! };
            if (DateTimeOffset.TryParse(Text(value, "timestampUtc") ?? Text(value, "timestamp"), System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out DateTimeOffset time)) item["utc"] = time.ToUniversalTime();
            string? error = Text(value, "errorCode");
            if (error is not null && StorageErrors.IsKnown(error)) item["errorCode"] = error;
            if (Guid.TryParseExact(Text(value, "correlationId"), "D", out Guid correlation)) item["correlationId"] = correlation;
            string? state = Text(value, "state");
            if (Enum.TryParse(state, out AppLifecycleState lifecycle) && Enum.IsDefined(lifecycle) && lifecycle.ToString() == state) item["state"] = state!;
            if (value.TryGetProperty("blocksMaintenance", out JsonElement blocks) && blocks.ValueKind is JsonValueKind.True or JsonValueKind.False)
                item["blocksMaintenance"] = blocks.GetBoolean();
            if (source == "runtime-events.jsonl")
            {
                if (value.TryGetProperty("profilePathLength", out JsonElement length) && length.ValueKind == JsonValueKind.Number && length.TryGetInt32(out int characters) && characters is > 0 and <= 32767)
                    item["profilePathLength"] = characters;
                if (value.TryGetProperty("startupFailure", out JsonElement failure) && failure.ValueKind == JsonValueKind.Object &&
                    !failure.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1))
                {
                    Dictionary<string, object> detail = [];
                    string? stage = Text(failure, "stage"), type = Text(failure, "errorType"), hresult = Text(failure, "hResult");
                    if (stage is "package" or "session" or "resources" or "profile" or "environment" or "controller" or "settings" or "navigation") detail["stage"] = stage;
                    if (type is "OperationCanceledException" or "COMException" or "UnauthorizedAccessException" or "IOException" or "ArgumentException" or "Other") detail["errorType"] = type;
                    if (hresult is { Length: 10 } && hresult.StartsWith("0x", StringComparison.Ordinal) && hresult.AsSpan(2).IndexOfAnyExcept("0123456789ABCDEF") < 0) detail["hResult"] = hresult;
                    if (detail.Count != 0) item["startupFailure"] = detail;
                }
            }
            return item;
        }
        catch (JsonException) { return null; }
    }
    private static string? Text(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() : null;
    private static void Write(ZipArchive zip, string name, byte[] bytes) { using Stream stream = zip.CreateEntry(name, CompressionLevel.Optimal).Open(); stream.Write(bytes); }
    private static void SafePath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new StoragePathException(StorageErrors.UnsafePath); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
