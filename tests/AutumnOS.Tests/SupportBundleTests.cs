using System.IO.Compression;
using System.Text.Json;
using AutumnOS.Storage;

namespace AutumnOS.Tests;

public static class SupportBundleTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("support_bundle.startup_failure_keeps_fixed_diagnostics_without_exception_text", () => InRoot((root, output) =>
        {
            File.WriteAllText(Path.Combine(root.Directories["Logs"], "runtime-events.jsonl"), JsonSerializer.Serialize(new
            { eventName = "startup_failed", profilePathLength = 201, startupFailure = new { stage = "environment", errorType = "COMException", hResult = "0x80070003", message = "SECRET_CONTENT", path = root.DataDirectory } }) + "\n");
            var result = new SupportBundleService(root, new()).Export(output);
            string text = Read(output);
            Require(result.Events == 1 && text.Contains("0x80070003") && text.Contains("environment") && text.Contains("201") && !text.Contains("SECRET_CONTENT") && !text.Contains(root.DataDirectory), "Startup failure projection lost diagnostics or leaked exception text.");
        }));
        yield return ("support_bundle.hostile_startup_details_are_omitted", () => InRoot((root, output) =>
        {
            File.WriteAllText(Path.Combine(root.Directories["Logs"], "runtime-events.jsonl"), "{\"eventName\":\"startup_failed\",\"profilePathLength\":\"SECRET\",\"startupFailure\":{\"stage\":\"SECRET_PATH\",\"errorType\":\"SECRET_TOKEN\",\"hResult\":\"SECRET_HRESULT\"}}\n" +
                "{\"eventName\":\"startup_failed\",\"profilePathLength\":40000,\"startupFailure\":{\"stage\":\"environment\",\"stage\":\"SECRET_DUPLICATE\"}}\n");
            var result = new SupportBundleService(root, new()).Export(output);
            string text = Read(output);
            Require(result.Events == 2 && !text.Contains("SECRET") && !text.Contains("40000") && !text.Contains("startupFailure"), "Hostile startup details leaked or prevented safe event export.");
        }));
        yield return ("support_bundle.projection_omits_secrets_paths_and_unknown_fields", () => InRoot((root, output) =>
        {
            string secret = "sensitive-fixture-do-not-export";
            File.WriteAllText(Path.Combine(root.Directories["Logs"], "diagnostics.jsonl"), JsonSerializer.Serialize(new
            { eventId = "StartupDataReady", errorCode = "NONE", correlationId = Guid.NewGuid(), timestampUtc = DateTimeOffset.UtcNow,
                token = secret, path = root.DataDirectory, message = secret, displayName = secret }) + "\n");
            File.WriteAllText(Path.Combine(root.Directories["Saves"], "private.json"), secret);
            File.WriteAllText(Path.Combine(root.Directories["Logs"], "identity-private.log"), secret);
            var result = new SupportBundleService(root, new()).Export(output);
            string text = Read(output);
            Require(result.Events == 1 && !text.Contains(secret) && !text.Contains(root.DataDirectory) && !text.Contains("private.json"), "Sensitive source was exported.");
            Require(File.ReadAllText(Path.Combine(root.Directories["Saves"], "private.json")) == secret, "Private data changed.");
            using ZipArchive zip = ZipFile.OpenRead(output);
            Require(zip.Entries.Select(e => e.FullName).Order().SequenceEqual(new[] { "events.json", "support.json" }), "Unexpected archive entry.");
        }));
        yield return ("support_bundle.corrupt_and_duplicate_json_never_export_raw_lines", () => InRoot((root, output) =>
        {
            File.WriteAllText(Path.Combine(root.Directories["Logs"], "diagnostics.jsonl"), "{secret}\n{\"eventId\":\"StartupDataReady\",\"eventId\":\"SECRET\"}\n{\"eventId\":\"SECRET\"}\n");
            var result = new SupportBundleService(root, new()).Export(output);
            Require(result.Events == 0 && result.OmittedRecords == 3 && !Read(output).Contains("SECRET"), "Malformed line leaked.");
        }));
        yield return ("support_bundle.retains_bounded_recent_projection_without_rotating_logs", () => InRoot((root, output) =>
        {
            string content = string.Concat(Enumerable.Repeat("{\"eventId\":\"ShellOpened\"}\n", 600));
            string log = Path.Combine(root.Directories["Logs"], "diagnostics.jsonl"); File.WriteAllText(log, content);
            var result = new SupportBundleService(root, new()).Export(output);
            Require(result.Events == 200 && result.OmittedRecords == 400 && File.ReadAllText(log) == content, "Unbounded or modified log.");
        }));
        yield return ("support_bundle.retains_recent_valid_records_across_rotations", () => InRoot((root, output) =>
        {
            Guid Id(int index) => new(index, 0, 0, new byte[8]);
            string Records(int first, int count) => "{malformed-secret}\n" + string.Concat(Enumerable.Range(first, count)
                .Select(index => JsonSerializer.Serialize(new { eventId = "ShellOpened", correlationId = Id(index), token = "SECRET_ROTATED_TOKEN", path = root.DataDirectory }) + "\n"));
            File.WriteAllText(Path.Combine(root.Directories["Logs"], "diagnostics.2.jsonl"), Records(0, 100));
            File.WriteAllText(Path.Combine(root.Directories["Logs"], "diagnostics.1.jsonl"), Records(100, 100));
            File.WriteAllText(Path.Combine(root.Directories["Logs"], "diagnostics.jsonl"), Records(200, 50));
            var result = new SupportBundleService(root, new()).Export(output);
            using JsonDocument events = ReadEntry(output, "events.json");
            var records = events.RootElement.EnumerateArray().ToArray();
            Require(result.Events == 200 && result.OmittedRecords == 53 && records.Select(item => item.GetProperty("correlationId").GetGuid()).SequenceEqual(Enumerable.Range(50, 200).Select(Id)),
                "Retention must select the latest 200 valid records across rotations and current log together.");
            string text = Read(output);
            Require(text.Contains("diagnostics.2.jsonl") && text.Contains("diagnostics.1.jsonl") && !text.Contains("SECRET_ROTATED_TOKEN") && !text.Contains("malformed-secret") && !text.Contains(root.DataDirectory),
                "Rotation metadata must be bounded and rotated sources must retain the privacy projection.");
        }));
        yield return ("support_bundle.includes_safe_process_log_failure_status", () => InRoot((root, output) =>
        {
            string conflict = Path.Combine(root.Directories["Logs"], "diagnostics.1.jsonl"); Directory.CreateDirectory(conflict);
            Require(!new StructuredLog(root).Write(DiagnosticEvent.ShellOpened).Success, "Fixture must fail a real write.");
            Directory.Delete(conflict);
            new SupportBundleService(root, new()).Export(output);
            using JsonDocument manifest = ReadEntry(output, "support.json");
            JsonElement status = manifest.RootElement.GetProperty("diagnosticLogStatus");
            Require(status.GetProperty("FailedWrites").GetInt64() == 1 && status.GetProperty("IsRecordingStopped").GetBoolean()
                && status.GetProperty("LastErrorCode").GetString() == StorageErrors.PathConflict && !Read(output).Contains(root.DataDirectory),
                "Support manifest must expose stopped logging without leaking paths or exception messages.");
        }));
        if (!OperatingSystem.IsWindows())
            yield return ("support_bundle.rejects_linked_rotation_sources", () => InRoot((root, output) =>
            {
                string outside = Path.Combine(Path.GetDirectoryName(output)!, "private.log"); File.WriteAllText(outside, "private-data");
                string link = Path.Combine(root.Directories["Logs"], "diagnostics.2.jsonl"); File.CreateSymbolicLink(link, outside);
                try
                {
                    Reject(() => new SupportBundleService(root, new()).Export(output));
                    Require(!File.Exists(output) && File.ReadAllText(outside) == "private-data", "Linked rotation source must not be read or changed.");
                }
                finally { File.Delete(link); }
            }));
        yield return ("support_bundle.oversized_log_is_omitted_not_copied", () => InRoot((root, output) =>
        {
            File.WriteAllText(Path.Combine(root.Directories["Logs"], "diagnostics.jsonl"), new string('x', SupportBundleService.MaximumInputBytes + 1));
            var result = new SupportBundleService(root, new()).Export(output);
            Require(result.Events == 0 && Read(output).Contains("omitted_size") && new FileInfo(output).Length < 8192, "Size bound missing.");
        }));
        yield return ("support_bundle.existing_output_is_never_overwritten", () => InRoot((root, output) =>
        {
            File.WriteAllText(output, "old bytes"); Reject(() => new SupportBundleService(root, new()).Export(output));
            Require(File.ReadAllText(output) == "old bytes", "Existing user file changed.");
        }));
        yield return ("support_bundle.cancellation_creates_no_output", () => InRoot((root, output) =>
        {
            try { new SupportBundleService(root, new()).Export(output, new CancellationToken(true)); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            Require(!File.Exists(output), "Cancelled operation created an output.");
        }));
        yield return ("support_bundle.maintenance_refuses_export_and_releases_write_lease", () => InRoot((root, output) =>
        {
            CriticalOperationCoordinator coordinator = new();
            using (var lease = coordinator.TryEnterMaintenance())
            {
                Require(lease is not null, "Could not obtain test maintenance.");
                try { new SupportBundleService(root, coordinator).Export(output); throw new InvalidOperationException("Maintenance bypassed."); }
                catch (DataStoreException) { }
            }
            Require(!File.Exists(output) && coordinator.ActiveWrites == 0, "Failed operation left a lease/output.");
            new SupportBundleService(root, coordinator).Export(output);
            Require(coordinator.ActiveWrites == 0, "Successful operation left a lease.");
        }));
        yield return ("support_bundle.busy_log_is_labeled_without_touching_its_owner", () => InRoot((root, output) =>
        {
            using FileStream owner = new(Path.Combine(root.Directories["Logs"], "diagnostics.jsonl"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            var result = new SupportBundleService(root, new()).Export(output);
            Require(result.Events == 0 && Read(output).Contains("omitted_busy_or_changed") && owner.CanWrite, "Busy source misreported.");
        }));
        yield return ("support_bundle.update_journal_projects_only_known_phase", () => InRoot((root, output) =>
        {
            string directory = Path.Combine(root.InstallationDirectory, ".autumnos-update"); Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "journal.json"), "{\"phase\":\"rolledBack\",\"root\":\"SECRET_ROOT\",\"errorCode\":\"SECRET_TOKEN\"}");
            new SupportBundleService(root, new()).Export(output);
            string content = Read(output);
            Require(content.Contains("rolledBack") && !content.Contains("SECRET_"), "Raw update journal leaked.");
        }));
    }
    private static string Read(string output)
    {
        using ZipArchive zip = ZipFile.OpenRead(output);
        return string.Join("\n", zip.Entries.Select(entry => { using StreamReader reader = new(entry.Open()); return reader.ReadToEnd(); }));
    }
    private static JsonDocument ReadEntry(string output, string name)
    {
        using ZipArchive zip = ZipFile.OpenRead(output);
        using Stream stream = (zip.GetEntry(name) ?? throw new InvalidOperationException("Missing archive entry.")).Open();
        return JsonDocument.Parse(stream);
    }
    private static void Reject(Action action) { try { action(); } catch (IOException) { return; } throw new InvalidOperationException("Expected refusal."); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void InRoot(Action<InstallationRoot, string> action)
    {
        string parent = Path.GetFullPath(Path.GetTempPath());
        string scope = Path.Combine(parent, "AutumnOS-support-tests-" + Guid.NewGuid().ToString("N"));
        string entry = Path.Combine(scope, "entry"); Directory.CreateDirectory(entry);
        InstallationRoot root = new(entry); Require(root.EnsureCreated().Success, "Test root unavailable.");
        try { action(root, Path.Combine(scope, "support.zip")); }
        finally
        {
            string full = Path.GetFullPath(scope);
            if (!full.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("AutumnOS-support-tests-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup.");
            Directory.Delete(full, true);
        }
    }
}
