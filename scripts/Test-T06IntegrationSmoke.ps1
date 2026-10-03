#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AExecutablePath,
    [Parameter(Mandatory)][string]$BPayloadDirectory,
    [Parameter(Mandatory)][string]$BadPayloadDirectory,
    [Parameter(Mandatory)][string]$FeedDirectory,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [string]$IsolationDirectory,
    [ValidateRange(15,90)][int]$WindowTimeoutSeconds=45,
    [ValidateRange(35,120)][int]$BlockedObservationSeconds=35,
    [ValidateRange(120,600)][int]$UpdateTimeoutSeconds=360
)
# Run exclusively: real native input and the product's same-user maintenance mutex are shared.
# This is ONE installed A session and ONE data root: Store -> app -> B -> bad build -> B.
# No other smoke driver's executable body is run. All data is written through real UI/SDK actions.
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/Smoke-NativeAppInput.ps1"
. "$PSScriptRoot/T06-NativeHelpers.ps1"
foreach($name in @('AExecutablePath','BPayloadDirectory','BadPayloadDirectory','FeedDirectory','ReportDirectory')){
    Set-Variable -Name $name -Value ([IO.Path]::GetFullPath((Get-Variable $name -ValueOnly)))
}
if(Test-Path -LiteralPath $ReportDirectory){throw 'Use a new report directory; historical evidence is retained.'}
$smokeId='T06-integration-'+[Guid]::NewGuid().ToString('N')
$testScope=Join-Path $projectRoot "artifacts/smoke/$smokeId"
if($IsolationDirectory){
    $testScope=[IO.Path]::GetFullPath($IsolationDirectory)
    $allowed=[IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')).TrimEnd('\')+'\'
    if(-not $testScope.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or
       $testScope -match '[\\/]AutumnOS_Data(?:[\\/]|$)' -or (Test-Path -LiteralPath $testScope)){
        throw 'An explicit diagnostic isolation directory must be new and inside project artifacts.'
    }
    for($ancestor=[IO.Path]::GetDirectoryName($testScope);$ancestor;$ancestor=[IO.Path]::GetDirectoryName($ancestor)){
        if((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){
            throw 'Redirected diagnostic isolation directory forbidden.'
        }
    }
}
New-Item -ItemType Directory -Path $ReportDirectory,$testScope|Out-Null
$reportPath=Join-Path $ReportDirectory 'integration-smoke.json'
$checks=[Collections.Generic.List[object]]::new();$screenshots=[Collections.Generic.List[object]]::new()
$inputActions=[Collections.Generic.List[object]]::new();$windowGeometry=[Collections.Generic.List[object]]::new()
$ownedProcesses=[Collections.Generic.List[Diagnostics.Process]]::new();$ownedEntryProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$trackedProcesses=[Collections.Generic.List[object]]::new();$repeatLaunches=[Collections.Generic.List[object]]::new()
$scenarioResults=[Collections.Generic.List[object]]::new()
$script:initialPointerPosition=$null;$script:product=$null;$script:scenario=$null
$script:stageDirectory=Join-Path $testScope '同一安装 中文目录'
$script:dataDirectory=Join-Path $stageDirectory 'AutumnOS_Data'
$script:runtimeLog=Join-Path $dataDirectory 'Logs/runtime-events.jsonl'
$report=[ordered]@{
    schema_version=1;task_id='T06';checkpoint='T06-A';status='failed';smoke_id=$smokeId
    started_utc=[DateTimeOffset]::UtcNow.ToString('o');a_executable=$AExecutablePath;test_scope=$testScope
    tested_executable=(Join-Path $stageDirectory 'AutumnOS.exe');build_id=$null;source_snapshot_id=$null
    driver_sha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    helper_sha256=(Get-FileHash -LiteralPath "$PSScriptRoot/T06-NativeHelpers.ps1" -Algorithm SHA256).Hash.ToLowerInvariant()
    store_source='explicitly enabled built-in controlled loopback fixture; not production GitHub releases'
    update_source='compile-time isolated local feed; real supplied signatures and delivered payloads'
    feed_directory=$FeedDirectory;test_signatures='supplied_by_caller';synthetic_release_list=$true
    user_data_writes='real UI and app SDK only';private_keys_read=$false;public_release=$false
    production_github_install='not_run';real_identity_profile_success='not_run_T03_deferred'
    identity_profile_guest_and_denial='not_run';account_switch='not_run';physical_power_loss='not_run'
    setup='deferred_by_user';general_community_app_sandbox='not_run';clean_machine_windows10='not_run'
    starting_suspended_closing_races='not_run_in_this_driver';checks=$checks;screenshots=$screenshots
    input_actions=$inputActions;window_geometry=$windowGeometry;scenarios=$scenarioResults
    process_ids=@();bootstrap_process_ids=@();remaining_processes=@();cleanup='not_run'
}
$desktopHelper=Join-Path $PSScriptRoot 'Test-DesktopInteractionSmoke.ps1'
$helperAst=Import-T06Functions $desktopHelper -Exclude @('Read-SmokeEvents')
$script:t05LegacyFocus=(Get-Item Function:Focus-SmokeProduct).ScriptBlock
$null=Import-T06Functions (Join-Path $PSScriptRoot 'Test-T05UpdateSmoke.ps1') -Exclude @('Invoke-T05Scenario','Read-T05Json')
$null=Import-T06Functions (Join-Path $PSScriptRoot 'Test-T04StoreSmoke.ps1') -Names @(
    'Show-Element','Click','Find','Visible','Require','Toggle','Value','Find-Named','Click-Named','Click-Web','Probe-Input','Input-Value','Set-Input','Wait-Download')
# Atomic-journal observers must share Delete; do not lock the product's rename operation.
$waitBody=(Get-Item Function:Wait-T05Transaction).ScriptBlock.ToString()
$waitBody=$waitBody.Replace('Copy-Item -LiteralPath $journalPath -Destination','Copy-T06ObservedFile -Source $journalPath -Destination')
$anchor='if($journal.root -ne $stageDirectory -or $journal.toBuildId -ne $TargetBuild)'
if(-not $waitBody.Contains($anchor)){throw 'Maintained transaction helper changed; review the adaptation.'}
$waitBody=$waitBody.Replace($anchor,@'
if($scenario.previous_transaction_id -and $journal.transactionId -eq $scenario.previous_transaction_id -and $journal.phase -in @('committed','rolledBack')){Start-Sleep -Milliseconds 200;continue}
            if($journal.root -ne $stageDirectory -or $journal.toBuildId -ne $TargetBuild)
'@)
Set-Item Function:Wait-T05Transaction -Value ([ScriptBlock]::Create($waitBody))
$report.maintained_helpers=@(foreach($name in @('Test-DesktopInteractionSmoke.ps1','Test-T05UpdateSmoke.ps1','Test-T04StoreSmoke.ps1','Smoke-NativeAppInput.ps1')){
    [ordered]@{path=$name;sha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()}
})

function New-T06Scenario([string]$Name,[bool]$Rollback){
    if(@($repeatLaunches|Where-Object {-not $_.Finished}).Count){throw 'Prior maintenance entry observation is incomplete.'}
    $repeatLaunches.Clear()
    $script:scenario=[ordered]@{name=$Name;status='in_progress';rollback=$Rollback;maximum_live_business_processes=1
        game_instance=$null;instance_history=@();transaction=$null;maintenance_repeated_launches=@();previous_transaction_id=$null
        entry=$report.tested_executable;stable_entry_hash=(Get-T05Hash $report.tested_executable)
        a_client_hash=(Get-T05Hash (Join-Path $stageDirectory 'AutumnOS.Client.dll'))
        a_build_id=(Read-T05Json (Join-Path $stageDirectory 'autumn.install.json')).buildId
        a_business_pid=$product.Process.Id;shortcut=$originalShortcut;shortcut_hash=$originalShortcutHash}
    $journalPath=Join-Path $stageDirectory '.autumnos-update/journal.json'
    if(Test-Path -LiteralPath $journalPath){$scenario.previous_transaction_id=(Read-T05Json $journalPath).transactionId}
    $scenarioResults.Add($scenario)
}
function Invoke-T06DesktopAppEntry {
    # The cross-module chain uses the real button's accessibility Click entry. It still
    # executes OnManagedIconClick/ConsumeHeldClick and all product admission checks.
    # Physical desktop pointer gestures are exercised by the separate DesktopSmoke.
    Assert-T05OwnedWindow $product
    $element=Wait-SmokeCondition {Find-VisibleSmokeElement $product.Element 'InstalledApp-cn.labchronicles.storeprobe'} 'visible installed desktop entry'
    if($element.Current.ProcessId -ne $product.Process.Id -or -not $element.Current.IsEnabled){throw 'Installed desktop entry ownership or enabled state changed.'}
    $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $inputActions.Add([ordered]@{kind='UIA_InvokePattern_real_desktop_button_Click';automation_id='InstalledApp-cn.labchronicles.storeprobe';process_id=$product.Process.Id
        product_guards='unchanged_OnManagedIconClick_and_ConsumeHeldClick';physical_pointer_assertion='not_run_this_cross_module_entry'})
}
function Open-T06App {
    $before=@(Read-SmokeEvents $runtimeLog).Count
    $opening=[ordered]@{started_utc=[DateTimeOffset]::UtcNow.ToString('o');timeout_seconds=$WindowTimeoutSeconds;status='waiting_for_native_navigation';events=@()}
    if(-not $scenario.Contains('app_open_observations')){$scenario.app_open_observations=@()}
    $scenario.app_open_observations+=@($opening)
    $watch=[Diagnostics.Stopwatch]::StartNew()
    Invoke-T06DesktopAppEntry
    # First WebView initialization can exceed the editor helper's six-second UIA lookup.
    # Wait for this action's real completed navigation before looking for page controls.
    $event=Wait-SmokeCondition {
        $events=@(Read-SmokeEvents $runtimeLog|Select-Object -Skip $before)
        $opening.events=$events
        $failure=@($events|Where-Object {$_.eventName -in @('navigation_failed','process_failed','resource_release_failed','startup_failed','startup_cancelled') -or $_.state -eq 'Closed'})
        if($failure.Count){throw ('Installed app failed before completed navigation: '+$failure[-1].eventName)}
        $ready=@($events|Where-Object {$_.eventName -eq 'navigation_completed' -and $_.state -eq 'Foreground' -and $_.blocksMaintenance})
        if($ready.Count){return $ready[-1]}
        return $null
    } 'installed app completed native navigation and entered Foreground' $WindowTimeoutSeconds
    $opening.navigation_seconds=$watch.Elapsed.TotalSeconds;$opening.status='native_navigation_ready_waiting_for_editor'
    $null=Probe-Input
    $opening.editor_ready_seconds=$watch.Elapsed.TotalSeconds;$opening.status='ready'
    $scenario.game_instance=[string]$event.instanceId
    $scenario.instance_history+=@([ordered]@{instance_id=$scenario.game_instance;host_pid=$product.Process.Id;timestamp_utc=[DateTimeOffset]::UtcNow.ToString('o')})
    Add-SmokeCheck ($scenario.name+'_installed_app_is_real_internal_foreground_instance') ($event.blocksMaintenance -and [bool](Find 'ManagedAppHome'))
}
function Save-T06AppText([string]$Text,[switch]$First){
    Set-Input $Text;Click-Web '保存测试文字'
    if($First){Click-Named '允许'}
    $null=Wait-SmokeCondition {Find-Named '已保存到独立游客测试存档。' ([Windows.Automation.ControlType]::Text)} 'real SDK save completion' 15
    $saved=@(Get-ChildItem -LiteralPath (Join-Path $dataDirectory 'Saves/cn.labchronicles.storeprobe') -Recurse -File -Filter 'probe.json')
    Add-SmokeCheck ($scenario.name+'_sdk_saved_one_source_scoped_guest_file') ($saved.Count -eq 1 -and $saved[0].FullName -match '[\\/]guest[\\/]probe.json$')
    $null=Wait-SmokeCondition {(Read-T05Json $saved[0].FullName).value.text -eq $Text} 'saved bytes reflect the user text' 10
    $scenario.saved_text=$Text;$scenario.save_path=[IO.Path]::GetRelativePath($dataDirectory,$saved[0].FullName)
}
function Confirm-T06PublicDefaultAndRestoreApp {
    Click 'StoreButton';$null=Require 'StoreSource'
    Add-SmokeCheck ($scenario.name+'_restart_does_not_silently_reenable_test_store') ((Require 'StoreSource').Current.Name -ne '本地集成测试数据，不是 GitHub 实时结果')
    $scenario.store_source_after_restart=(Require 'StoreSource').Current.Name
    Click 'StoreHome';Wait-SmokeDesktop $product
    Enable-T06StoreSource;Click 'StoreNav-installed'
    $null=Require 'InstalledOpen-cn.labchronicles.storeprobe'
    Add-SmokeCheck ($scenario.name+'_app_pin_and_preview_policy_restored') (
        (Require 'AppPin-cn.labchronicles.storeprobe').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::On -and
        (Require 'AppPreview-cn.labchronicles.storeprobe').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::On)
    Click 'StoreHome';Wait-SmokeDesktop $product;Open-T06App
    Click-Web '读取测试文字';Wait-T06ProbeText $scenario.saved_text
    Add-SmokeCheck ($scenario.name+'_actual_SDK_read_after_host_restart_matches_original_save') $true
    Save-T05Screenshot 'restored-real-installed-app-save';Close-T06ManagedApp
}
function Invoke-T06HostHop([string]$PayloadDirectory,[bool]$Rollback){
    $manifest=Publish-T05SignedFixture $PayloadDirectory
    $scenario.target_build_id=$manifest.buildId;$scenario.target_version=$manifest.version
    $unsaved='派蒙 T06 未保存 '+$scenario.name
    Set-Input $unsaved
    Click 'ManagedAppHome';Wait-SmokeDesktop $product;Show-T05Updates
    Click-T05 'CheckSystemUpdate'
    $null=Wait-SmokeCondition {
        $text=(Find-T05 'UpdateStatus').Current.Name
        if($text -match '更新未完成|自动安装尚未配置'){throw $text}
        if($text -match '已准备，尚未安装'){return $true}
        $download=Find-T05 'DownloadSystemUpdate';if($download -and $download.Current.IsEnabled){$download.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}
        return $false
    } 'real candidate download, hash and trusted signature verification' $UpdateTimeoutSeconds
    $scenario.staged_status=(Find-T05 'UpdateStatus').Current.Name;$scenario.candidate_text=(Find-T05 'UpdateCandidate').Current.Name
    Add-SmokeCheck ($scenario.name+'_signed_host_candidate_staged') ($scenario.candidate_text.Contains([string]$manifest.version))
    Add-SmokeCheck ($scenario.name+'_host_preview_remains_off_independent_of_app_preview') ((Find-T05 'PreviewUpdates').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::Off)
    $null=Wait-SmokeCondition {(Find-T05 'UpdateWaitingReason').Current.Name -match '后台运行'} 'actual installed-app background blocks update'
    Save-T05Screenshot 'background-wait';Assert-T06AppBlocks 'Background';Test-T06NormalRecall
    Click-T05 'SettingsHomeButton';Wait-SmokeDesktop $product
    $before=@(Read-SmokeEvents $runtimeLog).Count
    Invoke-T06DesktopAppEntry
    $event=Wait-SmokeNewForeground $runtimeLog $before $scenario.game_instance
    Wait-T06ProbeText $unsaved
    Add-SmokeCheck ($scenario.name+'_same_app_instance_and_unsaved_input_after_repeat_entry') ($event.instanceId -eq $scenario.game_instance)
    Assert-T06AppBlocks 'Foreground';Save-T05Screenshot 'foreground-unsaved-retained'
    Save-T06AppText $unsaved
    $scenario.data_before=@(Get-T06ProtectedData)
    $beforeReceipt=Read-T05Json (Join-Path $stageDirectory 'autumn.install.json')
    Close-T06ManagedApp
    # No forced restart action: default-on idle maintenance obtains the real coordinator lease.
    $script:product=Wait-T05Transaction $manifest.buildId $Rollback
    Wait-SmokeDesktop $product
    $expectedBuild=if($Rollback){$scenario.a_build_id}else{$manifest.buildId}
    $receipt=Read-T05Json (Join-Path $stageDirectory 'autumn.install.json')
    $metadata=Read-T05BuildMetadata $stageDirectory
    Add-SmokeCheck ($scenario.name+'_actual_assembly_receipt_and_expected_build_agree') ($receipt.buildId -eq $expectedBuild -and $metadata.BuildId -eq $expectedBuild -and $metadata.SourceSnapshotId -match '^sha256:[0-9a-f]{64}$')
    Add-SmokeCheck ($scenario.name+'_stable_entry_bytes_preserved') ((Get-T05Hash $report.tested_executable) -eq $scenario.stable_entry_hash)
    $files=if($Rollback){$beforeReceipt.files}else{$manifest.files}
    $differences=@(foreach($file in $files){if((Get-T05Hash (Join-Path $stageDirectory $file.path)) -ne $file.sha256){$file.path}})
    Add-SmokeCheck ($scenario.name+'_all_managed_program_files_match_expected_version') ($files.Count -gt 100 -and $differences.Count -eq 0)
    $scenario.verified_program_file_count=$files.Count
    if($Rollback){
        Add-SmokeCheck ($scenario.name+'_bad_build_persistently_blocked') ((Read-T05Json (Join-Path $stageDirectory '.autumnos-update/security.json')).failedBuilds -contains $manifest.buildId)
        Add-SmokeCheck ($scenario.name+'_compatible_previous_B_actual_bytes_restored') ((Get-T05Hash (Join-Path $stageDirectory 'AutumnOS.Client.dll')) -eq $scenario.a_client_hash)
    }else{
        $resultPath=Join-Path $stageDirectory ('.autumnos-update/'+$scenario.transaction.transactionId+'/result.json')
        $result=Wait-SmokeCondition {if(Test-Path -LiteralPath $resultPath){Read-T05Json $resultPath}} 'independent core-health confirmation'
        Add-SmokeCheck ($scenario.name+'_core_health_confirmed_before_success') ($result.outcome -eq 'committed' -and $result.coreHealthVerified -and $result.buildId -eq $manifest.buildId)
        Add-SmokeCheck ($scenario.name+'_actual_B_code_differs_from_A') ((Get-T05Hash (Join-Path $stageDirectory 'AutumnOS.Client.dll')) -ne $scenario.a_client_hash)
        Copy-T06ObservedFile $resultPath (Join-Path $ReportDirectory ($scenario.name+'-health-result.json'))
    }
    Assert-T06ProtectedData $scenario.data_before $scenario.name
    Show-T05Updates;Toggle-T05 'SettingsNavAbout' $true
    $null=Wait-SmokeCondition {(Find-T05 'AboutBuild').Current.Name -eq $expectedBuild} 'native About asynchronously shows actual result identity' 10
    Add-SmokeCheck ($scenario.name+'_native_about_matches_actual_build') $true;Save-T05Screenshot 'native-build-after-transaction'
    Click-T05 'SettingsHomeButton';Wait-SmokeDesktop $product
    Confirm-T06PublicDefaultAndRestoreApp
    if($Rollback){
        Show-T05Updates;Click-T05 'CheckSystemUpdate'
        $null=Wait-SmokeCondition {(Find-T05 'CheckSystemUpdate').Current.IsEnabled} 'explicit bad-build recheck finishes'
        Add-SmokeCheck ($scenario.name+'_failed_candidate_not_staged_again') (-not(Find-T05 'RestartSystemUpdate').Current.IsEnabled)
        $watch=[Diagnostics.Stopwatch]::StartNew()
        while($watch.Elapsed.TotalSeconds -lt 35){Observe-T05OwnedDescendants;if($product.Process.HasExited -or (Read-T05Json (Join-Path $stageDirectory '.autumnos-update/journal.json')).transactionId -ne $scenario.transaction.transactionId){throw 'Bad build caused another exit/update/rollback loop.'};Start-Sleep -Milliseconds 300}
        Add-SmokeCheck ($scenario.name+'_no_bad_version_loop_beyond_idle_delay') $true
        Click-T05 'SettingsHomeButton';Wait-SmokeDesktop $product
    }
    Add-SmokeCheck ($scenario.name+'_single_business_instance_through_handoff') ($scenario.maximum_live_business_processes -eq 1)
    $busy=@($scenario.maintenance_repeated_launches|Where-Object {$_.status -like 'passed_*'})
    $scenario.maintenance_repeat_launch=if($busy.Count){'passed_observed_busy_or_deferred'}else{'not_run_interval_missed'}
    $scenario.result_build=$expectedBuild;$scenario.status='passed'
}

try{
    if(-not $IsWindows){throw 'Real interactive Windows desktop required.'}
    if(@(Get-Process -Name AutumnOS,AutumnOS.Client,AutumnOS.Updater -ErrorAction SilentlyContinue).Count){throw 'Existing product process detected; no existing user instance is activated or closed.'}
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    Add-SmokeCheck 'standard_user' (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
    foreach($path in @($AExecutablePath,$BPayloadDirectory,$BadPayloadDirectory,$FeedDirectory,$ReportDirectory)){Assert-T05PlainPath $path}
    if([IO.Path]::GetFileName($AExecutablePath) -ne 'AutumnOS.exe'){throw 'Use a delivered stable entry with the actual update protocol.'}
    $sourceRoot=Split-Path $AExecutablePath -Parent
    $assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $sourceRoot 'AutumnOS.Update.dll')))
    $stream=$assembly.GetManifestResourceStream('AutumnOS.Update.TestFeed.txt')
    if(-not $stream){throw 'A must explicitly compile the isolated test update feed.'}
    $reader=[IO.StreamReader]::new($stream);try{$embeddedFeed=$reader.ReadToEnd().Trim()}finally{$reader.Dispose()}
    Add-SmokeCheck 'caller_feed_matches_compiled_isolated_feed' ([string]::Equals([IO.Path]::GetFullPath($embeddedFeed),$FeedDirectory,[StringComparison]::OrdinalIgnoreCase))
    New-Item -ItemType Directory -Path $FeedDirectory -Force|Out-Null
    if(Test-Path -LiteralPath (Join-Path $FeedDirectory 'releases.json')){Copy-T06ObservedFile (Join-Path $FeedDirectory 'releases.json') (Join-Path $ReportDirectory 'feed-before.json')}
    New-Item -ItemType Directory -Path $stageDirectory|Out-Null
    foreach($item in Get-ChildItem -LiteralPath $sourceRoot -Force){
        if($item.Name -in @('AutumnOS_Data','.autumnos-update')){continue}
        Assert-T05PlainPath $item.FullName
        if($item.PSIsContainer){foreach($child in Get-ChildItem -LiteralPath $item.FullName -Recurse -Force){Assert-T05PlainPath $child.FullName}}
        Copy-Item -LiteralPath $item.FullName -Destination $stageDirectory -Recurse
    }
    Add-SmokeCheck 'fresh_copy_excludes_user_data' (-not(Test-Path -LiteralPath $dataDirectory))
    $receipt=Read-T05Json (Join-Path $stageDirectory 'autumn.install.json');$metadata=Read-T05BuildMetadata $stageDirectory
    $report.build_id=$receipt.buildId;$report.source_snapshot_id=$metadata.SourceSnapshotId
    Add-SmokeCheck 'actual_A_assembly_matches_delivery_receipt' ($metadata.BuildId -eq $receipt.buildId -and $metadata.SourceSnapshotId -match '^sha256:[0-9a-f]{64}$')
    $report.a_program_file_count=$receipt.files.Count
    foreach($file in $receipt.files){if((Get-T05Hash (Join-Path $stageDirectory $file.path)) -ne $file.sha256){throw 'Source or isolated A copy differs from its delivery receipt.'}}
    $desktopRuntime=Join-Path $projectRoot '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')){Add-Type -Path (Join-Path $desktopRuntime $name)}
    Initialize-T05DialogNative;Initialize-T05FocusNative
    $nativeStrings=@($helperAst.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('namespace AutumnDesktopInteractionSmoke')},$true))
    if($nativeStrings.Count -ne 1){throw 'Maintained native helper declaration missing.'}
    if(-not('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){Add-Type -TypeDefinition $nativeStrings[0].Value}
    $originalShortcut=Join-Path $testScope 'T06 派蒙 原入口.lnk'
    $shell=New-Object -ComObject WScript.Shell;$link=$shell.CreateShortcut($originalShortcut)
    $link.TargetPath=$report.tested_executable;$link.WorkingDirectory=$stageDirectory;$link.Description='派蒙 · T06 独立本地演练';$link.Save()
    $originalShortcutHash=Get-T05Hash $originalShortcut
    Write-T05FeedList @() 'initial-empty-feed.json'
    $cwd=Join-Path $testScope '不同工作目录';New-Item -ItemType Directory -Path $cwd|Out-Null
    $script:product=Start-SmokeProduct $cwd
    Register-T05Process $product.EntryProcess 'AutumnOS.exe';Register-T05Process $product.Process 'AutumnOS.Client.exe' $product.EntryProcess.Id
    New-T06Scenario 'A-to-B' $false
    Click-T05 'HelloNextButton';Click-T05 'BrandNextButton';Click-T05 'PrepareDesktopButton';Wait-SmokeDesktop $product
    Show-T05Updates
    Add-SmokeCheck 'new_user_auto_on_host_preview_off' (
        (Find-T05 'AutomaticUpdates').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::On -and
        (Find-T05 'PreviewUpdates').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::Off)
    Toggle-T05 'SettingsNavAppearance' $true;Click-T05 'DarkThemeButton';Click-T05 'NightWallpaperButton'
    Click-T05 'SettingsHomeButton';Wait-SmokeDesktop $product
    Click 'StoreButton';$null=Require 'StoreSource'
    $report.initial_store_source=(Require 'StoreSource').Current.Name
    Add-SmokeCheck 'normal_store_source_precedes_explicit_test_choice' ($report.initial_store_source -ne '本地集成测试数据，不是 GitHub 实时结果')
    Click 'StoreHome';Wait-SmokeDesktop $product;Enable-T06StoreSource
    Value 'StoreSearch' 'store-probe';Click 'StoreRefresh'
    $null=Wait-SmokeCondition {(Find 'StoreSearch').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq 'store-probe' -and (Visible 'StoreRepository-90004001') -and (Find 'StoreStatus').Current.Name -match '(已读取|缓存) 1 个仓库'} 'controlled Store search result' 15
    Add-SmokeCheck 'same_A_session_search_request_and_result_observed' $true
    Save-T05Screenshot 'store-search';Click 'StoreRepository-90004001';$null=Require 'StoreVersionChannel'
    Select-T06StoreChannel '仅预览';$null=Require 'StoreInstall-40003'
    Add-SmokeCheck 'real_details_preview_filter_excludes_stable_release' (-not(Find 'StoreInstall-40001'))
    Select-T06StoreChannel '稳定版本';$null=Require 'StoreInstall-40001'
    Add-SmokeCheck 'real_details_stable_filter_excludes_preview_release' (-not(Find 'StoreInstall-40003'))
    Save-T05Screenshot 'store-stable-details';Click 'StoreInstall-40001';Click-Named '下载并安装'
    $scenario.download_progress=Wait-Download '1.0.0' '下载中|排队|正在下载|校验|安装';Save-T05Screenshot 'store-download'
    $null=Wait-Download '1.0.0' '已安装'
    $registration=Get-T06Registration
    Add-SmokeCheck 'store_download_verify_install_produces_real_registration' ($registration.package.manifest.version -eq '1.0.0' -and $registration.source.kind -eq 'test' -and $registration.source.repositoryId -eq 90004001)
    Click 'StoreNav-installed';$null=Require 'InstalledOpen-cn.labchronicles.storeprobe'
    Toggle 'AppPin-cn.labchronicles.storeprobe' $true;Toggle 'AppPreview-cn.labchronicles.storeprobe' $true
    $null=Wait-SmokeCondition {$item=Get-T06Registration;$item.pinnedVersion -eq '1.0.0' -and $item.allowPreview} 'installed-app policy persistence' 10
    Click 'StoreHome';Wait-SmokeDesktop $product;Open-T06App
    Click-Web '申请资料权限';Click-Named '拒绝' 'CloseButton'
    $null=Wait-SmokeCondition {Find-Named '资料权限：denied' ([Windows.Automation.ControlType]::Text)} 'real profile permission denial' 10
    Click-Web '读取最小资料'
    $null=Wait-SmokeCondition {Find-Named '操作未完成：AUTH_REQUIRED' ([Windows.Automation.ControlType]::Text)} 'real guest identity failure without fake profile' 10
    Add-SmokeCheck 'actual_guest_profile_request_returns_AUTH_REQUIRED_after_explicit_denial' $true
    $report.identity_profile_guest_and_denial='passed';Save-T05Screenshot 'real-profile-denial'
    Save-T06AppText ('派蒙 T06 首次存档 '+$smokeId) -First
    Invoke-T06HostHop $BPayloadDirectory $false
    New-T06Scenario 'B-bad-to-B' $true
    Open-T06App;Click-Web '读取测试文字'
    Wait-T06ProbeText $scenarioResults[0].saved_text
    Invoke-T06HostHop $BadPayloadDirectory $true
    Close-SmokeProduct $product
    $script:product=Start-T05Shortcut;Wait-SmokeDesktop $product
    Show-T05Updates;Toggle-T05 'SettingsNavAbout' $true
    $null=Wait-SmokeCondition {(Find-T05 'AboutBuild').Current.Name -eq $scenario.result_build} 'original shortcut restarts restored B' 10
    Add-SmokeCheck 'original_shortcut_still_starts_recovered_B' $true;Save-T05Screenshot 'original-shortcut-restart'
    Click-T05 'SettingsHomeButton';Wait-SmokeDesktop $product;Close-SmokeProduct $product
    Add-SmokeCheck 'different_working_directory_did_not_receive_data' (-not(Test-Path -LiteralPath (Join-Path $cwd 'AutumnOS_Data')))
    $report.status='passed'
}catch{
    $report.failure_type=$_.Exception.GetType().FullName;$report.failure=$_.Exception.Message
    $report.failure_stack=$_.ScriptStackTrace
    if($scenario){$scenario.status='failed'}
    Write-Warning ('T06 integration: '+$report.failure)
    if($product -and -not $product.Process.HasExited){try{Save-T05Screenshot 'failure'}catch{}}
}finally{
    $clean=$true
    if($scenario){try{Observe-T05OwnedDescendants;Poll-T05MaintenanceRepeats}catch{$clean=$false}}
    foreach($process in $ownedProcesses){try{if(-not $process.HasExited){$process.Refresh();if($process.MainWindowHandle -eq [IntPtr]::Zero -or -not $process.CloseMainWindow() -or -not $process.WaitForExit(10000)){$clean=$false;$report.remaining_processes+=@([ordered]@{pid=$process.Id;action='left_running_no_forced_termination'})}}}catch{$clean=$false}}
    foreach($entry in $ownedEntryProcesses){try{if(-not $entry.WaitForExit(10000)){$clean=$false;$report.remaining_processes+=@([ordered]@{pid=$entry.Id;action='bootstrap_left_running'})}}catch{$clean=$false}}
    if($scenario){try{Poll-T05MaintenanceRepeats}catch{$clean=$false}}
    foreach($tracked in $trackedProcesses|Where-Object Kind -eq 'AutumnOS.Updater.exe'){try{if(-not $tracked.Process.WaitForExit(10000)){$clean=$false;$report.remaining_processes+=@([ordered]@{pid=$tracked.Id;action='updater_left_running'})}}catch{$clean=$false}}
    Restore-AutumnNativePointer
    if($script:initialPointerPosition -and ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){$null=[AutumnDesktopInteractionSmoke.NativeWindows]::SetCursorPos($script:initialPointerPosition.X,$script:initialPointerPosition.Y)}
    $report.cleanup=if($clean){'passed'}else{'failed'};if(-not $clean){$report.status='failed'}
    $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o');$report.check_count=$checks.Count;$report.passed_checks=@($checks|Where-Object status -eq 'passed').Count
    $report.process_evidence=@($trackedProcesses|ForEach-Object {[ordered]@{pid=$_.Id;start_utc_ticks=$_.StartTicks;path=$_.Path;parent_pid=$_.ParentId;kind=$_.Kind}})
    $report|ConvertTo-Json -Depth 24|Set-Content -LiteralPath $reportPath -Encoding utf8
    foreach($process in $ownedProcesses){$process.Dispose()};foreach($entry in $ownedEntryProcesses){$entry.Dispose()}
    foreach($tracked in $trackedProcesses|Where-Object Kind -eq 'AutumnOS.Updater.exe'){$tracked.Process.Dispose()}
    Write-Host "REPORT $reportPath"
}
if($report.status -ne 'passed'){throw "T06 native integration did not pass; preserved evidence: $reportPath"}
