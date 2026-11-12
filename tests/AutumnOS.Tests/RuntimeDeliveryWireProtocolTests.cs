using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using AutumnOS.Runtime;

namespace AutumnOS.Tests;

// Memory streams, controlled Tasks and synthetic lifetimes only. No pipe peer,
// native Post, process authentication or OS retirement evidence is produced.
internal static class RuntimeDeliveryWireProtocolTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return Case("G2 wire roundtrips four DTO kinds with independent continuous sequence", RoundTrip);
        yield return Case("G2 wire reads arbitrary one-byte fragments without treating short reads as EOF", ShortReads);
        yield return Case("G2 wire rejects oversized and malformed headers before reading any body", HeaderBounds);
        yield return Case("G2 wire rejects unknown duplicate escaped-duplicate and nested metadata", ExactMetadata);
        yield return Case("G2 wire rejects invalid metadata UTF8 and JSON injection", InvalidMetadataEncoding);
        yield return Case("G2 wire verifies payload SHA256 and strict UTF8 independently", PayloadIntegrity);
        yield return Case("G2 wire validates nonce and every full-binding field", BindingAndNonce);
        yield return Case("G2 wire rejects replayed or skipped sequence while delivery IDs may decrease", SequenceReplay);
        yield return Case("G2 wire EOF and every partial-frame stage fault the direction", PartialFrames);
        yield return Case("G2 wire cancellation never returns a partial message or reusable direction", ReadCancellation);
        yield return Case("G2 wire permits only one in-flight read without abandoning its owner", ConcurrentRead);
        yield return Case("G2 wire write cancellation faults direction and cannot claim a receipt", WriteCancellation);
        yield return Case("G2 wire serializes owners rather than interleaving concurrent frame writes", ConcurrentWrite);
        yield return Case("G2 wire version1 has an explicit 32768-byte payload hard limit", PayloadBounds);
        yield return Case("G2 wire rejects enum counter descriptor and fence-generation forgeries", CounterAndEnum);
        yield return Case("G2 wire unknown Post ACK remains raw and is rejected by original evidence port", UnknownPostAck);
        yield return Case("G2 wire unknown fence ACK and EOF cannot mint a commit permit or retirement", UnknownFenceAck);
    }
    private static (string Name, Action Run) Case(string name, Func<Task> test) =>
        (name, () => test().WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult());
    private static void Check(bool condition, string message = "Wire protocol invariant failed.")
    { if (!condition) throw new InvalidOperationException(message); }
    private static RuntimeDeliveryBinding Binding() => new(Guid.NewGuid(), "fixture-account", 3, new string('a', 64), Guid.NewGuid(), 4, Guid.NewGuid(), 5, 6);
    private static string Hash(ReadOnlySpan<byte> value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static RuntimeDeliveryWrite Delivery(RuntimeDeliveryBinding binding, long id = 13, string body = "{\"fixture\":\"你好\",\"nested\":{\"ok\":true}}")
    { byte[] bytes = Encoding.UTF8.GetBytes(body); return new(id, binding, new(bytes.Length, Hash(bytes), 8), bytes); }
    private static async Task<byte[]> Frame(RuntimeDeliveryWrite value)
    { using var stream = new MemoryStream(); await new RuntimeDeliveryWireWriter(stream, Nonce).WriteDeliveryAsync(value); return stream.ToArray(); }
    private static async Task<byte[]> PostFrame(RuntimeNativePostReceipt value)
    { using var stream = new MemoryStream(); await new RuntimeDeliveryWireWriter(stream, Nonce).WritePostReceiptAsync(value); return stream.ToArray(); }
    private static async Task Reject<T>(Func<Task> operation) where T : Exception
    { try { await operation(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static async Task RejectFrame(byte[] frame, RuntimeDeliveryBinding binding, string? nonce = null)
    {
        using var stream = new MemoryStream(frame);
        var reader = new RuntimeDeliveryWireReader(stream, nonce ?? Nonce);
        await Reject<InvalidDataException>(async () => _ = await reader.ReadAsync(binding));
        await Reject<InvalidDataException>(async () => _ = await reader.ReadAsync(binding));
    }
    private static string Metadata(byte[] frame) => Encoding.UTF8.GetString(frame, RuntimeDeliveryWireProtocol.HeaderBytes,
        (int)BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8, 4)));
    private static byte[] ReplaceMetadata(byte[] original, string metadata)
        => ReplaceMetadataBytes(original, Encoding.UTF8.GetBytes(metadata));
    private static byte[] ReplaceMetadataBytes(byte[] original, byte[] metadata)
    {
        int oldLength = (int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(8, 4));
        int payloadLength = original.Length - RuntimeDeliveryWireProtocol.HeaderBytes - oldLength;
        byte[] result = new byte[RuntimeDeliveryWireProtocol.HeaderBytes + metadata.Length + payloadLength];
        original.AsSpan(0, RuntimeDeliveryWireProtocol.HeaderBytes).CopyTo(result);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(8, 4), (uint)metadata.Length);
        metadata.CopyTo(result, RuntimeDeliveryWireProtocol.HeaderBytes);
        original.AsSpan(RuntimeDeliveryWireProtocol.HeaderBytes + oldLength).CopyTo(result.AsSpan(RuntimeDeliveryWireProtocol.HeaderBytes + metadata.Length));
        return result;
    }
    private static async Task RoundTrip()
    {
        RuntimeDeliveryBinding binding = Binding(); RuntimeDeliveryWrite first = Delivery(binding, 90), second = Delivery(binding, 2);
        using var stream = new MemoryStream(); var writer = new RuntimeDeliveryWireWriter(stream, Nonce);
        await writer.WriteDeliveryAsync(first); await writer.WriteDeliveryAsync(second);
        RuntimeFenceWrite fence = new(17, binding, 90, 7); await writer.WriteFenceAsync(fence);
        RuntimeNativePostReceipt posted = new(90, binding, first.Descriptor.Sha256, RuntimeNativePostResult.Posted);
        RuntimeNativePostReceipt rejected = posted with { DeliveryId = 2, Result = RuntimeNativePostResult.RejectedBeforePost };
        await writer.WritePostReceiptAsync(posted); await writer.WritePostReceiptAsync(rejected);
        RuntimeNativeFenceReceipt ack = new(fence.FenceId, binding, fence.CutoffDeliveryId, fence.NextChannelGeneration);
        await writer.WriteFenceReceiptAsync(ack); stream.Position = 0;
        var reader = new RuntimeDeliveryWireReader(stream, Nonce);
        var a = (RuntimeDeliveryWireDelivery)await reader.ReadAsync(binding);
        var b = (RuntimeDeliveryWireDelivery)await reader.ReadAsync(binding);
        Check(a.WireSequence == 1 && b.WireSequence == 2 && a.Delivery.DeliveryId == 90 && b.Delivery.DeliveryId == 2);
        Check(a.Delivery.Descriptor == first.Descriptor && a.Delivery.Payload.Span.SequenceEqual(first.Payload.Span));
        var c = (RuntimeDeliveryWireFence)await reader.ReadAsync(binding); Check(c.WireSequence == 3 && c.Fence == fence);
        var d = (RuntimeDeliveryWirePostReceipt)await reader.ReadAsync(binding); Check(d.WireSequence == 4 && d.Receipt == posted);
        var e = (RuntimeDeliveryWirePostReceipt)await reader.ReadAsync(binding); Check(e.WireSequence == 5 && e.Receipt == rejected);
        var f = (RuntimeDeliveryWireFenceReceipt)await reader.ReadAsync(binding); Check(f.WireSequence == 6 && f.Receipt == ack);
        Check(stream.CanRead && stream.CanWrite && stream.Position == stream.Length);
    }
    private static async Task ShortReads()
    {
        RuntimeDeliveryBinding binding = Binding(); RuntimeDeliveryWrite expected = Delivery(binding);
        using var stream = new FragmentedReadStream(await Frame(expected), 1);
        var decoded = (RuntimeDeliveryWireDelivery)await new RuntimeDeliveryWireReader(stream, Nonce).ReadAsync(binding);
        Check(decoded.Delivery.Payload.Span.SequenceEqual(expected.Payload.Span) && stream.ReadCalls > 24);
    }
    private static async Task HeaderBounds()
    {
        RuntimeDeliveryBinding binding = Binding(); byte[] valid = await Frame(Delivery(binding));
        Action<byte[]>[] mutations = [x => x[0] ^= 1, x => x[4] = 2, x => x[6] = 0, x => x[6] = 255, x => x[7] = 1,
            x => BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(8, 4), 0),
            x => BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(8, 4), 4097),
            x => BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(8, 4), uint.MaxValue),
            x => BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(12, 4), 32769),
            x => BinaryPrimitives.WriteUInt32LittleEndian(x.AsSpan(12, 4), uint.MaxValue),
            x => x[6] = (byte)RuntimeDeliveryWireKind.Fence,
            x => BinaryPrimitives.WriteInt64LittleEndian(x.AsSpan(16, 8), 0),
            x => BinaryPrimitives.WriteInt64LittleEndian(x.AsSpan(16, 8), -1),
            x => BinaryPrimitives.WriteInt64LittleEndian(x.AsSpan(16, 8), 2)];
        foreach (Action<byte[]> mutate in mutations)
        {
            byte[] frame = valid.ToArray(); mutate(frame);
            using var stream = new FragmentedReadStream(frame, 24, stopAfterHeader: true);
            await Reject<InvalidDataException>(async () => _ = await new RuntimeDeliveryWireReader(stream, Nonce).ReadAsync(binding));
            Check(stream.BytesRead == 24, "Malformed header caused a body read/allocation path.");
        }
    }
    private static async Task ExactMetadata()
    {
        RuntimeDeliveryBinding binding = Binding(); byte[] frame = await Frame(Delivery(binding)); string metadata = Metadata(frame);
        foreach (string invalid in new[]
        {
            metadata[..^1] + ",\"unexpected\":0}", metadata[..^1] + ",\"nonce\":\"" + Nonce + "\"}",
            metadata[..^1] + ",\"\\u006eonce\":\"" + Nonce + "\"}",
            metadata.Replace("\"frozenRevision\":8", "\"frozenRevision\":{\"injected\":8}", StringComparison.Ordinal),
            metadata.Replace("\"frozenRevision\":8", "\"frozenRevision\":[8]", StringComparison.Ordinal),
            metadata.Replace("\"frozenRevision\":8", "\"frozenRevision\":true", StringComparison.Ordinal),
            metadata.Replace("\"frozenRevision\":8", "\"frozenRevision\":null", StringComparison.Ordinal),
            metadata.Replace(",\"frozenRevision\":8", "", StringComparison.Ordinal), metadata + "{}",
            metadata[..^1] + ",}", "[" + metadata + "]"
        }) await RejectFrame(ReplaceMetadata(frame, invalid), binding);
    }
    private static async Task InvalidMetadataEncoding()
    {
        RuntimeDeliveryBinding binding = Binding(); byte[] frame = await Frame(Delivery(binding));
        byte[] metadata = Encoding.UTF8.GetBytes(Metadata(frame)); metadata[metadata.Length / 2] = 0xff;
        await RejectFrame(ReplaceMetadataBytes(frame, metadata), binding);
        await RejectFrame(ReplaceMetadata(frame, Metadata(frame).Replace("\"account\":\"fixture-account\"", "\"account\":\"\\ud800\"", StringComparison.Ordinal)), binding);
        await RejectFrame(ReplaceMetadata(frame, Metadata(frame).Replace("\"account\":\"fixture-account\"", "\"account\":\"bad\\u0000account\"", StringComparison.Ordinal)), binding);
    }
    private static async Task PayloadIntegrity()
    {
        RuntimeDeliveryBinding binding = Binding(); byte[] valid = await Frame(Delivery(binding, body: "ok"));
        byte[] corrupted = valid.ToArray(); corrupted[^1] ^= 1; await RejectFrame(corrupted, binding);
        await RejectFrame(ReplaceMetadata(valid, Metadata(valid).Replace("\"bytes\":2", "\"bytes\":3", StringComparison.Ordinal)), binding);
        byte[] invalidUtf8 = valid.ToArray(); invalidUtf8[^2] = 0xc0; invalidUtf8[^1] = 0xaf;
        invalidUtf8 = ReplaceMetadata(invalidUtf8, Metadata(valid).Replace(Hash(Encoding.UTF8.GetBytes("ok")), Hash([0xc0, 0xaf]), StringComparison.Ordinal));
        await RejectFrame(invalidUtf8, binding);
    }
    private static async Task BindingAndNonce()
    {
        RuntimeDeliveryBinding binding = Binding(); byte[] frame = await Frame(Delivery(binding));
        await RejectFrame(frame, binding, new string('b', 64));
        await RejectFrame(ReplaceMetadata(frame, Metadata(frame).Replace(Nonce, Nonce.ToUpperInvariant(), StringComparison.Ordinal)), binding);
        RuntimeDeliveryBinding[] stale = [binding with { IdentityAuthorityId = Guid.NewGuid() }, binding with { AccountKey = "other" },
            binding with { AccountEpoch = 4 }, binding with { ApplicationBinding = new string('b', 64) }, binding with { InstanceId = Guid.NewGuid() },
            binding with { InstanceEpoch = 5 }, binding with { DocumentId = Guid.NewGuid() }, binding with { DocumentGeneration = 6 }, binding with { ChannelGeneration = 7 }];
        foreach (RuntimeDeliveryBinding expected in stale) await RejectFrame(frame, expected);
    }
    private static async Task SequenceReplay()
    {
        RuntimeDeliveryBinding binding = Binding(); byte[] first = await Frame(Delivery(binding, 90)), second = await Frame(Delivery(binding, 2));
        foreach (long sequence in new long[] { 1, 3 })
        {
            BinaryPrimitives.WriteInt64LittleEndian(second.AsSpan(16, 8), sequence);
            using var stream = new MemoryStream([.. first, .. second]); var reader = new RuntimeDeliveryWireReader(stream, Nonce);
            _ = await reader.ReadAsync(binding);
            await Reject<InvalidDataException>(async () => _ = await reader.ReadAsync(binding));
        }
    }
    private static async Task PartialFrames()
    {
        RuntimeDeliveryBinding binding = Binding(); byte[] frame = await Frame(Delivery(binding));
        int metadata = (int)BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(8, 4));
        foreach (int length in new[] { 0, 1, 23, 24, 24 + metadata - 1, frame.Length - 1 })
        {
            using var stream = new MemoryStream(frame[..length]); var reader = new RuntimeDeliveryWireReader(stream, Nonce);
            await Reject<EndOfStreamException>(async () => _ = await reader.ReadAsync(binding));
            await Reject<InvalidDataException>(async () => _ = await reader.ReadAsync(binding));
        }
    }
    private static async Task ReadCancellation()
    {
        RuntimeDeliveryBinding binding = Binding(); using var stream = new ControlledReadStream(await Frame(Delivery(binding)));
        var reader = new RuntimeDeliveryWireReader(stream, Nonce); using var cancellation = new CancellationTokenSource();
        Task<RuntimeDeliveryWireMessage> pending = reader.ReadAsync(binding, cancellation.Token).AsTask(); await stream.Started.Task;
        cancellation.Cancel(); await Reject<OperationCanceledException>(async () => _ = await pending);
        stream.Release.TrySetResult(); await Reject<InvalidDataException>(async () => _ = await reader.ReadAsync(binding));
    }
    private static async Task ConcurrentRead()
    {
        RuntimeDeliveryBinding binding = Binding(); using var stream = new ControlledReadStream(await Frame(Delivery(binding)));
        var reader = new RuntimeDeliveryWireReader(stream, Nonce); Task<RuntimeDeliveryWireMessage> first = reader.ReadAsync(binding).AsTask(); await stream.Started.Task;
        await Reject<InvalidOperationException>(async () => _ = await reader.ReadAsync(binding));
        stream.Release.TrySetResult(); Check((await first).WireSequence == 1);
    }
    private static async Task WriteCancellation()
    {
        using var stream = new ControlledWriteStream(); var writer = new RuntimeDeliveryWireWriter(stream, Nonce);
        using var cancellation = new CancellationTokenSource(); Task first = writer.WriteDeliveryAsync(Delivery(Binding()), cancellation.Token).AsTask();
        await stream.Started.Task; cancellation.Cancel(); await Reject<OperationCanceledException>(() => first);
        stream.Release.TrySetResult(); await Reject<InvalidDataException>(() => writer.WriteDeliveryAsync(Delivery(Binding())).AsTask());
        Check(stream.Bytes.Length == 0);
    }
    private static async Task ConcurrentWrite()
    {
        RuntimeDeliveryBinding binding = Binding(); using var stream = new ControlledWriteStream(); var writer = new RuntimeDeliveryWireWriter(stream, Nonce);
        Task first = writer.WriteDeliveryAsync(Delivery(binding, 90)).AsTask(); await stream.Started.Task;
        await Reject<InvalidOperationException>(() => writer.WriteDeliveryAsync(Delivery(binding, 7)).AsTask());
        stream.Release.TrySetResult(); await first; await writer.WriteDeliveryAsync(Delivery(binding, 2));
        using var input = new MemoryStream(stream.Bytes); var reader = new RuntimeDeliveryWireReader(input, Nonce);
        Check((await reader.ReadAsync(binding)).WireSequence == 1 && (await reader.ReadAsync(binding)).WireSequence == 2);
    }
    private static async Task PayloadBounds()
    {
        RuntimeDeliveryBinding binding = Binding(); byte[] frame = await Frame(Delivery(binding, body: new string('x', 32768)));
        using var input = new MemoryStream(frame);
        Check(((RuntimeDeliveryWireDelivery)await new RuntimeDeliveryWireReader(input, Nonce).ReadAsync(binding)).Delivery.Payload.Length == 32768);
        using var output = new MemoryStream(); var writer = new RuntimeDeliveryWireWriter(output, Nonce);
        await Reject<InvalidDataException>(() => writer.WriteDeliveryAsync(Delivery(binding, body: new string('x', 32769))).AsTask());
        Check(output.Length == 0);
        using var worstCase = new MemoryStream();
        await new RuntimeDeliveryWireWriter(worstCase, Nonce).WriteDeliveryAsync(Delivery(binding with { AccountKey = new string('"', 256) }));
        Check(BinaryPrimitives.ReadUInt32LittleEndian(worstCase.ToArray().AsSpan(8, 4)) <= 4096);
    }
    private static async Task CounterAndEnum()
    {
        RuntimeDeliveryBinding binding = Binding(); RuntimeDeliveryWrite delivery = Delivery(binding);
        foreach (RuntimeDeliveryWrite invalid in new[] { delivery with { DeliveryId = 0 }, delivery with { Descriptor = delivery.Descriptor with { FrozenRevision = -1 } },
            delivery with { Descriptor = delivery.Descriptor with { Bytes = -1 } }, delivery with { Binding = binding with { AccountEpoch = -1 } } })
        { using var output = new MemoryStream(); await Reject<InvalidDataException>(() => new RuntimeDeliveryWireWriter(output, Nonce).WriteDeliveryAsync(invalid).AsTask()); Check(output.Length == 0); }
        foreach (long next in new long[] { 0, 6, 8 })
        { using var output = new MemoryStream(); await Reject<InvalidDataException>(() => new RuntimeDeliveryWireWriter(output, Nonce).WriteFenceAsync(new(1, binding, 0, next)).AsTask()); }
        using (var output = new MemoryStream())
            await Reject<InvalidDataException>(() => new RuntimeDeliveryWireWriter(output, Nonce).WriteFenceAsync(new(1, binding with { ChannelGeneration = long.MaxValue }, 0, 0)).AsTask());
        byte[] post = await PostFrame(new(13, binding, delivery.Descriptor.Sha256, RuntimeNativePostResult.Posted));
        foreach (string replacement in new[] { "\"retired\"", "\"Posted\"", "1", "true" })
            await RejectFrame(ReplaceMetadata(post, Metadata(post).Replace("\"posted\"", replacement, StringComparison.Ordinal)), binding);
        await RejectFrame(ReplaceMetadata(post, Metadata(post).Replace("\"deliveryId\":13", "\"deliveryId\":1e0", StringComparison.Ordinal)), binding);
    }
    private static async Task UnknownPostAck()
    {
        var coordinator = new RuntimeDeliveryCoordinator(); RuntimeDeliveryBinding binding = Binding(); var driver = new CapturingDriver(); var lifetime = new Lifetime(binding);
        var host = coordinator.RegisterHostEndpoint(binding, driver, lifetime);
        RuntimeDeliveryWrite original = Delivery(binding); var reservation = coordinator.TryReserve(host.Endpoint, original.Descriptor);
        Check(reservation.Ticket is not null && coordinator.QueueValidated(reservation.Ticket!, original.Payload));
        RuntimeDeliveryWrite written = await driver.Delivery.Task;
        byte[] frame = await PostFrame(new(written.DeliveryId + 1, binding, written.Descriptor.Sha256, RuntimeNativePostResult.Posted));
        using var stream = new MemoryStream(frame); var reader = new RuntimeDeliveryWireReader(stream, Nonce);
        var raw = (RuntimeDeliveryWirePostReceipt)await reader.ReadAsync(binding);
        Check(!host.Evidence.AcceptNativePost(raw.Receipt) && !coordinator.WaitOutcomeAsync(reservation.Ticket!).IsCompleted);
        await Reject<EndOfStreamException>(async () => _ = await reader.ReadAsync(binding));
        Check(coordinator.StateOf(reservation.Ticket!) == RuntimeDeliveryState.Sending && coordinator.OutstandingCount(host.Endpoint) == 1,
            "Unknown ACK or EOF falsely retired/posted the original ticket.");
        lifetime.Retire.TrySetResult(); Check(await coordinator.WaitOutcomeAsync(reservation.Ticket!) == RuntimeDeliveryState.RetiredUnknown);
    }
    private static async Task UnknownFenceAck()
    {
        var coordinator = new RuntimeDeliveryCoordinator(); RuntimeDeliveryBinding binding = Binding(); var driver = new CapturingDriver(); var lifetime = new Lifetime(binding);
        var host = coordinator.RegisterHostEndpoint(binding, driver, lifetime);
        var operation = await coordinator.BeginFenceAsync(new(RuntimeAuthorityScopeKind.Instance, binding.IdentityAuthorityId, binding.AccountKey, binding.ApplicationBinding, binding.InstanceId));
        RuntimeFenceWrite fence = await driver.Fence.Task;
        using var stream = new MemoryStream();
        await new RuntimeDeliveryWireWriter(stream, Nonce).WriteFenceReceiptAsync(new(fence.FenceId + 1, binding, fence.CutoffDeliveryId, fence.NextChannelGeneration));
        stream.Position = 0; var reader = new RuntimeDeliveryWireReader(stream, Nonce);
        var raw = (RuntimeDeliveryWireFenceReceipt)await reader.ReadAsync(binding);
        Check(!host.Evidence.AcceptNativeFence(raw.Receipt) && !operation.WaitCommitPermitAsync().IsCompleted);
        await Reject<EndOfStreamException>(async () => _ = await reader.ReadAsync(binding));
        Check(!operation.WaitCommitPermitAsync().IsCompleted && !host.Evidence.RetirementObserved.IsCompleted);
        lifetime.Retire.TrySetResult(); (await operation.WaitCommitPermitAsync()).Commit(static () => { });
    }
    private sealed class Lifetime(RuntimeDeliveryBinding binding) : IOriginalRuntimeEndpointLifetime
    {
        internal readonly TaskCompletionSource Retire = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private RuntimeEndpointLifetimeRegistration? registration;
        public RuntimeEndpointLifetimeRegistration OriginalLifetime => registration ??= RuntimeEndpointLifetimeRegistration.BindForHost(binding, Retire.Task);
    }
    private sealed class CapturingDriver : IOrderedRuntimeDeliveryDriver
    {
        internal readonly TaskCompletionSource<RuntimeDeliveryWrite> Delivery = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource<RuntimeFenceWrite> Fence = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ValueTask WriteDeliveryAsync(RuntimeDeliveryWrite value) { Delivery.TrySetResult(value); return ValueTask.CompletedTask; }
        public ValueTask WriteFenceAsync(RuntimeFenceWrite value) { Fence.TrySetResult(value); return ValueTask.CompletedTask; }
    }
    private class FragmentedReadStream(byte[] bytes, int maximum, bool stopAfterHeader = false) : Stream
    {
        private readonly MemoryStream input = new(bytes);
        internal int ReadCalls, BytesRead;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => input.Length; public override long Position { get => input.Position; set => throw new NotSupportedException(); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); ReadCalls++;
            if (stopAfterHeader && BytesRead >= 24) throw new InvalidOperationException("Untrusted header caused a body read.");
            int count = input.Read(buffer.Span[..Math.Min(buffer.Length, maximum)]); BytesRead += count; return ValueTask.FromResult(count);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
    }
    private sealed class ControlledReadStream(byte[] bytes) : FragmentedReadStream(bytes, 3)
    {
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int first = 1;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref first, 0) != 0) { Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            return await base.ReadAsync(buffer, cancellationToken);
        }
    }
    private sealed class ControlledWriteStream : Stream
    {
        private readonly MemoryStream output = new(); private int first = 1;
        internal byte[] Bytes => output.ToArray();
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously), Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => output.Length; public override long Position { get => output.Position; set => throw new NotSupportedException(); }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref first, 0) != 0) { Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); }
            await output.WriteAsync(buffer, cancellationToken);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) output.Dispose(); base.Dispose(disposing); }
    }
}
