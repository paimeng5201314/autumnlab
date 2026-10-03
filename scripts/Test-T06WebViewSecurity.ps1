#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExecutablePath,[Parameter(Mandatory)][string]$ReportDirectory,
    [string]$HelpersDirectory=$PSScriptRoot,[ValidateRange(15,90)][int]$WindowTimeoutSeconds=45)
# Maintained real WebView negative test. Schedule exclusively with other native drivers.
$ErrorActionPreference='Stop'
$ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath);$ReportDirectory=[IO.Path]::GetFullPath($ReportDirectory);$HelpersDirectory=[IO.Path]::GetFullPath($HelpersDirectory)
if(Test-Path -LiteralPath $ReportDirectory){throw 'Use a new evidence directory.'}
. (Join-Path $HelpersDirectory 'Smoke-NativeAppInput.ps1')
. (Join-Path $HelpersDirectory 'T06-NativeHelpers.ps1')
$desktopAst=Import-T06Functions (Join-Path $HelpersDirectory 'Test-DesktopInteractionSmoke.ps1') -Exclude @('Read-SmokeEvents')
$script:t05LegacyFocus=(Get-Item Function:Focus-SmokeProduct).ScriptBlock
$null=Import-T06Functions (Join-Path $HelpersDirectory 'Test-T05UpdateSmoke.ps1') -Names @('Assert-T05OwnedWindow','Get-T05WindowGeometry','Focus-SmokeProduct','Initialize-T05FocusNative','Assert-T05PlainPath','Invoke-T05SharingRead','Get-T05Hash')
$null=Import-T06Functions (Join-Path $HelpersDirectory 'Test-T04StoreSmoke.ps1') -Names @('Show-Element','Click','Find','Visible','Require','Toggle','Click-Named')
$null=Import-T06Functions (Join-Path $HelpersDirectory 'Test-T06DeveloperSmoke.ps1') -Names @('Copy-PlainTree','Start-DeveloperCli','Finish-DeveloperCli','Run-DeveloperCli','Get-DeveloperScopes','Find-DeveloperNamed','Wait-DeveloperText','Click-DeveloperWeb','Read-DeveloperEvents','Start-ConfirmedPreview','Invoke-DeveloperDebug')
$checks=[Collections.Generic.List[object]]::new();$screenshots=[Collections.Generic.List[object]]::new();$commands=[Collections.Generic.List[object]]::new()
$inputActions=[Collections.Generic.List[object]]::new();$windowGeometry=[Collections.Generic.List[object]]::new()
$ownedProcesses=[Collections.Generic.List[Diagnostics.Process]]::new();$ownedEntryProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$trackedProcesses=[Collections.Generic.List[object]]::new();$cliProcesses=[Collections.Generic.List[object]]::new()
$testScope=Join-Path $ReportDirectory '隔离 native security';$stageDirectory=Join-Path $testScope '宿主';$cliRoot=Join-Path $testScope '独立工具';$cwd=Join-Path $testScope 'different-cwd'
$dataDirectory=Join-Path $stageDirectory 'AutumnOS_Data';$runtimeLog=Join-Path $dataDirectory 'Logs/runtime-events.jsonl'
$script:initialPointerPosition=$null;$script:product=$null;$script:documentTitle='AutumnOS 自建安全负面样例'
$listener=$null;$http=$null
$report=[ordered]@{schema_version=1;task_id='T06';status='failed';started_utc=[DateTimeOffset]::UtcNow.ToString('o');test_scope=$testScope
    source_executable=$ExecutablePath;tested_executable=(Join-Path $stageDirectory 'AutumnOS.exe');tested_cli=(Join-Path $cliRoot 'AutumnOS.Developer.Cli.exe')
    driver_sha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant();checks=$checks;commands=$commands;screenshots=$screenshots;input_actions=$inputActions;window_geometry=$windowGeometry
    process_ids=@();bootstrap_process_ids=@();remaining_processes=@();cleanup='not_run';complete_third_party_sandbox='not_run'
    account_isolation='not_run';arbitrary_command_execution='not_run_no_command_requested';external_system_protocol='not_run_no_handler_invoked';production_network='not_used'
    runtime_download_callback='not_run';native_test='explicit user-confirmed preview through normal WebAppHost; no test runtime exemptions'
    network_sandbox_verified=$null;sdk_capability_query='not_run';unverified_native_channels=@('WebRTC/STUN/TURN','WebTransport/HTTP3','DNS/preconnect/prefetch','ServiceWorker/worker','CSS/font/media network','native permission/file-dialog hardware','external system protocol','browser persistent state after account switch')
    permissions=@();listener='owned 127.0.0.1 ephemeral port; distinct controls and probe token routes'
    listener_scope='Zero probe-token HTTP request lines does not mean zero TCP connections. Incomplete/empty/timeout connections are separately recorded, with no attribution to a browser API.'}
