#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [string]$TestRoot,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]{0,90}$')][string]$SingleFileBuildId,
    [ValidateRange(15,90)][int]$WindowTimeoutSeconds = 45,
    [string]$RepositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..')), 
    [switch]$ManualPicker
)

# REVIEW DRAFT. Ordinary local-package flow only. No developer preview, test Store, CLI runtime commands,
# registry/config injection, preinstalled package copy, or forced process termination.
$ErrorActionPreference = 'Stop'
$helperRoot = Join-Path ([IO.Path]::GetFullPath($RepositoryRoot)) 'scripts'
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
if (-not $TestRoot) { $TestRoot = Join-Path ([IO.Path]::GetTempPath()) ('aosc-' + [Guid]::NewGuid().ToString('N').Substring(0,8)) }
$TestRoot = [IO.Path]::GetFullPath($TestRoot)
if ((Test-Path -LiteralPath $ReportDirectory) -or (Test-Path -LiteralPath $TestRoot)) { throw 'ReportDirectory and TestRoot must both be new; earlier evidence is retained.' }
if ($ReportDirectory -match '[\\/]AutumnOS_Data(?:[\\/]|$)' -or $TestRoot -match '[\\/]AutumnOS_Data(?:[\\/]|$)') { throw 'Test output cannot be placed in user data.' }
if ($TestRoot.Length -gt 100) { throw 'Use a new short TestRoot (100 characters or fewer) for native WebView2 runtime paths.' }
. (Join-Path $helperRoot 'Smoke-NativeAppInput.ps1')
. (Join-Path $helperRoot 'T06-NativeHelpers.ps1')
$desktopAst = Import-T06Functions (Join-Path $helperRoot 'Test-DesktopInteractionSmoke.ps1')
$script:t05LegacyFocus = (Get-Item Function:Focus-SmokeProduct).ScriptBlock
$null = Import-T06Functions (Join-Path $helperRoot 'Test-T05UpdateSmoke.ps1') -Names @(
    'Assert-T05OwnedWindow','Get-T05WindowGeometry','Focus-SmokeProduct','Initialize-T05FocusNative',
    'Assert-T05PlainPath','Invoke-T05SharingRead','Get-T05Hash')
$null = Import-T06Functions (Join-Path $helperRoot 'Test-T04StoreSmoke.ps1') -Names @(
    'Show-Element','Click','Find','Visible','Require','Toggle','Click-Named','Value','Failure-Buttons')
$null = Import-T06Functions (Join-Path $helperRoot 'Test-ChemLeapNativeSmoke.ps1') -Names @(
    'Copy-ChemLeapPlainTree','Get-ChemLeapScopes','Find-ChemLeapElement','Click-ChemLeap',
    'Get-ChemLeapScore','Get-ChemLeapSubstance','Wait-ChemLeapStatus','Read-ChemLeapRuntimeEvents')
if ($SingleFileBuildId) {
    # Import the maintained exact entry -> inner bootstrap -> WinUI child tracker.
    # Its process handles, parentage, path, start-time and Windows-session checks stay intact.
    $null = Import-T06Functions (Join-Path $helperRoot 'Test-SingleFileSmoke.ps1') -Names @('Start-SmokeProduct')
    $WindowTimeoutSeconds = 90
}
$checks = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[object]]::new()
$inputActions = [Collections.Generic.List[object]]::new()
$windowGeometry = [Collections.Generic.List[object]]::new()
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$ownedEntryProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$trackedProcesses = [Collections.Generic.List[object]]::new()
$modeObservations = [Collections.Generic.List[object]]::new()
$script:initialPointerPosition = $null
$script:product = $null
$script:preparationWindowCaptured = $false
$script:stageDirectory = Join-Path $TestRoot 'A'
$dataDirectory = Join-Path $stageDirectory 'AutumnOS_Data'
$runtimeLog = Join-Path $dataDirectory 'Logs/runtime-events.jsonl'
$cwd = Join-Path $TestRoot 'cwd'
$expectedHash = $null
$appId = 'cn.labchronicles.normallocal'
$fixtureRoot = Join-Path $RepositoryRoot 'tests/fixtures/normal-local'
$fixturePackage = Join-Path $TestRoot 'NormalLocal-1.0.0.autumn'
$singleFilePackage = $null
$singleFileBuild = $null
$programDirectory = $stageDirectory
$report = [ordered]@{
    schema_version=1; checkpoint='ordinary_local_package_native_gameplay'; status='failed'
    started_utc=[DateTimeOffset]::UtcNow.ToString('o'); finished_utc=$null
    source_executable=$ExecutablePath; tested_executable=(Join-Path $stageDirectory 'AutumnOS.exe'); isolated_root=$TestRoot
    input_mode=$(if($SingleFileBuildId){'single_file_only'}else{'ordinary_complete_directory'}); single_file_build_id=$SingleFileBuildId
    driver_sha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    build_id='not_recorded'; source_snapshot_id='not_recorded'; imported_helpers_sha256=@{}
    app_id=$appId; source_binding='local:cn.labchronicles.normallocal'; compared_source_binding='github:1414175562'
    version='1.0.0'; expected_package_sha256=$expectedHash; expected_package_bytes=0
    source='real_local_file_picker'; developer_mode='never_enabled'; developer_mode_observations=$modeObservations
    public_store='not_run'; local_picker='not_run'; local_install='not_run'; background_resume='not_run'
    normal_launch='not_run'; native_gameplay='not_run'; sdk_save='not_run'; background_continue='not_run'; new_instance_readback='not_run'
    external_publication='not_performed'; real_logto='not_run'; physical_input='not_run'; visual_review='not_run'
    checks=$checks; screenshots=$screenshots; input_actions=$inputActions; window_geometry=$windowGeometry
    process_ids=@(); bootstrap_process_ids=@(); remaining_processes=@(); cleanup='not_run'; isolated_test_data_retained=$true
    startup_observations=@(); single_file_preparation='not_applicable'; single_file_bootstrap_exit_codes=$null
}

