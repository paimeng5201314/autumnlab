[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [string]$ReportDirectory,
    [switch]$ExerciseRuntime,
    [ValidateRange(5, 90)][int]$WindowTimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$smokeId = $(if ($ExerciseRuntime) { 'T01-smoke-' } else { 'T00-smoke-' }) + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$scope = Join-Path $projectRoot "artifacts/smoke/$smokeId"
$stageDirectory = Join-Path $scope '中文 空格 目录'
if (-not $ReportDirectory) { $ReportDirectory = $scope }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$reportPath = Join-Path $ReportDirectory 'windows-smoke.json'
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$checks = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[object]]::new()
$report = [ordered]@{
    schema_version = 1
    task_id = 'T00'
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
    input_method_editor = 'not_run'
    physical_touch = 'not_run'
    network_sandbox = 'not_run'
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

function Invoke-SmokeButton($WindowElement, [string]$AutomationId) {
    $element = Find-SmokeElement $WindowElement $AutomationId
    if (-not $element -or -not $element.Current.IsEnabled) { throw "UI button is missing or disabled: $AutomationId" }
    $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Find-SmokeNamedElement($WindowElement, [string]$Name, $ControlType) {
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, $Name),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, $ControlType),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::IsEnabledProperty, $true))
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
        $title = Find-SmokeElement $element 'ProductTitle'
        $credit = Find-SmokeElement $element 'ProducerCredit'
        $checkpoint = Find-SmokeElement $element 'CheckpointText'
        if ($title -and $credit -and $checkpoint -and $checkpoint.Current.Name) { return $true }
        return $false
    } 'the populated native UI'
    Add-SmokeCheck 'native_window_owned_by_started_process' ($element.Current.ProcessId -eq $process.Id -and [AutumnSmoke.NativeWindows]::IsWindowVisible($handle))
    return [pscustomobject]@{ Process = $process; Handle = $handle; Element = $element }
}

function Close-SmokeProduct($Product) {
    if (-not $Product.Process.HasExited) {
        $closed = $Product.Process.CloseMainWindow()
        if (-not $closed -or -not $Product.Process.WaitForExit(10000)) { throw 'The recorded product window did not close cleanly.' }
    }
    Add-SmokeCheck 'recorded_product_process_closed_cleanly' ($Product.Process.ExitCode -eq 0)
}

