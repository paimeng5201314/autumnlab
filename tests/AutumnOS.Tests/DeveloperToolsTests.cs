using System.Security.Cryptography;
using System.Text.Json;
using AutumnOS.Packages;
using AutumnOS.Storage;
using AutumnOS.Store;
using AutumnOS.Contracts;

namespace AutumnOS.Tests;

public static class DeveloperToolsTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        foreach (var test in T06Cases()) yield return test;
        yield return ("T03 developer: disabled mode rejects capabilities and cancellation revokes work", () => InProject((root, project) =>
        {
            using DeveloperToolsService tools = new();
            Reject(() => tools.BuildProject(project, Path.Combine(root, "output")), "DEVELOPER_MODE_DISABLED");
            tools.SetEnabled(true); CancellationToken capability = tools.CapabilityToken;
            tools.SetEnabled(false);
            Assert(capability.IsCancellationRequested && !tools.Enabled, "Mode disable did not revoke capabilities.");
        }));
        yield return ("T03 developer: project builds real independently inspectable packages without overwriting old output", () => InProject((root, project) =>
        {
            using DeveloperToolsService tools = Enabled();
            DeveloperBuild first = tools.BuildProject(project, Path.Combine(root, "output"));
            string original = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(first.PackagePath)));
            DeveloperBuild second = tools.BuildProject(project, Path.Combine(root, "output"));
            Assert(first.PackagePath != second.PackagePath && File.Exists(first.PackagePath) && original == first.Inspection.Sha256,
                "Build overwritten old package or hash incorrect.");
            Assert(PackageInstaller.Inspect(second.PackagePath).Manifest.AppId == "cn.labchronicles.developerfixture", "Built artifact not accepted by actual installer parser.");
        }));
        foreach ((string name, byte[] bytes, string code) in new[]
        {
            ("game.exe", new byte[] { 0x4d, 0x5a, 0, 0 }, "PACKAGE_FILE_TYPE_FORBIDDEN"),
            ("disguised.js", new byte[] { 0x4d, 0x5a, 0, 0 }, "PACKAGE_FILE_TYPE_FORBIDDEN"),
            (".env", System.Text.Encoding.UTF8.GetBytes("secret=fixture"), "DEVELOPER_PRIVATE_PATH_REJECTED")
        })
            yield return ($"T03 developer: rejects unsafe project entry {name}", () => InProject((root, project) =>
            {
                File.WriteAllBytes(Path.Combine(project, name), bytes);
                using DeveloperToolsService tools = Enabled();
                Reject(() => tools.BuildProject(project, Path.Combine(root, "output")), code);
                Assert(!Directory.Exists(Path.Combine(root, "output")) || !Directory.EnumerateFiles(Path.Combine(root, "output")).Any(), "Failed build left an executable package.");
            }));
        yield return ("T03 developer: permission declaration checked by existing package parser", () => InProject((root, project) =>
        {
            File.WriteAllText(Path.Combine(project, "manifest.json"), Manifest(["host.execute"]));
            using DeveloperToolsService tools = Enabled();
            Reject(() => tools.BuildProject(project, Path.Combine(root, "output")), "PACKAGE_PERMISSION_UNSUPPORTED");
        }));
        yield return ("T03 developer: output cannot contaminate project and selected user-data root rejected", () => InProject((root, project) =>
        {
            using DeveloperToolsService tools = Enabled();
            Reject(() => tools.BuildProject(project, Path.Combine(project, "output")), "DEVELOPER_OUTPUT_INSIDE_PROJECT");
            string data = Path.Combine(root, "AutumnOS_Data", "project"); Directory.CreateDirectory(data);
            Reject(() => tools.BuildProject(data, Path.Combine(root, "output")), "DEVELOPER_PRIVATE_PATH_REJECTED");
        }));
        yield return ("T03 developer: SDK trace bounded and allowlist prevents token and arbitrary payload logging", () =>
        {
            using DeveloperToolsService tools = Enabled();
            tools.Record("fixture-secret-access-token", "fixture-secret-code");
            Assert(tools.GetTrace()[0].Method == "unknown-method" && tools.GetTrace()[0].ResultCode == "OTHER_RESULT", "Untrusted trace content leaked.");
            for (int i = 0; i < 1000; i++) tools.Record("saves.write", "OK");
            Assert(tools.GetTrace().Count == 200, "Trace capacity grew.");
            tools.SetEnabled(false); tools.Record("saves.read", "OK");
            Assert(tools.GetTrace().Count == 0, "Disabled debug trace retained/listened.");
        });
        yield return ("T03 developer: release metadata checks exact package hash size identity permissions", () => InProject((root, project) =>
        {
            using DeveloperToolsService tools = Enabled();
            DeveloperBuild build = tools.BuildProject(project, Path.Combine(root, "output"));
            string metadata = Path.Combine(root, "autumn.release.json");
            object Release(string hash) => new { schemaVersion = 1, appId = build.Inspection.Manifest.AppId, version = build.Inspection.Manifest.Version,
                channel = "stable", runtime = "web", minHostVersion = "0.3.0", minSdkVersion = "1.0.0", entry = "index.html",
                asset = Path.GetFileName(build.PackagePath), bytes = new FileInfo(build.PackagePath).Length, sha256 = hash, permissions = new[] { "saves" }, saveFormatVersion = 1 };
            File.WriteAllText(metadata, JsonSerializer.Serialize(Release(build.Inspection.Sha256)));
            Assert(tools.ValidateRelease(metadata, build.PackagePath).Sha256 == build.Inspection.Sha256, "Valid local release rejected.");
            File.WriteAllText(metadata, JsonSerializer.Serialize(Release(new string('0', 64))));
            Reject(() => tools.ValidateRelease(metadata, build.PackagePath), "RELEASE_METADATA_MISMATCH");
        }));
        yield return ("T03 developer: persisted mode restores without touching existing desktop preference file", () => InProject((root, _) =>
        {
            InstallationRoot installation = new(root); Assert(installation.EnsureCreated().Success, "Fixture data root unavailable.");
            string desktop = Path.Combine(installation.Directories["Config"], "desktop-preferences.json");
            File.WriteAllText(desktop, "fixture-preserve-existing-desktop");
            VersionedConfigurationStore store = new(installation, "developer-mode", 1);
            Assert(store.Save(JsonSerializer.SerializeToElement(new { enabled = true })).Success, "Mode save failed.");
            var loaded = new VersionedConfigurationStore(installation, "developer-mode", 1).Load();
            Assert(loaded.Success && loaded.Value!.Values.GetProperty("enabled").GetBoolean() && File.ReadAllText(desktop) == "fixture-preserve-existing-desktop", "Mode restore modified old desktop data.");
        }));
    }
    private static IEnumerable<(string Name, Action Run)> T06Cases()
    {
        yield return ("T06 developer: real same-version rebuild installs in a hash-specific preview cache and preserves business install", () => InProject((root, project) =>
        {
            using DeveloperToolsService tools = Enabled();
            DeveloperBuild first = tools.BuildProject(project, Path.Combine(root, "packages"));
            InstalledPackage business = PackageInstaller.Install(first.PackagePath, Path.Combine(root, "business"));
            InstalledPackage previewA = DeveloperToolsService.InstallPreviewPackage(first.PackagePath, Path.Combine(root, "previews"), first.Inspection.Sha256);
            string originalEntry = File.ReadAllText(Path.Combine(business.DirectoryPath, "index.html"));
            File.AppendAllText(Path.Combine(project, "index.html"), "<!-- actual edited app content, unchanged manifest version -->");
            DeveloperBuild second = tools.BuildProject(project, Path.Combine(root, "packages"));
            InstalledPackage previewB = DeveloperToolsService.InstallPreviewPackage(second.PackagePath, Path.Combine(root, "previews"), second.Inspection.Sha256);
            Assert(first.Inspection.Manifest.Version == second.Inspection.Manifest.Version && first.Inspection.Sha256 != second.Inspection.Sha256 &&
                previewA.DirectoryPath != previewB.DirectoryPath && previewA.HostSource != previewB.HostSource,
                "Same-version edited preview reused an old cache or source binding.");
            Assert(File.ReadAllText(Path.Combine(business.DirectoryPath, "index.html")) == originalEntry &&
                File.ReadAllText(Path.Combine(previewA.DirectoryPath, "index.html")) == originalEntry &&
                File.ReadAllText(Path.Combine(previewB.DirectoryPath, "index.html")) != originalEntry, "Preview modified old business/cache files.");
            Reject(() => DeveloperToolsService.InstallPreviewPackage(second.PackagePath, Path.Combine(root, "rejected"), first.Inspection.Sha256), "DEVELOPER_PREVIEW_HASH_CHANGED");
            Reject(() => DeveloperToolsService.InstallPreviewPackage(second.PackagePath, Path.Combine(root, "rejected"), ""), "DEVELOPER_PREVIEW_BINDING_REQUIRED");
            Assert(!Directory.Exists(Path.Combine(root, "rejected")), "Unconfirmed bytes produced an install root.");
        }));
        yield return ("T06 developer: identical app ID preview source cannot read or overwrite installed saves preferences or file handles", () => InProject((directory, _) =>
        {
            InstallationRoot root = new(directory); Assert(root.EnsureCreated().Success, "Isolated root unavailable.");
            const string appId = "cn.labchronicles.elementpairs";
            RuntimeApplication App(string source) => new(new(appId, null, null), source, "fixture", ["saves", "storage", "files.open"]);
            StorageScope Scope(RuntimeApplication app) => new(app.Identity, "guest", new(Guid.NewGuid()), new(1), app.BindingKey);
            StorageScope installedScope = Scope(App("bundled:" + appId));
            StorageScope previewScope = Scope(App("local-preview:" + new string('a', 64)));
            StorageScope rebuiltScope = Scope(App("local-preview:" + new string('b', 64)));
            AccountDataStore installed = new(root, installedScope, _ => true, allowLegacyGuest: true);
            AccountDataStore preview = new(root, previewScope, _ => true, allowLegacyGuest: false);
            AccountDataStore rebuilt = new(root, rebuiltScope, _ => true, allowLegacyGuest: false);
            Assert(installed.WriteSave("game", JsonSerializer.SerializeToElement(new { value = "business" })).Success &&
                installed.WritePrivate("pref_field_note", [7, 8, 9]).Success, "Could not create original real store records.");
            Assert(preview.ReadSave("game").Value is null && preview.ReadPrivate("pref_field_note").Value is null, "Preview read original data.");
            Assert(preview.WriteSave("game", JsonSerializer.SerializeToElement(new { value = "preview" })).Success &&
                preview.WritePrivate("pref_field_note", [1, 2, 3]).Success, "Could not write source-scoped preview.");
            Assert(installed.ReadSave("game").Value!.Value.GetProperty("value").GetString() == "business" &&
                installed.ReadPrivate("pref_field_note").Value!.SequenceEqual(new byte[] { 7, 8, 9 }) &&
                rebuilt.ReadSave("game").Value is null && rebuilt.ReadPrivate("pref_field_note").Value is null, "Preview/rebuild crossed data sources.");
            string file = Path.Combine(directory, "explicit-picker-fixture.txt"); File.WriteAllText(file, "business file");
            using FileCapabilityBroker broker = new(_ => true);
            string handle = broker.RegisterPickedFile(file, installedScope).Value!.Handle;
            Assert(broker.Read(handle, previewScope).ErrorCode == "FILE_HANDLE_WRONG_OWNER" &&
                File.ReadAllText(file) == "business file", "Preview borrowed an installed-app file capability.");
        }));
        yield return ("T06 developer: create uses only supplied templates and SDK, preserves original and produces a real valid project", () => InProject((root, project) =>
        {
            string templates = PrepareTemplates(root, project), destination = Path.Combine(root, "新项目 with spaces");
            byte[] original = File.ReadAllBytes(Path.Combine(templates, "hello-app", "manifest.json"));
            using DeveloperToolsService tools = Enabled();
            DeveloperProject created = tools.CreateProject(templates, Path.Combine(root, "sdk.js"), "hello-app", destination, "dev.example.created", "中文应用");
            PackageInspection valid = tools.ValidateProject(destination);
            DeveloperBuild built = tools.BuildProject(destination, Path.Combine(root, "packages"));
            Assert(created.Inspection.Manifest.AppId == "dev.example.created" && valid.Manifest.Name == "中文应用" && built.Inspection.Sha256 == valid.Sha256,
                "Create/validate/pack did not share actual package validation.");
            Assert(File.Exists(Path.Combine(destination, "autumn-sdk.js")) && File.Exists(Path.Combine(destination, "sample-ui.js")) &&
                File.Exists(Path.Combine(destination, "sample-ui.css")) && File.ReadAllBytes(Path.Combine(templates, "hello-app", "manifest.json")).SequenceEqual(original),
                "Shipped resources missing or template was rewritten.");
        }));
        yield return ("T06 developer: creation rejects invalid identity, unsupported template and existing output without replacing user files", () => InProject((root, project) =>
        {
            string templates = PrepareTemplates(root, project), destination = Path.Combine(root, "new-project");
            using DeveloperToolsService tools = Enabled();
            Reject(() => tools.CreateProject(templates, Path.Combine(root, "sdk.js"), "unknown", destination, "dev.example.new", "name"), "DEVELOPER_TEMPLATE_UNSUPPORTED");
            try { tools.CreateProject(templates, Path.Combine(root, "sdk.js"), "hello-app", destination, "../escape", "name"); throw new InvalidOperationException("Invalid identity accepted."); }
            catch (PackageException) { }
            Assert(!Directory.Exists(destination), "Invalid project was committed.");
            Directory.CreateDirectory(destination); File.WriteAllText(Path.Combine(destination, "keep.txt"), "untouched");
            Reject(() => tools.CreateProject(templates, Path.Combine(root, "sdk.js"), "hello-app", destination, "dev.example.new", "name"), "DEVELOPER_OUTPUT_ALREADY_EXISTS");
            Assert(File.ReadAllText(Path.Combine(destination, "keep.txt")) == "untouched", "Creation damaged existing directory.");
        }));
        yield return ("T06 developer: create validates delivered SDK bytes and rejects masquerading native payload before commit", () => InProject((root, project) =>
        {
            string templates = PrepareTemplates(root, project), destination = Path.Combine(root, "native-rejected");
            File.WriteAllBytes(Path.Combine(root, "sdk.js"), [0x4d, 0x5a, 0, 0]);
            using DeveloperToolsService tools = Enabled();
            Reject(() => tools.CreateProject(templates, Path.Combine(root, "sdk.js"), "hello-app", destination, "dev.example.new", "name"), "PACKAGE_FILE_TYPE_FORBIDDEN");
            Assert(!Directory.Exists(destination), "Unsafe SDK was committed.");
        }));
        yield return ("T06 developer: independent validation leaves selected project byte-identical and creates no output package", () => InProject((root, project) =>
        {
            byte[] before = File.ReadAllBytes(Path.Combine(project, "manifest.json"));
            using DeveloperToolsService tools = Enabled();
            Assert(tools.ValidateProject(project).Manifest.AppId == "cn.labchronicles.developerfixture", "Validation did not inspect manifest.");
            Assert(Directory.GetFiles(root, "*.autumn", SearchOption.AllDirectories).Length == 0 &&
                File.ReadAllBytes(Path.Combine(project, "manifest.json")).SequenceEqual(before), "Validation changed project or committed an output.");
            File.WriteAllText(Path.Combine(project, "manifest.json"), Manifest(["host.execute"]));
            Reject(() => tools.ValidateProject(project), "PACKAGE_PERMISSION_UNSUPPORTED");
        }));
        yield return ("T06 developer: preview stages independently verified exact bytes and rejects changed expected hash", () => InProject((root, project) =>
        {
            using DeveloperToolsService tools = Enabled();
            DeveloperBuild original = tools.BuildProject(project, Path.Combine(root, "packages"));
            string previews = Path.Combine(root, "previews");
            Reject(() => tools.PreparePreview(original.PackagePath, previews, new string('0', 64)), "DEVELOPER_PREVIEW_HASH_CHANGED");
            Assert(!Directory.Exists(previews), "Rejected preview produced output.");
            DeveloperBuild staged = tools.PreparePreview(original.PackagePath, previews, original.Inspection.Sha256);
            File.WriteAllBytes(original.PackagePath, [1, 2, 3]);
            Assert(staged.PackagePath != original.PackagePath && PackageInstaller.Inspect(staged.PackagePath).Sha256 == original.Inspection.Sha256,
                "Preview bytes followed later source changes.");
        }));
        yield return ("T06 developer: mode revocation is immediate and callbacks may re-enter service without deadlocking", () =>
        {
            using DeveloperToolsService tools = Enabled();
            CancellationToken old = tools.CapabilityToken;
            using ManualResetEventSlim observed = new();
            using CancellationTokenRegistration registration = old.Register(() => { _ = tools.Enabled; observed.Set(); });
            tools.SetEnabled(false);
            Assert(old.IsCancellationRequested && observed.Wait(TimeSpan.FromSeconds(3)), "Old development capability remained live or callback deadlocked.");
            tools.SetEnabled(true); Assert(!tools.CapabilityToken.IsCancellationRequested && old.IsCancellationRequested, "Re-enable revived an old capability.");
        });
        yield return ("T06 developer: shared publication service generates exact bytes and rechecks all three layers", () => InProject((root, project) =>
        {
            using DeveloperToolsService tools = Enabled();
            DeveloperBuild built = tools.BuildProject(project, Path.Combine(root, "packages"));
            DeveloperPublicationService publisher = new(tools);
            DeveloperPublication result = publisher.Generate(built.PackagePath, Path.Combine(root, "publication"), new("fixture developer", "local fixture", Offline: true));
            Assert(publisher.Validate(result.StorePath, result.ReleasePath, result.PackagePath).Sha256 == built.Inspection.Sha256 &&
                File.ReadAllBytes(result.PackagePath).SequenceEqual(File.ReadAllBytes(built.PackagePath)), "Shared publication workflow changed package bytes.");
            string text = File.ReadAllText(result.ReleasePath).Replace(result.Sha256, new string('0', 64), StringComparison.Ordinal);
            File.WriteAllText(result.ReleasePath, text);
            try { publisher.Validate(result.StorePath, result.ReleasePath, result.PackagePath); throw new InvalidOperationException("Bad publication accepted."); }
            catch (CatalogException error) { Assert(error.Code == "STORE_PACKAGE_METADATA_MISMATCH", "Wrong hash rejection."); }
            tools.SetEnabled(false);
            Reject(() => publisher.Generate(built.PackagePath, Path.Combine(root, "disabled"), new("fixture", "fixture")), "DEVELOPER_MODE_DISABLED");
        }));
        yield return ("T06 developer: dedicated preview protocol permits only exact package or scoped preview actions", () =>
        {
            string session = Guid.NewGuid().ToString("N");
            DeveloperPreviewProtocol.Validate(new(1, "preview", Path.GetFullPath("fixture.autumn"), new string('a', 64)));
            foreach (string action in new[] { "status", "trace", "clear-trace", "foreground", "background", "close" })
                DeveloperPreviewProtocol.Validate(new(1, action, SessionId: session));
            Reject(() => DeveloperPreviewProtocol.Validate(new(1, "execute", SessionId: session)), "DEVELOPER_COMMAND_UNSUPPORTED");
            Reject(() => DeveloperPreviewProtocol.Validate(new(1, "close", SessionId: "normal-game")), "DEVELOPER_REQUEST_INVALID");
            Reject(() => DeveloperPreviewProtocol.Validate(new(1, "preview", Path.GetFullPath("fixture.exe"), new string('a', 64))), "DEVELOPER_REQUEST_INVALID");
            Reject(() => DeveloperPreviewProtocol.Validate(new(2, "trace", SessionId: session)), "DEVELOPER_PROTOCOL_UNSUPPORTED");
        });
        yield return ("T06 developer: protocol rejects oversized, duplicate and unknown fields without accepting a command", () =>
        {
            using MemoryStream oversized = new(BitConverter.GetBytes(DeveloperPreviewProtocol.MaximumFrameBytes + 1));
            Reject(() => DeveloperPreviewProtocol.ReadAsync<DeveloperPreviewRequest>(oversized, CancellationToken.None).GetAwaiter().GetResult(), "DEVELOPER_FRAME_INVALID");
            foreach (string json in new[] { "{\"protocolVersion\":1,\"protocolVersion\":1,\"command\":\"preview\"}", "{\"protocolVersion\":1,\"command\":\"status\",\"exe\":\"not-allowed\"}" })
            {
                byte[] bytes = System.Text.Encoding.UTF8.GetBytes(json);
                using MemoryStream frame = new(); frame.Write(BitConverter.GetBytes(bytes.Length)); frame.Write(bytes); frame.Position = 0;
                Reject(() => DeveloperPreviewProtocol.ReadAsync<DeveloperPreviewRequest>(frame, CancellationToken.None).GetAwaiter().GetResult(), "DEVELOPER_FRAME_INVALID");
            }
        });
        if (OperatingSystem.IsWindows()) yield return ("T06 developer: CLI refuses a spoofed current-user pipe whose server is not the selected native client", () => InProject((root, _) =>
        {
            using var server = DeveloperPreviewProtocol.CreateServer(root);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task waiting = server.WaitForConnectionAsync(timeout.Token);
            Reject(() => DeveloperPreviewProtocol.SendAsync(root, new(1, "status", SessionId: Guid.NewGuid().ToString("N")), timeout.Token).GetAwaiter().GetResult(), "DEVELOPER_HOST_UNVERIFIED");
            waiting.GetAwaiter().GetResult();
        }));
    }
    private static string PrepareTemplates(string root, string project)
    {
        string templates = Path.Combine(root, "delivered", "Templates"), template = Path.Combine(templates, "hello-app");
        Directory.CreateDirectory(template); Directory.CreateDirectory(Path.Combine(templates, "_shared"));
        File.Copy(Path.Combine(project, "manifest.json"), Path.Combine(template, "manifest.json"));
        File.Copy(Path.Combine(project, "index.html"), Path.Combine(template, "index.html"));
        File.WriteAllText(Path.Combine(templates, "_shared", "sample-ui.js"), "/* shared fixture */");
        File.WriteAllText(Path.Combine(templates, "_shared", "sample-ui.css"), "body { color: black; }");
        File.WriteAllText(Path.Combine(root, "sdk.js"), "/* SDK fixture; no runtime claim */");
        return templates;
    }
    private static DeveloperToolsService Enabled() { DeveloperToolsService tools = new(); tools.SetEnabled(true); return tools; }
    private static void Reject(Action action, string code)
    { try { action(); throw new InvalidOperationException("Unsafe developer action accepted."); } catch (PackageException error) { Assert(error.Code == code, "Wrong rejection code: " + error.Code); } }
    private static string Manifest(string[] permissions) => JsonSerializer.Serialize(new
    { schemaVersion = 1, appId = "cn.labchronicles.developerfixture", name = "开发工具测试", version = "1.0.0", runtime = "web", entry = "index.html", permissions });
    private static void InProject(Action<string, string> test)
    {
        string root = Path.Combine(Path.GetTempPath(), "autumnos-developer-fixture-" + Guid.NewGuid().ToString("N"));
        string project = Path.Combine(root, "project"); Directory.CreateDirectory(project);
        try { File.WriteAllText(Path.Combine(project, "manifest.json"), Manifest(["saves"])); File.WriteAllText(Path.Combine(project, "index.html"), "<!doctype html><title>fixture</title>"); test(root, project); }
        finally { Directory.Delete(root, true); }
    }
    private static void Assert(bool passed, string message) { if (!passed) throw new InvalidOperationException(message); }
}
