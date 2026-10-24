using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using AutumnOS.Runtime;
using AutumnOS.Shell;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AutumnOS.ClipboardStationProbe;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        if(args.Length is not (10 or 12) || args[0] != "--runtime" || args[2] != "--report" || args[4] != "--leased-station" || args[6] != "--lease-nonce" || args[8] != "--lease-helper-pid" || args.Length==12&&args[10]!="--owned-desktop") return 2;
        string runtime=Path.GetFullPath(args[1]), report=Path.GetFullPath(args[3]);
        if(File.Exists(report) || !File.Exists(Path.Combine(runtime,"msedgewebview2.exe"))) return 2;
        Directory.CreateDirectory(Path.GetDirectoryName(report)!);
        var started=DateTimeOffset.UtcNow;
        // NO GUI/OLE/clipboard calls precede the private station/desktop association.
        try {
            using var station=PrivateStation.OpenLeased(args[5],args[7],uint.Parse(args[9],System.Globalization.CultureInfo.InvariantCulture),args.Length==12?args[11]:null);
            if(args.Length!=12)return station.LaunchOrdinaryChild(args,report);
            int result=1;Exception? workerFailure=null;
            var worker=new Thread(()=>{try{
                // lpDesktop was fixed by CreateProcess before CLR/COM initialization.
                station.RecordBinding("STA_delegate_before_GUI");
                Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                using var window=new ProbeWindow(runtime,report,station,started);
                _=window.Handle;station.RecordBinding("STA_form_handle_created");
                Application.Run(window);result=window.ExitCode;
            }catch(Exception error){workerFailure=error;}});
            worker.SetApartmentState(ApartmentState.STA);worker.Start();worker.Join();
            if(workerFailure is not null)throw workerFailure;
            return result;
        } catch(Exception error) {
            File.WriteAllText(File.Exists(report)?report+".bootstrap-failure.json":report,JsonSerializer.Serialize(new {schema_version=1,status="failed",stage="private_station_setup_or_window",started_utc=started,finished_utc=DateTimeOffset.UtcNow,
                failure=new {type=error.GetType().Name,error.Message,native_error=error is Win32Exception native?native.NativeErrorCode:(int?)null},clipboard_access="not_attempted_unless_owned_station_confirmed",system_changes="none"},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine("PRIVATE_STATION_PROBE_FAILED"); return 1;
        }
    }
}