function Read-NormalLocalHostMetadata([string]$ProgramRoot) {
    Assert-T05PlainPath $ProgramRoot
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $ProgramRoot 'AutumnOS.Contracts.dll')))
    foreach ($attribute in $assembly.GetCustomAttributesData()) {
        if ($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute') {
            $key=$attribute.ConstructorArguments[0].Value; $value=$attribute.ConstructorArguments[1].Value
            if ($key -eq 'BuildId') { $report.build_id=$value }; if ($key -eq 'SourceSnapshotId') { $report.source_snapshot_id=$value }
        }
    }
    if ($SingleFileBuildId) {
        Add-SmokeCheck 'extracted_client_matches_single_file_build_and_actual_source' (
            $report.build_id -eq $SingleFileBuildId -and $report.source_snapshot_id -eq $singleFilePackage.source_snapshot_id)
    }
}
function Assert-NormalLocalSingleFileRoot([string]$Phase) {
    $items = @(Get-ChildItem -LiteralPath $stageDirectory -Force)
    Add-SmokeCheck ($Phase+'_single_file_root_contains_only_exe_and_one_data_folder') (
        $items.Count -eq 2 -and @($items | Where-Object {$_.Name -eq 'AutumnOS.exe' -and -not $_.PSIsContainer}).Count -eq 1 -and
        @($items | Where-Object {$_.Name -eq 'AutumnOS_Data' -and $_.PSIsContainer}).Count -eq 1)
    Add-SmokeCheck ($Phase+'_single_file_has_no_nested_data_root') (-not(Test-Path -LiteralPath (Join-Path $programDirectory 'AutumnOS_Data')))
}
function Assert-NormalLocalExtractedPayload {
    $evidence = [IO.Path]::GetFullPath([string]$singleFilePackage.evidence)
    $buildRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path $helperRoot -Parent) ('artifacts/builds/'+$SingleFileBuildId)))
    if (-not $evidence.StartsWith($buildRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Single-file payload evidence escaped its tracked build.' }
    Assert-T05PlainPath $evidence
    $manifest = @(Read-T05Json (Join-Path $evidence 'delivery-hashes.json'))
    if ($manifest.Count -eq 0) { throw 'Single-file frozen payload manifest is empty.' }
    $allowed = [IO.Path]::GetFullPath($programDirectory).TrimEnd('\')+'\'
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $manifest) {
        $relative = [string]$item.path
        if ([string]::IsNullOrWhiteSpace($relative) -or [IO.Path]::IsPathFullyQualified($relative) -or $relative.Contains(':') -or -not $seen.Add($relative) -or $item.sha256 -notmatch '^[a-f0-9]{64}$') { throw 'Invalid tracked single-file payload item.' }
        $target = [IO.Path]::GetFullPath((Join-Path $programDirectory $relative))
        if (-not $target.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Tracked payload item escaped the actual extracted program directory.' }
        Assert-T05PlainPath $target
        if ((Get-T05Hash $target) -ne $item.sha256 -or (Get-Item -LiteralPath $target).Length -ne $item.bytes) { throw 'Actual extracted single-file payload differs from frozen delivery bytes.' }
    }
    Add-SmokeCheck 'all_actual_extracted_single_file_bytes_match_frozen_payload' $true
    $report.verified_extracted_files=$manifest.Count
    $report.program_relative_path=$singleFilePackage.program_relative_path
    $report.single_file_preparation='passed_actual_entry_extraction_and_exact_nested_process_tracking'
}

function Assert-NormalLocalDeveloperDisabled([string]$Phase,[switch]$InspectUi) {
    $configuration = Join-Path $dataDirectory 'Config/developer-mode.json'
    $present = Test-Path -LiteralPath $configuration
    if ($present) {
        $saved = Read-T05Json $configuration
        if ($saved.schemaVersion -ne 1 -or $saved.values.enabled -ne $false) { throw 'Developer mode must remain false throughout ordinary Store verification.' }
    }
    $uiState = 'not_visible'
    if ($InspectUi) {
        $toggle = Require 'DeveloperModeToggle'
        $state = $toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState
        if ($state -ne [Windows.Automation.ToggleState]::Off) { throw 'The real native developer-mode toggle is not Off.' }
        $uiState = 'off'
    }
    $modeObservations.Add([ordered]@{phase=$Phase;timestamp_utc=[DateTimeOffset]::UtcNow.ToString('o');configuration_present=$present;enabled=$false;ui=$uiState})
    if (@(Read-ChemLeapRuntimeEvents | Where-Object {$_.appId -eq $appId -and $_.eventName -match '^developer_preview'}).Count) { throw 'Developer preview must never execute in the ordinary flow.' }
}
function Get-NormalLocalRegistration {
    $registry = Read-T05Json (Join-Path $dataDirectory 'Apps/.autumnos-registry.json')
    $apps = @($registry.applications | Where-Object {$_.appId -eq $appId -and $_.isInstalled})
    if ($apps.Count -ne 1) { throw 'Exactly one installed local fixture registration is required.' }
    return $apps[0]
}
function Get-NormalLocalTexts {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Text)
    return @($product.Element.FindAll([Windows.Automation.TreeScope]::Descendants,$condition) | ForEach-Object {$_.Current.Name})
}
function Start-NormalLocalGame {
    Assert-NormalLocalDeveloperDisabled 'before_normal_desktop_launch'
    $before = @(Read-ChemLeapRuntimeEvents).Count
    $icon = 'InstalledApp-' + $appId
    $null = Wait-SmokeCondition { Visible $icon } 'local installed desktop icon' 15
    Invoke-SmokePointer $product $icon 'single_click'
    $null = Wait-SmokeCondition { Find-ChemLeapElement 'nickname' '^玩家昵称$' ([Windows.Automation.ControlType]::Edit) } 'ordinary installed local fixture native document' 35
    $events = Wait-SmokeCondition {
        $batch = @(Read-ChemLeapRuntimeEvents | Select-Object -Skip $before | Where-Object {$_.appId -eq $appId -and $_.packageSha256 -eq $expectedHash})
        $foreground = @($batch | Where-Object {$_.eventName -eq 'navigation_completed' -and $_.state -eq 'Foreground' -and $_.networkSandboxVerified}) | Select-Object -Last 1
        if ($foreground -and @($batch | Where-Object {$_.instanceId -eq $foreground.instanceId -and $_.eventName -eq 'isolation_verified' -and $_.networkSandboxVerified}).Count -eq 1) { return $foreground }
        return $null
    } 'instance-bound native isolation proof precedes ordinary app foreground' 15
    $all = @(Read-ChemLeapRuntimeEvents | Select-Object -Skip $before)
    $same = @($all | Where-Object instanceId -eq $events.instanceId)
    $proofIndex=-1; $foregroundIndex=-1
    for ($index=0; $index -lt $same.Count; $index++) {
        if ($same[$index].eventName -eq 'isolation_verified') { $proofIndex=$index }
        if ($same[$index].eventName -eq 'navigation_completed') { $foregroundIndex=$index }
    }
    Add-SmokeCheck 'normal_launch_requires_prior_instance_bound_native_isolation' ($proofIndex -ge 0 -and $foregroundIndex -gt $proofIndex -and $events.blocksMaintenance)
    Assert-NormalLocalDeveloperDisabled 'normal_game_foreground'
    return [string]$events.instanceId
}
function Stop-NormalLocalGame([string]$Instance) {
    Invoke-AutumnGameShortcut $product 'Close'
    Click-Named '确认结束'
    Wait-SmokeDesktop $product
    $null = Wait-SmokeCondition { @(Read-ChemLeapRuntimeEvents | Where-Object {$_.instanceId -eq $Instance -and $_.eventName -eq 'closed' -and $_.state -eq 'Closed' -and -not $_.blocksMaintenance -and -not $_.networkSandboxVerified}).Count -gt 0 } 'ordinary game fully closed and isolation proof cleared' 15
    Assert-NormalLocalDeveloperDisabled 'normal_game_closed'
}

# Reuse only the owned-renderer search/click helpers; this fixture has its own title.
function Get-ChemLeapScopes {
    $result = [Collections.Generic.List[object]]::new(); $result.Add($product.Element)
    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Document),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'本地隔离配对 · Lab Chronicles'))
    $window = $product.Element.Current.BoundingRectangle
    foreach ($document in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants,$condition)) {
        $bounds = $document.Current.BoundingRectangle
        if (-not $bounds.IsEmpty -and $bounds.Left -ge $window.Left -and $bounds.Top -ge $window.Top -and $bounds.Right -le $window.Right -and $bounds.Bottom -le $window.Bottom -and
            (Test-AutumnOwnedInputProcess $document.Current.ProcessId $product.Process)) { $result.Add($document) }
    }
    return @($result)
}
function Set-NormalLocalNickname([string]$Text) {
    Focus-SmokeProduct $product
    $edit = Wait-SmokeCondition { Find-ChemLeapElement 'nickname' '^玩家昵称$' ([Windows.Automation.ControlType]::Edit) } 'owned local fixture nickname'
    if (-not (Test-AutumnOwnedInputProcess $edit.Current.ProcessId $product.Process) -or -not (Test-SmokeForeground $product)) { throw 'Refusing nickname input outside the owned product.' }
    $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
    $null = Wait-SmokeCondition { (Get-NormalLocalNickname) -ceq $Text } 'actual native nickname value readback' 8
}
function Get-NormalLocalNickname {
    $edit = Find-ChemLeapElement 'nickname' '^玩家昵称$' ([Windows.Automation.ControlType]::Edit)
    if ($edit) { return $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value }
    return $null
}
function New-NormalLocalPackage {
    Assert-T05PlainPath $fixtureRoot
    $manifest = Read-T05Json (Join-Path $fixtureRoot 'manifest.json')
    if ($manifest.appId -ne $appId -or $manifest.version -ne '1.0.0' -or @($manifest.permissions).Count -ne 1 -or $manifest.permissions[0] -ne 'saves') { throw 'The committed local fixture manifest changed unexpectedly.' }
    $names = @('manifest.json','index.html','game.js','game.css','autumn-sdk.js')
    $report.fixture_resources = @()
    $archive = [IO.Compression.ZipFile]::Open($fixturePackage,[IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($name in $names) {
            $path = Join-Path $fixtureRoot $name; Assert-T05PlainPath $path
            $bytes = [IO.File]::ReadAllBytes($path)
            $entry = $archive.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2026,1,1,0,0,0,[TimeSpan]::Zero)
            $stream = $entry.Open(); try { $stream.Write($bytes) } finally { $stream.Dispose() }
            $report.fixture_resources += [ordered]@{path=$name;bytes=$bytes.Length;sha256=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes))}
        }
    } finally { $archive.Dispose() }
    $script:expectedHash = Get-T05Hash $fixturePackage
    $report.expected_package_sha256 = $expectedHash; $report.expected_package_bytes = (Get-Item -LiteralPath $fixturePackage).Length
    $report.fixture_package = $fixturePackage
}
function Find-NormalLocalPicker {
    if (-not $product -or $product.Process.HasExited) { return $null }
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Window)
    $windows = @([Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition))
    $windows += @($product.Element.FindAll([Windows.Automation.TreeScope]::Descendants,$condition))
    foreach ($window in $windows) {
        if ($window.Current.ProcessId -ne $product.Process.Id -or $window.Current.ClassName -ne '#32770' -or $window.Current.IsOffscreen) { continue }
        $owner = [AutumnDesktopInteractionSmoke.NativeWindows]::GetWindow([IntPtr]$window.Current.NativeWindowHandle,4)
        for ($depth=0; $depth -lt 16 -and $owner -ne [IntPtr]::Zero; $depth++) {
            if ($owner -eq $product.Handle) { return $window }
            $owner = [AutumnDesktopInteractionSmoke.NativeWindows]::GetWindow($owner,4)
        }
    }
    return $null
}
function Select-NormalLocalPickerFile {
    $full = [IO.Path]::GetFullPath($fixturePackage)
    if (-not $full.StartsWith($TestRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture picker path must remain within the new isolated test root.' }
    if ($ManualPicker) {
        Write-Warning ('Manual picker step: select only this generated fixture in the AutumnOS file picker: '+$full)
        $report.local_picker = 'manual_selection_pending'
        $null = Wait-SmokeCondition {
            $button = Find-SmokeNamedElement $product.Element '确认安装' ([Windows.Automation.ControlType]::Button)
            if ($button -and $button.Current.ProcessId -eq $product.Process.Id -and -not $button.Current.IsOffscreen) { return $button }
        } 'user selects the generated local fixture and reaches native installation confirmation' 180
        $report.local_picker = 'passed_manual_selection_reached_host_confirmation'
        return
    }
    $dialog = Wait-SmokeCondition { Find-NormalLocalPicker } 'same-PID native file picker owned by the tested product' 20
    $nameBox = Wait-SmokeCondition {
        foreach ($edit in $dialog.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit))) {
            if (($edit.Current.AutomationId -eq '1148' -or $edit.Current.Name -match '^(文件名|File name)(\(|:|：|$)') -and $edit.Current.IsEnabled -and -not $edit.Current.IsOffscreen -and $edit.Current.ProcessId -eq $product.Process.Id) { return $edit }
        }
    } 'owned picker filename edit' 10
    if (-not (Find-NormalLocalPicker)) { throw 'Picker ownership disappeared before setting the fixture path.' }
    $pattern = $nameBox.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($full)
    $null = Wait-SmokeCondition { $pattern.Current.Value -ceq $full } 'native picker confirms exact fixture path' 5
    $button = $dialog.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'1')))
    if (-not $button -or $button.Current.ProcessId -ne $product.Process.Id -or -not $button.Current.IsEnabled -or $button.Current.IsOffscreen -or -not (Find-NormalLocalPicker)) { throw 'Owned native picker Open button was not safely identified.' }
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $report.local_picker = 'passed_same_PID_owner_chain_verified_native_picker'
}

