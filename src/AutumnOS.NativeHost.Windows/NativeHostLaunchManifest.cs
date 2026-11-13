using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.NativeHost.Windows;

/// <summary>
/// Strict host-only launch input. Holds FileShare.Read pins for the manifest,
/// parent executable and every payload file, plus no-delete-share directory pins.
/// Directory pins prevent rename, not creation of extra children: the native owner
/// must also control staging-directory write access for the entire host lifetime.
/// A parsed parent PID/creation time is a binding, never proof of an original process.
/// </summary>
public sealed class NativeHostLaunchManifest : IDisposable
{
    public const int MaximumManifestBytes = 4 * 1024 * 1024;
    public const string RequiredHostExecutable = "AutumnOS.NativeHost.Windows.exe";
    public const string RequiredRuntimeVersion = "154.0.4258.53";

    private readonly NativePinReleaseGroup pins;
    public string ManifestPath { get; }
    public string ManifestSha256 { get; }
    public NativeHostBinding Binding { get; }
    public string Nonce { get; }
    public string SessionDirectory { get; }
    public string HostDirectory { get; }
    public string HostExecutable { get; }
    public string RuntimeDirectory { get; }
    public string RuntimeVersion { get; }
    public string PackageDirectory { get; }
    public string PackageEntry { get; }
    public uint ParentProcessId { get; }
    public ulong ParentCreationFileTime { get; }
    public string ParentExecutable { get; }
    public string ParentExecutableSha256 { get; }
    public IReadOnlyList<NativeHostFileBinding> HostFiles { get; }
    public IReadOnlyList<NativeHostFileBinding> RuntimeFiles { get; }
    public IReadOnlyList<NativeHostFileBinding> PackageFiles { get; }

    private NativeHostLaunchManifest(string path, string sha, Parsed input, List<IDisposable> held)
    {
        ManifestPath = path; ManifestSha256 = sha; Binding = input.Binding; Nonce = input.Nonce;
        SessionDirectory = input.SessionDirectory; HostDirectory = input.HostDirectory;
        HostExecutable = RequiredHostExecutable; RuntimeDirectory = input.RuntimeDirectory;
        RuntimeVersion = RequiredRuntimeVersion; PackageDirectory = input.PackageDirectory;
        PackageEntry = input.PackageEntry; ParentProcessId = input.ParentProcessId;
        ParentCreationFileTime = input.ParentCreationFileTime; ParentExecutable = input.ParentExecutable;
        ParentExecutableSha256 = input.ParentExecutableSha256;
        HostFiles = Array.AsReadOnly(input.HostFiles); RuntimeFiles = Array.AsReadOnly(input.RuntimeFiles);
        PackageFiles = Array.AsReadOnly(input.PackageFiles); pins = new(held);
    }

    public static NativeHostLaunchManifest LoadPinned(string absolutePath, NativeHostBinding expectedBinding)
    {
        ArgumentNullException.ThrowIfNull(expectedBinding);
        expectedBinding.Validate();
        return LoadCore(absolutePath, expectedBinding, null);
    }

    // Role bootstrap is pinned to a SHA passed by the trusted launcher. Its parsed
    // binding remains untrusted until the role verifies the original parent handle,
    // creation time, image bytes and kernel pipe-server identity against this input.
    internal static NativeHostLaunchManifest LoadPinnedForRole(string absolutePath, string expectedManifestSha256)
    {
        if (!NativeHostBinding.IsLowerSha256(expectedManifestSha256))
            throw new ArgumentException("NATIVE_HOST_MANIFEST_SHA_INVALID", nameof(expectedManifestSha256));
        return LoadCore(absolutePath, null, expectedManifestSha256);
    }

