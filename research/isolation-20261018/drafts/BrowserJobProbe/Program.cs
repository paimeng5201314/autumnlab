using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AutumnOS.Runtime;
using AutumnOS.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

internal static class Program
{
    [STAThread]private static int Main(string[] args)
    {
        if(args.Length!=4||args[0]!="--runtime"||args[2]!="--report")return 2;
        string runtime=Path.GetFullPath(args[1]),report=Path.GetFullPath(args[3]);if(File.Exists(report)||!File.Exists(Path.Combine(runtime,"msedgewebview2.exe")))return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
        using var window=new JobWindow(runtime,report);Application.Run(window);return window.ExitCode;
    }
}
internal sealed class JobWindow:Form
{
    private readonly string runtime,reportPath;
    private readonly List<object> cases=[],events=[];
    private readonly DateTimeOffset started=DateTimeOffset.UtcNow;
    public int ExitCode{get;private set;}=1;
    protected override bool ShowWithoutActivation=>true;
    protected override CreateParams CreateParams{get{var p=base.CreateParams;p.ExStyle|=0x08000000;return p;}}
    public JobWindow(string runtime,string reportPath){this.runtime=runtime;this.reportPath=reportPath;ShowInTaskbar=false;StartPosition=FormStartPosition.Manual;Location=new(-20000,-20000);Size=new(800,600);Shown+=async(_,_)=>await RunAsync();}
    private async Task RunAsync()
    {
        object? failure=null;try{bool a=await CaseAsync(false),b=await CaseAsync(true);ExitCode=a&&b?0:1;}catch(Exception error){failure=new{type=error.GetType().Name,error.Message};}
        File.WriteAllText(reportPath,JsonSerializer.Serialize(new{schema_version=1,status=ExitCode==0?"passed":"failed",kind="owned_browser_Job_UI_6_compatibility_only_NO_clipboard_APIs",
            started_utc=started,finished_utc=DateTimeOffset.UtcNow,cases,events,failure,scope="No clipboard functions or DOM clipboard operations exist in this executable's test content. A/B only validates owned browser job assignment and subsequent renderer/navigation/WASM/challenge behavior; never proves actual clipboard blocking.",
            system_changes="none",chromium_sandbox="default_enabled",breakaway_flags="none",kill_actions="none"},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(new{status=ExitCode==0?"passed":"failed",report=reportPath}));Close();
    }
    private async Task<bool> CaseAsync(bool restricted)
    {
        string label=restricted?"B-browser-Job-UI-6":"A-no-Job",root=Path.Combine(Path.GetDirectoryName(reportPath)!,label+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string origin="https://owned-job-"+Guid.NewGuid().ToString("N")+".autumn.invalid/";
        var environment=await CoreWebView2Environment.CreateAsync(runtime,Path.Combine(root,"profile"),new CoreWebView2EnvironmentOptions());
        var exited=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);environment.BrowserProcessExited+=(_,_)=>exited.TrySetResult(true);
        using var view=new WebView2{Dock=DockStyle.Fill};Controls.Add(view);BrowserClipboardBoundary? job=null;Process? captured=null;
        bool assigned=false,processFailed=false,browserExited=false,passed=false;uint pid=0;object? failure=null;JsonElement? observed=null;List<object> topology=[];List<Stream> streams=[];
        try{
            await view.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(20));var core=view.CoreWebView2;pid=core.BrowserProcessId;captured=Process.GetProcessById((int)pid);_=captured.Handle;
            topology.Add(new{stage="before_assignment",job_facts=JobFacts.Read(pid),processes=environment.GetProcessInfos().Select(p=>new{p.ProcessId,kind=p.Kind.ToString()}).ToArray()});
            if(restricted){job=BrowserClipboardBoundary.CreateForOwnedBrowser(captured,pid,runtime);job.VerifyCurrentBrowser(pid);assigned=true;}
            core.Settings.AreHostObjectsAllowed=false;core.Settings.AreDevToolsEnabled=false;core.Settings.AreDefaultScriptDialogsEnabled=false;
            core.PermissionRequested+=(_,e)=>{e.State=CoreWebView2PermissionState.Deny;e.Handled=true;};core.NewWindowRequested+=(_,e)=>e.Handled=true;core.DownloadStarting+=(_,e)=>e.Cancel=true;core.LaunchingExternalUriScheme+=(_,e)=>e.Cancel=true;
            core.NavigationStarting+=(_,e)=>{if(e.Uri!=origin+"index.html")e.Cancel=true;};core.FrameNavigationStarting+=(_,e)=>e.Cancel=true;
            core.ProcessFailed+=(_,e)=>{processFailed=true;events.Add(new{@case=label,kind=e.ProcessFailedKind.ToString(),reason=e.Reason.ToString(),exit_code=e.ExitCode});};
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All,CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested+=(_,e)=>{bool page=e.Request.Uri==origin+"index.html",script=e.Request.Uri==origin+"probe.js",allowed=e.Request.Method=="GET"&&(page||script);string text=page?"<!doctype html><meta charset=utf-8><script src=probe.js defer></script>":script?Script:"blocked";var stream=new MemoryStream(Encoding.UTF8.GetBytes(text),false);streams.Add(stream);e.Response=environment.CreateWebResourceResponse(stream,allowed?200:403,allowed?"OK":"Forbidden","Content-Type: "+(page?"text/html":"text/javascript")+"\r\n"+RuntimeIsolationPolicy.ResponseHeaders);};
            var message=new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);core.WebMessageReceived+=(_,e)=>{if(e.Source==origin+"index.html"){using var doc=JsonDocument.Parse(e.WebMessageAsJson);message.TrySetResult(doc.RootElement.Clone());}};
            core.Navigate(origin+"index.html");observed=await message.Task.WaitAsync(TimeSpan.FromSeconds(15));job?.VerifyCurrentBrowser(core.BrowserProcessId);
            topology.Add(new{stage="after_new_origin_navigation",processes=environment.GetProcessInfos().Select(p=>new{p.ProcessId,kind=p.Kind.ToString()}).ToArray()});
            passed=!processFailed&&observed.Value.GetProperty("executed").GetBoolean()&&observed.Value.GetProperty("wasm").GetInt32()==42;
        }catch(Exception error){failure=new{type=error.GetType().Name,error.Message,native_error=error is Win32Exception native?native.NativeErrorCode:(int?)null};}
        finally{view.Dispose();Controls.Remove(view);foreach(var stream in streams)stream.Dispose();try{browserExited=await exited.Task.WaitAsync(TimeSpan.FromSeconds(12));}catch(TimeoutException){}job?.Dispose();captured?.Dispose();}
        passed&=browserExited;cases.Add(new{label,status=passed?"passed":"failed",runtime=environment.BrowserVersionString,browser_pid=pid,job_assigned=assigned,job_ui_flags=restricted?6:0,observed,topology,process_failed=processFailed,browser_exited=browserExited,failure});return passed;
    }
    private const string Script="""
    'use strict';(async()=>{const result={executed:true};try{const hex='0061736d0100000001070160027f7f017f030201000707010361646400000a09010700200020016a0b';const bytes=Uint8Array.from(hex.match(/../g),v=>parseInt(v,16));const m=await WebAssembly.instantiate(bytes);result.wasm=m.instance.exports.add(20,22);}catch(e){result.error=e.name;}chrome.webview.postMessage(result);})();
    """;
}
