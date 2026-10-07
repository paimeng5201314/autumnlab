using System.Text.Json;
using System.Runtime.CompilerServices;

namespace AutumnOS.Storage;

public enum DiagnosticEvent
{
    StartupDataReady,
    StartupDataFailed,
    FirstRunCheckpointSaved,
    FirstRunStateRejected,
    ShellOpened
}

public sealed record DiagnosticLogStatus(long FailedWrites, long ConsecutiveFailures, string LastErrorCode,
    DateTimeOffset? LastFailureUtc, long Rotations)
{
    public bool IsRecordingStopped => ConsecutiveFailures != 0;
}

/// <summary>Bounded local diagnostics. No free-text payload, paths, exceptions, tokens or user content can be submitted.</summary>
public sealed class StructuredLog(InstallationRoot root)
{
    public const int MaximumLogBytes = 1024 * 1024;
    public const int RetainedRotations = 2;
    private static readonly object Gate = new();
    internal static object SynchronizationGate => Gate;
    private static readonly ConditionalWeakTable<InstallationRoot, LogState> States = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Process-local health shared by loggers using this installation root, including after recovery.</summary>
    public DiagnosticLogStatus Status
    {
        get
        {
            lock (Gate)
            {
                LogState state = States.GetValue(root, static _ => new LogState());
                return new(state.FailedWrites, state.ConsecutiveFailures, state.LastErrorCode, state.LastFailureUtc, state.Rotations);
            }
        }
    }

    internal static string FileName(int generation) => generation == 0 ? "diagnostics.jsonl" : $"diagnostics.{generation}.jsonl";

    public DataRootResult Write(DiagnosticEvent eventId, string? errorCode = null, Guid? correlationId = null)
    {
        if (!Enum.IsDefined(eventId)) throw new ArgumentOutOfRangeException(nameof(eventId));
        string code = errorCode ?? StorageErrors.None;
        if (!StorageErrors.IsKnown(code)) throw new ArgumentException("Only registered diagnostic error codes are accepted.", nameof(errorCode));
        lock (Gate)
        {
            LogState state = States.GetValue(root, static _ => new LogState());
            try
            {
                string[] paths = Enumerable.Range(0, RetainedRotations + 1)
                    .Select(generation => Path.Combine(root.Directories["Logs"], FileName(generation))).ToArray();
                // Validate every rotation target before moving or replacing any file.
                foreach (string path in paths)
                {
                    root.ValidateManagedFile(path);
                    if (File.Exists(path) && new FileInfo(path).Length > MaximumLogBytes)
                        throw new StoragePathException(StorageErrors.LogFull);
                }
                byte[] line = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    timestampUtc = DateTimeOffset.UtcNow,
                    eventId = eventId.ToString(),
                    errorCode = code,
                    correlationId = correlationId ?? Guid.NewGuid()
                }, JsonOptions);
                long length;
                using (var existing = new FileStream(paths[0], FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
                    length = existing.Length;
                if (length + line.Length + 1 > MaximumLogBytes)
                {
                    for (int generation = RetainedRotations; generation > 0; generation--)
                    {
                        root.ValidateManagedFile(paths[generation - 1]);
                        root.ValidateManagedFile(paths[generation]);
                        if (File.Exists(paths[generation - 1])) File.Move(paths[generation - 1], paths[generation], overwrite: true);
                    }
                    state.Rotations++;
                }
                root.ValidateManagedFile(paths[0]);
                using var stream = new FileStream(paths[0], FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
                if (stream.Length + line.Length + 1 > MaximumLogBytes) throw new StoragePathException(StorageErrors.LogFull);
                stream.Seek(0, SeekOrigin.End);
                stream.Write(line);
                stream.WriteByte((byte)'\n');
                stream.Flush(flushToDisk: true);
                state.ConsecutiveFailures = 0;
                return DataRootResult.Ready();
            }
            catch (Exception exception) when (StorageExceptionMapper.CanHandle(exception))
            {
                state.FailedWrites++;
                state.ConsecutiveFailures++;
                state.LastErrorCode = StorageExceptionMapper.Code(exception);
                state.LastFailureUtc = DateTimeOffset.UtcNow;
                return DataRootResult.Failed(state.LastErrorCode);
            }
        }
    }

    private sealed class LogState
    {
        public long FailedWrites;
        public long ConsecutiveFailures;
        public string LastErrorCode = StorageErrors.None;
        public DateTimeOffset? LastFailureUtc;
        public long Rotations;
    }
}
