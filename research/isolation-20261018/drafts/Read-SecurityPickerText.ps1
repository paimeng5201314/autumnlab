[CmdletBinding(DefaultParameterSetName='Picker')]
param(
    [Parameter(Mandatory,ParameterSetName='Picker')][ValidateRange(1,2147483647)][int]$PickerProcessId,
    [Parameter(Mandatory,ParameterSetName='Picker')][DateTimeOffset]$ExpectedStartTime,
    [Parameter(Mandatory,ParameterSetName='Product')][ValidateRange(1,2147483647)][int]$ProductProcessId,
    [switch]$CaptureWindowIfNoStaticText,
    [string]$ReportPath
)
# Read only a caller-identified, live Windows Security PickerHost window.
# Only static Text control Names are read. Never Edit/Value, buttons, file lists,
# credentials, command lines, clicks, focus changes or security-policy mutations.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
if (-not ('AutumnSecurityReadOnly.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace AutumnSecurityReadOnly {
 public static class Native {
  public delegate bool EnumProc(IntPtr h,IntPtr p);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
  [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p,IntPtr l);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h,StringBuilder s,int n);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h,StringBuilder s,int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h,out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h,IntPtr dc,uint f);
  public static IntPtr[] TopWindows() { var list=new List<IntPtr>(); EnumWindows((h,p)=>{list.Add(h);return true;},IntPtr.Zero);return list.ToArray(); }
  public static uint ProcessId(IntPtr h) { uint p;GetWindowThreadProcessId(h,out p);return p; }
  public static string Title(IntPtr h) { var b=new StringBuilder(256);GetWindowText(h,b,b.Capacity);return b.ToString(); }
  public static string ClassName(IntPtr h) { var b=new StringBuilder(256);GetClassName(h,b,b.Capacity);return b.ToString(); }
 }
}
'@
}
if ($PSCmdlet.ParameterSetName -eq 'Product') {
    $product = [Diagnostics.Process]::GetProcessById($ProductProcessId)
    try {
        $null = $product.Handle
        $productStarted = $product.StartTime.ToUniversalTime()
        $productSession = $product.SessionId
        $productWindow = $product.MainWindowHandle.ToInt64()
        $pickerSnapshots = @(
            foreach ($candidate in @(Get-Process -Name PickerHost -ErrorAction SilentlyContinue)) {
                try {
                    $null = $candidate.Handle
                    $candidateStarted = $candidate.StartTime.ToUniversalTime()
                    if ($candidate.SessionId -ne $productSession) { continue }
                    try {
                        $childArguments=@{PickerProcessId=$candidate.Id;ExpectedStartTime=([DateTimeOffset]$candidateStarted);CaptureWindowIfNoStaticText=$CaptureWindowIfNoStaticText}
                        if($ReportPath){$childArguments.ReportPath=[IO.Path]::ChangeExtension([IO.Path]::GetFullPath($ReportPath),('picker-'+$candidate.Id+'.json'))}
                        & $PSCommandPath @childArguments | ConvertFrom-Json
                    } catch {
                        [ordered]@{ process_id=$candidate.Id; start_utc=$candidateStarted.ToString('o');
                            result='not_read'; reason=$_.Exception.Message }
                    }
                } finally { $candidate.Dispose() }
            }
        )
        $metadata = & (Join-Path $PSScriptRoot 'Read-PickerHostMetadata.ps1') -ProductProcessId $ProductProcessId -ProductWindowHandle $productWindow | ConvertFrom-Json
        $combined = [ordered]@{
            schema='autumn-product-security-dialogs-readonly-v1'; captured_at_utc=[DateTimeOffset]::UtcNow.ToString('o')
            product_process_id=$ProductProcessId; product_start_utc=$productStarted.ToString('o')
            picker_security_static_text=$pickerSnapshots; windows_metadata=$metadata
            association_is_causal_proof=$false; actions_performed=@('Read identities','Read static Text Names','Read window metadata')
        }
        $json = $combined | ConvertTo-Json -Depth 16
        if ($ReportPath) {
            $fullReport=[IO.Path]::GetFullPath($ReportPath)
            if(Test-Path -LiteralPath $fullReport){throw "Refusing to replace an existing report: $fullReport"}
            [IO.File]::WriteAllText($fullReport,$json,[Text.UTF8Encoding]::new($false))
        }
        $json
    } finally { $product.Dispose() }
    return
}
$picker = [Diagnostics.Process]::GetProcessById($PickerProcessId)
try {
    $null = $picker.Handle
    $expectedPath = Join-Path ([Environment]::SystemDirectory) 'PickerHost.exe'
    $started = $picker.StartTime.ToUniversalTime()
    if ($picker.ProcessName -ine 'PickerHost' -or
        -not [string]::Equals($picker.MainModule.FileName,$expectedPath,[StringComparison]::OrdinalIgnoreCase) -or
        [Math]::Abs(($started-$ExpectedStartTime.UtcDateTime).TotalMilliseconds) -gt 1) {
        throw 'Exact expected system PickerHost identity was not verified.'
    }
    $foregroundWindow=[AutumnSecurityReadOnly.Native]::GetForegroundWindow()
    # Shell_SystemDialog may be absent from EnumWindows while its visible proxy is
    # enumerated. The exact foreground HWND is an independent candidate; every
    # candidate still requires the held PickerHost PID, visibility and exact title.
    $candidateWindows=@($foregroundWindow)+@([AutumnSecurityReadOnly.Native]::TopWindows())
    $securityWindows=@($candidateWindows | Select-Object -Unique | Where-Object {
        [AutumnSecurityReadOnly.Native]::ProcessId($_) -eq $PickerProcessId -and
        [AutumnSecurityReadOnly.Native]::IsWindowVisible($_) -and
        [AutumnSecurityReadOnly.Native]::ClassName($_) -ne 'Shell_SystemDialogProxy' -and
        [AutumnSecurityReadOnly.Native]::Title($_) -in @('Windows 安全中心','Windows Security')
    } | Sort-Object @{Expression={if($_ -eq $foregroundWindow){0}else{1}}})
    if ($securityWindows.Count -eq 0) {
        throw 'The expected Windows Security top-level window was not observed.'
    }
    $windowHandle=[IntPtr]$securityWindows[0]
    $windowTitle=[AutumnSecurityReadOnly.Native]::Title($windowHandle)
    $window = [Windows.Automation.AutomationElement]::FromHandle($windowHandle)
    if ($window.Current.ProcessId -ne $PickerProcessId) { throw 'UIA root process differs from held PickerHost identity.' }
    $filter = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$PickerProcessId),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Text))
    $elements = $window.FindAll([Windows.Automation.TreeScope]::Descendants,$filter)
    $texts = @(
        for ($index=0; $index -lt [Math]::Min(64,$elements.Count); $index++) {
            $element = $elements.Item($index)
            if ($element.Current.ProcessId -ne $PickerProcessId -or $element.Current.IsOffscreen -or
                $element.Current.ControlType -ne [Windows.Automation.ControlType]::Text) { continue }
            $value = $element.Current.Name
            if ($value.Length -gt 1024) { $value = $value.Substring(0,1024) }
            [ordered]@{ automation_id=$element.Current.AutomationId; text=$value }
        }
    )
    $picker.Refresh()
    if ($picker.HasExited -or $picker.StartTime.ToUniversalTime() -ne $started) { throw 'Picker identity disappeared during read-only snapshot.' }
    $capture=$null
    if($texts.Count -eq 0 -and $CaptureWindowIfNoStaticText){
        if(-not $ReportPath){throw 'A new report path is required for an exact-window capture.'}
        if([AutumnSecurityReadOnly.Native]::ProcessId($windowHandle) -ne $PickerProcessId -or
            [AutumnSecurityReadOnly.Native]::Title($windowHandle) -ne $windowTitle){throw 'Security window identity changed before capture.'}
        Add-Type -AssemblyName System.Drawing
        $rect=[AutumnSecurityReadOnly.Native+RECT]::new()
        if(-not [AutumnSecurityReadOnly.Native]::GetWindowRect($windowHandle,[ref]$rect)){throw 'No security window rectangle.'}
        $width=$rect.Right-$rect.Left;$height=$rect.Bottom-$rect.Top
        if($width -lt 1 -or $height -lt 1 -or $width -gt 4096 -or $height -gt 4096){throw 'Unexpected security window dimensions.'}
        $imagePath=[IO.Path]::ChangeExtension([IO.Path]::GetFullPath($ReportPath),'png')
        if(Test-Path -LiteralPath $imagePath){throw 'Refusing to replace an existing window capture.'}
        $bitmap=[Drawing.Bitmap]::new($width,$height)
        $graphics=[Drawing.Graphics]::FromImage($bitmap)
        try{
            $nativeContext=$graphics.GetHdc()
            try{$printed=[AutumnSecurityReadOnly.Native]::PrintWindow($windowHandle,$nativeContext,2)}finally{$graphics.ReleaseHdc($nativeContext)}
            $bitmap.Save($imagePath,[Drawing.Imaging.ImageFormat]::Png)
            $capture=[ordered]@{method='PrintWindow exact HWND only';print_window_return=$printed;width=$width;height=$height;path=$imagePath}
        }finally{$graphics.Dispose();$bitmap.Dispose()}
    }
    $report = [ordered]@{
        schema='autumn-security-picker-static-text-v1'; captured_at_utc=[DateTimeOffset]::UtcNow.ToString('o')
        read_only=$true; process_id=$PickerProcessId; start_utc=$started.ToString('o')
        executable_path=$picker.MainModule.FileName; hwnd=$windowHandle.ToInt64(); title=$windowTitle
        window_class=[AutumnSecurityReadOnly.Native]::ClassName($windowHandle); capture=$capture
        static_text=$texts; matching_text_count=$elements.Count; maximum_collected=64
        actions_performed=@('Read held process identity','Read window metadata','Read visible static Text Names')
    }
    $json = $report | ConvertTo-Json -Depth 8
    if ($ReportPath) {
        $fullReport=[IO.Path]::GetFullPath($ReportPath)
        if(Test-Path -LiteralPath $fullReport){throw "Refusing to replace an existing report: $fullReport"}
        [IO.File]::WriteAllText($fullReport,$json,[Text.UTF8Encoding]::new($false))
    }
    $json
} finally { $picker.Dispose() }
