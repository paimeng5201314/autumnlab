using System.IO.Compression;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AutumnOS.Setup;
using AutumnOS.UpdateProtocol;

namespace AutumnOS.Tests;

[SupportedOSPlatform("windows")]
internal static class SetupTransactionTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("setup.reserved_windows_names_and_data_paths_rejected", Paths);
        yield return ("setup.zip_special_entries_case_and_parent_collisions_rejected", Archives);
        yield return ("setup.interrupted_copy_has_no_stable_entry_and_same_installer_resumes", Resume);
        yield return ("setup.nonconflicting_unknown_files_and_data_survive_install_uninstall_reinstall", Reinstall);
        yield return ("setup.unknown_target_conflict_rejected_before_program_writes", UnknownConflict);
        yield return ("setup.changed_program_file_survives_uninstall_and_blocks_conflicting_reinstall", ChangedFile);
        yield return ("setup.interrupted_uninstall_resumes_even_after_current_receipt_deleted", ResumeUninstall);
        yield return ("setup.interrupted_install_rejects_different_embedded_payload_identity", WrongPayload);
        yield return ("setup.completed_install_is_idempotent_without_duplicate_data", Idempotent);
        yield return ("setup.private_journal_root_binding_rejects_alternate_target", WrongRoot);
    }
    private static void Paths()
    {
        foreach (string path in new[] { "CON", "con.txt", "x/COM1.dll", "LPT²", "NUL", "x?y", "a<b", "a|b", "a\"b", "a*b", "a:b", "../evil", "x/../evil", "/absolute", "x\\y", "x./a", "x /a", "AutumnOS_Data/save.json", ".autumnos-setup/journal.json", "AutumnOS.Uninstall.exe" }) Reject(() => SetupTransaction.ValidateName(path));
        SetupTransaction.ValidateName("合法目录/AutumnOS.Client.exe");
    }
    private static void Archives()
    {
        foreach (var (path, attrs) in new[] { ("evil", unchecked((int)0xa0000000)), ("evil", 0x400), ("evil", 0x10), ("evil", 0x40), ("evil", 0x10000000), ("core.dll/child", 0), ("CORE.DLL", 0) })
        { using var bytes = new MemoryStream(Payload(path, attrs)); using var zip = new ZipArchive(bytes); Reject(() => SetupTransaction.ValidateArchive(zip)); }
    }
    private static void Resume() => Temp((root, installer) =>
    {
        byte[] payload = Payload(); Reject(() => Install(root, installer, payload, n => { if (n == 1) throw new IOException("INJECTED_AFTER_FIRST_FILE"); }));
        Assert(!File.Exists(Path.Combine(root, "AutumnOS.exe")), "Stable entry appeared before all resources.");
        Install(root, installer, payload); Assert(File.ReadAllText(Path.Combine(root, "core.dll")) == "core", "Retry did not install actual bytes.");
    });
    private static void Reinstall() => Temp((root, installer) =>
    {
        Directory.CreateDirectory(Path.Combine(root, "AutumnOS_Data")); File.WriteAllText(Path.Combine(root, "AutumnOS_Data", "saved.txt"), "saved"); File.WriteAllText(Path.Combine(root, "unknown.txt"), "unknown");
        byte[] payload = Payload(); Install(root, installer, payload); SetupTransaction.UninstallFiles(root);
        Assert(!File.Exists(Path.Combine(root, "AutumnOS.exe")), "Uninstall left original bootstrap."); Install(root, installer, payload);
        Assert(File.ReadAllText(Path.Combine(root, "unknown.txt")) == "unknown" && File.ReadAllText(Path.Combine(root, "AutumnOS_Data", "saved.txt")) == "saved", "Reinstall changed unknown files or data.");
        Assert(File.Exists(Path.Combine(root, "AutumnOS.exe")), "Reinstall failed to restore entry.");
    });
    private static void UnknownConflict() => Temp((root, installer) =>
    {
        File.WriteAllText(Path.Combine(root, "core.dll"), "unknown"); Reject(() => Install(root, installer, Payload()));
        Assert(File.ReadAllText(Path.Combine(root, "core.dll")) == "unknown" && !File.Exists(Path.Combine(root, "AutumnOS.exe")), "Unknown collision was overwritten.");
    });
    private static void ChangedFile() => Temp((root, installer) =>
    {
        byte[] payload = Payload(); Install(root, installer, payload); File.WriteAllText(Path.Combine(root, "core.dll"), "changed"); SetupTransaction.UninstallFiles(root);
        Assert(File.ReadAllText(Path.Combine(root, "core.dll")) == "changed", "Uninstall removed changed file."); Reject(() => Install(root, installer, payload));
        Assert(File.ReadAllText(Path.Combine(root, "core.dll")) == "changed", "Reinstall overwrote changed file.");
    });
    private static void ResumeUninstall() => Temp((root, installer) =>
    {
        Install(root, installer, Payload()); Reject(() => SetupTransaction.UninstallFiles(root, n => { if (!File.Exists(Path.Combine(root, "autumn.install.json"))) throw new IOException("INJECTED_AFTER_RECEIPT_DELETE"); }));
        Assert(!File.Exists(Path.Combine(root, "autumn.install.json")), "Fixture did not reach receipt deletion."); SetupTransaction.UninstallFiles(root);
        Assert(File.Exists(Path.Combine(root, ".autumnos-setup", "uninstalled.json")), "Retry lost ownership after receipt deletion.");
    });
    private static void WrongPayload() => Temp((root, installer) =>
    {
        Reject(() => Install(root, installer, Payload(), n => throw new IOException("INJECTED")));
        Reject(() => Install(root, installer, Payload("additional.dll", 0)));
        Assert(!File.Exists(Path.Combine(root, "AutumnOS.exe")), "Different installer crossed pending transaction.");
    });
    private static void Idempotent() => Temp((root, installer) =>
    {
        byte[] payload = Payload(); Install(root, installer, payload); SetupTransaction.MarkRegistrationComplete(root); Install(root, installer, payload);
        Assert(File.ReadAllText(Path.Combine(root, "resources.pri")) == "resources", "Idempotent retry changed installed resources.");
    });
    private static void WrongRoot() => Temp((root, installer) =>
    {
        Reject(() => Install(root, installer, Payload(), n => throw new IOException("INJECTED")));
        string path = Path.Combine(root, ".autumnos-setup", "journal.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path)); var record = doc.RootElement.Deserialize<SetupJournal>()!;
        File.WriteAllText(path, JsonSerializer.Serialize(record with { Root = Path.GetTempPath() }));
        Reject(() => Install(root, installer, Payload()));
    });
    private static void Install(string root, string installer, byte[] bytes, Action<int>? injection = null)
    { using var payload = new MemoryStream(bytes); SetupTransaction.InstallFiles(root, payload, Convert.ToHexStringLower(SHA256.HashData(bytes)), installer, injection); }
    private static byte[] Payload(string? additional = null, int attrs = 0)
    {
        Dictionary<string, byte[]> content = new(StringComparer.Ordinal) { ["AutumnOS.exe"] = "bootstrap"u8.ToArray(), ["AutumnOS.Client.exe"] = "client"u8.ToArray(), ["AutumnOS.Updater.exe"] = "updater"u8.ToArray(), ["core.dll"] = "core"u8.ToArray(), ["resources.pri"] = "resources"u8.ToArray() };
        var managed = content.Where(x => x.Key is not ("AutumnOS.exe" or "AutumnOS.Updater.exe")).Select(x => new InstalledFile(x.Key, x.Value.Length, Convert.ToHexStringLower(SHA256.HashData(x.Value)))).ToArray();
        content["autumn.install.json"] = JsonSerializer.SerializeToUtf8Bytes(new InstalledBuild(1, "0.5.0", "setup-fixture", managed), UpdateFiles.Json);
        using var result = new MemoryStream();
        using (var zip = new ZipArchive(result, ZipArchiveMode.Create, true))
        {
            foreach (var entry in content) { var item = zip.CreateEntry(entry.Key); item.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); using var output = item.Open(); output.Write(entry.Value); }
            if (additional is not null) { var item = zip.CreateEntry(additional); item.ExternalAttributes = attrs; item.LastWriteTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero); using var output = item.Open(); output.Write("additional"u8); }
        }
        return result.ToArray();
    }
    private static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void Reject(Action action) { try { action(); } catch (Exception e) when (e is IOException or JsonException) { return; } throw new Exception("Unsafe operation unexpectedly succeeded."); }
    private static void Temp(Action<string, string> action)
    {
        string parent = Path.Combine(Path.GetTempPath(), "AutumnOS-setup-fixture-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(parent); string root = Path.Combine(parent, "install"); Directory.CreateDirectory(root); string installer = Path.Combine(parent, "setup.exe"); File.WriteAllText(installer, "fixture-setup-bytes");
        try { using var rootLease = UpdateRootLease.AcquireExclusive(root); action(root, installer); } finally { Directory.Delete(parent, true); }
    }
}
