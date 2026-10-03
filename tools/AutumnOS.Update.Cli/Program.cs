using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using AutumnOS.Update;

try
{
    if (args.Length == 0) throw new ArgumentException("Commands: keygen-test, inventory, prepare, sign, verify. Arguments use --name value.");
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i += 2)
        if (i + 1 >= args.Length || !args[i].StartsWith("--") || !values.TryAdd(args[i][2..], args[i + 1])) throw new ArgumentException("Invalid arguments.");
    string Get(string key) => values.TryGetValue(key, out string? value) ? value : throw new ArgumentException("Missing --" + key);
    string Optional(string key, string fallback) => values.GetValueOrDefault(key, fallback);
    void WriteNew(string path, byte[] content)
    {
        UpdatePaths.EnsureNoReparsePoints(path);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(content); output.Flush(true);
    }
    if (args[0] == "keygen-test")
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        string directory = Path.Combine(Path.GetTempPath(), "AutumnOS-T05-signing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
        using var key = RSA.Create(3072);
        WriteNew(Path.Combine(directory, "test-private.pk8"), key.ExportPkcs8PrivateKey());
        var trust = new UpdateTrustDocument(1, 1, "local-test", [new("local-drill", key.ExportSubjectPublicKeyInfoPem())]);
        WriteNew(Path.Combine(directory, "test-public.json"), JsonSerializer.SerializeToUtf8Bytes(trust, UpdateManifestCodec.JsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(new { directory, purpose = "local-test", private_key_exported_to_restricted_temporary_directory = true, cleanup_required = true }));
    }
    else if (args[0] == "prepare")
    {
        string source = Path.GetFullPath(Get("source")), output = Path.GetFullPath(Get("output"));
        if (Directory.Exists(output)) throw new IOException("Output already exists; previous materials preserved.");
        var files = UpdatePayload.CreateFileList(source);
        string asset = "AutumnOS-" + Get("version").Replace('+', '_') + "-win-x64-payload.zip";
        var now = DateTimeOffset.UtcNow;
        // Validate all caller-controlled identity fields before constructing any output file.
        var manifest = new UpdateManifest(1, Get("channel"), Get("version"), Get("build-id"), "win-x64", "1.0.0", long.Parse(Get("sequence")), now,
            now.AddDays(int.Parse(Optional("valid-days", "7"))), Get("key-id"), int.Parse(Optional("root-version", "1")),
            new(asset, 1, new string('0', 64), long.Parse(Get("release-id")), long.Parse(Get("asset-id"))), files, [], new(1, 1, true));
        UpdateManifestCodec.Validate(manifest);
        UpdatePaths.EnsureNoReparsePoints(output); Directory.CreateDirectory(output);
        string payload = Path.Combine(output, asset);
        using (var zip = ZipFile.Open(payload, ZipArchiveMode.Create))
            foreach (var file in files) zip.CreateEntryFromFile(Path.Combine(source, file.Path), file.Path, CompressionLevel.Optimal);
        string digest; using (var stream = File.OpenRead(payload)) digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        manifest = manifest with { Payload = manifest.Payload with { Bytes = new FileInfo(payload).Length, Sha256 = digest } };
        byte[] bytes = UpdateManifestCodec.Serialize(manifest);
        WriteNew(Path.Combine(output, "autumn.update.json"), bytes);
        await UpdatePayload.VerifyAsync(payload, manifest);
        Console.WriteLine(JsonSerializer.Serialize(new { output, payload, files = files.Count, signature = "not_created" }));
    }
    else if (args[0] == "inventory")
    {
        if (values.Keys.Any(key => key is not ("source" or "output"))) throw new ArgumentException("Unknown inventory argument.");
        string source = Path.GetFullPath(Get("source")), output = Path.GetFullPath(Get("output"));
        var files = UpdatePayload.CreateFileList(source);
        WriteNew(output, JsonSerializer.SerializeToUtf8Bytes(files, UpdateManifestCodec.JsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(new { output, files = files.Count }));
    }
    else if (args[0] == "sign")
    {
        byte[] manifestBytes = File.ReadAllBytes(Get("manifest"));
        var manifest = UpdateManifestCodec.Parse(manifestBytes);
        byte[] keyBytes = File.ReadAllBytes(Get("private-key-file"));
        try
        {
            using var key = RSA.Create(); key.ImportPkcs8PrivateKey(keyBytes, out int read);
            if (read != keyBytes.Length) throw new CryptographicException("Invalid PKCS8 file.");
            WriteNew(Get("output"), UpdateSignature.Create(manifestBytes, key, manifest.KeyId, manifest.TrustRootVersion));
        }
        finally { CryptographicOperations.ZeroMemory(keyBytes); }
        Console.WriteLine("Signature created; private key material was not printed.");
    }
    else if (args[0] == "verify")
    {
        var trust = new UpdateTrustStore(UpdateManifestCodec.ParseStrict<UpdateTrustDocument>(File.ReadAllBytes(Get("trust")), 64 * 1024));
        var manifest = UpdateSignature.Verify(File.ReadAllBytes(Get("manifest")), File.ReadAllBytes(Get("signature")), trust, DateTimeOffset.UtcNow, long.Parse(Optional("minimum-sequence", "0")));
        await UpdatePayload.VerifyAsync(Get("payload"), manifest);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", manifest.Version, manifest.BuildId, trust_purpose = trust.Document.Purpose }));
    }
    else throw new ArgumentException("Unknown command.");
    return 0;
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    Console.Error.WriteLine(error is UpdateException update ? update.Code : "UPDATE_TOOL_FAILED: " + error.GetType().Name);
    return 2;
}
