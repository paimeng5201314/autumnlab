using System.Security.Cryptography;
using System.Text;
using AutumnOS.Contracts;

namespace AutumnOS.Storage;

/// <summary>Only the host constructs this binding. SDK supplied identities are never accepted.</summary>
public sealed record StorageScope(AppIdentity Identity, string AccountKey, AppInstanceId InstanceId, SessionEpoch Epoch, string SourceBinding = "")
{
    public string SourceKey => Convert.ToHexStringLower(SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
        new { appId = Identity.AppId, repositoryId = Identity.RepositoryId, signingKey = Identity.SigningKeyFingerprint, source = SourceBinding })));
    public string AccountDirectory => AccountKey == "guest" ? "guest" : "account-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(AccountKey)));
}

public sealed record DataResult<T>(bool Success, T? Value, string ErrorCode)
{
    public static DataResult<T> Ok(T? value) => new(true, value, "NONE");
    public static DataResult<T> Fail(string code) => new(false, default, code);
}

public sealed record StorageLimits(int MaximumSaveBytes = 128 * 1024, int MaximumPrivateFileBytes = 1024 * 1024,
    long MaximumAccountBytes = 16 * 1024 * 1024, int MaximumSlots = 32, int MaximumPrivateFiles = 128);
public enum StorageWritePoint { BeforeWrite, AfterStagingFlush, BeforeCommit }
/// <summary>Explicit fault injection for isolated tests; never constructed from SDK input or normal configuration.</summary>
public sealed record StorageFaultHooks(Action<StorageWritePoint> Invoke);
public sealed record SaveSlot(string Slot, int FormatVersion, long Revision, long Bytes, bool HasBackup);
public sealed record SaveRecoveryInfo(string Slot, bool MainExists, bool BackupExists, bool BackupValid, string BackupError, bool BeforeRestoreExists);
public sealed record ConfigurationBackupInfo(bool MainExists, bool BackupExists, bool BackupValid, string BackupError, bool BeforeRestoreExists, int? BackupSchemaVersion, VersionedConfiguration? Backup = null);

public sealed class DataStoreException(string code) : IOException(code)
{
    public string Code { get; } = code;
}

internal static class ScopedStorageSafety
{
    internal static bool IsKey(string? key) => key is { Length: > 0 and <= 64 }
        && key.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_')
        && !new[] { "CON", "PRN", "AUX", "NUL" }.Contains(key, StringComparer.OrdinalIgnoreCase)
        && !Enumerable.Range(1, 9).Any(n => key.Equals($"COM{n}", StringComparison.OrdinalIgnoreCase) || key.Equals($"LPT{n}", StringComparison.OrdinalIgnoreCase));
    internal static void ValidateScope(StorageScope scope)
    {
        if (string.IsNullOrWhiteSpace(scope.Identity.AppId) || scope.Identity.AppId.Length > 128
            || scope.Identity.AppId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
            || scope.Identity.AppId is "." or ".." || scope.Identity.AppId.EndsWith('.')
            || string.IsNullOrWhiteSpace(scope.AccountKey) || scope.AccountKey.Length > 2048
            || scope.SourceBinding.Length > 4096 || scope.InstanceId.Value == Guid.Empty || scope.Epoch.Value < 0) throw new ArgumentException("Invalid host storage binding.");
    }
    internal static void Directory(string path, bool create)
    {
        var ancestors = new Stack<string>();
        for (string? p = Path.GetFullPath(path); p is not null; p = Path.GetDirectoryName(p)) ancestors.Push(p);
        while (ancestors.TryPop(out string? p))
        {
            FileAttributes? attrs = Attributes(p);
            if (attrs is not null && ((attrs & FileAttributes.ReparsePoint) != 0 || (attrs & FileAttributes.Directory) == 0))
                throw new DataStoreException("STORAGE_UNSAFE_PATH");
            if (create && attrs is null) { System.IO.Directory.CreateDirectory(p); Directory(p, false); }
        }
    }
    internal static void File(string path)
    {
        Directory(Path.GetDirectoryName(path)!, false);
        FileAttributes? attrs = Attributes(path);
        if (attrs is not null && (attrs & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new DataStoreException("STORAGE_UNSAFE_PATH");
    }
    private static FileAttributes? Attributes(string path)
    {
        try { return System.IO.File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    internal static string Error(Exception e) => e switch
    {
        DataStoreException d => d.Code,
        OperationCanceledException => "USER_CANCELLED",
        UnauthorizedAccessException or System.Security.SecurityException => "STORAGE_ACCESS_DENIED",
        IOException io when (io.HResult & 0xffff) is 112 or 39 => "STORAGE_DISK_FULL",
        IOException io when (io.HResult & 0xffff) is 32 or 33 => "STORAGE_BUSY",
        _ => "STORAGE_IO_ERROR"
    };
    internal static bool Handled(Exception e) => e is IOException or UnauthorizedAccessException or System.Security.SecurityException or OperationCanceledException;
}
