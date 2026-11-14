namespace AutumnOS.Runtime;

/// <summary>Every TLS/HTTP I/O start is admitted while the current short authority
/// lease is held. The lease never crosses await. Previously-started bytes cannot be recalled.</summary>
internal sealed class RuntimeNetworkAuthorityStream(Stream inner, Func<IDisposable> enterAuthority, CancellationToken lifetime) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanWrite => inner.CanWrite;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    // The broker exclusively uses asynchronous HTTP. Never hold a Monitor through
    // an unbounded synchronous socket read/write fallback.
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() { using var authority = enterAuthority(); lifetime.ThrowIfCancellationRequested(); }
    public override Task FlushAsync(CancellationToken cancellationToken) => FlushCore(cancellationToken);
    private async Task FlushCore(CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        Task pending;
        using (enterAuthority()) { linked.Token.ThrowIfCancellationRequested(); pending = inner.FlushAsync(linked.Token); }
        await pending.ConfigureAwait(false);
    }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        ValueTask<int> pending;
        using (enterAuthority()) { linked.Token.ThrowIfCancellationRequested(); pending = inner.ReadAsync(buffer, linked.Token); }
        int count = await pending.ConfigureAwait(false);
        using (enterAuthority()) linked.Token.ThrowIfCancellationRequested();
        return count;
    }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
        ValueTask pending;
        using (enterAuthority()) { linked.Token.ThrowIfCancellationRequested(); pending = inner.WriteAsync(buffer, linked.Token); }
        await pending.ConfigureAwait(false);
        using (enterAuthority()) linked.Token.ThrowIfCancellationRequested();
    }
    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override ValueTask DisposeAsync() => inner.DisposeAsync();
}
