using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace AutumnOS.OwnedBrowserParent;

internal sealed record SessionIdentity(string user_sid_sha256, uint session_id, int integrity_rid, bool elevated,
    bool appcontainer, string authentication_id, IReadOnlyList<string> enabled_privileges, IReadOnlyList<string> present_privileges)
{
    internal string UserSid { get; init; } = ""; // Not serialized: internal property.
}

internal static class SessionNative
{
    internal static Win32Exception Error(string stage) => new(Marshal.GetLastWin32Error(), stage);
    internal static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    internal static SafeProcessHandle Current() => new(GetCurrentProcess(), false);
    internal static SessionIdentity Identity(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 8, out SafeAccessTokenHandle token)) throw Error("TOKEN_OPEN");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            string sid = identity.User?.Value ?? throw new InvalidOperationException("TOKEN_USER");
            int Integer(int kind)
            {
                if (!GetTokenInt(token, kind, out int value, 4, out int required) || required != 4) throw Error("TOKEN_INTEGER");
                return value;
            }
            T Buffer<T>(int kind, Func<IntPtr, int, T> read)
            {
                bool first = GetTokenInformation(token, kind, IntPtr.Zero, 0, out int length);
                if (first || Marshal.GetLastWin32Error() != 122 || length is < 4 or > 65536) throw new InvalidOperationException("TOKEN_SIZE");
                IntPtr memory = Marshal.AllocHGlobal(length);
                try
                {
                    if (!GetTokenInformation(token, kind, memory, length, out int used) || used > length) throw Error("TOKEN_INFORMATION");
                    return read(memory, used);
                }
                finally { Marshal.FreeHGlobal(memory); }
            }
            int integrity = Buffer(25, (p, _) =>
            {
                string value = new SecurityIdentifier(Marshal.ReadIntPtr(p)).Value;
                return int.Parse(value[(value.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);
            });
            string authentication = Buffer(10, (p, n) =>
            {
                if (n < 56) throw new InvalidOperationException("TOKEN_STATISTICS_SIZE");
                return unchecked((uint)Marshal.ReadInt32(p, 12)).ToString("x8") + ":" + unchecked((uint)Marshal.ReadInt32(p, 8)).ToString("x8");
            });
            (string[] enabled, string[] present) privileges = Buffer(3, (p, n) =>
            {
                int count = Marshal.ReadInt32(p);
                if (count < 0 || count > 1024 || n < 4 + count * 12) throw new InvalidOperationException("TOKEN_PRIVILEGES_SIZE");
                var enabled = new List<string>(); var present = new List<string>();
                for (int i = 0; i < count; i++)
                {
                    int offset = 4 + i * 12;
                    string luid = unchecked((uint)Marshal.ReadInt32(p, offset + 4)).ToString("x8") + ":" + unchecked((uint)Marshal.ReadInt32(p, offset)).ToString("x8");
                    present.Add(luid);
                    if ((Marshal.ReadInt32(p, offset + 8) & 2) != 0) enabled.Add(luid);
                }
                return (enabled.Order(StringComparer.Ordinal).ToArray(), present.Order(StringComparer.Ordinal).ToArray());
            });
            return new(Hash(sid), unchecked((uint)Integer(12)), integrity, Integer(20) != 0, Integer(29) != 0, authentication, privileges.enabled, privileges.present) { UserSid = sid };
        }
    }
    internal static bool TokenAdmission(SessionIdentity parent, SessionIdentity child) =>
        child.user_sid_sha256 == parent.user_sid_sha256 && child.session_id == parent.session_id &&
        child.integrity_rid == parent.integrity_rid && child.elevated == parent.elevated && child.appcontainer == parent.appcontainer &&
        child.authentication_id != parent.authentication_id && child.enabled_privileges.All(parent.enabled_privileges.Contains) &&
        child.present_privileges.All(parent.present_privileges.Contains);
    internal static bool Exited(SafeProcessHandle process)
    {
        uint value = WaitForSingleObject(process, 0);
        if (value is not (0 or 258)) throw Error("ORIGINAL_PROCESS_WAIT");
        return value == 0;
    }
    internal static uint ExitCode(SafeProcessHandle process)
    {
        if (!Exited(process) || !GetExitCodeProcess(process, out uint code)) throw Error("ORIGINAL_PROCESS_EXIT_CODE");
        return code;
    }
    internal static string Image(SafeProcessHandle process)
    {
        var text = new StringBuilder(32768); uint length = (uint)text.Capacity;
        if (!QueryFullProcessImageNameW(process, 0, text, ref length)) throw Error("ORIGINAL_PROCESS_IMAGE");
        return text.ToString();
    }
    internal static string StationName(IntPtr station)
    {
        var text = new StringBuilder(257);
        if (!GetUserObjectInformationW(station, 2, text, text.Capacity * 2, out int used) || used < 2 || used > text.Capacity * 2 || text.Length is < 1 or > 256)
            throw Error("STATION_NAME");
        return text.ToString();
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] internal struct StartupInfo
    {
        internal int cb; internal string? reserved, desktop, title;
        internal uint x, y, xSize, ySize, xChars, yChars, fill, flags;
        internal ushort show, reservedSize; internal IntPtr reservedData, input, output, error;
    }
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { internal IntPtr process, thread; internal uint processId, threadId; }
    [StructLayout(LayoutKind.Sequential)] internal struct SecurityAttributes { internal int length; internal IntPtr descriptor; [MarshalAs(UnmanagedType.Bool)] internal bool inherit; }
    [StructLayout(LayoutKind.Sequential)] private struct UserObjectFlags { [MarshalAs(UnmanagedType.Bool)] internal bool Inherit; [MarshalAs(UnmanagedType.Bool)] internal bool Reserved; internal uint Flags; }
    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CreateProcessWithLogonW(string username, string domain, IntPtr password, uint logonFlags, string application,
        StringBuilder command, uint creationFlags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("kernel32.dll", EntryPoint = "WaitForSingleObject", SetLastError = true)] private static extern uint WaitForSingleObjectThread(SafeWaitHandle thread, uint milliseconds);
    internal static uint WaitOriginalThread(SafeWaitHandle thread)
    {
        uint result = WaitForSingleObjectThread(thread, 0);
        if (result is not (0 or 258)) throw Error("ORIGINAL_THREAD_WAIT");
        return result;
    }
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder text, ref uint length);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInt(SafeAccessTokenHandle token, int kind, out int value, int length, out int required);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr data, int length, out int required);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertDescriptor(string sddl, uint revision, out IntPtr descriptor, out uint bytes);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr CreateWindowStationW(string? name, uint flags, uint access, ref SecurityAttributes attributes);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] internal static extern IntPtr OpenWindowStationW(string name, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)] internal static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetProcessWindowStation(IntPtr station);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseWindowStation(IntPtr station);
    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformationW(IntPtr station, int kind, StringBuilder text, int bytes, out int required);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", ExactSpelling = true, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectFlags(IntPtr station, int kind, out UserObjectFlags flags, int bytes, out int required);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(IntPtr handle, int kind, uint info, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetSecurityDescriptorControl(IntPtr descriptor, out ushort control, out uint revision);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetAce(IntPtr acl, int index, out IntPtr ace);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", EntryPoint = "SetLastError")] internal static extern void ClearLastError(uint error);
}
