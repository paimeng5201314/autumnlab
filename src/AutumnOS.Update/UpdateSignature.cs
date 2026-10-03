using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;

namespace AutumnOS.Update;

public sealed class UpdateTrustStore
{
    public UpdateTrustDocument Document { get; }
    public bool IsConfigured => Document.Keys.Any(k => !k.Revoked);
    public static bool IsTestBuild
    {
        get
        {
#if AUTUMNOS_UPDATE_TEST_BUILD
            return true;
#else
            return false;
#endif
        }
    }
    public UpdateTrustStore(UpdateTrustDocument document)
    {
        if (document.SchemaVersion != 1 || document.Version < 1 || document.Purpose is not ("production" or "local-test") || document.Keys is null || document.Keys.Count > 16)
            throw new UpdateException("UPDATE_TRUST_INVALID");
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var key in document.Keys)
        {
            if (key is null || !UpdateManifestCodec.Identifier(key.KeyId) || !ids.Add(key.KeyId) ||
                key.PublicKeyPem is not { Length: < 8192 } || !key.PublicKeyPem.StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) ||
                key.PublicKeyPem.Contains("PRIVATE", StringComparison.Ordinal)) throw new UpdateException("UPDATE_TRUST_INVALID");
            try { using var rsa = RSA.Create(); rsa.ImportFromPem(key.PublicKeyPem); if (rsa.KeySize < 3072) throw new UpdateException("UPDATE_KEY_TOO_SMALL"); }
            catch (Exception e) when (e is ArgumentException or CryptographicException) { throw new UpdateException("UPDATE_TRUST_INVALID", e); }
        }
        Document = document;
    }
    public static UpdateTrustStore FromEmbedded()
    {
        string purpose = IsTestBuild ? "local-test" : "production";
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(IsTestBuild ? "AutumnOS.Update.TestTrust.json" : "AutumnOS.Update.ProductionTrust.json");
        if (stream is null) return new(new(1, 1, purpose, []));
        if (stream.Length > 64 * 1024) throw new UpdateException("UPDATE_TRUST_INVALID");
        using MemoryStream bytes = new(); stream.CopyTo(bytes);
        var document = UpdateManifestCodec.ParseStrict<UpdateTrustDocument>(bytes.ToArray(), 64 * 1024);
        if (document.Purpose != purpose) throw new UpdateException("UPDATE_TRUST_PURPOSE_MISMATCH");
        return new(document);
    }
    internal UpdateTrustKey Find(string keyId, int rootVersion)
    {
        if (!IsConfigured) throw new UpdateException("UPDATE_PRODUCTION_TRUST_NOT_CONFIGURED");
        if (rootVersion != Document.Version) throw new UpdateException("UPDATE_TRUST_ROOT_VERSION");
        var key = Document.Keys.SingleOrDefault(k => k.KeyId == keyId) ?? throw new UpdateException("UPDATE_UNKNOWN_KEY");
        if (key.Revoked) throw new UpdateException("UPDATE_REVOKED_KEY");
        return key;
    }
}

public static class UpdateSignature
{
    public const string Algorithm = "RSA-PSS-SHA256";
    public static byte[] Create(byte[] manifestBytes, RSA privateKey, string keyId, int rootVersion)
    {
        var manifest = UpdateManifestCodec.Parse(manifestBytes);
        if (manifest.KeyId != keyId || manifest.TrustRootVersion != rootVersion) throw new UpdateException("UPDATE_SIGNATURE_IDENTITY_MISMATCH");
        if (privateKey.KeySize < 3072) throw new UpdateException("UPDATE_KEY_TOO_SMALL");
        return JsonSerializer.SerializeToUtf8Bytes(new UpdateSignatureEnvelope(1, Algorithm, keyId, rootVersion,
            Convert.ToBase64String(privateKey.SignData(manifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))), UpdateManifestCodec.JsonOptions);
    }
    public static UpdateManifest Verify(byte[] manifestBytes, byte[] signatureBytes, UpdateTrustStore trust,
        DateTimeOffset now, long minSequence = 0)
    {
        var manifest = UpdateManifestCodec.Parse(manifestBytes);
        var signature = UpdateManifestCodec.ParseStrict<UpdateSignatureEnvelope>(signatureBytes, 16 * 1024);
        if (signature.SchemaVersion != 1 || signature.Algorithm != Algorithm || signature.KeyId != manifest.KeyId || signature.TrustRootVersion != manifest.TrustRootVersion)
            throw new UpdateException("UPDATE_SIGNATURE_IDENTITY_MISMATCH");
        var key = trust.Find(signature.KeyId, signature.TrustRootVersion);
        try
        {
            byte[] signed = Convert.FromBase64String(signature.Signature);
            using var rsa = RSA.Create(); rsa.ImportFromPem(key.PublicKeyPem);
            if (!rsa.VerifyData(manifestBytes, signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw new UpdateException("UPDATE_SIGNATURE_INVALID");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentException)
        { throw new UpdateException("UPDATE_SIGNATURE_INVALID", ex); }
        if (now < new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero) || manifest.IssuedAtUtc > now.AddMinutes(5)) throw new UpdateException("UPDATE_CLOCK_OR_FUTURE_METADATA");
        if (manifest.ExpiresAtUtc <= now) throw new UpdateException("UPDATE_METADATA_EXPIRED");
        if (manifest.Sequence < minSequence) throw new UpdateException("UPDATE_METADATA_REPLAY");
        return manifest;
    }
}
