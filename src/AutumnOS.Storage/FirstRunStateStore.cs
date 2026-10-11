using System.Text.Json;
using System.Text.Json.Serialization;

namespace AutumnOS.Storage;

public enum FirstRunCheckpoint { Hello, Brand, Initializing, Completed }
// This records an onboarding decision, never whether a usable account session exists now.
public enum FirstRunLoginStep { Pending, Completed, Skipped, NotOffered }

public sealed record FirstRunState(int SchemaVersion, FirstRunCheckpoint Checkpoint, long Revision, DateTimeOffset UpdatedUtc,
    FirstRunLoginStep LoginStep = FirstRunLoginStep.Pending)
{
    public const int CurrentSchemaVersion = 2;
    [JsonIgnore]
    public bool IsComplete => Checkpoint == FirstRunCheckpoint.Completed;
}

public sealed record FirstRunResult(bool Success, FirstRunState? State, string ErrorCode, string RecoveryMessage)
{
    internal static FirstRunResult Ready(FirstRunState state) => new(true, state, StorageErrors.None, StorageErrors.Recovery(StorageErrors.None));
    internal static FirstRunResult Failed(string code) => new(false, null, code, StorageErrors.Recovery(code));
}

/// <summary>Versioned resumable startup checkpoints. This is not the full T02 first-run UI or a generic settings store.</summary>
public sealed class FirstRunStateStore(InstallationRoot root, CriticalOperationCoordinator? coordinator = null)
{
    private readonly CriticalOperationCoordinator operations = coordinator ?? new();
    private const int MaximumStateBytes = 16 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter<FirstRunCheckpoint>(allowIntegerValues: false),
            new JsonStringEnumConverter<FirstRunLoginStep>(allowIntegerValues: false) }
    };

    public string StateFilePath => Path.Combine(root.Directories["Config"], "first-run.json");
    public string LockFilePath => Path.Combine(root.Directories["Config"], ".first-run.lock");

    public FirstRunResult Load(CancellationToken cancellationToken = default)
    {
        DataRootResult prepared = root.EnsureCreated(cancellationToken);
        if (!prepared.Success) return FirstRunResult.Failed(prepared.ErrorCode);
        try { return ReadCurrent(cancellationToken); }
        catch (Exception exception) when (StorageExceptionMapper.CanHandle(exception))
        { return FirstRunResult.Failed(StorageExceptionMapper.Code(exception)); }
    }

    /// <summary>Commits one adjacent checkpoint, or idempotently keeps the same checkpoint. Re-read the result on every call.</summary>
    public FirstRunResult Advance(FirstRunCheckpoint checkpoint, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(checkpoint)) return FirstRunResult.Failed(StorageErrors.InvalidTransition);
        return Save(checkpoint, null, cancellationToken);
    }

    /// <summary>Only the first-run controller calls this after a verified login or explicit skip.</summary>
    public FirstRunResult RecordLoginStep(FirstRunLoginStep step, CancellationToken cancellationToken = default)
    {
        if (step is not (FirstRunLoginStep.Completed or FirstRunLoginStep.Skipped))
            return FirstRunResult.Failed(StorageErrors.InvalidTransition);
        return Save(null, step, cancellationToken);
    }

    private FirstRunResult Save(FirstRunCheckpoint? requestedCheckpoint, FirstRunLoginStep? loginStep, CancellationToken cancellationToken)
    {
        string? temporaryPath = null;
        try
        {
            using IDisposable critical = operations.EnterWrite("首次启动配置写入");
            DataRootResult prepared = root.EnsureCreated(cancellationToken);
            if (!prepared.Success) return FirstRunResult.Failed(prepared.ErrorCode);
            root.ValidateManagedFile(LockFilePath);
            FileStream exclusiveLock;
            try { exclusiveLock = new FileStream(LockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException exception) when ((exception.HResult & 0xffff) is 32 or 33)
            { return FirstRunResult.Failed(StorageErrors.Busy); }

            using (exclusiveLock)
            {
                FirstRunResult currentResult = ReadCurrent(cancellationToken);
                if (!currentResult.Success) return currentResult;
                FirstRunState current = currentResult.State!;
                FirstRunCheckpoint checkpoint = requestedCheckpoint ?? current.Checkpoint;
                if (loginStep.HasValue && (current.Checkpoint != FirstRunCheckpoint.Initializing ||
                    current.LoginStep != FirstRunLoginStep.Pending && current.LoginStep != loginStep))
                    return FirstRunResult.Failed(StorageErrors.InvalidTransition);
                int distance = (int)checkpoint - (int)current.Checkpoint;
                if (distance is < 0 or > 1) return FirstRunResult.Failed(StorageErrors.InvalidTransition);
                if (checkpoint == FirstRunCheckpoint.Completed && current.LoginStep == FirstRunLoginStep.Pending)
                    return FirstRunResult.Failed(StorageErrors.InvalidTransition);
                bool alreadyExists = File.Exists(StateFilePath);
                if (distance == 0 && alreadyExists && (!loginStep.HasValue || loginStep == current.LoginStep)) return currentResult;
                if (current.Revision == long.MaxValue) return FirstRunResult.Failed(StorageErrors.CorruptState);

                var next = new FirstRunState(FirstRunState.CurrentSchemaVersion, checkpoint,
                    current.Revision + 1, DateTimeOffset.UtcNow, loginStep ?? current.LoginStep);
                temporaryPath = Path.Combine(root.Directories["Config"], $".first-run-{Guid.NewGuid():N}.tmp");
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
                if (alreadyExists)
                {
                    // Same-directory replacement is atomic. No delete-then-write gap and no fallback that clobbers bad JSON.
                    File.Replace(temporaryPath, StateFilePath, destinationBackupFileName: null);
                }
                else File.Move(temporaryPath, StateFilePath, overwrite: false);
                temporaryPath = null;
                return FirstRunResult.Ready(next);
            }
        }
        catch (Exception exception) when (StorageExceptionMapper.CanHandle(exception))
        { return FirstRunResult.Failed(StorageExceptionMapper.Code(exception)); }
        finally
        {
            if (temporaryPath is not null)
            {
                try { root.ValidateManagedFile(temporaryPath); File.Delete(temporaryPath); }
                catch (Exception exception) when (StorageExceptionMapper.CanHandle(exception)) { /* Own uncommitted staging file only; never reset the durable state. */ }
            }
        }
    }

    private FirstRunResult ReadCurrent(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        root.ValidateManagedFile(StateFilePath);
        if (!File.Exists(StateFilePath))
            return FirstRunResult.Ready(new(FirstRunState.CurrentSchemaVersion, FirstRunCheckpoint.Hello, 0, DateTimeOffset.UnixEpoch));

        using var stream = new FileStream(StateFilePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (stream.Length is <= 0 or > MaximumStateBytes) return FirstRunResult.Failed(StorageErrors.CorruptState);
        try
        {
            using JsonDocument document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            JsonElement record = document.RootElement;
            if (record.ValueKind != JsonValueKind.Object
                || !record.TryGetProperty("schemaVersion", out JsonElement schema)
                || !schema.TryGetInt32(out int version)) return FirstRunResult.Failed(StorageErrors.CorruptState);
            if (version > FirstRunState.CurrentSchemaVersion) return FirstRunResult.Failed(StorageErrors.FutureSchema);
            if (version is not (1 or FirstRunState.CurrentSchemaVersion)) return FirstRunResult.Failed(StorageErrors.CorruptState);

            string[] propertyNames = record.EnumerateObject().Select(property => property.Name).ToArray();
            int expectedProperties = version == 1 ? 4 : 5;
            if (propertyNames.Length != expectedProperties || propertyNames.Distinct(StringComparer.Ordinal).Count() != expectedProperties
                || propertyNames.Any(name => name is not ("schemaVersion" or "checkpoint" or "revision" or "updatedUtc") &&
                    !(version == 2 && name == "loginStep")))
                return FirstRunResult.Failed(StorageErrors.CorruptState);

            // Required fields are checked explicitly: serializer defaults must never turn truncated JSON into valid state.
            if (!record.TryGetProperty("checkpoint", out JsonElement checkpoint) || checkpoint.ValueKind != JsonValueKind.String
                || !Enum.TryParse(checkpoint.GetString(), ignoreCase: false, out FirstRunCheckpoint parsedCheckpoint)
                || !Enum.IsDefined(parsedCheckpoint)
                || !string.Equals(Enum.GetName(parsedCheckpoint), checkpoint.GetString(), StringComparison.Ordinal)
                || !record.TryGetProperty("revision", out JsonElement revision) || !revision.TryGetInt64(out long parsedRevision)
                || parsedRevision < 1
                || !record.TryGetProperty("updatedUtc", out JsonElement updated) || updated.ValueKind != JsonValueKind.String
                || !updated.TryGetDateTimeOffset(out DateTimeOffset parsedUpdated) || parsedUpdated.Offset != TimeSpan.Zero)
                return FirstRunResult.Failed(StorageErrors.CorruptState);
            FirstRunLoginStep step = parsedCheckpoint == FirstRunCheckpoint.Completed ? FirstRunLoginStep.NotOffered : FirstRunLoginStep.Pending;
            if (version == 2 && (!record.TryGetProperty("loginStep", out JsonElement login) || login.ValueKind != JsonValueKind.String ||
                !Enum.TryParse(login.GetString(), false, out step) || !Enum.IsDefined(step) ||
                Enum.GetName(step) != login.GetString())) return FirstRunResult.Failed(StorageErrors.CorruptState);
            if (parsedCheckpoint == FirstRunCheckpoint.Completed && step == FirstRunLoginStep.Pending ||
                parsedCheckpoint != FirstRunCheckpoint.Completed && step == FirstRunLoginStep.NotOffered ||
                parsedCheckpoint < FirstRunCheckpoint.Initializing && step != FirstRunLoginStep.Pending)
                return FirstRunResult.Failed(StorageErrors.CorruptState);
            // Legacy completed installs keep their completion marker; no new welcome or implied login.
            return FirstRunResult.Ready(new(FirstRunState.CurrentSchemaVersion, parsedCheckpoint, parsedRevision, parsedUpdated, step));
        }
        catch (JsonException) { return FirstRunResult.Failed(StorageErrors.CorruptState); }
        catch (InvalidOperationException) { return FirstRunResult.Failed(StorageErrors.CorruptState); }
        catch (FormatException) { return FirstRunResult.Failed(StorageErrors.CorruptState); }
    }
}
