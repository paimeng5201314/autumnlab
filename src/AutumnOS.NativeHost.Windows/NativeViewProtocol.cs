using System.Buffers.Binary;

namespace AutumnOS.NativeHost.Windows;

public enum NativeViewKind : byte
{
    PrepareDocument = 1, ReleaseDocument, Poll, Pointer, Key, Composition, Delivery, Close, ResetInput, Resize,
    Proof = 32, DocumentReady, SdkRequest, Frame, Idle, Accepted, Receipt, Closed, Failed, InputRejected
}

public sealed record NativeViewPacket(long Sequence, NativeViewKind Kind, byte[] Payload);

// Only transports owned by the authenticated original station/browser may carry these packets.
// The owner of each direction checks exact monotonically increasing sequence and request/reply pairing.
public static class NativeViewProtocol
{
    public const int MaximumFrameBytes = 1024 * 1024;
    private const int HeaderBytes = 20;
    internal static void Validate(long sequence, NativeViewKind kind, int size)
    {
        int limit = kind switch
        {
            NativeViewKind.Poll or NativeViewKind.ReleaseDocument or NativeViewKind.Close or NativeViewKind.ResetInput or NativeViewKind.Idle
                or NativeViewKind.DocumentReady or NativeViewKind.Accepted or NativeViewKind.Closed or NativeViewKind.InputRejected => 0,
            NativeViewKind.Frame => MaximumFrameBytes,
            NativeViewKind.Resize => 8,
            NativeViewKind.Delivery or NativeViewKind.Receipt => 4096 + 32768 + 24,
            NativeViewKind.Proof => 32768,
            NativeViewKind.SdkRequest => 32768,
            NativeViewKind.PrepareDocument or NativeViewKind.Key or NativeViewKind.Pointer or NativeViewKind.Composition => 4096,
            NativeViewKind.Failed => 128,
            _ => -1
        };
        if (sequence < 1 || size < 0 || size > limit || (limit > 0 && size == 0) || kind == NativeViewKind.Resize && size != 8)
            throw new InvalidDataException("NATIVE_VIEW_PACKET_INVALID");
    }

    public static async Task<NativeViewPacket> ReadAsync(Stream source, CancellationToken cancellationToken)
    {
        byte[] header = new byte[HeaderBytes];
        await source.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("AVP1"u8) || header[5] != 0 || header[6] != 0 || header[7] != 0)
            throw new InvalidDataException("NATIVE_VIEW_HEADER_INVALID");
        var kind = (NativeViewKind)header[4];
        long sequence = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(8));
        int length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
        Validate(sequence, kind, length);
        byte[] payload = new byte[length];
        await source.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        return new(sequence, kind, payload);
    }

    public static async Task WriteAsync(Stream destination, NativeViewPacket packet, CancellationToken cancellationToken)
    {
        Validate(packet.Sequence, packet.Kind, packet.Payload.Length);
        byte[] header = new byte[HeaderBytes];
        "AVP1"u8.CopyTo(header); header[4] = (byte)packet.Kind;
        BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(8), packet.Sequence);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), packet.Payload.Length);
        await destination.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await destination.WriteAsync(packet.Payload, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