function Get-SecurityObservation {
    $element=Wait-SmokeCondition {Find-DeveloperNamed '安全观测 JSON' ([Windows.Automation.ControlType]::Edit)} 'owned probe observation field' 15
    $element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value|ConvertFrom-Json
}
function Assert-NoProbeRequests([string]$Label){
    $attempts=@($listener.Requests|Where-Object {$_ -match ('^\S+ /'+[regex]::Escape($token)+'/probe/')})
    Add-SmokeCheck $Label ($attempts.Count -eq 0)
}
function Assert-ControlRecorded([string]$Control,[string]$Label){
    $expected='GET /'+$token+'/'+$Control+' HTTP/1.1'
    Add-SmokeCheck $Label (@($listener.Requests|Where-Object {$_ -ceq $expected}).Count -eq 1)
}
function Count-OwnEvent([string]$Name){@((Read-DeveloperEvents)|Where-Object {$_.instanceId -eq $sample.runtime_instance -and $_.eventName -eq $Name}).Count}
function Assert-SecurityPreviewPreserved([string]$Label){
    $observation=Get-SecurityObservation
    $field=Wait-SmokeCondition {Find-DeveloperNamed '未保存验证内容' ([Windows.Automation.ControlType]::Edit)} 'original unsaved input remains present' 10
    $value=$field.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    $status=Invoke-DeveloperDebug $session 'status'
    $events=@(Read-DeveloperEvents|Where-Object instanceId -eq $sample.runtime_instance)
    Add-SmokeCheck ($Label+'_original_input_document_and_probe_observations_preserved') (
        $value -ceq $unsavedMarker -and $observation.probeToken -ceq $token -and $observation.complete -eq $true -and
        $observation.started.Count -ge 6 -and @($probeNames|Where-Object {$_ -notin $observation.started}).Count -eq 0)
    Add-SmokeCheck ($Label+'_same_runtime_foreground_and_maintenance_blocker_remains') (
        $status.sessionId -eq $session -and $status.appId -eq $sample.appId -and $status.state -eq 'Foreground' -and
        $events.Count -gt 0 -and $events[-1].state -eq 'Foreground' -and $events[-1].blocksMaintenance -eq $true -and
        @($events|Where-Object {$_.state -in @('Closed','Crashed') -or $_.eventName -in @('navigation_failed','process_failed','closed')}).Count -eq 0 -and
        @($events|Where-Object eventName -eq 'navigation_completed').Count -eq 1)
    $report.preserved_input_observations+=@{label=$Label;value=$value;original_runtime=$sample.runtime_instance;session=$session;probe_token=$observation.probeToken;started=@($observation.started);state=$status.state;blocks_maintenance=$events[-1].blocksMaintenance}
}
try{
    if(@(Get-Process -Name AutumnOS,AutumnOS.Client,AutumnOS.Updater -ErrorAction SilentlyContinue).Count){throw 'Existing product instance is not touched; schedule this driver exclusively.'}
    Assert-T05PlainPath $ReportDirectory
    New-Item -ItemType Directory -Path $ReportDirectory,$testScope,$cwd|Out-Null
    Copy-PlainTree (Split-Path $ExecutablePath -Parent) $stageDirectory @('AutumnOS_Data','.autumnos-update')
    Copy-PlainTree (Join-Path (Split-Path $ExecutablePath -Parent) 'Developer') $cliRoot
    $receipt=Read-T05Json (Join-Path $stageDirectory 'autumn.install.json');$report.build_id=$receipt.buildId
    foreach($file in $receipt.files){if((Get-T05Hash (Join-Path $stageDirectory $file.path)) -ne $file.sha256){throw 'Copied delivered host differs from its receipt.'}}
    $report.verified_program_files=$receipt.files.Count
    Add-Type -TypeDefinition (Get-Content -LiteralPath (Join-Path $PSScriptRoot 'security-probes/LoopbackObserver.cs') -Raw)
    $listener=[AutumnT06Security.Observer]::new();$token=[Guid]::NewGuid().ToString('N');$base="http://127.0.0.1:$($listener.Port)/$token"
    $handler=[Net.Http.HttpClientHandler]::new();$handler.UseProxy=$false;$http=[Net.Http.HttpClient]::new($handler);$http.Timeout=[TimeSpan]::FromSeconds(5)
    $control=$http.GetStringAsync($base+'/control-before').GetAwaiter().GetResult()
    Add-SmokeCheck 'owned_listener_positive_control_before' ($control -eq 'owned-listener')
    Assert-ControlRecorded 'control-before' 'listener_recorded_exact_random_token_control_before'
    $project=Join-Path $testScope 'probe-project';New-Item -ItemType Directory -Path $project|Out-Null
    [ordered]@{schemaVersion=1;appId='dev.t06.securitynegative';name=$documentTitle;version='0.1.0';runtime='web';entry='index.html';permissions=@()}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $project 'manifest.json') -Encoding utf8NoBOM
    $html=@'
