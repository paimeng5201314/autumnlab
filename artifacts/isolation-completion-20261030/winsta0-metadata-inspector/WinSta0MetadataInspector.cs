using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace AutumnOS.ReadOnlyWinSta0Metadata
{
    // No entry point: the separate reviewed PowerShell driver invokes Inspect.
    // There are no object-creation, ACL-write, clipboard, input or process-launch
    // imports. Descriptors and nonmatching identities remain transient memory.
    public static class Inspector
    {
        private sealed class InspectionFailure : Exception
        {
            internal readonly string Stage;
            internal readonly uint NativeError;
            internal InspectionFailure(string stage, uint error) { Stage = stage; NativeError = error; }
        }
        private const uint ReadControl = 0x00020000, PrivateMask = 0x00000023;
        private static readonly SecurityIdentifier AllPackages = new SecurityIdentifier("S-1-15-2-1");
        private static readonly SecurityIdentifier RestrictedPackages = new SecurityIdentifier("S-1-15-2-2");

        public static Dictionary<string, object> Inspect()
        {
            var failures = new List<object>(); var selected = new List<object>(); var labels = new List<object>();
            var result = new Dictionary<string, object>
            {
                ["schema_version"] = 1, ["producer"] = "派蒙", ["kind"] = "readonly_existing_WinSta0_filtered_security_metadata",
                ["status"] = "not_run", ["target"] = "WinSta0", ["requested_access"] = "0x00020000", ["inherit_handle"] = false,
                ["security_information"] = "DACL_SECURITY_INFORMATION|LABEL_SECURITY_INFORMATION (0x14)",
                ["selected_builtin_aces"] = selected, ["mandatory_labels"] = labels, ["failures"] = failures,
                ["private_station_comparison_mask"] = "0x00000023",
                ["interpretation"] = "filtered_ACE_metadata_only_not_token_effective_access_or_USER32_causality_proof",
                ["user_SID_or_full_SDDL_recorded"] = false, ["full_descriptor_bytes_or_hash_recorded"] = false,
                ["ACL_write_calls"] = 0, ["privilege_adjustment_calls"] = 0, ["UAC_requests"] = 0,
                ["clipboard_operations"] = "not_run", ["profile_operations"] = "not_run", ["desktop_operations"] = "not_run",
                ["station_creation_calls"] = 0, ["station_switch_calls"] = 0, ["child_process_launches"] = 0,
                ["isolation_acceptance"] = "not_claimed", ["owned_station_handle_closed"] = false,
                ["borrowed_current_station_handle_closed"] = false, ["current_station_unchanged"] = false
            };
            IntPtr original = IntPtr.Zero, opened = IntPtr.Zero, descriptor = IntPtr.Zero;
            try
            {
                if (IntPtr.Size != 8 || Environment.OSVersion.Platform != PlatformID.Win32NT) throw new InspectionFailure("windows_x64_required", 0);
                ReadOrdinaryIdentity(result);
                original = GetProcessWindowStation();
                if (original == IntPtr.Zero || Name(original) != "WinSta0") throw new InspectionFailure("current_station_must_be_WinSta0", 0);
                opened = OpenWindowStationW("WinSta0", false, ReadControl);
                if (opened == IntPtr.Zero) throw new InspectionFailure("OpenWindowStation_READ_CONTROL_only", unchecked((uint)Marshal.GetLastWin32Error()));
                uint error = GetSecurityInfo(opened, 7 /* SE_WINDOW_OBJECT */, 0x14 /* DACL + mandatory label only */,
                    IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out descriptor);
                result["GetSecurityInfo_native_error"] = error;
                if (error != 0) throw new InspectionFailure("GetSecurityInfo_DACL_and_LABEL", error);
                if (descriptor == IntPtr.Zero) throw new InspectionFailure("descriptor_required", 0);
                uint count = GetSecurityDescriptorLength(descriptor);
                if (count < 20 || count > 131072) throw new InspectionFailure("bounded_descriptor_size_required", 0);
                byte[] bytes = new byte[count]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
                var sd = new RawSecurityDescriptor(bytes, 0);
                result["dacl_present"] = (sd.ControlFlags & ControlFlags.DiscretionaryAclPresent) != 0;
                result["dacl_null"] = sd.DiscretionaryAcl == null;
                result["dacl_protected"] = (sd.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0;
                if (sd.DiscretionaryAcl != null)
                {
                    if (sd.DiscretionaryAcl.Count > 4096) throw new InspectionFailure("bounded_DACL_required", 0);
                    for (int index = 0; index < sd.DiscretionaryAcl.Count; index++)
                    {
                        GenericAce generic = sd.DiscretionaryAcl[index]; KnownAce ace = generic as KnownAce;
                        if (ace == null) continue;
                        string builtin = ace.SecurityIdentifier.Equals(AllPackages) ? "ALL_APPLICATION_PACKAGES" :
                            ace.SecurityIdentifier.Equals(RestrictedPackages) ? "ALL_RESTRICTED_APPLICATION_PACKAGES" : null;
                        if (builtin == null) continue; // Never stringify/translate other trustee SIDs.
                        uint mask = unchecked((uint)ace.AccessMask), mapped = MapInteractiveGeneric(mask);
                        QualifiedAce qualified = ace as QualifiedAce;
                        selected.Add(new Dictionary<string, object>
                        {
                            ["builtin_group"] = builtin, ["builtin_sid"] = builtin == "ALL_APPLICATION_PACKAGES" ? "S-1-15-2-1" : "S-1-15-2-2",
                            ["ace_index"] = index, ["ace_type"] = ace.AceType.ToString(), ["ace_flags"] = (byte)ace.AceFlags,
                            ["qualifier"] = qualified == null ? "not_a_qualified_ACE" : qualified.AceQualifier.ToString(),
                            ["callback"] = qualified != null && qualified.IsCallback, ["object_scoped"] = ace is ObjectAce,
                            ["raw_mask"] = Hex(mask), ["interactive_generic_mapped_mask"] = Hex(mapped),
                            ["mapped_bits_outside_private_0x23"] = Hex(mapped & ~PrivateMask), ["mapped_rights"] = Rights(mapped)
                        });
                    }
                }
                if (sd.SystemAcl != null)
                {
                    if (sd.SystemAcl.Count > 32) throw new InspectionFailure("bounded_label_ACL_required", 0);
                    foreach (GenericAce ace in sd.SystemAcl)
                    {
                        byte[] label = new byte[ace.BinaryLength]; ace.GetBinaryForm(label, 0);
                        // A mandatory-label ACE is header4 + mask4 + SID12;
                        // SID revision1, one subauthority, mandatory authority16.
                        if (label.Length != 20 || label[0] != 0x11 || label[8] != 1 || label[9] != 1 ||
                            label[10] != 0 || label[11] != 0 || label[12] != 0 || label[13] != 0 || label[14] != 0 || label[15] != 16)
                            throw new InspectionFailure("only_standard_mandatory_label_metadata_expected", 0);
                        uint policy = BitConverter.ToUInt32(label, 4), rid = BitConverter.ToUInt32(label, 16);
                        labels.Add(new Dictionary<string, object> { ["integrity_rid"] = rid, ["label"] = IntegrityName(rid),
                            ["ace_flags"] = label[1], ["policy_mask"] = Hex(policy), ["no_write_up"] = (policy & 1) != 0,
                            ["no_read_up"] = (policy & 2) != 0, ["no_execute_up"] = (policy & 4) != 0 });
                    }
                }
                result["mandatory_label_count"] = labels.Count;
                result["selected_builtin_ACE_count"] = selected.Count;
                result["status"] = "completed_readonly_filtered_metadata";
            }
            catch (Exception ex) { Failure(failures, ex); result["status"] = "failed_readonly_metadata"; }
            finally
            {
                if (descriptor != IntPtr.Zero)
                {
                    IntPtr failed = LocalFree(descriptor);
                    if (failed != IntPtr.Zero) { failures.Add(new { stage = "LocalFree_owned_descriptor", native_error = unchecked((uint)Marshal.GetLastWin32Error()) }); result["status"] = "failed_readonly_metadata"; }
                }
                if (opened != IntPtr.Zero)
                {
                    bool closed = CloseWindowStation(opened); result["owned_station_handle_closed"] = closed;
                    if (!closed) { failures.Add(new { stage = "CloseWindowStation_owned_readonly_handle", native_error = unchecked((uint)Marshal.GetLastWin32Error()) }); result["status"] = "failed_readonly_metadata"; }
                }
                if (original != IntPtr.Zero)
                {
                    try
                    {
                        IntPtr after = GetProcessWindowStation(); bool unchanged = original == after && Name(after) == "WinSta0";
                        result["current_station_unchanged"] = unchanged;
                        if (!unchanged) throw new InspectionFailure("original_current_station_changed", 0);
                    }
                    catch (Exception ex) { Failure(failures, ex); result["status"] = "failed_readonly_metadata"; }
                }
            }
            return result;
        }
        private static void ReadOrdinaryIdentity(Dictionary<string, object> result)
        {
            IntPtr token;
            if (!OpenProcessToken(GetCurrentProcess(), 8, out token)) throw new InspectionFailure("OpenProcessToken_QUERY_only", unchecked((uint)Marshal.GetLastWin32Error()));
            try
            {
                int elevated = TokenInt(token, 20), ac = TokenInt(token, 29);
                result["ordinary_parent_elevated"] = elevated; result["ordinary_parent_is_appcontainer"] = ac;
                if (elevated != 0 || ac != 0) throw new InspectionFailure("unelevated_non_AppContainer_required", 0);
            }
            finally { if (!CloseHandle(token)) throw new InspectionFailure("CloseHandle_owned_query_token", unchecked((uint)Marshal.GetLastWin32Error())); }
        }
        private static int TokenInt(IntPtr token, int kind)
        {
            int value; uint returned;
            if (!GetTokenInformation(token, kind, out value, 4, out returned) || returned != 4)
                throw new InspectionFailure("GetTokenInformation_fixed_integer", unchecked((uint)Marshal.GetLastWin32Error()));
            return value;
        }
        private static string Name(IntPtr station)
        {
            var value = new StringBuilder(64); uint required;
            if (!GetUserObjectInformationW(station, 2, value, value.Capacity * 2, out required))
                throw new InspectionFailure("current_station_name_metadata", unchecked((uint)Marshal.GetLastWin32Error()));
            if (required > value.Capacity * 2) throw new InspectionFailure("bounded_station_name_required", 0);
            return value.ToString();
        }
        private static void Failure(List<object> failures, Exception ex)
        {
            var specific = ex as InspectionFailure;
            failures.Add(new { stage = specific == null ? "managed_metadata_parse" : specific.Stage,
                exception_type = ex.GetType().Name, hresult = Hex(unchecked((uint)ex.HResult)), native_error = specific == null ? 0 : specific.NativeError });
        }
        private static uint MapInteractiveGeneric(uint mask)
        {
            uint value = mask & 0x0fffffff;
            if ((mask & 0x80000000) != 0) value |= 0x00020303;
            if ((mask & 0x40000000) != 0) value |= 0x0002001c;
            if ((mask & 0x20000000) != 0) value |= 0x00020060;
            if ((mask & 0x10000000) != 0) value |= 0x000f037f;
            return value;
        }
        private static string[] Rights(uint mask)
        {
            uint[] bits = { 1, 2, 4, 8, 0x10, 0x20, 0x40, 0x100, 0x200, 0x10000, 0x20000, 0x40000, 0x80000 };
            string[] names = { "ENUMDESKTOPS", "READATTRIBUTES", "ACCESSCLIPBOARD", "CREATEDESKTOP", "WRITEATTRIBUTES", "ACCESSGLOBALATOMS", "EXITWINDOWS", "ENUMERATE", "READSCREEN", "DELETE", "READ_CONTROL", "WRITE_DAC", "WRITE_OWNER" };
            var selected = new List<string>(); for (int i = 0; i < bits.Length; i++) if ((mask & bits[i]) != 0) selected.Add(names[i]); return selected.ToArray();
        }
        private static string IntegrityName(uint rid)
        { switch (rid) { case 0: return "untrusted"; case 4096: return "low"; case 8192: return "medium"; case 12288: return "high"; case 16384: return "system"; default: return "other_mandatory_integrity_rid"; } }
        private static string Hex(uint value) { return "0x" + value.ToString("x8"); }

        [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr LocalFree(IntPtr memory);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(IntPtr token, int kind, out int value, uint size, out uint returned);
        [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenWindowStationW(string name, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseWindowStation(IntPtr station);
        [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformationW(IntPtr value, int index, StringBuilder buffer, int bytes, out uint required);
        [DllImport("advapi32.dll")] private static extern uint GetSecurityInfo(IntPtr handle, int kind, uint information, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")] private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    }
}
