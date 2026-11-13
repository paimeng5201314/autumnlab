using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AutumnOS.NativeHost.Windows;

// Opt-in owned-kernel regression. No LSA, USER, browser, clipboard, package,
// arbitrary executable/PID, application data or credential operations exist.
if (args.Length is 2 or 3 && args[0] == "--owned-child")
{
    if (args[1] == "normal") return 0;
    if (args[1] == "failed") return 23;
    if (args[1] == "hung" && args.Length == 3 && long.TryParse(args[2], out long ready) && ready > 0)
    { OwnedKernelProbe.SignalReady(new IntPtr(ready)); Thread.Sleep(Timeout.Infinite); return 24; }
    return 64;
}
if (args.Length == 0) return 0;
if (args.Length != 2 || args[0] != "--report" || !Path.IsPathFullyQualified(args[1])) return 64;
if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) return 78;
if (File.Exists(args[1])) return 73;
var cases = new List<object>();
bool passed = true;
foreach (string mode in new[] { "normal", "failed", "hung", "suspended" })
{
    try { cases.Add(OwnedKernelProbe.Run(mode)); }
    catch (Exception error) { passed = false; cases.Add(new { mode, status = "failed", error = error.GetType().Name, hresult = error.HResult }); }
}
Directory.CreateDirectory(Path.GetDirectoryName(args[1])!);
var report = new { status = passed ? "passed" : "failed", kind = "actual_new_owned_Job_and_fixed_self_child_only", cases,
    executed_utc = DateTimeOffset.UtcNow, os = Environment.OSVersion.VersionString,
    probe_assembly_sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(typeof(OwnedKernelProbe).Assembly.Location))),
    isolation_acceptance = "not_run", shell_assigned = false, lsa = "not_run", browser = "not_run", private_user_data = "not_accessed" };
using (var output = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.Read))
    JsonSerializer.Serialize(output, report, new JsonSerializerOptions { WriteIndented = true });
Console.WriteLine(args[1]);
return passed ? 0 : 1;

