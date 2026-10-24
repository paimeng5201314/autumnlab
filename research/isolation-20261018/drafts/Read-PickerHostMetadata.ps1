[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateRange(1,2147483647)][int]$ProductProcessId,
    [long]$ProductWindowHandle = 0,
    [string]$ReportPath
)

# Read-only diagnostics. No activation, input, close, file-list enumeration, UIA values,
# command lines or user files. Observed association is not authorization for input.
$ErrorActionPreference = 'Stop'
if (-not ('AutumnPickerReadOnly.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace AutumnPickerReadOnly {
  public static class Native {
    public delegate bool EnumProc(IntPtr h, IntPtr p);
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr h, EnumProc p, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr h);
    [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr h);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    public static IntPtr[] TopWindows() {
      var list = new List<IntPtr>(); EnumWindows((h,p) => { list.Add(h); return true; }, IntPtr.Zero); return list.ToArray();
    }
    public static IntPtr[] ChildWindows(IntPtr parent) {
      var list = new List<IntPtr>(); EnumChildWindows(parent, (h,p) => { if(list.Count >= 128) return false; list.Add(h); return true; }, IntPtr.Zero); return list.ToArray();
    }
    public static string ClassName(IntPtr h) { var b=new StringBuilder(256); GetClassName(h,b,b.Capacity); return b.ToString(); }
    public static string Title(IntPtr h) { var b=new StringBuilder(256); GetWindowText(h,b,b.Capacity); return b.ToString(); }
    public static uint ProcessId(IntPtr h) { uint p; GetWindowThreadProcessId(h,out p); return p; }
  }
}
'@
}

function Read-ProcessIdentity([int]$ProcessIdValue) {
    if ($ProcessIdValue -eq 0) { return $null }
    $process = $null
    try {
        $process = [Diagnostics.Process]::GetProcessById($ProcessIdValue)
        $null = $process.Handle
        $path = $null; $pathError = $null
        try { $path = $process.MainModule.FileName } catch { $pathError = $_.Exception.GetType().Name }
        $cim = Get-CimInstance Win32_Process -Filter "ProcessId=$ProcessIdValue" -ErrorAction Stop
        return [ordered]@{
            process_id = $ProcessIdValue; name = $process.ProcessName
            start_utc = $process.StartTime.ToUniversalTime().ToString('o')
            session_id = $process.SessionId; executable_path = $path; path_error = $pathError
            parent_process_id = if ($cim) { [int]$cim.ParentProcessId } else { $null }
            cim_creation_utc = if ($cim.CreationDate) { $cim.CreationDate.ToUniversalTime().ToString('o') } else { $null }
        }
    } catch {
        return [ordered]@{ process_id = $ProcessIdValue; read_error = $_.Exception.GetType().Name }
    } finally { if ($process) { $process.Dispose() } }
}

function Read-WindowMetadata([IntPtr]$WindowHandle, [switch]$PickerTitle) {
    if ($WindowHandle -eq [IntPtr]::Zero) { return $null }
    $windowProcessId = [int][AutumnPickerReadOnly.Native]::ProcessId($WindowHandle)
    $record = [ordered]@{
        hwnd = $WindowHandle.ToInt64(); process_id = $windowProcessId
        class = [AutumnPickerReadOnly.Native]::ClassName($WindowHandle)
        visible = [AutumnPickerReadOnly.Native]::IsWindowVisible($WindowHandle)
        enabled = [AutumnPickerReadOnly.Native]::IsWindowEnabled($WindowHandle)
        owner_hwnd = [AutumnPickerReadOnly.Native]::GetWindow($WindowHandle,4).ToInt64()
        root_hwnd = [AutumnPickerReadOnly.Native]::GetAncestor($WindowHandle,2).ToInt64()
        root_owner_hwnd = [AutumnPickerReadOnly.Native]::GetAncestor($WindowHandle,3).ToInt64()
    }
    if ($PickerTitle -and $script:pickerIds.Contains($windowProcessId)) {
        $record.title = [AutumnPickerReadOnly.Native]::Title($WindowHandle)
    }
    return $record
}

