using System.Text.Json;

namespace AutumnOS.OwnedBrowserParent;

internal static class StationMatrixSelfTests
{
    internal static int Run(string reportPath)
    {
        if (!Path.IsPathFullyQualified(reportPath)) throw new InvalidDataException("ABSOLUTE_FRESH_STATION_MATRIX_SELF_TEST_REPORT_REQUIRED");
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
        StationMatrixCase[] expected =
        [ new(0, "A23", "A", 0x23), new(1, "B23", "B", 0x23), new(2, "B20023", "B", 0x20023),
          new(3, "B20123", "B", 0x20123), new(4, "B20323", "B", 0x20323),
          new(5, "B23-restored", "B", 0x23), new(6, "A23-restored", "A", 0x23) ];
        Case("exact_fixed_seven_case_sequence_passes", () =>
        {
            Require(StationMatrixPolicy.Cases.SequenceEqual(expected));
            uint current = 0x23;
            foreach (StationMatrixCase item in expected)
            {
                StationMatrixPolicy.RequireTransition(item.index, item.index, item.label, item.mask, current, true, true);
                current = item.mask;
            }
            Require(current == 0x23);
        });
        Case("skip_replay_and_out_of_order_requests_rejected", () =>
        {
            foreach (StationMatrixCase item in expected)
            {
                uint before = item.index == 0 ? 0x23 : expected[item.index - 1].mask;
                foreach (StationMatrixCase other in expected.Where(c => c.index != item.index))
                    Reject(() => StationMatrixPolicy.RequireTransition(item.index, other.index, other.label, other.mask, before, true, true), "STATION_MATRIX_EXACT_ORDER_REQUIRED");
            }
        });
        Case("negative_and_exhausted_sequence_rejected", () =>
        {
            foreach (int next in new[] { -1, 7, int.MaxValue })
                Reject(() => StationMatrixPolicy.RequireTransition(next, next, "A23", 0x23, 0x23, true, true), "STATION_MATRIX_NO_NEXT_CASE");
        });
        Case("wrong_label_or_label_case_rejected", () =>
        {
            Reject(() => StationMatrixPolicy.RequireTransition(0, 0, "B23", 0x23, 0x23, true, true), "STATION_MATRIX_EXACT_CASE_SPEC_REQUIRED");
            Reject(() => StationMatrixPolicy.RequireTransition(0, 0, "a23", 0x23, 0x23, true, true), "STATION_MATRIX_EXACT_CASE_SPEC_REQUIRED");
        });
        Case("allowed_mask_for_wrong_case_rejected", () =>
            Reject(() => StationMatrixPolicy.RequireTransition(0, 0, "A23", 0x20023, 0x23, true, true), "STATION_MATRIX_EXACT_CASE_SPEC_REQUIRED"));
        Case("unexpected_current_allowed_mask_rejected", () =>
            Reject(() => StationMatrixPolicy.RequireTransition(2, 2, "B20023", 0x20023, 0x20123, true, true), "STATION_MATRIX_PREVIOUS_EXACT_MASK_REQUIRED"));
        Case("every_permission_bit_outside_fixed_union_rejected", () =>
        {
            foreach (uint mask in ForbiddenMasks())
                Reject(() => StationMatrixPolicy.RequireTransition(0, 0, "A23", mask, 0x23, true, true), "STATION_MATRIX_FIXED_MASK_ONLY");
        });
        Case("arbitrary_combination_of_allowed_bits_rejected", () =>
            Reject(() => StationMatrixPolicy.RequireTransition(0, 0, "A23", 0x123, 0x23, true, true), "STATION_MATRIX_FIXED_MASK_ONLY"));
        Case("transition_without_exact_owned_identity_rejected", () =>
            Reject(() => StationMatrixPolicy.RequireTransition(0, 0, "A23", 0x23, 0x23, false, true), "STATION_MATRIX_EXACT_OWNED_IDENTITY_REQUIRED"));
        Case("each_transition_with_live_or_unproven_previous_group_rejected", () =>
        {
            foreach (StationMatrixCase item in expected)
            {
                uint before = item.index == 0 ? 0x23 : expected[item.index - 1].mask;
                Reject(() => StationMatrixPolicy.RequireTransition(item.index, item.index, item.label, item.mask, before, true, false), "STATION_MATRIX_ALL_PREVIOUS_GROUPS_EXITED_REQUIRED");
            }
        });
        Case("restore_from_each_of_four_exact_masks_passes", () =>
        { foreach (uint mask in new uint[] { 0x23, 0x20023, 0x20123, 0x20323 }) StationMatrixPolicy.RequireRestore(mask, true, true); });
        Case("restore_from_unknown_or_forbidden_mask_rejected", () =>
        {
            foreach (uint mask in ForbiddenMasks().Concat(new uint[] { 0, 0x123 }))
                Reject(() => StationMatrixPolicy.RequireRestore(mask, true, true), "STATION_MATRIX_FIXED_MASK_ONLY");
        });
        Case("restore_without_exact_owned_identity_rejected", () =>
            Reject(() => StationMatrixPolicy.RequireRestore(0x20323, false, true), "STATION_MATRIX_EXACT_OWNED_IDENTITY_REQUIRED"));
        Case("restore_with_live_or_unproven_group_rejected", () =>
            Reject(() => StationMatrixPolicy.RequireRestore(0x20323, true, false), "STATION_MATRIX_ALL_PREVIOUS_GROUPS_EXITED_REQUIRED"));
        var report = new
        {
            schema_version = 1, producer = "派蒙", status = failed == 0 ? "passed" : "failed", passed, failed, cases = results,
            kind = "pure_actual_station_matrix_policy_regression", native_calls = 0, uac_requests = 0,
            native_call_scope = "project_probe_PInvoke_calls_only_not_a_claim_about_CLR_or_fresh_report_file_IO",
            process_execution = "not_run", profile_operations = "not_run", desktop_operations = "not_run", station_operations = "not_run",
            ACL_write_operations = "not_run", clipboard_operations = "not_run", isolation_acceptance = "not_claimed",
            scope = "actual_production_policy_only_synthetic_identity_and_exit_booleans_not_OS_evidence"
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
        using (var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { output.Write(bytes); output.Flush(true); }
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(bytes));
        return failed == 0 ? 0 : 1;
    }
    private static IEnumerable<uint> ForbiddenMasks()
    { for (int bit = 0; bit < 32; bit++) { uint flag = 1u << bit; if ((flag & 0x20323) == 0) yield return 0x23 | flag; } }
    private static void Reject(Action body, string expected)
    {
        try { body(); } catch (OwnedAssertionException ex) when (ex.Code == expected) { return; }
        throw new OwnedAssertionException("STATION_MATRIX_SELF_TEST_EXPECTED_REJECTION_MISSING");
    }
    private static void Require(bool value) { if (!value) throw new OwnedAssertionException("STATION_MATRIX_SELF_TEST_ASSERTION_FAILED"); }
}
