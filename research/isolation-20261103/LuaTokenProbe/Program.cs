using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.LuaTokenProbe;

internal static class Program
{
    private const uint Flags = 5; // LUA_TOKEN | DISABLE_MAX_PRIVILEGE; no restricting SIDs.

    private static int Main(string[] args)
    {
        if (args.Length is not (2 or 3 or 4) || args[0] != "--report" ||
            (args.Length == 3 && args[2] != "--repair-copy-default-dacl") ||
            (args.Length == 4 && args[2] != "--check-launch-access")) return 2;
        bool repairCopyDefaultDacl = args.Length == 3;
        string workspace = Path.GetFullPath(Environment.CurrentDirectory);
        string artifacts = Path.Combine(workspace, "artifacts");
        string report = Path.GetFullPath(args[1]);
        if (!File.Exists(Path.Combine(workspace, "AutumnOS.slnx")) ||
            !report.StartsWith(artifacts + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        for (string? cursor = Path.GetDirectoryName(report); cursor is not null && cursor.Length >= artifacts.Length; cursor = Path.GetDirectoryName(cursor))
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("REPORT_REPARSE_DIRECTORY_FORBIDDEN");

        using var output = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        var started = DateTimeOffset.UtcNow;
        SafeAccessTokenHandle? source = null, copy = null;
        TokenFacts? originalBefore = null, originalAfter = null, copyBefore = null, copyAfter = null;
        string stage = "open_own_primary_token";
        object? failure = null;
        bool copyClosed = false, originalUnchanged = false;
        object? launchAccess = null;
        TokenFacts? copyAfterDacl = null;
        try
        {
            // This requests rights on a handle. Only the returned independent copy
            // is ever passed to SetTokenInformation, never the source handle.
            if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x8b, out source)) throw Native.Error("OPEN_OWN_TOKEN_FAILED");
            originalBefore = Native.Read(source);
            if (originalBefore.token_type != 1) throw new InvalidOperationException("PRIMARY_TOKEN_REQUIRED");
            stage = "create_independent_lua_token_copy";
            if (!Native.CreateRestrictedToken(source, Flags, 0, IntPtr.Zero, 0, IntPtr.Zero, 0, IntPtr.Zero, out copy))
                throw Native.Error("CREATE_LUA_TOKEN_COPY_FAILED");
            stage = "read_copy_before_integrity_change";
            copyBefore = Native.Read(copy);
            if (copyBefore.user_sid_sha256 != originalBefore.user_sid_sha256 || copyBefore.session_id != originalBefore.session_id || copyBefore.token_type != 1)
                throw new InvalidOperationException("COPY_IDENTITY_OR_TYPE_MISMATCH");
            stage = "set_only_copy_integrity_medium";
            Native.SetMedium(copy);
            stage = "read_copy_after_integrity_change";
            copyAfter = Native.Read(copy);
            if (repairCopyDefaultDacl)
            {
                stage = "repair_only_independent_copy_private_object_default_dacl";
                Native.SetCopyPrivateObjectDefaultDacl(copy);
                copyAfterDacl = Native.Read(copy);
            }
            if (args.Length == 4)
            {
                stage = "pure_accesscheck_of_existing_fixed_launch_objects";
                launchAccess = LaunchAccessCheck.Run(workspace, args[3], copy);
            }
        }
        catch (Exception error)
        {
            failure = new { type = error.GetType().Name, operation = error.Message, native_error = error is Win32Exception w ? w.NativeErrorCode : (int?)null };
        }
        finally
        {
            copy?.Dispose();
            copyClosed = copy is not null && copy.IsClosed;
            if (source is not null && originalBefore is not null)
            {
                try
                {
                    originalAfter = Native.Read(source);
                    originalUnchanged = JsonSerializer.Serialize(originalBefore) == JsonSerializer.Serialize(originalAfter);
                    if (!originalUnchanged) failure ??= new { type = "InvalidOperationException", operation = "ORIGINAL_TOKEN_FACTS_CHANGED", native_error = (int?)null };
                }
                catch (Exception error) { failure ??= new { type = error.GetType().Name, operation = "ORIGINAL_TOKEN_RECHECK_FAILED", native_error = error is Win32Exception w ? w.NativeErrorCode : (int?)null }; }
            }
            source?.Dispose();
        }

        bool completed = failure is null && copyClosed && originalUnchanged && copyAfter is not null;
        bool baseline = completed && copyAfter!.integrity_rid == 0x2000 && !copyAfter.elevated && !copyAfter.administrators_enabled &&
            copyAfter.token_type == 1 && !copyAfter.ui_access && !copyAfter.sandbox_inert && !copyAfter.is_appcontainer &&
            copyAfter.restricting_sids.Length == 0 && !copyAfter.is_token_restricted &&
            copyAfter.privileges.Where(p => (p.attributes & 2) != 0).All(p => p.name == "SeChangeNotifyPrivilege");
        JsonSerializer.Serialize(output, new
        {
            schema_version = 1, kind = "synthetic_lua_token_handle_only_diagnostic",
            status = completed ? "memory_diagnostic_completed" : "failed",
            started_utc = started, finished_utc = DateTimeOffset.UtcNow, pid = Environment.ProcessId,
            create_restricted_token_flags = Flags, restricting_sids_requested = 0,
            original_before = originalBefore, copy_before_medium = copyBefore, copy_after_medium = copyAfter,
            repair_copy_default_dacl_requested = repairCopyDefaultDacl, copy_after_default_dacl_repair = copyAfterDacl,
            copy_handle_closed = copyClosed, original_after_copy_closed = originalAfter,
            original_token_facts_unchanged = originalUnchanged,
            synthetic_experiment_baseline_matches = baseline,
            launch_access_checks = launchAccess,
            not_a_real_uac_limited_user = true,
            claim = "Only an independent in-memory token was created and its integrity lowered; its private-object DefaultDacl changes only in the explicit repair mode. No ordinary-user desktop, child process, LPAC, browser, clipboard or product acceptance was performed.",
            child_processes = "none", token_attachment_or_impersonation = "none", original_token_changes = "none",
            clipboard_calls = "none", filesystem_acl_changes = "none", global_policy_changes = "none",
            stage, failure
        }, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine(report);
        return completed ? 0 : 1;
    }
}

internal sealed record SidFact(string sid_sha256, string[] well_known_types, uint attributes);
internal sealed record PrivilegeFact(string name, uint attributes);
internal sealed record TokenFacts(string user_sid_sha256, bool user_rid_500, string owner_sid_sha256, uint session_id,
    int token_type, int elevation_type, bool elevated, uint integrity_rid, bool has_restrictions, bool is_token_restricted,
    bool ui_access, bool sandbox_inert, bool is_appcontainer, uint mandatory_policy,
    bool administrators_enabled, SidFact[] groups, SidFact[] restricting_sids, PrivilegeFact[] privileges,
    string token_id_sha256, string authentication_id_sha256, string modified_id_sha256, DefaultDaclFact default_dacl);

internal static partial class Native
{
    internal static Win32Exception Error(string operation) => new(Marshal.GetLastWin32Error(), operation);
    private static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    internal static TokenFacts Read(SafeAccessTokenHandle token)
    {
        using var userMemory = Query(token, 1);
        var user = new SecurityIdentifier(Marshal.ReadIntPtr(userMemory.Pointer));
        using var ownerMemory = Query(token, 4);
        string owner = new SecurityIdentifier(Marshal.ReadIntPtr(ownerMemory.Pointer)).Value;
        using var integrityMemory = Query(token, 25);
        string integrity = new SecurityIdentifier(Marshal.ReadIntPtr(integrityMemory.Pointer)).Value;
        using var statisticsMemory = Query(token, 10);
        var statistics = Marshal.PtrToStructure<Statistics>(statisticsMemory.Pointer);
        SidFact[] groups = ReadGroups(token, 2), restrictions = ReadGroups(token, 11);
        Marshal.SetLastPInvokeError(0);
        bool restricted = IsTokenRestricted(token);
        int restrictedError = Marshal.GetLastPInvokeError();
        if (!restricted && restrictedError != 0) throw new Win32Exception(restrictedError, "IS_TOKEN_RESTRICTED_QUERY_FAILED");
        return new(Hash(user.Value), user.Value.StartsWith("S-1-5-21-", StringComparison.Ordinal) && user.Value.EndsWith("-500", StringComparison.Ordinal),
            Hash(owner), UInt(token, 12), checked((int)UInt(token, 8)), checked((int)UInt(token, 18)), Flag(token, 20),
            uint.Parse(integrity.AsSpan(integrity.LastIndexOf('-') + 1)), Flag(token, 21), restricted,
            Flag(token, 26), Flag(token, 15), Flag(token, 29), UInt(token, 27),
            groups.Any(g => g.well_known_types.Contains(nameof(WellKnownSidType.BuiltinAdministratorsSid)) && (g.attributes & 4) != 0 && (g.attributes & 0x10) == 0),
            groups, restrictions, ReadPrivileges(token), Hash(statistics.TokenId.ToString()), Hash(statistics.AuthenticationId.ToString()), Hash(statistics.ModifiedId.ToString()), ReadDefaultDacl(token));
    }

