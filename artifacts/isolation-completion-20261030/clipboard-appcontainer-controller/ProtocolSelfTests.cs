using System.Text;
using System.Text.Json;

namespace AutumnOS.OwnedBrowserParent;

// These tests execute the same protocol parser and acceptance gates as the
// controller. They never Start/Tick a child, create a Job, or call a P/Invoke.
internal static class ProtocolSelfTests
{
    private const string Nonce = "368f27bc32ad4123b8c770f122af0f7e";
    private static readonly string EmptyHash = OwnedFiles.HashText("");
    private static readonly string SyntheticBaselineHash = OwnedFiles.HashText("owned-self-test-native-baseline");

    internal static int Run(string? reportPath = null)
    {
        var results = new List<object>(); int passed = 0, failed = 0;
        void Case(string name, Action body)
        {
            try { body(); passed++; results.Add(new { name, status = "passed" }); }
            catch (Exception ex)
            {
                failed++; results.Add(new { name, status = "failed", exception_type = ex.GetType().Name,
                    hresult = unchecked((uint)ex.HResult), assertion = (ex as OwnedAssertionException)?.Code });
            }
        }
        Case("valid_restricted_browser_completed_facts_pass", () => EvidenceGates.RequireCompleted(Completed(), final: true));
        Case("healthy_parsed_event_is_taken_once", () =>
        {
            PipeChild child = Browser("B"); Feed(child, "page_ready", new { loaded = true });
            int callbacks = 0;
            JsonElement? value = EvidenceGates.TakeHealthyEvent(Program.Facts(child), () => { callbacks++; return child.Take("page_ready"); });
            Require(callbacks == 1 && value?.GetProperty("loaded").GetBoolean() == true, "HEALTHY_EVENT_NOT_RETURNED");
            Require(child.Take("page_ready") is null && child.audit.Held.Count == 0 && !child.job.created, "PURE_EVENT_TAKE_OR_NO_NATIVE_BRANCH");
        });
        Case("same_batch_failure_blocks_valid_event_before_consumption", () =>
        {
            PipeChild child = Browser("B"); Feed(child, "page_ready", new { loaded = true });
            Feed(child, "operation_failed", new { stage = "synthetic_pending_failure" });
            int callbacks = 0;
            Reject("CHILD_FAILURE_HISTORY_PRESENT", () => EvidenceGates.TakeHealthyEvent(Program.Facts(child), () =>
            { callbacks++; return child.Take("page_ready"); }));
            Require(callbacks == 0 && child.Take("page_ready") is not null, "FAILURE_MUST_PRECEDE_EVENT_CONSUMPTION");
        });
        Case("late_renderer_wrong_package_is_rejected_at_final_gate", () =>
            Reject("FINAL_ALL_RESTRICTED_BROWSER_RENDERER_UTILITY_OS_TOKENS_REQUIRED", () => EvidenceGates.RequireCompleted(
                Completed() with { restricted_tokens = [GoodToken(), GoodToken(), GoodToken() with { expected_package_sid_matches = false }] }, true)));
        Case("late_utility_outside_appcontainer_is_rejected_at_final_gate", () =>
            Reject("FINAL_ALL_RESTRICTED_BROWSER_RENDERER_UTILITY_OS_TOKENS_REQUIRED", () => EvidenceGates.RequireCompleted(
                Completed() with { restricted_tokens = [GoodToken(), GoodToken(), GoodToken() with { is_appcontainer = 0 }] }, true)));
        Case("late_utility_extra_capability_is_rejected_at_final_gate", () =>
            Reject("FINAL_ALL_RESTRICTED_BROWSER_RENDERER_UTILITY_OS_TOKENS_REQUIRED", () => EvidenceGates.RequireCompleted(
                Completed() with { restricted_tokens = [GoodToken(), GoodToken(), GoodToken() with { capability_count = 1,
                    capability_sid_sha256 = [OwnedFiles.HashText("synthetic-extra-capability")] }] }, true)));
        Case("late_token_observation_failure_is_rejected_at_final_gate", () =>
            Reject("FINAL_ALL_RESTRICTED_BROWSER_RENDERER_UTILITY_OS_TOKENS_REQUIRED", () => EvidenceGates.RequireCompleted(
                Completed() with { restricted_tokens = [GoodToken(), GoodToken(), null] }, true)));
        Case("job_failure_history_survives_later_empty_and_zero_exit", () =>
            Reject("EXACT_JOB_HISTORY_HAS_FAILURE", () => EvidenceGates.RequireCompleted(Completed() with { job_query_failure = true }, true)));
        Case("process_observation_gap_survives_later_normal_exit", () =>
            Reject("OS_PROCESS_OR_TOKEN_OBSERVATION_GAP", () => EvidenceGates.RequireCompleted(Completed() with { observation_gap = true }, true)));
        Case("pending_original_or_unread_stdout_cannot_pass", () =>
            Reject("FINAL_ORIGINAL_GROUP_ZERO_EXIT_AND_STDIO_PROOF_REQUIRED", () => EvidenceGates.RequireCompleted(
                Completed() with { original_group_exit = false, stdout_drained = false }, true)));
        Case("active_job_cannot_pass_even_when_sampled_process_handles_exited", () =>
            Reject("FINAL_ATOMIC_JOB_ACTIVE_ZERO_REQUIRED", () => EvidenceGates.RequireCompleted(Completed() with { job_empty = false }, true)));
        Case("tenth_snapshot_does_not_hide_eleventh_late_paste_event", () =>
        {
            PipeChild child = Browser("B"); child.Send("perform");
            for (int index = 1; index <= 10; index++) Paste(child, index, EmptyHash, true);
            Feed(child, "paste_sample", new { sample_index = 10, complete = true, value_sha256 = EmptyHash, value_empty = true });
            Require(child.failures.Count == 0 && child.paste_events.Count == 10, "EMPTY_PASTE_POSITIVE_FIXTURE_INVALID");
            Paste(child, 11, SyntheticBaselineHash, false);
            Require(child.paste_events.Count == 11 && child.failures.Contains("RESTRICTED_BROWSER_ACTUAL_PASTE_DATA_LEAK"), "LATE_PASTE_LEAK_NOT_RETAINED");
            Reject("CHILD_FAILURE_HISTORY_PRESENT", () => EvidenceGates.RequireHealthy(Program.Facts(child)));
        });
        Case("ordinary_paste_positive_control_matches_native_only_hash_and_terminal_summary", () =>
        {
            PipeChild child = Browser("A"); child.BindNativePasteBaselineHash(SyntheticBaselineHash); child.Send("perform");
            Paste(child, 1, SyntheticBaselineHash, false); Complete(child, SyntheticBaselineHash, false);
            EvidenceGateFacts facts = Program.Facts(child);
            Require(child.failures.Count == 0 && facts.paste_summary_matches && facts.paste_capture_complete && facts.paste_final_snapshot &&
                facts.paste_events_expected && facts.paste_final_dom_expected, "ORDINARY_PASTE_POSITIVE_CONTROL_OR_SUMMARY_INVALID");
            EvidenceGates.RequireHealthy(facts);
        });
        Case("restricted_empty_capture_and_terminal_summary_pass", () =>
        {
            PipeChild child = Browser("B"); child.Send("perform"); Complete(child, EmptyHash, true);
            EvidenceGateFacts facts = Program.Facts(child);
            Require(child.failures.Count == 0 && facts.paste_summary_matches && facts.paste_events_expected && facts.paste_final_dom_expected,
                "RESTRICTED_EMPTY_CAPTURE_POSITIVE_FIXTURE_INVALID");
            EvidenceGates.RequireCompleted(Completed() with { paste_summary_matches = facts.paste_summary_matches,
                paste_capture_complete = facts.paste_capture_complete, paste_final_snapshot = facts.paste_final_snapshot,
                paste_events_expected = facts.paste_events_expected, paste_final_dom_expected = facts.paste_final_dom_expected }, true);
        });
        Case("terminal_digest_mismatch_is_specific_failure_with_all_fields_present", () =>
        {
            PipeChild child = Browser("B"); child.Send("perform"); Paste(child, 1, EmptyHash, true);
            Complete(child, EmptyHash, true, digest: OwnedFiles.HashText("deliberately-mismatched-terminal-ledger"));
            Require(child.failures.Contains("TERMINAL_PASTE_ACCUMULATION_MISMATCH") && !child.failures.Any(item => item.StartsWith("PROTOCOL_", StringComparison.Ordinal)),
                "DIGEST_TEST_MUST_REACH_ACTUAL_SUMMARY_COMPARISON");
            Reject("CHILD_FAILURE_HISTORY_PRESENT", () => EvidenceGates.RequireHealthy(Program.Facts(child)));
        });
        Case("untrusted_paste_event_and_event_after_terminal_are_rejected", () =>
        {
            PipeChild untrusted = Browser("B"); Paste(untrusted, 1, EmptyHash, true, trusted: false);
            Require(untrusted.failures.Contains("PASTE_EVENT_UNTRUSTED"), "UNTRUSTED_EVENT_NOT_REJECTED");
            PipeChild terminal = Browser("B"); Complete(terminal, EmptyHash, true); Paste(terminal, 1, EmptyHash, true);
            Require(terminal.failures.Any(item => item.StartsWith("PROTOCOL_", StringComparison.Ordinal)), "EVENT_AFTER_TERMINAL_NOT_REJECTED");
        });
        Case("wrong_nonce_cannot_enter_real_protocol_event_ledger", () =>
        {
            PipeChild child = Browser("B"); Feed(child, "page_ready", new { loaded = true }, nonce: "00000000000000000000000000000000");
            Require(child.events.Count == 0 && child.failures.Any(item => item.StartsWith("PROTOCOL_", StringComparison.Ordinal)), "NONCE_BINDING_NOT_ENFORCED");
        });
        Case("final_paste_capture_ack_is_required_after_normal_exit", () =>
            Reject("FINAL_PASTE_CAPTURE_NOT_COMPLETE", () => EvidenceGates.RequireCompleted(Completed() with { paste_capture_complete = false }, true)));
        Case("durable_report_error_is_never_accepted_as_success", () =>
        {
            EvidenceGates.RequireDurableReport(null);
            Reject("DURABLE_REPORT_REQUIRED", () => EvidenceGates.RequireDurableReport("synthetic_report_io_error"));
        });
        Case("native_loader_complete_fixed_stage_sequence_is_accepted_as_metadata_only", () =>
        {
            PipeChild child = NativeLoader(); NativeLoaderSuccess(child);
            Require(child.failures.Count == 0 && child.events.Count == 11 && child.native_loader_report_received &&
                child.native_loader_completed && child.native_loader_failed_stage == "none" && child.native_loader_native_error == 0 &&
                child.native_loader_last_stage == "complete" && child.native_loader_last_result == "passed" && child.native_loader_last_native_error == 0,
                "COMPLETE_NATIVE_LOADER_METADATA_SEQUENCE_NOT_RETAINED");
            Require(child.audit.Held.Count == 0 && !child.job.created, "SELF_TEST_MUST_NOT_START_NATIVE_CHILD_OR_JOB");
        });
        Case("native_loader_USER32_1114_is_retained_as_completed_failed_report", () =>
        {
            PipeChild child = NativeLoader();
            NativeStage(child, "native_entry", "entered"); NativeStage(child, "environment_binding", "validated_not_identity_proof");
            NativeStage(child, "load_user32", "loading"); NativeStage(child, "load_user32", "failed", 1114);
            Feed(child, "native_loader_complete", new { stage = "complete", result = "failed", native_error = 1114,
                completed = false, failed_stage = "load_user32" });
            Require(child.failures.Count == 0 && child.native_loader_report_received && !child.native_loader_completed &&
                child.native_loader_failed_stage == "load_user32" && child.native_loader_native_error == 1114 &&
                child.native_loader_last_stage == "complete" && child.native_loader_last_result == "failed" && child.native_loader_last_native_error == 1114,
                "FAILED_NATIVE_LOADER_REPORT_MUST_NOT_BECOME_MECHANISM_SUCCESS");
            JsonElement actualFailure = child.events.Single(item => item.GetProperty("kind").GetString() == "native_loader_stage" &&
                item.GetProperty("detail").GetProperty("result").GetString() == "failed").GetProperty("detail");
            Require(actualFailure.GetProperty("stage").GetString() == "load_user32" && actualFailure.GetProperty("result").GetString() == "failed" &&
                actualFailure.GetProperty("native_error").GetUInt32() == 1114, "EXACT_NATIVE_FAILURE_STAGE_RESULT_ERROR_NOT_PRESERVED");
        });
        Case("native_loader_unknown_stage_and_result_are_protocol_failures", () =>
        {
            PipeChild unknownStage = NativeLoader(), unknownResult = NativeLoader();
            NativeStage(unknownStage, "unknown_stage", "loaded"); NativeStage(unknownResult, "load_user32", "unknown_result");
            foreach (PipeChild child in new[] { unknownStage, unknownResult })
            {
                Require(child.events.Count == 0 && !child.native_loader_report_received &&
                    child.failures.Count == 1 && child.failures[0].StartsWith("PROTOCOL_", StringComparison.Ordinal),
                    "UNKNOWN_NATIVE_STAGE_RESULT_MUST_BE_REJECTED_BEFORE_RECORDING");
                Reject("CHILD_FAILURE_HISTORY_PRESENT", () => EvidenceGates.RequireHealthy(Program.Facts(child)));
            }
        });
        Case("native_loader_event_after_terminal_preserves_protocol_failure", () =>
        {
            PipeChild child = NativeLoader(); NativeLoaderSuccess(child); int count = child.events.Count;
            NativeStage(child, "load_gdi32", "loaded");
            Require(child.events.Count == count && child.native_loader_report_received && child.native_loader_completed &&
                child.failures.Count == 1 && child.failures[0].StartsWith("PROTOCOL_", StringComparison.Ordinal),
                "LATE_NATIVE_EVENT_MUST_NOT_AMEND_COMPLETED_LEDGER");
            Reject("CHILD_FAILURE_HISTORY_PRESENT", () => EvidenceGates.RequireHealthy(Program.Facts(child)));
        });

        var report = new { schema_version = 1, producer = "派蒙", kind = "pure_controller_protocol_and_acceptance_gate_self_tests",
            status = failed == 0 ? "passed" : "failed", total = passed + failed, passed, failed,
            native_calls = 0, native_calls_scope = "No project PInvoke/native experiment methods invoked; CLR and report filesystem IO are outside this counter.",
            profile_operations = "not_run", clipboard_operations = "not_run", uac_requests = 0, process_launches = 0,
            mechanism_acceptance = "not_claimed", results };
        string json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            if (reportPath is not null)
            {
                if (!Path.IsPathFullyQualified(reportPath)) throw new InvalidDataException("ABSOLUTE_NEW_REPORT_REQUIRED");
                using FileStream file = new(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] bytes = Encoding.UTF8.GetBytes(json); file.Write(bytes); file.Flush(true);
            }
            Console.WriteLine(json); return failed == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine(JsonSerializer.Serialize(new { status = "failed_report_output", exception_type = ex.GetType().Name,
                hresult = unchecked((uint)ex.HResult), native_calls = 0, profile_operations = "not_run", clipboard_operations = "not_run", uac_requests = 0 }));
            return 1;
        }
    }

    private static PipeChild Browser(string side) => new("browser", Nonce, side, "paste", "unused-host", "unused-runtime", "unused-udf",
        "unused-temp", "unused.exe", IntPtr.Zero, null, new StationLease());
    private static PipeChild NativeLoader() => new("native", Nonce, "B", "native_loader", "unused-host", "unused-runtime", "unused-udf",
        "unused-temp", "unused.exe", IntPtr.Zero, null, new StationLease());
    private static void NativeStage(PipeChild child, string stage, string result, uint nativeError = 0) =>
        Feed(child, "native_loader_stage", new { stage, result, native_error = nativeError });
    private static void NativeLoaderSuccess(PipeChild child)
    {
        NativeStage(child, "native_entry", "entered"); NativeStage(child, "environment_binding", "validated_not_identity_proof");
        NativeStage(child, "load_user32", "loading"); NativeStage(child, "load_user32", "loaded");
        NativeStage(child, "user32_metadata_exports", "resolved"); NativeStage(child, "user32_binding", "exact_station_and_desktop_matched");
        NativeStage(child, "load_gdi32", "loading"); NativeStage(child, "load_gdi32", "loaded");
        NativeStage(child, "load_shell32", "loading"); NativeStage(child, "load_shell32", "loaded");
        Feed(child, "native_loader_complete", new { stage = "complete", result = "passed", native_error = 0, completed = true, failed_stage = "none" });
    }
    private static TokenFacts GoodToken() => new(1, OwnedFiles.HashText("synthetic-exact-package"), true, 4096, 0, [], true);
    private static EvidenceGateFacts Completed() => new()
    {
        original_group_exit = true, host_exit_zero = true, stdout_drained = true, atomic_job = true, job_empty = true,
        retired = true, job_closed = true, browser = true, browser_normal = true, sdk_empty = true, host_complete = true,
        preload_verified = true, restricted = true, restricted_tokens = [GoodToken(), GoodToken()],
        paste_summary_matches = true, paste_performed = true, paste_capture_complete = true, paste_final_snapshot = true,
        paste_events_expected = true, paste_final_dom_expected = true
    };
    private static void Feed(PipeChild child, string kind, object detail, string nonce = Nonce) => child.ParseProtocolLine(JsonSerializer.SerializeToUtf8Bytes(new
    { schema_version = 1, nonce, host_pid = 0, kind, detail, isolation_acceptance = "not_claimed_host_output_only" }));
    private static void Paste(PipeChild child, int index, string hash, bool empty, bool trusted = true) =>
        Feed(child, "paste_event_data", new { event_index = index, trusted, text_sha256 = hash, text_empty = empty });
    private static void Complete(PipeChild child, string? finalHash, bool? empty, string? digest = null) => Feed(child, "host_complete", new
    {
        environment_process_count = 0, browser_pid = 0, paste_event_count = child.paste_events.Count,
        paste_nonempty_event_count = child.paste_events.Count(item => !item.text_empty), paste_untrusted_event_count = child.paste_events.Count(item => !item.trusted),
        paste_event_digest_sha256 = digest ?? child.paste_event_digest_sha256, paste_capture_complete = true,
        paste_final_snapshot_observed = true, paste_final_value_sha256 = finalHash, paste_final_value_empty = empty
    });
    private static void Require(bool condition, string code) { if (!condition) throw new InvalidDataException(code); }
    private static void Reject(string expectedCode, Action body)
    {
        try { body(); }
        catch (OwnedAssertionException error) when (error.Code == expectedCode) { return; }
        throw new InvalidDataException("EXPECTED_SPECIFIC_ACCEPTANCE_REJECTION_" + expectedCode);
    }
}
