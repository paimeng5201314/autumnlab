// REVIEW DRAFT ONLY. Not included in any production project. No AssignProcess call has been run.
// Affects only the native browser handle supplied by WebAppHost after EnsureCoreWebView2Async.
// Apply BEFORE the host challenge and BEFORE any application resource/navigation or SDK message.
// User authorization for the stronger process restriction is required before trial/integration.
//
// Required validation before shipping:
// 1. Real fixed WebView2 browser has no pre-existing job; UI limits do NOT support nested jobs.
// 2. Chromium renderer/GPU/network process creation and sandbox remain functional. This draft
//    never permits breakaway or disables the Chromium sandbox to work around incompatibility.
// 3. Prove that every legacy clipboard request is brokered through this protected browser process.
//    Children already created before assignment are NOT retroactively included. Inspect actual
//    WebView2 process topology and test A/B in a disposable Windows user/session with synthetic
//    clipboard contents. Do not run read/write clipboard probes against the user's clipboard.
// 4. On browser restart/replacement: dispose the controller, revoke the instance, and require a
//    fresh binding before application execution; a remembered PID or old job is not proof.
// 5. Failure means refusing app execution and normally closing this owned controller. No fallback.
//
// Microsoft references:
// https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_basic_ui_restrictions
// https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject
// https://learn.microsoft.com/en-us/windows/win32/procthread/nested-jobs
// https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.Shell;

internal sealed class BrowserClipboardBoundary : IDisposable
{
    private const uint ReadClipboard = 0x2, WriteClipboard = 0x4;
    private const int BasicUiRestrictionsClass = 4;
    // AssignProcessToJobObject requires SET_QUOTA and TERMINATE rights even though this draft
    // NEVER invokes TerminateProcess/TerminateJobObject and never sets KILL_ON_JOB_CLOSE.
    private const uint RequiredProcessAccess = 0x0100 | 0x0001 | 0x1000;
    private readonly SafeJobHandle job;
    private readonly SafeProcessHandle browser;
    private bool disposed;

    private BrowserClipboardBoundary(SafeJobHandle job, SafeProcessHandle browser, uint processId)
    { this.job = job; this.browser = browser; ProcessId = processId; }

    public uint ProcessId { get; }

