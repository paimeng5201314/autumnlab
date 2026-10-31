using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AutumnOS.SessionBrowserProtocol;

internal static class SessionProtocolSelfTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    internal static int Run(string reportPath)
    {
        if (!Path.IsPathFullyQualified(reportPath)) throw new InvalidDataException("ABSOLUTE_FRESH_CODEC_REPORT_REQUIRED");
        var results = new List<object>(); int passed = 0, failed = 0; byte[] png = Png();
        void Case(string name, Action action)
        {
            try { action(); passed++; results.Add(new { name, status = "passed" }); }
            catch (Exception ex) { failed++; results.Add(new { name, status = "failed", exception_type = ex.GetType().Name, hresult = unchecked((uint)ex.HResult) }); }
        }
        Case("actual_png_structure_and_standard_CRC_vector", () =>
        {
            Require(SessionProtocol.Crc32(Encoding.ASCII.GetBytes("123456789")) == 0xcbf43926);
            SessionProtocol.ValidatePng(png);
        });
        Case("frame_roundtrip_through_actual_bounded_codec", () =>
        {
            SessionPacket frame = Frame(1, 1, png); SessionPacket? actual = RoundTrip(frame);
            Require(actual is not null && actual.header == frame.header && actual.data.SequenceEqual(png));
        });
        Case("data_for_nonframe_and_missing_frame_data_rejected", () =>
        {
            Reject(() => SessionProtocol.Validate(new(H("hello"), [1])));
            Reject(() => SessionProtocol.Validate(Frame(1, 1, [])));
        });
        Case("PNG_actual_dimensions_signature_CRC_and_trailing_bytes_rejected", () =>
        {
            byte[] wrongSignature = (byte[])png.Clone(); wrongSignature[0] ^= 1; Reject(() => SessionProtocol.ValidatePng(wrongSignature));
            byte[] wrongSize = Png(801, 600); Reject(() => SessionProtocol.ValidatePng(wrongSize));
            byte[] wrongCrc = (byte[])png.Clone(); wrongCrc[29] ^= 1; Reject(() => SessionProtocol.ValidatePng(wrongCrc));
            Reject(() => SessionProtocol.ValidatePng([.. png, 0]));
            Reject(() => SessionProtocol.ValidatePng(png[..^1]));
        });
        Case("PNG_compressed_metadata_and_raster_expansion_are_bounded", () =>
        {
            Reject(() => SessionProtocol.ValidatePng(Png(extraRawBytes: 1)), "SESSION_PNG_DECODED_RASTER_TOO_LARGE");
            Reject(() => SessionProtocol.ValidatePng(Png(extraRawBytes: -1)), "SESSION_PNG_DECODED_RASTER_TRUNCATED");
            Reject(() => SessionProtocol.ValidatePng(Png(scanlineFilter: 5)), "SESSION_PNG_SCANLINE_FILTER_REJECTED");
            Reject(() => SessionProtocol.ValidatePng(Png(compressedMetadata: true)), "SESSION_PNG_METADATA_NOT_ALLOWED");
        });
        Case("oversize_binary_and_header_lengths_rejected_before_allocation", () =>
        {
            using var headerStream = new MemoryStream(Prefix(SessionProtocol.HeaderLimit + 1));
            Reject(() => SessionProtocol.ReadAsync(headerStream, Nonce, 1, CancellationToken.None).GetAwaiter().GetResult());
            using var binaryStream = new MemoryStream([.. HeaderBytes(H("hello")), .. Prefix(SessionProtocol.PngLimit + 1)]);
            Reject(() => SessionProtocol.ReadAsync(binaryStream, Nonce, 1, CancellationToken.None).GetAwaiter().GetResult());
        });
        Case("normal_boundary_EOF_is_distinct_from_all_truncation_positions", () =>
        {
            using var empty = new MemoryStream();
            try { SessionProtocol.ReadAsync(empty, Nonce, 1, CancellationToken.None).GetAwaiter().GetResult(); }
            catch (EndOfStreamException) { goto Truncated; }
            throw new InvalidDataException("EXPECTED_BOUNDARY_EOF");
        Truncated:
            byte[] encoded = Encode(Frame(1, 1, png)); int headerLength = BinaryPrimitives.ReadInt32LittleEndian(encoded.AsSpan(0, 4));
            foreach (int length in new[] { 1, 3, 4, 5, headerLength + 3, headerLength + 4, headerLength + 6, encoded.Length - 1 })
            {
                using var stream = new MemoryStream(encoded[..length]);
                Reject(() => SessionProtocol.ReadAsync(stream, Nonce, 1, CancellationToken.None).GetAwaiter().GetResult(), "PROTOCOL_TRUNCATED");
            }
        });
        Case("stale_epoch_is_consumed_and_dropped_future_epoch_rejected", () =>
        {
            using var stream = new MemoryStream(Encode(new(H("hello"), [])));
            Require(SessionProtocol.ReadAsync(stream, Nonce, 2, CancellationToken.None).GetAwaiter().GetResult() is null && stream.Position == stream.Length);
            using var future = new MemoryStream(Encode(new(H("hello") with { epoch = 2 }, [])));
            Reject(() => SessionProtocol.ReadAsync(future, Nonce, 1, CancellationToken.None).GetAwaiter().GetResult());
        });
        Case("nonce_mismatch_unknown_duplicate_nested_and_invalid_UTF8_rejected", () =>
        {
            using var stream = new MemoryStream(Encode(new(H("hello"), [])));
            Reject(() => SessionProtocol.ReadAsync(stream, new string('a', 32), 1, CancellationToken.None).GetAwaiter().GetResult());
            string json = JsonSerializer.Serialize(H("hello"));
            Reject(() => SessionProtocol.ParseHeader(Encoding.UTF8.GetBytes("{\"schema_version\":1," + json[1..])));
            Reject(() => SessionProtocol.ParseHeader(Encoding.UTF8.GetBytes("{\"unknown\":0," + json[1..])));
            Reject(() => SessionProtocol.ParseHeader(Encoding.UTF8.GetBytes("{\"text\":[]," + json[1..])));
            Reject(() => SessionProtocol.ParseHeader([0xff]));
        });
        Case("control_native_ids_and_kind_fields_are_narrow", () =>
        {
            SessionProtocol.ValidateHeader(H("job_ready") with { job_handle = 42 });
            Reject(() => SessionProtocol.ValidateHeader(H("job_ready")));
            Reject(() => SessionProtocol.ValidateHeader(H("job_ready") with { job_handle = 0 }));
            Reject(() => SessionProtocol.ValidateHeader(H("hello") with { job_handle = 42 }));
            Reject(() => SessionProtocol.ValidateHeader(H("input") with { request_id = 1, command = "Runtime.evaluate" }));
            Reject(() => SessionProtocol.ValidateHeader(H("hello") with { request_id = 1 }));
        });
        Case("actual_presentation_ready_and_completion_metadata_shapes", () =>
        {
            SessionProtocol.ValidateHeader(H("ready") with { success = true, initialization_succeeded = true, page_succeeded = true, browser_pid = 42 });
            SessionProtocol.ValidateHeader(H("host_complete") with { success = true, cleanup_confirmed = true, browser_pid = 42,
                station_name = "Service-0x0-1234$", desktop_name = "AutumnOS_SessionDesktop_fixture", package_sid_sha256 = new string('a', 64),
                report_sha256 = new string('b', 64), normal_browser_exit = true, initialization_succeeded = true, page_succeeded = true,
                controller_disposed = true, final_environment_process_count = 0 });
        });
        Case("point_button_wheel_and_key_limits", () =>
        {
            var point = Input(1, "pointer_down") with { x = 799, y = 599, button = "left" }; SessionProtocol.ValidateHeader(point);
            Reject(() => SessionProtocol.ValidateHeader(point with { x = 800 }));
            Reject(() => SessionProtocol.ValidateHeader(point with { y = -1 }));
            Reject(() => SessionProtocol.ValidateHeader(point with { button = "extra" }));
            SessionProtocol.ValidateHeader(Input(1, "wheel") with { x = 0, y = 0, wheel_x = 0, wheel_y = 1200 });
            Reject(() => SessionProtocol.ValidateHeader(Input(1, "wheel") with { x = 0, y = 0, wheel_x = 0, wheel_y = 1201 }));
            SessionProtocol.ValidateHeader(Input(1, "key_down") with { key = "ArrowLeft" });
            Reject(() => SessionProtocol.ValidateHeader(Input(1, "key_down") with { key = "Alt+F4" }));
        });
        Case("UTF16_composition_and_text_bounds_are_actual_input_gate", () =>
        {
            var composition = Input(1, "ime_update") with { text = "字😀", selection_start = 0, selection_end = 3 };
            SessionProtocol.ValidateHeader(composition);
            Reject(() => SessionProtocol.ValidateHeader(composition with { selection_end = 4 }));
            Reject(() => SessionProtocol.ValidateHeader(composition with { replacement_start = 1 }));
            Reject(() => SessionProtocol.ValidateHeader(Input(1, "insert_text") with { text = new string('x', 1025) }));
            Reject(() => SessionProtocol.ValidateHeader(Input(1, "insert_text") with { text = "x\0y" }));
            Reject(() => SessionProtocol.ValidateHeader(Input(1, "insert_text") with { text = "\ud800" }));
            Reject(() => SessionProtocol.ValidateHeader(Input(1, "close") with { text = "not_allowed" }));
        });
        Case("modifiers_are_bounded_and_only_apply_to_fixed_key_or_pointer_actions", () =>
        {
            SessionProtocol.ValidateHeader(Input(1, "key_down") with { key = "V", modifiers = 2 });
            SessionProtocol.ValidateHeader(Input(1, "key_down") with { key = "ArrowLeft", modifiers = 8 });
            SessionProtocol.ValidateHeader(Input(1, "key_up") with { key = "Control", modifiers = 0 });
            Reject(() => SessionProtocol.ValidateHeader(Input(1, "key_down") with { key = "V", modifiers = 16 }), "SESSION_MODIFIERS_OUT_OF_RANGE");
            Reject(() => SessionProtocol.ValidateHeader(Input(1, "key_down") with { key = "V", modifiers = -1 }), "SESSION_MODIFIERS_OUT_OF_RANGE");
            Reject(() => SessionProtocol.ValidateHeader(Input(1, "capture") with { modifiers = 2 }), "SESSION_INPUT_FIELD_NOT_ALLOWED");
            Reject(() => SessionProtocol.ValidateHeader(Input(1, "ime_commit") with { text = "字", modifiers = 2 }), "SESSION_INPUT_FIELD_NOT_ALLOWED");
            Reject(() => SessionProtocol.ValidateHeader(Input(1, "Runtime.evaluate") with { text = "arbitrary" }), "SESSION_INPUT_COMMAND_NOT_ALLOWED");
        });
        Case("one_inflight_and_strict_request_sequence", () =>
        {
            var gate = new SessionRequestGate(Nonce, 1); gate.Begin(Input(1, "capture")); Require(gate.HasInFlight);
            Reject(() => gate.Begin(Input(2, "capture")));
            Reject(() => gate.Complete(Frame(2, 1, png).header));
            gate.Complete(Frame(1, 1, png).header); Require(!gate.HasInFlight);
            Reject(() => gate.Complete(Frame(1, 1, png).header));
            Reject(() => gate.Begin(Input(3, "capture")));
            gate.Begin(Input(2, "close")); gate.Complete(H("input_result") with { request_id = 2, success = true });
        });
        Case("stale_frame_and_wrong_reply_kind_do_not_clear_pending", () =>
        {
            var gate = new SessionRequestGate(Nonce, 1); gate.Begin(Input(1, "capture")); gate.Complete(Frame(1, 1, png).header);
            gate.Begin(Input(2, "capture")); Reject(() => gate.Complete(Frame(2, 1, png).header)); Require(gate.HasInFlight);
            Reject(() => gate.Complete(H("input_result") with { request_id = 2, success = true }));
            gate.Complete(Frame(2, 2, png).header); Require(!gate.HasInFlight);
        });
        Case("native_End_zero_range_caret7_commits_actual_post_End_buffer", () =>
        {
            var actual = new NativeCompositionTransaction(); actual.Begin(""); actual.RequireActive();
            Require(!actual.ShouldInsertPlainText("ni hao?"));
            long token = actual.End(0, 0, 7, 0, "ni hao?"); Require(!actual.CanSettle(token));
            actual.ObservePostEndNativeChange("你好"); NativeCompositionDispatch dispatch = actual.Settle(token, "你好");
            Require(dispatch.command == "ime_commit" && dispatch.text == "你好" && dispatch.fact.end_length == 0 &&
                dispatch.fact.end_selection_start == 7 && dispatch.fact.settled_length == 2 && dispatch.fact.initial_buffer_empty &&
                dispatch.fact.post_end_text_change_revision == 1 && dispatch.fact.settled_sha256 == TextHash("你好") &&
                dispatch.fact.notification_buffer_sha256 == TextHash("ni hao?"));
            actual.ConfirmDispatchQueued(dispatch); actual.ConfirmOwnBufferCleared(""); Require(actual.CanRequestNormalClose);
        });
        Case("native_cancellation_requires_empty_actual_post_End_buffer", () =>
        {
            var actual = new NativeCompositionTransaction(); actual.Begin(""); long token = actual.End(0, 0, 1, 0, "n");
            actual.ObservePostEndNativeChange(""); NativeCompositionDispatch dispatch = actual.Settle(token, "");
            Require(dispatch.command == "ime_cancel" && dispatch.text is null && dispatch.fact.decision == "cancel" &&
                dispatch.fact.settled_length == 0 && dispatch.fact.settled_sha256 == TextHash("") && dispatch.fact.initial_buffer_empty);
            actual.ConfirmDispatchQueued(dispatch); actual.ConfirmOwnBufferCleared("");
        });
        Case("native_TextChanged_before_and_after_End_never_duplicates_dispatch", () =>
        {
            var actual = new NativeCompositionTransaction(); actual.Begin(""); Require(!actual.ShouldInsertPlainText("ka"));
            long token = actual.End(0, 0, 2, 0, "ka"); Require(!actual.ShouldInsertPlainText("かな"));
            actual.ObservePostEndNativeChange("かな"); NativeCompositionDispatch dispatch = actual.Settle(token, "かな");
            Require(dispatch.text == "かな" && dispatch.fact.text_changed_notifications_suppressed == 2 && dispatch.fact.text_changed_after_end_suppressed == 1);
            Require(!actual.ShouldInsertPlainText("かな")); Reject(() => actual.Settle(token, "かな"));
            actual.ConfirmDispatchQueued(dispatch); Reject(() => actual.ConfirmDispatchQueued(dispatch));
            actual.ConfirmOwnBufferCleared(""); Require(!actual.ShouldInsertPlainText("") && actual.ShouldInsertPlainText("alpha"));
        });
        Case("native_settlement_requires_observed_post_End_revision_and_exact_buffer", () =>
        {
            var actual = new NativeCompositionTransaction(); actual.Begin(""); long token = actual.End(0, 0, 0, 0, "");
            Require(!actual.CanSettle(token)); Reject(() => actual.Settle(token, "old-preedit"));
            actual.ObservePostEndNativeChange("actual final"); Require(actual.CanSettle(token));
            Reject(() => actual.Settle(token, "unobserved new value"), "NATIVE_COMPOSITION_UNOBSERVED_SETTLED_BUFFER_VERSION");
            NativeCompositionDispatch dispatch = actual.Settle(token, "actual final"); Require(dispatch.text == "actual final");
            actual.ConfirmDispatchQueued(dispatch); actual.ConfirmOwnBufferCleared("");
        });
        Case("native_duplicate_unstarted_and_overlapping_End_transactions_rejected", () =>
        {
            var actual = new NativeCompositionTransaction(); Reject(() => actual.End(0, 0, 0, 0, ""));
            actual.Begin(""); long token = actual.End(0, 0, 0, 0, "");
            Reject(() => actual.End(0, 0, 0, 0, "")); Reject(() => actual.Begin(""));
            actual.InvalidateBoundary(); Reject(() => actual.Settle(token, "")); Require(actual.CanRequestNormalClose && actual.BlocksPlainText);
        });
        Case("native_pending_End_close_waits_for_dispatch_queue_and_own_clear", () =>
        {
            var actual = new NativeCompositionTransaction(); Require(actual.CanRequestNormalClose); actual.Begin(""); Require(!actual.CanRequestNormalClose);
            long token = actual.End(0, 0, 0, 0, ""); Require(!actual.CanRequestNormalClose);
            actual.ObservePostEndNativeChange("submitted"); NativeCompositionDispatch dispatch = actual.Settle(token, "submitted");
            Require(!actual.CanRequestNormalClose); Reject(() => actual.ConfirmOwnBufferCleared(""));
            actual.ConfirmDispatchQueued(dispatch); Require(!actual.CanRequestNormalClose); Reject(() => actual.ConfirmOwnBufferCleared("submitted"));
            actual.ConfirmOwnBufferCleared(""); Require(actual.CanRequestNormalClose);
        });
        Case("native_empty_baseline_and_End_total_buffer_bounds_are_required", () =>
        {
            var actual = new NativeCompositionTransaction(); Reject(() => actual.Begin("previous text"), "NATIVE_COMPOSITION_EMPTY_INITIAL_BUFFER_REQUIRED");
            actual.Begin(""); Reject(() => actual.End(0, 0, 1024, 1, "")); Reject(() => actual.End(1, 0, 0, 0, ""));
            Reject(() => actual.End(0, 1, 0, 0, "")); long token = actual.End(0, 0, 7, 0, "pinyin7"); Require(actual.IsCurrentPending(token));
        });
        Case("actual_readonly_DOM_before_after_diagnostics_roundtrip_bounds_and_no_authority_copy", () =>
        {
            SessionHeader observed = H("input_result") with
            {
                request_id = 9, success = true, operation_succeeded = true, dom_text_length = 2, dom_text_sha256 = TextHash("你好"),
                dom_owned_text_focused = true, dom_document_has_focus = false, dom_selection_start = 2, dom_selection_end = 2,
                dom_pointer_down_count = 3, dom_key_down_count = 4, document_instance_sha256 = new string('a', 64), navigation_start_count = 1,
                before_dom_owned_text_focused = false, before_dom_document_has_focus = false, before_dom_selection_start = 0, before_dom_selection_end = 0,
                before_dom_text_length = 0, before_dom_text_sha256 = TextHash(""), before_dom_composition_start = 0, before_dom_composition_update = 0, before_dom_composition_end = 0
            };
            SessionProtocol.ValidateHeader(observed);
            SessionHeader target = H("input_result") with { request_id = 2, success = false, operation_succeeded = false };
            SessionHeader copied = SessionProtocol.WithEvidence(target, observed);
            SessionPacket? roundtrip = RoundTrip(new(copied, []));
            Require(roundtrip is not null && roundtrip.header == copied && copied.request_id == 2 && copied.success == false &&
                copied.operation_succeeded == false && copied.text is null && copied.command is null && copied.dom_owned_text_focused == true &&
                copied.dom_document_has_focus == false && copied.document_instance_sha256 == observed.document_instance_sha256 &&
                copied.navigation_start_count == 1 && copied.before_dom_text_sha256 == TextHash("") && copied.before_dom_selection_end == 0);
            SessionHeader terminal = SessionProtocol.WithEvidence(H("host_complete") with { success = false, cleanup_confirmed = false }, observed);
            SessionProtocol.ValidateHeader(terminal);
            Require(terminal.success == false && terminal.cleanup_confirmed == false && terminal.dom_selection_end == 2 &&
                terminal.before_dom_text_length is null && terminal.before_dom_owned_text_focused is null && terminal.before_dom_composition_end is null);
            Reject(() => SessionProtocol.ValidateHeader(observed with { kind = "hello", request_id = 0 }), "SESSION_FIELD_NOT_ALLOWED_FOR_KIND");
            Reject(() => SessionProtocol.ValidateHeader(Frame(1, 1, png).header with { before_dom_text_length = 0 }), "SESSION_FIELD_NOT_ALLOWED_FOR_KIND");
            Reject(() => SessionProtocol.ValidateHeader(observed with { dom_key_down_count = 1000001 }), "SESSION_DOM_DIAGNOSTIC_COUNTER_REJECTED");
            Reject(() => SessionProtocol.ValidateHeader(observed with { navigation_start_count = -1 }), "SESSION_DOM_DIAGNOSTIC_COUNTER_REJECTED");
            Reject(() => SessionProtocol.ValidateHeader(observed with { before_dom_composition_end = 1000001 }), "SESSION_DOM_DIAGNOSTIC_COUNTER_REJECTED");
            Reject(() => SessionProtocol.ValidateHeader(observed with { dom_selection_end = 3 }), "SESSION_DOM_SELECTION_REJECTED");
            Reject(() => SessionProtocol.ValidateHeader(observed with { before_dom_selection_start = 1 }), "SESSION_DOM_SELECTION_REJECTED");
            Reject(() => SessionProtocol.ValidateHeader(observed with { dom_selection_start = null }), "SESSION_DOM_SELECTION_REJECTED");
            Reject(() => SessionProtocol.ValidateHeader(observed with { before_dom_text_length = 1025 }), "SESSION_DOM_EVIDENCE_BOUNDS_REJECTED");
            Reject(() => SessionProtocol.ValidateHeader(observed with { document_instance_sha256 = new string('A', 64) }), "SESSION_HASH_REQUIRED");
            Reject(() => SessionProtocol.ValidateHeader(observed with { before_dom_text_sha256 = "invalid" }), "SESSION_HASH_REQUIRED");
        });
        Case("actual_composition_admission_requires_focused_owned_target_and_preserves_live_native_transaction", () =>
        {
            CompositionEffectObservation baseline = EffectBase(); CompositionEffectGate.RequireAdmission(baseline, baseline.document_instance_sha256);
            Reject(() => CompositionEffectGate.RequireAdmission(baseline with { owned_text_focused = false }, baseline.document_instance_sha256));
            Reject(() => CompositionEffectGate.RequireAdmission(baseline with { document_has_focus = false }, baseline.document_instance_sha256));
            Reject(() => CompositionEffectGate.RequireAdmission(baseline, new string('b', 64)), "COMPOSITION_DOCUMENT_INSTANCE_CHANGED");
            SessionHeader focused = Frame(1, 1, png).header with { dom_owned_text_focused = true, dom_document_has_focus = true, document_instance_sha256 = baseline.document_instance_sha256 };
            SessionHeader unfocused = focused with { dom_owned_text_focused = false };
            Require(CompositionEffectGate.FrameAllowsNewComposition(focused, Nonce) && !CompositionEffectGate.FrameAllowsNewComposition(unfocused, Nonce));
            Require(!CompositionEffectGate.FrameAllowsNewComposition(focused, new string('b', 32)) && !CompositionEffectGate.FrameAllowsNewComposition(null, Nonce));
            Require(!CompositionEffectGate.NativeControlEnabled(true, false, false, false, unfocused, Nonce));
            Require(CompositionEffectGate.NativeControlEnabled(true, false, true, false, unfocused, Nonce));
            Require(CompositionEffectGate.NativeControlEnabled(true, true, false, true, unfocused, Nonce));
            Require(!CompositionEffectGate.NativeControlEnabled(true, true, false, false, focused, Nonce));
            Require(!CompositionEffectGate.NativeControlEnabled(false, false, true, false, focused, Nonce));
        });
        Case("actual_composition_update_requires_real_transaction_start_update_delta_and_matching_event_data", () =>
        {
            CompositionEffectObservation baseline = EffectBase();
            Require(CompositionEffectGate.HasRequiredEffect("ime_start", baseline, baseline, baseline, ""));
            var first = baseline with { composition_start = 1, composition_update = 1, composing = true, update_data_length = 1, update_data_sha256 = TextHash("n") };
            Require(CompositionEffectGate.HasRequiredEffect("ime_update", baseline, baseline, first, "n"));
            Require(!CompositionEffectGate.HasRequiredEffect("ime_update", baseline, baseline, baseline, "n"));
            Require(!CompositionEffectGate.HasRequiredEffect("ime_update", baseline, baseline, first with { composition_start = 0 }, "n"));
            Require(!CompositionEffectGate.HasRequiredEffect("ime_update", baseline, baseline, first with { update_data_sha256 = TextHash("x") }, "n"));
            Require(!CompositionEffectGate.HasRequiredEffect("ime_update", baseline, first, first, "n"));
            var second = first with { composition_update = 2, update_data_length = 2, update_data_sha256 = TextHash("ni") };
            Require(CompositionEffectGate.HasRequiredEffect("ime_update", baseline, first, second, "ni"));
            Reject(() => CompositionEffectGate.HasRequiredEffect("ime_update", baseline, first, second, ""), "COMPOSITION_EMPTY_UPDATE_REQUIRES_CANCEL");
        });
        Case("actual_composition_commit_cancel_require_fresh_total_End_noncomposing_and_commit_event_digest", () =>
        {
            CompositionEffectObservation baseline = EffectBase();
            var before = baseline with { composition_start = 1, composition_update = 3, composing = true, update_data_length = 4, update_data_sha256 = TextHash("niha") };
            var after = before with { composition_update = 4, composition_end = 1, composing = false, end_data_length = 2, end_data_sha256 = TextHash("你好") };
            Require(CompositionEffectGate.HasRequiredEffect("ime_commit", baseline, before, after, "你好"));
            Require(!CompositionEffectGate.HasRequiredEffect("ime_commit", baseline, before, after with { composition_end = 0 }, "你好"));
            Require(!CompositionEffectGate.HasRequiredEffect("ime_commit", baseline, before, after with { composing = true }, "你好"));
            Require(!CompositionEffectGate.HasRequiredEffect("ime_commit", baseline, before, after with { end_data_sha256 = TextHash("wrong") }, "你好"));
            Require(!CompositionEffectGate.HasRequiredEffect("ime_commit", baseline, after, after, "你好"));
            var cancelled = before with { composition_end = 1, composing = false, end_data_length = 0, end_data_sha256 = TextHash("") };
            Require(CompositionEffectGate.HasRequiredEffect("ime_cancel", baseline, before, cancelled, null));
            Require(!CompositionEffectGate.HasRequiredEffect("ime_cancel", baseline, before, before, null));
            Reject(() => CompositionEffectGate.HasRequiredEffect("ime_commit", baseline, before, after with { document_instance_sha256 = new string('b', 64) }, "你好"));
            Reject(() => CompositionEffectGate.HasRequiredEffect("ime_commit", baseline, before, after with { composition_update = 2 }, "你好"), "COMPOSITION_EVENT_COUNTER_REGRESSION");
        });
        Case("actual_composition_event_data_codec_requires_paired_bounded_digest_metadata", () =>
        {
            SessionHeader observed = H("input_result") with { request_id = 1, success = true, dom_composition_update_data_length = 1,
                dom_composition_update_data_sha256 = TextHash("n"), dom_composition_end_data_length = 2, dom_composition_end_data_sha256 = TextHash("你好") };
            SessionHeader copy = SessionProtocol.WithEvidence(H("input_result") with { request_id = 2, success = false }, observed);
            Require(RoundTrip(new(copy, []))?.header == copy && copy.dom_composition_end_data_length == 2 && copy.dom_composition_end_data_sha256 == TextHash("你好") && copy.success == false);
            Reject(() => SessionProtocol.ValidateHeader(observed with { dom_composition_update_data_sha256 = null }), "SESSION_COMPOSITION_EVENT_DATA_REJECTED");
            Reject(() => SessionProtocol.ValidateHeader(observed with { dom_composition_end_data_length = null }), "SESSION_COMPOSITION_EVENT_DATA_REJECTED");
            Reject(() => SessionProtocol.ValidateHeader(observed with { dom_composition_end_data_length = 1025 }), "SESSION_COMPOSITION_EVENT_DATA_REJECTED");
            Reject(() => SessionProtocol.ValidateHeader(observed with { dom_composition_end_data_sha256 = "bad" }), "SESSION_HASH_REQUIRED");
            Reject(() => CompositionEffectGate.RequireAdmission(EffectBase() with { update_data_length = 1 }, null), "COMPOSITION_EFFECT_EVENT_DATA_REJECTED");
            Reject(() => CompositionEffectGate.HashText("raw\0boundary"), "COMPOSITION_EFFECT_TEXT_BOUNDS_REQUIRED");
        });
        Case("native_end_trust_requires_real_uniform_control_and_exact_bridge_match", () =>
        {
            Reject(() => NativeEndTrustComparison.Measure(0, 0));
            Reject(() => NativeEndTrustComparison.Measure(2, 1));
            Reject(() => NativeEndTrustComparison.Measure(1, 2));
            bool nativeFalse = NativeEndTrustComparison.Measure(1, 0), nativeTrue = NativeEndTrustComparison.Measure(2, 2);
            Require(!nativeFalse && nativeTrue);
            Require(NativeEndTrustComparison.Matches(nativeFalse, 1, 0));
            Require(!NativeEndTrustComparison.Matches(nativeFalse, 0, 0));
            Require(!NativeEndTrustComparison.Matches(nativeFalse, 1, 1));
            Require(NativeEndTrustComparison.Matches(nativeTrue, 2, 2));
            Require(!NativeEndTrustComparison.Matches(nativeTrue, 2, 1));
        });
        NativeKeyDispatchGateSelfTests.Append(Case, reportPath);
        OwnedReportFileSelfTests.Append(Case, reportPath);
        var report = new
        {
            schema_version = 1, producer = "派蒙", status = failed == 0 ? "passed" : "failed", passed, failed, cases = results,
            scope = "actual_shared_codec_PNG_structure_inmemory_framing_input_native_composition_helpers_and_owned_fresh_file_IO_only_no_native_events_or_image_display",
            native_probe_calls = 0, logon_calls = 0, process_launches = 0, profile_operations = "not_run", station_operations = "not_run",
            browser_execution = "not_run", ui_execution = "not_run", clipboard_operations = "not_run", uac_requests = 0,
            native_call_scope = "project_probe_PInvoke_calls_only_not_a_claim_about_CLR_ZLib_or_fresh_report_file_IO", isolation_acceptance = "not_claimed"
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
        using (var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { output.Write(bytes); output.Flush(true); }
        Console.WriteLine(Encoding.UTF8.GetString(bytes)); return failed == 0 ? 0 : 1;
    }
    private static SessionHeader H(string kind) => new(1, Nonce, 1, kind);
    private static CompositionEffectObservation EffectBase() => new(new string('a', 64), true, true, 0, 0, 0, false, null, null, null, null);
    private static string TextHash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static SessionHeader Input(long id, string command) => H("input") with { request_id = id, command = command };
    private static SessionPacket Frame(long request, long frame, byte[] png) => new(H("frame") with { request_id = request, frame_id = frame, width = 800, height = 600 }, png);
    private static SessionPacket? RoundTrip(SessionPacket packet)
    { using var stream = new MemoryStream(Encode(packet)); return SessionProtocol.ReadAsync(stream, Nonce, 1, CancellationToken.None).GetAwaiter().GetResult(); }
    private static byte[] Encode(SessionPacket packet)
    { using var stream = new MemoryStream(); SessionProtocol.WriteAsync(stream, packet, CancellationToken.None).GetAwaiter().GetResult(); return stream.ToArray(); }
    private static byte[] Prefix(int count) { byte[] bytes = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, count); return bytes; }
    private static byte[] HeaderBytes(SessionHeader header) { byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(header); return [.. Prefix(bytes.Length), .. bytes]; }
    private static byte[] Png(int width = 800, int height = 600, int extraRawBytes = 0, byte scanlineFilter = 0, bool compressedMetadata = false)
    {
        using var stream = new MemoryStream(); stream.Write(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 });
        byte[] ihdr = new byte[13]; BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)width); BinaryPrimitives.WriteUInt32BigEndian(ihdr.AsSpan(4), (uint)height); ihdr[8] = 8; ihdr[9] = 6;
        Chunk(stream, "IHDR", ihdr);
        if (compressedMetadata) Chunk(stream, "iCCP", [65, 0, 0, 120, 156, 0]);
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, true))
        {
            byte[] row = new byte[width * 4 + 1]; row[0] = scanlineFilter;
            for (int y = 0; y < height; y++) zlib.Write(row.AsSpan(0, row.Length + (y == height - 1 && extraRawBytes < 0 ? extraRawBytes : 0)));
            if (extraRawBytes > 0) zlib.Write(new byte[extraRawBytes]);
        }
        Chunk(stream, "IDAT", compressed.ToArray()); Chunk(stream, "IEND", []); return stream.ToArray();
    }
    private static void Chunk(Stream stream, string name, byte[] data)
    {
        byte[] size = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(size, (uint)data.Length); stream.Write(size);
        byte[] content = [.. Encoding.ASCII.GetBytes(name), .. data]; stream.Write(content);
        BinaryPrimitives.WriteUInt32BigEndian(size, SessionProtocol.Crc32(content)); stream.Write(size);
    }
    private static void Reject(Action action, string? message = null)
    {
        try { action(); }
        catch (InvalidDataException ex) when (message is null || ex.Message == message) { return; }
        catch (JsonException) when (message is null) { return; }
        catch (EncoderFallbackException) when (message is null) { return; }
        catch (DecoderFallbackException) when (message is null) { return; }
        throw new InvalidDataException("CODEC_EXPECTED_REJECTION_MISSING");
    }
    private static void Require(bool condition) { if (!condition) throw new InvalidDataException("CODEC_SELF_TEST_ASSERTION_FAILED"); }
}
