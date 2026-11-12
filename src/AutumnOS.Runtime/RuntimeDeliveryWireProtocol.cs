using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutumnOS.Runtime;

// Wire values are untrusted DTOs. Only the original authenticated OS owner may
// submit receipts to its evidence port. There is deliberately no retire message.
internal enum RuntimeDeliveryWireKind : byte { Delivery = 1, Fence = 2, PostReceipt = 3, FenceReceipt = 4 }
internal abstract record RuntimeDeliveryWireMessage(RuntimeDeliveryWireKind Kind, long WireSequence);
internal sealed record RuntimeDeliveryWireDelivery(long WireSequence, RuntimeDeliveryWrite Delivery)
    : RuntimeDeliveryWireMessage(RuntimeDeliveryWireKind.Delivery, WireSequence);
internal sealed record RuntimeDeliveryWireFence(long WireSequence, RuntimeFenceWrite Fence)
    : RuntimeDeliveryWireMessage(RuntimeDeliveryWireKind.Fence, WireSequence);
internal sealed record RuntimeDeliveryWirePostReceipt(long WireSequence, RuntimeNativePostReceipt Receipt)
    : RuntimeDeliveryWireMessage(RuntimeDeliveryWireKind.PostReceipt, WireSequence);
internal sealed record RuntimeDeliveryWireFenceReceipt(long WireSequence, RuntimeNativeFenceReceipt Receipt)
    : RuntimeDeliveryWireMessage(RuntimeDeliveryWireKind.FenceReceipt, WireSequence);