    private static NativeHostLaunchManifest LoadCore(string absolutePath, NativeHostBinding? expectedBinding, string? expectedSha)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("NATIVE_HOST_WINDOWS_REQUIRED");
        var held = new List<IDisposable>();
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            string manifestPath = PlainAbsolute(absolutePath, directory: false);
            PinAncestors(Path.GetDirectoryName(manifestPath)!, held, directories);
            FileStream manifest = OpenPinnedFile(manifestPath, held);
            if (manifest.Length is < 1 or > MaximumManifestBytes) throw Invalid("MANIFEST_SIZE");
            byte[] bytes = new byte[checked((int)manifest.Length)];
            manifest.ReadExactly(bytes); manifest.Position = 0;
            string sha = Convert.ToHexStringLower(SHA256.HashData(bytes));
            if (expectedSha is not null && sha != expectedSha) throw Invalid("MANIFEST_SHA");
            Parsed input = Parse(bytes);
            if (expectedBinding is not null && input.Binding != expectedBinding) throw Invalid("BINDING_MISMATCH");

            string[] roots = [input.SessionDirectory, input.HostDirectory, input.RuntimeDirectory, input.PackageDirectory];
            for (int left = 0; left < roots.Length; left++)
            for (int right = left + 1; right < roots.Length; right++)
                if (SameOrChild(roots[left], roots[right]) || SameOrChild(roots[right], roots[left]))
                    throw Invalid("OVERLAPPING_ROOTS");
            foreach (string root in roots) PinAncestors(root, held, directories);

            ValidateAndPinPayload(input.HostDirectory, input.HostFiles, held, directories);
            ValidateAndPinPayload(input.RuntimeDirectory, input.RuntimeFiles, held, directories);
            ValidateAndPinPayload(input.PackageDirectory, input.PackageFiles, held, directories);
            RequireEntry(input.HostFiles, RequiredHostExecutable);
            RequireEntry(input.RuntimeFiles, "msedgewebview2.exe");
            RequireEntry(input.PackageFiles, input.PackageEntry);

