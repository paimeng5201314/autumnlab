using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutumnOS.OwnedBrowserParent;

internal static class Program
{
    internal static bool CancelRequested;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly List<ChildRun> Runs = [];
    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
    private static string Status = "not_run", Stage = "arguments", Failure = "", ManifestHash = "";
    private static string? FailureStage, FailureNativeStage, CleanupStage;
    private static string? ReportPath, ProfileName, ProfileFolder, PackageSidHash, ReportWriteFailure;
    private static string? PackageSidText;
    private static bool ProfileCreated, ProfileCleanupEligible;
    private static int? ProfileDeleteResult;
    private static bool? ProfileFolderRemains;
    private static uint? ProfileAttributes;
    private static int? ProfileAttributesError, ProfileSidPostQueryResult;
    private static bool ProfilePathNotFoundProven, ProfileSidNotFoundProven, ProfileSidPostReturnedPath;
    private static string ProfilePostcheckStage = "not_run";
    private static string UdfPlacement = "new_case_directory_for_A_new_OS_profile_child_directory_for_B";

    private static async Task<int> Main(string[] args)
    {
        // Build/inspection/no-argument invocation has no profile/ACL side effects.
        if (args.Length != 3 || args[0] != "--execute" || !Regex.IsMatch(args[1], "^[a-f0-9]{32}$") || !Path.IsPathFullyQualified(args[2]))
        { Console.WriteLine("Compiled ordinary-parent atomic JobList browser mechanism auditor. Run only with authorized --execute <fresh-32hex-case> <absolute-frozen-manifest.json>."); return 64; }
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) return 65;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; CancelRequested = true; };
        IntPtr package = IntPtr.Zero;
        try
        {
            Stage = "ordinary_parent_token";
            Native.Check(Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TokenQuery, out IntPtr token), Stage);
            try { if (Native.TokenInt(token, 20) != 0 || Native.TokenInt(token, 29) != 0) throw new InvalidDataException("ORDINARY_UNELEVATED_PARENT_REQUIRED"); }
            finally { Native.CloseHandle(token); }
            Stage = "verify_frozen_manifest";
            InputManifest manifest = OwnedFiles.ReadManifest(args[2]);
            using (var input = new FileStream(args[2], FileMode.Open, FileAccess.Read, FileShare.Read)) ManifestHash = OwnedFiles.Hash(input);
            if (!Path.GetPathRoot(manifest.case_root)!.Equals("D:\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("OWNED_D_VOLUME_CASE_ROOT_REQUIRED");
            string caseRoot = Path.Combine(manifest.case_root, "job-bound-browser-case-" + args[1]);
            OwnedFiles.CreatePrivateDirectory(caseRoot, null, false);
            ReportPath = Path.Combine(caseRoot, "report.json"); Status = "running"; Save(); RequireDurableReport();
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            OwnedFiles.FixedLocal(localAppData); OwnedFiles.PlainAncestors(localAppData);
            if (!Directory.Exists(localAppData)) throw new DirectoryNotFoundException("STANDARD_LOCALAPPDATA_REQUIRED");

            Stage = "prepare_ordinary_A";
            ChildRun a = Prepare("A", args[1], caseRoot, manifest, IntPtr.Zero, null, localAppData, null);
            Runs.Add(a); Save(); RequireDurableReport();
            Stage = "execute_ordinary_A"; await a.RunAsync(); Save();
            if (!a.Passed) throw new InvalidDataException("ORDINARY_BROWSER_POSITIVE_CONTROL_FAILED_NO_B_PROFILE_CREATED");
            if (CancelRequested) throw new OperationCanceledException("CANCELLED_BEFORE_PROFILE");
            RequireDurableReport();

            Stage = "create_one_new_profile";
            ProfileName = "AutumnOS.Paimon.BrowserProbe." + Guid.NewGuid().ToString("N"); Save(); RequireDurableReport();
            int hr = Native.CreateAppContainerProfile(ProfileName, "AutumnOS owned browser probe",
                "Temporary zero-capability browser compatibility probe; exact-owned cleanup only.", IntPtr.Zero, 0, out package);
            if (hr != 0) throw new COMException("CREATE_NEW_PROFILE_FAILED_NO_ADOPTION", hr);
            ProfileCreated = true; Save();
            if (package == IntPtr.Zero) throw new InvalidDataException("PROFILE_SID_MISSING");
            string sid = Native.SidString(package); PackageSidText = sid; PackageSidHash = OwnedFiles.HashText(sid);
            hr = Native.GetAppContainerFolderPath(sid, out IntPtr folder);
            if (hr != 0) throw new COMException("QUERY_OWN_PROFILE_FOLDER_FAILED", hr);
            try { ProfileFolder = Path.GetFullPath(Marshal.PtrToStringUni(folder) ?? throw new InvalidDataException("PROFILE_FOLDER_NULL")); }
            finally { Marshal.FreeCoTaskMem(folder); }
            OwnedFiles.FixedLocal(ProfileFolder); OwnedFiles.PlainAncestors(ProfileFolder);
            if (!Directory.Exists(ProfileFolder)) throw new DirectoryNotFoundException("NEW_OS_PROFILE_FOLDER_REQUIRED");

            // Only the directory CREATED above in this exact invocation is
            // re-ACL'd. Existing ancestors and OS profile ACL/SACL are untouched.
            Stage = "grant_new_case_only_package_read";
            OwnedFiles.PlainAncestors(caseRoot);
            new DirectoryInfo(caseRoot).SetAccessControl(OwnedFiles.Descriptor(sid, false));
            Stage = "prepare_restricted_B";
            ChildRun b = Prepare("B", args[1], caseRoot, manifest, package, sid, localAppData, ProfileFolder);
            Runs.Add(b); Save(); RequireDurableReport();
            Stage = "execute_restricted_B"; await b.RunAsync();
            ProfileCleanupEligible = b.cleanup_proven;
            Status = b.Passed ? "passed_sampled_browser_mechanism_only" : "failed";
            if (!b.Passed) { Failure = "RESTRICTED_BROWSER_MECHANISM_FAILED"; FailureStage = Stage; FailureNativeStage = b.failure_native_stage ?? b.last_native_stage; }
            Stage = "finished_observation";
        }
        catch (Exception ex)
        {
            Status = "failed"; Failure = FailureCode(ex); FailureStage = Stage;
            FailureNativeStage = (ex as OwnedNativeException)?.FixedStage;
            // Before a B child has been attempted there is no new browser
            // group to wait. The successfully-created exact profile is owned.
            if (ProfileCreated && !Runs.Any(r => r.mode == "B")) ProfileCleanupEligible = true;
            if (Runs.FirstOrDefault(r => r.mode == "B") is { } b) ProfileCleanupEligible = b.cleanup_proven;
        }
        finally
        {
            if (ProfileCreated && ProfileName is not null && ProfileCleanupEligible)
            {
                CleanupStage = "delete_exact_new_profile";
                ProfileDeleteResult = Native.DeleteAppContainerProfile(ProfileName);
                // Exact pre-delete path metadata: Directory.Exists(false)
                // would conflate genuine absence and ERROR_ACCESS_DENIED.
                if (ProfileFolder is not null)
                {
                    ProfilePostcheckStage = "original_profile_path_GetFileAttributesW";
                    uint attributes = Native.GetFileAttributesW(ProfileFolder);
                    int error = attributes == uint.MaxValue ? Marshal.GetLastWin32Error() : 0;
                    ProfileAttributes = attributes; ProfileAttributesError = error;
                    ProfilePathNotFoundProven = attributes == uint.MaxValue && error is 2 or 3;
                    ProfileFolderRemains = attributes != uint.MaxValue ? true : ProfilePathNotFoundProven ? false : null;
                }
                if (PackageSidText is not null)
                {
                    ProfilePostcheckStage = "same_created_SID_GetAppContainerFolderPath";
                    ProfileSidPostQueryResult = Native.GetAppContainerFolderPath(PackageSidText, out IntPtr postFolder);
                    ProfileSidPostReturnedPath = postFolder != IntPtr.Zero;
                    if (postFolder != IntPtr.Zero) Marshal.FreeCoTaskMem(postFolder);
                    ProfileSidNotFoundProven = ProfileSidPostQueryResult is unchecked((int)0x80070002) or unchecked((int)0x80070003);
                }
                if (ProfileDeleteResult != 0 || !ProfilePathNotFoundProven || !ProfileSidNotFoundProven)
                { Status = "failed_profile_cleanup"; Failure = "EXACT_PROFILE_DELETE_OR_FOLDER_POSTCHECK_FAILED_NO_BROAD_DELETE"; }
            }
            else if (ProfileCreated)
            { Status = "failed_profile_retained_cleanup_unproven"; if (Failure.Length == 0) Failure = "NO_NORMAL_ORIGINAL_HANDLE_FINAL_EMPTY_PROOF"; }
            if (package != IntPtr.Zero) Native.FreeSid(package);
            Save();
        }
        Console.WriteLine(Status + "; report=" + (ReportPath ?? "not_created") + "; complete_isolation=not_claimed");
        return Status == "passed_sampled_browser_mechanism_only" ? 0 : 1;
    }
    private static ChildRun Prepare(string mode, string nonce, string caseRoot, InputManifest manifest,
        IntPtr sid, string? sidText, string localAppData, string? profileFolder)
    {
        string phase = Path.Combine(caseRoot, mode), host = Path.Combine(phase, "host"), runtime = Path.Combine(phase, "runtime");
        OwnedFiles.CreatePrivateDirectory(phase, sidText, false);
        OwnedFiles.CopyFrozen(manifest.child_directory, host, manifest.child_files, sidText);
        OwnedFiles.CopyFrozen(manifest.runtime_directory, runtime, manifest.runtime_files, sidText);
        string data = profileFolder is null ? Path.Combine(phase, "data") : Path.Combine(profileFolder, "autumnos-udf-" + nonce);
        string temp = profileFolder is null ? Path.Combine(phase, "temp") : Path.Combine(profileFolder, "autumnos-temp-" + nonce);
        OwnedFiles.CreatePrivateDirectory(data, sidText, true); OwnedFiles.CreatePrivateDirectory(temp, sidText, true);
        return new ChildRun(mode, nonce, host, runtime, data, Path.Combine(host, manifest.child_executable), sid, sidText, localAppData, temp, Save);
    }
    private static void RequireDurableReport()
    { if (ReportWriteFailure is not null) throw new IOException("DURABLE_REPORT_REQUIRED_BEFORE_NEXT_SIDE_EFFECT"); }
    internal static string FailureCode(Exception ex) => ex.GetType().Name + ":0x" + unchecked((uint)ex.HResult).ToString("x8") +
        (ex is Win32Exception native ? ":win32=" + native.NativeErrorCode : "");
    private static void Save()
    {
        if (ReportPath is null) return;
        try
        {
            var report = new
            {
                schema_version = 1, producer = "派蒙", started_utc = Started, updated_utc = DateTimeOffset.UtcNow,
                status = Status, stage = Stage, failure = Failure, manifest_sha256 = ManifestHash,
                failure_stage = FailureStage, failure_native_stage = FailureNativeStage, cleanup_stage = CleanupStage,
                report_write_failure = ReportWriteFailure, profile_unique_name = ProfileName,
                profile_created_by_this_invocation = ProfileCreated, profile_sid_sha256 = PackageSidHash,
                profile_deletion_eligible = ProfileCleanupEligible, profile_deletion_hresult = ProfileDeleteResult,
                profile_folder_remains = ProfileFolderRemains, profile_path = "redacted_not_recorded",
                profile_postcheck_stage = ProfilePostcheckStage, original_profile_path_attributes = ProfileAttributes,
                original_profile_path_attributes_native_error = ProfileAttributesError,
                original_profile_path_not_found_proven = ProfilePathNotFoundProven,
                same_profile_SID_post_query_hresult = ProfileSidPostQueryResult,
                same_profile_SID_post_query_returned_path = ProfileSidPostReturnedPath,
                same_profile_SID_not_found_proven = ProfileSidNotFoundProven,
                udf_placement = UdfPlacement, requested_capabilities = 0, runs = Runs,
                process_observation_coverage = "specific_job_active_PID_list_original_handles_and_sampled_OS_SDK_identity_not_exhaustive_history",
                atomic_unnamed_job_list_required_for_each_A_B_host = true,
                specific_original_browser_job_membership_required_before_page = true,
                final_specific_job_active_zero_and_PID_list_empty_required_for_cleanup = true,
                job_kill_on_close_or_breakaway = "none_default_limits_verified_zero",
                complete_isolation = "not_claimed",
                isolation_acceptance = "not_claimed_mechanism_only", production_integration = "none",
                clipboard_operations = "not_run_none_in_this_probe", input_ime = "not_run",
                windows_profile_existing_acl_changes = "none", existing_ancestor_acl_changes = "none",
                new_owned_case_and_child_directory_acl_changes = "owner_and_exact_package_SID_only",
                force_termination = "none", global_policy_changes = "none", cancel_requested = CancelRequested
            };
            string temporary = ReportPath + ".new";
            OwnedFiles.PlainAncestors(ReportPath);
            using (FileStream file = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, report, Json); file.Flush(true); }
            File.Move(temporary, ReportPath, true);
        }
        catch (Exception ex) { ReportWriteFailure = FailureCode(ex); }
    }
}
