#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [Parameter(Mandatory)][string]$HelpersDirectory,
    [ValidateRange(15,90)][int]$WindowTimeoutSeconds=45
)
# Explicitly scheduled native test only. This file is an artifact, not a product bypass.
# It tests developer-preview refusal; installed/store/deep-link and bundled-game tests remain separate.
$ErrorActionPreference='Stop'
$ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath);$ReportDirectory=[IO.Path]::GetFullPath($ReportDirectory);$HelpersDirectory=[IO.Path]::GetFullPath($HelpersDirectory)
if(Test-Path -LiteralPath $ReportDirectory){throw 'Use a new report directory; preserve earlier evidence.'}
if($ReportDirectory -match '[\\/]AutumnOS_Data(?:[\\/]|$)'){throw 'Never place this test in real user data.'}
if($ReportDirectory.StartsWith((Split-Path $ExecutablePath -Parent).TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Report directory cannot be inside the copied distribution.'}
. (Join-Path $HelpersDirectory 'Smoke-NativeAppInput.ps1')
. (Join-Path $HelpersDirectory 'T06-NativeHelpers.ps1')
$desktopAst=Import-T06Functions (Join-Path $HelpersDirectory 'Test-DesktopInteractionSmoke.ps1') -Exclude @('Read-SmokeEvents')
$script:t05LegacyFocus=(Get-Item Function:Focus-SmokeProduct).ScriptBlock
$null=Import-T06Functions (Join-Path $HelpersDirectory 'Test-T05UpdateSmoke.ps1') -Names @('Assert-T05OwnedWindow','Get-T05WindowGeometry','Focus-SmokeProduct','Initialize-T05FocusNative','Assert-T05PlainPath','Invoke-T05SharingRead','Get-T05Hash')
$null=Import-T06Functions (Join-Path $HelpersDirectory 'Test-T04StoreSmoke.ps1') -Names @('Show-Element','Click','Find','Visible','Require','Toggle','Click-Named')
$null=Import-T06Functions (Join-Path $HelpersDirectory 'Test-T06DeveloperSmoke.ps1') -Names @('Copy-PlainTree','Start-DeveloperCli','Finish-DeveloperCli','Run-DeveloperCli','Get-DeveloperScopes','Find-DeveloperNamed','Wait-DeveloperText','Read-DeveloperEvents')
$checks=[Collections.Generic.List[object]]::new();$screenshots=[Collections.Generic.List[object]]::new();$commands=[Collections.Generic.List[object]]::new()
$inputActions=[Collections.Generic.List[object]]::new();$windowGeometry=[Collections.Generic.List[object]]::new()
$ownedProcesses=[Collections.Generic.List[Diagnostics.Process]]::new();$ownedEntryProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$trackedProcesses=[Collections.Generic.List[object]]::new();$cliProcesses=[Collections.Generic.List[object]]::new()
$testScope=Join-Path $ReportDirectory 'native';$script:stageDirectory=Join-Path $testScope 'host';$cwd=Join-Path $testScope 'cwd'
$script:dataDirectory=Join-Path $stageDirectory 'AutumnOS_Data';$script:runtimeLog=Join-Path $dataDirectory 'Logs/runtime-events.jsonl'
$script:documentTitle='AutumnOS 自有启动拒绝探针';$script:product=$null;$script:initialPointerPosition=$null
$report=[ordered]@{
    schema_version=1;checkpoint='developer_preview_missing_clipboard_boundary_refusal_only';status='failed'
    started_utc=[DateTimeOffset]::UtcNow.ToString('o');source_executable=$ExecutablePath;tested_executable=(Join-Path $stageDirectory 'AutumnOS.exe')
    tested_cli=(Join-Path $stageDirectory 'Developer/AutumnOS.Developer.Cli.exe');build_id=$null;source_snapshot_id=$null
    driver_sha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    source_executable_sha256=(Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    checks=$checks;commands=$commands;screenshots=$screenshots;input_actions=$inputActions;window_geometry=$windowGeometry
    process_ids=@();bootstrap_process_ids=@();remaining_processes=@();attempts=@();native_events=@();cleanup='not_run'
    developer_preview_rejection='not_run';ordinary_local_install_rejection='not_run';store_rejection='not_run';deep_link_rejection='not_run'
    bundled_game_and_saves='not_run_use_Test-WindowsSmoke_separately';clipboard_boundary_implemented=$false;ChemLeap_normal_launch='not_run'
    data_scope='new isolated copy; no real user data';external_network='not_used';claim='startup refusal only; not complete runtime isolation acceptance'
}
try{
    if(@(Get-Process -Name AutumnOS,AutumnOS.Client,AutumnOS.Updater -ErrorAction SilentlyContinue).Count){throw 'Existing user/product process is not touched; schedule this driver exclusively.'}
    Assert-T05PlainPath $ReportDirectory
    New-Item -ItemType Directory -Path $ReportDirectory,$testScope,$cwd|Out-Null
    Copy-PlainTree (Split-Path $ExecutablePath -Parent) $stageDirectory @('AutumnOS_Data','.autumnos-update')
    Add-SmokeCheck 'fresh_distribution_copy_contains_no_user_data' (-not(Test-Path -LiteralPath $dataDirectory) -and (Test-Path -LiteralPath $report.tested_cli))
    $receipt=Read-T05Json (Join-Path $stageDirectory 'autumn.install.json');$report.build_id=$receipt.buildId
    foreach($file in $receipt.files){if((Get-T05Hash (Join-Path $stageDirectory $file.path)) -cne $file.sha256){throw 'Copied distribution differs from its receipt.'}}
    $report.verified_program_files=$receipt.files.Count
    $metadata=@{};$assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $stageDirectory 'Developer/AutumnOS.Developer.Cli.dll')))
    foreach($attribute in $assembly.GetCustomAttributesData()){if($attribute.AttributeType.Name -eq 'AssemblyMetadataAttribute'){$metadata[$attribute.ConstructorArguments[0].Value]=$attribute.ConstructorArguments[1].Value}}
    $report.source_snapshot_id=$metadata.SourceSnapshotId
    Add-SmokeCheck 'host_CLI_and_receipt_bind_the_actual_build' ($metadata.BuildId -ceq $receipt.buildId -and $report.source_snapshot_id -match '^sha256:[a-f0-9]{64}$' -and (Get-T05Hash $report.tested_executable) -ceq $report.source_executable_sha256)
    $project=Join-Path $testScope 'owned-probe';New-Item -ItemType Directory -Path $project|Out-Null
    $appId='dev.autumnos.boundarydenial';$marker='owned_boundary_script_'+[Guid]::NewGuid().ToString('N')
    [ordered]@{schemaVersion=1;appId=$appId;name=$documentTitle;version='0.1.0';runtime='web';entry='index.html';permissions=@()}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $project 'manifest.json') -Encoding utf8NoBOM
    [IO.File]::WriteAllText((Join-Path $project 'index.html'),('<!doctype html><html><head><meta charset="utf-8"><title>'+$documentTitle+'</title></head><body><p id="marker">No package code should execute.</p><script src="probe.js"></script></body></html>'),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $project 'probe.js'),('document.getElementById("marker").textContent="'+$marker+'";window.chrome.webview.postMessage({protocolVersion:1,requestId:"boundary_probe",method:"platform.getCapabilities",params:{}});'),[Text.UTF8Encoding]::new($false))
    $validated=Run-DeveloperCli @('validate',$project);$packed=Run-DeveloperCli @('pack',$project,(Join-Path $testScope 'packages'))
    Add-SmokeCheck 'owned_probe_is_real_validated_package_with_verified_payload_hash' ($validated.appId -ceq $appId -and $packed.appId -ceq $appId -and (Get-T05Hash $packed.packagePath) -ceq $packed.sha256)
    $report.package=@{appId=$appId;sha256=$packed.sha256;path=$packed.packagePath;script_sha256=Get-T05Hash (Join-Path $project 'probe.js')}
    $desktopRuntime=Join-Path (Split-Path $HelpersDirectory -Parent) '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')){Add-Type -Path (Join-Path $desktopRuntime $name)}
    $strings=@($desktopAst.FindAll({param($node)$node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('namespace AutumnDesktopInteractionSmoke')},$true))
    if($strings.Count -ne 1){throw 'Maintained native helper declaration mismatch.'}
    if(-not('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){Add-Type -TypeDefinition $strings[0].Value};Initialize-T05FocusNative
    $script:product=Start-SmokeProduct $cwd
    $trackedProcesses.Add([pscustomobject]@{Process=$product.Process;StartTicks=$product.Process.StartTime.ToUniversalTime().Ticks;Path=$product.Process.MainModule.FileName;Root=$stageDirectory})
    Click 'HelloNextButton';Click 'BrandNextButton';Click 'WelcomeAppearanceNext';Click 'FirstRunSkip';Click 'PrepareDesktopButton';Wait-SmokeDesktop $product
    Click 'SettingsButton';Toggle 'SettingsNavDiagnostics' $true;Toggle 'DeveloperModeToggle' $true;Click 'SettingsHomeButton';Click 'DeveloperButton'
    foreach($attempt in 1..2){
        $before=@(Read-DeveloperEvents).Count
        $entry=Start-DeveloperCli @('preview',$packed.packagePath,'--host',$stageDirectory)
        $confirmation=Wait-DeveloperText ([regex]::Escape($packed.sha256))
        Add-SmokeCheck ('attempt_'+$attempt+'_native_confirmation_binds_exact_package') ($confirmation.Contains($appId) -and $confirmation.Contains($packed.sha256))
        Click-Named '确认此包并预览'
        $denied=Finish-DeveloperCli $entry 2
        Add-SmokeCheck ('attempt_'+$attempt+'_official_CLI_returns_exact_missing_boundary') ($denied.code -ceq 'RUNTIME_CLIPBOARD_BOUNDARY_UNAVAILABLE')
        $events=Wait-SmokeCondition {
            $observed=@(Read-DeveloperEvents|Select-Object -Skip $before)
            $failure=@($observed|Where-Object {$_.eventName -eq 'startup_failed' -and $_.appId -ceq $appId -and $_.packageSha256 -ceq $packed.sha256})
            if($failure.Count -eq 1 -and @($observed|Where-Object {$_.eventName -eq 'closed' -and $_.instanceId -eq $failure[0].instanceId -and -not $_.blocksMaintenance}).Count -eq 1){return ,$observed}
        } 'exact rejected instance closes and releases maintenance resources' 15
        $failure=@($events|Where-Object eventName -eq 'startup_failed')[0];$instance=$failure.instanceId
        $own=@($events|Where-Object instanceId -eq $instance)
        Add-SmokeCheck ('attempt_'+$attempt+'_host_refuses_before_browser_or_package_execution') (
            $failure.startupFailure.stage -ceq 'required_boundaries' -and $failure.startupFailure.missingBoundary -ceq 'clipboard' -and
            @($own|Where-Object {$_.networkSandboxVerified -ceq $true -or $_.browserVersion -or $_.browserProcessId -or $_.profileNamespace}).Count -eq 0 -and
            @($own|Where-Object {$_.eventName -notin @('startup_failed','closed')}).Count -eq 0)
        Add-SmokeCheck ('attempt_'+$attempt+'_no_renderer_document_or_script_marker') (
            -not(Find-DeveloperNamed $documentTitle ([Windows.Automation.ControlType]::Document)) -and
            -not(Find-DeveloperNamed $marker ([Windows.Automation.ControlType]::Text)))
        $browsers=@(Get-CimInstance Win32_Process -Filter "Name = 'msedgewebview2.exe'"|Where-Object ParentProcessId -eq $product.Process.Id|Select-Object ProcessId,ParentProcessId)
        Add-SmokeCheck ('attempt_'+$attempt+'_owned_host_has_no_browser_process') ($browsers.Count -eq 0)
        $report.attempts+=@{attempt=$attempt;instanceId=$instance;CLI=$denied;events=$own;owned_browser_processes=$browsers}
    }
    Add-SmokeCheck 'repeated_refusal_uses_new_instance_and_does_not_leave_runtime_leases' ($report.attempts[0].instanceId -ne $report.attempts[1].instanceId)
    $null=Save-SmokeScreenshot $product 'developer-preview-boundary-refused' -IncludeOverlays
    $report.developer_preview_rejection='passed_two_real_confirmed_previews_rejected_before_WebView';$report.native_events=@(Read-DeveloperEvents)
    Close-SmokeProduct $product;$report.status='passed_developer_entry_scope_only'
}catch{
    $report.failure=$_.Exception.Message;$report.failure_type=$_.Exception.GetType().FullName;$report.failure_stack=$_.ScriptStackTrace
    if($product -and -not $product.Process.HasExited){try{$null=Save-SmokeScreenshot $product 'boundary-refusal-failure' -IncludeOverlays}catch{}}
}finally{
    if(Test-Path -LiteralPath $runtimeLog){try{$report.native_events=@(Read-DeveloperEvents)}catch{$report.native_event_read_failure=$_.Exception.GetType().Name}}
    $clean=$true
    foreach($process in $ownedProcesses){try{for($attempt=0;$attempt -lt 2 -and -not $process.HasExited;$attempt++){$null=$process.CloseMainWindow();$null=$process.WaitForExit(5000)};if(-not $process.HasExited){$clean=$false;$report.remaining_processes+=@{pid=$process.Id;action='left_running_no_forced_termination'}}}catch{$clean=$false}}
    foreach($entry in $ownedEntryProcesses){try{if(-not $entry.WaitForExit(10000)){$clean=$false;$report.remaining_processes+=@{pid=$entry.Id;action='bootstrap_left_running'}}}catch{$clean=$false}}
    foreach($entry in $cliProcesses){try{if(-not $entry.Process.WaitForExit(5000)){$clean=$false;$report.remaining_processes+=@{pid=$entry.Process.Id;action='owned_CLI_left_running'}}}catch{$clean=$false}}
    Restore-AutumnNativePointer
    if($script:initialPointerPosition -and ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){$null=[AutumnDesktopInteractionSmoke.NativeWindows]::SetCursorPos($script:initialPointerPosition.X,$script:initialPointerPosition.Y)}
    $report.cleanup=if($clean){'passed'}else{'failed'};if(-not $clean){$report.status='failed'}
    $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o');$report.check_count=$checks.Count;$report.passed_checks=@($checks|Where-Object status -eq 'passed').Count
    if(Test-Path -LiteralPath $ReportDirectory){$report|ConvertTo-Json -Depth 25|Set-Content -LiteralPath (Join-Path $ReportDirectory 'runtime-boundary-unavailable.json') -Encoding utf8}
    foreach($process in $ownedProcesses){$process.Dispose()};foreach($entry in $ownedEntryProcesses){$entry.Dispose()};foreach($entry in $cliProcesses){$entry.Process.Dispose()}
}
if($report.status -ne 'passed_developer_entry_scope_only'){throw "Native runtime-boundary refusal test failed; retain $ReportDirectory"}
