using System.IO.Compression;
using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AutumnOS.Packages;

/// <summary>Trusted local host installer. Does not execute content, grant permissions, or touch user saves.</summary>
public static class PackageInstaller
{
    public const int MaximumSaveFormatVersion = 1_000_000;
    private const long MaxPackageBytes = 20L * 1024 * 1024;
    private const int MaxFiles = 128;
    private const int MaxFileBytes = 8 * 1024 * 1024;
    private const int MaxExpandedBytes = 16 * 1024 * 1024;
    private const string RecordName = ".autumnos-install.json";
    private const string LockName = ".autumnos-install.lock";
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        { ".html", ".js", ".css", ".json", ".png", ".jpg", ".svg", ".woff2", ".wasm" };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 12,
        WriteIndented = true
    };
    private static readonly uint[] CrcTable = CreateCrcTable();

    /// <summary>Read-only validation used by the developer tools; it does not install or execute a package.</summary>
    public static PackageInspection Inspect(string packagePath, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(packagePath)) Fail("PACKAGE_UNSAFE_PATH");
        CheckAncestors(packagePath);
        if (!string.Equals(Path.GetExtension(packagePath), ".autumn", StringComparison.OrdinalIgnoreCase)) Fail("PACKAGE_EXTENSION_INVALID");
        try
        {
            using var stream = new FileStream(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaxPackageBytes) Fail("PACKAGE_LIMIT_EXCEEDED");
            string hash = Convert.ToHexStringLower(SHA256.HashData(stream));
            stream.Position = 0;
            var package = ReadPackage(stream, cancellationToken);
            return new(package.Manifest, hash, package.Files.Count, package.Files.Sum(f => (long)f.Bytes.Length));
        }
        catch (JsonException) { throw new PackageException("PACKAGE_MANIFEST_INVALID"); }
        catch (InvalidDataException) { throw new PackageException("PACKAGE_ARCHIVE_INVALID"); }
    }

    public static InstalledPackage Install(string packagePath, string appsDirectory, CancellationToken cancellationToken = default)
    {
        string? staging = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(packagePath) || !Path.IsPathFullyQualified(appsDirectory))
                Fail("PACKAGE_UNSAFE_PATH");
            packagePath = Path.GetFullPath(packagePath);
            appsDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appsDirectory));
            if (!string.Equals(Path.GetExtension(packagePath), ".autumn", StringComparison.OrdinalIgnoreCase))
                Fail("PACKAGE_EXTENSION_INVALID");
            CheckAncestors(packagePath);
            CheckAncestors(appsDirectory);
            using FileStream packageStream = new(packagePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (packageStream.Length is <= 0 or > MaxPackageBytes) Fail("PACKAGE_LIMIT_EXCEEDED");
            string packageHash = Convert.ToHexString(SHA256.HashData(packageStream)).ToLowerInvariant();
            packageStream.Position = 0;
            ValidatedPackage package = ReadPackage(packageStream, cancellationToken);

            // All archive bytes and manifest fields are validated before creating installation state.
            cancellationToken.ThrowIfCancellationRequested();
            EnsureDirectory(appsDirectory);
            string lockPath = Path.Combine(appsDirectory, LockName);
            CheckAncestors(lockPath);
            using FileStream installLock = OpenLock(lockPath);
            CheckAncestors(appsDirectory);
            string appDirectory = Path.Combine(appsDirectory, package.Manifest.AppId);
            EnsureDirectory(appDirectory);
            string destination = Path.Combine(appDirectory, package.Manifest.Version);
            CheckAncestors(destination);
            if (Directory.Exists(destination))
                return VerifyExisting(destination, package, packageHash);
            if (File.Exists(destination)) Fail("PACKAGE_INSTALL_CONFLICT");

            staging = Path.Combine(appDirectory, ".staging-" + Guid.NewGuid().ToString("N"));
            EnsureDirectory(staging);
            foreach (PackageFile file in package.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string path = ContainedPath(staging, file.Path);
                EnsureDirectory(Path.GetDirectoryName(path)!);
                CheckAncestors(path);
                using FileStream output = new(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                output.Write(file.Bytes);
                output.Flush(flushToDisk: true);
            }
            var record = new InstallationRecord(1, package.Manifest.AppId, package.Manifest.Version, packageHash,
                package.Files.Select(file => new InstalledFile(file.Path, file.Bytes.Length, file.Sha256)).ToArray());
            byte[] recordBytes = JsonSerializer.SerializeToUtf8Bytes(record, JsonOptions);
            string recordPath = Path.Combine(staging, RecordName);
            using (FileStream recordStream = new(recordPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                recordStream.Write(recordBytes);
                recordStream.Flush(flushToDisk: true);
            }
            // The record and validated resources become visible together, on the same filesystem.
            CheckAncestors(staging);
            CheckAncestors(destination);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, destination);
            staging = null;
            return new(package.Manifest, destination, packageHash);
        }
        catch (PackageException) { throw; }
        catch (OperationCanceledException) { throw new PackageException("PACKAGE_CANCELLED"); }
        catch (InvalidDataException) { throw new PackageException("PACKAGE_ARCHIVE_INVALID"); }
        catch (JsonException) { throw new PackageException("PACKAGE_MANIFEST_INVALID"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw new PackageException("PACKAGE_IO_ERROR"); }
        finally
        {
            // Never recursively follow a directory created or replaced by another local process.
            if (staging is not null) RemoveOwnStaging(staging);
        }
    }

    private static ValidatedPackage ReadPackage(Stream stream, CancellationToken cancellationToken)
    {
        using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: true);
        if (archive.Entries.Count is < 1 or > MaxFiles) Fail("PACKAGE_LIMIT_EXCEEDED");
        Dictionary<string, (string Path, bool Directory, bool Explicit)> paths = new(StringComparer.OrdinalIgnoreCase);
        List<PackageFile> files = [];
        long expanded = 0;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = entry.FullName;
            bool directory = name.EndsWith("/", StringComparison.Ordinal);
            ValidateResourcePath(name, directory);
            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            int dosAttributes = entry.ExternalAttributes & 0xFFFF;
            if ((dosAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                (unixType != 0 && unixType != (directory ? 0x4000 : 0x8000)) ||
                (!directory && (dosAttributes & (int)FileAttributes.Directory) != 0))
                Fail("PACKAGE_LINK_FORBIDDEN");
            RegisterPath(paths, name.TrimEnd('/'), directory);
            if (directory)
            {
                if (entry.Length != 0) Fail("PACKAGE_ARCHIVE_INVALID");
                continue;
            }
            if (entry.Length is < 0 or > MaxFileBytes || (expanded += entry.Length) > MaxExpandedBytes ||
                (entry.Length > 1024 * 1024 && entry.Length / Math.Max(1, entry.CompressedLength) > 200))
                Fail("PACKAGE_LIMIT_EXCEEDED");
            if (!Extensions.Contains(Path.GetExtension(name))) Fail("PACKAGE_FILE_TYPE_FORBIDDEN");
            if (Path.GetFileName(name).Equals("manifest.json", StringComparison.OrdinalIgnoreCase) && name != "manifest.json")
                Fail("PACKAGE_MANIFEST_INVALID");
            if (name.Equals(RecordName, StringComparison.OrdinalIgnoreCase)) Fail("PACKAGE_RESERVED_PATH");
            using Stream input = entry.Open();
            using MemoryStream bytes = new();
            byte[] buffer = new byte[8192];
            int count;
            while ((count = input.Read(buffer)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bytes.Length + count > MaxFileBytes || bytes.Length + count > entry.Length)
                    Fail("PACKAGE_LIMIT_EXCEEDED");
                bytes.Write(buffer, 0, count);
            }
            byte[] content = bytes.ToArray();
            if (content.LongLength != entry.Length || ComputeCrc(content) != entry.Crc32) Fail("PACKAGE_ARCHIVE_INVALID");
            // Native images cannot masquerade as web resources by changing their filename extension.
            if ((content.Length >= 2 && content[0] == 'M' && content[1] == 'Z') ||
                (content.Length >= 4 && content[0] == 0x7f && content[1] == 'E' && content[2] == 'L' && content[3] == 'F'))
                Fail("PACKAGE_FILE_TYPE_FORBIDDEN");
            files.Add(new(name, content, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()));
        }
        PackageFile? manifestFile = files.SingleOrDefault(file => file.Path == "manifest.json");
        if (manifestFile is null || manifestFile.Bytes.Length > 64 * 1024) Fail("PACKAGE_MANIFEST_INVALID");
        AutumnPackageManifest manifest = ParseManifest(manifestFile!.Bytes);
        if (!files.Any(file => file.Path == manifest.Entry)) Fail("PACKAGE_ENTRY_MISSING");
        return new(manifest, files);
    }

    private static AutumnPackageManifest ParseManifest(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        JsonElement root = document.RootElement;
        string[] keys = ["schemaVersion", "appId", "name", "version", "runtime", "entry", "permissions"];
        if (root.ValueKind != JsonValueKind.Object) Fail("PACKAGE_MANIFEST_INVALID");
        JsonProperty[] properties = root.EnumerateObject().ToArray();
        string[] optional = ["desktop", "permissionPurposes", "saveFormatVersion", "minReadableSaveFormatVersion", "maxReadableSaveFormatVersion"];
        if (properties.Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
            keys.Any(key => !root.TryGetProperty(key, out _)) ||
            properties.Any(p => !keys.Contains(p.Name, StringComparer.Ordinal) && !optional.Contains(p.Name, StringComparer.Ordinal))) Fail("PACKAGE_MANIFEST_INVALID");
        if (root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
            !root.GetProperty("schemaVersion").TryGetInt32(out int schema) || schema != 1) Fail("PACKAGE_SCHEMA_UNSUPPORTED");
        string Text(string key)
        {
            JsonElement field = root.GetProperty(key);
            if (field.ValueKind != JsonValueKind.String || field.GetString() is not { Length: > 0 and <= 128 } text || text.Any(char.IsControl))
                throw new PackageException("PACKAGE_MANIFEST_INVALID");
            return text;
        }
        string appId = Text("appId");
        if (appId.Length > 80 || !Regex.IsMatch(appId, "^[a-z][a-z0-9]*(?:-[a-z0-9]+)*(?:\\.[a-z][a-z0-9]*(?:-[a-z0-9]+)*)+$", RegexOptions.CultureInvariant) ||
            appId.Split('.').Any(part => part.Length > 63) || IsReservedName(appId)) Fail("PACKAGE_APP_ID_INVALID");
        string version = Text("version");
        if (!IsSemVer(version)) Fail("PACKAGE_VERSION_INVALID");
        string name = Text("name");
        if (string.IsNullOrWhiteSpace(name)) Fail("PACKAGE_MANIFEST_INVALID");
        string runtime = Text("runtime");
        if (runtime != "web") Fail("PACKAGE_RUNTIME_UNSUPPORTED");
        string entry = Text("entry");
        ValidateResourcePath(entry, directory: false);
        if (!entry.EndsWith(".html", StringComparison.OrdinalIgnoreCase)) Fail("PACKAGE_ENTRY_INVALID");
        JsonElement permissionArray = root.GetProperty("permissions");
        string[] allowedPermissions = ["saves", "identity.profile", "storage", "files.open", "files.save", "notifications", "shortcuts", "widgets", "links"];
        if (permissionArray.ValueKind != JsonValueKind.Array || permissionArray.GetArrayLength() > allowedPermissions.Length) Fail("PACKAGE_PERMISSION_UNSUPPORTED");
        string[] permissions = permissionArray.EnumerateArray().Select(permission =>
        {
            if (permission.ValueKind != JsonValueKind.String || !allowedPermissions.Contains(permission.GetString(), StringComparer.Ordinal))
                throw new PackageException("PACKAGE_PERMISSION_UNSUPPORTED");
            return permission.GetString()!;
        }).ToArray();
        if (permissions.Distinct(StringComparer.Ordinal).Count() != permissions.Length) Fail("PACKAGE_PERMISSION_UNSUPPORTED");
        var desktop = ParseDesktop(root);
        if (desktop is not null && ((desktop.Shortcuts.Length > 0 && !permissions.Contains("shortcuts")) ||
            (desktop.Links.Length > 0 && !permissions.Contains("links")) || (desktop.Widgets.Length > 0 && !permissions.Contains("widgets")))) Fail("PACKAGE_PERMISSION_UNSUPPORTED");
        Dictionary<string, string>? purposes = null;
        if (root.TryGetProperty("permissionPurposes", out var purposeObject))
        {
            if (purposeObject.ValueKind != JsonValueKind.Object) Fail("PACKAGE_MANIFEST_INVALID");
            purposes = new(StringComparer.Ordinal);
            foreach (var property in purposeObject.EnumerateObject())
                if (!permissions.Contains(property.Name) || property.Value.ValueKind != JsonValueKind.String ||
                    property.Value.GetString() is not { Length: > 0 and <= 160 } purpose || purpose.Any(char.IsControl) ||
                    !purposes.TryAdd(property.Name, purpose)) Fail("PACKAGE_MANIFEST_INVALID");
        }
        int SaveVersion(string key, int fallback) => !root.TryGetProperty(key, out var field) ? fallback :
            field.ValueKind == JsonValueKind.Number && field.TryGetInt32(out int value) && value is >= 1 and <= MaximumSaveFormatVersion
                ? value : throw new PackageException("PACKAGE_SAVE_FORMAT_INVALID");
        int saveVersion = SaveVersion("saveFormatVersion", 1);
        int minimum = SaveVersion("minReadableSaveFormatVersion", saveVersion), maximum = SaveVersion("maxReadableSaveFormatVersion", saveVersion);
        if (minimum > saveVersion || maximum < saveVersion) Fail("PACKAGE_SAVE_FORMAT_INVALID");
        return new(1, appId, name, version, runtime, entry, permissions, desktop, purposes, saveVersion,
            root.TryGetProperty("minReadableSaveFormatVersion", out _) ? minimum : null,
            root.TryGetProperty("maxReadableSaveFormatVersion", out _) ? maximum : null);
    }

    private static PackageDesktopDeclarations? ParseDesktop(JsonElement root)
    {
        if (!root.TryGetProperty("desktop", out var desktop)) return null;
        static bool Exact(JsonElement value, params string[] keys) => value.ValueKind == JsonValueKind.Object &&
            value.EnumerateObject().Count() == keys.Length && keys.All(k => value.TryGetProperty(k, out _));
        static string Field(JsonElement value, string name, bool identifier)
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 64 } text ||
                text.Any(char.IsControl) || (identifier && !Regex.IsMatch(text, "^[a-z][a-z0-9_-]{0,39}$")))
                throw new PackageException("PACKAGE_DESKTOP_INVALID");
            return text;
        }
        if (!Exact(desktop, "shortcuts", "links", "widgets")) Fail("PACKAGE_DESKTOP_INVALID");
        foreach (string key in new[] { "shortcuts", "links", "widgets" })
            if (desktop.GetProperty(key).ValueKind != JsonValueKind.Array || desktop.GetProperty(key).GetArrayLength() > 4) Fail("PACKAGE_DESKTOP_INVALID");
        var links = desktop.GetProperty("links").EnumerateArray().Select(e => Field(e, "link", true)).ToArray();
        var shortcuts = desktop.GetProperty("shortcuts").EnumerateArray().Select(e =>
        {
            if (!Exact(e, "id", "title", "action")) throw new PackageException("PACKAGE_DESKTOP_INVALID");
            var shortcut = new PackageShortcut(Field(e.GetProperty("id"), "id", true), Field(e.GetProperty("title"), "title", false), Field(e.GetProperty("action"), "action", true));
            if (!links.Contains(shortcut.Action, StringComparer.Ordinal)) throw new PackageException("PACKAGE_DESKTOP_INVALID");
            return shortcut;
        }).ToArray();
        var widgets = desktop.GetProperty("widgets").EnumerateArray().Select(e =>
        {
            if (!Exact(e, "id", "title")) throw new PackageException("PACKAGE_DESKTOP_INVALID");
            return new PackageWidget(Field(e.GetProperty("id"), "id", true), Field(e.GetProperty("title"), "title", false));
        }).ToArray();
        if (links.Distinct(StringComparer.Ordinal).Count() != links.Length || shortcuts.Select(s => s.Id).Distinct().Count() != shortcuts.Length ||
            widgets.Select(w => w.Id).Distinct().Count() != widgets.Length) Fail("PACKAGE_DESKTOP_INVALID");
        return new(shortcuts, links, widgets);
    }

    private static bool IsSemVer(string version)
    {
        Match match = Regex.Match(version,
            "^(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?$",
            RegexOptions.CultureInvariant);
        return match.Success && (!match.Groups[4].Success || match.Groups[4].Value.Split('.').All(part =>
            !part.All(char.IsAsciiDigit) || part.Length == 1 || part[0] != '0'));
    }

    private static void ValidateResourcePath(string path, bool directory)
    {
        if (path.Length is < 1 or > 200 || path[0] == '/' || path.Contains('\\') || path.Contains(':') ||
            path.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '/' and not '.' and not '_' and not '-'))
            Fail("PACKAGE_UNSAFE_PATH");
        string trimmed = directory ? path[..^1] : path;
        foreach (string segment in trimmed.Split('/'))
        {
            if (segment.Length is < 1 or > 80 || segment is "." or ".." || segment.EndsWith('.') ||
                segment.StartsWith('.') || IsReservedName(segment)) Fail("PACKAGE_UNSAFE_PATH");
        }
    }

    private static bool IsReservedName(string name)
    {
        string stem = name.Split('.')[0].ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9');
    }

    private static void RegisterPath(Dictionary<string, (string Path, bool Directory, bool Explicit)> paths, string path, bool directory)
    {
        string[] segments = path.Split('/');
        for (int index = 0; index < segments.Length; index++)
        {
            string current = string.Join('/', segments.Take(index + 1));
            bool final = index == segments.Length - 1;
            bool isDirectory = !final || directory;
            if (paths.TryGetValue(current, out var previous))
            {
                if (previous.Path != current || previous.Directory != isDirectory || (final && previous.Explicit))
                    Fail("PACKAGE_PATH_CONFLICT");
                if (final) paths[current] = (current, isDirectory, true);
            }
            else paths.Add(current, (current, isDirectory, final));
        }
    }

    private static InstalledPackage VerifyExisting(string directory, ValidatedPackage package, string hash)
    {
        string recordPath = Path.Combine(directory, RecordName);
        CheckAncestors(recordPath);
        if (!File.Exists(recordPath) || new FileInfo(recordPath).Length > 64 * 1024) Fail("PACKAGE_INSTALL_CONFLICT");
        InstallationRecord? record;
        try
        {
            byte[] bytes = File.ReadAllBytes(recordPath);
            using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 12 });
            if (HasDuplicateProperties(document.RootElement)) Fail("PACKAGE_INSTALL_CORRUPT");
            record = JsonSerializer.Deserialize<InstallationRecord>(bytes, JsonOptions);
        }
        catch (JsonException) { throw new PackageException("PACKAGE_INSTALL_CORRUPT"); }
        if (record is null || record.SchemaVersion != 1 || record.AppId != package.Manifest.AppId ||
            record.Version != package.Manifest.Version || record.PackageSha256 != hash) Fail("PACKAGE_VERSION_CONFLICT");
        if (record!.Files is null || record.Files.Length != package.Files.Count || record.Files.Any(saved => saved is null)) Fail("PACKAGE_INSTALL_CORRUPT");
        foreach (PackageFile file in package.Files)
        {
            string path = ContainedPath(directory, file.Path);
            CheckAncestors(path);
            InstalledFile[] records = record.Files.Where(saved => saved.Path == file.Path).ToArray();
            if (records.Length != 1 || records[0].Sha256 != file.Sha256 || records[0].Bytes != file.Bytes.Length ||
                !File.Exists(path) || new FileInfo(path).Length != file.Bytes.Length) Fail("PACKAGE_INSTALL_CORRUPT");
            using FileStream input = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(input), Convert.FromHexString(file.Sha256)))
                Fail("PACKAGE_INSTALL_CORRUPT");
        }
        HashSet<string> expected = new(package.Files.Select(file => file.Path), StringComparer.Ordinal);
        expected.Add(RecordName);
        foreach (string existing in EnumerateSafeFiles(directory))
        {
            if (!expected.Remove(Path.GetRelativePath(directory, existing).Replace('\\', '/')))
                Fail("PACKAGE_INSTALL_CORRUPT");
        }
        if (expected.Count != 0) Fail("PACKAGE_INSTALL_CORRUPT");
        return new(package.Manifest, directory, hash);
    }

    private static IEnumerable<string> EnumerateSafeFiles(string directory)
    {
        CheckAncestors(directory);
        foreach (string path in Directory.EnumerateFileSystemEntries(directory))
        {
            CheckAncestors(path);
            if (Directory.Exists(path))
            {
                foreach (string child in EnumerateSafeFiles(path)) yield return child;
            }
            else yield return path;
        }
    }

    private static string ContainedPath(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) Fail("PACKAGE_UNSAFE_PATH");
        return path;
    }

    private static FileStream OpenLock(string path)
    {
        try { return new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new PackageException("PACKAGE_INSTALL_BUSY"); }
    }

    private static void EnsureDirectory(string path)
    {
        CheckAncestors(path);
        Directory.CreateDirectory(path);
        CheckAncestors(path);
    }

    private static void CheckAncestors(string path)
    {
        string? current = path;
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) Fail("PACKAGE_LINK_FORBIDDEN");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            current = Path.GetDirectoryName(current);
        }
    }

    private static void RemoveOwnStaging(string directory)
    {
        try
        {
            if (!Path.GetFileName(directory).StartsWith(".staging-", StringComparison.Ordinal)) return;
            CheckAncestors(directory);
            if (!Directory.Exists(directory)) return;
            foreach (string file in EnumerateSafeFiles(directory).ToArray()) File.Delete(file);
            RemoveEmptyDirectories(directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or PackageException) { }
    }

    private static void RemoveEmptyDirectories(string directory)
    {
        CheckAncestors(directory);
        foreach (string child in Directory.EnumerateDirectories(directory)) RemoveEmptyDirectories(child);
        Directory.Delete(directory, recursive: false);
    }

    private static uint[] CreateCrcTable()
    {
        uint[] table = new uint[256];
        for (uint value = 0; value < table.Length; value++)
        {
            uint crc = value;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
            table[value] = crc;
        }
        return table;
    }

    private static uint ComputeCrc(byte[] bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) crc = CrcTable[(crc ^ value) & 0xff] ^ (crc >> 8);
        return ~crc;
    }

    private static bool HasDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
                if (!names.Add(property.Name) || HasDuplicateProperties(property.Value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement child in element.EnumerateArray()) if (HasDuplicateProperties(child)) return true;
        return false;
    }

    [DoesNotReturn]
    private static void Fail(string code) => throw new PackageException(code);
    private sealed record PackageFile(string Path, byte[] Bytes, string Sha256);
    private sealed record ValidatedPackage(AutumnPackageManifest Manifest, List<PackageFile> Files);
    private sealed record InstalledFile(string Path, long Bytes, string Sha256);
    private sealed record InstallationRecord(int SchemaVersion, string AppId, string Version, string PackageSha256, InstalledFile[] Files);
}
