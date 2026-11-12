using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AutumnOS.SyntheticLuaLauncher;

internal sealed record OwnedDesktop(IntPtr Handle, string Name, string DescriptorSha256);
internal sealed class DesktopDescriptorMismatchException(object diagnostic) : InvalidOperationException("NEW_DESKTOP_OWNER_DACL_OR_LOW_LABEL_READBACK_MISMATCH")
{
    internal object Diagnostic { get; } = diagnostic;
}

internal static class PrivateDesktopNative
{
    private const uint DescriptorInformation = 0x17; // OWNER | GROUP | DACL | LABEL; no audit SACL query.
    private const uint RestrictedDesktopRights = 0x200ff; // No SWITCHDESKTOP, WRITE_DAC or WRITE_OWNER.
    internal static Win32Exception Error(string stage) => new(Marshal.GetLastWin32Error(), stage);

    internal static OwnedDesktop Create()
    {
        // Keep creation and any thread-local desktop association on a fresh MTA.
        // The parent launch thread never calls CreateDesktop or association APIs.
        var completion = new TaskCompletionSource<OwnedDesktop>(TaskCreationOptions.RunContinuationsAsynchronously);
        object gate = new();
        bool abandoned = false;
        var worker = new Thread(() =>
        {
            try
            {
                OwnedDesktop value = CreateOnWorker();
                lock (gate)
                {
                    if (abandoned) CloseDesktop(value.Handle);
                    else completion.TrySetResult(value);
                }
            }
            catch (Exception error) { completion.TrySetException(error); }
        }) { IsBackground = true, Name = "AutumnOS private desktop creation" };
        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        try { return completion.Task.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult(); }
        catch (TimeoutException)
        {
            lock (gate)
            {
                abandoned = true;
                if (completion.Task.IsCompletedSuccessfully) CloseDesktop(completion.Task.Result.Handle);
            }
            throw new TimeoutException("NEW_DESKTOP_CREATION_WORKER_TIMEOUT_NO_CHILD_LAUNCHED");
        }
    }

