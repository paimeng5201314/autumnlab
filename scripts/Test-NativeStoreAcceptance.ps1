#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^T06-[A-Za-z0-9_.-]{1,85}$')][string]$BuildId,
    [ValidateSet('Preflight','ServerNegative','ClientTrial')][string]$Stage='Preflight',
    [ValidateRange(15,180)][int]$WindowTimeoutSeconds=60,
    [ValidateRange(30,300)][int]$DownloadTimeoutSeconds=120
)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidence=Join-Path $workspace "artifacts/builds/$BuildId"
$ReportDirectory=Join-Path $evidence ('native-store-'+[Guid]::NewGuid().ToString('N'))
$appId='cn.labchronicles.chemleap'
$repositoryId=1414175562L
$releaseId=415748507L
$expectedHash='e00ecf58d1c4a5d1190163cd02a13199135809ad0a782d5b830274e9642da0a2'
$checks=[Collections.Generic.List[object]]::new()
$screenshots=[Collections.Generic.List[object]]::new()
$windowGeometry=[Collections.Generic.List[object]]::new()
$inputActions=[Collections.Generic.List[object]]::new()
$trackedProcesses=[Collections.Generic.List[object]]::new()
$ownedProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$ownedEntryProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$script:product=$null
$script:initialPointerPosition=$null
$report=[ordered]@{
    schema_version=1;scope='ordinary_public_store_ChemLeap_first_frame_and_original_group_exit';mode=$Stage;status='failed';
    build_id=$BuildId;source_snapshot_id=$null;tested_executable=$null;process_ids=@();bootstrap_process_ids=@()
    package_verified='not_run';store_install='not_run';open_attempt='not_run';expected_server_rejection='not_run'
    native_first_frame='not_run';operator_gameplay='not_run';physical_keyboard_ime='not_run';native_group_retirement='not_run'
    clipboard_matrix='not_run';network_receiver_matrix='not_run';account_matrix='not_run';complete_isolation=$false
    cleanup='not_run';checks=$checks;screenshots=$screenshots;input_actions=$inputActions;window_geometry=$windowGeometry
}
. (Join-Path $PSScriptRoot 'T06-NativeHelpers.ps1')
$desktopAst=Import-T06Functions (Join-Path $PSScriptRoot 'Test-DesktopInteractionSmoke.ps1')
$script:t05LegacyFocus=(Get-Item Function:Focus-SmokeProduct).ScriptBlock
$null=Import-T06Functions (Join-Path $PSScriptRoot 'Test-T05UpdateSmoke.ps1') -Names @(
    'Assert-T05OwnedWindow','Get-T05WindowGeometry','Focus-SmokeProduct','Initialize-T05FocusNative','Assert-T05PlainPath','Invoke-T05SharingRead','Get-T05Hash')
