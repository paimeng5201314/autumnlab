using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AutumnOS.Update;

namespace AutumnOS.Update.Cli;

internal static partial class ProductionUpdateSigner
{
    private const int LocalCodeLength = 64;
    private const string RecoveryCodeName = "recovery-code.txt";

    internal static ProductionSignerInfo ProvisionLocal(string keyId, string primaryDirectory, string restoredDirectory,
        string backupPath, string recoveryCodePath, string publicOutput)
    {
        if (keyId.Length is < 1 or > 128 || keyId.Any(symbol => !char.IsAsciiLetterOrDigit(symbol) && symbol is not ('-' or '_' or '.')))
            throw Failure("UPDATE_SIGNER_KEY_ID_INVALID");
        string primary = FullDirectory(primaryDirectory), restored = FullDirectory(restoredDirectory);
        string backup = LocalPath(backupPath), recovery = LocalRecoveryPath(recoveryCodePath);
        if (!backup.EndsWith(".encrypted.pk8", StringComparison.OrdinalIgnoreCase))
            throw Failure("UPDATE_SIGNER_BACKUP_PATH_INVALID");
        string backupDirectory = FullDirectory(Path.GetDirectoryName(backup)!);
        string recoveryDirectory = FullDirectory(Path.GetDirectoryName(recovery)!);
        string candidate = NewPublicOutput(publicOutput, primary);
        if (string.Equals(Path.GetPathRoot(backup), Path.GetPathRoot(recovery), StringComparison.OrdinalIgnoreCase))
            throw Failure("UPDATE_SIGNER_RECOVERY_STORAGE_NOT_SEPARATE");
        string[] freshDirectories = [primary, restored, backupDirectory, recoveryDirectory];
        foreach (string directory in freshDirectories)
        {
            RejectProjectOrInstallationPath(directory);
            if (File.Exists(directory) || Directory.Exists(directory)) throw Failure("UPDATE_SIGNER_DIRECTORY_EXISTS");
            if (!Directory.Exists(Path.GetDirectoryName(directory))) throw Failure("UPDATE_SIGNER_PARENT_MISSING");
            if (freshDirectories.Count(other => ProvisionContains(directory, other)) != 1 || ProvisionContains(directory, candidate))
                throw Failure("UPDATE_SIGNER_PATH_INVALID");
        }
        RejectProjectOrInstallationPath(candidate);

        CreatePrivateDirectory(backupDirectory);
        CreatePrivateDirectory(recoveryDirectory);
        CheckPrivateAccess(backupDirectory, isDirectory: true);
        CheckPrivateAccess(recoveryDirectory, isDirectory: true);
        StoreLocalRecoveryCode(recovery);
        string initialPublic = Path.Combine(recoveryDirectory, Guid.NewGuid().ToString("N") + ".public.json");
        string verifiedPublic = Path.Combine(Path.GetDirectoryName(candidate)!, Guid.NewGuid().ToString("N") + ".public.json");
        bool initialOwned = false, verifiedOwned = false;
        try
        {
            var initial = Init(primary, initialPublic, keyId);
            initialOwned = true;
            var trust = ReadLocalTrust(primary);
            var password = ReadLocalRecoveryCode(recovery);
            try { Backup(primary, backup, trust, keyId, password); }
            finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan())); }

            CheckPrivateAccess(backup, isDirectory: false);
            var restoredInfo = RestoreLocal(backup, restored, initialPublic, keyId, recovery);
            var projected = ExportPublic(restored, verifiedPublic);
            verifiedOwned = true;
            if (initial != restoredInfo || initial != projected ||
                !File.ReadAllBytes(initialPublic).AsSpan().SequenceEqual(File.ReadAllBytes(verifiedPublic)))
                throw Failure("UPDATE_SIGNER_BACKUP_READBACK_FAILED");
            UpdatePaths.EnsureNoReparsePoints(candidate);
            File.Move(verifiedPublic, candidate, overwrite: false);
            verifiedOwned = false;
            return initial;
        }
        finally
        {
            if (initialOwned) RemoveOwnedFile(initialPublic);
            if (verifiedOwned) RemoveOwnedFile(verifiedPublic);
        }
    }

    internal static ProductionSignerInfo RestoreLocal(string backupPath, string privateDirectory, string trustPath,
        string keyId, string recoveryCodePath)
    {
        string recovery = LocalRecoveryPath(recoveryCodePath), backup = LocalPath(backupPath);
        if (string.Equals(Path.GetPathRoot(backup), Path.GetPathRoot(recovery), StringComparison.OrdinalIgnoreCase))
            throw Failure("UPDATE_SIGNER_RECOVERY_STORAGE_NOT_SEPARATE");
        string publicPath = LocalPath(trustPath);
        UpdatePaths.EnsureNoReparsePoints(publicPath);
        var trust = new UpdateTrustStore(UpdateManifestCodec.ParseStrict<UpdateTrustDocument>(
            ReadBounded(publicPath, 64 * 1024), 64 * 1024));
        var password = ReadLocalRecoveryCode(recovery);
        try { return Restore(backup, privateDirectory, trust, keyId, password); }
        finally { CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(password.AsSpan())); }
    }

    private static bool ProvisionContains(string directory, string path) =>
        string.Equals(directory, path, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string LocalRecoveryPath(string supplied)
    {
        string path = LocalPath(supplied);
        UpdatePaths.EnsureNoReparsePoints(path);
        RejectProjectOrInstallationPath(path);
        if (!string.Equals(Path.GetFileName(path), RecoveryCodeName, StringComparison.OrdinalIgnoreCase))
            throw Failure("UPDATE_SIGNER_RECOVERY_CODE_PATH_INVALID");
        return path;
    }

    private static void StoreLocalRecoveryCode(string path)
    {
        Span<byte> entropy = stackalloc byte[LocalCodeLength / 2];
        byte[] encoded = GC.AllocateArray<byte>(LocalCodeLength + 2, pinned: true);
        string stage = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N") + ".tmp");
        bool owned = false;
        try
        {
            RandomNumberGenerator.Fill(entropy);
            ReadOnlySpan<byte> alphabet = "0123456789ABCDEF"u8;
            for (int index = 0; index < entropy.Length; index++)
            {
                encoded[index * 2] = alphabet[entropy[index] >> 4];
                encoded[index * 2 + 1] = alphabet[entropy[index] & 15];
            }
            encoded[^2] = (byte)'\r'; encoded[^1] = (byte)'\n';
            WritePrivateNew(stage, encoded);
            owned = true;
            CheckPrivateAccess(stage, isDirectory: false);
            byte[] readback = ReadBounded(stage, LocalCodeLength + 2);
            try
            {
                if (!CryptographicOperations.FixedTimeEquals(readback, encoded)) throw Failure("UPDATE_SIGNER_BACKUP_READBACK_FAILED");
            }
            finally { CryptographicOperations.ZeroMemory(readback); }
            UpdatePaths.EnsureNoReparsePoints(path);
            File.Move(stage, path, overwrite: false);
            owned = false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(entropy);
            CryptographicOperations.ZeroMemory(encoded);
            if (owned) RemoveOwnedFile(stage);
        }
    }

    private static char[] ReadLocalRecoveryCode(string path)
    {
        CheckPrivateAccess(Path.GetDirectoryName(path)!, isDirectory: true);
        CheckPrivateAccess(path, isDirectory: false);
        byte[] encoded = ReadBounded(path, LocalCodeLength + 2);
        try
        {
            if (encoded.Length != LocalCodeLength + 2 || encoded[^2] != '\r' || encoded[^1] != '\n' ||
                encoded.AsSpan(0, LocalCodeLength).IndexOfAnyExcept("0123456789ABCDEF"u8) >= 0)
                throw Failure("UPDATE_SIGNER_RECOVERY_CODE_FORMAT_INVALID");
            char[] password = GC.AllocateArray<char>(LocalCodeLength, pinned: true);
            for (int index = 0; index < LocalCodeLength; index++) password[index] = (char)encoded[index];
            return password;
        }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }
}
