#nullable enable
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

public static class AutumnOwnedStationMetadata
{
    public static object Read()
    {
        string original=Name(GetProcessWindowStation());var names=new List<string>();if(!EnumWindowStations((name,_)=>{names.Add(name);return true;},IntPtr.Zero))throw new Win32Exception(Marshal.GetLastWin32Error());
        if(!OpenProcessToken(GetCurrentProcess(),8,out IntPtr token))throw new Win32Exception(Marshal.GetLastWin32Error());
        string? expected=null;
        try{GetTokenInformation(token,10,IntPtr.Zero,0,out int size);IntPtr buffer=Marshal.AllocHGlobal(size);try{if(!GetTokenInformation(token,10,buffer,size,out _))throw new Win32Exception(Marshal.GetLastWin32Error());uint low=unchecked((uint)Marshal.ReadInt32(buffer,8));int high=Marshal.ReadInt32(buffer,12);expected="Service-0x"+high.ToString("x")+"-"+low.ToString("x")+"$";}finally{Marshal.FreeHGlobal(buffer);}}finally{CloseHandle(token);}
        var observations=new List<object>();
        foreach(string name in names){if(!name.Equals(expected,StringComparison.OrdinalIgnoreCase))continue;
            IntPtr handle=OpenWindowStation(name,false,0x00020003);int error=Marshal.GetLastWin32Error();if(handle==IntPtr.Zero){observations.Add(new{name,opened=false,error});continue;}
            try{bool ownerMatches=false;string? ownerHash=null;uint descriptorError=GetSecurityInfo(handle,7,1,out IntPtr owner,out _,out _,out _,out IntPtr descriptor);
                if(descriptorError==0)try{var sid=new SecurityIdentifier(owner);ownerMatches=WindowsIdentity.GetCurrent().User is SecurityIdentifier current&&sid.Equals(current);ownerHash=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid.Value))).ToLowerInvariant();}finally{LocalFree(descriptor);}
                var desktops=new List<string>();bool enumerated=EnumDesktops(handle,(desktop,_)=>{desktops.Add(desktop);return true;},IntPtr.Zero);
                observations.Add(new{name,opened=true,owner_matches_current_user=ownerMatches,owner_sid_sha256=ownerHash,descriptor_error=descriptorError,desktops_enumerated=enumerated,desktops});
            }finally{CloseWindowStation(handle);}
        }
        return new{current_station=original,expected_auto_station=expected,expected_present=names.Exists(n=>n.Equals(expected,StringComparison.OrdinalIgnoreCase)),matching_station=observations,
            scope="Read-only window-station names, ownership metadata and desktop names; no clipboard APIs, no new GUI or object mutation."};
    }
    private static string Name(IntPtr handle){var b=new StringBuilder(512);if(!GetUserObjectInformation(handle,2,b,b.Capacity*2,out _))throw new Win32Exception(Marshal.GetLastWin32Error());return b.ToString();}
    private delegate bool EnumerateName([MarshalAs(UnmanagedType.LPWStr)]string name,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool EnumWindowStations(EnumerateName callback,IntPtr data);
    [DllImport("user32.dll")]private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool GetUserObjectInformation(IntPtr h,int index,StringBuilder value,int size,out int needed);
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(IntPtr p,uint access,out IntPtr token);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool GetTokenInformation(IntPtr token,int kind,IntPtr data,int size,out int needed);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr h);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenWindowStation(string name,bool inherit,uint access);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool CloseWindowStation(IntPtr h);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityInfo(IntPtr h,int type,uint info,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr h);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool EnumDesktops(IntPtr station,EnumerateName callback,IntPtr data);
}
