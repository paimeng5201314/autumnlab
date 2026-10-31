using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace AutumnOS.OwnedBrowserParent;

// This object is owned by the ordinary NETONLY StationHost, never by a UAC
// holder. It never adopts/re-ACLs an existing station or desktop.
internal sealed class OwnedStation(string nonce)
{
    private IntPtr station, desktop, originalStation, derivedSid, createdSid;
    private string? profileFolder;
    public string profile_name { get; } = "AutumnOS.Paimon.SessionBrowser." + nonce;
    public string station_name { get; private set; } = "";
    public string desktop_name { get; } = "AutumnOS_SessionDesktop_" + Guid.NewGuid().ToString("N");
    public string package_sid { get; private set; } = "";
    public string package_sid_sha256 => OwnedFiles.HashText(package_sid);
    public bool station_created { get; private set; }
    public bool station_verified { get; private set; }
    public bool desktop_verified { get; private set; }
    public string? station_descriptor_sha256 { get; private set; }
    public string? desktop_descriptor_sha256 { get; private set; }
    public DesktopCreationFact? desktop_creation { get; private set; }
    public List<DesktopProbeFact> desktop_probes { get; } = [];
    public bool profile_created { get; private set; }
    public bool original_profile_path_captured => profileFolder is not null;
    public int? profile_delete_hresult { get; private set; }
    public int? profile_path_post_error { get; private set; }
    public int? profile_sid_post_hresult { get; private set; }
    public bool profile_path_absent { get; private set; }
    public bool profile_sid_absent { get; private set; }
    public bool original_station_restored { get; private set; }
    public bool desktop_closed { get; private set; }
    public bool desktop_absent { get; private set; }
    public bool station_closed { get; private set; }
    public bool no_desktop_created => desktop_creation is null || !desktop_creation.returned_handle;
    public CleanupJournal cleanup { get; } = new();
    public uint station_package_mask => 0x20023;
    public uint desktop_package_mask => 0x87;
    internal IntPtr PackageSid => createdSid;
    internal string DesktopBinding => station_name + "\\" + desktop_name;
    internal string ProfileFolder => profileFolder ?? throw new InvalidOperationException("OWNED_PROFILE_FOLDER_UNAVAILABLE");