/// <summary>Version 1 is fixed at 32 KiB, independently of larger trusted coordinator configurations.</summary>
internal static class RuntimeDeliveryWireProtocol
{
    internal const int HeaderBytes = 24, MaximumMetadataBytes = 4096, MaximumPayloadBytes = 32768;
    internal const uint Magic = 0x31444c41;
    internal const ushort Version = 1;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly string[] Common = ["nonce", "authorityId", "account", "accountEpoch", "application",
        "instanceId", "instanceEpoch", "documentId", "documentGeneration", "channelGeneration"];
    internal static string CreateNonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
    internal static InvalidDataException Invalid(string code) => new(code);
    internal static void ValidateNonce(string nonce)
    { if (!HashShape(nonce)) throw Invalid("WIRE_NONCE_INVALID"); }
    private static bool HashShape(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static void ValidateBinding(RuntimeDeliveryBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        if (binding.IdentityAuthorityId == Guid.Empty || binding.InstanceId == Guid.Empty || binding.DocumentId == Guid.Empty ||
            binding.AccountKey is not { Length: > 0 and <= 256 } || binding.AccountKey.Any(char.IsControl) ||
            !HashShape(binding.ApplicationBinding) || binding.AccountEpoch < 0 || binding.InstanceEpoch < 0 ||
            binding.DocumentGeneration < 1 || binding.ChannelGeneration < 1) throw Invalid("WIRE_BINDING_INVALID");
        try { if (Utf8.GetByteCount(binding.AccountKey) > 1024) throw Invalid("WIRE_ACCOUNT_TOO_LONG"); }
        catch (EncoderFallbackException) { throw Invalid("WIRE_UTF8_INVALID"); }
    }
    internal static void ValidatePayload(ReadOnlySpan<byte> payload, string hash)
    {
        if (!HashShape(hash) || Convert.ToHexStringLower(SHA256.HashData(payload)) != hash) throw Invalid("WIRE_PAYLOAD_HASH_INVALID");
        try { _ = Utf8.GetCharCount(payload); }
        catch (DecoderFallbackException) { throw Invalid("WIRE_UTF8_INVALID"); }
    }
    private static void ValidateFence(RuntimeDeliveryBinding binding, long id, long cutoff, long next)
    {
        if (id < 1 || cutoff < 0 || binding.ChannelGeneration == long.MaxValue || next != binding.ChannelGeneration + 1)
            throw Invalid("WIRE_FENCE_INVALID");
    }
    internal static (byte[] Metadata, byte[] Payload) Encode(RuntimeDeliveryWireKind kind, string nonce, object value)
    {
        ValidateNonce(nonce);
        RuntimeDeliveryBinding binding;
        byte[] payload = [];
        long id, cutoff = 0, next = 0, revision = 0;
        string hash = ""; RuntimeNativePostResult result = default;
        switch (value)
        {
            case RuntimeDeliveryWrite delivery when kind == RuntimeDeliveryWireKind.Delivery:
                ArgumentNullException.ThrowIfNull(delivery.Descriptor);
                binding = delivery.Binding; id = delivery.DeliveryId; hash = delivery.Descriptor.Sha256; revision = delivery.Descriptor.FrozenRevision;
                if (id < 1 || revision < 0 || delivery.Payload.Length > MaximumPayloadBytes ||
                    delivery.Descriptor.Bytes != delivery.Payload.Length) throw Invalid("WIRE_DELIVERY_INVALID");
                // Never retain the driver's memory after returning; copy only after the bound check.
                payload = delivery.Payload.ToArray(); ValidatePayload(payload, hash); break;
            case RuntimeFenceWrite fence when kind == RuntimeDeliveryWireKind.Fence:
                binding = fence.Binding; id = fence.FenceId; cutoff = fence.CutoffDeliveryId; next = fence.NextChannelGeneration; break;
            case RuntimeNativePostReceipt receipt when kind == RuntimeDeliveryWireKind.PostReceipt:
                binding = receipt.Binding; id = receipt.DeliveryId; hash = receipt.PayloadSha256; result = receipt.Result;
                if (id < 1 || !HashShape(hash) || !Enum.IsDefined(result)) throw Invalid("WIRE_POST_RECEIPT_INVALID");
                break;
            case RuntimeNativeFenceReceipt receipt when kind == RuntimeDeliveryWireKind.FenceReceipt:
                binding = receipt.Binding; id = receipt.FenceId; cutoff = receipt.CutoffDeliveryId; next = receipt.NextChannelGeneration; break;
            default: throw Invalid("WIRE_KIND_INVALID");
        }
        ValidateBinding(binding);
        if (kind is RuntimeDeliveryWireKind.Fence or RuntimeDeliveryWireKind.FenceReceipt) ValidateFence(binding, id, cutoff, next);
        var buffer = new ArrayBufferWriter<byte>(MaximumMetadataBytes);
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject(); json.WriteString("nonce", nonce);
            json.WriteString("authorityId", binding.IdentityAuthorityId.ToString("D")); json.WriteString("account", binding.AccountKey);
            json.WriteNumber("accountEpoch", binding.AccountEpoch); json.WriteString("application", binding.ApplicationBinding);
            json.WriteString("instanceId", binding.InstanceId.ToString("D")); json.WriteNumber("instanceEpoch", binding.InstanceEpoch);
            json.WriteString("documentId", binding.DocumentId.ToString("D")); json.WriteNumber("documentGeneration", binding.DocumentGeneration);
            json.WriteNumber("channelGeneration", binding.ChannelGeneration);
            if (kind is RuntimeDeliveryWireKind.Delivery or RuntimeDeliveryWireKind.PostReceipt)
            {
                json.WriteNumber("deliveryId", id);
                if (kind == RuntimeDeliveryWireKind.Delivery) json.WriteNumber("bytes", payload.Length);
                json.WriteString("sha256", hash);
                if (kind == RuntimeDeliveryWireKind.Delivery) json.WriteNumber("frozenRevision", revision);
                else json.WriteString("result", result == RuntimeNativePostResult.Posted ? "posted" : "rejectedBeforePost");
            }
            else { json.WriteNumber("fenceId", id); json.WriteNumber("cutoff", cutoff); json.WriteNumber("nextChannelGeneration", next); }
            json.WriteEndObject(); json.Flush();
        }
        if (buffer.WrittenCount is < 1 or > MaximumMetadataBytes) throw Invalid("WIRE_METADATA_LENGTH_INVALID");
        return (buffer.WrittenSpan.ToArray(), payload);
    }
    internal sealed record Metadata(RuntimeDeliveryBinding Binding, long Id, long Cutoff, long Next,
        string Hash, long Revision, RuntimeNativePostResult Result);
    internal static Metadata DecodeMetadata(byte[] metadata, RuntimeDeliveryWireKind kind, string nonce,
        RuntimeDeliveryBinding expectedBinding, int payloadBytes)
    {
        try
        {
            _ = Utf8.GetCharCount(metadata);
            using JsonDocument document = JsonDocument.Parse(metadata, new() { MaxDepth = 2, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw Invalid("WIRE_METADATA_INVALID");
            string[] extra = kind switch
            {
                RuntimeDeliveryWireKind.Delivery => ["deliveryId", "bytes", "sha256", "frozenRevision"],
                RuntimeDeliveryWireKind.PostReceipt => ["deliveryId", "sha256", "result"],
                _ => ["fenceId", "cutoff", "nextChannelGeneration"]
            };
            var fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                if ((!Common.Contains(property.Name, StringComparer.Ordinal) && !extra.Contains(property.Name, StringComparer.Ordinal)) ||
                    !fields.TryAdd(property.Name, property.Value) || property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))
                    throw Invalid("WIRE_FIELDS_INVALID");
            }
            if (fields.Count != Common.Length + extra.Length) throw Invalid("WIRE_FIELDS_INVALID");
            string Text(string name) => fields[name].ValueKind == JsonValueKind.String ? fields[name].GetString()! : throw Invalid("WIRE_FIELD_TYPE_INVALID");
            long Number(string name) => fields[name].ValueKind == JsonValueKind.Number && fields[name].TryGetInt64(out long value)
                ? value : throw Invalid("WIRE_FIELD_TYPE_INVALID");
            Guid Identifier(string name)
            {
                string text = Text(name);
                return Guid.TryParseExact(text, "D", out Guid id) && text == id.ToString("D") ? id : throw Invalid("WIRE_GUID_INVALID");
            }
            string receivedNonce = Text("nonce"); ValidateNonce(receivedNonce);
            if (!CryptographicOperations.FixedTimeEquals(Utf8.GetBytes(receivedNonce), Utf8.GetBytes(nonce))) throw Invalid("WIRE_NONCE_MISMATCH");
            RuntimeDeliveryBinding binding = new(Identifier("authorityId"), Text("account"), Number("accountEpoch"), Text("application"),
                Identifier("instanceId"), Number("instanceEpoch"), Identifier("documentId"), Number("documentGeneration"), Number("channelGeneration"));
            ValidateBinding(binding);
            if (binding != expectedBinding) throw Invalid("WIRE_BINDING_MISMATCH");
            if (kind is RuntimeDeliveryWireKind.Fence or RuntimeDeliveryWireKind.FenceReceipt)
            {
                long id = Number("fenceId"), cutoff = Number("cutoff"), next = Number("nextChannelGeneration");
                ValidateFence(binding, id, cutoff, next); return new(binding, id, cutoff, next, "", 0, default);
            }
            long deliveryId = Number("deliveryId"); string hash = Text("sha256");
            if (deliveryId < 1 || !HashShape(hash)) throw Invalid("WIRE_DELIVERY_INVALID");
            if (kind == RuntimeDeliveryWireKind.Delivery)
            {
                long revision = Number("frozenRevision");
                if (Number("bytes") != payloadBytes || revision < 0) throw Invalid("WIRE_DELIVERY_INVALID");
                return new(binding, deliveryId, 0, 0, hash, revision, default);
            }
            RuntimeNativePostResult result = Text("result") switch
            { "posted" => RuntimeNativePostResult.Posted, "rejectedBeforePost" => RuntimeNativePostResult.RejectedBeforePost, _ => throw Invalid("WIRE_POST_RECEIPT_INVALID") };
            return new(binding, deliveryId, 0, 0, hash, 0, result);
        }
        catch (JsonException) { throw Invalid("WIRE_JSON_INVALID"); }
        catch (DecoderFallbackException) { throw Invalid("WIRE_UTF8_INVALID"); }
        catch (EncoderFallbackException) { throw Invalid("WIRE_UTF8_INVALID"); }
        catch (InvalidOperationException) { throw Invalid("WIRE_JSON_INVALID"); }
    }
    internal static RuntimeDeliveryWireMessage CreateMessage(RuntimeDeliveryWireKind kind, long sequence, Metadata metadata, byte[] payload)
    {
        if (kind == RuntimeDeliveryWireKind.Delivery) ValidatePayload(payload, metadata.Hash);
        return kind switch
        {
            RuntimeDeliveryWireKind.Delivery => new RuntimeDeliveryWireDelivery(sequence, new(metadata.Id, metadata.Binding,
                new(payload.Length, metadata.Hash, metadata.Revision), payload)),
            RuntimeDeliveryWireKind.Fence => new RuntimeDeliveryWireFence(sequence, new(metadata.Id, metadata.Binding, metadata.Cutoff, metadata.Next)),
            RuntimeDeliveryWireKind.PostReceipt => new RuntimeDeliveryWirePostReceipt(sequence, new(metadata.Id, metadata.Binding, metadata.Hash, metadata.Result)),
            RuntimeDeliveryWireKind.FenceReceipt => new RuntimeDeliveryWireFenceReceipt(sequence, new(metadata.Id, metadata.Binding, metadata.Cutoff, metadata.Next)),
            _ => throw Invalid("WIRE_KIND_INVALID")
        };
    }
}

