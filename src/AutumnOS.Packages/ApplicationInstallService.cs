using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AutumnOS.Packages;

/// <summary>Host-only package registry. Content is immutable; the atomic registry replacement is the commit point.</summary>
public sealed class ApplicationInstallService
{
    private readonly object gate = new();
    private readonly string appsDirectory, registryPath;
    private readonly Dictionary<string, int> running = new(StringComparer.Ordinal);
    private readonly Func<IDisposable>? critical;
    private readonly Func<string, bool>? isRunning;
    private readonly Action<InstalledApplication, AutumnPackageManifest, bool>? prepareSaveChange;
    private readonly InstallFaultHooks? faults;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 24
    };
    public event Action<ApplicationRegistryChange>? Changed;

    public ApplicationInstallService(string appsDirectory, Func<IDisposable>? enterCriticalOperation = null,
        Func<string, bool>? isRunning = null,
        Action<InstalledApplication, AutumnPackageManifest, bool>? prepareSaveChange = null,
        InstallFaultHooks? testHooks = null)
    {
        this.appsDirectory = Absolute(appsDirectory); SafePath(this.appsDirectory);
        registryPath = Path.Combine(this.appsDirectory, ".autumnos-registry.json");
        critical = enterCriticalOperation; this.isRunning = isRunning;
        this.prepareSaveChange = prepareSaveChange; faults = testHooks;
    }

    public InstalledApplication[] GetInstalled()
    { lock (gate) return Read().Applications.Where(a => a.IsInstalled).OrderBy(a => a.Package.Manifest.Name, StringComparer.Ordinal).ToArray(); }
    public InstalledApplication? Find(string appId)
    { lock (gate) return Read().Applications.SingleOrDefault(a => a.AppId == appId && a.IsInstalled); }
    public bool HasRegistration(string appId)
    { lock (gate) return Read().Applications.Any(a => a.AppId == appId); }

    /// <summary>Acquire before the first launch work; release only after the WebView and its session are disposed.</summary>
    public IDisposable EnterRuntimeLease(string appId)
    {
        lock (gate)
        {
            if (Find(appId) is null) throw new PackageException("APP_NOT_INSTALLED");
            running[appId] = running.GetValueOrDefault(appId) + 1;
            return new Lease(() => { lock (gate) { if (--running[appId] == 0) running.Remove(appId); } });
        }
    }

    /// <summary>Adopt an already validated bundled install once. Never overrides a selected version or an uninstall tombstone.</summary>
    public InstalledApplication? RegisterExisting(InstalledPackage package, PackageSource? source = null)
    {
        lock (gate)
        {
            source ??= new("bundled"); ValidateSource(source); ValidateLocation(package.DirectoryPath);
            using var operation = critical?.Invoke(); using var fileLock = Lock();
            Registry registry = Read();
            var old = registry.Applications.SingleOrDefault(a => a.AppId == package.Manifest.AppId);
            if (old is not null) return old.IsInstalled ? old : null;
            VerifyInstalled(package);
            var app = new InstalledApplication(package.Manifest.AppId, package with { HostSource = source.Binding(package.Manifest.AppId) }, source, DateTimeOffset.UtcNow);
            Commit(registry, app); Notify("installed", app.AppId, app); return app;
        }
    }

    public InstalledApplication Install(string packagePath, ExpectedPackageIdentity? expected = null,
        InstallIntent intent = InstallIntent.InstallOrUpdate, bool sourceConfirmed = false,
        bool downgradeConfirmed = false, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            packagePath = Absolute(packagePath); SafePath(packagePath);
            if (!string.Equals(Path.GetExtension(packagePath), ".autumn", StringComparison.OrdinalIgnoreCase)) throw new PackageException("PACKAGE_EXTENSION_INVALID");
            // Hold the actual input bytes against modification between inspection and installation.
            using FileStream inputLease = new(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            PackageInspection inspection = PackageInstaller.Inspect(packagePath, cancellationToken);
            if (expected is not null) ValidateExpected(expected, inspection, inputLease.Length);
            PackageSource source = expected?.Source ?? new("local"); ValidateSource(source);
            lock (gate)
            {
                using var operation = critical?.Invoke(); using var fileLock = Lock();
                cancellationToken.ThrowIfCancellationRequested();
                Registry registry = Read(); string appId = inspection.Manifest.AppId;
                InstalledApplication? old = registry.Applications.SingleOrDefault(a => a.AppId == appId);
                if (old is not null && (old.Source.Binding(appId) != source.Binding(appId) || old.Source.SigningKeyFingerprint != source.SigningKeyFingerprint))
                    throw new PackageException("PACKAGE_SOURCE_CONFLICT");
                if (source.SigningKeyFingerprint is not null) throw new PackageException("PACKAGE_SIGNATURE_UNSUPPORTED");
                if (old is null && !sourceConfirmed) throw new PackageException("PACKAGE_SOURCE_CONFIRMATION_REQUIRED");
                if (registry.Versions?.Any(v => v.AppId == appId && v.Version == inspection.Manifest.Version && v.Sha256 != inspection.Sha256) == true)
                    throw new PackageException("PACKAGE_VERSION_CONFLICT");
                DemandNotRunning(appId);
                bool sameVersion = old?.Package.Manifest.Version == inspection.Manifest.Version;
                if (sameVersion && old!.Package.PackageSha256 != inspection.Sha256) throw new PackageException("PACKAGE_VERSION_CONFLICT");
                if (intent == InstallIntent.Repair && (old is not { IsInstalled: true } || !sameVersion)) throw new PackageException("PACKAGE_REPAIR_VERSION_REQUIRED");
                if (old?.PinnedVersion is not null && old.PinnedVersion != inspection.Manifest.Version) throw new PackageException("PACKAGE_VERSION_PINNED");
                if (inspection.Manifest.Version.Contains('-') && old is { AllowPreview: false }) throw new PackageException("PACKAGE_PREVIEW_DISABLED");
                bool downgrade = old is not null && CompareVersions(inspection.Manifest.Version, old.Package.Manifest.Version) < 0;
                if (downgrade && (intent != InstallIntent.Downgrade || !downgradeConfirmed)) throw new PackageException("PACKAGE_DOWNGRADE_CONFIRMATION_REQUIRED");
                if (old is { IsInstalled: true } && sameVersion && intent != InstallIntent.Repair)
                { VerifyInstalled(old.Package); return old; }
                if (old is not null && !sameVersion)
                {
                    if (prepareSaveChange is null && old.Package.Manifest.Permissions.Contains("saves")) throw new PackageException("SAVE_COMPATIBILITY_UNAVAILABLE");
                    prepareSaveChange?.Invoke(old, inspection.Manifest, downgrade);
                }
                faults?.Invoke(InstallCommitPoint.BeforeContentInstall);
                if (new DriveInfo(Path.GetPathRoot(appsDirectory)!).AvailableFreeSpace < inspection.ExpandedBytes + 2L * 1024 * 1024)
                    throw new PackageException("PACKAGE_DISK_FULL");
                string transaction = Guid.NewGuid().ToString("N");
                string contentRoot = Path.Combine(appsDirectory, ".content", transaction);
                InstalledPackage package = PackageInstaller.Install(packagePath, contentRoot, cancellationToken) with { HostSource = source.Binding(appId) };
                if (package.PackageSha256 != inspection.Sha256) throw new PackageException("PACKAGE_HASH_MISMATCH");
                var next = new InstalledApplication(appId, package, source, DateTimeOffset.UtcNow, old?.PinnedVersion,
                    old?.AllowPreview ?? inspection.Manifest.Version.Contains('-'), Provenance: expected?.Provenance);
                string journal = Path.Combine(appsDirectory, ".transactions", transaction + ".json");
                WriteNew(journal, JsonSerializer.SerializeToUtf8Bytes(new Transaction(1, appId, package.PackageSha256, "content_staged"), Json));
                faults?.Invoke(InstallCommitPoint.AfterContentInstall);
                cancellationToken.ThrowIfCancellationRequested();
                faults?.Invoke(InstallCommitPoint.BeforeRegistryCommit);
                Commit(registry, next);
                // Commit already succeeded: receipt or observer failure must not report a fictitious rollback.
                try { WriteNew(journal + ".committed", JsonSerializer.SerializeToUtf8Bytes(new Transaction(1, appId, package.PackageSha256, "committed"), Json)); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                Notify(old is { IsInstalled: true } ? intent == InstallIntent.Repair ? "repaired" : "updated" : "installed", appId, next);
                faults?.Invoke(InstallCommitPoint.AfterRegistryCommit);
                return next;
            }
        }
        catch (PackageException) { throw; }
        catch (OperationCanceledException) { throw new PackageException("PACKAGE_CANCELLED"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new PackageException(e is IOException io && (io.HResult & 0xffff) is 112 or 39 ? "PACKAGE_DISK_FULL" : "PACKAGE_IO_ERROR"); }
    }

    public void Uninstall(string appId)
    {
        lock (gate)
        {
            using var operation = critical?.Invoke(); using var fileLock = Lock();
            Registry registry = Read(); var app = registry.Applications.SingleOrDefault(a => a.AppId == appId);
            if (app is not { IsInstalled: true }) return;
            DemandNotRunning(appId);
            Commit(registry, app with { IsInstalled = false });
            // Keep immutable recovery content and source tombstone. Saves/AppData are never touched.
            Notify("uninstalled", appId, null);
        }
    }

    public InstalledApplication SetVersionPolicy(string appId, bool pinCurrentVersion, bool allowPreview)
    {
        lock (gate)
        {
            using var operation = critical?.Invoke(); using var fileLock = Lock();
            Registry registry = Read(); var app = registry.Applications.SingleOrDefault(a => a.AppId == appId && a.IsInstalled)
                ?? throw new PackageException("APP_NOT_INSTALLED");
            var next = app with { PinnedVersion = pinCurrentVersion ? app.Package.Manifest.Version : null, AllowPreview = allowPreview };
            Commit(registry, next); Notify("policy_changed", appId, next); return next;
        }
    }

    /// <summary>Read-only recovery inspection. Staged content never gets silently registered after a crash.</summary>
    public string[] InspectRecovery()
    {
        lock (gate)
        {
            string directory = Path.Combine(appsDirectory, ".transactions"); SafePath(directory);
            if (!Directory.Exists(directory)) return [];
            var registry = Read(); List<string> states = [];
            foreach (string path in Directory.EnumerateFiles(directory, "*.json").Take(1024))
            {
                SafePath(path);
                Transaction tx = ReadJson<Transaction>(path, 4096);
                bool committed = registry.Applications.Any(a => a.AppId == tx.AppId && a.Package.PackageSha256 == tx.Sha256);
                states.Add(committed ? "committed" : "staged_not_registered");
            }
            return states.ToArray();
        }
    }

    private void DemandNotRunning(string appId)
    { if (running.GetValueOrDefault(appId) != 0 || isRunning?.Invoke(appId) == true) throw new PackageException("PACKAGE_APP_RUNNING"); }
    private void Notify(string kind, string appId, InstalledApplication? app)
    {
        foreach (Delegate handler in Changed?.GetInvocationList() ?? [])
            try { ((Action<ApplicationRegistryChange>)handler)(new(kind, appId, app)); } catch { /* committed state stays authoritative */ }
    }
    private Registry Read()
    {
        SafePath(registryPath);
        if (!File.Exists(registryPath)) return new(1, []);
        Registry value = ReadJson<Registry>(registryPath, 4 * 1024 * 1024);
        if (value.SchemaVersion != 1 || value.Applications is null || value.Applications.Length > 2048 ||
            value.Applications.Any(a => a is null || a.Package is null || a.Package.Manifest is null || a.Source is null) ||
            value.Applications.Select(a => a.AppId).Distinct(StringComparer.Ordinal).Count() != value.Applications.Length)
            throw new PackageException("PACKAGE_REGISTRY_CORRUPT");
        if (value.Versions is { } versions && (versions.Length > 32768 || versions.Any(v => v is null || v.AppId is null ||
            v.Version is null || v.Sha256 is null || !Regex.IsMatch(v.Sha256, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) ||
            versions.Select(v => (v.AppId, v.Version)).Distinct().Count() != versions.Length)) throw new PackageException("PACKAGE_REGISTRY_CORRUPT");
        foreach (var app in value.Applications)
        {
            ValidateSource(app.Source); ValidateLocation(app.Package.DirectoryPath);
            if (app.AppId != app.Package.Manifest.AppId || app.Package.HostSource != app.Source.Binding(app.AppId) ||
                !Regex.IsMatch(app.Package.PackageSha256, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant)) throw new PackageException("PACKAGE_REGISTRY_CORRUPT");
        }
        return value;
    }
    private void Commit(Registry registry, InstalledApplication app)
    {
        var history = (registry.Versions ?? []).ToList();
        if (!history.Any(v => v.AppId == app.AppId && v.Version == app.Package.Manifest.Version))
            history.Add(new(app.AppId, app.Package.Manifest.Version, app.Package.PackageSha256));
        if (history.Count > 32768) throw new PackageException("PACKAGE_REGISTRY_LIMIT_EXCEEDED");
        Registry next = new(1, registry.Applications.Where(a => a.AppId != app.AppId).Append(app).ToArray(), history.ToArray());
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
        if (next.Applications.Length > 2048 || bytes.Length > 4 * 1024 * 1024) throw new PackageException("PACKAGE_REGISTRY_LIMIT_EXCEEDED");
        string staging = registryPath + ".staging-" + Guid.NewGuid().ToString("N");
        WriteNew(staging, bytes);
        SafePath(registryPath); SafePath(registryPath + ".bak");
        if (File.Exists(registryPath)) File.Replace(staging, registryPath, registryPath + ".bak");
        else File.Move(staging, registryPath);
    }
    private FileStream Lock()
    {
        SafePath(appsDirectory); Directory.CreateDirectory(appsDirectory);
        string path = Path.Combine(appsDirectory, ".autumnos-registry.lock"); SafePath(path);
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new PackageException("PACKAGE_INSTALL_BUSY"); }
    }
    private void ValidateLocation(string directory)
    {
        string path = Absolute(directory);
        if (!path.StartsWith(appsDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new PackageException("PACKAGE_REGISTRY_CORRUPT");
        SafePath(path);
    }
    private static void ValidateExpected(ExpectedPackageIdentity expected, PackageInspection actual, long bytes)
    {
        var manifest = actual.Manifest;
        if (expected.AppId != manifest.AppId || expected.Version != manifest.Version || expected.Runtime != manifest.Runtime ||
            expected.Entry != manifest.Entry || expected.SaveFormatVersion != manifest.SaveFormatVersion || expected.Permissions is null ||
            !expected.Permissions.Order(StringComparer.Ordinal).SequenceEqual(manifest.Permissions.Order(StringComparer.Ordinal))) throw new PackageException("PACKAGE_RELEASE_MISMATCH");
        if (expected.Bytes != bytes || expected.Sha256 != actual.Sha256) throw new PackageException("PACKAGE_HASH_MISMATCH");
        if (expected.Provenance is { } provenance && (provenance.ReleaseId is <= 0 || provenance.AssetId is <= 0 ||
            provenance.ReleaseMetadataSha256 is not null && !Regex.IsMatch(provenance.ReleaseMetadataSha256, "^[a-f0-9]{64}$") ||
            provenance.ReleaseCanonicalSha256 is not null && !Regex.IsMatch(provenance.ReleaseCanonicalSha256, "^[a-f0-9]{64}$") ||
            provenance.StoreContentSha is not null && !Regex.IsMatch(provenance.StoreContentSha, "^(?:[a-f0-9]{40}|[a-f0-9]{64})$")))
            throw new PackageException("PACKAGE_SOURCE_INVALID");
    }
    private static void ValidateSource(PackageSource source)
    {
        if (source.Kind is not ("github" or "local" or "bundled" or "test") ||
            (source.Kind is "github" or "test" ? source.RepositoryId is null or <= 0 : source.RepositoryId is not null) ||
            source.RepositoryName is { Length: > 256 } || source.RepositoryName?.Any(char.IsControl) == true ||
            source.SigningKeyFingerprint is not null && !Regex.IsMatch(source.SigningKeyFingerprint, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant))
            throw new PackageException("PACKAGE_SOURCE_INVALID");
    }
    public static void VerifyInstalled(InstalledPackage package)
    {
        string directory = Absolute(package.DirectoryPath); SafePath(directory);
        string recordPath = Path.Combine(directory, ".autumnos-install.json");
        Record record = ReadJson<Record>(recordPath, 64 * 1024);
        if (record.SchemaVersion != 1 || record.AppId != package.Manifest.AppId || record.Version != package.Manifest.Version ||
            record.PackageSha256 != package.PackageSha256 || record.Files is null || record.Files.Length is < 1 or > 128)
            throw new PackageException("PACKAGE_INSTALL_CORRUPT");
        HashSet<string> expected = new(StringComparer.OrdinalIgnoreCase) { ".autumnos-install.json" };
        long expanded = 0;
        foreach (var item in record.Files)
        {
            if (item is null || item.Path is null || !expected.Add(item.Path) || item.Path.Contains('\\') || item.Path.Split('/').Any(s => s is ".." or "." or "") ||
                item.Bytes is < 0 or > 8 * 1024 * 1024 || (expanded += item.Bytes) > 16 * 1024 * 1024 || !Regex.IsMatch(item.Sha256, "^[a-f0-9]{64}$"))
                throw new PackageException("PACKAGE_INSTALL_CORRUPT");
            string path = Path.GetFullPath(Path.Combine(directory, item.Path));
            if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new PackageException("PACKAGE_INSTALL_CORRUPT");
            SafePath(path);
            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length != item.Bytes || Convert.ToHexStringLower(SHA256.HashData(file)) != item.Sha256) throw new PackageException("PACKAGE_INSTALL_CORRUPT");
        }
        foreach (string path in SafeFiles(directory))
            if (!expected.Remove(Path.GetRelativePath(directory, path).Replace('\\', '/'))) throw new PackageException("PACKAGE_INSTALL_CORRUPT");
        if (expected.Count != 0) throw new PackageException("PACKAGE_INSTALL_CORRUPT");
    }
    internal static IEnumerable<string> SafeFiles(string directory)
    {
        SafePath(directory);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            SafePath(path);
            if (Directory.Exists(path)) { foreach (string file in SafeFiles(path)) yield return file; }
            else yield return path;
        }
    }
    internal static void SafePath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new PackageException("PACKAGE_LINK_FORBIDDEN"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
        }
    }
    internal static string Absolute(string path) => Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : throw new PackageException("PACKAGE_UNSAFE_PATH");
    internal static void WriteNew(string path, byte[] bytes)
    {
        SafePath(path); Directory.CreateDirectory(Path.GetDirectoryName(path)!); SafePath(path);
        using FileStream stream = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); stream.Write(bytes); stream.Flush(true);
    }
    private static T ReadJson<T>(string path, int maxBytes)
    {
        SafePath(path);
        try
        {
            using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 || input.Length > maxBytes) throw new PackageException("PACKAGE_REGISTRY_CORRUPT");
            using JsonDocument json = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 24 });
            CheckDuplicates(json.RootElement);
            return json.RootElement.Deserialize<T>(Json) ?? throw new PackageException("PACKAGE_REGISTRY_CORRUPT");
        }
        catch (JsonException) { throw new PackageException("PACKAGE_REGISTRY_CORRUPT"); }
    }
    internal static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> keys = new(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject()) { if (!keys.Add(property.Name)) throw new PackageException("PACKAGE_REGISTRY_CORRUPT"); CheckDuplicates(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) CheckDuplicates(child);
    }
    public static int CompareVersions(string left, string right)
    {
        if (left is null || right is null || left.Length > 128 || right.Length > 128 ||
            !Regex.IsMatch(left, ReleaseManifestValidator.VersionPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)) ||
            !Regex.IsMatch(right, ReleaseManifestValidator.VersionPattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            throw new PackageException("PACKAGE_VERSION_INVALID");
        string[] Parts(string value) => value.Split('+')[0].Split('-', 2);
        string[] a = Parts(left), b = Parts(right), ac = a[0].Split('.'), bc = b[0].Split('.');
        for (int i = 0; i < 3; i++) { int diff = BigInteger.Parse(ac[i]).CompareTo(BigInteger.Parse(bc[i])); if (diff != 0) return diff; }
        if (a.Length != b.Length) return a.Length == 1 ? 1 : -1;
        if (a.Length == 1) return 0;
        string[] ap = a[1].Split('.'), bp = b[1].Split('.');
        for (int i = 0; i < Math.Min(ap.Length, bp.Length); i++)
        {
            bool an = BigInteger.TryParse(ap[i], out var av), bn = BigInteger.TryParse(bp[i], out var bv);
            int diff = an && bn ? av.CompareTo(bv) : an != bn ? an ? -1 : 1 : StringComparer.Ordinal.Compare(ap[i], bp[i]);
            if (diff != 0) return diff;
        }
        return ap.Length.CompareTo(bp.Length);
    }
    private sealed record Registry(int SchemaVersion, InstalledApplication[] Applications, VersionDigest[]? Versions = null);
    private sealed record VersionDigest(string AppId, string Version, string Sha256);
    private sealed record Transaction(int SchemaVersion, string AppId, string Sha256, string State);
    private sealed record Record(int SchemaVersion, string AppId, string Version, string PackageSha256, RecordFile[] Files);
    private sealed record RecordFile(string Path, long Bytes, string Sha256);
    private sealed class Lease(Action release) : IDisposable { private Action? action = release; public void Dispose() => Interlocked.Exchange(ref action, null)?.Invoke(); }
}
