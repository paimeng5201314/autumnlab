// REVIEW DRAFT: compile only until the user authorizes this specific process-token trial.
// Never calls clipboard APIs, SetProcessWindowStation, SetThreadDesktop, ACL writes,
// UAC, token impersonation, process/job termination, or a browser/GUI initializer.
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace AutumnOS.RestrictedTokenLaunchProbe;
internal static class Program
{
    private const string RestrictedCodeSid="S-1-5-12";
    [MTAThread]
    private static int Main(string[] args)
    {
        Native.SetErrorMode(0x1|0x2|0x8000); // Only this process/children: no OS error dialogs.
        if(args.Length==4&&args[0]=="--child")return Child(args[1],args[2],args[3]);
        if(args.Length!=2||args[0]!="--report")return 2;
        string report=Path.GetFullPath(args[1]);if(File.Exists(report))return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var started=DateTimeOffset.UtcNow;var cases=new List<CaseResult>();object? failure=null;TokenFacts? initial=null,restrictedFacts=null;int code=1;
        try{
            // CreateRestrictedToken preserves handle access. CreateProcessAsUser requires all
            // three rights on its primary token handle; this does not adjust the caller token.
            using var original=Native.OpenOwnToken(0x0008|0x0002|0x0001); // QUERY | DUPLICATE | ASSIGN_PRIMARY.
            initial=Native.ReadToken(original);
            if(initial.elevated||initial.integrity_rid!=0x2000||initial.token_type!=1)throw new InvalidOperationException("PARENT_MUST_BE_ORDINARY_MEDIUM_PRIMARY_TOKEN");
            var a=RunCase("A-own-token-CreateProcess",null,initial);cases.Add(a);
            if(!a.passed)throw new InvalidOperationException("ORDINARY_CHILD_POSITIVE_CONTROL_FAILED_NO_RESTRICTED_LAUNCH");
            if(!Native.ConvertStringSidToSid(RestrictedCodeSid,out IntPtr restrictedSid))throw Native.Error("RESTRICTED_CODE_SID_PARSE_FAILED");
            SafeAccessTokenHandle restricted;
            try{
                var sid=new Native.SidAndAttributes{Sid=restrictedSid,Attributes=0};
                // Fixed policy, no fallback to Everyone, SANDBOX_INERT, or WRITE_RESTRICTED.
                if(!Native.CreateRestrictedToken(original,0x1,0,IntPtr.Zero,0,IntPtr.Zero,1,ref sid,out restricted))throw Native.Error("CREATE_OWN_RESTRICTED_PRIMARY_TOKEN_FAILED");
            }finally{Native.LocalFree(restrictedSid);}
            using(restricted){
                restrictedFacts=Native.ReadToken(restricted);
                if(!IsExpectedRestriction(restrictedFacts,initial))throw new InvalidOperationException("RESTRICTED_TOKEN_CONTENTS_UNEXPECTED_NO_CHILD_LAUNCH");
                var b=RunCase("B-RestrictedCode-CreateProcessAsUser",restricted,initial);cases.Add(b);
                if(!b.passed)throw new InvalidOperationException("RESTRICTED_CHILD_MECHANISM_OR_METADATA_FAILED");
            }
            code=0;
        }catch(Exception error){failure=Error(error);}
        File.WriteAllText(report,JsonSerializer.Serialize(new{schema_version=1,status=code==0?"passed":"failed",kind="ordinary_own_restricted_primary_token_launch_readonly_metadata",started_utc=started,finished_utc=DateTimeOffset.UtcNow,
            os=Environment.OSVersion.VersionString,initial_token=initial,created_restricted_token=restrictedFacts,
            requested_restriction=new{flags="DISABLE_MAX_PRIVILEGE (0x1)",restricting_sid=RestrictedCodeSid,sid_attributes=0,disabled_sid_list="none",separate_privilege_list="none"},
            cases,failure,checks=new{ordinary_child=cases.FirstOrDefault(c=>c.label.StartsWith("A-",StringComparison.Ordinal)) is { } aCase?(aCase.passed?"passed":"failed"):"not_run",
                restricted_token=restrictedFacts is null?"not_run":IsExpectedRestriction(restrictedFacts,initial!)?"passed":"failed",
                restricted_child=cases.FirstOrDefault(c=>c.label.StartsWith("B-",StringComparison.Ordinal)) is { } bCase?(bCase.passed?"passed":"failed"):"not_run"},
            scope="Only process launch and actual token/station-handle metadata. NOT WebView compatibility or clipboard isolation acceptance.",
            clipboard_isolation_acceptance="not_run",webview_compatibility="not_run",untrusted_content="not_run",
            clipboard_calls="none",station_or_desktop_association_changes="none",acl_or_global_policy_changes="none",UAC="none",kill_or_breakaway="none",fallback_on_denial="none"},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(report);return code;
    }
    private static bool IsExpectedRestriction(TokenFacts value,TokenFacts original)=>value.token_type==1&&!value.elevated&&value.integrity_rid==0x2000&&value.user_sid_sha256==original.user_sid_sha256&&value.session_id==original.session_id&&value.is_restricted&&value.restricting_sids.SequenceEqual(new[]{RestrictedCodeSid})&&value.enabled_privileges.All(name=>name=="SeChangeNotifyPrivilege");
    private static int Child(string nonce,string label,string expectedParent)
    {
        if(!System.Text.RegularExpressions.Regex.IsMatch(nonce,"\\A[0-9a-f]{32}\\z")||label is not ("A-own-token-CreateProcess" or "B-RestrictedCode-CreateProcessAsUser")||!int.TryParse(expectedParent,out int parent)||parent<=0)return 2;
        try{
            using var token=Native.OpenOwnToken(0x0008);TokenFacts facts=Native.ReadToken(token);
            if(facts.elevated||facts.integrity_rid!=0x2000||facts.token_type!=1)throw new InvalidOperationException("CHILD_TOKEN_NOT_ORDINARY_MEDIUM_PRIMARY");
            if(label.StartsWith("B-",StringComparison.Ordinal)&&(!facts.is_restricted||!facts.restricting_sids.SequenceEqual(new[]{RestrictedCodeSid})||facts.enabled_privileges.Any(name=>name!="SeChangeNotifyPrivilege")))throw new InvalidOperationException("CHILD_RESTRICTION_NOT_PRESENT");
            // Read-only handle opens: no call to OpenClipboard/Get/Set/EmptyClipboard.
            object station=Native.StationMetadata();
            Console.WriteLine(JsonSerializer.Serialize(new{schema_version=1,status="metadata_complete",nonce,label,pid=Environment.ProcessId,expected_parent_pid=parent,token=facts,station,
                clipboard_calls="none",association_changes="none",acl_changes="none",browser_or_GUI="never_initialized"}));return 0;
        }catch(Exception error){Console.WriteLine(JsonSerializer.Serialize(new{status="failed",nonce,label,pid=Environment.ProcessId,failure=Error(error)}));return 1;}
    }
    private sealed record CaseResult(string label,bool passed,bool api_started,int? api_error,int? child_pid,int? child_exit_code,bool natural_exit,string? stage,JsonElement? child_metadata,object? failure);
    private static CaseResult RunCase(string label,SafeAccessTokenHandle? token,TokenFacts original)
    {
        string nonce=Guid.NewGuid().ToString("N"),executable=Environment.ProcessPath!;
        if(Path.GetFileName(executable).Equals("dotnet.exe",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("COMPILED_APPHOST_REQUIRED");
        Native.ProcessInfo child=default;bool started=false,exited=false;int? nativeError=null,exitCode=null;JsonElement? metadata=null;object? failure=null;string stage="pipe_setup";
        using var pipes=new Native.OwnedPipes();
        try{
            var startup=pipes.StartupInfo();
            var command=new StringBuilder(string.Join(" ",new[]{executable,"--child",nonce,label,Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)}.Select(Quote)));
            stage="native_process_creation";
            started=token is null?Native.CreateProcess(executable,command,IntPtr.Zero,IntPtr.Zero,true,0x08080000,IntPtr.Zero,null,ref startup,out child):Native.CreateProcessAsUser(token,executable,command,IntPtr.Zero,IntPtr.Zero,true,0x08080000,IntPtr.Zero,null,ref startup,out child);
            if(!started){nativeError=Marshal.GetLastWin32Error();return new(label,false,false,nativeError,null,null,false,stage,null,new{reason="native_launch_denied_no_UAC_or_fallback"});}
            pipes.CloseParentWriteEnd();
            using var reader=new StreamReader(new FileStream(pipes.DetachOutputRead(),FileAccess.Read),Encoding.UTF8,detectEncodingFromByteOrderMarks:true,leaveOpen:false);
            Task<string> output=reader.ReadToEndAsync();stage="waiting_for_ordinary_natural_exit";
            uint wait=Native.WaitForSingleObject(child.Process,45000);
            if(wait!=0)throw new TimeoutException("OWNED_CHILD_NOT_EXITED_NO_FORCE_KILL");
            exited=true;if(!Native.GetExitCodeProcess(child.Process,out uint actualExit))throw Native.Error("CHILD_EXIT_CODE_QUERY_FAILED");exitCode=unchecked((int)actualExit);
            string text=output.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if(Encoding.UTF8.GetByteCount(text)>65536)throw new InvalidDataException("CHILD_OUTPUT_LIMIT_EXCEEDED");
            stage="validating_child_metadata";
            using var document=JsonDocument.Parse(text.Trim());metadata=document.RootElement.Clone();
            bool common=exitCode==0&&metadata.Value.GetProperty("status").GetString()=="metadata_complete"&&metadata.Value.GetProperty("nonce").GetString()==nonce&&metadata.Value.GetProperty("label").GetString()==label&&metadata.Value.GetProperty("pid").GetInt32()==child.ProcessId;
            if(!common)throw new InvalidDataException("CHILD_RESULT_IDENTITY_OR_COMPLETION_REJECTED");
            TokenFacts childFacts=metadata.Value.GetProperty("token").Deserialize<TokenFacts>()??throw new InvalidDataException("CHILD_TOKEN_METADATA_MISSING");
            bool match=childFacts.user_sid_sha256==original.user_sid_sha256&&childFacts.session_id==original.session_id&&!childFacts.elevated&&childFacts.integrity_rid==0x2000&&childFacts.token_type==1;
            if(token is not null)match&=IsExpectedRestriction(childFacts,original);
            else match&=childFacts.is_restricted==original.is_restricted&&childFacts.restricting_sids.SequenceEqual(original.restricting_sids);
            if(!match)throw new InvalidOperationException("CHILD_ACTUAL_TOKEN_MISMATCH");
            return new(label,true,true,null,(int)child.ProcessId,exitCode,true,"metadata_validated",metadata,null);
        }catch(Exception error){failure=Error(error);}
        finally{if(child.Thread!=IntPtr.Zero)Native.CloseHandle(child.Thread);if(child.Process!=IntPtr.Zero)Native.CloseHandle(child.Process);}
        return new(label,false,started,nativeError,started?(int?)child.ProcessId:null,exitCode,exited,stage,metadata,failure);
    }
    private static string Quote(string value){var b=new StringBuilder("\"");int slashes=0;foreach(char c in value){if(c=='\\'){slashes++;continue;}b.Append('\\',c=='\"'?slashes*2+1:slashes).Append(c);slashes=0;}return b.Append('\\',slashes*2).Append('"').ToString();}
    private static object Error(Exception error)=>new{type=error.GetType().Name,error.Message,native_error=error is Win32Exception win?win.NativeErrorCode:(int?)null};
}
internal sealed record TokenFacts(string user_sid_sha256,int token_type,uint session_id,uint integrity_rid,bool elevated,bool is_restricted,string[] restricting_sids,string[] enabled_privileges);
internal static class Native
{
    internal static Win32Exception Error(string stage)=>new(Marshal.GetLastWin32Error(),stage);
    internal static SafeAccessTokenHandle OpenOwnToken(uint access){if(!OpenProcessToken(GetCurrentProcess(),access,out var token))throw Error("OPEN_OWN_TOKEN_FAILED");return token;}
    internal static TokenFacts ReadToken(SafeAccessTokenHandle token)
    {
        string user="";int type=0;uint session=0,integrity=0;bool elevated=false;var restricted=new List<string>();var privileges=new List<string>();
        foreach(int kind in new[]{1,8,12,25,20,11,3}){
            GetTokenInformation(token,kind,IntPtr.Zero,0,out int size);if(size<=0||size>1048576)throw Error("TOKEN_METADATA_SIZE_FAILED");IntPtr memory=Marshal.AllocHGlobal(size);
            try{
                if(!GetTokenInformation(token,kind,memory,size,out _))throw Error("TOKEN_METADATA_QUERY_FAILED");
                if(kind==1)user=new SecurityIdentifier(Marshal.ReadIntPtr(memory)).Value;
                else if(kind==8)type=Marshal.ReadInt32(memory);
                else if(kind==12)session=unchecked((uint)Marshal.ReadInt32(memory));
                else if(kind==20)elevated=Marshal.ReadInt32(memory)!=0;
                else if(kind==25){string sid=new SecurityIdentifier(Marshal.ReadIntPtr(memory)).Value;integrity=uint.Parse(sid[(sid.LastIndexOf('-')+1)..],System.Globalization.CultureInfo.InvariantCulture);}
                else if(kind==11){int count=Marshal.ReadInt32(memory),offset=IntPtr.Size==8?8:4,entry=Marshal.SizeOf<SidAndAttributes>();if(count<0||offset+(long)count*entry>size)throw new InvalidDataException("RESTRICTED_SID_BUFFER_INVALID");for(int index=0;index<count;index++){var item=Marshal.PtrToStructure<SidAndAttributes>(memory+offset+index*entry);restricted.Add(new SecurityIdentifier(item.Sid).Value);}}
                else if(kind==3){int count=Marshal.ReadInt32(memory);if(count<0||4+(long)count*12>size)throw new InvalidDataException("PRIVILEGE_BUFFER_INVALID");for(int index=0;index<count;index++){IntPtr entry=memory+4+index*12;var luid=Marshal.PtrToStructure<Luid>(entry);uint attributes=unchecked((uint)Marshal.ReadInt32(entry+8));if((attributes&2)==0)continue;var name=new StringBuilder(256);uint chars=(uint)name.Capacity;if(!LookupPrivilegeName(null,ref luid,name,ref chars))throw Error("PRIVILEGE_NAME_QUERY_FAILED");privileges.Add(name.ToString());}}
            }finally{Marshal.FreeHGlobal(memory);}
        }
        return new(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(user))),type,session,integrity,elevated,IsTokenRestricted(token),restricted.Order(StringComparer.Ordinal).ToArray(),privileges.Order(StringComparer.Ordinal).ToArray());
    }
    internal static object StationMetadata()
    {
        IntPtr current=GetProcessWindowStation();int currentError=current==IntPtr.Zero?Marshal.GetLastWin32Error():0;
        object? association=current==IntPtr.Zero?null:UserObjectName(current);
        return new{process_station=association,process_station_error=currentError,
            readattributes_handle=OpenStationMetadata(2),clipboard_access_handle=OpenStationMetadata(4),
            interpretation="Granted-mask opens only; no clipboard access/association/ACL changes and no isolation claim."};
    }
    private static object OpenStationMetadata(uint requested)
    {
        IntPtr handle=OpenWindowStation("WinSta0",false,requested);if(handle==IntPtr.Zero)return new{opened=false,requested_access=requested,error=Marshal.GetLastWin32Error()};
        try{int status=NtQueryObject(handle,0,out ObjectBasic basic,(uint)Marshal.SizeOf<ObjectBasic>(),out _);return new{opened=true,requested_access=requested,query_status="0x"+unchecked((uint)status).ToString("X8"),granted_access=status==0?(uint?)basic.GrantedAccess:null,accessclipboard_granted=status==0?(bool?)((basic.GrantedAccess&4)!=0):null};}
        finally{CloseWindowStation(handle);}
    }
    private static object UserObjectName(IntPtr handle){var name=new StringBuilder(512);bool ok=GetUserObjectInformation(handle,2,name,name.Capacity*2,out _);return new{query_succeeded=ok,name=ok?name.ToString():null,error=ok?0:Marshal.GetLastWin32Error()};}
    internal sealed class OwnedPipes:IDisposable
    {
        private SafeFileHandle? outputRead,outputWrite,inputRead,inputWrite;
        private IntPtr attributes,handleList;
        private bool attributesInitialized;
        internal OwnedPipes(){
            var sa=new SecurityAttributes{Size=Marshal.SizeOf<SecurityAttributes>(),Inherit=true};
            try{
                if(!CreatePipe(out outputRead,out outputWrite,ref sa,0)||!CreatePipe(out inputRead,out inputWrite,ref sa,0))throw Error("OWNED_PIPE_CREATE_FAILED");
                if(!SetHandleInformation(outputRead,1,0)||!SetHandleInformation(inputWrite,1,0))throw Error("OWNED_PIPE_INHERITANCE_FAILED");
                // Parent closes stdin writer: child's valid inherited stdin sees EOF.
                inputWrite.Dispose();inputWrite=null;
                UIntPtr size=UIntPtr.Zero;InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);if(size==UIntPtr.Zero)throw Error("HANDLE_LIST_SIZE_FAILED");
                attributes=Marshal.AllocHGlobal(checked((int)size.ToUInt64()));if(!InitializeProcThreadAttributeList(attributes,1,0,ref size))throw Error("HANDLE_LIST_INITIALIZE_FAILED");attributesInitialized=true;
                handleList=Marshal.AllocHGlobal(IntPtr.Size*2);Marshal.WriteIntPtr(handleList,outputWrite.DangerousGetHandle());Marshal.WriteIntPtr(handleList+IntPtr.Size,inputRead.DangerousGetHandle());
                if(!UpdateProcThreadAttribute(attributes,0,(UIntPtr)0x20002,handleList,(UIntPtr)(IntPtr.Size*2),IntPtr.Zero,IntPtr.Zero))throw Error("HANDLE_LIST_UPDATE_FAILED");
            }catch{Dispose();throw;}
        }
        internal StartupInfoEx StartupInfo()=>new(){Startup=new(){Size=Marshal.SizeOf<StartupInfoEx>(),Flags=0x100,StdInput=inputRead!.DangerousGetHandle(),StdOutput=outputWrite!.DangerousGetHandle(),StdError=outputWrite.DangerousGetHandle()},Attributes=attributes};
        internal void CloseParentWriteEnd(){outputWrite?.Dispose();outputWrite=null;inputRead?.Dispose();inputRead=null;}
        internal SafeFileHandle DetachOutputRead(){var handle=outputRead??throw new InvalidOperationException("OUTPUT_HANDLE_MISSING");outputRead=null;return handle;}
        public void Dispose(){outputRead?.Dispose();outputWrite?.Dispose();inputRead?.Dispose();inputWrite?.Dispose();if(attributes!=IntPtr.Zero){if(attributesInitialized)DeleteProcThreadAttributeList(attributes);Marshal.FreeHGlobal(attributes);attributes=IntPtr.Zero;attributesInitialized=false;}if(handleList!=IntPtr.Zero){Marshal.FreeHGlobal(handleList);handleList=IntPtr.Zero;}}
    }
    [StructLayout(LayoutKind.Sequential)]internal struct SidAndAttributes{public IntPtr Sid;public uint Attributes;}
    [StructLayout(LayoutKind.Sequential)]private struct Luid{public uint Low;public int High;}
    [StructLayout(LayoutKind.Sequential)]private struct SecurityAttributes{public int Size;public IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit;}
    [StructLayout(LayoutKind.Sequential)]private struct ObjectBasic{public uint Attributes,GrantedAccess,HandleCount,PointerCount;[MarshalAs(UnmanagedType.ByValArray,SizeConst=10)]public uint[] Reserved;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct StartupInfo{public int Size;public string? Reserved,Desktop,Title;public uint X,Y,XSize,YSize,XCountChars,YCountChars,FillAttribute,Flags;public short ShowWindow,Reserved2;public IntPtr ReservedPointer,StdInput,StdOutput,StdError;}
    [StructLayout(LayoutKind.Sequential)]internal struct StartupInfoEx{public StartupInfo Startup;public IntPtr Attributes;}
    [StructLayout(LayoutKind.Sequential)]internal struct ProcessInfo{public IntPtr Process,Thread;public uint ProcessId,ThreadId;}
    [DllImport("kernel32.dll")]internal static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(IntPtr process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool GetTokenInformation(SafeAccessTokenHandle token,int kind,IntPtr information,int length,out int required);
    [DllImport("advapi32.dll")]private static extern bool IsTokenRestricted(SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool LookupPrivilegeName(string? system,ref Luid luid,StringBuilder name,ref uint chars);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern bool ConvertStringSidToSid(string value,out IntPtr sid);
    [DllImport("kernel32.dll")]internal static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll",SetLastError=true)]internal static extern bool CreateRestrictedToken(SafeAccessTokenHandle existing,uint flags,uint disabledCount,IntPtr disabled,uint deletedCount,IntPtr deleted,uint restrictedCount,ref SidAndAttributes restricted,out SafeAccessTokenHandle result);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string? directory,ref StartupInfoEx startup,out ProcessInfo process);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern bool CreateProcessAsUser(SafeAccessTokenHandle token,string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string? directory,ref StartupInfoEx startup,out ProcessInfo process);
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern uint WaitForSingleObject(IntPtr process,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    [DllImport("kernel32.dll")]internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool CreatePipe(out SafeFileHandle read,out SafeFileHandle write,ref SecurityAttributes attributes,uint size);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetHandleInformation(SafeFileHandle handle,uint mask,uint flags);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool InitializeProcThreadAttributeList(IntPtr attributes,int count,uint flags,ref UIntPtr size);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool UpdateProcThreadAttribute(IntPtr attributes,uint flags,UIntPtr attribute,IntPtr value,UIntPtr size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")]private static extern void DeleteProcThreadAttributeList(IntPtr attributes);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenWindowStation(string name,bool inherit,uint desired);
    [DllImport("user32.dll")]private static extern bool CloseWindowStation(IntPtr handle);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool GetUserObjectInformation(IntPtr handle,int kind,StringBuilder value,int size,out int required);
    [DllImport("ntdll.dll")]private static extern int NtQueryObject(IntPtr handle,int kind,out ObjectBasic information,uint size,out uint required);
}
