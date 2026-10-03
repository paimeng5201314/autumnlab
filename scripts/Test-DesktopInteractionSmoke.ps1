[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [string]$ReportDirectory,
    [ValidateRange(5, 90)][int]$WindowTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/Smoke-NativeAppInput.ps1"
$smokeId = 'T02-interaction-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$scope = Join-Path $projectRoot "artifacts/smoke/$smokeId"
$stageDirectory = Join-Path $scope '中文 空格 目录'
if (-not $ReportDirectory) { $ReportDirectory = $scope }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
$reportPath = Join-Path $ReportDirectory 'windows-smoke.json'
if (Test-Path -LiteralPath $reportPath) { throw "A smoke report already exists; choose a new ReportDirectory to preserve evidence: $reportPath" }
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$ownedEntryProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$checks = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[object]]::new()
$inputActions = [Collections.Generic.List[object]]::new()
$script:initialPointerPosition = $null
$expectedScreenshots = @('01-settings-overview', '02-appearance', '03-diagnostics', '04-background-icons',
    '05-double-click-actions', '06-continued-application', '07-closed-desktop',
    '22-empty-switcher', '23-arranged-desktop', '08-hello', '09-brand', '10-initialization', '11-light-desktop', '12-dark-desktop',
    '13-long-press-menu', '14-right-click-menu', '15-application-info', '16-about',
    '17-internal-application', '18-permission-overlay', '19-saved-application',
    '20-maximized-application', '21-restarted-desktop')
