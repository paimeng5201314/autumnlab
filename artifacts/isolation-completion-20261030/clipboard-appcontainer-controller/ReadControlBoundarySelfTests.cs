using System.Text.Json;

namespace AutumnOS.OwnedBrowserParent;

internal static class ReadControlBoundarySelfTests
{
    internal static int Run(string reportPath)
    {
        if (!Path.IsPathFullyQualified(reportPath)) throw new InvalidDataException("ABSOLUTE_FRESH_READCONTROL_SELF_TEST_REPORT_REQUIRED");
        var cases = new List<object>(); int passed = 0, failed = 0;
        void Case(string name, Action body)
        {
            try { body(); passed++; cases.Add(new { name, status = "passed" }); }
            catch (Exception ex)
            {
                failed++; cases.Add(new { name, status = "failed", exception_type = ex.GetType().Name,
                    hresult = unchecked((uint)ex.HResult), assertion = (ex as OwnedAssertionException)?.Code });
            }
        }
        Case("initial_23_enable_without_any_started_process_passes", () => ReadControlBoundaryPolicy.RequireEnable(0x23, true, true));
        Case("runtime_exact_20023_passes", () => ReadControlBoundaryPolicy.RequireRuntime(0x20023, true));
        Case("restore_from_both_fixed_masks_after_all_groups_retire_passes", () =>
        {
            ReadControlBoundaryPolicy.RequireRestore(0x23, true, true);
            ReadControlBoundaryPolicy.RequireRestore(0x20023, true, true);
        });
        Case("enable_after_any_process_start_rejected_even_at_initial_mask", () =>
            Reject(() => ReadControlBoundaryPolicy.RequireEnable(0x23, true, false), "READCONTROL_BOUNDARY_ENABLE_BEFORE_ANY_PROCESS_REQUIRED"));
        Case("already_enabled_mask_cannot_be_enabled_again", () =>
            Reject(() => ReadControlBoundaryPolicy.RequireEnable(0x20023, true, true), "READCONTROL_BOUNDARY_INITIAL_EXACT_23_REQUIRED"));
        Case("runtime_initial_or_restored_23_rejected", () =>
            Reject(() => ReadControlBoundaryPolicy.RequireRuntime(0x23, true), "READCONTROL_BOUNDARY_RUNTIME_EXACT_20023_REQUIRED"));
        Case("all_operations_require_exact_owned_identity", () =>
        {
            Reject(() => ReadControlBoundaryPolicy.RequireEnable(0x23, false, true), "READCONTROL_BOUNDARY_EXACT_OWNED_IDENTITY_REQUIRED");
            Reject(() => ReadControlBoundaryPolicy.RequireRuntime(0x20023, false), "READCONTROL_BOUNDARY_EXACT_OWNED_IDENTITY_REQUIRED");
            Reject(() => ReadControlBoundaryPolicy.RequireRestore(0x23, false, true), "READCONTROL_BOUNDARY_EXACT_OWNED_IDENTITY_REQUIRED");
            Reject(() => ReadControlBoundaryPolicy.RequireRestore(0x20023, false, true), "READCONTROL_BOUNDARY_EXACT_OWNED_IDENTITY_REQUIRED");
        });
        Case("restore_with_any_live_or_unproven_group_rejected", () =>
        {
            Reject(() => ReadControlBoundaryPolicy.RequireRestore(0x23, true, false), "READCONTROL_BOUNDARY_RESTORE_ALL_GROUPS_RETIRED_REQUIRED");
            Reject(() => ReadControlBoundaryPolicy.RequireRestore(0x20023, true, false), "READCONTROL_BOUNDARY_RESTORE_ALL_GROUPS_RETIRED_REQUIRED");
        });
        Case("every_extra_permission_bit_rejected_by_all_three_gates", () =>
        {
            // Only base23 plus READ_CONTROL is allowed. This covers clipboard,
            // CREATEDESKTOP, WRITEATTRIBUTES, EXITWINDOWS, ENUMERATE/READSCREEN,
            // WRITE_DAC/OWNER, DELETE, generic and every other unnamed bit.
            for (int bit = 0; bit < 32; bit++)
            {
                uint flag = 1u << bit; if ((flag & 0x20023) != 0) continue;
                foreach (uint baseline in new uint[] { 0x23, 0x20023 })
                {
                    uint mask = baseline | flag;
                    Reject(() => ReadControlBoundaryPolicy.RequireEnable(mask, true, true), "READCONTROL_BOUNDARY_INITIAL_EXACT_23_REQUIRED");
                    Reject(() => ReadControlBoundaryPolicy.RequireRuntime(mask, true), "READCONTROL_BOUNDARY_RUNTIME_EXACT_20023_REQUIRED");
                    Reject(() => ReadControlBoundaryPolicy.RequireRestore(mask, true, true), "READCONTROL_BOUNDARY_RESTORE_FIXED_MASK_ONLY");
                }
            }
        });
        Case("incomplete_base_and_unknown_masks_rejected", () =>
        {
            foreach (uint mask in new uint[] { 0, 1, 2, 0x20, 0x22, 0x20000, 0x20003, uint.MaxValue })
            {
                Reject(() => ReadControlBoundaryPolicy.RequireEnable(mask, true, true), "READCONTROL_BOUNDARY_INITIAL_EXACT_23_REQUIRED");
                Reject(() => ReadControlBoundaryPolicy.RequireRuntime(mask, true), "READCONTROL_BOUNDARY_RUNTIME_EXACT_20023_REQUIRED");
                Reject(() => ReadControlBoundaryPolicy.RequireRestore(mask, true, true), "READCONTROL_BOUNDARY_RESTORE_FIXED_MASK_ONLY");
            }
        });
        Case("broader_native_matrix_masks_are_not_browser_boundary_masks", () =>
        {
            foreach (uint mask in new uint[] { 0x20123, 0x20323 })
            {
                Reject(() => ReadControlBoundaryPolicy.RequireEnable(mask, true, true), "READCONTROL_BOUNDARY_INITIAL_EXACT_23_REQUIRED");
                Reject(() => ReadControlBoundaryPolicy.RequireRuntime(mask, true), "READCONTROL_BOUNDARY_RUNTIME_EXACT_20023_REQUIRED");
                Reject(() => ReadControlBoundaryPolicy.RequireRestore(mask, true, true), "READCONTROL_BOUNDARY_RESTORE_FIXED_MASK_ONLY");
            }
        });
        var report = new
        {
            schema_version = 1, producer = "派蒙", status = failed == 0 ? "passed" : "failed", passed, failed, cases,
            kind = "pure_actual_readcontrol_boundary_policy_regression", native_calls = 0, uac_requests = 0,
            native_call_scope = "project_probe_PInvoke_calls_only_not_a_claim_about_CLR_or_fresh_report_file_IO",
            process_execution = "not_run", profile_operations = "not_run", desktop_operations = "not_run", station_operations = "not_run",
            ACL_write_operations = "not_run", clipboard_operations = "not_run", isolation_acceptance = "not_claimed",
            scope = "actual_production_policy_only_synthetic_identity_and_process_booleans_not_OS_evidence"
        };
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
        using (var output = new FileStream(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read)) { output.Write(bytes); output.Flush(true); }
        Console.WriteLine(System.Text.Encoding.UTF8.GetString(bytes));
        return failed == 0 ? 0 : 1;
    }
    private static void Reject(Action body, string expected)
    {
        try { body(); } catch (OwnedAssertionException ex) when (ex.Code == expected) { return; }
        throw new OwnedAssertionException("READCONTROL_BOUNDARY_SELF_TEST_EXPECTED_REJECTION_MISSING");
    }
}
