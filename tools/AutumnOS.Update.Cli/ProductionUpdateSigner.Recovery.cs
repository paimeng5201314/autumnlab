using System.Formats.Asn1;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AutumnOS.Update;

namespace AutumnOS.Update.Cli;

internal static partial class ProductionUpdateSigner
{
    private const int BackupIterations = 600_000;
    private const int MaximumPassphraseLength = 256;

    internal static ProductionSignerInfo Backup(string privateDirectory, string backupPath, UpdateTrustStore expectedTrust,
        string keyId, ReadOnlySpan<char> passphrase)
    {
        ValidatePassphrase(passphrase);
        var expected = RecoveryTrustKey(expectedTrust, keyId);
        string directory = FullDirectory(privateDirectory);
        string output = RecoveryFile(backupPath, directory);
        if (File.Exists(output) || Directory.Exists(output)) throw Failure("UPDATE_SIGNER_BACKUP_EXISTS");
        var local = ReadLocalTrust(directory);
        var localKey = RecoveryTrustKey(local, keyId);
        using var rsa = OpenKey(directory, localKey);
        VerifyRecoveryKey(rsa, expected);
        byte[] encrypted = rsa.ExportEncryptedPkcs8PrivateKey(passphrase,
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, BackupIterations));
        try
        {
            ValidateBackupEncoding(encrypted);
            SaveRecoveryFile(output, encrypted, expected, passphrase);
            return Info(expected, JsonSerializer.SerializeToUtf8Bytes(expectedTrust.Document, UpdateManifestCodec.JsonOptions));
        }
        finally { CryptographicOperations.ZeroMemory(encrypted); }
    }

    internal static ProductionSignerInfo Restore(string backupPath, string privateDirectory, UpdateTrustStore expectedTrust,
        string keyId, ReadOnlySpan<char> passphrase)
    {
        ValidatePassphrase(passphrase);
        var expected = RecoveryTrustKey(expectedTrust, keyId);
        string directory = FullDirectory(privateDirectory);
        RejectProjectOrInstallationPath(directory);
        if (Directory.Exists(directory) || File.Exists(directory)) throw Failure("UPDATE_SIGNER_DIRECTORY_EXISTS");
        if (!Directory.Exists(Path.GetDirectoryName(directory))) throw Failure("UPDATE_SIGNER_PARENT_MISSING");
        string input = RecoveryFile(backupPath, directory);
        byte[] encrypted = ReadBounded(input, 16 * 1024);
        using var recovered = RSA.Create();
        try
        {
            ValidateBackupEncoding(encrypted);
            recovered.ImportEncryptedPkcs8PrivateKey(passphrase, encrypted, out int consumed);
            if (consumed != encrypted.Length) throw Failure("UPDATE_SIGNER_BACKUP_FORMAT_INVALID");
            VerifyRecoveryKey(recovered, expected);
        }
        catch (CryptographicException) { throw Failure("UPDATE_SIGNER_BACKUP_DECRYPT_FAILED"); }
        finally { CryptographicOperations.ZeroMemory(encrypted); }

        byte[] raw = recovered.ExportPkcs8PrivateKey();
        byte[] protectedKey;
        try { protectedKey = ProtectedData.Protect(raw, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(raw); }
        try
        {
            var canonical = expected with { PublicKeyPem = recovered.ExportSubjectPublicKeyInfoPem() };
            byte[] publicBytes = JsonSerializer.SerializeToUtf8Bytes(new UpdateTrustDocument(1, 1, "production", [canonical]), UpdateManifestCodec.JsonOptions);
            SaveRecoveredIdentity(directory, publicBytes, protectedKey, expected);
            return Info(expected, JsonSerializer.SerializeToUtf8Bytes(expectedTrust.Document, UpdateManifestCodec.JsonOptions));
        }
        finally { CryptographicOperations.ZeroMemory(protectedKey); }
    }

    private static void SaveRecoveryFile(string output, byte[] encrypted, UpdateTrustKey expected, ReadOnlySpan<char> passphrase)
    {
        string stage = Path.Combine(Path.GetDirectoryName(output)!, Guid.NewGuid().ToString("N") + ".encrypted.pk8");
        bool owned = false;
        try
        {
            WritePrivateNew(stage, encrypted);
            owned = true;
            byte[] stored = ReadBounded(stage, 16 * 1024);
            try
            {
                if (!stored.AsSpan().SequenceEqual(encrypted)) throw Failure("UPDATE_SIGNER_BACKUP_READBACK_FAILED");
                using var check = RSA.Create();
                check.ImportEncryptedPkcs8PrivateKey(passphrase, stored, out int consumed);
                if (consumed != stored.Length) throw Failure("UPDATE_SIGNER_BACKUP_READBACK_FAILED");
                VerifyRecoveryKey(check, expected);
            }
            finally { CryptographicOperations.ZeroMemory(stored); }
            UpdatePaths.EnsureNoReparsePoints(stage);
            UpdatePaths.EnsureNoReparsePoints(output);
            File.Move(stage, output, overwrite: false);
            owned = false;
        }
        finally
        {
            if (owned) RemoveOwnedFile(stage);
        }
    }

    private static void SaveRecoveredIdentity(string directory, byte[] publicBytes, byte[] protectedKey, UpdateTrustKey expected)
    {
        string stage = Path.Combine(Path.GetDirectoryName(directory)!, ".autumnos-restore-" + Guid.NewGuid().ToString("N"));
        CreatePrivateDirectory(stage);
        bool moved = false;
        try
        {
            WritePrivateNew(Path.Combine(stage, MetadataFileName), publicBytes);
            WritePrivateNew(Path.Combine(stage, PrivateFileName), protectedKey);
            using (var stored = OpenKey(stage, ReadLocalTrust(stage).Document.Keys[0]))
                VerifyRecoveryKey(stored, expected);
            UpdatePaths.EnsureNoReparsePoints(stage);
            UpdatePaths.EnsureNoReparsePoints(directory);
            Directory.Move(stage, directory);
            moved = true;
        }
        finally
        {
            if (!moved)
            {
                RemoveOwnedFile(Path.Combine(stage, PrivateFileName));
                RemoveOwnedFile(Path.Combine(stage, MetadataFileName));
                try
                {
                    UpdatePaths.EnsureNoReparsePoints(stage);
                    Directory.Delete(stage, recursive: false);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or UpdateException) { }
            }
        }
    }

    private static void RemoveOwnedFile(string path)
    {
        try
        {
            UpdatePaths.EnsureNoReparsePoints(path);
            File.Delete(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or UpdateException) { }
    }

    internal static ProductionSignerInfo RunRecoveryCommand(bool backup, string privateDirectory, string backupPath,
        string trustPath, string keyId)
    {
        string path = LocalPath(trustPath);
        UpdatePaths.EnsureNoReparsePoints(path);
        var trust = new UpdateTrustStore(UpdateManifestCodec.ParseStrict<UpdateTrustDocument>(ReadBounded(path, 64 * 1024), 64 * 1024));
        _ = RecoveryTrustKey(trust, keyId);
        if (Console.IsInputRedirected || Console.IsOutputRedirected || Console.IsErrorRedirected)
            throw Failure("UPDATE_SIGNER_INTERACTIVE_CONSOLE_REQUIRED");
        char[] first = GC.AllocateArray<char>(MaximumPassphraseLength, pinned: true);
        char[] second = GC.AllocateArray<char>(MaximumPassphraseLength, pinned: true);
        try
        {
            int length = ReadHidden(first, backup ? "New offline backup passphrase (16-256 characters): " : "Offline backup passphrase: ");
            var password = first.AsSpan(0, length);
            ValidatePassphrase(password);
            if (backup)
            {
                int repeated = ReadHidden(second, "Repeat the new passphrase: ");
                if (!CryptographicOperations.FixedTimeEquals(MemoryMarshal.AsBytes(password), MemoryMarshal.AsBytes(second.AsSpan(0, repeated))))
                    throw Failure("UPDATE_SIGNER_PASSPHRASE_MISMATCH");
                return Backup(privateDirectory, backupPath, trust, keyId, password);
            }
            return Restore(backupPath, privateDirectory, trust, keyId, password);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(first.AsSpan()));
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(second.AsSpan()));
        }
    }

    private static int ReadHidden(char[] buffer, string prompt)
    {
        Console.Error.Write(prompt);
        int length = 0;
        try
        {
            while (true)
            {
                ConsoleKeyInfo pressed = Console.ReadKey(intercept: true);
                if (pressed.Key == ConsoleKey.Enter) return length;
                if (pressed.Key == ConsoleKey.Escape) throw Failure("UPDATE_SIGNER_CANCELLED");
                if (pressed.Key == ConsoleKey.Backspace)
                {
                    if (length > 0) buffer[--length] = '\0';
                    continue;
                }
                if (char.IsControl(pressed.KeyChar)) continue;
                if (length == buffer.Length) throw Failure("UPDATE_SIGNER_PASSPHRASE_INVALID");
                buffer[length++] = pressed.KeyChar;
            }
        }
        finally { Console.Error.WriteLine(); }
    }

    private static void ValidatePassphrase(ReadOnlySpan<char> value)
    {
        if (value.Length is < 16 or > MaximumPassphraseLength || value.IsWhiteSpace()) throw Failure("UPDATE_SIGNER_PASSPHRASE_INVALID");
        foreach (char character in value)
            if (char.IsControl(character)) throw Failure("UPDATE_SIGNER_PASSPHRASE_INVALID");
    }

    private static UpdateTrustKey RecoveryTrustKey(UpdateTrustStore trust, string keyId)
    {
        if (trust.Document.Purpose != "production" || trust.Document.Version != 1 ||
            trust.Document.Keys.Count != 1 || trust.Document.Keys[0].Revoked || trust.Document.Keys[0].KeyId != keyId)
            throw Failure("UPDATE_SIGNER_TRUST_MISMATCH");
        return trust.Document.Keys[0];
    }

    private static void VerifyRecoveryKey(RSA rsa, UpdateTrustKey expected)
    {
        using var publicKey = RSA.Create();
        publicKey.ImportFromPem(expected.PublicKeyPem);
        if (rsa.KeySize < 3072 || !rsa.ExportSubjectPublicKeyInfo().AsSpan().SequenceEqual(publicKey.ExportSubjectPublicKeyInfo()))
            throw Failure("UPDATE_SIGNER_TRUST_MISMATCH");
        byte[] challenge = RandomNumberGenerator.GetBytes(32);
        byte[] response = rsa.SignData(challenge, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        if (!publicKey.VerifyData(challenge, response, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
            throw Failure("UPDATE_SIGNER_KEY_MISMATCH");
    }

    private static string RecoveryFile(string value, string directory)
    {
        string path = LocalPath(value);
        UpdatePaths.EnsureNoReparsePoints(path);
        if (!path.EndsWith(".encrypted.pk8", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw Failure("UPDATE_SIGNER_BACKUP_PATH_INVALID");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw Failure("UPDATE_SIGNER_PARENT_MISSING");
        RejectProjectOrInstallationPath(path);
        return path;
    }

    private static void RejectProjectOrInstallationPath(string path)
    {
        for (string? parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if (Directory.Exists(Path.Combine(parent, ".git")) || File.Exists(Path.Combine(parent, ".git")) ||
                File.Exists(Path.Combine(parent, "AutumnOS.slnx")) || File.Exists(Path.Combine(parent, "AutumnOS.exe")) ||
                File.Exists(Path.Combine(parent, "LOCAL_WORKFLOW.md")) && File.Exists(Path.Combine(parent, "CODEX_START.md")) ||
                string.Equals(Path.GetFileName(parent), "AutumnOS_Data", StringComparison.OrdinalIgnoreCase))
                throw Failure("UPDATE_SIGNER_BACKUP_PATH_INVALID");
        }
    }

    private static void ValidateBackupEncoding(byte[] encrypted)
    {
        try
        {
            var document = new AsnReader(encrypted, AsnEncodingRules.DER);
            var envelope = document.ReadSequence();
            var algorithm = envelope.ReadSequence();
            if (algorithm.ReadObjectIdentifier() != "1.2.840.113549.1.5.13") throw Failure("UPDATE_SIGNER_BACKUP_FORMAT_INVALID");
            var parameters = algorithm.ReadSequence();
            var derivation = parameters.ReadSequence();
            if (derivation.ReadObjectIdentifier() != "1.2.840.113549.1.5.12") throw Failure("UPDATE_SIGNER_BACKUP_FORMAT_INVALID");
            var kdf = derivation.ReadSequence();
            byte[] salt = kdf.ReadOctetString();
            if (salt.Length is < 16 or > 64 || !kdf.TryReadInt32(out int rounds) || rounds != BackupIterations)
                throw Failure("UPDATE_SIGNER_BACKUP_FORMAT_INVALID");
            if (kdf.HasData && kdf.PeekTag().HasSameClassAndValue(new Asn1Tag(UniversalTagNumber.Integer)) &&
                (!kdf.TryReadInt32(out int keyBytes) || keyBytes != 32)) throw Failure("UPDATE_SIGNER_BACKUP_FORMAT_INVALID");
            var prf = kdf.ReadSequence();
            if (prf.ReadObjectIdentifier() != "1.2.840.113549.2.9") throw Failure("UPDATE_SIGNER_BACKUP_FORMAT_INVALID");
            if (prf.HasData) prf.ReadNull();
            prf.ThrowIfNotEmpty(); kdf.ThrowIfNotEmpty(); derivation.ThrowIfNotEmpty();
            var cipher = parameters.ReadSequence();
            if (cipher.ReadObjectIdentifier() != "2.16.840.1.101.3.4.1.42" || cipher.ReadOctetString().Length != 16)
                throw Failure("UPDATE_SIGNER_BACKUP_FORMAT_INVALID");
            cipher.ThrowIfNotEmpty(); parameters.ThrowIfNotEmpty(); algorithm.ThrowIfNotEmpty();
            byte[] ciphertext = envelope.ReadOctetString();
            if (ciphertext.Length == 0 || ciphertext.Length % 16 != 0) throw Failure("UPDATE_SIGNER_BACKUP_FORMAT_INVALID");
            envelope.ThrowIfNotEmpty(); document.ThrowIfNotEmpty();
        }
        catch (AsnContentException) { throw Failure("UPDATE_SIGNER_BACKUP_FORMAT_INVALID"); }
    }
}
