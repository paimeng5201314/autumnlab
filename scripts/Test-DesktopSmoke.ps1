[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [string]$ReportDirectory,
    [ValidateRange(5, 90)][int]$WindowTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$smokeId = 'T02-desktop-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$scope = Join-Path $projectRoot "artifacts/smoke/$smokeId"
$stageDirectory = Join-Path $scope '中文 空格 目录'
if (-not $ReportDirectory) { $ReportDirectory = $scope }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$reportPath = Join-Path $ReportDirectory 'windows-smoke.json'
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$checks = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[object]]::new()
$inputActions = [Collections.Generic.List[object]]::new()
$expectedScreenshots = @('01-hello', '02-brand', '03-initialization', '04-light-desktop', '05-settings',
    '06-dark-desktop', '07-long-press-menu', '08-right-click-menu', '09-application-info', '10-about',
    '11-internal-application', '12-permission-overlay', '13-saved-application', '14-background-desktop',
    '15-restored-application', '16-maximized-application', '17-restarted-desktop')
$report = [ordered]@{
    schema_version = 1
    task_id = 'T02'
    checkpoint = 'desktop_appearance_internal_game'
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
    checks = $checks
    screenshots = $screenshots
    input_actions = $inputActions
    unchanged_state_sha256 = $null
    sentinel_sha256 = $null
    standard_user_window = 'not_run'
    first_run = 'not_run'
    restart_data_preservation = 'not_run'
    about_dialog = 'not_run'
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
    sample_app_id = $null
    sample_version = $null
    automation_input = 'UI Automation patterns; scoped synthetic mouse press/right-click; no physical device assertion'
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
    $foreground = [AutumnDesktopSmoke.NativeWindows]::GetForegroundWindow()
    [uint32]$foregroundProcess = 0
    $null = [AutumnDesktopSmoke.NativeWindows]::GetWindowThreadProcessId($foreground, [ref]$foregroundProcess)
    return $foregroundProcess -eq $Product.Process.Id
}

function Focus-SmokeProduct($Product) {
    $null = [AutumnDesktopSmoke.NativeWindows]::SetForegroundWindow($Product.Handle)
    $null = Wait-SmokeCondition { Test-SmokeForeground $Product } 'the owned product foreground' 5
}

function Send-SmokeEscape($Product) {
    if (-not (Test-SmokeForeground $Product)) { throw 'Refusing keyboard input outside the owned product process.' }
    [AutumnDesktopSmoke.NativeWindows]::keybd_event(0x1B, 0, 0, [UIntPtr]::Zero)
    [AutumnDesktopSmoke.NativeWindows]::keybd_event(0x1B, 0, 2, [UIntPtr]::Zero)
    $inputActions.Add([ordered]@{ kind='synthetic_escape'; process_id=$Product.Process.Id })
    Start-Sleep -Milliseconds 150
}

