using AutumnOS.Launcher;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace AutumnOS.Shell;

internal static class LauncherWindowActivation
{
    internal static Task<RecallResult> RecallAsync(Window window, LauncherCoordinator owner, CancellationToken token)
    {
        var completion = new TaskCompletionSource<RecallResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!window.DispatcherQueue.TryEnqueue(() =>
        {
            try
            {
                if (token.IsCancellationRequested || owner.IsStopping) { completion.TrySetResult(new(RecallOutcome.Closing, 0)); return; }
                nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
                if (hwnd == 0 || !IsWindow(hwnd)) { completion.TrySetResult(new(RecallOutcome.Closing, 0)); return; }
                // Restore only a minimized window. Its WINDOWPLACEMENT remembers normal/maximized
                // state and position. No navigation, new MainWindow, WebView or game activation.
                if (IsIconic(hwnd)) ShowWindow(hwnd, 9 /* SW_RESTORE */);
                window.Activate();
                SetForegroundWindow(hwnd);
                bool foreground = GetForegroundWindow() == hwnd;
                if (!foreground)
                {
                    var flash = new FlashInfo { Size = (uint)Marshal.SizeOf<FlashInfo>(), Window = hwnd, Flags = 2, Count = 3, Timeout = 0 };
                    FlashWindowEx(ref flash);
                }
                completion.TrySetResult(new(foreground ? RecallOutcome.Foreground : RecallOutcome.AttentionRequested, hwnd.ToInt64()));
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            { completion.TrySetResult(new(owner.IsStopping ? RecallOutcome.Closing : RecallOutcome.Rejected, 0)); }
        })) completion.TrySetResult(new(RecallOutcome.Closing, 0));
        return completion.Task;
    }
    [StructLayout(LayoutKind.Sequential)] private struct FlashInfo { public uint Size; public nint Window; public uint Flags, Count, Timeout; }
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool ShowWindow(nint window, int command);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetForegroundWindow(nint window);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FlashWindowEx(ref FlashInfo info);
}