function Save-SmokeScreenshot($Product, [string]$Name) {
    $path = Join-Path $ReportDirectory "$Name.png"
    $bitmap = $null
    $graphics = $null
    try {
        $null = [AutumnSmoke.NativeWindows]::SetForegroundWindow($Product.Handle)
        Start-Sleep -Milliseconds 350
        $bounds = [AutumnSmoke.NativeWindows+RECT]::new()
        if (-not [AutumnSmoke.NativeWindows]::GetWindowRect($Product.Handle, [ref]$bounds)) { throw 'Cannot read the product window bounds.' }
        $width = $bounds.Right - $bounds.Left
        $height = $bounds.Bottom - $bounds.Top
        if ($width -le 0 -or $height -le 0 -or $width -gt 10000 -or $height -gt 10000) { throw 'Invalid window capture bounds.' }
        $bitmap = [Drawing.Bitmap]::new($width, $height)
        $graphics = [Drawing.Graphics]::FromImage($bitmap)
        $deviceContext = $graphics.GetHdc()
        try { $printed = [AutumnSmoke.NativeWindows]::PrintWindow($Product.Handle, $deviceContext, 2) }
        finally { $graphics.ReleaseHdc($deviceContext) }
        $captureMode = 'PrintWindow_PW_RENDERFULLCONTENT'
        if (-not $printed) {
            $null = Wait-SmokeCondition { [AutumnSmoke.NativeWindows]::GetForegroundWindow() -eq $Product.Handle } 'product foreground for screenshot' 5
            # Fallback captures only the foreground product window rectangle, never the full desktop.
            $graphics.CopyFromScreen($bounds.Left, $bounds.Top, 0, 0, [Drawing.Size]::new($width, $height))
            $captureMode = 'foreground_window_rectangle'
        }
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        $screenshots.Add([ordered]@{ name = $Name; status = 'captured'; path = $path; width = $width; height = $height; process_id = $Product.Process.Id; capture_mode = $captureMode })
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
    if (-not ('AutumnSmoke.NativeWindows' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace AutumnSmoke {
    public static class NativeWindows {
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool PrintWindow(IntPtr hwnd, IntPtr deviceContext, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(IntPtr hwnd, int command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsZoomed(IntPtr hwnd);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr hwnd);
    }
}
'@
    }

    $firstWorkingDirectory = Join-Path $scope 'first-working-directory'
    $secondWorkingDirectory = Join-Path $scope 'second-working-directory'
    New-Item -ItemType Directory -Path $firstWorkingDirectory, $secondWorkingDirectory -Force | Out-Null
    $report.working_directories = @($firstWorkingDirectory, $secondWorkingDirectory)
    $first = Start-SmokeProduct $firstWorkingDirectory
    $report.standard_user_window = 'passed'
    Add-SmokeCheck 'native_product_title' ((Find-SmokeElement $first.Element 'ProductTitle').Current.Name -eq $metadata['DisplayName'])
    Add-SmokeCheck 'central_producer_credit_visible' ((Find-SmokeElement $first.Element 'ProducerCredit').Current.Name -eq $metadata['ProducerCredit'])
    $null = Save-SmokeScreenshot $first '01-native-window'
    Add-SmokeCheck 'initialization_button_is_enabled_on_first_run' (Find-SmokeElement $first.Element 'InitializeButton').Current.IsEnabled
    Invoke-SmokeButton $first.Element 'InitializeButton'
    $statePath = Join-Path $dataDirectory 'Config/first-run.json'
    $null = Wait-SmokeCondition {
        if (-not (Test-Path -LiteralPath $statePath)) { return $false }
        $state = Get-Content -LiteralPath $statePath -Raw | ConvertFrom-Json
        return $state.checkpoint -eq 'Completed'
    } 'a committed Completed first-run checkpoint'
    Add-SmokeCheck 'first_run_saved_completed_checkpoint' ((Find-SmokeElement $first.Element 'CheckpointText').Current.Name -match '完成')
    Add-SmokeCheck 'initialization_button_disabled_after_completion' (-not (Find-SmokeElement $first.Element 'InitializeButton').Current.IsEnabled)
    $report.first_run = 'passed'
    $report.unchanged_state_sha256 = (Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Close-SmokeProduct $first

    $sentinelPath = Join-Path $dataDirectory 'Saves/smoke-existing-save.bin'
    $sentinelBytes = [Text.Encoding]::UTF8.GetBytes("AutumnOS smoke preserved save | $smokeId | 制作人：派蒙")
    [IO.File]::WriteAllBytes($sentinelPath, $sentinelBytes)
    $report.sentinel_sha256 = (Get-FileHash -LiteralPath $sentinelPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $second = Start-SmokeProduct $secondWorkingDirectory
    Add-SmokeCheck 'restart_preserves_existing_save_bytes' ((Get-FileHash -LiteralPath $sentinelPath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $report.sentinel_sha256)
    Add-SmokeCheck 'restart_preserves_first_run_state_bytes' ((Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $report.unchanged_state_sha256)
    Add-SmokeCheck 'restart_does_not_repeat_first_run' (-not (Find-SmokeElement $second.Element 'InitializeButton').Current.IsEnabled)
    Add-SmokeCheck 'different_working_directories_receive_no_user_data' (-not (Test-Path -LiteralPath (Join-Path $firstWorkingDirectory 'AutumnOS_Data')) -and -not (Test-Path -LiteralPath (Join-Path $secondWorkingDirectory 'AutumnOS_Data')))
    $expectedDirectories = @('Apps', 'Packages', 'Downloads', 'Cache', 'AppData', 'Saves', 'Screenshots', 'Config', 'Logs', 'Runtime', 'Temp', 'Updates')
    $missingDirectories = @($expectedDirectories | Where-Object { -not (Test-Path -LiteralPath (Join-Path $dataDirectory $_) -PathType Container) })
    Add-SmokeCheck 'all_twelve_data_directories_created_by_real_app' ($missingDirectories.Count -eq 0)
    $report.restart_data_preservation = 'passed'

    $null = [AutumnSmoke.NativeWindows]::ShowWindow($second.Handle, 3)
    $null = Wait-SmokeCondition { [AutumnSmoke.NativeWindows]::IsZoomed($second.Handle) } 'window maximization'
    Add-SmokeCheck 'maximize_keeps_producer_visible' ((Find-SmokeElement $second.Element 'ProducerCredit').Current.Name -eq $metadata['ProducerCredit'])
    $null = Save-SmokeScreenshot $second '02-maximized-window'
    $null = [AutumnSmoke.NativeWindows]::ShowWindow($second.Handle, 9)
    $null = Wait-SmokeCondition { -not [AutumnSmoke.NativeWindows]::IsZoomed($second.Handle) } 'window restoration'
    Add-SmokeCheck 'restore_preserves_first_run_state' ((Get-FileHash -LiteralPath $statePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $report.unchanged_state_sha256)
    $report.maximize_restore = 'passed'

    Invoke-SmokeButton $second.Element 'AboutButton'
    $textCondition = [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Text)
    $null = Wait-SmokeCondition {
        $texts = $second.Element.FindAll([System.Windows.Automation.TreeScope]::Descendants, $textCondition)
        foreach ($textElement in $texts) {
            $name = $textElement.Current.Name
            if ($name -and $name.Contains($metadata['ProducerCredit']) -and $name.Contains('C# / .NET 10')) { return $true }
        }
        return $false
    } 'the About dialog with producer credit and native framework'
    $null = Save-SmokeScreenshot $second '03-about-dialog'
    Add-SmokeCheck 'about_dialog_contains_central_producer_credit' $true
    $closeCondition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Button),
        [System.Windows.Automation.PropertyCondition]::new([System.Windows.Automation.AutomationElement]::NameProperty, '关闭'))
    $closeButton = $second.Element.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $closeCondition)
    if (-not $closeButton) { throw 'About dialog close button was not found.' }
    $closeButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $report.about_dialog = 'passed'
    if ($ExerciseRuntime) {
        $report.task_id = 'T01'
        Invoke-SmokeButton $second.Element 'SampleButton'
        $null = Wait-SmokeCondition {
            $state = Find-SmokeElement $second.Element 'RuntimeStatus'
            return $state -and $state.Current.Name -match '前台运行'
        } 'internal WebView2 navigation'
        $runtimeLog = Join-Path $dataDirectory 'Logs/runtime-events.jsonl'
        $null = Wait-SmokeCondition { Test-Path -LiteralPath $runtimeLog } 'runtime audit events'
        $null = Save-SmokeScreenshot $second '04-internal-application'
        Add-SmokeCheck 'real_autumn_package_committed_before_launch' (Test-Path -LiteralPath (Join-Path $dataDirectory 'Apps/cn.labchronicles.elementpairs/0.1.0/.autumnos-install.json'))
        $nickname = Wait-SmokeCondition { Find-SmokeNamedElement $second.Element '玩家昵称' ([System.Windows.Automation.ControlType]::Edit) } 'HTML input exposed through native accessibility'
        $nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('派蒙·本地验证')
        Invoke-SmokeNamedButton $second.Element '第 1 张卡片，未翻开'
        Invoke-SmokeNamedButton $second.Element '第 2 张卡片，未翻开'
        Invoke-SmokeNamedButton $second.Element '保存进度'
        $null = Wait-SmokeCondition { Find-SmokeNamedElement $second.Element '允许' ([System.Windows.Automation.ControlType]::Button) } 'native permission overlay above the game'
        $null = Save-SmokeScreenshot $second '07-permission-overlay'
        Invoke-SmokeNamedButton $second.Element '允许'
        $savePath = Join-Path $dataDirectory 'Saves/cn.labchronicles.elementpairs/guest/game.json'
        $null = Wait-SmokeCondition { Test-Path -LiteralPath $savePath } 'SDK save committed on disk'
        Add-SmokeCheck 'guest_save_contains_real_unicode_input' ((Get-Content -LiteralPath $savePath -Raw).Contains('派蒙') -or ((Get-Content -LiteralPath $savePath -Raw | ConvertFrom-Json).value.nickname -eq '派蒙·本地验证'))
        Add-SmokeCheck 'card_interactions_persist_real_game_progress' ((Get-Content -LiteralPath $savePath -Raw | ConvertFrom-Json).value.moves -ge 1)
        $saveHash = (Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash
        $null = Save-SmokeScreenshot $second '05-saved-application'
        $null = [AutumnSmoke.NativeWindows]::ShowWindow($second.Handle, 3)
        $null = Wait-SmokeCondition { [AutumnSmoke.NativeWindows]::IsZoomed($second.Handle) } 'internal game maximization'
        $null = Save-SmokeScreenshot $second '08-maximized-application'
        $null = [AutumnSmoke.NativeWindows]::ShowWindow($second.Handle, 9)
        $null = Wait-SmokeCondition { -not [AutumnSmoke.NativeWindows]::IsZoomed($second.Handle) } 'internal game restoration'
        Invoke-SmokeButton $second.Element 'BackgroundButton'
        Add-SmokeCheck 'background_game_remains_live' ((Find-SmokeElement $second.Element 'SessionSummary').Current.Name -match '后台运行')
        $events = @(Get-Content -LiteralPath $runtimeLog | ForEach-Object { $_ | ConvertFrom-Json })
        Add-SmokeCheck 'background_game_blocks_maintenance_in_runtime' (@($events | Where-Object { $_.state -eq 'Background' -and $_.blocksMaintenance }).Count -gt 0)
        Invoke-SmokeButton $second.Element 'SampleButton'
        $null = Wait-SmokeCondition { (Find-SmokeElement $second.Element 'RuntimeStatus').Current.Name -match '前台运行' } 'foreground restoration'
        $nickname = Find-SmokeNamedElement $second.Element '玩家昵称' ([System.Windows.Automation.ControlType]::Edit)
        Add-SmokeCheck 'background_resume_preserves_input' ($nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '派蒙·本地验证')
        $nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue('临时修改')
        Invoke-SmokeNamedButton $second.Element '读取存档'
        $null = Wait-SmokeCondition { $nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '派蒙·本地验证' } 'saved nickname restored through SDK'
        Add-SmokeCheck 'load_restores_saved_input_without_rewriting_save' ((Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash -eq $saveHash)
        Invoke-SmokeButton $second.Element 'CloseGameButton'
        Invoke-SmokeNamedButton $second.Element '确认结束'
        $null = Wait-SmokeCondition { (Find-SmokeElement $second.Element 'SessionSummary').Current.Name -eq '无游戏会话' } 'actual game closure'
        $events = @(Get-Content -LiteralPath $runtimeLog | ForEach-Object { $_ | ConvertFrom-Json })
        Add-SmokeCheck 'closed_game_releases_maintenance_block' (@($events | Where-Object { $_.state -eq 'Closed' -and -not $_.blocksMaintenance }).Count -gt 0)
        Invoke-SmokeButton $second.Element 'SampleButton'
        $null = Wait-SmokeCondition { (Find-SmokeElement $second.Element 'RuntimeStatus').Current.Name -match '前台运行' } 'new runtime instance'
        Invoke-SmokeNamedButton $second.Element '读取存档'
        Invoke-SmokeNamedButton $second.Element '允许'
        $nickname = Wait-SmokeCondition { Find-SmokeNamedElement $second.Element '玩家昵称' ([System.Windows.Automation.ControlType]::Edit) } 'restarted HTML input'
        $null = Wait-SmokeCondition { $nickname.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '派蒙·本地验证' } 'save restored in a new application instance'
        Add-SmokeCheck 'new_instance_reopens_real_saved_progress' ((Get-FileHash -LiteralPath $savePath -Algorithm SHA256).Hash -eq $saveHash)
        $null = Save-SmokeScreenshot $second '06-restored-application'
        Invoke-SmokeButton $second.Element 'CloseGameButton'
        Invoke-SmokeNamedButton $second.Element '确认结束'
        $null = Wait-SmokeCondition { (Find-SmokeElement $second.Element 'SessionSummary').Current.Name -eq '无游戏会话' } 'restarted game closure'
        $report.internal_application = 'passed'
    }
    Close-SmokeProduct $second
    $report.status = 'passed'
} catch {
    $report.failure_type = $_.Exception.GetType().FullName
    $report.failure = $_.Exception.Message
    Write-Warning "Windows smoke failed: $($report.failure)"
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
                }
            }
        } catch {
            $checks.Add([ordered]@{ name = 'cleanup_recorded_process'; status = 'failed'; process_id = $process.Id; detail = $_.Exception.Message })
            $report.status = 'failed'
        } finally { $process.Dispose() }
    }
    $capturedCount = @($screenshots | Where-Object { $_.status -eq 'captured' }).Count
    $expectedScreenshotCount = if ($ExerciseRuntime) { 8 } else { 3 }
    if ($capturedCount -eq $expectedScreenshotCount) { $report.screenshot_status = 'captured_not_visually_reviewed' }
    elseif ($capturedCount -gt 0) { $report.screenshot_status = 'partially_captured_not_visually_reviewed' }
    $report.finished_utc = [DateTimeOffset]::UtcNow.ToString('o')
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "REPORT $reportPath"
}
if ($report.status -ne 'passed') { throw "Windows smoke verification failed. Evidence preserved at $reportPath" }