    // Pass the Process captured from the actual current controller, not a name search or arbitrary PID.
    // Its already-open SafeHandle pins the kernel process object against PID recycling.
    public static BrowserClipboardBoundary CreateForOwnedBrowser(Process capturedBrowser,
        uint nativeControllerBrowserProcessId, string fixedRuntimeDirectory)
    {
        ArgumentNullException.ThrowIfNull(capturedBrowser);
        if (!OperatingSystem.IsWindows()) throw Denied("RUNTIME_CLIPBOARD_PLATFORM_UNSUPPORTED");
        if (!Path.IsPathFullyQualified(fixedRuntimeDirectory)) throw Denied("RUNTIME_CLIPBOARD_ENGINE_PATH_INVALID");
        if (nativeControllerBrowserProcessId == 0 || nativeControllerBrowserProcessId == Environment.ProcessId
            || capturedBrowser.Id != nativeControllerBrowserProcessId || capturedBrowser.HasExited)
            throw Denied("RUNTIME_CLIPBOARD_BROWSER_BINDING_REJECTED");

        if (!DuplicateHandle(GetCurrentProcess(), capturedBrowser.SafeHandle, GetCurrentProcess(),
            out SafeProcessHandle browserHandle, RequiredProcessAccess, false, 0))
            throw NativeFailure("RUNTIME_CLIPBOARD_BROWSER_HANDLE_FAILED");

        SafeJobHandle? jobHandle = null;
        try
        {
            if (GetProcessId(browserHandle) != nativeControllerBrowserProcessId)
                throw Denied("RUNTIME_CLIPBOARD_BROWSER_BINDING_REJECTED");
            if (!ProcessIdToSessionId(nativeControllerBrowserProcessId, out uint browserSession)
                || !ProcessIdToSessionId((uint)Environment.ProcessId, out uint hostSession))
                throw NativeFailure("RUNTIME_CLIPBOARD_BROWSER_SESSION_QUERY_FAILED");
            if (browserSession != hostSession) throw Denied("RUNTIME_CLIPBOARD_BROWSER_BINDING_REJECTED");

            string expected = Path.GetFullPath(Path.Combine(fixedRuntimeDirectory, "msedgewebview2.exe"));
            var image = new StringBuilder(32768); uint capacity = (uint)image.Capacity;
            if (!QueryFullProcessImageName(browserHandle, 0, image, ref capacity))
                throw NativeFailure("RUNTIME_CLIPBOARD_BROWSER_IMAGE_QUERY_FAILED");
            if (!string.Equals(Path.GetFullPath(image.ToString()), expected, StringComparison.OrdinalIgnoreCase))
                throw Denied("RUNTIME_CLIPBOARD_BROWSER_IMAGE_REJECTED");

            if (!IsProcessInJob(browserHandle, IntPtr.Zero, out bool alreadyAssigned))
                throw NativeFailure("RUNTIME_CLIPBOARD_JOB_QUERY_FAILED");
            if (alreadyAssigned) throw Denied("RUNTIME_CLIPBOARD_NESTED_JOB_UNSUPPORTED");

            // Anonymous, non-inheritable handle. Never open or alter a pre-existing job object.
            jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (jobHandle.IsInvalid) throw NativeFailure("RUNTIME_CLIPBOARD_JOB_CREATE_FAILED");
            var limits = new BasicUiRestrictions { UiRestrictions = ReadClipboard | WriteClipboard };
            uint size = (uint)Marshal.SizeOf<BasicUiRestrictions>();
            if (!SetInformationJobObject(jobHandle, BasicUiRestrictionsClass, ref limits, size))
                throw NativeFailure("RUNTIME_CLIPBOARD_JOB_POLICY_FAILED");
            RequirePolicy(jobHandle);

            // This is the only process mutation in the draft. Do not call until authorized.
            if (!AssignProcessToJobObject(jobHandle, browserHandle))
                throw NativeFailure("RUNTIME_CLIPBOARD_JOB_ASSIGN_FAILED");
            if (!IsProcessInSpecificJob(browserHandle, jobHandle, out bool assigned) || !assigned)
                throw Denied("RUNTIME_CLIPBOARD_JOB_ASSIGN_NOT_CONFIRMED");
            RequirePolicy(jobHandle);

            return new BrowserClipboardBoundary(jobHandle, browserHandle, nativeControllerBrowserProcessId);
        }
        catch
        {
            // Association cannot be undone. If assignment succeeded before failure, the caller must
            // normally dispose its controller. Closing this handle does not kill any process and the
            // OS keeps restrictions until associated processes exit. Never try to reset limits here.
            jobHandle?.Dispose(); browserHandle.Dispose(); throw;
        }
    }

    public void VerifyCurrentBrowser(uint nativeControllerBrowserProcessId)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (nativeControllerBrowserProcessId != ProcessId || GetProcessId(browser) != ProcessId
            || !IsProcessInSpecificJob(browser, job, out bool assigned) || !assigned)
            throw Denied("RUNTIME_CLIPBOARD_BROWSER_BINDING_LOST");
        RequirePolicy(job);
    }

    private static void RequirePolicy(SafeJobHandle jobHandle)
    {
        uint size = (uint)Marshal.SizeOf<BasicUiRestrictions>();
        if (!QueryInformationJobObject(jobHandle, BasicUiRestrictionsClass, out BasicUiRestrictions actual, size, out uint returned)
            || returned != size || actual.UiRestrictions != (ReadClipboard | WriteClipboard))
            throw Denied("RUNTIME_CLIPBOARD_JOB_POLICY_NOT_CONFIRMED");
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        // No kill-on-close: hold for controller lifetime, then close our handles only.
        job.Dispose(); browser.Dispose();
    }

    private static InvalidOperationException Denied(string code) => new(code);
    private static Win32Exception NativeFailure(string code) => new(Marshal.GetLastWin32Error(), code);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicUiRestrictions { public uint UiRestrictions; }

    private sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public SafeJobHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseHandle(handle);
    }

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(IntPtr sourceProcess, SafeProcessHandle source,
        IntPtr targetProcess, out SafeProcessHandle target, uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint options);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeJobHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeJobHandle job, int informationClass,
        ref BasicUiRestrictions information, uint informationLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeJobHandle job, int informationClass,
        out BasicUiRestrictions information, uint informationLength, out uint returnedLength);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeJobHandle job, SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, IntPtr job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", EntryPoint = "IsProcessInJob", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInSpecificJob(SafeProcessHandle process, SafeJobHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ProcessIdToSessionId(uint processId, out uint sessionId);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags,
        StringBuilder imagePath, ref uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
