using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutumnOS.OwnedBrowserParent;

internal sealed record SessionInputs(int schema_version, string source_snapshot_id, string case_root,
    string core_directory, string core_executable, FileBinding[] core_files,
    string ui_directory, string ui_executable, FileBinding[] ui_files,
    string runtime_directory, string runtime_version, FileBinding[] runtime_files, string manifest_sha256) : IDisposable
{
    private List<FileStream> pins = [];
    internal void AdoptPins(List<FileStream> value) => pins = value;
    public void Dispose() { foreach (FileStream file in pins) file.Dispose(); pins.Clear(); }
}
internal static class ManifestInputs
{
    private const string CoreEntry = "AutumnOS.SessionBrowserPrototype.exe", UiEntry = "AutumnOS.SessionBrowserUI.exe", RuntimeVersion = "154.0.4258.53";
    private sealed record InputDocument(int schema_version, string source_snapshot_id, string case_root,
        string core_directory, string core_executable, FileBinding[] core_files,
        string ui_directory, string ui_executable, FileBinding[] ui_files,
        string runtime_directory, string runtime_version, FileBinding[] runtime_files);
    internal static SessionInputs Load(string absolutePath)
        => LoadBound(absolutePath, false);
    internal static SessionInputs LoadForViewer(string absolutePath)
        => LoadBound(absolutePath, true);
    private static SessionInputs LoadBound(string absolutePath, bool viewer)
    {
        var pins = new List<FileStream>(); bool transferred = false;
        try
        {
            string path = Plain(absolutePath, false);
            var manifest = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); pins.Add(manifest);
            if (manifest.Length is < 2 or > 4194304) throw new InvalidDataException("SESSION_MANIFEST_SIZE");
            byte[] bytes = new byte[checked((int)manifest.Length)]; manifest.ReadExactly(bytes); manifest.Position = 0;
            using (JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 }))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("SESSION_MANIFEST_OBJECT_REQUIRED");
                DuplicateKeys(document.RootElement);
            }
            InputDocument input = JsonSerializer.Deserialize<InputDocument>(bytes, new JsonSerializerOptions { MaxDepth = 16 })
                ?? throw new InvalidDataException("SESSION_MANIFEST_NULL");
            RequireFixedBindings(input.schema_version, input.source_snapshot_id, input.core_executable, input.ui_executable, input.runtime_version);
            string root = Plain(input.case_root, true), core = Plain(input.core_directory, true), ui = Plain(input.ui_directory, true), runtime = Plain(input.runtime_directory, true);
            if (!string.Equals(Path.GetPathRoot(root), @"D:\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("SESSION_EXISTING_D_CASE_ROOT_REQUIRED");
            string self = Environment.ProcessPath ?? throw new InvalidDataException("SESSION_APPHOST_REQUIRED");
            if (!string.Equals(Path.GetFullPath(self), Path.Combine(viewer ? ui : core, viewer ? UiEntry : CoreEntry), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("SESSION_RUNNING_FIXED_ENTRY_MISMATCH");
            Pin(core, input.core_files, CoreEntry, pins); Pin(ui, input.ui_files, UiEntry, pins); Pin(runtime, input.runtime_files, "msedgewebview2.exe", pins);
            var result = new SessionInputs(1, input.source_snapshot_id, root, core, CoreEntry, input.core_files, ui, UiEntry, input.ui_files,
                runtime, RuntimeVersion, input.runtime_files, Convert.ToHexStringLower(SHA256.HashData(bytes)));
            result.AdoptPins(pins); transferred = true; return result;
        }
        finally { if (!transferred) foreach (FileStream file in pins) file.Dispose(); }
    }
    internal static void RequireFixedBindings(int schema, string? source, string? core, string? ui, string? runtime)
    {
        if (schema != 1 || core != CoreEntry || ui != UiEntry || runtime != RuntimeVersion || source is null ||
            !Regex.IsMatch(source, "\\Asha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("SESSION_FIXED_MANIFEST_BINDINGS_REQUIRED");
    }
    private static void Pin(string directory, FileBinding[] files, string entry, List<FileStream> pins)
    {
        if (files is null || files.Length is < 1 or > 10000 || !files.Any(f => f is not null && f.path == entry)) throw new InvalidDataException("SESSION_COMPLETE_PUBLISH_SET_REQUIRED");
        foreach (FileBinding file in files)
            if (file is null || file.path is null || file.bytes < 0 || file.sha256 is null || !Regex.IsMatch(file.sha256, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant))
                throw new InvalidDataException("SESSION_FILE_BINDING_INVALID");
        OwnedFiles.Verify(directory, files);
        foreach (FileBinding binding in files)
        {
            string path = Plain(OwnedFiles.Bound(directory, binding.path), false);
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); pins.Add(file);
            if (file.Length != binding.bytes || OwnedFiles.Hash(file) != binding.sha256) throw new InvalidDataException("SESSION_PINNED_BYTES_CHANGED");
            file.Position = 0;
        }
        OwnedFiles.Verify(directory, files);
    }
    private static string Plain(string path, bool directory)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException("SESSION_ABSOLUTE_LOCAL_PATH_REQUIRED");
        string full = Path.GetFullPath(path); OwnedFiles.FixedLocal(full); OwnedFiles.PlainAncestors(full);
        string drive = Path.GetPathRoot(full)!;
        foreach (string part in full[drive.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
            if (part.Contains(':') || part.EndsWith(' ') || part.EndsWith('.')) throw new InvalidDataException("SESSION_PLAIN_PATH_REQUIRED");
        if (directory ? !Directory.Exists(full) : !File.Exists(full)) throw new InvalidDataException("SESSION_EXISTING_PLAIN_INPUT_REQUIRED");
        return directory ? Path.TrimEndingDirectorySeparator(full) : full;
    }
    private static void DuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("SESSION_DUPLICATE_MANIFEST_KEY"); DuplicateKeys(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (JsonElement item in value.EnumerateArray()) DuplicateKeys(item);
    }
}