try {
    if (-not $IsWindows) { throw 'An interactive Windows desktop is required.' }
    if (@(Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('AutumnOS.exe','AutumnOS.Client.exe','AutumnOS.Updater.exe')}).Count) { throw 'An existing AutumnOS product is running; this test never takes over or closes it.' }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this test as a standard user.' }
    Assert-T05PlainPath $ExecutablePath; Assert-T05PlainPath $ReportDirectory; Assert-T05PlainPath $TestRoot
    if ([IO.Path]::GetFileName($ExecutablePath) -ne 'AutumnOS.exe' -or -not(Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw 'A real delivered AutumnOS.exe is required.' }
    $hostRoot = Split-Path $ExecutablePath -Parent
    if ($SingleFileBuildId) {
        $buildRoot = Join-Path (Split-Path $helperRoot -Parent) ('artifacts/builds/'+$SingleFileBuildId)
        $packageRecord = Join-Path $buildRoot 'package-single-file.json'
        Assert-T05PlainPath $packageRecord
        $singleFilePackage = Read-T05Json $packageRecord
        $singleFileBuild = Read-T05Json (Join-Path $buildRoot 'build-result.json')
        if ($singleFileBuild.status -ne 'passed' -or $singleFileBuild.test_build -or $singleFileBuild.build_id -ne $SingleFileBuildId -or
            $singleFilePackage.status -notin @('prepared','passed') -or $singleFilePackage.build_id -ne $SingleFileBuildId -or
            $singleFilePackage.source_snapshot_id -notmatch '^sha256:[a-f0-9]{64}$' -or $singleFilePackage.source_snapshot_id -ne $singleFileBuild.source_snapshot_id -or
            $singleFilePackage.version -ne $singleFileBuild.version -or [string]::IsNullOrWhiteSpace([string]$singleFilePackage.release_label) -or
            -not [string]::Equals([IO.Path]::GetFullPath([string]$singleFilePackage.executable),$ExecutablePath,[StringComparison]::OrdinalIgnoreCase) -or
            $singleFilePackage.executable_sha256 -notmatch '^[a-f0-9]{64}$' -or (Get-T05Hash $ExecutablePath) -ne $singleFilePackage.executable_sha256 -or
            (Get-Item -LiteralPath $ExecutablePath).Length -ne $singleFilePackage.bytes) { throw 'Single-file input must exactly match a successful ordinary tracked build, source, package path, size and hash.' }
        if ($singleFilePackage.payload_sha256 -notmatch '^[a-f0-9]{64}$' -or
            $singleFilePackage.program_relative_path -notmatch '^AutumnOS_Data[\\/]System[\\/]Product-[a-f0-9]{20}$' -or
            ($singleFilePackage.program_relative_path.Replace('\','/')) -ne ('AutumnOS_Data/System/Product-'+$singleFilePackage.payload_sha256.Substring(0,20))) { throw 'Single-file extracted program path must be the exact payload-bound Data/System directory.' }
        $programDirectory = Join-Path $stageDirectory $singleFilePackage.program_relative_path
        $report.single_file_package_record=$packageRecord; $report.single_file_package_record_sha256=Get-T05Hash $packageRecord
        $report.release_label=$singleFilePackage.release_label
    } elseif (-not(Test-Path -LiteralPath (Join-Path $hostRoot 'Microsoft.UI.Xaml.dll')) -or -not(Test-Path -LiteralPath (Join-Path $hostRoot 'WebView2Runtime/msedgewebview2.exe'))) { throw 'Use this build normal complete directory with its fixed WebView2 runtime.' }
    if ($ReportDirectory.StartsWith($TestRoot.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or $TestRoot.StartsWith($ReportDirectory.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'ReportDirectory and TestRoot must be separate new scopes.' }
    New-Item -ItemType Directory -Path $ReportDirectory,$TestRoot,$cwd | Out-Null
    if ($SingleFileBuildId) {
        New-Item -ItemType Directory -Path $stageDirectory | Out-Null
        Copy-Item -LiteralPath $ExecutablePath -Destination $report.tested_executable
        $initialItems = @(Get-ChildItem -LiteralPath $stageDirectory -Force)
        Add-SmokeCheck 'single_file_before_first_start_only_actual_exe_and_no_data' ($initialItems.Count -eq 1 -and $initialItems[0].Name -eq 'AutumnOS.exe' -and -not $initialItems[0].PSIsContainer -and -not(Test-Path -LiteralPath $dataDirectory))
        Add-SmokeCheck 'single_file_compiled_release_label_matches_package' ((Get-Item -LiteralPath $report.tested_executable).VersionInfo.ProductVersion -eq $singleFilePackage.release_label)
    } else {
        Copy-ChemLeapPlainTree $hostRoot $stageDirectory @('AutumnOS_Data','.autumnos-update')
        Add-SmokeCheck 'ordinary_complete_host_copy_excludes_real_user_data' (-not(Test-Path -LiteralPath $dataDirectory))
    }
    $report.source_executable_sha256 = Get-T05Hash $ExecutablePath
    Add-SmokeCheck 'copied_native_executable_matches_actual_build' ((Get-T05Hash $report.tested_executable) -eq $report.source_executable_sha256)
    if (-not $SingleFileBuildId) { Read-NormalLocalHostMetadata $stageDirectory }
    foreach ($name in @('Smoke-NativeAppInput.ps1','T06-NativeHelpers.ps1','Test-DesktopInteractionSmoke.ps1','Test-T05UpdateSmoke.ps1','Test-T04StoreSmoke.ps1','Test-ChemLeapNativeSmoke.ps1')) { $report.imported_helpers_sha256[$name]=Get-T05Hash (Join-Path $helperRoot $name) }
    if ($SingleFileBuildId) { $report.imported_helpers_sha256['Test-SingleFileSmoke.ps1']=Get-T05Hash (Join-Path $helperRoot 'Test-SingleFileSmoke.ps1') }
    $desktopRuntime = Join-Path (Split-Path $helperRoot -Parent) '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach ($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')) { Add-Type -Path (Join-Path $desktopRuntime $name) }
    $strings = @($desktopAst.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('namespace AutumnDesktopInteractionSmoke')},$true))
    if ($strings.Count -ne 1) { throw 'Maintained native helper declaration is missing.' }
    if (-not('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])) { Add-Type -TypeDefinition $strings[0].Value }
    Initialize-T05FocusNative
    $script:product = Start-SmokeProduct $cwd
    if ($SingleFileBuildId) {
        Assert-NormalLocalSingleFileRoot 'after_actual_first_preparation'
        Assert-NormalLocalExtractedPayload
        Read-NormalLocalHostMetadata $programDirectory
    }
    $trackedProcesses.Add([pscustomobject]@{Process=$product.Process;StartTicks=$product.Process.StartTime.ToUniversalTime().Ticks;Path=$product.Process.MainModule.FileName;Root=$stageDirectory})
    Add-SmokeCheck 'ordinary_standard_user_owned_native_window' ($product.Element.Current.ProcessId -eq $product.Process.Id)
    Click 'HelloNextButton'; Click 'BrandNextButton'; Click 'WelcomeAppearanceNext'; Click 'FirstRunSkip'; Click 'PrepareDesktopButton'; Wait-SmokeDesktop $product
    Click 'SettingsButton'; Toggle 'SettingsNavDiagnostics' $true
    Assert-NormalLocalDeveloperDisabled 'initial_native_settings' -InspectUi
    Click 'SettingsHomeButton'; Wait-SmokeDesktop $product
    Add-SmokeCheck 'ordinary_desktop_hides_developer_entry' (-not(Visible 'DeveloperButton'))
    Click 'StoreButton'; $null=Require 'StoreHeading'
    Add-SmokeCheck 'actual_store_uses_public_github' ((Require 'StoreSource').Current.Name -eq 'GitHub 公开应用')

    New-NormalLocalPackage
    Add-SmokeCheck 'new_local_app_is_not_chemleap_or_bundled_elementpairs' ($appId -ne 'cn.labchronicles.chemleap' -and $appId -ne 'cn.labchronicles.elementpairs')
    Assert-NormalLocalDeveloperDisabled 'before_real_local_picker'
    Click 'StoreLocalInstall'; Select-NormalLocalPickerFile
    Click-Named '确认安装'
    $registration = Wait-SmokeCondition {
        $path = Join-Path $dataDirectory 'Apps/.autumnos-registry.json'
        if (Test-Path -LiteralPath $path) {
            $value = Read-T05Json $path
            $matches = @($value.applications | Where-Object { $_.appId -eq $appId -and $_.isInstalled })
            if ($matches.Count -eq 1) { return $matches[0] }
        }
    } 'real local picker installation transaction and registration' 30
    Add-SmokeCheck 'registered_source_is_local_and_distinct_from_chemleap_github' ($registration.source.kind -eq 'local' -and $null -eq $registration.source.repositoryId -and $registration.package.hostSource -eq ('local:'+$appId) -and $registration.package.hostSource -ne 'github:1414175562')
    Add-SmokeCheck 'local_install_matches_exact_generated_package' ($registration.package.packageSha256 -eq $expectedHash -and $registration.package.manifest.version -eq '1.0.0')
    $installedDirectory = [IO.Path]::GetFullPath($registration.package.directoryPath)
    $appsRoot = [IO.Path]::GetFullPath((Join-Path $dataDirectory 'Apps')).TrimEnd('\')+'\'
    Add-SmokeCheck 'local_installed_content_stays_under_isolated_apps' ($installedDirectory.StartsWith($appsRoot,[StringComparison]::OrdinalIgnoreCase))
    foreach ($item in $report.fixture_resources) {
        $installedPath = Join-Path $installedDirectory $item.path; Assert-T05PlainPath $installedPath
        Add-SmokeCheck ('local_installed_bytes_match_'+$item.path) ((Get-T05Hash $installedPath) -eq $item.sha256 -and (Get-Item -LiteralPath $installedPath).Length -eq $item.bytes)
    }
    $report.local_install = 'passed'; $report.public_store = 'passed_normal_mode_local_install_entry'
    Assert-NormalLocalDeveloperDisabled 'local_install_registered'
    Click 'StoreHome'; Wait-SmokeDesktop $product
    $firstInstance = Start-NormalLocalGame; $report.first_instance = $firstInstance; $report.normal_launch = 'passed'
    $savedText = '派蒙·本地隔离验证'; $unsavedText = '未保存·继续实例验证'
    Set-NormalLocalNickname $savedText
    Click-ChemLeap 'card-0' '^第 1 张卡片，未翻开$'
    Click-ChemLeap 'card-1' '^第 2 张卡片，未翻开$'
    $null = Wait-SmokeCondition {
        $button = Find-ChemLeapElement 'save-button' '^保存进度$' ([Windows.Automation.ControlType]::Button)
        if ($button -and $button.Current.IsEnabled) { return $button }
    } 'local game becomes ready to save after real card clicks' 8
    Click-ChemLeap 'save-button' '^保存进度$'; Click-Named '允许'; Wait-ChemLeapStatus '已保存到本机'
    $saveRoot = Join-Path $dataDirectory ('Saves/'+$appId)
    $saveFile = Wait-SmokeCondition {
        if (Test-Path -LiteralPath $saveRoot) {
            $files = @(Get-ChildItem -LiteralPath $saveRoot -Filter 'game.json' -Recurse -File)
            if ($files.Count -eq 1) { return $files[0] }
        }
    } 'real source-bound local fixture SDK save' 12
    Assert-T05PlainPath $saveFile.FullName
    $saved = Read-T05Json $saveFile.FullName
    $relative = [IO.Path]::GetRelativePath($saveRoot,$saveFile.FullName).Replace('\','/')
    Add-SmokeCheck 'SDK_save_has_new_app_source_guest_and_real_gameplay' ($saved.schemaVersion -eq 2 -and $saved.appId -eq $appId -and $saved.sourceKey -match '^[a-f0-9]{64}$' -and $saved.accountKey -eq 'guest' -and $saved.slot -eq 'game' -and $relative -eq ($saved.sourceKey+'/guest/game.json') -and $saved.value.nickname -ceq $savedText -and $saved.value.moves -ge 1)
    Add-SmokeCheck 'local_instance_uses_real_permission_and_save_gateway' (
        @(Read-ChemLeapRuntimeEvents | Where-Object { $_.instanceId -eq $firstInstance -and $_.eventName -eq 'permissions_request_ok' }).Count -gt 0 -and
        @(Read-ChemLeapRuntimeEvents | Where-Object { $_.instanceId -eq $firstInstance -and $_.eventName -eq 'saves_write_ok' }).Count -gt 0)
    $report.sdk_save = 'passed'; $report.native_gameplay = 'passed'; $report.saved_moves = $saved.value.moves
    $saveHash = Get-T05Hash $saveFile.FullName
    $report.save_file = @{path=[IO.Path]::GetRelativePath($dataDirectory,$saveFile.FullName);sha256=$saveHash;bytes=$saveFile.Length}
    Set-NormalLocalNickname $unsavedText
    Invoke-AutumnGameShortcut $product 'Desktop'; Wait-SmokeDesktop $product
    $null = Wait-SmokeCondition { @(Read-ChemLeapRuntimeEvents | Where-Object { $_.instanceId -eq $firstInstance -and $_.state -eq 'Background' -and $_.blocksMaintenance }).Count -gt 0 } 'local game remains alive in the background' 8
    Invoke-SmokePointer $product 'DesktopGestureHint' 'double_click'; Click 'ManagedContinue'
    $null = Wait-SmokeCondition { (Get-NormalLocalNickname) -ceq $unsavedText } 'multitasking Continue preserves unsaved input of the same local instance' 10
    $events = @(Read-ChemLeapRuntimeEvents | Where-Object { $_.appId -eq $appId -and $_.eventName -eq 'navigation_completed' })
    Add-SmokeCheck 'continue_reuses_original_instance_without_navigation_or_save_rewrite' ($events.Count -eq 1 -and $events[0].instanceId -eq $firstInstance -and (Get-T05Hash $saveFile.FullName) -eq $saveHash)
    $report.background_resume = 'passed'
    Stop-NormalLocalGame $firstInstance
    $secondInstance = Start-NormalLocalGame; $report.second_instance = $secondInstance
    Add-SmokeCheck 'real_end_then_reopen_creates_new_local_instance' ($secondInstance -ne $firstInstance -and (Get-NormalLocalNickname) -cne $unsavedText)
    Click-ChemLeap 'load-button' '^读取存档$'; Wait-ChemLeapStatus '已读取本机存档' -MayAuthorize
    $null = Wait-SmokeCondition { (Get-NormalLocalNickname) -ceq $savedText } 'new local instance reads actual earlier SDK save' 12
    Add-SmokeCheck 'new_local_instance_uses_SDK_read_and_preserves_saved_bytes' (@(Read-ChemLeapRuntimeEvents | Where-Object { $_.instanceId -eq $secondInstance -and $_.eventName -eq 'saves_read_ok' }).Count -gt 0 -and (Get-T05Hash $saveFile.FullName) -eq $saveHash)
    $report.new_instance_readback = 'passed'
    $null = Save-SmokeScreenshot $product '01-local-normal-SDK-readback' -IncludeOverlays
    Stop-NormalLocalGame $secondInstance
    Click 'SettingsButton'; Toggle 'SettingsNavDiagnostics' $true
    Assert-NormalLocalDeveloperDisabled 'final_native_settings' -InspectUi
    Click 'SettingsHomeButton'; Wait-SmokeDesktop $product
    Add-SmokeCheck 'developer_entry_remains_hidden_after_gameplay' (-not(Visible 'DeveloperButton'))
    Add-SmokeCheck 'unrelated_working_directory_has_no_user_data' (-not(Test-Path -LiteralPath (Join-Path $cwd 'AutumnOS_Data')))
    $report.runtime_evidence=@(Read-ChemLeapRuntimeEvents | Where-Object {$_.appId -eq $appId} | Select-Object timestampUtc,buildId,instanceId,eventName,state,blocksMaintenance,appId,packageSha256,networkSandboxVerified)
    Close-SmokeProduct $product
    if ($SingleFileBuildId) {
        Add-SmokeCheck 'single_file_inner_bootstrap_exits_after_normal_client_close' ($product.InnerProcess.WaitForExit(10000) -and $product.InnerProcess.ExitCode -eq 0)
        Add-SmokeCheck 'single_file_outer_entry_exits_after_normal_client_close' ($product.EntryProcess.WaitForExit(10000) -and $product.EntryProcess.ExitCode -eq 0)
        $report.single_file_bootstrap_exit_codes=[ordered]@{outer=$product.EntryProcess.ExitCode;inner=$product.InnerProcess.ExitCode;business=$product.Process.ExitCode}
        Assert-NormalLocalSingleFileRoot 'after_real_store_gameplay'
        Add-SmokeCheck 'single_file_entry_bytes_remain_exact_after_store_play' ((Get-T05Hash $report.tested_executable) -eq $singleFilePackage.executable_sha256)
    }
    $report.status='passed'
} catch {
    $report.failure=$_.Exception.Message; $report.failure_type=$_.Exception.GetType().FullName; $report.failure_stack=$_.ScriptStackTrace
    Write-Warning ('Normal local-app smoke: '+$report.failure)
    try { $report.failure_owned_buttons=@(Failure-Buttons) } catch { }
    if ($product -and -not $product.Process.HasExited) { try { $null=Save-SmokeScreenshot $product '99-normal-store-failure' -IncludeOverlays } catch { } }
} finally {
    $clean=$true
    foreach ($process in $ownedProcesses) {
        try { if (-not $process.HasExited -and (-not $process.CloseMainWindow() -or -not $process.WaitForExit(10000))) { $clean=$false; $report.remaining_processes+=@{pid=$process.Id;action='left_running_no_forced_termination'} } } catch { $clean=$false }
    }
    foreach ($entry in $ownedEntryProcesses) {
        try { if (-not $entry.WaitForExit(10000)) { $clean=$false; $report.remaining_processes+=@{pid=$entry.Id;action='bootstrap_left_running'} } } catch { $clean=$false }
    }
    Restore-AutumnNativePointer
    $report.cleanup=if($clean){'passed'}else{'failed'}
    if (-not $clean) { $report.status='failed' }
    $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o'); $report.check_count=$checks.Count; $report.passed_checks=@($checks | Where-Object status -eq 'passed').Count
    if (Test-Path -LiteralPath $ReportDirectory) { $report | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'normal-local-app.json') -Encoding utf8 }
    foreach ($process in $ownedProcesses) { $process.Dispose() }; foreach ($entry in $ownedEntryProcesses) { $entry.Dispose() }
}
if ($report.status -ne 'passed') { throw "Normal local-app smoke failed; evidence preserved at $ReportDirectory" }