internal sealed class ProbeWindow : Form
{
    private readonly string runtime, reportPath;
    private readonly PrivateStation station;
    private readonly DateTimeOffset started;
    private readonly List<object> checks=[],cases=[],events=[];
    public int ExitCode {get;private set;}=1;
    public ProbeWindow(string runtime,string reportPath,PrivateStation station,DateTimeOffset started)
    {
        this.runtime=runtime;this.reportPath=reportPath;this.station=station;this.started=started;
        Text="Owned noninteractive station clipboard test";Width=800;Height=600;ShowInTaskbar=false;
        Shown+=async(_,_)=>await RunAsync();
    }
    private void Check(string id,bool passed){checks.Add(new{id,status=passed?"passed":"failed"});if(!passed)throw new InvalidOperationException(id);}
    private async Task RunAsync()
    {
        object? failure=null;
        try {
            station.RequireCurrent();
            string control="owned-host-control-"+Guid.NewGuid().ToString("N");
            station.WriteClipboard(Handle,control); Check("private_station_native_synthetic_clipboard_positive_control",station.ReadClipboard(Handle)==control);
            Check("station_is_noninteractive_and_newly_created",station.IsNoninteractive&&!station.StationName.Equals("WinSta0",StringComparison.OrdinalIgnoreCase));
            bool all=true;foreach(string operation in new[]{"copy","cut","paste"}){bool a=await RunCaseAsync(false,operation),b=await RunCaseAsync(true,operation);all&=a&&b;}
            Check("all_six_independent_browser_A_positive_and_B_negative_cases_pass",all);
            string after="owned-host-after-"+Guid.NewGuid().ToString("N");station.WriteClipboard(Handle,after);
            Check("host_private_station_clipboard_still_writable_after_browser_job",station.ReadClipboard(Handle)==after);ExitCode=0;
        } catch(Exception error){failure=new{type=error.GetType().Name,error.Message,native_error=error is Win32Exception native?native.NativeErrorCode:(int?)null};}
        File.WriteAllText(reportPath,JsonSerializer.Serialize(new{schema_version=1,status=ExitCode==0?"passed":"failed",kind="owned_private_window_station_real_WebView2_legacy_clipboard_Job_UI_AB",
            started_utc=started,finished_utc=DateTimeOffset.UtcNow,os=Environment.OSVersion.VersionString,station=station.Description,
            clipboard_scope="Only the newly created private station; WinSta0 clipboard is never opened, read, cleared or written.",
            gesture_scope="CDP mouse and Ctrl+V keyboard events only stimulate the owned noninteractive test page; trusted activation, focus and local completion are required. No clipboard marker is inserted with CDP; it is only native clipboard data. CDP is not a security boundary.",
            policy_scope="Production response headers plus actual browser JobObject UI READCLIPBOARD|WRITECLIPBOARD=6 in B before document navigation. No kill, no sandbox disable, no breakaway, no global policy changes.",
            checks,cases,events,failure},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine(JsonSerializer.Serialize(new{report=reportPath,status=ExitCode==0?"passed":"failed",checks=checks.Count}));Close();
    }
    private async Task<bool> RunCaseAsync(bool restricted,string operation)
    {
        string label=(restricted?"B-browser-Job-UI-6":"A-no-browser-Job")+"-"+operation;
        string root=Path.Combine(Path.GetDirectoryName(reportPath)!,label+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string origin="https://owned-clipboard-"+Guid.NewGuid().ToString("N")+".autumn.invalid/";
        string baseline="owned-baseline-"+Guid.NewGuid().ToString("N"),marker="owned-page-"+Guid.NewGuid().ToString("N");
        station.WriteClipboard(Handle,baseline);if(station.ReadClipboard(Handle)!=baseline)throw new InvalidOperationException("OWNED_BASELINE_WRITE_FAILED");
        var environment=await CoreWebView2Environment.CreateAsync(runtime,Path.Combine(root,"profile"),new CoreWebView2EnvironmentOptions{AdditionalBrowserArguments=RuntimeIsolationPolicy.BrowserArguments});
        var exited=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);environment.BrowserProcessExited+=(_,_)=>exited.TrySetResult(true);
        var streams=new List<Stream>();var processes=new List<object>();bool browserExited=false,processFailed=false,privateBrowserWindow=false,jobAssigned=false;
        object? failure=null;var operations=new List<object>();var pageMessages=new List<JsonElement>();var receiverSamples=new List<object>();bool passed=false;uint browserPid=0,sequenceBefore=0,sequenceAfter=0;string? finalClipboard=null;
        using var view=new WebView2{Dock=DockStyle.Fill};Controls.Add(view);BrowserClipboardBoundary? boundary=null;Process? captured=null;
        try {
            await view.EnsureCoreWebView2Async(environment).WaitAsync(TimeSpan.FromSeconds(20));
            if(!RuntimeIsolationPolicy.IsSupportedBrowserVersion(environment.BrowserVersionString))throw new InvalidOperationException("FIXED_RUNTIME_POLICY_VERSION_UNSUPPORTED");
            station.RecordBinding(label+"_STA_after_WebView");
            var core=view.CoreWebView2;browserPid=core.BrowserProcessId;
            captured=Process.GetProcessById(checked((int)browserPid));_ = captured.Handle;
            station.RequireOrdinaryBrowser(captured);
            station.RequireCurrent();var ownWindows=station.ListPrivateDesktopWindows();
            privateBrowserWindow=ownWindows.Any(item=>item.process_id==browserPid);
            processes.Add(new{stage="controller_initialized",browser_pid=browserPid,browser_medium_unelevated_same_user=true,private_desktop_windows=ownWindows,engine_processes=environment.GetProcessInfos().Select(p=>new{pid=p.ProcessId,kind=p.Kind.ToString()}).ToArray()});
            if(!privateBrowserWindow)throw new InvalidOperationException("BROWSER_PRIVATE_DESKTOP_NOT_PROVEN_NO_CLIPBOARD_PROBE_ALLOWED");
            if(restricted){boundary=BrowserClipboardBoundary.CreateForOwnedBrowser(captured,browserPid,runtime);boundary.VerifyCurrentBrowser(browserPid);jobAssigned=true;}
            core.Settings.AreHostObjectsAllowed=false;core.Settings.AreDevToolsEnabled=false;core.Settings.AreDefaultContextMenusEnabled=false;
            core.Settings.AreDefaultScriptDialogsEnabled=false;core.Settings.IsPasswordAutosaveEnabled=false;core.Settings.IsGeneralAutofillEnabled=false;
            core.PermissionRequested+=(_,e)=>{e.State=CoreWebView2PermissionState.Deny;e.Handled=true;};
            core.NewWindowRequested+=(_,e)=>e.Handled=true;core.DownloadStarting+=(_,e)=>e.Cancel=true;core.LaunchingExternalUriScheme+=(_,e)=>e.Cancel=true;
            core.NavigationStarting+=(_,e)=>{if(e.Uri!=origin+"index.html")e.Cancel=true;};core.FrameNavigationStarting+=(_,e)=>e.Cancel=true;
            core.ProcessFailed+=(_,e)=>{processFailed=true;events.Add(new{@case=label,kind=e.ProcessFailedKind.ToString(),reason=e.Reason.ToString(),exit_code=e.ExitCode});};
            core.AddWebResourceRequestedFilter("*",CoreWebView2WebResourceContext.All,CoreWebView2WebResourceRequestSourceKinds.All);
            core.WebResourceRequested+=(_,e)=>{
                bool page=e.Request.Uri==origin+"index.html",script=e.Request.Uri==origin+"probe.js",allowed=e.Request.Method=="GET"&&(page||script);
                string content=page?"<!doctype html><meta charset=utf-8><title>Private station owned clipboard probe</title><button id=copy>Copy owned marker</button><button id=cut>Cut owned marker</button><button id=prepare>Prepare paste target</button><textarea id=selected></textarea><textarea id=paste></textarea><script src=probe.js defer></script>":script?Script.Replace("__MARKER__",JsonSerializer.Serialize(marker),StringComparison.Ordinal):"blocked";
                var stream=new MemoryStream(Encoding.UTF8.GetBytes(content),false);streams.Add(stream);
                e.Response=environment.CreateWebResourceResponse(stream,allowed?200:403,allowed?"OK":"Forbidden","Content-Type: "+(page?"text/html":"text/javascript")+"\r\n"+RuntimeIsolationPolicy.ResponseHeaders);
            };
            var nav=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);core.NavigationCompleted+=(_,e)=>nav.TrySetResult(e.IsSuccess);
            var messages=new[]{"ready","copy","cut","paste_ready","paste_keydown","paste_result"}.ToDictionary(key=>key,_=>new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously),StringComparer.Ordinal);
            core.WebMessageReceived+=(_,e)=>{if(e.Source==origin+"index.html"){using var doc=JsonDocument.Parse(e.WebMessageAsJson);var value=doc.RootElement.Clone();pageMessages.Add(value);if(value.TryGetProperty("phase",out var phase)&&messages.TryGetValue(phase.GetString()??"",out var completion))completion.TrySetResult(value);}};
            core.Navigate(origin+"index.html");if(!await nav.Task.WaitAsync(TimeSpan.FromSeconds(15)))throw new InvalidOperationException("OWNED_DOCUMENT_NAVIGATION_FAILED");
            await messages["ready"].Task.WaitAsync(TimeSpan.FromSeconds(10));
            if(processFailed)throw new InvalidOperationException("BROWSER_PROCESS_FAILED_BEFORE_COPY");boundary?.VerifyCurrentBrowser(core.BrowserProcessId);
            station.RequireCurrent();if(!station.ListPrivateDesktopWindows().Any(w=>w.process_id==browserPid))throw new InvalidOperationException("BROWSER_PRIVATE_DESKTOP_BINDING_LOST");
            // Activation affects only this proven noninteractive station; never SwitchDesktop.
            Activate();view.Focus();await Task.Delay(100);
            if(station.ReadClipboard(Handle)!=baseline)throw new InvalidOperationException("BASELINE_CHANGED_BEFORE_OPERATION");
            sequenceBefore=station.ClipboardSequence();
            if(operation is "copy" or "cut"){
                string expected=marker+"-"+operation;
                boundary?.VerifyCurrentBrowser(core.BrowserProcessId);
                await ClickAsync(core,operation);
                JsonElement observed=await messages[operation].Task.WaitAsync(TimeSpan.FromSeconds(10));
                bool executed=observed.GetProperty("trustedClick").GetBoolean()&&observed.GetProperty("userActivation").GetBoolean()&&observed.GetProperty("documentFocused").GetBoolean()&&observed.GetProperty("attempted").GetBoolean();
                passed=executed&&(restricted||observed.GetProperty("execCommandResult").GetBoolean()&&(operation!="cut"||observed.GetProperty("selectionAfter").GetString()==""));
                operations.Add(new{operation,activation_and_command=passed?"passed":"failed",baseline,expected_marker=expected,observed});
            }else{
            // The random native baseline/read marker never enters page source, ExecuteScript or CDP input.
            await ClickAsync(core,"prepare");JsonElement prepared=await messages["paste_ready"].Task.WaitAsync(TimeSpan.FromSeconds(10));
            await KeyAsync(core,"rawKeyDown","Control","ControlLeft",17,2);
            await KeyAsync(core,"rawKeyDown","v","KeyV",86,2);
            await KeyAsync(core,"keyUp","v","KeyV",86,2);
            await KeyAsync(core,"keyUp","Control","ControlLeft",17,0);
            JsonElement key=await messages["paste_keydown"].Task.WaitAsync(TimeSpan.FromSeconds(10));
            await core.ExecuteScriptAsync("window.reportPasteResult()");
            JsonElement paste=await messages["paste_result"].Task.WaitAsync(TimeSpan.FromSeconds(10));
            bool keyboard=key.GetProperty("trusted").GetBoolean()&&key.GetProperty("control").GetBoolean()&&key.GetProperty("userActivation").GetBoolean()&&key.GetProperty("documentFocused").GetBoolean()&&key.GetProperty("activeElement").GetString()=="paste";
            passed=keyboard&&prepared.GetProperty("trustedClick").GetBoolean()&&prepared.GetProperty("documentFocused").GetBoolean()&&paste.GetProperty("complete").GetBoolean();
            operations.Add(new{operation="read_via_native_paste",activation_and_local_completion=passed?"passed":"failed",native_only_marker=baseline,prepared,key,marker_never_sent_to_page=true});
            }
            // Observe the owned receiver and page repeatedly; never overwrite this baseline
            // until this independent browser has naturally exited. Sequence catches late
            // writes even if the value were later restored by a browser process.
            for(int sample=0;sample<10;sample++){
                await Task.Delay(100);boundary?.VerifyCurrentBrowser(core.BrowserProcessId);
                string? sampleValue=station.ReadClipboard(Handle);uint sampleSequence=station.ClipboardSequence();
                receiverSamples.Add(new{sample,sequence=sampleSequence,value=sampleValue});
                if(operation=="paste")await core.ExecuteScriptAsync("window.reportPasteResult()");
            }
            processes.Add(new{stage="observation_complete_before_normal_dispose",engine_processes=environment.GetProcessInfos().Select(p=>new{pid=p.ProcessId,kind=p.Kind.ToString()}).ToArray()});
        }catch(Exception error){passed=false;failure=new{type=error.GetType().Name,error.Message,native_error=error is Win32Exception native?native.NativeErrorCode:(int?)null};}
        finally{
            view.Dispose();Controls.Remove(view);foreach(var stream in streams)stream.Dispose();
            try{browserExited=await exited.Task.WaitAsync(TimeSpan.FromSeconds(12));}catch(TimeoutException){}
            boundary?.Dispose();captured?.Dispose();
        }
        if(browserExited){
            finalClipboard=station.ReadClipboard(Handle);sequenceAfter=station.ClipboardSequence();
            passed&=(restricted||operation=="paste")?sequenceBefore!=0&&sequenceAfter==sequenceBefore&&finalClipboard==baseline:sequenceBefore!=0&&sequenceAfter!=sequenceBefore&&finalClipboard==marker+"-"+operation;
            if(operation=="paste"){
                var snapshots=pageMessages.Where(m=>m.GetProperty("phase").GetString()=="paste_result").ToArray();
                var pasteEvents=pageMessages.Where(m=>m.GetProperty("phase").GetString()=="paste_event").ToArray();
                passed&=snapshots.Length>=10&&(restricted?snapshots.All(m=>m.GetProperty("value").GetString()=="")&&pasteEvents.All(m=>m.GetProperty("text").GetString()==""):snapshots.Any(m=>m.GetProperty("value").GetString()==baseline)&&pasteEvents.Any(m=>m.GetProperty("trusted").GetBoolean()&&m.GetProperty("text").GetString()==baseline));
            }
        }
        passed&=browserExited&&!processFailed;
        cases.Add(new{label,status=passed?"passed":"failed",runtime=environment.BrowserVersionString,browser_pid=browserPid,private_browser_window_proven=privateBrowserWindow,job_assigned=jobAssigned,job_ui_limits=restricted?6:0,
            operation,baseline,sequence_before=sequenceBefore,sequence_after_browser_exit=sequenceAfter,clipboard_after_browser_exit=finalClipboard,receiverSamples,pageMessages,operations,browser_exited=browserExited,process_failed=processFailed,processes,failure});
        if(!browserExited)throw new InvalidOperationException("BROWSER_NOT_EXITED_NO_NEXT_CASE_OR_BASELINE_REWRITE");
        return passed;
    }
    private static async Task ClickAsync(CoreWebView2 core,string id){
        using var geometry=JsonDocument.Parse(await core.ExecuteScriptAsync("(()=>{const r=document.getElementById("+JsonSerializer.Serialize(id)+").getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2}})()"));
        double x=geometry.RootElement.GetProperty("x").GetDouble(),y=geometry.RootElement.GetProperty("y").GetDouble();
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type="mousePressed",x,y,button="left",buttons=1,clickCount=1}));
        await core.CallDevToolsProtocolMethodAsync("Input.dispatchMouseEvent",JsonSerializer.Serialize(new{type="mouseReleased",x,y,button="left",buttons=0,clickCount=1}));
    }
    private static Task<string> KeyAsync(CoreWebView2 core,string type,string key,string code,int virtualKey,int modifiers)=>core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",JsonSerializer.Serialize(new{type,key,code,windowsVirtualKeyCode=virtualKey,nativeVirtualKeyCode=virtualKey,modifiers}));
    private const string Script="""
      'use strict';
      for(const operation of ['copy','cut'])document.getElementById(operation).addEventListener('click',event=>{
        const result={phase:operation,attempted:true,trustedClick:event.isTrusted,userActivation:navigator.userActivation.isActive,documentFocused:document.hasFocus()};
        const selected=document.getElementById('selected');selected.value=__MARKER__+'-'+operation;selected.focus();selected.select();result.selectionBefore=selected.value;
        try{result.execCommandResult=document.execCommand(operation);}catch(e){result.error=e.name;}
        result.selectionAfter=selected.value;
        chrome.webview.postMessage(result);
      });
      const paste=document.getElementById('paste'),pasteEvents=[];
      paste.addEventListener('paste',e=>{const event={phase:'paste_event',trusted:e.isTrusted,text:e.clipboardData.getData('text/plain')};pasteEvents.push(event);chrome.webview.postMessage(event);});
      paste.addEventListener('keydown',e=>{if(e.ctrlKey&&e.key.toLowerCase()==='v')chrome.webview.postMessage({phase:'paste_keydown',trusted:e.isTrusted,control:e.ctrlKey,userActivation:navigator.userActivation.isActive,documentFocused:document.hasFocus(),activeElement:document.activeElement.id});});
      document.getElementById('prepare').addEventListener('click',e=>{paste.value='';pasteEvents.length=0;paste.focus();chrome.webview.postMessage({phase:'paste_ready',trustedClick:e.isTrusted,userActivation:navigator.userActivation.isActive,documentFocused:document.hasFocus(),activeElement:document.activeElement.id});});
      window.reportPasteResult=()=>chrome.webview.postMessage({phase:'paste_result',complete:true,value:paste.value,pasteEvents,documentFocused:document.hasFocus(),activeElement:document.activeElement.id});
      chrome.webview.postMessage({phase:'ready',loaded:true});
      """;
}

