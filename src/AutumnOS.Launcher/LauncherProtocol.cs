using System.Buffers.Binary;

namespace AutumnOS.Launcher;

public enum RecallOutcome : byte { Foreground = 1, AttentionRequested = 2, Closing = 3, Timeout = 4, Rejected = 5 }
public readonly record struct RecallResult(RecallOutcome Outcome, long WindowHandle);

// One fixed-size message, one acknowledgement. No paths, command lines or arbitrary payloads.
public static class LauncherProtocol
{
    public const byte Version = 1;
    public const int RequestSize = 24, ResponseSize = 40;
    public static byte[] Request(Guid nonce)
    {
        byte[] bytes = new byte[RequestSize];
        "AOSL"u8.CopyTo(bytes); bytes[4] = Version; bytes[5] = 1;
        nonce.TryWriteBytes(bytes.AsSpan(8));
        return bytes;
    }
    public static bool TryReadRequest(ReadOnlySpan<byte> bytes, out Guid nonce)
    {
        nonce = default;
        if (bytes.Length != RequestSize || !bytes[..4].SequenceEqual("AOSL"u8) || bytes[4] != Version || bytes[5] != 1 || bytes[6] != 0 || bytes[7] != 0) return false;
        nonce = new Guid(bytes[8..]);
        return nonce != Guid.Empty;
    }
    public static byte[] Response(Guid nonce, int pid, int session, RecallResult result)
    {
        byte[] bytes = new byte[ResponseSize];
        "AOSA"u8.CopyTo(bytes); bytes[4] = Version; bytes[5] = (byte)result.Outcome;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), pid);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(12), session);
        nonce.TryWriteBytes(bytes.AsSpan(16));
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(32), result.WindowHandle);
        return bytes;
    }
    public static bool TryReadResponse(ReadOnlySpan<byte> bytes, Guid nonce, int pid, int session, out RecallResult result)
    {
        result = default;
        if (nonce == Guid.Empty || bytes.Length != ResponseSize || !bytes[..4].SequenceEqual("AOSA"u8) || bytes[4] != Version || bytes[6] != 0 || bytes[7] != 0 ||
            bytes[5] < 1 || bytes[5] > 5 || BinaryPrimitives.ReadInt32LittleEndian(bytes[8..]) != pid ||
            BinaryPrimitives.ReadInt32LittleEndian(bytes[12..]) != session || new Guid(bytes.Slice(16, 16)) != nonce) return false;
        result = new((RecallOutcome)bytes[5], BinaryPrimitives.ReadInt64LittleEndian(bytes[32..]));
        return true;
    }
}