    private static OwnedDesktop CreateOnWorker()
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent(TokenAccessLevels.Query);
        string user = identity.User?.Value ?? throw new InvalidOperationException("OWN_USER_SID_MISSING");
        string name = "AutumnOS_SyntheticLua_" + Guid.NewGuid().ToString("N");
        // private02 proved Windows maps desktop GA to 0x000f01ff and marks this
        // label SACL auto-inherited. Encode those exact observed semantics in
        // the input, then retain the same strict descriptor equality check.
        string sddl = "O:" + user + "G:" + user + "D:P(A;;0x000F01FF;;;" + user + ")(A;;0x" + RestrictedDesktopRights.ToString("X8") + ";;;S-1-15-2-2)S:AI(ML;;NW;;;LW)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out IntPtr descriptor, out _)) throw Error("NEW_DESKTOP_DESCRIPTOR_PARSE_FAILED");
        try
        {
            string expected = DescriptorString(descriptor);
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor, Inherit = false };
            IntPtr handle = CreateDesktop(name, IntPtr.Zero, IntPtr.Zero, 0, 0xf01ff, ref attributes);
            if (handle == IntPtr.Zero) throw Error("NEW_DESKTOP_CREATE_FAILED");
            try
            {
                if (Native.UserObjectName(handle) != name) throw new InvalidOperationException("NEW_DESKTOP_NAME_READBACK_MISMATCH");
                uint status = GetSecurityInfo(handle, 7, DescriptorInformation, out _, out _, out _, out _, out IntPtr observedDescriptor);
                if (status != 0) throw new Win32Exception((int)status, "NEW_DESKTOP_OWNER_DACL_LABEL_QUERY_FAILED");
                try
                {
                    string observed = DescriptorString(observedDescriptor);
                    if (observed != expected)
                    {
                        object expectedFacts = DescriptorFacts(descriptor), observedFacts = DescriptorFacts(observedDescriptor);
                        throw new DesktopDescriptorMismatchException(new
                        {
                            expected_descriptor = expectedFacts, observed_descriptor = observedFacts,
                            expected_native_sddl_sha256 = Hash(expected), observed_native_sddl_sha256 = Hash(observed),
                            structure_equal = JsonSerializer.Serialize(expectedFacts) == JsonSerializer.Serialize(observedFacts),
                            interpretation = "Strict equality failed. Facts are diagnostic only; no ACL or mandatory-label assertion was relaxed."
                        });
                    }
                    // Native SDDL comparison includes the label. An explicit additional
                    // label ACE check avoids inferring Low from successful creation alone.
                    RequireLowNoWriteUpLabel(observedDescriptor);
                    uint bytes = GetSecurityDescriptorLength(observedDescriptor);
                    if (bytes == 0 || bytes > 1048576) throw new InvalidDataException("NEW_DESKTOP_DESCRIPTOR_SIZE_INVALID");
                    byte[] copy = new byte[bytes]; Marshal.Copy(observedDescriptor, copy, 0, copy.Length);
                    return new(handle, name, Convert.ToHexStringLower(SHA256.HashData(copy)));
                }
                finally { LocalFree(observedDescriptor); }
            }
            catch { CloseDesktop(handle); throw; }
        }
        finally { LocalFree(descriptor); }
    }

    private static string DescriptorString(IntPtr descriptor)
    {
        if (descriptor == IntPtr.Zero || !IsValidSecurityDescriptor(descriptor)) throw new InvalidDataException("DESKTOP_DESCRIPTOR_INVALID");
        if (!ConvertSecurityDescriptorToStringSecurityDescriptor(descriptor, 1, DescriptorInformation, out IntPtr text, out _)) throw Error("DESKTOP_DESCRIPTOR_STRING_CONVERSION_FAILED");
        try { return Marshal.PtrToStringUni(text) ?? throw new InvalidDataException("DESKTOP_DESCRIPTOR_STRING_EMPTY"); }
        finally { LocalFree(text); }
    }

    private static object DescriptorFacts(IntPtr descriptor)
    {
        if (!GetSecurityDescriptorOwner(descriptor, out IntPtr owner, out bool ownerDefaulted) ||
            !GetSecurityDescriptorGroup(descriptor, out IntPtr group, out bool groupDefaulted) ||
            !GetSecurityDescriptorControl(descriptor, out ushort control, out uint revision) ||
            !GetSecurityDescriptorDacl(descriptor, out bool daclPresent, out IntPtr dacl, out bool daclDefaulted) ||
            !GetSecurityDescriptorSacl(descriptor, out bool saclPresent, out IntPtr sacl, out bool saclDefaulted))
            throw Error("DESKTOP_DESCRIPTOR_DIAGNOSTIC_READ_FAILED");
        return new
        {
            owner_sid_sha256 = owner == IntPtr.Zero ? null : Hash(new SecurityIdentifier(owner).Value), owner_defaulted = ownerDefaulted,
            group_sid_sha256 = group == IntPtr.Zero ? null : Hash(new SecurityIdentifier(group).Value), group_defaulted = groupDefaulted,
            revision, control_hex = "0x" + control.ToString("X4"),
            dacl_protected = (control & 0x1000) != 0, sacl_protected = (control & 0x2000) != 0,
            dacl_auto_inherited = (control & 0x400) != 0, sacl_auto_inherited = (control & 0x800) != 0,
            dacl_present = daclPresent, dacl_defaulted = daclDefaulted, dacl = AclFacts(dacl),
            sacl_present = saclPresent, sacl_defaulted = saclDefaulted, sacl = AclFacts(sacl)
        };
    }

    private static object[]? AclFacts(IntPtr acl)
    {
        if (acl == IntPtr.Zero) return null;
        if (!GetAclInformation(acl, out AclSize information, (uint)Marshal.SizeOf<AclSize>(), 2)) throw Error("DESKTOP_DIAGNOSTIC_ACL_QUERY_FAILED");
        if (information.AceCount > 64) throw new InvalidDataException("DESKTOP_DIAGNOSTIC_ACE_COUNT_INVALID");
        var facts = new List<object>();
        for (uint i = 0; i < information.AceCount; i++)
        {
            if (!GetAce(acl, i, out IntPtr ace)) throw Error("DESKTOP_DIAGNOSTIC_ACE_QUERY_FAILED");
            byte type = Marshal.ReadByte(ace), flags = Marshal.ReadByte(ace, 1);
            ushort size = unchecked((ushort)Marshal.ReadInt16(ace, 2));
            if (size < 4 || size > information.BytesInUse) throw new InvalidDataException("DESKTOP_DIAGNOSTIC_ACE_SIZE_INVALID");
            byte[] bytes = new byte[size]; Marshal.Copy(ace, bytes, 0, size);
            string? sid = null;
            uint? mask = null;
            if (type is 0 or 1 or 0x11 && size >= 20)
            {
                sid = new SecurityIdentifier(ace + 8).Value;
                mask = unchecked((uint)Marshal.ReadInt32(ace, 4));
            }
            facts.Add(new
            {
                index = i, type, flags, size, access_mask = mask,
                well_known_sid = sid is "S-1-15-2-2" or "S-1-15-2-1" or "S-1-16-4096" or "S-1-16-8192" or "S-1-16-12288" ? sid : null,
                sid_sha256 = sid is null ? null : Hash(sid), ace_sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes))
            });
        }
        return facts.ToArray();
    }
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static void RequireLowNoWriteUpLabel(IntPtr descriptor)
    {
        if (!GetSecurityDescriptorSacl(descriptor, out bool present, out IntPtr acl, out _) || !present || acl == IntPtr.Zero)
            throw Error("NEW_DESKTOP_MANDATORY_LABEL_MISSING");
        if (!GetAclInformation(acl, out AclSize information, (uint)Marshal.SizeOf<AclSize>(), 2)) throw Error("NEW_DESKTOP_LABEL_ACL_QUERY_FAILED");
        if (information.AceCount != 1 || !GetAce(acl, 0, out IntPtr ace)) throw new InvalidDataException("NEW_DESKTOP_LABEL_ACE_COUNT_INVALID");
        byte type = Marshal.ReadByte(ace), flags = Marshal.ReadByte(ace, 1);
        ushort size = unchecked((ushort)Marshal.ReadInt16(ace, 2));
        if (type != 0x11 || flags != 0 || size < 20 || Marshal.ReadInt32(ace, 4) != 1 || new SecurityIdentifier(ace + 8).Value != "S-1-16-4096")
            throw new InvalidDataException("NEW_DESKTOP_LABEL_MUST_BE_LOW_NO_WRITE_UP");
    }

    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct AclSize { public uint AceCount, BytesInUse, BytesFree; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string value, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertSecurityDescriptorToStringSecurityDescriptor(IntPtr descriptor, uint revision, uint information, out IntPtr text, out uint length);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern uint GetSecurityInfo(IntPtr handle, int type, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool IsValidSecurityDescriptor(IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr acl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present, out IntPtr acl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorOwner(IntPtr descriptor, out IntPtr owner, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorGroup(IntPtr descriptor, out IntPtr group, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetSecurityDescriptorControl(IntPtr descriptor, out ushort control, out uint revision);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetAclInformation(IntPtr acl, out AclSize information, uint size, int kind);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetAce(IntPtr acl, uint index, out IntPtr ace);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, ref SecurityAttributes attributes);
    [DllImport("user32.dll", SetLastError = true)] internal static extern bool CloseDesktop(IntPtr desktop);
}
