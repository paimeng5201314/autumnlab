using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace AutumnOS.NetOnlyStationProbe;

// Pure actual protocol/string/result/token-gate regressions. Program.Main and
// native P/Invokes are never invoked; stream tests use managed memory only.
internal static class ProtocolSelfTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    internal static int Run(string reportPath)
    {
        if (!Path.IsPathFullyQualified(reportPath)) throw new InvalidDataException("ABSOLUTE_FRESH_SELF_TEST_REPORT_REQUIRED");
        var cases = new List<object>(); int passed = 0, failed = 0;
        void Case(string name, Action body)
        {
            try { body(); passed++; cases.Add(new { name, status = "passed" }); }
            catch (Exception ex) { failed++; cases.Add(new { name, status = "failed", exception_type = ex.GetType().Name, hresult = unchecked((uint)ex.HResult) }); }
        }
        Case("all_five_fixed_kinds_roundtrip_actual_serializer_parser", () =>
        {
            foreach (string kind in new[] { "hello", "ready", "create", "result", "ack" })
            {
                NetMessage message = kind == "result" ? Created() : new(1, Nonce, kind);
                Require(Protocol.Parse(Protocol.Serialize(message), Nonce, kind) == message);
            }
        });
        Case("explicit_create_failure_is_retained_not_fabricated_success", () =>
        {
            var message = new NetMessage(1, Nonce, "result", failure_stage: "create_NULL_CWF", native_error: 5, cleanup_confirmed: true,
                original_station_unchanged: true, create_attempted: true, creation_succeeded: false, exact_name_verified: false,
                noninteractive_verified: false, original_handle_closed: false);
            NetMessage actual = Protocol.Parse(Protocol.Serialize(message), Nonce, "result");
            Require(actual == message && actual.creation_succeeded == false && actual.native_error == 5 && actual.descriptor_sha256 is null);
        });
        Case("wrong_nonce_schema_or_expected_kind_rejected", () =>
        {
            byte[] hello = Protocol.Serialize(new(1, Nonce, "hello"));
            Reject(() => Protocol.Parse(hello, new string('a', 32), "hello"), "NET_PROTOCOL_ENVELOPE_OR_EXPECTED_STATE_REJECTED");
            Reject(() => Protocol.Parse(hello, Nonce, "create"), "NET_PROTOCOL_ENVELOPE_OR_EXPECTED_STATE_REJECTED");
            Reject(() => Protocol.Serialize(new(2, Nonce, "hello")), "NET_PROTOCOL_ENVELOPE_OR_EXPECTED_STATE_REJECTED");
            Reject(() => Protocol.Serialize(new(1, Nonce.ToUpperInvariant(), "hello")), "NET_PROTOCOL_ENVELOPE_OR_EXPECTED_STATE_REJECTED");
        });
        Case("unknown_kind_and_field_rejected", () =>
        {
            Reject(() => Protocol.Serialize(new(1, Nonce, "execute")), "NET_PROTOCOL_ENVELOPE_OR_EXPECTED_STATE_REJECTED");
            Reject(() => Parse("{\"unexpected\":0," + HelloJson()[1..]), "NET_PROTOCOL_UNKNOWN_OR_DUPLICATE_KEY");
        });
        Case("duplicate_key_rejected_before_deserialization", () =>
            Reject(() => Parse("{\"schema_version\":1," + HelloJson()[1..]), "NET_PROTOCOL_UNKNOWN_OR_DUPLICATE_KEY"));
        Case("nonobject_and_nested_fields_rejected", () =>
        {
            Reject(() => Parse("[]"), "NET_PROTOCOL_OBJECT_REQUIRED");
            Reject(() => Parse("{\"station_name\":[]," + HelloJson()[1..]), "NET_PROTOCOL_SCALAR_FIELDS_ONLY");
        });
        Case("strict_UTF8_and_oversize_payload_rejected", () =>
        {
            Reject(() => Protocol.Parse([0xff], Nonce, "hello"));
            Reject(() => Protocol.Parse(new byte[4097], Nonce, "hello"), "NET_PROTOCOL_LENGTH_REJECTED");
            Reject(() => Protocol.Parse([], Nonce, "hello"), "NET_PROTOCOL_LENGTH_REJECTED");
        });
        Case("handshake_cannot_carry_result_facts", () =>
        {
            foreach (string kind in new[] { "hello", "ready", "create", "ack" })
                Reject(() => Protocol.Serialize(new(1, Nonce, kind, native_error: 0)), "NET_PROTOCOL_HANDSHAKE_MUST_NOT_CARRY_FACTS");
        });
        Case("result_requires_all_fixed_boolean_facts", () =>
            Reject(() => Protocol.Serialize(new(1, Nonce, "result")), "NET_PROTOCOL_RESULT_FACTS_REQUIRED"));
        Case("OS_generated_station_name_accepts_no_assumed_custom_prefix", () =>
        {
            NetMessage message = Created() with { station_name = "Service-0x0-123abc$" };
            Require(Protocol.Parse(Protocol.Serialize(message), Nonce, "result").station_name == message.station_name);
        });
        Case("bad_station_names_and_created_without_name_rejected", () =>
        {
            foreach (string name in new[] { "", "station\\desktop", "station/name", "station\0suffix", new string('x', 257) })
                Reject(() => Protocol.Serialize(Created() with { station_name = name }), "NET_PROTOCOL_BOUNDED_OS_STATION_NAME_REQUIRED");
            Reject(() => Protocol.Serialize(Created() with { station_name = null }), "NET_PROTOCOL_CREATED_STATION_NAME_REQUIRED");
        });
        Case("failure_stage_error_and_hash_are_bounded", () =>
        {
            Reject(() => Protocol.Serialize(Created() with { failure_stage = "path/or/message" }), "NET_PROTOCOL_FIXED_FAILURE_STAGE_REQUIRED");
            Reject(() => Protocol.Serialize(Created() with { native_error = -1 }), "NET_PROTOCOL_ERROR_OR_DESCRIPTOR_HASH_REJECTED");
            Reject(() => Protocol.Serialize(Created() with { descriptor_sha256 = "not_a_sha256" }), "NET_PROTOCOL_ERROR_OR_DESCRIPTOR_HASH_REJECTED");
        });
        Case("length_prefixed_inmemory_stream_roundtrip", () =>
        {
            using var stream = new MemoryStream(); var hello = new NetMessage(1, Nonce, "hello");
            Protocol.WriteAsync(stream, hello, CancellationToken.None).GetAwaiter().GetResult(); stream.Position = 0;
            Require(Protocol.ReadAsync(stream, Nonce, "hello", CancellationToken.None).GetAwaiter().GetResult() == hello && stream.Position == stream.Length);
        });
        Case("invalid_frame_lengths_rejected_before_payload_read", () =>
        {
            foreach (int length in new[] { 0, -1, 4097, int.MaxValue })
            {
                byte[] prefix = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(prefix, length); using var stream = new MemoryStream(prefix);
                Reject(() => Protocol.ReadAsync(stream, Nonce, "hello", CancellationToken.None).GetAwaiter().GetResult(), "NET_PROTOCOL_LENGTH_REJECTED");
            }
        });
        Case("truncated_frame_is_not_a_valid_message", () =>
        {
            using var stream = new MemoryStream(new byte[] { 5, 0, 0, 0, (byte)'{' });
            try { Protocol.ReadAsync(stream, Nonce, "hello", CancellationToken.None).GetAwaiter().GetResult(); }
            catch (EndOfStreamException) { return; }
            throw new InvalidDataException("TRUNCATED_FRAME_ACCEPTED");
        });
        Case("actual_manifest_fixed_metadata_gate", () =>
        {
            string source = "sha256:" + new string('a', 64), entry = "AutumnOS.NetOnlyStationProbe.exe";
            ManifestInputs.RequireFixedBindings(1, source, entry);
            Reject(() => ManifestInputs.RequireFixedBindings(2, source, entry), "FIXED_SCHEMA_SOURCE_AND_ENTRY_REQUIRED");
            Reject(() => ManifestInputs.RequireFixedBindings(1, "sha256:invalid", entry), "FIXED_SCHEMA_SOURCE_AND_ENTRY_REQUIRED");
            Reject(() => ManifestInputs.RequireFixedBindings(1, source, "other.exe"), "FIXED_SCHEMA_SOURCE_AND_ENTRY_REQUIRED");
        });
        Case("actual_manifest_relative_binding_gate", () =>
        {
            const string root = @"D:\synthetic_publish_not_opened";
            Require(ManifestInputs.Bound(root, "sub/file.dll") == Path.Combine(root, "sub", "file.dll"));
            foreach (string path in new[] { "../escape", "sub/../escape", "sub//file", "file:stream", @"D:\outside.exe", "file.", "file ", "" })
                Reject(() => ManifestInputs.Bound(root, path), "PLAIN_RELATIVE_FILE_BINDING_REQUIRED");
        });
        Case("actual_child_result_gate_requires_complete_success_and_cleanup", () =>
        {
            NetMessage valid = Created(); Require(Program.ChildResultPass(valid));
            foreach (NetMessage invalid in new[] { valid with { cleanup_confirmed = false }, valid with { station_name = null },
                valid with { descriptor_sha256 = null }, valid with { native_error = 5 }, valid with { close_native_error = 5 },
                valid with { original_station_unchanged = false }, valid with { noninteractive_verified = false },
                valid with { original_handle_closed = false }, valid with { create_attempted = false } })
                Require(!Program.ChildResultPass(invalid));
            Require(!Program.ChildResultPass(null));
        });
        Case("actual_token_admission_requires_identity_and_no_privilege_gain", () =>
        {
            var parent = new TokenFacts(new string('a', 64), 1, 8192, false, false, "00000000:00000011",
                ["00000000:00000017"], ["00000000:00000017", "00000000:00000018"]);
            TokenFacts valid = parent with { authentication_id = "00000000:00000022" };
            Require(Native.TokenAdmission(parent, valid));
            foreach (TokenFacts invalid in new[] { valid with { authentication_id = parent.authentication_id },
                valid with { user_sid_sha256 = new string('b', 64) }, valid with { session_id = 2 }, valid with { integrity_rid = 12288 },
                valid with { elevated = true }, valid with { appcontainer = true },
                valid with { enabled_privileges = ["00000000:00000099"] },
                valid with { present_privileges = ["00000000:00000017", "00000000:00000018", "00000000:00000099"] } })
                Require(!Native.TokenAdmission(parent, invalid));
        });
        var report = new
        {
            schema_version = 1, producer = "派蒙", status = failed == 0 ? "passed" : "failed", passed, failed, cases,
            scope = "actual_protocol_parser_serializer_inmemory_framing_manifest_strings_child_result_and_token_admission_pure_gates_only",
            native_calls = 0, logon_calls = 0, CreateProcessWithLogonW_calls = 0, station_calls = 0, profile_calls = 0,
            clipboard_calls = 0, uac_requests = 0, process_execution = "not_run", native_probe_execution = "not_run", isolation_acceptance = "not_claimed",
            native_call_scope = "project_probe_PInvoke_calls_only_not_a_claim_about_CLR_or_fresh_report_file_IO"
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
        using (var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { output.Write(bytes); output.Flush(true); }
        Console.WriteLine(Encoding.UTF8.GetString(bytes)); return failed == 0 ? 0 : 1;
    }
    private static NetMessage Created() => new(1, Nonce, "result", "Service-0x0-123abc$", "none", 0, true, new string('a', 64), true, true, true, true, true, true, 0);
    private static string HelloJson() => Encoding.UTF8.GetString(Protocol.Serialize(new(1, Nonce, "hello")));
    private static NetMessage Parse(string text) => Protocol.Parse(Encoding.UTF8.GetBytes(text), Nonce, "hello");
    private static void Reject(Action body, string? expectedCode = null)
    {
        try { body(); }
        catch (InvalidDataException ex) when (expectedCode is null || ex.Message == expectedCode) { return; }
        catch (JsonException) when (expectedCode is null) { return; }
        catch (DecoderFallbackException) when (expectedCode is null) { return; }
        throw new InvalidDataException("EXPECTED_PROTOCOL_REJECTION_MISSING");
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidDataException("PURE_PROTOCOL_SELF_TEST_ASSERTION_FAILED"); }
}
