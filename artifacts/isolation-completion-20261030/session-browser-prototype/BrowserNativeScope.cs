using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace AutumnOS.OwnedBrowserParent;

// The parent, holding the original full station/desktop handles, proves the
// owner/SYSTEM/package DACL and Low NW label. This restricted child is not
// given READ_CONTROL on desktop0x87 or permission to read its SACL.
internal sealed class BrowserNativeScope : IDisposable
{
    private IntPtr host;
    private readonly string stationName, desktopName;
    internal BrowserNativeScope(string expectedStation, string expectedDesktop, uint hostPid, ulong hostCreation, ulong waitHandle)
    {
        if (hostPid == 0 || hostCreation == 0 || waitHandle is 0 or ulong.MaxValue ||
            string.IsNullOrEmpty(expectedStation) || expectedStation.Length > 256 || expectedStation.IndexOfAny(['\0', '\\', '/']) >= 0 ||
            expectedStation.Any(char.IsControl) || expectedStation.Equals("WinSta0", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(expectedDesktop, "\\AAutumnOS_SessionDesktop_[0-9a-f]{32}\\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("EXACT_AUTOMATIC_PRIVATE_CONTEXT_REQUIRED");
        stationName = expectedStation; desktopName = expectedDesktop;
        host = new IntPtr(unchecked((long)waitHandle));
        if (!SetHandleInformation(host, 1, 0)) throw new Win32Exception(Marshal.GetLastWin32Error(), "CLEAR_SYNC_HOST_INHERIT");
        // PID and creation are parent-bound metadata. The inherited original
        // object has SYNCHRONIZE only: never query it or reopen by numeric PID.
        RequireBinding();
    }
    internal void RequireBinding()
    {
        uint state = host == IntPtr.Zero ? uint.MaxValue : WaitForSingleObject(host, 0);
        if (state != 258) throw new InvalidDataException("ORIGINAL_STATION_HOST_NOT_LIVE");
        IntPtr station = GetProcessWindowStation(), desktop = GetThreadDesktop(GetCurrentThreadId());
        if (Name(station) != stationName || Name(desktop) != desktopName || stationName.Equals("WinSta0", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("EXACT_PRIVATE_BROWSER_CONTEXT_REQUIRED");
        IntPtr flags = Marshal.AllocHGlobal(12);
        try
        {
            if (!GetUserObjectInformationW(station, 1, flags, 12, out uint required) || required != 12 || (Marshal.ReadInt32(flags, 8) & 1) != 0)
                throw new InvalidDataException("NONINTERACTIVE_STATION_REQUIRED");
        }
        finally { Marshal.FreeHGlobal(flags); }
    }
    private static string Name(IntPtr handle)
    {
        if (handle == IntPtr.Zero) throw new InvalidDataException("CURRENT_USER_OBJECT_REQUIRED");
        IntPtr data = Marshal.AllocHGlobal(1024);
        try
        {
            if (!GetUserObjectInformationW(handle, 2, data, 1024, out uint needed) || needed is < 2 or > 1024)
                throw new InvalidDataException("CURRENT_USER_OBJECT_NAME_REQUIRED");
            return Marshal.PtrToStringUni(data) ?? throw new InvalidDataException("CURRENT_USER_OBJECT_NAME_NULL");
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    public void Dispose()
    { if (host != IntPtr.Zero) { _ = CloseHandle(host); host = IntPtr.Zero; } }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, IntPtr buffer, uint bytes, out uint needed);
}
