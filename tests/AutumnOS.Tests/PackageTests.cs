using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.Packages;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

internal static class PackageTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("packages.install_commits_verified_resources_and_record", Install);
        yield return ("packages.repeat_install_is_idempotent", IdempotentInstall);
        yield return ("packages.reject_same_version_different_archive", VersionConflict);
        yield return ("packages.modified_install_is_not_silently_repaired", TamperedInstall);
        yield return ("packages.unregistered_and_extra_files_are_rejected", ExtraFiles);
        yield return ("packages.install_record_must_be_unambiguous", DuplicateRecordFields);
        yield return ("packages.reject_non_autumn_extension_and_relative_paths", InvalidInputPaths);
        yield return ("packages.reject_invalid_archive_and_crc", InvalidArchive);
        yield return ("packages.reject_traversal_and_windows_device_paths", UnsafePaths);
        yield return ("packages.reject_duplicate_and_case_colliding_paths", ConflictingPaths);
        yield return ("packages.reject_links_and_nonregular_entries", LinkEntries);
        yield return ("packages.reject_native_executable_content", NativeFiles);
        yield return ("packages.reject_missing_or_ambiguous_manifest", InvalidManifests);
        yield return ("packages.app_identity_matches_runtime_contract", AppIdentity);
        yield return ("packages.semver_rejects_noncanonical_versions", Versions);
        yield return ("packages.unsupported_runtime_and_permissions_fail", UnsupportedCapabilities);
        yield return ("packages.entry_must_be_packaged_html", EntryValidation);
        yield return ("packages.archive_limits_prevent_resource_exhaustion", ArchiveLimits);
        yield return ("packages.busy_install_does_not_commit", BusyInstall);
        yield return ("packages.cancelled_install_does_not_create_state", CancelledInstall);
        yield return ("packages.file_conflicts_preserve_existing_data", DestinationConflict);
        yield return ("packages.install_leaves_saves_unchanged", SavesPreserved);
        if (OperatingSystem.IsWindows()) yield return ("packages.install_junction_cannot_escape_apps", AppsJunction);
    }

    private static void Install() => InTemp(root =>
    {
        string path = Package(root, Entries(entry: "pages/play.html"));
        InstalledPackage installed = PackageInstaller.Install(path, Apps(root));
        Assert(installed.Manifest.AppId == "test.game" && installed.Manifest.Entry == "pages/play.html", "Installed metadata must match the package manifest.");
        Assert(installed.DirectoryPath == Path.Combine(Apps(root), "test.game", "1.0.0"), "Install layout must use validated identity and version.");
        Assert(File.ReadAllText(Path.Combine(installed.DirectoryPath, "pages", "play.html")) == "<!doctype html><title>元素配对</title>", "The actual web content must be extracted intact.");
        Assert(installed.PackageSha256 == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(), "The archive hash must match actual bytes.");
        using JsonDocument record = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(installed.DirectoryPath, ".autumnos-install.json")));
        Assert(record.RootElement.GetProperty("files").GetArrayLength() == 2, "The commit record must enumerate all package files.");
        Assert(!Directory.EnumerateDirectories(Apps(root), ".staging-*", SearchOption.AllDirectories).Any(), "Successful installation must not leave staged directories.");
        var session = new RuntimeSession(installed.Manifest.AppId, installed.Manifest.Permissions, Path.Combine(root, "Saves"), installed.Manifest.Entry);
        Assert(session.PageUri.EndsWith("/pages/play.html", StringComparison.Ordinal), "Accepted packages must create a runtime session using their manifest entry.");
    });

    private static void IdempotentInstall() => InTemp(root =>
    {
        string path = Package(root, Entries());
        InstalledPackage first = PackageInstaller.Install(path, Apps(root));
        string record = Path.Combine(first.DirectoryPath, ".autumnos-install.json");
        byte[] before = File.ReadAllBytes(record);
        DateTime writeTime = File.GetLastWriteTimeUtc(record);
        InstalledPackage second = PackageInstaller.Install(path, Apps(root));
        Assert(first == second || first.DirectoryPath == second.DirectoryPath && first.PackageSha256 == second.PackageSha256, "Repeat installation must resolve the same commit.");
        Assert(File.ReadAllBytes(record).SequenceEqual(before) && File.GetLastWriteTimeUtc(record) == writeTime, "An idempotent install must not rewrite its record.");
    });

    private static void VersionConflict() => InTemp(root =>
    {
        InstalledPackage installed = PackageInstaller.Install(Package(root, Entries()), Apps(root));
        string html = Path.Combine(installed.DirectoryPath, "index.html"); byte[] before = File.ReadAllBytes(html);
        List<Entry> changed = Entries(); changed[1] = TextEntry("index.html", "<title>replacement</title>");
        Reject(Package(root, changed), Apps(root), "PACKAGE_VERSION_CONFLICT");
        Assert(File.ReadAllBytes(html).SequenceEqual(before), "A different archive cannot replace an existing version.");
    });

    private static void TamperedInstall() => InTemp(root =>
    {
        string package = Package(root, Entries());
        InstalledPackage installed = PackageInstaller.Install(package, Apps(root));
        string html = Path.Combine(installed.DirectoryPath, "index.html");
        File.WriteAllText(html, "tampered");
        Reject(package, Apps(root), "PACKAGE_INSTALL_CORRUPT");
        Assert(File.ReadAllText(html) == "tampered", "The installer must report corruption without silently overwriting local files.");
    });

    private static void ExtraFiles() => InTemp(root =>
    {
        string package = Package(root, Entries()); InstalledPackage installed = PackageInstaller.Install(package, Apps(root));
        string extra = Path.Combine(installed.DirectoryPath, "extra.js"); File.WriteAllText(extra, "retained");
        Reject(package, Apps(root), "PACKAGE_INSTALL_CORRUPT");
        Assert(File.ReadAllText(extra) == "retained", "Unknown files must not be deleted during validation.");
        File.Delete(extra);
        string record = Path.Combine(installed.DirectoryPath, ".autumnos-install.json"); File.Delete(record);
        Reject(package, Apps(root), "PACKAGE_INSTALL_CONFLICT");
    });

    private static void DuplicateRecordFields() => InTemp(root =>
    {
        string package = Package(root, Entries()); InstalledPackage installed = PackageInstaller.Install(package, Apps(root));
        string path = Path.Combine(installed.DirectoryPath, ".autumnos-install.json");
        string record = File.ReadAllText(path); File.WriteAllText(path, record.Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99, \"schemaVersion\": 1", StringComparison.Ordinal));
        byte[] before = File.ReadAllBytes(path);
        Reject(package, Apps(root), "PACKAGE_INSTALL_CORRUPT");
        Assert(File.ReadAllBytes(path).SequenceEqual(before), "An ambiguous installation record must be preserved for diagnosis.");
    });

    private static void InvalidInputPaths() => InTemp(root =>
    {
        string package = Package(root, Entries(), extension: ".zip");
        Reject(package, Apps(root), "PACKAGE_EXTENSION_INVALID");
        Reject("relative.autumn", Apps(root), "PACKAGE_UNSAFE_PATH");
        Reject(package, "relative", "PACKAGE_UNSAFE_PATH");
    });

    private static void InvalidArchive() => InTemp(root =>
    {
        string path = Path.Combine(root, "invalid.autumn"); File.WriteAllText(path, "not a zip archive");
        Reject(path, Apps(root), "PACKAGE_ARCHIVE_INVALID");
        path = Package(root, Entries());
        byte[] bytes = File.ReadAllBytes(path);
        byte[] marker = Encoding.UTF8.GetBytes("<!doctype html>");
        int position = Find(bytes, marker);
        Assert(position >= 0, "Stored fixture content must be identifiable before corrupting its CRC.");
        bytes[position] = (byte)'?'; File.WriteAllBytes(path, bytes);
        Reject(path, Apps(root), "PACKAGE_ARCHIVE_INVALID");
        Assert(!Directory.Exists(Apps(root)), "Invalid archives must be rejected before installation directories are created.");
    });

    private static void UnsafePaths()
    {
        foreach (string path in new[] { "../outside.js", "/absolute.js", "folder\\escape.js", "folder/../escape.js", "folder//empty.js", "C:/drive.js", "index.html:stream", ".hidden.js", "NUL.json", "folder/COM1.txt", "folder./file.js", "folder/space name.js", "folder/%2e%2e/file.js" })
            RejectEntries([.. Entries(), TextEntry(path, "content")], "PACKAGE_UNSAFE_PATH");
    }

    private static void ConflictingPaths()
    {
        RejectEntries([.. Entries(), TextEntry("index.html", "duplicate")], "PACKAGE_PATH_CONFLICT");
        RejectEntries([.. Entries(), TextEntry("INDEX.HTML", "case collision")], "PACKAGE_PATH_CONFLICT");
        RejectEntries([.. Entries(), TextEntry("assets/file.js", "a"), TextEntry("Assets/second.js", "b")], "PACKAGE_PATH_CONFLICT");
        RejectEntries([.. Entries(), TextEntry("assets.js", "a"), TextEntry("assets.js/child.js", "b")], "PACKAGE_PATH_CONFLICT");
        RejectEntries([.. Entries(), new Entry("folder/", [], 0), new Entry("folder/", [], 0)], "PACKAGE_PATH_CONFLICT");
    }

    private static void LinkEntries()
    {
        RejectEntries([.. Entries(), new Entry("link.js", Encoding.UTF8.GetBytes("index.html"), unchecked((int)0xA0000000))], "PACKAGE_LINK_FORBIDDEN");
        RejectEntries([.. Entries(), new Entry("link.js", [], (int)FileAttributes.ReparsePoint)], "PACKAGE_LINK_FORBIDDEN");
        RejectEntries([.. Entries(), new Entry("device.js", [], 0x20000000)], "PACKAGE_LINK_FORBIDDEN");
        RejectEntries([.. Entries(), new Entry("notdir.js", [], (int)FileAttributes.Directory)], "PACKAGE_LINK_FORBIDDEN");
    }

    private static void NativeFiles()
    {
        RejectEntries([.. Entries(), TextEntry("program.exe", "binary")], "PACKAGE_FILE_TYPE_FORBIDDEN");
        RejectEntries([.. Entries(), new Entry("hidden.js", [(byte)'M', (byte)'Z', 0, 1], 0)], "PACKAGE_FILE_TYPE_FORBIDDEN");
        RejectEntries([.. Entries(), new Entry("hidden.wasm", [0x7f, (byte)'E', (byte)'L', (byte)'F'], 0)], "PACKAGE_FILE_TYPE_FORBIDDEN");
    }

    private static void InvalidManifests()
    {
        RejectEntries([TextEntry("index.html", "content")], "PACKAGE_MANIFEST_INVALID");
        RejectEntries([TextEntry("Manifest.json", Manifest()), TextEntry("index.html", "content")], "PACKAGE_MANIFEST_INVALID");
        foreach (string manifest in new[] { "null", "[]", "{}", "{", Manifest().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal), Manifest().Replace("\"schemaVersion\":1", "\"extra\":true,\"schemaVersion\":1", StringComparison.Ordinal) })
            RejectEntries([TextEntry("manifest.json", manifest), TextEntry("index.html", "content")], "PACKAGE_MANIFEST_INVALID");
        RejectEntries([TextEntry("manifest.json", Manifest().Replace("\"schemaVersion\":1", "\"schemaVersion\":2", StringComparison.Ordinal)), TextEntry("index.html", "content")], "PACKAGE_SCHEMA_UNSUPPORTED");
    }

    private static void AppIdentity()
    {
        foreach (string appId in new[] { "game", "test..game", "test.1game", "con.game", "UPPER.game", new string('a', 64) + ".game", new string('a', 40) + "." + new string('b', 40) })
        {
            Assert(!RuntimeSession.IsValidAppId(appId), "The runtime must reject package identities that the installer rejects.");
            RejectEntries(Entries(manifest: Manifest(appId: appId)), "PACKAGE_APP_ID_INVALID");
        }
        foreach (string appId in new[] { "cn.labchronicles.element-match", "test.game", "x.y" }) InTemp(root =>
        {
            InstalledPackage installed = PackageInstaller.Install(Package(root, Entries(manifest: Manifest(appId: appId))), Apps(root));
            Assert(RuntimeSession.IsValidAppId(installed.Manifest.AppId), "Every accepted package identity must be launchable.");
        });
    }

    private static void Versions()
    {
        foreach (string version in new[] { "1", "01.0.0", "1.0.0.0", "v1.0.0", "1.0.0-01", "1.0.0-alpha..1", "1.0.0+", "1.0.0/../outside" })
            RejectEntries(Entries(manifest: Manifest(version: version)), "PACKAGE_VERSION_INVALID");
        InTemp(root => PackageInstaller.Install(Package(root, Entries(manifest: Manifest(version: "1.2.3-preview.2+local"))), Apps(root)));
    }

    private static void UnsupportedCapabilities()
    {
        RejectEntries(Entries(manifest: Manifest(runtime: "native")), "PACKAGE_RUNTIME_UNSUPPORTED");
        RejectEntries(Entries(manifest: Manifest(permissions: ["network"])), "PACKAGE_PERMISSION_UNSUPPORTED");
        RejectEntries(Entries(manifest: Manifest(permissions: ["saves", "saves"])), "PACKAGE_PERMISSION_UNSUPPORTED");
    }

    private static void EntryValidation()
    {
        RejectEntries(Entries(manifest: Manifest(entry: "missing.html")), "PACKAGE_ENTRY_MISSING");
        RejectEntries(Entries(manifest: Manifest(entry: "code.js")), "PACKAGE_ENTRY_INVALID");
        RejectEntries(Entries(manifest: Manifest(entry: "../index.html")), "PACKAGE_UNSAFE_PATH");
    }

    private static void ArchiveLimits()
    {
        RejectEntries([.. Entries(), new Entry("large.wasm", new byte[8 * 1024 * 1024 + 1], 0)], "PACKAGE_LIMIT_EXCEEDED");
        RejectEntries([.. Entries(), .. Enumerable.Range(0, 127).Select(index => TextEntry($"assets/file-{index}.js", "x"))], "PACKAGE_LIMIT_EXCEEDED");
        InTemp(root =>
        {
            string package = Package(root, [.. Entries(), new Entry("bomb.js", new byte[2 * 1024 * 1024], 0)], compression: CompressionLevel.SmallestSize);
            Reject(package, Apps(root), "PACKAGE_LIMIT_EXCEEDED");
            Assert(!Directory.Exists(Apps(root)), "Rejected compressed bombs must not create installation state.");
        });
    }

    private static void BusyInstall() => InTemp(root =>
    {
        string package = Package(root, Entries()); Directory.CreateDirectory(Apps(root));
        using (new FileStream(Path.Combine(Apps(root), ".autumnos-install.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
            Reject(package, Apps(root), "PACKAGE_INSTALL_BUSY");
        Assert(!Directory.Exists(Path.Combine(Apps(root), "test.game")), "A busy install must not begin staging an app.");
        PackageInstaller.Install(package, Apps(root));
    });

    private static void CancelledInstall() => InTemp(root =>
    {
        string package = Package(root, Entries()); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { PackageInstaller.Install(package, Apps(root), cancelled.Token); }
        catch (PackageException error)
        {
            Assert(error.Code == "PACKAGE_CANCELLED" && !Directory.Exists(Apps(root)), "Cancellation must be explicit and cannot create install state.");
            return;
        }
        throw new InvalidOperationException("Cancelled installation must fail.");
    });

    private static void DestinationConflict() => InTemp(root =>
    {
        string package = Package(root, Entries()); string app = Path.Combine(Apps(root), "test.game"); Directory.CreateDirectory(app);
        string destination = Path.Combine(app, "1.0.0"); File.WriteAllText(destination, "preserve");
        Reject(package, Apps(root), "PACKAGE_INSTALL_CONFLICT");
        Assert(File.ReadAllText(destination) == "preserve", "A conflicting file must be retained.");
    });

    private static void SavesPreserved() => InTemp(root =>
    {
        string save = Path.Combine(root, "Saves", "test.game", "guest", "game.json"); Directory.CreateDirectory(Path.GetDirectoryName(save)!);
        byte[] bytes = [0, 1, 255, 77]; File.WriteAllBytes(save, bytes);
        PackageInstaller.Install(Package(root, Entries()), Apps(root));
        PackageInstaller.Install(Package(root, Entries(manifest: Manifest(version: "2.0.0"))), Apps(root));
        Assert(File.ReadAllBytes(save).SequenceEqual(bytes), "Installing another version must not touch user saves.");
    });

    private static void AppsJunction() => InTemp(root =>
    {
        string package = Package(root, Entries()); string target = Path.Combine(root, "outside"); Directory.CreateDirectory(target);
        CreateJunction(Apps(root), target);
        try
        {
            Reject(package, Apps(root), "PACKAGE_LINK_FORBIDDEN");
            Assert(!Directory.EnumerateFileSystemEntries(target).Any(), "An Apps junction must not receive extraction writes.");
        }
        finally { Directory.Delete(Apps(root), recursive: false); }
    });

    private static string Manifest(string appId = "test.game", string version = "1.0.0", string runtime = "web", string entry = "index.html", string[]? permissions = null) =>
        JsonSerializer.Serialize(new { schemaVersion = 1, appId, name = "元素配对", version, runtime, entry, permissions = permissions ?? ["saves"] });
    private static List<Entry> Entries(string? manifest = null, string entry = "index.html") =>
        [TextEntry("manifest.json", manifest ?? Manifest(entry: entry)), TextEntry(entry, "<!doctype html><title>元素配对</title>")];
    private static Entry TextEntry(string path, string content) => new(path, Encoding.UTF8.GetBytes(content), 0);
    private static string Apps(string root) => Path.Combine(root, "Apps");

    private static string Package(string root, IEnumerable<Entry> entries, string extension = ".autumn", CompressionLevel compression = CompressionLevel.NoCompression)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + extension);
        using FileStream stream = new(path, FileMode.CreateNew);
        using ZipArchive archive = new(stream, ZipArchiveMode.Create);
        foreach (Entry source in entries)
        {
            ZipArchiveEntry entry = archive.CreateEntry(source.Path, compression); entry.ExternalAttributes = source.Attributes;
            using Stream output = entry.Open(); output.Write(source.Content);
        }
        return path;
    }

    private static void RejectEntries(IEnumerable<Entry> entries, string expected) => InTemp(root =>
    {
        Reject(Package(root, entries), Apps(root), expected);
        Assert(!Directory.Exists(Apps(root)), "An invalid package must be completely validated before install state is created.");
    });

    private static void Reject(string package, string apps, string expected)
    {
        try { PackageInstaller.Install(package, apps); }
        catch (PackageException error)
        {
            Assert(error.Code == expected, "Expected " + expected + " but got " + error.Code);
            Assert(error.Message == error.Code, "Package errors must not leak paths or attacker-controlled messages.");
            return;
        }
        throw new InvalidOperationException("Expected package rejection " + expected);
    }

    private static int Find(byte[] bytes, byte[] pattern)
    {
        for (int offset = 0; offset <= bytes.Length - pattern.Length; offset++)
            if (bytes.AsSpan(offset, pattern.Length).SequenceEqual(pattern)) return offset;
        return -1;
    }

    private static void CreateJunction(string link, string target)
    {
        if (link.IndexOfAny(['"', '%', '\r', '\n']) >= 0 || target.IndexOfAny(['"', '%', '\r', '\n']) >= 0) throw new InvalidOperationException("Unsafe temporary path.");
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
        { Arguments = $"/d /c mklink /J \"{link}\" \"{target}\"", UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Cannot create test junction.");
        process.StandardOutput.ReadToEnd(); process.StandardError.ReadToEnd(); process.WaitForExit();
        Assert(process.ExitCode == 0, "Test junction creation must succeed.");
    }

    private static void InTemp(Action<string> action)
    {
        string parent = Path.GetFullPath(Path.GetTempPath()); string root = Path.Combine(parent, "AutumnOS-package-tests-中文 空格-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            string canonical = Path.GetFullPath(root);
            if (!canonical.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(canonical).StartsWith("AutumnOS-package-tests-中文 空格-", StringComparison.Ordinal)) throw new InvalidOperationException("Unverified test cleanup path.");
            Directory.Delete(canonical, recursive: true);
        }
    }

    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed record Entry(string Path, byte[] Content, int Attributes);
}
