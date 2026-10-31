using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace AutumnOS.OwnedBrowserParent;

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
            if (failure is not null) { fact.worker_exception = Program.FailureCode(failure); throw failure; }
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

    internal static int RunNativeSelfTest(string reportPath)
    {
        if (!Path.IsPathFullyQualified(reportPath)) return 64;
        // Acquire only this fresh report before doing any metadata operation.
        using FileStream report = new(reportPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        string target = "AutumnOS_DesktopGuard_中文_" + Guid.NewGuid().ToString("N");
        List<DesktopProbeFact> probes = []; List<object> checks = [];
        var creation = new DesktopCreationFact { target = target };
        string status = "running", stage = "ordinary_identity", failure = "";
        IntPtr original = IntPtr.Zero; bool originalClosed = false, existingRejected = false, ownerOnlyVerified = false;
        uint originalCloseError = 0; int originalCloseCalls = 0; string? stationBefore = null, stationAfter = null; IntPtr threadBefore = IntPtr.Zero, threadAfter = IntPtr.Zero;
        void Save()
        {
            report.Position = 0; report.SetLength(0);
            JsonSerializer.Serialize(report, new { schema_version = 1, producer = "派蒙", kind = "ordinary_native_desktop_guard_metadata_self_test",
                status, stage, failure, target, checks, probes, creation, original_created_HDESK_closed = originalClosed,
                original_close_native_error = originalCloseError, existing_target_rejected_by_real_guard = existingRejected,
                new_desktop_owner_only_descriptor_verified = ownerOnlyVerified, worker_joined = creation.worker_joined,
                controller_station_before = stationBefore, controller_station_after = stationAfter,
                main_thread_desktop_unchanged = threadBefore != IntPtr.Zero && threadBefore == threadAfter,
                OpenDesktopW_calls = probes.Count, CreateDesktopW_calls = creation.create_call_attempted ? 1 : 0,
                CloseDesktop_probe_calls = probes.Count(p => p.returned_handle), CloseDesktop_original_calls = originalCloseCalls,
                SetLastError_explicit_clear_calls = probes.Count + (creation.worker_started ? 1 : 0),
                native_calls_scope = "actual desktop metadata calls plus ordinary identity checks; not a no-native protocol test",
                current_input_desktop_switch_calls = 0, gui_windows_created_by_probe = 0, COM_initializations_by_probe = 0,
                clipboard_operations = "not_run", profile_operations = "not_run", station_creation_calls = 0, uac_requests = 0,
                existing_object_ACL_changes = "none", private_station_and_AppContainer_browser_test = "not_run", mechanism_acceptance = "not_claimed" },
                new JsonSerializerOptions { WriteIndented = true }); report.Flush(true);
        }
        Save();
        try
        {
            Native.Check(Native.OpenProcessToken(Native.GetCurrentProcess(), Native.TokenQuery, out IntPtr token), "desktop_guard_ordinary_identity");
            try { if (Native.TokenInt(token, 20) != 0 || Native.TokenInt(token, 29) != 0) throw new OwnedAssertionException("ORDINARY_UNELEVATED_METADATA_TEST_REQUIRED"); }
            finally { Native.CloseHandle(token); }
            stationBefore = UserObjectName(GetProcessWindowStation()); threadBefore = GetThreadDesktop(GetCurrentThreadId());
            stage = "real_before_create_absence_guard"; RequireFresh(target, stage, probes); checks.Add(new { stage, passed = true }); Save();
            string owner = WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("OWNER_SID_REQUIRED");
            RawSecurityDescriptor sd = new("O:" + owner + "G:" + owner + "D:P(A;;0x000F01FF;;;" + owner + ")");
            byte[] descriptor = new byte[sd.BinaryLength]; sd.GetBinaryForm(descriptor, 0);
            stage = "create_only_owned_desktop_metadata_worker";
            original = CreateOnJoinedWorker(target, descriptor, 0x00020083, creation);
            if (UserObjectName(original) != target) throw new OwnedAssertionException("EXACT_UNICODE_CREATED_DESKTOP_NAME");
            ownerOnlyVerified = VerifyOwnerOnly(original, owner); if (!ownerOnlyVerified) throw new OwnedAssertionException("NEW_OBJECT_OWNER_ONLY_DACL");
            checks.Add(new { stage, passed = true }); Save();
            stage = "real_existing_target_must_be_rejected";
            try { RequireFresh(target, stage, probes); }
            catch (OwnedAssertionException ex) when (ex.Code == "DESKTOP_TARGET_ALREADY_EXISTS_NO_CREATE") { existingRejected = true; }
            if (!existingRejected || probes[^1].probe_handle_closed != true) throw new OwnedAssertionException("EXISTING_GUARD_NEGATIVE_CONTROL");
            checks.Add(new { stage, passed = true }); Save();
            stage = "close_original_after_creator_thread_real_exit";
            originalCloseCalls++; bool closed = CloseDesktop(original); int closeError = closed ? 0 : Marshal.GetLastWin32Error(); originalCloseError = unchecked((uint)closeError);
            if (!closed) throw new OwnedNativeException(closeError, stage);
            original = IntPtr.Zero; originalClosed = true; checks.Add(new { stage, passed = true }); Save();
            stage = "real_after_close_target_not_found"; RequireFresh(target, stage, probes); checks.Add(new { stage, passed = true });
            stationAfter = UserObjectName(GetProcessWindowStation()); threadAfter = GetThreadDesktop(GetCurrentThreadId());
            if (stationAfter != stationBefore || threadAfter != threadBefore) throw new OwnedAssertionException("CALLING_STATION_OR_MAIN_THREAD_DESKTOP_CHANGED");
            status = "passed"; stage = "complete";
        }
        catch (Exception ex) { status = "failed"; failure = Program.FailureCode(ex); }
        finally
        {
            if (original != IntPtr.Zero)
            {
                originalCloseCalls++; bool closed = CloseDesktop(original); int closeError = closed ? 0 : Marshal.GetLastWin32Error(); originalCloseError = unchecked((uint)closeError);
                originalClosed = closed; if (!closed) { status = "failed_cleanup"; failure = "CloseDesktop_original_failed:win32=" + closeError; }
            }
            Save();
        }
        Console.WriteLine(status + "; desktop_guard_metadata_report=" + reportPath); return status == "passed" ? 0 : 1;
    }
    private static bool VerifyOwnerOnly(IntPtr handle, string owner)
    {
        uint error = GetSecurityInfo(handle, 7, 5, out _, out _, out _, out _, out IntPtr descriptor);
        if (error != 0) throw new OwnedNativeException((int)error, "new_test_desktop_owner_DACL_readback");
        try
        {
            uint length = GetSecurityDescriptorLength(descriptor); if (length is < 20 or > 8192) throw new InvalidDataException("DESKTOP_DESCRIPTOR_SIZE");
            byte[] data = new byte[length]; Marshal.Copy(descriptor, data, 0, data.Length); RawSecurityDescriptor sd = new(data, 0);
            return sd.Owner?.Value == owner && (sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0 && sd.DiscretionaryAcl is { Count: 1 } acl &&
                acl[0] is CommonAce ace && ace.AceQualifier == AceQualifier.AccessAllowed && ace.AceFlags == AceFlags.None && ace.SecurityIdentifier.Value == owner && ace.AccessMask == 0x000F01FF;
        }
        finally { Native.LocalFree(descriptor); }
    }
    private static string UserObjectName(IntPtr handle)
    { var text = new StringBuilder(512); Native.Check(GetUserObjectInformationW(handle, 2, text, text.Capacity * 2, out _), "desktop_guard_user_object_name"); return text.ToString(); }
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
