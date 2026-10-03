[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [string]$PeerExecutablePath,
    [string]$ReportDirectory,
    [ValidateRange(5,90)][int]$WindowTimeoutSeconds = 30,
    [ValidateRange(2,12)][int]$ConcurrentLaunchCount = 6
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/Smoke-NativeAppInput.ps1"
$smokeId = 'single-instance-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$scope = Join-Path $projectRoot "artifacts/smoke/$smokeId"
if (-not $ReportDirectory) { $ReportDirectory = $scope }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
$reportPath = Join-Path $ReportDirectory 'single-instance-smoke.json'
if (Test-Path -LiteralPath $reportPath) { throw "Report already exists; choose a new ReportDirectory: $reportPath" }
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$ownedLaunches = [Collections.Generic.List[object]]::new()
$launches = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[object]]::new()
$inputActions = [Collections.Generic.List[object]]::new()
$script:initialPointerPosition = $null
$script:suspendedProcess = $null
$script:focusProbeProduct = $null
$activeProduct = $null
$copyDirectories = @()
$report = [ordered]@{
    schema_version=1; task_id='T02'; checkpoint='single_instance_launcher'; smoke_id=$smokeId
    status='failed'; started_utc=[DateTimeOffset]::UtcNow.ToString('o')
    source_executable=$null; source_executable_sha256=$null; build_id='local-untracked'; source_snapshot_id='not_recorded'
    environment=[Environment]::OSVersion.VersionString; isElevated=$null; process_ids=@(); launches=$launches
    checks=$checks; screenshots=$screenshots; input_actions=$inputActions; existing_processes=@()
    standard_user_window='not_run'; normal_recall='not_run'; minimized_recall='not_run'; maximized_recall='not_run'; foreground_activation='not_run'
    foreground_game_preservation='not_run'; background_game_preservation='not_run'; multiple_working_directories='not_run'
    shortcut_launch='not_run'; cross_copy_recall='not_run'; concurrent_cold_start='not_run'; clean_exit_restart='not_run'
    crash_restart='not_run'; unresponsive_primary='not_run'; forwarding_failure_no_second_root='not_run'; resource_failure='not_run'; transport_recovery='not_run'
    source_data_preserved='not_run'; distinct_build_versions='not_run'; unsupported_legacy_builds='not_run'
    windows10_22h2='not_run'; different_windows_users='not_run'; different_windows_sessions='not_run'
    elevated_process_matrix='not_run'; physical_keyboard_mouse='not_run'; input_method_editor='not_run'; physical_touch='not_run'
    automation_window_condition='Only this script-owned test windows may be temporarily TOPMOST and normally activated through a verified native caption. Activation assertions run before helper focus or screenshots. No system focus policy or unrelated window is changed.'
    automation_pointer_condition='Actual pointer down verifies current cursor, fresh UIA bounds, process ownership and foreground. Cursor stays in test window between actions and is restored in final cleanup.'
    fault_injection='Only held, path/start-time verified test primary processes are suspended/resumed or terminated; no process-name cleanup.'
    protocol_scope='Same Windows user and SessionId; supported copies use fixed product identity independent of path/version.'
}
function Add-SmokeCheck([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $checks.Add([ordered]@{ name = $Name; status = $(if ($Passed) { 'passed' } else { 'failed' }); detail = $Detail })
    if (-not $Passed) { throw "Smoke check failed: $Name. $Detail" }
    Write-Host "PASS $Name"
}

function Wait-SmokeCondition([scriptblock]$Condition, [string]$Description, [int]$Seconds = $WindowTimeoutSeconds) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 150
    } while ($timer.Elapsed.TotalSeconds -lt $Seconds)
    throw "Timed out waiting for $Description after $Seconds seconds."
}

function Find-SmokeElement($WindowElement, [string]$AutomationId) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    return $WindowElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-VisibleSmokeElement($WindowElement, [string]$AutomationId) {
    $element = Find-SmokeElement $WindowElement $AutomationId
    if ($element -and -not $element.Current.IsOffscreen -and -not $element.Current.BoundingRectangle.IsEmpty) { return $element }
    return $null
}

function Invoke-SmokeButton($WindowElement, [string]$AutomationId) {
    $element = Wait-SmokeCondition {
        $candidate = Find-SmokeElement $WindowElement $AutomationId
        if (-not $candidate -or -not $candidate.Current.IsEnabled) { return $null }
        $scroll = $null
        if ($candidate.Current.IsOffscreen -and $candidate.TryGetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern, [ref]$scroll)) { $scroll.ScrollIntoView() }
        if (-not $candidate.Current.IsOffscreen -and -not $candidate.Current.BoundingRectangle.IsEmpty) { return $candidate }
        return $null
    } "visible and enabled button $AutomationId"
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Find-SmokeMenuItem($Product, [string]$AutomationId, [string[]]$Names) {
    $scopeElements = [Collections.Generic.List[object]]::new()
    $scopeElements.Add($Product.Element)
    $owned = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty, $Product.Process.Id)
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Children, $owned)
    $mainBounds = $Product.Element.Current.BoundingRectangle
    foreach ($window in $windows) {
        $bounds = $window.Current.BoundingRectangle
        if (-not $bounds.IsEmpty -and $bounds.Left -ge $mainBounds.Left -and $bounds.Top -ge $mainBounds.Top -and
            $bounds.Right -le $mainBounds.Right -and $bounds.Bottom -le $mainBounds.Bottom) { $scopeElements.Add($window) }
    }
    foreach ($elementScope in $scopeElements) {
        $item = Find-VisibleSmokeElement $elementScope $AutomationId
        if ($item -and $item.Current.IsEnabled) { return $item }
        foreach ($name in $Names) {
            $condition = [System.Windows.Automation.AndCondition]::new(
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $name),
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem),
                [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsEnabledProperty, $true))
            $item = $elementScope.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
            if ($item -and -not $item.Current.IsOffscreen) { return $item }
        }
    }
    return $null
}

