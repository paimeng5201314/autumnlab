using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AutumnOS.ClipboardAppContainerProbe;

internal static class ProbeNative
{
    private static IntPtr holder;
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct ObjectFlags { internal int Inherit, Reserved; internal uint Flags; }
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetThreadDesktop(uint id);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformationW(IntPtr handle, int index, IntPtr data, uint bytes, out uint needed);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool EmptyClipboard();
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetClipboardData(uint format, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll")] internal static extern uint GetClipboardSequenceNumber();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalLock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GlobalUnlock(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr GlobalFree(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nuint GlobalSize(IntPtr memory);
    private delegate bool WindowCallback(IntPtr window, IntPtr state);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDesktopWindows(IntPtr desktop, WindowCallback callback, IntPtr state);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);

    private static void Check(bool succeeded, string step)
    { if (!succeeded) throw new Win32Exception(Marshal.GetLastWin32Error(), step); }
    internal static void AdoptWaitOnlyHolder(IntPtr handle)
    {
        if (holder != IntPtr.Zero || handle == IntPtr.Zero || handle == new IntPtr(-1)) throw new InvalidDataException("ORIGINAL_SYNC_ONLY_HOLDER_REQUIRED");
        holder = handle;
        Check(SetHandleInformation(holder, 1, 0), "CLEAR_HOLDER_INHERIT_BEFORE_BROWSER_CREATION");
        RequireHolderLive();
        Program.Emit("holder_wait_handle_bound", new { granted_access = "SYNCHRONIZE_only_parent_duplicate",
            inherit_cleared_before_browser = true,
            holder_identity_query = "not_available_sync_only_parent_original_binding_required", original_object_live = true });
    }
    internal static void ReleaseHolder()
    { if (holder != IntPtr.Zero) { _ = CloseHandle(holder); holder = IntPtr.Zero; } }
    private static void RequireHolderLive()
    {
        if (holder == IntPtr.Zero) throw new InvalidDataException("ORIGINAL_WAIT_HOLDER_REQUIRED");
        uint result = WaitForSingleObject(holder, 0);
        if (result == 258) return;
        if (result == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "WAIT_ORIGINAL_HOLDER");
        throw new InvalidDataException("ORIGINAL_HOLDER_NO_LONGER_LIVE");
    }
    private static string Name(IntPtr handle)
    {
        if (handle == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "CURRENT_GUI_OBJECT_REQUIRED");
        IntPtr data = Marshal.AllocHGlobal(1024);
        try
        {
            Check(GetUserObjectInformationW(handle, 2, data, 1024, out uint needed), "GUI_OBJECT_NAME");
            if (needed < 2 || needed > 1024) throw new InvalidDataException("GUI_OBJECT_NAME_SIZE");
            return Marshal.PtrToStringUni(data) ?? throw new InvalidDataException("GUI_OBJECT_NAME_NULL");
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    internal static void RequirePrivateBinding(string expectedStation, string expectedDesktop)
    {
        RequireHolderLive();
        IntPtr station = GetProcessWindowStation(), desktop = GetThreadDesktop(GetCurrentThreadId());
        if (!string.Equals(Name(station), expectedStation, StringComparison.Ordinal) ||
            !string.Equals(Name(desktop), expectedDesktop, StringComparison.Ordinal) ||
            expectedStation.Equals("WinSta0", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("EXACT_PRIVATE_GUI_BINDING_REQUIRED");
        IntPtr data = Marshal.AllocHGlobal(Marshal.SizeOf<ObjectFlags>());
        try
        {
            Check(GetUserObjectInformationW(station, 1, data, (uint)Marshal.SizeOf<ObjectFlags>(), out _), "STATION_VISIBILITY");
            if ((Marshal.PtrToStructure<ObjectFlags>(data).Flags & 1) != 0) throw new InvalidDataException("NONINTERACTIVE_STATION_REQUIRED");
        }
        finally { Marshal.FreeHGlobal(data); }
    }
    internal static uint[] DesktopWindowPids()
    {
        HashSet<uint> pids = []; bool overflow = false;
        WindowCallback callback = (window, state) =>
        {
            _ = GetWindowThreadProcessId(window, out uint pid);
            if (pid != 0) pids.Add(pid);
            if (pids.Count > 128) { overflow = true; return false; }
            return true;
        };
        bool succeeded = EnumDesktopWindows(GetThreadDesktop(GetCurrentThreadId()), callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        if (overflow) throw new InvalidDataException("PRIVATE_DESKTOP_WINDOW_COUNT_LIMIT");
        Check(succeeded, "ENUM_PRIVATE_DESKTOP_WINDOWS");
        return pids.Order().ToArray();
    }
    private static void Open(IntPtr owner)
    {
        for (int attempt = 0; attempt < 10; attempt++)
        { if (OpenClipboard(owner)) return; if (attempt != 9) Thread.Sleep(30); }
        throw new Win32Exception(Marshal.GetLastWin32Error(), "OPEN_SYNTHETIC_CLIPBOARD");
    }
    // These methods are invoked only by ClipboardCollector AFTER its exact
    // newly leased noninteractive station/desktop has been rechecked.
    internal static void Seed(IntPtr owner, string text)
    {
        if (text.Length > 256) throw new InvalidDataException("SEED_LIMIT");
        Open(owner); IntPtr memory = IntPtr.Zero;
        try
        {
            Check(EmptyClipboard(), "EMPTY_NEW_PRIVATE_CLIPBOARD_FOR_SYNTHETIC_SEED");
            byte[] bytes = Encoding.Unicode.GetBytes(text + "\0");
            memory = GlobalAlloc(0x42, (nuint)bytes.Length);
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "GLOBAL_ALLOC_SEED");
            IntPtr pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "GLOBAL_LOCK_SEED");
            try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
            finally { _ = GlobalUnlock(memory); }
            if (SetClipboardData(13, memory) == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "SET_SYNTHETIC_UNICODE_TEXT");
            memory = IntPtr.Zero; // USER owns it after a successful transfer.
        }
        finally { if (memory != IntPtr.Zero) _ = GlobalFree(memory); Check(CloseClipboard(), "CLOSE_SEED_CLIPBOARD"); }
    }
    internal static string? Read(IntPtr owner)
    {
        Open(owner);
        try
        {
            if (!IsClipboardFormatAvailable(13)) return null;
            IntPtr memory = GetClipboardData(13);
            if (memory == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "GET_SYNTHETIC_UNICODE_TEXT");
            nuint size = GlobalSize(memory);
            if (size is < 2 or > 8192 || size % 2 != 0) throw new InvalidDataException("CLIPBOARD_TEXT_SIZE_LIMIT");
            IntPtr pointer = GlobalLock(memory);
            if (pointer == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "GLOBAL_LOCK_READ");
            try
            {
                int chars = checked((int)(size / 2)), length = 0;
                while (length < chars && Marshal.ReadInt16(pointer, length * 2) != 0) length++;
                if (length == chars) throw new InvalidDataException("CLIPBOARD_TEXT_TERMINATOR_REQUIRED");
                return Marshal.PtrToStringUni(pointer, length) ?? "";
            }
            finally { _ = GlobalUnlock(memory); }
        }
        finally { Check(CloseClipboard(), "CLOSE_READ_CLIPBOARD"); }
    }
}
