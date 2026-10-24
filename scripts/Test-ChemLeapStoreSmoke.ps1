#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [string]$TestRoot,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]{0,90}$')][string]$SingleFileBuildId,
    [ValidateRange(15,90)][int]$WindowTimeoutSeconds = 45,
    [ValidateRange(30,300)][int]$DownloadTimeoutSeconds = 120
)

# Ordinary public Store flow only. No developer preview, test Store, CLI runtime commands,
# registry/config injection, preinstalled package copy, or forced process termination.
$ErrorActionPreference = 'Stop'
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
if (-not $TestRoot) { $TestRoot = Join-Path ([IO.Path]::GetTempPath()) ('aosc-' + [Guid]::NewGuid().ToString('N').Substring(0,8)) }
$TestRoot = [IO.Path]::GetFullPath($TestRoot)
if ((Test-Path -LiteralPath $ReportDirectory) -or (Test-Path -LiteralPath $TestRoot)) { throw 'ReportDirectory and TestRoot must both be new; earlier evidence is retained.' }
if ($ReportDirectory -match '[\\/]AutumnOS_Data(?:[\\/]|$)' -or $TestRoot -match '[\\/]AutumnOS_Data(?:[\\/]|$)') { throw 'Test output cannot be placed in user data.' }
if ($TestRoot.Length -gt 100) { throw 'Use a new short TestRoot (100 characters or fewer) for native WebView2 runtime paths.' }
. (Join-Path $PSScriptRoot 'Smoke-NativeAppInput.ps1')
. (Join-Path $PSScriptRoot 'T06-NativeHelpers.ps1')
$desktopAst = Import-T06Functions (Join-Path $PSScriptRoot 'Test-DesktopInteractionSmoke.ps1')
$script:t05LegacyFocus = (Get-Item Function:Focus-SmokeProduct).ScriptBlock
$null = Import-T06Functions (Join-Path $PSScriptRoot 'Test-T05UpdateSmoke.ps1') -Names @(
    'Assert-T05OwnedWindow','Get-T05WindowGeometry','Focus-SmokeProduct','Initialize-T05FocusNative',
    'Assert-T05PlainPath','Invoke-T05SharingRead','Get-T05Hash')
$null = Import-T06Functions (Join-Path $PSScriptRoot 'Test-T04StoreSmoke.ps1') -Names @(
    'Show-Element','Click','Find','Visible','Require','Toggle','Click-Named','Value','Failure-Buttons')
$null = Import-T06Functions (Join-Path $PSScriptRoot 'Test-ChemLeapNativeSmoke.ps1') -Names @(
    'Copy-ChemLeapPlainTree','Get-ChemLeapScopes','Find-ChemLeapElement','Click-ChemLeap',
    'Get-ChemLeapScore','Get-ChemLeapSubstance','Wait-ChemLeapStatus','Read-ChemLeapRuntimeEvents')