    private static uint UInt(SafeAccessTokenHandle token, int kind)
    {
        using var data = Query(token, kind);
        if (data.Length != 4) throw new InvalidDataException("TOKEN_DWORD_SIZE_INVALID_CLASS_" + kind);
        return unchecked((uint)Marshal.ReadInt32(data.Pointer));
    }

    private static bool Flag(SafeAccessTokenHandle token, int kind)
    {
        using var data = Query(token, kind);
        // Some Windows versions return the native BOOLEAN representation for
        // TokenSandBoxInert. Never read beyond the buffer returned by the API.
        uint value = data.Length switch
        {
            1 => Marshal.ReadByte(data.Pointer),
            4 => unchecked((uint)Marshal.ReadInt32(data.Pointer)),
            _ => throw new InvalidDataException("TOKEN_BOOLEAN_SIZE_INVALID_CLASS_" + kind)
        };
        if (value > 1) throw new InvalidDataException("TOKEN_BOOLEAN_VALUE_INVALID_CLASS_" + kind);
        return value == 1;
    }

    private static SidFact[] ReadGroups(SafeAccessTokenHandle token, int kind)
    {
        using var data = Query(token, kind);
        int count = Marshal.ReadInt32(data.Pointer), offset = Marshal.OffsetOf<GroupHeader>(nameof(GroupHeader.First)).ToInt32();
        int size = Marshal.SizeOf<SidAndAttributes>();
        if (count < 0 || count > 4096 || (count != 0 && offset + (long)count * size > data.Length)) throw new InvalidDataException("TOKEN_GROUPS_SIZE_INVALID");
        var result = new SidFact[count];
        for (int i = 0; i < count; i++)
        {
            var entry = Marshal.PtrToStructure<SidAndAttributes>(data.Pointer + offset + i * size);
            var sid = new SecurityIdentifier(entry.Sid);
            var types = new List<string>();
            foreach (WellKnownSidType type in Enum.GetValues<WellKnownSidType>())
            {
                try { if (sid.IsWellKnown(type)) types.Add(type.ToString()); }
                catch (ArgumentException) { }
            }
            result[i] = new(Hash(sid.Value), types.ToArray(), entry.Attributes);
        }
        return result.OrderBy(s => s.sid_sha256, StringComparer.Ordinal).ToArray();
    }

