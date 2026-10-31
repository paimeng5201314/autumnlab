using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutumnOS.SessionBrowserProtocol;

// Actual in-process Windows file sharing and durable IO. This is not a pure
// protocol fixture and performs no process, UI, LSA, ACL or clipboard actions.
internal static class OwnedReportFileSelfTests
{
    internal static void Append(Action<string, Action> Case, string reportPath)
    {
        if (!Path.IsPathFullyQualified(reportPath)) throw new InvalidDataException("ABSOLUTE_OWNED_REPORT_TEST_PATH_REQUIRED");
        string root = reportPath + ".owned-report-files";
        Case("owned_report_unblocked_replace_is_complete_and_consumes_pending", () =>
        {
            string target = NewTarget(root, "unblocked");
            int attempts = OwnedReportFile.SaveExisting(target, Payload(1));
            Require(attempts == 1 && Generation(ReadPath(target)) == 1 && !File.Exists(target + ".pending"));
            Observe(target, new { attempts, final_generation = 1, pending_exists = false });
        });
        Case("owned_report_ReadDelete_reader_keeps_old_bytes_and_new_open_sees_new_bytes", () =>
        {
            string target = NewTarget(root, "read-delete"); byte[] old = ReadPath(target);
            using var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            int attempts = OwnedReportFile.SaveExisting(target, Payload(1));
            byte[] fromHeld = ReadHeld(held), current = ReadPath(target);
            Require(attempts == 1 && fromHeld.SequenceEqual(old) && Generation(fromHeld) == 0 && Generation(current) == 1);
            Observe(target, new { attempts, held_generation = 0, new_open_generation = 1, old_sha256 = Hash(old), new_sha256 = Hash(current) });
        });
        Case("owned_report_noDelete_reader_released_after_200ms_proves_real_replace_retry", () =>
        {
            string target = NewTarget(root, "delayed-release");
            var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var ready = new ManualResetEventSlim();
            Task release = Task.Run(() => { ready.Set(); Thread.Sleep(200); held.Dispose(); });
            int attempts; var elapsed = Stopwatch.StartNew();
            try
            {
                Require(ready.Wait(TimeSpan.FromSeconds(5)));
                attempts = OwnedReportFile.SaveExisting(target, Payload(1));
            }
            finally { release.GetAwaiter().GetResult(); held.Dispose(); }
            Require(attempts > 1 && attempts <= 101 && Generation(ReadPath(target)) == 1 && !File.Exists(target + ".pending"));
            Observe(target, new { attempts, elapsed_ms = elapsed.ElapsedMilliseconds, delayed_release_ms = 200, final_generation = 1 });
        });
        Case("owned_report_unreleased_noDelete_reader_exhausts_budget_without_corrupting_old_or_pending", () =>
        {
            string target = NewTarget(root, "blocked-reader"); byte[] old = ReadPath(target);
            using var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read);
            var elapsed = Stopwatch.StartNew(); OwnedReportWriteException failure = Failure(() => OwnedReportFile.SaveExisting(target, Payload(1)));
            Require(failure.Stage == "replace" && failure.Attempts > 1 && failure.Attempts <= 101 && elapsed.ElapsedMilliseconds >= 1800 &&
                failure.HResult == failure.InnerHResult && RetryCode(failure.InnerHResult) && ReadPath(target).SequenceEqual(old) &&
                Generation(ReadPath(target + ".pending")) == 1);
            Observe(target, new { failure.Stage, failure.InnerType, failure.InnerHResult, failure.Attempts,
                elapsed_ms = elapsed.ElapsedMilliseconds, old_generation = 0, pending_generation = 1, old_sha256 = Hash(old) });
        });
        Case("owned_report_missing_target_never_creates_target_or_pending", () =>
        {
            string directory = NewDirectory(root, "missing-target"), target = Path.Combine(directory, "report.json");
            OwnedReportWriteException failure = Failure(() => OwnedReportFile.SaveExisting(target, Payload(1)));
            Require(failure.Stage == "replace" && failure.Attempts == 0 && failure.HResult == failure.InnerHResult &&
                !File.Exists(target) && !File.Exists(target + ".pending"));
            Observe(target, new { failure.Stage, failure.InnerType, failure.InnerHResult, failure.Attempts, target_exists = false, pending_exists = false });
        });
        Case("owned_report_real_serializer_cycle_failure_preserves_old_JSON_and_safe_error", () =>
        {
            string target = NewTarget(root, "serialization"); byte[] old = ReadPath(target); var value = new Cycle(); value.Next = value;
            OwnedReportWriteException failure = Failure(() => OwnedReportFile.SaveExisting(target, value));
            Require(failure.Stage == "serialize" && failure.InnerType == nameof(JsonException) && failure.Attempts == 0 &&
                failure.HResult == failure.InnerHResult && failure.InnerException is null && !failure.Message.Contains(target, StringComparison.OrdinalIgnoreCase) &&
                ReadPath(target).SequenceEqual(old) && Generation(ReadPath(target)) == 0 && File.Exists(target + ".pending"));
            Observe(target, new { failure.Stage, failure.InnerType, failure.InnerHResult, failure.Attempts, old_sha256 = Hash(old), pending_retained = true });
        });
        Case("owned_report_new_readonly_target_denial_is_not_hidden_and_original_attributes_are_restored", () =>
        {
            string target = NewTarget(root, "readonly-target"); byte[] old = ReadPath(target); FileAttributes original = File.GetAttributes(target);
            OwnedReportWriteException failure;
            try
            {
                // This path was created immediately above in this unique test
                // directory. No existing object ACL or attribute is changed.
                File.SetAttributes(target, original | FileAttributes.ReadOnly);
                failure = Failure(() => OwnedReportFile.SaveExisting(target, Payload(1)));
                Require(failure.Stage == "replace" && failure.Attempts >= 1 && failure.Attempts <= 101 && failure.HResult == failure.InnerHResult &&
                    ReadPath(target).SequenceEqual(old) && Generation(ReadPath(target + ".pending")) == 1);
            }
            finally { File.SetAttributes(target, original); }
            Require(File.GetAttributes(target) == original);
            Observe(target, new { failure.Stage, failure.InnerType, failure.InnerHResult, failure.Attempts, original_attributes_restored = true, old_sha256 = Hash(old) });
        });
        Case("owned_report_100_replacements_accept_only_complete_snapshots_and_record_unavailable", () =>
        {
            static int UnavailableCode(Exception error)
            {
                if (error is FileNotFoundException) return 2;
                if (error is DirectoryNotFoundException) return 3;
                uint hresult = unchecked((uint)error.HResult), code = hresult & 0xffffU;
                return (hresult & 0xffff0000U) == 0x80070000U && (code is 32 or 33) ? (int)code : 0;
            }
            string target = NewTarget(root, "hundred-generations");
            using var held = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            byte[] initial = ReadHeld(held); using var stop = new CancellationTokenSource(); using var ready = new ManualResetEventSlim();
            int snapshots = 0, readerAttempts = 0, unavailable2 = 0, unavailable3 = 0, unavailable32 = 0, unavailable33 = 0;
            Exception? readerFailure = null;
            Task reader = Task.Run(() =>
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref readerAttempts);
                        FileStream snapshot;
                        try
                        {
                            snapshot = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
                        }
                        catch (Exception error) when (UnavailableCode(error) != 0)
                        {
                            switch (UnavailableCode(error))
                            {
                                case 2: Interlocked.Increment(ref unavailable2); break;
                                case 3: Interlocked.Increment(ref unavailable3); break;
                                case 32: Interlocked.Increment(ref unavailable32); break;
                                case 33: Interlocked.Increment(ref unavailable33); break;
                            }
                            ready.Set(); Thread.Yield(); continue;
                        }
                        // Only an Open failure can be unavailable. Once Open
                        // succeeded, every Read/JSON/integrity error is fatal.
                        using (snapshot)
                        {
                            int generation = Generation(ReadHeld(snapshot)); Require(generation is >= 0 and <= 100);
                            Interlocked.Increment(ref snapshots);
                        }
                        ready.Set(); Thread.Yield();
                    }
                }
                catch (Exception error) { readerFailure = error; ready.Set(); }
            });
            int maximumAttempts = 0, completedReplacements = 0;
            try
            {
                Require(ready.Wait(TimeSpan.FromSeconds(5)));
                for (int generation = 1; generation <= 100; generation++)
                {
                    int attempts = OwnedReportFile.SaveExisting(target, Payload(generation)); maximumAttempts = Math.Max(maximumAttempts, attempts);
                    Require(Generation(ReadPath(target)) == generation && ReadHeld(held).SequenceEqual(initial));
                    completedReplacements++;
                }
            }
            finally { stop.Cancel(); reader.GetAwaiter().GetResult(); }
            byte[]? finalHeldBytes = null, finalPathBytes = null; int? finalHeldGeneration = null, finalPathGeneration = null;
            Exception? diagnosticFailure = null;
            try
            {
                finalHeldBytes = ReadHeld(held); finalHeldGeneration = Generation(finalHeldBytes);
                finalPathBytes = ReadPath(target); finalPathGeneration = Generation(finalPathBytes);
            }
            catch (Exception error) { diagnosticFailure = error; }
            int unavailable = unavailable2 + unavailable3 + unavailable32 + unavailable33;
            // A concurrent observation is either a verified complete generation
            // or explicitly unavailable. No unavailable sample is accepted.
            // Preserve diagnostics before asserting this observation contract.
            Observe(target, new { replacements = completedReplacements, verified_new_open_snapshots = completedReplacements,
                concurrent_reader_snapshots = snapshots, concurrent_reader_accepted = snapshots, concurrent_reader_attempts = readerAttempts, concurrent_reader_unavailable = unavailable,
                unavailable_by_code = new { error_2 = unavailable2, error_3 = unavailable3, error_32 = unavailable32, error_33 = unavailable33 },
                attempts_equal_accepted_plus_unavailable = readerAttempts == snapshots + unavailable,
                maximumAttempts, held_initial_generation = Generation(initial),
                held_initial_sha256 = Hash(initial), held_final_generation = finalHeldGeneration,
                held_final_sha256 = finalHeldBytes is null ? null : Hash(finalHeldBytes), held_initial_bytes = initial.Length, held_final_bytes = finalHeldBytes?.Length,
                held_final_matches_initial = finalHeldBytes?.SequenceEqual(initial), final_generation = finalPathGeneration,
                final_sha256 = finalPathBytes is null ? null : Hash(finalPathBytes), final_bytes = finalPathBytes?.Length,
                pending_exists = File.Exists(target + ".pending"), reader_failure_type = readerFailure?.GetType().Name,
                reader_failure_hresult = readerFailure?.HResult, reader_failure_fixed_message = SafeFixedMessage(readerFailure),
                diagnostic_failure_type = diagnosticFailure?.GetType().Name, diagnostic_failure_hresult = diagnosticFailure?.HResult,
                diagnostic_failure_fixed_message = SafeFixedMessage(diagnosticFailure) });
            Require(completedReplacements == 100 && readerFailure is null && snapshots > 0 && readerAttempts == snapshots + unavailable &&
                diagnosticFailure is null && finalHeldBytes is not null && finalHeldBytes.SequenceEqual(initial) && finalHeldGeneration == 0 && finalPathGeneration == 100 &&
                Generation(ReadPath(target)) == 100 && Generation(initial) == 0 && !File.Exists(target + ".pending"));
        });
    }

    private static object Payload(int generation) => new { generation, marker = "owned_report_synthetic", checksum = generation * 17 + 3 };
    private static string NewDirectory(string root, string label)
    {
        string directory = Path.Combine(root, label + "-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(directory) || File.Exists(directory)) throw new InvalidDataException("FRESH_OWNED_REPORT_FIXTURE_REQUIRED");
        Directory.CreateDirectory(directory); return directory;
    }
    private static string NewTarget(string root, string label)
    {
        string target = Path.Combine(NewDirectory(root, label), "report.json");
        using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, Payload(0)); file.Flush(true); return target;
    }
    private static byte[] ReadPath(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        return ReadHeld(file);
    }
    private static byte[] ReadHeld(FileStream file)
    {
        if (file.Length is < 2 or > 65536) throw new InvalidDataException("BOUNDED_OWNED_REPORT_FIXTURE_REQUIRED");
        file.Position = 0; byte[] bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes); return bytes;
    }
    private static int Generation(byte[] bytes)
    {
        using JsonDocument json = JsonDocument.Parse(bytes); JsonElement value = json.RootElement;
        int generation = value.GetProperty("generation").GetInt32();
        Require(value.GetProperty("marker").GetString() == "owned_report_synthetic" && value.GetProperty("checksum").GetInt32() == generation * 17 + 3);
        return generation;
    }
    private static void Observe(string target, object value)
    {
        using var file = new FileStream(Path.Combine(Path.GetDirectoryName(target)!, "observations.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true }); file.Flush(true);
    }
    private static OwnedReportWriteException Failure(Action action)
    {
        try { action(); } catch (OwnedReportWriteException error) { return error; }
        throw new InvalidDataException("ACTUAL_OWNED_REPORT_FAILURE_EXPECTED");
    }
    private static bool RetryCode(int hresult) => (unchecked((uint)hresult) & 0xffff0000U) == 0x80070000U && (hresult & 0xffff) is 5 or 32 or 33;
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static string? SafeFixedMessage(Exception? error)
    {
        if (error is null) return null;
        string message = error.Message;
        return message.Length is > 0 and <= 128 && message.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')
            ? message : "NON_FIXED_MESSAGE_OMITTED";
    }
    private static void Require(bool value) { if (!value) throw new InvalidDataException("OWNED_REPORT_FILE_SELF_TEST_ASSERTION_FAILED"); }
    private sealed class Cycle { public Cycle? Next { get; set; } }
}