function Invoke-SmokeMenuItem($Product, [string]$AutomationId, [string[]]$Names) {
    $item = Wait-SmokeCondition { Find-SmokeMenuItem $Product $AutomationId $Names } "menu item $AutomationId"
    $item.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Test-SmokeForeground($Product) {
    $foreground = [AutumnSingleInstanceSmoke.NativeWindows]::GetForegroundWindow()
    [uint32]$foregroundProcess = 0
    $null = [AutumnSingleInstanceSmoke.NativeWindows]::GetWindowThreadProcessId($foreground, [ref]$foregroundProcess)
    return $foregroundProcess -eq $Product.Process.Id
}

function Focus-SmokeProduct($Product, [switch]$RequireCaptionInput) {
    $null = [AutumnSingleInstanceSmoke.NativeWindows]::SetWindowPos($Product.Handle, [IntPtr](-1), 0, 0, 0, 0, 3)
    $null = [AutumnSingleInstanceSmoke.NativeWindows]::SetForegroundWindow($Product.Handle)
    if (-not (Test-SmokeForeground $Product)) {
        try { $Product.Element.SetFocus() } catch { Write-Verbose 'The window provider did not accept SetFocus; normal foreground requests will be tried.' }
    }
    if ($RequireCaptionInput -or -not (Test-SmokeForeground $Product)) {
        # Windows may reject programmatic activation while an unrelated application has focus.
        # An ordinary click on this test window's verified native caption requests activation.
        Start-Sleep -Milliseconds ([AutumnSingleInstanceSmoke.NativeWindows]::GetDoubleClickTime() + 100)
        # Only the native driver probe may move to avoid an unrelated overlay. Product
        # geometry is never changed here: its own restore/placement remains under test.
        $isProbe=$script:focusProbeProduct -and $Product.Handle -eq $script:focusProbeProduct.Handle -and $Product.Process.Id -eq [Environment]::ProcessId
        if($isProbe){
            [uint32]$probeOwner=0
            $null=[AutumnSingleInstanceSmoke.NativeWindows]::GetWindowThreadProcessId($Product.Handle,[ref]$probeOwner)
            $probeClass=[Text.StringBuilder]::new(256)
            $null=[AutumnSingleInstanceSmoke.NativeWindows]::GetClassName($Product.Handle,$probeClass,$probeClass.Capacity)
            if($probeOwner -ne [Environment]::ProcessId -or $probeClass.ToString() -ne 'AutumnSingleInstanceSmokeForegroundProbe'){throw 'The held focus probe identity changed.'}
        }
        $original = [AutumnSingleInstanceSmoke.NativeWindows+POINT]::new()
        $null = [AutumnSingleInstanceSmoke.NativeWindows]::GetCursorPos([ref]$original)
        if (-not $script:initialPointerPosition) { $script:initialPointerPosition = $original }
        $pressed = $false
        $captionClicked=$false
        $captionTimer=[Diagnostics.Stopwatch]::StartNew()
        try {
            for($attempt=0;$attempt -lt 7 -and $captionTimer.Elapsed.TotalSeconds -lt 5 -and -not $captionClicked;$attempt++){
                $rect=[AutumnSingleInstanceSmoke.NativeWindows+RECT]::new()
                if(-not [AutumnSingleInstanceSmoke.NativeWindows]::GetWindowRect($Product.Handle,[ref]$rect)){throw 'Cannot locate the owned window caption.'}
                if($isProbe -and $attempt -gt 0){
                    $desktop=[AutumnSingleInstanceSmoke.NativeWindows+RECT]::new()
                    $desktopHandle=[IntPtr][System.Windows.Automation.AutomationElement]::RootElement.Current.NativeWindowHandle
                    if(-not [AutumnSingleInstanceSmoke.NativeWindows]::GetWindowRect($desktopHandle,[ref]$desktop)){throw 'Cannot read the native desktop bounds for probe placement.'}
                    $width=$rect.Right-$rect.Left;$height=$rect.Bottom-$rect.Top
                    $minX=[int]$desktop.Left+24;$maxX=[int]$desktop.Right-$width-24
                    $minY=[int]$desktop.Top+24;$maxY=[int]$desktop.Bottom-$height-24
                    if($maxX -ge $minX -and $maxY -ge $minY){
                        $placement=($attempt-1)%6
                        $nextX=if($placement -in @(0,3)){$maxX}elseif($placement -in @(1,4)){$minX}else{[int](($minX+$maxX)/2)}
                        $nextY=if($placement -lt 3){$minY}else{$maxY}
                        if(-not [AutumnSingleInstanceSmoke.NativeWindows]::SetWindowPos($Product.Handle,[IntPtr](-1),$nextX,$nextY,0,0,0x11)){throw 'Could not move the owned focus probe.'}
                        $inputActions.Add([ordered]@{kind='reposition_owned_focus_probe';process_id=$Product.Process.Id;window=$Product.Handle.ToInt64();x=$nextX;y=$nextY;attempt=$attempt})
                        Start-Sleep -Milliseconds 100
                    }
                }
                $null=[AutumnSingleInstanceSmoke.NativeWindows]::SetWindowPos($Product.Handle,[IntPtr](-1),0,0,0,0,3)
                if(-not [AutumnSingleInstanceSmoke.NativeWindows]::GetWindowRect($Product.Handle,[ref]$rect)){throw 'Cannot recheck the owned caption bounds.'}
                $scale=[Math]::Max(1,[AutumnSingleInstanceSmoke.NativeWindows]::GetDpiForWindow($Product.Handle)/96.0)
                $blockedOwner=0;$blockedHit=[IntPtr]::Zero
                foreach($fraction in @(0.50,0.30,0.65)){
                    foreach($offset in @(16,23,11)){
                        $point=[AutumnSingleInstanceSmoke.NativeWindows+POINT]::new()
                        $point.X=$rect.Left+[int](($rect.Right-$rect.Left)*$fraction)
                        $point.Y=$rect.Top+[int]($offset*$scale)
                        [uint32]$candidateOwner=0
                        $candidateHit=[AutumnSingleInstanceSmoke.NativeWindows]::WindowFromPoint($point)
                        $null=[AutumnSingleInstanceSmoke.NativeWindows]::GetWindowThreadProcessId($candidateHit,[ref]$candidateOwner)
                        $packed=[IntPtr]([long](($point.Y -band 65535)*65536)+($point.X -band 65535))
                        if($candidateOwner -ne $Product.Process.Id -or $candidateHit -ne $Product.Handle){$blockedOwner=$candidateOwner;$blockedHit=$candidateHit;continue}
                        if([AutumnSingleInstanceSmoke.NativeWindows]::SendMessage($Product.Handle,0x84,[IntPtr]::Zero,$packed).ToInt64() -ne 2){continue}
                        if(-not [AutumnSingleInstanceSmoke.NativeWindows]::SetCursorPos($point.X,$point.Y)){throw 'Cannot position cursor over owned caption.'}
                        $actual=[AutumnSingleInstanceSmoke.NativeWindows+POINT]::new()
                        if(-not [AutumnSingleInstanceSmoke.NativeWindows]::GetCursorPos([ref]$actual) -or $actual.X -ne $point.X -or $actual.Y -ne $point.Y){throw 'Caption activation cursor moved.'}
                        [uint32]$owner=0
                        $hit=[AutumnSingleInstanceSmoke.NativeWindows]::WindowFromPoint($actual)
                        $null=[AutumnSingleInstanceSmoke.NativeWindows]::GetWindowThreadProcessId($hit,[ref]$owner)
                        $fresh=[AutumnSingleInstanceSmoke.NativeWindows+RECT]::new()
                        $freshRect=[AutumnSingleInstanceSmoke.NativeWindows]::GetWindowRect($Product.Handle,[ref]$fresh)
                        $freshCaption=[AutumnSingleInstanceSmoke.NativeWindows]::SendMessage($Product.Handle,0x84,[IntPtr]::Zero,$packed).ToInt64()
                        if(-not [AutumnSingleInstanceSmoke.NativeWindows]::GetCursorPos([ref]$actual) -or $actual.X -ne $point.X -or $actual.Y -ne $point.Y){throw 'Caption activation cursor moved during the final hit test.'}
                        $hit=[AutumnSingleInstanceSmoke.NativeWindows]::WindowFromPoint($actual)
                        $null=[AutumnSingleInstanceSmoke.NativeWindows]::GetWindowThreadProcessId($hit,[ref]$owner)
                        if($owner -ne $Product.Process.Id -or $hit -ne $Product.Handle -or -not $freshRect -or $actual.X -lt $fresh.Left -or $actual.X -ge $fresh.Right -or $actual.Y -lt $fresh.Top -or $actual.Y -ge $fresh.Bottom -or $freshCaption -ne 2){
                            $className=[Text.StringBuilder]::new(256)
                            $null=[AutumnSingleInstanceSmoke.NativeWindows]::GetClassName($hit,$className,$className.Capacity)
                            $inputActions.Add([ordered]@{kind='refused_caption_input';expected_process_id=$Product.Process.Id;actual_process_id=$owner;target_window=$hit.ToInt64();window_class=$className.ToString();x=$actual.X;y=$actual.Y;attempt=$attempt})
                            continue
                        }
                        [AutumnSingleInstanceSmoke.NativeWindows]::mouse_event(0x0002,0,0,0,[UIntPtr]::Zero)
                        $pressed=$true
                        Start-Sleep -Milliseconds 40
                        [AutumnSingleInstanceSmoke.NativeWindows]::mouse_event(0x0004,0,0,0,[UIntPtr]::Zero)
                        $pressed=$false;$captionClicked=$true
                        $inputActions.Add([ordered]@{kind='activation_title_bar_click';process_id=$Product.Process.Id;x=$actual.X;y=$actual.Y;hit_test='HTCAPTION';target_window=$hit.ToInt64();attempt=$attempt})
                        break
                    }
                    if($captionClicked){break}
                }
                if(-not $captionClicked){
                    $blockedClass=[Text.StringBuilder]::new(256)
                    if($blockedHit -ne [IntPtr]::Zero){$null=[AutumnSingleInstanceSmoke.NativeWindows]::GetClassName($blockedHit,$blockedClass,$blockedClass.Capacity)}
                    $inputActions.Add([ordered]@{kind='caption_obstructed_no_input';process_id=$Product.Process.Id;window=$Product.Handle.ToInt64();attempt=$attempt;probe_may_reposition=[bool]$isProbe;obstructing_process_id=$blockedOwner;obstructing_window=$blockedHit.ToInt64();obstructing_class=$blockedClass.ToString()})
                    Start-Sleep -Milliseconds 150
                }
            }
            if(-not $captionClicked){throw 'No safely owned HTCAPTION point was available within the bounded activation attempts; no foreign window was clicked.'}
            Start-Sleep -Milliseconds 150
        } finally {
            if ($pressed) { [AutumnSingleInstanceSmoke.NativeWindows]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) }
        }
    }
    $null = Wait-SmokeCondition {
        if (Test-SmokeForeground $Product) { return $true }
        $null = [AutumnSingleInstanceSmoke.NativeWindows]::SetForegroundWindow($Product.Handle)
        return (Test-SmokeForeground $Product)
    } 'the owned product foreground' 5
}

function Send-SmokeEscape($Product) {
    if (-not (Test-SmokeForeground $Product)) { throw 'Refusing keyboard input outside the owned product process.' }
    [AutumnSingleInstanceSmoke.NativeWindows]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
    [AutumnSingleInstanceSmoke.NativeWindows]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
    $inputActions.Add([ordered]@{ kind='synthetic_escape'; process_id=$Product.Process.Id })
    Start-Sleep -Milliseconds 150
}

function Assert-SmokeCaptureUnoccluded($Product, [int]$Left, [int]$Top, [int]$Width, [int]$Height) {
    if (-not (Test-SmokeForeground $Product)) { throw 'Refusing screen capture after the product lost foreground.' }
    $window = [AutumnSingleInstanceSmoke.NativeWindows]::GetTopWindow([IntPtr]::Zero)
    for ($count = 0; $window -ne [IntPtr]::Zero -and $count -lt 500; $count++) {
        if ($window -eq $Product.Handle) { return }
        if ([AutumnSingleInstanceSmoke.NativeWindows]::IsWindowVisible($window)) {
            [uint32]$windowProcess = 0
            $null = [AutumnSingleInstanceSmoke.NativeWindows]::GetWindowThreadProcessId($window, [ref]$windowProcess)
            [int]$cloaked = 0
            $null = [AutumnSingleInstanceSmoke.NativeWindows]::DwmGetWindowAttribute($window, 14, [ref]$cloaked, 4)
            if ($windowProcess -ne $Product.Process.Id -and $cloaked -eq 0) {
                $other = [AutumnSingleInstanceSmoke.NativeWindows+RECT]::new()
                if ([AutumnSingleInstanceSmoke.NativeWindows]::GetWindowRect($window, [ref]$other) -and
                    $other.Left -lt ($Left + $Width) -and $other.Right -gt $Left -and
                    $other.Top -lt ($Top + $Height) -and $other.Bottom -gt $Top) {
                    throw 'Refusing screen capture because a foreign window occludes the product client area.'
                }
            }
        }
        $window = [AutumnSingleInstanceSmoke.NativeWindows]::GetWindow($window, 2)
    }
    throw 'Cannot establish an unobscured product window in the desktop stacking order.'
}

