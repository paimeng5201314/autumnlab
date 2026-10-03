# Shared, local Windows smoke input. Dot-source this helper; it does not launch or stop applications.
# Callers must pass the Process object they themselves started, not an arbitrary PID lookup.
$ErrorActionPreference = 'Stop'

function Resolve-SmokeBusinessProcess {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][Diagnostics.Process]$EntryProcess,
        [Parameter(Mandatory)][string]$ExecutablePath,
        [ValidateRange(1,90)][int]$TimeoutSeconds = 30
    )
    $entryPath = [IO.Path]::GetFullPath($ExecutablePath)
    $entryPid = $EntryProcess.Id
    $entryStarted = $EntryProcess.StartTime.ToUniversalTime()
    $entrySession = $EntryProcess.SessionId
    $null = $EntryProcess.Handle # Pin the actual self-started process identity before examining children.
    $clientPath = Join-Path ([IO.Path]::GetDirectoryName($entryPath)) 'AutumnOS.Client.exe'
    $bootstrap = Test-Path -LiteralPath $clientPath -PathType Leaf
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $EntryProcess.Refresh()
        if ($EntryProcess.HasExited) { throw "The held entry exited before its business process was observed (code $($EntryProcess.ExitCode))." }
        if ($EntryProcess.Id -ne $entryPid -or $EntryProcess.StartTime.ToUniversalTime() -ne $entryStarted -or
            -not [string]::Equals($EntryProcess.MainModule.FileName,$entryPath,[StringComparison]::OrdinalIgnoreCase)) {
            throw 'The held entry identity or its exact executable path changed.'
        }
        # Historical pre-T05 packages put the WinUI application directly in the stable entry.
        if (-not $bootstrap) { return $EntryProcess }
        $candidates = @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$entryPid" -ErrorAction Stop | Where-Object {
            $_.CreationDate -and $_.CreationDate.ToUniversalTime() -ge $entryStarted -and
            [string]::Equals($_.ExecutablePath,$clientPath,[StringComparison]::OrdinalIgnoreCase)
        })
        if ($candidates.Count -gt 1) { throw 'The held bootstrap created multiple live business processes.' }
        foreach ($candidate in $candidates) {
            $business = $null
            try {
                $business = [Diagnostics.Process]::GetProcessById([int]$candidate.ProcessId)
                $null = $business.Handle
                $business.Refresh()
                if ($business.HasExited) { $business.Dispose(); continue }
                $businessStarted = $business.StartTime.ToUniversalTime()
                if ($businessStarted -lt $entryStarted -or $business.SessionId -ne $entrySession -or
                    [Math]::Abs(($businessStarted - $candidate.CreationDate.ToUniversalTime()).TotalMilliseconds) -gt 1 -or
                    -not [string]::Equals($business.MainModule.FileName,$clientPath,[StringComparison]::OrdinalIgnoreCase)) {
                    throw 'The candidate child identity, creation time, session or executable path was not verified.'
                }
                $EntryProcess.Refresh()
                if ($EntryProcess.HasExited -or $EntryProcess.StartTime.ToUniversalTime() -ne $entryStarted) {
                    throw 'The verified bootstrap no longer owns a live launch interval.'
                }
                return $business
            } catch {
                if ($business) { $business.Dispose() }
                throw
            }
        }
        Start-Sleep -Milliseconds 75
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    throw 'The held bootstrap did not produce a verified same-directory business child within the timeout.'
}

if (-not ('AutumnSmokeInput.Native' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace AutumnSmokeInput {
  public static class Native {
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO { public uint cbSize,flags; public IntPtr hwndActive,hwndFocus,hwndCapture,hwndMenuOwner,hwndMoveSize,hwndCaret; public RECT rcCaret; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public uint cbSize; public RECT rcMonitor,rcWork; public uint flags; }
    [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int dx,dy; public uint mouseData,dwFlags,time; public UIntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public MOUSEINPUT mouse; }
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h,out uint p);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr h,out RECT r);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool GetClientRect(IntPtr h,out RECT r);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool ClientToScreen(IntPtr h,ref POINT p);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool SetCursorPos(int x,int y);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool GetGUIThreadInfo(uint thread,ref GUITHREADINFO info);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h,uint flags);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool GetMonitorInfo(IntPtr monitor,ref MONITORINFO info);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(IntPtr h,int command);
    [DllImport("user32.dll",SetLastError=true)] public static extern uint SendInput(uint count,INPUT[] input,int size);
    public static uint SendCaptionClick() {
      // One batch prevents mouse motion from interleaving between caption down and up.
      var input=new INPUT[2]; input[0].mouse.dwFlags=2; input[1].mouse.dwFlags=4;
      return SendInput(2,input,Marshal.SizeOf<INPUT>());
    }
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h,uint m,IntPtr w,IntPtr l);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool SetWindowPos(IntPtr h,IntPtr a,int x,int y,int w,int z,uint f);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h,int i);
  }
}
'@
}

