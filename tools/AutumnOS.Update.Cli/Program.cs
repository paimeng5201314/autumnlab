using System.IO.Compression;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using AutumnOS.Update;
using AutumnOS.Update.Cli;
using AutumnOS.Store;

try
{
    if (args.Length == 0) throw new ArgumentException("命令：keygen-test、init-production、export-production-public、inventory、prepare、prepare-draft、bind-release、sign、sign-production、verify。参数格式：--名称 值。");
    var values = new Dictionary<string, string>(StringComparer.Ordinal);
    for (int i = 1; i < args.Length; i += 2)
        if (i + 1 >= args.Length || !args[i].StartsWith("--") || !values.TryAdd(args[i][2..], args[i + 1])) throw new ArgumentException("Invalid arguments.");
    string Get(string key) => values.TryGetValue(key, out string? value) ? value : throw new ArgumentException("Missing --" + key);
    string Optional(string key, string fallback) => values.GetValueOrDefault(key, fallback);
    void AllowOnly(params string[] names)
    {
        if (values.Keys.Any(key => !names.Contains(key, StringComparer.Ordinal))) throw new ArgumentException("未知参数。");
    }
    void WriteNew(string path, byte[] content)
    {
        UpdatePaths.EnsureNoReparsePoints(path);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(content); output.Flush(true);
    }
    if (args[0] == "keygen-test")
    {
        AllowOnly();
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
    else if (args[0] == "init-production")
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        AllowOnly("private-directory", "public-output", "key-id");
        Console.WriteLine(JsonSerializer.Serialize(ProductionUpdateSigner.Init(Get("private-directory"), Get("public-output"), Get("key-id"))));
    }
    else if (args[0] == "export-production-public")
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        AllowOnly("private-directory", "public-output");
        Console.WriteLine(JsonSerializer.Serialize(ProductionUpdateSigner.ExportPublic(Get("private-directory"), Get("public-output"))));
    }
    else if (args[0] is "prepare" or "prepare-draft")
    {
        bool draft = args[0] == "prepare-draft";
        AllowOnly("source", "output", "version", "build-id", "channel", "sequence", "valid-days", "key-id", "root-version", "release-id", "asset-id", "release-tag", "minimum-updater-version", "previous-manifest");
        if (draft && (values.ContainsKey("release-id") || values.ContainsKey("asset-id"))) throw new ArgumentException("Drafts cannot claim GitHub asset identities.");
        string source = Path.GetFullPath(Get("source")), output = Path.GetFullPath(Get("output"));
        if (Directory.Exists(output)) throw new IOException("Output already exists; previous materials preserved.");
        var files = UpdatePayload.CreateFileList(source);
        var removed = values.TryGetValue("previous-manifest", out string? previousPath)
            ? UpdateManifestCodec.Parse(File.ReadAllBytes(previousPath)).Files.Select(file => file.Path)
                .Except(files.Select(file => file.Path), StringComparer.OrdinalIgnoreCase).ToArray()
            : [];
        string asset = "AutumnOS-" + Get("version").Replace('+', '_') + "-win-x64-payload.zip";
        var now = DateTimeOffset.UtcNow;
        // Validate all caller-controlled identity fields before constructing any output file.
        var manifest = new UpdateManifest(1, Get("channel"), Get("version"), Get("build-id"), "win-x64", Optional("minimum-updater-version", UpdateService.UpdaterVersion), long.Parse(Get("sequence")), now,
            now.AddDays(int.Parse(Optional("valid-days", "7"))), Get("key-id"), int.Parse(Optional("root-version", "1")),
            new(asset, 1, new string('0', 64), draft ? 1 : long.Parse(Get("release-id")), draft ? 1 : long.Parse(Get("asset-id"))), files, removed, new(1, 1, true),
            ReleaseTag: values.GetValueOrDefault("release-tag"));
        UpdateManifestCodec.Validate(manifest);
        UpdatePaths.EnsureNoReparsePoints(output); Directory.CreateDirectory(output);
        string payload = Path.Combine(output, asset);
        using (var zip = ZipFile.Open(payload, ZipArchiveMode.Create))
            foreach (var file in files) zip.CreateEntryFromFile(Path.Combine(source, file.Path), file.Path, CompressionLevel.Optimal);
        string digest; using (var stream = File.OpenRead(payload)) digest = Convert.ToHexStringLower(SHA256.HashData(stream));
        manifest = manifest with { Payload = manifest.Payload with { Bytes = new FileInfo(payload).Length, Sha256 = digest } };
        await UpdatePayload.VerifyAsync(payload, manifest);
        // An unbound draft is deliberately not installable or signable: real release
        // identities only become available after a separately authorized publication.
        byte[] bytes = draft ? JsonSerializer.SerializeToUtf8Bytes(manifest with { Payload = manifest.Payload with { ReleaseId = 0, AssetId = 0 } }, UpdateManifestCodec.JsonOptions)
            : UpdateManifestCodec.Serialize(manifest);
        WriteNew(Path.Combine(output, draft ? "autumn.update.draft.json" : "autumn.update.json"), bytes);
        Console.WriteLine(JsonSerializer.Serialize(new { output, payload, files = files.Count, release_binding = draft ? "not_assigned" : "assigned", signature = "not_created" }));
    }
    else if (args[0] == "bind-release")
    {
        // Preparing a ZIP precedes obtaining its GitHub asset ID. Bind the final IDs
        // locally without regenerating frozen payload bytes, then sign this new file.
        // This command never contacts GitHub or creates/modifies a remote Release.
        AllowOnly("manifest", "payload", "output", "release-id", "asset-id", "release-tag");
        var manifest = UpdateManifestCodec.ParseStrict<UpdateManifest>(File.ReadAllBytes(Get("manifest")), UpdateManifestCodec.MaximumManifestBytes);
        manifest = manifest with
        {
            ReleaseTag = Get("release-tag"),
            Payload = manifest.Payload with { ReleaseId = long.Parse(Get("release-id")), AssetId = long.Parse(Get("asset-id")) }
        };
        byte[] rebound = UpdateManifestCodec.Serialize(manifest);
        await UpdatePayload.VerifyAsync(Get("payload"), manifest);
        WriteNew(Get("output"), rebound);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "bound", manifest.ReleaseTag, manifest.Payload.ReleaseId, manifest.Payload.AssetId,
            manifest.Version, manifest.MinimumUpdaterVersion, manifest.Payload.Sha256, payload_changed = false, signature = "not_created" }));
    }
    else if (args[0] == "inventory")
    {
        AllowOnly("source", "output");
        string source = Path.GetFullPath(Get("source")), output = Path.GetFullPath(Get("output"));
        var files = UpdatePayload.CreateFileList(source);
        WriteNew(output, JsonSerializer.SerializeToUtf8Bytes(files, UpdateManifestCodec.JsonOptions));
        Console.WriteLine(JsonSerializer.Serialize(new { output, files = files.Count }));
    }
    else if (args[0] == "sign")
    {
        AllowOnly("manifest", "private-key-file", "output");
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
    else if (args[0] == "sign-production")
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        AllowOnly("manifest", "private-directory", "trust", "output");
        var trust = new UpdateTrustStore(UpdateManifestCodec.ParseStrict<UpdateTrustDocument>(File.ReadAllBytes(Get("trust")), 65536));
        WriteNew(Get("output"), ProductionUpdateSigner.Sign(File.ReadAllBytes(Get("manifest")), Get("private-directory"), trust));
        Console.WriteLine("Production update manifest signed. Private key was not exported or printed; Windows Authenticode is separate.");
    }
    else if (args[0] == "check-production")
    {
        AllowOnly("data-directory", "current-version", "build-id", "preview");
        string data = Path.GetFullPath(Get("data-directory"));
        UpdatePaths.EnsureNoReparsePoints(data);
        if (Directory.Exists(data) || File.Exists(data)) throw new IOException("A new isolated diagnostic directory is required.");
        var embedded = UpdateTrustStore.FromEmbedded();
        if (!embedded.IsConfigured || embedded.Document.Purpose != "production" || UpdateTrustStore.IsTestBuild)
            throw new UpdateException("UPDATE_PRODUCTION_TRUST_NOT_CONFIGURED");
        Directory.CreateDirectory(data);
        using var service = UpdateService.CreateDefault(data, Get("current-version"), Get("build-id"), () => new StoreNetworkSettings());
        await service.CheckAsync(bool.Parse(Optional("preview", "false")));
        var state = service.Snapshot;
        var report = new { kind = "headless_real_update_service_metadata_check_not_GUI_or_install", repository = UpdateService.Repository,
            state.AutomaticInstallConfigured, state.LastReleaseMetadataUtc, state.ReleasesExamined, state.CheckComplete,
            state.LastSuccessfulCheckUtc, state.ErrorCode, state.Warnings, candidate = state.Candidate?.Tag,
            state.BytesReceived, state.TotalBytes, installation = "not_run" };
        byte[] reportBytes = JsonSerializer.SerializeToUtf8Bytes(report, UpdateManifestCodec.JsonOptions);
        WriteNew(Path.Combine(data, "check-result.json"), reportBytes);
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(reportBytes));
        return state.LastReleaseMetadataUtc.HasValue ? 0 : 3;
    }
    else if (args[0] == "verify")
    {
        AllowOnly("manifest", "signature", "trust", "payload", "minimum-sequence");
        var trust = new UpdateTrustStore(UpdateManifestCodec.ParseStrict<UpdateTrustDocument>(File.ReadAllBytes(Get("trust")), 64 * 1024));
        var manifest = UpdateSignature.Verify(File.ReadAllBytes(Get("manifest")), File.ReadAllBytes(Get("signature")), trust, DateTimeOffset.UtcNow, long.Parse(Optional("minimum-sequence", "0")));
        await UpdatePayload.VerifyAsync(Get("payload"), manifest);
        Console.WriteLine(JsonSerializer.Serialize(new { status = "passed", manifest.Version, manifest.BuildId, manifest.ReleaseTag,
            manifest.MinimumUpdaterVersion, manifest.Payload.ReleaseId, manifest.Payload.AssetId, trust_purpose = trust.Document.Purpose }));
    }
    else throw new ArgumentException("Unknown command.");
    return 0;
}
catch (Exception error) when (error is not OutOfMemoryException)
{
    Console.Error.WriteLine(error is UpdateException update ? update.Code : "UPDATE_TOOL_FAILED: " + error.GetType().Name);
    return 2;
}
