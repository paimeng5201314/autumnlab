using System.ComponentModel;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.NativeHost.Windows;

public sealed record RuntimeHostStagingInput(string DirectoryPath, IReadOnlyList<NativeHostFileBinding> Files);

public sealed class RuntimeHostStagingRollbackException : AggregateException
{
    public RuntimeHostStaging PendingOwner { get; }
    internal RuntimeHostStagingRollbackException(RuntimeHostStaging owner, Exception preparation, Exception rollback)
        : base("NATIVE_STAGE_PREPARE_AND_CLEANUP_FAILED", preparation, rollback) => PendingOwner = owner;
}

/// <summary>Owns only freshly created objects. The Shell must retain this owner until the original process group has retired.</summary>
public sealed class RuntimeHostStaging : IDisposable
{
    private readonly List<StageObject> created = [];
    private readonly string scratch, root;
    private readonly SecurityIdentifier owner, packageSid;
    private NativeHostLaunchManifest? heldManifest;
    private SourcePins? preparationPins;
    private bool unboundCreatedObject, disposed;
    public string ManifestPath { get; }
    public string Nonce { get; }
    public string ProfileSid => packageSid.Value;

    private RuntimeHostStaging(string scratchDirectory)
    {
        scratch = NativeHostLaunchManifest.PlainAbsolute(scratchDirectory, true);
        Nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        root = Path.Combine(scratch, StageLeaf(Nonce));
        ManifestPath = Path.Combine(root, "session", "launch.json");
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        owner = identity.User ?? throw new InvalidOperationException("NATIVE_STAGE_OWNER_REQUIRED");
        packageSid = DeriveProfileSid(Nonce);
    }

    public static Task<RuntimeHostStaging> CreateAsync(string existingScratchDirectory, NativeHostBinding binding, string packageEntry,
        RuntimeHostStagingInput host, RuntimeHostStagingInput runtime, RuntimeHostStagingInput package, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(binding);
        binding.Validate();
        NativeHostLaunchManifest.Relative(packageEntry);
        var trustedHost = Freeze(host);
        var trustedRuntime = Freeze(runtime);
        var trustedPackage = Freeze(package);
        return Task.Run(() =>
        {
            var stage = new RuntimeHostStaging(existingScratchDirectory);
            try
            {
                stage.Prepare(binding, packageEntry, trustedHost, trustedRuntime, trustedPackage, cancellationToken);
                return stage;
            }
            catch (Exception preparation)
            {
                try { stage.Dispose(); }
                catch (Exception cleanup) { throw new RuntimeHostStagingRollbackException(stage, preparation, cleanup); }
                throw;
            }
        }, cancellationToken);
    }