function Assert-AutumnOwnedNativeWindow([Diagnostics.Process]$Process,[IntPtr]$Handle) {
    $Process.Refresh()
    [uint32]$owner=0
    $thread=[AutumnSmokeInput.Native]::GetWindowThreadProcessId($Handle,[ref]$owner)
    if($Process.HasExited -or $Handle -eq [IntPtr]::Zero -or $Process.MainWindowHandle -ne $Handle -or $owner -ne $Process.Id -or $thread -eq 0){
        throw 'The held process no longer owns its original native window.'
    }
    return $thread
}

function Wait-AutumnOwnedPointerIdle([Diagnostics.Process]$Process,[IntPtr]$Handle) {
    # GetCapture only observes the calling thread. Query the verified target GUI thread instead.
    # Never release another thread's capture or send an Escape/cancel command to another window.
    $timer=[Diagnostics.Stopwatch]::StartNew();$stable=0;$previous='';$last=$null
    do {
        $thread=Assert-AutumnOwnedNativeWindow $Process $Handle
        $info=[AutumnSmokeInput.Native+GUITHREADINFO]::new()
        $info.cbSize=[Runtime.InteropServices.Marshal]::SizeOf($info)
        $rect=[AutumnSmokeInput.Native+RECT]::new()
        if(-not [AutumnSmokeInput.Native]::GetGUIThreadInfo($thread,[ref]$info) -or
           -not [AutumnSmokeInput.Native]::GetWindowRect($Handle,[ref]$rect)){throw 'Cannot inspect the owned GUI input state.'}
        $buttonsDown=([AutumnSmokeInput.Native]::GetAsyncKeyState(1) -band 0x8000) -ne 0 -or
            ([AutumnSmokeInput.Native]::GetAsyncKeyState(2) -band 0x8000) -ne 0 -or
            ([AutumnSmokeInput.Native]::GetAsyncKeyState(4) -band 0x8000) -ne 0
        $key="$($rect.Left),$($rect.Top),$($rect.Right),$($rect.Bottom)"
        $idle=-not $buttonsDown -and $info.hwndCapture -eq [IntPtr]::Zero -and $info.hwndMoveSize -eq [IntPtr]::Zero -and ($info.flags -band 2) -eq 0
        if($idle -and $key -eq $previous){$stable++}else{$stable=0}
        $last=[ordered]@{pid=$Process.Id;hwnd=$Handle.ToInt64();gui_flags=$info.flags;capture_hwnd=$info.hwndCapture.ToInt64();move_size_hwnd=$info.hwndMoveSize.ToInt64();buttons_down=$buttonsDown;outer=@($rect.Left,$rect.Top,$rect.Right,$rect.Bottom)}
        if($stable -ge 2){return}
        $previous=$key
        Start-Sleep -Milliseconds 80
    } while($timer.Elapsed.TotalMilliseconds -lt 3000)
    throw ('Owned pointer input did not settle within 3 seconds: '+($last|ConvertTo-Json -Compress))
}

