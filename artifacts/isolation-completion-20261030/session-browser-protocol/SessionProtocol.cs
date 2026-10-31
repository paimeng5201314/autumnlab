using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AutumnOS.SessionBrowserProtocol;

internal sealed record SessionHeader(int schema_version, string nonce, long epoch, string kind)
{
    public long request_id { get; init; }
    public long frame_id { get; init; }
    public string? station_name { get; init; }
    public string? desktop_name { get; init; }
    public string? package_sid_sha256 { get; init; }
    public string? report_sha256 { get; init; }
    public ulong? job_handle { get; init; }
    public ulong? host_process_handle { get; init; }
    public uint? host_pid { get; init; }
    public ulong? host_creation_filetime { get; init; }
    public uint? browser_pid { get; init; }
    public ulong? creation_filetime { get; init; }
    public int? width { get; init; }
    public int? height { get; init; }
    public string? command { get; init; }
    public int? x { get; init; }
    public int? y { get; init; }
    public string? button { get; init; }
    public string? key { get; init; }
    public int? modifiers { get; init; }
    public string? text { get; init; }
    public int? wheel_x { get; init; }
    public int? wheel_y { get; init; }
    public int? selection_start { get; init; }
    public int? selection_end { get; init; }
    public int? replacement_start { get; init; }
    public int? replacement_end { get; init; }
    public bool? success { get; init; }
    public bool? cleanup_confirmed { get; init; }
    public bool? normal_browser_exit { get; init; }
    public bool? initialization_succeeded { get; init; }
    public bool? page_succeeded { get; init; }
    public bool? operation_succeeded { get; init; }
    public bool? controller_disposed { get; init; }
    public int? final_environment_process_count { get; init; }
    public string? error_stage { get; init; }
    public int? native_error { get; init; }
    public int? wasm_result { get; init; }
    public int? raf_count { get; init; }
    public int? dom_composition_start { get; init; }
    public int? dom_composition_update { get; init; }
    public int? dom_composition_end { get; init; }
    public int? dom_input_count { get; init; }
    public int? dom_trusted_composition_start { get; init; }
    public int? dom_trusted_composition_update { get; init; }
    public int? dom_trusted_composition_end { get; init; }
    public int? dom_trusted_input_count { get; init; }
    public int? dom_text_length { get; init; }
    public string? dom_text_sha256 { get; init; }
    public string? dom_phase_digest_sha256 { get; init; }
    public bool? dom_expected_hello { get; init; }
    public bool? dom_composing { get; init; }
    public int? dom_composition_update_data_length { get; init; }
    public string? dom_composition_update_data_sha256 { get; init; }
    public int? dom_composition_end_data_length { get; init; }
    public string? dom_composition_end_data_sha256 { get; init; }
    public bool? dom_owned_text_focused { get; init; }
    public bool? dom_document_has_focus { get; init; }
    public int? dom_selection_start { get; init; }
    public int? dom_selection_end { get; init; }
    public int? dom_pointer_down_count { get; init; }
    public int? dom_key_down_count { get; init; }
    public string? document_instance_sha256 { get; init; }
    public int? navigation_start_count { get; init; }
    public bool? before_dom_owned_text_focused { get; init; }
    public bool? before_dom_document_has_focus { get; init; }
    public int? before_dom_selection_start { get; init; }
    public int? before_dom_selection_end { get; init; }
    public int? before_dom_text_length { get; init; }
    public string? before_dom_text_sha256 { get; init; }
    public int? before_dom_composition_start { get; init; }
    public int? before_dom_composition_update { get; init; }
    public int? before_dom_composition_end { get; init; }
    public bool? frame_pixel_sentinel { get; init; }
    public bool? different_frame_observed { get; init; }
    public int? captured_frame_count { get; init; }
}
internal sealed record SessionPacket(SessionHeader header, byte[] data);