            PinAncestors(Path.GetDirectoryName(input.ParentExecutable)!, held, directories);
            FileStream parent = OpenPinnedFile(input.ParentExecutable, held);
            if (parent.Length < 1 || Hash(parent) != input.ParentExecutableSha256) throw Invalid("PARENT_EXECUTABLE_SHA");
            // Repeat all file-set checks after every expected file has been pinned.
            ValidateCompleteSet(input.HostDirectory, input.HostFiles, held, directories);
            ValidateCompleteSet(input.RuntimeDirectory, input.RuntimeFiles, held, directories);
            ValidateCompleteSet(input.PackageDirectory, input.PackageFiles, held, directories);
            if (Hash(manifest) != sha) throw Invalid("MANIFEST_CHANGED");
            return new(manifestPath, sha, input, held);
        }
        catch { DisposeAll(held, propagate: false); throw; }
    }

    public void Dispose() => pins.Dispose();

    // Native process auditing must use the exact roots whose bytes are pinned,
    // never guessed staging child paths under the disjoint session directory.
    internal (string Host, string Runtime) ProcessAuditRoots => (HostDirectory, RuntimeDirectory);

    private sealed record Parsed(NativeHostBinding Binding, string Nonce, string SessionDirectory,
        string HostDirectory, string RuntimeDirectory, string PackageDirectory, string PackageEntry,
        uint ParentProcessId, ulong ParentCreationFileTime, string ParentExecutable,
        string ParentExecutableSha256, NativeHostFileBinding[] HostFiles,
        NativeHostFileBinding[] RuntimeFiles, NativeHostFileBinding[] PackageFiles);

    private static Parsed Parse(byte[] bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
        JsonElement root = document.RootElement;
        Exact(root, "schemaVersion", "binding", "nonce", "sessionDirectory", "hostDirectory", "hostExecutable",
            "runtimeDirectory", "runtimeVersion", "packageDirectory", "packageEntry", "parentProcessId",
            "parentCreationFileTime", "parentExecutable", "parentExecutableSha256", "hostFiles", "runtimeFiles", "packageFiles");
        if (Number(root, "schemaVersion") != 1 || Text(root, "hostExecutable") != RequiredHostExecutable ||
            Text(root, "runtimeVersion") != RequiredRuntimeVersion) throw Invalid("SCHEMA_OR_FIXED_ENTRY");
        JsonElement binding = root.GetProperty("binding");
        Exact(binding, "authorityId", "account", "accountEpoch", "applicationBinding", "instanceId", "instanceEpoch",
            "documentId", "documentGeneration", "channelGeneration");
        var identity = new NativeHostBinding(GuidValue(binding, "authorityId"), Text(binding, "account"),
            Number(binding, "accountEpoch"), Text(binding, "applicationBinding"), GuidValue(binding, "instanceId"),
            Number(binding, "instanceEpoch"), GuidValue(binding, "documentId"), Number(binding, "documentGeneration"),
            Number(binding, "channelGeneration"));
        identity.Validate();
        string nonce = Text(root, "nonce");
        if (!NativeHostBinding.IsLowerSha256(nonce)) throw Invalid("NONCE");
        JsonElement pid = root.GetProperty("parentProcessId"), creation = root.GetProperty("parentCreationFileTime");
        if (pid.ValueKind != JsonValueKind.Number || !pid.TryGetUInt32(out uint parentPid) || parentPid == 0 ||
            creation.ValueKind != JsonValueKind.Number || !creation.TryGetUInt64(out ulong parentCreation) || parentCreation == 0)
            throw Invalid("PARENT_PROCESS_BINDING");
        string parent = PlainAbsolute(Text(root, "parentExecutable"), directory: false);
        string parentSha = Text(root, "parentExecutableSha256");
        if (!parent.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !NativeHostBinding.IsLowerSha256(parentSha))
            throw Invalid("PARENT_EXECUTABLE_BINDING");
        return new(identity, nonce, PlainAbsolute(Text(root, "sessionDirectory"), directory: true),
            PlainAbsolute(Text(root, "hostDirectory"), directory: true), PlainAbsolute(Text(root, "runtimeDirectory"), directory: true),
            PlainAbsolute(Text(root, "packageDirectory"), directory: true), Relative(Text(root, "packageEntry")),
            parentPid, parentCreation, parent, parentSha, Files(root, "hostFiles"), Files(root, "runtimeFiles"), Files(root, "packageFiles"));
    }

    private static NativeHostFileBinding[] Files(JsonElement root, string property)
    {
        JsonElement array = root.GetProperty(property);
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is < 1 or > 32768) throw Invalid("FILE_BINDINGS");
        var result = new NativeHostFileBinding[array.GetArrayLength()];
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int index = 0;
        foreach (JsonElement value in array.EnumerateArray())
        {
            Exact(value, "path", "bytes", "sha256");
            string path = Relative(Text(value, "path")); long length = Number(value, "bytes");
            string hash = Text(value, "sha256");
            if (length < 0 || !NativeHostBinding.IsLowerSha256(hash) || !seen.Add(path)) throw Invalid("FILE_BINDING");
            result[index++] = new(path, length, hash);
        }
        return result;
    }
    private static void Exact(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid("OBJECT_REQUIRED");
        var expected = new HashSet<string>(names, StringComparer.Ordinal);
        foreach (JsonProperty property in value.EnumerateObject())
            if (!expected.Remove(property.Name)) throw Invalid("UNKNOWN_OR_DUPLICATE_PROPERTY");
        if (expected.Count != 0) throw Invalid("MISSING_PROPERTY");
    }
    private static string Text(JsonElement value, string property)
    {
        JsonElement text = value.GetProperty(property);
        if (text.ValueKind != JsonValueKind.String || text.GetString() is not { Length: > 0 } result) throw Invalid("STRING_REQUIRED");
        return result;
    }
    private static long Number(JsonElement value, string property)
    {
        JsonElement number = value.GetProperty(property);
        if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt64(out long result)) throw Invalid("INTEGER_REQUIRED");
        return result;
    }
    private static Guid GuidValue(JsonElement value, string property)
    {
        if (!Guid.TryParseExact(Text(value, property), "D", out Guid result)) throw Invalid("GUID_REQUIRED");
        return result;
    }

    // Host-only validation shared by the Shell before it reads installation metadata.
    // This validates an existing path; it neither grants access nor admits a package.
    public static string PlainAbsolute(string value, bool directory)
    {
        if (value.Length is < 4 or > 32760 || value[1] != ':' || value[2] != '\\' ||
            !(value[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z') || value.Contains('/') || value.EndsWith('\\'))
            throw Invalid("PLAIN_ABSOLUTE_PATH");
        foreach (string part in value[3..].Split('\\')) ValidateComponent(part);
        string full = Path.GetFullPath(value);
        if (!full.Equals(value, StringComparison.OrdinalIgnoreCase) || !Path.IsPathFullyQualified(full) ||
            new DriveInfo(Path.GetPathRoot(full)!).DriveType != DriveType.Fixed) throw Invalid("FIXED_LOCAL_PATH");
        FileAttributes attributes = File.GetAttributes(full);
        if ((attributes & FileAttributes.ReparsePoint) != 0 || ((attributes & FileAttributes.Directory) != 0) != directory)
            throw Invalid("PLAIN_FILE_OR_DIRECTORY");
        return full;
    }
    internal static string Relative(string value)
    {
        if (value.Length is < 1 or > 32760 || Path.IsPathRooted(value)) throw Invalid("RELATIVE_PATH");
        string normalized = value.Replace('\\', '/');
        foreach (string part in normalized.Split('/')) ValidateComponent(part);
        return normalized;
    }
    private static void ValidateComponent(string value)
    {
        if (value is "" or "." or ".." || value.EndsWith(' ') || value.EndsWith('.') || value.Any(char.IsControl) ||
            value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw Invalid("PLAIN_PATH_COMPONENT");
        string stem = value.Split('.')[0];
        if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
            stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
            stem[3] is >= '1' and <= '9') throw Invalid("DEVICE_PATH_COMPONENT");
    }
    private static bool SameOrChild(string candidate, string root) => candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void RequireEntry(NativeHostFileBinding[] files, string entry)
    {
        NativeHostFileBinding? file = files.SingleOrDefault(file => file.Path.Equals(entry, StringComparison.Ordinal));
        if (file is null || file.Bytes < 1) throw Invalid("REQUIRED_ENTRY");
    }

    private static void ValidateAndPinPayload(string root, NativeHostFileBinding[] bindings, List<IDisposable> held, HashSet<string> directories)
    {
        ValidateCompleteSet(root, bindings, held, directories);
        foreach (NativeHostFileBinding binding in bindings)
        {
            string full = Path.GetFullPath(Path.Combine(root, binding.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!SameOrChild(full, root) || full.Equals(root, StringComparison.OrdinalIgnoreCase)) throw Invalid("ESCAPED_FILE_BINDING");
            PinAncestors(Path.GetDirectoryName(full)!, held, directories);
            FileStream file = OpenPinnedFile(full, held);
            if (file.Length != binding.Bytes || Hash(file) != binding.Sha256 || file.Length != binding.Bytes) throw Invalid("PAYLOAD_FILE_SHA_OR_LENGTH");
        }
        ValidateCompleteSet(root, bindings, held, directories);
    }
    private static void ValidateCompleteSet(string root, NativeHostFileBinding[] bindings, List<IDisposable> held, HashSet<string> directories)
    {
        var expected = bindings.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var expectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (NativeHostFileBinding binding in bindings)
        {
            int slash = binding.Path.LastIndexOf('/');
            while (slash > 0) { string parent = binding.Path[..slash]; expectedDirectories.Add(parent); slash = parent.LastIndexOf('/'); }
        }
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count != 0)
        {
            string directory = pending.Pop(); PinAncestors(directory, held, directories);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw Invalid("PAYLOAD_REPARSE_POINT");
                string relative = Relative(Path.GetRelativePath(root, entry));
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!expectedDirectories.Contains(relative)) throw Invalid("UNBOUND_PAYLOAD_DIRECTORY");
                    PinAncestors(entry, held, directories); pending.Push(entry);
                }
                else if (!expected.ContainsKey(relative) || !seen.Add(relative)) throw Invalid("PAYLOAD_FILE_SET");
            }
        }
        if (seen.Count != expected.Count) throw Invalid("PAYLOAD_FILE_SET");
    }
    internal static void PinAncestors(string directory, List<IDisposable> held, HashSet<string> directories)
    {
        string full = Path.GetFullPath(directory), root = Path.GetPathRoot(full)!;
        if (root.Length != 3 || new DriveInfo(root).DriveType != DriveType.Fixed) throw Invalid("FIXED_LOCAL_DIRECTORY");
        string current = root;
        PinDirectory(current, held, directories);
        foreach (string component in full[root.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        { ValidateComponent(component); current = Path.Combine(current, component); PinDirectory(current, held, directories); }
    }
    private static void PinDirectory(string full, List<IDisposable> held, HashSet<string> directories)
    {
        if (directories.Contains(full)) return;
        FileAttributes observed = File.GetAttributes(full);
        if ((observed & FileAttributes.Directory) == 0 || (observed & FileAttributes.ReparsePoint) != 0) throw Invalid("DIRECTORY_REPARSE_OR_TYPE");
        SafeFileHandle handle = NativePins.CreateFileW("\\\\?\\" + full, 0x80, 0x00000001 | 0x00000002, IntPtr.Zero, 3,
            0x02000000 | 0x00200000, IntPtr.Zero); // READ_ATTRIBUTES; share R/W, not DELETE; BACKUP|OPEN_REPARSE
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "NATIVE_HOST_DIRECTORY_PIN_FAILED"); }
        try { VerifyHandle(handle, full, directory: true); held.Add(handle); directories.Add(full); }
        catch { handle.Dispose(); throw; }
    }
    internal static FileStream OpenPinnedFile(string full, List<IDisposable> held)
    {
        _ = PlainAbsolute(full, directory: false);
        var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        try { VerifyHandle(stream.SafeFileHandle, full, directory: false); held.Add(stream); return stream; }
        catch { stream.Dispose(); throw; }
    }
    internal static void VerifyHandle(SafeFileHandle handle, string expectedPath, bool directory)
    {
        if (!NativePins.GetFileInformationByHandle(handle, out NativePins.FileInformation facts))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "NATIVE_HOST_FILE_INFORMATION_FAILED");
        if ((facts.Attributes & 0x400) != 0 || ((facts.Attributes & 0x10) != 0) != directory) throw Invalid("PINNED_OBJECT_TYPE");
        var buffer = new StringBuilder(1024);
        uint count = NativePins.GetFinalPathNameByHandleW(handle, buffer, checked((uint)buffer.Capacity), 0);
        if (count >= buffer.Capacity)
        {
            if (count > 32768) throw Invalid("PINNED_FINAL_PATH_SIZE");
            buffer = new StringBuilder(checked((int)count + 1));
            count = NativePins.GetFinalPathNameByHandleW(handle, buffer, checked((uint)buffer.Capacity), 0);
        }
        if (count == 0 || count >= buffer.Capacity) throw new Win32Exception(Marshal.GetLastWin32Error(), "NATIVE_HOST_FINAL_PATH_FAILED");
        string actual = buffer.ToString();
        if (!actual.StartsWith("\\\\?\\", StringComparison.Ordinal) || actual.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) throw Invalid("PINNED_FINAL_PATH");
        actual = actual[4..];
        if (!actual.Equals(expectedPath, StringComparison.OrdinalIgnoreCase)) throw Invalid("PINNED_PATH_CHANGED");
    }
    // An AppContainer may read an admitted file but cannot resolve its DOS drive
    // mapping. The trusted station keeps its original path/directory pins and
    // sends this kernel identity over the original inherited startup pipe.
    internal static NativeHostFileIdentity ReadPlainFileIdentity(SafeFileHandle handle)
    {
        if (!NativePins.GetFileInformationByHandle(handle, out NativePins.FileInformation facts))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "NATIVE_HOST_FILE_INFORMATION_FAILED");
        if ((facts.Attributes & (0x400 | 0x10)) != 0) throw Invalid("PINNED_OBJECT_TYPE");
        var identity = new NativeHostFileIdentity(facts.VolumeSerial, facts.FileIndexHigh, facts.FileIndexLow);
        if (!identity.IsValid) throw Invalid("PINNED_FILE_IDENTITY_REQUIRED");
        return identity;
    }
    private static string Hash(FileStream stream)
    {
        stream.Position = 0;
        string result = Convert.ToHexStringLower(SHA256.HashData(stream)); stream.Position = 0; return result;
    }
    private static InvalidDataException Invalid(string code) => new("NATIVE_HOST_" + code);
    private static void DisposeAll(List<IDisposable> values, bool propagate)
    {
        List<Exception>? failures = null;
        for (int index = values.Count - 1; index >= 0; index--)
            try { values[index].Dispose(); } catch (Exception error) { (failures ??= []).Add(error); }
        if (propagate && failures is not null) throw new AggregateException("NATIVE_HOST_PIN_RELEASE_FAILED", failures);
    }

    private static class NativePins
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct FileInformation
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh,
                VolumeSerial, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation facts);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
        internal static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder buffer, uint length, uint flags);
    }
}

