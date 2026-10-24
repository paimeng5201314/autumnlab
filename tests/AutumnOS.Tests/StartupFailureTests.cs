using System.ComponentModel;
using System.Text.Json;
using AutumnOS.Startup;

namespace AutumnOS.Tests;

// These are error-handling and private temporary-file tests. They do not execute
// the launcher or count as Windows Smart App Control / GUI acceptance.
internal static class StartupFailureTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        foreach (var (native, expected) in new (int, string)[]
        {
            (577, "STARTUP_WINDOWS_POLICY_BLOCKED"),
            (1260, "STARTUP_WINDOWS_POLICY_BLOCKED"),
            (4551, "STARTUP_WINDOWS_POLICY_BLOCKED"),
            (5, "STARTUP_ACCESS_DENIED"),
            (2, "STARTUP_FILE_MISSING"),
            (3, "STARTUP_FILE_MISSING"),
            (126, "STARTUP_DEPENDENCY_MISSING"),
            (193, "STARTUP_BAD_IMAGE")
        })
            yield return ($"startup.native_error_{native}_has_distinct_classification", () =>
            {
                var exception = new Win32Exception(native, "synthetic message is not a classification input");
                var failure = Create(exception);
                Check(failure.Code == expected && failure.NativeErrorCode == native && failure.HResult == exception.HResult,
                    "Native error identity must survive classification without being guessed from message text.");
            });
        yield return ("startup.policy_not_inferred_from_exception_text_or_hresult", DoesNotGuessPolicy);
        yield return ("startup.only_exact_product_error_codes_are_retained", ExactProductCodes);
        yield return ("startup.report_contains_numeric_and_build_evidence_without_exception_secrets", ReportEvidence);
        yield return ("startup.arbitrary_outer_and_inner_exception_text_is_not_written", ExceptionPrivacy);
        yield return ("startup.repeated_reports_do_not_replace_reports_or_existing_saves", UniqueReports);
        yield return ("startup.report_write_failure_has_no_fictitious_report_path", WriteFailure);
        yield return ("startup.missing_data_root_reports_write_unavailable", EmptyDataRoot);
    }

    private static StartupFailure Create(Exception exception) => StartupFailure.Create(exception, "bootstrap", "client_start",
        "AutumnOS.Client.exe", "T06-synthetic-startup", "sha256:" + new string('a', 64));

    private static void DoesNotGuessPolicy()
    {
        foreach (var error in new Exception[]
        {
            new Win32Exception(12345, "Smart App Control policy blocked signature 4551"),
            new IOException("STARTUP_WINDOWS_POLICY_BLOCKED"),
            new IOException("signed application denied", unchecked((int)0x80070241)),
            new InvalidOperationException("ERROR_SYSTEM_INTEGRITY_POLICY_VIOLATION")
        })
        {
            var failure = Create(error);
            Check(failure.Code == "STARTUP_FAILED", "Text or an HRESULT alone must not become a verified Windows native error.");
            if (error is not Win32Exception)
                Check(failure.NativeErrorCode is null, "A managed exception must not invent a native error code.");
        }
    }

    private static void ExactProductCodes()
    {
        foreach (var code in new[] { "UPDATE_CLIENT_START_FAILED", "UPDATE_RECOVERY_REQUIRED", "PORTABLE_PAYLOAD_INVALID", "PORTABLE_LAYOUT_INVALID" })
        {
            Check(Create(new IOException(code)).Code == code, "A known product error must retain its exact identity.");
            Check(Create(new IOException(code + "\nprivate details")).Code == "STARTUP_FAILED", "A code prefix cannot expose trailing exception details.");
        }
        Check(Create(new IOException("UPDATE_BEARER_SYNTHETIC_SECRET")).Code == "STARTUP_FAILED", "An arbitrary UPDATE_ prefix is not a safe product code.");
        Check(Create(new InvalidOperationException("UPDATE_CLIENT_START_FAILED")).Code == "STARTUP_FAILED", "Only the intended exception category may supply product codes.");
    }

    private static void ReportEvidence() => InScope(root =>
    {
        const string secret = "owned-synthetic-token-do-not-report";
        const string privatePath = @"Z:\owned-fixture\private-account-file.json";
        var exception = new Win32Exception(4551, secret + " " + privatePath);
        var failure = Create(exception);
        string reportPath = failure.TryWrite(root);
        Check(reportPath.Length != 0 && File.Exists(reportPath), "The returned report must actually exist.");
        Check(Path.GetDirectoryName(reportPath) == Path.Combine(root, "Logs", "Startup"), "Failure reports must stay in the selected data root.");
        string json = File.ReadAllText(reportPath);
        using var report = JsonDocument.Parse(json);
        var record = report.RootElement;
        Check(record.GetProperty("SchemaVersion").GetInt32() == 1 &&
              record.GetProperty("NativeErrorCode").GetInt32() == 4551 &&
              record.GetProperty("HResult").GetInt32() == exception.HResult &&
              record.GetProperty("Code").GetString() == "STARTUP_WINDOWS_POLICY_BLOCKED",
            "The persisted JSON must retain the actual numeric error and diagnostic code.");
        Check(record.GetProperty("Role").GetString() == "bootstrap" && record.GetProperty("Stage").GetString() == "client_start" &&
              record.GetProperty("Target").GetString() == "AutumnOS.Client.exe" &&
              record.GetProperty("BuildId").GetString() == "T06-synthetic-startup" &&
              record.GetProperty("SourceSnapshotId").GetString() == "sha256:" + new string('a', 64) &&
              record.GetProperty("ExceptionType").GetString() == typeof(Win32Exception).FullName &&
              DateTimeOffset.TryParse(record.GetProperty("CreatedUtc").GetString(), out _),
            "The report must identify its stage, build, source and exception type without exception details.");
        string message = failure.Message(reportPath);
        Check(message.Contains(reportPath, StringComparison.Ordinal) && message.Contains("4551", StringComparison.Ordinal) &&
              message.Contains("Windows 安全策略", StringComparison.Ordinal) && !message.Contains(".autumnos-update", StringComparison.Ordinal),
            "The dialog must identify a real report and the policy error, not a nonexistent update report.");
        Check(!json.Contains(secret, StringComparison.Ordinal) && !json.Contains("private-account-file", StringComparison.Ordinal) &&
              !message.Contains(secret, StringComparison.Ordinal) && !message.Contains(privatePath, StringComparison.Ordinal),
            "Neither JSON nor the dialog may copy arbitrary error text or private paths.");
    });

    private static void ExceptionPrivacy() => InScope(root =>
    {
        const string secret = "SYNTHETIC_TOKEN_ACCOUNT_AND_ARGUMENTS";
        foreach (var error in new Exception[]
        {
            new IOException("UPDATE_" + secret),
            new Exception(secret, new Exception("inner-" + secret)),
            new Win32Exception(5, secret)
        })
        {
            var failure = Create(error);
            string path = failure.TryWrite(root);
            Check(path.Length != 0, "Each synthetic failure needs a real report as the privacy test's positive control.");
            string json = File.ReadAllText(path);
            using var parsed = JsonDocument.Parse(json);
            Check(!json.Contains(secret, StringComparison.Ordinal) && !failure.Message(path).Contains(secret, StringComparison.Ordinal),
                "Outer/inner exception messages must never be copied to the report or dialog.");
            Check(!parsed.RootElement.TryGetProperty("Message", out _) && !parsed.RootElement.TryGetProperty("StackTrace", out _),
                "Failure reports must not carry unbounded exception details.");
        }
    });

    private static void UniqueReports() => InScope(root =>
    {
        string save = Path.Combine(root, "Saves", "owned-fixture.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(save)!);
        byte[] saveBytes = [17, 29, 0, 255, 81];
        File.WriteAllBytes(save, saveBytes);
        var first = Create(new Win32Exception(577));
        string firstPath = first.TryWrite(root);
        Check(firstPath.Length != 0, "The first report must be written.");
        byte[] firstBytes = File.ReadAllBytes(firstPath);
        string secondPath = Create(new Win32Exception(5)).TryWrite(root);
        Check(secondPath.Length != 0 && secondPath != firstPath && File.Exists(secondPath), "Repeated failures must create separate reports.");
        Check(File.ReadAllBytes(firstPath).SequenceEqual(firstBytes) && File.ReadAllBytes(save).SequenceEqual(saveBytes),
            "Writing another diagnostic must not alter previous reports or existing synthetic saves.");
    });

    private static void WriteFailure() => InScope(root =>
    {
        string blocked = Path.Combine(root, "owned-file-not-directory");
        const string bytes = "preserve-owned-existing-file";
        File.WriteAllText(blocked, bytes);
        var failure = Create(new Win32Exception(4551));
        string path = failure.TryWrite(blocked);
        Check(path.Length == 0 && File.ReadAllText(blocked) == bytes, "An unwritable report destination must return no path and preserve the obstructing file.");
        string message = failure.Message(path);
        Check(message.Contains("未能写入诊断文件", StringComparison.Ordinal) && !message.Contains("诊断文件：", StringComparison.Ordinal) &&
              !message.Contains(".autumnos-update", StringComparison.Ordinal) && message.Contains("client_start", StringComparison.Ordinal),
            "Write failure must retain error context while honestly saying no report was written.");
        Check(Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Length == 0, "The failed write must not silently select another report location.");
    });

    private static void EmptyDataRoot()
    {
        var failure = Create(new InvalidOperationException("synthetic"));
        Check(failure.TryWrite("") == "" && failure.Message("").Contains("未能写入诊断文件", StringComparison.Ordinal),
            "An unavailable data root must not select the process working directory or claim a report exists.");
    }

    private static void InScope(Action<string> action)
    {
        string parent = Path.GetFullPath(Path.GetTempPath());
        string name = "AutumnOS-startup-failure-test-" + Guid.NewGuid().ToString("N");
        string root = Path.GetFullPath(Path.Combine(parent, name));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            // Cleanup only the exact directory allocated by this test; never resolve
            // a user-supplied location or recurse through a redirected root.
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(parent) ||
                Path.GetFileName(root) != name || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Synthetic test cleanup boundary changed.");
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
