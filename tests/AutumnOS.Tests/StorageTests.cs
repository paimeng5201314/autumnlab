using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using AutumnOS.Storage;

namespace AutumnOS.Tests;

public static class StorageTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("storage.entry_path_uses_appcontext", CurrentProcessPath);
        yield return ("storage.rejects_relative_installation", RejectsRelativePath);
        yield return ("storage.creates_all_twelve_directories", CreatesDirectories);
        yield return ("storage.existing_data_and_probe_cleanup", PreservesExistingData);
        yield return ("storage.working_directory_does_not_select_data_root", IgnoresWorkingDirectory);
        yield return ("storage.directory_file_conflict_is_recoverable", FileConflict);
        yield return ("storage.cancelled_initialization_preserves_data", CancelledInitialization);
        yield return ("storage.first_run_resumes_and_finishes", FirstRunResume);
        yield return ("storage.first_run_enforces_order_and_idempotency", FirstRunTransitions);
        yield return ("storage.corrupt_json_never_overwritten", CorruptState);
        yield return ("storage.future_schema_never_overwritten", FutureSchema);
        yield return ("storage.invalid_records_never_overwritten", InvalidRecords);
        yield return ("storage.interrupted_staging_preserves_last_commit", InterruptedStaging);
        yield return ("storage.busy_checkpoint_is_retryable", BusyCheckpoint);
        yield return ("storage.readonly_checkpoint_preserves_bytes", ReadOnlyCheckpoint);
        yield return ("storage.diagnostics_accepts_only_known_fields", SafeDiagnostics);
        yield return ("storage.diagnostics_capacity_preserves_existing_log", BoundedDiagnostics);
        if (OperatingSystem.IsWindows())
        {
            yield return ("storage.windows_acl_write_denial_is_recoverable", WindowsWriteDenial);
            yield return ("storage.windows_root_junction_is_rejected", RootJunction);
            yield return ("storage.windows_child_junction_is_rejected", ChildJunction);
            yield return ("storage.windows_state_junction_is_rejected", StateJunction);
        }
    }

    private static void CurrentProcessPath()
    {
        InstallationRoot root = InstallationRoot.ForCurrentProcess();
        Assert(root.InstallationDirectory == Path.GetFullPath(AppContext.BaseDirectory), "Entry directory must come from AppContext.");
        Assert(root.DataDirectory == Path.Combine(AppContext.BaseDirectory, "AutumnOS_Data"), "Data must be adjacent to entry.");
        // Do not initialize here: tests must never create data beside the test runner or user's application.
    }

    private static void RejectsRelativePath()
    {
        try { _ = new InstallationRoot("relative"); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Relative installation paths must be rejected.");
    }

    private static void CreatesDirectories() => InTemp((root, _) =>
    {
        Assert(root.EnsureCreated().Success, "A writable temporary installation must initialize.");
        string[] expected = ["Apps", "Packages", "Downloads", "Cache", "AppData", "Saves", "Screenshots", "Config", "Logs", "Runtime", "Temp", "Updates"];
        Assert(root.Directories.Keys.Order().SequenceEqual(expected.Order()), "Exactly twelve required directories must be exposed.");
        Assert(expected.All(name => Directory.Exists(Path.Combine(root.DataDirectory, name))), "Required directories must exist.");
    });

    private static void PreservesExistingData() => InTemp((root, _) =>
    {
        Directory.CreateDirectory(Path.Combine(root.DataDirectory, "Saves"));
        string save = Path.Combine(root.DataDirectory, "Saves", "existing.bin");
        byte[] content = [0, 7, 255, 90];
        File.WriteAllBytes(save, content);
        Assert(root.EnsureCreated().Success && root.EnsureCreated().Success, "Repeated initialization must succeed.");
        Assert(File.ReadAllBytes(save).SequenceEqual(content), "Existing data must remain byte-for-byte intact.");
        Assert(!Directory.EnumerateFiles(root.DataDirectory, ".autumnos-write-probe-*", SearchOption.AllDirectories).Any(), "Write probes must be deleted.");
    });

    private static void IgnoresWorkingDirectory() => InTemp((root, scope) =>
    {
        string original = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = scope;
            Assert(root.EnsureCreated().Success, "The explicit entry root must initialize.");
            Assert(!Directory.Exists(Path.Combine(scope, "AutumnOS_Data")), "Working directory must never receive data.");
        }
        finally { Environment.CurrentDirectory = original; }
    });

    private static void FileConflict() => InTemp((root, _) =>
    {
        File.WriteAllText(root.DataDirectory, "preserve");
        DataRootResult result = root.EnsureCreated();
        Assert(!result.Success && result.ErrorCode == StorageErrors.PathConflict, "A file in place of the data directory must be recoverable.");
        Assert(File.ReadAllText(root.DataDirectory) == "preserve", "Conflicting file must not be removed.");
    });

    private static void CancelledInitialization() => InTemp((root, _) =>
    {
        Assert(root.EnsureCreated().Success, "Setup must initialize.");
        string save = Path.Combine(root.Directories["Saves"], "keep.dat");
        File.WriteAllText(save, "preserve");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert(root.EnsureCreated(cancelled.Token).ErrorCode == StorageErrors.Cancelled, "Cancellation must be explicit.");
        Assert(File.ReadAllText(save) == "preserve", "Cancellation must preserve existing data.");
        Assert(root.EnsureCreated().Success, "The next run must resume.");
    });

    private static void FirstRunResume() => InTemp((root, _) =>
    {
        var store = new FirstRunStateStore(root);
        FirstRunResult initial = store.Load();
        Assert(initial.Success && initial.State!.Checkpoint == FirstRunCheckpoint.Hello && initial.State.Revision == 0, "A fresh install must start at Hello.");
        Assert(!File.Exists(store.StateFilePath), "Read must not create a checkpoint file.");
        Assert(store.Advance(FirstRunCheckpoint.Hello).Success, "Hello checkpoint must persist.");
        Assert(store.Advance(FirstRunCheckpoint.Brand).Success, "Brand checkpoint must persist.");
        var resumed = new FirstRunStateStore(new InstallationRoot(root.InstallationDirectory));
        Assert(resumed.Load().State!.Checkpoint == FirstRunCheckpoint.Brand, "A new service instance must resume persisted progress.");
        Assert(resumed.Advance(FirstRunCheckpoint.Initializing).Success, "Initializing checkpoint must persist.");
        Assert(resumed.Advance(FirstRunCheckpoint.Completed).State!.IsComplete, "Completion must persist.");
        Assert(new FirstRunStateStore(root).Load().State!.IsComplete, "Completion must survive service restart.");
    });

    private static void FirstRunTransitions() => InTemp((root, _) =>
    {
        var store = new FirstRunStateStore(root);
        Assert(store.Advance(FirstRunCheckpoint.Completed).ErrorCode == StorageErrors.InvalidTransition, "Steps cannot be skipped.");
        FirstRunResult first = store.Advance(FirstRunCheckpoint.Hello);
        byte[] original = File.ReadAllBytes(store.StateFilePath);
        Assert(store.Advance(FirstRunCheckpoint.Hello).State == first.State, "Repeated checkpoints must be idempotent.");
        Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(original), "Idempotent calls must not rewrite state.");
        Assert(store.Advance(FirstRunCheckpoint.Brand).Success, "Next step must succeed.");
        Assert(store.Advance(FirstRunCheckpoint.Hello).ErrorCode == StorageErrors.InvalidTransition, "Completed progress cannot go backwards.");
        Assert(store.Advance((FirstRunCheckpoint)999).ErrorCode == StorageErrors.InvalidTransition, "Unknown checkpoints must fail.");
    });

    private static void CorruptState() => CheckRejectedState("{broken", StorageErrors.CorruptState);
    private static void FutureSchema() => CheckRejectedState("{\"schemaVersion\":999,\"futureData\":\"retain\"}", StorageErrors.FutureSchema);

    private static void InvalidRecords()
    {
        string[] invalid =
        [
            "{}", "[]", "null", "{\"schemaVersion\":1}",
            "{\"schemaVersion\":\"1\"}",
            "{\"schemaVersion\":0}",
            "{\"schemaVersion\":1,\"checkpoint\":\"2\",\"revision\":1,\"updatedUtc\":\"2026-01-01T00:00:00Z\"}",
            "{\"schemaVersion\":1,\"checkpoint\":\"Hello\",\"revision\":-1,\"updatedUtc\":\"2026-01-01T00:00:00Z\"}",
            "{\"schemaVersion\":1,\"checkpoint\":\"Hello\",\"revision\":1,\"revision\":2,\"updatedUtc\":\"2026-01-01T00:00:00Z\"}",
            "{\"schemaVersion\":1,\"checkpoint\":\"Hello\",\"revision\":1,\"updatedUtc\":\"2026-01-01T00:00:00Z\",\"extra\":true}",
            new string(' ', 17 * 1024)
        ];
        foreach (string json in invalid) CheckRejectedState(json, StorageErrors.CorruptState);
    }

    private static void CheckRejectedState(string json, string expectedError) => InTemp((root, _) =>
    {
        Assert(root.EnsureCreated().Success, "Setup must initialize.");
        var store = new FirstRunStateStore(root);
        File.WriteAllText(store.StateFilePath, json);
        byte[] original = File.ReadAllBytes(store.StateFilePath);
        Assert(store.Load().ErrorCode == expectedError, "Malformed or unsupported state must have the expected error.");
        Assert(store.Advance(FirstRunCheckpoint.Hello).ErrorCode == expectedError, "Saving must refuse unreadable state.");
        Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(original), "Rejected configuration must retain exact bytes.");
    });

    private static void InterruptedStaging() => InTemp((root, _) =>
    {
        var store = new FirstRunStateStore(root);
        Assert(store.Advance(FirstRunCheckpoint.Hello).Success, "Initial checkpoint must persist.");
        Assert(store.Advance(FirstRunCheckpoint.Brand).Success, "Last durable checkpoint must persist.");
        string orphan = Path.Combine(root.Directories["Config"], ".first-run-crash.tmp");
        File.WriteAllText(orphan, "{incomplete staging file");
        FirstRunResult loaded = new FirstRunStateStore(root).Load();
        Assert(loaded.Success && loaded.State!.Checkpoint == FirstRunCheckpoint.Brand, "Uncommitted staging files must never be promoted.");
        Assert(store.Advance(FirstRunCheckpoint.Initializing).Success, "Progress must resume from last commit.");
        Assert(File.ReadAllText(orphan) == "{incomplete staging file", "Unknown stale files must not be deleted automatically.");
    });

    private static void BusyCheckpoint() => InTemp((root, _) =>
    {
        var store = new FirstRunStateStore(root);
        Assert(store.Advance(FirstRunCheckpoint.Hello).Success, "Initial checkpoint must persist.");
        using (new FileStream(store.LockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert(store.Advance(FirstRunCheckpoint.Brand).ErrorCode == StorageErrors.Busy, "Concurrent writer must receive CONFIG_BUSY.");
        Assert(store.Advance(FirstRunCheckpoint.Brand).Success, "A released writer lock must allow retry.");
    });

    private static void ReadOnlyCheckpoint() => InTemp((root, _) =>
    {
        var store = new FirstRunStateStore(root);
        Assert(store.Advance(FirstRunCheckpoint.Hello).Success, "Initial checkpoint must persist.");
        byte[] original = File.ReadAllBytes(store.StateFilePath);
        File.SetAttributes(store.StateFilePath, File.GetAttributes(store.StateFilePath) | FileAttributes.ReadOnly);
        try
        {
            FirstRunResult result = store.Advance(FirstRunCheckpoint.Brand);
            Assert(!result.Success, "A protected checkpoint must not be replaced.");
            Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(original), "Failed atomic save must preserve original bytes.");
        }
        finally { File.SetAttributes(store.StateFilePath, FileAttributes.Normal); }
    });

    private static void SafeDiagnostics() => InTemp((root, _) =>
    {
        Assert(root.EnsureCreated().Success, "Setup must initialize.");
        var logger = new StructuredLog(root);
        Guid correlation = Guid.NewGuid();
        Assert(logger.Write(DiagnosticEvent.StartupDataReady, correlationId: correlation).Success, "Registered event must write.");
        string path = Path.Combine(root.Directories["Logs"], "diagnostics.jsonl");
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        Assert(json.RootElement.EnumerateObject().Select(item => item.Name).Order().SequenceEqual(new[] { "timestampUtc", "eventId", "errorCode", "correlationId" }.Order()), "Log shape must exclude arbitrary payloads.");
        Assert(json.RootElement.GetProperty("correlationId").GetGuid() == correlation, "Correlation must be preserved.");
        string before = File.ReadAllText(path);
        try { logger.Write(DiagnosticEvent.StartupDataFailed, "sensitive_token_payload"); }
        catch (ArgumentException)
        {
            Assert(File.ReadAllText(path) == before, "Rejected free text must never enter the log.");
            return;
        }
        throw new InvalidOperationException("Unknown error strings must be rejected.");
    });

    private static void BoundedDiagnostics() => InTemp((root, _) =>
    {
        Assert(root.EnsureCreated().Success, "Setup must initialize.");
        string path = Path.Combine(root.Directories["Logs"], "diagnostics.jsonl");
        byte[] full = new byte[1024 * 1024];
        File.WriteAllBytes(path, full);
        Assert(new StructuredLog(root).Write(DiagnosticEvent.ShellOpened).ErrorCode == StorageErrors.LogFull, "Capacity limit must be explicit.");
        Assert(File.ReadAllBytes(path).SequenceEqual(full), "Capacity rejection must preserve existing logs.");
    });

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WindowsWriteDenial() => InTemp((root, _) =>
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Windows user SID is unavailable.");
        string icacls = Path.Combine(Environment.SystemDirectory, "icacls.exe");
        RunProcess(icacls, [root.InstallationDirectory, "/deny", $"*{sid}:(OI)(CI)(W)"]);
        try
        {
            DataRootResult result = root.EnsureCreated();
            Assert(!result.Success && result.ErrorCode == StorageErrors.AccessDenied, "Real ACL write denial must be recoverable.");
            Assert(!Directory.Exists(root.DataDirectory), "Denied initialization must not redirect data elsewhere.");
        }
        finally { RunProcess(icacls, [root.InstallationDirectory, "/remove:d", $"*{sid}"]); }
        Assert(root.EnsureCreated().Success, "Restoring write access must permit retry.");
    });

    private static void RootJunction() => InTemp((root, scope) =>
    {
        string target = Path.Combine(scope, "outside-root");
        Directory.CreateDirectory(target);
        CreateJunction(root.DataDirectory, target);
        try
        {
            Assert(root.EnsureCreated().ErrorCode == StorageErrors.UnsafePath, "Data root junction must be rejected.");
            Assert(!Directory.EnumerateFileSystemEntries(target).Any(), "External target must remain untouched.");
        }
        finally { Directory.Delete(root.DataDirectory, recursive: false); }
    });

    private static void ChildJunction() => InTemp((root, scope) =>
    {
        Directory.CreateDirectory(root.DataDirectory);
        string target = Path.Combine(scope, "outside-child");
        Directory.CreateDirectory(target);
        string link = root.Directories["Saves"];
        CreateJunction(link, target);
        try
        {
            Assert(root.EnsureCreated().ErrorCode == StorageErrors.UnsafePath, "Managed child junction must be rejected.");
            Assert(!Directory.EnumerateFileSystemEntries(target).Any(), "External child target must remain untouched.");
        }
        finally { Directory.Delete(link, recursive: false); }
    });

    private static void StateJunction() => InTemp((root, scope) =>
    {
        Assert(root.EnsureCreated().Success, "Setup must initialize.");
        var store = new FirstRunStateStore(root);
        string target = Path.Combine(scope, "outside-state");
        Directory.CreateDirectory(target);
        CreateJunction(store.StateFilePath, target);
        try
        {
            Assert(store.Load().ErrorCode == StorageErrors.UnsafePath, "Checkpoint reparse point must be rejected.");
            Assert(store.Advance(FirstRunCheckpoint.Hello).ErrorCode == StorageErrors.UnsafePath, "Checkpoint write must reject redirection.");
            Assert(!Directory.EnumerateFileSystemEntries(target).Any(), "Checkpoint link target must remain untouched.");
        }
        finally { Directory.Delete(store.StateFilePath, recursive: false); }
    });

    private static void CreateJunction(string link, string target)
    {
        if (link.IndexOfAny(['"', '%', '\r', '\n']) >= 0 || target.IndexOfAny(['"', '%', '\r', '\n']) >= 0)
            throw new InvalidOperationException("Unsafe temporary test path.");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Junction test process could not start.");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert(process.ExitCode == 0, "Test junction creation must succeed before testing path rejection.");
    }

    private static void RunProcess(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Test helper could not start.");
        process.StandardOutput.ReadToEnd();
        process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert(process.ExitCode == 0, "Test helper must complete successfully.");
    }

    private static void InTemp(Action<InstallationRoot, string> action)
    {
        string tempParent = Path.GetFullPath(Path.GetTempPath());
        string scope = Path.Combine(tempParent, $"AutumnOS-tests-中文 空格-{Guid.NewGuid():N}");
        string entry = Path.Combine(scope, "entry");
        Directory.CreateDirectory(entry);
        try { action(new InstallationRoot(entry), scope); }
        finally
        {
            string canonical = Path.GetFullPath(scope);
            if (!canonical.StartsWith(Path.TrimEndingDirectorySeparator(tempParent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(canonical).StartsWith("AutumnOS-tests-中文 空格-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to delete an unverified temporary path.");
            Directory.Delete(canonical, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
