using System.Text.Json;

namespace AutumnOS.OwnedBrowserParent;

// Synthetic protocol input only. The production parser is exercised without
// starting the child, querying OS identity, or opening a desktop/profile/Job.
internal static class NativeProtocolSelfTests
{
    private const string Nonce = "0123456789abcdef0123456789abcdef";
    private static readonly (string stage, string result)[] Success =
    [ ("native_entry", "entered"), ("environment_binding", "validated_not_identity_proof"),
      ("load_user32", "loading"), ("load_user32", "loaded"), ("user32_metadata_exports", "resolved"),
      ("user32_binding", "exact_station_and_desktop_matched"), ("load_gdi32", "loading"), ("load_gdi32", "loaded"),
      ("load_shell32", "loading"), ("load_shell32", "loaded") ];

    internal static int Run(string reportPath)
    {
        if (!Path.IsPathFullyQualified(reportPath)) throw new InvalidDataException("ABSOLUTE_FRESH_SELF_TEST_REPORT_REQUIRED");
        var cases = new List<object>(); int passed = 0, failed = 0;
        void Test(string name, Action action)
        {
            try { action(); cases.Add(new { name, status = "passed", exception_type = (string?)null, hresult = (string?)null }); passed++; }
            catch (Exception ex)
            {
                cases.Add(new { name, status = "failed", exception_type = ex.GetType().Name, hresult = "0x" + unchecked((uint)ex.HResult).ToString("x8") }); failed++;
            }
        }
        Test("complete_success_requires_all_ten_stages", () =>
        {
            NativeChild child = Fixture(); Prefix(child, Success.Length); Terminal(child, true);
            Require(child.failures.Count == 0 && child.events.Count == 11 && child.terminal_received && child.loader_completed &&
                child.failed_stage == "none" && child.native_error == 0);
        });
        Test("success_terminal_without_stages_rejected", () =>
        {
            NativeChild child = Fixture(); Terminal(child, true); Rejected(child);
        });
        Test("success_terminal_with_missing_final_stage_rejected", () =>
        {
            NativeChild child = Fixture(); Prefix(child, Success.Length - 1); Terminal(child, true); Rejected(child);
        });
        Test("explicit_user32_1114_prefix_retained_as_failed_loader", () =>
        {
            NativeChild child = Fixture(); Prefix(child, 3);
            Send(child, "load_user32", "failed", 1114, false, "load_user32"); Terminal(child, false, "load_user32", 1114);
            Require(child.failures.Count == 0 && child.events.Count == 5 && child.terminal_received && !child.loader_completed &&
                child.failed_stage == "load_user32" && child.native_error == 1114);
            JsonElement failure = child.events[3].GetProperty("detail");
            Require(failure.GetProperty("stage").GetString() == "load_user32" && failure.GetProperty("result").GetString() == "failed" &&
                failure.GetProperty("native_error").GetUInt32() == 1114);
        });
        Test("dll_failure_without_loading_prefix_rejected", () =>
        {
            NativeChild child = Fixture(); Prefix(child, 2);
            Send(child, "load_user32", "failed", 1114, false, "load_user32"); Terminal(child, false, "load_user32", 1114); Rejected(child);
        });
        Test("unknown_stage_or_result_rejected", () =>
        {
            NativeChild wrongStage = Fixture(); Send(wrongStage, "unknown", "entered"); Rejected(wrongStage);
            NativeChild wrongResult = Fixture(); Send(wrongResult, "native_entry", "unknown"); Rejected(wrongResult);
        });
        Test("duplicate_stage_rejected_and_failure_not_erased", () =>
        {
            NativeChild child = Fixture(); Prefix(child, 1); Send(child, "native_entry", "entered"); Rejected(child);
            for (int i = 1; i < Success.Length; i++) Send(child, Success[i].stage, Success[i].result);
            Terminal(child, true); Rejected(child);
        });
        Test("event_after_terminal_rejected_and_history_preserved", () =>
        {
            NativeChild child = Fixture(); Prefix(child, Success.Length); Terminal(child, true);
            Require(child.failures.Count == 0); Send(child, "native_entry", "entered"); Rejected(child);
            Require(child.events.Count == 11);
        });
        var report = new
        {
            schema_version = 1, producer = "派蒙", status = failed == 0 ? "passed" : "failed", passed, failed, cases,
            scope = "synthetic_events_into_actual_production_parser_only_no_child_Start_or_Tick",
            native_probe_calls = 0, process_execution = "not_run", profile_operations = "not_run", desktop_operations = "not_run",
            clipboard_operations = "not_run", uac_requests = 0, isolation_acceptance = "not_claimed",
            native_call_scope = "project_probe_PInvoke_calls_only_not_a_claim_about_CLR_or_fresh_report_file_IO"
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
        using (var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { output.Write(bytes); output.Flush(true); }
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(bytes));
        return failed == 0 ? 0 : 1;
    }
    private static NativeChild Fixture() => new("unused_no_Start", "unused_no_Start", Nonce, "B", "unused_no_Start", "unused_no_Start", IntPtr.Zero, null, () => { });
    private static void Prefix(NativeChild child, int count)
    { for (int i = 0; i < count; i++) Send(child, Success[i].stage, Success[i].result); }
    private static void Terminal(NativeChild child, bool passed, string failedStage = "none", uint error = 0) =>
        Send(child, "complete", passed ? "passed" : "failed", error, passed, failedStage, "native_loader_complete");
    private static void Send(NativeChild child, string stage, string result, uint error = 0, bool completed = false,
        string failedStage = "none", string kind = "native_loader_stage") => child.ParseProtocolLine(JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema_version = 1, nonce = Nonce, host_pid = 0, isolation_acceptance = "not_claimed_host_output_only", kind,
            detail = new { stage, result, native_error = error, preloaded = false, completed, failed_stage = failedStage }
        }));
    private static void Rejected(NativeChild child) => Require(child.failures.Count != 0);
    private static void Require(bool value) { if (!value) throw new InvalidDataException("NATIVE_PROTOCOL_SELF_TEST_ASSERTION_FAILED"); }
}