$capturedAt = [DateTimeOffset]::UtcNow.ToString('o')
$productIdentity = Read-ProcessIdentity $ProductProcessId
if ($ProductWindowHandle -eq 0) {
    $productForWindow = [Diagnostics.Process]::GetProcessById($ProductProcessId)
    try { $ProductWindowHandle = $productForWindow.MainWindowHandle.ToInt64() } finally { $productForWindow.Dispose() }
}
$pickerRows = @(Get-CimInstance Win32_Process -Filter "Name='PickerHost.exe'" -ErrorAction Stop)
$script:pickerIds = [Collections.Generic.HashSet[int]]::new()
foreach ($row in $pickerRows) { $null = $script:pickerIds.Add([int]$row.ProcessId) }
$pickerWindows = @(
    foreach ($window in [AutumnPickerReadOnly.Native]::TopWindows()) {
        if (-not $script:pickerIds.Contains([int][AutumnPickerReadOnly.Native]::ProcessId($window))) { continue }
        $seenOwners = [Collections.Generic.HashSet[long]]::new()
        $ownerChain = @(); $currentWindow = $window
        for ($depth=0; $depth -lt 16 -and $currentWindow -ne [IntPtr]::Zero; $depth++) {
            if (-not $seenOwners.Add($currentWindow.ToInt64())) { break }
            $ownerChain += Read-WindowMetadata $currentWindow -PickerTitle
            $currentWindow = [AutumnPickerReadOnly.Native]::GetWindow($currentWindow,4)
        }
        # HWND control class/IDs only. Do not read file names, values, list items or child text.
        $children = @(
            foreach ($childWindow in [AutumnPickerReadOnly.Native]::ChildWindows($window)) {
                if (-not $script:pickerIds.Contains([int][AutumnPickerReadOnly.Native]::ProcessId($childWindow))) { continue }
                [ordered]@{
                    hwnd = $childWindow.ToInt64(); process_id = [AutumnPickerReadOnly.Native]::ProcessId($childWindow)
                    class = [AutumnPickerReadOnly.Native]::ClassName($childWindow)
                    control_id = [AutumnPickerReadOnly.Native]::GetDlgCtrlID($childWindow)
                }
            }
        )
        [ordered]@{ window = Read-WindowMetadata $window -PickerTitle; owner_chain = $ownerChain
            owner_chain_contains_product_hwnd = $seenOwners.Contains($ProductWindowHandle)
            controls_metadata_only = $children }
    }
)
$foreground = [AutumnPickerReadOnly.Native]::GetForegroundWindow()
$cursorPoint = [AutumnPickerReadOnly.Native+POINT]::new()
$hasCursor = [AutumnPickerReadOnly.Native]::GetCursorPos([ref]$cursorPoint)
$report = [ordered]@{
    schema = 'autumn-picker-readonly-metadata-v1'; captured_at_utc = $capturedAt
    read_only = $true; association_is_input_authorization = $false
    product = $productIdentity; product_window = Read-WindowMetadata ([IntPtr]$ProductWindowHandle)
    picker_processes = @($pickerRows | ForEach-Object { Read-ProcessIdentity ([int]$_.ProcessId) })
    picker_windows = $pickerWindows
    foreground = Read-WindowMetadata $foreground -PickerTitle
    cursor = if ($hasCursor) { [ordered]@{ x=$cursorPoint.X; y=$cursorPoint.Y; hit_window=Read-WindowMetadata ([AutumnPickerReadOnly.Native]::WindowFromPoint($cursorPoint)) } } else { $null }
    limitations = @('Snapshot is racy and does not prove who opened a picker.', 'No UIA names/values or file contents collected.', 'No foreground changes or input performed.')
}
$json = $report | ConvertTo-Json -Depth 12
if ($ReportPath) {
    $fullReport = [IO.Path]::GetFullPath($ReportPath)
    if (Test-Path -LiteralPath $fullReport) { throw "Refusing to replace an existing report: $fullReport" }
    [IO.File]::WriteAllText($fullReport,$json,[Text.UTF8Encoding]::new($false))
}
$json