internal static class OwnedKernelProbe
{
    internal static object Run(string mode)
    {
        string image = Path.Combine(AppContext.BaseDirectory, "AutumnOS.OwnedJob.Probe.exe");
        if (!File.Exists(image)) throw new InvalidDataException("FIXED_SELF_APPHOST_REQUIRED");
        using var pin = new FileStream(image, FileMode.Open, FileAccess.Read, FileShare.Read);
        IntPtr job = CreateJobObjectW(IntPtr.Zero, null), attributes = IntPtr.Zero, jobs = IntPtr.Zero;
        IntPtr readyEvent = IntPtr.Zero, childReady = IntPtr.Zero, inheritedHandles = IntPtr.Zero;
        ProcessInfo child = default; bool initialized = false, observed = false, terminated = false;
        int parentId = Environment.ProcessId;
        try
        {
            Check(job != IntPtr.Zero); OwnedJobPolicy.ApplyToFreshJob(job); OwnedJobPolicy.RequireKernelPolicy(job);
            using var parent = Process.GetCurrentProcess();
            Check(IsProcessInJob(parent.Handle, job, out bool parentInJob));
            if (parentInJob) throw new InvalidDataException("PROBE_PARENT_MUST_STAY_OUTSIDE_CHILD_JOB");
            readyEvent = CreateEventW(IntPtr.Zero, true, false, null); Check(readyEvent != IntPtr.Zero);
            Check(DuplicateHandle(parent.Handle, readyEvent, parent.Handle, out childReady, 2, true, 0));
            nuint bytes = 0;
            _ = InitializeProcThreadAttributeList(IntPtr.Zero, 2, 0, ref bytes);
            if (bytes is < 32 or > 65536) throw new InvalidDataException("PROBE_ATTRIBUTE_LIMIT");
            attributes = Marshal.AllocHGlobal(checked((int)bytes));
            Check(InitializeProcThreadAttributeList(attributes, 2, 0, ref bytes)); initialized = true;
            jobs = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobs, job);
            Check(UpdateProcThreadAttribute(attributes, 0, 0x0002000D, jobs, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
            inheritedHandles = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(inheritedHandles, childReady);
            Check(UpdateProcThreadAttribute(attributes, 0, 0x00020002, inheritedHandles, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
            var startup = new StartupEx { Startup = new Startup { Size = (uint)Marshal.SizeOf<StartupEx>() }, Attributes = attributes };
            string childMode = mode == "suspended" ? "hung" : mode;
            var command = new StringBuilder('"' + image + "\" --owned-child " + childMode + (childMode == "hung" ? " " + childReady.ToInt64() : ""));
            Check(CreateProcessW(image, command, IntPtr.Zero, IntPtr.Zero, true, 0x08080004, IntPtr.Zero, AppContext.BaseDirectory, ref startup, out child));
            Check(IsProcessInJob(child.Process, job, out bool belongs));
            if (!belongs || child.ProcessId == parentId) throw new InvalidDataException("PROBE_ORIGINAL_CHILD_JOB_BINDING_REQUIRED");
            if (mode != "suspended" && ResumeThread(child.Thread) != 1) throw new Win32Exception(Marshal.GetLastWin32Error());
            if (mode is "hung" or "suspended")
            {
                if (mode == "hung" && WaitForSingleObject(readyEvent, 10000) != 0) throw new InvalidDataException("PROBE_CHILD_DID_NOT_ENTER_OWNED_HOLD");
                if (WaitForSingleObject(child.Process, 0) != 258) throw new InvalidDataException("PROBE_HELD_CHILD_NOT_RUNNING");
                var recovery = new OwnedJobRecovery();
                terminated = recovery.TryRequest(OwnedJobPolicy.GracePeriod, true, false, () => OwnedJobPolicy.RequestTermination(job));
            }
            if (WaitForSingleObject(child.Process, 10000) != 0 || WaitForSingleObject(child.Thread, 10000) != 0)
                throw new InvalidDataException("PROBE_ORIGINAL_PROCESS_THREAD_EXIT_NOT_OBSERVED");
            Check(GetExitCodeProcess(child.Process, out uint exit));
            uint expected = mode is "hung" or "suspended" ? OwnedJobPolicy.RecoveryExitCode : mode == "normal" ? 0u : 23u;
            if (exit != expected) throw new InvalidDataException("PROBE_ORIGINAL_EXIT_CODE_MISMATCH");
            byte[] accounting = new byte[48], ids = new byte[8 + 32 * 8];
            Check(QueryInformationJobObject(job, 1, accounting, 48, out _));
            Check(QueryInformationJobObject(job, 3, ids, (uint)ids.Length, out _));
            if (BinaryPrimitives.ReadUInt32LittleEndian(accounting.AsSpan(40)) != 0 ||
                BinaryPrimitives.ReadUInt32LittleEndian(ids) != 0 || BinaryPrimitives.ReadUInt32LittleEndian(ids.AsSpan(4)) != 0)
                throw new InvalidDataException("PROBE_JOB_NOT_EMPTY");
            observed = true;
            return new { mode, status = "passed", quota_readback = true, actual_original_process_and_thread_signaled = true,
                actual_job_active_zero_and_pid_list_empty = true, recovery_requested = terminated, exit_code = exit, parent_not_assigned = true,
                child_hold_readiness_observed = mode == "hung", original_thread_intentionally_not_resumed = mode == "suspended" };
        }
        finally
        {
            // Any failed case remains failed. Cleanup requests only target this
            // newly created private Job, never a user PID or an existing Job.
            if (!observed && child.Process != IntPtr.Zero)
            {
                try { OwnedJobPolicy.RequestTermination(job); } catch { }
                _ = WaitForSingleObject(child.Process, 10000); _ = WaitForSingleObject(child.Thread, 10000);
            }
            if (child.Thread != IntPtr.Zero) _ = CloseHandle(child.Thread);
            if (child.Process != IntPtr.Zero) _ = CloseHandle(child.Process);
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (jobs != IntPtr.Zero) Marshal.FreeHGlobal(jobs);
            if (inheritedHandles != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandles);
            if (childReady != IntPtr.Zero) _ = CloseHandle(childReady);
            if (readyEvent != IntPtr.Zero) _ = CloseHandle(readyEvent);
            if (job != IntPtr.Zero) _ = CloseHandle(job);
        }
    }
    internal static void SignalReady(IntPtr readyEvent) { Check(SetEvent(readyEvent)); Check(CloseHandle(readyEvent)); }
    private static void Check(bool value) { if (!value) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private struct Startup
    {
        internal uint Size; internal IntPtr Reserved, Desktop, Title; internal uint X, Y, Width, Height, XChars, YChars, Fill, Flags;
        internal ushort Show, ReservedBytes; internal IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct StartupEx { internal Startup Startup; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(IntPtr job, int kind, [Out] byte[] data, uint bytes, out uint returned);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(IntPtr process, IntPtr job, [MarshalAs(UnmanagedType.Bool)] out bool belongs);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr attributes, int count, uint flags, ref nuint bytes);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr attributes, uint flags, nuint attribute, IntPtr value, nuint bytes, IntPtr previous, IntPtr returned);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string executable, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupEx startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateEventW(IntPtr attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetEvent(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}
