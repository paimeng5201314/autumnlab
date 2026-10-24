using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AutumnOS.Runtime;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutumnOS.NavigationCancelProbe;
internal static class Program
{
    [MTAThread] private static int Main(string[] args)
    {
        if(args.Length is not(4 or 6)||args[0]!="--runtime"||args[2]!="--report"||args.Length==6&&args[4]!="--owned-desktop")return 2;
        string runtime=Path.GetFullPath(args[1]),report=Path.GetFullPath(args[3]);
        if(File.Exists(report)||!File.Exists(Path.Combine(runtime,"msedgewebview2.exe")))return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        if(args.Length==4)return Launch(args);
        string desktop=args[5];
        if(!desktop.StartsWith("AutumnOS_NavigationProbe_",StringComparison.Ordinal)||Native.Name(Native.GetThreadDesktop(Native.GetCurrentThreadId()))!=desktop)return 2;
        int result=1;Exception? failure=null;
        var ui=new Thread(()=>{try{
            if(Native.Name(Native.GetThreadDesktop(Native.GetCurrentThreadId()))!=desktop)throw new InvalidOperationException("STA_DESKTOP_MISMATCH");
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            using var window=new ProbeWindow(runtime,report,desktop);Application.Run(window);result=window.Result;
        }catch(Exception error){failure=error;}});ui.SetApartmentState(ApartmentState.STA);ui.Start();ui.Join();
        if(failure is not null){File.WriteAllText(report,JsonSerializer.Serialize(new{status="failed",failure=failure.ToString()}));return 1;}
        return result;
    }
    private static int Launch(string[] args)
    {
        string desktop="AutumnOS_NavigationProbe_"+Guid.NewGuid().ToString("N"),exe=Environment.ProcessPath!;
        var sa=new Native.SecurityAttributes{Length=Marshal.SizeOf<Native.SecurityAttributes>(),Inherit=false};
        IntPtr handle=Native.CreateDesktop(desktop,IntPtr.Zero,IntPtr.Zero,0,0xF01FF,ref sa);if(handle==IntPtr.Zero)throw Native.Error("CREATE_DESKTOP_FAILED");
        var startup=new Native.StartupInfo{Size=Marshal.SizeOf<Native.StartupInfo>(),Desktop=Native.Name(Native.GetProcessWindowStation())+"\\"+desktop};
        var command=new StringBuilder(string.Join(" ",new[]{exe}.Concat(args).Concat(new[]{"--owned-desktop",desktop}).Select(Quote)));
        if(!Native.CreateProcess(exe,command,IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,null,ref startup,out var child))throw Native.Error("CREATE_CHILD_FAILED");
        try{if(Native.WaitForSingleObject(child.Process,180000)!=0)throw new TimeoutException("CHILD_TIMEOUT_NO_KILL");if(!Native.GetExitCodeProcess(child.Process,out uint exit))throw Native.Error("EXIT_CODE_FAILED");return (int)exit;}
        finally{Native.CloseHandle(child.Thread);Native.CloseHandle(child.Process);Native.CloseDesktop(handle);}
    }
    private static string Quote(string value){var b=new StringBuilder("\"");int slashes=0;foreach(char c in value){if(c=='\\'){slashes++;continue;}b.Append('\\',c=='\"'?slashes*2+1:slashes).Append(c);slashes=0;}return b.Append('\\',slashes*2).Append('"').ToString();}
}
internal sealed class ProbeWindow:Form
{
    private readonly string runtime,report,desktop;private readonly List<object> cases=[];public int Result{get;private set;}=1;
    public ProbeWindow(string runtime,string report,string desktop){this.runtime=runtime;this.report=report;this.desktop=desktop;Width=800;Height=600;ShowInTaskbar=false;Text="Owned navigation cancellation experiment";Shown+=async(_,_)=>await RunAsync();}
    private async Task RunAsync()
    {
        object? failure=null;using var collector=new Collector();
        try{
            using var http=new HttpClient(new HttpClientHandler{UseProxy=false,UseCookies=false,UseDefaultCredentials=false});
            if(await http.GetStringAsync(collector.Origin+"host-control-before")!="owned collector")throw new InvalidOperationException("COLLECTOR_CONTROL_FAILED");
            foreach(var variant in new[]{new Variant("A-unrestricted-positive",false,false,false),new Variant("B-cancel",true,false,false),new Variant("C-cancel-no-error-page",true,true,false),new Variant("D-cancel-stop",true,false,true),new Variant("E-cancel-no-error-page-stop",true,true,true)})await CaseAsync(variant,collector);
            if(await http.GetStringAsync(collector.Origin+"host-control-after")!="owned collector")throw new InvalidOperationException("COLLECTOR_AFTER_FAILED");
            Result=0;
        }catch(Exception error){failure=new{type=error.GetType().Name,error.Message};}
        File.WriteAllText(report,JsonSerializer.Serialize(new{schema_version=1,status=Result==0?"completed":"failed",kind="real_WebView2_navigation_Cancel_error_document_experiment",os=Environment.OSVersion.VersionString,policy=RuntimeIsolationPolicy.PolicyId,browser_arguments=RuntimeIsolationPolicy.BrowserArguments,desktop,cases,collector_requests=collector.Requests,failure,
            scope="Own new noninteractive desktop in current station. No clipboard API, UAC, SwitchDesktop, user input, global setting or unrelated service. CDP input stimulates only the owned fixture. Preserving the document is separate from preventing network delivery."},new JsonSerializerOptions{WriteIndented=true}));Close();
    }
    private async Task CaseAsync(Variant variant,Collector collector)
    {
        string marker=Guid.NewGuid().ToString("N"),origin="https://owned-nav-"+marker+".autumn.invalid/",page=origin+"index.html",target=collector.Origin+variant.Name;
        var events=new List<object>();var samples=new List<object>();var messages=new List<JsonElement>();var streams=new List<Stream>();bool exited=false,failed=false;object? failure=null;
        var environment=await CoreWebView2Environment.CreateAsync(runtime,Path.Combine(Path.GetDirectoryName(report)!,variant.Name,"profile"),new CoreWebView2EnvironmentOptions{AdditionalBrowserArguments=RuntimeIsolationPolicy.BrowserArguments});
        if(!RuntimeIsolationPolicy.IsSupportedBrowserVersion(environment.BrowserVersionString))throw new InvalidOperationException("ENGINE_UNSUPPORTED");
        var exit=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);environment.BrowserProcessExited+=(_,_)=>exit.TrySetResult(true);
        using var view=new WebView2{Dock=DockStyle.Fill};Controls.Add(view);uint browser=0;bool ownWindow=false,initial=false,clicked=false,preserved=false;
        try{
            await view.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(20));var core=view.CoreWebView2;browser=core.BrowserProcessId;
            IntPtr desk=Native.GetThreadDesktop(Native.GetCurrentThreadId());ownWindow=Native.WindowPids(desk).Contains(browser);
            if(Native.Name(desk)!=desktop||!ownWindow)throw new InvalidOperationException("BROWSER_OWNED_DESKTOP_NOT_PROVEN");
            core.Settings.IsBuiltInErrorPageEnabled=!variant.NoErrorPage;core.Settings.AreHostObjectsAllowed=false;core.Settings.AreDevToolsEnabled=false;core.Settings.AreDefaultContextMenusEnabled=false;
            core.PermissionRequested+=(_,e)=>{e.State=CoreWebView2PermissionState.Deny;e.Handled=true;};core.NewWindowRequested+=(_,e)=>e.Handled=true;core.DownloadStarting+=(_,e)=>e.Cancel=true;core.LaunchingExternalUriScheme+=(_,e)=>e.Cancel=true;
            core.NavigationStarting+=(_,e)=>{bool deny=variant.Protected&&e.Uri!=page;if(deny){e.Cancel=true;if(variant.Stop)core.Stop();}events.Add(new{eventName="NavigationStarting",e.NavigationId,e.Uri,cancel=e.Cancel,e.IsUserInitiated,source=core.Source});};
            core.SourceChanged+=(_,e)=>events.Add(new{eventName="SourceChanged",e.IsNewDocument,source=core.Source});
            core.ContentLoading+=(_,e)=>events.Add(new{eventName="ContentLoading",e.NavigationId,e.IsErrorPage,source=core.Source});
            core.DOMContentLoaded+=(_,e)=>events.Add(new{eventName="DOMContentLoaded",e.NavigationId,source=core.Source});
            core.NavigationCompleted+=(_,e)=>events.Add(new{eventName="NavigationCompleted",e.NavigationId,e.IsSuccess,error=e.WebErrorStatus.ToString(),source=core.Source});
            core.ProcessFailed+=(_,e)=>{failed=true;events.Add(new{eventName="ProcessFailed",kind=e.ProcessFailedKind.ToString()});};
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All,CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested+=(_,e)=>{
                bool html=e.Request.Uri==page,script=e.Request.Uri==origin+"probe.js";
                if(!html&&!script&&!variant.Protected)return;
                string text=html?"<!doctype html><meta charset=utf-8><button id=go>Owned blocked top navigation</button><p id=mark></p><script src=probe.js defer></script>":script?"window.marker="+JsonSerializer.Serialize(marker)+";window.ticks=0;setInterval(()=>window.ticks++,20);document.getElementById('mark').textContent=window.marker;document.getElementById('go').onclick=e=>{chrome.webview.postMessage({phase:'clicked',trusted:e.isTrusted});location.assign("+JsonSerializer.Serialize(target)+")};chrome.webview.postMessage({phase:'ready',marker:window.marker});":"denied";
                var stream=new MemoryStream(Encoding.UTF8.GetBytes(text),false);streams.Add(stream);
                e.Response=environment.CreateWebResourceResponse(stream,html||script?200:403,html||script?"OK":"Forbidden","Content-Type: "+(html?"text/html":"text/javascript")+"\r\n"+(variant.Protected?RuntimeIsolationPolicy.ResponseHeaders:"Cache-Control: no-store"));
                events.Add(new{eventName="WebResourceRequested",e.Request.Uri,status=html||script?200:403});
            };
            var ready=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var click=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            core.WebMessageReceived+=(_,e)=>{using var json=JsonDocument.Parse(e.WebMessageAsJson);var item=json.RootElement.Clone();messages.Add(item);string? phase=item.GetProperty("phase").GetString();if(phase=="ready")ready.TrySetResult(e.Source==page&&item.GetProperty("marker").GetString()==marker);if(phase=="clicked")click.TrySetResult(e.Source==page&&item.GetProperty("trusted").GetBoolean());};
            core.Navigate(page);initial=await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));if(!initial)throw new InvalidOperationException("INITIAL_SCRIPT_NOT_PROVEN");
            Activate();view.Focus();await Task.Delay(100);samples.Add(await Sample(core,"before"));
            using(var box=JsonDocument.Parse(await core.ExecuteScriptAsync("(()=>{const r=document.getElementById('go').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()"))){double x=box.RootElement.GetProperty("x").GetDouble(),y=box.RootElement.GetProperty("y").GetDouble();foreach(string type in new[]{"mousePressed","mouseReleased"})await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type,x,y,button="left",buttons=type=="mousePressed"?1:0,clickCount=1}));}
            clicked=await click.Task.WaitAsync(TimeSpan.FromSeconds(8));if(!clicked)throw new InvalidOperationException("NAVIGATION_TRIGGER_NOT_PROVEN");
            foreach(int delay in new[]{100,400,700}){await Task.Delay(delay);samples.Add(await Sample(core,"after_"+delay));}
            string actual=await core.ExecuteScriptAsync("JSON.stringify({marker:window.marker??null,href:location.href})");using var outer=JsonDocument.Parse(actual);using var inner=JsonDocument.Parse(outer.RootElement.GetString()!);preserved=inner.RootElement.GetProperty("marker").GetString()==marker&&inner.RootElement.GetProperty("href").GetString()==page&&core.Source==page;
        }catch(Exception error){failure=new{type=error.GetType().Name,error.Message};}
        finally{view.Dispose();Controls.Remove(view);foreach(var stream in streams)stream.Dispose();try{exited=await exit.Task.WaitAsync(TimeSpan.FromSeconds(12));}catch(TimeoutException){}}
        cases.Add(new{variant=variant.Name,variant.Protected,variant.NoErrorPage,variant.Stop,runtime=environment.BrowserVersionString,browser,ownWindow,initial_script_proven=initial,trusted_click_proven=clicked,old_document_preserved=preserved,receiver_observed_target=collector.Requests.Any(r=>r.Contains(" "+"/"+variant.Name+" ",StringComparison.Ordinal)),browser_exited=exited,process_failed=failed,events,messages,samples,failure});
        if(!exited)throw new InvalidOperationException("BROWSER_NOT_EXITED_NO_NEXT_CASE");
    }
    private static async Task<object> Sample(CoreWebView2 core,string phase){string result=await core.ExecuteScriptAsync("JSON.stringify({href:location.href,marker:window.marker??null,ticks:window.ticks??null,body:document.body?.innerText.slice(0,200)})");return new{phase,native_source=core.Source,document=result};}
    private sealed record Variant(string Name,bool Protected,bool NoErrorPage,bool Stop);
}
internal sealed class Collector:IDisposable
{
    private readonly TcpListener server=new(IPAddress.Loopback,0);private readonly CancellationTokenSource stop=new();private readonly List<string> requests=[];private readonly Task loop;
    public string Origin{get;}public string[] Requests{get{lock(requests)return requests.ToArray();}}
    public Collector(){server.Start();Origin="http://127.0.0.1:"+((IPEndPoint)server.LocalEndpoint).Port+"/";loop=Loop();}
    private async Task Loop(){try{while(!stop.IsCancellationRequested){using var client=await server.AcceptTcpClientAsync(stop.Token);using var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);string? first=await reader.ReadLineAsync(stop.Token);if(first is null)continue;lock(requests)requests.Add(first);while(!string.IsNullOrEmpty(await reader.ReadLineAsync(stop.Token))){}byte[] body=Encoding.UTF8.GetBytes("owned collector"),header=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/html\r\nContent-Length: "+body.Length+"\r\nConnection: close\r\n\r\n");await stream.WriteAsync(header,stop.Token);await stream.WriteAsync(body,stop.Token);}}catch(Exception error)when(error is OperationCanceledException or ObjectDisposedException or SocketException){} }
    public void Dispose(){stop.Cancel();server.Stop();stop.Dispose();}
}
internal static class Native
{
    internal static Win32Exception Error(string text)=>new(Marshal.GetLastWin32Error(),text);
    internal static string Name(IntPtr handle){var b=new StringBuilder(512);if(!GetUserObjectInformation(handle,2,b,b.Capacity*2,out _))throw Error("OBJECT_NAME_FAILED");return b.ToString();}
    internal static HashSet<uint> WindowPids(IntPtr desktop){var ids=new HashSet<uint>();bool Visit(IntPtr hwnd,IntPtr _){GetWindowThreadProcessId(hwnd,out uint id);ids.Add(id);EnumChildWindows(hwnd,(child,_)=>{GetWindowThreadProcessId(child,out uint childId);ids.Add(childId);return true;},IntPtr.Zero);return true;}if(!EnumDesktopWindows(desktop,Visit,IntPtr.Zero))throw Error("ENUM_OWNED_WINDOWS_FAILED");return ids;}
    [StructLayout(LayoutKind.Sequential)]internal struct SecurityAttributes{public int Length;public IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]internal struct StartupInfo{public int Size;public string? Reserved,Desktop,Title;public uint X,Y,XSize,YSize,XCountChars,YCountChars,FillAttribute,Flags;public short ShowWindow,Reserved2;public IntPtr ReservedPointer,StdInput,StdOutput,StdError;}
    [StructLayout(LayoutKind.Sequential)]internal struct ProcessInfo{public IntPtr Process,Thread;public uint ProcessId,ThreadId;}
    private delegate bool EnumWindow(IntPtr hwnd,IntPtr data);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string? directory,ref StartupInfo startup,out ProcessInfo process);
    [DllImport("kernel32.dll")]internal static extern uint WaitForSingleObject(IntPtr process,uint time);
    [DllImport("kernel32.dll",SetLastError=true)]internal static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    [DllImport("kernel32.dll")]internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]internal static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")]internal static extern IntPtr GetThreadDesktop(uint id);
    [DllImport("user32.dll")]internal static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")]internal static extern bool CloseDesktop(IntPtr handle);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]internal static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,ref SecurityAttributes attributes);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder data,int size,out int required);
    [DllImport("user32.dll",SetLastError=true)]private static extern bool EnumDesktopWindows(IntPtr desktop,EnumWindow visit,IntPtr parameter);
    [DllImport("user32.dll")]private static extern bool EnumChildWindows(IntPtr parent,EnumWindow visit,IntPtr parameter);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr handle,out uint id);
}