    internal void Create()
    {
        if (station != IntPtr.Zero || station_created || profile_created) throw new InvalidOperationException("ONE_NEW_OWNED_CONTEXT_ONLY");
        int hr = DeriveAppContainerSidFromAppContainerName(profile_name, out derivedSid);
        if (hr != 0 || derivedSid == IntPtr.Zero) throw new COMException("DERIVE_NEW_PROFILE_SID", hr);
        package_sid = Native.SidString(derivedSid);
        originalStation = SessionNative.GetProcessWindowStation();
        if (originalStation == IntPtr.Zero || SessionNative.StationName(originalStation) != "WinSta0") throw new InvalidDataException("STATION_HOST_ORIGINAL_WINSTA0_REQUIRED");
        string owner = WindowsIdentity.GetCurrent().User!.Value;
        string sddl = $"O:{owner}G:{owner}D:P(A;;0xF037F;;;{owner})(A;;0xF037F;;;SY)(A;;0x20023;;;{package_sid})S:(ML;;NW;;;LW)";
        Native.Check(SessionNative.ConvertDescriptor(sddl, 1, out IntPtr descriptor, out _), "owned_automatic_station_descriptor");
        try
        {
            var attributes = new SessionNative.SecurityAttributes { length = Marshal.SizeOf<SessionNative.SecurityAttributes>(), descriptor = descriptor, inherit = false };
            station = SessionNative.CreateWindowStationW(null, 1 /* CWF_CREATE_ONLY */, 0xF037F, ref attributes);
            Native.Check(station != IntPtr.Zero, "one_NULL_CWF_CREATE_ONLY_automatic_station"); station_created = true;
        }
        finally { Native.LocalFree(descriptor); }
        station_name = SessionNative.StationName(station);
        if (station_name.Equals("WinSta0", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("NEW_AUTOMATIC_NONINTERACTIVE_STATION_REQUIRED");
        station_descriptor_sha256 = VerifyStationHandle(station, station_name, owner, package_sid); station_verified = true;
        Native.Check(SessionNative.SetProcessWindowStation(station), "associate_only_NETONLY_station_host");
        if (SessionNative.StationName(SessionNative.GetProcessWindowStation()) != station_name) throw new InvalidDataException("OWNED_STATION_ASSOCIATION");
        DesktopFreshness.RequireFresh(desktop_name, "before_new_owned_desktop", desktop_probes);
        var desktopDescriptor = new RawSecurityDescriptor($"O:{owner}G:{owner}D:P(A;;0xF01FF;;;{owner})(A;;0xF01FF;;;SY)(A;;0x87;;;{package_sid})S:(ML;;NW;;;LW)");
        byte[] bytes = new byte[desktopDescriptor.BinaryLength]; desktopDescriptor.GetBinaryForm(bytes, 0);
        desktop_creation = new DesktopCreationFact { target = desktop_name };
        desktop = DesktopFreshness.CreateOnJoinedWorker(desktop_name, bytes, 0xF01FF, desktop_creation);
        if (SessionNative.StationName(desktop) != desktop_name) throw new InvalidDataException("OWNED_DESKTOP_EXACT_NAME");
        desktop_descriptor_sha256 = VerifySecurity(desktop, owner, package_sid, 0x87, 0xF01FF); desktop_verified = true;
        hr = Native.CreateAppContainerProfile(profile_name, "AutumnOS session browser prototype", "Owned temporary zero-capability synthetic-page prototype", IntPtr.Zero, 0, out createdSid);
        if (hr != 0) throw new COMException("CREATE_EXACT_NEW_PROFILE_NO_ADOPTION", hr);
        profile_created = true;
        if (createdSid == IntPtr.Zero || Native.SidString(createdSid) != package_sid) throw new InvalidDataException("CREATED_PROFILE_DERIVED_SID_BINDING");
        CaptureOriginalProfileFolder();
    }
    private void CaptureOriginalProfileFolder()
    {
        int hr = Native.GetAppContainerFolderPath(package_sid, out IntPtr folder);
        try
        {
            if (hr != 0 || folder == IntPtr.Zero) throw new COMException("QUERY_EXACT_NEW_PROFILE_FOLDER", hr);
            profileFolder = Path.GetFullPath(Marshal.PtrToStringUni(folder)!);
            OwnedFiles.FixedLocal(profileFolder); OwnedFiles.PlainAncestors(profileFolder);
        }
        finally { if (folder != IntPtr.Zero) Marshal.FreeCoTaskMem(folder); }
    }
    internal void VerifyRuntime()
    {
        string owner = WindowsIdentity.GetCurrent().User!.Value;
        if (!station_created || !station_verified || !desktop_verified || SessionNative.StationName(SessionNative.GetProcessWindowStation()) != station_name ||
            VerifyStationHandle(station, station_name, owner, package_sid) != station_descriptor_sha256 ||
            VerifySecurity(desktop, owner, package_sid, 0x87, 0xF01FF) != desktop_descriptor_sha256)
            throw new InvalidDataException("OWNED_STATION_DESKTOP_RUNTIME_SECURITY_BINDING");
    }
    internal void CleanupAfterGroupEmpty(bool actualGroupRetired)
    {
        if (!actualGroupRetired) return; // Live/unproven children keep all resources owned.
        if (profile_created && profileFolder is null) cleanup.Attempt("capture_original_owned_profile_path_before_any_delete", CaptureOriginalProfileFolder);
        if (profile_created && profileFolder is not null && profile_delete_hresult != 0) cleanup.Attempt("delete_only_exact_new_profile", () =>
        {
            profile_delete_hresult = Native.DeleteAppContainerProfile(profile_name);
            if (profile_delete_hresult != 0) throw new COMException("DELETE_EXACT_PROFILE", profile_delete_hresult.Value);
        });
        if (profile_created && profileFolder is not null) cleanup.Attempt("profile_original_path_and_same_SID_absence", () =>
        {
            uint attrs = Native.GetFileAttributesW(profileFolder); profile_path_post_error = attrs == uint.MaxValue ? Marshal.GetLastWin32Error() : 0;
            profile_path_absent = attrs == uint.MaxValue && profile_path_post_error is 2 or 3;
            profile_sid_post_hresult = Native.GetAppContainerFolderPath(package_sid, out IntPtr path);
            if (path != IntPtr.Zero) Marshal.FreeCoTaskMem(path);
            profile_sid_absent = profile_sid_post_hresult is unchecked((int)0x80070002) or unchecked((int)0x80070003);
            if (!profile_path_absent || !profile_sid_absent) throw new InvalidDataException("EXACT_PROFILE_ABSENCE_UNPROVEN");
        });
        if (desktop != IntPtr.Zero) cleanup.Attempt("close_original_joined_worker_HDESK", () =>
        { Native.Check(CloseDesktop(desktop), "close_exact_owned_desktop"); desktop = IntPtr.Zero; desktop_closed = true; });
        if (desktop_closed && !desktop_absent) cleanup.Attempt("owned_desktop_exact_not_found", () =>
        {
            if (SessionNative.StationName(SessionNative.GetProcessWindowStation()) != station_name) throw new InvalidDataException("DESKTOP_ABSENCE_MUST_USE_ITS_EXACT_OWNED_STATION");
            DesktopFreshness.RequireFresh(desktop_name, "after_original_desktop_closed", desktop_probes); desktop_absent = true;
        });
        if (no_desktop_created || desktop_absent) cleanup.Attempt("restore_borrowed_original_station", () =>
        {
            if (originalStation != IntPtr.Zero)
            {
                Native.Check(SessionNative.SetProcessWindowStation(originalStation), "restore_original_station");
                original_station_restored = SessionNative.GetProcessWindowStation() == originalStation && SessionNative.StationName(originalStation) == "WinSta0";
                if (!original_station_restored) throw new InvalidDataException("ORIGINAL_STATION_RESTORE_UNPROVEN");
            }
        });
        if (station != IntPtr.Zero && original_station_restored && (no_desktop_created || desktop_absent) && desktop == IntPtr.Zero) cleanup.Attempt("close_original_created_HWINSTA", () =>
        { Native.Check(SessionNative.CloseWindowStation(station), "close_exact_owned_station"); station = IntPtr.Zero; station_closed = true; });
        if (createdSid != IntPtr.Zero) { Native.FreeSid(createdSid); createdSid = IntPtr.Zero; }
        if (derivedSid != IntPtr.Zero) { Native.FreeSid(derivedSid); derivedSid = IntPtr.Zero; }
    }
    public bool cleanup_proven => (!profile_created || profile_delete_hresult == 0 && profile_path_absent && profile_sid_absent) &&
        (!station_created || (no_desktop_created || desktop_closed && desktop_absent) && original_station_restored && station_closed);

    internal static string VerifyStationHandle(IntPtr handle, string name, string owner, string package)
    {
        if (SessionNative.StationName(handle) != name || !GetObjectFlags(handle, 1, out ObjectFlags flags, Marshal.SizeOf<ObjectFlags>(), out _) || flags.inherit || (flags.flags & 1) != 0)
            throw new InvalidDataException("EXACT_NONINTERACTIVE_NONINHERITED_STATION");
        return VerifySecurity(handle, owner, package, 0x20023, 0xF037F);
    }
    private static string VerifySecurity(IntPtr handle, string owner, string package, uint packageMask, uint fullMask)
    {
        uint error = GetSecurityInfo(handle, 7, 0x17, out _, out _, out _, out _, out IntPtr descriptor);
        if (error != 0) throw new OwnedNativeException(unchecked((int)error), "owned_USER_object_security_readback");
        try
        {
            uint length = GetSecurityDescriptorLength(descriptor); if (length is < 20 or > 8192) throw new InvalidDataException("OWNED_USER_DESCRIPTOR_LENGTH");
            byte[] bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length); var sd = new RawSecurityDescriptor(bytes, 0);
            if (sd.Owner?.Value != owner || sd.Group?.Value != owner || (sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 || sd.DiscretionaryAcl is not { Count: 3 } acl)
                throw new InvalidDataException("OWNED_USER_OWNER_GROUP_PROTECTED_THREE_ACE");
            var expected = new Dictionary<string, uint> { [owner] = fullMask, ["S-1-5-18"] = fullMask, [package] = packageMask };
            foreach (GenericAce generic in acl)
                if (generic is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessAllowed || ace.AceFlags != AceFlags.None ||
                    !expected.Remove(ace.SecurityIdentifier.Value, out uint mask) || unchecked((uint)ace.AccessMask) != mask) throw new InvalidDataException("OWNED_USER_EXACT_PERMISSION_MASKS");
            if (expected.Count != 0 || sd.SystemAcl is not { Count: 1 } labels) throw new InvalidDataException("OWNED_USER_ONE_LOW_NW_LABEL");
            byte[] label = new byte[labels[0].BinaryLength]; labels[0].GetBinaryForm(label, 0);
            if (label[0] != 0x11 || label[1] != 0 || BitConverter.ToUInt32(label, 4) != 1 || new SecurityIdentifier(label, 8).Value != "S-1-16-4096")
                throw new InvalidDataException("OWNED_USER_EXACT_LOW_NW_LABEL");
            return OwnedFiles.HashText(Convert.ToBase64String(bytes));
        }
        finally { Native.LocalFree(descriptor); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ObjectFlags { [MarshalAs(UnmanagedType.Bool)] internal bool inherit; [MarshalAs(UnmanagedType.Bool)] internal bool reserved; internal uint flags; }
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetObjectFlags(IntPtr handle, int kind, out ObjectFlags flags, int bytes, out int used);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(IntPtr handle, int type, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
}