$report = [ordered]@{
    schema_version = 1
    task_id = 'T02'
    checkpoint = 'tablet_settings_background_game_interactions'
    full_t01_acceptance = 'in_progress'
    full_t02_acceptance = 'not_run'
    smoke_id = $smokeId
    source_snapshot_id = 'not_recorded'
    build_id = 'local-untracked'
    status = 'failed'
    started_utc = [DateTimeOffset]::UtcNow.ToString('o')
    environment = [Environment]::OSVersion.VersionString
    isElevated = $null
    source_executable = $null
    tested_executable = $null
    source_executable_sha256 = $null
    working_directories = @()
    process_ids = @()
    bootstrap_process_ids = @()
    checks = $checks
    screenshots = $screenshots
    input_actions = $inputActions
    unchanged_state_sha256 = $null
    sentinel_sha256 = $null
    standard_user_window = 'not_run'
    first_run = 'not_run'
    restart_data_preservation = 'not_run'
    about_dialog = 'not_run'
    about_page = 'not_run'
    maximize_restore = 'not_run'
    screenshot_status = 'not_run'
    windows10_22h2 = 'not_run'
    physical_power_failure = 'not_run'
    installer = 'not_run'
    internal_application = 'not_run'
    desktop_checkpoint = 'not_run'
    appearance_persistence = 'not_run'
    long_press = 'not_run'
    right_click_menu = 'not_run'
    same_instance_resume = 'not_run'
    settings_navigation = 'not_run'
    background_single_click = 'not_run'
    background_double_click = 'not_run'
    background_markers = 'not_run'
    background_keyboard_actions = 'not_run'
    sample_app_id = $null
    sample_version = $null
    automation_input = 'UI Automation patterns; scoped real pointer single/double click and press/right-click via synthetic input; no physical device assertion'
    automation_window_condition = 'Only windows started by this test temporarily use HWND_TOPMOST to prevent unrelated windows intercepting real pointer input; restored before close.'
    automation_pointer_condition = 'Pointer remains inside the owned product between actions to avoid external hover activation; initial cursor position restored in final cleanup.'
    physical_keyboard_mouse = 'not_run'
    input_method_editor = 'not_run'
    physical_touch = 'not_run'
    network_sandbox = 'not_run'
    clean_machine = 'not_run'
    actual_browser_suspension = 'not_run'
    dpi_matrix = 'not_run'
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
    $foreground = [AutumnDesktopInteractionSmoke.NativeWindows]::GetForegroundWindow()
    [uint32]$foregroundProcess = 0
    $null = [AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowThreadProcessId($foreground, [ref]$foregroundProcess)
    return $foregroundProcess -eq $Product.Process.Id
}

function Focus-SmokeProduct($Product) {
    if($Product.Element.Current.ProcessId -ne $Product.Process.Id){throw 'Refusing foreground input outside the owned UIA process.'}
    $recovery=Ensure-AutumnOwnedWindowWorkArea $Product.Process $Product.Handle
    if($recovery){$inputActions.Add($recovery)}
    $null = [AutumnDesktopInteractionSmoke.NativeWindows]::SetWindowPos($Product.Handle, [IntPtr](-1), 0, 0, 0, 0, 3)
    $null = [AutumnDesktopInteractionSmoke.NativeWindows]::SetForegroundWindow($Product.Handle)
    if (-not (Test-SmokeForeground $Product)) {
        try { $Product.Element.SetFocus() } catch { Write-Verbose 'The window provider did not accept SetFocus; normal foreground requests will be tried.' }
    }
    if (-not (Test-SmokeForeground $Product)) {
        # Windows may reject programmatic activation while an unrelated application has focus.
        # An ordinary click on this test window's verified native caption requests activation.
        Start-Sleep -Milliseconds ([AutumnDesktopInteractionSmoke.NativeWindows]::GetDoubleClickTime() + 100)
        $rect = [AutumnDesktopInteractionSmoke.NativeWindows+RECT]::new()
        if (-not [AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowRect($Product.Handle, [ref]$rect)) { throw 'Cannot locate the owned window caption.' }
        $point = [AutumnDesktopInteractionSmoke.NativeWindows+POINT]::new()
        $point.X = $rect.Left + [int](($rect.Right - $rect.Left) / 2)
        $point.Y = $rect.Top + 16
        $packed = [IntPtr]([long](($point.Y -band 65535) * 65536) + ($point.X -band 65535))
        if ([AutumnDesktopInteractionSmoke.NativeWindows]::SendMessage($Product.Handle, 0x84, [IntPtr]::Zero, $packed).ToInt64() -ne 2) { throw 'Activation point is not the owned native caption.' }
        $original = [AutumnDesktopInteractionSmoke.NativeWindows+POINT]::new()
        $null = [AutumnDesktopInteractionSmoke.NativeWindows]::GetCursorPos([ref]$original)
        if (-not $script:initialPointerPosition) { $script:initialPointerPosition = $original }
        $pressed = $false
        try {
            if (-not [AutumnDesktopInteractionSmoke.NativeWindows]::SetCursorPos($point.X, $point.Y)) { throw 'Cannot position cursor over owned caption.' }
            $actual = [AutumnDesktopInteractionSmoke.NativeWindows+POINT]::new()
            if (-not [AutumnDesktopInteractionSmoke.NativeWindows]::GetCursorPos([ref]$actual) -or $actual.X -ne $point.X -or $actual.Y -ne $point.Y) { throw 'Caption activation cursor moved.' }
            [uint32]$owner = 0
            $hit = [AutumnDesktopInteractionSmoke.NativeWindows]::WindowFromPoint($actual)
            $null = [AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowThreadProcessId($hit, [ref]$owner)
            if ($owner -ne $Product.Process.Id) { throw 'Refusing caption activation outside the test process.' }
            $pressed = $true
            if([AutumnSmokeInput.Native]::SendCaptionClick() -ne 2){throw 'The owned caption click was not fully inserted.'}
            $pressed = $false
            Wait-AutumnOwnedPointerIdle $Product.Process $Product.Handle
            $after=[AutumnDesktopInteractionSmoke.NativeWindows+RECT]::new()
            if(-not [AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowRect($Product.Handle,[ref]$after)){throw 'Cannot inspect caption activation geometry.'}
            $inputActions.Add([ordered]@{ kind='activation_title_bar_click'; process_id=$Product.Process.Id; x=$actual.X; y=$actual.Y; hit_test='HTCAPTION'; target_window=$hit.ToInt64();
                injection='single_SendInput_down_up_batch';capture_and_move_size_released=$true;
                before=@($rect.Left,$rect.Top,$rect.Right,$rect.Bottom);after=@($after.Left,$after.Top,$after.Right,$after.Bottom) })
            if($rect.Left -ne $after.Left -or $rect.Top -ne $after.Top -or $rect.Right -ne $after.Right -or $rect.Bottom -ne $after.Bottom){
                throw 'The owned window moved during caption activation; refusing subsequent input.'
            }
        } finally {
            if ($pressed) { [AutumnDesktopInteractionSmoke.NativeWindows]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero) }
        }
    }
    $null = Wait-SmokeCondition {
        if (Test-SmokeForeground $Product) { return $true }
        $null = [AutumnDesktopInteractionSmoke.NativeWindows]::SetForegroundWindow($Product.Handle)
        return (Test-SmokeForeground $Product)
    } 'the owned product foreground' 5
    Wait-AutumnOwnedPointerIdle $Product.Process $Product.Handle
}

function Send-SmokeEscape($Product) {
    Wait-AutumnOwnedPointerIdle $Product.Process $Product.Handle
    if (-not (Test-SmokeForeground $Product)) { throw 'Refusing keyboard input outside the owned product process.' }
    [AutumnDesktopInteractionSmoke.NativeWindows]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
    [AutumnDesktopInteractionSmoke.NativeWindows]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
    $inputActions.Add([ordered]@{ kind='synthetic_escape'; process_id=$Product.Process.Id })
    Start-Sleep -Milliseconds 150
}

function Send-SmokeIconKey($Product, [string]$AutomationId, [ValidateSet('Enter', 'Space')][string]$Key) {
    Focus-SmokeProduct $Product
    $element = Wait-SmokeCondition { Find-VisibleSmokeElement $Product.Element $AutomationId } "keyboard target $AutomationId"
    $element.SetFocus()
    $null = Wait-SmokeCondition { $element.Current.HasKeyboardFocus } "actual keyboard focus on $AutomationId" 5
    Wait-AutumnOwnedPointerIdle $Product.Process $Product.Handle
    if (-not (Test-SmokeForeground $Product)) { throw 'Refusing keyboard input outside the owned product.' }
    [byte]$keyCode = if ($Key -eq 'Enter') { 0x0D } else { 0x20 }
    try {
        [AutumnDesktopInteractionSmoke.NativeWindows]::keybd_event($keyCode, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 40
    } finally { [AutumnDesktopInteractionSmoke.NativeWindows]::keybd_event($keyCode, 0, 2, [UIntPtr]::Zero) }
    $inputActions.Add([ordered]@{ kind='synthetic_key'; automation_id=$AutomationId; key=$Key; process_id=$Product.Process.Id })
}

function Assert-SmokeCaptureUnoccluded($Product, [int]$Left, [int]$Top, [int]$Width, [int]$Height) {
    Assert-AutumnVisibleCapture $Product.Process $Product.Handle $Left $Top $Width $Height
    if (-not (Test-SmokeForeground $Product)) { throw 'Refusing screen capture after the product lost foreground.' }
    $window = [AutumnDesktopInteractionSmoke.NativeWindows]::GetTopWindow([IntPtr]::Zero)
    for ($count = 0; $window -ne [IntPtr]::Zero -and $count -lt 500; $count++) {
        if ($window -eq $Product.Handle) { return }
        if ([AutumnDesktopInteractionSmoke.NativeWindows]::IsWindowVisible($window)) {
            [uint32]$windowProcess = 0
            $null = [AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowThreadProcessId($window, [ref]$windowProcess)
            [int]$cloaked = 0
            $null = [AutumnDesktopInteractionSmoke.NativeWindows]::DwmGetWindowAttribute($window, 14, [ref]$cloaked, 4)
            if ($windowProcess -ne $Product.Process.Id -and $cloaked -eq 0) {
                $other = [AutumnDesktopInteractionSmoke.NativeWindows+RECT]::new()
                if ([AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowRect($window, [ref]$other) -and
                    $other.Left -lt ($Left + $Width) -and $other.Right -gt $Left -and
                    $other.Top -lt ($Top + $Height) -and $other.Bottom -gt $Top) {
                    throw 'Refusing screen capture because a foreign window occludes the product client area.'
                }
            }
        }
        $window = [AutumnDesktopInteractionSmoke.NativeWindows]::GetWindow($window, 2)
    }
    throw 'Cannot establish an unobscured product window in the desktop stacking order.'
}

function Invoke-SmokePointer($Product, [string]$AutomationId, [ValidateSet('single_click','double_click','long_press','right_click')][string]$Kind, [switch]$GapAfterIcon) {
    if ($GapAfterIcon -and $Kind -notin @('single_click', 'double_click')) { throw 'Icon-gap input supports ordinary clicks only.' }
    $doubleClickMs = [AutumnDesktopInteractionSmoke.NativeWindows]::GetDoubleClickTime()
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
    $gapScrollElement = $null
    $gapHitEvidence = [Collections.Generic.List[object]]::new()
    if ($GapAfterIcon) {
        # The small gap is outside the icon's actual Button, still inside its ScrollViewer.
        $x = [int][Math]::Ceiling($bounds.Right + 3)
        $gapAncestor = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($element)
        for ($gapDepth = 0; $gapAncestor -and $gapDepth -lt 40; $gapDepth++) {
            if ($gapAncestor.Current.ProcessId -ne $Product.Process.Id) { break }
            if ($gapAncestor.Current.ClassName -eq 'ScrollViewer' -or $gapAncestor.Current.AutomationId -eq 'DesktopAppScroll') {
                $gapScrollElement = $gapAncestor; break
            }
            $gapAncestor = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($gapAncestor)
        }
        if (-not $gapScrollElement) { throw 'Cannot identify the icon ScrollViewer for a guarded gap click.' }
    }
    $assertPointerGap = {
        param($GapIconBounds, [int]$GapX, [int]$GapY)
        $gapCenterY = ($GapIconBounds.Top + $GapIconBounds.Bottom) / 2
        $gapViewportBounds = $gapScrollElement.Current.BoundingRectangle
        $gapWindowBounds = $Product.Element.Current.BoundingRectangle
        if ($GapX -lt ($GapIconBounds.Right + 2) -or $GapX -gt ($GapIconBounds.Right + 6) -or [Math]::Abs($GapY - $gapCenterY) -gt 2 -or
            $gapViewportBounds.IsEmpty -or $GapX -lt $gapViewportBounds.Left -or $GapX -ge $gapViewportBounds.Right -or
            $GapY -lt $gapViewportBounds.Top -or $GapY -ge $gapViewportBounds.Bottom -or
            $GapX -lt $gapWindowBounds.Left -or $GapX -ge $gapWindowBounds.Right -or $GapY -lt $gapWindowBounds.Top -or $GapY -ge $gapWindowBounds.Bottom) {
            throw 'Gap input is not in the narrow guarded rectangle inside the icon ScrollViewer.'
        }
        $gapActualHit = [System.Windows.Automation.AutomationElement]::FromPoint([System.Windows.Point]::new($GapX, $GapY))
        if (-not $gapActualHit -or $gapActualHit.Current.ProcessId -ne $Product.Process.Id) { throw 'Gap UIA hit-test is outside the owned process.' }
        $gapNode = $gapActualHit
        $gapReachedOwnedRoot = $false
        for ($gapDepth = 0; $gapNode -and $gapDepth -lt 40; $gapDepth++) {
            if ($gapNode.Current.ProcessId -ne $Product.Process.Id) { throw 'Gap ancestry left the owned product.' }
            if ($gapNode.Current.ControlType -eq [System.Windows.Automation.ControlType]::Button -or
                $gapNode.Current.ControlType -eq [System.Windows.Automation.ControlType]::MenuItem -or
                $gapNode.Current.ControlType -eq [System.Windows.Automation.ControlType]::ScrollBar) { throw 'Gap point hit an actionable control instead of blank space.' }
            if ([System.Windows.Automation.Automation]::Compare($gapNode, $Product.Element)) { $gapReachedOwnedRoot = $true; break }
            $gapNode = [System.Windows.Automation.TreeWalker]::RawViewWalker.GetParent($gapNode)
        }
        if (-not $gapReachedOwnedRoot) { throw 'Gap hit-test did not belong to the recorded product window.' }
        $gapHitEvidence.Add([ordered]@{ x=$GapX; y=$GapY; hit_class=$gapActualHit.Current.ClassName;
            hit_control_type=$gapActualHit.Current.ControlType.ProgrammaticName; hit_automation_id=$gapActualHit.Current.AutomationId;
            process_id=$gapActualHit.Current.ProcessId; no_button_ancestor=$true;
            scroll_left=$gapViewportBounds.Left; scroll_top=$gapViewportBounds.Top; scroll_right=$gapViewportBounds.Right; scroll_bottom=$gapViewportBounds.Bottom })
    }
    if ($GapAfterIcon) { & $assertPointerGap $bounds $x $y }
    $original = [AutumnDesktopInteractionSmoke.NativeWindows+POINT]::new()
    if (-not [AutumnDesktopInteractionSmoke.NativeWindows]::GetCursorPos([ref]$original)) { throw 'Cannot record cursor position.' }
    if (-not $script:initialPointerPosition) { $script:initialPointerPosition = $original }
    $downFlag = if ($Kind -eq 'right_click') { 0x0008 } else { 0x0002 }
    $upFlag = if ($Kind -eq 'right_click') { 0x0010 } else { 0x0004 }
    $pressed = $false
    try {
        if (-not (Test-SmokeForeground $Product)) { throw 'Product lost foreground before pointer input.' }
        if (-not [AutumnDesktopInteractionSmoke.NativeWindows]::SetCursorPos($x, $y)) { throw 'Cannot position pointer inside the owned window.' }
        $point = [AutumnDesktopInteractionSmoke.NativeWindows+POINT]::new(); $point.X = $x; $point.Y = $y
        [uint32]$targetProcess = 0
        $targetWindow = [AutumnDesktopInteractionSmoke.NativeWindows]::WindowFromPoint($point)
        $null = [AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowThreadProcessId($targetWindow, [ref]$targetProcess)
        if ($targetProcess -ne $Product.Process.Id -or -not (Test-SmokeForeground $Product)) { throw 'Pointer hit-test is not owned by the product.' }
        [uint32]$currentTargetProcess = 0
        $currentTarget = [AutumnDesktopInteractionSmoke.NativeWindows]::WindowFromPoint($point)
        $null = [AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowThreadProcessId($currentTarget, [ref]$currentTargetProcess)
        if ($currentTargetProcess -ne $Product.Process.Id) { throw 'Pointer target became occluded before input.' }
        $heldMs = if ($Kind -eq 'long_press') { 750 } else { [Math]::Max(1, [Math]::Min(40, [Math]::Floor($doubleClickMs / 5))) }
        $clickCount = if ($Kind -eq 'double_click') { 2 } else { 1 }
        $clickTimer = [Diagnostics.Stopwatch]::new()
        $secondDownMs = $null
        $actualClicks = [Collections.Generic.List[object]]::new()
        $observeHeldPointer={
            param([string]$Phase,[Diagnostics.Stopwatch]$Timer)
            $observed=[AutumnDesktopInteractionSmoke.NativeWindows+POINT]::new()
            $positionValid=[AutumnDesktopInteractionSmoke.NativeWindows]::GetCursorPos([ref]$observed)
            $observedHit=[AutumnDesktopInteractionSmoke.NativeWindows]::WindowFromPoint($observed);[uint32]$observedOwner=0
            $null=[AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowThreadProcessId($observedHit,[ref]$observedOwner)
            $observedForeground=[AutumnDesktopInteractionSmoke.NativeWindows]::GetForegroundWindow();[uint32]$observedForegroundOwner=0
            $null=[AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowThreadProcessId($observedForeground,[ref]$observedForegroundOwner)
            return [ordered]@{phase=$Phase;elapsed_ms=$Timer.Elapsed.TotalMilliseconds;position_valid=$positionValid;x=$observed.X;y=$observed.Y;
                hit_hwnd=$observedHit.ToInt64();hit_pid=$observedOwner;foreground_hwnd=$observedForeground.ToInt64();foreground_pid=$observedForegroundOwner;
                hit_owned=($observedOwner -eq $Product.Process.Id);foreground_owned=($observedForegroundOwner -eq $Product.Process.Id)}
        }
        for ($click = 0; $click -lt $clickCount; $click++) {
            $actualPoint = [AutumnDesktopInteractionSmoke.NativeWindows+POINT]::new()
            if (-not [AutumnDesktopInteractionSmoke.NativeWindows]::GetCursorPos([ref]$actualPoint)) { throw 'Cannot verify the actual cursor before button down.' }
            $liveTarget = Find-VisibleSmokeElement $Product.Element $AutomationId
            if (-not $liveTarget) { throw 'Pointer target disappeared before button down.' }
            $liveBounds = $liveTarget.Current.BoundingRectangle
            if ($GapAfterIcon) { & $assertPointerGap $liveBounds $actualPoint.X $actualPoint.Y }
            elseif ($actualPoint.X -lt $liveBounds.Left -or $actualPoint.X -ge $liveBounds.Right -or
                $actualPoint.Y -lt $liveBounds.Top -or $actualPoint.Y -ge $liveBounds.Bottom) { throw 'Actual cursor moved outside the current target before button down.' }
            [uint32]$actualProcess = 0
            $actualWindow = [AutumnDesktopInteractionSmoke.NativeWindows]::WindowFromPoint($actualPoint)
            $null = [AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowThreadProcessId($actualWindow, [ref]$actualProcess)
            if ($actualProcess -ne $Product.Process.Id -or -not (Test-SmokeForeground $Product)) { throw 'Actual cursor or foreground is not owned by the product before button down.' }
            $heldObservations=[Collections.Generic.List[object]]::new()
            $clickEvidence=[ordered]@{ click_index=$click; actual_x=$actualPoint.X; actual_y=$actualPoint.Y;
                owner_process_id=$actualProcess; target_window=$actualWindow.ToInt64(); target_left=$liveBounds.Left;
                target_top=$liveBounds.Top; target_right=$liveBounds.Right; target_bottom=$liveBounds.Bottom;held_observations=$heldObservations;observed_held_ms=$null }
            $actualClicks.Add($clickEvidence)
            if ($click -eq 0) { $clickTimer.Start() } else {
                $secondDownMs = $clickTimer.Elapsed.TotalMilliseconds
                if ($secondDownMs -ge $doubleClickMs) { throw 'Synthetic clicks missed the system double-click interval; no double-click assertion is valid.' }
            }
            [AutumnDesktopInteractionSmoke.NativeWindows]::mouse_event($downFlag, 0, 0, 0, [UIntPtr]::Zero)
            $pressed = $true
            # Observe interference without changing the requested hold duration or moving the pointer.
            # These are sampled OS coordinates around injected input, not a claim of delivered WM events.
            $holdTimer=[Diagnostics.Stopwatch]::StartNew()
            while($holdTimer.Elapsed.TotalMilliseconds -lt $heldMs){
                $heldObservations.Add((& $observeHeldPointer 'held' $holdTimer))
                $remainingHold=$heldMs-$holdTimer.Elapsed.TotalMilliseconds
                if($remainingHold -gt 0){Start-Sleep -Milliseconds ([Math]::Min(10,[Math]::Max(1,[int][Math]::Ceiling($remainingHold))))}
            }
            $heldObservations.Add((& $observeHeldPointer 'before_injected_up' $holdTimer))
            [AutumnDesktopInteractionSmoke.NativeWindows]::mouse_event($upFlag, 0, 0, 0, [UIntPtr]::Zero)
            $pressed = $false
            $clickEvidence.observed_held_ms=$holdTimer.Elapsed.TotalMilliseconds
            $heldObservations.Add((& $observeHeldPointer 'after_injected_up' $holdTimer))
            if ($clickCount -eq 2 -and $click -eq 0) { Start-Sleep -Milliseconds ([Math]::Max(1, [Math]::Min(80, [Math]::Floor($doubleClickMs / 4)))) }
        }
        # Do not move the cursor for the next action while the owned GUI still holds capture.
        Wait-AutumnOwnedPointerIdle $Product.Process $Product.Handle
        $inputActions.Add([ordered]@{ kind=$Kind; automation_id=$AutomationId; held_ms=$heldMs; click_count=$clickCount;
            system_double_click_ms=$doubleClickMs; observed_down_interval_ms=$secondDownMs; x=$x; y=$y;
            process_id=$Product.Process.Id; target_window=$targetWindow.ToInt64(); actual_clicks=$actualClicks;
            gap_after_icon=[bool]$GapAfterIcon; gap_hit_tests=$gapHitEvidence })
    } finally {
        if ($pressed) { [AutumnDesktopInteractionSmoke.NativeWindows]::mouse_event($upFlag, 0, 0, 0, [UIntPtr]::Zero) }
    }
}

function Invoke-SmokeDesktopDrag {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]$Product,
        [Parameter(Mandatory)][string]$FromId,
        [Parameter(Mandatory)][string]$ToId,
        [switch]$CancelWithEscape,
        [switch]$DropOutsideGrid
    )

    if ($CancelWithEscape -and $DropOutsideGrid) { throw 'Select one cancellation case per drag.' }
    if (-not $ownedProcesses -or -not @($ownedProcesses | Where-Object { [object]::ReferenceEquals($_, $Product.Process) }).Count) {
        throw 'Refusing drag for a process not started and recorded by this smoke test.'
    }
    $Product.Process.Refresh()
    [uint32]$dragWindowOwner = 0
    $null = [AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowThreadProcessId($Product.Handle, [ref]$dragWindowOwner)
    if ($Product.Process.HasExited -or $dragWindowOwner -ne $Product.Process.Id -or $Product.Element.Current.ProcessId -ne $Product.Process.Id) {
        throw 'The recorded drag process/window identity is no longer valid.'
    }
    $dragDoubleClickMs = [AutumnDesktopInteractionSmoke.NativeWindows]::GetDoubleClickTime()
    if ($dragDoubleClickMs -lt 1 -or $dragDoubleClickMs -gt 6000) { throw 'Unbounded system double-click interval.' }
    Start-Sleep -Milliseconds ($dragDoubleClickMs + 100)
    Focus-SmokeProduct $Product
    $dragSourceElement = Wait-SmokeCondition { Find-VisibleSmokeElement $Product.Element $FromId } "drag source $FromId" 6
    $dragDestinationId = if ($DropOutsideGrid) { 'DesktopGestureHint' } else { $ToId }
    $dragDestinationElement = Wait-SmokeCondition { Find-VisibleSmokeElement $Product.Element $dragDestinationId } "drag target $dragDestinationId" 6
    $dragSourceBounds = $dragSourceElement.Current.BoundingRectangle
    $dragTargetBounds = $dragDestinationElement.Current.BoundingRectangle
    $dragProductBounds = $Product.Element.Current.BoundingRectangle
    foreach ($dragBounds in @($dragSourceBounds, $dragTargetBounds)) {
        if ($dragBounds.IsEmpty -or $dragBounds.Left -lt $dragProductBounds.Left -or $dragBounds.Top -lt $dragProductBounds.Top -or
            $dragBounds.Right -gt $dragProductBounds.Right -or $dragBounds.Bottom -gt $dragProductBounds.Bottom) {
            throw 'A drag endpoint is outside the recorded product window.'
        }
    }
    $dragStartX = [int][Math]::Round(($dragSourceBounds.Left + $dragSourceBounds.Right) / 2)
    $dragStartY = [int][Math]::Round(($dragSourceBounds.Top + $dragSourceBounds.Bottom) / 2)
    $dragEndX = [int][Math]::Round(($dragTargetBounds.Left + $dragTargetBounds.Right) / 2)
    $dragEndY = [int][Math]::Round(($dragTargetBounds.Top + $dragTargetBounds.Bottom) / 2)
    $dragDistance = [Math]::Sqrt([Math]::Pow($dragEndX - $dragStartX, 2) + [Math]::Pow($dragEndY - $dragStartY, 2))
    if ($dragDistance -le 24) { throw 'Drag endpoints are too close to exercise the movement threshold.' }

    $dragOriginalCursor = [AutumnDesktopInteractionSmoke.NativeWindows+POINT]::new()
    if (-not [AutumnDesktopInteractionSmoke.NativeWindows]::GetCursorPos([ref]$dragOriginalCursor)) { throw 'Cannot record original pointer.' }
    if (-not $script:initialPointerPosition) { $script:initialPointerPosition = $dragOriginalCursor }
    $dragTrace = [Collections.Generic.List[object]]::new()
    $dragStopwatch = [Diagnostics.Stopwatch]::StartNew()
    $dragIsPressed = $false
    $dragCompleted = $false
    $dragCleanup = 'not_needed'
    $dragLastX = $dragStartX
    $dragLastY = $dragStartY

    # Invoked before EVERY cursor move and every down/up/key injection. No input is sent
    # into a foreign window. A captured pointer does not waive physical ownership checks.
    $assertDragPoint = {
        param([int]$PointX, [int]$PointY, [string]$Phase)
        $Product.Process.Refresh()
        if ($Product.Process.HasExited -or -not (Test-SmokeForeground $Product)) { throw "Lost owned foreground during drag: $Phase" }
        [uint32]$currentDragOwner = 0
        $null = [AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowThreadProcessId($Product.Handle, [ref]$currentDragOwner)
        if ($currentDragOwner -ne $Product.Process.Id) { throw 'Drag window identity changed.' }
        $currentDragBounds = $Product.Element.Current.BoundingRectangle
        if ($PointX -lt $currentDragBounds.Left -or $PointX -ge $currentDragBounds.Right -or
            $PointY -lt $currentDragBounds.Top -or $PointY -ge $currentDragBounds.Bottom) { throw 'Refusing drag point outside owned bounds.' }
        $dragProbe = [AutumnDesktopInteractionSmoke.NativeWindows+POINT]::new()
        $dragProbe.X = $PointX; $dragProbe.Y = $PointY
        $dragHit = [AutumnDesktopInteractionSmoke.NativeWindows]::WindowFromPoint($dragProbe)
        [uint32]$dragHitOwner = 0
        $null = [AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowThreadProcessId($dragHit, [ref]$dragHitOwner)
        if ($dragHitOwner -ne $Product.Process.Id) { throw "Refusing drag input over another process: $Phase" }
        $dragTrace.Add([ordered]@{ phase=$Phase; elapsed_ms=[Math]::Round($dragStopwatch.Elapsed.TotalMilliseconds, 1);
            x=$PointX; y=$PointY; owner_process_id=$dragHitOwner; target_window=$dragHit.ToInt64() })
    }
    $moveDragPointer = {
        param([int]$PointX, [int]$PointY, [string]$Phase)
        & $assertDragPoint $PointX $PointY ($Phase + '_before_move')
        # Use actual non-coalesced mouse input, not a cursor-position update. Normalize
        # against the whole virtual desktop, including monitors at negative coordinates.
        $dragVirtualLeft = [AutumnDesktopInteractionSmoke.NativeWindows]::GetSystemMetrics(76)
        $dragVirtualTop = [AutumnDesktopInteractionSmoke.NativeWindows]::GetSystemMetrics(77)
        $dragVirtualWidth = [AutumnDesktopInteractionSmoke.NativeWindows]::GetSystemMetrics(78)
        $dragVirtualHeight = [AutumnDesktopInteractionSmoke.NativeWindows]::GetSystemMetrics(79)
        if ($dragVirtualWidth -lt 1 -or $dragVirtualWidth -gt 65536 -or $dragVirtualHeight -lt 1 -or $dragVirtualHeight -gt 65536 -or
            $PointX -lt $dragVirtualLeft -or $PointX -ge ([long]$dragVirtualLeft + $dragVirtualWidth) -or
            $PointY -lt $dragVirtualTop -or $PointY -ge ([long]$dragVirtualTop + $dragVirtualHeight)) {
            throw 'The requested drag point cannot be represented safely on the current virtual desktop.'
        }
        # Target the middle of the requested physical pixel's normalized interval.
        [uint32]$dragAbsoluteX = [Math]::Floor((([long]$PointX - $dragVirtualLeft + 0.5) * 65536.0) / $dragVirtualWidth)
        [uint32]$dragAbsoluteY = [Math]::Floor((([long]$PointY - $dragVirtualTop + 0.5) * 65536.0) / $dragVirtualHeight)
        if ($dragAbsoluteX -gt 65535 -or $dragAbsoluteY -gt 65535) { throw 'Invalid normalized drag coordinates.' }
        & $assertDragPoint $PointX $PointY ($Phase + '_before_injection')
        # MOVE | ABSOLUTE | VIRTUALDESK | MOVE_NOCOALESCE. Button state is unchanged.
        [AutumnDesktopInteractionSmoke.NativeWindows]::mouse_event(0xE001, $dragAbsoluteX, $dragAbsoluteY, 0, [UIntPtr]::Zero)
        $dragTrace.Add([ordered]@{ phase=$Phase + '_input'; flags='MOVE|ABSOLUTE|VIRTUALDESK|MOVE_NOCOALESCE';
            absolute_x=$dragAbsoluteX; absolute_y=$dragAbsoluteY; virtual_left=$dragVirtualLeft; virtual_top=$dragVirtualTop;
            virtual_width=$dragVirtualWidth; virtual_height=$dragVirtualHeight })
        $observedDragCursor = [AutumnDesktopInteractionSmoke.NativeWindows+POINT]::new()
        $dragMoveObserved = $false
        $dragMoveWait = [Diagnostics.Stopwatch]::StartNew()
        do {
            if (-not [AutumnDesktopInteractionSmoke.NativeWindows]::GetCursorPos([ref]$observedDragCursor)) { throw 'Cannot inspect the injected drag pointer.' }
            & $assertDragPoint $observedDragCursor.X $observedDragCursor.Y ($Phase + '_observed')
            if ($observedDragCursor.X -eq $PointX -and $observedDragCursor.Y -eq $PointY) { $dragMoveObserved = $true; break }
            Start-Sleep -Milliseconds 5
        } while ($dragMoveWait.ElapsedMilliseconds -lt 100)
        if (-not $dragMoveObserved) { throw 'Injected mouse motion did not reach the exact guarded drag point.' }
        & $assertDragPoint $observedDragCursor.X $observedDragCursor.Y ($Phase + '_actual')
    }
    try {
        & $moveDragPointer $dragStartX $dragStartY 'down'
        [AutumnDesktopInteractionSmoke.NativeWindows]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
        $dragIsPressed = $true
        Start-Sleep -Milliseconds 45
        # Cross >8px promptly (before the 600ms menu timer), then use several actual moves.
        $dragSteps = 12
        for ($dragStep = 1; $dragStep -le $dragSteps; $dragStep++) {
            $dragFraction = [Math]::Max(0.12, $dragStep / [double]$dragSteps)
            $dragLastX = [int][Math]::Round($dragStartX + ($dragEndX - $dragStartX) * $dragFraction)
            $dragLastY = [int][Math]::Round($dragStartY + ($dragEndY - $dragStartY) * $dragFraction)
            & $moveDragPointer $dragLastX $dragLastY ("move_$dragStep")
            Start-Sleep -Milliseconds 28
        }
        if ($CancelWithEscape) {
            & $assertDragPoint $dragLastX $dragLastY 'escape_down'
            [AutumnDesktopInteractionSmoke.NativeWindows]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
            & $assertDragPoint $dragLastX $dragLastY 'escape_up'
            [AutumnDesktopInteractionSmoke.NativeWindows]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 80
        }
        & $moveDragPointer $dragLastX $dragLastY 'release'
        [AutumnDesktopInteractionSmoke.NativeWindows]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
        $dragIsPressed = $false
        $dragCompleted = $true
        Start-Sleep -Milliseconds 250
    } finally {
        if ($dragIsPressed) {
            try {
                & $moveDragPointer $dragLastX $dragLastY 'cleanup_release'
                [AutumnDesktopInteractionSmoke.NativeWindows]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
                $dragIsPressed = $false; $dragCleanup = 'released_in_owned_window'
            } catch {
                # Do not inject an unconditional global mouse-up after the user changed windows.
                $dragCleanup = 'blocked_foreground_or_hit_ownership_changed'
                Write-Warning 'Drag cleanup was blocked by changed window ownership; release the mouse manually before resuming tests.'
            }
        }
        $inputActions.Add([ordered]@{ kind='desktop_icon_drag'; from_id=$FromId; requested_to_id=$ToId;
            actual_to_id=$dragDestinationId; cancel_with_escape=[bool]$CancelWithEscape; drop_outside_grid=[bool]$DropOutsideGrid;
            process_id=$Product.Process.Id; source_window=$Product.Handle.ToInt64(); completed=$dragCompleted;
            cleanup=$dragCleanup; synthetic_input=$true; physical_mouse='not_run'; trace=$dragTrace })
    }
}

function Test-SmokeDesktopArrange {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Product, [Parameter(Mandatory)][string]$DataDirectory)

    $arrangeLayoutPath = Join-Path $DataDirectory 'Config/desktop-layout.json'
    $arrangeRuntimePath = Join-Path $DataDirectory 'Logs/runtime-events.jsonl'
    $arrangeOriginalRuntimeHash = if (Test-Path -LiteralPath $arrangeRuntimePath) { (Get-FileHash -LiteralPath $arrangeRuntimePath -Algorithm SHA256).Hash } else { $null }
    Wait-SmokeDesktop $Product
    Add-SmokeCheck 'arrange_checkpoint_starts_without_running_game' (-not (Find-VisibleSmokeElement $Product.Element 'SampleRunningBadge'))
    $null = Wait-SmokeCondition { Find-VisibleSmokeElement $Product.Element 'DesktopGestureHint' } 'desktop blank-area gesture target' 6

    Invoke-SmokePointer $Product 'DesktopGestureHint' 'single_click'
    $arrangeObservation = [Diagnostics.Stopwatch]::StartNew()
    do {
        Add-SmokeCheck ('blank_single_click_no_switcher_' + [int]$arrangeObservation.ElapsedMilliseconds) (-not (Find-VisibleSmokeElement $Product.Element 'RunningSwitcherTitle'))
        Start-Sleep -Milliseconds 150
    } while ($arrangeObservation.ElapsedMilliseconds -lt 650)
    Invoke-SmokePointer $Product 'DesktopGestureHint' 'double_click'
    $null = Wait-SmokeCondition { Find-VisibleSmokeElement $Product.Element 'RunningSwitcherEmpty' } 'the real empty app switcher' 6
    Add-SmokeCheck 'blank_double_click_opens_empty_switcher' ($null -ne (Find-VisibleSmokeElement $Product.Element 'RunningSwitcherTitle'))
    $arrangeEmptyScreenshot = Save-SmokeScreenshot $Product '22-empty-switcher' -IncludeOverlays
    Add-SmokeCheck 'empty_switcher_real_screenshot' (-not [string]::IsNullOrWhiteSpace($arrangeEmptyScreenshot))
    Invoke-SmokeButton $Product.Element 'RunningSwitcherClose'
    $null = Wait-SmokeCondition { -not (Find-VisibleSmokeElement $Product.Element 'RunningSwitcherTitle') } 'empty switcher closed' 6
    Wait-SmokeDesktop $Product

    Invoke-SmokePointer $Product 'SampleButton' 'double_click' -GapAfterIcon
    $null = Wait-SmokeCondition { Find-VisibleSmokeElement $Product.Element 'RunningSwitcherEmpty' } 'empty switcher opened from a real ScrollViewer icon gap' 6
    Add-SmokeCheck 'icon_scrollviewer_gap_double_click_opens_empty_switcher' ($null -ne (Find-VisibleSmokeElement $Product.Element 'RunningSwitcherTitle'))
    Invoke-SmokeButton $Product.Element 'RunningSwitcherClose'
    $null = Wait-SmokeCondition { -not (Find-VisibleSmokeElement $Product.Element 'RunningSwitcherTitle') } 'gap-opened switcher closed' 6
    Wait-SmokeDesktop $Product

    $arrangeSampleBefore = (Find-VisibleSmokeElement $Product.Element 'SampleButton').Current.BoundingRectangle
    Invoke-SmokeDesktopDrag -Product $Product -FromId 'SampleButton' -ToId 'StoreButton'
    $null = Wait-SmokeCondition {
        $arrangeCandidate = Find-VisibleSmokeElement $Product.Element 'SampleButton'
        if (-not $arrangeCandidate -or -not (Test-Path -LiteralPath $arrangeLayoutPath)) { return $false }
        $arrangeCandidateBounds = $arrangeCandidate.Current.BoundingRectangle
        return [Math]::Abs($arrangeCandidateBounds.Left - $arrangeSampleBefore.Left) -gt 8 -or
            [Math]::Abs($arrangeCandidateBounds.Top - $arrangeSampleBefore.Top) -gt 8
    } 'a real reordered icon and durable desktop layout' 6
    $arrangeSaved = Get-Content -LiteralPath $arrangeLayoutPath -Raw | ConvertFrom-Json
    Add-SmokeCheck 'desktop_layout_contains_real_sample_id' (@($arrangeSaved.orderedIds) -contains 'app.cn.labchronicles.elementpairs')
    Add-SmokeCheck 'desktop_layout_is_versioned_unique_order' ($arrangeSaved.schemaVersion -eq 1 -and $arrangeSaved.revision -ge 1 -and
        @($arrangeSaved.orderedIds).Count -eq @($arrangeSaved.orderedIds | Select-Object -Unique).Count)
    $arrangeSavedHash = (Get-FileHash -LiteralPath $arrangeLayoutPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $arrangeSampleAfter = (Find-VisibleSmokeElement $Product.Element 'SampleButton').Current.BoundingRectangle

    $assertArrangeDidNotLaunch = {
        param([string]$ArrangeCheckPrefix)
        Wait-SmokeDesktop $Product
        Add-SmokeCheck ($ArrangeCheckPrefix + '_no_game_no_switcher') (-not (Find-VisibleSmokeElement $Product.Element 'RuntimeStatus') -and
            -not (Find-VisibleSmokeElement $Product.Element 'SampleRunningBadge') -and -not (Find-VisibleSmokeElement $Product.Element 'RunningSwitcherTitle'))
        Add-SmokeCheck ($ArrangeCheckPrefix + '_no_context_menu') (-not (Find-SmokeMenuItem $Product 'SampleMenuOpen' @()) -and
            -not (Find-SmokeMenuItem $Product 'SampleMenuInfo' @()))
        $arrangeCurrentRuntimeHash = if (Test-Path -LiteralPath $arrangeRuntimePath) { (Get-FileHash -LiteralPath $arrangeRuntimePath -Algorithm SHA256).Hash } else { $null }
        Add-SmokeCheck ($ArrangeCheckPrefix + '_runtime_log_unchanged') ($arrangeCurrentRuntimeHash -eq $arrangeOriginalRuntimeHash)
    }
    & $assertArrangeDidNotLaunch 'completed_drag'
    $arrangeDesktopScreenshot = Save-SmokeScreenshot $Product '23-arranged-desktop' -IncludeOverlays
    Add-SmokeCheck 'arranged_desktop_real_screenshot' (-not [string]::IsNullOrWhiteSpace($arrangeDesktopScreenshot))

    foreach ($arrangeCancelMode in @('escape', 'outside')) {
        if ($arrangeCancelMode -eq 'escape') { Invoke-SmokeDesktopDrag -Product $Product -FromId 'SampleButton' -ToId 'StoreButton' -CancelWithEscape }
        else { Invoke-SmokeDesktopDrag -Product $Product -FromId 'SampleButton' -ToId 'StoreButton' -DropOutsideGrid }
        Start-Sleep -Milliseconds 400
        Add-SmokeCheck ("cancel_${arrangeCancelMode}_does_not_write_layout") ((Get-FileHash -LiteralPath $arrangeLayoutPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $arrangeSavedHash)
        $arrangeRestoredBounds = (Find-VisibleSmokeElement $Product.Element 'SampleButton').Current.BoundingRectangle
        Add-SmokeCheck ("cancel_${arrangeCancelMode}_restores_position") ([Math]::Abs($arrangeRestoredBounds.Left - $arrangeSampleAfter.Left) -lt 2 -and
            [Math]::Abs($arrangeRestoredBounds.Top - $arrangeSampleAfter.Top) -lt 2)
        & $assertArrangeDidNotLaunch ("cancel_$arrangeCancelMode")
    }
    # Exercise the actual keyboard activation path after cancellation without starting
    # a game early: subsequent smoke cases still require an unstarted sample instance.
    Invoke-SmokeDesktopDrag -Product $Product -FromId 'SettingsButton' -ToId 'StoreButton' -CancelWithEscape
    Send-SmokeIconKey $Product 'SettingsButton' 'Enter'
    $null = Wait-SmokeCondition {
        (Find-VisibleSmokeElement $Product.Element 'SettingsDetailTitle') -and (Find-VisibleSmokeElement $Product.Element 'SettingsHomeButton')
    } 'settings opened by a real Enter key after drag cancellation' 6
    Add-SmokeCheck 'cancelled_drag_allows_next_keyboard_activation' $true
    Invoke-SmokeButton $Product.Element 'SettingsHomeButton'
    Wait-SmokeDesktop $Product
    Add-SmokeCheck 'cancelled_settings_drag_and_keyboard_leave_layout_unchanged' ((Get-FileHash -LiteralPath $arrangeLayoutPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $arrangeSavedHash)
    & $assertArrangeDidNotLaunch 'cancelled_settings_drag_keyboard'
    return [pscustomobject]@{
        layoutHash=$arrangeSavedHash; layoutPath=$arrangeLayoutPath; orderedIds=@($arrangeSaved.orderedIds);
        sampleBounds=[ordered]@{ left=$arrangeSampleAfter.Left; top=$arrangeSampleAfter.Top; width=$arrangeSampleAfter.Width; height=$arrangeSampleAfter.Height };
        sampleRelativeBounds=[ordered]@{ left=$arrangeSampleAfter.Left - $Product.Element.Current.BoundingRectangle.Left;
            top=$arrangeSampleAfter.Top - $Product.Element.Current.BoundingRectangle.Top; width=$arrangeSampleAfter.Width; height=$arrangeSampleAfter.Height };
        processId=$Product.Process.Id; runtimeLogHash=$arrangeOriginalRuntimeHash;
        status='passed'; restart_restore='not_run'; physical_touch='not_run'
    }
}


function Wait-SmokeDesktop($Product) {
    $null = Wait-SmokeCondition {
        (Find-VisibleSmokeElement $Product.Element 'SampleButton') -and
        (Find-VisibleSmokeElement $Product.Element 'SettingsButton') -and
        (Find-VisibleSmokeElement $Product.Element 'DesktopProducer')
    } 'the desktop with real application icons'
}

function Assert-SmokeBackgroundRemains($Product, [string]$RuntimeLog, [string]$InstanceId, [int]$BaselineCount, [int]$ObservationMs = 1200, [string]$IconId = 'SampleButton') {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        if (-not (Find-VisibleSmokeElement $Product.Element 'SampleButton') -or
            (Find-VisibleSmokeElement $Product.Element 'RuntimeStatus')) { throw 'A background single-click unexpectedly activated the game.' }
        if (Find-SmokeMenuItem $Product 'RunningContinueButton' @()) { throw 'A single click unexpectedly opened the double-click action panel.' }
        $currentEvents = @(Read-SmokeEvents $RuntimeLog)
        $newEvents = @($currentEvents | Select-Object -Skip $BaselineCount)
        if (@($newEvents | Where-Object { $_.state -eq 'Foreground' -or $_.state -eq 'Starting' -or $_.instanceId -ne $InstanceId }).Count -gt 0) {
            throw 'A background single-click changed or replaced the running instance.'
        }
        $lastState = @($currentEvents | Where-Object { $_.instanceId -eq $InstanceId })[-1]
        if ($lastState.state -ne 'Background' -or -not $lastState.blocksMaintenance) { throw 'Background game state was not preserved.' }
        Start-Sleep -Milliseconds 100
    } while ($timer.ElapsedMilliseconds -lt $ObservationMs)
    Add-SmokeCheck "background_single_click_preserves_instance_without_panel_$IconId" $true "Observed actual UI and runtime events for at least $ObservationMs ms after a real synthetic pointer click."
}

function Get-SmokeSelectedState($Element) {
    $selection = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern, [ref]$selection)) { return $selection.Current.IsSelected }
    $toggle = $null
    if ($Element.TryGetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern, [ref]$toggle)) {
        return $toggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On
    }
    # Host-declared accessibility state is only a fallback when no native selection pattern is offered.
    if ($Element.Current.ItemStatus -in @('selected', 'not_selected')) { return $Element.Current.ItemStatus -eq 'selected' }
    if ($Element.Current.HelpText -in @('已选中', '未选中')) { return $Element.Current.HelpText -eq '已选中' }
    throw 'The settings navigation does not expose a verifiable selected state.'
}

function Assert-SmokeSettingsSelection($Product, [string]$SelectedId, [string[]]$AllIds, [string]$ExpectedTitle) {
    $null = Wait-SmokeCondition {
        $selected = Find-VisibleSmokeElement $Product.Element $SelectedId
        $heading = Find-VisibleSmokeElement $Product.Element 'SettingsDetailTitle'
        $selected -and (Get-SmokeSelectedState $selected) -and $heading -and $heading.Current.Name -eq $ExpectedTitle
    } "settings selection and matching detail title $ExpectedTitle"
    $selectedIds = @()
    foreach ($id in $AllIds) {
        $item = Find-VisibleSmokeElement $Product.Element $id
        if (-not $item) { throw "The settings sidebar item disappeared: $id" }
        if (Get-SmokeSelectedState $item) { $selectedIds += $id }
    }
    Add-SmokeCheck "settings_single_selected_category_$SelectedId" ($selectedIds.Count -eq 1 -and $selectedIds[0] -eq $SelectedId) 'All five navigation items remain visible; only the current category exposes the selected toggle state.'
    Add-SmokeCheck "settings_right_detail_matches_$SelectedId" ((Find-VisibleSmokeElement $Product.Element 'SettingsDetailTitle').Current.Name -eq $ExpectedTitle)
}

function Select-SmokeSettings($Product, [string]$Id, [string]$Title, [string[]]$AllIds) {
    $item = Wait-SmokeCondition { Find-VisibleSmokeElement $Product.Element $Id } "settings category $Id"
    $toggle = $item.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
    Assert-SmokeSettingsSelection $Product $Id $AllIds $Title
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

function Wait-SmokePreferences([string]$Path, [string]$Theme, [string]$Wallpaper) {
    return Wait-SmokeCondition {
        if (-not (Test-Path -LiteralPath $Path)) { return $null }
        $saved = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        if ($saved.schemaVersion -eq 1 -and $saved.theme -eq $Theme -and $saved.wallpaper -eq $Wallpaper -and $saved.revision -ge 1) { return $saved }
        return $null
    } "committed appearance $Theme / $Wallpaper"
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

function Start-SmokeProduct([string]$WorkingDirectory) {
    # A visible product window is explicitly part of the requested Windows runtime verification.
    $entry = Start-Process -FilePath $report.tested_executable -WorkingDirectory $WorkingDirectory -WindowStyle Normal -PassThru
    $isBootstrap = Test-Path -LiteralPath (Join-Path (Split-Path $report.tested_executable -Parent) 'AutumnOS.Client.exe') -PathType Leaf
    if ($isBootstrap) {
        # Other maintained drivers import this function by AST; preserve that calling convention.
        if (-not (Get-Variable -Name ownedEntryProcesses -ErrorAction SilentlyContinue)) { $script:ownedEntryProcesses = [Collections.Generic.List[Diagnostics.Process]]::new() }
        $ownedEntryProcesses.Add($entry)
        if (-not $report.Contains('bootstrap_process_ids')) { $report['bootstrap_process_ids'] = @() }
        $report.bootstrap_process_ids += $entry.Id
    } else { $ownedProcesses.Add($entry) }
    $process = Resolve-SmokeBusinessProcess -EntryProcess $entry -ExecutablePath $report.tested_executable -TimeoutSeconds $WindowTimeoutSeconds
    if ($isBootstrap) { $ownedProcesses.Add($process) }
    $report.process_ids += $process.Id
    $idle = $process.WaitForInputIdle($WindowTimeoutSeconds * 1000)
    if (-not $idle) { throw 'The product did not reach an input-idle state within the timeout.' }
    $handle = Wait-SmokeCondition {
        $process.Refresh()
        if ($process.HasExited) { throw "The product exited before its window appeared (exit $($process.ExitCode))." }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { return $process.MainWindowHandle }
        return $null
    } 'the native product window'
    $element = [System.Windows.Automation.AutomationElement]::FromHandle($handle)
    $null = Wait-SmokeCondition {
        (Find-VisibleSmokeElement $element 'HelloNextButton') -or (Find-VisibleSmokeElement $element 'SampleButton')
    } 'the first-run or restored desktop UI'
    Add-SmokeCheck 'native_window_owned_by_started_process' ($element.Current.ProcessId -eq $process.Id -and [AutumnDesktopInteractionSmoke.NativeWindows]::IsWindowVisible($handle))
    return [pscustomobject]@{ Process = $process; EntryProcess = $entry; IsBootstrap = $isBootstrap; Handle = $handle; Element = $element }
}

function Close-SmokeProduct($Product) {
    if (-not $Product.Process.HasExited) {
        $null = [AutumnDesktopInteractionSmoke.NativeWindows]::SetWindowPos($Product.Handle, [IntPtr](-2), 0, 0, 0, 0, 3)
        $closed = $Product.Process.CloseMainWindow()
        if (-not $closed -or -not $Product.Process.WaitForExit(10000)) { throw 'The recorded product window did not close cleanly.' }
    }
    Add-SmokeCheck 'recorded_product_process_closed_cleanly' ($Product.Process.ExitCode -eq 0)
    if ($Product.IsBootstrap) {
        Add-SmokeCheck 'stable_entry_returns_business_exit_code' ($Product.EntryProcess.WaitForExit(10000) -and $Product.EntryProcess.ExitCode -eq $Product.Process.ExitCode)
    }
}

function Save-SmokeScreenshot($Product, [string]$Name, [switch]$IncludeOverlays) {
    $path = Join-Path $ReportDirectory "$Name.png"
    $bitmap = $null
    $graphics = $null
    try {
        if ($IncludeOverlays) { Focus-SmokeProduct $Product }
        Start-Sleep -Milliseconds 350
        $bounds = [AutumnDesktopInteractionSmoke.NativeWindows+RECT]::new()
        if (-not [AutumnDesktopInteractionSmoke.NativeWindows]::GetClientRect($Product.Handle, [ref]$bounds)) { throw 'Cannot read the product client bounds.' }
        $clientOrigin = [AutumnDesktopInteractionSmoke.NativeWindows+POINT]::new()
        if (-not [AutumnDesktopInteractionSmoke.NativeWindows]::ClientToScreen($Product.Handle, [ref]$clientOrigin)) { throw 'Cannot locate the product client area.' }
        $width = $bounds.Right - $bounds.Left
        $height = $bounds.Bottom - $bounds.Top
        if ($width -le 0 -or $height -le 0 -or $width -gt 10000 -or $height -gt 10000) { throw 'Invalid window capture bounds.' }
        $bitmap = [Drawing.Bitmap]::new($width, $height)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $printed = $false
        if (-not $IncludeOverlays) {
            $deviceContext = $graphics.GetHdc()
            # PW_CLIENTONLY | PW_RENDERFULLCONTENT excludes window shadows and the desktop beneath them.
            try { $printed = [AutumnDesktopInteractionSmoke.NativeWindows]::PrintWindow($Product.Handle, $deviceContext, 3) }
            finally { $graphics.ReleaseHdc($deviceContext) }
        }
        $captureMode = 'PrintWindow_CLIENTONLY_RENDERFULLCONTENT'
        if (-not $printed) {
            Focus-SmokeProduct $Product
            # Foreground recovery can move the held window. Measure only after that final focus.
            $latestBounds=[AutumnDesktopInteractionSmoke.NativeWindows+RECT]::new()
            $clientOrigin=[AutumnDesktopInteractionSmoke.NativeWindows+POINT]::new()
            if(-not [AutumnDesktopInteractionSmoke.NativeWindows]::GetClientRect($Product.Handle,[ref]$latestBounds) -or
               -not [AutumnDesktopInteractionSmoke.NativeWindows]::ClientToScreen($Product.Handle,[ref]$clientOrigin)){throw 'Cannot read final foreground capture geometry.'}
            if(($latestBounds.Right-$latestBounds.Left) -ne $width -or ($latestBounds.Bottom-$latestBounds.Top) -ne $height){
                throw 'The owned client size changed before capture; refusing an inconsistent screenshot.'
            }
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

try {
    if (-not $IsWindows) { throw 'Windows smoke verification requires Windows and an interactive desktop.' }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    $report.isElevated = $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Add-SmokeCheck 'standard_user_token' (-not $report.isElevated) 'No elevation is requested or used.'
    $existing = @(Get-Process -Name AutumnOS,AutumnOS.Client -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq [Diagnostics.Process]::GetCurrentProcess().SessionId })
    if ($existing.Count) { throw 'An existing AutumnOS entry or business process is running; it was not activated or closed. Exit it normally before smoke testing.' }

    $trackedBuild = $null
    $trackedPrepared = $null
    if (-not $ExecutablePath) {
        $latest = (Get-Content -LiteralPath (Join-Path $projectRoot 'artifacts/latest-build.txt') -Raw).Trim()
        if ($latest -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,90}$') { throw 'Invalid latest build identity.' }
        $trackedBuild = Get-Content -LiteralPath (Join-Path $projectRoot "artifacts/builds/$latest/build-result.json") -Raw | ConvertFrom-Json
        if ($trackedBuild.status -ne 'passed') { throw 'A successful Windows build is required.' }
        $ExecutablePath = Join-Path $projectRoot $trackedBuild.output_path
        if ([IO.Path]::GetFileName($ExecutablePath) -eq 'AutumnOS.Client.exe') {
            $preparedPath = Join-Path $projectRoot "artifacts/builds/$latest/package-prepared.json"
            if (-not (Test-Path -LiteralPath $preparedPath -PathType Leaf)) { throw 'A split client build needs its prepared AutumnOS.exe delivery; supply -ExecutablePath after packaging.' }
            $trackedPrepared = Get-Content -LiteralPath $preparedPath -Raw | ConvertFrom-Json
            if ($trackedPrepared.build_id -ne $trackedBuild.build_id -or $trackedPrepared.source_snapshot_id -ne $trackedBuild.source_snapshot_id) { throw 'Prepared stable entry belongs to a different tracked build.' }
            $ExecutablePath = $trackedPrepared.executable
        }
    }
    $ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
    if ([IO.Path]::GetFileName($ExecutablePath) -ne 'AutumnOS.exe' -or -not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) {
        throw 'ExecutablePath must identify an existing AutumnOS.exe build.'
    }
    $sourceDirectory = Split-Path $ExecutablePath -Parent
    $report.source_executable = $ExecutablePath
    $report.source_executable_sha256 = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath $sourceDirectory -Force) {
        if ($entry.Name -eq 'AutumnOS_Data') { continue }
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Build output contains a reparse point; refusing to follow it.' }
        if ($entry.PSIsContainer) {
            $links = @(Get-ChildItem -LiteralPath $entry.FullName -Recurse -Force | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 })
            if ($links.Count -gt 0) { throw 'Build output contains nested reparse points; refusing to follow them.' }
        }
        Copy-Item -LiteralPath $entry.FullName -Destination $stageDirectory -Recurse -Force
    }
    $report.tested_executable = Join-Path $stageDirectory 'AutumnOS.exe'
    Add-SmokeCheck 'copied_executable_matches_build' ((Get-FileHash -LiteralPath $report.tested_executable -Algorithm SHA256).Hash.ToLowerInvariant() -eq $report.source_executable_sha256)
    $dataDirectory = Join-Path $stageDirectory 'AutumnOS_Data'
    Add-SmokeCheck 'copied_build_excludes_original_user_data' (-not (Test-Path -LiteralPath $dataDirectory))

    $businessName = if (Test-Path -LiteralPath (Join-Path $stageDirectory 'AutumnOS.Client.exe') -PathType Leaf) { 'AutumnOS.Client' } else { 'AutumnOS' }
    $report.business_executable = Join-Path $stageDirectory ($businessName + '.exe')
    $report.source_business_sha256 = (Get-FileHash -LiteralPath (Join-Path $sourceDirectory ($businessName + '.exe')) -Algorithm SHA256).Hash.ToLowerInvariant()
    Add-SmokeCheck 'copied_business_executable_matches_build' ((Get-FileHash -LiteralPath $report.business_executable -Algorithm SHA256).Hash.ToLowerInvariant() -eq $report.source_business_sha256)
    foreach ($resource in @(($businessName + '.pri'), ($businessName + '.deps.json'), ($businessName + '.runtimeconfig.json'), 'App.xbf', 'MainWindow.xbf')) {
        Add-SmokeCheck "resource_$resource" (Test-Path -LiteralPath (Join-Path $stageDirectory $resource) -PathType Leaf)
    }
    # Load bytes so metadata inspection cannot occupy the delivered DLL across a later action.
    $contractsAssembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $stageDirectory 'AutumnOS.Contracts.dll')))
    $metadata = @{}
    foreach ($attribute in $contractsAssembly.GetCustomAttributesData()) {
        if ($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute') {
            $metadata[$attribute.ConstructorArguments[0].Value] = $attribute.ConstructorArguments[1].Value
        }
        elseif ($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyProductAttribute') {
            $metadata['ProductName'] = $attribute.ConstructorArguments[0].Value
        }
        elseif ($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyInformationalVersionAttribute') {
            $metadata['ProductVersion'] = $attribute.ConstructorArguments[0].Value
        }
    }
    $report.build_id = $metadata['BuildId']
    $report.source_snapshot_id = $metadata['SourceSnapshotId']
    if ($trackedBuild) {
        Add-SmokeCheck 'binary_matches_tracked_build_identity' ($report.build_id -eq $trackedBuild.build_id -and $report.source_snapshot_id -eq $trackedBuild.source_snapshot_id)
        $hashManifest = Join-Path $projectRoot "artifacts/builds/$($trackedBuild.build_id)/artifact-hashes.json"
        if (Test-Path -LiteralPath $hashManifest) {
            $hashes = @(Get-Content -LiteralPath $hashManifest -Raw | ConvertFrom-Json)
            $expectedExecutable = @($hashes | Where-Object { $_.path -eq $trackedBuild.output_path })
            Add-SmokeCheck 'executable_matches_tracked_hash' ($expectedExecutable.Count -eq 1 -and $expectedExecutable[0].sha256 -eq $report.source_business_sha256)
            if ($trackedPrepared) { Add-SmokeCheck 'stable_entry_matches_prepared_hash' ($report.source_executable_sha256 -eq $trackedPrepared.executable_sha256) }
        } else { throw 'Tracked build is missing artifact-hashes.json.' }
    }

    $desktopRuntime = Join-Path $projectRoot '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach ($assemblyName in @('WindowsBase.dll', 'UIAutomationTypes.dll', 'UIAutomationClient.dll', 'System.Drawing.Common.dll')) {
        Add-Type -Path (Join-Path $desktopRuntime $assemblyName)
    }
    if (-not ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace AutumnDesktopInteractionSmoke {
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
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
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
    }
}
'@
    }

    $samplePath = Join-Path $stageDirectory 'Samples/element-pairs.autumn'
    if (-not (Test-Path -LiteralPath $samplePath -PathType Leaf)) { throw 'The built-in .autumn sample is absent from this executable directory.' }
    $archive = [IO.Compression.ZipFile]::OpenRead($samplePath)
    try {
        $manifestEntry = $archive.GetEntry('manifest.json')
        if (-not $manifestEntry -or $manifestEntry.Length -gt 65536) { throw 'The sample manifest is absent or too large.' }
        $manifestReader = [IO.StreamReader]::new($manifestEntry.Open())
        try { $sample = $manifestReader.ReadToEnd() | ConvertFrom-Json }
        finally { $manifestReader.Dispose() }
    } finally { $archive.Dispose() }
    if ($sample.appId -notmatch '^[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*)+$' -or $sample.version -notmatch '^[0-9][0-9A-Za-z.+-]{0,127}$') {
        throw 'The sample manifest identity or version cannot safely form a test path.'
    }
    $report.sample_app_id = $sample.appId
    $report.sample_version = $sample.version
    $sampleHash = (Get-FileHash -LiteralPath $samplePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $installRecordPath = Join-Path $dataDirectory "Apps/$($sample.appId)/$($sample.version)/.autumnos-install.json"
    $statePath = Join-Path $dataDirectory 'Config/first-run.json'
    $preferencesPath = Join-Path $dataDirectory 'Config/desktop-preferences.json'
    $runtimeLog = Join-Path $dataDirectory 'Logs/runtime-events.jsonl'
    $savePath = Join-Path $dataDirectory "Saves/$($sample.appId)/guest/game.json"
    $firstWorkingDirectory = Join-Path $scope 'first-working-directory'
    $secondWorkingDirectory = Join-Path $scope 'second-working-directory'
    New-Item -ItemType Directory -Path $firstWorkingDirectory, $secondWorkingDirectory -Force | Out-Null
    $report.working_directories = @($firstWorkingDirectory, $secondWorkingDirectory)

    $first = Start-SmokeProduct $firstWorkingDirectory
    $report.standard_user_window = 'passed'
    Add-SmokeCheck 'native_product_window_title' ($first.Element.Current.Name -eq $metadata['ProductName'] -or $first.Element.Current.Name -eq 'Lab Chronicles AutumnOS')
    Add-SmokeCheck 'first_launch_starts_at_hello' ($null -ne (Find-VisibleSmokeElement $first.Element 'HelloNextButton'))
    $null = Save-SmokeScreenshot $first '08-hello'
    Invoke-SmokeButton $first.Element 'HelloNextButton'
    $null = Wait-SmokeCondition { Find-VisibleSmokeElement $first.Element 'BrandNextButton' } 'the AutumnOS brand stage'
    Add-SmokeCheck 'brand_stage_has_product_name' ((Find-SmokeElement $first.Element 'WelcomeBrand').Current.Name -eq $metadata['DisplayName'])
    $null = Save-SmokeScreenshot $first '09-brand'
    Invoke-SmokeButton $first.Element 'BrandNextButton'
    $null = Wait-SmokeCondition { Find-VisibleSmokeElement $first.Element 'PrepareDesktopButton' } 'the initialization stage'
    $null = Save-SmokeScreenshot $first '10-initialization'
    Invoke-SmokeButton $first.Element 'PrepareDesktopButton'
    Wait-SmokeDesktop $first
    $null = Wait-SmokeCondition {
        if (-not (Test-Path -LiteralPath $statePath)) { return $false }
        (Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json).checkpoint -eq 'Completed'
    } 'the durable Completed first-run checkpoint'
    Add-SmokeCheck 'first_run_committed_completed_after_preparation' $true
    $null = Wait-SmokeCondition { Test-Path -LiteralPath $installRecordPath } 'the sample installed during desktop preparation'
    $installed = Get-Content -LiteralPath $installRecordPath -Raw | ConvertFrom-Json
    Add-SmokeCheck 'desktop_sample_matches_bundled_manifest_and_archive' ($installed.appId -eq $sample.appId -and $installed.version -eq $sample.version -and $installed.packageSha256 -eq $sampleHash)
    foreach ($iconId in @('SampleButton', 'SettingsButton', 'DockSampleButton', 'DockSettingsButton')) {
        Add-SmokeCheck "visible_real_desktop_icon_$iconId" ($null -ne (Find-VisibleSmokeElement $first.Element $iconId))
    }
    Add-SmokeCheck 'desktop_central_producer_credit_visible' ((Find-SmokeElement $first.Element 'DesktopProducer').Current.Name -eq $metadata['ProducerCredit'])
    Assert-SmokeRunningBadges $first $false 'unstarted_game_has_no_running_badge'
    $null = Save-SmokeScreenshot $first '11-light-desktop'
    $arrangeResult = Test-SmokeDesktopArrange -Product $first -DataDirectory $dataDirectory
    $report.desktop_arrange = $arrangeResult
    $report.first_run = 'passed'
    $report.unchanged_state_sha256 = (Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash.ToLowerInvariant()

    $settingsIds = @('SettingsNavAppearance', 'SettingsNavDesktop', 'SettingsNavNotifications', 'SettingsNavDiagnostics', 'SettingsNavAbout')
    Invoke-SmokeButton $first.Element 'SettingsButton'
    Assert-SmokeSettingsSelection $first 'SettingsNavAppearance' $settingsIds '外观'
    $detailBounds = (Find-VisibleSmokeElement $first.Element 'SettingsDetailTitle').Current.BoundingRectangle
    foreach ($navId in $settingsIds) {
        $navBounds = (Find-VisibleSmokeElement $first.Element $navId).Current.BoundingRectangle
        if ($navBounds.Right -ge $detailBounds.Left) { throw "Settings category is not left of the detail pane: $navId" }
    }
    Add-SmokeCheck 'five_navigation_items_are_left_of_settings_detail_pane' $true
    $null = Save-SmokeScreenshot $first '01-settings-overview'
    Select-SmokeSettings $first 'SettingsNavDesktop' '桌面与 Dock' $settingsIds
    Select-SmokeSettings $first 'SettingsNavNotifications' '通知与控制中心' $settingsIds
    Select-SmokeSettings $first 'SettingsNavDiagnostics' '开发者诊断' $settingsIds
    Add-SmokeCheck 'diagnostics_preserves_product_and_producer_in_detail_pane' ((Find-VisibleSmokeElement $first.Element 'ProductTitle').Current.Name -eq $metadata['DisplayName'] -and
        (Find-VisibleSmokeElement $first.Element 'ProducerCredit').Current.Name -eq $metadata['ProducerCredit'])
    Add-SmokeCheck 'diagnostics_does_not_repeat_completed_initialization' (-not (Find-SmokeElement $first.Element 'InitializeButton').Current.IsEnabled)
    Add-SmokeCheck 'diagnostics_keeps_real_checkpoint_and_configuration' (-not [string]::IsNullOrWhiteSpace((Find-SmokeElement $first.Element 'CheckpointText').Current.Name) -and
        -not [string]::IsNullOrWhiteSpace((Find-SmokeElement $first.Element 'ConfigurationText').Current.Name)) 'Retained real accessible values; diagnostics scrolls when the developer mode section pushes configuration below the fold.'
    if($report.build_id.StartsWith('T06-')){
        Add-SmokeCheck 'diagnostics_exposes_real_local_support_export' ($null -ne (Find-SmokeElement $first.Element 'SupportExportButton'))
        Invoke-SmokeButton $first.Element 'SupportExportButton'
        $null=Wait-SmokeCondition {Find-SmokeNamedElement $first.Element '选择保存位置' ([Windows.Automation.ControlType]::Button)} 'native diagnostic export consent'
        $null=Save-SmokeScreenshot $first '03a-support-export-consent' -IncludeOverlays
        Invoke-SmokeNamedButton $first.Element '取消'
        $null=Wait-SmokeCondition {(Find-SmokeElement $first.Element 'SupportExportStatus').Current.Name -eq '已取消，没有生成诊断包。'} 'explicit export cancellation'
        Add-SmokeCheck 'support_export_cancel_reports_no_output' ((Find-SmokeElement $first.Element 'SupportExportButton').Current.IsEnabled)
    }
    $null = Save-SmokeScreenshot $first '03-diagnostics'
    Select-SmokeSettings $first 'SettingsNavAbout' '关于' $settingsIds
    Add-SmokeCheck 'about_page_preserves_central_brand_and_producer' ((Find-VisibleSmokeElement $first.Element 'AboutProductName').Current.Name -eq $metadata['ProductName'] -and
        (Find-VisibleSmokeElement $first.Element 'AboutProducer').Current.Name -eq $metadata['ProducerCredit'])
    Add-SmokeCheck 'about_page_has_actual_version_and_build' ((Find-VisibleSmokeElement $first.Element 'AboutVersion').Current.Name -eq $metadata['ProductVersion'] -and
        (Find-VisibleSmokeElement $first.Element 'AboutBuild').Current.Name -eq $report.build_id)
    $null = Save-SmokeScreenshot $first '16-about'
    $report.about_page = 'passed'
    $report.about_surface = 'native_settings_detail_pane'
    $report.settings_navigation = 'passed'
    Select-SmokeSettings $first 'SettingsNavAppearance' '外观' $settingsIds
    Invoke-SmokeButton $first.Element 'SystemThemeButton'
    Invoke-SmokeButton $first.Element 'MistWallpaperButton'
    $null = Wait-SmokePreferences $preferencesPath 'system' 'mist'
    Add-SmokeCheck 'system_theme_and_mist_wallpaper_are_persisted' $true
    Invoke-SmokeButton $first.Element 'LightThemeButton'
    Invoke-SmokeButton $first.Element 'WarmWallpaperButton'
    $null = Wait-SmokePreferences $preferencesPath 'light' 'warm'
    Add-SmokeCheck 'light_theme_and_warm_wallpaper_are_persisted' $true
    $null = Save-SmokeScreenshot $first '02-appearance'
    Invoke-SmokeButton $first.Element 'DarkThemeButton'
    Invoke-SmokeButton $first.Element 'NightWallpaperButton'
    $darkPreferences = Wait-SmokePreferences $preferencesPath 'dark' 'night'
    Add-SmokeCheck 'dark_theme_and_night_wallpaper_are_persisted' ($darkPreferences.revision -ge 3)
    Add-SmokeCheck 'settings_reports_appearance_status' (-not [string]::IsNullOrWhiteSpace((Find-SmokeElement $first.Element 'PreferencesStatus').Current.Name))
    Invoke-SmokeButton $first.Element 'SettingsHomeButton'
    Wait-SmokeDesktop $first
    $darkAppearance = (Find-SmokeElement $first.Element 'DesktopAppearance').Current.Name
    Add-SmokeCheck 'desktop_reports_selected_dark_appearance' ($darkAppearance -match '深色' -and $darkAppearance -match '暮色')
    $null = Save-SmokeScreenshot $first '12-dark-desktop'
    $preferencesHash = (Get-FileHash -LiteralPath $preferencesPath -Algorithm SHA256).Hash.ToLowerInvariant()

    Invoke-SmokePointer $first 'SampleButton' 'long_press'
    $null = Wait-SmokeCondition { Find-SmokeMenuItem $first 'SampleMenuInfo' @('应用信息') } 'the native long-press application menu'
    Add-SmokeCheck 'synthetic_750ms_press_opens_native_context_menu' $true 'Actual synthetic left-button down/up targeted at SampleButton; physical touch remains not_run.'
    Add-SmokeCheck 'unstarted_context_menu_only_offers_open_and_information' ((Find-SmokeMenuItem $first 'SampleMenuOpen' @('打开')).Current.Name -eq '打开' -and
        $null -eq (Find-SmokeMenuItem $first 'SampleMenuClose' @('结束游戏')))
    $null = Save-SmokeScreenshot $first '13-long-press-menu' -IncludeOverlays
    Send-SmokeEscape $first
    $null = Wait-SmokeCondition { -not (Find-SmokeMenuItem $first 'SampleMenuInfo' @('应用信息')) } 'long-press menu dismissed with Escape'
    Add-SmokeCheck 'long_press_does_not_launch_the_game' (-not (Test-Path -LiteralPath $runtimeLog))
    Invoke-SmokePointer $first 'SampleButton' 'right_click'
    $null = Wait-SmokeCondition { Find-SmokeMenuItem $first 'SampleMenuInfo' @('应用信息') } 'the native right-click application menu'
    Add-SmokeCheck 'right_click_opens_native_context_menu' $true
    $null = Save-SmokeScreenshot $first '14-right-click-menu' -IncludeOverlays
    Invoke-SmokeMenuItem $first 'SampleMenuInfo' @('应用信息')
    $null = Wait-SmokeCondition { Find-SmokeNamedElement $first.Element '关闭' ([System.Windows.Automation.ControlType]::Button) } 'application information dialog'
    $null = Save-SmokeScreenshot $first '15-application-info'
    $textCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $texts = $first.Element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
    $information = @($texts | ForEach-Object { $_.Current.Name }) -join "`n"
    Add-SmokeCheck 'application_information_uses_installed_identity' ($information.Contains($sample.appId) -and $information.Contains($sample.version))
    Invoke-SmokeNamedButton $first.Element '关闭'

    # A real single pointer click on a stopped icon must launch an actual internal session.
    Invoke-SmokePointer $first 'SampleButton' 'single_click'
    $null = Wait-SmokeCondition {
        $state = Find-VisibleSmokeElement $first.Element 'RuntimeStatus'
        $state -and $state.Current.Name -match '前台运行'
    } 'internal WebView2 game launched by a real single click'
    $null = Wait-SmokeCondition { @(Read-SmokeEvents $runtimeLog | Where-Object { $_.state -eq 'Foreground' }).Count -gt 0 } 'actual foreground runtime event'
    $firstInstance = @(Read-SmokeEvents $runtimeLog | Where-Object { $_.state -eq 'Foreground' })[-1].instanceId
    Add-SmokeCheck 'single_click_on_stopped_desktop_icon_launches_real_internal_application' (-not [string]::IsNullOrWhiteSpace($firstInstance))
    $report.first_instance_id = $firstInstance
    $null = Save-SmokeScreenshot $first '17-internal-application'
    $nickname = Wait-SmokeCondition { Find-SmokeNamedElement $first.Element '玩家昵称' ([System.Windows.Automation.ControlType]::Edit) } 'game nickname input exposed by native accessibility'
    $nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('派蒙·交互验证')
    Invoke-SmokeNamedButton $first.Element '第 1 张卡片，未翻开'
    Invoke-SmokeNamedButton $first.Element '第 2 张卡片，未翻开'
    Invoke-SmokeNamedButton $first.Element '保存进度'
    $null = Wait-SmokeCondition { Find-SmokeNamedElement $first.Element '允许' ([System.Windows.Automation.ControlType]::Button) } 'native permission overlay'
    $null = Save-SmokeScreenshot $first '18-permission-overlay'
    Invoke-SmokeNamedButton $first.Element '允许'
    $null = Wait-SmokeCondition { Test-Path -LiteralPath $savePath } 'a real guest save committed through the SDK'
    $save = Get-Content -LiteralPath $savePath -Raw | ConvertFrom-Json
    Add-SmokeCheck 'guest_save_contains_unicode_input_and_card_progress' ($save.appId -eq $sample.appId -and $save.value.nickname -eq '派蒙·交互验证' -and $save.value.moves -ge 1)
    $saveHash = (Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $null = Save-SmokeScreenshot $first '19-saved-application'
    # Keep distinct, unsaved input to prove that Continue preserves the same WebView instance.
    $nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('派蒙·尚未保存')
    Invoke-SmokeButton $first.Element 'BackgroundButton'
    Wait-SmokeDesktop $first
    Assert-SmokeRunningBadges $first $true 'background_game_marks_both_desktop_and_dock_icons'
    Add-SmokeCheck 'background_desktop_has_no_explanatory_running_card' ($null -eq (Find-VisibleSmokeElement $first.Element 'RunningCard') -and $null -eq (Find-VisibleSmokeElement $first.Element 'RunningTitle'))
    $events = Read-SmokeEvents $runtimeLog
    Add-SmokeCheck 'background_game_keeps_same_instance_and_blocks_maintenance' (@($events | Where-Object { $_.instanceId -eq $firstInstance -and $_.state -eq 'Background' -and $_.blocksMaintenance }).Count -gt 0)
    $null = Save-SmokeScreenshot $first '04-background-icons'
    # User superseded icon double-click management: a single icon click now resumes,
    # while double-clicking blank desktop opens the shared running-app switcher.
    foreach ($resumeIcon in @('SampleButton','DockSampleButton')) {
        $singleClickBaseline = @(Read-SmokeEvents $runtimeLog).Count
        Invoke-SmokePointer $first $resumeIcon 'single_click'
        $foreground = Wait-SmokeNewForeground $runtimeLog $singleClickBaseline $firstInstance
        Add-SmokeCheck "background_single_click_resumes_same_instance_$resumeIcon" ($foreground.instanceId -eq $firstInstance -and -not (Find-VisibleSmokeElement $first.Element 'RunningSwitcherTitle'))
        Invoke-SmokeButton $first.Element 'BackgroundButton';Wait-SmokeDesktop $first
    }
    $report.background_single_click = 'passed'
    foreach ($keyboardCase in @(@('SampleButton', 'Enter'), @('DockSampleButton', 'Space'))) {
        $keyBaseline = @(Read-SmokeEvents $runtimeLog).Count
        Send-SmokeIconKey $first $keyboardCase[0] $keyboardCase[1]
        $latest = Wait-SmokeNewForeground $runtimeLog $keyBaseline $firstInstance
        Add-SmokeCheck "background_keyboard_resumes_same_instance_$($keyboardCase[1])" ($latest.instanceId -eq $firstInstance)
        Invoke-SmokeButton $first.Element 'BackgroundButton';Wait-SmokeDesktop $first
    }
    $report.background_keyboard_actions = 'passed'
    Invoke-SmokePointer $first 'DesktopGestureHint' 'double_click'
    Wait-SmokeRunningPanel $first
    $latest = @(Read-SmokeEvents $runtimeLog)[-1]
    Add-SmokeCheck 'blank_desktop_double_click_opens_switcher_without_activating_game' ($latest.instanceId -eq $firstInstance -and $latest.state -eq 'Background' -and $latest.blocksMaintenance)
    $null = Save-SmokeScreenshot $first '05-double-click-actions' -IncludeOverlays
    $continueBaseline = @(Read-SmokeEvents $runtimeLog).Count
    Invoke-SmokeOwnedButton $first 'RunningContinueButton'
    $null = Wait-SmokeCondition { (Find-VisibleSmokeElement $first.Element 'RuntimeStatus').Current.Name -match '前台运行' } 'the Continue action restoring the existing game'
    $latestForeground = Wait-SmokeNewForeground $runtimeLog $continueBaseline $firstInstance
    Add-SmokeCheck 'continue_panel_resumes_same_runtime_instance' ($latestForeground.instanceId -eq $firstInstance)
    $nickname = Wait-SmokeCondition { Find-SmokeNamedElement $first.Element '玩家昵称' ([System.Windows.Automation.ControlType]::Edit) } 'restored nickname input'
    Add-SmokeCheck 'continue_preserves_unsaved_input_and_saved_bytes' ($nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '派蒙·尚未保存' -and
        (Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $saveHash)
    $null = Save-SmokeScreenshot $first '06-continued-application'
    $report.same_instance_resume = 'passed'
    Invoke-SmokeNamedButton $first.Element '读取存档'
    $null = Wait-SmokeCondition { $nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '派蒙·交互验证' } 'the saved nickname restored through the SDK'
    Add-SmokeCheck 'read_save_does_not_rewrite_committed_bytes' ((Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $saveHash)
    $null = [AutumnDesktopInteractionSmoke.NativeWindows]::ShowWindow($first.Handle, 3)
    $null = Wait-SmokeCondition { [AutumnDesktopInteractionSmoke.NativeWindows]::IsZoomed($first.Handle) } 'game window maximization'
    $null = Save-SmokeScreenshot $first '20-maximized-application'
    $null = [AutumnDesktopInteractionSmoke.NativeWindows]::ShowWindow($first.Handle, 9)
    $null = Wait-SmokeCondition { -not [AutumnDesktopInteractionSmoke.NativeWindows]::IsZoomed($first.Handle) } 'game window restoration'
    $report.maximize_restore = 'passed'

    # End in the blank-desktop switcher closes the real captured session.
    Invoke-SmokeButton $first.Element 'BackgroundButton'
    Wait-SmokeDesktop $first
    Invoke-SmokePointer $first 'DesktopGestureHint' 'double_click'
    Wait-SmokeRunningPanel $first
    Invoke-SmokeOwnedButton $first 'RunningEndButton'
    Invoke-SmokeNamedButton $first.Element '确认结束'
    $null = Wait-SmokeCondition {
        @(Read-SmokeEvents $runtimeLog | Where-Object { $_.instanceId -eq $firstInstance -and $_.state -eq 'Closed' -and -not $_.blocksMaintenance }).Count -gt 0
    } 'the panel End action actually closing the game'
    Wait-SmokeDesktop $first
    Assert-SmokeRunningBadges $first $false 'panel_end_removes_both_running_badges'
    $null = Save-SmokeScreenshot $first '07-closed-desktop'
    $report.background_double_click = 'passed'

    $newInstanceBaseline = @(Read-SmokeEvents $runtimeLog).Count
    Invoke-SmokePointer $first 'DockSampleButton' 'single_click'
    $null = Wait-SmokeCondition { (Find-VisibleSmokeElement $first.Element 'RuntimeStatus').Current.Name -match '前台运行' } 'new application instance launched from the Dock'
    $secondInstance = (Wait-SmokeNewForeground $runtimeLog $newInstanceBaseline '').instanceId
    Add-SmokeCheck 'single_dock_click_after_close_creates_new_instance' ($secondInstance -ne $firstInstance -and -not [string]::IsNullOrWhiteSpace($secondInstance))
    $report.second_instance_id = $secondInstance
    Invoke-SmokeNamedButton $first.Element '读取存档'
    # T03 persists the account/application grant. Older binaries still prompt per instance.
    if (-not (Test-Path -LiteralPath (Join-Path $dataDirectory 'Config/application-permissions.v1.json'))) {
        Invoke-SmokeNamedButton $first.Element '允许'
    } else {
        Add-SmokeCheck 'persisted_permission_reused_by_new_instance' $true 'The real permission store exists; successful SDK read below also verifies it is honored.'
    }
    $nickname = Wait-SmokeCondition { Find-SmokeNamedElement $first.Element '玩家昵称' ([System.Windows.Automation.ControlType]::Edit) } 'new instance nickname input'
    $null = Wait-SmokeCondition { $nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '派蒙·交互验证' } 'new instance restores the earlier guest save'
    Add-SmokeCheck 'new_instance_reopens_saved_progress_without_rewriting' ((Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $saveHash)
    Invoke-SmokeButton $first.Element 'BackgroundButton'
    Wait-SmokeDesktop $first
    Assert-SmokeRunningBadges $first $true 'new_background_instance_has_both_badges'
    Invoke-SmokePointer $first 'SampleButton' 'long_press'
    $null = Wait-SmokeCondition { Find-SmokeMenuItem $first 'SampleMenuOpen' @('继续游戏') } 'the live long-press Continue menu'
    Add-SmokeCheck 'live_long_press_menu_has_continue_and_end' ((Find-SmokeMenuItem $first 'SampleMenuOpen' @('继续游戏')).Current.Name -eq '继续游戏' -and
        $null -ne (Find-SmokeMenuItem $first 'SampleMenuClose' @('结束游戏')))
    $continueBaseline = @(Read-SmokeEvents $runtimeLog).Count
    Invoke-SmokeMenuItem $first 'SampleMenuOpen' @('继续游戏')
    $null = Wait-SmokeCondition { (Find-VisibleSmokeElement $first.Element 'RuntimeStatus').Current.Name -match '前台运行' } 'long-press Continue resuming the new instance'
    Add-SmokeCheck 'long_press_continue_keeps_same_instance' ((Wait-SmokeNewForeground $runtimeLog $continueBaseline $secondInstance).instanceId -eq $secondInstance)
    $report.long_press = 'passed'
    Invoke-SmokeButton $first.Element 'BackgroundButton'
    Wait-SmokeDesktop $first
    Invoke-SmokePointer $first 'DockSampleButton' 'right_click'
    $null = Wait-SmokeCondition { Find-SmokeMenuItem $first 'SampleMenuClose' @('结束游戏') } 'the live right-click End menu'
    Add-SmokeCheck 'live_right_click_menu_has_continue_and_end' ((Find-SmokeMenuItem $first 'SampleMenuOpen' @('继续游戏')).Current.Name -eq '继续游戏' -and
        $null -ne (Find-SmokeMenuItem $first 'SampleMenuClose' @('结束游戏')))
    Invoke-SmokeMenuItem $first 'SampleMenuClose' @('结束游戏')
    Invoke-SmokeNamedButton $first.Element '确认结束'
    $null = Wait-SmokeCondition { @(Read-SmokeEvents $runtimeLog | Where-Object { $_.instanceId -eq $secondInstance -and $_.state -eq 'Closed' -and -not $_.blocksMaintenance }).Count -gt 0 } 'context menu End actually closing the new session'
    Wait-SmokeDesktop $first
    Assert-SmokeRunningBadges $first $false 'context_end_removes_both_running_badges'
    Add-SmokeCheck 'both_ended_instances_release_maintenance_block' (@(Read-SmokeEvents $runtimeLog | Where-Object { $_.state -eq 'Closed' -and -not $_.blocksMaintenance } | Select-Object -ExpandProperty instanceId -Unique).Count -eq 2)
    $report.right_click_menu = 'passed'
    $report.background_markers = 'passed'
    $report.internal_application = 'passed'
    Close-SmokeProduct $first

    $sentinelPath = Join-Path $dataDirectory 'Saves/smoke-existing-save.bin'
    $sentinelBytes = [Text.Encoding]::UTF8.GetBytes("AutumnOS interaction smoke preserved save | $smokeId | 制作人：派蒙")
    [IO.File]::WriteAllBytes($sentinelPath, $sentinelBytes)
    $report.sentinel_sha256 = (Get-FileHash -LiteralPath $sentinelPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $second = Start-SmokeProduct $secondWorkingDirectory
    Wait-SmokeDesktop $second
    Add-SmokeCheck 'restart_does_not_repeat_hello_or_preparation' ($null -eq (Find-VisibleSmokeElement $second.Element 'HelloNextButton') -and $null -eq (Find-VisibleSmokeElement $second.Element 'PrepareDesktopButton'))
    Add-SmokeCheck 'restart_preserves_completed_checkpoint_bytes' ((Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $report.unchanged_state_sha256)
    Add-SmokeCheck 'restart_preserves_existing_sentinel_and_game_save' ((Get-FileHash -LiteralPath $sentinelPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $report.sentinel_sha256 -and
        (Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $saveHash)
    $null = Wait-SmokePreferences $preferencesPath 'dark' 'night'
    Add-SmokeCheck 'restart_preserves_desktop_preferences_exact_bytes' ((Get-FileHash -LiteralPath $preferencesPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $preferencesHash)
    Add-SmokeCheck 'restart_restores_visible_desktop_appearance' ((Find-SmokeElement $second.Element 'DesktopAppearance').Current.Name -eq $darkAppearance)
    Assert-SmokeRunningBadges $second $false 'restart_does_not_invent_a_running_instance'
    Add-SmokeCheck 'different_working_directories_receive_no_user_data' (-not (Test-Path -LiteralPath (Join-Path $firstWorkingDirectory 'AutumnOS_Data')) -and -not (Test-Path -LiteralPath (Join-Path $secondWorkingDirectory 'AutumnOS_Data')))
    $expectedDirectories = @('Apps', 'Packages', 'Downloads', 'Cache', 'AppData', 'Saves', 'Screenshots', 'Config', 'Logs', 'Runtime', 'Temp', 'Updates')
    $missingDirectories = @($expectedDirectories | Where-Object { -not (Test-Path -LiteralPath (Join-Path $dataDirectory $_) -PathType Container) })
    Add-SmokeCheck 'real_app_created_all_twelve_data_directories' ($missingDirectories.Count -eq 0)
    $null = Save-SmokeScreenshot $second '21-restarted-desktop'
    $restartedLayoutHash=(Get-FileHash -LiteralPath $arrangeResult.layoutPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $restartedIcon=(Find-VisibleSmokeElement $second.Element 'SampleButton').Current.BoundingRectangle
    $restartedWindow=$second.Element.Current.BoundingRectangle
    Add-SmokeCheck 'restart_preserves_exact_layout_bytes' ($restartedLayoutHash -eq $arrangeResult.layoutHash)
    Add-SmokeCheck 'restart_restores_actual_icon_position' ([Math]::Abs($restartedIcon.Left-$restartedWindow.Left-$arrangeResult.sampleRelativeBounds.left) -lt 2 -and [Math]::Abs($restartedIcon.Top-$restartedWindow.Top-$arrangeResult.sampleRelativeBounds.top) -lt 2)
    $report.desktop_arrange.restart_restore='passed'
    Invoke-SmokeButton $second.Element 'DockSettingsButton'
    Select-SmokeSettings $second 'SettingsNavAppearance' '外观' $settingsIds
    Add-SmokeCheck 'reopened_settings_do_not_rewrite_preferences' ((Get-FileHash -LiteralPath $preferencesPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $preferencesHash)
    $report.restart_data_preservation = 'passed'
    $report.appearance_persistence = 'passed'
    $report.desktop_checkpoint = 'passed'
    Close-SmokeProduct $second
    $missingScreenshots = @($expectedScreenshots | Where-Object { $expected = $_; -not @($screenshots | Where-Object { $_.name -eq $expected -and $_.status -eq 'captured' }).Count })
    Add-SmokeCheck 'all_requested_checkpoint_screenshots_captured' ($missingScreenshots.Count -eq 0) 'Screenshots are captured evidence, not producer design approval.'
    $report.status = 'passed'
} catch {
    $report.failure_type = $_.Exception.GetType().FullName
    $report.failure = $_.Exception.Message
    Write-Warning "Windows smoke failed: $($report.failure)"
    $failedProduct = if ($second -and -not $second.Process.HasExited) { $second } elseif ($first -and -not $first.Process.HasExited) { $first } else { $null }
    if ($failedProduct) { $null = Save-SmokeScreenshot $failedProduct '99-failure' -IncludeOverlays }
} finally {
    foreach ($process in $ownedProcesses) {
        try {
            if (-not $process.HasExited) {
                if ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type]) {
                    $process.Refresh()
                    $null = [AutumnDesktopInteractionSmoke.NativeWindows]::SetWindowPos($process.MainWindowHandle, [IntPtr](-2), 0, 0, 0, 0, 3)
                }
                $null = $process.CloseMainWindow()
                if (-not $process.WaitForExit(3000)) {
                    # The held Process object identifies only a process started by this script; never enumerate/kill by name.
                    $process.Kill()
                    $null = $process.WaitForExit(3000)
                    $checks.Add([ordered]@{ name = 'cleanup_recorded_process'; status = 'forced_close'; process_id = $process.Id })
                    $report.status = 'failed'
                }
            }
        } catch {
            $checks.Add([ordered]@{ name = 'cleanup_recorded_process'; status = 'failed'; process_id = $process.Id; detail = $_.Exception.Message })
            $report.status = 'failed'
        } finally { $process.Dispose() }
    }
    foreach ($entry in $ownedEntryProcesses) {
        try {
            if (-not $entry.WaitForExit(10000)) {
                $checks.Add([ordered]@{name='cleanup_owned_bootstrap';status='failed';process_id=$entry.Id;detail='Owned bootstrap did not exit; left running without termination.'})
                $report.status='failed'
            }
        } catch { $checks.Add([ordered]@{name='cleanup_owned_bootstrap';status='failed';detail=$_.Exception.Message}); $report.status='failed' }
        finally { $entry.Dispose() }
    }
    $capturedCount = @($screenshots | Where-Object { $_.status -eq 'captured' }).Count
    if ($script:initialPointerPosition -and ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])) {
        $null = [AutumnDesktopInteractionSmoke.NativeWindows]::SetCursorPos($script:initialPointerPosition.X, $script:initialPointerPosition.Y)
    }
    $expectedScreenshotCount = $expectedScreenshots.Count
    if ($capturedCount -eq $expectedScreenshotCount) { $report.screenshot_status = 'captured_not_visually_reviewed' }
    elseif ($capturedCount -gt 0) { $report.screenshot_status = 'partially_captured_not_visually_reviewed' }
    $report.finished_utc = [DateTimeOffset]::UtcNow.ToString('o')
    $report.check_count = $checks.Count
    $report.passed_checks = @($checks | Where-Object { $_.status -eq 'passed' }).Count
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "REPORT $reportPath"
}
if ($report.status -ne 'passed') { throw "Windows smoke verification failed. Evidence preserved at $reportPath" }