internal readonly record struct NativeHostFileIdentity(uint VolumeSerial, uint FileIndexHigh, uint FileIndexLow)
{
    // A volume serial can legitimately be zero; a missing file index is never
    // accepted as an original-object identity.
    [System.Text.Json.Serialization.JsonIgnore]
    internal bool IsValid => FileIndexHigh != 0 || FileIndexLow != 0;
}

// Pure ownership helper, with no OS capability or test-only public factory.
// A failure is sticky: even a later no-op Dispose must keep reporting failure.
// A disposable may have partly released its resource before throwing, so a
// second call cannot safely reinterpret that uncertainty as successful release.
internal sealed class NativePinReleaseGroup : IDisposable
{
    private readonly object sync = new();
    private readonly List<IDisposable> owned;
    private AggregateException? failure;
    private bool releasing, released;
    internal NativePinReleaseGroup(IEnumerable<IDisposable> pins)
    {
        ArgumentNullException.ThrowIfNull(pins);
        owned = pins.ToList();
        if (owned.Any(pin => pin is null)) throw new ArgumentException("NATIVE_PIN_NULL");
    }
    internal bool Released { get { lock (sync) return released; } }
    public void Dispose()
    {
        lock (sync)
        {
            if (failure is not null) throw failure;
            if (released) return;
            if (releasing) throw new InvalidOperationException("NATIVE_PIN_RELEASE_REENTRANT");
            releasing = true;
            try
            {
                List<Exception>? errors = null;
                for (int index = owned.Count - 1; index >= 0; index--)
                    try { owned[index].Dispose(); } catch (Exception error) { (errors ??= []).Add(error); }
                if (errors is not null)
                {
                    failure = new AggregateException("NATIVE_HOST_PIN_RELEASE_FAILED", errors);
                    throw failure;
                }
                owned.Clear(); released = true;
            }
            finally { releasing = false; }
        }
    }
}
