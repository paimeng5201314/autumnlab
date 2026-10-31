using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AutumnOS.OwnedBrowserParent;

internal sealed class OwnedAssertionException(string code) : Exception(code) { internal string Code { get; } = code; }
internal static class Program
{
    private static readonly List<OwnedCase> Cases = [];
    private static string Status = "not_run", Stage = "arguments", Failure = "";
    private static string? ReportPath, ReportError, SourceId, ManifestHash;
    private static SystemDllBinding[] SystemDlls = [];
    private static uint MainThread;
    private static IntPtr BeforeDesktop;
    private static string? StationBefore, StationAfter, DesktopBefore, DesktopAfter;
    private static bool ContextUnchanged;
    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--protocol-self-test" && Path.IsPathFullyQualified(args[1])) return NativeProtocolSelfTests.Run(args[1]);
        if (args.Length != 1 || !Path.IsPathFullyQualified(args[0]))
        { Console.WriteLine("COMPILED ONLY. Explicit one-argument frozen input-manifest path required; no UAC or clipboard actions."); return 64; }
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) return 65;
        try
        {
            Native.Check(Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TokenQuery, out IntPtr token), "ordinary_desktop_native_parent_identity");
            try { if (Native.TokenInt(token, 20) != 0 || Native.TokenInt(token, 29) != 0) throw new OwnedAssertionException("UNELEVATED_NON_APPCONTAINER_PARENT_REQUIRED"); }
            finally { Native.CloseHandle(token); }
            using PreparedInputs inputs = ManifestInputs.Load(args[0]);
            SourceId = inputs.source_snapshot_id; ManifestHash = inputs.manifest_sha256; SystemDlls = inputs.system_dlls;
            string run = Path.Combine(inputs.case_root, "desktop-native-case-" + Guid.NewGuid().ToString("N"));
            OwnedFiles.CreatePrivateDirectory(run, null, false); ReportPath = Path.Combine(run, "report.json");
            MainThread = GetCurrentThreadId(); BeforeDesktop = GetThreadDesktop(MainThread);
            StationBefore = DesktopSecurity.CurrentStation(); DesktopBefore = UserName(BeforeDesktop);
            if (StationBefore != "WinSta0" || BeforeDesktop == IntPtr.Zero) throw new OwnedAssertionException("CURRENT_OWN_WINSTA0_DESKTOP_REQUIRED");
            Status = "running"; Save(); RequireReport();
            (string label, string side, uint mask)[] fixedCases = [("A87", "A", 0x87), ("B87", "B", 0x87), ("B200C7", "B", 0x200C7)];
            foreach (var item in fixedCases)
            {
                Stage = item.label; RequireReport(); inputs.RecheckSystemDlls();
                var runCase = new OwnedCase(item.label, item.side, item.mask, run, inputs, Save); Cases.Add(runCase); Save(); RequireReport();
                await runCase.RunAsync(); Save(); RequireReport();
                if (!runCase.cleanup_proven || !runCase.diagnostic_execution_completed) throw new OwnedAssertionException("DIAGNOSTIC_CASE_INCOMPLETE_NO_NEXT_CASE");
                if (item.side == "A" && !runCase.loader_succeeded) throw new OwnedAssertionException("ORDINARY_A_NATIVE_LAYER_POSITIVE_CONTROL_FAILED");
            }
            Stage = "final_context_and_exact_three_case_gate";
            StationAfter = DesktopSecurity.CurrentStation(); IntPtr after = GetThreadDesktop(MainThread); DesktopAfter = UserName(after);
            ContextUnchanged = StationAfter == StationBefore && after == BeforeDesktop && DesktopAfter == DesktopBefore;
            if (!ContextUnchanged || Cases.Count != 3 || Cases.Any(c => !c.diagnostic_execution_completed || !c.cleanup_proven)) throw new OwnedAssertionException("MATRIX_OR_ORIGINAL_THREAD_STATION_CONTEXT_FAILED");
            inputs.RecheckSystemDlls(); Status = "completed_diagnostic_matrix"; Stage = "complete"; Save(); RequireReport();
        }
        catch (Exception ex) { Status = "failed_diagnostic_execution"; Failure = FailureCode(ex); Save(); }
        if (ReportError is not null) { Status = "failed_report_io"; Save(); }
        try { Console.WriteLine(Status + "; report=" + ReportPath); } catch (Exception) { if (Status == "completed_diagnostic_matrix") Status = "failed_output"; }
        return Status == "completed_diagnostic_matrix" ? 0 : 1;
    }
    internal static string FailureCode(Exception ex) => ex.GetType().Name + ":0x" + unchecked((uint)ex.HResult).ToString("x8") +
        (ex is OwnedNativeException native ? ":stage=" + native.FixedStage + ":win32=" + native.NativeErrorCode :
            ex is OwnedAssertionException assertion ? ":assertion=" + assertion.Code : ex is Win32Exception error ? ":win32=" + error.NativeErrorCode : "");
    private static string UserName(IntPtr handle)
    { var text = new StringBuilder(512); Native.Check(GetUserObjectInformationW(handle, 2, text, text.Capacity * 2, out _), "original_thread_desktop_metadata"); return text.ToString(); }
    private static void RequireReport() { if (ReportError is not null) throw new OwnedAssertionException("DURABLE_REPORT_REQUIRED_BEFORE_NEXT_CASE"); }
    private static void Save()
    {
        if (ReportPath is null) return;
        try
        {
            var report = new { schema_version = 1, producer = "派蒙", kind = "ordinary_desktop_only_native_loader_fixed_matrix",
                status = Status, stage = Stage, failure = Failure, started_utc = Started, updated_utc = DateTimeOffset.UtcNow,
                source_snapshot_id = SourceId, manifest_sha256 = ManifestHash, report_error = ReportError,
                cases = Cases, cleanup_proven = Cases.All(c => c.cleanup_proven), diagnostic_execution_completed = Status == "completed_diagnostic_matrix",
                system_dlls_observed_metadata = SystemDlls, system_dlls_scope = "read_rechecked_disk_metadata_not_actual_loader_mapping_hash_proof",
                original_main_thread_id = MainThread, original_station_before = StationBefore, original_station_after = StationAfter,
                original_thread_desktop_before = DesktopBefore, original_thread_desktop_after = DesktopAfter, original_context_unchanged = ContextUnchanged,
                fixed_case_labels = new[] { "A87", "B87", "B200C7" }, fixed_desktop_package_masks = new uint[] { 0x87, 0x87, 0x200C7 },
                loader_outcome_scope = "per-case loader failures are observed results; completed matrix does not assert isolation",
                clipboard_operations = "not_run", browser_execution = "not_run", window_creation_calls_by_probe = 0,
                COM_initialization_calls_by_probe = 0, SwitchDesktop_calls = 0, profile_scope = "only_exact_new_profile_per_B",
                existing_WinSta0_or_Default_ACL_changes = "none", UAC_requests = 0, force_termination = "none", isolation_acceptance = "not_claimed",
                process_observation_scope = "suspended_original_token_and_exact_unnamed_job_membership_then_original_exit_Job0_EOF" };
            using (FileStream output = new(ReportPath + ".new", FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(output, report, new JsonSerializerOptions { WriteIndented = true }); output.Flush(true); }
            File.Move(ReportPath + ".new", ReportPath, true);
        }
        catch (Exception ex) { ReportError = FailureCode(ex); }
    }
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint id);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder text, int bytes, out uint required);
}
