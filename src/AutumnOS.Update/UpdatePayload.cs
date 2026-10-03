using System.IO.Compression;
using System.Security.Cryptography;

namespace AutumnOS.Update;

public static class UpdatePayload
{
    public static IReadOnlyList<UpdateFile> CreateFileList(string root)
    {
        root = Path.GetFullPath(root); UpdatePaths.EnsureNoReparsePoints(root);
        List<UpdateFile> files = [];
        void Visit(string directory)
        {
            foreach (string item in Directory.EnumerateFileSystemEntries(directory))
            {
                string relative = Path.GetRelativePath(root, item).Replace('\\', '/');
                if (UpdatePaths.IsStableOrPrivate(relative)) continue;
                UpdatePaths.EnsureNoReparsePoints(item);
                if (Directory.Exists(item)) { Visit(item); continue; }
                UpdatePaths.ValidateManagedRelativePath(relative);
                using var input = new FileStream(item, FileMode.Open, FileAccess.Read, FileShare.Read);
                files.Add(new(relative, input.Length, Convert.ToHexStringLower(SHA256.HashData(input))));
                if (files.Count > UpdateManifestCodec.MaximumFiles) throw new UpdateException("UPDATE_FILE_LIMIT");
            }
        }
        Visit(root); return files.OrderBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }

    public static Task VerifyAsync(string payloadPath, UpdateManifest manifest, CancellationToken cancellationToken = default)
        => ReadPayloadAsync(payloadPath, null, manifest, cancellationToken);
    public static Task ExtractVerifiedAsync(string payloadPath, string destination, UpdateManifest manifest, CancellationToken cancellationToken = default)
        => ReadPayloadAsync(payloadPath, destination, manifest, cancellationToken);

    private static async Task ReadPayloadAsync(string payloadPath, string? destination, UpdateManifest manifest, CancellationToken token)
    {
        UpdateManifestCodec.Validate(manifest); UpdatePaths.EnsureNoReparsePoints(payloadPath);
        await using FileStream payload = new(payloadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 131072, FileOptions.Asynchronous);
        if (payload.Length != manifest.Payload.Bytes) throw new UpdateException("UPDATE_PAYLOAD_SIZE_MISMATCH");
        if (!CryptographicOperations.FixedTimeEquals(await SHA256.HashDataAsync(payload, token).ConfigureAwait(false), Convert.FromHexString(manifest.Payload.Sha256)))
            throw new UpdateException("UPDATE_PAYLOAD_HASH_MISMATCH");
        payload.Position = 0;
        if (destination is not null)
        {
            destination = Path.GetFullPath(destination); UpdatePaths.EnsureNoReparsePoints(destination);
            if (Directory.Exists(destination) && Directory.EnumerateFileSystemEntries(destination).Any()) throw new UpdateException("UPDATE_EXTRACTION_NOT_EMPTY");
            Directory.CreateDirectory(destination); UpdatePaths.EnsureNoReparsePoints(destination);
        }
        try
        {
            using ZipArchive zip = new(payload, ZipArchiveMode.Read, leaveOpen: true);
            if (zip.Entries.Count != manifest.Files.Count) throw new UpdateException("UPDATE_PAYLOAD_FILE_TABLE_MISMATCH");
            var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested(); UpdatePaths.ValidateManagedRelativePath(entry.FullName);
                int unixType = (entry.ExternalAttributes >> 16) & 0xf000;
                if (unixType is not (0 or 0x8000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                    !seen.Add(entry.FullName) || !expected.TryGetValue(entry.FullName, out var file) || file.Path != entry.FullName || entry.Length != file.Bytes ||
                    entry.Length > Math.Max(1, entry.CompressedLength) * 1000L) throw new UpdateException("UPDATE_PAYLOAD_ENTRY_INVALID");
                await using Stream source = entry.Open();
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                FileStream? output = null;
                try
                {
                    if (destination is not null)
                    {
                        string target = UpdatePaths.ResolveManagedPath(destination, file.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!); UpdatePaths.EnsureNoReparsePoints(target);
                        output = new(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 131072, FileOptions.Asynchronous);
                    }
                    byte[] buffer = new byte[131072]; long received = 0; int read;
                    while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
                    {
                        received += read; if (received > file.Bytes) throw new UpdateException("UPDATE_PAYLOAD_ENTRY_SIZE_MISMATCH");
                        hash.AppendData(buffer, 0, read);
                        if (output is not null) await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                    }
                    if (received != file.Bytes || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(file.Sha256)))
                        throw new UpdateException("UPDATE_PAYLOAD_ENTRY_HASH_MISMATCH");
                    if (output is not null) { await output.FlushAsync(token).ConfigureAwait(false); output.Flush(true); }
                }
                finally { if (output is not null) await output.DisposeAsync().ConfigureAwait(false); }
            }
        }
        catch (InvalidDataException e) { throw new UpdateException("UPDATE_ARCHIVE_INVALID", e); }
    }
}