function Invoke-SmokePointer($Product, [string]$AutomationId, [ValidateSet('single_click','double_click','long_press','right_click')][string]$Kind) {
    $doubleClickMs = [AutumnSingleInstanceSmoke.NativeWindows]::GetDoubleClickTime()
    if ($doubleClickMs -lt 1 -or $doubleClickMs -gt 6000) { throw 'The system double-click time is not suitable for a bounded pointer test.' }
    # Independent clicks are separated before activation, never by idling after activation.
    Start-Sleep -Milliseconds ($doubleClickMs + 100)
    Focus-SmokeProduct $Product
    $element = Wait-SmokeCondition { Find-VisibleSmokeElement $Product.Element $AutomationId } "visible pointer target $AutomationId"
    $bounds = $element.Current.BoundingRectangle
    $windowBounds = $Product.Element.Current.BoundingRectangle
    if ($bounds.IsEmpty -or $bounds.Left -lt $windowBounds.Left -or $bounds.Top -lt $windowBounds.Top -or
        $bounds.Right -gt $windowBounds.Right -or $bounds.Bottom -gt $windowBounds.Bottom) { throw 'Pointer target is outside the owned window.' }
    $x = [int][Math]::Round(($bounds.Left + $bounds.Right) / 2)
    $y = [int][Math]::Round(($bounds.Top + $bounds.Bottom) / 2)
    $original = [AutumnSingleInstanceSmoke.NativeWindows+POINT]::new()
    if (-not [AutumnSingleInstanceSmoke.NativeWindows]::GetCursorPos([ref]$original)) { throw 'Cannot record cursor position.' }
    if (-not $script:initialPointerPosition) { $script:initialPointerPosition = $original }
    $downFlag = if ($Kind -eq 'right_click') { 0x0008 } else { 0x0002 }
    $upFlag = if ($Kind -eq 'right_click') { 0x0010 } else { 0x0004 }
    $pressed = $false
    try {
        if (-not (Test-SmokeForeground $Product)) { throw 'Product lost foreground before pointer input.' }
        if (-not [AutumnSingleInstanceSmoke.NativeWindows]::SetCursorPos($x, $y)) { throw 'Cannot position pointer inside the owned window.' }
        $point = [AutumnSingleInstanceSmoke.NativeWindows+POINT]::new(); $point.X = $x; $point.Y = $y
        [uint32]$targetProcess = 0
        $targetWindow = [AutumnSingleInstanceSmoke.NativeWindows]::WindowFromPoint($point)
        $null = [AutumnSingleInstanceSmoke.NativeWindows]::GetWindowThreadProcessId($targetWindow, [ref]$targetProcess)
        if ($targetProcess -ne $Product.Process.Id -or -not (Test-SmokeForeground $Product)) { throw 'Pointer hit-test is not owned by the product.' }
        [uint32]$currentTargetProcess = 0
        $currentTarget = [AutumnSingleInstanceSmoke.NativeWindows]::WindowFromPoint($point)
        $null = [AutumnSingleInstanceSmoke.NativeWindows]::GetWindowThreadProcessId($currentTarget, [ref]$currentTargetProcess)
        if ($currentTargetProcess -ne $Product.Process.Id) { throw 'Pointer target became occluded before input.' }
        $heldMs = if ($Kind -eq 'long_press') { 750 } else { [Math]::Max(1, [Math]::Min(40, [Math]::Floor($doubleClickMs / 5))) }
        $clickCount = if ($Kind -eq 'double_click') { 2 } else { 1 }
        $clickTimer = [Diagnostics.Stopwatch]::new()
        $secondDownMs = $null
        $actualClicks = [Collections.Generic.List[object]]::new()
        for ($click = 0; $click -lt $clickCount; $click++) {
            $actualPoint = [AutumnSingleInstanceSmoke.NativeWindows+POINT]::new()
            if (-not [AutumnSingleInstanceSmoke.NativeWindows]::GetCursorPos([ref]$actualPoint)) { throw 'Cannot verify the actual cursor before button down.' }
            $liveTarget = Find-VisibleSmokeElement $Product.Element $AutomationId
            if (-not $liveTarget) { throw 'Pointer target disappeared before button down.' }
            $liveBounds = $liveTarget.Current.BoundingRectangle
            if ($actualPoint.X -lt $liveBounds.Left -or $actualPoint.X -ge $liveBounds.Right -or
                $actualPoint.Y -lt $liveBounds.Top -or $actualPoint.Y -ge $liveBounds.Bottom) { throw 'Actual cursor moved outside the current target before button down.' }
            [uint32]$actualProcess = 0
            $actualWindow = [AutumnSingleInstanceSmoke.NativeWindows]::WindowFromPoint($actualPoint)
            $null = [AutumnSingleInstanceSmoke.NativeWindows]::GetWindowThreadProcessId($actualWindow, [ref]$actualProcess)
            if ($actualProcess -ne $Product.Process.Id -or -not (Test-SmokeForeground $Product)) { throw 'Actual cursor or foreground is not owned by the product before button down.' }
            $actualClicks.Add([ordered]@{ click_index=$click; actual_x=$actualPoint.X; actual_y=$actualPoint.Y;
                owner_process_id=$actualProcess; target_window=$actualWindow.ToInt64(); target_left=$liveBounds.Left;
                target_top=$liveBounds.Top; target_right=$liveBounds.Right; target_bottom=$liveBounds.Bottom })
            if ($click -eq 0) { $clickTimer.Start() } else {
                $secondDownMs = $clickTimer.Elapsed.TotalMilliseconds
                if ($secondDownMs -ge $doubleClickMs) { throw 'Synthetic clicks missed the system double-click interval; no double-click assertion is valid.' }
            }
            [AutumnSingleInstanceSmoke.NativeWindows]::mouse_event($downFlag, 0, 0, 0, [UIntPtr]::Zero)
            $pressed = $true
            Start-Sleep -Milliseconds $heldMs
            [AutumnSingleInstanceSmoke.NativeWindows]::mouse_event($upFlag, 0, 0, 0, [UIntPtr]::Zero)
            $pressed = $false
            if ($clickCount -eq 2 -and $click -eq 0) { Start-Sleep -Milliseconds ([Math]::Max(1, [Math]::Min(80, [Math]::Floor($doubleClickMs / 4)))) }
        }
        # Let the queued button-up reach WinUI before restoring the saved cursor position.
        Start-Sleep -Milliseconds 150
        $inputActions.Add([ordered]@{ kind=$Kind; automation_id=$AutomationId; held_ms=$heldMs; click_count=$clickCount;
            system_double_click_ms=$doubleClickMs; observed_down_interval_ms=$secondDownMs; x=$x; y=$y;
            process_id=$Product.Process.Id; target_window=$targetWindow.ToInt64(); actual_clicks=$actualClicks })
    } finally {
        if ($pressed) { [AutumnSingleInstanceSmoke.NativeWindows]::mouse_event($upFlag, 0, 0, 0, [UIntPtr]::Zero) }
    }
}

function Wait-SmokeDesktop($Product) {
    $null = Wait-SmokeCondition {
        (Find-VisibleSmokeElement $Product.Element 'SampleButton') -and
        (Find-VisibleSmokeElement $Product.Element 'SettingsButton') -and
        (Find-VisibleSmokeElement $Product.Element 'DesktopProducer')
    } 'the desktop with real application icons'
}

function Invoke-SmokeOwnedButton($Product, [string]$Id) {
    $button = Wait-SmokeCondition { Find-SmokeMenuItem $Product $Id @() } "owned flyout button $Id"
    $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Assert-SmokeRunningBadges($Product, [bool]$Visible, [string]$CheckName) {
    foreach ($id in @('SampleRunningBadge', 'DockSampleRunningBadge')) {
        $badge = Find-VisibleSmokeElement $Product.Element $id
        if ($Visible -and (-not $badge -or $badge.Current.Name -ne '运行中')) { throw "Missing real running badge: $id" }
        if (-not $Visible -and $badge) { throw "The ended or absent game still shows a running badge: $id" }
    }
    Add-SmokeCheck $CheckName $true
}

function Wait-SmokeRunningPanel($Product) {
    $title = Wait-SmokeCondition { Find-SmokeMenuItem $Product 'RunningPanelTitle' @() } 'the native background action panel'
    $null = Wait-SmokeCondition { Find-SmokeMenuItem $Product 'RunningContinueButton' @() } 'Continue action in the native panel'
    $null = Wait-SmokeCondition { Find-SmokeMenuItem $Product 'RunningEndButton' @() } 'End action in the native panel'
    if ($title.Current.Name -ne '元素配对') { throw 'The background panel title does not identify the actual game.' }
}

function Read-SmokeEvents([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    return @(Get-Content -LiteralPath $Path | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
}

function Wait-SmokeNewForeground([string]$RuntimeLog, [int]$BaselineCount, [string]$ExpectedInstance) {
    return Wait-SmokeCondition {
        $events = @(Read-SmokeEvents $RuntimeLog | Select-Object -Skip $BaselineCount)
        $foreground = @($events | Where-Object { $_.state -eq 'Foreground' })
        if ($foreground.Count -gt 0) {
            if ($ExpectedInstance -and $foreground[-1].instanceId -ne $ExpectedInstance) { throw 'Continue created or activated a different instance.' }
            return $foreground[-1]
        }
        return $null
    } 'a new foreground event emitted after the tested action'
}

function Find-SmokeNamedElement($WindowElement, [string]$Name, $ControlType) {
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsEnabledProperty, $true),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsOffscreenProperty, $false))
    $element = $WindowElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    if ($element) { return $element }
    # WinUI composition hosting exposes the Chromium accessibility document separately.
    # Match only this sample and verify the renderer's process ancestry and window bounds.
    $documentCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, '元素配对 · Lab Chronicles'),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Document))
    $documents = [System.Windows.Automation.AutomationElement]::RootElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $documentCondition)
    $windowBounds = $WindowElement.Current.BoundingRectangle
    foreach ($document in $documents) {
        $bounds = $document.Current.BoundingRectangle
        if ($bounds.IsEmpty -or $bounds.Left -lt $windowBounds.Left -or $bounds.Top -lt $windowBounds.Top -or
            $bounds.Right -gt $windowBounds.Right -or $bounds.Bottom -gt $windowBounds.Bottom) { continue }
        $processId = $document.Current.ProcessId
        $belongsToProduct = $false
        for ($depth = 0; $depth -lt 10 -and $processId -gt 0; $depth++) {
            if ($processId -eq $WindowElement.Current.ProcessId) { $belongsToProduct = $true; break }
            $processInfo = Get-CimInstance Win32_Process -Filter "ProcessId=$processId"
            if (-not $processInfo -or $processInfo.ParentProcessId -eq $processId) { break }
            $processId = $processInfo.ParentProcessId
        }
        if (-not $belongsToProduct) { continue }
        $element = $document.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element) { return $element }
    }
    return $null
}

function Invoke-SmokeNamedButton($WindowElement, [string]$Name) {
    $button = Wait-SmokeCondition { Find-SmokeNamedElement $WindowElement $Name ([System.Windows.Automation.ControlType]::Button) } "button $Name"
    if ($Name -in @('保存进度', '读取存档')) {
        $owner = @($ownedProcesses | Where-Object { -not $_.HasExited -and $_.Id -eq $WindowElement.Current.ProcessId })
        if ($owner.Count -ne 1) { throw 'No held test-owned process for native application input.' }
        $inputActions.Add((Invoke-AutumnNativeElementClick -WindowElement $WindowElement -Element $button -Process $owner[0]))
        return
    }
    $pattern = $null
    if ($button.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) { $pattern.Invoke() }
    elseif ($button.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$pattern)) { $pattern.Toggle() }
    else { throw "Button has no supported activation pattern: $Name" }
    if ($Name -eq '关闭') {
        $null = Wait-SmokeCondition { -not (Find-SmokeNamedElement $WindowElement '关闭' ([System.Windows.Automation.ControlType]::Button)) } 'the native dialog dismissed'
        # ContentDialog completes its host-side ShowAsync after the closing animation.
        Start-Sleep -Milliseconds 350
    }
}

function Close-SmokeProduct($Product) {
    if (-not $Product.Process.HasExited) {
        $null = [AutumnSingleInstanceSmoke.NativeWindows]::SetWindowPos($Product.Handle, [IntPtr](-2), 0, 0, 0, 0, 3)
        $closed = $Product.Process.CloseMainWindow()
        if (-not $closed -or -not $Product.Process.WaitForExit(10000)) { throw 'The recorded product window did not close cleanly.' }
    }
    Add-SmokeCheck 'recorded_product_process_closed_cleanly' ($Product.Process.ExitCode -eq 0)
}

