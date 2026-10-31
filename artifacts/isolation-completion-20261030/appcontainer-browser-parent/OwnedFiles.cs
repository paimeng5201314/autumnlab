using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AutumnOS.OwnedBrowserParent;

internal sealed record FileBinding(string path, string sha256, long bytes);
internal sealed record InputManifest(int schema_version, string case_root, string child_directory, string runtime_directory,
    string child_executable, string runtime_version, FileBinding[] child_files, FileBinding[] runtime_files);

internal static class OwnedFiles
{
    internal static InputManifest ReadManifest(string path)
    {
        PlainAncestors(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 4 * 1024 * 1024) throw new InvalidDataException("MANIFEST_TOO_LARGE");
        var manifest = JsonSerializer.Deserialize<InputManifest>(file) ?? throw new InvalidDataException("MANIFEST_NULL");
        if (manifest.schema_version != 1 || manifest.runtime_version != "154.0.4258.53" ||
            manifest.child_executable != "AutumnOS.OwnedAppContainerBrowserHost.exe") throw new InvalidDataException("FIXED_BINDINGS_REQUIRED");
        foreach (string directory in new[] { manifest.case_root, manifest.child_directory, manifest.runtime_directory })
        { FixedLocal(directory); PlainAncestors(directory); if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("PREPARED_INPUT_DIRECTORY_REQUIRED"); }
        Verify(manifest.child_directory, manifest.child_files); Verify(manifest.runtime_directory, manifest.runtime_files);
        if (!manifest.child_files.Any(f => f.path == manifest.child_executable) || !manifest.runtime_files.Any(f => f.path == "msedgewebview2.exe"))
            throw new InvalidDataException("ENTRY_BINDING_MISSING");
        return manifest;
    }
    internal static string Hash(Stream stream) => Convert.ToHexStringLower(SHA256.HashData(stream));
    internal static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static void Verify(string root, FileBinding[] files)
    {
        if (files.Length == 0 || files.Length > 10000) throw new InvalidDataException("MANIFEST_FILE_COUNT");
        var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in files)
        {
            string full = Bound(root, item.path);
            if (!expected.Add(full) || item.bytes < 0 || item.sha256.Length != 64 || !item.sha256.All(Uri.IsHexDigit)) throw new InvalidDataException("INVALID_FILE_BINDING");
            PlainAncestors(full);
            using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length != item.bytes || Hash(file) != item.sha256) throw new InvalidDataException("FROZEN_INPUT_HASH_MISMATCH");
        }
        if (!expected.SetEquals(EnumeratePlain(root))) throw new InvalidDataException("FROZEN_INPUT_FILE_SET_MISMATCH");
    }
    internal static string Bound(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':') || relative.Contains('\0') ||
            relative.Split('/', '\\').Any(s => s is "" or "." or ".." || s.EndsWith(' ') || s.EndsWith('.')))
            throw new InvalidDataException("PLAIN_RELATIVE_PATH_REQUIRED");
        string result = Path.GetFullPath(Path.Combine(root, relative));
        if (!result.StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("PATH_ESCAPE");
        return result;
    }
    private static IEnumerable<string> EnumeratePlain(string root)
    {
        Stack<string> pending = new(); pending.Push(root);
        while (pending.TryPop(out string? current))
        {
            PlainAncestors(current);
            foreach (string file in Directory.EnumerateFiles(current)) { PlainAncestors(file); yield return Path.GetFullPath(file); }
            foreach (string directory in Directory.EnumerateDirectories(current)) { PlainAncestors(directory); pending.Push(directory); }
        }
    }
    internal static void CopyFrozen(string source, string target, FileBinding[] files, string? packageSid)
    {
        CreatePrivateDirectory(target, packageSid, false);
        foreach (var file in files)
        {
            string original = Bound(source, file.path), destination = Bound(target, file.path);
            EnsureNewParents(target, Path.GetDirectoryName(destination)!, packageSid);
            PlainAncestors(original);
            using (var input = new FileStream(original, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { if (input.Length != file.bytes || Hash(input) != file.sha256) throw new InvalidDataException("SOURCE_CHANGED_BEFORE_COPY"); input.Position = 0; input.CopyTo(output); output.Flush(true); }
        }
        Verify(target, files);
    }
    private static void EnsureNewParents(string root, string path, string? sid)
    {
        if (path.Equals(root, StringComparison.OrdinalIgnoreCase)) return;
        if (!path.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("OWNED_PARENT_ESCAPE");
        string current = root;
        foreach (string segment in Path.GetRelativePath(root, path).Split('\\'))
        { current = Path.Combine(current, segment); if (!Directory.Exists(current)) CreatePrivateDirectory(current, sid, false); else PlainAncestors(current); }
    }
    internal static DirectorySecurity Descriptor(string? sid, bool writable)
    {
        DirectorySecurity security = new(); security.SetAccessRuleProtection(true, false);
        SecurityIdentifier owner = WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("CURRENT_OWNER_REQUIRED");
        security.SetOwner(owner);
        security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        if (sid is not null) security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid),
            writable ? FileSystemRights.Modify | FileSystemRights.Synchronize : FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
    internal static void CreatePrivateDirectory(string path, string? sid, bool writable)
    {
        PlainAncestors(Path.GetDirectoryName(path)!);
        if (Directory.Exists(path) || File.Exists(path)) throw new IOException("NEW_DIRECTORY_ONLY");
        byte[] descriptor = Descriptor(sid, writable).GetSecurityDescriptorBinaryForm();
        IntPtr memory = Marshal.AllocHGlobal(descriptor.Length);
        try
        {
            Marshal.Copy(descriptor, 0, memory, descriptor.Length);
            var attributes = new Native.SecurityAttributes { Length = (uint)Marshal.SizeOf<Native.SecurityAttributes>(), Descriptor = memory };
            Native.Check(Native.CreateDirectoryW(path, ref attributes), "create_new_private_directory");
        }
        finally { Marshal.FreeHGlobal(memory); }
        PlainAncestors(path);
    }
    internal static void PlainAncestors(string path)
    {
        string full = Path.GetFullPath(path); string drive = Path.GetPathRoot(full)!;
        string current = drive;
        foreach (string segment in full[drive.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("REPARSE_POINT_REJECTED");
        }
    }
    internal static void FixedLocal(string path)
    {
        if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("ABSOLUTE_LOCAL_PATH_REQUIRED");
        string root = Path.GetPathRoot(path)!;
        if (root.Length != 3 || root[1] != ':' || new DriveInfo(root).DriveType != DriveType.Fixed) throw new InvalidDataException("FIXED_LOCAL_VOLUME_REQUIRED");
    }
}
