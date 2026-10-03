using System.Text.Json;

namespace AutumnOS.Storage;

/// <summary>Host-owned desktop appearance. These values never grant capabilities to an application.</summary>
public sealed record DesktopPreferences(int SchemaVersion, string Theme, string Wallpaper, long Revision, DateTimeOffset UpdatedUtc)
{
    public const int CurrentSchemaVersion = 1;
    public static DesktopPreferences Default => new(CurrentSchemaVersion, "light", "warm", 0, DateTimeOffset.UnixEpoch);
}

public sealed record DesktopPreferencesResult(bool Success, DesktopPreferences? State, string ErrorCode, string RecoveryMessage)
{
    internal static DesktopPreferencesResult Ready(DesktopPreferences state) => new(true, state, StorageErrors.None, "桌面外观已就绪。");
    internal static DesktopPreferencesResult Failed(string code) => new(false, null, code, code switch
    {
        StorageErrors.CorruptState => "桌面外观配置无法读取。原文件已保留；请先备份并检查配置，再重试。",
        StorageErrors.FutureSchema => "桌面外观配置来自较新版本。原文件已保留，请使用兼容的 AutumnOS 版本。",
        StorageErrors.Busy => "另一个进程正在保存桌面外观。请稍后重试。",
        DesktopPreferencesStore.InvalidValue => "主题或壁纸选项无效。请选择当前支持的内置选项。",
        StorageErrors.Cancelled => "外观保存已取消；原有配置保留。",
        _ => StorageErrors.Recovery(code)
    });
}

/// <summary>
/// Versioned, bounded appearance storage beside the actual entry point. This service accepts only
/// built-in identifiers, never file paths or URLs, and is available to trusted host UI only.
/// </summary>
public sealed class DesktopPreferencesStore(InstallationRoot root, CriticalOperationCoordinator? coordinator = null)
{
    private readonly CriticalOperationCoordinator operations = coordinator ?? new();
    public const string InvalidValue = "CONFIG_INVALID_VALUE";
    private const int MaximumStateBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public string StateFilePath => Path.Combine(root.Directories["Config"], "desktop-preferences.json");
    public string LockFilePath => Path.Combine(root.Directories["Config"], ".desktop-preferences.lock");

    /// <summary>A missing file returns light/warm defaults without persisting them; invalid files are never reset.</summary>
    public DesktopPreferencesResult Load(CancellationToken cancellationToken = default)
    {
        DataRootResult prepared = root.EnsureCreated(cancellationToken);
        if (!prepared.Success) return DesktopPreferencesResult.Failed(prepared.ErrorCode);
        try { return ReadCurrent(cancellationToken); }
        catch (Exception exception) when (StorageExceptionMapper.CanHandle(exception))
        { return DesktopPreferencesResult.Failed(StorageExceptionMapper.Code(exception)); }
    }