function Save-SmokeScreenshot($Product, [string]$Name, [switch]$IncludeOverlays) {
    $path = Join-Path $ReportDirectory "$Name.png"
    $bitmap = $null
    $graphics = $null
    try {
        if ($IncludeOverlays) { Focus-SmokeProduct $Product }
        Start-Sleep -Milliseconds 350
        $bounds = [AutumnSingleInstanceSmoke.NativeWindows+RECT]::new()
        if (-not [AutumnSingleInstanceSmoke.NativeWindows]::GetClientRect($Product.Handle, [ref]$bounds)) { throw 'Cannot read the product client bounds.' }
        $clientOrigin = [AutumnSingleInstanceSmoke.NativeWindows+POINT]::new()
        if (-not [AutumnSingleInstanceSmoke.NativeWindows]::ClientToScreen($Product.Handle, [ref]$clientOrigin)) { throw 'Cannot locate the product client area.' }
        $width = $bounds.Right - $bounds.Left
        $height = $bounds.Bottom - $bounds.Top
        if ($width -le 0 -or $height -le 0 -or $width -gt 10000 -or $height -gt 10000) { throw 'Invalid window capture bounds.' }
        $bitmap = [Drawing.Bitmap]::new($width, $height)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $printed = $false
        if (-not $IncludeOverlays) {
            $deviceContext = $graphics.GetHdc()
            # PW_CLIENTONLY | PW_RENDERFULLCONTENT excludes window shadows and the desktop beneath them.
            try { $printed = [AutumnSingleInstanceSmoke.NativeWindows]::PrintWindow($Product.Handle, $deviceContext, 3) }
            finally { $graphics.ReleaseHdc($deviceContext) }
        }
        $captureMode = 'PrintWindow_CLIENTONLY_RENDERFULLCONTENT'
        if (-not $printed) {
            Focus-SmokeProduct $Product
            # Native flyouts require visible pixels; capture only this process's client rectangle.
            # Never include the non-client shadow, another application, or the full desktop.
            Assert-SmokeCaptureUnoccluded $Product $clientOrigin.X $clientOrigin.Y $width $height
            $graphics.CopyFromScreen($clientOrigin.X, $clientOrigin.Y, 0, 0, [Drawing.Size]::new($width, $height))
            Assert-SmokeCaptureUnoccluded $Product $clientOrigin.X $clientOrigin.Y $width $height
            $captureMode = 'foreground_product_client_rectangle'
        }
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        $screenshots.Add([ordered]@{ name = $Name; status = 'captured'; path = $path; width = $width; height = $height; process_id = $Product.Process.Id;
            capture_mode = $captureMode; capture_bounds = 'product_client_area'; screen_x = $clientOrigin.X; screen_y = $clientOrigin.Y })
        Write-Host "SCREENSHOT $path"
        return $path
    } catch {
        $screenshots.Add([ordered]@{ name = $Name; status = 'not_run'; reason = $_.Exception.Message })
        Write-Warning "Screenshot $Name unavailable: $($_.Exception.Message)"
        return $null
    } finally {
        if ($graphics) { $graphics.Dispose() }
        if ($bitmap) { $bitmap.Dispose() }
    }
}