$null=Import-T06Functions (Join-Path $PSScriptRoot 'Test-T04StoreSmoke.ps1') -Names @('Show-Element','Click','Find','Visible','Require','Toggle','Click-Named','Value')
$null=Import-T06Functions (Join-Path $PSScriptRoot 'Test-ChemLeapNativeSmoke.ps1') -Names @('Read-ChemLeapRuntimeEvents')
$null=Import-T06Functions (Join-Path $PSScriptRoot 'Test-ChemLeapStoreSmoke.ps1') -Names @('Get-ChemStoreRegistration')
Assert-T05PlainPath $evidence
if (!(Test-Path -LiteralPath (Join-Path $evidence 'package-prepared.json'))) {throw 'Package-T05 -Stage Prepare must complete for this build first.'}
New-Item -ItemType Directory -Path $ReportDirectory | Out-Null
function Read-Record([string]$Path) {
    Assert-T05PlainPath $Path
    return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json -Depth 40)
}
function Match-Expected([bool]$Condition,[string]$Description) {
    Add-SmokeCheck $Description $Condition
}
function Assert-Image([string]$Name) {
    $path=Save-SmokeScreenshot $product $Name -IncludeOverlays
    if (!$path -or !(Test-Path -LiteralPath $path -PathType Leaf)) {throw 'The owned product client screenshot is required.'}
    $report[$Name+'_sha256']=Get-T05Hash $path
}
function Target-Events {
    return @(Read-ChemLeapRuntimeEvents | Where-Object {$_.buildId -eq $BuildId -and $_.appId -eq $appId -and $_.packageSha256 -eq $expectedHash})
}
try {
    $build=Read-Record (Join-Path $evidence 'build-result.json')
    $prepared=Read-Record (Join-Path $evidence 'package-prepared.json')
    if ($build.status -ne 'passed' -or $prepared.status -ne 'prepared' -or $build.test_build -or $prepared.test_build -or
        $prepared.build_id -ne $BuildId -or $build.build_id -ne $BuildId -or $prepared.source_snapshot_id -ne $build.source_snapshot_id -or
        $build.source_snapshot_id -notmatch '^sha256:[a-f0-9]{64}$' -or !$build.isolation_acceptance_root) {throw 'A tracked ordinary-trust acceptance build and matching prepared directory are required.'}
    $report.driver_sha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant()
    $report.driver_path=$PSCommandPath
    $report.source_snapshot_id=$build.source_snapshot_id
    $stageDirectory=[IO.Path]::GetFullPath($build.isolation_acceptance_root)
    $expectedDirectory=Join-Path $workspace ("artifacts/packages/$BuildId/AutumnOS-"+$build.version+'-win-x64-development')
    if ($stageDirectory -ne $expectedDirectory -or $prepared.executable -ne (Join-Path $stageDirectory 'AutumnOS.exe')) {throw 'Acceptance path must equal the dedicated prepared build directory.'}
    Assert-T05PlainPath $stageDirectory
    $report.tested_executable=$prepared.executable
    $dataDirectory=Join-Path $stageDirectory 'AutumnOS_Data'
    $runtimeLog=Join-Path $dataDirectory 'Logs/runtime-events.jsonl'
    if (Test-Path -LiteralPath $dataDirectory) {throw 'The candidate already has data. Preserve it and prepare a fresh acceptance build.'}
    $delivery=@(Read-Record (Join-Path $evidence 'delivery-hashes.json'))
    $matched=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $delivery) {
        $relative=[string]$item.path
        if (!$relative -or [IO.Path]::IsPathRooted($relative) -or $relative -match '[:\\]|(^|/)(\.\.?|AutumnOS_Data)($|/)' -or !$matched.Add($relative)) {throw 'Invalid or repeated delivery path.'}
        $file=[IO.Path]::GetFullPath((Join-Path $stageDirectory $relative))
        Assert-T05PlainPath $file
        if (!$file.StartsWith($stageDirectory+'\',[StringComparison]::OrdinalIgnoreCase) -or (Get-Item -LiteralPath $file).Length -ne $item.bytes -or
            (Get-T05Hash $file) -ne $item.sha256) {throw 'Prepared file differs from delivery evidence.'}
    }
    Match-Expected (@(Get-ChildItem -LiteralPath $stageDirectory -File -Recurse -Force).Count -eq $matched.Count) 'delivery has exactly the tracked files'
    $client=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $stageDirectory 'AutumnOS.Client.dll')))
    $embedded=$client.GetManifestResourceStream('AutumnOS.NativePayloadTable')
    if (!$embedded) {throw 'Missing compiled acceptance catalog.'}
    try {$embeddedHash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($embedded))}finally{$embedded.Dispose()}
    $tablePath=Join-Path $evidence 'native-payload-table.json'
    $table=Read-Record $tablePath
    Match-Expected ($embeddedHash -eq $build.native_payload_table_sha256 -and (Get-T05Hash $tablePath) -eq $embeddedHash -and $table.acceptanceRoot -eq $stageDirectory) 'compiled catalog and exact prepared root match'
    $report.catalog_sha256=$embeddedHash
    $report.package_verified='passed'
    $os=Get-CimInstance Win32_OperatingSystem
    $report.os=[ordered]@{build=[int]$os.BuildNumber;product_type=[int]$os.ProductType;x64=[Environment]::Is64BitProcess;interactive=[Environment]::UserInteractive}
    if ($Stage -eq 'Preflight') {$report.status='preflight_passed';return}
    if (!$IsWindows -or ![Environment]::UserInteractive -or ![Environment]::Is64BitProcess) {throw 'Interactive x64 Windows session required.'}
    if (@(Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('AutumnOS.exe','AutumnOS.Client.exe','AutumnOS.Updater.exe','AutumnOS.NativeHost.Windows.exe')}).Count) {throw 'Existing AutumnOS instance detected. No existing process will be controlled.'}
    if ($Stage -eq 'ServerNegative') {
        if ([int]$os.ProductType -ne 3 -or [int]$os.BuildNumber -ne 20348) {throw 'Server negative mode requires Windows Server build 20348.'}
    }else{
        $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
        if ([int]$os.ProductType -ne 1 -or [int]$os.BuildNumber -lt 19045 -or $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {throw 'Client trial requires a standard-user Windows 10 22H2/11 desktop.'}
    }
    . (Join-Path $PSScriptRoot 'Smoke-NativeAppInput.ps1')
    $desktopRuntime=Join-Path $workspace '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach ($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')) {Add-Type -Path (Join-Path $desktopRuntime $name)}
    $strings=@($desktopAst.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('namespace AutumnDesktopInteractionSmoke')},$true))
    if ($strings.Count -ne 1) {throw 'Maintained process/window binding helper required.'}
    if (!('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])) {Add-Type -TypeDefinition $strings[0].Value}
    Initialize-T05FocusNative
    $product=Start-SmokeProduct $ReportDirectory
    $product.Process.Refresh()
    $originalPath=$product.Process.MainModule.FileName
    if ($product.Process.HasExited -or $product.Process.MainWindowHandle -ne $product.Handle -or !$product.IsBootstrap -or
        ![string]::Equals($originalPath,(Join-Path $stageDirectory 'AutumnOS.Client.exe'),[StringComparison]::OrdinalIgnoreCase)) {
        throw 'The original process and bootstrap-selected client must own the prepared image and HWND.'
    }
    $trackedProcesses.Add([pscustomobject]@{Process=$product.Process;StartTicks=$product.Process.StartTime.ToUniversalTime().Ticks;Path=$originalPath;Root=$stageDirectory})
    Assert-T05OwnedWindow $product
    Match-Expected ($product.Element.Current.ProcessId -eq $product.Process.Id) 'held original native window'
    Click 'HelloNextButton'; Click 'BrandNextButton'; Click 'WelcomeAppearanceNext'; Click 'FirstRunSkip'; Click 'PrepareDesktopButton'
    Wait-SmokeDesktop $product
    Click 'StoreButton'; $null=Require 'StoreHeading'
    Match-Expected ((Require 'StoreSource').Current.Name -eq 'GitHub 公开应用') 'public store UI active'
    Click 'StoreOpenRepository'; Value 'StoreRepositoryInput' 'https://github.com/paimeng5201314/chemleap'; Click-Named '查看应用'
    $null=Wait-SmokeCondition {$button=Find ('StoreInstall-'+$releaseId);if($button -and $button.Current.IsEnabled){$button}} 'expected public release' 60
    Click ('StoreInstall-'+$releaseId); Click-Named '下载并安装'
    $null=Wait-SmokeCondition {
        $row=Find 'DownloadState-0.1.1'
        if ($row -and $row.Current.Name -match '^失败') {throw 'Public download or installation failed.'}
        if ($row -and $row.Current.Name -match '^已安装') {$row}
    } 'real verified store installation' $DownloadTimeoutSeconds
    $app=Get-ChemStoreRegistration
    Match-Expected ($app.source.repositoryId -eq $repositoryId -and $app.source.kind -eq 'github' -and $app.package.hostSource -eq ('github:'+$repositoryId) -and
        $app.provenance.releaseId -eq $releaseId -and $app.provenance.assetId -eq 650220127 -and $app.package.manifest.version -eq '0.1.1' -and $app.package.packageSha256 -eq $expectedHash) 'exact installed public release and asset'
    $history=Read-Record (Join-Path $dataDirectory 'Downloads/store-v1/history.json')
    $download=@($history.Tasks | Where-Object {$_.State -eq 6 -and $_.Request.AssetId -eq 650220127 -and $_.Request.Sha256 -eq $expectedHash})
    if ($download.Count -ne 1 -or $download[0].Id -notmatch '^[a-f0-9]{32}$' -or (Get-T05Hash (Join-Path $dataDirectory ('Downloads/store-v1/'+$download[0].Id+'.autumn'))) -ne $expectedHash) {throw 'Real download receipt and original archive required.'}
    $report.store_install='passed'
    Click 'StoreHome'; Wait-SmokeDesktop $product
    $icon='InstalledApp-'+$appId
    $null=Wait-SmokeCondition {Visible $icon} 'installed desktop app'
    if (@(Target-Events).Count) {throw 'Unexpected prior run in fresh test data.'}
    if ($Stage -eq 'ClientTrial') {
        $phrase="ALLOW ONE AUTUMNOS NATIVE TRIAL $BuildId $embeddedHash"
        Write-Host "The next action opens this verified game through the private native host. It may create one NETONLY LSA session, a fresh private station/desktop and AppContainer profile. Test installation: $stageDirectory"
        if ((Read-Host "To run once, type $phrase") -cne $phrase) {throw 'Specific operator authorization was not supplied.'}
        $marker=Join-Path $ReportDirectory 'authorized-once.json'
        $authority=[IO.File]::Open($marker,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        try {$bytes=[Text.Encoding]::UTF8.GetBytes((@{build=$BuildId;catalog=$embeddedHash;app=$expectedHash;scope='one_private_native_store_open';confirmed_utc=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json));$authority.Write($bytes);$authority.Flush($true)}finally{$authority.Dispose()}
    }
    $report.open_attempt='dispatch_requested_outcome_unknown'
    Invoke-SmokePointer $product $icon 'single_click'
    $report.open_attempt='input_dispatched'
    if ($Stage -eq 'ServerNegative') {
        $failure=Wait-SmokeCondition {@(Target-Events | Where-Object eventName -eq 'startup_failed') | Select-Object -First 1} 'recorded startup rejection' 35
        Match-Expected ($failure.startupFailure.stage -eq 'required_boundaries' -and $failure.startupFailure.code -eq 'RUNTIME_NATIVE_DESKTOP_REQUIRED' -and
            $null -eq $failure.nativeHost -and $failure.browserCleanup.environmentCreationAttempted -eq $false -and $failure.browserCleanup.controllerCreationAttempted -eq $false) 'exact server gate before native creation'
        $instance=$failure.instanceId
        $report.expected_server_rejection='passed'
        Assert-Image '01-server-rejection'
    }else{
        $foreground=Wait-SmokeCondition {
            $all=@(Target-Events)
            if (@($all|Where-Object eventName -eq 'startup_failed').Count) {throw 'Independent host failed during real startup.'}
            @($all|Where-Object {$_.eventName -eq 'navigation_completed' -and $_.state -eq 'Foreground' -and $_.networkSandboxVerified}) | Select-Object -Last 1
        } 'actual isolated game foreground' 180
        $instance=$foreground.instanceId
        $names=@(Target-Events | Where-Object instanceId -eq $instance | ForEach-Object eventName)
        $index=-1
        foreach($eventName in @('native_endpoint_registered','bootstrap_gate_released','native_first_frame','navigation_completed')){
            $next=[Array]::IndexOf($names,$eventName)
            Match-Expected ($next -gt $index) ("ordered "+$eventName);$index=$next
        }
        Match-Expected ($foreground.nativeHost.OriginalQueryJobBound -and $foreground.nativeHost.CreationAttempted -and $foreground.nativeHost.OriginalProcessId -gt 0) 'original native station and job observed'
        Assert-Image '01-private-first-frame'
        $report.native_first_frame='passed'
        Write-Host 'Inspect the game on the AutumnOS screen and try a jump with the mouse. Type PLAYED only after seeing the game react.'
        if ((Read-Host 'Operator observation') -ceq 'PLAYED') {$report.operator_gameplay='observed_by_operator'; Assert-Image '02-after-manual-jump'}
        Invoke-AutumnGameShortcut $product 'Close'; Click-Named '确认结束'
    }
    $retired=Wait-SmokeCondition {@(Target-Events|Where-Object {$_.eventName -eq 'closed' -and $_.instanceId -eq $instance -and
        !$_.blocksMaintenance -and !$_.browserCleanup.holdsRuntimeLease -and !$_.browserCleanup.holdsMaintenanceLease})|Select-Object -Last 1} 'original instance closure and release of both leases' 90
    if ($Stage -eq 'ClientTrial'){
        Match-Expected ($retired.nativeHost.ResourcesRetired -and $retired.nativeHost.OriginalProcessExited -and $retired.nativeHost.OriginalThreadExited -and
            $retired.nativeHost.OriginalJobEmpty -and $retired.nativeHost.AllObservedOriginalProcessHandlesSignaled) 'original handle group retired'
        $report.native_group_retirement='passed'
    }else{
        Match-Expected (@(Target-Events | Where-Object { $_.nativeHost -or $_.eventName -in @('native_first_frame','navigation_completed','native_endpoint_registered') }).Count -eq 0) 'rejected instance has no remote endpoint or frame'
    }
    Close-SmokeProduct $product
    $report.status=if($Stage -eq 'ServerNegative'){'passed_expected_rejection'}else{'passed_native_first_frame_and_exit'}
}catch{
    $report.failure=$_.Exception.Message
    $report.failure_stack=$_.ScriptStackTrace
    if ($product -and !$product.Process.HasExited){try{$null=Save-SmokeScreenshot $product '99-owned-failure' -IncludeOverlays}catch{}}
    throw
}finally{
    $remaining=@()
    foreach($process in $ownedProcesses){
        try {if(!$process.HasExited -and (!$process.CloseMainWindow() -or !$process.WaitForExit(10000))){$remaining+=$process.Id}}catch{$remaining+=$process.Id}
    }
    foreach($entry in $ownedEntryProcesses){try{if(!$entry.WaitForExit(10000)){$remaining+=$entry.Id}}catch{$remaining+=$entry.Id}}
    if($Stage -ne 'Preflight'){$report.cleanup=if($remaining.Count){'failed_original_process_pending'}else{'original_ui_processes_drained'}}
    if($remaining.Count){$report.status='failed';$report.remaining_processes=$remaining}
    if($Stage -ne 'Preflight' -and (Get-Command Restore-AutumnNativePointer -ErrorAction SilentlyContinue)){Restore-AutumnNativePointer}
    foreach($process in $ownedProcesses){$process.Dispose()};foreach($entry in $ownedEntryProcesses){$entry.Dispose()}
    if ($runtimeLog -and (Test-Path -LiteralPath $runtimeLog)) {$report.runtime_evidence=@(Target-Events | Select-Object instanceId,eventName,state,packageSha256,networkSandboxVerified,startupFailure,nativeHost,browserCleanup)}
    $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o')
    $report | ConvertTo-Json -Depth 18 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'acceptance.json') -Encoding utf8NoBOM
    Write-Host "Acceptance scope $($report.status), report $ReportDirectory"
}

if ($report.status -eq 'failed') {throw "Acceptance failed, see $ReportDirectory"}
