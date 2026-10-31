using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace AutumnOS.OwnedBrowserParent;

internal sealed class HeldProcess
{
    internal IntPtr Handle;
    public uint pid { get; init; }
    public ulong creation_filetime { get; init; }
    public string discovery { get; init; } = "";
    public string image_relative { get; init; } = "";
    public string image_basename { get; init; } = "";
    public string image_path_binding_sha256 { get; init; } = "";
    public bool image_is_exact_windows_system32_conhost { get; init; }
    public bool image_in_new_copied_trees { get; init; }
    public string sdk_role_hint { get; set; } = "unknown";
    public string role_evidence { get; init; } = "SDK_hint_plus_OS_image_identity_not_independent_command_line_role";
    public TokenFacts? token { get; set; }
    public bool original_handle_signaled { get; set; }
    public uint? exit_code { get; set; }
    public string? failure { get; set; }
    public DateTimeOffset observed_first_utc { get; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? original_exit_observed_utc { get; set; }
}
internal sealed record TokenFacts(int is_appcontainer, string? package_sid_sha256, bool? expected_package_sid_matches,
    int integrity_rid, int capability_count, string[] capability_sid_sha256, bool ordinary_parent_os_query);

internal sealed class ProcessAudit(string hostRoot, string runtimeRoot, string? expectedSid)
{
    public List<HeldProcess> Held { get; } = [];
    public List<object> Failures { get; } = [];
    public bool observation_gap => ObservationGap;
    public bool ObservationGap { get; private set; }
    public bool process_identity_gap { get; private set; }
    public uint RootPid { get; private set; }
    public ulong RootCreation { get; private set; }
    internal bool AllSignaled => Held.All(p => p.original_handle_signaled);
    internal void AdoptRoot(IntPtr handle, uint pid)
    {
        RootPid = pid;
        try
        {
            var held = Inspect(handle, pid, "original_CreateProcess_handle", "host");
            RootCreation = held.creation_filetime; Held.Add(held);
        }
        catch (Exception ex)
        {
            ObservationGap = true; process_identity_gap = true;
            Held.Add(new HeldProcess { Handle = handle, pid = pid, discovery = "original_CreateProcess_handle",
                sdk_role_hint = "host", failure = Program.FailureCode(ex) });
        }
    }
    internal static TokenFacts ReadToken(IntPtr process, string? expected)
    {
        Native.Check(Native.OpenProcessToken(process, Native.TokenQuery, out IntPtr token), "open_process_token");
        try
        {
            int ac = Native.TokenInt(token, 29);
            string? sid = null; int il;
            IntPtr package = TokenBuffer(token, 31);
            try { IntPtr value = Marshal.ReadIntPtr(package); if (value != IntPtr.Zero) sid = Native.SidString(value); }
            finally { Marshal.FreeHGlobal(package); }
            IntPtr integrity = TokenBuffer(token, 25);
            try { string text = Native.SidString(Marshal.ReadIntPtr(integrity)); il = int.Parse(text[(text.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture); }
            finally { Marshal.FreeHGlobal(integrity); }
            IntPtr caps = TokenBuffer(token, 30);
            string[] hashes;
            try
            {
                int count = Marshal.ReadInt32(caps);
                if (count < 0 || count > 256) throw new InvalidDataException("CAPABILITY_COUNT_INVALID");
                hashes = new string[count]; int offset = IntPtr.Size == 8 ? 8 : 4; int stride = IntPtr.Size == 8 ? 16 : 8;
                for (int i = 0; i < count; i++) hashes[i] = OwnedFiles.HashText(Native.SidString(Marshal.ReadIntPtr(caps, offset + i * stride)));
            }
            finally { Marshal.FreeHGlobal(caps); }
            return new TokenFacts(ac, sid is null ? null : OwnedFiles.HashText(sid), expected is null ? null : sid == expected,
                il, hashes.Length, hashes, true);
        }
        finally { Native.CloseHandle(token); }
    }
    private static IntPtr TokenBuffer(IntPtr token, int kind)
    {
        bool first = Native.GetTokenInformation(token, kind, IntPtr.Zero, 0, out uint bytes);
        int error = Marshal.GetLastWin32Error();
        if (first || error != 122 || bytes < 4 || bytes > 65536) throw new Win32Exception(error, "token_size_" + kind);
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try { Native.Check(Native.GetTokenInformation(token, kind, buffer, bytes, out _), "token_read_" + kind); return buffer; }
        catch { Marshal.FreeHGlobal(buffer); throw; }
    }
    private HeldProcess Inspect(IntPtr handle, uint pid, string discovery, string role)
    {
        Native.Check(Native.GetProcessTimes(handle, out var created, out _, out _, out _), "held_process_times");
        var image = new StringBuilder(32768); uint chars = (uint)image.Capacity;
        Native.Check(Native.QueryFullProcessImageNameW(handle, 0, image, ref chars), "held_process_image");
        string full = Path.GetFullPath(image.ToString()); string relative;
        bool inCopiedTrees = true;
        if (full.StartsWith(hostRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) relative = "host/" + Path.GetRelativePath(hostRoot, full);
        else if (full.StartsWith(runtimeRoot.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) relative = "runtime/" + Path.GetRelativePath(runtimeRoot, full);
        else { relative = "outside_new_copied_trees_path_redacted"; inCopiedTrees = false; }
        string conhost = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "conhost.exe"));
        var result = new HeldProcess { Handle = handle, pid = pid, creation_filetime = created.Value, discovery = discovery,
            image_relative = relative, image_basename = Path.GetFileName(full),
            image_path_binding_sha256 = "sha256:" + OwnedFiles.HashText(Path.TrimEndingDirectorySeparator(full).ToUpperInvariant()),
            image_is_exact_windows_system32_conhost = full.Equals(conhost, StringComparison.OrdinalIgnoreCase),
            image_in_new_copied_trees = inCopiedTrees, sdk_role_hint = role };
        if (!inCopiedTrees)
        {
            // Identification evidence is retained before recording the same
            // failure. Exact conhost recognition is diagnostic, no exemption.
            Gap(pid, "OBSERVED_PROCESS_OUTSIDE_NEW_COPIED_HOST_AND_RUNTIME", null);
            result.failure = "OBSERVED_PROCESS_OUTSIDE_NEW_COPIED_HOST_AND_RUNTIME";
        }
        try { result.token = ReadToken(handle, expectedSid); }
        catch (Exception ex) { result.failure = (result.failure is null ? "" : result.failure + ";") + Program.FailureCode(ex); ObservationGap = true; }
        return result;
    }
    internal HeldProcess? Observe(uint pid, string role, string discovery)
    {
        RefreshExits();
        var live = Held.LastOrDefault(p => p.pid == pid && !p.original_handle_signaled);
        if (live is not null) { if (role != "unknown") live.sdk_role_hint = role; return live; }
        IntPtr handle = Native.OpenProcess(Native.QueryLimited | Native.Synchronize, false, pid);
        if (handle == IntPtr.Zero)
        { int error = Marshal.GetLastWin32Error(); Gap(pid, "open_process", error); return null; }
        try
        {
            var result = Inspect(handle, pid, discovery, role);
            if (result.creation_filetime < RootCreation) throw new InvalidDataException("PID_CREATED_BEFORE_ORIGINAL_HOST");
            Held.Add(result); handle = IntPtr.Zero; return result;
        }
        catch (Exception ex)
        {
            Gap(pid, Program.FailureCode(ex), (ex as Win32Exception)?.NativeErrorCode);
            // An observed handle remains owned until its exact process exits,
            // even when image/token verification failed. Never drop it live.
            var failed = new HeldProcess { Handle = handle, pid = pid, discovery = discovery,
                sdk_role_hint = role, failure = Program.FailureCode(ex) };
            Held.Add(failed); handle = IntPtr.Zero; return failed;
        }
        finally { if (handle != IntPtr.Zero) Native.CloseHandle(handle); }
    }
    private void Gap(uint pid, string stage, int? error)
    { ObservationGap = true; process_identity_gap = true; if (Failures.Count < 256) Failures.Add(new { pid, stage, native_error = error }); }
    internal void Snapshot()
    {
        if (RootPid == 0) return;
        IntPtr snapshot = Native.CreateToolhelp32Snapshot(2, 0);
        if (snapshot == Native.InvalidHandle) { Gap(0, "toolhelp_snapshot", Marshal.GetLastWin32Error()); return; }
        try
        {
            List<(uint pid, uint parent)> rows = [];
            var row = new Native.ProcessEntry { Size = (uint)Marshal.SizeOf<Native.ProcessEntry>(), Image = "" };
            if (!Native.Process32First(snapshot, ref row)) { Gap(0, "process_first", Marshal.GetLastWin32Error()); return; }
            do { rows.Add((row.Pid, row.ParentPid)); } while (Native.Process32Next(snapshot, ref row));
            int last = Marshal.GetLastWin32Error(); if (last != 18) Gap(0, "process_next", last);
            HashSet<uint> lineage = Held.Where(p => !p.original_handle_signaled).Select(p => p.pid).ToHashSet();
            lineage.Add(RootPid);
            bool changed;
            do
            {
                changed = false;
                foreach (var item in rows)
                    if (lineage.Contains(item.parent) && lineage.Add(item.pid))
                    { Observe(item.pid, "unknown", "OS_Toolhelp_descendant_snapshot"); changed = true; }
            } while (changed);
        }
        catch (Exception ex) { Gap(0, Program.FailureCode(ex), (ex as Win32Exception)?.NativeErrorCode); }
        finally { Native.CloseHandle(snapshot); }
        RefreshExits();
    }
    internal void RefreshExits()
    {
        foreach (var item in Held.Where(p => !p.original_handle_signaled))
        {
            uint wait = Native.WaitForSingleObject(item.Handle, 0);
            if (wait == Native.WaitObject0)
            {
                item.original_handle_signaled = true;
                item.original_exit_observed_utc = DateTimeOffset.UtcNow;
                if (Native.GetExitCodeProcess(item.Handle, out uint exit)) item.exit_code = exit;
                else Gap(item.pid, "held_exit_code", Marshal.GetLastWin32Error());
            }
            else if (wait != Native.WaitTimeout) Gap(item.pid, "held_wait", Marshal.GetLastWin32Error());
        }
    }
    internal bool BoundRestricted(HeldProcess? process) => process?.token is { is_appcontainer: 1,
        expected_package_sid_matches: true, integrity_rid: <= 4096, capability_count: 0 };
    internal bool RestrictedObservedTokensPass => Held.Count > 1 && Held.All(BoundRestricted);
    internal void ReleaseSignaledHandles()
    {
        foreach (var process in Held.Where(p => p.original_handle_signaled && p.Handle != IntPtr.Zero))
        { Native.CloseHandle(process.Handle); process.Handle = IntPtr.Zero; }
    }
}
