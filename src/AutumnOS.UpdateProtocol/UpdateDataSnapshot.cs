using System.Text.Json;
using AutumnOS.Update;

namespace AutumnOS.UpdateProtocol;

public sealed record UpdateDataSnapshotRecord(int SchemaVersion, string SnapshotId, string BuildId, string Version,
    int DataSchemaVersion, DateTimeOffset CreatedUtc, InstalledFile[] Files, string[] AbsentFiles);

/// <summary>Only host desktop settings belong to this transaction. Credentials, onboarding,
/// channel policy, installed applications and game saves are deliberately outside this scope.</summary>
public static class UpdateDataSnapshot
{
    private static readonly string[] Names = ["desktop-preferences.json", "desktop-layout.json"];
    private const int MaximumBytes = 128 * 1024;

    public static void CheckCompatibility(string root, UpdateManifest target)
    {
        var current = UpdateFiles.ReadInstalled(root);
        if (current.DataSchemaVersion != 1 || target.Data.SchemaVersion != 1 || target.Data.MinimumReadableVersion != 1 || !target.Data.RollbackCompatible)
            throw new IOException("UPDATE_DATA_MIGRATION_UNSUPPORTED");
        foreach (string name in Names)
        {
            string source = ResolveData(root, name);
            if (File.Exists(source)) ValidateBytes(name, ReadBounded(source));
        }
    }

    public static UpdateDataSnapshotRecord Capture(string root, string transactionId, InstalledBuild installed)
    {
        if (installed.DataSchemaVersion != 1) throw new IOException("UPDATE_DATA_MIGRATION_UNSUPPORTED");
        string directory = Path.Combine(UpdateFiles.Transaction(root, transactionId), "data-snapshot");
        UpdatePaths.EnsureNoReparsePoints(directory);
        Directory.CreateDirectory(directory);
        var files = new List<InstalledFile>(); var absent = new List<string>();
        foreach (string name in Names)
        {
            string source = ResolveData(root, name);
            if (!File.Exists(source)) { absent.Add(name); continue; }
            byte[] bytes = ReadBounded(source); ValidateBytes(name, bytes);
            string target = Path.Combine(directory, name);
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(bytes); output.Flush(true); }
            files.Add(new(name, bytes.Length, UpdateFiles.Hash(target)));
        }
        var snapshot = new UpdateDataSnapshotRecord(1, transactionId, installed.BuildId, installed.Version,
            installed.DataSchemaVersion, DateTimeOffset.UtcNow, files.ToArray(), absent.ToArray());
        UpdateFiles.Write(Path.Combine(directory, "snapshot.json"), snapshot);
        Validate(root, transactionId, installed);
        return snapshot;
    }

    public static UpdateDataSnapshotRecord Validate(string root, string id, InstalledBuild installed)
    {
        string directory = Path.Combine(UpdateFiles.Transaction(root, id), "data-snapshot");
        var record = UpdateFiles.Read<UpdateDataSnapshotRecord>(Path.Combine(directory, "snapshot.json"), 16 * 1024);
        if (record.SchemaVersion != 1 || record.SnapshotId != id || record.BuildId != installed.BuildId || record.Version != installed.Version ||
            record.DataSchemaVersion != installed.DataSchemaVersion || record.Files.Length + record.AbsentFiles.Length != Names.Length ||
            !record.Files.Select(f => f.Path).Concat(record.AbsentFiles).Order().SequenceEqual(Names.Order()))
            throw new IOException("UPDATE_DATA_SNAPSHOT_INVALID");
        foreach (var file in record.Files)
        {
            string path = Path.Combine(directory, file.Path); UpdatePaths.EnsureNoReparsePoints(path);
            if (!UpdateFiles.Matches(path, file)) throw new IOException("UPDATE_DATA_SNAPSHOT_INVALID");
            ValidateBytes(file.Path, ReadBounded(path));
        }
        return record;
    }

    // Automatic transaction recovery only. A manual same-schema rollback keeps current settings.
    public static void Restore(string root, string id, InstalledBuild installed)
    {
        var record = Validate(root, id, installed);
        string directory = Path.Combine(UpdateFiles.Transaction(root, id), "data-snapshot");
        foreach (var file in record.Files)
        {
            string target = ResolveData(root, file.Path);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string temporary = target + ".update-" + id + ".tmp";
            UpdatePaths.EnsureNoReparsePoints(temporary);
            using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(ReadBounded(Path.Combine(directory, file.Path))); output.Flush(true); }
            File.Move(temporary, target, true);
        }
        foreach (string absent in record.AbsentFiles)
        {
            string target = ResolveData(root, absent);
            if (File.Exists(target)) { ValidateBytes(absent, ReadBounded(target)); File.Delete(target); }
        }
    }

    private static string ResolveData(string root, string name)
    {
        if (!Names.Contains(name, StringComparer.Ordinal)) throw new IOException("UPDATE_DATA_SCOPE_REJECTED");
        string path = Path.Combine(UpdateFiles.DataRoot(root), "Config", name);
        UpdatePaths.EnsureNoReparsePoints(path); return path;
    }
    private static byte[] ReadBounded(string path)
    {
        UpdatePaths.EnsureNoReparsePoints(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumBytes) throw new IOException("UPDATE_DATA_SNAPSHOT_INVALID");
        using var output = new MemoryStream(); stream.CopyTo(output); return output.ToArray();
    }
    private static void ValidateBytes(string name, byte[] bytes)
    {
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var value = doc.RootElement;
            string[] allowed = name == "desktop-preferences.json" ? ["schemaVersion", "theme", "wallpaper", "revision", "updatedUtc"] : ["schemaVersion", "orderedIds", "revision", "updatedUtc"];
            if (value.ValueKind != JsonValueKind.Object || !value.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(allowed.Order()) ||
                value.GetProperty("schemaVersion").GetInt32() != 1 || value.GetProperty("revision").GetInt64() < 1 ||
                value.GetProperty("updatedUtc").GetDateTimeOffset().Offset != TimeSpan.Zero)
                throw new IOException("UPDATE_DATA_SNAPSHOT_INVALID");
            if (name == "desktop-preferences.json")
            {
                if (value.GetProperty("theme").GetString() is not ("light" or "dark" or "system") ||
                    value.GetProperty("wallpaper").GetString() is not ("warm" or "mist" or "night")) throw new IOException("UPDATE_DATA_SNAPSHOT_INVALID");
            }
            else
            {
                var ids = value.GetProperty("orderedIds");
                if (ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > 512 || ids.EnumerateArray().Any(x =>
                    x.ValueKind != JsonValueKind.String || x.GetString() is not { Length: > 0 and <= 160 } text || text.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or '_'))))
                    throw new IOException("UPDATE_DATA_SNAPSHOT_INVALID");
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        { throw new IOException("UPDATE_DATA_SNAPSHOT_INVALID", e); }
    }
}
