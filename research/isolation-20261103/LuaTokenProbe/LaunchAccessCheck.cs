using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.LuaTokenProbe;

// Captures existing owner/group/DACL and evaluates it in memory. The
// impersonation-type token OBJECT is never attached to a thread or process.
internal static class LaunchAccessCheck
{
    internal static object Run(string workspace, string apphost, SafeAccessTokenHandle medium)
    {
        RequireNoThreadToken();
        if (!DuplicateTokenEx(medium, 8, IntPtr.Zero, 2, 2, out var query)) throw Error("ACCESSCHECK_QUERY_TOKEN_COPY_FAILED");
        using (query)
        {
            TokenFacts queryFacts = Native.Read(query);
            if (queryFacts.token_type != 2 || queryFacts.integrity_rid != 8192 || queryFacts.elevated || queryFacts.administrators_enabled)
                throw new InvalidOperationException("ACCESSCHECK_EXPECTED_NONADMIN_MEDIUM_QUERY_TOKEN");
            string target = Path.GetFullPath(apphost);
            if (!Path.IsPathFullyQualified(apphost) || !target.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !(target.StartsWith(Path.Combine(workspace, "tools") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                  target.StartsWith(Path.Combine(workspace, "research") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) ||
                !Path.GetFileName(target).StartsWith("AutumnOS.", StringComparison.Ordinal) || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("ACCESSCHECK_REQUIRES_EXACT_OWN_TOOLS_OR_RESEARCH_APPHOST");
            using var targetLease = Lease(workspace, target);
            if (FileVersionInfo.GetVersionInfo(target).ProductName != "Lab Chronicles AutumnOS") throw new InvalidDataException("OWN_APPHOST_PRODUCT_METADATA_MISMATCH");
            var results = new List<object>();
            IntPtr station = GetProcessWindowStation(), desktop = GetThreadDesktop(GetCurrentThreadId());
            string stationName = ObjectName(station), desktopName = ObjectName(desktop);
            if (!stationName.Equals("WinSta0", StringComparison.OrdinalIgnoreCase) || !desktopName.Equals("Default", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("ACCESSCHECK_REQUIRES_OWN_CURRENT_WINSTA0_DEFAULT");
            bool complete = Check("current_WinSta0", "window_station", () => UserDescriptor(station), new(0x20303, 0x2001c, 0x20060, 0xf037f),
                [new("READATTRIBUTES", 2), new("ACCESSCLIPBOARD", 4), new("CREATEDESKTOP", 8), new("READ_CONTROL", 0x20000), new("GENERIC_READ", 0x80000000), new("GENERIC_WRITE", 0x40000000)], query, results);
            complete &= Check("current_Default", "desktop", () => UserDescriptor(desktop), new(0x20041, 0x200be, 0x20100, 0xf01ff),
                [new("CREATEWINDOW", 2), new("READOBJECTS", 1), new("WRITEOBJECTS", 0x80), new("READ_CONTROL", 0x20000), new("GENERIC_READ", 0x80000000), new("GENERIC_WRITE", 0x40000000)], query, results);
            string[] files = [target, Path.ChangeExtension(target, ".dll"),
                Path.Combine(workspace, ".tools/dotnet/host/fxr/10.0.12/hostfxr.dll"),
                Path.Combine(workspace, ".tools/dotnet/shared/Microsoft.NETCore.App/10.0.12/coreclr.dll"),
                Path.Combine(workspace, ".tools/dotnet/shared/Microsoft.NETCore.App/10.0.12/System.Private.CoreLib.dll")];
            foreach (string file in files)
            {
                string relative = Path.GetRelativePath(workspace, file);
                complete &= Check(relative, "file", () => FileDescriptor(workspace, file), new(0x120089, 0x120116, 0x1200a0, 0x1f01ff),
                    [new("FILE_READ_DATA", 1), new("FILE_EXECUTE", 0x20), new("READ_CONTROL", 0x20000), new("GENERIC_READ_EXECUTE", 0xa0000000)], query, results);
            }
            RequireNoThreadToken();
            if (GetProcessWindowStation() != station || GetThreadDesktop(GetCurrentThreadId()) != desktop) throw new InvalidOperationException("ACCESSCHECK_ASSOCIATION_CHANGED");
            return new { status = complete ? "completed_readonly_accesscheck" : "partial_readonly_accesscheck", query_token = queryFacts, objects = results,
                token_attachment = "none", acl_changes = "none", process_launch = "none", clipboard_calls = "none",
                meaning = "DACL AccessCheck on captured owner/group/DACL only; mandatory integrity and other kernel policy are not modeled. Denied is a diagnostic lead, not a loader root-cause trace. Allowed does not prove process or browser initialization." };
        }
    }

    private static bool Check(string label, string kind, Func<byte[]> descriptor, Mapping mapping, Right[] rights, SafeAccessTokenHandle token, List<object> results)
    {
        try
        {
            byte[] sd = descriptor();
            var raw = new RawSecurityDescriptor(sd, 0);
            var checks = new List<object>(); bool complete = true;
            foreach (Right right in rights)
            {
                uint desired = right.Mask; MapGenericMask(ref desired, ref mapping);
                GCHandle pinned = GCHandle.Alloc(sd, GCHandleType.Pinned);
                IntPtr privileges = Marshal.AllocHGlobal(65536);
                try
                {
                    uint bytes = 65536;
                    bool api = AccessCheck(pinned.AddrOfPinnedObject(), token, desired, ref mapping, privileges, ref bytes, out uint granted, out bool allowed);
                    int nativeError = Marshal.GetLastWin32Error();
                    complete &= api;
                    checks.Add(new { right = right.Name, requested_mask = right.Mask, mapped_mask = desired, api_succeeded = api,
                        access_allowed = api ? (bool?)allowed : null, granted_mask = api ? (uint?)granted : null, native_error = api ? (int?)null : nativeError });
                }
                finally { Marshal.FreeHGlobal(privileges); pinned.Free(); }
            }
            results.Add(new { label, kind, descriptor_sha256 = Hash(sd), owner_sid_sha256 = raw.Owner is null ? null : Hash(Encoding.UTF8.GetBytes(raw.Owner.Value)),
                group_sid_sha256 = raw.Group is null ? null : Hash(Encoding.UTF8.GetBytes(raw.Group.Value)), dacl_present = (raw.ControlFlags & ControlFlags.DiscretionaryAclPresent) != 0,
                dacl_null = raw.DiscretionaryAcl is null,
                generic_mapping = new { read = mapping.Read, write = mapping.Write, execute = mapping.Execute, all = mapping.All }, checks });
            return complete;
        }
        catch (Exception error)
        {
            results.Add(new { label, kind, status = "read_or_accesscheck_failed", failure = error.GetType().Name, operation = error.Message,
                native_error = error is Win32Exception w ? w.NativeErrorCode : (int?)null });
            return false;
        }
    }

    private static FileStream Lease(string workspace, string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var text = new StringBuilder(32768);
            uint length = GetFinalPathNameByHandle(stream.SafeFileHandle, text, (uint)text.Capacity, 0);
            if (length == 0 || length >= text.Capacity) throw Error("FIXED_FILE_FINAL_PATH_QUERY_FAILED");
            string actual = text.ToString();
            if (actual.StartsWith("\\\\?\\", StringComparison.Ordinal)) actual = actual[4..];
            if (!actual.Equals(Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase) || !actual.StartsWith(workspace + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("FIXED_FILE_REPARSE_OR_PATH_ESCAPE");
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    private static byte[] FileDescriptor(string workspace, string path)
    {
        using var file = Lease(workspace, path);
        uint error = GetSecurityInfo(file.SafeFileHandle.DangerousGetHandle(), 1, 7, out _, out _, out _, out _, out IntPtr descriptor);
        if (error != 0) throw new Win32Exception((int)error, "FIXED_FILE_SD_READ_FAILED");
        try { return CopyDescriptor(descriptor); } finally { LocalFree(descriptor); }
    }
    private static byte[] UserDescriptor(IntPtr handle)
    {
        uint info = 7;
        GetUserObjectSecurity(handle, ref info, IntPtr.Zero, 0, out uint required);
        if (required == 0 || required > 1048576) throw Error("USER_OBJECT_SD_SIZE_FAILED");
        IntPtr memory = Marshal.AllocHGlobal(checked((int)required));
        try
        {
            if (!GetUserObjectSecurity(handle, ref info, memory, required, out uint actual) || actual > required) throw Error("USER_OBJECT_SD_READ_FAILED");
            return CopyDescriptor(memory);
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    private static byte[] CopyDescriptor(IntPtr descriptor)
    {
        uint length = GetSecurityDescriptorLength(descriptor);
        if (descriptor == IntPtr.Zero || length == 0 || length > 1048576) throw new InvalidDataException("CAPTURED_SD_SIZE_INVALID");
        byte[] result = new byte[length]; Marshal.Copy(descriptor, result, 0, result.Length); return result;
    }
    private static void RequireNoThreadToken()
    {
        if (OpenThreadToken(GetCurrentThread(), 8, true, out var token)) { token.Dispose(); throw new InvalidOperationException("EXISTING_THREAD_TOKEN_NOT_ALLOWED"); }
        if (Marshal.GetLastWin32Error() != 1008) throw Error("THREAD_TOKEN_QUERY_FAILED");
    }
    private static string ObjectName(IntPtr handle)
    {
        var value = new StringBuilder(512);
        if (handle == IntPtr.Zero || !GetUserObjectInformation(handle, 2, value, value.Capacity * 2, out _)) throw Error("OWN_USER_OBJECT_NAME_FAILED");
        return value.ToString();
    }
    private static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation);
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private sealed record Right(string Name, uint Mask);
    [StructLayout(LayoutKind.Sequential)] private struct Mapping(uint read, uint write, uint execute, uint all) { public uint Read = read, Write = write, Execute = execute, All = all; }
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token, uint access, IntPtr attributes, int level, int type, out SafeAccessTokenHandle copy);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AccessCheck(IntPtr sd, SafeAccessTokenHandle token, uint desired, ref Mapping mapping, IntPtr privileges, ref uint length, out uint granted, out bool allowed);
    [DllImport("advapi32.dll")] private static extern void MapGenericMask(ref uint mask, ref Mapping mapping);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(IntPtr handle, int kind, uint information, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
    [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenThreadToken(IntPtr thread, uint access, bool openAsSelf, out SafeAccessTokenHandle token);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder text, uint size, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetUserObjectInformation(IntPtr handle, int kind, StringBuilder data, int length, out int required);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetUserObjectSecurity(IntPtr handle, ref uint information, IntPtr descriptor, uint length, out uint required);
}
