using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutumnOS.OwnedBrowserParent;

internal sealed record ClipboardManifest(int schema_version, string case_root, string child_directory, string runtime_directory,
    string child_executable, string runtime_version, FileBinding[] child_files, FileBinding[] runtime_files,
    string broker_executable, string broker_sha256, string? trial_mode = null, string? native_child_directory = null,
    string? native_child_executable = null, FileBinding[]? native_child_files = null);

internal sealed class OwnedAssertionException(string code) : Exception(code)
{ internal string Code { get; } = code; }

internal static class Program
{
    private static readonly List<PipeChild> Children = [];
    private static readonly List<object> Cases = [];
    private static readonly StationLease Lease = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;
    private static string Status = "not_run", Stage = "arguments", Failure = "", ManifestHash = "";
    private static string? ReportPath, FailureStage, ReportError, ProfileName, ProfileFolder, PackageSid;
    private static string? CaseRoot, AHost, ARuntime, BHost, BRuntime;
    private static bool CancelRequested, ProfileCreated, CleanupProven;
    private static int? DeleteProfileResult, ProfilePostQueryResult, ProfilePathError;
    private static uint? ProfilePathAttributes;
    private static bool ProfilePathNotFound, ProfileSidNotFound;
    private static int SeedCommandsSent;
    private static readonly CleanupJournal Cleanup = new();
    private static string CleanupStage = "not_started";
    private static bool DerivedSidFreed, CreatedSidFreed;
    private static string TrialMode = "clipboard_sixcase";
    private static bool NativeOnly => TrialMode is "native_loader_diagnostic" or "native_station_read_matrix";
    private static StationReadMatrix? StationMatrix;
    private static bool ReadControlSixCase => TrialMode == "clipboard_readcontrol_sixcase";
    private static string PassedStatus => ReadControlSixCase ? "passed_owned_private_station_readcontrol_sixcase_mechanism_only" :
        TrialMode == "native_station_read_matrix" ? "completed_native_station_read_matrix_only" :
        TrialMode == "native_loader_diagnostic" ? "passed_native_loader_diagnostic_only" : "passed_owned_private_station_sixcase_mechanism_only";

    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test") return ProtocolSelfTests.Run();
        if (args.Length == 2 && args[0] == "--self-test" && Path.IsPathFullyQualified(args[1])) return ProtocolSelfTests.Run(args[1]);
        if (args.Length == 2 && args[0] == "--desktop-guard-self-test" && Path.IsPathFullyQualified(args[1])) return DesktopFreshness.RunNativeSelfTest(args[1]);
        if (args.Length == 2 && args[0] == "--cleanup-self-test" && Path.IsPathFullyQualified(args[1])) return CleanupSelfTests.Run(args[1]);
        if (args.Length == 2 && args[0] == "--native-protocol-self-test" && Path.IsPathFullyQualified(args[1])) return NativeProtocolSelfTests.Run(args[1]);
        if (args.Length == 2 && args[0] == "--station-matrix-self-test" && Path.IsPathFullyQualified(args[1])) return StationMatrixSelfTests.Run(args[1]);
        if (args.Length == 2 && args[0] == "--readcontrol-boundary-self-test" && Path.IsPathFullyQualified(args[1])) return ReadControlBoundarySelfTests.Run(args[1]);
        if (args.Length != 5 || args[0] != "--execute" || !Regex.IsMatch(args[1], "^[a-f0-9]{32}$") ||
            !Path.IsPathFullyQualified(args[2]) || !uint.TryParse(args[3], out uint holder) || !uint.TryParse(args[4], out uint broker))
        { Console.WriteLine("COMPILED ONLY. Authorized external lease broker required: --execute nonce manifest holderPid brokerPid."); return 64; }
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) return 65;
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; CancelRequested = true; };
        IntPtr derived = IntPtr.Zero, created = IntPtr.Zero;
        try
        {
            Stage = "ordinary_controller_identity";
            Native.Check(Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TokenQuery, out IntPtr token), Stage);
            try { if (Native.TokenInt(token, 20) != 0 || Native.TokenInt(token, 29) != 0) throw new InvalidDataException("ORDINARY_UNELEVATED_CONTROLLER_REQUIRED"); }
            finally { Native.CloseHandle(token); }
            Stage = "verify_frozen_inputs";
            var manifest = ReadManifest(args[2]);
            TrialMode = manifest.trial_mode ?? "clipboard_sixcase";
            CaseRoot = Path.Combine(manifest.case_root, "clipboard-case-" + args[1]);
            OwnedFiles.CreatePrivateDirectory(CaseRoot, null, false); ReportPath = Path.Combine(CaseRoot, "report.json"); Status = "running"; Save(); RequireReport();
            ProfileName = "AutumnOS.Paimon.ClipboardProbe." + args[1];
            int hr = DeriveAppContainerSidFromAppContainerName(ProfileName, out derived);
            if (hr != 0 || derived == IntPtr.Zero) throw new COMException("DERIVE_EXACT_UNREGISTERED_PROFILE_SID", hr);
            PackageSid = Native.SidString(derived);
            Stage = "verify_external_authenticated_holder_and_create_own_desktop";
            Lease.Open(args[1], holder, broker, manifest.broker_executable, manifest.broker_sha256, PackageSid); Save(); RequireReport();
            if (ReadControlSixCase)
            {
                Stage = "enable_owned_station_READ_CONTROL_only_before_any_child";
                Lease.EnableClipboardReadControl(PackageSid, Children); Save(); RequireReport();
            }
            Stage = "copy_ordinary_frozen_child_runtime";
            if (TrialMode == "native_station_read_matrix")
            {
                StationMatrix = new(Lease, manifest, CaseRoot, PackageSid, Save, RequireReport);
                Stage = "station_read_matrix_A23_ordinary_positive"; await StationMatrix.RunInitialAAsync();
            }
            else if (TrialMode == "native_loader_diagnostic")
            {
                AHost = CopyNativeSide("A", manifest, null); ARuntime = AHost;
                Stage = "ordinary_private_station_native_loader_no_browser_or_clipboard";
                await RunNativeLoader("A", manifest, IntPtr.Zero);
            }
            else
            {
                (AHost, ARuntime) = CopySide("A", manifest, null);
                Stage = "ordinary_private_station_preflight_no_clipboard";
                await BrowserPreflight("A", manifest, IntPtr.Zero, args[1]);
            }

            Stage = "create_exact_new_profile"; Save(); RequireReport();
            hr = Native.CreateAppContainerProfile(ProfileName, "AutumnOS clipboard boundary probe", "Temporary exact-owned zero-capability private-station test.", IntPtr.Zero, 0, out created);
            if (hr != 0) throw new COMException("EXACT_PROFILE_CREATE_FAILED_NO_ADOPTION", hr);
            ProfileCreated = true; Save();
            if (created == IntPtr.Zero || Native.SidString(created) != PackageSid) throw new InvalidDataException("CREATED_SID_MUST_MATCH_DERIVED_STATION_PRINCIPAL");
            hr = Native.GetAppContainerFolderPath(PackageSid, out IntPtr folder);
            if (hr != 0) throw new COMException("QUERY_EXACT_CREATED_PROFILE_PATH", hr);
            try { ProfileFolder = Path.GetFullPath(Marshal.PtrToStringUni(folder) ?? throw new InvalidDataException("PROFILE_PATH_NULL")); }
            finally { Marshal.FreeCoTaskMem(folder); }
            OwnedFiles.FixedLocal(ProfileFolder); OwnedFiles.PlainAncestors(ProfileFolder);
            new DirectoryInfo(CaseRoot).SetAccessControl(OwnedFiles.Descriptor(PackageSid, false));
            Stage = "copy_restricted_frozen_child_runtime";
            if (TrialMode == "native_station_read_matrix")
            {
                Stage = "station_read_matrix_remaining_six_fixed_cases"; await StationMatrix!.RunAfterProfileAsync(created, ProfileFolder);
            }
            else if (TrialMode == "native_loader_diagnostic")
            {
                BHost = CopyNativeSide("B", manifest, PackageSid); BRuntime = BHost;
                Stage = "restricted_private_station_native_loader_no_browser_or_clipboard";
                await RunNativeLoader("B", manifest, created);
            }
            else
            {
                (BHost, BRuntime) = CopySide("B", manifest, PackageSid);
                Stage = "restricted_private_station_preflight_no_clipboard";
                await BrowserPreflight("B", manifest, created, args[1]);
                foreach (string operation in new[] { "copy", "cut", "paste" })
                    foreach (string side in new[] { "A", "B" })
                    { Stage = side + "_" + operation + "_sixcase"; Save(); RequireReport(); await RunClipboardCase(side, operation, manifest, side == "A" ? IntPtr.Zero : created); }
            }
            Stage = "final_all_children_fact_gate"; TickAll(); RequireReport();
            foreach (var child in Children) AssertSuccessfulChild(child, final: true);
            VerifyReadControl("runtime_final_all_case_facts");
            if (StationMatrix is not null && (!StationMatrix.native_mode_completed || !StationMatrix.restored_original_DACL || !StationMatrix.AllDiagnosticGroupsComplete))
                throw new OwnedAssertionException("STATION_READ_MATRIX_ALL_SEVEN_AND_RESTORE_REQUIRED");
            Status = PassedStatus; Stage = "diagnostic_or_sixcase_complete"; Save(); RequireReport();
        }
        catch (Exception ex) { Status = "failed"; Failure = FailureCode(ex); FailureStage = Stage; }
        finally
        {
            bool finished = false;
            while (!finished)
            {
                try { await CleanupOwnedRun(derived, created); finished = true; }
                catch (Exception ex)
                {
                    // Last-resort barrier for unforeseen managed cleanup
                    // exceptions. Primary failure stays intact; no finally
                    // exception may discard remaining owned cleanup stages.
                    try { Cleanup.Attempt("cleanup_outer_barrier_" + CleanupStage, () => throw ex); } catch (Exception) { }
                    Status = "failed_pending_cleanup_exception_retained"; Save();
                    try { await Task.Delay(250); } catch (Exception) { }
                }
            }
        }
        try { Console.WriteLine(Status + "; report=" + ReportPath); }
        catch (Exception ex) { Cleanup.Attempt("final_console_output", () => throw ex, critical: false); Status = "failed_output"; Save(); }
        return Status == PassedStatus ? 0 : 1;
    }
    private static async Task CleanupOwnedRun(IntPtr derived, IntPtr created)
    {
        bool primaryPassed = Status == PassedStatus;
        CleanupStage = "request_cooperative_child_close";
        foreach (var child in Children.Where(c => !c.retired))
        {
            if (!primaryPassed) child.EnterFailureCleanup();
            if (!child.no_child_created_proven) Cleanup.Attempt("request_owned_child_close", () => child.Send("close"), critical: false);
        }
        CleanupStage = "observe_owned_original_groups";
        while (Children.Any(c => !c.resource_cleanup_proven))
        {
            Status = "failed_pending_owned_original_group_exit_proof";
            foreach (var child in Children.Where(c => !c.retired)) Cleanup.Attempt("observe_child_original_group", child.Tick);
            Save(); await Task.Delay(250);
        }
        CleanupStage = "retire_each_owned_child_resource";
        // Broken writer flush is a failed cleanup action, but every transport,
        // Job and original process handle is still independently attempted.
        while (Children.Any(c => !c.retired))
        {
            foreach (var child in Children.Where(c => !c.retired)) Cleanup.Attempt("retire_owned_child_resources", child.ClosePipesAfterOriginalExit);
            if (Children.Any(c => !c.retired)) { Status = "failed_pending_child_resource_close"; Save(); await Task.Delay(250); }
        }
        CleanupStage = "retire_native_station_matrix_groups_and_restore_initial_DACL";
        if (StationMatrix is not null)
        {
            bool restored = false;
            while (!restored)
            {
                try
                {
                    await StationMatrix.EnsureRetiredAndRestoreAsync();
                    restored = !Lease.station_read_matrix_started || StationMatrix.AllOwnedResourcesRetired && StationMatrix.restored_original_DACL;
                }
                catch (Exception ex) { Cleanup.Attempt("station_matrix_owned_group_retirement_or_initial_DACL_restore", () => throw ex); }
                if (!restored) { Status = "failed_pending_native_station_matrix_restore"; Save(); await Task.Delay(250); }
            }
        }
        CleanupStage = "restore_clipboard_READ_CONTROL_initial_DACL_after_all_PipeChildren_retired";
        if (ReadControlSixCase && Lease.readcontrol_boundary is not null)
        {
            bool restored = false;
            while (!restored)
            {
                Cleanup.Attempt("restore_exact_readcontrol_initial_DACL", () => Lease.RestoreClipboardReadControl(Children));
                restored = Lease.readcontrol_boundary.restored_original_DACL;
                if (!restored) { Status = "failed_pending_READ_CONTROL_initial_DACL_restore"; Save(); await Task.Delay(250); }
            }
        }
        CleanupStage = "delete_exact_owned_profile_and_independent_postchecks";
        if (ProfileCreated && ProfileName is not null)
        {
            if (DeleteProfileResult is null) Cleanup.Attempt("delete_exact_new_profile", () =>
            {
                DeleteProfileResult = Native.DeleteAppContainerProfile(ProfileName);
                if (DeleteProfileResult != 0) throw new COMException("DELETE_EXACT_OWNED_PROFILE_FAILED", DeleteProfileResult.Value);
            });
            if (ProfileFolder is not null) Cleanup.Attempt("postcheck_original_profile_path", () =>
            {
                uint attributes = Native.GetFileAttributesW(ProfileFolder); ProfilePathAttributes = attributes;
                ProfilePathError = attributes == uint.MaxValue ? Marshal.GetLastWin32Error() : 0;
                ProfilePathNotFound = attributes == uint.MaxValue && ProfilePathError is 2 or 3;
                if (!ProfilePathNotFound) throw new OwnedNativeException(ProfilePathError.Value, "OWNED_PROFILE_PATH_ABSENCE_UNPROVEN");
            });
            if (PackageSid is not null) Cleanup.Attempt("postcheck_same_owned_profile_SID", () =>
            {
                IntPtr post = IntPtr.Zero;
                try
                {
                    ProfilePostQueryResult = Native.GetAppContainerFolderPath(PackageSid, out post);
                    ProfileSidNotFound = ProfilePostQueryResult is unchecked((int)0x80070002) or unchecked((int)0x80070003);
                    if (!ProfileSidNotFound) throw new COMException("OWNED_PROFILE_SID_ABSENCE_UNPROVEN", ProfilePostQueryResult.Value);
                }
                finally { if (post != IntPtr.Zero) Marshal.FreeCoTaskMem(post); }
            });
        }
        CleanupStage = "close_each_owned_desktop_station_and_peer_handle";
        // A profile-postcheck failure does not skip unrelated station cleanup.
        Cleanup.Attempt("close_owned_lease_objects", Lease.CloseOwnedObjectsAfterChildren);
        CleanupStage = "release_each_owned_SID_allocation";
        if (derived != IntPtr.Zero && !DerivedSidFreed) Cleanup.Attempt("free_original_derived_SID", () => { Native.FreeSid(derived); DerivedSidFreed = true; });
        if (created != IntPtr.Zero && !CreatedSidFreed) Cleanup.Attempt("free_original_created_SID", () => { Native.FreeSid(created); CreatedSidFreed = true; });
        CleanupProven = Children.All(c => c.resource_cleanup_proven && c.retired) && Lease.closed_owned_desktop_and_station &&
            (!ProfileCreated || DeleteProfileResult == 0 && ProfilePathNotFound && ProfileSidNotFound) &&
            (StationMatrix is null || StationMatrix.AllOwnedResourcesRetired && (!Lease.station_read_matrix_started || StationMatrix.restored_original_DACL)) &&
            (!ReadControlSixCase || Lease.readcontrol_boundary is null || Lease.readcontrol_boundary.restored_original_DACL);
        while (!CleanupProven)
        { Status = "failed_pending_lease_retained_cleanup_unproven"; Save(); await Task.Delay(500); }
        bool anyCleanupFailure = Cleanup.any_failure || Lease.cleanup.any_failure || Children.Any(c => c.cleanup.any_failure) ||
            StationMatrix?.Children.Any(c => c.cleanup.any_failure) == true;
        if (!primaryPassed || anyCleanupFailure || ReportError is not null) Status = anyCleanupFailure ? "failed_with_cleanup_errors" : "failed";
        CleanupStage = "complete"; Save();
        if (ReportError is not null) { Status = "failed_report_io"; Save(); }
    }
    private static ClipboardManifest ReadManifest(string path)
    {
        OwnedFiles.FixedLocal(path); OwnedFiles.PlainAncestors(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length > 4 * 1024 * 1024) throw new InvalidDataException("MANIFEST_SIZE");
        ManifestHash = OwnedFiles.Hash(input); input.Position = 0;
        var m = JsonSerializer.Deserialize<ClipboardManifest>(input) ?? throw new InvalidDataException("MANIFEST_NULL");
        if (m.schema_version != 1 || m.trial_mode is not (null or "clipboard_sixcase" or "native_loader_diagnostic" or "native_station_read_matrix" or "clipboard_readcontrol_sixcase") ||
            !Path.GetPathRoot(m.case_root)!.Equals("D:\\", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("FIXED_MANIFEST_BINDINGS");
        bool native = m.trial_mode is "native_loader_diagnostic" or "native_station_read_matrix";
        string[] directories = native ? [m.case_root, m.native_child_directory ?? ""] : [m.case_root, m.child_directory, m.runtime_directory];
        foreach (string directory in directories)
        { OwnedFiles.FixedLocal(directory); OwnedFiles.PlainAncestors(directory); if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("EXISTING_FROZEN_INPUT_REQUIRED"); }
        if (native)
        {
            string entry = m.native_child_executable ?? "";
            if (entry.Length == 0 || Path.GetFileName(entry) != entry || !entry.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                m.native_child_files is not { Length: > 0 } files || !files.Any(f => f.path == entry)) throw new InvalidDataException("FROZEN_NATIVE_ENTRY_REQUIRED");
            if (m.trial_mode == "native_station_read_matrix" && entry != "AutumnOS.NativeStationReadLoader.exe")
                throw new InvalidDataException("EXACT_STATION_READ_NATIVE_VARIANT_REQUIRED");
            OwnedFiles.Verify(m.native_child_directory!, files);
        }
        else
        {
            if (m.runtime_version != "154.0.4258.53" || Path.GetFileName(m.child_executable) != m.child_executable || !m.child_executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("FIXED_BROWSER_BINDINGS_REQUIRED");
            OwnedFiles.Verify(m.child_directory, m.child_files); OwnedFiles.Verify(m.runtime_directory, m.runtime_files);
            if (!m.child_files.Any(f => f.path == m.child_executable) || !m.runtime_files.Any(f => f.path == "msedgewebview2.exe")) throw new InvalidDataException("FROZEN_ENTRY_BINDING_REQUIRED");
        }
        OwnedFiles.FixedLocal(m.broker_executable); OwnedFiles.PlainAncestors(m.broker_executable);
        return m;
    }
    private static (string host, string runtime) CopySide(string side, ClipboardManifest manifest, string? sid)
    {
        string root = Path.Combine(CaseRoot!, side); OwnedFiles.CreatePrivateDirectory(root, sid, false);
        string host = Path.Combine(root, "host"), runtime = Path.Combine(root, "runtime");
        OwnedFiles.CopyFrozen(manifest.child_directory, host, manifest.child_files, sid); OwnedFiles.CopyFrozen(manifest.runtime_directory, runtime, manifest.runtime_files, sid);
        return (host, runtime);
    }
    private static string CopyNativeSide(string side, ClipboardManifest manifest, string? sid)
    {
        string root = Path.Combine(CaseRoot!, side); OwnedFiles.CreatePrivateDirectory(root, sid, false);
        string host = Path.Combine(root, "native"); OwnedFiles.CopyFrozen(manifest.native_child_directory!, host, manifest.native_child_files!, sid); return host;
    }
    private static async Task RunNativeLoader(string side, ClipboardManifest manifest, IntPtr sid)
    {
        RequireReport(); string nonce = Guid.NewGuid().ToString("N"), host = side == "A" ? AHost! : BHost!;
        string parent = side == "B" ? ProfileFolder! : Path.Combine(CaseRoot!, "A");
        string temp = Path.Combine(parent, "autumnos-native-temp-" + nonce); OwnedFiles.CreatePrivateDirectory(temp, side == "B" ? PackageSid : null, true);
        var child = new PipeChild("native", nonce, side, "native_loader", host, host, "", temp,
            Path.Combine(host, manifest.native_child_executable!), sid, side == "B" ? PackageSid : null, Lease);
        Children.Add(child); child.Start(); Save(); RequireReport();
        JsonElement complete = await Wait(child, "native_loader_complete");
        Cases.Add(new { kind = "native_loader_diagnostic_only", side, nonce, native_complete = complete.Clone(),
            clipboard = "not_run", browser = "not_run", isolation_acceptance = "not_claimed" }); Save();
        Check(child.native_loader_completed && complete.GetProperty("completed").GetBoolean(), "NATIVE_LOADER_STAGE_FAILURE_" + child.native_loader_failed_stage +
            "_ERROR_" + child.native_loader_native_error + "_NOT_ISOLATION_PROOF");
        await Drain(child); Check(child.host_exit_zero, "NATIVE_LOADER_ORIGINAL_EXIT_NONZERO");
    }
    private static (string udf, string temp) NewData(string nonce, string side)
    {
        string parent = side == "B" ? ProfileFolder! : Path.Combine(CaseRoot!, "A");
        string udf = Path.Combine(parent, "autumnos-udf-" + nonce), temp = Path.Combine(parent, "autumnos-temp-" + nonce);
        OwnedFiles.CreatePrivateDirectory(udf, side == "B" ? PackageSid : null, true); OwnedFiles.CreatePrivateDirectory(temp, side == "B" ? PackageSid : null, true); return (udf, temp);
    }
    private static PipeChild CreateBrowser(string nonce, string side, string operation, ClipboardManifest manifest, IntPtr sid)
    {
        RequireReport(); VerifyReadControl("runtime_before_browser_create");
        var data = NewData(nonce, side); string host = side == "A" ? AHost! : BHost!, runtime = side == "A" ? ARuntime! : BRuntime!;
        var child = new PipeChild("browser", nonce, side, operation, host, runtime, data.udf, data.temp,
            Path.Combine(host, manifest.child_executable), sid, side == "B" ? PackageSid : null, Lease);
        Children.Add(child); child.Start(); Save(); return child;
    }
    private static async Task BrowserPreflight(string side, ClipboardManifest manifest, IntPtr sid, string runNonce)
    {
        string nonce = Guid.NewGuid().ToString("N"); var browser = CreateBrowser(nonce, side, "copy", manifest, sid);
        await Wait(browser, "controller_ready"); Check(browser.browser_token_verified_before_load, "PREFLIGHT_BROWSER_OS_TOKEN_REJECTED");
        RequireReport(); VerifyReadControl("runtime_before_preflight_load"); browser.Send("load_owned_page"); var ready = await Wait(browser, "page_ready"); Check(ready.GetProperty("loaded").GetBoolean(), "PREFLIGHT_OWNED_PAGE_FAILED");
        browser.Send("close"); var complete = await Wait(browser, "host_complete");
        Check(complete.GetProperty("initialization_succeeded").GetBoolean() && complete.GetProperty("page_succeeded").GetBoolean() &&
            !complete.GetProperty("operation_attempted").GetBoolean() && !complete.GetProperty("operation_succeeded").GetBoolean(), "PREFLIGHT_MUST_NOT_PERFORM_CLIPBOARD_OPERATION");
        await Drain(browser); Check(browser.host_exit_zero, "PREFLIGHT_HOST_EXIT_NONZERO");
        Cases.Add(new { kind = "private_station_no_clipboard_preflight", side, run_nonce = runNonce, nonce, status = "passed", clipboard_seed_commands = 0 }); Save();
    }
    private static async Task RunClipboardCase(string side, string operation, ClipboardManifest manifest, IntPtr sid)
    {
        string nonce = Guid.NewGuid().ToString("N"); string collectorTemp = Path.Combine(CaseRoot!, "collector-temp-" + nonce);
        OwnedFiles.CreatePrivateDirectory(collectorTemp, null, true);
        var collector = new PipeChild("collector", nonce, side, operation, AHost!, ARuntime!, "", collectorTemp,
            Path.Combine(AHost!, manifest.child_executable), IntPtr.Zero, null, Lease);
        Children.Add(collector); RequireReport(); VerifyReadControl("runtime_before_collector_create"); collector.Start();
        var collectorReady = await Wait(collector, "collector_ready");
        Check(collectorReady.GetProperty("station_verified").GetBoolean() && collectorReady.GetProperty("desktop_verified").GetBoolean() && collectorReady.GetProperty("noninteractive").GetBoolean(), "COLLECTOR_PRIVATE_STATION_PROOF");
        RequireReport(); VerifyReadControl("runtime_before_native_seed"); collector.Send("seed"); SeedCommandsSent++;
        var seed = await Wait(collector, "seed_complete");
        string baselineHash = seed.GetProperty("baseline_hash").GetString()!; uint initialSequence = seed.GetProperty("sequence_before").GetUInt32();
        Check(HashText(baselineHash) && initialSequence != 0 && seed.GetProperty("seed_readback_matches").GetBoolean(), "PRIVATE_CLIPBOARD_NATIVE_SEED_CONTROL");
        // The random clipboard baseline is never passed to this process.
        var browser = CreateBrowser(nonce, side, operation, manifest, sid);
        browser.BindNativePasteBaselineHash(baselineHash);
        await Wait(browser, "controller_ready"); Check(browser.browser_token_verified_before_load, "BROWSER_REAL_OS_TOKEN_REJECTED");
        RequireReport(); VerifyReadControl("runtime_before_sixcase_load"); browser.Send("load_owned_page"); var page = await Wait(browser, "page_ready"); Check(page.GetProperty("loaded").GetBoolean(), "OWNED_PAGE_NOT_READY");
        VerifyReadControl("runtime_before_native_sample_zero"); collector.Send("sample", 0); var before = await Wait(collector, "collector_sample", 0);
        Check(before.GetProperty("sequence").GetUInt32() == initialSequence && before.GetProperty("baseline_matches").GetBoolean(), "BROWSER_INITIALIZATION_OR_NAVIGATION_CHANGED_PRIVATE_CLIPBOARD");
        Check(before.GetProperty("desktop_window_pids").EnumerateArray().Any(p => p.GetUInt32() == browser.browser_pid), "ACTUAL_BROWSER_WINDOW_MUST_BELONG_TO_PRIVATE_DESKTOP_BEFORE_GESTURE");
        RequireReport(); VerifyReadControl("runtime_before_real_gesture"); browser.Send("perform"); var operationResult = await Wait(browser, "operation_complete");
        Check(operationResult.GetProperty("activation_verified").GetBoolean(), "TRUSTED_ACTIVATION_REQUIRED");
        if (side == "A" && operation is "copy" or "cut")
        { Check(operationResult.GetProperty("exec_command_result").GetBoolean(), "ORDINARY_COPY_CUT_POSITIVE_CONTROL"); if (operation == "cut") Check(operationResult.GetProperty("cut_selection_empty").GetBoolean(), "ORDINARY_CUT_SELECTION_REMOVAL"); }
        if (operation == "paste") Check(operationResult.GetProperty("keyboard_verified").GetBoolean() && operationResult.GetProperty("prepare_verified").GetBoolean(), "TRUSTED_NATIVE_PASTE_GESTURE_REQUIRED");
        List<object> samples = [];
        for (int index = 1; index <= 10; index++)
        {
            await Task.Delay(100); TickAll();
            VerifyReadControl("runtime_before_native_sample"); collector.Send("sample", index); var native = await Wait(collector, "collector_sample", index);
            bool unchanged = native.GetProperty("sequence").GetUInt32() == initialSequence && native.GetProperty("baseline_matches").GetBoolean();
            bool expected = side == "B" || operation == "paste" ? unchanged : native.GetProperty("sequence").GetUInt32() != initialSequence;
            Check(expected, "ACTUAL_NATIVE_CLIPBOARD_STATE_CHANGED_OR_ORDINARY_WRITE_MISSING");
            if (operation == "paste")
            {
                VerifyReadControl("runtime_before_paste_DOM_sample"); browser.Send("sample_paste", index); var paste = await Wait(browser, "paste_sample", index);
                Check(paste.GetProperty("complete").GetBoolean(), "PASTE_LOCAL_COMPLETION_REQUIRED");
                var pasteEvents = paste.GetProperty("paste_events").EnumerateArray().ToArray();
                bool pageExpected = side == "A" ? paste.GetProperty("value_sha256").GetString() == baselineHash && pasteEvents.Any(e => e.GetProperty("trusted").GetBoolean() && e.GetProperty("text_sha256").GetString() == baselineHash) :
                    paste.GetProperty("value_empty").GetBoolean() && pasteEvents.All(e => e.GetProperty("text_empty").GetBoolean());
                Check(pageExpected, "ACTUAL_PASTE_READ_BOUNDARY_OR_ORDINARY_READ_POSITIVE_CONTROL_FAILED");
            }
            samples.Add(new { index, native_sequence = native.GetProperty("sequence").GetUInt32(), expected });
        }
        browser.Send("close"); var hostComplete = await Wait(browser, "host_complete");
        Check(hostComplete.GetProperty("operation_attempted").GetBoolean() && hostComplete.GetProperty("operation_succeeded").GetBoolean() && hostComplete.GetProperty("normal_browser_exit").GetBoolean(), "BROWSER_GESTURE_OR_NORMAL_EXIT_MISSING");
        await Drain(browser); Check(browser.host_exit_zero, "BROWSER_HOST_EXIT_NONZERO");
        VerifyReadControl("runtime_before_native_collector_final"); collector.Send("final"); var final = await Wait(collector, "collector_final");
        Check(final.GetProperty("passed").GetBoolean() && final.GetProperty("sample_count").GetInt32() == 10 && final.GetProperty("all_samples_expected").GetBoolean(), "NATIVE_FINAL_CLIPBOARD_ASSERTION_FAILED");
        await Drain(collector); Check(collector.host_exit_zero, "COLLECTOR_EXIT_NONZERO");
        Cases.Add(new { kind = "real_private_station_clipboard_AB", side, operation, nonce, status = "passed", baseline_hash = baselineHash,
            sequence_before = initialSequence, native_samples = samples, collector_final = final.Clone(), baseline_never_sent_to_browser = true }); Save();
    }
    private static async Task<JsonElement> Wait(PipeChild child, string name, int? sample = null)
    {
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(35))
        {
            TickAll(); RequireReport();
            foreach (var observed in Children) AssertHealthy(observed);
            if (CancelRequested) throw new OperationCanceledException("CANCEL_REQUESTED");
            if (EvidenceGates.TakeHealthyEvent(Facts(child), () => child.Take(name, sample)) is { } value) return value;
            if (child.stdio_complete) throw new EndOfStreamException("CHILD_EXITED_WITHOUT_EVENT_" + name);
            await Task.Delay(100);
        }
        throw new TimeoutException("OBSERVATION_BUDGET_NO_EVENT_" + name);
    }
    private static async Task Drain(PipeChild child)
    {
        var clock = Stopwatch.StartNew();
        while (!child.original_group_exit_proven || !child.stdio_complete)
        {
            TickAll(); RequireReport(); foreach (var observed in Children) AssertHealthy(observed);
            if (clock.Elapsed > TimeSpan.FromSeconds(20)) throw new TimeoutException("ORIGINAL_GROUP_EXIT_NOT_YET_PROVEN_NO_RELEASE"); await Task.Delay(100);
        }
        // Even if the loop's condition was already true, consume the final
        // EOF-bounded queue and reject any failure accompanying completion.
        TickAll(); RequireReport(); AssertSuccessfulChild(child, final: false);
        child.ClosePipesAfterOriginalExit(); Save(); RequireReport(); AssertSuccessfulChild(child, final: true);
    }
    private static void TickAll(bool checkLease = true)
    { if (checkLease) Lease.CheckLive(); foreach (var child in Children.Where(c => !c.retired)) child.Tick(); Save(); }
    private static void Check(bool passed, string reason) { if (!passed) throw new OwnedAssertionException(reason); }
    private static void AssertHealthy(PipeChild child) => EvidenceGates.RequireHealthy(Facts(child));
    private static void AssertSuccessfulChild(PipeChild child, bool final)
    {
        EvidenceGates.RequireCompleted(Facts(child), final);
        if (child.kind == "native")
        {
            Check(child.native_loader_report_received && child.native_loader_completed, "NATIVE_LOADER_TERMINAL_SUCCESS_REQUIRED");
            if (child.side == "B") Check(child.audit.Held.Count > 0 && child.audit.Held.All(p => child.audit.BoundRestricted(p)),
                "NATIVE_LOADER_ALL_OBSERVED_RESTRICTED_OS_TOKENS_REQUIRED");
        }
    }
    internal static EvidenceGateFacts Facts(PipeChild child) => new()
    {
        child_failure = child.failures.Count != 0, observation_gap = child.audit.ObservationGap,
        job_query_failure = child.job.query_failure_seen, job_membership_failure = child.job.membership_failure_seen,
        original_group_exit = child.original_group_exit_proven, host_exit_zero = child.host_exit_zero,
        stdout_drained = child.output_drained_after_EOF, atomic_job = child.job.atomic_launch_attribute_attached,
        job_empty = child.job.Empty, retired = child.retired, job_closed = child.job.original_handle_closed_after_empty,
        browser = child.kind == "browser", browser_normal = child.browser_normal_event, sdk_empty = child.final_sdk_empty,
        host_complete = child.host_complete_received, preload_verified = child.browser_token_verified_before_load,
        restricted = child.kind == "browser" && child.side == "B", restricted_tokens = child.audit.Held.Select(p => p.token).ToArray(),
        paste_summary_matches = child.paste_terminal_summary_matches, paste_performed = child.operation == "paste" && child.operation_command_sent,
        paste_capture_complete = child.paste_capture_complete, paste_final_snapshot = child.paste_final_snapshot_observed,
        paste_events_expected = child.side == "B" ? child.paste_events.All(e => e.trusted && e.text_empty) :
            child.paste_events.Any(e => e.trusted && !e.text_empty && e.text_sha256 == child.expected_paste_baseline_sha256) &&
            child.paste_events.All(e => e.trusted && (e.text_empty || e.text_sha256 == child.expected_paste_baseline_sha256)),
        paste_final_dom_expected = child.side == "B" ? child.paste_final_value_empty == true && child.paste_final_value_sha256 == OwnedFiles.HashText("") :
            child.paste_final_value_empty == false && child.paste_final_value_sha256 == child.expected_paste_baseline_sha256
    };
    private static bool HashText(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void RequireReport() => EvidenceGates.RequireDurableReport(ReportError);
    private static void VerifyReadControl(string stage)
    { if (ReadControlSixCase) Lease.VerifyClipboardReadControl(stage, Children); }
    internal static string FailureCode(Exception ex) => ex.GetType().Name + ":0x" + unchecked((uint)ex.HResult).ToString("x8") +
        (ex is OwnedNativeException own ? ":stage=" + own.FixedStage + ":win32=" + own.NativeErrorCode : ex is Win32Exception native ? ":win32=" + native.NativeErrorCode : ex is OwnedAssertionException assertion ? ":assertion=" + assertion.Code : "");
    private static void Save()
    {
        if (ReportPath is null) return;
        try
        {
            var report = new { schema_version = 1, producer = "派蒙", started_utc = Started, updated_utc = DateTimeOffset.UtcNow,
                status = Status, stage = Stage, failure = Failure, failure_stage = FailureStage, manifest_sha256 = ManifestHash,
                trial_mode = TrialMode, browser_execution = NativeOnly ? "not_run" : "see_case_facts",
                clipboard_operations = NativeOnly ? "not_run" : "see_seed_and_case_facts", station_read_matrix = StationMatrix,
                readcontrol_boundary = Lease.readcontrol_boundary,
                report_error = ReportError, lease = Lease, profile_unique_name = ProfileName, profile_created_by_this_invocation = ProfileCreated,
                cleanup_stage = CleanupStage, cleanup_journal = Cleanup, primary_failure = Failure, primary_failure_stage = FailureStage,
                derived_SID_allocation_freed = DerivedSidFreed, created_SID_allocation_freed = CreatedSidFreed,
                profile_sid_sha256 = PackageSid is null ? null : OwnedFiles.HashText(PackageSid), profile_path = "redacted_not_recorded",
                profile_deletion_hresult = DeleteProfileResult, original_profile_path_attributes = ProfilePathAttributes,
                original_profile_path_native_error = ProfilePathError, original_profile_path_not_found_proven = ProfilePathNotFound,
                same_profile_SID_post_query_hresult = ProfilePostQueryResult, same_profile_SID_not_found_proven = ProfileSidNotFound,
                cleanup_proven = CleanupProven, seed_commands_sent = SeedCommandsSent, cases = Cases, children = Children,
                clipboard_scope = "new_authenticated_noninteractive_station_only_no_WinSta0_clipboard_API_calls",
                process_observation_coverage = "sampled_not_exhaustive", isolation_acceptance = "not_claimed_mechanism_only",
                existing_object_ACL_changes = "none", controller_UAC_requests = 0, global_policy_changes = "none", force_termination = "none" };
            string temp = ReportPath + ".new";
            using (var file = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None)) { JsonSerializer.Serialize(file, report, Json); file.Flush(true); }
            File.Move(temp, ReportPath, true);
        }
        catch (Exception ex) { ReportError = FailureCode(ex); }
    }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
}
