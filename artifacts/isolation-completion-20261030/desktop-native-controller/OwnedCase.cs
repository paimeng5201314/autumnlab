using System.Runtime.InteropServices;

namespace AutumnOS.OwnedBrowserParent;

internal sealed class OwnedCase(string label, string side, uint desktop_package_mask, string root, PreparedInputs inputs, Action save)
{
    public string label { get; } = label;
    public string side { get; } = side;
    public uint desktop_package_mask { get; } = desktop_package_mask;
    public string nonce { get; } = Guid.NewGuid().ToString("N");
    public string status { get; private set; } = "not_run";
    public string stage { get; private set; } = "not_started";
    public string? primary_failure { get; private set; }
    public bool profile_created { get; private set; }
    public string? profile_name { get; private set; }
    public string? package_sid_sha256 { get; private set; }
    public int? profile_delete_hresult { get; private set; }
    public uint? original_profile_path_attributes { get; private set; }
    public int? original_profile_path_native_error { get; private set; }
    public int? same_profile_SID_post_query_hresult { get; private set; }
    public bool profile_path_not_found { get; private set; }
    public bool profile_sid_not_found { get; private set; }
    public bool cleanup_proven { get; private set; }
    public bool diagnostic_execution_completed => child?.diagnostic_execution_completed == true && cleanup_proven && !cleanup.any_failure && !desktop.cleanup.any_failure;
    public bool loader_succeeded => child?.loader_completed == true && child.original_exit_code == 0;
    public DesktopSecurity desktop { get; } = new();
    public NativeChild? child { get; private set; }
    public CleanupJournal cleanup { get; } = new();
    private string? sidText, profileFolder;
    private IntPtr derived, created;
    private bool derivedFreed, createdFreed;

    internal async Task RunAsync()
    {
        try
        {
            status = "running"; stage = "derive_unique_package_sid";
            profile_name = "AutumnOS.Paimon.DesktopLoader." + nonce;
            int hr = DeriveAppContainerSidFromAppContainerName(profile_name, out derived);
            if (hr != 0 || derived == IntPtr.Zero) throw new COMException("DERIVE_DESKTOP_CASE_SID_FAILED", hr);
            sidText = Native.SidString(derived); package_sid_sha256 = OwnedFiles.HashText(sidText);
            if (side == "B")
            {
                stage = "create_exact_new_profile";
                hr = Native.CreateAppContainerProfile(profile_name, "AutumnOS owned desktop loader", "Temporary zero-capability desktop metadata diagnostic.", IntPtr.Zero, 0, out created);
                if (hr != 0) throw new COMException("PROFILE_CREATE_FAILED_NO_ADOPTION", hr); profile_created = true; save();
                if (created == IntPtr.Zero || Native.SidString(created) != sidText) throw new OwnedAssertionException("PROFILE_SID_MUST_MATCH_DERIVED_NEW_DESKTOP_ACE");
                hr = Native.GetAppContainerFolderPath(sidText, out IntPtr folder);
                if (hr != 0) throw new COMException("NEW_PROFILE_PATH_QUERY_FAILED", hr);
                try { profileFolder = Path.GetFullPath(Marshal.PtrToStringUni(folder) ?? throw new InvalidDataException("PROFILE_PATH_NULL")); }
                finally { Marshal.FreeCoTaskMem(folder); }
                OwnedFiles.FixedLocal(profileFolder); OwnedFiles.PlainAncestors(profileFolder);
            }
            stage = "create_verify_only_new_desktop"; desktop.Create(sidText, desktop_package_mask); save();
            string directory = Path.Combine(root, label); OwnedFiles.CreatePrivateDirectory(directory, side == "B" ? sidText : null, false);
            string copy = Path.Combine(directory, "native"); OwnedFiles.CopyFrozen(inputs.child_directory, copy, inputs.child_files, side == "B" ? sidText : null);
            string temp = Path.Combine(side == "B" ? profileFolder! : directory, "temp-" + nonce);
            OwnedFiles.CreatePrivateDirectory(temp, side == "B" ? sidText : null, true);
            inputs.RecheckSystemDlls(); stage = "suspended_original_token_then_native_DLL_layers";
            child = new(copy, inputs.child_executable, nonce, side, desktop.name, temp, side == "B" ? created : IntPtr.Zero, side == "B" ? sidText : null, save);
            await child.RunAsync(); status = child.diagnostic_execution_completed ? child.loader_completed ? "loader_passed" : "loader_failed_observed" : "diagnostic_execution_failed";
        }
        catch (Exception ex) { primary_failure = Program.FailureCode(ex); status = "diagnostic_execution_failed"; }
        finally
        {
            // Child.RunAsync returns only after exact Job0/original handles/EOF
            // and independent resource retirement, never on a time budget.
            while (child is not null && (!child.group_empty_proven || !child.retired))
            { status = "pending_owned_child_proof_no_next_case"; save(); await Task.Delay(250); }
            bool done = false;
            while (!done)
            {
                try { CleanOwned(); done = cleanup_proven; }
                catch (Exception ex) { cleanup.Attempt("cleanup_outer_barrier", () => throw ex); }
                if (!done) { status = "pending_exact_owned_cleanup"; save(); await Task.Delay(500); }
            }
            stage = "complete"; save();
        }
    }
    private void CleanOwned()
    {
        if (profile_created && profile_name is not null)
        {
            if (profile_delete_hresult is null or not 0) cleanup.Attempt("delete_only_exact_new_case_profile", () =>
            { profile_delete_hresult = Native.DeleteAppContainerProfile(profile_name); if (profile_delete_hresult != 0) throw new COMException("OWNED_PROFILE_DELETE_FAILED", profile_delete_hresult.Value); });
            if (!profile_path_not_found && profileFolder is not null) cleanup.Attempt("original_profile_path_native_postcheck", () =>
            {
                uint attributes = Native.GetFileAttributesW(profileFolder); original_profile_path_attributes = attributes;
                original_profile_path_native_error = attributes == uint.MaxValue ? Marshal.GetLastWin32Error() : 0;
                profile_path_not_found = attributes == uint.MaxValue && original_profile_path_native_error is 2 or 3;
                if (!profile_path_not_found) throw new OwnedAssertionException("PROFILE_PATH_ABSENCE_UNPROVEN");
            });
            if (!profile_sid_not_found && sidText is not null) cleanup.Attempt("same_case_profile_SID_native_postcheck", () =>
            {
                same_profile_SID_post_query_hresult = Native.GetAppContainerFolderPath(sidText, out IntPtr folder);
                try { profile_sid_not_found = same_profile_SID_post_query_hresult is unchecked((int)0x80070002) or unchecked((int)0x80070003); }
                finally { if (folder != IntPtr.Zero) Marshal.FreeCoTaskMem(folder); }
                if (!profile_sid_not_found) throw new OwnedAssertionException("PROFILE_SID_ABSENCE_UNPROVEN");
            });
        }
        cleanup.Attempt("close_verify_only_owned_desktop", desktop.CloseAndVerify);
        if (derived != IntPtr.Zero && !derivedFreed) cleanup.Attempt("free_original_derived_SID", () => { Native.FreeSid(derived); derivedFreed = true; });
        if (created != IntPtr.Zero && !createdFreed) cleanup.Attempt("free_original_created_SID", () => { Native.FreeSid(created); createdFreed = true; });
        cleanup_proven = (child is null || child.group_empty_proven && child.retired) &&
            (desktop.NoObjectWasCreated || desktop.original_handle_closed && desktop.post_close_not_found) &&
            (!profile_created || profile_delete_hresult == 0 && profile_path_not_found && profile_sid_not_found);
    }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
}