    private static RuntimeHostStagingInput Freeze(RuntimeHostStagingInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Files is null || input.Files.Count is < 1 or > 32768) throw Invalid("INPUT_COUNT");
        NativeHostFileBinding[] files = input.Files.ToArray();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in files)
        {
            if (file is null || file.Path != NativeHostLaunchManifest.Relative(file.Path) || !paths.Add(file.Path) ||
                file.Bytes is < 0 or > 1_073_741_824 || (total += file.Bytes) > 4_294_967_296 ||
                !NativeHostBinding.IsLowerSha256(file.Sha256)) throw Invalid("INPUT_FILE_BINDING");
        }
        return new(NativeHostLaunchManifest.PlainAbsolute(input.DirectoryPath, true), Array.AsReadOnly(files));
    }

    // Call only for an installation already admitted by PackageInstaller. The original
    // trusted archive digest anchors the per-file table independently of the installed receipt.
    public static RuntimeHostStagingInput FromInstalledArchive(string installedDirectory, string originalAutumnPath, string expectedArchiveSha256)
    {
        if (!NativeHostBinding.IsLowerSha256(expectedArchiveSha256)) throw Invalid("PACKAGE_DIGEST");
        string directory = NativeHostLaunchManifest.PlainAbsolute(installedDirectory, true);
        string archivePath = NativeHostLaunchManifest.PlainAbsolute(originalAutumnPath, false);
        if (!archivePath.EndsWith(".autumn", StringComparison.OrdinalIgnoreCase)) throw Invalid("PACKAGE_EXTENSION");
        using var sources = new SourcePins();
        FileStream input = sources.Open(archivePath);
        if (input.Length is < 1 or > 20 * 1024 * 1024 || Convert.ToHexStringLower(SHA256.HashData(input)) != expectedArchiveSha256)
            throw Invalid("PACKAGE_ARCHIVE_HASH");
        input.Position = 0;
        using var zip = new ZipArchive(input, ZipArchiveMode.Read, true);
        if (zip.Entries.Count is < 1 or > 128) throw Invalid("PACKAGE_COUNT");
        long total = 0;
        var files = new List<NativeHostFileBinding>();
        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            if (entry.FullName.EndsWith('/'))
            {
                NativeHostLaunchManifest.Relative(entry.FullName[..^1]);
                if (entry.Length != 0) throw Invalid("PACKAGE_DIRECTORY");
                continue;
            }
            if (entry.Length is < 0 or > 8 * 1024 * 1024 || (total += entry.Length) > 16 * 1024 * 1024 ||
                entry.FullName != NativeHostLaunchManifest.Relative(entry.FullName) ||
                entry.FullName.Equals(".autumnos-install.json", StringComparison.OrdinalIgnoreCase)) throw Invalid("PACKAGE_ENTRY");
            using Stream content = entry.Open();
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            CopyExactlyAndHash(content, null, entry.Length, digest, CancellationToken.None);
            files.Add(new(entry.FullName, entry.Length, Convert.ToHexStringLower(digest.GetHashAndReset())));
        }
        return Freeze(new(directory, files));
    }

    private void Prepare(NativeHostBinding binding, string entry, RuntimeHostStagingInput host, RuntimeHostStagingInput runtime,
        RuntimeHostStagingInput package, CancellationToken cancellationToken)
    {
        var sources = preparationPins = new SourcePins();
        sources.HoldDirectory(scratch);
        foreach (RuntimeHostStagingInput input in new[] { host, runtime, package })
        {
            if (Within(root, input.DirectoryPath) || Within(input.DirectoryPath, root)) throw Invalid("OVERLAPPING_SOURCE");
        }
        cancellationToken.ThrowIfCancellationRequested();
        AddDirectory(root);
        string session = Path.Combine(root, "session");
        AddDirectory(session);
        string hostPath = CopyTree(sources, host, "host", cancellationToken);
        string runtimePath = CopyTree(sources, runtime, "runtime", cancellationToken);
        string packagePath = CopyTree(sources, package, "package", cancellationToken);
        string parent = Environment.ProcessPath ?? throw Invalid("PARENT_EXE");
        FileStream parentFile = sources.Open(parent);
        Native.Check(Native.GetProcessTimes(Native.GetCurrentProcess(), out var creation, out _, out _, out _), "stage_original_parent_creation");
        byte[] manifest = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1, binding, nonce = Nonce, sessionDirectory = session,
            hostDirectory = hostPath, hostExecutable = NativeHostLaunchManifest.RequiredHostExecutable,
            runtimeDirectory = runtimePath, runtimeVersion = NativeHostLaunchManifest.RequiredRuntimeVersion,
            packageDirectory = packagePath, packageEntry = entry, parentProcessId = (uint)Environment.ProcessId,
            parentCreationFileTime = creation.Value, parentExecutable = parent, parentExecutableSha256 = Convert.ToHexStringLower(SHA256.HashData(parentFile)),
            hostFiles = host.Files, runtimeFiles = runtime.Files, packageFiles = package.Files
        });
        if (manifest.Length > NativeHostLaunchManifest.MaximumManifestBytes) throw Invalid("MANIFEST_SIZE");
        using (var stream = CreateFile(ManifestPath)) { stream.Write(manifest); stream.Flush(true); }
        foreach (var item in created.AsEnumerable().Reverse())
        {
            cancellationToken.ThrowIfCancellationRequested();
            Seal(item);
        }
        foreach (StageObject item in created.Where(item => item.Directory))
            item.BlockDirectoryWriters();
        heldManifest = NativeHostLaunchManifest.LoadPinned(ManifestPath, binding);
        RequireSealed(heldManifest);
        cancellationToken.ThrowIfCancellationRequested();
        ReleasePreparationPins();
    }

    private string CopyTree(SourcePins pins, RuntimeHostStagingInput input, string role, CancellationToken cancellationToken)
    {
        string target = Path.Combine(root, role);
        AddDirectory(target);
        pins.HoldDirectory(input.DirectoryPath);
        var expected = input.Files.ToDictionary(item => item.Path, StringComparer.OrdinalIgnoreCase);
        var expectedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string relativePath in expected.Keys)
        {
            string? parent = Path.GetDirectoryName(relativePath.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(parent) && expectedDirectories.Add(parent.Replace(Path.DirectorySeparatorChar, '/')))
                parent = Path.GetDirectoryName(parent);
        }
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var targetDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target };
        var pending = new Stack<string>();
        pending.Push(input.DirectoryPath);
        while (pending.TryPop(out string? next))
        {
            foreach (string path in Directory.EnumerateFileSystemEntries(next))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw Invalid("SOURCE_REPARSE");
                string relative = Path.GetRelativePath(input.DirectoryPath, path).Replace('\\', '/');
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!expectedDirectories.Contains(relative)) throw Invalid("UNLISTED_DIRECTORY");
                    pins.HoldDirectory(path);
                    pending.Push(path);
                    continue;
                }
                if (role == "package" && relative == ".autumnos-install.json") continue;
                if (!expected.TryGetValue(relative, out var file) || relative != file.Path || !visited.Add(relative)) throw Invalid("UNLISTED_SOURCE");
                string outputPath = OwnedFiles.Bound(target, relative);
                EnsureParents(target, Path.GetDirectoryName(outputPath)!, targetDirectories);
                var source = pins.Open(path);
                using var output = CreateFile(outputPath);
                if (source.Length != file.Bytes) throw Invalid("SOURCE_LENGTH");
                using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                CopyExactlyAndHash(source, output, file.Bytes, digest, cancellationToken);
                if (Convert.ToHexStringLower(digest.GetHashAndReset()) != file.Sha256) throw Invalid("SOURCE_HASH");
                output.Flush(true);
            }
        }
        if (visited.Count != expected.Count) throw Invalid("MISSING_SOURCE_FILE");
        return target;
    }

    private static void CopyExactlyAndHash(Stream input, Stream? output, long bytes, IncrementalHash digest, CancellationToken token)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(65536);
        try
        {
            long remaining = bytes;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                int count = input.Read(buffer.AsSpan(0, (int)Math.Min(remaining, buffer.Length)));
                if (count == 0) throw Invalid("SOURCE_TRUNCATED");
                digest.AppendData(buffer.AsSpan(0, count));
                output?.Write(buffer, 0, count);
                remaining -= count;
            }
            if (input.ReadByte() != -1) throw Invalid("SOURCE_GREW");
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer, clearArray: true); }
    }


    private void EnsureParents(string start, string directory, HashSet<string> known)
    {
        if (known.Contains(directory)) return;
        if (!Within(directory, start)) throw Invalid("TARGET_ESCAPE");
        string current = start;
        foreach (string segment in Path.GetRelativePath(start, directory).Split(Path.DirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if (known.Add(current)) AddDirectory(current);
        }
    }

    private void AddDirectory(string path)
    {
        try { OwnedFiles.CreatePrivateDirectory(path, null, false); }
        catch
        {
            // The native create can succeed before the subsequent path check
            // fails. An existing unbound name does not authorize its deletion.
            if (Path.Exists(path)) unboundCreatedObject = true;
            throw;
        }
        unboundCreatedObject = true;
        created.Add(StageObject.Capture(path, true));
        unboundCreatedObject = false;
    }

    private FileStream CreateFile(string path)
    {
        var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 65536, FileOptions.SequentialScan);
        unboundCreatedObject = true;
        try
        {
            created.Add(StageObject.Capture(path, false, stream.SafeFileHandle));
            unboundCreatedObject = false;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    // The whole nonce authenticates wire/manifest data. 128 bits in an
    // atomically created directory name leave room for native image paths.
    private static string StageLeaf(string nonce) => "n-" + nonce[..32];

    private static bool Within(string path, string basePath) => path.Equals(basePath, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(Path.TrimEndingDirectorySeparator(basePath) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private void Seal(StageObject item)
    {
        FileSystemSecurity security = item.Directory ? new DirectorySecurity() : new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.SetOwner(owner);
        foreach (var rule in Rules(owner, packageSid)) security.AddAccessRule(rule);
        item.SetSecurityOnOriginal(security);
    }

    // OWNER RIGHTS explicitly replaces the implicit WRITE_DAC normally available to
    // the owner. The parent retains DELETE for cleanup; there is no FILE_ADD_FILE,
    // FILE_ADD_SUBDIRECTORY or WRITE_DAC grant on any sealed directory.
    private static FileSystemAccessRule[] Rules(SecurityIdentifier owner, SecurityIdentifier sid)
    {
        const FileSystemRights read = FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize;
        return [new(owner, read | FileSystemRights.Delete, AccessControlType.Allow),
            new(sid, read, AccessControlType.Allow),
            new(new SecurityIdentifier("S-1-3-4"), FileSystemRights.ReadPermissions, AccessControlType.Allow)];
    }

    public static void RequireSealed(NativeHostLaunchManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        string expectedRoot = Path.GetDirectoryName(manifest.SessionDirectory) ?? throw Invalid("ROOT_REQUIRED");
        if (Path.GetFileName(expectedRoot) != StageLeaf(manifest.Nonce) ||
            manifest.ManifestPath != Path.Combine(expectedRoot, "session", "launch.json") ||
            manifest.SessionDirectory != Path.Combine(expectedRoot, "session") ||
            manifest.HostDirectory != Path.Combine(expectedRoot, "host") || manifest.RuntimeDirectory != Path.Combine(expectedRoot, "runtime") ||
            manifest.PackageDirectory != Path.Combine(expectedRoot, "package")) throw Invalid("LAYOUT");
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw Invalid("OWNER_REQUIRED");
        var rules = Rules(user, DeriveProfileSid(manifest.Nonce));
        var expected = rules.ToDictionary(rule => ((SecurityIdentifier)rule.IdentityReference).Value, rule => rule.FileSystemRights);
        void Check(string path, bool directory)
        {
            const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Access;
            FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl(sections) : new FileInfo(path).GetAccessControl(sections);
            if (!security.AreAccessRulesProtected || !security.AreAccessRulesCanonical || !user.Equals(security.GetOwner(typeof(SecurityIdentifier))))
                throw Invalid("SEALED_OWNER_OR_PROTECTION");
            var actual = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
            if (actual.Length != expected.Count || actual.Select(rule => rule.IdentityReference.Value).Distinct().Count() != expected.Count ||
                actual.Any(rule => rule.AccessControlType != AccessControlType.Allow || rule.InheritanceFlags != InheritanceFlags.None ||
                    rule.PropagationFlags != PropagationFlags.None || rule.IsInherited ||
                    !expected.TryGetValue(rule.IdentityReference.Value, out var rights) || rights != rule.FileSystemRights))
                throw Invalid("SEALED_ACCESS");
        }
        Check(expectedRoot, true); Check(manifest.SessionDirectory, true); Check(manifest.ManifestPath, false);
        var roleRoots = new HashSet<string>(new[] { manifest.HostDirectory, manifest.RuntimeDirectory, manifest.SessionDirectory, manifest.PackageDirectory },
            StringComparer.OrdinalIgnoreCase);
        if (!roleRoots.SetEquals(Directory.EnumerateFileSystemEntries(expectedRoot)) ||
            !Directory.EnumerateFileSystemEntries(manifest.SessionDirectory).SequenceEqual([manifest.ManifestPath], StringComparer.OrdinalIgnoreCase))
            throw Invalid("SEALED_ROOT_CONTENTS");
        foreach (var (directory, files) in new[] { (manifest.HostDirectory, manifest.HostFiles), (manifest.RuntimeDirectory, manifest.RuntimeFiles),
            (manifest.PackageDirectory, manifest.PackageFiles) })
        {
            Check(directory, true);
            var checkedDirectories = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { directory };
            foreach (var file in files)
            {
                string full = OwnedFiles.Bound(directory, file.Path);
                Check(full, false);
                for (string? parent = Path.GetDirectoryName(full); parent is not null && checkedDirectories.Add(parent); parent = Path.GetDirectoryName(parent))
                {
                    if (!Within(parent, directory)) throw Invalid("SEALED_DIRECTORY_ESCAPE");
                    Check(parent, true);
                }
            }
        }
    }

    public void Dispose()
    {
        lock (created)
        {
            if (disposed) return;
            ReleasePreparationPins();
            if (unboundCreatedObject) throw Invalid("UNBOUND_CREATED_OBJECT_PENDING");
            heldManifest?.Dispose();
            heldManifest = null;
            while (created.Count != 0)
            {
                StageObject last = created[^1];
                if (!Within(last.Path, root) || Path.GetDirectoryName(root) != scratch || Path.GetFileName(root) != StageLeaf(Nonce))
                    throw Invalid("CLEANUP_SCOPE");
                last.DeleteOriginal();
                created.RemoveAt(created.Count - 1);
            }
            disposed = true;
        }
    }

    private void ReleasePreparationPins()
    {
        preparationPins?.Dispose();
        preparationPins = null;
    }

    private sealed class SourcePins : IDisposable
    {
        private readonly List<IDisposable> held = [];
        private NativePinReleaseGroup? release;
        private readonly HashSet<string> directories = new(StringComparer.OrdinalIgnoreCase);
        internal void HoldDirectory(string directory) => NativeHostLaunchManifest.PinAncestors(directory, held, directories);
        internal FileStream Open(string path)
        { HoldDirectory(Path.GetDirectoryName(path)!); return NativeHostLaunchManifest.OpenPinnedFile(path, held); }
        public void Dispose()
        {
            (release ??= new NativePinReleaseGroup(held)).Dispose();
        }
    }

    private static InvalidDataException Invalid(string code) => new("NATIVE_STAGE_" + code);

    private static SecurityIdentifier DeriveProfileSid(string nonce)
    {
        if (!NativeHostBinding.IsLowerSha256(nonce)) throw Invalid("NONCE");
        int hr = DeriveAppContainerSidFromAppContainerName("AutumnOS.Paimon.NativeHost." + nonce[..32], out IntPtr sid);
        try
        {
            if (hr != 0 || sid == IntPtr.Zero) throw new COMException("NATIVE_STAGE_DERIVE_PACKAGE_SID", hr);
            return new SecurityIdentifier(sid);
        }
        finally { if (sid != IntPtr.Zero) Native.FreeSid(sid); }
    }

    [DllImport("userenv.dll", CharSet = CharSet.Unicode)]
    private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);

    private sealed record StageObject(string Path, bool Directory, uint Volume, ulong FileId, SafeFileHandle SetupHandle)
    {
        internal static StageObject Capture(string path, bool directory, SafeFileHandle? originalHandle = null)
        {
            // READ_CONTROL / WRITE_DAC / WRITE_OWNER / READ_ATTRIBUTES. Open with
            // no delete share while the original creation handle still exists.
            SafeFileHandle handle = Open(path, directory, 0xE0080, 3);
            try
            {
                NativeHostLaunchManifest.VerifyHandle(handle, path, directory);
                var facts = FileIdentity(handle);
                if (originalHandle is not null)
                {
                    var original = FileIdentity(originalHandle);
                    if (facts.VolumeSerial != original.VolumeSerial || facts.IdHigh != original.IdHigh || facts.IdLow != original.IdLow)
                        throw Invalid("FRESH_FILE_REPLACED");
                }
                return new(path, directory, facts.VolumeSerial, ((ulong)facts.IdHigh << 32) | facts.IdLow, handle);
            }
            catch { handle.Dispose(); throw; }
        }
        private SafeFileHandle? readDirectory;
        internal void BlockDirectoryWriters()
        {
            // Refuse an already-open FILE_ADD_FILE/ADD_SUBDIRECTORY handle, and
            // keep this original directory without share-write or share-delete.
            readDirectory = Open(Path, true, 0x81, 1);
            NativeHostLaunchManifest.VerifyHandle(readDirectory, Path, true);
        }
        internal void SetSecurityOnOriginal(FileSystemSecurity security)
        {
            var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
            var owner = descriptor.Owner ?? throw Invalid("SEAL_OWNER");
            var acl = descriptor.DiscretionaryAcl ?? throw Invalid("SEAL_DACL");
            byte[] ownerBytes = new byte[owner.BinaryLength], aclBytes = new byte[acl.BinaryLength];
            owner.GetBinaryForm(ownerBytes, 0); acl.GetBinaryForm(aclBytes, 0);
            uint result = SetSecurityInfo(SetupHandle, 1 /* SE_FILE_OBJECT */, 0x80000005 /* protected DACL and owner */,
                ownerBytes, IntPtr.Zero, aclBytes, IntPtr.Zero);
            if (result != 0) throw new Win32Exception(unchecked((int)result), "NATIVE_STAGE_SEAL_ORIGINAL");
        }
        internal void DeleteOriginal()
        {
            readDirectory?.Dispose();
            SetupHandle.Dispose();
            using SafeFileHandle handle = Open(Path, Directory, 0x10080, 7);
            NativeHostLaunchManifest.VerifyHandle(handle, Path, Directory);
            var identity = FileIdentity(handle);
            if (identity.VolumeSerial != Volume || ((ulong)identity.IdHigh << 32 | identity.IdLow) != FileId)
                throw Invalid("CLEANUP_ORIGINAL_OBJECT");
            int disposition = 1;
            if (!SetFileInformationByHandle(handle, 4, ref disposition, sizeof(int)))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "NATIVE_STAGE_DELETE_ORIGINAL");
        }
        private static SafeFileHandle Open(string path, bool directory, uint access, uint share)
        {
            SafeFileHandle handle = CreateFileW(@"\\?\" + path, access, share, IntPtr.Zero, 3, 0x00200000u | (directory ? 0x02000000u : 0u), IntPtr.Zero);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new Win32Exception(error, "NATIVE_STAGE_OPEN_ORIGINAL"); }
            return handle;
        }
        private static FileInfoNative FileIdentity(SafeFileHandle handle)
        {
            if (!GetFileInformationByHandle(handle, out var identity))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "NATIVE_STAGE_FILE_ID");
            return identity;
        }
        [StructLayout(LayoutKind.Sequential)] private struct FileInfoNative
        {
            internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, VolumeSerial, SizeHigh, SizeLow, Links, IdHigh, IdLow;
        }
        [DllImport("advapi32.dll", ExactSpelling = true)]
        private static extern uint SetSecurityInfo(SafeFileHandle handle, int type, uint flags, byte[] owner, IntPtr group, byte[] dacl, IntPtr sacl);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
        private static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoNative info);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle handle, int informationClass, ref int data, int size);
    }
}
