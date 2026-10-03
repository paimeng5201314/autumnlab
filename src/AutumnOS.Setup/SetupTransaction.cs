using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AutumnOS.UpdateProtocol;

namespace AutumnOS.Setup;

internal sealed record SetupEntry(string Path, long Bytes, string Sha256);
internal sealed record SetupReceipt(int Schema, string Product, string Root, SetupEntry[] Files, SetupEntry? Uninstaller = null);
internal sealed record SetupJournal(int Schema, string TransactionId, string Root, string PayloadSha256, string Phase, SetupEntry[] Files, SetupEntry Uninstaller);
internal sealed record UninstallJournal(int Schema, string Root, string Phase, SetupEntry[] Files, string[] Preserved);
internal sealed record UninstalledMarker(int Schema, string Root, DateTimeOffset CompletedUtc, string[] Preserved);

/// <summary>Caller owns maintenance, business and physical-root leases. No scripts or recursive removal.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class SetupTransaction
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 24 };
    private static string Work(string root) => Path.Combine(root, ".autumnos-setup");
    private static string JournalPath(string root) => Path.Combine(Work(root), "journal.json");

    internal static void InstallFiles(string root, Stream payload, string expected, string installerPath, Action<int>? afterFile = null)
    {
        Safe(root); Safe(installerPath);
        if (expected.Length != 64 || expected.Any(c => !char.IsAsciiHexDigit(c)) || !payload.CanSeek || Convert.ToHexStringLower(SHA256.HashData(payload)) != expected) throw new IOException("SETUP_PAYLOAD_INVALID");
        payload.Position = 0; using var zip = new ZipArchive(payload, ZipArchiveMode.Read, true); ValidateArchive(zip);
        string markerPath = Path.Combine(Work(root), "uninstalled.json");
        bool uninstalled = File.Exists(markerPath);
        if (uninstalled) { var marker = Read<UninstalledMarker>(markerPath); if (marker.Schema != 1 || marker.Root != root) throw new IOException("SETUP_RECEIPT_INVALID"); }
        SetupReceipt? oldReceipt = File.Exists(Path.Combine(root, ".autumnos-install.json")) ? ReadReceipt(root) : null;
        SetupJournal? existing = File.Exists(JournalPath(root)) ? Read<SetupJournal>(JournalPath(root)) : null;
        if (existing is not null) ValidateJournal(root, existing);
        if (!uninstalled && existing is null && oldReceipt is not null) throw new IOException("SETUP_EXISTING_INSTALL_PRESERVED");
        if (!uninstalled && existing is not null && existing.PayloadSha256 != expected) throw new IOException("SETUP_RECOVERY_RUN_SAME_INSTALLER");
        string uninstallPath = Path.Combine(Work(root), "uninstall.json");
        if (!uninstalled && File.Exists(uninstallPath) && Read<UninstallJournal>(uninstallPath).Phase != "completed") throw new IOException("SETUP_FINISH_UNINSTALL_FIRST");
        long total = checked(zip.Entries.Sum(e => e.Length));
        if (new DriveInfo(Path.GetPathRoot(root)!).AvailableFreeSpace < total * 2 + new FileInfo(installerPath).Length + 64L * 1024 * 1024) throw new IOException("SETUP_INSUFFICIENT_SPACE");
        Directory.CreateDirectory(root); UpdateFiles.PrivateDirectory(Work(root));
        string id = !uninstalled && existing is not null ? existing.TransactionId : Guid.NewGuid().ToString("N");
        string stage = Path.Combine(Work(root), id, "stage"); Safe(stage); Directory.CreateDirectory(stage);
        List<SetupEntry> files = [];
        foreach (var entry in zip.Entries)
        {
            string output = Resolve(stage, entry.FullName); Directory.CreateDirectory(Path.GetDirectoryName(output)!); Safe(output);
            string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (Stream input = entry.Open())
            using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough))
            {
                byte[] buffer = new byte[81920]; long received = 0; int read;
                while ((read = input.Read(buffer)) != 0) { received = checked(received + read); if (received > entry.Length) throw new IOException("SETUP_PAYLOAD_INVALID"); destination.Write(buffer, 0, read); }
                if (received != entry.Length) throw new IOException("SETUP_PAYLOAD_INVALID"); destination.Flush(true);
            }
            var file = Describe(temporary, entry.FullName); files.Add(file);
            if (File.Exists(output)) { if (!Matches(output, file)) throw new IOException("SETUP_STAGE_CHANGED"); File.Delete(temporary); }
            else File.Move(temporary, output, false);
        }
        var uninstaller = Describe(installerPath, "AutumnOS.Uninstall.exe");
        var planned = files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
        if (!uninstalled && existing is not null && (!SameEntries(existing.Files, planned) || existing.Uninstaller != uninstaller)) throw new IOException("SETUP_RECOVERY_RUN_SAME_INSTALLER");
        var owned = existing is not null && !uninstalled ? existing.Files : oldReceipt?.Files ?? [];
        var ownedMap = owned.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase);
        foreach (var file in planned)
        {
            string target = Resolve(root, file.Path); EnsureParentsAreDirectories(root, file.Path);
            if (Directory.Exists(target)) throw new IOException("SETUP_UNKNOWN_FILE_COLLISION");
            if (File.Exists(target) && (!ownedMap.TryGetValue(file.Path, out var prior) || !Matches(target, prior) || prior != file)) throw new IOException("SETUP_UNKNOWN_OR_CHANGED_FILE_PRESERVED");
        }
        string uninstallExe = Path.Combine(root, uninstaller.Path); Safe(uninstallExe);
        if (File.Exists(uninstallExe))
        {
            var permitted = !uninstalled && existing is not null ? existing.Uninstaller : oldReceipt?.Uninstaller;
            if (permitted is null || !Matches(uninstallExe, permitted)) throw new IOException("SETUP_CHANGED_UNINSTALLER_PRESERVED");
        }
        var journal = new SetupJournal(1, id, root, expected, "prepared", planned, uninstaller); Write(JournalPath(root), journal);
        if (uninstalled) File.Delete(markerPath);
        if (File.Exists(uninstallPath)) File.Delete(uninstallPath);
        journal = journal with { Phase = "applying" }; Write(JournalPath(root), journal);
        int count = 0;
        foreach (var file in planned.Where(f => !f.Path.Equals("AutumnOS.exe", StringComparison.OrdinalIgnoreCase)))
        { CopyOwned(Resolve(stage, file.Path), Resolve(root, file.Path), file, file); afterFile?.Invoke(++count); }
        CopyOwned(installerPath, uninstallExe, uninstaller, oldReceipt?.Uninstaller ?? existing?.Uninstaller);
        Write(Path.Combine(root, ".autumnos-install.json"), new SetupReceipt(1, "LabChronicles.AutumnOS", root, planned, uninstaller));
        // Stable entry is last: a terminated fresh install cannot expose half-installed resources.
        var bootstrap = planned.Single(f => f.Path == "AutumnOS.exe");
        CopyOwned(Resolve(stage, bootstrap.Path), Resolve(root, bootstrap.Path), bootstrap, bootstrap); afterFile?.Invoke(++count);
        Write(JournalPath(root), journal with { Phase = "filesCommitted" });
    }
    internal static void MarkRegistrationComplete(string root)
    {
        var journal = Read<SetupJournal>(JournalPath(root)); ValidateJournal(root, journal);
        if (journal.Phase != "filesCommitted") throw new IOException("SETUP_JOURNAL_INVALID"); Write(JournalPath(root), journal with { Phase = "completed" });
    }
    internal static void UninstallFiles(string root, Action<int>? afterDelete = null)
    {
        Safe(root); UpdateFiles.PrivateDirectory(Work(root));
        string path = Path.Combine(Work(root), "uninstall.json"), marker = Path.Combine(Work(root), "uninstalled.json"); UninstallJournal journal;
        if (File.Exists(path))
        { journal = Read<UninstallJournal>(path); ValidateUninstallJournal(root, journal); if (journal.Phase == "completed" && File.Exists(marker)) return; }
        else
        {
            SetupEntry[] files;
            if (File.Exists(Path.Combine(root, ".autumnos-install.json")))
            {
                var receipt = ReadReceipt(root);
                if (File.Exists(Path.Combine(root, UpdateFiles.ReceiptName)))
                {
                    var installed = UpdateFiles.ReadInstalled(root);
                    files = installed.Files.Select(f => new SetupEntry(f.Path, f.Bytes, f.Sha256)).Concat(receipt.Files.Where(f => f.Path is "AutumnOS.exe" or "AutumnOS.Updater.exe"))
                        .Append(Describe(Path.Combine(root, UpdateFiles.ReceiptName), UpdateFiles.ReceiptName)).DistinctBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToArray();
                }
                else files = receipt.Files;
            }
            else if (File.Exists(JournalPath(root))) { var install = Read<SetupJournal>(JournalPath(root)); ValidateJournal(root, install); files = install.Files; }
            else throw new IOException("SETUP_RECEIPT_INVALID");
            journal = new(1, root, "deleting", files, []); ValidateUninstallJournal(root, journal); Write(path, journal);
        }
        var preserved = journal.Preserved.ToHashSet(StringComparer.OrdinalIgnoreCase); int count = 0;
        foreach (var file in journal.Files.OrderBy(f => f.Path == "AutumnOS.exe" ? 0 : 1))
        {
            string target = Resolve(root, file.Path);
            if (File.Exists(target)) { if (Matches(target, file)) File.Delete(target); else preserved.Add(file.Path); }
            afterDelete?.Invoke(++count);
        }
        Write(marker, new UninstalledMarker(1, root, DateTimeOffset.UtcNow, preserved.OrderBy(p => p, StringComparer.Ordinal).ToArray()));
        Write(path, journal with { Phase = "completed", Preserved = preserved.OrderBy(p => p, StringComparer.Ordinal).ToArray() });
    }
    internal static void ValidateArchive(ZipArchive zip)
    {
        if (zip.Entries.Count is < 5 or > 20000) throw new IOException("SETUP_PAYLOAD_INVALID");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var entry in zip.Entries)
        {
            ValidateName(entry.FullName); int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
            if (!names.Add(entry.FullName) || entry.Length is < 0 or > 1024L * 1024 * 1024 || unixType is not (0 or 0x8000) ||
                (entry.ExternalAttributes & ((int)FileAttributes.ReparsePoint | (int)FileAttributes.Directory | (int)FileAttributes.Device)) != 0 ||
                entry.Length > Math.Max(1, entry.CompressedLength) * 1000L) throw new IOException("SETUP_PAYLOAD_INVALID");
            total = checked(total + entry.Length); if (total > 12L * 1024 * 1024 * 1024) throw new IOException("SETUP_PAYLOAD_INVALID");
        }
        foreach (string name in names)
        { int slash = name.IndexOf('/'); while (slash >= 0) { if (names.Contains(name[..slash])) throw new IOException("SETUP_PARENT_FILE_COLLISION"); slash = name.IndexOf('/', slash + 1); } }
        foreach (string required in new[] { "AutumnOS.exe", "AutumnOS.Client.exe", "AutumnOS.Updater.exe", "autumn.install.json" })
            if (!zip.Entries.Any(e => e.FullName == required)) throw new IOException("SETUP_REQUIRED_ENTRY_MISSING");
    }
    internal static void ValidateName(string name)
    {
        if (name.Length is < 1 or > 240 || name.Contains('\\') || name.StartsWith('/') || name.Any(c => char.IsControl(c) || c is ':' or '*' or '?' or '"' or '<' or '>' or '|')) throw new IOException("SETUP_UNSAFE_PATH");
        foreach (string part in name.Split('/'))
        {
            string stem = part.Split('.')[0].ToUpperInvariant();
            if (part.Length is < 1 or > 200 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && (char.IsAsciiDigit(stem[3]) || "¹²³".Contains(stem[3]))) throw new IOException("SETUP_UNSAFE_PATH");
        }
        string first = name.Split('/')[0];
        if (first.Equals("AutumnOS_Data", StringComparison.OrdinalIgnoreCase) || first.StartsWith(".autumnos", StringComparison.OrdinalIgnoreCase) || first.Equals("AutumnOS.Uninstall.exe", StringComparison.OrdinalIgnoreCase)) throw new IOException("SETUP_USER_DATA_REJECTED");
    }
    internal static void Safe(string path)
    {
        for (string? p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p))
        { try { if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("SETUP_UNSAFE_PATH"); } catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { } }
    }
    private static string Resolve(string root, string relative)
    {
        ValidateName(relative); string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(canonical, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(canonical, StringComparison.OrdinalIgnoreCase)) throw new IOException("SETUP_UNSAFE_PATH"); Safe(full); return full;
    }
    private static void EnsureParentsAreDirectories(string root, string relative)
    {
        string? parent = Path.GetDirectoryName(Resolve(root, relative));
        while (parent is not null && !parent.Equals(root, StringComparison.OrdinalIgnoreCase)) { if (File.Exists(parent)) throw new IOException("SETUP_PARENT_FILE_COLLISION"); parent = Path.GetDirectoryName(parent); }
    }
    private static void CopyOwned(string source, string target, SetupEntry expected, SetupEntry? replaceable)
    {
        Safe(source); Safe(target); if (!Matches(source, expected)) throw new IOException("SETUP_STAGE_CHANGED");
        if (File.Exists(target)) { if (Matches(target, expected)) return; if (replaceable is null || !Matches(target, replaceable)) throw new IOException("SETUP_UNKNOWN_OR_CHANGED_FILE_PRESERVED"); }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); Safe(target); string temp = target + "." + Guid.NewGuid().ToString("N") + ".setup-tmp";
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough)) { input.CopyTo(output); output.Flush(true); }
        if (!Matches(temp, expected)) throw new IOException("SETUP_COPY_INVALID"); Safe(target);
        if (File.Exists(target) && (replaceable is null || !Matches(target, replaceable))) throw new IOException("SETUP_UNKNOWN_OR_CHANGED_FILE_PRESERVED");
        File.Move(temp, target, replaceable is not null);
    }
    private static SetupEntry Describe(string path, string relative) { Safe(path); using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return new(relative, input.Length, Convert.ToHexStringLower(SHA256.HashData(input))); }
    private static bool Matches(string path, SetupEntry entry) { Safe(path); return File.Exists(path) && new FileInfo(path).Length == entry.Bytes && Describe(path, entry.Path).Sha256 == entry.Sha256; }
    private static bool SameEntries(SetupEntry[] a, SetupEntry[] b) => a.OrderBy(e => e.Path, StringComparer.Ordinal).SequenceEqual(b.OrderBy(e => e.Path, StringComparer.Ordinal));
    private static SetupReceipt ReadReceipt(string root)
    {
        var receipt = Read<SetupReceipt>(Path.Combine(root, ".autumnos-install.json"));
        if (receipt.Schema != 1 || receipt.Product != "LabChronicles.AutumnOS" || receipt.Root != root) throw new IOException("SETUP_RECEIPT_INVALID");
        ValidateEntries(receipt.Files); if (receipt.Uninstaller is not null) ValidateUninstaller(receipt.Uninstaller); return receipt;
    }
    private static void ValidateEntries(SetupEntry[] entries)
    {
        if (entries.Length is < 1 or > 20000) throw new IOException("SETUP_RECEIPT_INVALID"); var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in entries) { ValidateName(f.Path); if (!paths.Add(f.Path) || f.Bytes < 0 || f.Sha256.Length != 64 || f.Sha256.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("SETUP_RECEIPT_INVALID"); }
    }
    private static void ValidateUninstaller(SetupEntry file) { if (file.Path != "AutumnOS.Uninstall.exe" || file.Bytes < 0 || file.Sha256.Length != 64 || file.Sha256.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("SETUP_RECEIPT_INVALID"); }
    private static void ValidateJournal(string root, SetupJournal j)
    {
        if (j.Schema != 1 || j.Root != root || !Guid.TryParseExact(j.TransactionId, "N", out _) || j.PayloadSha256.Length != 64 || j.PayloadSha256.Any(c => !char.IsAsciiHexDigit(c)) || j.Phase is not ("prepared" or "applying" or "filesCommitted" or "completed")) throw new IOException("SETUP_JOURNAL_INVALID"); ValidateEntries(j.Files); ValidateUninstaller(j.Uninstaller);
    }
    private static void ValidateUninstallJournal(string root, UninstallJournal j) { if (j.Schema != 1 || j.Root != root || j.Phase is not ("deleting" or "completed")) throw new IOException("SETUP_JOURNAL_INVALID"); ValidateEntries(j.Files); foreach (string p in j.Preserved) ValidateName(p); }
    private static T Read<T>(string path)
    {
        Safe(path); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); if (stream.Length is < 2 or > 8 * 1024 * 1024) throw new IOException("SETUP_RECEIPT_INVALID");
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 24 }); RejectDuplicates(doc.RootElement); return doc.RootElement.Deserialize<T>(Json) ?? throw new IOException("SETUP_RECEIPT_INVALID");
    }
    private static void RejectDuplicates(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object) { var names = new HashSet<string>(StringComparer.Ordinal); foreach (var p in e.EnumerateObject()) { if (!names.Add(p.Name)) throw new IOException("SETUP_RECEIPT_INVALID"); RejectDuplicates(p.Value); } }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var item in e.EnumerateArray()) RejectDuplicates(item);
    }
    private static void Write<T>(string path, T value)
    {
        Safe(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.WriteThrough)) { JsonSerializer.Serialize(output, value, Json); output.Flush(true); } Safe(path); File.Move(temp, path, true);
    }
}
