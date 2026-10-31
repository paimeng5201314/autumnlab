namespace AutumnOS.OwnedBrowserParent;

internal sealed record CleanupAttempt(string stage, string status, bool critical, string? exception_type,
    uint? hresult, int? native_error, string? fixed_native_stage);

// Every action is attempted independently. A writer's final flush can fail
// after the peer has exited; that cannot skip unrelated owned resources.
internal sealed class CleanupJournal
{
    public List<CleanupAttempt> attempts { get; } = [];
    public bool any_failure => attempts.Any(a => a.status == "failed");
    public bool critical_failure => attempts.Any(a => a.status == "failed" && a.critical);
    internal bool Attempt(string stage, Action action, bool critical = true)
    {
        try { action(); attempts.Add(new(stage, "passed", critical, null, null, null, null)); return true; }
        catch (Exception error)
        {
            attempts.Add(new(stage, "failed", critical, error.GetType().Name, unchecked((uint)error.HResult),
                (error as System.ComponentModel.Win32Exception)?.NativeErrorCode, (error as OwnedNativeException)?.FixedStage));
            return false;
        }
    }
    internal void DisposeWriterAndTransport(string stage, StreamWriter? writer, Stream? transport)
    {
        if (writer is not null) Attempt(stage + ".writer_dispose_flush", writer.Dispose, critical: false);
        if (transport is not null) Attempt(stage + ".transport_dispose", transport.Dispose);
    }
}
