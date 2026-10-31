namespace AutumnOS.OwnedBrowserParent;

internal sealed class StationReadCase
{
    public required string label { get; init; }
    public required string side { get; init; }
    public required uint station_package_mask { get; init; }
    public required string nonce { get; init; }
    public NativeChild? child { get; internal set; }
    public bool diagnostic_execution_completed => child?.diagnostic_execution_completed == true;
    public bool loader_succeeded => child?.loader_completed == true && child.original_exit_code == 0;
}

internal sealed class StationReadMatrix(StationLease lease, ClipboardManifest manifest, string root, string packageSid, Action save, Action durableGate)
{
    public List<StationReadCase> cases { get; } = [];
    public string? initial_DACL_sha256 => lease.initial_DACL_sha256;
    public string? final_DACL_sha256 => lease.final_DACL_sha256;
    public bool restored_original_DACL => lease.restored_original_DACL;
    public bool native_mode_completed { get; private set; }
    public string clipboard_operations => "not_run";
    public string browser_execution => "not_run";
    public string isolation_acceptance => "not_claimed";
    public string inference_limit => "first_compatible_cumulative_read_mask_is_not_proof_of_one_unique_required_bit; restored_baseline_tests_order_effects";
    private string? aHost, bHost;
    private string? profileFolder;
    private IntPtr packageHandle;
    internal IReadOnlyList<NativeChild> Children => cases.Where(c => c.child is not null).Select(c => c.child!).ToArray();
    internal bool AllOwnedResourcesRetired => Children.All(c => c.retired && c.group_empty_proven && (!c.job.created || c.job.Empty) && c.audit.AllSignaled);
    internal bool AllDiagnosticGroupsComplete => AllOwnedResourcesRetired && Children.All(c => c.diagnostic_execution_completed);

    internal async Task RunInitialAAsync()
    {
        lease.BeginStationReadMatrix(packageSid);
        string owned = Path.Combine(root, "station-read-native"); OwnedFiles.CreatePrivateDirectory(owned, packageSid, false);
        aHost = Path.Combine(owned, "ordinary"); bHost = Path.Combine(owned, "appcontainer");
        OwnedFiles.CopyFrozen(manifest.native_child_directory!, aHost, manifest.native_child_files!, null);
        OwnedFiles.CopyFrozen(manifest.native_child_directory!, bHost, manifest.native_child_files!, packageSid);
        await RunCase(0);
    }
    internal async Task RunAfterProfileAsync(IntPtr exactPackage, string exactProfileFolder)
    {
        if (exactPackage == IntPtr.Zero || Native.SidString(exactPackage) != packageSid || cases.Count != 1 || !cases[0].loader_succeeded)
            throw new OwnedAssertionException("STATION_MATRIX_SAME_EXACT_PROFILE_AFTER_ORDINARY_POSITIVE_CONTROL");
        packageHandle = exactPackage; profileFolder = exactProfileFolder;
        for (int index = 1; index < StationMatrixPolicy.Cases.Count; index++) await RunCase(index);
        if (cases.Count != 7 || cases.Any(c => !c.diagnostic_execution_completed) || !AllDiagnosticGroupsComplete)
            throw new OwnedAssertionException("STATION_MATRIX_ALL_SEVEN_NATIVE_CASES_AND_GROUPS_REQUIRED");
        lease.RestoreStationReadMatrix(Children);
        native_mode_completed = lease.restored_original_DACL && lease.initial_DACL_sha256 == lease.final_DACL_sha256;
        if (!native_mode_completed) throw new OwnedAssertionException("STATION_MATRIX_FINAL_INITIAL_DACL_RESTORE_REQUIRED");
        save();
    }
    private async Task RunCase(int index)
    {
        if (index != cases.Count || (index > 0 && cases.Any(c => !c.diagnostic_execution_completed)))
            throw new OwnedAssertionException("STATION_MATRIX_NO_SKIP_OR_CONTINUE_AFTER_UNPROVEN_DIAGNOSTIC");
        durableGate();
        var spec = StationMatrixPolicy.Cases[index];
        // The lease obtains liveness/Job0/EOF/retirement from these actual
        // NativeChild objects; no report boolean can authorize an ACL change.
        lease.ApplyStationReadCase(index, Children);
        string nonce = Guid.NewGuid().ToString("N"), side = spec.side;
        string temp = Path.Combine(side == "B" ? profileFolder! : root, "station-read-temp-" + nonce);
        OwnedFiles.CreatePrivateDirectory(temp, side == "B" ? packageSid : null, true);
        var row = new StationReadCase { label = spec.label, side = side, station_package_mask = spec.mask, nonce = nonce }; cases.Add(row);
        string host = side == "B" ? bHost! : aHost!;
        row.child = new NativeChild(host, manifest.native_child_executable!, nonce, side, lease.desktop_name, temp,
            side == "B" ? packageHandle : IntPtr.Zero, side == "B" ? packageSid : null, save, lease.station_name);
        await row.child.RunAsync(); save(); durableGate();
        if (!row.diagnostic_execution_completed) throw new OwnedAssertionException("STATION_MATRIX_NATIVE_INFRASTRUCTURE_UNPROVEN_" + spec.label);
        if (side == "A" && !row.loader_succeeded) throw new OwnedAssertionException("STATION_MATRIX_ORDINARY_POSITIVE_CONTROL_FAILED_" + spec.label);
    }
    internal async Task EnsureRetiredAndRestoreAsync()
    {
        foreach (var child in Children.Where(c => !c.retired || !c.group_empty_proven)) await child.EnsureRetiredAsync();
        if (!AllOwnedResourcesRetired) throw new OwnedAssertionException("STATION_MATRIX_LIVE_OR_UNPROVEN_NATIVE_CHILD_PREVENTS_RESTORE");
        lease.RestoreStationReadMatrix(Children); save();
    }
}
