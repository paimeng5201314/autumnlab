[CmdletBinding()]
param([Parameter(Mandatory)][string]$RunRoot,[string]$OutputPath)
# Read only except an explicitly requested NEW output file. No process launch or GUI.
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$RunRoot=[IO.Path]::GetFullPath($RunRoot)
$manifestPath=Join-Path $RunRoot 'run-manifest.json'
if(-not(Test-Path -LiteralPath $manifestPath -PathType Leaf)){throw 'Fresh run-manifest.json is required; no discovery of historical reports.'}
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
if($manifest.kind -ne 'fresh_owned_probe_matrix_invocation' -or [IO.Path]::GetFullPath($manifest.run_root) -ne $RunRoot){throw 'Manifest root or kind mismatch.'}
$start=[DateTimeOffset]::Parse($manifest.started_utc)
$bindingErrors=[Collections.Generic.List[string]]::new()
foreach($file in @($manifest.source_files)+@($manifest.binary_files)){
    if(-not(Test-Path -LiteralPath $file.path -PathType Leaf)){$bindingErrors.Add('Missing bound file: '+$file.path);continue}
    if((Get-FileHash -LiteralPath $file.path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256){$bindingErrors.Add('Changed bound file: '+$file.path)}
}
function Require([bool]$Condition,[string]$Reason){if(-not $Condition){throw $Reason}}
function Hash([string]$Path){(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Get-NetLogProof($Case,[string]$Directory,[string]$ExpectedRuntime){
    $path=[IO.Path]::GetFullPath((Join-Path $Directory $Case.netlog))
    Require ($path.StartsWith($Directory+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) 'NetLog path escapes this new mode directory.'
    Require (Test-Path -LiteralPath $path -PathType Leaf) 'New-case NetLog missing.'
    $log=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -Depth 100
    $client=$log.constants.clientInfo
    $command=[string]$client.command_line
    Require ($client.version -eq $ExpectedRuntime) 'NetLog actual runtime differs.'
    Require ($command -match '(?:^|\s)--force-renderer-accessibility(?:\s|=|$)') 'Actual NetLog command line lacks force-renderer-accessibility.'
    $disabled=@([regex]::Matches($command,'(?:^|\s)--disable-features=([^\s"]+)')|ForEach-Object{$_.Groups[1].Value -split ','})
    $enabled=@([regex]::Matches($command,'(?:^|\s)--enable-features=([^\s"]+)')|ForEach-Object{$_.Groups[1].Value -split ','})
    Require ($disabled -contains 'WebRtcHideLocalIpsWithMdns') 'Actual NetLog command line lacks the shared mDNS suppression.'
    Require (-not($enabled -contains 'WebRtcHideLocalIpsWithMdns')) 'Conflicting feature enable switch found.'
    Require ($command -notmatch '(?:^|\s)--(?:no-sandbox|disable-web-security|ignore-certificate-errors)(?:\s|=|$)') 'Unexpected weakening switch in actual command line.'
    Require ($command -match '(?:^|\s)--no-proxy-server(?:\s|$)' -and $command.Contains('--net-log-capture-mode=Default')) 'Expected test-only NetLog/no-proxy arguments missing.'
    [pscustomobject]@{netlog=$path;sha256=Hash $path;runtime=$client.version;effective_arguments_evidence='NetLog constants.clientInfo.command_line';actual_command_line=$command;shared_flags_present=$true;sandbox_disable_switch_absent=$true}
}
$results=[Collections.Generic.List[object]]::new()
foreach($mode in @('runtime-ab','resources-storage','network','same-origin')){
    $directory=Join-Path $RunRoot $mode;$path=Join-Path $directory 'probe.json';$invocationPath=Join-Path $directory 'invocation.json'
    if(-not(Test-Path -LiteralPath $path -PathType Leaf)){$results.Add([pscustomobject]@{mode=$mode;status='not_run';reason='No report at exact new-run path.'});continue}
    $details=[ordered]@{};$errors=[Collections.Generic.List[string]]::new()
    try{
        Require ($bindingErrors.Count -eq 0) 'Source/binary binding changed after run start.'
        Require ($manifest.requested_modes -contains $mode) 'Report mode was not requested by this manifest.'
        Require (Test-Path -LiteralPath $invocationPath -PathType Leaf) 'New invocation record missing.'
        $invocation=Get-Content -LiteralPath $invocationPath -Raw|ConvertFrom-Json
        $report=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -Depth 100
        Require ($invocation.mode -eq $mode -and [IO.Path]::GetFullPath($invocation.report) -eq $path) 'Invocation path/mode mismatch.'
        Require ($invocation.exit_code -eq 0) 'Native probe process did not exit successfully.'
        $rs=[DateTimeOffset]::Parse($report.started_utc);$rf=[DateTimeOffset]::Parse($report.finished_utc)
        Require ($rs -ge $start -and $rs -ge [DateTimeOffset]::Parse($invocation.started_utc).AddSeconds(-1) -and $rf -ge $rs -and $rf -le [DateTimeOffset]::Parse($invocation.finished_utc).AddSeconds(1)) 'Report timestamps do not belong to this new invocation.'
        Require ($report.status -eq 'passed' -and $null -eq $report.failure) 'Report itself failed or did not prove pass.'
        $details.report=$path;$details.report_sha256=Hash $path;$details.started_utc=$report.started_utc;$details.finished_utc=$report.finished_utc
        if($mode -in @('runtime-ab','resources-storage')){
            $arg=if($mode -eq 'runtime-ab'){$report.browser_arguments}else{$report.additional_browser_arguments}
            Require ($arg -eq $manifest.expected_browser_arguments) 'Configured AdditionalBrowserArguments differs from current source.'
            $details.argument_scope='Exact configured API argument string; complete actual browser command line was not captured by this mode.'
        }else{
            $arg=if($mode -eq 'same-origin'){$report.browser_arguments}else{$report.additional_browser_arguments}
            Require ($arg.StartsWith($manifest.expected_browser_arguments+' [test_only_', [StringComparison]::Ordinal)) 'Shared configured arguments were not reported.'
            $details.argument_scope='Report placeholder checked; actual per-case command line verified separately from fresh NetLog.'
            $details.effective_arguments=@($report.cases|ForEach-Object{Get-NetLogProof $_ $directory $manifest.expected_runtime})
        }
        if($mode -eq 'runtime-ab'){
            Require (@($report.cases).Count -eq 2) 'Expected independent A/B runtime cases.'
            foreach($c in $report.cases){Require ($c.status -eq 'passed' -and $c.runtime -eq $manifest.expected_runtime -and -not $c.process_failed -and $c.source_checked_before_and_after) 'Runtime case failed/binding mismatch.'}
            $a=@($report.cases|Where-Object case -eq 'A');$b=@($report.cases|Where-Object case -eq 'B')
            Require ($a.Count -eq 1 -and $b.Count -eq 1 -and -not $a[0].validator_produced_proof -and $b[0].validator_produced_proof -and $b[0].proof_bound_to_instance_and_origin) 'Required reject/accept controls missing.'
            $details.scope='Host ExecuteScript challenge accepts production headers and rejects missing Connection-Allowlist; no package/SDK product entry test.'
        }elseif($mode -eq 'resources-storage'){
            $required=@('external_script_executed_under_production_headers','local_fetch_explicitly_blocked_by_current_profile','inline_WASM_bytes_compile_and_execute_allowed','arbitrary_javascript_eval_remains_blocked','storage_positive_control_all_stores_write_and_read','same_origin_same_profile_navigation_positive_control','distinct_profiles_generated_by_production_binding_function','all_owned_browser_processes_exited','same_profile_InPrivate_restart_all_stores_empty','async_clipboard_document_policy_denies_both_directions')
            Require (@($report.checks).Count -ge 25 -and @($report.checks|Where-Object status -ne 'passed').Count -eq 0) 'Resource/storage checks failed or missing.'
            foreach($id in $required){Require (@($report.checks|Where-Object id -eq $id).Count -eq 1) ('Required resource/storage assertion missing: '+$id)}
            Require (@($report.contexts).Count -eq 5) 'Expected five controlled profile contexts.'
            foreach($c in $report.contexts){Require ($c.runtime -eq $manifest.expected_runtime -and $c.in_private -and $c.browser_exited -and -not $c.ProcessFailed) 'Storage browser lifecycle/runtime mismatch.'}
            $details.check_count=@($report.checks).Count;$details.scope='Controlled identities, same-origin account/app/source storage separation, local resources and inline WASM; async clipboard policy/query only.'
        }elseif($mode -eq 'network'){
            Require (@($report.cases).Count -eq 2) 'Expected A/B network cases.'
            $expectedChannels=@('fetch','xhr','beacon','websocket','eventsource','img','remote-script','module-import','css-link','fontface','audio','video','iframe','form-frame','worker-fetch')
            foreach($c in $report.cases){
                Require ($c.case -in @('A','B') -and $c.status -eq 'passed' -and $c.runtime -eq $manifest.expected_runtime -and $c.browser_exited -and -not $c.process_failed -and $c.valid_netlog) 'Network case incomplete/failed.'
                Require ($c.positive_control.verified -and $c.positive_control_after.verified -and $c.network_complete -and $c.fixture_channel_attempt_count -ge 26 -and $c.extra_dns_and_preconnect_hint_count -eq 2 -and $c.per_channel_controls -and $c.native_worker_controls) 'Network execution/receiver controls incomplete.'
                foreach($channel in $expectedChannels){$entry=@($c.channel_receiver_checks|Where-Object channel -eq $channel);Require ($entry.Count -eq 1 -and $entry[0].receiver_observed -eq ($c.case -eq 'A')) ('Missing/wrong receiver evidence: '+$c.case+'/'+$channel)}
                if($c.case -eq 'A'){Require ($c.extra_tcp_connections -gt 0 -and $c.extra_udp_datagrams -gt 0 -and @($c.dns_resolver_events).Count -gt 0 -and @($c.preconnect_resolver_events).Count -gt 0) 'A must exercise TCP, UDP, DNS and preconnect separately.'}
                else{Require ($c.extra_tcp_connections -eq 0 -and $c.extra_udp_datagrams -eq 0 -and @($c.dns_resolver_events).Count -eq 0 -and @($c.preconnect_resolver_events).Count -eq 0) 'B receiver observed unauthorized activity.'}
            }
            Require (@($report.cases|Where-Object case -eq 'A').Count -eq 1 -and @($report.cases|Where-Object case -eq 'B').Count -eq 1) 'A/B labels are not unique.'
            $details.scope='Owned loopback receiver plus host NetLog, 15 per-channel receiver controls, worker/Service Worker and DNS/preconnect.'
        }else{
            Require (@($report.cases).Count -eq 4) 'Expected four same-origin cases.'
            foreach($channel in @('websocket','webtransport')){foreach($production in @($false,$true)){
                $entries=@($report.cases|Where-Object{$_.channel -eq $channel -and $_.production_policy -eq $production});Require ($entries.Count -eq 1) 'Same-origin case missing or duplicated.';$c=$entries[0]
                Require ($c.status -eq 'passed' -and $c.runtime -eq $manifest.expected_runtime -and $c.observed.attempted -and $c.observed.available -and $c.valid_netlog -and $c.browser_exited -and -not $c.process_failed) 'Same-origin native execution incomplete.'
                Require ($c.channel_network_request_started -eq (-not $production)) 'Same-origin exact /owned request control mismatch.'
                Require (($production -and @($c.exact_channel_network_requests).Count -eq 0) -or (-not $production -and @($c.exact_channel_network_requests).Count -gt 0 -and @($c.resolver_events).Count -gt 0)) 'Same-origin receiver/network-service counterexample missing.'
            }}
            $details.scope='connect-self local counterexample versus production connect-none for exact /owned requests. Initial synthetic navigation DNS is retained, not claimed zero. No trusted WebTransport handshake.'
        }
        $results.Add([pscustomobject]@{mode=$mode;status='passed';details=$details})
    }catch{$errors.Add($_.Exception.Message);$results.Add([pscustomobject]@{mode=$mode;status='failed';report=$path;errors=$errors.ToArray();details=$details})}
}
$overall=if(@($results|Where-Object status -eq 'failed').Count -gt 0 -or $bindingErrors.Count -gt 0){'failed'}elseif(@($results|Where-Object status -eq 'not_run').Count -gt 0){'not_run'}else{'passed'}
$summary=[pscustomobject]@{schema_version=1;kind='fresh_owned_probe_matrix_summary';status=$overall;build_id=$manifest.build_id;run_root=$RunRoot;started_utc=$manifest.started_utc;summarized_utc=[DateTimeOffset]::UtcNow.ToString('o');manifest_sha256=Hash $manifestPath;binding_errors=$bindingErrors.ToArray();results=$results.ToArray();not_run=@('trusted_certificate_HTTP3_WebTransport_handshake','authoritative_external_DNS_receiver','real_Logto_dual_account_acceptance','actual_clipboard_AB_use_separate_report','final_EXE_ZIP_product_UI_SDK_save_use_separate_reports');approval_effect='none; this evidence summary is never an application runtime authorization source'}
$json=$summary|ConvertTo-Json -Depth 30
if($OutputPath){$OutputPath=[IO.Path]::GetFullPath($OutputPath);if(Test-Path -LiteralPath $OutputPath){throw 'Summary output already exists; refusing overwrite.'};if(-not$OutputPath.StartsWith($RunRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Summary must remain inside this new run directory.'};$json|Set-Content -LiteralPath $OutputPath -Encoding utf8}
$json
