// Diagnostic draft: AccessCheck in memory only. No token is ever attached to a
// thread/process; no child, GUI, clipboard, station/desktop or ACL mutation APIs.
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.RestrictedTokenAccessCheck;
internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        if(args.Length!=4||args[0]!="--workspace"||args[2]!="--report")return 2;
        string workspace=Path.GetFullPath(args[1]),report=Path.GetFullPath(args[3]);
        if(!report.StartsWith(Path.Combine(workspace,"artifacts")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)||File.Exists(report))return 2;
        string failedProbe=Path.Combine(workspace,"artifacts","isolation-20261018","drafts","RestrictedTokenLaunchProbe","bin","x64","Release","net10.0-windows","win-x64","AutumnOS.RestrictedTokenLaunchProbe.exe");
        if(!File.Exists(failedProbe)||Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(failedProbe)))!="5a6ddd657fb62d8b0218ae52ccb81a449281ca77025d60c4d07e3cef9c196021")return 2;
        var started=DateTimeOffset.UtcNow;var tokens=new List<object>();var objects=new List<object>();object? failure=null;string status="failed";
        try{
            Native.RequireNoThreadImpersonation();
            using var own=Native.OwnToken();TokenFacts original=Native.Facts(own);
            if(original.type!=1||original.elevated||original.integrity_rid!=0x2000||original.restricted)throw new InvalidOperationException("REQUIRE_ORDINARY_UNRESTRICTED_MEDIUM_PRIMARY");
            using var a=Native.QueryImpersonationCopy(own);
            using var bPrimary=Native.RestrictedCopy(own,0x1);
            using var b=Native.QueryImpersonationCopy(bPrimary);
            using var cPrimary=Native.RestrictedCopy(own,0x9);
            using var c=Native.QueryImpersonationCopy(cPrimary);
            var cases=new[]{new TokenCase("A_original_query_copy",a,0),new TokenCase("B_DisableMax_RestrictedCode",b,1),new TokenCase("C_WriteRestricted_diagnostic_only",c,9)};
            foreach(var token in cases){var facts=Native.Facts(token.Handle);if(facts.type!=2||facts.elevated||facts.integrity_rid!=0x2000||facts.user_sid_sha256!=original.user_sid_sha256||facts.session!=original.session)throw new InvalidOperationException("QUERY_TOKEN_IDENTITY_MISMATCH");
                if(token.Flags!=0&&(!facts.restricted||!facts.restricting_sids.SequenceEqual(new[]{"S-1-5-12"})||facts.enabled_privileges.Any(p=>p!="SeChangeNotifyPrivilege")))throw new InvalidOperationException("QUERY_TOKEN_RESTRICTION_MISMATCH");
                tokens.Add(new{label=token.Label,requested_flags="0x"+token.Flags.ToString("X"),facts,attached_to_thread_or_process=false});}
            IntPtr station=Native.GetProcessWindowStation(),desktop=Native.GetThreadDesktop(Native.GetCurrentThreadId());
            if(station==IntPtr.Zero||desktop==IntPtr.Zero||!Native.UserObjectName(station).Equals("WinSta0",StringComparison.OrdinalIgnoreCase)||!Native.UserObjectName(desktop).Equals("Default",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("REQUIRE_EXISTING_OWN_WINSTA0_DEFAULT_NO_ASSOCIATION_CHANGE");
            CheckObject("current_WinSta0","window_station",()=>Native.UserDescriptor(station),Native.StationMapping,StationRights,cases,objects);
            CheckObject("current_Default","desktop",()=>Native.UserDescriptor(desktop),Native.DesktopMapping,DesktopRights,cases,objects);
            var files=new List<(string Label,string Path)>{("failed_apphost",failedProbe),("failed_managed_assembly",Path.ChangeExtension(failedProbe,".dll"))};
            foreach(string module in new[]{"ntdll.dll","kernel32.dll","kernelbase.dll","user32.dll","shell32.dll","advapi32.dll","ucrtbase.dll"})files.Add(("System32_"+module,Path.Combine(Environment.SystemDirectory,module)));
            files.Add(("local_dotnet_hostfxr_10.0.12",Path.Combine(workspace,".tools","dotnet","host","fxr","10.0.12","hostfxr.dll")));
            foreach(string module in new[]{"hostpolicy.dll","coreclr.dll","System.Private.CoreLib.dll"})files.Add(("local_dotnet_10.0.12_"+module,Path.Combine(workspace,".tools","dotnet","shared","Microsoft.NETCore.App","10.0.12",module)));
            foreach(var file in files)CheckObject(file.Label,"file",()=>Native.FileDescriptor(file.Path),Native.FileMapping,FileRights,cases,objects);
            Native.RequireNoThreadImpersonation();
            if(Native.Facts(own)!=original){ // Records contain arrays, so compare serialized values rather than reference identity.
                if(JsonSerializer.Serialize(Native.Facts(own))!=JsonSerializer.Serialize(original))throw new InvalidOperationException("ORIGINAL_TOKEN_CHANGED");}
            if(Native.GetProcessWindowStation()!=station||Native.GetThreadDesktop(Native.GetCurrentThreadId())!=desktop)throw new InvalidOperationException("ASSOCIATION_CHANGED");
            status="completed_readonly_diagnostics";
        }catch(Exception error){failure=Failure(error);}
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        File.WriteAllText(report,JsonSerializer.Serialize(new{schema_version=1,status,started_utc=started,finished_utc=DateTimeOffset.UtcNow,os=Environment.OSVersion.VersionString,
            tokens,objects,failure,meaning="AccessCheck results are hypothetical access to a captured descriptor. Denied is not a root-cause trace; allowed is not real object/API or browser acceptance.",
            write_restricted="Third in-memory diagnostic token only; not a proposed verified production boundary.",
            clipboard="not_run; no calls",browser="not_run",child_launch="none",impersonation="none",token_attachment="none",acl_writes="none",station_desktop_changes="none",UAC="none",sacl="not_requested; mandatory integrity and other kernel policy not fully modeled"},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(report);return failure is null?0:1;
    }
    private sealed record TokenCase(string Label,SafeAccessTokenHandle Handle,uint Flags);
    private sealed record Right(string Name,uint Mask);
    private static readonly Right[] StationRights=[new("MAXIMUM_ALLOWED",0x02000000),new("READ_CONTROL",0x20000),new("WINSTA_ENUMDESKTOPS",1),new("WINSTA_READATTRIBUTES",2),new("WINSTA_ACCESSCLIPBOARD",4),new("WINSTA_CREATEDESKTOP",8),new("WINSTA_WRITEATTRIBUTES",0x10),new("WINSTA_ACCESSGLOBALATOMS",0x20),new("WINSTA_EXITWINDOWS",0x40),new("WINSTA_ENUMERATE",0x100),new("WINSTA_READSCREEN",0x200),new("GENERIC_READ",0x80000000),new("GENERIC_WRITE",0x40000000),new("GENERIC_EXECUTE",0x20000000),new("GENERIC_ALL",0x10000000)];
    private static readonly Right[] DesktopRights=[new("MAXIMUM_ALLOWED",0x02000000),new("READ_CONTROL",0x20000),new("DESKTOP_READOBJECTS",1),new("DESKTOP_CREATEWINDOW",2),new("DESKTOP_CREATEMENU",4),new("DESKTOP_HOOKCONTROL",8),new("DESKTOP_JOURNALRECORD",0x10),new("DESKTOP_JOURNALPLAYBACK",0x20),new("DESKTOP_ENUMERATE",0x40),new("DESKTOP_WRITEOBJECTS",0x80),new("DESKTOP_SWITCHDESKTOP",0x100),new("GENERIC_READ",0x80000000),new("GENERIC_WRITE",0x40000000),new("GENERIC_EXECUTE",0x20000000),new("GENERIC_ALL",0x10000000)];
    private static readonly Right[] FileRights=[new("MAXIMUM_ALLOWED",0x02000000),new("FILE_READ_DATA",1),new("FILE_READ_EA",8),new("FILE_EXECUTE",0x20),new("FILE_READ_ATTRIBUTES",0x80),new("READ_CONTROL",0x20000),new("FILE_GENERIC_READ",0x80000000),new("FILE_GENERIC_EXECUTE",0x20000000),new("FILE_GENERIC_READ_EXECUTE",0xA0000000),new("FILE_WRITE_DATA_control",2)];
    private static void CheckObject(string label,string kind,Func<byte[]> getDescriptor,Native.GenericMapping mapping,Right[] rights,TokenCase[] tokens,List<object> results)
    {
        try{
            byte[] sd=getDescriptor();var raw=new RawSecurityDescriptor(sd,0);
            if(raw.Owner is null||raw.Group is null)throw new InvalidDataException("DESCRIPTOR_OWNER_OR_GROUP_MISSING");
            var acl=new List<object>();if(raw.DiscretionaryAcl is not null)foreach(GenericAce ace in raw.DiscretionaryAcl){var known=ace as KnownAce;acl.Add(new{ace_type=ace.AceType.ToString(),ace_flags=ace.AceFlags.ToString(),mask=known is null?null:"0x"+unchecked((uint)known.AccessMask).ToString("X8"),sid=known is null?null:SafeSid(known.SecurityIdentifier.Value)});}
            var checks=new List<object>();foreach(var right in rights){uint mapped=right.Mask;Native.MapGenericMask(ref mapped,ref mapping);foreach(var token in tokens)checks.Add(new{right=right.Name,requested_mask="0x"+right.Mask.ToString("X8"),mapped_mask="0x"+mapped.ToString("X8"),token=token.Label,result=Native.Check(sd,token.Handle,mapped,mapping)});}
            results.Add(new{label,kind,status="descriptor_read_and_accesscheck_attempted",descriptor_sha256=Convert.ToHexStringLower(SHA256.HashData(sd)),owner=SafeSid(raw.Owner.Value),group=SafeSid(raw.Group.Value),dacl_present=(raw.ControlFlags&ControlFlags.DiscretionaryAclPresent)!=0,dacl_null=raw.DiscretionaryAcl is null,ace=acl,
                generic_mapping=new{read="0x"+mapping.Read.ToString("X8"),write="0x"+mapping.Write.ToString("X8"),execute="0x"+mapping.Execute.ToString("X8"),all="0x"+mapping.All.ToString("X8")},checks});
        }catch(Exception error){results.Add(new{label,kind,status="read_or_query_failed",failure=Failure(error)});}
    }
    private static string SafeSid(string sid)=>sid is "S-1-1-0" or "S-1-5-12" or "S-1-5-18" or "S-1-5-32-544" or "S-1-5-32-545" or "S-1-5-11" or "S-1-5-4" or "S-1-5-2" or "S-1-5-6" or "S-1-5-7" or "S-1-3-0" or "S-1-3-1" or "S-1-3-4" or "S-1-15-2-1" or "S-1-15-2-2"?sid:"sha256:"+Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sid)));
    internal static object Failure(Exception error)=>new{type=error.GetType().Name,stage=error.Message,native_error=error is Win32Exception w?w.NativeErrorCode:(int?)null};
}
internal sealed record TokenFacts(string user_sid_sha256,int type,uint session,uint integrity_rid,bool elevated,bool restricted,string[] restricting_sids,string[] enabled_privileges);
internal static class Native
{
    internal static readonly GenericMapping StationMapping=new(){Read=0x20303,Write=0x2001c,Execute=0x20060,All=0xf037f};
    internal static readonly GenericMapping DesktopMapping=new(){Read=0x20041,Write=0x200be,Execute=0x20100,All=0xf01ff};
    internal static readonly GenericMapping FileMapping=new(){Read=0x120089,Write=0x120116,Execute=0x1200a0,All=0x1f01ff};
    private static Win32Exception Error(string stage)=>new(Marshal.GetLastWin32Error(),stage);
    internal static void RequireNoThreadImpersonation(){if(OpenThreadToken(GetCurrentThread(),8,true,out var token)){token.Dispose();throw new InvalidOperationException("UNEXPECTED_EXISTING_THREAD_TOKEN");}if(Marshal.GetLastWin32Error()!=1008)throw Error("THREAD_TOKEN_QUERY_FAILED");}
    internal static SafeAccessTokenHandle OwnToken(){if(!OpenProcessToken(GetCurrentProcess(),0xa,out var token))throw Error("OWN_TOKEN_QUERY_DUPLICATE_FAILED");return token;}
    internal static SafeAccessTokenHandle RestrictedCopy(SafeAccessTokenHandle own,uint flags){if(flags is not (1 or 9))throw new InvalidOperationException("UNAPPROVED_DIAGNOSTIC_FLAGS");if(!ConvertStringSidToSid("S-1-5-12",out IntPtr sid))throw Error("RESTRICTED_SID_PARSE_FAILED");try{var item=new SidAndAttributes{Sid=sid,Attributes=0};if(!CreateRestrictedToken(own,flags,0,IntPtr.Zero,0,IntPtr.Zero,1,ref item,out var token))throw Error("RESTRICTED_QUERY_TOKEN_CREATE_FAILED");return token;}finally{LocalFree(sid);}}
    internal static SafeAccessTokenHandle QueryImpersonationCopy(SafeAccessTokenHandle token){if(!DuplicateTokenEx(token,8,IntPtr.Zero,2,2,out var copy))throw Error("QUERY_ONLY_IMPERSONATION_OBJECT_CREATE_FAILED");return copy;}
    internal static TokenFacts Facts(SafeAccessTokenHandle token)
    {
        string user="";int type=0;uint session=0,integrity=0;bool elevated=false;var restricting=new List<string>();var privileges=new List<string>();
        foreach(int kind in new[]{1,8,12,25,20,11,3}){GetTokenInformation(token,kind,IntPtr.Zero,0,out int size);if(size<=0||size>1048576)throw Error("TOKEN_QUERY_SIZE_FAILED");IntPtr memory=Marshal.AllocHGlobal(size);try{if(!GetTokenInformation(token,kind,memory,size,out _))throw Error("TOKEN_QUERY_FAILED");
            if(kind==1)user=new SecurityIdentifier(Marshal.ReadIntPtr(memory)).Value;
            else if(kind==8)type=Marshal.ReadInt32(memory);
            else if(kind==12)session=unchecked((uint)Marshal.ReadInt32(memory));
            else if(kind==20)elevated=Marshal.ReadInt32(memory)!=0;
            else if(kind==25){string sid=new SecurityIdentifier(Marshal.ReadIntPtr(memory)).Value;integrity=uint.Parse(sid[(sid.LastIndexOf('-')+1)..],System.Globalization.CultureInfo.InvariantCulture);}
            else if(kind==11){int count=Marshal.ReadInt32(memory),offset=IntPtr.Size==8?8:4,entry=Marshal.SizeOf<SidAndAttributes>();if(count<0||offset+(long)count*entry>size)throw new InvalidDataException("SID_QUERY_BUFFER_INVALID");for(int i=0;i<count;i++)restricting.Add(new SecurityIdentifier(Marshal.PtrToStructure<SidAndAttributes>(memory+offset+i*entry).Sid).Value);}
            else if(kind==3){int count=Marshal.ReadInt32(memory);if(count<0||4+(long)count*12>size)throw new InvalidDataException("PRIVILEGE_BUFFER_INVALID");for(int i=0;i<count;i++){IntPtr p=memory+4+i*12;if((Marshal.ReadInt32(p+8)&2)==0)continue;var luid=Marshal.PtrToStructure<Luid>(p);var name=new StringBuilder(256);uint chars=256;if(!LookupPrivilegeName(null,ref luid,name,ref chars))throw Error("PRIVILEGE_NAME_QUERY_FAILED");privileges.Add(name.ToString());}}
        }finally{Marshal.FreeHGlobal(memory);}}
        return new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(user))),type,session,integrity,elevated,IsTokenRestricted(token),restricting.Order(StringComparer.Ordinal).ToArray(),privileges.Order(StringComparer.Ordinal).ToArray());
    }
    internal static string UserObjectName(IntPtr handle){var name=new StringBuilder(512);if(!GetUserObjectInformation(handle,2,name,name.Capacity*2,out _))throw Error("OWN_USER_OBJECT_NAME_QUERY_FAILED");return name.ToString();}
    internal static byte[] UserDescriptor(IntPtr handle){uint information=7;GetUserObjectSecurity(handle,ref information,IntPtr.Zero,0,out uint size);if(size==0||size>1048576)throw Error("USER_OBJECT_SD_SIZE_FAILED");IntPtr memory=Marshal.AllocHGlobal(checked((int)size));try{if(!GetUserObjectSecurity(handle,ref information,memory,size,out uint actual)||actual>size)throw Error("USER_OBJECT_SD_READ_FAILED");return CopyDescriptor(memory);}finally{Marshal.FreeHGlobal(memory);}}
    internal static byte[] FileDescriptor(string path){uint error=GetNamedSecurityInfo(path,1,7,out _,out _,out _,out _,out IntPtr descriptor);if(error!=0)throw new Win32Exception((int)error,"FIXED_BINARY_SD_READ_FAILED");try{return CopyDescriptor(descriptor);}finally{LocalFree(descriptor);}}
    private static byte[] CopyDescriptor(IntPtr descriptor){if(descriptor==IntPtr.Zero||!IsValidSecurityDescriptor(descriptor))throw new InvalidDataException("INVALID_CAPTURED_SECURITY_DESCRIPTOR");uint size=GetSecurityDescriptorLength(descriptor);if(size==0||size>1048576)throw new InvalidDataException("SECURITY_DESCRIPTOR_SIZE_INVALID");byte[] copy=new byte[size];Marshal.Copy(descriptor,copy,0,copy.Length);return copy;}
    internal static object Check(byte[] descriptor,SafeAccessTokenHandle token,uint desired,GenericMapping mapping)
    {
        GCHandle pinned=GCHandle.Alloc(descriptor,GCHandleType.Pinned);IntPtr privileges=Marshal.AllocHGlobal(65536);try{uint bytes=65536;bool api=AccessCheck(pinned.AddrOfPinnedObject(),token,desired,ref mapping,privileges,ref bytes,out uint granted,out bool allowed);int error=Marshal.GetLastWin32Error();return new{api_succeeded=api,access_status=api?(bool?)allowed:null,granted_access=api?"0x"+granted.ToString("X8"):null,native_error=!api||!allowed?(int?)error:null,privilege_buffer_bytes=bytes};}finally{Marshal.FreeHGlobal(privileges);pinned.Free();}
    }
    [StructLayout(LayoutKind.Sequential)]internal struct GenericMapping{public uint Read,Write,Execute,All;}
    [StructLayout(LayoutKind.Sequential)]private struct SidAndAttributes{public IntPtr Sid;public uint Attributes;}
    [StructLayout(LayoutKind.Sequential)]private struct Luid{public uint Low;public int High;}
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll")]internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(IntPtr process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenThreadToken(IntPtr thread,uint access,bool openAsSelf,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool GetTokenInformation(SafeAccessTokenHandle token,int kind,IntPtr buffer,int size,out int required);
    [DllImport("advapi32.dll")]private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool LookupPrivilegeName(string? system,ref Luid luid,StringBuilder name,ref uint chars);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool ConvertStringSidToSid(string sid,out IntPtr memory);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool CreateRestrictedToken(SafeAccessTokenHandle token,uint flags,uint disableCount,IntPtr disabled,uint deleteCount,IntPtr deleted,uint restrictCount,ref SidAndAttributes restricted,out SafeAccessTokenHandle result);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool DuplicateTokenEx(SafeAccessTokenHandle token,uint access,IntPtr attributes,int impersonationLevel,int type,out SafeAccessTokenHandle result);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool AccessCheck(IntPtr sd,SafeAccessTokenHandle token,uint desired,ref GenericMapping mapping,IntPtr privileges,ref uint size,out uint granted,[MarshalAs(UnmanagedType.Bool)]out bool allowed);
    [DllImport("advapi32.dll")]internal static extern void MapGenericMask(ref uint mask,ref GenericMapping mapping);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode)]private static extern uint GetNamedSecurityInfo(string path,int objectType,uint information,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll")]private static extern bool IsValidSecurityDescriptor(IntPtr descriptor);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("user32.dll",SetLastError=true)]internal static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll",SetLastError=true)]internal static extern IntPtr GetThreadDesktop(uint threadId);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool GetUserObjectInformation(IntPtr handle,int kind,StringBuilder value,int size,out int required);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool GetUserObjectSecurity(IntPtr handle,ref uint information,IntPtr descriptor,uint size,out uint required);
}
