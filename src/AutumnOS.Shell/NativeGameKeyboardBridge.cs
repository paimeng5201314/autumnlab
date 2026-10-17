using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.System;

namespace AutumnOS.Shell;

/// <summary>
/// Receives native shortcuts while the out-of-process WebView owns keyboard focus.
/// Installed only while a game page is visible. The foreground ownership check runs
/// before reading a key; unrelated input is passed through without inspection.
/// </summary>
internal sealed class NativeGameKeyboardBridge : IDisposable
{
    private readonly Func<nint, bool> ownsForeground;
    private readonly Func<VirtualKey, VirtualKeyModifiers, bool> invoke;
    private readonly HookProc callback;
    private nint hook;
    private bool escapeDown;
    private bool qDown;

    internal NativeGameKeyboardBridge(Func<nint, bool> ownsForeground, Func<VirtualKey, VirtualKeyModifiers, bool> invoke)
    {
        this.ownsForeground = ownsForeground;
        this.invoke = invoke;
        callback = Handle;
        hook = SetWindowsHookEx(13 /* WH_KEYBOARD_LL; executes in this UI thread */, callback, GetModuleHandle(null), 0);
        if (hook == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    private nint Handle(int code, nint message, nint data)
    {
        if (code < 0) return CallNextHookEx(hook, code, message, data);
        try
        {
            if (!ownsForeground(GetForegroundWindow()))
            {
                escapeDown = qDown = false;
                return CallNextHookEx(hook, code, message, data);
            }
            // Only two virtual keys are inspected. No text, scan codes, timestamps,
            // extra-info, other application's input or keystroke history is retained.
            var key = (VirtualKey)Marshal.ReadInt32(data);
            if (key is not (VirtualKey.Escape or VirtualKey.Q)) return CallNextHookEx(hook, code, message, data);
            bool released = message is 0x101 or 0x105; // WM_KEYUP / WM_SYSKEYUP
            ref bool down = ref (key == VirtualKey.Escape ? ref escapeDown : ref qDown);
            if (released) { down = false; return CallNextHookEx(hook, code, message, data); }
            if (message is not (0x100 or 0x104)) return CallNextHookEx(hook, code, message, data);
            // Windows has not updated async state yet when this hook is called.
            // Its prior down bit also catches a held key when entering this window.
            bool repeated = down || IsDown(key);
            down = true;
            VirtualKeyModifiers modifiers = VirtualKeyModifiers.None;
            if (IsDown(VirtualKey.Control)) modifiers |= VirtualKeyModifiers.Control;
            if (IsDown(VirtualKey.Shift)) modifiers |= VirtualKeyModifiers.Shift;
            if (IsDown(VirtualKey.Menu)) modifiers |= VirtualKeyModifiers.Menu;
            if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) modifiers |= VirtualKeyModifiers.Windows;
            bool shortcut = (key == VirtualKey.Escape && modifiers == VirtualKeyModifiers.None) ||
                (key == VirtualKey.Q && modifiers == (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift));
            // The first Esc reaches an open native dialog; repeats cannot escape
            // the game immediately after that same held key dismissed the dialog.
            if (shortcut && (repeated || invoke(key, modifiers))) return 1;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // A stale window or closing native host must never prevent normal input.
        }
        return CallNextHookEx(hook, code, message, data);
    }

    private static bool IsDown(VirtualKey key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;
    internal static uint ProcessForWindow(nint window) { _ = GetWindowThreadProcessId(window, out uint processId); return processId; }
    public void Dispose()
    {
        nint old = Interlocked.Exchange(ref hook, 0);
        if (old != 0) _ = UnhookWindowsHookEx(old);
        GC.KeepAlive(callback);
    }

    private delegate nint HookProc(int code, nint message, nint data);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int kind, HookProc procedure, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandle(string? name);
}
