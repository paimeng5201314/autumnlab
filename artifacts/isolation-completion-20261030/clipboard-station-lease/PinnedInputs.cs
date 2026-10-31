using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutumnOS.OwnedClipboardStationLease;

// Used only by the ordinary broker. The elevated holder never instantiates
// this type, reads a manifest, enumerates a directory or opens a file.
internal sealed class PinnedInputs : IDisposable
{
    private readonly List<FileStream> held = [];
    internal string ManifestPath { get; }
    internal string ManifestSha256 { get; }
    internal string ControllerPath { get; }
    internal string BrokerPath { get; }
    internal int BrokerFileCount { get; }
    private PinnedInputs(string manifestPath, string controller, string self)
    {
        try
        {
        ManifestPath = PlainFile(manifestPath); ControllerPath = PlainFile(controller); BrokerPath = PlainFile(self);
        using var document = JsonDocument.Parse(ReadManifest(ManifestPath), new JsonDocumentOptions { MaxDepth = 32 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("MANIFEST_OBJECT_REQUIRED");
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
            if (!keys.Add(property.Name)) throw new InvalidDataException("MANIFEST_DUPLICATE_PROPERTY");
        if (!Same(PlainFile(Text(root, "broker_executable")), BrokerPath) || !Same(PlainFile(Text(root, "controller_executable")), ControllerPath))
            throw new InvalidDataException("MANIFEST_EXECUTABLE_BINDING_MISMATCH");
        string brokerSha = Sha(Text(root, "broker_sha256")), controllerSha = Sha(Text(root, "controller_sha256"));
        JsonElement entries = root.GetProperty("broker_files");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() is < 2 or > 2048) throw new InvalidDataException("BROKER_FILE_SET_INVALID");
        string directory = Path.GetDirectoryName(BrokerPath)!;
        var expected = new Dictionary<string, (long Bytes, string Sha)>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || entry.EnumerateObject().Count() != 3 ||
                entry.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != 3)
                throw new InvalidDataException("BROKER_FILE_ENTRY_INVALID");
            string relative = Relative(Text(entry, "path")); long bytes = entry.GetProperty("bytes").GetInt64();
            if (bytes < 0 || !expected.TryAdd(relative, (bytes, Sha(Text(entry, "sha256"))))) throw new InvalidDataException("BROKER_FILE_ENTRY_INVALID");
        }
        var actual = EnumeratePlainFiles(directory).Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!actual.SetEquals(expected.Keys)) throw new InvalidDataException("BROKER_PUBLISH_FILE_SET_MISMATCH");
        string brokerRelative = Path.GetRelativePath(directory, BrokerPath).Replace('\\', '/');
        if (!expected.TryGetValue(brokerRelative, out var brokerEntry) || brokerEntry.Sha != brokerSha) throw new InvalidDataException("BROKER_SELF_HASH_MISMATCH");
        foreach (var pair in expected)
        {
            string file = PlainFile(Path.Combine(directory, pair.Key.Replace('/', Path.DirectorySeparatorChar)));
            Pin(file, pair.Value.Bytes, pair.Value.Sha);
        }
        // Pin the controller EXE as well. It runs as the existing ordinary user;
        // controller dependency inventory and child/runtime pinning are its own contract.
        var controllerInfo = new FileInfo(ControllerPath); Pin(ControllerPath, controllerInfo.Length, controllerSha);
        BrokerFileCount = expected.Count;
        FileStream manifest = held[0]; manifest.Position = 0;
        ManifestSha256 = Convert.ToHexStringLower(SHA256.HashData(manifest)); manifest.Position = 0;
        }
        catch { Dispose(); throw; }
    }
    internal static PinnedInputs Open(string manifest, string controller, string self)
    {
        return new PinnedInputs(manifest, controller, self);
    }
    private byte[] ReadManifest(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        held.Add(stream);
        if (stream.Length is < 2 or > 4194304) throw new InvalidDataException("MANIFEST_SIZE_INVALID");
        byte[] bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); stream.Position = 0; return bytes;
    }
    private void Pin(string path, long bytes, string sha)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); held.Add(stream);
        if (stream.Length != bytes || Convert.ToHexStringLower(SHA256.HashData(stream)) != sha) throw new InvalidDataException("PINNED_FILE_BYTES_MISMATCH");
        stream.Position = 0;
    }
    private static string Text(JsonElement value, string name) => value.GetProperty(name).GetString() ?? throw new InvalidDataException("MANIFEST_TEXT_REQUIRED");
    private static string Sha(string value) => Regex.IsMatch(value, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)
        ? value : throw new InvalidDataException("MANIFEST_SHA256_INVALID");
    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    private static string Relative(string value)
    {
        if (value.Length is < 1 or > 240 || Path.IsPathRooted(value) || value.Contains('\\') ||
            value.Split('/').Any(part => part is "" or "." or ".." || part.EndsWith(' ') || part.EndsWith('.') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("MANIFEST_RELATIVE_PATH_INVALID");
        return value;
    }
    private static string PlainFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)) throw new InvalidDataException("LOCAL_FULL_PATH_REQUIRED");
        string full = Path.GetFullPath(path), root = Path.GetPathRoot(full)!;
        if (new DriveInfo(root).DriveType != DriveType.Fixed) throw new InvalidDataException("LOCAL_FIXED_VOLUME_REQUIRED");
        string current = root;
        foreach (string part in Path.GetRelativePath(root, full).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, part);
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("REDIRECTED_INPUT_REJECTED");
        }
        if (!File.Exists(full)) throw new InvalidDataException("EXISTING_FILE_REQUIRED");
        return full;
    }
    private static IEnumerable<string> EnumeratePlainFiles(string directory)
    {
        foreach (string item in Directory.EnumerateFileSystemEntries(directory))
        {
            FileAttributes attributes = File.GetAttributes(item);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("REDIRECTED_PUBLISH_ENTRY_REJECTED");
            if ((attributes & FileAttributes.Directory) != 0) foreach (string nested in EnumeratePlainFiles(item)) yield return nested;
            else yield return item;
        }
    }
    public void Dispose() { foreach (FileStream stream in held) stream.Dispose(); held.Clear(); }
}