function Initialize-SingleSmokeNative {
    $desktopRuntime = Join-Path $projectRoot '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach ($assemblyName in @('WindowsBase.dll', 'UIAutomationTypes.dll', 'UIAutomationClient.dll', 'System.Drawing.Common.dll')) {
        Add-Type -Path (Join-Path $desktopRuntime $assemblyName)
    }
    if (-not ('AutumnSingleInstanceSmoke.NativeWindows' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace AutumnSingleInstanceSmoke {
    public static class NativeWindows {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PrintWindow(IntPtr hwnd, IntPtr deviceContext, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
        [DllImport("user32.dll")] public static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extraInfo);
        [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
        [DllImport("user32.dll")] public static extern IntPtr GetTopWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint command);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hwnd, uint attribute, out int value, uint size);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint GetDpiForSystem();
        [DllImport("user32.dll")] public static extern IntPtr GetThreadDpiAwarenessContext();
        [DllImport("user32.dll")] public static extern int GetAwarenessFromDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("ntdll.dll")] public static extern int NtSuspendProcess(IntPtr handle);
        [DllImport("ntdll.dll")] public static extern int NtResumeProcess(IntPtr handle);
        [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll", SetLastError=true)] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool AllowSetForegroundWindow(uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsHungAppWindow(IntPtr hwnd);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr hwnd,System.Text.StringBuilder name,int maxCount);
    }
    // A test-owned native window gives this launch driver legitimate foreground rights.
    // It is not an AutumnOS business window and never accesses product data.
    public static class ForegroundProbe {
        static System.Threading.Thread thread;
        static IntPtr window;
        static uint threadId;
        delegate IntPtr WindowProc(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
        static readonly WindowProc procedure=(hwnd,message,wParam,lParam)=>DefWindowProc(hwnd,message,wParam,lParam);
        [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct WNDCLASS { public uint style; public IntPtr procedure; public int classExtra,windowExtra; public IntPtr instance,icon,cursor,background; [MarshalAs(UnmanagedType.LPWStr)]public string menu; [MarshalAs(UnmanagedType.LPWStr)]public string name; }
        [StructLayout(LayoutKind.Sequential)] struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public NativeWindows.POINT point; public uint privateData; }
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr CreateWindowEx(uint exStyle,string className,string title,uint style,int x,int y,int width,int height,IntPtr parent,IntPtr menu,IntPtr instance,IntPtr parameter);
        [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern ushort RegisterClass(ref WNDCLASS windowClass);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern IntPtr DefWindowProc(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern bool UnregisterClass(string name,IntPtr instance);
        [DllImport("user32.dll")] static extern int GetMessage(out MSG message,IntPtr window,uint min,uint max);
        [DllImport("user32.dll")] static extern bool TranslateMessage(ref MSG message);
        [DllImport("user32.dll")] static extern IntPtr DispatchMessage(ref MSG message);
        [DllImport("user32.dll")] static extern bool PostThreadMessage(uint thread,uint message,UIntPtr wParam,IntPtr lParam);
        [DllImport("user32.dll")] static extern bool DestroyWindow(IntPtr window);
        [DllImport("user32.dll")] static extern bool IsWindow(IntPtr window);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll",CharSet=CharSet.Unicode)] static extern IntPtr GetModuleHandle(string name);
        public static IntPtr Start() {
            if(window!=IntPtr.Zero)return window;
            var ready=new System.Threading.ManualResetEventSlim();
            thread=new System.Threading.Thread(()=>{
                threadId=GetCurrentThreadId();
                var instance=GetModuleHandle(null);
                var className="AutumnSingleInstanceSmokeForegroundProbe";
                var windowClass=new WNDCLASS { procedure=Marshal.GetFunctionPointerForDelegate(procedure),instance=instance,background=(IntPtr)6,name=className };
                if(RegisterClass(ref windowClass)!=0)
                    window=CreateWindowEx(0,className,"AutumnOS - owned launcher focus probe",0x10CF0000,40,40,430,150,IntPtr.Zero,IntPtr.Zero,instance,IntPtr.Zero);
                ready.Set();
                if(window==IntPtr.Zero)return;
                try { while(GetMessage(out var message,IntPtr.Zero,0,0)>0){TranslateMessage(ref message);DispatchMessage(ref message);} }
                finally { if(IsWindow(window))DestroyWindow(window);window=IntPtr.Zero;UnregisterClass(className,instance); }
            });
            thread.IsBackground=true;thread.SetApartmentState(System.Threading.ApartmentState.STA);thread.Start();
            if(!ready.Wait(TimeSpan.FromSeconds(5)) || window==IntPtr.Zero)throw new InvalidOperationException("Cannot create the test-owned foreground probe.");
            return window;
        }
        public static void Stop() {
            if(thread==null)return;
            PostThreadMessage(threadId,0x12,UIntPtr.Zero,IntPtr.Zero);
            if(!thread.Join(5000))throw new InvalidOperationException("The test-owned foreground probe did not stop.");
            thread=null;
        }
    }
}
'@
    }


}

function Get-SingleDataFingerprint([string]$Directory) {
    if (-not (Test-Path -LiteralPath $Directory)) { return [pscustomobject]@{ exists=$false; files=0; sha256='absent' } }
    if ((Get-Item -LiteralPath $Directory -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Data fingerprint refuses a reparse-point root.' }
    $entries = @(Get-ChildItem -LiteralPath $Directory -Recurse -Force | Sort-Object FullName)
    if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Data fingerprint refuses nested reparse points.' }
    $lines = foreach ($entry in $entries) {
        $relative = [IO.Path]::GetRelativePath($Directory, $entry.FullName)
        if ($entry.PSIsContainer) { 'D|' + $relative }
        else { 'F|' + $relative + '|' + $entry.Length + '|' + (Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash }
    }
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($lines -join "`n")))
    return [pscustomobject]@{ exists=$true; files=@($entries | Where-Object { -not $_.PSIsContainer }).Count; sha256=[Convert]::ToHexString($hash).ToLowerInvariant() }
}

function Get-SingleBinaryMetadata([string]$Path) {
    $context=[Runtime.Loader.AssemblyLoadContext]::new(('single-smoke-metadata-'+[Guid]::NewGuid().ToString('N')),$true)
    try {
        $assembly=$context.LoadFromAssemblyPath([IO.Path]::GetFullPath($Path));$result=@{}
        foreach($attribute in $assembly.GetCustomAttributesData()) {
            if($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute') {$result[$attribute.ConstructorArguments[0].Value]=$attribute.ConstructorArguments[1].Value}
            elseif($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyProductAttribute') {$result.ProductName=$attribute.ConstructorArguments[0].Value}
            elseif($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyInformationalVersionAttribute') {$result.ProductVersion=$attribute.ConstructorArguments[0].Value}
        }
        return $result
    } finally {$context.Unload()}
}

function Copy-SingleBundle([string]$Source, [string]$Destination, [string]$ExpectedHash=$report.source_executable_sha256) {
    if (Test-Path -LiteralPath $Destination) { throw 'Isolated bundle destination already exists.' }
    New-Item -ItemType Directory -Path $Destination | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $Source -Force) {
        if ($entry.Name -eq 'AutumnOS_Data') { continue }
        if ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Bundle contains a reparse point.' }
        if ($entry.PSIsContainer -and @(Get-ChildItem -LiteralPath $entry.FullName -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Bundle contains a nested reparse point.' }
        Copy-Item -LiteralPath $entry.FullName -Destination $Destination -Recurse
    }
    $copied = Join-Path $Destination 'AutumnOS.exe'
    Add-SmokeCheck 'isolated_copy_matches_real_executable' ((Get-FileHash -LiteralPath $copied -Algorithm SHA256).Hash.ToLowerInvariant() -eq $ExpectedHash) $Destination
    return $copied
}

function Start-SingleProcess([string]$Path, [string]$WorkingDirectory, [string]$Role, [switch]$Shortcut, [switch]$GrantForeground) {
    $index = $launches.Count + 1
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $Path
    $startInfo.WorkingDirectory = $WorkingDirectory
    $startInfo.UseShellExecute = [bool]$Shortcut
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal
    if (-not $Shortcut) { $startInfo.RedirectStandardOutput=$true; $startInfo.RedirectStandardError=$true }
    $process = [Diagnostics.Process]::Start($startInfo)
    if (-not $process) { throw 'Launch returned no process handle; this launch cannot be safely owned or verified.' }
    $foregroundGrant=$null
    if($GrantForeground){$foregroundGrant=[AutumnSingleInstanceSmoke.NativeWindows]::AllowSetForegroundWindow([uint32]$process.Id)}
    $ownedProcesses.Add($process)
    $report.process_ids += $process.Id
    $stdoutPath = Join-Path $ReportDirectory ('launch-{0:d2}-stdout.log' -f $index)
    $stderrPath = Join-Path $ReportDirectory ('launch-{0:d2}-stderr.log' -f $index)
    $record = [ordered]@{ sequence=$index; role=$Role; process_id=$process.Id; executable_or_shortcut=$Path;
        working_directory=$WorkingDirectory; start_utc=$process.StartTime.ToUniversalTime().ToString('o');
        shell_execute=[bool]$Shortcut; foreground_grant_to_secondary=$foregroundGrant; stdout=$(if($Shortcut){'not_redirected_shell_shortcut'}else{$stdoutPath});
        stderr=$(if($Shortcut){'not_redirected_shell_shortcut'}else{$stderrPath}); exit_code=$null; business_window_seen=$false }
    $launches.Add($record)
    $stdoutTask = $null; $stderrTask = $null
    if (-not $Shortcut) { $stdoutTask=$process.StandardOutput.ReadToEndAsync(); $stderrTask=$process.StandardError.ReadToEndAsync() }
    $actualExecutable = if ($Shortcut) { $script:shortcutTarget } else { $Path }
    $launch = [pscustomobject]@{ Process=$process; Executable=$actualExecutable; StartedTicks=$process.StartTime.ToUniversalTime().Ticks;
        Record=$record; OutTask=$stdoutTask; ErrorTask=$stderrTask; OutPath=$stdoutPath; ErrorPath=$stderrPath }
    $ownedLaunches.Add($launch)
    return $launch
}

function Complete-SingleOutput($Launch) {
    if (-not $Launch.Process.HasExited) { return }
    $Launch.Record.exit_code = $Launch.Process.ExitCode
    if ($Launch.OutTask) {
        [IO.File]::WriteAllText($Launch.OutPath, $Launch.OutTask.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($Launch.ErrorPath, $Launch.ErrorTask.GetAwaiter().GetResult(), [Text.UTF8Encoding]::new($false))
    }
}

function Get-SingleWindow($Launch) {
    $process=$Launch.Process
    $handle=Wait-SmokeCondition {
        $process.Refresh()
        if ($process.HasExited) { Complete-SingleOutput $Launch; throw "Primary exited before a usable native window (exit $($process.ExitCode))." }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { return $process.MainWindowHandle }
        return $null
    } 'a primary native window'
    $element=[System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $null=Wait-SmokeCondition { (Find-VisibleSmokeElement $element 'HelloNextButton') -or (Find-VisibleSmokeElement $element 'SampleButton') } 'primary first-run or desktop controls'
    Add-SmokeCheck 'primary_window_belongs_to_held_process' ($element.Current.ProcessId -eq $process.Id -and [AutumnSingleInstanceSmoke.NativeWindows]::IsWindowVisible($handle))
    $Launch.Record.business_window_seen=$true
    return [pscustomobject]@{ Process=$process; Handle=$handle; Element=$element; Launch=$Launch }
}

function Wait-SingleSecondary($Launch, [int]$ExpectedExit=0, [int]$TimeoutSeconds=35) {
    $timer=[Diagnostics.Stopwatch]::StartNew()
    do {
        $Launch.Process.Refresh()
        if ($Launch.Process.HasExited) { break }
        if ($Launch.Process.MainWindowHandle -ne [IntPtr]::Zero) {
            $Launch.Record.business_window_seen=$true
            throw 'A secondary launcher created a window instead of exiting through the existing primary.'
        }
        Start-Sleep -Milliseconds 50
    } while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds)
    if (-not $Launch.Process.HasExited) { throw "Secondary launcher exceeded its bounded wait ($TimeoutSeconds seconds)." }
    Complete-SingleOutput $Launch
    $Launch.Record.observed_exit_seconds=$timer.Elapsed.TotalSeconds
    Add-SmokeCheck "secondary_$($Launch.Record.sequence)_exit_$ExpectedExit" ($Launch.Process.ExitCode -eq $ExpectedExit)
    if ($Launch.OutTask) {
        $output=[IO.File]::ReadAllText($Launch.OutPath)+[IO.File]::ReadAllText($Launch.ErrorPath)
        $expectedRole=if($ExpectedExit -eq 0){'forwarded'}else{'failed'}
        Add-SmokeCheck "secondary_$($Launch.Record.sequence)_launcher_diagnostic" ($output -match ('AUTUMNOS_LAUNCHER\s+role='+$expectedRole+'\b') -and $output -match ('\bpid='+$Launch.Process.Id+'\b') -and $output -match ('\bcode='+$ExpectedExit+'\b'))
    }
}

function Read-LauncherEvents([string]$Root) {
    $path=Join-Path $Root 'AutumnOS_Data/Logs/launcher-events.jsonl'
    if (-not (Test-Path -LiteralPath $path)) { return @() }
    $content=[IO.File]::ReadAllText($path)
    $lines=$content.Split("`n")
    # A reader never treats a writer's unfinished final record as complete evidence.
    return @($lines | Select-Object -SkipLast 1 | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
}

function Assert-SinglePrimaryIdentity($Product, [string]$Name) {
    $Product.Process.Refresh()
    Add-SmokeCheck $Name (-not $Product.Process.HasExited -and $Product.Process.MainWindowHandle -eq $Product.Handle -and
        $Product.Element.Current.ProcessId -eq $Product.Process.Id)
    foreach ($owned in $ownedProcesses) {
        if ($owned.Id -eq $Product.Process.Id -or $owned.HasExited) { continue }
        $owned.Refresh()
        if ($owned.MainWindowHandle -ne [IntPtr]::Zero) { throw 'Another script-started process has a second native window.' }
    }
}

function Invoke-SingleRecall($Product, [string]$BundleRoot, [string]$Path, [string]$WorkingDirectory, [string]$Name, [switch]$Shortcut) {
    if (-not $script:focusProbeProduct) {
        $probeHandle=[AutumnSingleInstanceSmoke.ForegroundProbe]::Start()
        $script:focusProbeProduct=[pscustomobject]@{Process=[Diagnostics.Process]::GetCurrentProcess();Handle=$probeHandle;Element=[System.Windows.Automation.AutomationElement]::FromHandle($probeHandle)}
    }
    $null=[AutumnSingleInstanceSmoke.NativeWindows]::ShowWindow($script:focusProbeProduct.Handle,5)
    Focus-SmokeProduct $script:focusProbeProduct -RequireCaptionInput
    Add-SmokeCheck "${Name}_launch_driver_has_legitimate_foreground" (Test-SmokeForeground $script:focusProbeProduct)
    $baseline=@(Read-LauncherEvents $BundleRoot).Count
    $launch=Start-SingleProcess $Path $WorkingDirectory $Name -Shortcut:$Shortcut -GrantForeground
    Add-SmokeCheck "${Name}_secondary_receives_legitimate_foreground_grant" $launch.Record.foreground_grant_to_secondary
    Wait-SingleSecondary $launch
    $event=Wait-SmokeCondition {
        $fresh=@(Read-LauncherEvents $BundleRoot | Select-Object -Skip $baseline |
            Where-Object { $_.eventName -eq 'recall_handled' -and $_.requestPid -eq $launch.Process.Id })
        if ($fresh.Count) { return $fresh[-1] }
        return $null
    } 'primary evidence for this exact secondary request'
    Add-SmokeCheck "${Name}_recall_matches_existing_pid_and_hwnd" ($event.primaryPid -eq $Product.Process.Id -and [long]$event.windowHandle -eq $Product.Handle.ToInt64())
    $launch.Record.recall_outcome=$event.outcome
    $launch.Record.primary_foreground_after_recall=Test-SmokeForeground $Product
    Add-SmokeCheck "${Name}_product_itself_activates_existing_hwnd" ($event.outcome -eq 'foreground' -and (Test-SmokeForeground $Product)) 'Checked before activating the primary through any test helper.'
    $report.foreground_activation='passed'
    Assert-SinglePrimaryIdentity $Product "${Name}_keeps_same_native_window"
    $null=[AutumnSingleInstanceSmoke.NativeWindows]::ShowWindow($script:focusProbeProduct.Handle,0)
    return $event
}

function Assert-SingleFaultOwnership($Launch) {
    $process=$Launch.Process
    if ($process.HasExited -or $process.StartTime.ToUniversalTime().Ticks -ne $Launch.StartedTicks -or
        -not [string]::Equals($process.MainModule.FileName,$Launch.Executable,[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Refusing fault injection: the held process identity/path/start time no longer match.'
    }
    $expectedRoot=[IO.Path]::GetFullPath($scope).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    if (-not [IO.Path]::GetFullPath($Launch.Executable).StartsWith($expectedRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Fault injection target is outside the isolated test scope.' }
}

function Disable-SingleFaultResources([string]$BundleRoot) {
    $boundary=[IO.Path]::GetFullPath($BundleRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    $scopeBoundary=[IO.Path]::GetFullPath($scope).TrimEnd([IO.Path]::DirectorySeparatorChar)+[IO.Path]::DirectorySeparatorChar
    if(-not $boundary.StartsWith($scopeBoundary,[StringComparison]::OrdinalIgnoreCase)){throw 'Resource fault bundle escaped the isolated scope.'}
    # WinUI embeds XBF content in the application PRI. Removing only the loose XBF
    # does not reliably remove the runtime resource, so preserve both real files.
    foreach($name in @('AutumnOS.pri','MainWindow.xbf')) {
        $resource=[IO.Path]::GetFullPath((Join-Path $BundleRoot $name))
        $preserved=[IO.Path]::GetFullPath((Join-Path $BundleRoot ($name+'.isolated-test-disabled')))
        if(-not $resource.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase) -or -not $preserved.StartsWith($boundary,[StringComparison]::OrdinalIgnoreCase)){throw 'Resource fault paths escaped the isolated bundle directory.'}
        [IO.File]::Move($resource,$preserved)
    }
}

function Finish-SingleOnboarding($Product) {
    if (Find-VisibleSmokeElement $Product.Element 'HelloNextButton') {
        Invoke-SmokeButton $Product.Element 'HelloNextButton'
        $null=Wait-SmokeCondition {Find-VisibleSmokeElement $Product.Element 'BrandNextButton'} 'the next onboarding brand page'
    }
    if (Find-VisibleSmokeElement $Product.Element 'BrandNextButton') { Invoke-SmokeButton $Product.Element 'BrandNextButton' }
    $null=Wait-SmokeCondition { (Find-VisibleSmokeElement $Product.Element 'PrepareDesktopButton') -or (Find-VisibleSmokeElement $Product.Element 'SampleButton') } 'preparation or completed desktop'
    if (Find-VisibleSmokeElement $Product.Element 'PrepareDesktopButton') { Invoke-SmokeButton $Product.Element 'PrepareDesktopButton' }
    Wait-SmokeDesktop $Product
}

function Assert-SingleBackgroundUnchanged($Product, [string]$RuntimePath, [string]$InstanceId, [int]$BaselineCount, [string]$Name) {
    Wait-SmokeDesktop $Product
    $events=@(Read-SmokeEvents $RuntimePath)
    $fresh=@($events | Select-Object -Skip $BaselineCount)
    Add-SmokeCheck $Name ($fresh.Count -eq 0 -and $events[-1].instanceId -eq $InstanceId -and $events[-1].state -eq 'Background' -and
        $events[-1].blocksMaintenance -and $null -eq (Find-VisibleSmokeElement $Product.Element 'RuntimeStatus'))
    Assert-SmokeRunningBadges $Product $true "${Name}_both_badges_remain"
}

try {
    if (-not $IsWindows) { throw 'Single-instance smoke requires a real interactive Windows session.' }
    $identity=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    $report.isElevated=$identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Add-SmokeCheck 'standard_user_token' (-not $report.isElevated)
    $existing=@(Get-Process -Name AutumnOS -ErrorAction SilentlyContinue)
    if ($existing.Count) {
        $report.existing_processes=@($existing | Select-Object Id,SessionId,MainWindowHandle)
        $report.status='blocked_existing_user_instance'
        throw 'Existing AutumnOS process detected. It was not activated, suspended or closed. Ask its user to exit it normally before rerunning this isolated test.'
    }
    $ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath)
    if ([IO.Path]::GetFileName($ExecutablePath) -ne 'AutumnOS.exe' -or -not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw 'ExecutablePath must be the actual built or delivered AutumnOS.exe.' }
    $sourceRoot=Split-Path $ExecutablePath -Parent
    $report.source_executable=$ExecutablePath
    $report.source_executable_sha256=(Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $sourceData=Join-Path $sourceRoot 'AutumnOS_Data'
    $sourceDataBefore=Get-SingleDataFingerprint $sourceData
    $metadata=Get-SingleBinaryMetadata (Join-Path $sourceRoot 'AutumnOS.Contracts.dll')
    $report.build_id=$metadata.BuildId; $report.source_snapshot_id=$metadata.SourceSnapshotId
    $report.primary_version=$metadata.ProductVersion
    $peerRoot=$sourceRoot;$peerHash=$report.source_executable_sha256;$peerMetadata=$metadata
    if($PeerExecutablePath){
        $PeerExecutablePath=[IO.Path]::GetFullPath($PeerExecutablePath)
        if([IO.Path]::GetFileName($PeerExecutablePath) -ne 'AutumnOS.exe' -or -not (Test-Path -LiteralPath $PeerExecutablePath -PathType Leaf)){throw 'PeerExecutablePath must identify a real protocol-compatible AutumnOS.exe.'}
        $peerRoot=Split-Path $PeerExecutablePath -Parent
        $peerHash=(Get-FileHash -LiteralPath $PeerExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
        $peerMetadata=Get-SingleBinaryMetadata (Join-Path $peerRoot 'AutumnOS.Contracts.dll')
        Add-SmokeCheck 'provided_peer_has_a_real_distinct_binary_version' ($peerMetadata.ProductVersion -ne $metadata.ProductVersion)
        $peerDataBefore=Get-SingleDataFingerprint (Join-Path $peerRoot 'AutumnOS_Data')
    }
    $report.peer_source_executable=if($PeerExecutablePath){$PeerExecutablePath}else{$ExecutablePath}
    $report.peer_source_executable_sha256=$peerHash;$report.peer_version=$peerMetadata.ProductVersion
    $report.peer_build_id=$peerMetadata.BuildId;$report.peer_source_snapshot_id=$peerMetadata.SourceSnapshotId
    Initialize-SingleSmokeNative
    New-Item -ItemType Directory -Path $scope -Force | Out-Null
    $aRoot=Join-Path $scope '发行副本 A 中文 空格';$bRoot=Join-Path $scope '发行副本 B 中文 空格'
    $cRoot=Join-Path $scope '并发冷启 C';$dRoot=Join-Path $scope '资源故障 D'
    $copyDirectories=@($aRoot,$bRoot,$cRoot,$dRoot)
    $aExe=Copy-SingleBundle $sourceRoot $aRoot
    $bExe=Copy-SingleBundle $peerRoot $bRoot $peerHash
    $cExe=Copy-SingleBundle $sourceRoot $cRoot
    $dExe=Copy-SingleBundle $sourceRoot $dRoot
    $report.copy_directories=$copyDirectories
    $report.copy_test_kind=if($PeerExecutablePath){'A/C/D use the real primary build; B uses an independently built, distinct supported version. No legacy compatibility claim.'}else{'Four byte-identical supported build copies at distinct physical paths; no claim that legacy versions implement this protocol.'}
    $cwdOne=Join-Path $scope '工作目录 一';$cwdTwo=Join-Path $scope '工作目录 二'
    New-Item -ItemType Directory -Path $cwdOne,$cwdTwo | Out-Null
    $bSentinel=Join-Path $bRoot 'AutumnOS_Data/Saves/preexisting-test-save.bin'
    New-Item -ItemType Directory -Path (Split-Path $bSentinel -Parent) | Out-Null
    [IO.File]::WriteAllBytes($bSentinel,[Text.Encoding]::UTF8.GetBytes("Isolated B data belongs only to B | $smokeId"))
    $bBefore=Get-SingleDataFingerprint (Join-Path $bRoot 'AutumnOS_Data')
    $bSentinelHash=(Get-FileHash -LiteralPath $bSentinel -Algorithm SHA256).Hash
    $primary=Start-SingleProcess $aExe $cwdOne 'initial_primary'
    $activeProduct=Get-SingleWindow $primary
    $report.standard_user_window='passed'
    $report.initial_primary_pid=$primary.Process.Id;$report.initial_window_handle=$activeProduct.Handle.ToInt64()
    Finish-SingleOnboarding $activeProduct
    $ready=Wait-SmokeCondition {
        @(Read-LauncherEvents $aRoot | Where-Object { $_.eventName -eq 'window_ready' -and $_.primaryPid -eq $primary.Process.Id }) | Select-Object -Last 1
    } 'window_ready from the actual elected primary'
    Add-SmokeCheck 'launcher_window_ready_matches_actual_handle' ([long]$ready.windowHandle -eq $activeProduct.Handle.ToInt64() -and $ready.buildId -eq $report.build_id -and $ready.sourceSnapshotId -eq $report.source_snapshot_id)
    $dataA=Join-Path $aRoot 'AutumnOS_Data';$statePath=Join-Path $dataA 'Config/first-run.json'
    $stateHash=(Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash
    $runtimePath=Join-Path $dataA 'Logs/runtime-events.jsonl'
    $sampleArchive=[IO.Compression.ZipFile]::OpenRead((Join-Path $aRoot 'Samples/element-pairs.autumn'))
    try { $reader=[IO.StreamReader]::new($sampleArchive.GetEntry('manifest.json').Open());try{$manifest=$reader.ReadToEnd()|ConvertFrom-Json}finally{$reader.Dispose()} } finally{$sampleArchive.Dispose()}
    if($manifest.appId -notmatch '^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)+$'){throw 'Unsafe bundled app identity.'}
    $savePath=Join-Path $dataA "Saves/$($manifest.appId)/guest/game.json"
    $report.sample_app_id=$manifest.appId;$report.sample_version=$manifest.version

    $null=Invoke-SingleRecall $activeProduct $aRoot $aExe $cwdOne 'normal_recall'
    Add-SmokeCheck 'normal_recall_keeps_normal_visible_window' (-not [AutumnSingleInstanceSmoke.NativeWindows]::IsIconic($activeProduct.Handle) -and -not [AutumnSingleInstanceSmoke.NativeWindows]::IsZoomed($activeProduct.Handle))
    $report.normal_recall='passed'
    $null=Save-SmokeScreenshot $activeProduct '01-same-normal-window'
    $null=[AutumnSingleInstanceSmoke.NativeWindows]::ShowWindow($activeProduct.Handle,6)
    $null=Wait-SmokeCondition {[AutumnSingleInstanceSmoke.NativeWindows]::IsIconic($activeProduct.Handle)} 'minimized normal primary'
    $null=Invoke-SingleRecall $activeProduct $aRoot $aExe $cwdTwo 'minimized_recall'
    Add-SmokeCheck 'minimized_normal_window_is_restored_without_maximizing' (-not [AutumnSingleInstanceSmoke.NativeWindows]::IsIconic($activeProduct.Handle) -and -not [AutumnSingleInstanceSmoke.NativeWindows]::IsZoomed($activeProduct.Handle))
    $report.minimized_recall='passed'
    $null=Save-SmokeScreenshot $activeProduct '02-restored-normal-window'
    $null=[AutumnSingleInstanceSmoke.NativeWindows]::ShowWindow($activeProduct.Handle,3)
    $null=Wait-SmokeCondition {[AutumnSingleInstanceSmoke.NativeWindows]::IsZoomed($activeProduct.Handle)} 'maximized primary'
    $null=Invoke-SingleRecall $activeProduct $aRoot $aExe $cwdOne 'maximized_recall'
    Add-SmokeCheck 'recall_preserves_maximized_state' ([AutumnSingleInstanceSmoke.NativeWindows]::IsZoomed($activeProduct.Handle))
    $null=[AutumnSingleInstanceSmoke.NativeWindows]::ShowWindow($activeProduct.Handle,6)
    $null=Wait-SmokeCondition {[AutumnSingleInstanceSmoke.NativeWindows]::IsIconic($activeProduct.Handle)} 'minimized maximized primary'
    $null=Invoke-SingleRecall $activeProduct $aRoot $aExe $cwdTwo 'minimized_maximized_recall'
    Add-SmokeCheck 'recall_restores_previously_maximized_window_as_maximized' (-not [AutumnSingleInstanceSmoke.NativeWindows]::IsIconic($activeProduct.Handle) -and [AutumnSingleInstanceSmoke.NativeWindows]::IsZoomed($activeProduct.Handle))
    $report.maximized_recall='passed'
    $null=Save-SmokeScreenshot $activeProduct '03-restored-maximized-window'
    $null=[AutumnSingleInstanceSmoke.NativeWindows]::ShowWindow($activeProduct.Handle,9)
    $null=Wait-SmokeCondition {-not [AutumnSingleInstanceSmoke.NativeWindows]::IsZoomed($activeProduct.Handle)} 'normal primary after restoration'

    $shortcut=Join-Path $scope 'AutumnOS 隔离副本快捷方式.lnk';$script:shortcutTarget=$aExe
    $shell=New-Object -ComObject WScript.Shell
    try { $link=$shell.CreateShortcut($shortcut);$link.TargetPath=$aExe;$link.WorkingDirectory=$cwdTwo;$link.Description='Isolated AutumnOS single-instance test';$link.Save() }
    finally { if($link){[Runtime.InteropServices.Marshal]::FinalReleaseComObject($link)|Out-Null};[Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell)|Out-Null }
    $null=Invoke-SingleRecall $activeProduct $aRoot $shortcut $cwdOne 'shortcut_recall' -Shortcut
    $report.shortcut_launch='passed'
    $crossRecall=Invoke-SingleRecall $activeProduct $aRoot $bExe $cwdTwo 'cross_copy_recall'
    Add-SmokeCheck 'cross_copy_recall_preserves_primary_binary_version' ($crossRecall.version -eq $metadata.ProductVersion -and $crossRecall.buildId -eq $metadata.BuildId)
    Add-SmokeCheck 'secondary_copy_does_not_initialize_or_modify_its_data' ((Get-SingleDataFingerprint (Join-Path $bRoot 'AutumnOS_Data')).sha256 -eq $bBefore.sha256)
    Add-SmokeCheck 'different_working_directories_receive_no_business_data' (-not (Test-Path -LiteralPath (Join-Path $cwdOne 'AutumnOS_Data')) -and -not (Test-Path -LiteralPath (Join-Path $cwdTwo 'AutumnOS_Data')))
    $report.cross_copy_recall='passed';$report.multiple_working_directories='passed'

    Invoke-SmokePointer $activeProduct 'SampleButton' 'single_click'
    $null=Wait-SmokeCondition {(Find-VisibleSmokeElement $activeProduct.Element 'RuntimeStatus').Current.Name -match '前台运行'} 'real internal game'
    $gameInstance=(Wait-SmokeNewForeground $runtimePath 0 '').instanceId
    $report.game_instance_id=$gameInstance
    $nickname=Wait-SmokeCondition {Find-SmokeNamedElement $activeProduct.Element '玩家昵称' ([System.Windows.Automation.ControlType]::Edit)} 'nickname input'
    $nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('派蒙·单实例存档')
    Invoke-SmokeNamedButton $activeProduct.Element '第 1 张卡片，未翻开'
    Invoke-SmokeNamedButton $activeProduct.Element '第 2 张卡片，未翻开'
    Invoke-SmokeNamedButton $activeProduct.Element '保存进度'
    Invoke-SmokeNamedButton $activeProduct.Element '允许'
    $null=Wait-SmokeCondition {Test-Path -LiteralPath $savePath} 'real committed guest save'
    $saveHash=(Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash
    $nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('派蒙·单实例未保存输入')
    $gameBaseline=@(Read-SmokeEvents $runtimePath).Count
    $null=Save-SmokeScreenshot $activeProduct '04-game-before-repeat-launch'
    $null=Invoke-SingleRecall $activeProduct $aRoot $bExe $cwdTwo 'foreground_game_recall'
    Add-SmokeCheck 'foreground_game_recall_does_not_create_or_transition_runtime' (@(Read-SmokeEvents $runtimePath).Count -eq $gameBaseline -and
        (Find-VisibleSmokeElement $activeProduct.Element 'RuntimeStatus').Current.Name -match '前台运行')
    Add-SmokeCheck 'foreground_game_recall_keeps_unsaved_input_and_committed_save' ($nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '派蒙·单实例未保存输入' -and
        (Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash -eq $saveHash)
    $report.foreground_game_preservation='passed'
    $null=Save-SmokeScreenshot $activeProduct '05-game-after-repeat-launch'
    Invoke-SmokeButton $activeProduct.Element 'BackgroundButton';Wait-SmokeDesktop $activeProduct
    $backgroundBaseline=@(Read-SmokeEvents $runtimePath).Count
    $null=Invoke-SingleRecall $activeProduct $aRoot $aExe $cwdOne 'background_game_recall'
    Assert-SingleBackgroundUnchanged $activeProduct $runtimePath $gameInstance $backgroundBaseline 'repeated_launch_keeps_game_background'
    $null=Save-SmokeScreenshot $activeProduct '06-background-after-repeat-launch'
    Invoke-SmokePointer $activeProduct 'DesktopGestureHint' 'double_click';Wait-SmokeRunningPanel $activeProduct
    $continueBaseline=@(Read-SmokeEvents $runtimePath).Count
    Invoke-SmokeOwnedButton $activeProduct 'RunningContinueButton'
    $resumed=Wait-SmokeNewForeground $runtimePath $continueBaseline $gameInstance
    $nickname=Wait-SmokeCondition {Find-SmokeNamedElement $activeProduct.Element '玩家昵称' ([System.Windows.Automation.ControlType]::Edit)} 'continued same game input'
    Add-SmokeCheck 'explicit_continue_after_recall_keeps_same_instance_and_unsaved_input' ($resumed.instanceId -eq $gameInstance -and
        $nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '派蒙·单实例未保存输入')
    $report.background_game_preservation='passed'
    Invoke-SmokeButton $activeProduct.Element 'BackgroundButton';Wait-SmokeDesktop $activeProduct

    # No response is injected only into this isolated, held primary; it is always resumed before cleanup.
    $preSuspendLauncherCount=@(Read-LauncherEvents $aRoot).Count
    Assert-SingleFaultOwnership $primary
    $suspendCode=[AutumnSingleInstanceSmoke.NativeWindows]::NtSuspendProcess($primary.Process.Handle)
    if($suspendCode -ne 0){throw "NtSuspendProcess failed for the owned test process: $suspendCode"}
    $script:suspendedProcess=$primary
    try {
        [IntPtr]$response=[IntPtr]::Zero
        $nullResponse=[AutumnSingleInstanceSmoke.NativeWindows]::SendMessageTimeout($activeProduct.Handle,0,[IntPtr]::Zero,[IntPtr]::Zero,2,300,[ref]$response)
        Add-SmokeCheck 'isolated_primary_is_actually_unresponsive' ($nullResponse -eq [IntPtr]::Zero)
        $unresponsive=Start-SingleProcess $bExe $cwdTwo 'unresponsive_primary_forwarding'
        Wait-SingleSecondary $unresponsive 20
        Add-SmokeCheck 'unresponsive_forwarding_returns_within_bounded_budget' ($unresponsive.Record.observed_exit_seconds -lt 25)
        Add-SmokeCheck 'forwarding_timeout_never_creates_second_data_root' ((Get-SingleDataFingerprint (Join-Path $bRoot 'AutumnOS_Data')).sha256 -eq $bBefore.sha256)
        $report.unresponsive_primary='passed'
    } finally {
        $resumeCode=[AutumnSingleInstanceSmoke.NativeWindows]::NtResumeProcess($primary.Process.Handle)
        if($resumeCode -ne 0){throw "Owned primary resume failed: $resumeCode"}
        $script:suspendedProcess=$null
    }
    $null=Wait-SmokeCondition {
        [IntPtr]$reply=[IntPtr]::Zero
        $responsive=[AutumnSingleInstanceSmoke.NativeWindows]::SendMessageTimeout($activeProduct.Handle,0,[IntPtr]::Zero,[IntPtr]::Zero,2,200,[ref]$reply)
        $responsive -ne [IntPtr]::Zero -and -not [AutumnSingleInstanceSmoke.NativeWindows]::IsHungAppWindow($activeProduct.Handle)
    } 'the original HWND responding again after process resume'
    $settling=[Diagnostics.Stopwatch]::StartNew();$quiet=[Diagnostics.Stopwatch]::StartNew();$lastCount=-1
    do {
        $freshLauncher=@(Read-LauncherEvents $aRoot|Select-Object -Skip $preSuspendLauncherCount)
        $transportFailures=@($freshLauncher|Where-Object eventName -eq 'transport_unavailable')
        if($freshLauncher.Count -ne $lastCount){$lastCount=$freshLauncher.Count;$quiet.Restart()}
        if($freshLauncher.Count -gt 16 -or $transportFailures.Count -gt 8){throw 'Launcher recovery generated an unbounded transport/event loop after one disconnected request.'}
        if($settling.Elapsed.TotalSeconds -ge 2 -and $quiet.ElapsedMilliseconds -ge 500){break}
        Start-Sleep -Milliseconds 100
    }while($settling.Elapsed.TotalSeconds -lt 5)
    Add-SmokeCheck 'disconnected_request_recovery_does_not_spin_or_flood_logs' ($freshLauncher.Count -le 16 -and $transportFailures.Count -le 8 -and $settling.Elapsed.TotalSeconds -ge 2 -and $quiet.ElapsedMilliseconds -ge 500)
    $report.recovery_launcher_events=$freshLauncher.Count
    $report.recovery_transport_unavailable_events=$transportFailures.Count
    $report.recovery_observation_seconds=$settling.Elapsed.TotalSeconds
    $null=Invoke-SingleRecall $activeProduct $aRoot $aExe $cwdOne 'after_unresponsive_recovery'
    Assert-SinglePrimaryIdentity $activeProduct 'same_primary_recovers_after_injected_unresponsiveness'
    $report.transport_recovery='passed'
    Add-SmokeCheck 'failed_forwarding_preserves_committed_state_and_save' ((Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash -eq $stateHash -and (Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash -eq $saveHash)

    # Abrupt termination uses only the exact held process handle in this script-owned directory.
    Assert-SingleFaultOwnership $primary
    $crashedPid=$primary.Process.Id
    $primary.Process.Kill()
    if(-not $primary.Process.WaitForExit(10000)){throw 'The injected primary crash did not terminate.'}
    Complete-SingleOutput $primary
    $primary.Record.injected_termination=$true
    $activeProduct=$null
    $restarted=Start-SingleProcess $aExe $cwdTwo 'after_injected_crash'
    $activeProduct=Get-SingleWindow $restarted;Wait-SmokeDesktop $activeProduct
    Add-SmokeCheck 'crash_restart_elects_new_primary_without_lock_file_cleanup' ($restarted.Process.Id -ne $crashedPid)
    Add-SmokeCheck 'crash_restart_preserves_completed_state_and_committed_save' ((Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash -eq $stateHash -and (Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash -eq $saveHash)
    Assert-SmokeRunningBadges $activeProduct $false 'crash_restart_does_not_fabricate_a_surviving_game'
    $null=Save-SmokeScreenshot $activeProduct '07-primary-after-isolated-crash'
    $report.crash_restart='passed'
    Close-SmokeProduct $activeProduct;Complete-SingleOutput $restarted;$activeProduct=$null
    $stopped=@(Read-LauncherEvents $aRoot|Where-Object{$_.eventName -eq 'stopping' -and $_.primaryPid -eq $restarted.Process.Id})
    Add-SmokeCheck 'normal_exit_records_primary_stopping' ($stopped.Count -gt 0)
    $normalRestart=Start-SingleProcess $aExe $cwdOne 'after_normal_exit'
    $activeProduct=Get-SingleWindow $normalRestart;Wait-SmokeDesktop $activeProduct
    Add-SmokeCheck 'normal_exit_releases_single_instance_for_restart' ($normalRestart.Process.Id -ne $restarted.Process.Id)
    Close-SmokeProduct $activeProduct;Complete-SingleOutput $normalRestart;$activeProduct=$null
    $report.clean_exit_restart='passed'

    # Once A exits, B becomes primary and initializes only B's own portable data.
    $bPrimary=Start-SingleProcess $bExe $cwdTwo 'copy_b_becomes_primary'
    $activeProduct=Get-SingleWindow $bPrimary
    Add-SmokeCheck 'independent_copy_starts_its_own_first_run_after_a_exits' ($null -ne (Find-VisibleSmokeElement $activeProduct.Element 'HelloNextButton'))
    Finish-SingleOnboarding $activeProduct
    $bReady=Wait-SmokeCondition {@(Read-LauncherEvents $bRoot|Where-Object{$_.eventName -eq 'window_ready' -and $_.primaryPid -eq $bPrimary.Process.Id})|Select-Object -Last 1} 'B own primary version evidence'
    Add-SmokeCheck 'copy_b_uses_its_own_binary_version_when_primary' ($bReady.version -eq $peerMetadata.ProductVersion -and $bReady.buildId -eq $peerMetadata.BuildId)
    if($PeerExecutablePath){$report.distinct_build_versions='passed'}
    Add-SmokeCheck 'copy_b_preserves_preexisting_test_save' ((Get-FileHash -LiteralPath $bSentinel -Algorithm SHA256).Hash -eq $bSentinelHash)
    Add-SmokeCheck 'copy_b_does_not_borrow_or_overwrite_a_save' (-not (Test-Path -LiteralPath (Join-Path $bRoot "AutumnOS_Data/Saves/$($manifest.appId)/guest/game.json")) -and
        (Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash -eq $saveHash -and (Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash -eq $stateHash)
    Close-SmokeProduct $activeProduct;Complete-SingleOutput $bPrimary;$activeProduct=$null

    # The broken resources are preserved under other filenames in a fresh fault-only copy.
    Disable-SingleFaultResources $dRoot
    $broken=Start-SingleProcess $dExe $cwdOne 'isolated_resource_failure'
    Wait-SingleSecondary $broken 22
    $report.resource_failure='passed'
    $afterBroken=Start-SingleProcess $aExe $cwdTwo 'after_resource_failure'
    $activeProduct=Get-SingleWindow $afterBroken;Wait-SmokeDesktop $activeProduct
    Add-SmokeCheck 'resource_failure_releases_coordination_without_manual_cleanup' $true
    Close-SmokeProduct $activeProduct;Complete-SingleOutput $afterBroken;$activeProduct=$null

    # Hold only the real per-user/session election mutex, with no pipe listener.
    # This exercises IPC-unavailable failure separately from a suspended real primary.
    $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $sidHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sid))).ToLowerInvariant()
    $mutexName='LabChronicles.AutumnOS.Launcher.v1.'+$sidHash+'.s'+[Diagnostics.Process]::GetCurrentProcess().SessionId
    $mutexOptions=[Threading.NamedWaitHandleOptions]::new();$mutexOptions.CurrentUserOnly=$true;$mutexOptions.CurrentSessionOnly=$true
    $testMutex=[Threading.Mutex]::new($false,$mutexName,$mutexOptions);$ownsTestMutex=$false
    try {
        try {$ownsTestMutex=$testMutex.WaitOne(0)} catch [Threading.AbandonedMutexException] {$ownsTestMutex=$true}
        if(-not $ownsTestMutex){throw 'Another owner appeared before the isolated IPC-unavailable test; it was not modified.'}
        $noPipe=Start-SingleProcess $cExe $cwdTwo 'election_busy_but_ipc_unavailable'
        Wait-SingleSecondary $noPipe 20
        Add-SmokeCheck 'ipc_unavailable_never_initializes_a_second_business_root' (-not (Test-Path -LiteralPath (Join-Path $cRoot 'AutumnOS_Data')))
        $report.forwarding_failure_no_second_root='passed'
    } finally {if($ownsTestMutex){$testMutex.ReleaseMutex()};$testMutex.Dispose()}

    # Start every contender before waiting for any window or exit; this is an actual cold race.
    $race=[Collections.Generic.List[object]]::new();$raceTimer=[Diagnostics.Stopwatch]::StartNew()
    for($n=0;$n -lt $ConcurrentLaunchCount;$n++){$race.Add((Start-SingleProcess $cExe $(if($n%2){$cwdOne}else{$cwdTwo}) "cold_contender_$n"))}
    $report.concurrent_launch_span_ms=$raceTimer.ElapsedMilliseconds
    $winner=Wait-SmokeCondition {
        $live=@($race|Where-Object{-not $_.Process.HasExited})
        $readyPids=@(Read-LauncherEvents $cRoot|Where-Object eventName -eq 'window_ready'|Select-Object -ExpandProperty primaryPid -Unique)
        if($readyPids.Count -gt 1){throw 'Concurrent launch produced more than one window-ready primary.'}
        if($live.Count -eq 1 -and $readyPids.Count -eq 1 -and $live[0].Process.Id -eq $readyPids[0]){return $live[0]}
        return $null
    } 'exactly one surviving cold-start primary and all forwarded launchers exiting' 45
    foreach($contender in $race){if($contender.Process.Id -ne $winner.Process.Id){Wait-SingleSecondary $contender}}
    $activeProduct=Get-SingleWindow $winner
    $cEvents=@(Read-LauncherEvents $cRoot)
    Add-SmokeCheck 'concurrent_cold_start_has_one_primary_and_one_window' (@($cEvents|Where-Object eventName -eq 'primary_started').Count -eq 1 -and @($cEvents|Where-Object eventName -eq 'window_ready').Count -eq 1)
    Add-SmokeCheck 'every_cold_secondary_is_acknowledged_by_the_same_primary' (@($cEvents|Where-Object eventName -eq 'recall_handled'|Select-Object -ExpandProperty requestPid -Unique).Count -eq ($ConcurrentLaunchCount-1))
    Assert-SinglePrimaryIdentity $activeProduct 'cold_race_keeps_only_the_elected_native_window'
    $report.concurrent_winner_pid=$winner.Process.Id;$report.concurrent_cold_start='passed'
    $null=Save-SmokeScreenshot $activeProduct '08-concurrent-cold-start-winner'
    Close-SmokeProduct $activeProduct;Complete-SingleOutput $winner;$activeProduct=$null
    Add-SmokeCheck 'all_test_owned_processes_exited' (@($ownedProcesses|Where-Object{-not $_.HasExited}).Count -eq 0)
    $requiredScreenshots=@('01-same-normal-window','02-restored-normal-window','03-restored-maximized-window','04-game-before-repeat-launch',
        '05-game-after-repeat-launch','06-background-after-repeat-launch','07-primary-after-isolated-crash','08-concurrent-cold-start-winner')
    $missingScreenshots=@($requiredScreenshots|Where-Object{$expected=$_;-not @($screenshots|Where-Object{$_.name -eq $expected -and $_.status -eq 'captured'}).Count})
    Add-SmokeCheck 'all_single_instance_evidence_screenshots_captured' ($missingScreenshots.Count -eq 0)
    $sourceDataAfter=Get-SingleDataFingerprint $sourceData
    Add-SmokeCheck 'source_bundle_user_data_is_unchanged' ($sourceDataBefore.sha256 -eq $sourceDataAfter.sha256 -and $sourceDataBefore.exists -eq $sourceDataAfter.exists)
    $report.source_data_preserved='passed'
    if($PeerExecutablePath){Add-SmokeCheck 'peer_source_bundle_data_is_unchanged' ((Get-SingleDataFingerprint (Join-Path $peerRoot 'AutumnOS_Data')).sha256 -eq $peerDataBefore.sha256)}
    $report.status='passed'
} catch {
    $report.failure_type=$_.Exception.GetType().FullName;$report.failure=$_.Exception.Message
    Write-Warning "Single-instance smoke failed: $($report.failure)"
    if($activeProduct -and -not $activeProduct.Process.HasExited -and -not $script:suspendedProcess){$null=Save-SmokeScreenshot $activeProduct '99-failure'}
} finally {
    if ('AutumnSingleInstanceSmoke.ForegroundProbe' -as [type]) {
        try { [AutumnSingleInstanceSmoke.ForegroundProbe]::Stop() } catch { $checks.Add([ordered]@{name='cleanup_owned_focus_probe';status='failed';detail=$_.Exception.Message});$report.status='failed' }
        if($script:focusProbeProduct){$script:focusProbeProduct.Process.Dispose();$script:focusProbeProduct=$null}
    }
    if($script:suspendedProcess -and -not $script:suspendedProcess.Process.HasExited){
        $resume=[AutumnSingleInstanceSmoke.NativeWindows]::NtResumeProcess($script:suspendedProcess.Process.Handle)
        $checks.Add([ordered]@{name='finally_resume_owned_primary';status=$(if($resume -eq 0){'passed'}else{'failed'});native_status=$resume})
        $script:suspendedProcess=$null
    }
    foreach($launch in $ownedLaunches){
        $process=$launch.Process
        try {
            if(-not $process.HasExited){
                if('AutumnSingleInstanceSmoke.NativeWindows' -as [type]){$process.Refresh();if($process.MainWindowHandle -ne [IntPtr]::Zero){$null=[AutumnSingleInstanceSmoke.NativeWindows]::SetWindowPos($process.MainWindowHandle,[IntPtr](-2),0,0,0,0,3)}}
                $null=$process.CloseMainWindow()
                if(-not $process.WaitForExit(3000)){
                    Assert-SingleFaultOwnership $launch
                    $process.Kill();$null=$process.WaitForExit(3000)
                    $checks.Add([ordered]@{name='cleanup_held_test_process';status='forced_close';process_id=$process.Id})
                    $report.status='failed'
                }
            }
            Complete-SingleOutput $launch
        } catch {$checks.Add([ordered]@{name='cleanup_held_test_process';status='failed';process_id=$process.Id;detail=$_.Exception.Message});$report.status='failed'}
        finally{$process.Dispose()}
    }
    if($script:initialPointerPosition -and ('AutumnSingleInstanceSmoke.NativeWindows' -as [type])){$null=[AutumnSingleInstanceSmoke.NativeWindows]::SetCursorPos($script:initialPointerPosition.X,$script:initialPointerPosition.Y)}
    foreach($directory in $copyDirectories){
        foreach($name in @('launcher-events.jsonl','runtime-events.jsonl')){
            $path=Join-Path $directory "AutumnOS_Data/Logs/$name"
            if(Test-Path -LiteralPath $path){Copy-Item -LiteralPath $path -Destination (Join-Path $ReportDirectory ((Split-Path $directory -Leaf)+'-'+$name))}
        }
    }
    $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o');$report.check_count=$checks.Count
    $report.passed_checks=@($checks|Where-Object status -eq 'passed').Count
    $report.screenshot_status=if(@($screenshots|Where-Object status -eq 'captured').Count){'captured_not_visually_reviewed'}else{'not_run'}
    $report | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "REPORT $reportPath"
}
if($report.status -ne 'passed'){throw "Single-instance smoke failed or was blocked. Evidence preserved at $reportPath"}
