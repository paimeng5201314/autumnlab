using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using AutumnOS.Update;

namespace AutumnOS.UpdateProtocol;

public sealed record InstalledFile(string Path, long Bytes, string Sha256);
public sealed record InstalledBuild(int SchemaVersion, string Version, string BuildId, InstalledFile[] Files);
public sealed record HandoffRecord(int SchemaVersion, string TransactionId, string Root, string DataRoot, int ParentPid, long ParentStartUtcTicks,
    string CurrentBuildId, string CurrentVersion, string ManifestSha256, DateTimeOffset ExpiresUtc);
public sealed record UpdateJournal(int SchemaVersion, string TransactionId, string Root, string Phase, string FromBuildId, string ToBuildId,
    InstalledBuild Previous, InstalledFile[] NextFiles, string[] Touched, string? ErrorCode = null, int ChildPid = 0, long ChildStartUtcTicks = 0);

public static class UpdateFiles
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    public const string ReceiptName = "autumn.install.json";
    public static string Root(string path)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        UpdatePaths.EnsureNoReparsePoints(root);
        if (!Directory.Exists(root) || root == Path.GetPathRoot(root)) throw new IOException("UPDATE_TARGET_ROOT_INVALID");
        return root;
    }
    public static string Work(string root) => Path.Combine(Root(root), ".autumnos-update");
    public static string DataRoot(string root) => AutumnOS.Contracts.PortableLayout.DataDirectory(Root(root));
    public static string Transaction(string root, string id)
    {
        if (!Guid.TryParseExact(id, "N", out _)) throw new IOException("UPDATE_TRANSACTION_INVALID");
        return Path.Combine(Work(root), id);
    }
    public static string Managed(string root, string relative)
    {
        if (relative.Equals("AutumnOS.exe", StringComparison.OrdinalIgnoreCase) || relative.Equals("AutumnOS.Updater.exe", StringComparison.OrdinalIgnoreCase) ||
            relative.Equals(ReceiptName, StringComparison.OrdinalIgnoreCase) || relative.Equals(AutumnOS.Contracts.PortableLayout.MarkerName, StringComparison.OrdinalIgnoreCase) || relative.StartsWith(".autumnos", StringComparison.OrdinalIgnoreCase))
            throw new IOException("UPDATE_STABLE_FILE_FORBIDDEN");
        return UpdatePaths.ResolveManagedPath(root, relative);
    }
    public static void PrivateDirectory(string path)
    {
        UpdatePaths.EnsureNoReparsePoints(path);
        Directory.CreateDirectory(path);
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    public static void Write<T>(string path, T value)
    {
        UpdatePaths.EnsureNoReparsePoints(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 8192, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, value, Json); stream.Flush(true); }
        File.Move(temp, path, true);
    }
    public static T Read<T>(string path, int maximumBytes = 8 * 1024 * 1024)
    {
        UpdatePaths.EnsureNoReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 2 || stream.Length > maximumBytes) throw new IOException("UPDATE_RECORD_INVALID");
        using var bytes = new MemoryStream(); stream.CopyTo(bytes);
        return UpdateManifestCodec.ParseStrict<T>(bytes.ToArray(), maximumBytes);
    }
    public static string Hash(string path)
    { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexStringLower(SHA256.HashData(stream)); }
    public static bool Matches(string path, InstalledFile file) => File.Exists(path) && new FileInfo(path).Length == file.Bytes && Hash(path) == file.Sha256;
    public static InstalledBuild ReadInstalled(string root)
    {
        var receipt = Read<InstalledBuild>(Path.Combine(root, ReceiptName));
        ValidateInstalled(root, receipt);
        return receipt;
    }
    public static void ValidateInstalled(string root, InstalledBuild receipt)
    {
        if (receipt.SchemaVersion != 1 || receipt.Files.Length is < 3 or > 20000 || string.IsNullOrWhiteSpace(receipt.BuildId)) throw new IOException("UPDATE_INSTALL_RECEIPT_INVALID");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in receipt.Files)
        {
            _ = Managed(root, file.Path);
            if (!seen.Add(file.Path) || file.Bytes < 0 || file.Sha256.Length != 64 || file.Sha256.Any(c => !char.IsAsciiHexDigit(c))) throw new IOException("UPDATE_INSTALL_RECEIPT_INVALID");
        }
    }
}
