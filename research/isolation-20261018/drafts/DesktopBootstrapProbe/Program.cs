using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutumnOS.DesktopBootstrapProbe;
internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        if(args.Length is not (4 or 5 or 6)||args[0]!="--runtime"||args[2]!="--report")return 2;
        string runtime=Path.GetFullPath(args[1]),report=Path.GetFullPath(args[3]);
        if(File.Exists(report)||!File.Exists(Path.Combine(runtime,"msedgewebview2.exe")))return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        if(args.Length==5&&args[4]=="--launch-child")return LaunchChild(runtime,report);
        bool isChild=args.Length==6&&args[4]=="--child-desktop";
        if(args.Length!=4&&!isChild)return 2;
        var events=new List<object>();object? failure=null;int result=1;
        string target=isChild?args[5]:"AutumnOS_DesktopBootstrap_"+Guid.NewGuid().ToString("N");
        IntPtr desktop=IntPtr.Zero;
        try {
            events.Add(Facts("MTA_entry"));
            var attributes=new Native.SecurityAttributes{Length=Marshal.SizeOf<Native.SecurityAttributes>(),Inherit=true};
            desktop=isChild?Native.OpenDesktop(target,0,false,0xF01FF):Native.CreateDesktop(target,IntPtr.Zero,IntPtr.Zero,0,0xF01FF,ref attributes);
            if(desktop==IntPtr.Zero)throw Native.Error("CREATE_NEW_DESKTOP_FAILED");
            if(isChild&&Native.Name(Native.GetThreadDesktop(Native.GetCurrentThreadId()))!=target)throw new InvalidOperationException("CHILD_PROCESS_STARTUP_DESKTOP_WRONG");
            if(!isChild&&!Native.SetThreadDesktop(desktop))throw Native.Error("MTA_SET_THREAD_DESKTOP_FAILED");
            events.Add(Facts("MTA_target_associated"));
            var worker=new Thread(()=>{
                try{
                    events.Add(Facts("STA_delegate_entry"));
                    if(Native.Name(Native.GetThreadDesktop(Native.GetCurrentThreadId()))!=target)
                        throw new InvalidOperationException("STA_DID_NOT_INHERIT_TARGET_DESKTOP_NO_GUI_CREATED");
                    Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                    using var form=new Form{Text="Private desktop bootstrap probe",Width=640,Height=480,ShowInTaskbar=false};
                    _=form.Handle;events.Add(Facts("STA_form_handle_created"));
                    form.Shown+=async(_,_)=>{
                        WebView2? view=null;var exit=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        try{
                            var environment=await CoreWebView2Environment.CreateAsync(runtime,Path.Combine(Path.GetDirectoryName(report)!,"profile"));
                            environment.BrowserProcessExited+=(_,_)=>exit.TrySetResult(true);
                            view=new WebView2{Dock=DockStyle.Fill};form.Controls.Add(view);
                            await view.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(20));
                            view.CoreWebView2.Settings.AreHostObjectsAllowed=false;
                            view.CoreWebView2.PermissionRequested+=(_,e)=>{e.State=CoreWebView2PermissionState.Deny;e.Handled=true;};
                            uint browser=view.CoreWebView2.BrowserProcessId;
                            var windows=Native.WindowPids(desktop);
                            events.Add(new{stage="WebView_initialized",thread=Facts("STA_after_WebView"),browser_pid=browser,browser_has_target_desktop_hwnd=windows.Contains(browser),runtime=environment.BrowserVersionString});
                            if(Native.Name(Native.GetThreadDesktop(Native.GetCurrentThreadId()))!=target||!windows.Contains(browser))throw new InvalidOperationException("BROWSER_PRIVATE_DESKTOP_NOT_PROVEN");
                            var messages=new[]{"ready","click","key"}.ToDictionary(key=>key,_=>new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously));
                            view.CoreWebView2.WebMessageReceived+=(_,e)=>{using var doc=JsonDocument.Parse(e.WebMessageAsJson);if(messages.TryGetValue(doc.RootElement.GetProperty("phase").GetString()!,out var completion))completion.TrySetResult(doc.RootElement.Clone());};
                            view.CoreWebView2.NavigateToString("<!doctype html><button id=button>Owned focus test</button><textarea id=target></textarea><script>button.onclick=e=>{target.focus();chrome.webview.postMessage({phase:'click',trusted:e.isTrusted,active:navigator.userActivation.isActive,focused:document.hasFocus()})};target.onkeydown=e=>{if(e.key==='x')chrome.webview.postMessage({phase:'key',trusted:e.isTrusted,active:navigator.userActivation.isActive,focused:document.hasFocus(),key:e.key})};chrome.webview.postMessage({phase:'ready'})</script>");
                            await messages["ready"].Task.WaitAsync(TimeSpan.FromSeconds(10));form.Activate();view.Focus();await Task.Delay(100);
                            using(var geometry=JsonDocument.Parse(await view.CoreWebView2.ExecuteScriptAsync("(()=>{const r=button.getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()"))){
                                double x=geometry.RootElement.GetProperty("x").GetDouble(),y=geometry.RootElement.GetProperty("y").GetDouble();
                                foreach(string type in new[]{"mousePressed","mouseReleased"})await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type,x,y,button="left",buttons=type=="mousePressed"?1:0,clickCount=1}));
                            }
                            foreach(string type in new[]{"rawKeyDown","keyUp"})await view.CoreWebView2.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",JsonSerializer.Serialize(new{type,key="x",code="KeyX",windowsVirtualKeyCode=88,nativeVirtualKeyCode=88,modifiers=0}));
                            var click=await messages["click"].Task.WaitAsync(TimeSpan.FromSeconds(10));var key=await messages["key"].Task.WaitAsync(TimeSpan.FromSeconds(10));
                            events.Add(new{stage="owned_focus_input_no_clipboard_commands",click,key});
                            if(!click.GetProperty("trusted").GetBoolean()||!click.GetProperty("active").GetBoolean()||!click.GetProperty("focused").GetBoolean()||!key.GetProperty("trusted").GetBoolean()||!key.GetProperty("focused").GetBoolean())throw new InvalidOperationException("OWNED_INPUT_FOCUS_POSITIVE_CONTROL_FAILED");
                            result=0;
                        }catch(Exception error){failure=Error(error);}
                        finally{
                            view?.Dispose();
                            if(view is not null){try{await exit.Task.WaitAsync(TimeSpan.FromSeconds(12));events.Add(new{stage="browser_normal_exit",status="passed"});}catch(TimeoutException){result=1;events.Add(new{stage="browser_normal_exit",status="failed"});}}
                            form.Close();
                        }
                    };
                    Application.Run(form);
                }catch(Exception error){failure=Error(error);}
            });
            worker.SetApartmentState(ApartmentState.STA);worker.Start();worker.Join();
        }catch(Exception error){failure=Error(error);}
        File.WriteAllText(report,JsonSerializer.Serialize(new{schema_version=1,status=result==0?"passed":"failed",test=isChild?"explicit_process_startup_desktop_before_COM_no_clipboard":"MTA_new_desktop_then_STA_inheritance_no_clipboard",target,events,failure,
            clipboard_calls="none",user_input="CDP only owned button and plain unmodified x key; no clipboard command or shortcut",desktop_switch="none",scope="Only creates a fresh desktop; no user windows enumerated or changed. Only inline owned focus page. No UAC."},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(report);return result;
    }
    private static int LaunchChild(string runtime,string report)
    {
        string target="AutumnOS_DesktopBootstrap_"+Guid.NewGuid().ToString("N"),station=Native.Name(Native.GetProcessWindowStation());
        var attributes=new Native.SecurityAttributes{Length=Marshal.SizeOf<Native.SecurityAttributes>(),Inherit=true};
        IntPtr desktop=Native.CreateDesktop(target,IntPtr.Zero,IntPtr.Zero,0,0xF01FF,ref attributes);if(desktop==IntPtr.Zero)throw Native.Error("PARENT_CREATE_DESKTOP_FAILED");
        string exe=Environment.ProcessPath!;
        var startup=new Native.StartupInfo{Size=Marshal.SizeOf<Native.StartupInfo>(),Desktop=station+"\\"+target};
        var command=new StringBuilder(string.Join(" ",new[]{exe,"--runtime",runtime,"--report",report,"--child-desktop",target}.Select(Quote)));
        if(!Native.CreateProcess(exe,command,IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,null,ref startup,out var child))throw Native.Error("CREATE_ORDINARY_DESKTOP_CHILD_FAILED");
        try{if(Native.WaitForSingleObject(child.Process,120000)!=0)throw new TimeoutException("CHILD_NOT_FINISHED_NO_FORCE_KILL");if(!Native.GetExitCodeProcess(child.Process,out uint code))throw Native.Error("CHILD_EXIT_CODE_FAILED");return (int)code;}
        finally{Native.CloseHandle(child.Thread);Native.CloseHandle(child.Process);Native.CloseDesktop(desktop);}
    }
    private static string Quote(string value){var b=new StringBuilder("\"");int slashes=0;foreach(char c in value){if(c=='\\'){slashes++;continue;}b.Append('\\',c=='\"'?slashes*2+1:slashes).Append(c);slashes=0;}return b.Append('\\',slashes*2).Append('"').ToString();}
    private static object Facts(string stage)=>new{stage,apartment=Thread.CurrentThread.GetApartmentState().ToString(),station=Native.Name(Native.GetProcessWindowStation()),desktop=Native.Name(Native.GetThreadDesktop(Native.GetCurrentThreadId())),thread=Native.GetCurrentThreadId()};
    private static object Error(Exception error)=>new{type=error.GetType().Name,error.Message,native_error=error is Win32Exception native?native.NativeErrorCode:(int?)null};
}
internal static class Native
{
    internal static Win32Exception Error(string stage)=>new(Marshal.GetLastWin32Error(),stage);
    internal static string Name(IntPtr handle){var value=new StringBuilder(512);if(!GetUserObjectInformation(handle,2,value,value.Capacity*2,out _))throw Error("OBJECT_NAME_QUERY_FAILED");return value.ToString();}
    internal static HashSet<uint> WindowPids(IntPtr desktop){var result=new HashSet<uint>();bool Visit(IntPtr hwnd,IntPtr _){GetWindowThreadProcessId(hwnd,out uint pid);result.Add(pid);EnumChildWindows(hwnd,(child,_)=>{GetWindowThreadProcessId(child,out uint childPid);result.Add(childPid);return true;},IntPtr.Zero);return true;}if(!EnumDesktopWindows(desktop,Visit,IntPtr.Zero))throw Error("TARGET_DESKTOP_ENUM_FAILED");return result;}
    [StructLayout(LayoutKind.Sequential)]internal struct SecurityAttributes{public int Length;public IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct StartupInfo{public int Size;public string? Reserved,Desktop,Title;public uint X,Y,XSize,YSize,XCountChars,YCountChars,FillAttribute,Flags;public short ShowWindow,Reserved2;public IntPtr ReservedPointer,StdInput,StdOutput,StdError;}
    [StructLayout(LayoutKind.Sequential)]internal struct ProcessInfo{public IntPtr Process,Thread;public uint ProcessId,ThreadId;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inheritHandles,uint flags,IntPtr environment,string? directory,ref StartupInfo startup,out ProcessInfo process);
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    [DllImport("kernel32.dll")]internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")]internal static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern IntPtr OpenDesktop(string name,uint flags,bool inherit,uint access);
    private delegate bool EnumWindow(IntPtr hwnd,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,ref SecurityAttributes attributes);
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]internal static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")]internal static extern IntPtr GetProcessWindowStation();
    [DllImport("kernel32.dll")]internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]internal static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder value,int length,out int required);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool EnumDesktopWindows(IntPtr desktop,EnumWindow callback,IntPtr parameter);
    [DllImport("user32.dll")]private static extern bool EnumChildWindows(IntPtr hwnd,EnumWindow callback,IntPtr parameter);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint process);
}