internal sealed class PrivateStation : IDisposable
{
    private readonly IntPtr station,desktop;
    private SafeProcessHandle? helper;
    private object? leaseEvidence;
    private readonly List<object> bindings=[];
    public string StationName{get;}
    public string DesktopName{get;}
    public bool IsNoninteractive=>!Visible(station);
    public object Description=>new{station=StationName,desktop=DesktopName,interactive=!IsNoninteractive,created_before_GUI=true,create_only=true,name_source=helper is null?"OS_generated_for_logon_no_admin_custom_name":"fresh_random_authenticated_helper_lease",lease=leaseEvidence,bindings,real_clipboard_touched=false};
    private PrivateStation(IntPtr station,IntPtr desktop,string stationName,string desktopName){this.station=station;this.desktop=desktop;StationName=stationName;DesktopName=desktopName;}
    public static PrivateStation Create()
    {
        string desktopName="OwnedDesktop_"+Guid.NewGuid().ToString("N");
        var attributes=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Inherit=true};
        // Microsoft restricts caller-supplied station names to administrators. NULL uses the OS
        // name; CREATE_ONLY proves it was newly created, never a pre-existing shared station.
        IntPtr station=CreateWindowStation(null,1,0x0000037F,ref attributes);if(station==IntPtr.Zero)throw Error("CREATE_PRIVATE_STATION_FAILED");
        string stationName=Name(station);if(stationName.Equals("WinSta0",StringComparison.OrdinalIgnoreCase)||Visible(station))throw new InvalidOperationException("PRIVATE_STATION_MUST_BE_NEW_AND_NONINTERACTIVE");
        if(!SetProcessWindowStation(station))throw Error("SET_PRIVATE_STATION_FAILED");
        IntPtr desktop=CreateDesktop(desktopName,IntPtr.Zero,IntPtr.Zero,0,0x000F01FF,ref attributes);if(desktop==IntPtr.Zero)throw Error("CREATE_PRIVATE_DESKTOP_FAILED");
        if(!SetThreadDesktop(desktop))throw Error("SET_PRIVATE_DESKTOP_FAILED");
        var result=new PrivateStation(station,desktop,stationName,desktopName);result.RequireCurrent();return result;
    }
    public static PrivateStation OpenLeased(string stationName,string nonce,uint helperPid,string? existingDesktop=null)
    {
        // A ready JSON file is never accepted as identity or ownership proof.
        if(nonce.Length!=32||nonce.Any(c=>c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f'))||stationName!="AutumnOS_ClipboardProbe_"+nonce||helperPid==0||helperPid==Environment.ProcessId)
            throw new InvalidOperationException("OWNED_LEASE_BINDING_INVALID");
        if(existingDesktop is not null&&(!System.Text.RegularExpressions.Regex.IsMatch(existingDesktop,"\\AOwnedDesktop_[0-9a-f]{32}\\z")||Name(GetProcessWindowStation())!=stationName||Name(GetThreadDesktop(GetCurrentThreadId()))!=existingDesktop))
            throw new InvalidOperationException("CHILD_STARTUP_STATION_DESKTOP_BINDING_INVALID");
        TokenFacts current=GetTokenFacts(GetCurrentProcess());
        if(current.Elevated||current.Integrity<0x2000||current.Integrity>=0x3000)throw new InvalidOperationException("PROBE_MUST_RUN_AS_UNELEVATED_MEDIUM_USER");
        var helper=OpenProcess(0x00101000,false,helperPid);if(helper.IsInvalid)throw Error("OWNED_LEASE_HELPER_QUERY_FAILED");
        try{
            if(GetProcessId(helper)!=helperPid||WaitForSingleObject(helper,0)!=258)throw new InvalidOperationException("OWNED_LEASE_HELPER_NOT_LIVE");
            if(!ProcessIdToSessionId(helperPid,out uint helperSession)||!ProcessIdToSessionId((uint)Environment.ProcessId,out uint currentSession)||helperSession!=currentSession)throw new InvalidOperationException("OWNED_LEASE_HELPER_SESSION_REJECTED");
            TokenFacts elevated=GetTokenFacts(helper.DangerousGetHandle());
            if(elevated.Sid!=current.Sid||!elevated.Elevated||elevated.Integrity<0x3000)throw new InvalidOperationException("OWNED_LEASE_HELPER_TOKEN_REJECTED");
            IntPtr station=OpenWindowStation(stationName,false,0x0002037F);if(station==IntPtr.Zero)throw Error("OPEN_OWNED_LEASE_STATION_FAILED");
            if(Name(station)!=stationName||Visible(station))throw new InvalidOperationException("OWNED_LEASE_STATION_NAME_OR_VISIBILITY_REJECTED");
            uint result=GetSecurityInfo(station,7,5,out IntPtr owner,out _,out _,out _,out IntPtr descriptor);if(result!=0)throw new Win32Exception((int)result,"OWNED_LEASE_STATION_OWNER_QUERY_FAILED");
            try{
                if(new SecurityIdentifier(owner).Value!=current.Sid)throw new InvalidOperationException("OWNED_LEASE_STATION_OWNER_REJECTED");
                uint bytes=GetSecurityDescriptorLength(descriptor);if(bytes>4096)throw new InvalidOperationException("OWNED_LEASE_STATION_DESCRIPTOR_SIZE_REJECTED");var raw=new byte[bytes];Marshal.Copy(descriptor,raw,0,raw.Length);var security=new RawSecurityDescriptor(raw,0);
                if(security.DiscretionaryAcl is not RawAcl acl||acl.Count!=2||acl.Cast<GenericAce>().Any(ace=>ace is not CommonAce allowed||allowed.AceQualifier!=AceQualifier.AccessAllowed||allowed.AceFlags!=AceFlags.None||allowed.AccessMask!=0x000F037F||allowed.SecurityIdentifier.Value!=current.Sid&&allowed.SecurityIdentifier.Value!="S-1-5-18")||acl.Cast<CommonAce>().Select(ace=>ace.SecurityIdentifier.Value).Distinct(StringComparer.Ordinal).Count()!=2)
                    throw new InvalidOperationException("OWNED_LEASE_STATION_DACL_REJECTED");
            }finally{LocalFree(descriptor);}
            var stationFlags=new UserObjectFlags{Inherit=true};
            if(!SetUserObjectInformation(station,1,ref stationFlags,Marshal.SizeOf<UserObjectFlags>())||!GetUserObjectFlags(station,1,out var checkedFlags,Marshal.SizeOf<UserObjectFlags>(),out _)||!checkedFlags.Inherit)
                throw Error("OWNED_STATION_INHERITANCE_FAILED");
            if(!SetProcessWindowStation(station))throw Error("SET_OWNED_LEASE_STATION_FAILED");
            string desktopName=existingDesktop??("OwnedDesktop_"+Guid.NewGuid().ToString("N"));var attributes=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Inherit=true};
            IntPtr desktop=existingDesktop is null?CreateDesktop(desktopName,IntPtr.Zero,IntPtr.Zero,0,0x000F01FF,ref attributes):OpenDesktop(desktopName,0,true,0x000F01FF);if(desktop==IntPtr.Zero)throw Error("CREATE_OR_OPEN_OWNED_LEASE_DESKTOP_FAILED");
            if(existingDesktop is null&&!SetThreadDesktop(desktop))throw Error("SET_OWNED_LEASE_DESKTOP_FAILED");
            var lease=new PrivateStation(station,desktop,stationName,desktopName){helper=helper,leaseEvidence=new{helper_pid=helperPid,session_matches=true,helper_same_user=true,helper_high_integrity=true,probe_medium_unelevated=true,kernel_owner_matches=true,kernel_dacl_only_current_user_and_system=true,nonce_bound_to_name=true}};
            lease.RecordBinding(existingDesktop is null?"MTA_bootstrap_created_desktop":"MTA_child_explicit_startup_desktop");return lease;
        }catch{helper.Dispose();throw;}
    }
    public void RequireCurrent(){if(Name(GetProcessWindowStation())!=StationName||Name(GetThreadDesktop(GetCurrentThreadId()))!=DesktopName||StationName.Equals("WinSta0",StringComparison.OrdinalIgnoreCase)||helper is not null&&WaitForSingleObject(helper,0)!=258)throw new InvalidOperationException("PRIVATE_STATION_BINDING_REQUIRED");}
    public void RecordBinding(string stage){RequireCurrent();bindings.Add(new{stage,station=Name(GetProcessWindowStation()),desktop=Name(GetThreadDesktop(GetCurrentThreadId())),apartment=Thread.CurrentThread.GetApartmentState().ToString(),thread=GetCurrentThreadId()});}
    public int LaunchOrdinaryChild(string[] args,string report)
    {
        RequireCurrent();string executable=Environment.ProcessPath!;
        var startup=new StartupInfo{Size=Marshal.SizeOf<StartupInfo>(),Desktop=StationName+"\\"+DesktopName};
        var command=new StringBuilder(string.Join(" ",new[]{executable}.Concat(args).Concat(new[]{"--owned-desktop",DesktopName}).Select(Quote)));
        if(!CreateProcess(executable,command,IntPtr.Zero,IntPtr.Zero,false,0x08000000,IntPtr.Zero,null,ref startup,out var child))throw Error("CREATE_ORDINARY_OWNED_DESKTOP_CHILD_FAILED");
        try{
            if(WaitForSingleObjectRaw(child.Process,240000)!=0)throw new TimeoutException("OWNED_CHILD_TIMEOUT_NO_FORCE_KILL");
            if(!GetExitCodeProcess(child.Process,out uint code))throw Error("OWNED_CHILD_EXIT_CODE_FAILED");
            File.WriteAllText(report+".bootstrap.json",JsonSerializer.Serialize(new{status=code==0?"passed":"failed",child_pid=child.ProcessId,child_exit_code=code,station=Description,startup_desktop_explicit=true,elevation="ordinary_CreateProcess_same_token",clipboard_calls_in_bootstrap="none"},new JsonSerializerOptions{WriteIndented=true}));
            return (int)code;
        }finally{CloseHandle(child.Thread);CloseHandle(child.Process);}
    }
    private static string Quote(string value){var b=new StringBuilder("\"");int slashes=0;foreach(char c in value){if(c=='\\'){slashes++;continue;}b.Append('\\',c=='\"'?slashes*2+1:slashes).Append(c);slashes=0;}return b.Append('\\',slashes*2).Append('"').ToString();}
    public void RequireOrdinaryBrowser(Process browser){TokenFacts actual=GetTokenFacts(browser.Handle),current=GetTokenFacts(GetCurrentProcess());if(actual.Sid!=current.Sid||actual.Elevated||actual.Integrity<0x2000||actual.Integrity>=0x3000)throw new InvalidOperationException("BROWSER_MUST_RUN_AS_UNELEVATED_MEDIUM_USER");}
    public uint ClipboardSequence(){RequireCurrent();uint value=GetClipboardSequenceNumber();if(value==0)throw new InvalidOperationException("OWNED_CLIPBOARD_SEQUENCE_UNAVAILABLE");return value;}
    public List<WindowInfo> ListPrivateDesktopWindows()
    {
        RequireCurrent();var result=new List<WindowInfo>();
        bool Visit(IntPtr hwnd,IntPtr _){GetWindowThreadProcessId(hwnd,out uint processId);result.Add(new(hwnd.ToInt64(),processId));EnumChildWindows(hwnd,(child,_)=>{GetWindowThreadProcessId(child,out uint childId);result.Add(new(child.ToInt64(),childId));return true;},IntPtr.Zero);return true;}
        if(!EnumDesktopWindows(desktop,Visit,IntPtr.Zero))throw Error("PRIVATE_DESKTOP_ENUM_FAILED");return result;
    }
    public void WriteClipboard(IntPtr owner,string value)
    {
        RequireCurrent();if(!OpenClipboard(owner))throw Error("OWNED_OPEN_CLIPBOARD_FAILED");
        IntPtr memory=IntPtr.Zero;
        try{if(!EmptyClipboard())throw Error("OWNED_EMPTY_CLIPBOARD_FAILED");byte[] bytes=Encoding.Unicode.GetBytes(value+'\0');memory=GlobalAlloc(2,(UIntPtr)bytes.Length);if(memory==IntPtr.Zero)throw Error("OWNED_CLIPBOARD_ALLOC_FAILED");IntPtr target=GlobalLock(memory);if(target==IntPtr.Zero)throw Error("OWNED_CLIPBOARD_LOCK_FAILED");try{Marshal.Copy(bytes,0,target,bytes.Length);}finally{GlobalUnlock(memory);}if(SetClipboardData(13,memory)==IntPtr.Zero)throw Error("OWNED_CLIPBOARD_SET_FAILED");memory=IntPtr.Zero;}
        finally{if(memory!=IntPtr.Zero)GlobalFree(memory);CloseClipboard();}
    }
    public string? ReadClipboard(IntPtr owner)
    {
        RequireCurrent();if(!OpenClipboard(owner))throw Error("OWNED_OPEN_CLIPBOARD_FAILED");try{IntPtr data=GetClipboardData(13);if(data==IntPtr.Zero)return null;UIntPtr size=GlobalSize(data);if(size.ToUInt64()>4096)throw new InvalidOperationException("OWNED_CLIPBOARD_SIZE_UNEXPECTED");IntPtr value=GlobalLock(data);if(value==IntPtr.Zero)throw Error("OWNED_CLIPBOARD_LOCK_FAILED");try{return Marshal.PtrToStringUni(value);}finally{GlobalUnlock(data);}}finally{CloseClipboard();}
    }
    private static string Name(IntPtr handle){var text=new StringBuilder(512);if(!GetUserObjectInformation(handle,2,text,text.Capacity*2,out _))throw Error("PRIVATE_USER_OBJECT_QUERY_FAILED");return text.ToString();}
    private static bool Visible(IntPtr handle){if(!GetUserObjectFlags(handle,1,out var flags,Marshal.SizeOf<UserObjectFlags>(),out _))throw Error("PRIVATE_STATION_FLAGS_FAILED");return (flags.Flags&1)!=0;}
    private static Win32Exception Error(string stage)=>new(Marshal.GetLastWin32Error(),stage);
    // Current station/desktop cannot be closed while assigned. OS closes owned handles at this
    // short-lived process exit; never switch this process back to the user's real station.
    public void Dispose(){helper?.Dispose();}
    private sealed record TokenFacts(string Sid,uint Integrity,bool Elevated);
    private static TokenFacts GetTokenFacts(IntPtr process)
    {
        if(!OpenProcessToken(process,8,out IntPtr token))throw Error("OWNED_LEASE_TOKEN_OPEN_FAILED");
        try{string sid="";uint integrity=0;bool elevated=false;foreach(int kind in new[]{1,25,20}){GetTokenInformation(token,kind,IntPtr.Zero,0,out int size);IntPtr buffer=Marshal.AllocHGlobal(size);try{if(!GetTokenInformation(token,kind,buffer,size,out _))throw Error("OWNED_LEASE_TOKEN_QUERY_FAILED");if(kind==1)sid=new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;else if(kind==20)elevated=Marshal.ReadInt32(buffer)!=0;else{IntPtr label=Marshal.ReadIntPtr(buffer);byte count=Marshal.ReadByte(GetSidSubAuthorityCount(label));integrity=unchecked((uint)Marshal.ReadInt32(GetSidSubAuthority(label,(uint)(count-1))));}}finally{Marshal.FreeHGlobal(buffer);}}return new(sid,integrity,elevated);}finally{CloseHandle(token);}
    }
    internal sealed record WindowInfo(long hwnd,uint process_id);
    [StructLayout(LayoutKind.Sequential)]private struct SecurityAttributes{public int Length;public IntPtr SecurityDescriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit;}
    [StructLayout(LayoutKind.Sequential)]private struct UserObjectFlags{[MarshalAs(UnmanagedType.Bool)]public bool Inherit;[MarshalAs(UnmanagedType.Bool)]public bool Reserved;public uint Flags;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct StartupInfo{public int Size;public string? Reserved,Desktop,Title;public uint X,Y,XSize,YSize,XCountChars,YCountChars,FillAttribute,Flags;public short ShowWindow,Reserved2;public IntPtr ReservedPointer,StdInput,StdOutput,StdError;}
    [StructLayout(LayoutKind.Sequential)]private struct ProcessInfo{public IntPtr Process,Thread;public uint ProcessId,ThreadId;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateProcess(string application,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inheritHandles,uint flags,IntPtr environment,string? directory,ref StartupInfo startup,out ProcessInfo process);
    [DllImport("kernel32.dll",EntryPoint="WaitForSingleObject",SetLastError=true)]private static extern uint WaitForSingleObjectRaw(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenDesktop(string name,uint flags,bool inherit,uint access);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool SetUserObjectInformation(IntPtr handle,int index,ref UserObjectFlags flags,int size);
    [DllImport("user32.dll")]private static extern uint GetClipboardSequenceNumber();
    private delegate bool EnumWindow(IntPtr hwnd,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateWindowStation(string? name,uint flags,uint access,ref SecurityAttributes attributes);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenWindowStation(string name,bool inherit,uint access);
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetProcessWindowStation(IntPtr station);
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateDesktop(string name,IntPtr device,IntPtr mode,uint flags,uint access,ref SecurityAttributes attributes);
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetThreadDesktop(IntPtr desktop);
    [DllImport("user32.dll")]private static extern IntPtr GetProcessWindowStation();
    [DllImport("user32.dll")]private static extern IntPtr GetThreadDesktop(uint thread);
    [DllImport("kernel32.dll")]private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetUserObjectInformation(IntPtr handle,int index,StringBuilder buffer,int size,out int required);
    [DllImport("user32.dll",EntryPoint="GetUserObjectInformationW",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetUserObjectFlags(IntPtr handle,int index,out UserObjectFlags buffer,int size,out int required);
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool EnumDesktopWindows(IntPtr desktop,EnumWindow callback,IntPtr data);
    [DllImport("user32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool EnumChildWindows(IntPtr parent,EnumWindow callback,IntPtr data);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint processId);
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseClipboard();
    [DllImport("user32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool EmptyClipboard();
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr SetClipboardData(uint format,IntPtr data);
    [DllImport("user32.dll",SetLastError=true)]private static extern IntPtr GetClipboardData(uint format);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr GlobalAlloc(uint flags,UIntPtr size);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr GlobalLock(IntPtr data);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GlobalUnlock(IntPtr data);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern IntPtr GlobalFree(IntPtr data);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern UIntPtr GlobalSize(IntPtr data);
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll")]private static extern uint GetProcessId(SafeProcessHandle handle);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeProcessHandle handle,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool ProcessIdToSessionId(uint pid,out uint session);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(IntPtr process,uint access,out IntPtr token);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool GetTokenInformation(IntPtr token,int kind,IntPtr value,int size,out int needed);
    [DllImport("advapi32.dll")]private static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll")]private static extern IntPtr GetSidSubAuthority(IntPtr sid,uint index);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityInfo(IntPtr handle,int type,uint information,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr value);
}
