using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

// Explicit synthetic duplex stream and OS peer. These tests prove protocol
// ordering, never a native handle, WebView Post, LSA session or process exit.
internal static class RuntimeDeliveryParentTransportTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return Case("write_and_cancel_are_not_post_or_retirement", WriteIsNotPost);
        yield return Case("fence_ack_advances_exact_channel", FenceAdvance);
        yield return Case("wrong_hash_quarantines_until_original_retirement", WrongHash);
        yield return Case("wrong_wire_direction_is_unknown", WrongDirection);
        yield return Case("peer_rechecked_after_blocked_read", PeerChanged);
        yield return Case("eof_is_unknown_and_preserves_fence", Eof);
        yield return Case("evidence_start_is_once", StartOnce);
    }

    private static (string, Action) Case(string name, Func<Task> action) =>
        ("g2c.transport.pure_fake_" + name, () => action().WaitAsync(TimeSpan.FromSeconds(8)).GetAwaiter().GetResult());

    private static async Task WriteIsNotPost()
    {
        var e = new Endpoint(); var ticket = e.Queue("{\"owned\":1}");
        var sent = (RuntimeDeliveryWireDelivery)await e.RemoteReader.ReadAsync(e.Binding);
        Check(e.Core.StateOf(ticket) == RuntimeDeliveryState.Sending);
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () => _ = await e.Core.WaitOutcomeAsync(ticket, cancellation.Token));
        Check(e.Core.OutstandingCount(e.Registration.Endpoint) == 1 && !e.Peer.Retired.Task.IsCompleted);
        await e.RemoteWriter.WritePostReceiptAsync(Post(sent.Delivery));
        Check(await e.Core.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.Posted);
        await e.Finish();
    }

    private static async Task FenceAdvance()
    {
        var e = new Endpoint(); var ticket = e.Queue("{}");
        _ = await e.RemoteReader.ReadAsync(e.Binding);
        var fence = await e.Core.BeginFenceAsync(Scope(e.Binding));
        var sent = (RuntimeDeliveryWireFence)await e.RemoteReader.ReadAsync(e.Binding);
        Task<RuntimeDeliveryCoordinator.MutationPermit> permit = fence.WaitCommitPermitAsync();
        Check(!permit.IsCompleted);
        await e.RemoteWriter.WriteFenceReceiptAsync(new(sent.Fence.FenceId, sent.Fence.Binding,
            sent.Fence.CutoffDeliveryId, sent.Fence.NextChannelGeneration));
        (await permit).Commit(() => { });
        Check(await e.Core.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.FencedUnknown);
        var next = e.Queue("{\"next\":true}");
        RuntimeDeliveryBinding advanced = e.Binding with { ChannelGeneration = 2 };
        var delivery = (RuntimeDeliveryWireDelivery)await e.RemoteReader.ReadAsync(advanced);
        await e.RemoteWriter.WritePostReceiptAsync(Post(delivery.Delivery));
        Check(await e.Core.WaitOutcomeAsync(next) == RuntimeDeliveryState.Posted);
        await e.Finish();
    }

    private static async Task WrongHash()
    {
        var e = new Endpoint(); var ticket = e.Queue("{}");
        var sent = (RuntimeDeliveryWireDelivery)await e.RemoteReader.ReadAsync(e.Binding);
        await e.RemoteWriter.WritePostReceiptAsync(Post(sent.Delivery) with { PayloadSha256 = new string('b', 64) });
        await ThrowsAsync<InvalidDataException>(() => e.Transport.WhenReaderStopped);
        Check(e.Transport.IsUnavailable && e.Core.StateOf(ticket) == RuntimeDeliveryState.Unknown);
        var fence = await e.Core.BeginFenceAsync(Scope(e.Binding));
        Task<RuntimeDeliveryCoordinator.MutationPermit> permit = fence.WaitCommitPermitAsync();
        Check(!permit.IsCompleted && !e.Peer.Retired.Task.IsCompleted);
        await e.Finish(); (await permit).Commit(() => { });
        Check(await e.Core.WaitOutcomeAsync(ticket) == RuntimeDeliveryState.RetiredUnknown);
    }

    private static async Task WrongDirection()
    {
        var e = new Endpoint(); var ticket = e.Queue("{}");
        var sent = (RuntimeDeliveryWireDelivery)await e.RemoteReader.ReadAsync(e.Binding);
        await e.RemoteWriter.WriteDeliveryAsync(sent.Delivery);
        await ThrowsAsync<InvalidDataException>(() => e.Transport.WhenReaderStopped);
        Check(e.Core.StateOf(ticket) == RuntimeDeliveryState.Unknown);
        await e.Finish();
    }

    private static async Task PeerChanged()
    {
        var e = new Endpoint(); var ticket = e.Queue("{}");
        var sent = (RuntimeDeliveryWireDelivery)await e.RemoteReader.ReadAsync(e.Binding);
        e.Peer.Authenticated = false;
        await e.RemoteWriter.WritePostReceiptAsync(Post(sent.Delivery));
        await ThrowsAsync<UnauthorizedAccessException>(() => e.Transport.WhenReaderStopped);
        Check(e.Core.StateOf(ticket) == RuntimeDeliveryState.Unknown && !e.Peer.Retired.Task.IsCompleted);
        await e.Finish();
    }

    private static async Task Eof()
    {
        var e = new Endpoint(); var ticket = e.Queue("{}");
        _ = await e.RemoteReader.ReadAsync(e.Binding);
        e.RemoteStream.EndOutput();
        await ThrowsAsync<EndOfStreamException>(() => e.Transport.WhenReaderStopped);
        var fence = await e.Core.BeginFenceAsync(Scope(e.Binding));
        Task<RuntimeDeliveryCoordinator.MutationPermit> permit = fence.WaitCommitPermitAsync();
        Check(e.Core.StateOf(ticket) == RuntimeDeliveryState.Unknown && !permit.IsCompleted);
        await e.Finish(); (await permit).Commit(() => { });
    }

    private static async Task StartOnce()
    {
        var e = new Endpoint();
        Throws<InvalidOperationException>(() => e.Transport.BindEvidenceAndStart(e.Registration.Evidence));
        Check(e.Core.SnapshotCounts().Endpoints == 1);
        await e.Finish();
    }

    private sealed class Endpoint
    {
        internal readonly RuntimeDeliveryBinding Binding = new(Guid.NewGuid(), "synthetic-account", 1, new string('a', 64),
            Guid.NewGuid(), 1, Guid.NewGuid(), 1, 1);
        internal readonly RuntimeDeliveryCoordinator Core = new();
        internal readonly FakePeer Peer;
        internal readonly RuntimeDeliveryParentTransport Transport;
        internal readonly RuntimeDeliveryCoordinator.HostRegistration Registration;
        internal readonly Duplex RemoteStream;
        internal readonly RuntimeDeliveryWireReader RemoteReader;
        internal readonly RuntimeDeliveryWireWriter RemoteWriter;
        internal Endpoint()
        {
            (Duplex local, RemoteStream) = Duplex.Pair();
            string nonce = RuntimeDeliveryWireProtocol.CreateNonce();
            Peer = new(Binding, local); Transport = new(Peer, nonce);
            Registration = Core.RegisterHostEndpoint(Binding, Transport, Transport);
            RemoteReader = new(RemoteStream, nonce); RemoteWriter = new(RemoteStream, nonce);
            Transport.BindEvidenceAndStart(Registration.Evidence);
        }
        internal RuntimeDeliveryCoordinator.DeliveryTicket Queue(string json)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            var ticket = Core.TryReserve(Registration.Endpoint, new(bytes.Length, Convert.ToHexStringLower(SHA256.HashData(bytes)), 1)).Ticket;
            Check(ticket is not null && Core.QueueValidated(ticket, bytes)); return ticket!;
        }
        internal async Task Finish()
        {
            Peer.Retired.TrySetResult(true); RemoteStream.EndOutput();
            Check(await Registration.Evidence.RetirementObserved);
            await Registration.Evidence.WhenIdle;
            try { await Transport.WhenReaderStopped; }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
    }

    private sealed class FakePeer : IOriginalRuntimeDeliveryPeer
    {
        internal readonly TaskCompletionSource<bool> Retired = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal volatile bool Authenticated = true;
        public RuntimeDeliveryBinding InitialBinding { get; }
        public Stream DuplexStream { get; }
        public RuntimeEndpointLifetimeRegistration OriginalLifetime { get; }
        internal FakePeer(RuntimeDeliveryBinding binding, Stream stream)
        { InitialBinding = binding; DuplexStream = stream; OriginalLifetime = RuntimeEndpointLifetimeRegistration.BindForHost(binding, Retired.Task); }
        public void RequireOriginalPeer() { if (!Authenticated) throw new UnauthorizedAccessException("SYNTHETIC_ORIGINAL_PEER_CHANGED"); }
    }

    private sealed class Duplex(ChannelReader<byte[]> input, ChannelWriter<byte[]> output) : Stream
    {
        private byte[] current = []; private int offset;
        internal static (Duplex, Duplex) Pair()
        {
            var a = Channel.CreateBounded<byte[]>(4); var b = Channel.CreateBounded<byte[]>(4);
            return (new(a.Reader, b.Writer), new(b.Reader, a.Writer));
        }
        internal void EndOutput() => output.TryComplete();
        public override bool CanRead => true; public override bool CanWrite => true; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (offset == current.Length)
            {
                if (!await input.WaitToReadAsync(cancellationToken)) return 0;
                if (!input.TryRead(out byte[]? chunk)) continue;
                current = chunk; offset = 0;
            }
            int count = Math.Min(buffer.Length, current.Length - offset);
            current.AsMemory(offset, count).CopyTo(buffer); offset += count; return count;
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer.ToArray(), cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static RuntimeNativePostReceipt Post(RuntimeDeliveryWrite value) => new(value.DeliveryId, value.Binding, value.Descriptor.Sha256, RuntimeNativePostResult.Posted);
    private static RuntimeAuthorityScope Scope(RuntimeDeliveryBinding b) => new(RuntimeAuthorityScopeKind.Instance, b.IdentityAuthorityId, b.AccountKey, b.ApplicationBinding, b.InstanceId);
    private static void Check(bool value) { if (!value) throw new InvalidOperationException("ASSERTION_FAILED"); }
    private static void Throws<T>(Action action) where T : Exception { try { action(); } catch (T) { return; } throw new InvalidOperationException("EXPECTED_" + typeof(T).Name); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception { try { await action(); } catch (T) { return; } throw new InvalidOperationException("EXPECTED_" + typeof(T).Name); }
}
