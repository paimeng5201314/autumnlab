using System.Text.Json;

namespace AutumnOS.Storage;

public enum DiagnosticEvent
{
    StartupDataReady,
    StartupDataFailed,
    FirstRunCheckpointSaved,
    FirstRunStateRejected,
    ShellOpened
}

/// <summary>Bounded local diagnostics. No free-text payload, paths, exceptions, tokens or user content can be submitted.</summary>
public sealed class StructuredLog(InstallationRoot root)
{
    private const long MaximumLogBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public DataRootResult Write(DiagnosticEvent eventId, string? errorCode = null, Guid? correlationId = null)
    {
        if (!Enum.IsDefined(eventId)) throw new ArgumentOutOfRangeException(nameof(eventId));
        string code = errorCode ?? StorageErrors.None;
        if (!StorageErrors.IsKnown(code)) throw new ArgumentException("Only registered diagnostic error codes are accepted.", nameof(errorCode));
        string logPath = Path.Combine(root.Directories["Logs"], "diagnostics.jsonl");
        try
        {
            root.ValidateManagedFile(logPath);
            byte[] line = JsonSerializer.SerializeToUtf8Bytes(new
            {
                timestampUtc = DateTimeOffset.UtcNow,
                eventId = eventId.ToString(),
                errorCode = code,
                correlationId = correlationId ?? Guid.NewGuid()
            }, JsonOptions);
            using var stream = new FileStream(logPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
            if (stream.Length + line.Length + 1 > MaximumLogBytes) return DataRootResult.Failed(StorageErrors.LogFull);
            stream.Seek(0, SeekOrigin.End);
            stream.Write(line);
            stream.WriteByte((byte)'\n');
            stream.Flush(flushToDisk: true);
            return DataRootResult.Ready();
        }
        catch (Exception exception) when (StorageExceptionMapper.CanHandle(exception))
        { return DataRootResult.Failed(StorageExceptionMapper.Code(exception)); }
    }
}
