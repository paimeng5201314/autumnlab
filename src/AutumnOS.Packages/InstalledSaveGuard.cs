using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutumnOS.Packages;

/// <summary>Checks actual account/guest records, never infers compatibility from release declarations alone.</summary>
public sealed class InstalledSaveGuard
{
    private readonly string savesRoot, backupRoot;
    public string? LastBackupPath { get; private set; }
    public InstalledSaveGuard(string savesDirectory, string backupDirectory)
    {
        savesRoot = ApplicationInstallService.Absolute(savesDirectory);
        backupRoot = ApplicationInstallService.Absolute(backupDirectory);
        if (backupRoot.Equals(savesRoot, StringComparison.OrdinalIgnoreCase) || backupRoot.StartsWith(savesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new PackageException("PACKAGE_UNSAFE_PATH");
    }

    /// <summary>Invoke only while the application launch lock and host critical operation lease are held.</summary>
    public void PrepareChange(InstalledApplication old, AutumnPackageManifest next, bool downgrade)
    {
        LastBackupPath = null;
        if (old.AppId != next.AppId || !Regex.IsMatch(old.AppId, "^[a-z][a-z0-9.-]{1,79}$", RegexOptions.CultureInvariant)) throw new PackageException("PACKAGE_APP_ID_INVALID");
        string appRoot = Path.Combine(savesRoot, old.AppId); ApplicationInstallService.SafePath(appRoot);
        if (!Directory.Exists(appRoot)) return;
        List<(string Name, byte[] Bytes)> files = []; long total = 0;
        foreach (string path in ApplicationInstallService.SafeFiles(appRoot))
        {
            if (Path.GetFileName(path) == ".account-data.lock") continue;
            if (files.Count >= 4096) throw new PackageException("SAVE_BACKUP_LIMIT_EXCEEDED");
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 8 * 1024 * 1024 || (total += stream.Length) > 256 * 1024 * 1024) throw new PackageException("SAVE_BACKUP_LIMIT_EXCEEDED");
            byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            string relative = Path.GetRelativePath(appRoot, path);
            if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                int version = ReadSaveFormat(bytes, old.AppId, relative);
                if (version < (next.MinReadableSaveFormatVersion ?? next.SaveFormatVersion) || version > (next.MaxReadableSaveFormatVersion ?? next.SaveFormatVersion))
                    throw new PackageException("SAVE_FORMAT_INCOMPATIBLE");
            }
            files.Add((relative, bytes));
        }
        if (!downgrade || files.Count == 0) return;
        string destination = Path.Combine(backupRoot, old.AppId, "downgrade-" + Guid.NewGuid().ToString("N"));
        ApplicationInstallService.SafePath(destination);
        foreach (var file in files)
        {
            string path = Path.GetFullPath(Path.Combine(destination, file.Name));
            if (!path.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new PackageException("PACKAGE_UNSAFE_PATH");
            ApplicationInstallService.WriteNew(path, file.Bytes);
            using FileStream check = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(check), SHA256.HashData(file.Bytes))) throw new PackageException("SAVE_BACKUP_FAILED");
        }
        byte[] report = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, appId = old.AppId, sourceVersion = old.Package.Manifest.Version, targetVersion = next.Version,
            createdAt = DateTimeOffset.UtcNow,
            files = files.Select(f => new { path = f.Name.Replace('\\', '/'), bytes = f.Bytes.Length, sha256 = Convert.ToHexStringLower(SHA256.HashData(f.Bytes)) })
        });
        ApplicationInstallService.WriteNew(Path.Combine(destination, ".backup-complete.json"), report);
        LastBackupPath = destination;
    }

    private static int ReadSaveFormat(byte[] bytes, string appId, string relative)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 40 });
            JsonElement root = document.RootElement; ApplicationInstallService.CheckDuplicates(root);
            if (root.GetProperty("appId").GetString() != appId) throw new PackageException("SAVE_CORRUPT");
            int schema = root.GetProperty("schemaVersion").GetInt32();
            string[] segments = relative.Split(Path.DirectorySeparatorChar);
            if (schema == 1 && root.EnumerateObject().Count() == 5 && segments.Length == 2 && segments[0] == "guest" && segments[1] == "game.json" &&
                root.GetProperty("accountMode").GetString() == "guest" && root.GetProperty("slot").GetString() == "game" && root.TryGetProperty("value", out _)) return 1;
            if (schema != 2 || root.EnumerateObject().Count() != 9 || segments.Length != 3 ||
                root.GetProperty("sourceKey").GetString() != segments[0] || root.GetProperty("accountKey").GetString() != segments[1] ||
                root.GetProperty("slot").GetString() != Path.GetFileNameWithoutExtension(segments[2]) || root.GetProperty("revision").GetInt64() < 1)
                throw new PackageException("SAVE_CORRUPT");
            int format = root.GetProperty("formatVersion").GetInt32();
            string hash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(root.GetProperty("value"))));
            if (format is < 1 or > 1_000_000 || hash != root.GetProperty("contentHash").GetString()) throw new PackageException("SAVE_CORRUPT");
            return format;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
        { throw new PackageException("SAVE_CORRUPT"); }
    }
}