function Ensure-AutumnOwnedWindowWorkArea([Diagnostics.Process]$Process,[IntPtr]$Handle) {
    $null=Assert-AutumnOwnedNativeWindow $Process $Handle
    Wait-AutumnOwnedPointerIdle $Process $Handle
    $restored=[AutumnSmokeInput.Native]::IsIconic($Handle)
    if($restored){$null=[AutumnSmokeInput.Native]::ShowWindow($Handle,9);Wait-AutumnOwnedPointerIdle $Process $Handle}
    $before=[AutumnSmokeInput.Native+RECT]::new()
    $monitor=[AutumnSmokeInput.Native+MONITORINFO]::new();$monitor.cbSize=[Runtime.InteropServices.Marshal]::SizeOf($monitor)
    if(-not [AutumnSmokeInput.Native]::GetWindowRect($Handle,[ref]$before) -or
       -not [AutumnSmokeInput.Native]::GetMonitorInfo([AutumnSmokeInput.Native]::MonitorFromWindow($Handle,2),[ref]$monitor)){
        throw 'Cannot locate the owned window work area.'
    }
    # Maximized windows include invisible resize borders outside the work area; do not resize them.
    $moved=$false
    if(-not [AutumnSmokeInput.Native]::IsZoomed($Handle)){
        $width=$before.Right-$before.Left;$height=$before.Bottom-$before.Top
        if($width -gt ($monitor.rcWork.Right-$monitor.rcWork.Left) -or $height -gt ($monitor.rcWork.Bottom-$monitor.rcWork.Top)){
            throw 'The owned window cannot fit the monitor work area without changing its tested size.'
        }
        $x=[Math]::Max($monitor.rcWork.Left,[Math]::Min($before.Left,$monitor.rcWork.Right-$width))
        $y=[Math]::Max($monitor.rcWork.Top,[Math]::Min($before.Top,$monitor.rcWork.Bottom-$height))
        if($x -ne $before.Left -or $y -ne $before.Top){
            $null=Assert-AutumnOwnedNativeWindow $Process $Handle
            # One move of this held HWND, preserving size, activation and z-order. No recovery loop.
            if(-not [AutumnSmokeInput.Native]::SetWindowPos($Handle,[IntPtr]::Zero,$x,$y,0,0,0x15)){throw 'Cannot move the owned window into its visible work area.'}
            $moved=$true
            Wait-AutumnOwnedPointerIdle $Process $Handle
        }
    }
    $after=[AutumnSmokeInput.Native+RECT]::new()
    if(-not [AutumnSmokeInput.Native]::GetWindowRect($Handle,[ref]$after)){throw 'Cannot confirm the owned work area geometry.'}
    if($moved -and ($after.Left -ne $x -or $after.Top -ne $y -or ($after.Right-$after.Left) -ne $width -or ($after.Bottom-$after.Top) -ne $height)){
        throw 'The owned window changed during its single work area recovery.'
    }
    if($moved -or $restored){
        return [ordered]@{kind='owned_window_work_area_recovery';process_id=$Process.Id;hwnd=$Handle.ToInt64();attempts=1;restored=$restored;moved=$moved;
            before=@($before.Left,$before.Top,$before.Right,$before.Bottom);after=@($after.Left,$after.Top,$after.Right,$after.Bottom);
            work_area=@($monitor.rcWork.Left,$monitor.rcWork.Top,$monitor.rcWork.Right,$monitor.rcWork.Bottom)}
    }
}

function Assert-AutumnVisibleCapture([Diagnostics.Process]$Process,[IntPtr]$Handle,[int]$Left,[int]$Top,[int]$Width,[int]$Height) {
    $null=Assert-AutumnOwnedNativeWindow $Process $Handle
    $client=[AutumnSmokeInput.Native+RECT]::new();$origin=[AutumnSmokeInput.Native+POINT]::new()
    $monitor=[AutumnSmokeInput.Native+MONITORINFO]::new();$monitor.cbSize=[Runtime.InteropServices.Marshal]::SizeOf($monitor)
    if(-not [AutumnSmokeInput.Native]::GetClientRect($Handle,[ref]$client) -or -not [AutumnSmokeInput.Native]::ClientToScreen($Handle,[ref]$origin) -or
       -not [AutumnSmokeInput.Native]::GetMonitorInfo([AutumnSmokeInput.Native]::MonitorFromWindow($Handle,2),[ref]$monitor)){
        throw 'Cannot verify the owned capture geometry.'
    }
    if($origin.X -ne $Left -or $origin.Y -ne $Top -or ($client.Right-$client.Left) -ne $Width -or ($client.Bottom-$client.Top) -ne $Height){
        throw 'The owned client geometry changed during capture; refusing stale coordinates.'
    }
    if($Width -le 0 -or $Height -le 0 -or $Left -lt $monitor.rcMonitor.Left -or $Top -lt $monitor.rcMonitor.Top -or
       ($Left+$Width) -gt $monitor.rcMonitor.Right -or ($Top+$Height) -gt $monitor.rcMonitor.Bottom){
        throw 'The complete owned client rectangle is not on one visible monitor; refusing off-screen pixels.'
    }
}

