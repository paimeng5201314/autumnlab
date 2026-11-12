using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutumnOS.NativeHost.Windows;

// Sent through the inherited parent-to-child pipe after the parent has
// admitted this suspended process to its original Job. The browser never
// needs access to the parent image, launcher manifest or private session files.
internal sealed record NativeBrowserStartup(string Nonce, NativeHostBinding Binding, string RuntimeDirectory,
    string PackageDirectory, string PackageEntry, NativeBrowserFileBinding[] PackageFiles)
{
    private const int MaximumBytes = 128 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        AllowDuplicateProperties = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, MaxDepth = 5
    };

    internal static async Task WriteAsync(Stream output, NativeHostLaunchManifest manifest)
    {
        var descriptor = new NativeBrowserStartup(manifest.Nonce, manifest.Binding, manifest.RuntimeDirectory,
            manifest.PackageDirectory, manifest.PackageEntry, manifest.PackageFiles.Select(file => CaptureFile(manifest, file)).ToArray());
        descriptor.Validate(manifest.Nonce);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(descriptor, Json);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("BROWSER_DESCRIPTOR_LIMIT");
        byte[] header = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(header, (uint)bytes.Length);
        await output.WriteAsync(header).ConfigureAwait(false);
        await output.WriteAsync(bytes).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }

    private static NativeBrowserFileBinding CaptureFile(NativeHostLaunchManifest manifest, NativeHostFileBinding file)
    {
        // Manifest and ancestor pins remain owned for the whole native lifetime,
        // excluding replacement/rename/write while this path is opened again.
        string path = Path.Combine(manifest.PackageDirectory, file.Path.Replace('/', '\\'));
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        NativeHostLaunchManifest.VerifyHandle(stream.SafeFileHandle, path, directory: false);
        if (stream.Length != file.Bytes) throw new InvalidDataException("BROWSER_STAGED_RESOURCE_LENGTH");
        return new(file.Path, file.Bytes, file.Sha256, NativeHostLaunchManifest.ReadPlainFileIdentity(stream.SafeFileHandle));
    }

    internal static async Task<NativeBrowserStartup> ReadAsync(Stream input, string expectedNonce)
    {
        byte[] header = new byte[4];
        await input.ReadExactlyAsync(header).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (length is 0 or > MaximumBytes) throw new InvalidDataException("BROWSER_DESCRIPTOR_LIMIT");
        byte[] bytes = new byte[length];
        await input.ReadExactlyAsync(bytes).ConfigureAwait(false);
        var descriptor = JsonSerializer.Deserialize<NativeBrowserStartup>(bytes, Json)
            ?? throw new InvalidDataException("BROWSER_DESCRIPTOR_REQUIRED");
        descriptor.Validate(expectedNonce);
        return descriptor;
    }

    private void Validate(string expectedNonce)
    {
        Binding.Validate();
        if (!NativeHostBinding.IsLowerSha256(Nonce) || Nonce != expectedNonce ||
            !CanonicalRoot(RuntimeDirectory) || !CanonicalRoot(PackageDirectory) ||
            PackageFiles.Length is < 1 or > 128)
            throw new InvalidDataException("BROWSER_DESCRIPTOR_INVALID");
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var file in PackageFiles)
        {
            if (file is null || !SafeName(file.Path) || NativeHostLaunchManifest.Relative(file.Path) != file.Path || !seen.Add(file.Path) ||
                !NativeHostBinding.IsLowerSha256(file.Sha256) || !file.Identity.IsValid || file.Bytes is < 0 or > 8 * 1024 * 1024 ||
                (total += file.Bytes) > 16 * 1024 * 1024)
                throw new InvalidDataException("BROWSER_RESOURCE_BINDING_INVALID");
        }
        if (!PackageFiles.Any(file => file.Path == PackageEntry && file.Path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("BROWSER_ENTRY_REQUIRED");
    }

    private static bool CanonicalRoot(string path) => path.Length >= 4 && char.IsAsciiLetter(path[0]) &&
        path[1] == ':' && path[2] == '\\' && !path.Contains('/') && !path.EndsWith('\\') &&
        Path.GetFullPath(path).Equals(path, StringComparison.OrdinalIgnoreCase);

    private static bool SafeName(string path) => path.Length is > 0 and <= 256 &&
        path.Split('/').All(segment => segment.Length != 0 && !segment.StartsWith('.') && !segment.EndsWith('.') &&
            segment.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'));

    internal Dictionary<string, byte[]> ReadPackage()
    {
        Dictionary<string, byte[]> result = new(StringComparer.Ordinal);
        foreach (var file in PackageFiles)
        {
            string path = Path.Combine(PackageDirectory, file.Path.Replace('/', '\\'));
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            // Compare the actual opened object with the station's path-pinned
            // identity, without asking the restricted token to map a DOS drive.
            if (NativeHostLaunchManifest.ReadPlainFileIdentity(stream.SafeFileHandle) != file.Identity)
                throw new InvalidDataException("BROWSER_STAGED_RESOURCE_IDENTITY");
            if (stream.Length != file.Bytes) throw new InvalidDataException("BROWSER_STAGED_RESOURCE_LENGTH");
            byte[] bytes = new byte[checked((int)file.Bytes)];
            stream.ReadExactly(bytes);
            if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != file.Sha256)
                throw new InvalidDataException("BROWSER_STAGED_RESOURCE_HASH");
            result.Add(file.Path, bytes);
        }
        return result;
    }
}

internal sealed record NativeBrowserFileBinding(string Path, long Bytes, string Sha256, NativeHostFileIdentity Identity);
