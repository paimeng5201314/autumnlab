using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AutumnOS.Update;

namespace AutumnOS.Update.Cli;

internal sealed record ProductionSignerInfo(string KeyId, string PublicKeySha256, string PublicTrustSha256);

/// <summary>Publisher-side update-manifest signing, not Windows Authenticode or a client trust override.</summary>
[SupportedOSPlatform("windows")]
internal static class ProductionUpdateSigner
{
    internal const string PrivateFileName = "signing-key.dpapi";
    internal const string MetadataFileName = "public-trust.json";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LabChronicles.AutumnOS.UpdateSigning.DPAPI.CurrentUser.v1");

    internal static ProductionSignerInfo Init(string privateDirectory, string publicOutput, string keyId)
    {
        if (keyId.Length is < 1 or > 128 || keyId.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw Failure("UPDATE_SIGNER_KEY_ID_INVALID");
        string directory = FullDirectory(privateDirectory);
        string output = NewPublicOutput(publicOutput, directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw Failure("UPDATE_SIGNER_DIRECTORY_EXISTS");
        string parent = Path.GetDirectoryName(directory) ?? throw Failure("UPDATE_SIGNER_PATH_INVALID");
        if (!Directory.Exists(parent)) throw Failure("UPDATE_SIGNER_PARENT_MISSING");

        using var rsa = RSA.Create(3072);
        var trust = new UpdateTrustStore(new(1, 1, "production", [new(keyId, rsa.ExportSubjectPublicKeyInfoPem())]));
        byte[] publicBytes = JsonSerializer.SerializeToUtf8Bytes(trust.Document, UpdateManifestCodec.JsonOptions);
        byte[] privateBytes = rsa.ExportPkcs8PrivateKey();
        byte[]? encrypted = null;
        try
        {
            encrypted = ProtectedData.Protect(privateBytes, Entropy, DataProtectionScope.CurrentUser);
            // Win32 CreateDirectory is atomic and fails for an existing leaf. The
            // private DACL is attached at creation, before any key bytes are written.
            CreatePrivateDirectory(directory);
            WriteNew(Path.Combine(directory, MetadataFileName), publicBytes);
            WriteNew(Path.Combine(directory, PrivateFileName), encrypted);
            CheckPrivateAccess(directory, isDirectory: true);
            CheckPrivateAccess(Path.Combine(directory, PrivateFileName), isDirectory: false);
            // If this last operation fails, retain the protected key and local
            // metadata. ExportPublic can retry without changing the signing identity.
            WriteNew(output, publicBytes);
            return Info(trust.Document.Keys[0], publicBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(privateBytes);
            if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
        }
    }

    internal static ProductionSignerInfo ExportPublic(string privateDirectory, string publicOutput)
    {
        string directory = FullDirectory(privateDirectory);
        string output = NewPublicOutput(publicOutput, directory);
        var local = ReadLocalTrust(directory);
        using var rsa = OpenKey(directory, local.Document.Keys[0]);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(local.Document, UpdateManifestCodec.JsonOptions);
        WriteNew(output, bytes);
        return Info(local.Document.Keys[0], bytes);
    }

    internal static byte[] Sign(byte[] manifestBytes, string privateDirectory, UpdateTrustStore expectedTrust)
    {
        var manifest = UpdateManifestCodec.Parse(manifestBytes);
        string directory = FullDirectory(privateDirectory);
        var local = ReadLocalTrust(directory);
        var own = local.Document.Keys[0];
        if (expectedTrust.Document.Purpose != "production" || expectedTrust.Document.Version != local.Document.Version ||
            manifest.KeyId != own.KeyId || manifest.TrustRootVersion != local.Document.Version)
            throw Failure("UPDATE_SIGNER_TRUST_MISMATCH");
        var expected = expectedTrust.Document.Keys.SingleOrDefault(k => k.KeyId == own.KeyId && !k.Revoked)
            ?? throw Failure("UPDATE_SIGNER_TRUST_MISMATCH");
        using var rsa = OpenKey(directory, own);
        using var expectedKey = RSA.Create();
        expectedKey.ImportFromPem(expected.PublicKeyPem);
        if (!rsa.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(expectedKey.ExportSubjectPublicKeyInfo()))
            throw Failure("UPDATE_SIGNER_TRUST_MISMATCH");
        return UpdateSignature.Create(manifestBytes, rsa, own.KeyId, local.Document.Version);
    }

    private static UpdateTrustStore ReadLocalTrust(string directory)
    {
        CheckPrivateAccess(directory, isDirectory: true);
        string path = Path.Combine(directory, MetadataFileName);
        UpdatePaths.EnsureNoReparsePoints(path);
        var trust = new UpdateTrustStore(UpdateManifestCodec.ParseStrict<UpdateTrustDocument>(ReadBounded(path, 64 * 1024), 64 * 1024));
        if (trust.Document.Purpose != "production" || trust.Document.Version != 1 || trust.Document.Keys.Count != 1 || trust.Document.Keys[0].Revoked)
            throw Failure("UPDATE_SIGNER_METADATA_INVALID");
        return trust;
    }

    private static RSA OpenKey(string directory, UpdateTrustKey localKey)
    {
        string path = Path.Combine(directory, PrivateFileName);
        CheckPrivateAccess(path, isDirectory: false);
        byte[] ciphertext = ReadBounded(path, 16 * 1024);
        byte[]? plain = null;
        RSA? rsa = null;
        try
        {
            plain = ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
            rsa = RSA.Create();
            rsa.ImportPkcs8PrivateKey(plain, out int read);
            if (read != plain.Length || rsa.KeySize < 3072 || rsa.ExportSubjectPublicKeyInfoPem() != localKey.PublicKeyPem)
                throw Failure("UPDATE_SIGNER_KEY_MISMATCH");
            RSA result = rsa; rsa = null; return result;
        }
        finally
        {
            rsa?.Dispose();
            CryptographicOperations.ZeroMemory(ciphertext);
            if (plain is not null) CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static ProductionSignerInfo Info(UpdateTrustKey key, byte[] publicBytes)
    {
        using var rsa = RSA.Create(); rsa.ImportFromPem(key.PublicKeyPem);
        return new(key.KeyId, Convert.ToHexStringLower(SHA256.HashData(rsa.ExportSubjectPublicKeyInfo())),
            Convert.ToHexStringLower(SHA256.HashData(publicBytes)));
    }

    private static string FullDirectory(string value)
    {
        string path = Path.TrimEndingDirectorySeparator(LocalPath(value));
        if (path == Path.GetPathRoot(path)) throw Failure("UPDATE_SIGNER_PATH_INVALID");
        UpdatePaths.EnsureNoReparsePoints(path);
        return path;
    }

    private static string NewPublicOutput(string value, string privateDirectory)
    {
        string path = LocalPath(value);
        UpdatePaths.EnsureNoReparsePoints(path);
        if (!path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(privateDirectory, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(privateDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Failure("UPDATE_SIGNER_PUBLIC_PATH_INVALID");
        if (File.Exists(path) || Directory.Exists(path)) throw Failure("UPDATE_SIGNER_PUBLIC_EXISTS");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw Failure("UPDATE_SIGNER_PARENT_MISSING");
        return path;
    }

    private static string LocalPath(string value)
    {
        if (!Path.IsPathFullyQualified(value)) throw Failure("UPDATE_SIGNER_PATH_INVALID");
        string suppliedRoot = Path.GetPathRoot(value) ?? "";
        if (suppliedRoot.Length != 3 || !char.IsAsciiLetter(suppliedRoot[0]) || suppliedRoot[1] != ':')
            throw Failure("UPDATE_SIGNER_PATH_INVALID");
        // Validate the caller's spelling before GetFullPath can normalize Windows
        // aliases such as trailing periods or parent-directory components.
        foreach (string component in value[suppliedRoot.Length..].Split(['\\', '/']))
        {
            string stem = component.Split('.')[0].ToUpperInvariant();
            if (component.Contains(':') || component.EndsWith('.') || component.EndsWith(' ') ||
                component.Any(char.IsControl) || stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                (char.IsAsciiDigit(stem[3]) || "¹²³".Contains(stem[3])))
                throw Failure("UPDATE_SIGNER_PATH_INVALID");
        }
        string path = Path.GetFullPath(value), root = Path.GetPathRoot(path) ?? "";
        // Never turn a caller-supplied UNC or mapped share into a key/public export.
        if (root.Length != 3 || !char.IsAsciiLetter(root[0]) || root[1] != ':' ||
            new DriveInfo(root).DriveType is not (DriveType.Fixed or DriveType.Removable))
            throw Failure("UPDATE_SIGNER_PATH_INVALID");
        foreach (string component in path[root.Length..].Split(Path.DirectorySeparatorChar))
            if (component.Contains(':') || component.EndsWith('.') || component.EndsWith(' '))
                throw Failure("UPDATE_SIGNER_PATH_INVALID");
        return path;
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 1 || stream.Length > maximum) throw Failure("UPDATE_SIGNER_RECORD_INVALID");
        byte[] bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes); return bytes;
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        UpdatePaths.EnsureNoReparsePoints(path);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes); stream.Flush(true);
    }

    private static void CheckPrivateAccess(string path, bool isDirectory)
    {
        UpdatePaths.EnsureNoReparsePoints(path);
        var current = WindowsIdentity.GetCurrent().User!;
        FileSystemSecurity security = isDirectory
            ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner)
            : new FileInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (security.GetOwner(typeof(SecurityIdentifier)) != current || isDirectory && !security.AreAccessRulesProtected)
            throw Failure("UPDATE_SIGNER_ACL_INVALID");
        bool ownAllow = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (rule.IdentityReference != current) throw Failure("UPDATE_SIGNER_ACL_INVALID");
            ownAllow = true;
        }
        if (!ownAllow) throw Failure("UPDATE_SIGNER_ACL_INVALID");
    }

    private static void CreatePrivateDirectory(string path)
    {
        UpdatePaths.EnsureNoReparsePoints(path);
        var current = WindowsIdentity.GetCurrent().User!;
        var acl = new DirectorySecurity(); acl.SetOwner(current); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        byte[] descriptor = acl.GetSecurityDescriptorBinaryForm();
        var pinned = GCHandle.Alloc(descriptor, GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = pinned.AddrOfPinnedObject() };
            if (!CreateDirectoryW(path, ref attributes))
                throw Failure(Marshal.GetLastWin32Error() == 183 ? "UPDATE_SIGNER_DIRECTORY_EXISTS" : "UPDATE_SIGNER_DIRECTORY_CREATE_FAILED");
        }
        finally { pinned.Free(); }
    }

    private static UpdateException Failure(string code) => new(code);
    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes { internal int Length; internal nint Descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool InheritHandle; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string name, ref SecurityAttributes attributes);
}