function Invoke-SmokePointer($Product, [string]$AutomationId, [ValidateSet('long_press','right_click')][string]$Kind) {
    Focus-SmokeProduct $Product
    $element = Wait-SmokeCondition { Find-VisibleSmokeElement $Product.Element $AutomationId } "visible pointer target $AutomationId"
    $bounds = $element.Current.BoundingRectangle
    $windowBounds = $Product.Element.Current.BoundingRectangle
    if ($bounds.IsEmpty -or $bounds.Left -lt $windowBounds.Left -or $bounds.Top -lt $windowBounds.Top -or
        $bounds.Right -gt $windowBounds.Right -or $bounds.Bottom -gt $windowBounds.Bottom) { throw 'Pointer target is outside the owned window.' }
    $x = [int][Math]::Round(($bounds.Left + $bounds.Right) / 2)
    $y = [int][Math]::Round(($bounds.Top + $bounds.Bottom) / 2)
    $original = [AutumnDesktopSmoke.NativeWindows+POINT]::new()
    if (-not [AutumnDesktopSmoke.NativeWindows]::GetCursorPos([ref]$original)) { throw 'Cannot record cursor position.' }
    $downFlag = if ($Kind -eq 'long_press') { 0x0002 } else { 0x0008 }
    $upFlag = if ($Kind -eq 'long_press') { 0x0004 } else { 0x0010 }
    $pressed = $false
    try {
        if (-not [AutumnDesktopSmoke.NativeWindows]::SetCursorPos($x, $y)) { throw 'Cannot position pointer inside the owned window.' }
        $point = [AutumnDesktopSmoke.NativeWindows+POINT]::new(); $point.X = $x; $point.Y = $y
        [uint32]$targetProcess = 0
        $targetWindow = [AutumnDesktopSmoke.NativeWindows]::WindowFromPoint($point)
        $null = [AutumnDesktopSmoke.NativeWindows]::GetWindowThreadProcessId($targetWindow, [ref]$targetProcess)
        if ($targetProcess -ne $Product.Process.Id -or -not (Test-SmokeForeground $Product)) { throw 'Pointer hit-test is not owned by the product.' }
        [AutumnDesktopSmoke.NativeWindows]::mouse_event($downFlag, 0, 0, 0, [UIntPtr]::Zero)
        $pressed = $true
        # The product threshold is 600 ms. Hold for 750 ms to cross it without relying on scheduler timing.
        $heldMs = if ($Kind -eq 'long_press') { 750 } else { 80 }
        Start-Sleep -Milliseconds $heldMs
        [AutumnDesktopSmoke.NativeWindows]::mouse_event($upFlag, 0, 0, 0, [UIntPtr]::Zero)
        $pressed = $false
        $inputActions.Add([ordered]@{ kind=$Kind; automation_id=$AutomationId; held_ms=$heldMs; x=$x; y=$y; process_id=$Product.Process.Id; target_window=$targetWindow.ToInt64() })
    } finally {
        if ($pressed) { [AutumnDesktopSmoke.NativeWindows]::mouse_event($upFlag, 0, 0, 0, [UIntPtr]::Zero) }
        $null = [AutumnDesktopSmoke.NativeWindows]::SetCursorPos($original.X, $original.Y)
    }
}

function Wait-SmokeDesktop($Product) {
    $null = Wait-SmokeCondition {
        (Find-VisibleSmokeElement $Product.Element 'SampleButton') -and
        (Find-VisibleSmokeElement $Product.Element 'SettingsButton') -and
        (Find-VisibleSmokeElement $Product.Element 'DesktopProducer')
    } 'the desktop with real application icons'
}

