namespace AutumnOS.NativeHost.Windows;

// WebView2 reads response content asynchronously. Its managed IStream adapter
// retains this Stream through the COM callable wrapper, so removing our tracking
// reference must not Dispose a stream the browser may still read or seek.
// https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/working-with-local-content#example-of-handling-the-webresourcerequested-event
// https://learn.microsoft.com/en-us/dotnet/standard/native-interop/com-callable-wrapper
// Only immutable, already bounded package/challenge byte arrays are accepted:
// no file handles, network streams or additional copies of response bodies.
internal sealed class BrowserResponseStream : Stream
{
    internal const int MaximumPending = 512;
    private readonly object sync = new();
    private readonly MemoryStream content;
    private long readThrough;
    private bool consumed;
    private bool Consumed => Volatile.Read(ref consumed);

    private BrowserResponseStream(byte[] bytes) => content = new(bytes, writable: false);

    // Called only by the browser STA. Reader callbacks never mutate the list;
    // the existing controller-close path can still dispose every pending stream.
    internal static Stream Open(List<Stream> pending, byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(bytes);
        pending.RemoveAll(static item => item is BrowserResponseStream { Consumed: true });
        if (bytes.Length == 0) return Stream.Null;
        if (pending.Count >= MaximumPending) throw new InvalidDataException("BROWSER_RESPONSE_STREAM_LIMIT");
        var stream = new BrowserResponseStream(bytes);
        pending.Add(stream);
        return stream;
    }

    public override bool CanRead { get { lock (sync) return content.CanRead; } }
    public override bool CanSeek { get { lock (sync) return content.CanSeek; } }
    public override bool CanWrite => false;
    public override long Length { get { lock (sync) return content.Length; } }
    public override long Position
    {
        get { lock (sync) return content.Position; }
        set { lock (sync) content.Position = value; }
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        lock (sync)
        {
            long start = content.Position;
            int read = content.Read(buffer, offset, count);
            ObserveRead(start, read);
            return read;
        }
    }

    public override int Read(Span<byte> buffer)
    {
        lock (sync)
        {
            long start = content.Position;
            int read = content.Read(buffer);
            ObserveRead(start, read);
            return read;
        }
    }

    public override int ReadByte()
    {
        lock (sync)
        {
            long start = content.Position;
            int value = content.ReadByte();
            ObserveRead(start, value < 0 ? 0 : 1);
            return value;
        }
    }

    private void ObserveRead(long start, int read)
    {
        // A seek or zero-length read is not consumption. Require coverage from
        // byte zero, including when a consumer stops after an exact Length read
        // without requesting another EOF read. Keep the stream readable after
        // this point: COM may legitimately inspect or rewind it before release.
        if (read > 0 && start <= readThrough)
        {
            readThrough = Math.Max(readThrough, start + read);
            if (readThrough == content.Length) Volatile.Write(ref consumed, true);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    { lock (sync) return content.Seek(offset, origin); }
    public override void Flush() { lock (sync) content.Flush(); }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(ReadOnlySpan<byte> buffer) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing) lock (sync) content.Dispose();
        base.Dispose(disposing);
    }
}
