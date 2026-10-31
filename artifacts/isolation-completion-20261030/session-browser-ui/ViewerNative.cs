using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using AutumnOS.SessionBrowserProtocol;

namespace AutumnOS.SessionBrowserUI;

internal sealed class ViewerNativeException(int code, string stage) : Win32Exception(code, stage)
{ internal string FixedStage { get; } = stage; }
internal sealed record ViewerTokenFacts(string user_sid_sha256, uint session_id, int integrity_rid, bool elevated, bool appcontainer,
    string authentication_id, string[] enabled_privileges, string[] present_privileges);
internal sealed record CoreIdentityBinding(ViewerTokenFacts current, ViewerTokenFacts core, string image_path_sha256,
    uint original_pid, ulong creation_filetime, bool verified);

internal static class ViewerNative
{
    internal static ViewerNativeException Error(string stage) => new(Marshal.GetLastWin32Error(), stage);
    internal static string Failure(Exception error) => error.GetType().Name + ":0x" + unchecked((uint)error.HResult).ToString("x8") +
        (error is Win32Exception native ? ":win32=" + native.NativeErrorCode : "") + (error is ViewerNativeException own ? ":stage=" + own.FixedStage : "") +
        (error is OwnedReportWriteException report ? ":report_stage=" + report.Stage + ":inner=" + report.InnerType + ":inner_hresult=" + unchecked((uint)report.InnerHResult).ToString("x8") + ":attempts=" + report.Attempts : "");
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static CoreIdentityBinding RequireCore(SafeProcessHandle originalCore, string expectedFullPath)
    {
        RequireLive(originalCore);
        using var current = new SafeProcessHandle(GetCurrentProcess(), false);
        ViewerTokenFacts self = ReadToken(current), core = ReadToken(originalCore);
        if (self.elevated || self.appcontainer || self.integrity_rid != 8192 || core.user_sid_sha256 != self.user_sid_sha256 || core.session_id != self.session_id ||
            core.integrity_rid != self.integrity_rid || core.elevated != self.elevated || core.appcontainer != self.appcontainer || core.authentication_id != self.authentication_id ||
            !core.enabled_privileges.All(self.enabled_privileges.Contains) || !core.present_privileges.All(self.present_privileges.Contains))
            throw new InvalidDataException("ORIGINAL_CORE_TOKEN_MUST_MATCH_ORDINARY_VIEWER_WITHOUT_PRIVILEGE_GAIN");
        uint pid = GetProcessId(originalCore); var image = new StringBuilder(32768); uint length = (uint)image.Capacity;
        if (pid == 0 || !QueryFullProcessImageNameW(originalCore, 0, image, ref length)) throw Error("original_core_image");
        string actual = Path.GetFullPath(image.ToString());
        if (!Path.IsPathFullyQualified(expectedFullPath) || !actual.Equals(Path.GetFullPath(expectedFullPath), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("FROZEN_ORIGINAL_CORE_IMAGE_PATH_MISMATCH");
        if (!GetProcessTimes(originalCore, out FileTime created, out _, out _, out _)) throw Error("original_core_creation_time");
        RequireLive(originalCore);
        return new(self, core, Hash(actual.ToUpperInvariant()), pid, created.Value, true);
    }
    internal static void RequireLive(SafeProcessHandle process)
    {
        if (process.IsInvalid || process.IsClosed) throw new InvalidDataException("ORIGINAL_CORE_HANDLE_REQUIRED");
        uint wait = WaitForSingleObject(process, 0);
        if (wait is not (0 or 258)) throw Error("original_core_wait");
        if (wait == 0) throw new InvalidDataException("ORIGINAL_CORE_ALREADY_EXITED");
    }
    internal static SafeProcessHandle BindStationHost(SafeProcessHandle originalCore, ulong remoteHandle, uint announcedPid, ulong announcedCreation, string expectedCorePath)
    {
        RequireLive(originalCore);
        if (remoteHandle == 0 || announcedPid == 0 || announcedCreation == 0) throw new InvalidDataException("ORIGINAL_STATION_HOST_BINDING_METADATA_REQUIRED");
        if (!DuplicateProcessHandle(originalCore, new IntPtr(unchecked((long)remoteHandle)), GetCurrentProcess(), out SafeProcessHandle host, 0x101000, false, 0))
            throw Error("viewer_duplicate_original_station_host_QUERY_SYNC_only");
        bool admitted = false;
        try
        {
            RequireLive(host);
            uint actualPid = GetProcessId(host);
            if (actualPid == 0) throw Error("viewer_original_station_host_PID");
            if (!GetProcessTimes(host, out FileTime created, out _, out _, out _)) throw Error("viewer_original_station_host_creation_time");
            if (actualPid != announcedPid || created.Value != announcedCreation) throw new InvalidDataException("EXACT_ORIGINAL_STATION_HOST_PID_AND_CREATION_REQUIRED");
            var image = new StringBuilder(32768); uint length = (uint)image.Capacity;
            if (!QueryFullProcessImageNameW(host, 0, image, ref length)) throw Error("viewer_original_station_host_image");
            if (!Path.IsPathFullyQualified(expectedCorePath) || !Path.GetFullPath(image.ToString()).Equals(Path.GetFullPath(expectedCorePath), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("FROZEN_ORIGINAL_STATION_HOST_IMAGE_REQUIRED");
            using var current = new SafeProcessHandle(GetCurrentProcess(), false);
            ViewerTokenFacts self = ReadToken(current), peer = ReadToken(host);
            if (self.elevated || self.appcontainer || self.integrity_rid != 8192 || peer.user_sid_sha256 != self.user_sid_sha256 || peer.session_id != self.session_id ||
                peer.integrity_rid != 8192 || peer.elevated || peer.appcontainer || peer.authentication_id == self.authentication_id ||
                !peer.enabled_privileges.All(self.enabled_privileges.Contains) || !peer.present_privileges.All(self.present_privileges.Contains))
                throw new InvalidDataException("ORIGINAL_NETONLY_STATION_HOST_TOKEN_RELATION_REQUIRED");
            RequireLive(host); admitted = true; return host;
        }
        finally { if (!admitted) host.Dispose(); }
    }
    internal static bool OriginalExited(SafeProcessHandle original)
    {
        uint result = WaitForSingleObject(original, 0);
        if (result is not (0 or 258)) throw Error("viewer_original_process_wait");
        return result == 0;
    }
    internal static uint ExitCode(SafeProcessHandle original)
    {
        if (!OriginalExited(original)) throw new InvalidDataException("ORIGINAL_PROCESS_EXIT_REQUIRED");
        if (!GetExitCodeProcess(original, out uint code)) throw Error("viewer_original_process_exit_code");
        return code;
    }
    internal static ViewerTokenFacts ReadIdentity(SafeProcessHandle original) => ReadToken(original);
    private static ViewerTokenFacts ReadToken(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 8, out SafeAccessTokenHandle token)) throw Error("viewer_original_token_open");
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            string user = identity.User?.Value ?? throw new InvalidDataException("TOKEN_USER_REQUIRED");
            int Integer(int kind)
            { if (!GetTokenInt(token, kind, out int value, 4, out int bytes) || bytes != 4) throw Error("viewer_token_integer"); return value; }
            T Buffer<T>(int kind, Func<IntPtr, int, T> parse)
            {
                bool first = GetTokenInformation(token, kind, IntPtr.Zero, 0, out int length);
                if (first || Marshal.GetLastWin32Error() != 122 || length is < 4 or > 65536) throw new InvalidDataException("VIEWER_TOKEN_BUFFER_SIZE");
                IntPtr memory = Marshal.AllocHGlobal(length);
                try { if (!GetTokenInformation(token, kind, memory, length, out int used) || used > length) throw Error("viewer_token_information"); return parse(memory, used); }
                finally { Marshal.FreeHGlobal(memory); }
            }
            int il = Buffer(25, (p, _) => { string sid = new SecurityIdentifier(Marshal.ReadIntPtr(p)).Value; return int.Parse(sid[(sid.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture); });
            string authentication = Buffer(10, (p, n) =>
            { if (n < 56) throw new InvalidDataException("VIEWER_TOKEN_STATISTICS_SIZE"); return unchecked((uint)Marshal.ReadInt32(p, 12)).ToString("x8") + ":" + unchecked((uint)Marshal.ReadInt32(p, 8)).ToString("x8"); });
            (string[] enabled, string[] present) privileges = Buffer(3, (p, n) =>
            {
                int count = Marshal.ReadInt32(p); if (count < 0 || count > 1024 || n < 4 + count * 12) throw new InvalidDataException("VIEWER_TOKEN_PRIVILEGES_SIZE");
                var enabled = new List<string>(); var present = new List<string>();
                for (int index = 0; index < count; index++)
                {
                    int offset = 4 + index * 12;
                    string luid = unchecked((uint)Marshal.ReadInt32(p, offset + 4)).ToString("x8") + ":" + unchecked((uint)Marshal.ReadInt32(p, offset)).ToString("x8");
                    present.Add(luid); if ((Marshal.ReadInt32(p, offset + 8) & 2) != 0) enabled.Add(luid);
                }
                return (enabled.Order(StringComparer.Ordinal).ToArray(), present.Order(StringComparer.Ordinal).ToArray());
            });
            return new(Hash(user), unchecked((uint)Integer(12)), il, Integer(20) != 0, Integer(29) != 0, authentication, privileges.enabled, privileges.present);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct FileTime { internal uint low, high; internal readonly ulong Value => ((ulong)high << 32) | low; }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", EntryPoint = "DuplicateHandle", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateProcessHandle(SafeProcessHandle sourceProcess, IntPtr source, IntPtr targetProcess, out SafeProcessHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint GetProcessId(SafeProcessHandle process);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process, uint flags, StringBuilder image, ref uint length);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetProcessTimes(SafeProcessHandle process, out FileTime created, out FileTime exited, out FileTime kernel, out FileTime user);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", EntryPoint = "GetTokenInformation", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInt(SafeAccessTokenHandle token, int kind, out int value, int bytes, out int required);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr buffer, int bytes, out int required);
}

internal sealed class ViewerJobWitness
{
    private IntPtr handle;
    public bool InitialEmptyVerified { get; private set; }
    public bool Empty => original_job_duplicate_bound && last_query_succeeded && active_processes == 0 && process_ids.Length == 0;
    public bool HistoricalFailure { get; private set; }
    public bool original_job_duplicate_bound { get; private set; }
    public uint requested_access => 4;
    public uint? limit_flags { get; private set; }
    public uint? active_processes { get; private set; }
    public uint[] process_ids { get; private set; } = [];
    public bool last_query_succeeded { get; private set; }
    public bool closed_after_empty { get; private set; }
    public List<string> failures { get; } = [];
    internal ViewerJobWitness(SafeProcessHandle originalCore, ulong remoteHandle)
    {
        ViewerNative.RequireLive(originalCore);
        if (remoteHandle == 0) throw new InvalidDataException("ORIGINAL_CORE_JOB_HANDLE_REQUIRED");
        if (!DuplicateHandle(originalCore, new IntPtr(unchecked((long)remoteHandle)), ViewerNative.GetCurrentProcess(), out handle, 4, false, 0)) throw ViewerNative.Error("viewer_duplicate_exact_job_QUERY_only");
        original_job_duplicate_bound = true; Tick(); InitialEmptyVerified = Empty && !HistoricalFailure && limit_flags == 0;
    }
    internal void Tick()
    {
        if (handle == IntPtr.Zero || closed_after_empty) return;
        last_query_succeeded = false;
        IntPtr limits = Marshal.AllocHGlobal(144), accounting = Marshal.AllocHGlobal(48), list = Marshal.AllocHGlobal(8 + 4096 * IntPtr.Size);
        try
        {
            if (!QueryInformationJobObject(handle, 9, limits, 144, out _)) throw ViewerNative.Error("viewer_job_flags_query");
            limit_flags = unchecked((uint)Marshal.ReadInt32(limits, 16));
            if (limit_flags != 0) throw new InvalidDataException("EXACT_JOB_FLAGS_MUST_REMAIN_ZERO");
            if (!QueryInformationJobObject(handle, 1, accounting, 48, out _)) throw ViewerNative.Error("viewer_job_active_query");
            active_processes = unchecked((uint)Marshal.ReadInt32(accounting, 40));
            if (!QueryInformationJobObject(handle, 3, list, (uint)(8 + 4096 * IntPtr.Size), out _)) throw ViewerNative.Error("viewer_job_PID_list_query");
            uint assigned = unchecked((uint)Marshal.ReadInt32(list)), count = unchecked((uint)Marshal.ReadInt32(list, 4));
            if (count > 4096 || assigned != count) throw new InvalidDataException("VIEWER_EXACT_JOB_PID_LIST_MUST_NOT_TRUNCATE");
            uint[] ids = new uint[count];
            for (int index = 0; index < ids.Length; index++)
            { ulong value = unchecked((ulong)Marshal.ReadIntPtr(list, 8 + index * IntPtr.Size).ToInt64()); if (value is 0 or > uint.MaxValue) throw new InvalidDataException("VIEWER_JOB_PID_INVALID"); ids[index] = (uint)value; }
            process_ids = ids; last_query_succeeded = true;
        }
        catch (Exception error) { HistoricalFailure = true; if (failures.Count < 64) failures.Add(ViewerNative.Failure(error)); }
        finally { Marshal.FreeHGlobal(limits); Marshal.FreeHGlobal(accounting); Marshal.FreeHGlobal(list); }
    }
    internal void CloseAfterEmpty()
    {
        Tick(); if (!Empty) throw new InvalidDataException("VIEWER_ORIGINAL_JOB_EMPTY_REQUIRED_BEFORE_CLOSE");
        if (!CloseHandle(handle)) throw ViewerNative.Error("viewer_close_original_QUERY_only_job");
        handle = IntPtr.Zero; closed_after_empty = true;
    }
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DuplicateHandle(SafeProcessHandle sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(IntPtr job, int kind, IntPtr buffer, uint bytes, out uint returned);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
}