/// <summary>One read direction, one reader. Does not own or authenticate the stream.</summary>
internal sealed class RuntimeDeliveryWireReader
{
    private readonly Stream stream;
    private readonly string nonce;
    private long nextSequence = 1;
    private int busy, failed;
    internal RuntimeDeliveryWireReader(Stream stream, string expectedNonce)
    {
        ArgumentNullException.ThrowIfNull(stream); RuntimeDeliveryWireProtocol.ValidateNonce(expectedNonce);
        if (!stream.CanRead) throw new ArgumentException("WIRE_READ_STREAM_REQUIRED", nameof(stream));
        this.stream = stream; nonce = expectedNonce;
    }
    internal async ValueTask<RuntimeDeliveryWireMessage> ReadAsync(RuntimeDeliveryBinding expectedBinding, CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new InvalidOperationException("WIRE_CONCURRENT_READ");
        try
        {
            if (Volatile.Read(ref failed) != 0 || nextSequence == 0) throw RuntimeDeliveryWireProtocol.Invalid("WIRE_DIRECTION_FAILED");
            RuntimeDeliveryWireProtocol.ValidateBinding(expectedBinding); cancellationToken.ThrowIfCancellationRequested();
            byte[] header = new byte[RuntimeDeliveryWireProtocol.HeaderBytes];
            await ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            RuntimeDeliveryWireKind kind = (RuntimeDeliveryWireKind)header[6];
            uint metadataLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
            uint payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
            long sequence = BinaryPrimitives.ReadInt64LittleEndian(header.AsSpan(16, 8));
            // All attacker-controlled sizes and the sequence are checked before body allocation/read.
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != RuntimeDeliveryWireProtocol.Magic ||
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(4, 2)) != RuntimeDeliveryWireProtocol.Version || header[7] != 0 ||
                !Enum.IsDefined(kind) || metadataLength is < 1 or > RuntimeDeliveryWireProtocol.MaximumMetadataBytes ||
                payloadLength > RuntimeDeliveryWireProtocol.MaximumPayloadBytes ||
                kind != RuntimeDeliveryWireKind.Delivery && payloadLength != 0 || sequence != nextSequence)
                throw RuntimeDeliveryWireProtocol.Invalid("WIRE_HEADER_INVALID");
            byte[] metadata = new byte[(int)metadataLength];
            await ReadExactlyAsync(metadata, cancellationToken).ConfigureAwait(false);
            RuntimeDeliveryWireProtocol.Metadata decoded = RuntimeDeliveryWireProtocol.DecodeMetadata(metadata, kind, nonce, expectedBinding, (int)payloadLength);
            byte[] payload = payloadLength == 0 ? [] : new byte[(int)payloadLength];
            if (payload.Length != 0) await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
            RuntimeDeliveryWireMessage message = RuntimeDeliveryWireProtocol.CreateMessage(kind, sequence, decoded, payload);
            cancellationToken.ThrowIfCancellationRequested();
            nextSequence = sequence == long.MaxValue ? 0 : sequence + 1;
            return message;
        }
        catch { Volatile.Write(ref failed, 1); throw; }
        finally { Volatile.Write(ref busy, 0); }
    }
    private async ValueTask ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int offset = 0;
        while (offset != buffer.Length)
        {
            int count = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new EndOfStreamException("WIRE_FRAME_INCOMPLETE");
            offset += count;
        }
    }
}

