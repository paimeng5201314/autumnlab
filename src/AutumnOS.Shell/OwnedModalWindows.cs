using System.Runtime.InteropServices;
using System.Text;

namespace AutumnOS.Shell;

/// <summary>Read-only, bounded observation of this exact process/window's common dialogs.</summary>
internal static class OwnedModalWindows
{
    internal readonly record struct Snapshot(bool Complete, int Count);

    internal static Snapshot Observe(IntPtr owner)
    {
        uint pid = (uint)Environment.ProcessId;
        if (owner == IntPtr.Zero || !IsWindow(owner) || GetWindowThreadProcessId(owner, out uint ownerPid) == 0 || ownerPid != pid)
            return new(false, 0);
        int examined = 0, count = 0;
        bool failed = false;
        bool enumerated = EnumWindows((window, _) =>
        {
            if (++examined > 8192) { failed = true; return false; }
            if (window == owner || GetWindowThreadProcessId(window, out uint candidatePid) == 0 || candidatePid != pid) return true;
            var name = new StringBuilder(64);
            if (GetClassName(window, name, name.Capacity) == 0) { if (IsWindow(window)) failed = true; return true; }
            if (name.ToString() != "#32770") return true;
            var seen = new HashSet<IntPtr>();
            IntPtr current = window;
            for (int depth = 0; depth < 16; depth++)
            {
                if (!seen.Add(current)) { failed = true; return true; }
                current = GetWindow(current, 4); // GW_OWNER, never parent or foreground guesses.
                if (current == IntPtr.Zero) return true;
                if (current == owner) { count++; return true; }
                if (GetWindowThreadProcessId(current, out uint ancestorPid) == 0 || ancestorPid != pid) return true;
            }
            failed = true;
            return true;
        }, IntPtr.Zero);
        // Hidden dialogs count too. No WM_CLOSE, input, text, file list or foreign
        // window mutation is performed by this observer.
        return new(enumerated && !failed, count);
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int length);
}
