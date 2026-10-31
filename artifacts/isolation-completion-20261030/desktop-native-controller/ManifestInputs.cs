using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutumnOS.OwnedBrowserParent;

internal sealed record SystemDllBinding(string name, string path, long bytes, string sha256, string file_version);

internal sealed record PreparedInputs(int schema_version, string source_snapshot_id, string case_root,
    string child_directory, string child_executable, FileBinding[] child_files,
    string controller_directory, string controller_executable, FileBinding[] controller_files,
    SystemDllBinding[] system_dlls, string manifest_sha256) : IDisposable
{
    private List<FileStream> pins = [];
    internal void AdoptPins(List<FileStream> value) => pins = value;
    // System DLLs are observed/rechecked, not held for the duration of the run.
    // These hashes are not proof of the actual modules mapped by LoadLibrary.
    internal void RecheckSystemDlls() => ManifestInputs.RecheckSystemDlls(system_dlls);
    public void Dispose()
    {
        foreach (FileStream file in pins) file.Dispose();
        pins.Clear();
    }
}

internal static class ManifestInputs
{
    private sealed record InputDocument(int schema_version, string source_snapshot_id, string case_root,
        string child_directory, string child_executable, FileBinding[] child_files,
        string controller_directory, string controller_executable, FileBinding[] controller_files,
        SystemDllBinding[] system_dlls);
    private const string ChildEntry = "AutumnOS.NativeDesktopLoader.exe";
    private const string ControllerEntry = "AutumnOS.OwnedDesktopNativeController.exe";

