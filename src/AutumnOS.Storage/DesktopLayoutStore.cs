using System.Text.Json;

namespace AutumnOS.Storage;

/// <summary>Host-owned desktop order, independent of appearance, package registration, and application data.</summary>
public sealed record DesktopLayout(int SchemaVersion, IReadOnlyList<string> OrderedIds, long Revision, DateTimeOffset UpdatedUtc)
{
    public const int CurrentSchemaVersion = 1;
    public static DesktopLayout Default => new(CurrentSchemaVersion, Array.Empty<string>(), 0, DateTimeOffset.UnixEpoch);
}

public sealed record DesktopLayoutResult(bool Success, DesktopLayout? State, string ErrorCode, string RecoveryMessage)
{
    internal static DesktopLayoutResult Ready(DesktopLayout state) => new(true, state, StorageErrors.None, "桌面排列已就绪。");
    internal static DesktopLayoutResult Failed(string code) => new(false, null, code, code switch
    {
        StorageErrors.CorruptState => "桌面排列无法读取。原文件已保留；请先备份并检查配置，再重试。",
        StorageErrors.FutureSchema => "桌面排列来自较新版本。原文件已保留，请使用兼容的 AutumnOS 版本。",
        StorageErrors.Busy => "另一个进程正在保存桌面排列。请稍后重试。",
        DesktopLayoutStore.InvalidValue => "桌面排列含无效、重复或过多的应用标识。原排列保留。",
        StorageErrors.Cancelled => "桌面排列保存已取消；原有配置保留。",
        _ => StorageErrors.Recovery(code)
    });
}

/// <summary>
/// Bounded, atomic ordering for trusted host UI. IDs are labels, never paths or launch instructions.
/// The order retains temporarily unavailable IDs so hiding a developer app does not discard its position.
/// </summary>
public sealed class DesktopLayoutStore(InstallationRoot root, CriticalOperationCoordinator? coordinator = null)
{
    private readonly CriticalOperationCoordinator operations = coordinator ?? new();
    public const string InvalidValue = "DESKTOP_LAYOUT_INVALID";
    public const int MaximumIds = 512;
    public const int MaximumIdLength = 160;
    private const int MaximumStateBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public string StateFilePath => Path.Combine(root.Directories["Config"], "desktop-layout.json");
    public string LockFilePath => Path.Combine(root.Directories["Config"], ".desktop-layout.lock");

    /// <summary>Missing state returns an empty order without writing defaults. Invalid state is never reset.</summary>
    public DesktopLayoutResult Load(CancellationToken cancellationToken = default)
    {
        DataRootResult prepared = root.EnsureCreated(cancellationToken);
        if (!prepared.Success) return DesktopLayoutResult.Failed(prepared.ErrorCode);
        try { return ReadCurrent(cancellationToken); }
        catch (Exception exception) when (StorageExceptionMapper.CanHandle(exception))
        { return DesktopLayoutResult.Failed(StorageExceptionMapper.Code(exception)); }
    }

