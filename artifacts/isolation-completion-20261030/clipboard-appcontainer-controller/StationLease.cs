using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Security.Cryptography;

namespace AutumnOS.OwnedBrowserParent;

internal sealed class StationLease
{
    private IntPtr holder, broker, station, desktop, previousStation;
    public uint holder_pid { get; private set; }
    public uint broker_pid { get; private set; }
    public ulong holder_creation_filetime { get; private set; }
    internal IntPtr HolderHandle => holder;
    public string station_name { get; private set; } = "";
    public string desktop_name { get; private set; } = "";
    public bool peers_OS_identity_verified { get; private set; }
    public bool holder_original_handle_live { get; private set; }
    public bool broker_original_handle_live { get; private set; }
    public string? station_DACL_sha256 { get; private set; }
    public bool station_package_rights_exact_0x23 { get; private set; }
    public bool station_label_low { get; private set; }
    public bool desktop_DACL_verified { get; private set; }
    public bool desktop_label_low { get; private set; }
    public List<DesktopProbeFact> desktop_freshness_checks { get; } = [];
    public DesktopCreationFact? desktop_creation { get; private set; }
    public bool noninteractive { get; private set; }
    public bool closed_owned_desktop_and_station { get; private set; }
    public CleanupJournal cleanup { get; } = new();
    public bool station_read_matrix_started { get; private set; }
    public bool restored_original_DACL { get; private set; }
    public string? initial_DACL_sha256 { get; private set; }
    public string? final_DACL_sha256 { get; private set; }
    public uint station_read_matrix_current_mask { get; private set; } = 0x23;
    public int station_read_matrix_next_index { get; private set; }
    public List<object> station_read_matrix_changes { get; } = [];
    private string? matrixPackageSid;
    private byte[]? matrixInitialDacl;
    private string? matrixInitialSaclHash;
    private string? ownedPackageSid;
    private byte[]? clipboardInitialDacl;
    private string? clipboardInitialSaclHash;
    public ClipboardReadControlBoundary? readcontrol_boundary { get; private set; }
    internal string DesktopBinding => station_name + "\\" + desktop_name;

