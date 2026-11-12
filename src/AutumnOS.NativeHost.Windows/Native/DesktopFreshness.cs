using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AutumnOS.NativeHost.Windows;

internal sealed record DesktopProbeFact(string stage, string target, bool returned_handle, int native_error,
    bool probe_handle_closed, bool fresh_not_found, uint requested_access, bool inherit, uint flags);
internal sealed class DesktopCreationFact
{
    public string target { get; init; } = "";
    public bool worker_started { get; internal set; }
    public bool worker_joined { get; internal set; }
    public bool create_call_attempted { get; internal set; }
    public bool worker_still_alive { get; internal set; }
    public bool returned_handle { get; internal set; }
    public int native_error { get; internal set; }
    public string? worker_exception { get; internal set; }
    public string scope => "new_object_only_metadata_worker_no_GUI_COM_clipboard_or_SwitchDesktop";
}

internal static class DesktopFreshness
{
    // Explicit last-error reset prevents a stale success/error from being used
    // as an absence proof. FALSE/0 is never equivalent to not found.
    internal static DesktopProbeFact RequireFresh(string target, string stage, List<DesktopProbeFact> ledger)
    {
        ValidateName(target); SetLastError(0);
        IntPtr found = OpenDesktopW(target, 0, false, 1); int error = Marshal.GetLastWin32Error();
        bool closed = false;
        if (found != IntPtr.Zero)
        {
            closed = CloseDesktop(found); int closeError = closed ? 0 : Marshal.GetLastWin32Error();
            ledger.Add(new(stage, target, true, error, closed, false, 1, false, 0));
            if (!closed) throw new OwnedNativeException(closeError, "close_existing_desktop_probe_handle");
            throw new OwnedAssertionException("DESKTOP_TARGET_ALREADY_EXISTS_NO_CREATE");
        }
        bool absent = error is 2 or 3;
        var fact = new DesktopProbeFact(stage, target, false, error, false, absent, 1, false, 0); ledger.Add(fact);
        if (!absent) throw new OwnedNativeException(error, "OpenDesktop_target_absence_not_proven");
        return fact;
    }
    internal static IntPtr CreateOnJoinedWorker(string target, byte[] descriptor, uint desiredAccess, DesktopCreationFact fact)
    {
        ValidateName(target);
        IntPtr memory = Marshal.AllocHGlobal(descriptor.Length), result = IntPtr.Zero;
        Exception? failure = null; int error = 0;
        Thread? worker = null;
        try
        {
            Marshal.Copy(descriptor, 0, memory, descriptor.Length);
            worker = new Thread(() =>
            {
                // No other USER/GUI/COM calls precede this CreateDesktop. A
                // new private station does not need a preexisting Default.
                try
                {
                    var attributes = new Native.SecurityAttributes { Length = (uint)Marshal.SizeOf<Native.SecurityAttributes>(), Descriptor = memory, Inherit = false };
                    SetLastError(0); fact.create_call_attempted = true; result = CreateDesktopW(target, IntPtr.Zero, IntPtr.Zero, 0, desiredAccess, ref attributes);
                    error = Marshal.GetLastWin32Error();
                }
                catch (Exception ex) { failure = ex; }
            });
            worker.Start(); fact.worker_started = true;
            worker.Join(); fact.worker_joined = true; fact.worker_still_alive = worker.IsAlive;
            fact.returned_handle = result != IntPtr.Zero; fact.native_error = error;
            if (failure is not null) { fact.worker_exception = NativeFailure.Describe(failure); throw failure; }
            if (!fact.worker_joined || fact.worker_still_alive) throw new OwnedAssertionException("METADATA_CREATOR_THREAD_EXIT_NOT_PROVEN");
            if (result == IntPtr.Zero) throw new OwnedNativeException(error, "CreateDesktop_on_joined_metadata_worker");
            return result;
        }
        finally
        {
            // Never free the descriptor while the native worker might still
            // use it. Do not terminate or abandon the thread on an error.
            if (worker is not null && fact.worker_started && worker.IsAlive) { worker.Join(); fact.worker_joined = true; fact.worker_still_alive = worker.IsAlive; }
            Marshal.FreeHGlobal(memory);
        }
    }
    private static void ValidateName(string target)
    { if (string.IsNullOrWhiteSpace(target) || target.Length > 180 || target.IndexOfAny(['\\', '/', ':', '\0']) >= 0) throw new InvalidDataException("EXACT_PLAIN_DESKTOP_NAME_REQUIRED"); }

    [DllImport("kernel32.dll", EntryPoint = "SetLastError", ExactSpelling = true)] private static extern void SetLastError(uint error);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll", EntryPoint = "OpenDesktopW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenDesktopW(string target, uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint desiredAccess);
    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateDesktopW(string target, IntPtr device, IntPtr mode, uint flags, uint desiredAccess, ref Native.SecurityAttributes attributes);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr handle);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformationW(IntPtr handle, int index, StringBuilder text, int bytes, out uint required);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(IntPtr handle, int type, uint flags, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
}
