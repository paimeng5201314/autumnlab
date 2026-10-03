using System.Diagnostics;
using System.Security.Principal;
using System.Text.Json;
using AutumnOS.Storage;

namespace AutumnOS.Tests;

public static class DesktopPreferencesTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("desktop_preferences.defaults_do_not_overwrite_or_persist", Defaults);
        yield return ("desktop_preferences.persist_and_reload_all_choices", Persistence);
        yield return ("desktop_preferences.repeating_choice_preserves_bytes", Idempotency);
        yield return ("desktop_preferences.invalid_values_do_not_change_state", InvalidValues);
        yield return ("desktop_preferences.corrupt_state_is_preserved", CorruptState);
        yield return ("desktop_preferences.future_schema_is_preserved", FutureSchema);
        yield return ("desktop_preferences.strict_schema_and_size_are_enforced", StrictSchema);
        yield return ("desktop_preferences.cancelled_write_preserves_commit", Cancellation);
        yield return ("desktop_preferences.stale_staging_is_never_promoted", StaleStaging);
        yield return ("desktop_preferences.concurrent_writer_is_retryable", BusyWriter);
        yield return ("desktop_preferences.readonly_destination_preserves_commit", ReadOnlyFile);
        yield return ("desktop_preferences.path_conflict_preserves_directory", PathConflict);
        yield return ("desktop_preferences.data_is_beside_entry_not_current_directory", EntryPath);
        yield return ("desktop_preferences.revision_overflow_preserves_commit", RevisionOverflow);
        if (OperatingSystem.IsWindows())
        {
            yield return ("desktop_preferences.windows_acl_denial_is_recoverable", WindowsAclDenial);
            yield return ("desktop_preferences.windows_config_junction_is_rejected", ConfigJunction);
            yield return ("desktop_preferences.windows_state_junction_is_rejected", StateJunction);
            yield return ("desktop_preferences.windows_lock_junction_is_rejected", LockJunction);
        }
    }

    private static void Defaults() => InTemp((root, _) =>
    {
        var store = new DesktopPreferencesStore(root);
        DesktopPreferencesResult loaded = store.Load();
        Assert(loaded.Success && loaded.State == DesktopPreferences.Default, "Missing preferences must select light/warm with revision zero.");
        Assert(!File.Exists(store.StateFilePath), "Loading defaults must not claim that settings were saved.");
    });

    private static void Persistence() => InTemp((root, _) =>
    {
        var store = new DesktopPreferencesStore(root);
        long revision = 0;
        foreach (string theme in new[] { "light", "dark", "system" })
        foreach (string wallpaper in new[] { "warm", "mist", "night" })
        {
            DesktopPreferencesResult saved = store.Save(theme, wallpaper);
            Assert(saved.Success && saved.State!.Theme == theme && saved.State.Wallpaper == wallpaper
                && saved.State.Revision == ++revision && saved.State.UpdatedUtc.Offset == TimeSpan.Zero, "Each choice must commit a complete versioned record.");
            var restarted = new DesktopPreferencesStore(new InstallationRoot(root.InstallationDirectory));
            Assert(restarted.Load().State == saved.State, "A fresh service must read the exact durable choice.");
        }
        Assert(!Directory.EnumerateFiles(root.Directories["Config"], ".desktop-preferences-*.tmp").Any(), "Successful commits must leave no staging files.");
    });

    private static void Idempotency() => InTemp((root, _) =>
    {
        var store = new DesktopPreferencesStore(root);
        DesktopPreferencesResult first = store.Save("dark", "night");
        Assert(first.Success, "Initial choice must commit.");
        byte[] original = File.ReadAllBytes(store.StateFilePath);
        Assert(store.Save("dark", "night").State == first.State, "Repeated identical choice must preserve revision and time.");
        Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(original), "Repeated identical choice must preserve exact bytes.");
    });

    private static void InvalidValues() => InTemp((root, _) =>
    {
        var store = new DesktopPreferencesStore(root);
        Assert(store.Save("light", "warm").Success, "Initial choice must commit.");
        byte[] original = File.ReadAllBytes(store.StateFilePath);
        foreach (string invalid in new[] { "", "DARK", " light", "custom", "../night", "https://example.invalid/wallpaper", "C:\\wallpaper.png" })
        {
            Assert(store.Save(invalid, "warm").ErrorCode == DesktopPreferencesStore.InvalidValue, "Unknown theme must be rejected.");
            Assert(store.Save("light", invalid).ErrorCode == DesktopPreferencesStore.InvalidValue, "Unknown or path-like wallpaper must be rejected.");
        }
        Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(original), "Invalid choices must preserve persisted state.");
    });

    private static void CorruptState() => RejectRecord("{broken", StorageErrors.CorruptState);
    private static void FutureSchema() => RejectRecord("{\"schemaVersion\":2,\"futureSetting\":\"preserve\"}", StorageErrors.FutureSchema);

    private static void StrictSchema()
    {
        string valid = JsonSerializer.Serialize(new { schemaVersion = 1, theme = "light", wallpaper = "warm", revision = 1, updatedUtc = "2026-10-01T00:00:00Z" });
        string[] invalid =
        [
            "", "{}", "[]", "null", "{\"schemaVersion\":\"1\"}", "{\"schemaVersion\":0}",
            valid.Replace("\"light\"", "\"Light\"", StringComparison.Ordinal),
            valid.Replace("\"warm\"", "\"../outside\"", StringComparison.Ordinal),
            valid.Replace("\"revision\":1", "\"revision\":0", StringComparison.Ordinal),
            valid.Replace("\"revision\":1", "\"revision\":1,\"revision\":2", StringComparison.Ordinal),
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":2", StringComparison.Ordinal),
            valid.Replace("\"theme\":\"light\",", "", StringComparison.Ordinal),
            valid.Replace("00:00:00Z", "08:00:00+08:00", StringComparison.Ordinal),
            valid.Replace("}", ",\"unknown\":true}", StringComparison.Ordinal),
            new string(' ', 17 * 1024)
        ];
        foreach (string json in invalid) RejectRecord(json, StorageErrors.CorruptState);
    }

    private static void RejectRecord(string json, string expectedCode) => InTemp((root, _) =>
    {
        Assert(root.EnsureCreated().Success, "Test data root must initialize.");
        var store = new DesktopPreferencesStore(root);
        File.WriteAllText(store.StateFilePath, json);
        byte[] original = File.ReadAllBytes(store.StateFilePath);
        DesktopPreferencesResult read = store.Load();
        Assert(!read.Success && read.State is null && read.ErrorCode == expectedCode, "Unreadable preferences must return an explicit error, never defaults.");
        Assert(store.Save("dark", "night").ErrorCode == expectedCode, "Saving must not overwrite invalid or unknown-version data.");
        Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(original), "Rejected configuration must remain byte-for-byte intact.");
    });

    private static void Cancellation() => InTemp((root, _) =>
    {
        var store = new DesktopPreferencesStore(root);
        Assert(store.Save("light", "warm").Success, "Initial choice must commit.");
        byte[] original = File.ReadAllBytes(store.StateFilePath);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert(store.Save("dark", "night", cancelled.Token).ErrorCode == StorageErrors.Cancelled, "Cancelled save must not report success.");
        Assert(store.Load(cancelled.Token).ErrorCode == StorageErrors.Cancelled, "Cancelled read must not proceed.");
        Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(original), "Cancellation must preserve the last commit.");
    });

    private static void StaleStaging() => InTemp((root, _) =>
    {
        var store = new DesktopPreferencesStore(root);
        Assert(store.Save("dark", "night").Success, "Initial choice must commit.");
        string orphan = Path.Combine(root.Directories["Config"], ".desktop-preferences-interrupted.tmp");
        File.WriteAllText(orphan, "{partial");
        Assert(new DesktopPreferencesStore(root).Load().State!.Wallpaper == "night", "Unknown staging files must never replace committed preferences.");
        Assert(store.Save("light", "mist").Success, "An orphan must not prevent a later valid commit.");
        Assert(File.ReadAllText(orphan) == "{partial", "An unrelated interrupted file must not be deleted automatically.");
    });

    private static void BusyWriter() => InTemp((root, _) =>
    {
        var store = new DesktopPreferencesStore(root);
        Assert(store.Save("light", "warm").Success, "Initial choice must commit.");
        byte[] original = File.ReadAllBytes(store.StateFilePath);
        using (new FileStream(store.LockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert(store.Save("dark", "night").ErrorCode == StorageErrors.Busy, "An active writer must produce a retryable busy error.");
            Assert(store.Load().State!.Wallpaper == "warm", "Readers must retain access to the last durable commit.");
        }
        Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(original), "Failed concurrent write must not modify bytes.");
        Assert(store.Save("dark", "night").Success, "Retry after lock release must succeed.");
    });

    private static void ReadOnlyFile() => InTemp((root, _) =>
    {
        var store = new DesktopPreferencesStore(root);
        Assert(store.Save("light", "warm").Success, "Initial choice must commit.");
        byte[] original = File.ReadAllBytes(store.StateFilePath);
        File.SetAttributes(store.StateFilePath, FileAttributes.ReadOnly);
        try
        {
            Assert(!store.Save("dark", "night").Success, "A protected record must not be overwritten.");
            Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(original), "Failed replacement must preserve the last commit.");
            Assert(!Directory.EnumerateFiles(root.Directories["Config"], ".desktop-preferences-*.tmp").Any(), "A failed save must clean its own staging file.");
        }
        finally { File.SetAttributes(store.StateFilePath, FileAttributes.Normal); }
    });

    private static void PathConflict() => InTemp((root, _) =>
    {
        Assert(root.EnsureCreated().Success, "Test root must initialize.");
        var store = new DesktopPreferencesStore(root);
        Directory.CreateDirectory(store.StateFilePath);
        Assert(store.Load().ErrorCode == StorageErrors.PathConflict && store.Save("dark", "night").ErrorCode == StorageErrors.PathConflict,
            "A directory at the state path must be rejected and preserved.");
        Assert(Directory.Exists(store.StateFilePath), "Conflicting directory must not be removed.");
    });

    private static void EntryPath() => InTemp((root, scope) =>
    {
        string original = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = scope;
            var store = new DesktopPreferencesStore(root);
            Assert(store.Save("system", "mist").Success, "Entry-relative preferences must save.");
            Assert(store.StateFilePath == Path.Combine(root.InstallationDirectory, "AutumnOS_Data", "Config", "desktop-preferences.json"), "Settings must remain beside the supplied entry.");
            Assert(!Directory.Exists(Path.Combine(scope, "AutumnOS_Data")), "Working directory must not receive application data.");
        }
        finally { Environment.CurrentDirectory = original; }
    });

    private static void RevisionOverflow() => InTemp((root, _) =>
    {
        Assert(root.EnsureCreated().Success, "Test root must initialize.");
        var store = new DesktopPreferencesStore(root);
        File.WriteAllText(store.StateFilePath, JsonSerializer.Serialize(new { schemaVersion = 1, theme = "light", wallpaper = "warm", revision = long.MaxValue, updatedUtc = DateTimeOffset.UtcNow }));
        byte[] original = File.ReadAllBytes(store.StateFilePath);
        Assert(store.Save("dark", "night").ErrorCode == StorageErrors.CorruptState, "Revision must never wrap around.");
        Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(original), "Overflow must preserve existing bytes.");
    });

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void WindowsAclDenial() => InTemp((root, _) =>
    {
        var store = new DesktopPreferencesStore(root);
        Assert(store.Save("light", "warm").Success, "Initial choice must commit.");
        byte[] original = File.ReadAllBytes(store.StateFilePath);
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Current user SID is unavailable.");
        string icacls = Path.Combine(Environment.SystemDirectory, "icacls.exe");
        RunProcess(icacls, [root.Directories["Config"], "/deny", $"*{sid}:(OI)(CI)(W)"]);
        try
        {
            Assert(store.Save("dark", "night").ErrorCode == StorageErrors.AccessDenied, "Real ACL denial must be recoverable.");
        }
        finally { RunProcess(icacls, [root.Directories["Config"], "/remove:d", $"*{sid}"]); }
        Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(original), "ACL-denied save must preserve committed data.");
        Assert(store.Save("dark", "night").Success, "Restored ACL must permit retry.");
    });

    private static void ConfigJunction() => RejectJunction("config");
    private static void StateJunction() => RejectJunction("state");
    private static void LockJunction() => RejectJunction("lock");

    private static void RejectJunction(string kind) => InTemp((root, scope) =>
    {
        Assert(root.EnsureCreated().Success, "Test root must initialize.");
        var store = new DesktopPreferencesStore(root);
        string link = kind switch { "config" => root.Directories["Config"], "state" => store.StateFilePath, _ => store.LockFilePath };
        if (kind == "config") Directory.Delete(link, recursive: false);
        string target = Path.Combine(scope, "outside-preferences");
        Directory.CreateDirectory(target);
        if (link.IndexOfAny(['"', '%', '\r', '\n']) >= 0 || target.IndexOfAny(['"', '%', '\r', '\n']) >= 0)
            throw new InvalidOperationException("Unsafe temporary test path.");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (Process process = Process.Start(start) ?? throw new InvalidOperationException("Junction test helper could not start."))
        {
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert(process.ExitCode == 0, "Junction must be created successfully before checking rejection.");
        }
        try
        {
            if (kind != "lock") Assert(store.Load().ErrorCode == StorageErrors.UnsafePath, "Loading a redirected path must fail.");
            Assert(store.Save("dark", "night").ErrorCode == StorageErrors.UnsafePath, "Saving through a redirected path must fail.");
            Assert(!Directory.EnumerateFileSystemEntries(target).Any(), "External link target must remain untouched.");
        }
        finally { Directory.Delete(link, recursive: false); }
    });

    private static void RunProcess(string executable, string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Test helper could not start.");
        process.StandardOutput.ReadToEnd();
        string error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert(process.ExitCode == 0, $"Test helper must complete successfully: {error}");
    }

    private static void InTemp(Action<InstallationRoot, string> action)
    {
        string tempParent = Path.GetFullPath(Path.GetTempPath());
        string scope = Path.Combine(tempParent, $"AutumnOS-preferences-tests-中文 空格-{Guid.NewGuid():N}");
        string entry = Path.Combine(scope, "entry");
        Directory.CreateDirectory(entry);
        try { action(new InstallationRoot(entry), scope); }
        finally
        {
            string canonical = Path.GetFullPath(scope);
            if (!canonical.StartsWith(Path.TrimEndingDirectorySeparator(tempParent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(canonical).StartsWith("AutumnOS-preferences-tests-中文 空格-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove an unverified temporary path.");
            Directory.Delete(canonical, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