function Test-AutumnOwnedInputProcess([int]$CandidateId, [Diagnostics.Process]$Process) {
    $Process.Refresh()
    if ($Process.HasExited) { return $false }
    if ($CandidateId -eq $Process.Id) { return $true }
    $started = $Process.StartTime.ToUniversalTime()
    for ($depth=0; $depth -lt 10 -and $CandidateId -gt 0; $depth++) {
        if ($CandidateId -eq $Process.Id) { return $true }
        $candidate = Get-CimInstance Win32_Process -Filter "ProcessId=$CandidateId" -ErrorAction SilentlyContinue
        if (-not $candidate -or -not $candidate.CreationDate -or $candidate.CreationDate.ToUniversalTime() -lt $started -or $candidate.ParentProcessId -eq $CandidateId) { return $false }
        # Native input may reach a WebView2 child HWND; unrelated process names are never sufficient.
        if ($candidate.Name -ne 'msedgewebview2.exe') { return $false }
        $CandidateId = [int]$candidate.ParentProcessId
    }
    return $false
}

function Invoke-AutumnNativeElementClick {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$WindowElement,
        [Parameter(Mandatory)]$Element,
        [Parameter(Mandatory)][Diagnostics.Process]$Process
    )
    $Process.Refresh()
    $hwnd = $Process.MainWindowHandle
    [uint32]$owner = 0
    $null = [AutumnSmokeInput.Native]::GetWindowThreadProcessId($hwnd,[ref]$owner)
    if ($Process.HasExited -or $hwnd -eq [IntPtr]::Zero -or $owner -ne $Process.Id -or $WindowElement.Current.ProcessId -ne $Process.Id -or
        -not (Test-AutumnOwnedInputProcess $Element.Current.ProcessId $Process)) { throw 'Native input ownership could not be verified.' }
    if (-not $Element.Current.IsEnabled) { throw 'Native input target is disabled.' }
    $workAreaRecovery=Ensure-AutumnOwnedWindowWorkArea $Process $hwnd
    $captionObservation=$null
    $initial = [AutumnSmokeInput.Native+POINT]::new()
    $null = [AutumnSmokeInput.Native]::GetCursorPos([ref]$initial)
    if (-not (Get-Variable -Name AutumnNativeInitialPointer -Scope Script -ErrorAction SilentlyContinue)) { $script:AutumnNativeInitialPointer = $initial }
    $wasTopmost = ([AutumnSmokeInput.Native]::GetWindowLongPtr($hwnd,-20).ToInt64() -band 8) -ne 0
    $pressed = $false
    try {
        # Only this script's product window may temporarily be raised for checked synthetic input.
        if (-not $wasTopmost) { $null = [AutumnSmokeInput.Native]::SetWindowPos($hwnd,[IntPtr](-1),0,0,0,0,0x13) }
        $null = [AutumnSmokeInput.Native]::SetForegroundWindow($hwnd)
        try { $WindowElement.SetFocus() } catch { }
        Start-Sleep -Milliseconds 100
        [uint32]$foregroundOwner = 0
        $null = [AutumnSmokeInput.Native]::GetWindowThreadProcessId([AutumnSmokeInput.Native]::GetForegroundWindow(),[ref]$foregroundOwner)
        if (-not (Test-AutumnOwnedInputProcess $foregroundOwner $Process)) {
            # An ordinary verified caption click requests activation without changing Windows policy.
            $rect = [AutumnSmokeInput.Native+RECT]::new()
            if (-not [AutumnSmokeInput.Native]::GetWindowRect($hwnd,[ref]$rect)) { throw 'Cannot locate owned caption.' }
            Start-Sleep -Milliseconds ([AutumnSmokeInput.Native]::GetDoubleClickTime()+100)
            $caption = [AutumnSmokeInput.Native+POINT]::new(); $caption.X=($rect.Left+$rect.Right)/2; $caption.Y=$rect.Top+16
            $packed=[IntPtr]([long](($caption.Y -band 65535)*65536)+($caption.X -band 65535))
            if ([AutumnSmokeInput.Native]::SendMessage($hwnd,0x84,[IntPtr]::Zero,$packed).ToInt64() -ne 2) { throw 'Activation target is not HTCAPTION.' }
            $null=[AutumnSmokeInput.Native]::SetCursorPos($caption.X,$caption.Y)
            $actual=[AutumnSmokeInput.Native+POINT]::new();$null=[AutumnSmokeInput.Native]::GetCursorPos([ref]$actual)
            [uint32]$hitOwner=0;$hit=[AutumnSmokeInput.Native]::WindowFromPoint($actual)
            $null=[AutumnSmokeInput.Native]::GetWindowThreadProcessId($hit,[ref]$hitOwner)
            if ($actual.X -ne $caption.X -or $actual.Y -ne $caption.Y -or $hitOwner -ne $Process.Id) { throw 'Owned caption is occluded; refusing input.' }
            $pressed=$true
            if([AutumnSmokeInput.Native]::SendCaptionClick() -ne 2){throw 'The owned caption click was not fully inserted.'}
            $pressed=$false
            Wait-AutumnOwnedPointerIdle $Process $hwnd
            $after=[AutumnSmokeInput.Native+RECT]::new()
            if(-not [AutumnSmokeInput.Native]::GetWindowRect($hwnd,[ref]$after)){throw 'Cannot inspect caption activation geometry.'}
            $captionObservation=[ordered]@{injection='single_SendInput_down_up_batch';capture_and_move_size_released=$true;
                before=@($rect.Left,$rect.Top,$rect.Right,$rect.Bottom);after=@($after.Left,$after.Top,$after.Right,$after.Bottom)}
            if($rect.Left -ne $after.Left -or $rect.Top -ne $after.Top -or $rect.Right -ne $after.Right -or $rect.Bottom -ne $after.Bottom){
                throw ('The owned window moved during caption activation: '+($captionObservation|ConvertTo-Json -Compress))
            }
        }
        Wait-AutumnOwnedPointerIdle $Process $hwnd
        $observations=[Collections.Generic.List[object]]::new();$ready=$false
        for($attempt=1;$attempt -le 4;$attempt++){
            $scroll=$null
            if($Element.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern,[ref]$scroll)){$scroll.ScrollIntoView()}
            $null=[AutumnSmokeInput.Native]::SetForegroundWindow($hwnd)
            try{$Element.SetFocus()}catch{}
            Start-Sleep -Milliseconds (200*$attempt)
            $bounds=$Element.Current.BoundingRectangle
            $client=[AutumnSmokeInput.Native+RECT]::new();$origin=[AutumnSmokeInput.Native+POINT]::new()
            if(-not [AutumnSmokeInput.Native]::GetClientRect($hwnd,[ref]$client) -or -not [AutumnSmokeInput.Native]::ClientToScreen($hwnd,[ref]$origin)){throw 'Cannot locate owned client rectangle.'}
            $point=[AutumnSmokeInput.Native+POINT]::new();$point.X=[int](($bounds.Left+$bounds.Right)/2);$point.Y=[int](($bounds.Top+$bounds.Bottom)/2)
            $visible=-not $Element.Current.IsOffscreen -and -not $bounds.IsEmpty -and $point.X -ge $origin.X -and $point.X -lt ($origin.X+$client.Right) -and $point.Y -ge $origin.Y -and $point.Y -lt ($origin.Y+$client.Bottom)
            if($visible){$null=[AutumnSmokeInput.Native]::SetCursorPos($point.X,$point.Y)}
            Start-Sleep -Milliseconds 80
            $actual=[AutumnSmokeInput.Native+POINT]::new();$null=[AutumnSmokeInput.Native]::GetCursorPos([ref]$actual)
            $live=$Element.Current.BoundingRectangle
            [uint32]$hitOwner=0;$hit=[AutumnSmokeInput.Native]::WindowFromPoint($actual)
            $null=[AutumnSmokeInput.Native]::GetWindowThreadProcessId($hit,[ref]$hitOwner)
            [uint32]$foregroundOwner=0;$foreground=[AutumnSmokeInput.Native]::GetForegroundWindow()
            $null=[AutumnSmokeInput.Native]::GetWindowThreadProcessId($foreground,[ref]$foregroundOwner)
            $Process.Refresh()
            $ready=$visible -and -not $Process.HasExited -and $Process.MainWindowHandle -eq $hwnd -and $actual.X -eq $point.X -and $actual.Y -eq $point.Y -and
                $actual.X -ge $live.Left -and $actual.X -lt $live.Right -and $actual.Y -ge $live.Top -and $actual.Y -lt $live.Bottom -and
                (Test-AutumnOwnedInputProcess $hitOwner $Process) -and (Test-AutumnOwnedInputProcess $foregroundOwner $Process)
            $observations.Add([ordered]@{attempt=$attempt;ready=$ready;expected_pid=$Process.Id;main_hwnd=$hwnd.ToInt64();current_main_hwnd=$Process.MainWindowHandle.ToInt64();foreground_pid=$foregroundOwner;foreground_hwnd=$foreground.ToInt64();hit_pid=$hitOwner;hit_hwnd=$hit.ToInt64();intended=@($point.X,$point.Y);actual=@($actual.X,$actual.Y);target_rect=@($live.Left,$live.Top,$live.Right,$live.Bottom);client_origin=@($origin.X,$origin.Y);client_size=@($client.Right,$client.Bottom);offscreen=$Element.Current.IsOffscreen})
            if($ready){break}
        }
        if(-not $ready){throw ('Refusing native input after bounded stability checks: '+($observations|ConvertTo-Json -Compress -Depth 5))}
        # Recheck immediately before button-down; no sleep or focus operation follows this validation.
        $finalPoint=[AutumnSmokeInput.Native+POINT]::new();$null=[AutumnSmokeInput.Native]::GetCursorPos([ref]$finalPoint)
        [uint32]$finalOwner=0;$finalHit=[AutumnSmokeInput.Native]::WindowFromPoint($finalPoint)
        $null=[AutumnSmokeInput.Native]::GetWindowThreadProcessId($finalHit,[ref]$finalOwner)
        if($finalPoint.X -ne $actual.X -or $finalPoint.Y -ne $actual.Y -or $finalHit -ne $hit -or $finalOwner -ne $hitOwner -or [AutumnSmokeInput.Native]::GetForegroundWindow() -ne $foreground){throw ('Native input changed immediately before mouse-down: '+($observations[-1]|ConvertTo-Json -Compress -Depth 5))}
        [AutumnSmokeInput.Native]::mouse_event(2,0,0,0,[UIntPtr]::Zero);$pressed=$true;Start-Sleep -Milliseconds 35
        [AutumnSmokeInput.Native]::mouse_event(4,0,0,0,[UIntPtr]::Zero);$pressed=$false
        Wait-AutumnOwnedPointerIdle $Process $hwnd
        return [pscustomobject]@{ kind='verified_native_single_click'; main_pid=$Process.Id; target_pid=$hitOwner; target_window=$hit.ToInt64()
            target_name=$Element.Current.Name; x=$actual.X; y=$actual.Y; foreground_verified=$true; temporary_topmost=(-not $wasTopmost);stability_observations=@($observations);
            work_area_recovery=$workAreaRecovery;caption_activation=$captionObservation }
    } finally {
        if($pressed){[AutumnSmokeInput.Native]::mouse_event(4,0,0,0,[UIntPtr]::Zero)}
        if(-not $wasTopmost -and -not $Process.HasExited){$null=[AutumnSmokeInput.Native]::SetWindowPos($hwnd,[IntPtr](-2),0,0,0,0,0x13)}
    }
}

function Restore-AutumnNativePointer {
    if(Get-Variable -Name AutumnNativeInitialPointer -Scope Script -ErrorAction SilentlyContinue){
        $null=[AutumnSmokeInput.Native]::SetCursorPos($script:AutumnNativeInitialPointer.X,$script:AutumnNativeInitialPointer.Y)
        Remove-Variable -Name AutumnNativeInitialPointer -Scope Script
    }
}