<!doctype html><html lang="zh-CN"><head><meta charset="utf-8"><title>AutumnOS 自建安全负面样例</title><link rel="stylesheet" href="probe.css"></head><body><main><h1>自建安全负面样例</h1><p>仅在本机独立目录测试正常 WebView 边界；无资料、存档或网络权限。</p><label for="unreleased-input">未保存验证内容</label><input id="unreleased-input" type="text"><div><button id="network">尝试六类网络请求</button><button id="popup">尝试新窗口</button><button id="navigate">尝试外部导航</button><button id="download">尝试本地下载</button></div><label for="results">安全观测 JSON</label><textarea id="results" readonly rows="9"></textarea></main><script src="autumn-sdk.js"></script><script src="probe.js"></script></body></html>
'@
    [IO.File]::WriteAllText((Join-Path $project 'index.html'),$html,[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $project 'probe.css'),'body{font:16px Segoe UI;background:#eef3ee;margin:0;padding:24px}main{max-width:900px;margin:auto}button{font:inherit;padding:12px;margin:8px}label{display:block}textarea{width:100%;font:13px Consolas}',[Text.UTF8Encoding]::new($false))
    $js=(Get-Content -LiteralPath (Join-Path $PSScriptRoot 'security-probes/probe.js') -Raw).Replace('__BASE__',$base)
    [IO.File]::WriteAllText((Join-Path $project 'probe.js'),$js,[Text.UTF8Encoding]::new($false))
    [IO.File]::Copy((Join-Path $cliRoot 'SDK/autumn-sdk.js'),(Join-Path $project 'autumn-sdk.js'))
    $validated=Run-DeveloperCli @('validate',$project);$packed=Run-DeveloperCli @('pack',$project,(Join-Path $testScope 'packages'))
    Add-SmokeCheck 'probe_is_real_permissionless_validated_package' ($validated.appId -eq 'dev.t06.securitynegative' -and (Get-T05Hash $packed.packagePath) -eq $packed.sha256)
    $sample=[ordered]@{template='security';appId=$packed.appId;package=$packed.packagePath;sha256=$packed.sha256;version=$packed.version;session=$null;runtime_instance=$null};$report.sample=$sample
    $projectRoot=Split-Path $HelpersDirectory -Parent;$desktopRuntime=Join-Path $projectRoot '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')){Add-Type -Path (Join-Path $desktopRuntime $name)}
    $strings=@($desktopAst.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('namespace AutumnDesktopInteractionSmoke')},$true))
    if($strings.Count -ne 1){throw 'Native helper declaration mismatch.'};Add-Type -TypeDefinition $strings[0].Value;Initialize-T05FocusNative
    $script:product=Start-SmokeProduct $cwd
    $trackedProcesses.Add([pscustomobject]@{Process=$product.Process;StartTicks=$product.Process.StartTime.ToUniversalTime().Ticks;Path=$product.Process.MainModule.FileName;Root=$stageDirectory})
    Click 'HelloNextButton';Click 'BrandNextButton';Click 'PrepareDesktopButton';Wait-SmokeDesktop $product
    Click 'SettingsButton';Toggle 'SettingsNavDiagnostics' $true;Toggle 'DeveloperModeToggle' $true;Click 'SettingsHomeButton';Click 'DeveloperButton'
    $session=Start-ConfirmedPreview $sample
    $capabilityObservation=Wait-SmokeCondition {$value=Get-SecurityObservation;if($value.capabilityError){throw ('Real SDK capability query failed: '+$value.capabilityError)};if($value.runtimeCapabilities){return $value}} 'real SDK runtime capability response' 15
    Add-SmokeCheck 'real_runtime_capability_keeps_complete_network_sandbox_unverified' ($capabilityObservation.runtimeCapabilities.protocolVersion -eq 1 -and $capabilityObservation.runtimeCapabilities.networkSandboxVerified -ceq $false)
    $report.network_sandbox_verified=$capabilityObservation.runtimeCapabilities.networkSandboxVerified;$report.sdk_capability_query='passed_real_internal_protocol'
    Click-DeveloperWeb '尝试六类网络请求'
    $observation=Wait-SmokeCondition {$value=Get-SecurityObservation;if($value.complete){return $value}} 'six real browser network probes settle' 15
    $probeNames=@('fetch','xhr','beacon','websocket','img','iframe')
    Add-SmokeCheck 'actual_document_observations_match_this_random_probe_token' ($observation.probeToken -ceq $token)
    Add-SmokeCheck 'all_six_native_network_apis_available' (@($probeNames|Where-Object {$observation.apiAvailable.$_ -cne $true}).Count -eq 0)
    Add-SmokeCheck 'all_six_network_apis_actually_attempted_once' (@($probeNames|Where-Object {$_ -notin $observation.started}).Count -eq 0 -and $observation.started.Count -eq 6)
    Add-SmokeCheck 'all_six_actual_API_calls_settled_without_positive_network_response' (
        $observation.outcomes.fetch -in @('TypeError','SecurityError') -and
        $observation.outcomes.xhr -in @('error','SecurityError','NetworkError') -and
        $observation.outcomes.beacon -in @('queued','rejected','SecurityError','TypeError') -and
        $observation.outcomes.websocket -in @('error','SecurityError') -and
        $observation.outcomes.img -eq 'error' -and $observation.outcomes.iframe -in @('settled','error'))
    foreach($directive in @('connect-src','img-src','frame-src')){Add-SmokeCheck ('actual_CSP_violation_'+$directive) (@($observation.csp|Where-Object directive -eq $directive).Count -gt 0)}
    Assert-NoProbeRequests 'no_probe_token_network_request_reached_owned_listener'
    $unsavedMarker='T06 未保存输入保留 '+[Guid]::NewGuid().ToString('N');$report.unsaved_marker=$unsavedMarker;$report.preserved_input_observations=@()
    $field=Wait-SmokeCondition {Find-DeveloperNamed '未保存验证内容' ([Windows.Automation.ControlType]::Edit)} 'actual editable probe input' 10
    $field.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($unsavedMarker)
    Assert-SecurityPreviewPreserved 'before_blocked_navigation'
    $capture=Save-SmokeScreenshot $product 'before-blocked-navigation' -IncludeOverlays;Add-SmokeCheck 'owned_before_denial_input_screenshot' ([bool]$capture)
    $before=Count-OwnEvent 'new_window_denied';Click-DeveloperWeb '尝试新窗口'
    $null=Wait-SmokeCondition {(Count-OwnEvent 'new_window_denied') -gt $before} 'actual host new-window denied event' 10
    Add-SmokeCheck 'native_new_window_callback_rejected_the_real_attempt' $true
    $before=Count-OwnEvent 'navigation_denied';Click-DeveloperWeb '尝试外部导航'
    $null=Wait-SmokeCondition {(Count-OwnEvent 'navigation_denied') -gt $before} 'actual host external-navigation denied event' 10
    Assert-SecurityPreviewPreserved 'after_blocked_navigation'
    Add-SmokeCheck 'native_navigation_callback_kept_original_package_page' $true
    $beforeDownload=Count-OwnEvent 'download_denied';$beforeNavigation=Count-OwnEvent 'navigation_denied';Click-DeveloperWeb '尝试本地下载'
    $null=Wait-SmokeCondition {(Count-OwnEvent 'download_denied') -gt $beforeDownload -or (Count-OwnEvent 'navigation_denied') -gt $beforeNavigation} 'actual host download or earlier navigation rejection' 10
    $report.runtime_download_callback=if((Count-OwnEvent 'download_denied') -gt $beforeDownload){'passed_observed_denial'}else{'not_run_blocked_by_navigation_first'}
    Add-SmokeCheck 'local_download_attempt_rejected_by_observed_native_layer' $true
    Start-Sleep -Seconds 2
    Assert-SecurityPreviewPreserved 'after_blocked_download'
    $report.javascript_observations=Get-SecurityObservation
    $control=$http.GetStringAsync($base+'/control-after').GetAwaiter().GetResult()
    Add-SmokeCheck 'owned_listener_positive_control_after' ($control -eq 'owned-listener')
    Assert-ControlRecorded 'control-after' 'listener_recorded_exact_random_token_control_after'
    Assert-NoProbeRequests 'all_probe_token_routes_still_absent_after_native_attempts'
    Add-SmokeCheck 'observer_had_no_listener_failure' ($listener.Failures.Length -eq 0)
    $capture=Save-SmokeScreenshot $product 'actual-negative-WebView' -IncludeOverlays;Add-SmokeCheck 'owned_native_negative_probe_screenshot' ([bool]$capture)
    $report.native_events=@(Read-DeveloperEvents|Where-Object instanceId -eq $sample.runtime_instance)
    $closed=Invoke-DeveloperDebug $session 'close';Add-SmokeCheck 'only_owned_preview_closed_via_supported_protocol' ($closed.state -eq 'Closed')
    Close-SmokeProduct $product;$report.status='passed'
}catch{
    $report.failure=$_.Exception.Message;$report.failure_type=$_.Exception.GetType().FullName;$report.failure_stack=$_.ScriptStackTrace
    if($product -and -not $product.Process.HasExited){try{$null=Save-SmokeScreenshot $product 'failure' -IncludeOverlays}catch{}}
}finally{
    if($listener){$listener.Dispose();$report.listener_requests=$listener.Requests;$report.listener_failures=$listener.Failures;$report.listener_connections=$listener.Connections;$report.incomplete_or_transport_connections=@($listener.Connections|Where-Object Outcome -ne 'http_request').Count;if($listener.Failures.Length -gt 0){$report.status='failed'}};if($http){$http.Dispose()}
    $clean=$true
    foreach($process in $ownedProcesses){try{if(-not $process.HasExited){$process.Refresh();if($process.MainWindowHandle -eq [IntPtr]::Zero -or -not $process.CloseMainWindow() -or -not $process.WaitForExit(10000)){$clean=$false;$report.remaining_processes+=@{pid=$process.Id;action='left_running_no_forced_termination'}}}}catch{$clean=$false}}
    foreach($entry in $ownedEntryProcesses){try{if(-not $entry.WaitForExit(10000)){$clean=$false;$report.remaining_processes+=@{pid=$entry.Id;action='bootstrap_left_running'}}}catch{$clean=$false}}
    foreach($entry in $cliProcesses){try{if(-not $entry.Process.WaitForExit(5000)){$clean=$false;$report.remaining_processes+=@{pid=$entry.Process.Id;action='bounded_CLI_left_running'}}}catch{$clean=$false}}
    Restore-AutumnNativePointer
    if($script:initialPointerPosition -and ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){$null=[AutumnDesktopInteractionSmoke.NativeWindows]::SetCursorPos($script:initialPointerPosition.X,$script:initialPointerPosition.Y)}
    $report.cleanup=if($clean){'passed'}else{'failed'};if(-not $clean){$report.status='failed'}
    $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o');$report.check_count=$checks.Count;$report.passed_checks=@($checks|Where-Object status -eq 'passed').Count
    if(Test-Path -LiteralPath $ReportDirectory){$report|ConvertTo-Json -Depth 24|Set-Content -LiteralPath (Join-Path $ReportDirectory 'webview-security.json') -Encoding utf8}
    foreach($process in $ownedProcesses){$process.Dispose()};foreach($entry in $ownedEntryProcesses){$entry.Dispose()};foreach($entry in $cliProcesses){$entry.Process.Dispose()}
}
if($report.status -ne 'passed'){throw "Native negative WebView test failed; retain $ReportDirectory"}
