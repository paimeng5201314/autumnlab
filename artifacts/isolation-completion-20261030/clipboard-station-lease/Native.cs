using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace AutumnOS.OwnedClipboardStationLease;

internal sealed record Identity(string Sid, uint Session, bool Elevated, int Integrity, bool AppContainer);

internal sealed class HeldProcess : IDisposable
{
    internal readonly SafeProcessHandle Handle;
    internal readonly uint Id;
    internal HeldProcess(SafeProcessHandle handle)
    {
        Handle = handle;
        if (handle.IsInvalid) throw Native.Error("PROCESS_HANDLE_REQUIRED");
        Id = Native.GetProcessId(handle);
        // A failed PID query must not discard a valid original process handle.
        // Admission rejects it below, while the caller still observes its exit.
    }
    internal static HeldProcess Open(uint pid) => new(Native.OpenProcess(0x00101000 /* QUERY_LIMITED_INFORMATION | SYNCHRONIZE */, false, pid));
    internal bool Exited
    {
        get
        {
            uint result = Native.WaitForSingleObject(Handle, 0);
            if (result is not (0 or 258)) throw Native.Error("HELD_PROCESS_WAIT_FAILED");
            return result == 0;
        }
    }
    internal void RequireLive() { if (Exited) throw new InvalidOperationException("LEASE_PEER_EXITED"); }
    internal async Task WaitForExit(CancellationToken observationCancellation = default)
    {
        bool faultReported = false;
        while (true)
        {
            observationCancellation.ThrowIfCancellationRequested();
            uint result = Native.WaitForSingleObject(Handle, 0);
            if (result == 0) return;
            if (result != 258 && !faultReported)
            {
                faultReported = true;
                Console.WriteLine("{\"schema_version\":1,\"status\":\"pending\",\"stage\":\"original_process_handle_exit_not_proven\"}");
            }
            // Failed observation preserves the original handle and all pins;
            // never reopen by PID or reinterpret a timeout as cleanup.
            await Task.Delay(100, observationCancellation);
        }
    }
    internal uint ExitCode
    {
        get
        {
            if (!Exited) throw new InvalidOperationException("ORIGINAL_PROCESS_EXIT_NOT_PROVEN");
            if (!Native.GetExitCodeProcess(Handle, out uint code)) throw Native.Error("PROCESS_EXIT_CODE_QUERY_FAILED");
            return code;
        }
    }
    internal Identity RequireIdentity(Identity current, bool elevated, string expectedImage)
    {
        if (Id == 0) throw new InvalidOperationException("PROCESS_ID_QUERY_NOT_PROVEN");
        RequireLive(); Identity peer = Native.GetIdentity(Handle);
        if (peer.Sid != current.Sid || peer.Session != current.Session || peer.Elevated != elevated || peer.AppContainer ||
            peer.Integrity != (elevated ? 0x3000 : 0x2000)) throw new InvalidOperationException("LEASE_PEER_TOKEN_MISMATCH");
        if (!string.Equals(Native.Image(Handle), expectedImage, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("LEASE_PEER_IMAGE_MISMATCH");
        RequireLive(); return peer;
    }
    public void Dispose() => Handle.Dispose();
}

internal static class Native
{
    internal static Win32Exception Error(string stage) => new(Marshal.GetLastWin32Error(), stage);
    internal static Identity CurrentIdentity()
    {
        using var current = new SafeProcessHandle(GetCurrentProcess(), ownsHandle: false);
        return GetIdentity(current);
    }
    internal static Identity GetIdentity(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 8 /* TOKEN_QUERY */, out SafeAccessTokenHandle token)) throw Error("TOKEN_OPEN_FAILED");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            uint pid = GetProcessId(process);
            if (pid == 0 || !ProcessIdToSessionId(pid, out uint session)) throw Error("SESSION_QUERY_FAILED");
            return new(identity.User?.Value ?? throw new InvalidOperationException("TOKEN_USER_REQUIRED"), session,
                TokenInt(token, 20) != 0, Integrity(token), TokenInt(token, 29) != 0);
        }
    }
    private static int TokenInt(SafeAccessTokenHandle token, int kind)
    {
        if (!GetTokenInformationInt(token, kind, out int result, 4, out int required) || required != 4) throw Error("TOKEN_INT_QUERY_FAILED");
        return result;
    }
    private static int Integrity(SafeAccessTokenHandle token)
    {
        bool first = GetTokenInformation(token, 25, IntPtr.Zero, 0, out int bytes);
        if (first || Marshal.GetLastWin32Error() != 122 || bytes is < 16 or > 4096) throw new InvalidOperationException("TOKEN_LABEL_SIZE_INVALID");
        IntPtr buffer = Marshal.AllocHGlobal(bytes);
        try
        {
            if (!GetTokenInformation(token, 25, buffer, bytes, out _)) throw Error("TOKEN_LABEL_QUERY_FAILED");
            string sid = new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
            return int.Parse(sid[(sid.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static string Image(SafeProcessHandle process)
    {
        var text = new StringBuilder(32768); uint length = (uint)text.Capacity;
        if (!QueryFullProcessImageName(process, 0, text, ref length)) throw Error("PROCESS_IMAGE_QUERY_FAILED");
        return text.ToString();
    }
    internal static string DerivePackageSid(string nonce)
    {
        if (!Policy.Nonce(nonce)) throw new InvalidOperationException("NONCE_INVALID");
        int result = DeriveAppContainerSidFromAppContainerName(Policy.ProfilePrefix + nonce, out IntPtr sid);
        if (result < 0) throw new COMException("DERIVE_PACKAGE_SID_FAILED", result);
        if (sid == IntPtr.Zero) throw new InvalidOperationException("PACKAGE_SID_REQUIRED");
        try
        {
            string value = new SecurityIdentifier(sid).Value;
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, "\\AS-1-15-2(?:-[0-9]+){7}\\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
                throw new InvalidOperationException("DERIVED_PACKAGE_SID_SHAPE_INVALID");
            return value;
        }
        finally { FreeSid(sid); }
    }
    internal static string StationName(IntPtr station)
    {
        var text = new StringBuilder(256);
        if (!GetUserObjectInformation(station, 2, text, text.Capacity * 2, out _)) throw Error("STATION_NAME_QUERY_FAILED");
        return text.ToString();
    }
    internal static void RequireStation(IntPtr station, string nonce, string owner, string packageSid)
    {
        if (StationName(station) != Policy.StationPrefix + nonce || !GetUserObjectFlags(station, 1, out UserObjectFlags flags, Marshal.SizeOf<UserObjectFlags>(), out _) ||
            flags.Inherit || (flags.Flags & 1) != 0) throw new InvalidOperationException("NEW_NONINTERACTIVE_NONINHERITED_STATION_REQUIRED");
        uint result = GetSecurityInfo(station, 7 /* SE_WINDOW_OBJECT */, 0x17 /* OWNER | GROUP | DACL | LABEL */,
            out IntPtr actualOwner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        if (result != 0) throw new Win32Exception(checked((int)result), "STATION_SECURITY_READBACK_FAILED");
        try
        {
            if (new SecurityIdentifier(actualOwner).Value != owner || new SecurityIdentifier(group).Value != owner ||
                !GetSecurityDescriptorControl(descriptor, out ushort control, out _) || (control & 0x1000) == 0 ||
                dacl == IntPtr.Zero || Marshal.ReadInt16(dacl, 4) != 3 || sacl == IntPtr.Zero || Marshal.ReadInt16(sacl, 4) != 1)
                throw new InvalidOperationException("STATION_SECURITY_SHAPE_MISMATCH");
            var expected = new Dictionary<string, uint>(StringComparer.Ordinal) { [owner] = Policy.StationOwnerAccess,
                ["S-1-5-18"] = Policy.StationOwnerAccess, [packageSid] = Policy.StationPackageAccess };
            for (int i = 0; i < 3; i++)
            {
                if (!GetAce(dacl, i, out IntPtr ace)) throw Error("STATION_ACE_READBACK_FAILED");
                string sid = new SecurityIdentifier(IntPtr.Add(ace, 8)).Value;
                if (Marshal.ReadByte(ace) != 0 || Marshal.ReadByte(ace, 1) != 0 || !expected.Remove(sid, out uint mask) ||
                    unchecked((uint)Marshal.ReadInt32(ace, 4)) != mask) throw new InvalidOperationException("STATION_DACL_MISMATCH");
            }
            if (expected.Count != 0 || !GetAce(sacl, 0, out IntPtr label) || Marshal.ReadByte(label) != 0x11 || Marshal.ReadByte(label, 1) != 0 ||
                Marshal.ReadInt32(label, 4) != 1 || new SecurityIdentifier(IntPtr.Add(label, 8)).Value != "S-1-16-4096")
                throw new InvalidOperationException("STATION_LOW_NO_WRITE_UP_LABEL_REQUIRED");
        }
        finally { LocalFree(descriptor); }
    }
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct UserObjectFlags { [MarshalAs(UnmanagedType.Bool)] public bool Inherit; [MarshalAs(UnmanagedType.Bool)] public bool Reserved; public uint Flags; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct ShellExecuteInfo
    {
        public uint Size, Mask; public IntPtr Window;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Verb, File, Parameters, Directory;
        public int Show; public IntPtr Instance, IdList;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Class;
        public IntPtr ClassKey; public uint HotKey; public IntPtr Icon, Process;
    }
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ShellExecuteEx(ref ShellExecuteInfo info);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformationInt(SafeAccessTokenHandle token, int kind, out int data, int bytes, out int required);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr data, int bytes, out int required);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder image, ref uint length);
    [DllImport("userenv.dll", CharSet = CharSet.Unicode)] private static extern int DeriveAppContainerSidFromAppContainerName(string name, out IntPtr sid);
    [DllImport("advapi32.dll")] private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint bytes);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateWindowStation(string name, uint flags, uint access, ref SecurityAttributes attributes);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseWindowStation(IntPtr station);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(IntPtr station, int kind, StringBuilder data, int bytes, out int required);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectFlags(IntPtr station, int kind, out UserObjectFlags flags, int bytes, out int required);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(IntPtr handle, int objectType, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSecurityDescriptorControl(IntPtr descriptor, out ushort control, out uint revision);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetAce(IntPtr acl, int index, out IntPtr ace);
    [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
}
