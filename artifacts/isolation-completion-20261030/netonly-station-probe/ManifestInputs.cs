using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutumnOS.NetOnlyStationProbe;

internal sealed record FileBinding(string path, long bytes, string sha256);
internal sealed record PreparedInputs(int schema_version, string source_snapshot_id, string case_root,
    string payload_directory, string executable, FileBinding[] files, string manifest_sha256) : IDisposable
{
    private List<FileStream> pins = [];
    internal void AdoptPins(List<FileStream> value) => pins = value;
    public void Dispose() { foreach (FileStream file in pins) file.Dispose(); pins.Clear(); }
}

internal static class ManifestInputs
{
    private const string Entry = "AutumnOS.NetOnlyStationProbe.exe";
    private sealed record InputDocument(int schema_version, string source_snapshot_id, string case_root,
        string payload_directory, string executable, FileBinding[] files);

    internal static PreparedInputs Load(string absoluteManifestPath)
    {
        var pins = new List<FileStream>(); bool transferred = false;
        try
        {
            string manifestPath = PlainPath(absoluteManifestPath, false);
            var input = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read); pins.Add(input);
            if (input.Length is < 2 or > 4194304) throw new InvalidDataException("MANIFEST_SIZE_INVALID");
            byte[] bytes = new byte[checked((int)input.Length)]; input.ReadExactly(bytes); input.Position = 0;
            using (JsonDocument json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }))
            {
                if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("MANIFEST_OBJECT_REQUIRED");
                RejectDuplicateKeys(json.RootElement);
            }
            InputDocument manifest = JsonSerializer.Deserialize<InputDocument>(bytes, new JsonSerializerOptions { MaxDepth = 16 })
                ?? throw new InvalidDataException("MANIFEST_NULL");
            RequireFixedBindings(manifest.schema_version, manifest.source_snapshot_id, manifest.executable);
            string caseRoot = PlainPath(manifest.case_root, true), payload = PlainPath(manifest.payload_directory, true);
            if (!string.Equals(Path.GetPathRoot(caseRoot), @"D:\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("EXISTING_D_VOLUME_CASE_ROOT_REQUIRED");
            string self = Environment.ProcessPath ?? throw new InvalidDataException("COMPILED_APPHOST_REQUIRED");
            if (!string.Equals(Path.GetFullPath(self), Path.Combine(payload, Entry), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("RUNNING_APPHOST_MUST_MATCH_FROZEN_ENTRY");
            FileBinding[] files = manifest.files;
            if (files is null || files.Length is < 1 or > 10000 || !files.Any(f => f is not null && f.path == Entry))
                throw new InvalidDataException("COMPLETE_OWN_PUBLISH_SET_REQUIRED");
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (FileBinding binding in files)
            {
                if (binding is null || binding.bytes < 0 || binding.sha256 is null ||
                    !Regex.IsMatch(binding.sha256, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("FILE_BINDING_INVALID");
                string path = Bound(payload, binding.path);
                if (!expected.Add(path)) throw new InvalidDataException("DUPLICATE_PUBLISH_FILE_BINDING");
                PlainPath(path, false);
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); pins.Add(file);
                if (file.Length != binding.bytes || Convert.ToHexStringLower(SHA256.HashData(file)) != binding.sha256)
                    throw new InvalidDataException("PINNED_PUBLISH_BYTES_CHANGED");
                file.Position = 0;
            }
            if (!expected.SetEquals(EnumeratePlainFiles(payload))) throw new InvalidDataException("COMPLETE_PUBLISH_FILE_SET_MISMATCH");
            var prepared = new PreparedInputs(manifest.schema_version, manifest.source_snapshot_id, caseRoot, payload, Entry,
                files, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            prepared.AdoptPins(pins); transferred = true; return prepared;
        }
        finally { if (!transferred) foreach (FileStream file in pins) file.Dispose(); }
    }
    // The actual Load path and pure self-tests use these same string gates.
    internal static void RequireFixedBindings(int schemaVersion, string? sourceSnapshotId, string? executable)
    {
        if (schemaVersion != 1 || executable != Entry || sourceSnapshotId is null ||
            !Regex.IsMatch(sourceSnapshotId, "\\Asha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("FIXED_SCHEMA_SOURCE_AND_ENTRY_REQUIRED");
    }
    internal static string Bound(string root, string relative)
    {
        if (!Path.IsPathFullyQualified(root) || string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0') ||
            relative.Split('/', '\\').Any(s => s is "" or "." or ".." || s.EndsWith(' ') || s.EndsWith('.') || s.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException("PLAIN_RELATIVE_FILE_BINDING_REQUIRED");
        string path = Path.GetFullPath(Path.Combine(root, relative));
        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("PUBLISH_PATH_ESCAPE");
        return path;
    }
    private static IEnumerable<string> EnumeratePlainFiles(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out string? current))
        {
            PlainPath(current, true);
            foreach (string file in Directory.EnumerateFiles(current)) yield return PlainPath(file, false);
            foreach (string directory in Directory.EnumerateDirectories(current)) pending.Push(PlainPath(directory, true));
        }
    }
    private static string PlainPath(string path, bool directory)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException("ABSOLUTE_FIXED_LOCAL_PATH_REQUIRED");
        string full = Path.GetFullPath(path), drive = Path.GetPathRoot(full)!;
        if (drive.Length != 3 || drive[1] != ':' || new DriveInfo(drive).DriveType != DriveType.Fixed)
            throw new InvalidDataException("FIXED_LOCAL_VOLUME_REQUIRED");
        string current = drive;
        foreach (string segment in full[drive.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment.Contains(':') || segment.EndsWith(' ') || segment.EndsWith('.')) throw new InvalidDataException("PLAIN_PATH_SEGMENTS_REQUIRED");
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("REPARSE_POINT_REJECTED");
        }
        if (directory ? !Directory.Exists(full) : !File.Exists(full)) throw new InvalidDataException("EXISTING_PLAIN_INPUT_REQUIRED");
        return directory ? Path.TrimEndingDirectorySeparator(full) : full;
    }
    private static void RejectDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("DUPLICATE_MANIFEST_KEY"); RejectDuplicateKeys(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (JsonElement item in value.EnumerateArray()) RejectDuplicateKeys(item);
    }
}
