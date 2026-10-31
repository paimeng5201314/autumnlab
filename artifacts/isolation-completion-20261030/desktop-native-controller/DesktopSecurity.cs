using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace AutumnOS.OwnedBrowserParent;

internal sealed class DesktopSecurity
{
    private IntPtr handle;
    public string name { get; } = "AutumnOS_DesktopLoader_" + Guid.NewGuid().ToString("N");
    public List<DesktopProbeFact> freshness { get; } = [];
    public DesktopCreationFact? creation { get; private set; }
    public bool exact_descriptor_verified { get; private set; }
    public bool low_label_verified { get; private set; }
    public bool original_handle_closed { get; private set; }
    public bool post_close_not_found { get; private set; }
    public string? descriptor_sha256 { get; private set; }
    public CleanupJournal cleanup { get; } = new();

    internal static string CurrentStation()
    { var text = new StringBuilder(512); Native.Check(GetUserObjectInformationW(GetProcessWindowStation(), 2, text, text.Capacity * 2, out _), "current_station_metadata"); return text.ToString(); }
    internal void Create(string packageSid, uint packageMask)
    {
        if (packageMask is not (0x87 or 0x200C7) || CurrentStation() != "WinSta0") throw new OwnedAssertionException("FIXED_DESKTOP_CASE_CONTEXT_REQUIRED");
        DesktopFreshness.RequireFresh(name, "before_create_exact_current_station_target", freshness);
        string owner = WindowsIdentity.GetCurrent().User!.Value;
        RawSecurityDescriptor sd = new("O:" + owner + "G:" + owner + "D:P(A;;0x000F01FF;;;" + owner + ")(A;;0x000F01FF;;;SY)(A;;0x" +
            packageMask.ToString("x") + ";;;" + packageSid + ")S:(ML;;NW;;;LW)");
        byte[] data = new byte[sd.BinaryLength]; sd.GetBinaryForm(data, 0);
        creation = new DesktopCreationFact { target = name }; handle = DesktopFreshness.CreateOnJoinedWorker(name, data, 0xF01FF, creation);
        var text = new StringBuilder(512); Native.Check(GetUserObjectInformationW(handle, 2, text, text.Capacity * 2, out _), "exact_created_desktop_name");
        if (text.ToString() != name) throw new OwnedAssertionException("EXACT_NEW_DESKTOP_NAME_REQUIRED");
        uint error = GetSecurityInfo(handle, 7, 0x17, out _, out _, out _, out _, out IntPtr descriptor);
        if (error != 0) throw new OwnedNativeException((int)error, "new_desktop_security_readback");
        try
        {
            uint size = GetSecurityDescriptorLength(descriptor); if (size is < 20 or > 8192) throw new InvalidDataException("NEW_DESCRIPTOR_SIZE");
            byte[] actual = new byte[size]; Marshal.Copy(descriptor, actual, 0, actual.Length); var read = new RawSecurityDescriptor(actual, 0);
            var expected = new Dictionary<string, int> { [owner] = 0xF01FF, ["S-1-5-18"] = 0xF01FF, [packageSid] = unchecked((int)packageMask) };
            if (read.Owner?.Value != owner || read.Group?.Value != owner || (read.ControlFlags & ControlFlags.DiscretionaryAclProtected) == 0 || read.DiscretionaryAcl is not { Count: 3 } acl)
                throw new OwnedAssertionException("EXACT_OWNER_GROUP_PROTECTED_THREE_ACE_DESCRIPTOR_REQUIRED");
            foreach (GenericAce generic in acl)
                if (generic is not CommonAce ace || ace.AceQualifier != AceQualifier.AccessAllowed || ace.AceFlags != AceFlags.None ||
                    !expected.Remove(ace.SecurityIdentifier.Value, out int mask) || ace.AccessMask != mask) throw new OwnedAssertionException("EXACT_DESKTOP_PACKAGE_MASK_READBACK_REQUIRED");
            exact_descriptor_verified = expected.Count == 0;
            if (read.SystemAcl is not { Count: 1 } labels) throw new OwnedAssertionException("ONE_LOW_NW_LABEL_REQUIRED");
            byte[] label = new byte[labels[0].BinaryLength]; labels[0].GetBinaryForm(label, 0);
            low_label_verified = label[0] == 0x11 && label[1] == 0 && BitConverter.ToUInt32(label, 4) == 1 && new SecurityIdentifier(label, 8).Value == "S-1-16-4096";
            if (!exact_descriptor_verified || !low_label_verified) throw new OwnedAssertionException("NEW_DESKTOP_SECURITY_PROOF_FAILED");
            descriptor_sha256 = OwnedFiles.HashText(Convert.ToBase64String(actual));
        }
        finally { Native.LocalFree(descriptor); }
    }
    internal void CloseAndVerify()
    {
        if (handle != IntPtr.Zero) cleanup.Attempt("close_original_joined_worker_desktop_handle", () =>
        { Native.Check(CloseDesktop(handle), "close_exact_owned_desktop"); handle = IntPtr.Zero; original_handle_closed = true; });
        if (original_handle_closed && !post_close_not_found) cleanup.Attempt("exact_desktop_post_close_not_found", () =>
        { DesktopFreshness.RequireFresh(name, "after_original_desktop_close", freshness); post_close_not_found = true; });
    }
    internal bool NoObjectWasCreated => creation is null || !creation.returned_handle;
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder text, int bytes, out uint required);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr handle);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(IntPtr handle, int kind, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
}