    private static PrivilegeFact[] ReadPrivileges(SafeAccessTokenHandle token)
    {
        using var data = Query(token, 3);
        int count = Marshal.ReadInt32(data.Pointer), size = Marshal.SizeOf<LuidAndAttributes>();
        if (count < 0 || count > 4096 || 4 + (long)count * size > data.Length) throw new InvalidDataException("TOKEN_PRIVILEGES_SIZE_INVALID");
        var result = new List<PrivilegeFact>();
        for (int i = 0; i < count; i++)
        {
            var item = Marshal.PtrToStructure<LuidAndAttributes>(data.Pointer + 4 + i * size);
            var name = new StringBuilder(256); uint length = (uint)name.Capacity;
            if (!LookupPrivilegeName(null, ref item.Luid, name, ref length)) throw Error("PRIVILEGE_NAME_QUERY_FAILED");
            result.Add(new(name.ToString(), item.Attributes));
        }
        return result.OrderBy(p => p.name, StringComparer.Ordinal).ToArray();
    }

    internal static void SetMedium(SafeAccessTokenHandle independentCopy)
    {
        var sid = new SecurityIdentifier("S-1-16-8192");
        int headerSize = Marshal.SizeOf<SidAndAttributes>();
        using var memory = new Buffer(headerSize + sid.BinaryLength);
        byte[] bytes = new byte[sid.BinaryLength]; sid.GetBinaryForm(bytes, 0);
        Marshal.Copy(bytes, 0, memory.Pointer + headerSize, bytes.Length);
        Marshal.StructureToPtr(new SidAndAttributes { Sid = memory.Pointer + headerSize, Attributes = 0x20 }, memory.Pointer, false);
        if (!SetTokenInformation(independentCopy, 25, memory.Pointer, (uint)memory.Length)) throw Error("SET_ONLY_COPY_INTEGRITY_MEDIUM_FAILED");
    }

