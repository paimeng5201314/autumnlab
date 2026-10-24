using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using AutumnOS.Update;
using AutumnOS.Update.Cli;

namespace AutumnOS.Tests;

// Own temporary signing identities only; no production key, trust-store change,
// Authenticode signature, remote publication or product GUI is involved.
[SupportedOSPlatform("windows")]
internal static class ProductionUpdateSignerTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("production_signer.dpapi_public_root_and_manifest_signature_roundtrip", () => InScope(root =>
        {
            var f = Init(root);
            Check(f.Trust.Document.Purpose == "production" && f.Trust.IsConfigured, "A real public production-purpose root must be exported.");
            byte[] manifest = Manifest("fixture-key");
            byte[] signature = ProductionUpdateSigner.Sign(manifest, f.Private, f.Trust);
            Check(UpdateSignature.Verify(manifest, signature, f.Trust, DateTimeOffset.UtcNow).BuildId == "T06-signing-fixture", "Signature must verify against exported public root.");
            Reject(() => UpdateSignature.Verify([.. manifest, (byte)' '], signature, f.Trust, DateTimeOffset.UtcNow), "UPDATE_SIGNATURE_INVALID");
            byte[] protectedKey = File.ReadAllBytes(Path.Combine(f.Private, ProductionUpdateSigner.PrivateFileName));
            using var rsa = RSA.Create();
            bool refused = false;
            try { rsa.ImportPkcs8PrivateKey(protectedKey, out _); }
            catch (CryptographicException) { refused = true; }
            Check(refused, "Persisted key must not be naked PKCS8.");
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(f.Public)).Contains("PRIVATE", StringComparison.Ordinal), "Only the public key may leave the private directory.");
        }));
        yield return ("production_signer.private_directory_and_file_allow_only_current_sid", () => InScope(root =>
        {
            var f = Init(root); var current = WindowsIdentity.GetCurrent().User!;
            var directoryAcl = new DirectoryInfo(f.Private).GetAccessControl();
            Check(directoryAcl.AreAccessRulesProtected && directoryAcl.GetOwner(typeof(SecurityIdentifier)) == current, "Private directory must have current-user owner and protected DACL.");
            foreach (FileSystemAccessRule rule in directoryAcl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                Check(rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference == current, "Other identities cannot be allowed private directory access.");
            foreach (FileSystemAccessRule rule in new FileInfo(Path.Combine(f.Private, ProductionUpdateSigner.PrivateFileName)).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)))
                Check(rule.AccessControlType != AccessControlType.Allow || rule.IdentityReference == current, "Private key must inherit only the current-user access rule.");
        }));
        yield return ("production_signer.existing_public_file_rejected_before_key_creation", () => InScope(root =>
        {
            string target = Path.Combine(root, "existing.json"), key = Path.Combine(root, "private-new");
            File.WriteAllText(target, "owned-existing-public-content");
            Reject(() => ProductionUpdateSigner.Init(key, target, "fixture-key"), "UPDATE_SIGNER_PUBLIC_EXISTS");
            Check(File.ReadAllText(target) == "owned-existing-public-content" && !Directory.Exists(key), "Existing public file and absence of a key directory must be retained.");
        }));
        yield return ("production_signer.existing_identity_is_never_regenerated", () => InScope(root =>
        {
            var f = Init(root); byte[] previous = File.ReadAllBytes(Path.Combine(f.Private, ProductionUpdateSigner.PrivateFileName));
            string anotherPublic = Path.Combine(root, "another.json");
            Reject(() => ProductionUpdateSigner.Init(f.Private, anotherPublic, "replacement"), "UPDATE_SIGNER_DIRECTORY_EXISTS");
            Check(!File.Exists(anotherPublic) && File.ReadAllBytes(Path.Combine(f.Private, ProductionUpdateSigner.PrivateFileName)).SequenceEqual(previous), "Retry cannot rotate or overwrite an existing private identity.");
        }));
        yield return ("production_signer.explicit_public_reexport_preserves_identity", () => InScope(root =>
        {
            var f = Init(root); string exported = Path.Combine(root, "recovered-public.json");
            var info = ProductionUpdateSigner.ExportPublic(f.Private, exported);
            Check(info == f.Info && File.ReadAllBytes(exported).SequenceEqual(File.ReadAllBytes(f.Public)), "Explicit recovery must export the original bound public identity.");
            Reject(() => ProductionUpdateSigner.ExportPublic(f.Private, exported), "UPDATE_SIGNER_PUBLIC_EXISTS");
        }));
        yield return ("production_signer_failed_public_write_retains_recoverable_protected_key", () => InScope(root =>
        {
            string destination = Path.Combine(root, "public-destination"), key = Path.Combine(root, "private");
            Directory.CreateDirectory(destination);
            var directory = new DirectoryInfo(destination); var original = directory.GetAccessControl();
            var blocked = directory.GetAccessControl();
            blocked.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.CreateFiles, AccessControlType.Deny));
            directory.SetAccessControl(blocked);
            string output = Path.Combine(destination, "public.json");
            try
            {
                bool failed = false;
                try { _ = ProductionUpdateSigner.Init(key, output, "fixture-key"); }
                catch (UnauthorizedAccessException) { failed = true; }
                Check(failed && !File.Exists(output), "Public write denial must propagate, not claim completed initialization.");
                Check(File.Exists(Path.Combine(key, ProductionUpdateSigner.PrivateFileName)) && File.Exists(Path.Combine(key, ProductionUpdateSigner.MetadataFileName)),
                    "The already protected identity must survive public-output failure.");
            }
            finally { RestoreAccess(directory, original); }
            _ = ProductionUpdateSigner.ExportPublic(key, output);
            var trust = new UpdateTrustStore(UpdateManifestCodec.ParseStrict<UpdateTrustDocument>(File.ReadAllBytes(output), 64 * 1024));
            byte[] bytes = Manifest("fixture-key"), signature = ProductionUpdateSigner.Sign(bytes, key, trust);
            Check(UpdateSignature.Verify(bytes, signature, trust, DateTimeOffset.UtcNow).KeyId == "fixture-key", "Recovering public output must preserve the usable original signing identity.");
        }));
        yield return ("production_signer_wrong_root_and_test_purpose_cannot_authorize_signature", () => InScope(root =>
        {
            var f = Init(root); byte[] manifest = Manifest("fixture-key");
            using var other = RSA.Create(3072);
            var wrong = new UpdateTrustStore(new(1, 1, "production", [new("fixture-key", other.ExportSubjectPublicKeyInfoPem())]));
            Reject(() => ProductionUpdateSigner.Sign(manifest, f.Private, wrong), "UPDATE_SIGNER_TRUST_MISMATCH");
            var test = new UpdateTrustStore(f.Trust.Document with { Purpose = "local-test" });
            Reject(() => ProductionUpdateSigner.Sign(manifest, f.Private, test), "UPDATE_SIGNER_TRUST_MISMATCH");
            var revoked = new UpdateTrustStore(f.Trust.Document with { Keys = [f.Trust.Document.Keys[0] with { Revoked = true }] });
            Reject(() => ProductionUpdateSigner.Sign(manifest, f.Private, revoked), "UPDATE_SIGNER_TRUST_MISMATCH");
        }));
        yield return ("production_signer_manifest_key_and_version_must_match", () => InScope(root =>
        {
            var f = Init(root);
            Reject(() => ProductionUpdateSigner.Sign(Manifest("another-key"), f.Private, f.Trust), "UPDATE_SIGNER_TRUST_MISMATCH");
            var manifest = UpdateManifestCodec.Parse(Manifest("fixture-key")) with { TrustRootVersion = 2 };
            Reject(() => ProductionUpdateSigner.Sign(UpdateManifestCodec.Serialize(manifest), f.Private, f.Trust), "UPDATE_SIGNER_TRUST_MISMATCH");
        }));
        yield return ("production_signer_modified_local_public_metadata_does_not_rebind_key", () => InScope(root =>
        {
            var f = Init(root); using var other = RSA.Create(3072);
            var changed = new UpdateTrustStore(new(1, 1, "production", [new("fixture-key", other.ExportSubjectPublicKeyInfoPem())]));
            File.WriteAllBytes(Path.Combine(f.Private, ProductionUpdateSigner.MetadataFileName), System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(changed.Document, UpdateManifestCodec.JsonOptions));
            Reject(() => ProductionUpdateSigner.Sign(Manifest("fixture-key"), f.Private, changed), "UPDATE_SIGNER_KEY_MISMATCH");
            Reject(() => ProductionUpdateSigner.ExportPublic(f.Private, Path.Combine(root, "forged.json")), "UPDATE_SIGNER_KEY_MISMATCH");
            Check(!File.Exists(Path.Combine(root, "forged.json")), "Invalid local public identity must never be exported.");
        }));
        yield return ("production_signer_tampered_ciphertext_fails_without_signature", () => InScope(root =>
        {
            var f = Init(root); string path = Path.Combine(f.Private, ProductionUpdateSigner.PrivateFileName);
            byte[] ciphertext = File.ReadAllBytes(path); ciphertext[^1] ^= 0xff; File.WriteAllBytes(path, ciphertext);
            bool failed = false;
            try { _ = ProductionUpdateSigner.Sign(Manifest("fixture-key"), f.Private, f.Trust); }
            catch (CryptographicException) { failed = true; }
            Check(failed, "DPAPI must reject an altered protected blob.");
        }));
        yield return ("production_signer_relaxed_private_acl_is_rejected_without_rewriting_it", () => InScope(root =>
        {
            var f = Init(root); var directory = new DirectoryInfo(f.Private); var original = directory.GetAccessControl();
            var changed = directory.GetAccessControl(); changed.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
            directory.SetAccessControl(changed);
            try
            {
                Reject(() => ProductionUpdateSigner.Sign(Manifest("fixture-key"), f.Private, f.Trust), "UPDATE_SIGNER_ACL_INVALID");
                Check(directory.GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r => r.IdentityReference.Value == "S-1-1-0"), "Signer must not silently alter an existing ACL.");
            }
            finally { RestoreAccess(directory, original); }
        }));
        yield return ("production_signer_invalid_paths_and_key_id_create_no_identity", () => InScope(root =>
        {
            string key = Path.Combine(root, "not-created");
            Reject(() => ProductionUpdateSigner.Init(key, Path.Combine(root, "public.json"), "unsafe/key"), "UPDATE_SIGNER_KEY_ID_INVALID");
            Reject(() => ProductionUpdateSigner.Init("relative", Path.Combine(root, "public.json"), "fixture-key"), "UPDATE_SIGNER_PATH_INVALID");
            Reject(() => ProductionUpdateSigner.Init(key, Path.Combine(key, "public.json"), "fixture-key"), "UPDATE_SIGNER_PUBLIC_PATH_INVALID");
            Reject(() => ProductionUpdateSigner.Init(key, Path.Combine(root, "missing", "public.json"), "fixture-key"), "UPDATE_SIGNER_PARENT_MISSING");
            Reject(() => ProductionUpdateSigner.Init(@"\\invalid-owned-fixture.invalid\share\key", Path.Combine(root, "public.json"), "fixture-key"), "UPDATE_SIGNER_PATH_INVALID");
            Reject(() => ProductionUpdateSigner.Init(key, Path.Combine(root, "owned-file:stream.json"), "fixture-key"), "UPDATE_SIGNER_PATH_INVALID");
            Reject(() => ProductionUpdateSigner.Init(key + ".", Path.Combine(root, "public.json"), "fixture-key"), "UPDATE_SIGNER_PATH_INVALID");
            Reject(() => ProductionUpdateSigner.Init(key + " ", Path.Combine(root, "public.json"), "fixture-key"), "UPDATE_SIGNER_PATH_INVALID");
            Reject(() => ProductionUpdateSigner.Init(key, Path.Combine(root, "NUL.json"), "fixture-key"), "UPDATE_SIGNER_PATH_INVALID");
            string samePath = Path.Combine(root, "same.json");
            Reject(() => ProductionUpdateSigner.Init(samePath, samePath, "fixture-key"), "UPDATE_SIGNER_PUBLIC_PATH_INVALID");
            Check(!Directory.Exists(key), "Invalid inputs cannot leave a partial signing identity.");
        }));
    }

    private sealed record Fixture(string Private, string Public, UpdateTrustStore Trust, ProductionSignerInfo Info);
    private static Fixture Init(string root)
    {
        string directory = Path.Combine(root, "private"), output = Path.Combine(root, "public.json");
        var info = ProductionUpdateSigner.Init(directory, output, "fixture-key");
        return new(directory, output, new(UpdateManifestCodec.ParseStrict<UpdateTrustDocument>(File.ReadAllBytes(output), 64 * 1024)), info);
    }
    private static byte[] Manifest(string keyId)
    {
        var now = DateTimeOffset.UtcNow;
        return UpdateManifestCodec.Serialize(new(1, "plus", "1.0.1", "T06-signing-fixture", "win-x64", "1.1.0", 1,
            now, now.AddDays(1), keyId, 1, new("owned-payload.zip", 1, new string('a', 64), 101, 102),
            [new("AutumnOS.Client.exe", 1, new string('b', 64))], [], new(1, 1, true)));
    }
    private static void Reject(Action action, string code)
    {
        try { action(); }
        catch (UpdateException error) when (error.Code == code) { return; }
        throw new InvalidOperationException("Expected fixed signer failure: " + code);
    }
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
    private static void RestoreAccess(DirectoryInfo directory, DirectorySecurity original)
    {
        // SetAccessControl persists sections marked modified. A descriptor merely
        // read before the test has no modified flags and cannot undo the test ACE.
        var restore = new DirectorySecurity();
        restore.SetSecurityDescriptorBinaryForm(original.GetSecurityDescriptorBinaryForm(), AccessControlSections.Access);
        directory.SetAccessControl(restore);
    }
    private static void InScope(Action<string> action)
    {
        string parent = Path.GetFullPath(Path.GetTempPath());
        string name = "AutumnOS-production-signer-fixture-" + Guid.NewGuid().ToString("N");
        string root = Path.GetFullPath(Path.Combine(parent, name));
        Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            if (Path.GetDirectoryName(root) != Path.TrimEndingDirectorySeparator(parent) || Path.GetFileName(root) != name)
                throw new InvalidOperationException("Owned fixture root changed.");
            UpdatePaths.EnsureNoReparsePoints(root);
            Directory.Delete(root, recursive: true);
        }
    }
}