    /// <summary>
    /// Atomically persists one complete choice. Concurrent processes serialize commits; the most
    /// recent successful commit wins. Repeating an already-persisted choice leaves its bytes unchanged.
    /// </summary>
    public DesktopPreferencesResult Save(string theme, string wallpaper, CancellationToken cancellationToken = default)
    {
        if (!IsTheme(theme) || !IsWallpaper(wallpaper)) return DesktopPreferencesResult.Failed(InvalidValue);
        string? temporaryPath = null;
        try
        {
            using IDisposable critical = operations.EnterWrite("桌面外观配置写入");
            DataRootResult prepared = root.EnsureCreated(cancellationToken);
            if (!prepared.Success) return DesktopPreferencesResult.Failed(prepared.ErrorCode);
            root.ValidateManagedFile(LockFilePath);
            FileStream exclusiveLock;
            try { exclusiveLock = new FileStream(LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
            { return DesktopPreferencesResult.Failed(StorageErrors.Busy); }

            using (exclusiveLock)
            {
                DesktopPreferencesResult currentResult = ReadCurrent(cancellationToken);
                if (!currentResult.Success) return currentResult;
                DesktopPreferences current = currentResult.State!;
                bool alreadyExists = File.Exists(StateFilePath);
                if (alreadyExists && current.Theme == theme && current.Wallpaper == wallpaper) return currentResult;
                if (current.Revision == long.MaxValue) return DesktopPreferencesResult.Failed(StorageErrors.CorruptState);

                var next = new DesktopPreferences(DesktopPreferences.CurrentSchemaVersion, theme, wallpaper,
                    current.Revision + 1, DateTimeOffset.UtcNow);
                temporaryPath = Path.Combine(root.Directories["Config"], $".desktop-preferences-{Guid.NewGuid():N}.tmp");
                root.ValidateManagedFile(temporaryPath);
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write,
                           FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    JsonSerializer.Serialize(stream, next, JsonOptions);
                    stream.Flush(flushToDisk: true);
                }
                cancellationToken.ThrowIfCancellationRequested();
                root.ValidateManagedFile(StateFilePath);
                root.ValidateManagedFile(temporaryPath);
                if (alreadyExists) File.Replace(temporaryPath, StateFilePath, destinationBackupFileName: null);
                else File.Move(temporaryPath, StateFilePath, overwrite: false);
                temporaryPath = null;
                return DesktopPreferencesResult.Ready(next);
            }
        }
        catch (Exception exception) when (StorageExceptionMapper.CanHandle(exception))
        { return DesktopPreferencesResult.Failed(StorageExceptionMapper.Code(exception)); }
        finally
        {
            if (temporaryPath is not null)
            {
                try { root.ValidateManagedFile(temporaryPath); File.Delete(temporaryPath); }
                catch (Exception exception) when (StorageExceptionMapper.CanHandle(exception))
                { /* Only this call's uncommitted staging file can be cleaned up; durable state is preserved. */ }
            }
        }
    }

    private DesktopPreferencesResult ReadCurrent(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        root.ValidateManagedFile(StateFilePath);
        if (!File.Exists(StateFilePath)) return DesktopPreferencesResult.Ready(DesktopPreferences.Default);

        using var stream = new FileStream(StateFilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is <= 0 or > MaximumStateBytes) return DesktopPreferencesResult.Failed(StorageErrors.CorruptState);
        try
        {
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement record = document.RootElement;
            if (record.ValueKind != JsonValueKind.Object) return DesktopPreferencesResult.Failed(StorageErrors.CorruptState);
            string[] names = record.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || !record.TryGetProperty("schemaVersion", out JsonElement schema)
                || !schema.TryGetInt32(out int version)) return DesktopPreferencesResult.Failed(StorageErrors.CorruptState);
            if (version > DesktopPreferences.CurrentSchemaVersion) return DesktopPreferencesResult.Failed(StorageErrors.FutureSchema);
            if (version != DesktopPreferences.CurrentSchemaVersion || names.Length != 5
                || names.Any(name => name is not ("schemaVersion" or "theme" or "wallpaper" or "revision" or "updatedUtc")))
                return DesktopPreferencesResult.Failed(StorageErrors.CorruptState);

            if (!record.TryGetProperty("theme", out JsonElement theme) || theme.ValueKind != JsonValueKind.String || !IsTheme(theme.GetString())
                || !record.TryGetProperty("wallpaper", out JsonElement wallpaper) || wallpaper.ValueKind != JsonValueKind.String || !IsWallpaper(wallpaper.GetString())
                || !record.TryGetProperty("revision", out JsonElement revision) || !revision.TryGetInt64(out long parsedRevision) || parsedRevision < 1
                || !record.TryGetProperty("updatedUtc", out JsonElement updated) || updated.ValueKind != JsonValueKind.String
                || !updated.TryGetDateTimeOffset(out DateTimeOffset parsedUpdated) || parsedUpdated.Offset != TimeSpan.Zero)
                return DesktopPreferencesResult.Failed(StorageErrors.CorruptState);

            return DesktopPreferencesResult.Ready(new(version, theme.GetString()!, wallpaper.GetString()!, parsedRevision, parsedUpdated));
        }
        catch (JsonException) { return DesktopPreferencesResult.Failed(StorageErrors.CorruptState); }
        catch (InvalidOperationException) { return DesktopPreferencesResult.Failed(StorageErrors.CorruptState); }
        catch (FormatException) { return DesktopPreferencesResult.Failed(StorageErrors.CorruptState); }
    }

    private static bool IsTheme(string? value) => value is "light" or "dark" or "system";
    private static bool IsWallpaper(string? value) => value is "warm" or "mist" or "night";
}