internal static class SessionProtocol
{
    internal const int HeaderLimit = 16384, PngLimit = 2097152, Width = 800, Height = 600;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly JsonSerializerOptions Json = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull, MaxDepth = 2 };
    private static readonly HashSet<string> Envelope = new(StringComparer.Ordinal) { "schema_version", "nonce", "epoch", "kind", "request_id", "frame_id" };
    private static readonly HashSet<string> AllFields = JsonSerializer.SerializeToElement(new SessionHeader(1, "", 1, ""),
        new JsonSerializerOptions()).EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
    private static readonly HashSet<string> Keys = new(StringComparer.Ordinal)
    { "Enter", "Backspace", "Tab", "Escape", "Delete", "Home", "End", "PageUp", "PageDown", "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Space", "Control", "Shift", "Alt" };

    internal static async Task WriteAsync(Stream stream, SessionPacket packet, CancellationToken cancellationToken)
    {
        Validate(packet); byte[] header = JsonSerializer.SerializeToUtf8Bytes(packet.header, Json);
        if (header.Length > HeaderLimit) throw new InvalidDataException("SESSION_HEADER_TOO_LARGE");
        byte[] size = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(size, header.Length);
        await stream.WriteAsync(size, cancellationToken); await stream.WriteAsync(header, cancellationToken);
        BinaryPrimitives.WriteInt32LittleEndian(size, packet.data.Length);
        await stream.WriteAsync(size, cancellationToken); if (packet.data.Length != 0) await stream.WriteAsync(packet.data, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
    // EOF throws EndOfStreamException. A fully consumed stale-epoch packet
    // returns null, so it cannot paint or execute input in the current session.
    internal static async Task<SessionPacket?> ReadAsync(Stream stream, string expectedNonce, long expectedEpoch, CancellationToken cancellationToken)
    {
        byte[] size = new byte[4];
        if (await stream.ReadAsync(size.AsMemory(0, 1), cancellationToken) == 0) throw new EndOfStreamException("SESSION_CLEAN_BOUNDARY_EOF");
        await ReadRequired(stream, size.AsMemory(1, 3), cancellationToken);
        int length = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (length is < 2 or > HeaderLimit) throw new InvalidDataException("SESSION_HEADER_LENGTH_REJECTED");
        byte[] bytes = new byte[length]; await ReadRequired(stream, bytes, cancellationToken); SessionHeader header = ParseHeader(bytes);
        if (header.nonce != expectedNonce || expectedEpoch < 1) throw new InvalidDataException("SESSION_NONCE_BINDING_REJECTED");
        if (header.epoch > expectedEpoch) throw new InvalidDataException("SESSION_FUTURE_EPOCH_REJECTED");
        await ReadRequired(stream, size, cancellationToken); int count = BinaryPrimitives.ReadInt32LittleEndian(size);
        if (count is < 0 or > PngLimit || header.kind != "frame" && count != 0) throw new InvalidDataException("SESSION_BINARY_LENGTH_REJECTED");
        byte[] data = new byte[count]; if (count != 0) await ReadRequired(stream, data, cancellationToken);
        if (header.epoch < expectedEpoch) return null;
        var packet = new SessionPacket(header, data); Validate(packet);
        return packet;
    }
    private static async Task ReadRequired(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        try { await stream.ReadExactlyAsync(buffer, cancellationToken); }
        catch (EndOfStreamException) { throw new InvalidDataException("PROTOCOL_TRUNCATED"); }
    }
    internal static SessionHeader ParseHeader(byte[] bytes)
    {
        if (bytes.Length is < 2 or > HeaderLimit) throw new InvalidDataException("SESSION_HEADER_LENGTH_REJECTED");
        using JsonDocument document = JsonDocument.Parse(Utf8.GetString(bytes), new JsonDocumentOptions { MaxDepth = 2 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("SESSION_HEADER_OBJECT_REQUIRED");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in document.RootElement.EnumerateObject())
            if (!AllFields.Contains(property.Name) || !names.Add(property.Name) || property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                throw new InvalidDataException("SESSION_UNKNOWN_DUPLICATE_OR_NESTED_FIELD");
        if (!Envelope.IsSubsetOf(names)) throw new InvalidDataException("SESSION_EXPLICIT_ENVELOPE_REQUIRED");
        SessionHeader header = JsonSerializer.Deserialize<SessionHeader>(bytes, Json) ?? throw new InvalidDataException("SESSION_HEADER_NULL");
        ValidateHeader(header); return header;
    }
    internal static void Validate(SessionPacket packet)
    {
        ValidateHeader(packet.header);
        if (packet.data is null || packet.data.Length > PngLimit) throw new InvalidDataException("SESSION_BINARY_LENGTH_REJECTED");
        if (packet.header.kind == "frame") ValidatePng(packet.data);
        else if (packet.data.Length != 0) throw new InvalidDataException("SESSION_UNEXPECTED_BINARY_DATA");
    }
    internal static void ValidateHeader(SessionHeader h)
    {
        if (h.schema_version != 1 || h.nonce is null || !Regex.IsMatch(h.nonce, "\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant) ||
            h.epoch is < 1 or > 1000000 || h.request_id is < 0 or > 1000000 || h.frame_id is < 0 or > 1000000)
            throw new InvalidDataException("SESSION_ENVELOPE_REJECTED");
        string allowed = h.kind switch
        {
            "hello" or "start" or "ack" => "success,error_stage,native_error",
            "ready" => "success,initialization_succeeded,page_succeeded,browser_pid,error_stage,native_error",
            "job_ready" => "job_handle,host_process_handle,host_pid,host_creation_filetime,success,error_stage,native_error,creation_filetime",
            "job_bound" => "job_handle,success,error_stage,native_error,creation_filetime",
            "owned_ready" => "station_name,desktop_name,package_sid_sha256,success,error_stage,native_error",
            "controller_ready" => "browser_pid,creation_filetime,initialization_succeeded,success,error_stage,native_error",
            "input" => "command,x,y,button,key,modifiers,text,wheel_x,wheel_y,selection_start,selection_end,replacement_start,replacement_end",
            "frame" => "width,height,success",
            "input_result" => "success,page_succeeded,operation_succeeded,error_stage,native_error",
            "host_complete" => "success,cleanup_confirmed,report_sha256,station_name,desktop_name,package_sid_sha256,browser_pid,normal_browser_exit,initialization_succeeded,page_succeeded,operation_succeeded,controller_disposed,final_environment_process_count,error_stage,native_error",
            _ => throw new InvalidDataException("SESSION_KIND_NOT_ALLOWED")
        };
        var permitted = allowed.Split(',').ToHashSet(StringComparer.Ordinal);
        if (h.kind is "frame" or "input_result" or "host_complete")
            permitted.UnionWith(new[] { "wasm_result", "raf_count", "dom_composition_start", "dom_composition_update", "dom_composition_end", "dom_input_count",
                "dom_trusted_composition_start", "dom_trusted_composition_update", "dom_trusted_composition_end", "dom_trusted_input_count",
                "dom_text_length", "dom_text_sha256", "dom_phase_digest_sha256", "dom_expected_hello", "dom_composing",
                "dom_composition_update_data_length", "dom_composition_update_data_sha256", "dom_composition_end_data_length", "dom_composition_end_data_sha256",
                "dom_owned_text_focused", "dom_document_has_focus", "dom_selection_start", "dom_selection_end",
                "dom_pointer_down_count", "dom_key_down_count", "document_instance_sha256", "navigation_start_count",
                "frame_pixel_sentinel", "different_frame_observed", "captured_frame_count" });
        if (h.kind == "input_result")
            permitted.UnionWith(new[] { "before_dom_owned_text_focused", "before_dom_document_has_focus", "before_dom_selection_start", "before_dom_selection_end",
                "before_dom_text_length", "before_dom_text_sha256", "before_dom_composition_start", "before_dom_composition_update", "before_dom_composition_end" });
        foreach (JsonProperty property in JsonSerializer.SerializeToElement(h, Json).EnumerateObject())
            if (!Envelope.Contains(property.Name) && !permitted.Contains(property.Name)) throw new InvalidDataException("SESSION_FIELD_NOT_ALLOWED_FOR_KIND");
        bool rpc = h.kind is "input" or "frame" or "input_result";
        if (rpc ? h.request_id == 0 : h.request_id != 0 || h.frame_id != 0) throw new InvalidDataException("SESSION_REQUEST_ID_CONTEXT_REJECTED");
        if (h.kind != "frame" && h.frame_id != 0) throw new InvalidDataException("SESSION_FRAME_ID_CONTEXT_REJECTED");
        if ((h.kind == "job_ready" && (h.job_handle is null or 0)) || h.job_handle == 0 || h.browser_pid == 0 || h.creation_filetime == 0)
            throw new InvalidDataException("SESSION_NONZERO_NATIVE_ID_REQUIRED");
        if (h.host_process_handle == 0 || h.host_pid == 0 || h.host_creation_filetime == 0) throw new InvalidDataException("SESSION_NONZERO_HOST_ID_REQUIRED");
        if (h.kind == "controller_ready" && h.browser_pid is null) throw new InvalidDataException("SESSION_BROWSER_PID_REQUIRED");
        if (h.kind == "owned_ready" && (h.station_name is null || h.desktop_name is null || h.package_sid_sha256 is null))
            throw new InvalidDataException("SESSION_OWNED_NAMES_REQUIRED");
        foreach (string? name in new[] { h.station_name, h.desktop_name })
            if (name is not null && (name.Length is < 1 or > 256 || name.IndexOfAny(['\0', '\\', '/']) >= 0)) throw new InvalidDataException("SESSION_BOUNDED_OBJECT_NAME_REQUIRED");
        foreach (string? hash in new[] { h.package_sid_sha256, h.report_sha256, h.dom_text_sha256, h.dom_phase_digest_sha256, h.document_instance_sha256, h.before_dom_text_sha256,
            h.dom_composition_update_data_sha256, h.dom_composition_end_data_sha256 })
            if (hash is not null && !Regex.IsMatch(hash, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("SESSION_HASH_REQUIRED");
        if (h.native_error is < 0 || h.final_environment_process_count is < 0 or > 4096 || h.error_stage is not null &&
            !Regex.IsMatch(h.error_stage, "\\A[A-Za-z][A-Za-z0-9_]{0,63}\\z", RegexOptions.CultureInvariant)) throw new InvalidDataException("SESSION_FIXED_ERROR_METADATA_REQUIRED");
        if (h.kind == "frame" && (h.width != Width || h.height != Height || h.frame_id < 1)) throw new InvalidDataException("SESSION_FIXED_FRAME_SIZE_REQUIRED");
        if (h.kind == "input_result" && h.success is null) throw new InvalidDataException("SESSION_INPUT_RESULT_REQUIRED");
        if (h.kind == "host_complete" && (h.success is null || h.cleanup_confirmed is null)) throw new InvalidDataException("SESSION_HOST_COMPLETION_REQUIRED");
        if (h.kind == "input") ValidateInput(h);
        foreach (int? count in new[] { h.raf_count, h.dom_composition_start, h.dom_composition_update, h.dom_composition_end, h.dom_input_count,
            h.dom_trusted_composition_start, h.dom_trusted_composition_update, h.dom_trusted_composition_end, h.dom_trusted_input_count, h.captured_frame_count })
            if (count is < 0 or > 100000000) throw new InvalidDataException("SESSION_DOM_COUNTER_REJECTED");
        if (h.dom_text_length is < 0 or > 1024 || h.wasm_result is not null and not 42) throw new InvalidDataException("SESSION_DOM_EVIDENCE_BOUNDS_REJECTED");
        if (h.before_dom_text_length is < 0 or > 1024) throw new InvalidDataException("SESSION_DOM_EVIDENCE_BOUNDS_REJECTED");
        foreach (int? count in new[] { h.dom_pointer_down_count, h.dom_key_down_count, h.navigation_start_count,
            h.before_dom_composition_start, h.before_dom_composition_update, h.before_dom_composition_end })
            if (count is < 0 or > 1000000) throw new InvalidDataException("SESSION_DOM_DIAGNOSTIC_COUNTER_REJECTED");
        ValidateDomSelection(h.dom_selection_start, h.dom_selection_end, h.dom_text_length);
        ValidateDomSelection(h.before_dom_selection_start, h.before_dom_selection_end, h.before_dom_text_length);
        ValidateEventData(h.dom_composition_update_data_length, h.dom_composition_update_data_sha256);
        ValidateEventData(h.dom_composition_end_data_length, h.dom_composition_end_data_sha256);
        if (h.captured_frame_count is > 1000000) throw new InvalidDataException("SESSION_FRAME_COUNTER_REJECTED");
    }
    private static void ValidateEventData(int? length, string? hash)
    {
        if (length.HasValue != (hash is not null) || length is < 0 or > 1024)
            throw new InvalidDataException("SESSION_COMPOSITION_EVENT_DATA_REJECTED");
    }
    private static void ValidateDomSelection(int? start, int? end, int? textLength)
    {
        if (start.HasValue != end.HasValue || start is < 0 or > 1024 || end is < 0 or > 1024 ||
            start.HasValue && (end!.Value < start.Value || textLength.HasValue && end.Value > textLength.Value))
            throw new InvalidDataException("SESSION_DOM_SELECTION_REJECTED");
    }
    internal static SessionHeader WithEvidence(SessionHeader target, SessionHeader actual) => target with
    {
        wasm_result = actual.wasm_result, raf_count = actual.raf_count,
        dom_composition_start = actual.dom_composition_start, dom_composition_update = actual.dom_composition_update,
        dom_composition_end = actual.dom_composition_end, dom_input_count = actual.dom_input_count,
        dom_trusted_composition_start = actual.dom_trusted_composition_start, dom_trusted_composition_update = actual.dom_trusted_composition_update,
        dom_trusted_composition_end = actual.dom_trusted_composition_end, dom_trusted_input_count = actual.dom_trusted_input_count,
        dom_text_length = actual.dom_text_length, dom_text_sha256 = actual.dom_text_sha256, dom_phase_digest_sha256 = actual.dom_phase_digest_sha256,
        dom_expected_hello = actual.dom_expected_hello, dom_composing = actual.dom_composing,
        dom_composition_update_data_length = actual.dom_composition_update_data_length, dom_composition_update_data_sha256 = actual.dom_composition_update_data_sha256,
        dom_composition_end_data_length = actual.dom_composition_end_data_length, dom_composition_end_data_sha256 = actual.dom_composition_end_data_sha256,
        dom_owned_text_focused = actual.dom_owned_text_focused, dom_document_has_focus = actual.dom_document_has_focus,
        dom_selection_start = actual.dom_selection_start, dom_selection_end = actual.dom_selection_end,
        dom_pointer_down_count = actual.dom_pointer_down_count, dom_key_down_count = actual.dom_key_down_count,
        document_instance_sha256 = actual.document_instance_sha256, navigation_start_count = actual.navigation_start_count,
        before_dom_owned_text_focused = target.kind == "input_result" ? actual.before_dom_owned_text_focused : null,
        before_dom_document_has_focus = target.kind == "input_result" ? actual.before_dom_document_has_focus : null,
        before_dom_selection_start = target.kind == "input_result" ? actual.before_dom_selection_start : null,
        before_dom_selection_end = target.kind == "input_result" ? actual.before_dom_selection_end : null,
        before_dom_text_length = target.kind == "input_result" ? actual.before_dom_text_length : null,
        before_dom_text_sha256 = target.kind == "input_result" ? actual.before_dom_text_sha256 : null,
        before_dom_composition_start = target.kind == "input_result" ? actual.before_dom_composition_start : null,
        before_dom_composition_update = target.kind == "input_result" ? actual.before_dom_composition_update : null,
        before_dom_composition_end = target.kind == "input_result" ? actual.before_dom_composition_end : null,
        frame_pixel_sentinel = actual.frame_pixel_sentinel, different_frame_observed = actual.different_frame_observed, captured_frame_count = actual.captured_frame_count
    };
    private static void ValidateInput(SessionHeader h)
    {
        string allowed = h.command switch
        {
            "capture" or "load_owned_page" or "close" or "private_paste" => "",
            "pointer_move" => "x,y,modifiers", "pointer_down" or "pointer_up" => "x,y,button,modifiers", "wheel" => "x,y,wheel_x,wheel_y,modifiers",
            "key_down" or "key_up" => "key,modifiers", "insert_text" or "ime_commit" => "text",
            "ime_start" or "ime_update" or "ime_set_composition" => "text,selection_start,selection_end,replacement_start,replacement_end",
            "ime_cancel" => "", _ => throw new InvalidDataException("SESSION_INPUT_COMMAND_NOT_ALLOWED")
        };
        var permitted = allowed.Length == 0 ? new HashSet<string>(StringComparer.Ordinal) : allowed.Split(',').ToHashSet(StringComparer.Ordinal);
        foreach (JsonProperty property in JsonSerializer.SerializeToElement(h, Json).EnumerateObject())
            if (!Envelope.Contains(property.Name) && property.Name != "command" && !permitted.Contains(property.Name)) throw new InvalidDataException("SESSION_INPUT_FIELD_NOT_ALLOWED");
        if (h.modifiers is < 0 or > 15) throw new InvalidDataException("SESSION_MODIFIERS_OUT_OF_RANGE");
        if (h.command is "pointer_move" or "pointer_down" or "pointer_up" or "wheel")
            if (h.x is null or < 0 or >= Width || h.y is null or < 0 or >= Height) throw new InvalidDataException("SESSION_INPUT_POINT_OUTSIDE_FRAME");
        if (h.command is "pointer_down" or "pointer_up" && h.button is not ("left" or "right" or "middle")) throw new InvalidDataException("SESSION_BUTTON_NOT_ALLOWED");
        if (h.command == "wheel" && (h.wheel_x is null or < -1200 or > 1200 || h.wheel_y is null or < -1200 or > 1200)) throw new InvalidDataException("SESSION_WHEEL_BOUNDED_REQUIRED");
        if (h.command is "key_down" or "key_up")
            if (h.key is null || !(Keys.Contains(h.key) || h.key.Length == 1 && (h.key[0] is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9')))
                throw new InvalidDataException("SESSION_KEY_NOT_ALLOWED");
        if (h.command is "insert_text" or "ime_commit" or "ime_start" or "ime_update" or "ime_set_composition")
        {
            if (h.text is null || h.text.Length > 1024 || h.text.Contains('\0')) throw new InvalidDataException("SESSION_BOUNDED_TEXT_REQUIRED");
            _ = Utf8.GetByteCount(h.text);
        }
        if (h.command is "ime_start" or "ime_update" or "ime_set_composition")
        {
            if (h.selection_start is null or < 0 || h.selection_end is null || h.selection_end.Value < h.selection_start.Value || h.selection_end.Value > h.text!.Length)
                throw new InvalidDataException("SESSION_IME_SELECTION_REJECTED");
            if (h.replacement_start.HasValue != h.replacement_end.HasValue || h.replacement_start is < 0 or > 1024 ||
                h.replacement_end is < 0 or > 1024 || (h.replacement_start.HasValue && h.replacement_end!.Value < h.replacement_start.Value)) throw new InvalidDataException("SESSION_IME_REPLACEMENT_REJECTED");
        }
    }
    internal static void ValidatePng(byte[] data)
    {
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (data.Length is < 57 or > PngLimit || !data.AsSpan(0, 8).SequenceEqual(signature)) throw new InvalidDataException("SESSION_PNG_SIGNATURE_REJECTED");
        int offset = 8, channels = 0; bool ihdr = false, idat = false, ended = false, palette = false, dataEnded = false;
        using var compressed = new MemoryStream();
        var ancillary = new HashSet<string>(StringComparer.Ordinal);
        while (offset <= data.Length - 12)
        {
            uint length = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
            if (length > (uint)(data.Length - offset - 12)) throw new InvalidDataException("SESSION_PNG_CHUNK_LENGTH_REJECTED");
            int n = checked((int)length); ReadOnlySpan<byte> type = data.AsSpan(offset + 4, 4), content = data.AsSpan(offset + 8, n);
            foreach (byte letter in type)
                if (letter is not (>= 65 and <= 90 or >= 97 and <= 122)) throw new InvalidDataException("SESSION_PNG_CHUNK_TYPE_REJECTED");
            if ((type[2] & 32) != 0) throw new InvalidDataException("SESSION_PNG_RESERVED_CHUNK_BIT_REJECTED");
            uint crc = Crc32(data.AsSpan(offset + 4, n + 4));
            if (crc != BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset + 8 + n, 4))) throw new InvalidDataException("SESSION_PNG_CRC_REJECTED");
            string name = Encoding.ASCII.GetString(type);
            if (!ihdr && name != "IHDR") throw new InvalidDataException("SESSION_PNG_IHDR_FIRST_REQUIRED");
            if (name == "IHDR")
            {
                if (ihdr || n != 13 || BinaryPrimitives.ReadUInt32BigEndian(content[..4]) != Width || BinaryPrimitives.ReadUInt32BigEndian(content.Slice(4, 4)) != Height ||
                    content[8] != 8 || content[9] is not (2 or 6) || content[10] != 0 || content[11] != 0 || content[12] != 0)
                    throw new InvalidDataException("SESSION_PNG_ACTUAL_DIMENSIONS_OR_FORMAT_REJECTED");
                ihdr = true;
                channels = content[9] == 6 ? 4 : 3;
            }
            else if (name == "IDAT")
            {
                if (n == 0 || dataEnded || compressed.Length + n > PngLimit) throw new InvalidDataException("SESSION_PNG_IDAT_ORDER_REJECTED");
                idat = true; compressed.Write(content);
            }
            else if (name == "IEND") { if (n != 0 || !idat) throw new InvalidDataException("SESSION_PNG_IEND_REJECTED"); ended = true; }
            else if (name == "PLTE")
            { if (palette || idat || n is < 3 or > 768 || n % 3 != 0) throw new InvalidDataException("SESSION_PNG_PALETTE_REJECTED"); palette = true; }
            else
            {
                // No ICC profile, compressed text, APNG, EXIF or unknown chunk
                // reaches the UI decoder. Only these tiny uncompressed fields
                // are accepted for the frozen browser's PNG output.
                if (idat || !ancillary.Add(name)) throw new InvalidDataException("SESSION_PNG_METADATA_ORDER_REJECTED");
                bool safe = name switch
                {
                    "sRGB" => n == 1 && content[0] <= 3,
                    "gAMA" => n == 4 && BinaryPrimitives.ReadUInt32BigEndian(content) != 0,
                    "pHYs" => n == 9 && content[8] <= 1,
                    "cHRM" => n == 32,
                    "bKGD" => n == 6 && BinaryPrimitives.ReadUInt16BigEndian(content[..2]) <= 255 &&
                        BinaryPrimitives.ReadUInt16BigEndian(content.Slice(2, 2)) <= 255 && BinaryPrimitives.ReadUInt16BigEndian(content.Slice(4, 2)) <= 255,
                    "sBIT" => n == channels && ValidSignificantBits(content),
                    _ => false
                };
                if (!safe) throw new InvalidDataException("SESSION_PNG_METADATA_NOT_ALLOWED");
            }
            if (idat && name != "IDAT") dataEnded = true;
            offset += n + 12; if (ended) break;
        }
        if (!ended || offset != data.Length) throw new InvalidDataException("SESSION_PNG_TRUNCATED_OR_TRAILING_DATA");
        // Output is consumed a scanline at a time. At most one byte beyond the
        // fixed decoded raster is requested; compressed size cannot authorize
        // a larger allocation or decompression workload in the UI decoder.
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress, true);
        byte[] row = new byte[checked(Width * channels + 1)];
        try
        {
            for (int y = 0; y < Height; y++)
            {
                zlib.ReadExactly(row);
                if (row[0] > 4) throw new InvalidDataException("SESSION_PNG_SCANLINE_FILTER_REJECTED");
            }
            if (zlib.ReadByte() != -1) throw new InvalidDataException("SESSION_PNG_DECODED_RASTER_TOO_LARGE");
        }
        catch (EndOfStreamException) { throw new InvalidDataException("SESSION_PNG_DECODED_RASTER_TRUNCATED"); }
    }
    private static bool ValidSignificantBits(ReadOnlySpan<byte> values)
    { foreach (byte value in values) if (value is < 1 or > 8) return false; return true; }
    internal static uint Crc32(ReadOnlySpan<byte> bytes)
    {
        uint crc = uint.MaxValue;
        foreach (byte value in bytes) { crc ^= value; for (int bit = 0; bit < 8; bit++) crc = crc >> 1 ^ ((crc & 1) == 0 ? 0 : 0xedb88320u); }
        return ~crc;
    }
}

// A separate instance belongs to each channel end. The caller serializes all
// reads/writes: no second request is admitted before the first response.
internal sealed class SessionRequestGate(string nonce, long epoch)
{
    private long lastRequest, lastFrame;
    private SessionHeader? pending;
    internal bool HasInFlight => pending is not null;
    internal void Begin(SessionHeader request)
    {
        SessionProtocol.ValidateHeader(request);
        if (request.nonce != nonce || request.epoch != epoch || request.kind != "input" || pending is not null || request.request_id != lastRequest + 1)
            throw new InvalidDataException("SESSION_REQUEST_SEQUENCE_OR_INFLIGHT_REJECTED");
        pending = request; lastRequest = request.request_id;
    }
    internal void Complete(SessionHeader response)
    {
        SessionProtocol.ValidateHeader(response);
        if (pending is null || response.nonce != nonce || response.epoch != epoch || response.request_id != pending.request_id ||
            (pending.command == "capture" ? response.kind != "frame" || response.frame_id <= lastFrame : response.kind != "input_result"))
            throw new InvalidDataException("SESSION_RESPONSE_SEQUENCE_OR_STALE_FRAME_REJECTED");
        if (response.kind == "frame") lastFrame = response.frame_id;
        pending = null;
    }
}
