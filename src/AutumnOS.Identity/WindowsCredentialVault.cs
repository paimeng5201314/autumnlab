using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AutumnOS.Identity;

internal sealed record ProtectedIdentitySession(int Format, string Issuer, string ClientId, string Subject,
    string? DisplayName, string? AvatarUrl, string AccessToken, string? RefreshToken, string IdentityToken,
    DateTimeOffset ExpiresAt, string Nonce);

internal interface ICredentialVault
{
    ProtectedIdentitySession? Read();
    void Write(ProtectedIdentitySession session);
    void Clear();
}

/// <summary>Only encrypted bytes reach disk. No credential backups survive local logout.</summary>
[SupportedOSPlatform("windows")]
internal sealed class WindowsCredentialVault : ICredentialVault
{
    private readonly string _directory;
    private readonly string _file;
    private readonly string _signedOut;
    private readonly byte[] _entropy;
    private readonly Func<string, IDisposable>? _enterCriticalOperation;
    // LogtoIdentityService wraps the complete protocol transaction. Direct host/test users of this
    // internal vault may inject the same gate here; do not double-acquire within an admitted transaction.
    public WindowsCredentialVault(string dataRoot, LogtoPublicOptions options, Func<string, IDisposable>? enterCriticalOperation = null)
    {
        _enterCriticalOperation = enterCriticalOperation;
        _directory = Path.Combine(Path.GetFullPath(dataRoot), "Identity");
        _file = Path.Combine(_directory, "session.dpapi");
        _signedOut = Path.Combine(_directory, "signed-out.marker");
        _entropy = SHA256.HashData(Encoding.UTF8.GetBytes("AutumnOS.Identity.v1\n" + options.Authority.AbsoluteUri + "\n" + options.ClientId));
    }
    public ProtectedIdentitySession? Read()
    {
        using IDisposable? operation = _enterCriticalOperation?.Invoke("身份凭据读取与权限维护");
        CheckPath(_signedOut);
        if (File.Exists(_signedOut) || !File.Exists(_file)) return null;
        EnsureSecureDirectory();
        CheckPath(_file);
        if (new FileInfo(_file).Length is < 1 or > 131072) throw new IdentityFlowException("AUTH_CREDENTIALS_UNREADABLE");
        byte[] plaintext;
        try { plaintext = ProtectedData.Unprotect(File.ReadAllBytes(_file), _entropy, DataProtectionScope.CurrentUser); }
        catch (CryptographicException) { throw new IdentityFlowException("AUTH_CREDENTIALS_UNREADABLE"); }
        try { return JsonSerializer.Deserialize<ProtectedIdentitySession>(plaintext) ?? throw new IdentityFlowException("AUTH_CREDENTIALS_UNREADABLE"); }
        catch (JsonException) { throw new IdentityFlowException("AUTH_CREDENTIALS_UNREADABLE"); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
    public void Write(ProtectedIdentitySession session)
    {
        using IDisposable? operation = _enterCriticalOperation?.Invoke("身份凭据原子写入");
        EnsureSecureDirectory();
        CheckPath(_file);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(session);
        byte[] ciphertext;
        try { ciphertext = ProtectedData.Protect(plaintext, _entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        string temporary = Path.Combine(_directory, "session-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (FileStream output = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(ciphertext); output.Flush(true); }
            SecureFile(temporary);
            CheckPath(_file);
            if (File.Exists(_file)) File.Replace(temporary, _file, null);
            else File.Move(temporary, _file);
            SecureFile(_file);
            CheckPath(_signedOut);
            if (File.Exists(_signedOut)) File.Delete(_signedOut);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Clear()
    {
        using IDisposable? operation = _enterCriticalOperation?.Invoke("身份退出标记与凭据清理");
        if (!Directory.Exists(_directory)) return;
        EnsureSecureDirectory();
        CheckPath(_file);
        CheckPath(_signedOut);
        // A durable non-secret tombstone prevents a surviving encrypted file from restoring after an interrupted deletion.
        using (FileStream marker = new(_signedOut, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { marker.Write("AutumnOS local logout v1"u8); marker.Flush(true); }
        SecureFile(_signedOut);
        if (File.Exists(_file)) File.Delete(_file);
        foreach (string temporary in Directory.EnumerateFiles(_directory, "session-*.tmp"))
        { CheckPath(temporary); File.Delete(temporary); }
    }
    private void EnsureSecureDirectory()
    {
        CheckPath(_directory);
        Directory.CreateDirectory(_directory);
        DirectorySecurity security = new();
        security.SetAccessRuleProtection(true, false);
        // Newly created directories already belong to their creator. Re-setting that owner asks for
        // WRITE_OWNER, which an owner inheriting Modify need not have; implicit WRITE_DAC is sufficient.
        // Submit only the restrictive DACL, retaining the existing owner and all CurrentUser protection.
        using WindowsIdentity currentUser = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(currentUser.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(_directory).SetAccessControl(security);
    }
    private static void SecureFile(string file)
    {
        FileSecurity security = new();
        security.SetAccessRuleProtection(true, false);
        using WindowsIdentity currentUser = WindowsIdentity.GetCurrent();
        security.AddAccessRule(new FileSystemAccessRule(currentUser.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }
    private static void CheckPath(string path)
    {
        for (string? item = path; item is not null; item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) && (File.GetAttributes(item) & FileAttributes.ReparsePoint) != 0)
                throw new IdentityFlowException("AUTH_CREDENTIALS_UNSAFE_PATH");
    }
}