    internal void Open(string nonce, uint holderPid, uint brokerPid, string peerImage, string peerHash, string packageSid)
    {
        holder_pid = holderPid; broker_pid = brokerPid;
        if (holderPid == brokerPid || holderPid == Environment.ProcessId || brokerPid == Environment.ProcessId || holderPid == 0 || brokerPid == 0)
            throw new InvalidDataException("DISTINCT_PEER_IDENTITIES_REQUIRED");
        holder = BindPeer(holderPid, peerImage, peerHash, true); broker = BindPeer(brokerPid, peerImage, peerHash, false);
        Native.Check(Native.GetProcessTimes(holder, out var holderCreated, out _, out _, out _), "original_holder_creation_time");
        holder_creation_filetime = holderCreated.Value;
        peers_OS_identity_verified = true; CheckLive();
        station_name = "AutumnOS_ClipboardProbe_" + nonce;
        station = OpenWindowStationW(station_name, false, 0x000F037F);
        Native.Check(station != IntPtr.Zero, "open_exact_new_leased_station");
        if (Name(station) != station_name || IsVisible(station)) throw new InvalidDataException("EXACT_NONINTERACTIVE_LEASE_REQUIRED");
        noninteractive = true;
        station_DACL_sha256 = CheckSecurity(station, packageSid, 0x23);
        ownedPackageSid = packageSid;
        station_package_rights_exact_0x23 = true;
        station_label_low = HasLowLabel(station);
        if (!station_label_low) throw new InvalidDataException("LEASED_STATION_LOW_LABEL_REQUIRED");
        previousStation = GetProcessWindowStation();
        Native.Check(SetProcessWindowStation(station), "associate_only_owned_controller_with_new_station");
        desktop_name = "AutumnOS_ClipboardDesktop_" + Guid.NewGuid().ToString("N");
        if (Name(GetProcessWindowStation()) != station_name) throw new OwnedAssertionException("EXACT_AUTHENTICATED_STATION_BEFORE_DESKTOP_GUARD");
        CheckLive();
        DesktopFreshness.RequireFresh(desktop_name, "before_create_in_authenticated_new_station", desktop_freshness_checks);
        string user = WindowsIdentity.GetCurrent().User!.Value;
        RawSecurityDescriptor descriptor = new("O:" + user + "G:" + user + "D:P(A;;0x000F01FF;;;" + user + ")(A;;0x000F01FF;;;SY)(A;;0x87;;;" + packageSid + ")S:(ML;;NW;;;LW)");
        byte[] bytes = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(bytes, 0);
        desktop_creation = new DesktopCreationFact { target = desktop_name };
        desktop = DesktopFreshness.CreateOnJoinedWorker(desktop_name, bytes, 0x000F01FF, desktop_creation);
        if (Name(desktop) != desktop_name) throw new InvalidDataException("DESKTOP_NAME_BINDING");
        CheckSecurity(desktop, packageSid, 0x87); desktop_DACL_verified = true;
        desktop_label_low = HasLowLabel(desktop);
        if (!desktop_label_low) throw new InvalidDataException("NEW_DESKTOP_LOW_LABEL_REQUIRED");
    }
    internal void CheckLive()
    {
        holder_original_handle_live = holder != IntPtr.Zero && Native.WaitForSingleObject(holder, 0) == Native.WaitTimeout;
        broker_original_handle_live = broker != IntPtr.Zero && Native.WaitForSingleObject(broker, 0) == Native.WaitTimeout;
        if (!holder_original_handle_live || !broker_original_handle_live) throw new InvalidDataException("EXACT_LEASE_PEERS_NO_LONGER_LIVE");
    }
    internal void BeginStationReadMatrix(string packageSid)
    {
        if (station_read_matrix_started || readcontrol_boundary is not null) throw new OwnedAssertionException("STATION_MATRIX_ALREADY_STARTED_OR_OTHER_BOUNDARY_ACTIVE");
        CheckLive(); matrixPackageSid = packageSid;
        var snapshot = ReadMatrixSecurity();
        if (snapshot.mask != 0x23) throw new OwnedAssertionException("INITIAL_OWNED_STATION_MUST_BE_EXACT_23");
        matrixInitialDacl = snapshot.dacl; matrixInitialSaclHash = snapshot.saclHash;
        initial_DACL_sha256 = snapshot.daclHash; station_read_matrix_current_mask = 0x23; station_read_matrix_started = true;
    }
    internal void EnableClipboardReadControl(string packageSid, IReadOnlyList<PipeChild> actualChildren)
    {
        CheckLive();
        if (readcontrol_boundary is not null || station_read_matrix_started || packageSid != ownedPackageSid)
            throw new OwnedAssertionException("CLIPBOARD_READCONTROL_DISTINCT_OWNED_MODE_ENABLE_ONCE");
        matrixPackageSid = packageSid; var before = ReadMatrixSecurity();
        ReadControlBoundaryPolicy.RequireEnable(before.mask, packageSid == ownedPackageSid, actualChildren.Count == 0);
        clipboardInitialDacl = before.dacl; clipboardInitialSaclHash = before.saclHash;
        readcontrol_boundary = new ClipboardReadControlBoundary { initial_DACL_sha256 = before.daclHash, current_station_package_mask = before.mask };
        AddReadControlObservation("initial", before, actualChildren, true);
        SetClipboardPackageDacl(0x20023);
        var after = ReadMatrixSecurity();
        ReadControlBoundaryPolicy.RequireRuntime(after.mask, packageSid == ownedPackageSid && after.saclHash == clipboardInitialSaclHash);
        readcontrol_boundary.runtime_DACL_sha256 = after.daclHash;
        readcontrol_boundary.current_station_package_mask = after.mask;
        readcontrol_boundary.readcontrol_only_applied = (before.mask ^ after.mask) == 0x20000;
        station_package_rights_exact_0x23 = false;
        if (!readcontrol_boundary.readcontrol_only_applied) throw new OwnedAssertionException("ONLY_READ_CONTROL_ADDITION_REQUIRED");
        AddReadControlObservation("runtime", after, actualChildren, true);
    }
    internal void VerifyClipboardReadControl(string stage, IReadOnlyList<PipeChild> actualChildren)
    {
        if (readcontrol_boundary is null || clipboardInitialDacl is null) throw new OwnedAssertionException("READCONTROL_BOUNDARY_NOT_INITIALIZED");
        CheckLive(); var snapshot = ReadMatrixSecurity();
        ReadControlBoundaryPolicy.RequireRuntime(snapshot.mask, snapshot.saclHash == clipboardInitialSaclHash && matrixPackageSid == ownedPackageSid);
        if (snapshot.daclHash != readcontrol_boundary.runtime_DACL_sha256) throw new OwnedAssertionException("READCONTROL_RUNTIME_DACL_BYTES_CHANGED");
        readcontrol_boundary.current_station_package_mask = snapshot.mask;
        AddReadControlObservation(stage, snapshot, actualChildren, false);
    }
    internal void RestoreClipboardReadControl(IReadOnlyList<PipeChild> actualChildren)
    {
        if (readcontrol_boundary is null) return;
        var before = ReadMatrixSecurity();
        bool retired = actualChildren.All(c => c.retired && c.resource_cleanup_proven && c.audit.AllSignaled &&
            (!c.job.created || c.job.Empty) && (c.no_child_created_proven || c.stdio_complete));
        ReadControlBoundaryPolicy.RequireRestore(before.mask, clipboardInitialDacl is not null && matrixPackageSid == ownedPackageSid &&
            before.saclHash == clipboardInitialSaclHash, retired);
        if (before.mask != 0x23 || before.daclHash != readcontrol_boundary.initial_DACL_sha256) SetClipboardPackageDacl(0x23);
        var after = ReadMatrixSecurity();
        readcontrol_boundary.final_DACL_sha256 = after.daclHash;
        readcontrol_boundary.current_station_package_mask = after.mask;
        readcontrol_boundary.restored_original_DACL = after.mask == 0x23 && after.daclHash == readcontrol_boundary.initial_DACL_sha256 && after.saclHash == clipboardInitialSaclHash;
        station_package_rights_exact_0x23 = after.mask == 0x23;
        if (!readcontrol_boundary.restored_original_DACL) throw new OwnedAssertionException("READCONTROL_EXACT_INITIAL_DACL_RESTORE_REQUIRED");
        AddReadControlObservation("final", after, actualChildren, retired);
    }
    private void SetClipboardPackageDacl(uint mask)
    {
        if (mask is not (0x23 or 0x20023) || clipboardInitialDacl is null || ownedPackageSid is null)
            throw new OwnedAssertionException("CLIPBOARD_BOUNDARY_NO_ARBITRARY_MASK_OR_DESCRIPTOR");
        var acl = new RawAcl(clipboardInitialDacl, 0);
        if (mask == 0x20023)
            foreach (GenericAce generic in acl)
                if (generic is CommonAce ace && ace.SecurityIdentifier.Value == ownedPackageSid) ace.AccessMask = unchecked((int)mask);
        byte[] bytes = new byte[acl.BinaryLength]; acl.GetBinaryForm(bytes, 0); IntPtr memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            uint error = SetSecurityInfo(station, 7, 0x80000004, IntPtr.Zero, IntPtr.Zero, memory, IntPtr.Zero);
            if (error != 0) throw new OwnedNativeException((int)error, "only_owned_station_READCONTROL_package_DACL_change");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    private void AddReadControlObservation(string stage, (uint mask, byte[] dacl, string daclHash, string saclHash) snapshot,
        IReadOnlyList<PipeChild> actualChildren, bool expectedGroupState)
    {
        readcontrol_boundary!.observations.Add(new
        {
            stage, station_package_mask = snapshot.mask, DACL_sha256 = snapshot.daclHash,
            original_handle_and_exact_sid_verified = true, owner_system_package_only = true, protected_DACL = true,
            Low_NW_unchanged = true, desktop_package_mask = 0x87, existing_object_ACL_changes = "none",
            actual_child_count = actualChildren.Count, group_state_gate_satisfied = expectedGroupState,
            group_exit_gate_required = stage is "initial" or "final", boundary_readback_verified = true,
            groups = actualChildren.Select(c => new { kind = c.kind, side = c.side, original_pid = c.audit.RootPid,
                c.retired, c.stdio_complete, c.resource_cleanup_proven, original_handles_signaled = c.audit.AllSignaled,
                job_active_processes = c.job.active_processes, job_process_ids = c.job.last_process_ids, job_empty = !c.job.created || c.job.Empty }).ToArray()
        });
    }
    internal void ApplyStationReadCase(int index, IReadOnlyList<NativeChild> previous)
    {
        CheckLive(); var before = ReadMatrixSecurity(); var spec = StationMatrixPolicy.Cases[index];
        bool exited = NativeGroupsRetired(previous) && previous.All(c => c.diagnostic_execution_completed);
        StationMatrixPolicy.RequireTransition(station_read_matrix_next_index, index, spec.label, spec.mask, before.mask,
            station_read_matrix_started && matrixInitialDacl is not null, exited);
        if (before.mask != spec.mask) SetOnlyMatrixPackageDacl(spec.mask);
        var after = ReadMatrixSecurity();
        if (after.mask != spec.mask || after.saclHash != matrixInitialSaclHash) throw new OwnedAssertionException("STATION_READ_MASK_OR_LOW_LABEL_READBACK_FAILED");
        station_read_matrix_current_mask = after.mask; station_package_rights_exact_0x23 = after.mask == 0x23;
        station_read_matrix_next_index++;
        station_read_matrix_changes.Add(new { stage = "fixed_case_before_launch", spec.index, spec.label, station_package_mask = spec.mask,
            previous_groups_empty_and_retired = exited, before_mask = before.mask, before_DACL_sha256 = before.daclHash,
            after_DACL_sha256 = after.daclHash, owner_system_package_only = true, protected_DACL = true, Low_NW_unchanged = true });
    }
    internal void RestoreStationReadMatrix(IReadOnlyList<NativeChild> children)
    {
        if (!station_read_matrix_started) return;
        var before = ReadMatrixSecurity();
        StationMatrixPolicy.RequireRestore(before.mask, matrixInitialDacl is not null, NativeGroupsRetired(children));
        SetOnlyMatrixPackageDacl(0x23, exactInitial: true);
        var after = ReadMatrixSecurity();
        final_DACL_sha256 = after.daclHash; restored_original_DACL = after.mask == 0x23 && after.daclHash == initial_DACL_sha256 && after.saclHash == matrixInitialSaclHash;
        station_read_matrix_current_mask = after.mask; station_package_rights_exact_0x23 = after.mask == 0x23;
        if (!restored_original_DACL) throw new OwnedAssertionException("EXACT_INITIAL_STATION_DACL_RESTORE_UNPROVEN");
        station_read_matrix_changes.Add(new { stage = "final_cleanup_restore", before_mask = before.mask, station_package_mask = after.mask,
            initial_DACL_sha256, final_DACL_sha256, restored_original_DACL, previous_groups_empty_and_retired = true, Low_NW_unchanged = true });
    }
    private static bool NativeGroupsRetired(IReadOnlyList<NativeChild> children) => children.All(c => c.retired && c.group_empty_proven &&
        (!c.job.created || c.job.Empty) && c.audit.AllSignaled);
    private (uint mask, byte[] dacl, string daclHash, string saclHash) ReadMatrixSecurity()
    {
        // Original owned handle, exact name and security descriptor establish
        // identity during cleanup even if the holder's time scope just expired.
        if (station == IntPtr.Zero || matrixPackageSid is null || Name(station) != station_name || IsVisible(station))
            throw new OwnedAssertionException("EXACT_NEW_PRIVATE_STATION_HANDLE_REQUIRED");
        uint error = GetSecurityInfo(station, 7, 0x17, out _, out _, out _, out _, out IntPtr descriptor);
        if (error != 0) throw new OwnedNativeException((int)error, "matrix_station_security_readback");
        try
        {
            uint size = GetSecurityDescriptorLength(descriptor); if (size is < 20 or > 8192) throw new InvalidDataException("MATRIX_SECURITY_DESCRIPTOR_SIZE");
            byte[] bytes = new byte[size]; Marshal.Copy(descriptor, bytes, 0, bytes.Length); var read = new RawSecurityDescriptor(bytes, 0);
            string user = WindowsIdentity.GetCurrent().User!.Value;
            if (read.Owner?.Value != user || read.Group?.Value != user || (read.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 ||
                read.DiscretionaryAcl is not { Count: 3 } dacl || read.SystemAcl is not { Count: 1 } sacl) throw new OwnedAssertionException("MATRIX_EXACT_OWNER_GROUP_PROTECTED_THREE_ACE_LOW_SHAPE");
            var expected = new HashSet<string>(StringComparer.Ordinal) { user, "S-1-5-18", matrixPackageSid }; uint packageMask = 0;
            foreach (GenericAce generic in dacl)
            {
                if (generic is not CommonAce ace || ace.AceFlags != AceFlags.None || ace.AceQualifier != AceQualifier.AccessAllowed ||
                    !expected.Remove(ace.SecurityIdentifier.Value)) throw new OwnedAssertionException("MATRIX_EXACT_THREE_ALLOW_ACES");
                if (ace.SecurityIdentifier.Value == matrixPackageSid) packageMask = unchecked((uint)ace.AccessMask);
                else if (ace.AccessMask != 0xF037F) throw new OwnedAssertionException("MATRIX_OWNER_SYSTEM_FULL_RIGHTS_UNCHANGED");
            }
            if (expected.Count != 0 || packageMask is not (0x23 or 0x20023 or 0x20123 or 0x20323)) throw new OwnedAssertionException("MATRIX_ONLY_FIXED_READ_PACKAGE_MASKS");
            byte[] label = new byte[sacl[0].BinaryLength]; sacl[0].GetBinaryForm(label, 0);
            if (label[0] != 0x11 || label[1] != 0 || BitConverter.ToUInt32(label, 4) != 1 || new SecurityIdentifier(label, 8).Value != "S-1-16-4096")
                throw new OwnedAssertionException("MATRIX_LOW_NW_LABEL_UNCHANGED_REQUIRED");
            byte[] aclBytes = new byte[dacl.BinaryLength]; dacl.GetBinaryForm(aclBytes, 0);
            byte[] saclBytes = new byte[sacl.BinaryLength]; sacl.GetBinaryForm(saclBytes, 0);
            // Re-read the unchanged original desktop boundary on every phase.
            CheckSecurity(desktop, matrixPackageSid, 0x87);
            if (!HasLowLabel(desktop) || Name(desktop) != desktop_name) throw new OwnedAssertionException("MATRIX_FIXED_ORIGINAL_DESKTOP_87_LOW_BINDING");
            return (packageMask, aclBytes, Convert.ToHexStringLower(SHA256.HashData(aclBytes)), Convert.ToHexStringLower(SHA256.HashData(saclBytes)));
        }
        finally { Native.LocalFree(descriptor); }
    }
    private void SetOnlyMatrixPackageDacl(uint mask, bool exactInitial = false)
    {
        if (matrixInitialDacl is null || matrixPackageSid is null || mask is not (0x23 or 0x20023 or 0x20123 or 0x20323))
            throw new OwnedAssertionException("MATRIX_NO_ARBITRARY_DACL_OR_MASK");
        var acl = new RawAcl(matrixInitialDacl, 0);
        if (!exactInitial)
            foreach (GenericAce generic in acl)
                if (generic is CommonAce ace && ace.SecurityIdentifier.Value == matrixPackageSid) ace.AccessMask = unchecked((int)mask);
        byte[] bytes = new byte[acl.BinaryLength]; acl.GetBinaryForm(bytes, 0); IntPtr memory = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            uint error = SetSecurityInfo(station, 7, 0x80000004, IntPtr.Zero, IntPtr.Zero, memory, IntPtr.Zero);
            if (error != 0) throw new OwnedNativeException((int)error, "only_exact_new_station_package_DACL_write");
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    private static IntPtr BindPeer(uint pid, string image, string sha, bool elevated)
    {
        IntPtr process = Native.OpenProcess(Native.QueryLimited | Native.Synchronize, false, pid);
        Native.Check(process != IntPtr.Zero, "open_original_lease_peer");
        try
        {
            if (Native.WaitForSingleObject(process, 0) != Native.WaitTimeout) throw new InvalidDataException("LIVE_LEASE_PEER_REQUIRED");
            var path = new StringBuilder(32768); uint size = (uint)path.Capacity;
            Native.Check(Native.QueryFullProcessImageNameW(process, 0, path, ref size), "lease_peer_image");
            if (!Path.GetFullPath(path.ToString()).Equals(Path.GetFullPath(image), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("EXACT_FROZEN_LEASE_PEER_IMAGE_REQUIRED");
            using (var file = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read))
                if (OwnedFiles.Hash(file) != sha) throw new InvalidDataException("LEASE_PEER_IMAGE_HASH_MISMATCH");
            Native.Check(Native.OpenProcessToken(process, Native.TokenQuery, out IntPtr token), "lease_peer_token");
            try
            {
                int isElevated = Native.TokenInt(token, 20), ac = Native.TokenInt(token, 29), session = Native.TokenInt(token, 12);
                Native.Check(Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TokenQuery, out IntPtr own), "own_lease_identity_token");
                try
                {
                    if (TokenUser(token) != TokenUser(own) || session != Native.TokenInt(own, 12) || ac != 0 || isElevated != (elevated ? 1 : 0))
                        throw new InvalidDataException("LEASE_PEER_USER_SESSION_ELEVATION_BINDING");
                }
                finally { Native.CloseHandle(own); }
                TokenFacts facts = ProcessAudit.ReadToken(process, null);
                if (elevated ? facts.integrity_rid < 12288 : facts.integrity_rid is < 8192 or >= 12288)
                    throw new InvalidDataException("LEASE_PEER_INTEGRITY_BINDING");
            }
            finally { Native.CloseHandle(token); }
            IntPtr result = process; process = IntPtr.Zero; return result;
        }
        finally { if (process != IntPtr.Zero) Native.CloseHandle(process); }
    }
    private static string TokenUser(IntPtr token)
    {
        IntPtr buffer = Marshal.AllocHGlobal(2048);
        try { Native.Check(Native.GetTokenInformation(token, 1, buffer, 2048, out _), "lease_user_sid"); return Native.SidString(Marshal.ReadIntPtr(buffer)); }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static string CheckSecurity(IntPtr handle, string packageSid, int packageMask)
    {
        uint error = GetSecurityInfo(handle, 7, 5, out _, out _, out _, out _, out IntPtr descriptor);
        if (error != 0) throw new OwnedNativeException((int)error, "owned_user_object_security");
        try
        {
            uint length = GetSecurityDescriptorLength(descriptor); if (length is < 20 or > 8192) throw new InvalidDataException("DESCRIPTOR_SIZE");
            byte[] bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var sd = new RawSecurityDescriptor(bytes, 0); string user = WindowsIdentity.GetCurrent().User!.Value;
            if (sd.Owner?.Value != user || sd.DiscretionaryAcl is not { Count: 3 } acl) throw new InvalidDataException("EXACT_NEW_OBJECT_OWNER_DACL_REQUIRED");
            var expected = new Dictionary<string, int> { [user] = packageMask == 0x23 ? 0x000F037F : 0x000F01FF,
                ["S-1-5-18"] = packageMask == 0x23 ? 0x000F037F : 0x000F01FF, [packageSid] = packageMask };
            foreach (GenericAce generic in acl)
                if (generic is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessAllowed || ace.AceFlags != AceFlags.None ||
                    !expected.Remove(ace.SecurityIdentifier.Value, out int mask) || ace.AccessMask != mask) throw new InvalidDataException("EXACT_THREE_ACE_RIGHTS_REQUIRED");
            if (expected.Count != 0) throw new InvalidDataException("NEW_OBJECT_ACE_MISSING");
            return OwnedFiles.HashText(Convert.ToBase64String(bytes));
        }
        finally { Native.LocalFree(descriptor); }
    }
    private static bool HasLowLabel(IntPtr handle)
    {
        uint error = GetSecurityInfo(handle, 7, 0x10, out _, out _, out _, out _, out IntPtr descriptor);
        if (error != 0) throw new OwnedNativeException((int)error, "new_object_mandatory_label_read");
        try
        {
            uint length = GetSecurityDescriptorLength(descriptor); if (length is < 20 or > 8192) throw new InvalidDataException("LABEL_DESCRIPTOR_SIZE");
            byte[] bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            var sd = new RawSecurityDescriptor(bytes, 0);
            if (sd.SystemAcl is not { Count: 1 } acl) return false;
            byte[] ace = new byte[acl[0].BinaryLength]; acl[0].GetBinaryForm(ace, 0);
            return ace[0] == 0x11 && BitConverter.ToUInt32(ace, 4) == 1 && new SecurityIdentifier(ace, 8).Value == "S-1-16-4096";
        }
        finally { Native.LocalFree(descriptor); }
    }
    private static string Name(IntPtr handle)
    { var value = new StringBuilder(512); Native.Check(GetUserObjectInformationW(handle, 2, value, value.Capacity * 2, out _), "user_object_name"); return value.ToString(); }
    private static bool IsVisible(IntPtr handle)
    { Native.Check(GetUserObjectFlags(handle, 1, out var flags, Marshal.SizeOf<UserFlags>(), out _), "user_object_flags"); return (flags.Flags & 1) != 0; }
    internal void CloseOwnedObjectsAfterChildren()
    {
        bool restored = previousStation == IntPtr.Zero || cleanup.Attempt("restore_controller_station_after_children", () =>
            Native.Check(SetProcessWindowStation(previousStation), "restore_controller_station_after_children"));
        if (desktop != IntPtr.Zero) cleanup.Attempt("close_new_owned_desktop", () =>
        { Native.Check(CloseDesktop(desktop), "close_new_owned_desktop"); desktop = IntPtr.Zero; });
        if (station != IntPtr.Zero) cleanup.Attempt("close_own_leased_station_handle", () =>
        { Native.Check(CloseWindowStation(station), "close_own_leased_station_handle"); station = IntPtr.Zero; });
        if (holder != IntPtr.Zero) cleanup.Attempt("close_owned_holder_query_handle", () =>
        { Native.Check(Native.CloseHandle(holder), "close_owned_holder_query_handle"); holder = IntPtr.Zero; });
        if (broker != IntPtr.Zero) cleanup.Attempt("close_owned_broker_query_handle", () =>
        { Native.Check(Native.CloseHandle(broker), "close_owned_broker_query_handle"); broker = IntPtr.Zero; });
        closed_owned_desktop_and_station = restored && desktop == IntPtr.Zero && station == IntPtr.Zero && holder == IntPtr.Zero && broker == IntPtr.Zero;
    }
    [StructLayout(LayoutKind.Sequential)] private struct UserFlags { internal int Inherit, Reserved; internal uint Flags; }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenWindowStationW(string name, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetProcessWindowStation(IntPtr handle);
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseWindowStation(IntPtr station);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder value, int bytes, out int required);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectFlags(IntPtr handle, int index, out UserFlags value, int bytes, out int required);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(IntPtr handle, int type, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint SetSecurityInfo(IntPtr handle, int type, uint info, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
}