    internal static PreparedInputs Load(string absoluteManifestPath)
    {
        var pins = new List<FileStream>(); bool transferred = false;
        try
        {
            string manifestPath = PlainFile(absoluteManifestPath);
            var manifestFile = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read); pins.Add(manifestFile);
            if (manifestFile.Length is < 2 or > 4194304) throw new InvalidDataException("MANIFEST_SIZE_INVALID");
            byte[] bytes = new byte[checked((int)manifestFile.Length)]; manifestFile.ReadExactly(bytes); manifestFile.Position = 0;
            string manifestHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            using (JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 }))
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("MANIFEST_OBJECT_REQUIRED");
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonProperty property in document.RootElement.EnumerateObject())
                    if (!names.Add(property.Name)) throw new InvalidDataException("DUPLICATE_MANIFEST_FIELD");
            }
            InputDocument input = JsonSerializer.Deserialize<InputDocument>(bytes, new JsonSerializerOptions { MaxDepth = 32 })
                ?? throw new InvalidDataException("MANIFEST_NULL");
            if (input.schema_version != 1 || input.source_snapshot_id is null ||
                !Regex.IsMatch(input.source_snapshot_id, "\\Asha256:[0-9a-f]{64}\\z", RegexOptions.CultureInvariant))
                throw new InvalidDataException("ACTUAL_SOURCE_SNAPSHOT_BINDING_REQUIRED");
            string caseRoot = PlainDirectory(input.case_root);
            if (!string.Equals(Path.GetPathRoot(caseRoot), @"D:\", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("EXISTING_D_VOLUME_CASE_ROOT_REQUIRED");
            string childRoot = PlainDirectory(input.child_directory), controllerRoot = PlainDirectory(input.controller_directory);
            if (input.child_executable != ChildEntry || input.controller_executable != ControllerEntry)
                throw new InvalidDataException("FIXED_NATIVE_AND_CONTROLLER_ENTRY_REQUIRED");
            string self = Environment.ProcessPath ?? throw new InvalidDataException("CONTROLLER_COMPILED_APPHOST_REQUIRED");
            if (!string.Equals(Path.GetFullPath(self), Path.Combine(controllerRoot, ControllerEntry), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("RUNNING_CONTROLLER_MUST_MATCH_MANIFEST_ENTRY");

            PinPublishSet(childRoot, input.child_files, ChildEntry, pins);
            PinPublishSet(controllerRoot, input.controller_files, ControllerEntry, pins);
            if (input.system_dlls is null) throw new InvalidDataException("SYSTEM_DLL_METADATA_REQUIRED");
            RecheckSystemDlls(input.system_dlls);
            var prepared = new PreparedInputs(input.schema_version, input.source_snapshot_id, caseRoot, childRoot, ChildEntry,
                input.child_files, controllerRoot, ControllerEntry, input.controller_files, input.system_dlls, manifestHash);
            prepared.AdoptPins(pins); transferred = true; return prepared;
        }
        finally
        {
            if (!transferred) foreach (FileStream file in pins) file.Dispose();
        }
    }

    internal static void RecheckSystemDlls(SystemDllBinding[] expected)
    {
        if (expected.Length != 3) throw new InvalidDataException("EXACT_THREE_SYSTEM_DLLS_REQUIRED");
        string system = PlainDirectory(Environment.GetFolderPath(Environment.SpecialFolder.System, Environment.SpecialFolderOption.DoNotVerify));
        var names = new HashSet<string>(["user32.dll", "gdi32.dll", "shell32.dll"], StringComparer.Ordinal);
        foreach (SystemDllBinding entry in expected)
        {
            if (entry is null || entry.name is null || !names.Remove(entry.name) || entry.bytes <= 0 || !ValidHash(entry.sha256) ||
                string.IsNullOrWhiteSpace(entry.file_version) || entry.file_version.Length > 256)
                throw new InvalidDataException("SYSTEM_DLL_METADATA_BINDING_INVALID");
            string actualPath = PlainFile(entry.path), requiredPath = Path.Combine(system, entry.name);
            if (!string.Equals(actualPath, requiredPath, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("EXACT_SYSTEM32_DLL_PATH_REQUIRED");
            // A short observation only. Allow OS maintenance to replace/update
            // these system files; do not hold share restrictions across a run.
            string? beforeVersion = FileVersionInfo.GetVersionInfo(actualPath).FileVersion;
            var before = new FileInfo(actualPath); long beforeBytes = before.Length; DateTime beforeWrite = before.LastWriteTimeUtc;
            string hash;
            using (var file = new FileStream(actualPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (file.Length != entry.bytes) throw new InvalidDataException("SYSTEM_DLL_OPEN_HANDLE_LENGTH_CHANGED");
                hash = OwnedFiles.Hash(file);
            }
            string? afterVersion = FileVersionInfo.GetVersionInfo(actualPath).FileVersion;
            var after = new FileInfo(actualPath);
            if (beforeBytes != entry.bytes || after.Length != entry.bytes || beforeWrite != after.LastWriteTimeUtc ||
                !string.Equals(beforeVersion, entry.file_version, StringComparison.Ordinal) ||
                !string.Equals(afterVersion, entry.file_version, StringComparison.Ordinal) || hash != entry.sha256)
                throw new InvalidDataException("OBSERVED_SYSTEM_DLL_BYTES_OR_VERSION_CHANGED");
        }
        if (names.Count != 0) throw new InvalidDataException("SYSTEM_DLL_NAME_SET_INCOMPLETE");
    }

    private static void PinPublishSet(string directory, FileBinding[] files, string entry, List<FileStream> pins)
    {
        if (files is null || files.Length is < 1 or > 10000 || !files.Any(file => file is not null && file.path == entry))
            throw new InvalidDataException("COMPLETE_PUBLISH_FILE_SET_REQUIRED");
        foreach (FileBinding file in files)
            if (file is null || file.path is null || file.bytes < 0 || !ValidHash(file.sha256)) throw new InvalidDataException("PUBLISH_FILE_BINDING_INVALID");
        OwnedFiles.Verify(directory, files);
        foreach (FileBinding binding in files)
        {
            string path = PlainFile(OwnedFiles.Bound(directory, binding.path));
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); pins.Add(file);
            if (file.Length != binding.bytes || OwnedFiles.Hash(file) != binding.sha256)
                throw new InvalidDataException("PINNED_PUBLISH_BYTES_CHANGED");
            file.Position = 0;
        }
    }
    private static bool ValidHash(string? value) => value is not null && Regex.IsMatch(value, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);
    private static string PlainDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException("ABSOLUTE_LOCAL_DIRECTORY_REQUIRED");
        string full = Path.GetFullPath(path); OwnedFiles.FixedLocal(full); OwnedFiles.PlainAncestors(full);
        if (!Directory.Exists(full)) throw new InvalidDataException("EXISTING_DIRECTORY_REQUIRED");
        return Path.TrimEndingDirectorySeparator(full);
    }
    private static string PlainFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) throw new InvalidDataException("ABSOLUTE_LOCAL_FILE_REQUIRED");
        string full = Path.GetFullPath(path); OwnedFiles.FixedLocal(full); OwnedFiles.PlainAncestors(full);
        if (!File.Exists(full)) throw new InvalidDataException("EXISTING_FILE_REQUIRED");
        return full;
    }
}
