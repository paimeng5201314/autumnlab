using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AutumnOS.Packages;

namespace AutumnOS.Tests;

internal static class PackageDesktopTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("packages.t03_real_sample_declares_nine_permissions_purposes_and_desktop", RealSample);
        yield return ("packages.t03_inspection_is_read_only_and_hashes_actual_archive", InspectOnly);
        yield return ("packages.t03_desktop_sections_require_specific_permissions", UndeclaredSections);
        yield return ("packages.t03_purposes_require_declared_permission_and_bounded_text", Purposes);
        yield return ("packages.t03_duplicate_desktop_identifiers_are_rejected", DuplicateIds);
        yield return ("packages.t03_action_and_widget_identifiers_are_restricted", RestrictedIds);
        yield return ("packages.t03_shortcut_action_must_be_declared_link", ShortcutTarget);
        yield return ("packages.t03_desktop_collections_are_bounded", CollectionBounds);
        yield return ("packages.t03_desktop_titles_reject_controls_and_oversize", Titles);
        yield return ("packages.t03_desktop_shape_is_exact_and_unambiguous", ExactDesktop);
        yield return ("packages.t03_inspection_enforces_resource_path_and_executable_restrictions", ResourceSafety);
        yield return ("packages.t03_inspection_cancellation_preserves_archive", CancelInspection);
    }

    private static void RealSample() => InTemp(root =>
    {
        string workspace = Workspace(); string source = Path.Combine(workspace, "samples", "element-pairs");
        var content = Directory.EnumerateFiles(source).ToDictionary(path => Path.GetFileName(path), File.ReadAllBytes, StringComparer.Ordinal);
        content.Add("autumn-sdk.js", File.ReadAllBytes(Path.Combine(workspace, "sdk", "autumn-sdk.js")));
        var inspected = PackageInstaller.Inspect(Package(root, content));
        Assert(inspected.Manifest.AppId == "cn.labchronicles.elementpairs", "The actual shipped sample manifest was read.");
        string[] expected = ["saves", "identity.profile", "storage", "files.open", "files.save", "notifications", "shortcuts", "widgets", "links"];
        Assert(inspected.Manifest.Permissions.Order().SequenceEqual(expected.Order()), "All nine requested permission families remain declared.");
        Assert(inspected.Manifest.PermissionPurposes is not null && inspected.Manifest.PermissionPurposes.Count == 9
            && expected.All(inspected.Manifest.PermissionPurposes.ContainsKey), "Every sample permission has an actual declared purpose.");
        var desktop = inspected.Manifest.Desktop ?? throw new InvalidOperationException("Missing real sample desktop declarations.");
        Assert(desktop.Links.Contains("resume") && desktop.Shortcuts.Any(item => item.Id == "resume" && item.Action == "resume")
            && desktop.Widgets.Any(item => item.Id == "progress"), "Actual sample extension IDs must match its runnable source.");
    });

    private static void InspectOnly() => InTemp(root =>
    {
        string path = Package(root, Manifest()); byte[] original = File.ReadAllBytes(path); DateTime timestamp = File.GetLastWriteTimeUtc(path);
        var inspection = PackageInstaller.Inspect(path);
        Assert(inspection.Sha256 == Convert.ToHexStringLower(SHA256.HashData(original)), "Inspection identifies real archive bytes.");
        Assert(File.ReadAllBytes(path).SequenceEqual(original) && File.GetLastWriteTimeUtc(path) == timestamp, "Inspection never rewrites the selected project/package.");
        Assert(Directory.EnumerateFileSystemEntries(root).Count() == 1, "Inspection creates no Apps, cache, configuration or staging directories.");
    });

    private static void UndeclaredSections()
    {
        foreach (string permission in new[] { "shortcuts", "widgets", "links" })
        {
            var manifest = Manifest(); var permissions = manifest["permissions"]!.AsArray();
            var remove = permissions.Single(item => item!.GetValue<string>() == permission); permissions.Remove(remove);
            manifest["permissionPurposes"]!.AsObject().Remove(permission);
            Reject(manifest, "PACKAGE_PERMISSION_UNSUPPORTED");
        }
    }

    private static void Purposes()
    {
        foreach (JsonNode? purpose in new JsonNode?[] { "", new string('p', 161), "not\nallowed", 3, null })
        {
            var manifest = Manifest(); manifest["permissionPurposes"]!["saves"] = purpose; Reject(manifest, "PACKAGE_MANIFEST_INVALID");
        }
        var foreign = Manifest(); foreign["permissionPurposes"]!["host.execute"] = "Run an arbitrary process"; Reject(foreign, "PACKAGE_MANIFEST_INVALID");
        var undeclared = Manifest(); undeclared["permissions"] = JsonNode.Parse("[\"shortcuts\",\"widgets\",\"links\"]"); Reject(undeclared, "PACKAGE_MANIFEST_INVALID");
    }

    private static void DuplicateIds()
    {
        foreach (string section in new[] { "shortcuts", "widgets", "links" })
        {
            var manifest = Manifest(); var values = manifest["desktop"]![section]!.AsArray(); values.Add(values[0]!.DeepClone());
            Reject(manifest, "PACKAGE_DESKTOP_INVALID");
        }
    }

    private static void RestrictedIds()
    {
        foreach (string invalid in new[] { "Resume", "resume/path", "..", "powershell.exe", "scheme:run", new string('a', 41), "has space", "bad\n" })
        {
            foreach (string section in new[] { "shortcuts", "widgets", "links" })
            {
                var manifest = Manifest();
                if (section == "links") manifest["desktop"]!["links"]![0] = invalid;
                else manifest["desktop"]![section]![0]!["id"] = invalid;
                Reject(manifest, "PACKAGE_DESKTOP_INVALID");
            }
        }
    }

    private static void ShortcutTarget()
    {
        var manifest = Manifest(); manifest["desktop"]!["shortcuts"]![0]!["action"] = "not_declared";
        Reject(manifest, "PACKAGE_DESKTOP_INVALID");
    }

    private static void CollectionBounds()
    {
        foreach (string section in new[] { "shortcuts", "widgets", "links" })
        {
            var manifest = Manifest(); var array = manifest["desktop"]![section]!.AsArray(); array.Clear();
            for (int index = 0; index < 5; index++)
                array.Add(section == "links" ? JsonValue.Create("link" + index)
                    : section == "widgets" ? new JsonObject { ["id"] = "widget" + index, ["title"] = "组件" }
                    : new JsonObject { ["id"] = "shortcut" + index, ["title"] = "操作", ["action"] = "resume" });
            Reject(manifest, "PACKAGE_DESKTOP_INVALID");
        }
    }

    private static void Titles()
    {
        foreach (string section in new[] { "shortcuts", "widgets" })
            foreach (string title in new[] { "", new string('a', 65), "unsafe\ncontrol" })
            { var manifest = Manifest(); manifest["desktop"]![section]![0]!["title"] = title; Reject(manifest, "PACKAGE_DESKTOP_INVALID"); }
    }

    private static void ExactDesktop()
    {
        var missing = Manifest(); missing["desktop"]!.AsObject().Remove("links"); Reject(missing, "PACKAGE_DESKTOP_INVALID");
        var extra = Manifest(); extra["desktop"]!["command"] = "run.exe"; Reject(extra, "PACKAGE_DESKTOP_INVALID");
        var action = Manifest(); action["desktop"]!["shortcuts"]![0]!["command"] = "run.exe"; Reject(action, "PACKAGE_DESKTOP_INVALID");
        InTemp(root =>
        {
            string json = Manifest().ToJsonString().Replace("\"action\":\"resume\"", "\"action\":\"resume\",\"action\":\"resume\"", StringComparison.Ordinal);
            Assert(json.Contains("\"action\":\"resume\",\"action\":\"resume\"", StringComparison.Ordinal), "Fixture contains a duplicate JSON property.");
            RejectFile(Package(root, new Dictionary<string, byte[]> { ["manifest.json"] = Encoding.UTF8.GetBytes(json), ["index.html"] = "<!doctype html>"u8.ToArray() }), "PACKAGE_DESKTOP_INVALID");
        });
    }

    private static void ResourceSafety()
    {
        foreach (string unsafePath in new[] { "../outside.js", "C:/secret.js", "assets\\escape.js", ".hidden.js", "CON.js", "assets/COM1.js" })
            InTemp(root => RejectFile(Package(root, new Dictionary<string, byte[]> { ["manifest.json"] = Encoding.UTF8.GetBytes(Manifest().ToJsonString()), ["index.html"] = "<!doctype html>"u8.ToArray(), [unsafePath] = "blocked"u8.ToArray() }), "PACKAGE_UNSAFE_PATH"));
        InTemp(root =>
        {
            RejectFile(Package(root, new Dictionary<string, byte[]> { ["manifest.json"] = Encoding.UTF8.GetBytes(Manifest().ToJsonString()), ["index.html"] = "<!doctype html>"u8.ToArray(), ["script.js"] = [(byte)'M', (byte)'Z', 0, 1] }), "PACKAGE_FILE_TYPE_FORBIDDEN");
            try { PackageInstaller.Inspect("relative.autumn"); } catch (PackageException error) { Assert(error.Code == "PACKAGE_UNSAFE_PATH", "Relative input fails safely."); return; }
            throw new InvalidOperationException("Relative inspection input must not be accepted.");
        });
    }

    private static void CancelInspection() => InTemp(root =>
    {
        string path = Package(root, Manifest()); byte[] original = File.ReadAllBytes(path);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { PackageInstaller.Inspect(path, cancellation.Token); }
        catch (OperationCanceledException) { Assert(File.ReadAllBytes(path).SequenceEqual(original), "Cancelled inspection preserves original archive."); return; }
        catch (PackageException error) when (error.Code == "PACKAGE_CANCELLED") { Assert(File.ReadAllBytes(path).SequenceEqual(original), "Cancelled inspection preserves original archive."); return; }
        throw new InvalidOperationException("Cancelled inspection must not report success.");
    });

    private static JsonObject Manifest() => JsonNode.Parse("""
        {"schemaVersion":1,"appId":"test.desktop","name":"真实桌面扩展测试","version":"0.3.0","runtime":"web","entry":"index.html",
         "permissions":["saves","shortcuts","widgets","links"],"permissionPurposes":{"saves":"保存测试进度"},
         "desktop":{"shortcuts":[{"id":"resume","title":"继续","action":"resume"}],"links":["resume"],"widgets":[{"id":"progress","title":"进度"}]}}
        """)!.AsObject();
    private static string Package(string root, JsonObject manifest) => Package(root, new Dictionary<string, byte[]>
    { ["manifest.json"] = Encoding.UTF8.GetBytes(manifest.ToJsonString()), ["index.html"] = "<!doctype html><title>Restricted test fixture</title>"u8.ToArray() });
    private static string Package(string root, IReadOnlyDictionary<string, byte[]> files)
    {
        string path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".autumn");
        using var output = new FileStream(path, FileMode.CreateNew); using var archive = new ZipArchive(output, ZipArchiveMode.Create);
        foreach (var pair in files) { using var stream = archive.CreateEntry(pair.Key, CompressionLevel.NoCompression).Open(); stream.Write(pair.Value); }
        return path;
    }
    private static void Reject(JsonObject manifest, string code) => InTemp(root => RejectFile(Package(root, manifest), code));
    private static void RejectFile(string path, string code)
    {
        byte[] original = File.ReadAllBytes(path);
        try { PackageInstaller.Inspect(path); }
        catch (PackageException error)
        {
            Assert(error.Code == code && error.Message == code, $"Expected safe {code}, got {error.Code}.");
            Assert(File.ReadAllBytes(path).SequenceEqual(original), "Rejected archive must remain byte-identical."); return;
        }
        throw new InvalidOperationException("Expected " + code);
    }
    private static string Workspace()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (string? path = Path.GetFullPath(start); path is not null; path = Path.GetDirectoryName(path))
                if (File.Exists(Path.Combine(path, "samples", "element-pairs", "manifest.json")) && File.Exists(Path.Combine(path, "AutumnOS.slnx"))) return path;
        throw new InvalidOperationException("Local sample source is required; a generated fixture cannot replace the real sample test.");
    }
    private static void InTemp(Action<string> action)
    {
        string parent = Path.GetFullPath(Path.GetTempPath()); string root = Path.Combine(parent, "AutumnOS-T03-package-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            string canonical = Path.GetFullPath(root);
            if (!canonical.StartsWith(Path.TrimEndingDirectorySeparator(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !Path.GetFileName(canonical).StartsWith("AutumnOS-T03-package-", StringComparison.Ordinal)) throw new InvalidOperationException("Unverified isolated fixture cleanup path.");
            Directory.Delete(canonical, true);
        }
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
