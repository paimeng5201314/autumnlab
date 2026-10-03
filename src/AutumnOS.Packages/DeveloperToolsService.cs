using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AutumnOS.Packages;

public sealed record DeveloperBuild(string PackagePath, PackageInspection Inspection);
public sealed record DeveloperSdkCall(DateTimeOffset Timestamp, string Method, string ResultCode);
public sealed record DeveloperReleaseCheck(string AppId, string Version, string Channel, string Sha256);
public sealed record DeveloperProject(string ProjectDirectory, string Template, PackageInspection Inspection);

/// <summary>Local bounded tools, never a host command runner. Disabling cancels all outstanding capabilities.</summary>
public sealed class DeveloperToolsService : IDisposable
{
    private readonly object _gate = new();
    private readonly Queue<DeveloperSdkCall> _trace = [];
    private CancellationTokenSource _capabilities = new();
    private bool _enabled;
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".html", ".js", ".css", ".json", ".png", ".jpg", ".svg", ".woff2", ".wasm" };
    private static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    { "platform.getCapabilities", "lifecycle.getState", "identity.getProfile", "identity.requestProfile", "identity.beginAppSession", "permissions.query", "permissions.request",
      "preferences.get", "preferences.set", "storage.read", "storage.write", "files.pickOpen", "files.pickSave", "files.read", "files.write", "saves.list", "saves.read", "saves.write", "saves.restore",
      "network.getStatus", "network.request", "notifications.show", "notifications.setBadge", "appearance.get", "shortcuts.register", "links.openInternal", "widgets.update", "diagnostics.log" };
    private static readonly HashSet<string> Codes = new(StringComparer.Ordinal)
    { "OK", "NONE", "AUTH_REQUIRED", "AUTH_NOT_CONFIGURED", "SESSION_EXPIRED", "OFFLINE", "USER_CANCELLED", "PERMISSION_NOT_DECLARED", "PERMISSION_DENIED", "PERMISSION_REVOKED",
      "CAPABILITY_UNAVAILABLE", "SOURCE_REJECTED", "INVALID_REQUEST", "INVALID_PARAMS", "REQUEST_REPLAYED", "RATE_LIMITED", "RATE_LIMIT_EXCEEDED", "REQUEST_TIMEOUT", "APP_CLOSED", "APP_SUSPENDED", "NOT_FOUND", "QUOTA_EXCEEDED" };
    public bool Enabled { get { lock (_gate) return _enabled; } }
    public CancellationToken CapabilityToken { get { lock (_gate) return _capabilities.Token; } }
    public void SetEnabled(bool enabled)
    {
        CancellationTokenSource previous;
        Task cancellation;
        lock (_gate)
        {
            if (_enabled == enabled) return;
            _enabled = enabled;
            previous = _capabilities; _capabilities = new();
            if (!enabled) { _capabilities.Cancel(); _trace.Clear(); }
            // CancelAsync marks the old token cancelled synchronously, then runs callbacks outside this gate.
            cancellation = previous.CancelAsync();
        }
        _ = cancellation.ContinueWith(task => { _ = task.Exception; previous.Dispose(); }, CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }
    public void Record(string method, string resultCode)
    {
        lock (_gate)
        {
            if (!_enabled) return;
            _trace.Enqueue(new(DateTimeOffset.UtcNow, Methods.Contains(method) ? method : "unknown-method", Codes.Contains(resultCode) ? resultCode : "OTHER_RESULT"));
            while (_trace.Count > 200) _trace.Dequeue();
        }
    }
    public IReadOnlyList<DeveloperSdkCall> GetTrace() { lock (_gate) return _trace.ToArray(); }
    public void ClearTrace() { lock (_gate) _trace.Clear(); }
    private CancellationTokenSource Begin(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_enabled) throw new PackageException("DEVELOPER_MODE_DISABLED");
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _capabilities.Token);
        }
    }
    public DeveloperBuild BuildProject(string projectDirectory, string outputDirectory, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource work = Begin(cancellationToken);
        CancellationToken token = work.Token;
        string project = Absolute(projectDirectory), output = Absolute(outputDirectory);
        if (output.StartsWith(Path.TrimEndingDirectorySeparator(project) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || output.Equals(project, StringComparison.OrdinalIgnoreCase))
            throw new PackageException("DEVELOPER_OUTPUT_INSIDE_PROJECT");
        SafePath(project); SafePath(output);
        List<(string Name, byte[] Bytes)> files = ReadProject(project, token);
        Directory.CreateDirectory(output); SafePath(output);
        string staging = Path.Combine(output, "validation-" + Guid.NewGuid().ToString("N") + ".autumn");
        try
        {
            PackageInspection inspection = WriteArchive(files, staging, token);
            string destination = Path.Combine(output, $"{inspection.Manifest.AppId}-{inspection.Manifest.Version}-{Guid.NewGuid():N}.autumn");
            lock (_gate)
            {
                token.ThrowIfCancellationRequested();
                if (!_enabled) throw new PackageException("DEVELOPER_MODE_DISABLED");
                SafePath(staging); SafePath(destination); File.Move(staging, destination);
            }
            return new(destination, inspection);
        }
        finally { SafePath(staging); if (File.Exists(staging)) File.Delete(staging); }
    }
    public PackageInspection ValidateProject(string projectDirectory, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource work = Begin(cancellationToken);
        return ValidateFiles(ReadProject(Absolute(projectDirectory), work.Token), work.Token);
    }
    public DeveloperProject CreateProject(string templateRoot, string sdkPath, string template, string destinationDirectory,
        string appId, string name, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource work = Begin(cancellationToken);
        CancellationToken token = work.Token;
        if (template is not ("hello-app" or "identity-app" or "save-game" or "desktop-extension"))
            throw new PackageException("DEVELOPER_TEMPLATE_UNSUPPORTED");
        string destination = Absolute(destinationDirectory), root = Absolute(templateRoot);
        SafeProjectPath(destination); SafePath(root);
        if (Directory.Exists(destination) || File.Exists(destination)) throw new PackageException("DEVELOPER_OUTPUT_ALREADY_EXISTS");
        List<(string Name, byte[] Bytes)> files = ReadProject(Path.Combine(root, template), token);
        _ = ValidateFiles(files, token);
        var manifestFile = files.Single(file => file.Name == "manifest.json");
        JsonObject manifest;
        try { manifest = JsonNode.Parse(manifestFile.Bytes)?.AsObject() ?? throw new JsonException(); }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw new PackageException("PACKAGE_MANIFEST_INVALID"); }
        manifest["appId"] = appId; manifest["name"] = name;
        files[files.IndexOf(manifestFile)] = ("manifest.json", JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true }));
        void AddResource(string source, string fileName)
        {
            token.ThrowIfCancellationRequested(); SafePath(source);
            if (files.Any(file => file.Name.Equals(fileName, StringComparison.OrdinalIgnoreCase))) throw new PackageException("DEVELOPER_TEMPLATE_RESOURCE_CONFLICT");
            using FileStream input = new(source, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > 8 * 1024 * 1024) throw new PackageException("PACKAGE_LIMIT_EXCEEDED");
            byte[] bytes = new byte[(int)input.Length]; input.ReadExactly(bytes); files.Add((fileName, bytes));
        }
        AddResource(Absolute(sdkPath), "autumn-sdk.js");
        AddResource(Path.Combine(root, "_shared", "sample-ui.js"), "sample-ui.js");
        AddResource(Path.Combine(root, "_shared", "sample-ui.css"), "sample-ui.css");
        PackageInspection inspection = ValidateFiles(files, token);
        string parent = Path.GetDirectoryName(destination) ?? throw new PackageException("PACKAGE_UNSAFE_PATH");
        SafePath(parent); Directory.CreateDirectory(parent); SafePath(parent);
        string staging = Path.Combine(parent, ".autumnos-create-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging); SafePath(staging);
        List<string> created = [];
        try
        {
            foreach (var file in files)
            {
                token.ThrowIfCancellationRequested();
                string target = Path.Combine(staging, file.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!); SafePath(target);
                using FileStream output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                created.Add(target); output.Write(file.Bytes); output.Flush(true);
            }
            lock (_gate)
            {
                token.ThrowIfCancellationRequested(); if (!_enabled) throw new PackageException("DEVELOPER_MODE_DISABLED");
                SafePath(staging); SafePath(destination); Directory.Move(staging, destination);
            }
            return new(destination, template, inspection);
        }
        finally
        {
            // Remove only paths created by this operation, never an existing project or unknown injected file.
            if (Directory.Exists(staging))
            {
                SafePath(staging);
                foreach (string file in created) { SafePath(file); if (File.Exists(file)) File.Delete(file); }
                foreach (string? directory in created.Select(Path.GetDirectoryName).Where(value => value is not null).Distinct().OrderByDescending(value => value!.Length))
                    if (directory is not null && directory != staging && Directory.Exists(directory)) { SafePath(directory); try { Directory.Delete(directory); } catch (IOException) { } }
                try { Directory.Delete(staging); } catch (IOException) { }
            }
        }
    }
    public DeveloperBuild PreparePreview(string packagePath, string outputDirectory, string? expectedSha256 = null, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource work = Begin(cancellationToken);
        string package = Absolute(packagePath), output = Absolute(outputDirectory);
        SafePath(package); SafePath(output);
        using FileStream source = new(package, FileMode.Open, FileAccess.Read, FileShare.Read);
        PackageInspection inspection = PackageInstaller.Inspect(package, work.Token);
        if (expectedSha256 is not null && !string.Equals(expectedSha256, inspection.Sha256, StringComparison.Ordinal))
            throw new PackageException("DEVELOPER_PREVIEW_HASH_CHANGED");
        Directory.CreateDirectory(output); SafePath(output);
        string copy = Path.Combine(output, "preview-" + Guid.NewGuid().ToString("N") + ".autumn");
        try
        {
            using (FileStream destination = new(copy, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { source.CopyTo(destination); destination.Flush(true); }
            PackageInspection checkedCopy = PackageInstaller.Inspect(copy, work.Token);
            if (checkedCopy.Sha256 != inspection.Sha256) throw new PackageException("DEVELOPER_PREVIEW_HASH_CHANGED");
            work.Token.ThrowIfCancellationRequested(); return new(copy, checkedCopy);
        }
        catch { SafePath(copy); if (File.Exists(copy)) File.Delete(copy); throw; }
    }
    /// <summary>Trusted host-only install of an already confirmed preview. Every payload hash has a separate immutable cache.</summary>
    public static InstalledPackage InstallPreviewPackage(string packagePath, string cacheDirectory, string expectedSha256, CancellationToken cancellationToken = default)
    {
        if (expectedSha256 is not { Length: 64 } || expectedSha256.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new PackageException("DEVELOPER_PREVIEW_BINDING_REQUIRED");
        string package = Absolute(packagePath), cache = Absolute(cacheDirectory);
        SafePath(package); SafePath(cache);
        using FileStream payload = new(package, FileMode.Open, FileAccess.Read, FileShare.Read);
        PackageInspection inspection = PackageInstaller.Inspect(package, cancellationToken);
        if (inspection.Sha256 != expectedSha256) throw new PackageException("DEVELOPER_PREVIEW_HASH_CHANGED");
        string previewRoot = Path.Combine(cache, inspection.Sha256); SafePath(previewRoot);
        InstalledPackage result = PackageInstaller.Install(package, previewRoot, cancellationToken);
        if (result.PackageSha256 != expectedSha256) throw new PackageException("DEVELOPER_PREVIEW_HASH_CHANGED");
        return result with { HostSource = "local-preview:" + expectedSha256 };
    }
    private static List<(string Name, byte[] Bytes)> ReadProject(string project, CancellationToken token)
    {
        SafeProjectPath(project);
        if (!Directory.Exists(project)) throw new PackageException("DEVELOPER_PROJECT_MISSING");
        List<(string Name, byte[] Bytes)> files = [];
        int directoryCount = 0; long total = 0;
        void Visit(string directory, int depth)
        {
            token.ThrowIfCancellationRequested(); SafePath(directory);
            if (++directoryCount > 32 || depth > 12) throw new PackageException("PACKAGE_LIMIT_EXCEEDED");
            foreach (string path in Directory.EnumerateFileSystemEntries(directory))
            {
                token.ThrowIfCancellationRequested(); SafePath(path);
                string name = Path.GetFileName(path);
                if (name.StartsWith('.') || name.Equals("AutumnOS_Data", StringComparison.OrdinalIgnoreCase)) throw new PackageException("DEVELOPER_PRIVATE_PATH_REJECTED");
                if (Directory.Exists(path)) { Visit(path, depth + 1); continue; }
                if (!Extensions.Contains(Path.GetExtension(path))) throw new PackageException("PACKAGE_FILE_TYPE_FORBIDDEN");
                if (files.Count >= 128) throw new PackageException("PACKAGE_LIMIT_EXCEEDED");
                using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (input.Length > 8 * 1024 * 1024 || (total += input.Length) > 16 * 1024 * 1024) throw new PackageException("PACKAGE_LIMIT_EXCEEDED");
                byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes);
                files.Add((Path.GetRelativePath(project, path).Replace('\\', '/'), bytes));
            }
        }
        Visit(project, 0);
        files.Sort((left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        if (!files.Any(file => file.Name == "manifest.json")) throw new PackageException("PACKAGE_MANIFEST_INVALID");
        return files;
    }
    private static PackageInspection ValidateFiles(List<(string Name, byte[] Bytes)> files, CancellationToken token)
    {
        string temporary = Path.Combine(Path.GetTempPath(), "autumnos-project-check-" + Guid.NewGuid().ToString("N") + ".autumn");
        SafePath(temporary);
        try { return WriteArchive(files, temporary, token); }
        finally { SafePath(temporary); if (File.Exists(temporary)) File.Delete(temporary); }
    }
    private static PackageInspection WriteArchive(List<(string Name, byte[] Bytes)> files, string staging, CancellationToken token)
    {
        if (files.Count > 128 || files.Sum(file => (long)file.Bytes.Length) > 16 * 1024 * 1024) throw new PackageException("PACKAGE_LIMIT_EXCEEDED");
        SafePath(staging);
        using (FileStream stream = new(staging, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
                using (ZipArchive zip = new(stream, ZipArchiveMode.Create, true))
                    foreach (var file in files.OrderBy(file => file.Name, StringComparer.Ordinal))
                    {
                        token.ThrowIfCancellationRequested();
                        ZipArchiveEntry entry = zip.CreateEntry(file.Name, CompressionLevel.Optimal);
                        entry.LastWriteTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
                        using Stream content = entry.Open(); content.Write(file.Bytes);
                    }
                stream.Flush(true);
        }
        return PackageInstaller.Inspect(staging, token);
    }
    public PackageInspection InspectPackage(string packagePath, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource work = Begin(cancellationToken);
        PackageInspection result = PackageInstaller.Inspect(Absolute(packagePath), work.Token);
        work.Token.ThrowIfCancellationRequested(); return result;
    }
    public DeveloperReleaseCheck ValidateRelease(string metadataPath, string packagePath, CancellationToken cancellationToken = default)
    {
        using CancellationTokenSource work = Begin(cancellationToken);
        PackageInspection package = PackageInstaller.Inspect(Absolute(packagePath), work.Token);
        string metadata = Absolute(metadataPath); SafePath(metadata);
        using FileStream input = new(metadata, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 65536) throw new PackageException("RELEASE_METADATA_INVALID");
        using MemoryStream bytes = new(); input.CopyTo(bytes);
        PackageReleaseManifest release = ReleaseManifestValidator.Parse(bytes.ToArray());
        ReleaseManifestValidator.ValidatePackage(release, package, packagePath);
        work.Token.ThrowIfCancellationRequested();
        return new(package.Manifest.AppId, package.Manifest.Version, release.Channel, package.Sha256);
    }
    private static string Absolute(string path) => Path.IsPathFullyQualified(path) ? Path.GetFullPath(path) : throw new PackageException("PACKAGE_UNSAFE_PATH");
    private static void SafeProjectPath(string path)
    {
        SafePath(path);
        if (path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Contains("AutumnOS_Data", StringComparer.OrdinalIgnoreCase))
            throw new PackageException("DEVELOPER_PRIVATE_PATH_REJECTED");
    }
    private static void SafePath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new PackageException("PACKAGE_LINK_FORBIDDEN"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    public void Dispose() { SetEnabled(false); }
}
