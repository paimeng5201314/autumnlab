using System.Diagnostics;
using System.Text.Json;
using AutumnOS.Storage;

namespace AutumnOS.Tests;

public static class DesktopLayoutTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("desktop_layout.defaults_do_not_persist", Defaults);
        yield return ("desktop_layout.save_reorder_and_reload", Persistence);
        yield return ("desktop_layout.identical_save_preserves_bytes", Idempotency);
        yield return ("desktop_layout.new_ids_append_and_hidden_ids_restore", HiddenOrder);
        yield return ("desktop_layout.full_capacity_ids_round_trip", Capacity);
        yield return ("desktop_layout.illegal_and_duplicate_ids_rejected", InvalidIds);
        yield return ("desktop_layout.resolver_enforces_capacity_and_uniqueness", InvalidResolve);
        yield return ("desktop_layout.corrupt_state_preserved", () => RejectRecord("{broken", StorageErrors.CorruptState));
        yield return ("desktop_layout.future_schema_preserved", () => RejectRecord("{\"schemaVersion\":2,\"future\":true}", StorageErrors.FutureSchema));
        yield return ("desktop_layout.strict_schema_and_size_enforced", StrictSchema);
        yield return ("desktop_layout.cancelled_save_preserves_bytes", Cancellation);
        yield return ("desktop_layout.concurrent_writer_is_retryable", BusyWriter);
        yield return ("desktop_layout.readonly_failure_preserves_commit", ReadOnlyFile);
        yield return ("desktop_layout.stale_staging_never_promoted", StaleStaging);
        yield return ("desktop_layout.state_path_conflict_is_preserved", PathConflict);
        yield return ("desktop_layout.revision_cannot_overflow", RevisionOverflow);
        yield return ("desktop_layout.save_cannot_change_appearance", AppearancePreserved);
        if (OperatingSystem.IsWindows())
        {
            yield return ("desktop_layout.windows_config_junction_rejected", () => RejectJunction("config"));
            yield return ("desktop_layout.windows_state_junction_rejected", () => RejectJunction("state"));
            yield return ("desktop_layout.windows_lock_junction_rejected", () => RejectJunction("lock"));
        }
    }

    private static readonly string[] Initial = ["system.settings", "cn.labchronicles.element-match", "system.store"];

    private static void Defaults() => InTemp(root =>
    {
        var store = new DesktopLayoutStore(root);
        DesktopLayoutResult result = store.Load();
        Assert(result.Success && result.State!.OrderedIds.Count == 0 && result.State.Revision == 0, "Missing state must return empty defaults.");
        Assert(!File.Exists(store.StateFilePath), "Load must not persist default layout.");
        Assert(store.StateFilePath == Path.Combine(root.InstallationDirectory, "AutumnOS_Data", "Config", "desktop-layout.json"), "Layout must be entry-relative.");
    });

    private static void Persistence() => InTemp(root =>
    {
        var store = new DesktopLayoutStore(root);
        var input = Initial.ToArray();
        DesktopLayoutResult first = store.Save(input);
        Assert(first.Success && first.State!.Revision == 1, "Initial order must commit.");
        input[0] = "mutated-after-call";
        Assert(first.State!.OrderedIds.SequenceEqual(Initial), "Saved model must own a copy of the caller's list.");
        string[] reordered = Initial.Reverse().ToArray();
        DesktopLayoutResult next = store.Save(reordered);
        Assert(next.Success && next.State!.Revision == 2 && next.State.UpdatedUtc.Offset == TimeSpan.Zero, "Reorder must advance revision.");
        DesktopLayout? loaded = new DesktopLayoutStore(new InstallationRoot(root.InstallationDirectory)).Load().State;
        Assert(loaded is not null && loaded.OrderedIds.SequenceEqual(reordered) && loaded.Revision == 2
            && loaded.UpdatedUtc == next.State!.UpdatedUtc, "New service instance must load exact committed order.");
        Assert(!Directory.EnumerateFiles(root.Directories["Config"], ".desktop-layout-*.tmp").Any(), "Commit must leave no staging files.");
    });

    private static void Idempotency() => InTemp(root =>
    {
        var store = new DesktopLayoutStore(root);
        DesktopLayoutResult first = store.Save(Initial);
        byte[] before = File.ReadAllBytes(store.StateFilePath);
        DesktopLayoutResult next = store.Save(Initial.ToArray());
        Assert(next.Success && next.State!.Revision == first.State!.Revision && next.State.UpdatedUtc == first.State.UpdatedUtc,
            "Identical order must not produce another revision or timestamp.");
        Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(before), "Identical order must preserve bytes.");
    });

    private static void HiddenOrder() => InTemp(root =>
    {
        var store = new DesktopLayoutStore(root);
        string[] original = ["system.settings", "system.developer", "sample.game"];
        string[] available = ["sample.game", "system.settings", "system.store"];
        IReadOnlyList<string> merged = DesktopLayoutStore.ResolveOrder(original, available);
        Assert(merged.SequenceEqual(["system.settings", "system.developer", "sample.game", "system.store"]), "New apps append; hidden developer order is retained.");
        Assert(merged.Where(available.Contains).SequenceEqual(["system.settings", "sample.game", "system.store"]), "Rendering can filter hidden IDs without deleting them.");
        Assert(store.Save(merged).Success, "Merged order must persist.");
        IReadOnlyList<string> restored = DesktopLayoutStore.ResolveOrder(new DesktopLayoutStore(root).Load().State!.OrderedIds,
            ["sample.game", "system.store", "system.settings", "system.developer"]);
        Assert(restored.SequenceEqual(merged), "Re-enabling developer app must restore original position, not append it.");
        Assert(original.SequenceEqual(["system.settings", "system.developer", "sample.game"]), "Resolver must not mutate source state.");
    });

    private static void Capacity() => InTemp(root =>
    {
        var store = new DesktopLayoutStore(root);
        string[] ids = Enumerable.Range(0, DesktopLayoutStore.MaximumIds)
            .Select(index => index.ToString("D4") + new string('a', DesktopLayoutStore.MaximumIdLength - 4)).ToArray();
        Assert(store.Save(ids).Success && store.Load().State!.OrderedIds.SequenceEqual(ids), "Maximum count and length must fit actual on-disk size limit.");
        Assert(store.Save(Array.Empty<string>()).Success && store.Load().State!.OrderedIds.Count == 0, "An empty complete order is valid.");
    });

    private static void InvalidIds() => InTemp(root =>
    {
        var store = new DesktopLayoutStore(root);
        Assert(store.Save(Initial).Success, "Initial layout must commit.");
        byte[] before = File.ReadAllBytes(store.StateFilePath);
        string[][] invalid =
        [
            [""], ["hello world"], ["system.设置"], ["../outside"], ["C:\\outside"], ["https://example.invalid"],
            ["a\nb"], ["a\0b"], [new string('x', 161)], ["same", "same"], [null!],
            Enumerable.Range(0, 513).Select(index => "app" + index).ToArray()
        ];
        foreach (string[] ids in invalid) Assert(store.Save(ids).ErrorCode == DesktopLayoutStore.InvalidValue, "Invalid identifiers must be rejected.");
        Assert(store.Save(null!).ErrorCode == DesktopLayoutStore.InvalidValue, "Null order must be rejected.");
        Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(before), "Invalid calls must preserve original bytes.");
    });

    private static void InvalidResolve()
    {
        ExpectArgument(() => DesktopLayoutStore.ResolveOrder(["duplicate", "duplicate"], []));
        ExpectArgument(() => DesktopLayoutStore.ResolveOrder([], ["bad/path"]));
        ExpectArgument(() => DesktopLayoutStore.ResolveOrder(Enumerable.Range(0, 512).Select(index => "app" + index).ToArray(), ["another"]));
        ExpectArgument(() => DesktopLayoutStore.ResolveOrder(null!, []));
    }

    private static void StrictSchema()
    {
        string valid = Record(1);
        string[] invalid =
        [
            "", "{}", "[]", "null", "{\"schemaVersion\":\"1\"}", "{\"schemaVersion\":0}",
            valid.Replace("\"revision\":1", "\"revision\":0", StringComparison.Ordinal),
            valid.Replace("\"revision\":1", "\"revision\":1,\"revision\":2", StringComparison.Ordinal),
            valid.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":2", StringComparison.Ordinal),
            valid.Replace("[\"app.one\"]", "[\"app.one\",\"app.one\"]", StringComparison.Ordinal),
            valid.Replace("[\"app.one\"]", "[1]", StringComparison.Ordinal),
            valid.Replace("[\"app.one\"]", "[null]", StringComparison.Ordinal),
            valid.Replace("[\"app.one\"]", "[\"bad/path\"]", StringComparison.Ordinal),
            valid.Replace("[\"app.one\"]", JsonSerializer.Serialize(Enumerable.Range(0, 513).Select(index => "app" + index)), StringComparison.Ordinal),
            valid.Replace("00:00:00Z", "08:00:00+08:00", StringComparison.Ordinal),
            valid.Replace("}", ",\"unknown\":true}", StringComparison.Ordinal), new string(' ', 129 * 1024)
        ];
        foreach (string json in invalid) RejectRecord(json, StorageErrors.CorruptState);
    }

    private static string Record(long revision) => JsonSerializer.Serialize(new
    {
        schemaVersion = 1, orderedIds = new[] { "app.one" }, revision, updatedUtc = "2026-10-02T00:00:00Z"
    });

    private static void RejectRecord(string json, string code) => InTemp(root =>
    {
        Assert(root.EnsureCreated().Success, "Test root must initialize.");
        var store = new DesktopLayoutStore(root);
        File.WriteAllText(store.StateFilePath, json);
        byte[] before = File.ReadAllBytes(store.StateFilePath);
        Assert(store.Load().ErrorCode == code && store.Save(Initial).ErrorCode == code, "Unreadable state must never be reset to defaults or replaced.");
        Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(before), "Unreadable state must remain intact.");
    });

    private static void Cancellation() => InTemp(root =>
    {
        var store = new DesktopLayoutStore(root);
        Assert(store.Save(Initial).Success, "Initial order must commit.");
        byte[] before = File.ReadAllBytes(store.StateFilePath);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert(store.Save(Initial.Reverse().ToArray(), cancelled.Token).ErrorCode == StorageErrors.Cancelled, "Cancelled save must fail.");
        Assert(store.Load(cancelled.Token).ErrorCode == StorageErrors.Cancelled, "Cancelled read must fail.");
        Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(before), "Cancellation must preserve prior bytes.");
    });

    private static void BusyWriter() => InTemp(root =>
    {
        var store = new DesktopLayoutStore(root);
        Assert(store.Save(Initial).Success, "Initial order must commit.");
        byte[] before = File.ReadAllBytes(store.StateFilePath);
        using (new FileStream(store.LockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert(new DesktopLayoutStore(root).Save(Initial.Reverse().ToArray()).ErrorCode == StorageErrors.Busy, "Concurrent writer must receive busy.");
            Assert(store.Load().State!.OrderedIds.SequenceEqual(Initial), "Read must see last atomic commit while writer owns lock.");
        }
        Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(before), "Busy save must preserve bytes.");
        Assert(store.Save(Initial.Reverse().ToArray()).Success, "Retry after release must work.");
    });

    private static void ReadOnlyFile() => InTemp(root =>
    {
        var store = new DesktopLayoutStore(root);
        Assert(store.Save(Initial).Success, "Initial order must commit.");
        byte[] before = File.ReadAllBytes(store.StateFilePath);
        File.SetAttributes(store.StateFilePath, FileAttributes.ReadOnly);
        try
        {
            Assert(!store.Save(Initial.Reverse().ToArray()).Success, "Read-only target cannot be replaced.");
            Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(before), "Failed atomic replace must retain prior bytes.");
            Assert(!Directory.EnumerateFiles(root.Directories["Config"], ".desktop-layout-*.tmp").Any(), "Failed replace must clean own staging.");
        }
        finally { File.SetAttributes(store.StateFilePath, FileAttributes.Normal); }
    });

    private static void StaleStaging() => InTemp(root =>
    {
        var store = new DesktopLayoutStore(root);
        Assert(store.Save(Initial).Success, "Initial order must commit.");
        string orphan = Path.Combine(root.Directories["Config"], ".desktop-layout-interrupted.tmp");
        File.WriteAllText(orphan, "{partial");
        Assert(new DesktopLayoutStore(root).Load().State!.OrderedIds.SequenceEqual(Initial), "Partial staging must never replace durable state.");
        Assert(store.Save(Initial.Reverse().ToArray()).Success, "Later valid save must succeed.");
        Assert(File.ReadAllText(orphan) == "{partial", "Unrelated staging evidence must be retained.");
    });

    private static void PathConflict() => InTemp(root =>
    {
        Assert(root.EnsureCreated().Success, "Test root must initialize.");
        var store = new DesktopLayoutStore(root);
        Directory.CreateDirectory(store.StateFilePath);
        Assert(store.Load().ErrorCode == StorageErrors.PathConflict && store.Save(Initial).ErrorCode == StorageErrors.PathConflict,
            "A directory at state location must not be replaced.");
        Assert(Directory.Exists(store.StateFilePath), "Directory must survive rejection.");
    });

    private static void RevisionOverflow() => InTemp(root =>
    {
        Assert(root.EnsureCreated().Success, "Test root must initialize.");
        var store = new DesktopLayoutStore(root);
        File.WriteAllText(store.StateFilePath, Record(long.MaxValue));
        byte[] before = File.ReadAllBytes(store.StateFilePath);
        Assert(store.Save(Initial).ErrorCode == StorageErrors.CorruptState, "Revision must never overflow.");
        Assert(File.ReadAllBytes(store.StateFilePath).SequenceEqual(before), "Overflow must preserve durable state.");
    });

    private static void AppearancePreserved() => InTemp(root =>
    {
        var preferences = new DesktopPreferencesStore(root);
        Assert(preferences.Save("dark", "night").Success, "Existing appearance must persist.");
        byte[] before = File.ReadAllBytes(preferences.StateFilePath);
        var layout = new DesktopLayoutStore(root);
        Assert(layout.Save(Initial).Success && layout.Save(Initial.Reverse().ToArray()).Success, "Layout saves must succeed.");
        Assert(File.ReadAllBytes(preferences.StateFilePath).SequenceEqual(before), "Desktop order cannot modify appearance or its revision.");
    });

    private static void RejectJunction(string kind) => InTemp(root =>
    {
        Assert(root.EnsureCreated().Success, "Test root must initialize.");
        var store = new DesktopLayoutStore(root);
        string link = kind switch { "config" => root.Directories["Config"], "state" => store.StateFilePath, _ => store.LockFilePath };
        if (kind == "config") Directory.Delete(link, recursive: false);
        string target = Path.Combine(root.InstallationDirectory, "outside-layout");
        Directory.CreateDirectory(target);
        if (link.IndexOfAny(['"', '%', '\r', '\n']) >= 0 || target.IndexOfAny(['"', '%', '\r', '\n']) >= 0)
            throw new InvalidOperationException("Unsafe temporary junction test path.");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        {
            Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        using (Process process = Process.Start(start) ?? throw new InvalidOperationException("Junction helper could not start."))
        {
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert(process.ExitCode == 0, "Real junction must exist before negative test.");
        }
        try
        {
            if (kind != "lock") Assert(store.Load().ErrorCode == StorageErrors.UnsafePath, "Read cannot follow redirected path.");
            Assert(store.Save(Initial).ErrorCode == StorageErrors.UnsafePath, "Save cannot follow redirected path.");
            Assert(!Directory.EnumerateFileSystemEntries(target).Any(), "Outside link target must remain untouched.");
        }
        finally { Directory.Delete(link, recursive: false); }
    });

    private static void ExpectArgument(Action action)
    {
        try { action(); }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException("Invalid resolver input must throw ArgumentException.");
    }

    private static void InTemp(Action<InstallationRoot> action)
    {
        string tempParent = Path.GetFullPath(Path.GetTempPath());
        string scope = Path.Combine(tempParent, $"AutumnOS-layout-tests-中文 空格-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scope);
        try { action(new InstallationRoot(scope)); }
        finally
        {
            string canonical = Path.GetFullPath(scope);
            if (!canonical.StartsWith(Path.TrimEndingDirectorySeparator(tempParent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(canonical).StartsWith("AutumnOS-layout-tests-中文 空格-", StringComparison.Ordinal))
                throw new InvalidOperationException("Refusing to remove an unverified temporary path.");
            Directory.Delete(canonical, recursive: true);
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
