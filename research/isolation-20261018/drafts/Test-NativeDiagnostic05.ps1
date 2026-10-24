#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [string]$HelpersDirectory=$PSScriptRoot,
    [string]$FixtureDirectory=(Join-Path (Split-Path $PSScriptRoot -Parent) 'tests/fixtures/runtime-isolation'),
    [ValidateRange(15,90)][int]$WindowTimeoutSeconds=45
)
# Explicit local negative test. Uses the production WebAppHost and its mandatory native
# isolation check through the supported, user-confirmed developer preview entry point.
# This driver never exempts a package, writes permission configuration, or uses real Data.
$ErrorActionPreference='Stop'
$ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath)
$ReportDirectory=[IO.Path]::GetFullPath($ReportDirectory)
$HelpersDirectory=[IO.Path]::GetFullPath($HelpersDirectory)
$FixtureDirectory=[IO.Path]::GetFullPath($FixtureDirectory)
if(Test-Path -LiteralPath $ReportDirectory){throw 'Use a new report directory; historical evidence is preserved.'}
if($ReportDirectory -match '[\\/]AutumnOS_Data(?:[\\/]|$)'){throw 'Reports cannot be placed in user data.'}
. (Join-Path $HelpersDirectory 'Smoke-NativeAppInput.ps1')
. (Join-Path $HelpersDirectory 'T06-NativeHelpers.ps1')
$desktopAst=Import-T06Functions (Join-Path $HelpersDirectory 'Test-DesktopInteractionSmoke.ps1') -Exclude @('Read-SmokeEvents')
$script:t05LegacyFocus=(Get-Item Function:Focus-SmokeProduct).ScriptBlock
$null=Import-T06Functions (Join-Path $HelpersDirectory 'Test-T05UpdateSmoke.ps1') -Names @('Assert-T05OwnedWindow','Get-T05WindowGeometry','Focus-SmokeProduct','Initialize-T05FocusNative','Assert-T05PlainPath','Invoke-T05SharingRead','Get-T05Hash')
$null=Import-T06Functions (Join-Path $HelpersDirectory 'Test-T04StoreSmoke.ps1') -Names @('Show-Element','Click','Find','Visible','Require','Toggle','Click-Named')
$null=Import-T06Functions (Join-Path $HelpersDirectory 'Test-T06DeveloperSmoke.ps1') -Names @('Copy-PlainTree','Start-DeveloperCli','Finish-DeveloperCli','Run-DeveloperCli','Get-DeveloperScopes','Find-DeveloperNamed','Wait-DeveloperText','Click-DeveloperWeb','Read-DeveloperEvents','Start-ConfirmedPreview','Invoke-DeveloperDebug','Switch-DeveloperTestAccount')
$checks=[Collections.Generic.List[object]]::new();$screenshots=[Collections.Generic.List[object]]::new();$commands=[Collections.Generic.List[object]]::new()
$inputActions=[Collections.Generic.List[object]]::new();$windowGeometry=[Collections.Generic.List[object]]::new()
$ownedProcesses=[Collections.Generic.List[Diagnostics.Process]]::new();$ownedEntryProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$trackedProcesses=[Collections.Generic.List[object]]::new();$cliProcesses=[Collections.Generic.List[object]]::new()
$scope=Join-Path $ReportDirectory 'native';$script:stageDirectory=Join-Path $scope 'host';$cwd=Join-Path $scope 'cwd'
$script:dataDirectory=Join-Path $stageDirectory 'AutumnOS_Data';$script:runtimeLog=Join-Path $dataDirectory 'Logs/runtime-events.jsonl'
$script:documentTitle='AutumnOS 完整隔离负面样例';$script:product=$null;$script:initialPointerPosition=$null
$listener=$null;$http=$null;$script:activeSample=$null;$script:session=$null
$report=[ordered]@{
    schema_version=1;task_id='T01/T06';checkpoint='production_webapphost_mandatory_isolation_negative_test';status='failed'
    started_utc=[DateTimeOffset]::UtcNow.ToString('o');source_executable=$ExecutablePath;tested_executable=(Join-Path $stageDirectory 'AutumnOS.exe')
    tested_cli=(Join-Path $stageDirectory 'Developer/AutumnOS.Developer.Cli.exe');driver_sha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant()
    build_id=$null;source_snapshot_id=$null;environment=[Environment]::OSVersion.VersionString;source_executable_sha256=(Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    checks=$checks;commands=$commands;screenshots=$screenshots;input_actions=$inputActions;window_geometry=$windowGeometry
    process_ids=@();bootstrap_process_ids=@();remaining_processes=@();cleanup='not_run';fixture_files=@();observations=@();native_events=@();listener_controls=@()
    preview_entry='official CLI, native developer mode and exact package confirmation; same mandatory production isolation policy'
    store_install_and_normal_launch='not_run_separate_game_driver_required';network_negative='not_run';sdk_denial='not_run';declared_permission_denial='not_run';navigation='not_run'
    browser_permissions='not_run';sequential_instance_storage='not_run';controlled_account_browser_storage='not_run';close_inflight_readonly='not_run';concurrent_BroadcastChannel='not_run';real_account_switch='not_run'
    arbitrary_external_DNS='not_run_localhost_hint_only';trusted_certificate_WebTransport_handshake='not_run';production_network='not_used'
    initial_aboutblank_RTC='not_run'
    external_protocol='unregistered_random_scheme_only_no_real_handler';user_data='new isolated directory only';listener_scope='counts every accepted TCP connection, including zero bytes and TLS, plus every UDP datagram'
}
function Read-IsolationObservation {
    $field=Wait-SmokeCondition {Find-DeveloperNamed '完整隔离观测 JSON' ([Windows.Automation.ControlType]::Edit)} 'owned isolation observation field' 15
    return $field.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value|ConvertFrom-Json
}
function Get-IsolationEvents {return @(Read-DeveloperEvents|Where-Object instanceId -eq $script:activeSample.runtime_instance)}
function Count-IsolationEvent([string[]]$Names){return @(Get-IsolationEvents|Where-Object eventName -in $Names).Count}
function Assert-IsolationPreserved([string]$Label){
    $value=Read-IsolationObservation;$state=Invoke-DeveloperDebug $script:session 'status';$events=Get-IsolationEvents
    $field=Find-DeveloperNamed '隔离验证未保存内容' ([Windows.Automation.ControlType]::Edit)
    $actualInput=if($field){$field.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value}else{$null}
    Add-SmokeCheck ($Label+'_same_document_input_instance_foreground') ($value.documentNonce -ceq $script:documentNonce -and $actualInput -ceq $script:inputMarker -and
        $state.sessionId -eq $script:session -and $state.state -eq 'Foreground' -and $events[-1].blocksMaintenance -eq $true -and
        @($events|Where-Object eventName -in @('process_failed','navigation_failed','closed')).Count -eq 0)
}
function Invoke-IsolationControls([ValidateSet('before','after')][string]$Phase){
    $response=$http.GetStringAsync($script:base+'/control-'+$Phase).GetAwaiter().GetResult()
    foreach($port in @($listener.HttpPort,$listener.TcpPort)){
        $client=[Net.Sockets.TcpClient]::new()
        try{$client.Connect('127.0.0.1',$port);$bytes=[Text.Encoding]::ASCII.GetBytes('CONTROL:'+$script:token+':'+$Phase+"`n");$client.GetStream().Write($bytes,0,$bytes.Length)}finally{$client.Dispose()}
    }
    $udp=[Net.Sockets.UdpClient]::new()
    try{$bytes=[Text.Encoding]::ASCII.GetBytes('CONTROL:'+$script:token+':'+$Phase+"`n");$null=$udp.Send($bytes,$bytes.Length,'127.0.0.1',$listener.UdpPort)}finally{$udp.Dispose()}
    $request='GET /'+$script:token+'/control-'+$Phase+' HTTP/1.1'
    $null=Wait-SmokeCondition {
        @($listener.CompletedConnections|Where-Object control -CEQ $Phase).Count -eq 2 -and
        @($listener.CompletedConnections|Where-Object requestLine -CEQ $request).Count -eq 1 -and
        @($listener.Datagrams|Where-Object control -CEQ $Phase).Count -eq 1
    } ('real HTTP, both TCP and UDP positive controls '+$Phase) 8
    Add-SmokeCheck ('owned_listener_all_transports_positive_control_'+$Phase) ($response -ceq 'owned-isolation-observer' -and @($listener.HttpRequests|Where-Object {$_ -ceq $request}).Count -eq 1 -and $listener.Failures.Count -eq 0)
    $report.listener_controls+=@{phase=$Phase;accepted_tcp=$listener.AcceptedConnections.Count;completed_tcp=$listener.CompletedConnections.Count;udp=$listener.Datagrams.Count;http=$listener.HttpRequests.Count}
}
function Assert-IsolationNoLeak([string]$Label){
    Add-SmokeCheck $Label ($listener.AcceptedConnections.Count -eq $script:tcpBaseline -and $listener.Datagrams.Count -eq $script:udpBaseline -and
        $listener.HttpRequests.Count -eq $script:httpBaseline -and $listener.Failures.Count -eq 0) 'Includes incomplete handshakes, preconnect and non-HTTP TCP, not only HTTP request lines.'
}
function New-IsolationPackage([string]$Name,[string[]]$Permissions=@()){
    $project=Join-Path $scope ('projects/'+$Name);New-Item -ItemType Directory -Path $project -Force|Out-Null
    foreach($fileName in @('manifest.json','index.html','probe.css','config.js','probe.js','network.css','worker.js','service-worker.js')){Copy-Item -LiteralPath (Join-Path $FixtureDirectory $fileName) -Destination (Join-Path $project $fileName)}
    $manifest=Get-Content -LiteralPath (Join-Path $project 'manifest.json') -Raw|ConvertFrom-Json
    $manifest.appId='dev.autumnos.isolation'+$Name;$manifest.permissions=@($Permissions)
    [IO.File]::WriteAllText((Join-Path $project 'manifest.json'),($manifest|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
    $config=[ordered]@{token=$script:token;httpBase=$script:base;tcpPort=$listener.TcpPort;udpPort=$listener.UdpPort;deadlineMs=3500;allowNetworkAttempts=$true}
    [IO.File]::WriteAllText((Join-Path $project 'config.js'),('window.autumnIsolationProbeConfig=Object.freeze('+($config|ConvertTo-Json -Compress)+');'),[Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $project 'network.css'),((Get-Content -LiteralPath (Join-Path $project 'network.css') -Raw).Replace('__HTTP_BASE__',$script:base)),[Text.UTF8Encoding]::new($false))
    $validated=Run-DeveloperCli @('validate',$project);$packed=Run-DeveloperCli @('pack',$project,(Join-Path $scope ('packages/'+$Name)))
    Add-SmokeCheck ($Name+'_real_package_validated_and_hash_verified') ($validated.appId -ceq $manifest.appId -and $packed.appId -ceq $manifest.appId -and (Get-T05Hash $packed.packagePath) -ceq $packed.sha256)
    return [ordered]@{template=$Name;appId=$manifest.appId;package=$packed.packagePath;sha256=$packed.sha256;version=$packed.version;session=$null;runtime_instance=$null}
}
function Start-IsolationPackage($Sample){
    $script:activeSample=$Sample;$script:session=Start-ConfirmedPreview $Sample
    $value=Wait-SmokeCondition {$v=Read-IsolationObservation;if($v.capabilityError){throw ('Real capability query failed: '+$v.capabilityError)};if($v.runtimeCapabilities){return $v}} 'real mandatory isolation capability query' 15
    $events=Get-IsolationEvents
    $proofIndex=-1;$navigationIndex=-1
    for($i=0;$i -lt $events.Count;$i++){if($events[$i].eventName -eq 'isolation_verified'){$proofIndex=$i};if($events[$i].eventName -eq 'navigation_completed'){$navigationIndex=$i}}
    Add-SmokeCheck ($Sample.template+'_native_proof_precedes_package_navigation_and_SDK_reports_verified') ($value.configured -ceq $true -and $value.token -ceq $script:token -and
        $value.runtimeCapabilities.networkSandboxVerified -ceq $true -and $proofIndex -ge 0 -and $navigationIndex -gt $proofIndex -and @($events|Where-Object eventName -eq 'isolation_verified').Count -eq 1 -and
        @($events|Where-Object {$_.eventName -eq 'navigation_completed' -and $_.networkSandboxVerified -ceq $true}).Count -eq 1)
    $script:documentNonce=$value.documentNonce;$script:inputMarker='未保存隔离验证 '+[Guid]::NewGuid().ToString('N')
    Record-IsolationDiagnostic 'before-first-web-focus'
    & (Join-Path $PSScriptRoot 'Read-PickerHostMetadata.ps1') -ProductProcessId $product.Process.Id -ProductWindowHandle $product.Handle.ToInt64() -ReportPath (Join-Path $ReportDirectory 'picker-metadata.json')
    foreach($p in @(Get-Process -Name PickerHost -ErrorAction SilentlyContinue)){
        & (Join-Path $PSScriptRoot 'Read-SecurityPickerText.ps1') -PickerProcessId $p.Id -ExpectedStartTime $p.StartTime -ReportPath (Join-Path $ReportDirectory ('security-picker-'+$p.Id+'.json'))
    }
    Focus-SmokeProduct $product
    $field=Wait-SmokeCondition {Find-DeveloperNamed '隔离验证未保存内容' ([Windows.Automation.ControlType]::Edit)} 'owned unsaved input' 10
    $field.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($script:inputMarker)
    $null=Wait-SmokeCondition {$current=Find-DeveloperNamed '隔离验证未保存内容' ([Windows.Automation.ControlType]::Edit);$current -and $current.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ceq $script:inputMarker} 'native accessibility value update after SetValue' 5
    Add-SmokeCheck ($Sample.template+'_real_unsaved_marker_was_entered') $true
    return $value
}
function Close-IsolationPackage {
    $closing=$script:activeSample.runtime_instance;$result=Invoke-DeveloperDebug $script:session 'close'
    $null=Wait-SmokeCondition {@(Read-DeveloperEvents|Where-Object {$_.instanceId -eq $closing -and $_.eventName -eq 'closed' -and -not $_.blocksMaintenance}).Count -gt 0} 'owned WebView and maintenance lease released' 15
    $expired=Invoke-DeveloperDebug $script:session 'status' 2
    Add-SmokeCheck ($script:activeSample.template+'_close_releases_instance_and_expires_debug_session') ($result.state -eq 'Closed' -and $expired.code -eq 'DEVELOPER_PREVIEW_SESSION_EXPIRED')
}
function Assert-IsolationStorage([string]$Label,[switch]$Repeat) {
    $previous=Read-IsolationObservation
    Click-DeveloperWeb '读取并标记浏览器存储'
    $stored=Wait-SmokeCondition {
        $v=Read-IsolationObservation
        if($v.storage.complete -and $v.storage.indexedDb.after -and
            (-not $Repeat -or $v.storage.indexedDb.before -ceq $v.storage.marker)){return $v}
    } ('real browser storage write/read '+$Label) 15
    $s=$stored.storage;$marker=$s.marker
    Add-SmokeCheck ($Label+'_five_browser_stores_really_write_and_read') (
        $s.localStorage.after -ceq $marker -and $s.sessionStorage.after -ceq $marker -and
        $s.cookieAfter.Contains([Uri]::EscapeDataString($marker)) -and $s.indexedDb.after -ceq $marker -and $s.cache.after -ceq $marker)
    if($Repeat){
        Add-SmokeCheck ($Label+'_same_document_positive_control_reads_previous_values') (
            $stored.documentNonce -ceq $previous.documentNonce -and $s.localStorage.previous -ceq $marker -and
            $s.sessionStorage.previous -ceq $marker -and $s.cookieBefore.Contains([Uri]::EscapeDataString($marker)) -and
            $s.indexedDb.before -ceq $marker -and $s.cache.before -ceq $marker)
    }else{
        Add-SmokeCheck ($Label+'_fresh_document_has_no_other_instance_account_or_app_values') (
            -not $s.localStorage.previous -and -not $s.sessionStorage.previous -and -not $s.cookieBefore -and
            $null -eq $s.indexedDb.before -and $null -eq $s.cache.before)
    }
    Add-SmokeCheck ($Label+'_native_WASM_runs_while_JavaScript_eval_stays_denied') ($s.wasm -eq 42 -and $s.evalBlocked -ceq $true)
    $report.observations+=@{phase=$Label;value=$stored}
    return $stored
}
function Switch-IsolationAccount([string]$Action) {
    $result=Switch-DeveloperTestAccount $script:session $Action
    $script:session=$result.sessionId
    $current=@(Read-DeveloperEvents|Where-Object {$_.appId -ceq $script:activeSample.appId -and $_.eventName -eq 'navigation_completed'})|Select-Object -Last 1
    $script:activeSample.runtime_instance=$current.instanceId
    $v=Wait-SmokeCondition {$value=Read-IsolationObservation;if($value.runtimeCapabilities.networkSandboxVerified -ceq $true){return $value}} 'new controlled account requires fresh native isolation proof' 15
    Add-SmokeCheck ($Action+'_actual_profile_version_and_policy_match') ($current.profileNamespace -match '^[a-f0-9]{64}$' -and
        $current.browserVersion -ceq '154.0.4258.53' -and $current.isolationPolicy -ceq 'autumn-local-web-v2')
    return $current
}
$script:diagnosticClick=(Get-Item Function:Click).ScriptBlock
$script:diagnosticToggle=(Get-Item Function:Toggle).ScriptBlock
$report.diagnostic_actions=[Collections.Generic.List[object]]::new()
function Record-IsolationDiagnostic([string]$Action){
    $geometry=Get-T05WindowGeometry $product $Action
    $pickers=@(Get-CimInstance Win32_Process -Filter "Name='PickerHost.exe'" | Select-Object ProcessId,ParentProcessId,CreationDate,ExecutablePath,SessionId)
    $report.diagnostic_actions.Add(@{action=$Action;geometry=$geometry;pickers=$pickers})
}
function Click([string]$Id){
    $target=Show-Element $Id
    $report.diagnostic_actions.Add(@{action='invoke-target';requested=$Id;id=$target.Current.AutomationId;name=$target.Current.Name;pid=$target.Current.ProcessId;runtimeId=@($target.GetRuntimeId())})
    & $script:diagnosticClick $Id
    Start-Sleep -Milliseconds 300
    Record-IsolationDiagnostic ('after-click-'+$Id)
}
function Toggle([string]$Id,[bool]$Value){
    & $script:diagnosticToggle $Id $Value
    Start-Sleep -Milliseconds 300
    Record-IsolationDiagnostic ('after-toggle-'+$Id)
}
try{
    if(@(Get-Process -Name AutumnOS,AutumnOS.Client,AutumnOS.Updater -ErrorAction SilentlyContinue).Count){throw 'Existing product instance is not touched; schedule this driver exclusively.'}
    Assert-T05PlainPath $ReportDirectory;Assert-T05PlainPath $FixtureDirectory
    if($stageDirectory.Length -gt 130){throw 'Use a short isolated report path for native WebView profile subdirectories.'}
    New-Item -ItemType Directory -Path $ReportDirectory,$scope,$cwd|Out-Null
    Copy-PlainTree (Split-Path $ExecutablePath -Parent) $stageDirectory @('AutumnOS_Data','.autumnos-update')
    Add-SmokeCheck 'complete_normal_distribution_copied_without_user_data' (-not(Test-Path -LiteralPath $dataDirectory) -and (Test-Path -LiteralPath $report.tested_cli))
    $receipt=Read-T05Json (Join-Path $stageDirectory 'autumn.install.json');$report.build_id=$receipt.buildId
    foreach($file in $receipt.files){if((Get-T05Hash (Join-Path $stageDirectory $file.path)) -ne $file.sha256){throw 'Copied normal distribution differs from its receipt.'}}
    $report.verified_program_files=$receipt.files.Count
    $metadata=@{};$assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $stageDirectory 'Developer/AutumnOS.Developer.Cli.dll')))
    foreach($attribute in $assembly.GetCustomAttributesData()){if($attribute.AttributeType.Name -eq 'AssemblyMetadataAttribute'){$metadata[$attribute.ConstructorArguments[0].Value]=$attribute.ConstructorArguments[1].Value}}
    $report.source_snapshot_id=$metadata.SourceSnapshotId
    Add-SmokeCheck 'normal_host_and_delivered_CLI_share_real_build_and_source_snapshot' ($metadata.BuildId -ceq $receipt.buildId -and $report.source_snapshot_id -match '^sha256:[a-f0-9]{64}$' -and (Get-T05Hash $report.tested_executable) -ceq $report.source_executable_sha256)
    foreach($file in Get-ChildItem -LiteralPath $FixtureDirectory -File){$report.fixture_files+=@{path=$file.Name;sha256=Get-T05Hash $file.FullName}}
    Add-Type -Path (Join-Path $FixtureDirectory 'LoopbackNetworkObserver.cs')
    $script:token=[Guid]::NewGuid().ToString('N');$listener=[AutumnRuntimeIsolationProbe.LoopbackNetworkObserver]::new($script:token)
    $script:base='http://127.0.0.1:'+$listener.HttpPort+'/'+$script:token
    $handler=[Net.Http.HttpClientHandler]::new();$handler.UseProxy=$false;$http=[Net.Http.HttpClient]::new($handler);$http.Timeout=[TimeSpan]::FromSeconds(5)
    Invoke-IsolationControls 'before'
    $script:tcpBaseline=$listener.AcceptedConnections.Count;$script:udpBaseline=$listener.Datagrams.Count;$script:httpBaseline=$listener.HttpRequests.Count
    $ordinary=New-IsolationPackage 'none';$declared=New-IsolationPackage 'saves' @('saves');$report.packages=@($ordinary,$declared)
    $desktopRuntime=Join-Path (Split-Path $HelpersDirectory -Parent) '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')){Add-Type -Path (Join-Path $desktopRuntime $name)}
    $strings=@($desktopAst.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('namespace AutumnDesktopInteractionSmoke')},$true))
    if($strings.Count -ne 1){throw 'Native helper declaration mismatch.'};if(-not('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){Add-Type -TypeDefinition $strings[0].Value};Initialize-T05FocusNative
    $script:product=Start-SmokeProduct $cwd
    $trackedProcesses.Add([pscustomobject]@{Process=$product.Process;StartTicks=$product.Process.StartTime.ToUniversalTime().Ticks;Path=$product.Process.MainModule.FileName;Root=$stageDirectory})
    Click 'HelloNextButton';Click 'BrandNextButton';Click 'WelcomeAppearanceNext';Click 'FirstRunSkip';Click 'PrepareDesktopButton';Wait-SmokeDesktop $product
    Click 'SettingsButton';Toggle 'SettingsNavDiagnostics' $true;Toggle 'DeveloperModeToggle' $true;Click 'SettingsHomeButton';Click 'DeveloperButton'
    $initial=Start-IsolationPackage $ordinary
    Click-DeveloperWeb '探测 SDK 权限与伪造'
    $sdk=Wait-SmokeCondition {$v=Read-IsolationObservation;if($v.sdk.complete){return $v.sdk.responses}} 'real unprivileged gateway probes settle' 25
    $expected=@{'saves-query'='PERMISSION_NOT_DECLARED';'save-without-permission'='PERMISSION_NOT_DECLARED';'read-without-permission'='PERMISSION_NOT_DECLARED';'storage-without-declaration'='PERMISSION_NOT_DECLARED';'file-without-declaration'='PERMISSION_NOT_DECLARED';'forged-app-identity'='INVALID_REQUEST';'invalid-protocol'='PROTOCOL_UNSUPPORTED';'unknown-native-command'='CAPABILITY_UNAVAILABLE';'host-token-unavailable'='CAPABILITY_UNAVAILABLE';'duplicate-second'='DUPLICATE_REQUEST';'save-after-denial'='PERMISSION_NOT_DECLARED'}
    $wrong=@(foreach($name in $expected.Keys){if($sdk.$name.ok -cne $false -or $sdk.$name.code -cne $expected[$name]){$name}})
    Add-SmokeCheck 'raw_gateway_rejects_undeclared_capabilities_forgery_native_commands_and_replay' ($wrong.Count -eq 0 -and $sdk.'duplicate-first'.ok -ceq $true -and $sdk.'forged-permission-param'.code -in @('INVALID_REQUEST','PERMISSION_NOT_DECLARED')) ($wrong -join ',')
    $report.sdk_denial='passed';$report.observations+=@{phase='unprivileged-sdk';value=Read-IsolationObservation}
    Click-DeveloperWeb '探测浏览器权限拒绝'
    $permissions=Wait-SmokeCondition {$v=Read-IsolationObservation;if($v.browserPermissions.complete){return $v.browserPermissions}} 'browser permission requests denied or explicitly unsupported' 20
    $invalid=@(@('geolocation','camera','microphone','notification')|Where-Object {$permissions.$_.state -notin @('denied','exception','api_unavailable')})
    Add-SmokeCheck 'browser_device_location_notifications_never_granted' ($invalid.Count -eq 0 -and ($permissions.geolocation.state -ne 'denied' -or $permissions.geolocation.code -eq 1))
    $report.browser_permissions='passed_API_denials_or_explicit_unavailable';$report.browser_permission_native_callback_count=Count-IsolationEvent @('browser_permission_denied')
    foreach($action in @(
        @{button='尝试隔离新窗口';events=@('new_window_denied');label='popup'},
        @{button='尝试隔离外部导航';events=@('navigation_denied');label='navigation'},
        @{button='尝试隔离本地下载';events=@('download_denied','navigation_denied');label='download'},
        @{button='尝试未注册外部协议';events=@('external_protocol_denied','navigation_denied');label='protocol'})){
        $before=Count-IsolationEvent $action.events;Click-DeveloperWeb $action.button
        $null=Wait-SmokeCondition {(Count-IsolationEvent $action.events) -gt $before} ('actual native denial '+$action.label) 10
        Assert-IsolationPreserved $action.label
    }
    Click-DeveloperWeb '尝试隔离表单导航'
    $null=Wait-SmokeCondition {$v=Read-IsolationObservation;@($v.csp|Where-Object directive -eq 'form-action').Count -gt 0} 'native browser CSP form-action rejection' 10
    Assert-IsolationPreserved 'form';$report.navigation='passed_native_callbacks_and_form_CSP'
    Click-DeveloperWeb '尝试所有网络通道'
    $network=Wait-SmokeCondition {$v=Read-IsolationObservation;if($v.network.complete){return $v}} 'all real browser channel probes settle' 25
    $names=@('fetch','xhr','beacon','websocket','eventsource','img','remote-script','module-import','css-link','css-nested','fontface','audio','video','iframe','form-frame','prefetch','preconnect','dns-prefetch','worker-local','worker-blob','shared-worker','service-worker','rtc-stun-udp','rtc-turn-udp','rtc-turn-tcp','rtc-direct-udp','webtransport')
    $missing=@($names|Where-Object {$_ -notin $network.network.started -or $_ -notin $network.network.outcomes.PSObject.Properties.Name})
    $report.unsupported_network_APIs=@($names|Where-Object {$network.network.apiAvailable.$_ -ceq $false})
    Add-SmokeCheck 'every_expected_browser_channel_actually_started_and_reported' ($missing.Count -eq 0) ($missing -join ',')
    Add-SmokeCheck 'transport_workers_and_serviceworker_have_no_successful_connection_or_execution' (
        $network.network.outcomes.websocket.state -ne 'opened' -and $network.network.outcomes.eventsource.state -ne 'opened' -and $network.network.outcomes.webtransport.state -ne 'ready' -and
        $network.network.outcomes.'worker-local'.state -ne 'worker_started' -and $network.network.outcomes.'worker-blob'.state -ne 'constructed' -and $network.network.outcomes.'shared-worker'.state -ne 'constructed' -and $network.network.outcomes.'service-worker'.state -ne 'registered')
    $rtcProblems=@(@('rtc-stun-udp','rtc-turn-udp','rtc-turn-tcp')|Where-Object {$network.network.apiAvailable.$_ -cne $true -or $network.network.outcomes.$_.state -ne 'ice_attempted' -or @($network.network.outcomes.$_.candidates).Count -ne 0})
    Add-SmokeCheck 'three_real_RTC_ICE_attempts_have_zero_candidates' ($rtcProblems.Count -eq 0) ($rtcProblems -join ',')
    $direct=$network.network.outcomes.'rtc-direct-udp'
    Add-SmokeCheck 'native_remote_host_candidate_was_supplied_without_starting_offer_peer_ICE' ($network.network.apiAvailable.'rtc-direct-udp' -ceq $true -and $direct.state -ceq 'remote_candidate_attempted' -and
        $direct.candidateTarget -ceq '127.0.0.1' -and $direct.candidatePort -eq $listener.UdpPort -and $direct.offererLocalDescriptionUnset -ceq $true -and $direct.candidateResult -in @('accepted','rejected') -and @($direct.candidates).Count -eq 0)
    $report.direct_remote_ICE_observation=@{candidate_result=$direct.candidateResult;candidate_error=$direct.candidateError;ice_state=$direct.iceConnectionState;limit=$direct.observationLimit;delivery_evidence='owned UDP observer, not candidate acceptance or browser exceptions'}
    Assert-IsolationPreserved 'network';Assert-IsolationNoLeak 'all_network_apis_created_zero_extra_TCP_connections_UDP_datagrams_and_HTTP_requests'
    $report.network_negative='passed_loopback_TCP_UDP_observation';$report.observations+=@{phase='all-channels';value=$network}
    $null=Save-SmokeScreenshot $product 'isolation-network-and-SDK' -IncludeOverlays
    $stored=Assert-IsolationStorage 'guest_first'
    $null=Assert-IsolationStorage 'guest_same_document' -Repeat
    $firstOrigin=$stored.pageOrigin;$firstNonce=$stored.documentNonce;Close-IsolationPackage
    $null=Start-IsolationPackage $ordinary
    $reopened=Assert-IsolationStorage 'guest_reopened'
    Add-SmokeCheck 'new_instance_uses_independent_origin_and_has_no_previous_storage_or_cookie_marker' ($reopened.pageOrigin -cne $firstOrigin -and $reopened.documentNonce -cne $firstNonce -and
        -not $reopened.storage.localStorage.previous -and -not $reopened.storage.sessionStorage.previous -and -not $reopened.storage.cookieBefore)
    $report.sequential_instance_storage='passed';$report.observations+=@{phase='new-instance-storage';value=$reopened}
    $guestProfile=@(Get-IsolationEvents|Where-Object eventName -eq 'navigation_completed')[-1].profileNamespace
    $accountA=Switch-IsolationAccount 'account-a';$null=Assert-IsolationStorage 'controlled_account_A';$null=Assert-IsolationStorage 'controlled_account_A_same_document' -Repeat
    $accountB=Switch-IsolationAccount 'account-b';$null=Assert-IsolationStorage 'controlled_account_B'
    $accountAgain=Switch-IsolationAccount 'account-a';$null=Assert-IsolationStorage 'controlled_account_A_reopened'
    Add-SmokeCheck 'controlled_accounts_use_distinct_actual_profiles_and_A_reuses_its_own_namespace' (
        $guestProfile -cne $accountA.profileNamespace -and $accountA.profileNamespace -cne $accountB.profileNamespace -and $accountA.profileNamespace -ceq $accountAgain.profileNamespace)
    $report.controlled_account_browser_storage='passed_explicit_test_identities_not_real_Logto';Close-IsolationPackage
    $null=Start-IsolationPackage $declared;Click-DeveloperWeb '探测 SDK 权限与伪造'
    $null=Wait-DeveloperText '使用私有存档';Click-Named '拒绝' 'CloseButton'
    $denied=Wait-SmokeCondition {$v=Read-IsolationObservation;if($v.sdk.complete){return $v}} 'real native permission refusal followed by repeat and write' 25
    Add-SmokeCheck 'native_user_denial_persists_on_repeat_and_blocks_real_save' ($denied.sdk.responses.'permission-request-first'.ok -ceq $true -and $denied.sdk.responses.'permission-request-first'.result.state -ceq 'denied' -and
        $denied.sdk.responses.'permission-request-repeat'.ok -ceq $true -and $denied.sdk.responses.'permission-request-repeat'.result.state -ceq 'denied' -and $denied.sdk.responses.'save-after-denial'.code -ceq 'PERMISSION_DENIED' -and
        -not(Test-Path -LiteralPath (Join-Path $dataDirectory ('Saves/'+$declared.appId))))
    Assert-IsolationPreserved 'permission-denial';$report.declared_permission_denial='passed_real_native_dialog';$report.observations+=@{phase='real-permission-denied';value=$denied}
    $null=Save-SmokeScreenshot $product 'isolation-real-permission-denied' -IncludeOverlays
    Click-DeveloperWeb '排队请求后由宿主结束'
    $null=Wait-SmokeCondition {$v=Read-IsolationObservation;if($v.closeQueue.sent -gt 0){return $v.closeQueue}} 'real readonly request queued before close' 10
    Close-IsolationPackage;$report.close_inflight_readonly='passed_native_close_and_expired_session'
    $null=Start-IsolationPackage $ordinary
    Click-DeveloperWeb '尝试初始 about:blank RTC'
    $blankResult=Wait-SmokeCondition {
        $events=Get-IsolationEvents
        if(@($events|Where-Object eventName -eq 'process_failed').Count -gt 0){return 'renderer_failed'}
        try {$v=Read-IsolationObservation} catch {
            if(@(Get-IsolationEvents|Where-Object eventName -eq 'process_failed').Count -gt 0){return 'renderer_failed'}
            throw
        }
        if($v.aboutBlankRTC.state -eq 'remote_candidate_attempted'){
            if($v.aboutBlankRTC.candidateTarget -cne '127.0.0.1' -or $v.aboutBlankRTC.candidatePort -ne $listener.UdpPort -or
                $v.aboutBlankRTC.offererLocalDescriptionUnset -cne $true -or @($v.aboutBlankRTC.candidates).Count -ne 0){throw 'Initial realm RTC observation does not bind the owned candidate.'}
            return 'completed_without_crash'
        }
        if($v.aboutBlankRTC.state -in @('api_unavailable','exception')){return 'native_api_blocked_or_unavailable'}
    } 'initial aboutblank RTC completes or fails closed in the real host' 20
    if($blankResult -eq 'renderer_failed'){
        $null=Wait-SmokeCondition {@(Get-IsolationEvents|Where-Object {$_.eventName -eq 'closed' -and -not $_.blocksMaintenance -and $_.networkSandboxVerified -ceq $false}).Count -eq 1} 'renderer failure revokes proof and releases actual WebView leases' 15
        $events=Get-IsolationEvents
        Add-SmokeCheck 'actual_renderer_failure_revokes_network_proof_and_releases_resources' (@($events|Where-Object {$_.eventName -eq 'process_failed' -and $_.networkSandboxVerified -ceq $false}).Count -gt 0 -and @($events|Where-Object eventName -eq 'resource_release_failed').Count -eq 0)
        $report.initial_aboutblank_RTC='passed_actual_renderer_failure_closed_revoked_and_released'
    }elseif($blankResult -eq 'completed_without_crash'){
        $report.observations+=@{phase='initial-aboutblank-RTC';value=Read-IsolationObservation}
        Close-IsolationPackage
        $report.initial_aboutblank_RTC='passed_completed_and_closed_without_network_delivery'
    }else{
        $report.observations+=@{phase='initial-aboutblank-RTC-native-unavailable';value=Read-IsolationObservation}
        Close-IsolationPackage
        $report.initial_aboutblank_RTC='blocked_native_API_no_delivery_remote_ICE_not_run_in_this_realm'
    }
    Assert-IsolationNoLeak 'all_permission_navigation_restart_and_close_phases_created_zero_extra_TCP_or_UDP'
    Invoke-IsolationControls 'after'
    Add-SmokeCheck 'only_exact_positive_controls_reached_all_owned_transports' ($listener.AcceptedConnections.Count -eq 6 -and $listener.Datagrams.Count -eq 2 -and $listener.HttpRequests.Count -eq 2 -and $listener.Failures.Count -eq 0)
    $report.native_events=@(Read-DeveloperEvents);Close-SmokeProduct $product;$report.status='passed'
}catch{
    $report.failure=$_.Exception.Message;$report.failure_type=$_.Exception.GetType().FullName;$report.failure_stack=$_.ScriptStackTrace
    if($product -and -not $product.Process.HasExited){try{$null=Save-SmokeScreenshot $product 'isolation-failure' -IncludeOverlays}catch{}}
}finally{
    if(Test-Path -LiteralPath $runtimeLog){try{$report.native_events=@(Read-DeveloperEvents)}catch{$report.native_event_read_failure=$_.Exception.GetType().Name}}
    if($listener){$listener.Dispose();$report.listener_accepted=$listener.AcceptedConnections;$report.listener_completed=$listener.CompletedConnections;$report.listener_datagrams=$listener.Datagrams;$report.listener_requests=$listener.HttpRequests;$report.listener_failures=$listener.Failures;if($listener.Failures.Count){$report.status='failed'}}
    if($http){$http.Dispose()}
    $clean=$true
    foreach($process in $ownedProcesses){try{for($attempt=0;$attempt -lt 2 -and -not $process.HasExited;$attempt++){$null=$process.CloseMainWindow();$null=$process.WaitForExit(5000)};if(-not $process.HasExited){$clean=$false;$report.remaining_processes+=@{pid=$process.Id;action='left_running_no_forced_termination'}}}catch{$clean=$false}}
    foreach($entry in $ownedEntryProcesses){try{if(-not $entry.WaitForExit(10000)){$clean=$false;$report.remaining_processes+=@{pid=$entry.Id;action='bootstrap_left_running'}}}catch{$clean=$false}}
    foreach($entry in $cliProcesses){try{if(-not $entry.Process.WaitForExit(5000)){$clean=$false;$report.remaining_processes+=@{pid=$entry.Process.Id;action='owned_CLI_left_running'}}}catch{$clean=$false}}
    Restore-AutumnNativePointer
    if($script:initialPointerPosition -and ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){$null=[AutumnDesktopInteractionSmoke.NativeWindows]::SetCursorPos($script:initialPointerPosition.X,$script:initialPointerPosition.Y)}
    $report.cleanup=if($clean){'passed'}else{'failed'};if(-not $clean){$report.status='failed'}
    $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o');$report.check_count=$checks.Count;$report.passed_checks=@($checks|Where-Object status -eq 'passed').Count
    if(Test-Path -LiteralPath $ReportDirectory){$report|ConvertTo-Json -Depth 30|Set-Content -LiteralPath (Join-Path $ReportDirectory 'runtime-isolation-native.json') -Encoding utf8}
    foreach($process in $ownedProcesses){$process.Dispose()};foreach($entry in $ownedEntryProcesses){$entry.Dispose()};foreach($entry in $cliProcesses){$entry.Process.Dispose()}
}
if($report.status -ne 'passed'){throw "Native runtime isolation test failed; retain $ReportDirectory"}
