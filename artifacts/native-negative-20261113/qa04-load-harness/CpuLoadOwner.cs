using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

// Owns only one fresh helper and a fresh unnamed Job, never the product Job.
internal sealed class CpuLoadOwner
{
    private const string ManifestHash = "9a479aa1f84441ab81ba5898cd73f76b5d7589a3ede4c8c54f9949640d3ec852";
    private readonly string mode, id = Guid.NewGuid().ToString("N");
    private readonly List<FileStream> pins = [];
    private readonly Stopwatch clock = new();
    private IntPtr job;
    private ProcessInfo child;
    private bool createAttempted, createReturned, noChildProven, ready, quotaVerified, parentOutside, retired, recovered, pendingWritten;
    private TimeSpan readyAt;
    private int? exitCode;
    private DateTimeOffset? closeStarted, closeFinished;
    private TimeSpan? closeStartedAt, closeFinishedAt;
    private double? closeStartCpuSeconds, closeEndCpuSeconds;
    private JsonElement? telemetry;
    private string? failure;
    private string Folder => Path.Combine(AppContext.BaseDirectory, "cpu-worker-runs", id);
    internal CpuLoadOwner(string mode) => this.mode = mode == "cpu-control" ? "control" : mode == "cpu-load" ? "load" : throw new ArgumentException("FIXED_CPU_MODE_REQUIRED");
    internal bool Passed => ready && quotaVerified && parentOutside && retired && !recovered && exitCode == 0 && telemetry.HasValue && failure is null;
    internal object Report => new { mode, id, ready_observed = ready, quota_readback = quotaVerified, parent_outside_helper_job = parentOutside,
        original_process_thread_and_job0_retired = retired, recovery_requested = recovered, original_exit_code = exitCode, failure,
        close_started_utc = closeStarted, close_finished_utc = closeFinished, helper_cpu_at_close_start_seconds = closeStartCpuSeconds,
        close_elapsed_monotonic_seconds = (closeFinishedAt - closeStartedAt)?.TotalSeconds, helper_cpu_at_close_end_seconds = closeEndCpuSeconds,
        helper_cpu_during_close_seconds = closeEndCpuSeconds - closeStartCpuSeconds, worker = telemetry, passed = Passed };
    internal void MarkCloseStarted()
    {
        Require(child.Process != IntPtr.Zero && WaitForSingleObject(child.Process, 0) == 258, "CPU_HELPER_NOT_LIVE_AT_CLOSE");
        closeStarted = DateTimeOffset.UtcNow; closeStartedAt = clock.Elapsed; closeStartCpuSeconds = CpuSeconds();
    }
    internal void MarkCloseFinished() { closeFinished = DateTimeOffset.UtcNow; closeFinishedAt = clock.Elapsed; closeEndCpuSeconds = CpuSeconds(); }
    private double CpuSeconds() { Check(GetProcessTimes(child.Process, out _, out _, out long kernel, out long user)); return (kernel + user) / 10000000d; }
    internal async Task StartAsync()
    {
        string manifestPath = Path.Combine(AppContext.BaseDirectory, "cpu-helper-inputs.json");
        var manifestPin = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read); pins.Add(manifestPin);
        Require(manifestPin.Length is >= 1 and <= 16384, "CPU_HELPER_MANIFEST_BOUND");
        var manifestBytes = new byte[(int)manifestPin.Length]; manifestPin.ReadExactly(manifestBytes);
        Require(Convert.ToHexStringLower(SHA256.HashData(manifestBytes)) == ManifestHash, "CPU_HELPER_MANIFEST_CHANGED");
        using var manifest = JsonDocument.Parse(manifestBytes);
        string[] names = ["AutumnOS.CpuLoad.Helper.exe", "AutumnOS.CpuLoad.Helper.dll", "AutumnOS.CpuLoad.Helper.deps.json", "AutumnOS.CpuLoad.Helper.runtimeconfig.json"];
        Require(manifest.RootElement.GetArrayLength() == names.Length, "CPU_HELPER_INPUT_COUNT");
        Require(manifest.RootElement.EnumerateArray().Select(r => r.GetProperty("name").GetString()).Distinct(StringComparer.Ordinal).Count() == names.Length, "CPU_HELPER_INPUT_NAMES_MUST_BE_UNIQUE");
        foreach (var row in manifest.RootElement.EnumerateArray())
        {
            string name = row.GetProperty("name").GetString()!;
            Require(names.Contains(name, StringComparer.Ordinal), "CPU_HELPER_FIXED_INPUT_REQUIRED");
            string path = Path.Combine(AppContext.BaseDirectory, name);
            pins.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            Require(Hash(path) == row.GetProperty("sha256").GetString(), "CPU_HELPER_INPUT_HASH_CHANGED");
        }
        Directory.CreateDirectory(Folder);
        clock.Start();
        job = CreateJobObjectW(IntPtr.Zero, null); Check(job != IntPtr.Zero);
        byte[] ext = new byte[144], cpu = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(ext.AsSpan(16), 0x2308);
        BinaryPrimitives.WriteUInt32LittleEndian(ext.AsSpan(40), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(ext.AsSpan(112), 128UL * 1024 * 1024);
        BinaryPrimitives.WriteUInt64LittleEndian(ext.AsSpan(120), 128UL * 1024 * 1024);
        BinaryPrimitives.WriteUInt32LittleEndian(cpu, 5); BinaryPrimitives.WriteUInt32LittleEndian(cpu.AsSpan(4), 2500);
        Check(SetInformationJobObject(job, 9, ext, 144)); Check(SetInformationJobObject(job, 15, cpu, 8));
        byte[] actual = Query(9, 144), actualCpu = Query(15, 8);
        Require(actual.AsSpan(16, 4).SequenceEqual(ext.AsSpan(16, 4)) && actual.AsSpan(40, 4).SequenceEqual(ext.AsSpan(40, 4)) &&
            actual.AsSpan(112, 16).SequenceEqual(ext.AsSpan(112, 16)) && actualCpu.SequenceEqual(cpu), "CPU_HELPER_QUOTA_READBACK");
        quotaVerified = true;
        Check(IsProcessInJob(GetCurrentProcess(), job, out bool parentInJob)); Require(!parentInJob, "CPU_PARENT_IN_HELPER_JOB"); parentOutside = true;
        IntPtr attributes = IntPtr.Zero, jobs = IntPtr.Zero; bool initialized = false;
        try
        {
            nuint bytes = 0; _ = InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref bytes);
            Require(bytes is >= 32 and <= 65536, "CPU_ATTRIBUTE_SIZE");
            attributes = Marshal.AllocHGlobal((int)bytes); Check(InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes)); initialized = true;
            jobs = Marshal.AllocHGlobal(IntPtr.Size); Marshal.WriteIntPtr(jobs, job);
            Check(UpdateProcThreadAttribute(attributes, 0, 0x0002000d, jobs, (nuint)IntPtr.Size, IntPtr.Zero, IntPtr.Zero));
            var startup = new StartupEx { Startup = new Startup { Size = (uint)Marshal.SizeOf<StartupEx>() }, Attributes = attributes };
            string image = Path.Combine(AppContext.BaseDirectory, names[0]);
            var command = new StringBuilder('"' + image + "\" --owned-cpu " + mode + " " + id);
            createAttempted = true;
            bool ok = CreateProcessW(image, command, IntPtr.Zero, IntPtr.Zero, false, 0x08084004, IntPtr.Zero, AppContext.BaseDirectory, ref startup, out child);
            createReturned = true; noChildProven = !ok && child.Process == IntPtr.Zero && child.Thread == IntPtr.Zero && child.ProcessId == 0 && child.ThreadId == 0;
            Check(ok); Check(IsProcessInJob(child.Process, job, out bool belongs)); Require(belongs && child.ProcessId != Environment.ProcessId, "CPU_ORIGINAL_CHILD_JOB_BINDING");
            Require(GetPriorityClass(child.Process) == 0x4000, "CPU_HELPER_BELOW_NORMAL_REQUIRED");
            Require(ResumeThread(child.Thread) == 1, "CPU_ORIGINAL_THREAD_RESUME");
        }
        finally
        {
            if (initialized) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (jobs != IntPtr.Zero) Marshal.FreeHGlobal(jobs);
        }
        while (clock.Elapsed < TimeSpan.FromSeconds(8))
        {
            if (WaitForSingleObject(child.Process, 0) == 0) throw new InvalidDataException("CPU_HELPER_EXITED_BEFORE_READY");
            string path = Path.Combine(Folder, "ready.json");
            if (File.Exists(path))
            {
                try
                {
                    using var doc = Read(path); var r = doc.RootElement;
                    Require(r.GetProperty("status").GetString() == "ready" && r.GetProperty("mode").GetString() == mode &&
                        r.GetProperty("id").GetString() == id && r.GetProperty("bufferBytes").GetInt32() == 33554432 &&
                        r.GetProperty("workers").GetInt32() is >= 1 and <= 2, "CPU_HELPER_READY_BINDING");
                    ready = true; readyAt = clock.Elapsed; return;
                }
                catch (JsonException) { }
                catch (IOException) { }
            }
            await Task.Delay(50);
        }
        throw new TimeoutException("CPU_HELPER_READY_NOT_OBSERVED");
    }
    internal async Task RetireAsync()
    {
        while (!retired)
        {
            bool originalExited = child.Process != IntPtr.Zero && child.Thread != IntPtr.Zero && WaitForSingleObject(child.Process, 0) == 0 && WaitForSingleObject(child.Thread, 0) == 0;
            bool neverCreated = !createAttempted || createReturned && noChildProven;
            bool empty = job == IntPtr.Zero;
            try { if (job != IntPtr.Zero) { byte[] a = Query(1, 48), p = Query(3, 264); empty = BinaryPrimitives.ReadUInt32LittleEndian(a.AsSpan(40)) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(p) == 0 && BinaryPrimitives.ReadUInt32LittleEndian(p.AsSpan(4)) == 0; } }
            catch (Exception error) { failure ??= "CPU_JOB_QUERY_" + error.GetType().Name; }
            if ((originalExited || neverCreated) && empty)
            {
                if (originalExited) { Check(GetExitCodeProcess(child.Process, out uint code)); exitCode = unchecked((int)code); }
                retired = true; break;
            }
            TimeSpan limit = ready ? readyAt + TimeSpan.FromSeconds(25) : TimeSpan.FromSeconds(13);
            if (!recovered && clock.Elapsed >= limit && job != IntPtr.Zero)
            { recovered = true; if (!TerminateJobObject(job, 0xe0a7c001)) failure ??= "CPU_ORIGINAL_JOB_RECOVERY_FAILED"; }
            if (!pendingWritten && clock.Elapsed > limit + TimeSpan.FromSeconds(10))
            { File.WriteAllText(Path.Combine(Folder, "PENDING-HELPER.json"), JsonSerializer.Serialize(Report)); pendingWritten = true; }
            await Task.Delay(75);
        }
        if (child.Thread != IntPtr.Zero) { Check(CloseHandle(child.Thread)); child.Thread = IntPtr.Zero; }
        if (child.Process != IntPtr.Zero) { Check(CloseHandle(child.Process)); child.Process = IntPtr.Zero; }
        if (job != IntPtr.Zero) { Check(CloseHandle(job)); job = IntPtr.Zero; }
        foreach (var pin in pins) pin.Dispose(); pins.Clear();
        if (!ready || recovered || exitCode != 0) { failure ??= "CPU_HELPER_NOT_NATURAL_EXIT"; return; }
        try
        {
            using var doc = Read(Path.Combine(Folder, "result.json")); var r = doc.RootElement;
            Require(r.GetProperty("status").GetString() == "completed" && r.GetProperty("mode").GetString() == mode && r.GetProperty("id").GetString() == id &&
                r.GetProperty("bufferBytes").GetInt32() == 33554432 && r.GetProperty("workers").GetInt32() is >= 1 and <= 2 &&
                r.GetProperty("wallSeconds").GetDouble() >= 19 && (mode == "control" || r.GetProperty("iterations").GetInt64() > 0 && r.GetProperty("cpuSeconds").GetDouble() > 0), "CPU_ACTUAL_WORK_NOT_CONFIRMED");
            Require(closeStarted.HasValue && closeFinished.HasValue && closeStarted >= r.GetProperty("startUtc").GetDateTimeOffset() &&
                closeFinished <= r.GetProperty("endUtc").GetDateTimeOffset() && (mode == "control" || closeEndCpuSeconds - closeStartCpuSeconds > 0), "CPU_WORK_WINDOW_DID_NOT_COVER_CLOSE");
            telemetry = r.Clone();
        }
        catch (Exception error) { failure ??= "CPU_HELPER_TELEMETRY_" + error.GetType().Name; }
    }
    private static JsonDocument Read(string path) { Require(new FileInfo(path).Length <= 16384, "CPU_BOUNDED_TELEMETRY_REQUIRED"); return JsonDocument.Parse(File.ReadAllBytes(path)); }
    private byte[] Query(int kind, int size) { var bytes = new byte[size]; Check(QueryInformationJobObject(job, kind, bytes, (uint)size, out uint length)); Require(length >= (kind == 3 ? 8 : size) && length <= size, "CPU_JOB_QUERY_LENGTH"); return bytes; }
    private static string Hash(string path) { using var s = File.OpenRead(path); return Convert.ToHexStringLower(SHA256.HashData(s)); }
    private static void Require(bool value, string code) { if (!value) throw new InvalidDataException(code); }
    private static void Check(bool value) { if (!value) throw new Win32Exception(Marshal.GetLastWin32Error()); }
    [StructLayout(LayoutKind.Sequential)] private struct Startup { internal uint Size; internal IntPtr Reserved, Desktop, Title; internal uint X, Y, Width, Height, XChars, YChars, Fill, Flags; internal ushort Show, ReservedBytes; internal IntPtr ReservedData, Input, Output, Error; }
    [StructLayout(LayoutKind.Sequential)] private struct StartupEx { internal Startup Startup; internal IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { internal IntPtr Process, Thread; internal uint ProcessId, ThreadId; }
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateJobObjectW(IntPtr a, string? n);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr j, int k, byte[] b, uint n);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(IntPtr j, int k, [Out] byte[] b, uint n, out uint r);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsProcessInJob(IntPtr p, IntPtr j, [MarshalAs(UnmanagedType.Bool)] out bool b);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr a, int c, uint f, ref nuint b);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr a, uint f, nuint k, IntPtr v, nuint s, IntPtr p, IntPtr r);
    [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr a);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcessW(string e, StringBuilder c, IntPtr pa, IntPtr ta, [MarshalAs(UnmanagedType.Bool)] bool i, uint f, IntPtr env, string d, ref StartupEx s, out ProcessInfo p);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr t);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr h, uint t);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(IntPtr p, out uint c);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(IntPtr p, out long c, out long e, out long k, out long u);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint GetPriorityClass(IntPtr p);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(IntPtr j, uint e);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr h);
}