if ($SingleFileBuildId) {
    # Import the maintained exact entry -> inner bootstrap -> WinUI child tracker.
    # Its process handles, parentage, path, start-time and Windows-session checks stay intact.
    $null = Import-T06Functions (Join-Path $PSScriptRoot 'Test-SingleFileSmoke.ps1') -Names @('Start-SmokeProduct')
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
$expectedHash = 'e00ecf58d1c4a5d1190163cd02a13199135809ad0a782d5b830274e9642da0a2'
$appId = 'cn.labchronicles.chemleap'
$repositoryId = 1414175562L
$releaseId = 415748507L
$singleFilePackage = $null
$singleFileBuild = $null
$programDirectory = $stageDirectory
$report = [ordered]@{
    schema_version=1; checkpoint='chemleap_public_store_install_and_normal_native_gameplay'; status='failed'
    started_utc=[DateTimeOffset]::UtcNow.ToString('o'); finished_utc=$null
    source_executable=$ExecutablePath; tested_executable=(Join-Path $stageDirectory 'AutumnOS.exe'); isolated_root=$TestRoot
    input_mode=$(if($SingleFileBuildId){'single_file_only'}else{'ordinary_complete_directory'}); single_file_build_id=$SingleFileBuildId
    driver_sha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    build_id='not_recorded'; source_snapshot_id='not_recorded'; imported_helpers_sha256=@{}
    app_id=$appId; repository='paimeng5201314/chemleap'; repository_id=$repositoryId; release_id=$releaseId
    version='0.1.1'; expected_package_sha256=$expectedHash; expected_package_bytes=28871
    source='real_public_github'; developer_mode='never_enabled'; developer_mode_observations=$modeObservations
    public_store='not_run'; public_release_download='not_run'; public_release_install='not_run'
    normal_launch='not_run'; native_gameplay='not_run'; sdk_save='not_run'; background_continue='not_run'; new_instance_readback='not_run'
    external_publication='not_performed'; real_logto='not_run'; physical_input='not_run'; visual_review='not_run'
    checks=$checks; screenshots=$screenshots; input_actions=$inputActions; window_geometry=$windowGeometry
    process_ids=@(); bootstrap_process_ids=@(); remaining_processes=@(); cleanup='not_run'; isolated_test_data_retained=$true
    startup_observations=@(); single_file_preparation='not_applicable'; single_file_bootstrap_exit_codes=$null
}

function Read-ChemStoreHostMetadata([string]$ProgramRoot) {
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
function Assert-ChemStoreSingleFileRoot([string]$Phase) {
    $items = @(Get-ChildItem -LiteralPath $stageDirectory -Force)
    Add-SmokeCheck ($Phase+'_single_file_root_contains_only_exe_and_one_data_folder') (
        $items.Count -eq 2 -and @($items | Where-Object {$_.Name -eq 'AutumnOS.exe' -and -not $_.PSIsContainer}).Count -eq 1 -and
        @($items | Where-Object {$_.Name -eq 'AutumnOS_Data' -and $_.PSIsContainer}).Count -eq 1)
    Add-SmokeCheck ($Phase+'_single_file_has_no_nested_data_root') (-not(Test-Path -LiteralPath (Join-Path $programDirectory 'AutumnOS_Data')))
}
function Assert-ChemStoreExtractedPayload {
    $evidence = [IO.Path]::GetFullPath([string]$singleFilePackage.evidence)
    $buildRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/builds/'+$SingleFileBuildId)))
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

function Assert-ChemStoreDeveloperDisabled([string]$Phase,[switch]$InspectUi) {
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
function Get-ChemStoreRegistration {
    $registry = Read-T05Json (Join-Path $dataDirectory 'Apps/.autumnos-registry.json')
    $apps = @($registry.applications | Where-Object {$_.appId -eq $appId -and $_.isInstalled})
    if ($apps.Count -ne 1) { throw 'Exactly one real installed ChemLeap registration is required.' }
    return $apps[0]
}
function Get-ChemStoreTexts {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Text)
    return @($product.Element.FindAll([Windows.Automation.TreeScope]::Descendants,$condition) | ForEach-Object {$_.Current.Name})
}
function Start-ChemStoreGame {
    Assert-ChemStoreDeveloperDisabled 'before_normal_desktop_launch'
    $before = @(Read-ChemLeapRuntimeEvents).Count
    $icon = 'InstalledApp-' + $appId
    $null = Wait-SmokeCondition { Visible $icon } 'real public-installed desktop icon' 15
    Invoke-SmokePointer $product $icon 'single_click'
    $null = Wait-SmokeCondition { Find-ChemLeapElement 'start-button' '^开始跳跃' ([Windows.Automation.ControlType]::Button) } 'ordinary installed ChemLeap native document' 35
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
    Assert-ChemStoreDeveloperDisabled 'normal_game_foreground'
    return [string]$events.instanceId
}
function Stop-ChemStoreGame([string]$Instance) {
    Invoke-AutumnGameShortcut $product 'Close'
    Click-Named '确认结束'
    Wait-SmokeDesktop $product
    $null = Wait-SmokeCondition { @(Read-ChemLeapRuntimeEvents | Where-Object {$_.instanceId -eq $Instance -and $_.eventName -eq 'closed' -and $_.state -eq 'Closed' -and -not $_.blocksMaintenance -and -not $_.networkSandboxVerified}).Count -gt 0 } 'ordinary game fully closed and isolation proof cleared' 15
    Assert-ChemStoreDeveloperDisabled 'normal_game_closed'
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
        $buildRoot = Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/builds/'+$SingleFileBuildId)
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
    if (-not $SingleFileBuildId) { Read-ChemStoreHostMetadata $stageDirectory }
    foreach ($name in @('Smoke-NativeAppInput.ps1','T06-NativeHelpers.ps1','Test-DesktopInteractionSmoke.ps1','Test-T05UpdateSmoke.ps1','Test-T04StoreSmoke.ps1','Test-ChemLeapNativeSmoke.ps1')) { $report.imported_helpers_sha256[$name]=Get-T05Hash (Join-Path $PSScriptRoot $name) }
    if ($SingleFileBuildId) { $report.imported_helpers_sha256['Test-SingleFileSmoke.ps1']=Get-T05Hash (Join-Path $PSScriptRoot 'Test-SingleFileSmoke.ps1') }
    $desktopRuntime = Join-Path (Split-Path $PSScriptRoot -Parent) '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach ($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')) { Add-Type -Path (Join-Path $desktopRuntime $name) }
    $strings = @($desktopAst.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('namespace AutumnDesktopInteractionSmoke')},$true))
    if ($strings.Count -ne 1) { throw 'Maintained native helper declaration is missing.' }
    if (-not('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])) { Add-Type -TypeDefinition $strings[0].Value }
    Initialize-T05FocusNative
    $script:product = Start-SmokeProduct $cwd
    if ($SingleFileBuildId) {
        Assert-ChemStoreSingleFileRoot 'after_actual_first_preparation'
        Assert-ChemStoreExtractedPayload
        Read-ChemStoreHostMetadata $programDirectory
    }
    $trackedProcesses.Add([pscustomobject]@{Process=$product.Process;StartTicks=$product.Process.StartTime.ToUniversalTime().Ticks;Path=$product.Process.MainModule.FileName;Root=$stageDirectory})
    Add-SmokeCheck 'ordinary_standard_user_owned_native_window' ($product.Element.Current.ProcessId -eq $product.Process.Id)
    Click 'HelloNextButton'; Click 'BrandNextButton'; Click 'WelcomeAppearanceNext'; Click 'FirstRunSkip'; Click 'PrepareDesktopButton'; Wait-SmokeDesktop $product
    Click 'SettingsButton'; Toggle 'SettingsNavDiagnostics' $true
    Assert-ChemStoreDeveloperDisabled 'initial_native_settings' -InspectUi
    Click 'SettingsHomeButton'; Wait-SmokeDesktop $product
    Add-SmokeCheck 'ordinary_desktop_hides_developer_entry' (-not(Visible 'DeveloperButton'))
    Click 'StoreButton'; $null=Require 'StoreHeading'
    Add-SmokeCheck 'actual_store_uses_public_github' ((Require 'StoreSource').Current.Name -eq 'GitHub 公开应用')
    Click 'StoreOpenRepository'; Value 'StoreRepositoryInput' 'https://github.com/paimeng5201314/chemleap'; Click-Named '查看应用'
    $null = Wait-SmokeCondition { $button=Find ('StoreInstall-'+$releaseId); if ($button -and $button.Current.IsEnabled) { return $button } } 'real compatible stable public release 415748507' 60
    $detailText = (Get-ChemStoreTexts) -join "`n"
    Add-SmokeCheck 'public_store_details_match_real_numeric_repository_and_app' ($detailText -match 'repositoryId：1414175562' -and $detailText -match 'paimeng5201314/chemleap' -and $detailText -match [regex]::Escape($appId))
    Add-SmokeCheck 'stable_release_is_selectable_in_public_store' ((Require 'StoreHeading').Current.Name -match 'ChemLeap' -and (Require ('StoreInstall-'+$releaseId)).Current.IsEnabled)
    $report.public_store='passed'; $report.observed_store_details=$detailText
    $null=Save-SmokeScreenshot $product '01-real-public-chemleap-release' -IncludeOverlays
    Assert-ChemStoreDeveloperDisabled 'before_real_release_download'
    Click ('StoreInstall-'+$releaseId); Click-Named '下载并安装'
    $downloadStatus = Wait-SmokeCondition {
        Assert-ChemStoreDeveloperDisabled 'waiting_for_public_download'
        $row=Find 'DownloadState-0.1.1'
        if ($row -and $row.Current.Name -match '^已安装') { return $row.Current.Name }
        if ($row -and $row.Current.Name -match '^失败') { throw ('Real public release download/install failed: '+$row.Current.Name) }
        return $null
    } 'actual GitHub release download hash verification and ordinary install' $DownloadTimeoutSeconds
    $registration = Get-ChemStoreRegistration
    Add-SmokeCheck 'registered_source_is_real_github_numeric_repository' ($registration.source.kind -eq 'github' -and $registration.source.repositoryId -eq $repositoryId -and $registration.source.repositoryName -eq 'paimeng5201314/chemleap')
    Add-SmokeCheck 'registered_public_release_identity_and_package_bytes_match' ($registration.provenance.releaseId -eq $releaseId -and $registration.provenance.assetId -gt 0 -and $registration.package.packageSha256 -eq $expectedHash -and $registration.package.manifest.version -eq '0.1.1' -and $registration.package.hostSource -eq ('github:'+$repositoryId))
    $history = Read-T05Json (Join-Path $dataDirectory 'Downloads/store-v1/history.json')
    $downloads = @($history.Tasks | Where-Object {$_.Request.AppId -eq $appId -and $_.Request.RepositoryId -eq $repositoryId -and $_.Request.ReleaseId -eq $releaseId})
    if ($downloads.Count -ne 1 -or $downloads[0].Id -notmatch '^[a-f0-9]{32}$') { throw 'Exactly one real public download receipt with a bounded owned cache path is required.' }
    $download = $downloads[0]; $cachedPackage=Join-Path $dataDirectory ('Downloads/store-v1/'+$download.Id+'.autumn')
    Add-SmokeCheck 'download_receipt_binds_release_asset_expected_length_and_hash' ($download.State -eq 6 -and $download.BytesReceived -eq 28871 -and $download.Request.ExpectedBytes -eq 28871 -and $download.Request.Sha256 -eq $expectedHash -and $download.Request.AssetId -eq $registration.provenance.assetId -and $download.Request.Url -eq 'https://github.com/paimeng5201314/chemleap/releases/download/v0.1.1/ChemLeap-0.1.1.autumn')
    Add-SmokeCheck 'actually_downloaded_release_package_hash_matches' ((Get-Item -LiteralPath $cachedPackage).Length -eq 28871 -and (Get-T05Hash $cachedPackage) -eq $expectedHash)
    $installedDirectory = [IO.Path]::GetFullPath($registration.package.directoryPath)
    $allowedAppsRoot = [IO.Path]::GetFullPath((Join-Path $dataDirectory 'Apps')).TrimEnd('\')+'\'
    Add-SmokeCheck 'ordinary_installer_owns_new_content_under_isolated_apps' ($installedDirectory.StartsWith($allowedAppsRoot,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath (Join-Path $installedDirectory 'manifest.json')))
    $report.download=[ordered]@{status_ui=$downloadStatus;cache_relative_path=[IO.Path]::GetRelativePath($dataDirectory,$cachedPackage);sha256=Get-T05Hash $cachedPackage;bytes=(Get-Item -LiteralPath $cachedPackage).Length;repository_id=$download.Request.RepositoryId;release_id=$download.Request.ReleaseId;asset_id=$download.Request.AssetId;url=$download.Request.Url;route=$download.Route}
    $report.public_release_download='passed'; $report.public_release_install='passed'
    $null=Save-SmokeScreenshot $product '02-real-release-installed' -IncludeOverlays
    Click 'StoreHome'; Wait-SmokeDesktop $product
    $null=Wait-SmokeCondition { Visible ('InstalledApp-'+$appId) } 'ordinary installed ChemLeap desktop icon' 15
    $firstInstance=Start-ChemStoreGame; $report.first_instance=$firstInstance; $report.normal_launch='passed'
    Click-ChemLeap 'start-button' '^开始跳跃'
    $null=Wait-SmokeCondition { $score=Get-ChemLeapScore; $null -ne $score -and $score -eq 0 } 'initial real game score zero' 15
    $option=Wait-SmokeCondition { $candidate=Find-ChemLeapElement '' '^\d\s+(?:O₂|O2|Cl₂|Cl2)\s' ([Windows.Automation.ControlType]::Button); if ($candidate -and $candidate.Current.IsEnabled) { return $candidate } } 'actual first chemically compatible platform' 15
    $report.chosen_first_platform=$option.Current.Name
    $inputActions.Add((Invoke-AutumnNativeElementClick -WindowElement $product.Element -Element $option -Process $product.Process))
    $savedScore=Wait-SmokeCondition { $score=Get-ChemLeapScore; if ($null -ne $score -and $score -gt 0) { return $score } } 'score after real installed-game jump' 15
    $savedSubstance=Wait-SmokeCondition { $substance=Get-ChemLeapSubstance; if ($substance -match 'H₂O|H2O|HCl') { return $substance } } 'real reaction product' 15
    Add-SmokeCheck 'ordinary_installed_game_jump_changes_real_score_and_substance' ($savedScore -gt 0 -and $savedSubstance -match 'H₂O|H2O|HCl')
    $report.saved_score=$savedScore; $report.saved_substance=$savedSubstance; $report.native_gameplay='passed'
    $null=Save-SmokeScreenshot $product '03-normal-installed-game-jump' -IncludeOverlays
    Click-ChemLeap 'save-button' '^保存进度$'; Click-Named '允许'; Wait-ChemLeapStatus '已保存'
    $saveRoot=Join-Path $dataDirectory ('Saves/'+$appId)
    $saveFiles=Wait-SmokeCondition { if (Test-Path -LiteralPath $saveRoot) { $items=@(Get-ChildItem -LiteralPath $saveRoot -Filter '*.json' -Recurse -File); if ($items.Count -gt 0) { return ,$items } }; return $null } 'actual host SDK source-bound save' 15
    foreach ($file in $saveFiles) {
        Assert-T05PlainPath $file.FullName
        $record=Read-T05Json $file.FullName; $relative=[IO.Path]::GetRelativePath($saveRoot,$file.FullName).Replace('\','/')
        Add-SmokeCheck 'normal_SDK_save_binds_own_source_guest_and_real_game_score' ($record.schemaVersion -eq 2 -and $record.appId -eq $appId -and $record.sourceKey -match '^[a-f0-9]{64}$' -and $record.accountKey -eq 'guest' -and $record.slot -eq 'game' -and $record.formatVersion -eq 1 -and $relative -eq ($record.sourceKey+'/guest/game.json') -and $record.value.score -eq $savedScore)
    }
    Add-SmokeCheck 'normal_SDK_save_writes_actual_file' ($saveFiles.Count -gt 0)
    Add-SmokeCheck 'normal_instance_uses_actual_SDK_permission_and_save_gateway' (
        @(Read-ChemLeapRuntimeEvents | Where-Object {$_.instanceId -eq $firstInstance -and $_.eventName -eq 'permissions_request_ok'}).Count -gt 0 -and
        @(Read-ChemLeapRuntimeEvents | Where-Object {$_.instanceId -eq $firstInstance -and $_.eventName -eq 'saves_write_ok'}).Count -gt 0)
    $report.save_files=@(foreach ($file in $saveFiles) { [ordered]@{path=[IO.Path]::GetRelativePath($dataDirectory,$file.FullName);sha256=Get-T05Hash $file.FullName;bytes=$file.Length} })
    $report.sdk_save='passed'
    Invoke-AutumnGameShortcut $product 'Desktop'
    Wait-SmokeDesktop $product
    $null=Wait-SmokeCondition { @(Read-ChemLeapRuntimeEvents | Where-Object {$_.instanceId -eq $firstInstance -and $_.eventName -eq 'background' -and $_.blocksMaintenance}).Count -gt 0 } 'ordinary game remains alive in background' 15
    Invoke-SmokePointer $product ('InstalledApp-'+$appId) 'single_click'
    $null=Wait-SmokeCondition { (Get-ChemLeapScore) -eq $savedScore -and (Get-ChemLeapSubstance) -eq $savedSubstance } 'desktop resumes exact game state' 15
    Add-SmokeCheck 'ordinary_return_and_continue_preserves_same_instance_and_game_state' (
        @(Read-ChemLeapRuntimeEvents | Where-Object {$_.appId -eq $appId -and $_.eventName -eq 'navigation_completed'}).Count -eq 1 -and
        @(Read-ChemLeapRuntimeEvents | Where-Object {$_.instanceId -eq $firstInstance -and $_.eventName -eq 'closed'}).Count -eq 0)
    $report.background_continue='passed'
    Stop-ChemStoreGame $firstInstance
    $secondInstance=Start-ChemStoreGame; $report.second_instance=$secondInstance
    Add-SmokeCheck 'desktop_reopen_after_real_end_creates_new_instance' ($secondInstance -ne $firstInstance)
    Click-ChemLeap 'load-button' '^读取存档$'; Wait-ChemLeapStatus '已读取|已恢复' -MayAuthorize
    $null=Wait-SmokeCondition { (Get-ChemLeapScore) -eq $savedScore -and (Get-ChemLeapSubstance) -eq $savedSubstance } 'new ordinary game instance reads original real SDK save' 20
    Add-SmokeCheck 'new_normal_instance_restores_saved_score_and_reaction_product' $true
    Add-SmokeCheck 'new_normal_instance_reads_through_actual_SDK_gateway' (@(Read-ChemLeapRuntimeEvents | Where-Object {$_.instanceId -eq $secondInstance -and $_.eventName -eq 'saves_read_ok'}).Count -gt 0)
    $report.new_instance_readback='passed'
    $null=Save-SmokeScreenshot $product '04-normal-new-instance-save-readback' -IncludeOverlays
    Stop-ChemStoreGame $secondInstance
    Click 'SettingsButton'; Toggle 'SettingsNavDiagnostics' $true
    Assert-ChemStoreDeveloperDisabled 'final_native_settings' -InspectUi
    Click 'SettingsHomeButton'; Wait-SmokeDesktop $product
    Add-SmokeCheck 'developer_entry_remains_hidden_after_gameplay' (-not(Visible 'DeveloperButton'))
    Add-SmokeCheck 'unrelated_working_directory_has_no_user_data' (-not(Test-Path -LiteralPath (Join-Path $cwd 'AutumnOS_Data')))
    $report.runtime_evidence=@(Read-ChemLeapRuntimeEvents | Where-Object {$_.appId -eq $appId} | Select-Object timestampUtc,buildId,instanceId,eventName,state,blocksMaintenance,appId,packageSha256,networkSandboxVerified)
    Close-SmokeProduct $product
    if ($SingleFileBuildId) {
        Add-SmokeCheck 'single_file_inner_bootstrap_exits_after_normal_client_close' ($product.InnerProcess.WaitForExit(10000) -and $product.InnerProcess.ExitCode -eq 0)
        Add-SmokeCheck 'single_file_outer_entry_exits_after_normal_client_close' ($product.EntryProcess.WaitForExit(10000) -and $product.EntryProcess.ExitCode -eq 0)
        $report.single_file_bootstrap_exit_codes=[ordered]@{outer=$product.EntryProcess.ExitCode;inner=$product.InnerProcess.ExitCode;business=$product.Process.ExitCode}
        Assert-ChemStoreSingleFileRoot 'after_real_store_gameplay'
        Add-SmokeCheck 'single_file_entry_bytes_remain_exact_after_store_play' ((Get-T05Hash $report.tested_executable) -eq $singleFilePackage.executable_sha256)
    }
    $report.status='passed'
} catch {
    $report.failure=$_.Exception.Message; $report.failure_type=$_.Exception.GetType().FullName; $report.failure_stack=$_.ScriptStackTrace
    Write-Warning ('ChemLeap normal Store smoke: '+$report.failure)
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
    if (Test-Path -LiteralPath $ReportDirectory) { $report | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'chemleap-store-smoke.json') -Encoding utf8 }
    foreach ($process in $ownedProcesses) { $process.Dispose() }; foreach ($entry in $ownedEntryProcesses) { $entry.Dispose() }
}
if ($report.status -ne 'passed') { throw "ChemLeap normal Store smoke failed; evidence preserved at $ReportDirectory" }
