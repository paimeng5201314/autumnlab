[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReportPath)
# Read-only metadata diagnostic. No GUI, clipboard API, inheritance change, process
# creation/restriction, SetProcessWindowStation, SetThreadDesktop, or ACL write.
$ErrorActionPreference='Stop'
$ReportPath=[IO.Path]::GetFullPath($ReportPath)
if(Test-Path -LiteralPath $ReportPath){throw 'Refusing to overwrite evidence.'}
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class AutumnStationHandleAccessReadOnly {
 [StructLayout(LayoutKind.Sequential)] struct Basic {public uint Attributes,GrantedAccess,HandleCount,PointerCount;[MarshalAs(UnmanagedType.ByValArray,SizeConst=10)]public uint[] Reserved;}
 [StructLayout(LayoutKind.Sequential)] struct Flags {[MarshalAs(UnmanagedType.Bool)]public bool Inherit;[MarshalAs(UnmanagedType.Bool)]public bool Reserved;public uint Value;}
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern IntPtr OpenWindowStation(string name,bool inherit,uint desired);
 [DllImport("user32.dll",SetLastError=true)]static extern bool CloseWindowStation(IntPtr handle);
 [DllImport("user32.dll")]static extern IntPtr GetProcessWindowStation();
 [DllImport("user32.dll")]static extern IntPtr GetThreadDesktop(uint thread);
 [DllImport("kernel32.dll")]static extern uint GetCurrentThreadId();
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder value,int size,out int required);
 [DllImport("user32.dll",EntryPoint="GetUserObjectInformationW",SetLastError=true)]static extern bool GetFlags(IntPtr handle,int index,out Flags value,int size,out int required);
 [DllImport("ntdll.dll")]static extern int NtQueryObject(IntPtr handle,int information,out Basic value,uint size,out uint required);
 static string Name(IntPtr handle){var b=new StringBuilder(512);if(!GetUserObjectInformation(handle,2,b,b.Capacity*2,out _))throw new Win32Exception(Marshal.GetLastWin32Error(),"METADATA_NAME_QUERY_FAILED");return b.ToString();}
 public static object Run(){
  IntPtr beforeStation=GetProcessWindowStation(),beforeDesktop=GetThreadDesktop(GetCurrentThreadId());
  string name=Name(beforeStation),desktop=Name(beforeDesktop);
  if(!name.Equals("WinSta0",StringComparison.OrdinalIgnoreCase))throw new InvalidOperationException("DIAGNOSTIC_EXPECTS_EXISTING_INTERACTIVE_STATION");
  var reduced=OpenWindowStation(name,false,2);int reducedError=reduced==IntPtr.Zero?Marshal.GetLastWin32Error():0;
  var reopened=OpenWindowStation(name,false,4);int reopenError=reopened==IntPtr.Zero?Marshal.GetLastWin32Error():0;
  try{
   object reducedFacts=reduced==IntPtr.Zero?(object)new{opened=false,error=reducedError}:Facts(reduced,2);
   object reopenedFacts=reopened==IntPtr.Zero?(object)new{opened=false,error=reopenError}:Facts(reopened,4);
   return new{station=name,desktop,readattributes_only_handle=reducedFacts,separate_reopened_accessclipboard_handle=reopenedFacts,
    process_station_unchanged=GetProcessWindowStation()==beforeStation,thread_desktop_unchanged=GetThreadDesktop(GetCurrentThreadId())==beforeDesktop,
    clipboard_api_calls="none",association_changes="none",acl_changes="none",child_processes="none",inheritance_changes="none",
    meaning="Opening one reduced handle does not alter the same token's ability to request another handle. This does not test an attached restricted browser."};
  }finally{if(reopened!=IntPtr.Zero)CloseWindowStation(reopened);if(reduced!=IntPtr.Zero)CloseWindowStation(reduced);}
 }
 static object Facts(IntPtr handle,uint requested){
  int status=NtQueryObject(handle,0,out Basic basic,(uint)Marshal.SizeOf<Basic>(),out uint required);
  bool flagsOk=GetFlags(handle,1,out Flags flags,Marshal.SizeOf<Flags>(),out _);
  return new{opened=true,requested_access=requested,nt_query_status="0x"+unchecked((uint)status).ToString("X8"),granted_access=status==0?(uint?)basic.GrantedAccess:null,
   accessclipboard_granted=status==0?(bool?)((basic.GrantedAccess&4)!=0):null,handle_inheritable=flagsOk?(bool?)flags.Inherit:null};
 }
}
'@
$result=[pscustomobject]@{schema_version=1;kind='read_only_window_station_handle_granted_access_metadata';time_utc=[DateTimeOffset]::UtcNow.ToString('o');os=[Environment]::OSVersion.VersionString;facts=[AutumnStationHandleAccessReadOnly]::Run()}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($ReportPath))|Out-Null
$result|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $ReportPath -Encoding utf8
$result|ConvertTo-Json -Depth 8
