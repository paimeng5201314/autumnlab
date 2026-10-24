using System.Diagnostics;
using System.Runtime.InteropServices;

internal static class JobFacts
{
    public static object Read(uint browserPid)
    {
        using var browser=Process.GetProcessById((int)browserPid);bool hostQuery=IsProcessInJob(GetCurrentProcess(),IntPtr.Zero,out bool hostInJob),browserQuery=IsProcessInJob(browser.Handle,IntPtr.Zero,out bool browserInJob);
        IntPtr buffer=Marshal.AllocHGlobal(65536);
        try{
            bool uiQuery=QueryInformationJobObject(IntPtr.Zero,4,buffer,4,out _);int uiError=Marshal.GetLastWin32Error();uint? ui=uiQuery?unchecked((uint)Marshal.ReadInt32(buffer)):(uint?)null;
            bool limitsQuery=QueryInformationJobObject(IntPtr.Zero,9,buffer,144,out _);int limitsError=Marshal.GetLastWin32Error();uint? limitFlags=limitsQuery?unchecked((uint)Marshal.ReadInt32(buffer,16)):(uint?)null;
            bool idsQuery=QueryInformationJobObject(IntPtr.Zero,3,buffer,65536,out _);int idsError=Marshal.GetLastWin32Error();uint total=idsQuery?unchecked((uint)Marshal.ReadInt32(buffer,0)):0,count=idsQuery?unchecked((uint)Marshal.ReadInt32(buffer,4)):0;
            var ids=new List<ulong>();if(idsQuery&&count<8191)for(int i=0;i<count;i++)ids.Add(unchecked((ulong)Marshal.ReadInt64(buffer,8+i*8)));
            return new{host_pid=Environment.ProcessId,browser_pid=browserPid,host_job_query=hostQuery,host_in_job=hostInJob,browser_job_query=browserQuery,browser_in_job=browserInJob,
                current_host_job_ui_query=uiQuery,current_host_job_ui_flags=ui,current_host_job_ui_error=uiQuery?0:uiError,current_host_job_limits_query=limitsQuery,current_host_job_limit_flags=limitFlags,current_host_job_limits_error=limitsQuery?0:limitsError,
                current_host_job_process_list_query=idsQuery,current_host_job_process_count=total,current_host_job_listed_count=count,current_host_job_contains_host=ids.Contains((ulong)Environment.ProcessId),current_host_job_contains_browser=ids.Contains(browserPid),current_host_job_list_error=idsQuery?0:idsError,
                browser_parent_pid=Parent(browserPid),scope="Read-only current-job metadata; no opening/changing existing job, no breakaway, no unrelated process IDs printed."};
        }finally{Marshal.FreeHGlobal(buffer);}
    }
    private static uint? Parent(uint pid){IntPtr snapshot=CreateToolhelp32Snapshot(2,0);if(snapshot==new IntPtr(-1))return null;try{var entry=new ProcessEntry{Size=(uint)Marshal.SizeOf<ProcessEntry>()};if(Process32First(snapshot,ref entry))do{if(entry.ProcessId==pid)return entry.ParentId;}while(Process32Next(snapshot,ref entry));return null;}finally{CloseHandle(snapshot);}}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct ProcessEntry{public uint Size,Usage,ProcessId;public UIntPtr DefaultHeap;public uint ModuleId,Threads,ParentId;public int BasePriority;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)]public string Exe;}
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool IsProcessInJob(IntPtr process,IntPtr job,out bool assigned);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool QueryInformationJobObject(IntPtr job,int kind,IntPtr value,uint size,out uint returned);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",EntryPoint="Process32FirstW",CharSet=CharSet.Unicode)]private static extern bool Process32First(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",EntryPoint="Process32NextW",CharSet=CharSet.Unicode)]private static extern bool Process32Next(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
}