/// <summary>One write direction. Full frame write is never a native Post receipt.</summary>
internal sealed class RuntimeDeliveryWireWriter
{
    private readonly Stream stream;
    private readonly string nonce;
    private long nextSequence = 1;
    private int busy, failed;
    internal RuntimeDeliveryWireWriter(Stream stream, string nonce)
    {
        ArgumentNullException.ThrowIfNull(stream); RuntimeDeliveryWireProtocol.ValidateNonce(nonce);
        if (!stream.CanWrite) throw new ArgumentException("WIRE_WRITE_STREAM_REQUIRED", nameof(stream));
        this.stream = stream; this.nonce = nonce;
    }
    internal ValueTask WriteDeliveryAsync(RuntimeDeliveryWrite value, CancellationToken cancellationToken = default)
        => WriteAsync(RuntimeDeliveryWireKind.Delivery, value, cancellationToken);
    internal ValueTask WriteFenceAsync(RuntimeFenceWrite value, CancellationToken cancellationToken = default)
        => WriteAsync(RuntimeDeliveryWireKind.Fence, value, cancellationToken);
    internal ValueTask WritePostReceiptAsync(RuntimeNativePostReceipt value, CancellationToken cancellationToken = default)
        => WriteAsync(RuntimeDeliveryWireKind.PostReceipt, value, cancellationToken);
    internal ValueTask WriteFenceReceiptAsync(RuntimeNativeFenceReceipt value, CancellationToken cancellationToken = default)
        => WriteAsync(RuntimeDeliveryWireKind.FenceReceipt, value, cancellationToken);
    private async ValueTask WriteAsync(RuntimeDeliveryWireKind kind, object value, CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) throw new InvalidOperationException("WIRE_CONCURRENT_WRITE");
        try
        {
            if (Volatile.Read(ref failed) != 0 || nextSequence == 0) throw RuntimeDeliveryWireProtocol.Invalid("WIRE_DIRECTION_FAILED");
            cancellationToken.ThrowIfCancellationRequested();
            var encoded = RuntimeDeliveryWireProtocol.Encode(kind, nonce, value);
            byte[] header = new byte[RuntimeDeliveryWireProtocol.HeaderBytes];
            BinaryPrimitives.WriteUInt32LittleEndian(header, RuntimeDeliveryWireProtocol.Magic);
            BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), RuntimeDeliveryWireProtocol.Version);
            header[6] = (byte)kind;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), (uint)encoded.Metadata.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12, 4), (uint)encoded.Payload.Length);
            BinaryPrimitives.WriteInt64LittleEndian(header.AsSpan(16, 8), nextSequence);
            await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(encoded.Metadata, cancellationToken).ConfigureAwait(false);
            if (encoded.Payload.Length != 0) await stream.WriteAsync(encoded.Payload, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            nextSequence = nextSequence == long.MaxValue ? 0 : nextSequence + 1;
        }
        catch { Volatile.Write(ref failed, 1); throw; }
        finally { Volatile.Write(ref busy, 0); }
    }
}