    /// <summary>
    /// Saves a complete order under an exclusive commit lock. Identical saves keep the same bytes and revision.
    /// Callers preserve hidden IDs in this list; ResolveOrder helps append newly registered applications.
    /// </summary>
    public DesktopLayoutResult Save(IReadOnlyList<string> orderedIds, CancellationToken cancellationToken = default)
    {
        if (orderedIds is null || orderedIds.Count > MaximumIds) return DesktopLayoutResult.Failed(InvalidValue);
        string[] snapshot = orderedIds.ToArray();
        if (!AreValidIds(snapshot)) return DesktopLayoutResult.Failed(InvalidValue);
        string? temporaryPath = null;
        try
        {
            using IDisposable critical = operations.EnterWrite("桌面排列配置写入");
            DataRootResult prepared = root.EnsureCreated(cancellationToken);
            if (!prepared.Success) return DesktopLayoutResult.Failed(prepared.ErrorCode);
            root.ValidateManagedFile(LockFilePath);
            FileStream exclusiveLock;
            try { exclusiveLock = new FileStream(LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
            { return DesktopLayoutResult.Failed(StorageErrors.Busy); }

            using (exclusiveLock)
            {
                DesktopLayoutResult currentResult = ReadCurrent(cancellationToken);
                if (!currentResult.Success) return currentResult;
                DesktopLayout current = currentResult.State!;
                bool alreadyExists = File.Exists(StateFilePath);
                if (alreadyExists && current.OrderedIds.SequenceEqual(snapshot, StringComparer.Ordinal)) return currentResult;
                if (current.Revision == long.MaxValue) return DesktopLayoutResult.Failed(StorageErrors.CorruptState);
                var next = new DesktopLayout(DesktopLayout.CurrentSchemaVersion, Array.AsReadOnly(snapshot),
                    current.Revision + 1, DateTimeOffset.UtcNow);
                temporaryPath = Path.Combine(root.Directories["Config"], $".desktop-layout-{Guid.NewGuid():N}.tmp");
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
                return DesktopLayoutResult.Ready(next);
            }
        }
        catch (Exception exception) when (StorageExceptionMapper.CanHandle(exception))
        { return DesktopLayoutResult.Failed(StorageExceptionMapper.Code(exception)); }
        finally
        {
            if (temporaryPath is not null)
            {
                try { root.ValidateManagedFile(temporaryPath); File.Delete(temporaryPath); }
                catch (Exception exception) when (StorageExceptionMapper.CanHandle(exception))
                { /* Only this call's uncommitted staging file is eligible for cleanup. */ }
            }
        }
    }

    /// <summary>
    /// Returns saved order (including temporarily hidden IDs), with new available IDs appended in source order.
    /// Rendering filters the result against available IDs; it must not persist that filtered list as the full order.
    /// Invalid input or a merged order beyond the same persistence bound throws ArgumentException without IO.
    /// </summary>
    public static IReadOnlyList<string> ResolveOrder(IReadOnlyList<string> savedIds, IReadOnlyList<string> availableIds)
    {
        ArgumentNullException.ThrowIfNull(savedIds);
        ArgumentNullException.ThrowIfNull(availableIds);
        if (!AreValidIds(savedIds) || !AreValidIds(availableIds))
            throw new ArgumentException("Desktop identifiers must be unique, bounded safe ASCII identifiers.");
        var merged = new List<string>(savedIds);
        var known = new HashSet<string>(savedIds, StringComparer.Ordinal);
        foreach (string id in availableIds)
            if (known.Add(id)) merged.Add(id);
        if (merged.Count > MaximumIds) throw new ArgumentException("The merged desktop order exceeds its capacity.");
        return merged.AsReadOnly();
    }

    private DesktopLayoutResult ReadCurrent(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        root.ValidateManagedFile(StateFilePath);
        if (!File.Exists(StateFilePath)) return DesktopLayoutResult.Ready(DesktopLayout.Default);
        using var stream = new FileStream(StateFilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is <= 0 or > MaximumStateBytes) return DesktopLayoutResult.Failed(StorageErrors.CorruptState);
        try
        {
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement record = document.RootElement;
            if (record.ValueKind != JsonValueKind.Object) return DesktopLayoutResult.Failed(StorageErrors.CorruptState);
            string[] names = record.EnumerateObject().Select(property => property.Name).ToArray();
            if (names.Distinct(StringComparer.Ordinal).Count() != names.Length
                || !record.TryGetProperty("schemaVersion", out JsonElement schema) || !schema.TryGetInt32(out int version))
                return DesktopLayoutResult.Failed(StorageErrors.CorruptState);
            if (version > DesktopLayout.CurrentSchemaVersion) return DesktopLayoutResult.Failed(StorageErrors.FutureSchema);
            if (version != DesktopLayout.CurrentSchemaVersion || names.Length != 4
                || names.Any(name => name is not ("schemaVersion" or "orderedIds" or "revision" or "updatedUtc")))
                return DesktopLayoutResult.Failed(StorageErrors.CorruptState);
            if (!record.TryGetProperty("orderedIds", out JsonElement ids) || ids.ValueKind != JsonValueKind.Array
                || ids.GetArrayLength() > MaximumIds || ids.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.String)
                || !record.TryGetProperty("revision", out JsonElement revision) || !revision.TryGetInt64(out long parsedRevision) || parsedRevision < 1
                || !record.TryGetProperty("updatedUtc", out JsonElement updated) || updated.ValueKind != JsonValueKind.String
                || !updated.TryGetDateTimeOffset(out DateTimeOffset parsedUpdated) || parsedUpdated.Offset != TimeSpan.Zero)
                return DesktopLayoutResult.Failed(StorageErrors.CorruptState);
            string[] orderedIds = ids.EnumerateArray().Select(item => item.GetString()!).ToArray();
            if (!AreValidIds(orderedIds)) return DesktopLayoutResult.Failed(StorageErrors.CorruptState);
            cancellationToken.ThrowIfCancellationRequested();
            return DesktopLayoutResult.Ready(new(version, Array.AsReadOnly(orderedIds), parsedRevision, parsedUpdated));
        }
        catch (JsonException) { return DesktopLayoutResult.Failed(StorageErrors.CorruptState); }
        catch (InvalidOperationException) { return DesktopLayoutResult.Failed(StorageErrors.CorruptState); }
        catch (FormatException) { return DesktopLayoutResult.Failed(StorageErrors.CorruptState); }
    }

    private static bool AreValidIds(IReadOnlyList<string> ids) => ids.Count <= MaximumIds
        && ids.All(id => id is { Length: > 0 and <= MaximumIdLength }
            && id.All(character => character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '.' or '_' or '-'))
        && ids.Distinct(StringComparer.Ordinal).Count() == ids.Count;
}