function Read-SmokeEvents([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return @() }
    return @(Get-Content -LiteralPath $Path | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
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
    $process = Start-Process -FilePath $report.tested_executable -WorkingDirectory $WorkingDirectory -WindowStyle Normal -PassThru
    $ownedProcesses.Add($process)
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
    Add-SmokeCheck 'native_window_owned_by_started_process' ($element.Current.ProcessId -eq $process.Id -and [AutumnDesktopSmoke.NativeWindows]::IsWindowVisible($handle))
    return [pscustomobject]@{ Process = $process; Handle = $handle; Element = $element }
}

function Close-SmokeProduct($Product) {
    if (-not $Product.Process.HasExited) {
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
        Focus-SmokeProduct $Product
        Start-Sleep -Milliseconds 350
        $bounds = [AutumnDesktopSmoke.NativeWindows+RECT]::new()
        if (-not [AutumnDesktopSmoke.NativeWindows]::GetClientRect($Product.Handle, [ref]$bounds)) { throw 'Cannot read the product client bounds.' }
        $clientOrigin = [AutumnDesktopSmoke.NativeWindows+POINT]::new()
        if (-not [AutumnDesktopSmoke.NativeWindows]::ClientToScreen($Product.Handle, [ref]$clientOrigin)) { throw 'Cannot locate the product client area.' }
        $width = $bounds.Right - $bounds.Left
        $height = $bounds.Bottom - $bounds.Top
        if ($width -le 0 -or $height -le 0 -or $width -gt 10000 -or $height -gt 10000) { throw 'Invalid window capture bounds.' }
        $bitmap = [Drawing.Bitmap]::new($width, $height)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $printed = $false
        if (-not $IncludeOverlays) {
            $deviceContext = $graphics.GetHdc()
            # PW_CLIENTONLY | PW_RENDERFULLCONTENT excludes window shadows and the desktop beneath them.
            try { $printed = [AutumnDesktopSmoke.NativeWindows]::PrintWindow($Product.Handle, $deviceContext, 3) }
            finally { $graphics.ReleaseHdc($deviceContext) }
        }
        $captureMode = 'PrintWindow_CLIENTONLY_RENDERFULLCONTENT'
        if (-not $printed) {
            $null = Wait-SmokeCondition { Test-SmokeForeground $Product } 'product foreground for screenshot' 5
            # Native flyouts require visible pixels; capture only this process's client rectangle.
            # Never include the non-client shadow, another application, or the full desktop.
            $graphics.CopyFromScreen($clientOrigin.X, $clientOrigin.Y, 0, 0, [Drawing.Size]::new($width, $height))
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

    $trackedBuild = $null
    if (-not $ExecutablePath) {
        $latest = (Get-Content -LiteralPath (Join-Path $projectRoot 'artifacts/latest-build.txt') -Raw).Trim()
        if ($latest -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,90}$') { throw 'Invalid latest build identity.' }
        $trackedBuild = Get-Content -LiteralPath (Join-Path $projectRoot "artifacts/builds/$latest/build-result.json") -Raw | ConvertFrom-Json
        if ($trackedBuild.status -ne 'passed') { throw 'A successful Windows build is required.' }
        $ExecutablePath = Join-Path $projectRoot $trackedBuild.output_path
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

    $contractsAssembly = [Reflection.Assembly]::LoadFrom((Join-Path $stageDirectory 'AutumnOS.Contracts.dll'))
    $metadata = @{}
    foreach ($attribute in $contractsAssembly.GetCustomAttributesData()) {
        if ($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute') {
            $metadata[$attribute.ConstructorArguments[0].Value] = $attribute.ConstructorArguments[1].Value
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
            Add-SmokeCheck 'executable_matches_tracked_hash' ($expectedExecutable.Count -eq 1 -and $expectedExecutable[0].sha256 -eq $report.source_executable_sha256)
        } else { throw 'Tracked build is missing artifact-hashes.json.' }
    }

    $desktopRuntime = Join-Path $projectRoot '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach ($assemblyName in @('WindowsBase.dll', 'UIAutomationTypes.dll', 'UIAutomationClient.dll', 'System.Drawing.Common.dll')) {
        Add-Type -Path (Join-Path $desktopRuntime $assemblyName)
    }
    if (-not ('AutumnDesktopSmoke.NativeWindows' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace AutumnDesktopSmoke {
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
    $null = Save-SmokeScreenshot $first '01-hello'
    Invoke-SmokeButton $first.Element 'HelloNextButton'
    $null = Wait-SmokeCondition { Find-VisibleSmokeElement $first.Element 'BrandNextButton' } 'the AutumnOS brand stage'
    Add-SmokeCheck 'brand_stage_has_product_name' ((Find-SmokeElement $first.Element 'WelcomeBrand').Current.Name -eq $metadata['DisplayName'])
    $null = Save-SmokeScreenshot $first '02-brand'
    Invoke-SmokeButton $first.Element 'BrandNextButton'
    $null = Wait-SmokeCondition { Find-VisibleSmokeElement $first.Element 'PrepareDesktopButton' } 'the initialization stage'
    $null = Save-SmokeScreenshot $first '03-initialization'
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
    $null = Save-SmokeScreenshot $first '04-light-desktop'
    $report.first_run = 'passed'
    $report.unchanged_state_sha256 = (Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash.ToLowerInvariant()

    Invoke-SmokeButton $first.Element 'SettingsButton'
    $null = Wait-SmokeCondition { Find-VisibleSmokeElement $first.Element 'LightThemeButton' } 'native settings application'
    $null = Save-SmokeScreenshot $first '05-settings'
    Invoke-SmokeButton $first.Element 'SystemThemeButton'
    Invoke-SmokeButton $first.Element 'MistWallpaperButton'
    $null = Wait-SmokePreferences $preferencesPath 'system' 'mist'
    Add-SmokeCheck 'system_theme_and_mist_wallpaper_are_persisted' $true
    Invoke-SmokeButton $first.Element 'LightThemeButton'
    Invoke-SmokeButton $first.Element 'WarmWallpaperButton'
    $null = Wait-SmokePreferences $preferencesPath 'light' 'warm'
    Add-SmokeCheck 'light_theme_and_warm_wallpaper_are_persisted' $true
    Invoke-SmokeButton $first.Element 'DarkThemeButton'
    Invoke-SmokeButton $first.Element 'NightWallpaperButton'
    $darkPreferences = Wait-SmokePreferences $preferencesPath 'dark' 'night'
    Add-SmokeCheck 'dark_theme_and_night_wallpaper_are_persisted' ($darkPreferences.revision -ge 3)
    Add-SmokeCheck 'settings_reports_appearance_status' (-not [string]::IsNullOrWhiteSpace((Find-SmokeElement $first.Element 'PreferencesStatus').Current.Name))
    Invoke-SmokeButton $first.Element 'SettingsHomeButton'
    Wait-SmokeDesktop $first
    $darkAppearance = (Find-SmokeElement $first.Element 'DesktopAppearance').Current.Name
    Add-SmokeCheck 'desktop_reports_selected_dark_appearance' ($darkAppearance -match '深色' -and $darkAppearance -match '暮色')
    $null = Save-SmokeScreenshot $first '06-dark-desktop'
    $preferencesHash = (Get-FileHash -LiteralPath $preferencesPath -Algorithm SHA256).Hash.ToLowerInvariant()

    Invoke-SmokePointer $first 'SampleButton' 'long_press'
    $null = Wait-SmokeCondition { Find-SmokeMenuItem $first 'SampleMenuInfo' @('应用信息') } 'the native long-press application menu'
    Add-SmokeCheck 'synthetic_750ms_press_opens_native_context_menu' $true 'Real left-button down/up targeted at SampleButton; physical touch remains not_run.'
    $null = Save-SmokeScreenshot $first '07-long-press-menu' -IncludeOverlays
    Send-SmokeEscape $first
    $null = Wait-SmokeCondition { -not (Find-SmokeMenuItem $first 'SampleMenuInfo' @('应用信息')) } 'long-press menu dismissed with Escape'
    Add-SmokeCheck 'long_press_does_not_launch_the_game' (-not (Test-Path -LiteralPath $runtimeLog))
    $report.long_press = 'passed'

    Invoke-SmokePointer $first 'SampleButton' 'right_click'
    $null = Wait-SmokeCondition { Find-SmokeMenuItem $first 'SampleMenuInfo' @('应用信息') } 'the native right-click application menu'
    Add-SmokeCheck 'right_click_opens_native_context_menu' $true
    $null = Save-SmokeScreenshot $first '08-right-click-menu' -IncludeOverlays
    Invoke-SmokeMenuItem $first 'SampleMenuInfo' @('应用信息')
    $null = Wait-SmokeCondition { Find-SmokeNamedElement $first.Element '关闭' ([System.Windows.Automation.ControlType]::Button) } 'application information dialog'
    $null = Save-SmokeScreenshot $first '09-application-info'
    $textCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $texts = $first.Element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
    $information = @($texts | ForEach-Object { $_.Current.Name }) -join "`n"
    Add-SmokeCheck 'application_information_uses_installed_identity' ($information.Contains($sample.appId) -and $information.Contains($sample.version))
    Invoke-SmokeNamedButton $first.Element '关闭'
    $report.right_click_menu = 'passed'

    Invoke-SmokeButton $first.Element 'DockSettingsButton'
    Invoke-SmokeButton $first.Element 'SettingsAboutButton'
    $null = Wait-SmokeCondition {
        $texts = $first.Element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
        foreach ($textElement in $texts) {
            $name = $textElement.Current.Name
            if ($name -and $name.Contains($metadata['ProducerCredit']) -and $name.Contains('C# / .NET 10')) { return $true }
        }
        return $false
    } 'the About dialog with producer credit and native framework'
    $null = Save-SmokeScreenshot $first '10-about'
    Add-SmokeCheck 'about_dialog_preserves_central_brand_and_native_framework' $true
    Invoke-SmokeNamedButton $first.Element '关闭'
    $report.about_dialog = 'passed'
    Invoke-SmokeButton $first.Element 'DiagnosticsButton'
    $null = Wait-SmokeCondition { Find-SmokeElement $first.Element 'CheckpointText' } 'retained developer diagnostics'
    Add-SmokeCheck 'diagnostics_preserves_product_and_producer' ((Find-SmokeElement $first.Element 'ProductTitle').Current.Name -eq $metadata['DisplayName'] -and
        (Find-SmokeElement $first.Element 'ProducerCredit').Current.Name -eq $metadata['ProducerCredit'])
    Add-SmokeCheck 'diagnostics_does_not_repeat_completed_initialization' (-not (Find-SmokeElement $first.Element 'InitializeButton').Current.IsEnabled)
    Invoke-SmokeButton $first.Element 'DiagnosticsBackButton'
    Invoke-SmokeButton $first.Element 'SettingsHomeButton'
    Wait-SmokeDesktop $first

    # Exercise the context menu Open action, then return and restore via the actual desktop icon.
    Invoke-SmokePointer $first 'SampleButton' 'right_click'
    Invoke-SmokeMenuItem $first 'SampleMenuOpen' @('打开', '打开应用', '打开元素配对')
    $null = Wait-SmokeCondition {
        $state = Find-VisibleSmokeElement $first.Element 'RuntimeStatus'
        $state -and $state.Current.Name -match '前台运行'
    } 'internal WebView2 game launch from the desktop menu'
    $null = Wait-SmokeCondition { @(Read-SmokeEvents $runtimeLog | Where-Object { $_.state -eq 'Foreground' }).Count -gt 0 } 'actual foreground runtime event'
    $firstInstance = @(Read-SmokeEvents $runtimeLog | Where-Object { $_.state -eq 'Foreground' })[-1].instanceId
    Add-SmokeCheck 'desktop_menu_opens_real_internal_application' (-not [string]::IsNullOrWhiteSpace($firstInstance))
    $null = Save-SmokeScreenshot $first '11-internal-application'
    $nickname = Wait-SmokeCondition { Find-SmokeNamedElement $first.Element '玩家昵称' ([System.Windows.Automation.ControlType]::Edit) } 'game nickname input exposed by native accessibility'
    $nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('派蒙·桌面验证')
    Invoke-SmokeNamedButton $first.Element '第 1 张卡片，未翻开'
    Invoke-SmokeNamedButton $first.Element '第 2 张卡片，未翻开'
    Invoke-SmokeNamedButton $first.Element '保存进度'
    $null = Wait-SmokeCondition { Find-SmokeNamedElement $first.Element '允许' ([System.Windows.Automation.ControlType]::Button) } 'native permission overlay'
    $null = Save-SmokeScreenshot $first '12-permission-overlay'
    Invoke-SmokeNamedButton $first.Element '允许'
    $null = Wait-SmokeCondition { Test-Path -LiteralPath $savePath } 'a real guest save committed through the SDK'
    $save = Get-Content -LiteralPath $savePath -Raw | ConvertFrom-Json
    Add-SmokeCheck 'guest_save_contains_unicode_input_and_card_progress' ($save.appId -eq $sample.appId -and $save.value.nickname -eq '派蒙·桌面验证' -and $save.value.moves -ge 1)
    $saveHash = (Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $null = Save-SmokeScreenshot $first '13-saved-application'
    Invoke-SmokeButton $first.Element 'BackgroundButton'
    Wait-SmokeDesktop $first
    $running = Wait-SmokeCondition { Find-VisibleSmokeElement $first.Element 'RunningTitle' } 'the live background-game card'
    Add-SmokeCheck 'background_card_shows_real_game' ($running.Current.Name -match '元素配对')
    $events = Read-SmokeEvents $runtimeLog
    Add-SmokeCheck 'background_game_keeps_same_instance_and_blocks_maintenance' (@($events | Where-Object { $_.instanceId -eq $firstInstance -and $_.state -eq 'Background' -and $_.blocksMaintenance }).Count -gt 0)
    $null = Save-SmokeScreenshot $first '14-background-desktop'
    Invoke-SmokeButton $first.Element 'SampleButton'
    $null = Wait-SmokeCondition { (Find-VisibleSmokeElement $first.Element 'RuntimeStatus').Current.Name -match '前台运行' } 'same game instance restored by its desktop icon'
    $events = Read-SmokeEvents $runtimeLog
    $latestForeground = @($events | Where-Object { $_.state -eq 'Foreground' })[-1]
    Add-SmokeCheck 'desktop_icon_resumes_same_runtime_instance' ($latestForeground.instanceId -eq $firstInstance)
    $nickname = Wait-SmokeCondition { Find-SmokeNamedElement $first.Element '玩家昵称' ([System.Windows.Automation.ControlType]::Edit) } 'restored nickname input'
    Add-SmokeCheck 'background_resume_preserves_unsaved_input_and_saved_bytes' ($nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '派蒙·桌面验证' -and
        (Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $saveHash)
    $report.same_instance_resume = 'passed'
    $nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('临时修改')
    Invoke-SmokeNamedButton $first.Element '读取存档'
    $null = Wait-SmokeCondition { $nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '派蒙·桌面验证' } 'the saved nickname restored through the SDK'
    Add-SmokeCheck 'read_save_does_not_rewrite_committed_bytes' ((Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $saveHash)
    $null = Save-SmokeScreenshot $first '15-restored-application'
    $null = [AutumnDesktopSmoke.NativeWindows]::ShowWindow($first.Handle, 3)
    $null = Wait-SmokeCondition { [AutumnDesktopSmoke.NativeWindows]::IsZoomed($first.Handle) } 'game window maximization'
    $null = Save-SmokeScreenshot $first '16-maximized-application'
    $null = [AutumnDesktopSmoke.NativeWindows]::ShowWindow($first.Handle, 9)
    $null = Wait-SmokeCondition { -not [AutumnDesktopSmoke.NativeWindows]::IsZoomed($first.Handle) } 'game window restoration'
    $report.maximize_restore = 'passed'

    # Closing a background game through its icon menu must end the actual existing session.
    Invoke-SmokeButton $first.Element 'BackgroundButton'
    Wait-SmokeDesktop $first
    Invoke-SmokePointer $first 'SampleButton' 'right_click'
    Invoke-SmokeMenuItem $first 'SampleMenuClose' @('结束游戏')
    Invoke-SmokeNamedButton $first.Element '确认结束'
    $null = Wait-SmokeCondition {
        @(Read-SmokeEvents $runtimeLog | Where-Object { $_.instanceId -eq $firstInstance -and $_.state -eq 'Closed' -and -not $_.blocksMaintenance }).Count -gt 0
    } 'the background game actually closed and released its runtime block'
    Wait-SmokeDesktop $first
    Add-SmokeCheck 'background_menu_close_removes_running_card' ($null -eq (Find-VisibleSmokeElement $first.Element 'RunningTitle'))
    Invoke-SmokeButton $first.Element 'DockSampleButton'
    $null = Wait-SmokeCondition { (Find-VisibleSmokeElement $first.Element 'RuntimeStatus').Current.Name -match '前台运行' } 'new application instance launched from the Dock'
    $secondInstance = @(Read-SmokeEvents $runtimeLog | Where-Object { $_.state -eq 'Foreground' })[-1].instanceId
    Add-SmokeCheck 'dock_launch_after_close_creates_new_instance' ($secondInstance -ne $firstInstance)
    Invoke-SmokeNamedButton $first.Element '读取存档'
    Invoke-SmokeNamedButton $first.Element '允许'
    $nickname = Wait-SmokeCondition { Find-SmokeNamedElement $first.Element '玩家昵称' ([System.Windows.Automation.ControlType]::Edit) } 'new instance nickname input'
    $null = Wait-SmokeCondition { $nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '派蒙·桌面验证' } 'new instance restores the earlier guest save'
    Add-SmokeCheck 'new_instance_reopens_saved_progress_without_rewriting' ((Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $saveHash)
    Invoke-SmokeButton $first.Element 'CloseGameButton'
    Invoke-SmokeNamedButton $first.Element '确认结束'
    $null = Wait-SmokeCondition { @(Read-SmokeEvents $runtimeLog | Where-Object { $_.instanceId -eq $secondInstance -and $_.state -eq 'Closed' }).Count -gt 0 } 'the second actual game session ended'
    Wait-SmokeDesktop $first
    $report.internal_application = 'passed'
    Close-SmokeProduct $first

    $sentinelPath = Join-Path $dataDirectory 'Saves/smoke-existing-save.bin'
    $sentinelBytes = [Text.Encoding]::UTF8.GetBytes("AutumnOS desktop smoke preserved save | $smokeId | 制作人：派蒙")
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
    Add-SmokeCheck 'different_working_directories_receive_no_user_data' (-not (Test-Path -LiteralPath (Join-Path $firstWorkingDirectory 'AutumnOS_Data')) -and -not (Test-Path -LiteralPath (Join-Path $secondWorkingDirectory 'AutumnOS_Data')))
    $expectedDirectories = @('Apps', 'Packages', 'Downloads', 'Cache', 'AppData', 'Saves', 'Screenshots', 'Config', 'Logs', 'Runtime', 'Temp', 'Updates')
    $missingDirectories = @($expectedDirectories | Where-Object { -not (Test-Path -LiteralPath (Join-Path $dataDirectory $_) -PathType Container) })
    Add-SmokeCheck 'real_app_created_all_twelve_data_directories' ($missingDirectories.Count -eq 0)
    $null = Save-SmokeScreenshot $second '17-restarted-desktop'
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
    $capturedCount = @($screenshots | Where-Object { $_.status -eq 'captured' }).Count
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
