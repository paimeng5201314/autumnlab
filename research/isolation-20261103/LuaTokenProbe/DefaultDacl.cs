using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.LuaTokenProbe;

internal sealed record DaclAceFact(byte ace_type, byte ace_flags, uint? access_mask, string? sid_sha256, string? well_known_sid, string binary_sha256);
internal sealed record DefaultDaclFact(bool null_dacl, byte revision, int binary_length, string? binary_sha256, DaclAceFact[] aces);

internal static partial class Native
{
    internal static DefaultDaclFact ReadDefaultDacl(SafeAccessTokenHandle token)
    {
        using var data = Query(token, 6);
        if (data.Length < IntPtr.Size) throw new InvalidDataException("TOKEN_DEFAULT_DACL_HEADER_INVALID");
        IntPtr pointer = Marshal.ReadIntPtr(data.Pointer);
        if (pointer == IntPtr.Zero) return new(true, 0, 0, null, []);
        long offset = pointer.ToInt64() - data.Pointer.ToInt64();
        if (offset < IntPtr.Size || offset + 8 > data.Length) throw new InvalidDataException("TOKEN_DEFAULT_DACL_POINTER_OUTSIDE_BUFFER");
        int length = unchecked((ushort)Marshal.ReadInt16(pointer + 2));
        if (length < 8 || offset + length > data.Length) throw new InvalidDataException("TOKEN_DEFAULT_DACL_LENGTH_INVALID");
        byte[] bytes = new byte[length]; Marshal.Copy(pointer, bytes, 0, length);
        var acl = new RawAcl(bytes, 0);
        var entries = new List<DaclAceFact>();
        foreach (GenericAce ace in acl)
        {
            byte[] encoded = new byte[ace.BinaryLength]; ace.GetBinaryForm(encoded, 0);
            KnownAce? known = ace as KnownAce;
            string? sid = known?.SecurityIdentifier.Value;
            entries.Add(new((byte)ace.AceType, (byte)ace.AceFlags, known is null ? null : unchecked((uint)known.AccessMask),
                sid is null ? null : Hash(sid), sid is "S-1-5-18" or "S-1-5-32-544" ? sid : null,
                Convert.ToHexStringLower(SHA256.HashData(encoded))));
        }
        return new(false, acl.Revision, length, Convert.ToHexStringLower(SHA256.HashData(bytes)), entries.ToArray());
    }

    internal static void SetCopyPrivateObjectDefaultDacl(SafeAccessTokenHandle independentCopy)
    {
        TokenFacts before = Read(independentCopy);
        // Refuse both an elevated/unsuitable token and any handle identifying
        // the actual process token, even if a caller accidentally passes it.
        using SafeAccessTokenHandle own = OpenOwnQueryToken();
        TokenFacts originalBefore = Read(own);
        if (before.token_type != 1 || before.integrity_rid != 0x2000 || before.elevated || before.administrators_enabled ||
            !before.has_restrictions || before.owner_sid_sha256 != before.user_sid_sha256 || before.ui_access || before.sandbox_inert ||
            before.is_appcontainer || before.is_token_restricted || before.restricting_sids.Length != 0 ||
            before.user_sid_sha256 != originalBefore.user_sid_sha256 || before.session_id != originalBefore.session_id ||
            before.token_id_sha256 == originalBefore.token_id_sha256)
            throw new InvalidOperationException("DEFAULT_DACL_REPAIR_REQUIRES_INDEPENDENT_OWN_NONADMIN_FILTERED_MEDIUM_PRIMARY_WITH_USER_OWNER");
        if (before.privileges.Any(p => (p.attributes & 2) != 0 && p.name != "SeChangeNotifyPrivilege"))
            throw new InvalidOperationException("DEFAULT_DACL_REPAIR_UNEXPECTED_ENABLED_PRIVILEGE");

        using var userData = Query(independentCopy, 1);
        var user = new SecurityIdentifier(Marshal.ReadIntPtr(userData.Pointer));
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var acl = new RawAcl(GenericAcl.AclRevision, 2);
        acl.InsertAce(0, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x10000000, user, false, null));
        acl.InsertAce(1, new CommonAce(AceFlags.None, AceQualifier.AccessAllowed, 0x10000000, system, false, null));
        byte[] bytes = new byte[acl.BinaryLength]; acl.GetBinaryForm(bytes, 0);
        using var memory = new Buffer(IntPtr.Size + bytes.Length);
        Marshal.WriteIntPtr(memory.Pointer, memory.Pointer + IntPtr.Size);
        Marshal.Copy(bytes, 0, memory.Pointer + IntPtr.Size, bytes.Length);
        if (!SetTokenInformation(independentCopy, 6, memory.Pointer, (uint)IntPtr.Size))
            throw Error("SET_ONLY_COPY_PRIVATE_OBJECT_DEFAULT_DACL_FAILED");
        DefaultDaclFact observed = ReadDefaultDacl(independentCopy);
        if (observed.null_dacl || observed.revision != acl.Revision || observed.binary_length != bytes.Length ||
            observed.binary_sha256 != Convert.ToHexStringLower(SHA256.HashData(bytes)) || observed.aces.Length != 2 ||
            observed.aces.Any(a => a.ace_type != (byte)AceType.AccessAllowed || a.ace_flags != 0 || a.access_mask != 0x10000000) ||
            observed.aces[0].sid_sha256 != before.user_sid_sha256 || observed.aces[1].sid_sha256 != Hash(system.Value))
            throw new InvalidDataException("COPY_PRIVATE_OBJECT_DEFAULT_DACL_READBACK_MISMATCH");
        TokenFacts after = Read(independentCopy);
        TokenFacts originalAfter = Read(own);
        if (JsonSerializer.Serialize(originalBefore) != JsonSerializer.Serialize(originalAfter))
            throw new InvalidOperationException("ORIGINAL_PROCESS_TOKEN_CHANGED_DURING_COPY_DEFAULT_DACL_REPAIR");
        // DefaultDacl and ModifiedId are the only expected changes.
        if (JsonSerializer.Serialize(before with { default_dacl = after.default_dacl, modified_id_sha256 = after.modified_id_sha256 }) != JsonSerializer.Serialize(after))
            throw new InvalidOperationException("COPY_OTHER_TOKEN_FACTS_CHANGED_DURING_DEFAULT_DACL_REPAIR");
    }

    private static SafeAccessTokenHandle OpenOwnQueryToken()
    {
        if (!OpenProcessToken(GetCurrentProcess(), 8, out var own)) throw Error("DEFAULT_DACL_ORIGINAL_QUERY_FAILED");
        return own;
    }
}
