using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutumnOS.NetOnlyStationProbe;

internal sealed record NetMessage(int schema_version, string nonce, string kind, string? station_name = null,
    string? failure_stage = null, int? native_error = null, bool? cleanup_confirmed = null, string? descriptor_sha256 = null,
    bool? original_station_unchanged = null, bool? create_attempted = null, bool? creation_succeeded = null,
    bool? exact_name_verified = null, bool? noninteractive_verified = null, bool? original_handle_closed = null, int? close_native_error = null);

// Only fixed scalar metadata is transported. No credential, SDDL, arbitrary
// command, executable path, or native operation is represented by this API.
internal static class Protocol
{
    internal const int MaxBytes = 4096;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    { "schema_version", "nonce", "kind", "station_name", "failure_stage", "native_error", "cleanup_confirmed", "descriptor_sha256",
      "original_station_unchanged", "create_attempted", "creation_succeeded", "exact_name_verified", "noninteractive_verified", "original_handle_closed", "close_native_error" };

    internal static async Task WriteAsync(Stream stream, NetMessage message, CancellationToken cancellationToken)
    {
        byte[] payload = Serialize(message), header = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken); await stream.WriteAsync(payload, cancellationToken); await stream.FlushAsync(cancellationToken);
    }
    internal static async Task<NetMessage> ReadAsync(Stream stream, string expectedNonce, string expectedKind, CancellationToken cancellationToken)
    {
        byte[] header = new byte[4]; await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is < 1 or > MaxBytes) throw new InvalidDataException("NET_PROTOCOL_LENGTH_REJECTED");
        byte[] payload = new byte[length]; await stream.ReadExactlyAsync(payload, cancellationToken);
        return Parse(payload, expectedNonce, expectedKind);
    }
    internal static byte[] Serialize(NetMessage message)
    {
        Validate(message, message.nonce, message.kind);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(message);
        if (payload.Length > MaxBytes) throw new InvalidDataException("NET_PROTOCOL_LENGTH_REJECTED");
        return payload;
    }
    internal static NetMessage Parse(byte[] payload, string expectedNonce, string expectedKind)
    {
        if (payload.Length is < 1 or > MaxBytes) throw new InvalidDataException("NET_PROTOCOL_LENGTH_REJECTED");
        string text = StrictUtf8.GetString(payload);
        using JsonDocument json = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 2 });
        if (json.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("NET_PROTOCOL_OBJECT_REQUIRED");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in json.RootElement.EnumerateObject())
        {
            if (!Fields.Contains(property.Name) || !names.Add(property.Name)) throw new InvalidDataException("NET_PROTOCOL_UNKNOWN_OR_DUPLICATE_KEY");
            if (property.Value.ValueKind is JsonValueKind.Array or JsonValueKind.Object) throw new InvalidDataException("NET_PROTOCOL_SCALAR_FIELDS_ONLY");
        }
        NetMessage message = JsonSerializer.Deserialize<NetMessage>(text) ?? throw new InvalidDataException("NET_PROTOCOL_NULL");
        Validate(message, expectedNonce, expectedKind); return message;
    }
    private static void Validate(NetMessage message, string expectedNonce, string expectedKind)
    {
        if (message.schema_version != 1 || message.nonce is null || !Regex.IsMatch(message.nonce, "\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant) ||
            message.nonce != expectedNonce || message.kind != expectedKind || message.kind is not ("hello" or "ready" or "create" or "result" or "ack"))
            throw new InvalidDataException("NET_PROTOCOL_ENVELOPE_OR_EXPECTED_STATE_REJECTED");
        bool anyFacts = message.station_name is not null || message.failure_stage is not null || message.native_error is not null ||
            message.cleanup_confirmed is not null || message.descriptor_sha256 is not null || message.original_station_unchanged is not null ||
            message.create_attempted is not null || message.creation_succeeded is not null || message.exact_name_verified is not null ||
            message.noninteractive_verified is not null || message.original_handle_closed is not null || message.close_native_error is not null;
        if (message.kind != "result")
        { if (anyFacts) throw new InvalidDataException("NET_PROTOCOL_HANDSHAKE_MUST_NOT_CARRY_FACTS"); return; }
        if (message.create_attempted is null || message.creation_succeeded is null || message.exact_name_verified is null ||
            message.noninteractive_verified is null || message.original_handle_closed is null || message.original_station_unchanged is null || message.cleanup_confirmed is null)
            throw new InvalidDataException("NET_PROTOCOL_RESULT_FACTS_REQUIRED");
        if (message.station_name is not null)
        {
            if (message.station_name.Length is < 1 or > 256 || message.station_name.IndexOfAny(['\0', '\\', '/']) >= 0)
                throw new InvalidDataException("NET_PROTOCOL_BOUNDED_OS_STATION_NAME_REQUIRED");
            _ = StrictUtf8.GetByteCount(message.station_name);
        }
        if (message.creation_succeeded == true && (message.create_attempted != true || message.station_name is null))
            throw new InvalidDataException("NET_PROTOCOL_CREATED_STATION_NAME_REQUIRED");
        if (message.failure_stage is not null && !Regex.IsMatch(message.failure_stage, "\\A[A-Za-z][A-Za-z0-9_]{0,63}\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("NET_PROTOCOL_FIXED_FAILURE_STAGE_REQUIRED");
        if (message.native_error is < 0 || message.close_native_error is < 0 ||
            message.descriptor_sha256 is not null && !Regex.IsMatch(message.descriptor_sha256, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("NET_PROTOCOL_ERROR_OR_DESCRIPTOR_HASH_REJECTED");
    }
}