    private static Buffer Query(SafeAccessTokenHandle token, int kind)
    {
        GetTokenInformation(token, kind, IntPtr.Zero, 0, out uint required);
        if (required == 0 || required > 1048576) throw Error("TOKEN_INFORMATION_SIZE_FAILED");
        var memory = new Buffer(checked((int)required));
        if (!GetTokenInformation(token, kind, memory.Pointer, required, out uint actual) || actual > required)
        { var error = Error("TOKEN_INFORMATION_QUERY_FAILED"); memory.Dispose(); throw error; }
        return memory;
    }

    private sealed class Buffer : IDisposable
    {
        internal IntPtr Pointer { get; }
        internal int Length { get; }
        internal Buffer(int length) { Length = length; Pointer = Marshal.AllocHGlobal(length); }
        public void Dispose() => Marshal.FreeHGlobal(Pointer);
    }
    [StructLayout(LayoutKind.Sequential)] private struct SidAndAttributes { internal IntPtr Sid; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct GroupHeader { internal uint Count; internal SidAndAttributes First; }
    [StructLayout(LayoutKind.Sequential)] private struct Luid { internal uint Low; internal int High; public override string ToString() => High.ToString("X8") + Low.ToString("X8"); }
    [StructLayout(LayoutKind.Sequential)] private struct LuidAndAttributes { internal Luid Luid; internal uint Attributes; }
    [StructLayout(LayoutKind.Sequential)] private struct Statistics { internal Luid TokenId, AuthenticationId; internal long ExpirationTime; internal int TokenType, ImpersonationLevel; internal uint DynamicCharged, DynamicAvailable, GroupCount, PrivilegeCount; internal Luid ModifiedId; }
    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool CreateRestrictedToken(SafeAccessTokenHandle source, uint flags, uint disableCount, IntPtr disabled, uint deleteCount, IntPtr deleted, uint restrictCount, IntPtr restricted, out SafeAccessTokenHandle result);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr data, uint length, out uint required);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr data, uint length);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeName(string? system, ref Luid luid, StringBuilder name, ref uint length);
}
