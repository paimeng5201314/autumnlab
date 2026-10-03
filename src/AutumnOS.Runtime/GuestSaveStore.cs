using System.Text.Json;

namespace AutumnOS.Runtime;

internal sealed class RuntimeStorageException(string code) : IOException
{
    internal string Code { get; } = code;
}

internal sealed class GuestSaveStore
{
    internal const int MaximumSaveBytes = 128 * 1024;
    private readonly string appId;
    private readonly string directory;
    private string FilePath => Path.Combine(directory, "game.json");

    internal GuestSaveStore(string savesRoot, string appId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(savesRoot);
        if (!Path.IsPathFullyQualified(savesRoot)) throw new ArgumentException("An absolute saves root is required.", nameof(savesRoot));
        this.appId = appId;
        directory = Path.Combine(Path.GetFullPath(savesRoot), appId, "guest");
    }

    internal JsonElement? Read()
    {
        EnsureSafeDirectory();
        CheckFile(FilePath);
        if (!File.Exists(FilePath)) return null;
        using var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is <= 0 or > MaximumSaveBytes) throw new RuntimeStorageException("SAVE_CORRUPT");
        try
        {
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 40 });
            JsonElement record = document.RootElement;
            if (!RuntimeSession.ExactProperties(record, "schemaVersion", "appId", "accountMode", "slot", "value")
                || RuntimeSession.HasDuplicateProperties(record)
                || !record.GetProperty("schemaVersion").TryGetInt32(out int version) || version != 1
                || record.GetProperty("appId").GetString() != appId
                || record.GetProperty("accountMode").GetString() != "guest"
                || record.GetProperty("slot").GetString() != "game") throw new RuntimeStorageException("SAVE_CORRUPT");
            return record.GetProperty("value").Clone();
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        { throw new RuntimeStorageException("SAVE_CORRUPT"); }
    }

    internal void Write(JsonElement value, CancellationToken cancellationToken)
    {
        byte[] content = JsonSerializer.SerializeToUtf8Bytes(new { schemaVersion = 1, appId, accountMode = "guest", slot = "game", value });
        if (content.Length > MaximumSaveBytes) throw new RuntimeStorageException("SAVE_TOO_LARGE");
        EnsureSafeDirectory();
        string lockPath = Path.Combine(directory, ".game.lock");
        CheckFile(lockPath);
        FileStream exclusiveLock;
        try { exclusiveLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
        { throw new RuntimeStorageException("SAVE_BUSY"); }
        using (exclusiveLock)
        {
            _ = Read(); // Never overwrite an unsupported or corrupt save under the guise of recovery.
            string temporary = Path.Combine(directory, $".game-{Guid.NewGuid():N}.tmp");
            try
            {
                CheckFile(temporary);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(content);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                CheckFile(FilePath);
                CheckFile(temporary);
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null);
                else File.Move(temporary, FilePath, overwrite: false);
            }
            finally
            {
                try { CheckFile(temporary); File.Delete(temporary); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
            }
        }
    }

    private void EnsureSafeDirectory()
    {
        var ancestors = new Stack<string>();
        for (string? current = directory; current is not null; current = Path.GetDirectoryName(current)) ancestors.Push(current);
        while (ancestors.TryPop(out string? path))
        {
            CheckDirectory(path);
            Directory.CreateDirectory(path);
            CheckDirectory(path);
        }
    }

    private void CheckFile(string path)
    {
        for (string? current = directory; current is not null; current = Path.GetDirectoryName(current)) CheckDirectory(current);
        FileAttributes? attributes = Attributes(path);
        if (attributes is null) return;
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new RuntimeStorageException("STORAGE_UNSAFE_PATH");
        if ((attributes & FileAttributes.Directory) != 0) throw new RuntimeStorageException("STORAGE_IO_ERROR");
    }

    private static void CheckDirectory(string path)
    {
        FileAttributes? attributes = Attributes(path);
        if (attributes is null) return;
        if ((attributes & FileAttributes.ReparsePoint) != 0) throw new RuntimeStorageException("STORAGE_UNSAFE_PATH");
        if ((attributes & FileAttributes.Directory) == 0) throw new RuntimeStorageException("STORAGE_IO_ERROR");
    }

    private static FileAttributes? Attributes(string path)
    {
        try { return File.GetAttributes(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
