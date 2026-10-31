[CmdletBinding()]
param(
    [ValidateSet('Prepare','Execute')][string]$Stage='Prepare',
    [string]$BuildDirectory,
    [string]$PreparedDirectory,
    [ValidateSet('ClipboardSixCase','ClipboardReadControlSixCase','NativeLoader','StationReadMatrix')][string]$TrialMode='ClipboardSixCase'
)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=Join-Path $workspace 'artifacts/isolation-completion-20261030'
function Assert-PlainPath([string]$Path){
    $full=[IO.Path]::GetFullPath($Path);$drive=[IO.Path]::GetPathRoot($full)
    if($drive.Length -ne 3 -or [IO.DriveInfo]::new($drive).DriveType -ne [IO.DriveType]::Fixed){throw 'Fixed local volume required.'}
    $current=$drive
    foreach($part in $full.Substring($drive.Length).Split([char]'\',[StringSplitOptions]::RemoveEmptyEntries)){
        $current=Join-Path $current $part;$item=Get-Item -LiteralPath $current -Force
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse-point clipboard trial input rejected.'}
    }
}
function Assert-FileSet([string]$Directory,$Files){
    Assert-PlainPath $Directory
    $expected=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($file in $Files){
        $full=[IO.Path]::GetFullPath((Join-Path $Directory $file.path))
        if(-not $full.StartsWith($Directory+'\',[StringComparison]::OrdinalIgnoreCase) -or -not $expected.Add($full)){throw 'Invalid frozen trial path.'}
        Assert-PlainPath $full
        if((Get-Item -LiteralPath $full).Length -ne $file.bytes -or
            (Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256){throw 'Frozen trial bytes differ.'}
    }
    $actual=@(Get-ChildItem -LiteralPath $Directory -Recurse -File|Select-Object -ExpandProperty FullName)
    if(-not $expected.SetEquals([string[]]$actual)){throw 'Frozen trial file set differs.'}
}
if($Stage -eq 'Execute'){
    if(-not $PreparedDirectory -or $BuildDirectory){throw 'Execute requires only the exact PreparedDirectory reviewed for this one UAC trial.'}
    $prepared=[IO.Path]::GetFullPath($PreparedDirectory)
    if(-not $prepared.StartsWith((Join-Path $root 'clipboard-invocations')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Prepared trial must belong to this local work package.'}
    Assert-PlainPath $prepared
    $preparation=Get-Content -LiteralPath (Join-Path $prepared 'preparation.json') -Raw|ConvertFrom-Json
    if($preparation.status -cne 'prepared_not_executed' -or $preparation.source_snapshot_id -notmatch '^sha256:[a-f0-9]{64}$'){throw 'Prepared, unexecuted trial required.'}
    $BuildDirectory=$preparation.build_directory
    if($preparation.trial_mode -ceq 'native_loader_diagnostic'){$TrialMode='NativeLoader'}
    elseif($preparation.trial_mode -ceq 'native_station_read_matrix'){$TrialMode='StationReadMatrix'}
    elseif($preparation.trial_mode -ceq 'clipboard_readcontrol_sixcase'){$TrialMode='ClipboardReadControlSixCase'}
    elseif($preparation.trial_mode -cne 'clipboard_sixcase'){throw 'Prepared trial mode is not supported.'}
}else{
    if(-not $BuildDirectory -or $PreparedDirectory){throw 'Prepare requires only a tracked BuildDirectory; it does not execute programs or request UAC.'}
}
$build=[IO.Path]::GetFullPath($BuildDirectory)
if(-not $build.StartsWith((Join-Path $root 'clipboard-builds')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Tracked clipboard compilation directory required.'}
Assert-PlainPath $build
$record=Get-Content -LiteralPath (Join-Path $build 'build-result.json') -Raw|ConvertFrom-Json
if($record.status -cne 'passed_compilation_only'){throw 'Successful tracked compilation required.'}
foreach($source in $record.source_files){
    $path=[IO.Path]::GetFullPath((Join-Path $workspace $source.path))
    if(-not $path.StartsWith($workspace+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Source binding escapes workspace.'}
    Assert-PlainPath $path
    if((Get-Item -LiteralPath $path).Length -ne $source.bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $source.sha256){throw 'Clipboard trial sources changed; rebuild before proceeding.'}
}
$outputs=@{}
foreach($output in $record.outputs){
    if($output.directory -cne (Join-Path $build $output.kind) -or $outputs.ContainsKey($output.kind)){throw 'Invalid output directory binding.'}
    Assert-FileSet $output.directory $output.files;$outputs[$output.kind]=$output
}
foreach($name in @('lease','controller','probe')){if(-not $outputs.ContainsKey($name)){throw 'Missing clipboard trial output.'}}
$stationReadTrial=$TrialMode -ceq 'StationReadMatrix'
$readControlBrowserTrial=$TrialMode -ceq 'ClipboardReadControlSixCase'
$nativeTrial=$TrialMode -in 'NativeLoader','StationReadMatrix'
if($nativeTrial -and (-not $record.native_loader_variant -or -not $outputs.ContainsKey('native'))){throw 'Native-loader output and compiled variant required.'}
if($stationReadTrial -ne [bool]$record.station_read_matrix_variant){throw 'Native station matrix mode must match the compiled variant.'}
function Get-ProbeExecutable([string]$Kind,[string]$Project){
    [xml]$xml=Get-Content -LiteralPath (Join-Path $root $Project) -Raw
    $name=$xml.SelectSingleNode('/Project/PropertyGroup/AssemblyName').InnerText
    if($name -notmatch '^AutumnOS\.[A-Za-z0-9_.]+$'){throw 'Unexpected trial assembly name.'}
    $path=Join-Path $outputs[$Kind].directory ($name+'.exe')
    Assert-PlainPath $path;return $path
}
$broker=Get-ProbeExecutable 'lease' 'clipboard-station-lease/ClipboardStationLease.csproj'
$controller=Get-ProbeExecutable 'controller' 'clipboard-appcontainer-controller/ClipboardController.csproj'
$probe=Get-ProbeExecutable 'probe' 'clipboard-appcontainer-probe/ClipboardAppContainerProbe.csproj'
if($Stage -eq 'Prepare'){
    $prepared=Join-Path $root ('clipboard-invocations/prepared-'+[Guid]::NewGuid().ToString('N'))
    if([IO.Directory]::Exists($prepared) -or [IO.File]::Exists($prepared)){throw 'Fresh preparation required.'}
    [void][IO.Directory]::CreateDirectory($prepared)
    $cases=Join-Path $root 'clipboard-runs';[void][IO.Directory]::CreateDirectory($cases);Assert-PlainPath $cases
    $runtime='';$runtimeFiles=@()
    if(-not $nativeTrial){
        $runtime=Join-Path $workspace '.tools/webview2/154.0.4258.53/Microsoft.WebView2.FixedVersionRuntime.154.0.4258.53.x64'
        Assert-PlainPath $runtime
        $runtimeFiles=@(Get-ChildItem -LiteralPath $runtime -Recurse -File|Sort-Object FullName|ForEach-Object{
            Assert-PlainPath $_.FullName
            [ordered]@{path=[IO.Path]::GetRelativePath($runtime,$_.FullName).Replace('\','/');bytes=$_.Length;
                sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
        })
    }
    $manifest=[ordered]@{schema_version=1;case_root=$cases;child_directory=$outputs.probe.directory;
        child_executable=[IO.Path]::GetFileName($probe);child_files=$outputs.probe.files;runtime_directory=$runtime;
        runtime_version='154.0.4258.53';runtime_files=$runtimeFiles;
        broker_executable=$broker;broker_sha256=(Get-FileHash -LiteralPath $broker -Algorithm SHA256).Hash.ToLowerInvariant();broker_files=$outputs.lease.files;
        controller_executable=$controller;controller_sha256=(Get-FileHash -LiteralPath $controller -Algorithm SHA256).Hash.ToLowerInvariant();controller_files=$outputs.controller.files}
    $scope='One new private noninteractive station lease; ordinary/AppContainer startup gates, then six copy/cut/paste positive and negative cases on synthetic private clipboard only.'
    $manifest.trial_mode='clipboard_sixcase'
    if($readControlBrowserTrial){
        $manifest.trial_mode='clipboard_readcontrol_sixcase'
        $scope='One fresh private noninteractive station initially0x23. Ordinary controller changes only its exact package ACE to0x20023 (add READ_CONTROL only; no clipboard/ENUMERATE/READSCREEN). Same desktop0x87/Low label. Actual ordinary/AppContainer browser startup gates then six copy/cut/paste A/B cases with synthetic private clipboard only. After exact held-process exits/Job0/EOF/resource retirement restore initial station DACL hash, delete exact new profile and release lease. Existing WinSta0/Default ACLs and user clipboard untouched.'
    }
    if($nativeTrial){
        $manifest.trial_mode=if($stationReadTrial){'native_station_read_matrix'}else{'native_loader_diagnostic'}
        $manifest.native_child_directory=$outputs.native.directory
        $manifest.native_child_executable=if($stationReadTrial){'AutumnOS.NativeStationReadLoader.exe'}else{'AutumnOS.NativeUser32Loader.exe'}
        $manifest.native_child_files=$outputs.native.files
        $systemDirectory=[Environment]::GetFolderPath([Environment+SpecialFolder]::System)
        $manifest.native_system_dlls=@(foreach($name in @('user32.dll','gdi32.dll','shell32.dll')){
            $path=Join-Path $systemDirectory $name;Assert-PlainPath $path;$item=Get-Item -LiteralPath $path
            [ordered]@{name=$name;path=$item.FullName;bytes=$item.Length;version=$item.VersionInfo.FileVersion;
                sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
        })
        $scope='One new private noninteractive station; ordinary/AppContainer KERNEL32-only entry and System32 USER32/GDI32/SHELL32 DLL-load stages at unchanged 0x23/0x87 rights. No browser or clipboard actions.'
        if($stationReadTrial){$scope='One new private noninteractive station at initial 0x23; fixed A23/B23/B20023/B20123/B20323/B23-restored/A23-restored native DLL-load cases. Only its exact package ACE changes, after original groups exit/Job0/EOF, adding READ_CONTROL/ENUMERATE/READSCREEN of this new noninteractive station, never clipboard. Same desktop0x87/profileSID; restore original0x23 DACL and verify hash before release. No browser or clipboard actions.'}
    }
    $manifestPath=Join-Path $prepared 'input-manifest.json'
    $manifest|ConvertTo-Json -Depth 9|Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    [ordered]@{schema_version=1;producer='派蒙';status='prepared_not_executed';build_directory=$build;build_id=$record.build_id;
        source_snapshot_id=$record.source_snapshot_id;manifest_sha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();
        scope=$scope;trial_mode=$manifest.trial_mode;
        uac='not_run_requires_explicit_authorization_for_this_prepared_trial';clipboard='not_run';station_creation='not_run';profile_creation='not_run';
        product_acceptance='not_claimed';publication='not_performed'}|
        ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $prepared 'preparation.json') -Encoding utf8NoBOM
    Write-Host "Prepared only. No program, UAC, station, profile or clipboard call executed: $prepared"
    exit 0
}
$manifestPath=Join-Path $prepared 'input-manifest.json'
Assert-PlainPath $manifestPath
if((Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $preparation.manifest_sha256 -or
    $record.source_snapshot_id -cne $preparation.source_snapshot_id){throw 'Prepared trial identity differs.'}
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
if($manifest.broker_executable -cne $broker -or $manifest.controller_executable -cne $controller){throw 'Prepared executable paths differ.'}
if($manifest.trial_mode -cne $preparation.trial_mode){throw 'Prepared trial mode differs.'}
if($nativeTrial){
    Assert-FileSet $manifest.native_child_directory $manifest.native_child_files
    if(@($manifest.native_system_dlls).Count -ne 3){throw 'Reviewed system-DLL metadata bindings required.'}
    foreach($dll in $manifest.native_system_dlls){
        $expected=Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) $dll.name
        if($dll.name -notin 'user32.dll','gdi32.dll','shell32.dll' -or $dll.path -ine $expected){throw 'System DLL scope differs.'}
        Assert-PlainPath $dll.path
        if((Get-Item -LiteralPath $dll.path).Length -ne $dll.bytes -or
            (Get-FileHash -LiteralPath $dll.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $dll.sha256){throw 'Observed system DLL bytes changed before the trial.'}
    }
}
else{Assert-FileSet $manifest.runtime_directory $manifest.runtime_files}
$executionPath=Join-Path $prepared 'execution.json'
if([IO.File]::Exists($executionPath)){throw 'This prepared trial has already been attempted; retain the exact first execution record.'}
$execution=[ordered]@{schema_version=1;producer='派蒙';status='launching_authorized_trial';started_utc=[DateTimeOffset]::UtcNow;
    build_id=$record.build_id;source_snapshot_id=$record.source_snapshot_id;manifest_sha256=$preparation.manifest_sha256;uac_scope='one_new_private_station_holder_only'}
$execution|ConvertTo-Json|Set-Content -LiteralPath $executionPath -Encoding utf8NoBOM
# Execute only after the user explicitly authorizes this concrete prepared trial.
# The broker is ordinary. Its one runas child can only hold the new station.
# This script never terminates a process or changes a policy/ACL itself.
Write-Host "Executing the explicitly authorized prepared private-station trial: $prepared"
& $broker --broker --probe $controller --manifest $manifestPath 2>&1|
    Tee-Object -FilePath (Join-Path $prepared 'broker-output.log')|Out-Host
$nativeExit=$LASTEXITCODE
$execution.status='broker_returned';$execution.exit_code=$nativeExit;$execution.completed_utc=[DateTimeOffset]::UtcNow
$execution|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $executionPath -Encoding utf8NoBOM
if($nativeExit -ne 0){throw 'Prepared clipboard trial failed or was cancelled. Inspect retained evidence before any retry.'}
try{
    $brokerRecords=@(foreach($line in (Get-Content -LiteralPath (Join-Path $prepared 'broker-output.log'))){
        if([Text.Encoding]::UTF8.GetByteCount($line) -gt 65536){throw 'Broker output line exceeds the reviewed limit.'}
        try{$row=$line|ConvertFrom-Json -ErrorAction Stop}catch{continue}
        if($row.kind -ceq 'owned_private_clipboard_station_lease'){$row}
    })
    $ready=@($brokerRecords|Where-Object status -CEQ 'ready_for_single_uac')
    $done=@($brokerRecords|Where-Object status -CEQ 'passed_lease_only')
    if($ready.Count -ne 1 -or $done.Count -ne 1 -or
        $ready[0].detail.nonce -cne $done[0].detail.nonce -or $done[0].detail.nonce -notmatch '^[a-f0-9]{32}$' -or
        $ready[0].detail.ManifestSha256 -cne $preparation.manifest_sha256 -or
        -not $done[0].detail.original_controller_handle_exit_observed -or -not $done[0].detail.original_helper_handle_exit_observed -or
        -not $done[0].detail.explicit_release_handshake -or $done[0].detail.controller_exit_code -ne 0 -or $done[0].detail.helper_exit_code -ne 0){throw 'Independent lease completion evidence differs.'}
    if($manifest.case_root -cne (Join-Path $root 'clipboard-runs')){throw 'Controller report root differs from the prepared scope.'}
    $reportPath=Join-Path $manifest.case_root ('clipboard-case-'+$done[0].detail.nonce+'/report.json')
    Assert-PlainPath $reportPath
    $report=Get-Content -LiteralPath $reportPath -Raw|ConvertFrom-Json
    $expectedStatus=if($stationReadTrial){'completed_native_station_read_matrix_only'}elseif($nativeTrial){'passed_native_loader_diagnostic_only'}elseif($readControlBrowserTrial){'passed_owned_private_station_readcontrol_sixcase_mechanism_only'}else{'passed_owned_private_station_sixcase_mechanism_only'}
    $expectedSeeds=if($nativeTrial){0}else{6}
    if($report.status -cne $expectedStatus -or
        $report.manifest_sha256 -cne $preparation.manifest_sha256 -or -not $report.cleanup_proven -or $report.report_error -or
        $report.seed_commands_sent -ne $expectedSeeds -or -not $report.profile_created_by_this_invocation -or $report.profile_deletion_hresult -ne 0 -or
        -not $report.original_profile_path_not_found_proven -or -not $report.same_profile_SID_not_found_proven -or
        -not $report.lease.closed_owned_desktop_and_station){throw 'Controller completion or exact-owned cleanup proof is missing.'}
    if($stationReadTrial){
        if($report.trial_mode -cne 'native_station_read_matrix' -or $report.browser_execution -cne 'not_run' -or $report.clipboard_operations -cne 'not_run' -or @($report.children).Count -ne 0){throw 'Station read matrix scope differs.'}
        if($report.lease.station_name -cne ('AutumnOS_ClipboardProbe_'+$done[0].detail.nonce) -or
            $report.lease.desktop_name -cnotmatch '^AutumnOS_ClipboardDesktop_[a-f0-9]{32}$' -or $report.lease.noninteractive -ne $true -or
            $report.lease.desktop_DACL_verified -ne $true -or $report.lease.desktop_label_low -ne $true -or $report.lease.station_label_low -ne $true -or
            $report.cleanup_journal.any_failure -ne $false -or $report.lease.cleanup.any_failure -ne $false){throw 'One exact authenticated private station, fixed desktop or cleanup binding incomplete.'}
        $matrix=$report.station_read_matrix
        if($matrix.native_mode_completed -ne $true -or $matrix.restored_original_DACL -ne $true -or
            $matrix.initial_DACL_sha256 -notmatch '^[a-f0-9]{64}$' -or $matrix.final_DACL_sha256 -cne $matrix.initial_DACL_sha256){throw 'Exact initial station DACL restoration is not proven.'}
        $fixed=@(@{label='A23';side='A';mask=35},@{label='B23';side='B';mask=35},
            @{label='B20023';side='B';mask=131107},@{label='B20123';side='B';mask=131363},@{label='B20323';side='B';mask=131875},
            @{label='B23-restored';side='B';mask=35},@{label='A23-restored';side='A';mask=35})
        if(@($matrix.cases).Count -ne $fixed.Count){throw 'Fixed seven-case station matrix incomplete.'}
        $changes=@($report.lease.station_read_matrix_changes|Where-Object stage -CEQ 'fixed_case_before_launch')
        if($changes.Count -ne $fixed.Count -or $report.lease.station_read_matrix_current_mask -ne 35 -or
            $report.lease.station_read_matrix_next_index -ne 7 -or $report.lease.restored_original_DACL -ne $true){throw 'Observed station-mask sequence or final original descriptor is incomplete.'}
        $seenNonces=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        for($index=0;$index -lt $fixed.Count;$index++){
            $case=$matrix.cases[$index];$expected=$fixed[$index];$child=$case.child
            if($child.expected_station -cne $report.lease.station_name -or $child.expected_desktop -cne $report.lease.desktop_name -or
                @($child.launch_facts).Count -ne 1 -or $child.launch_facts[0].station -cne $report.lease.station_name -or
                $child.launch_facts[0].exact_desktop -cne $report.lease.desktop_name -or $child.launch_facts[0].succeeded -ne $true -or
                $child.launch_facts[0].native_error -ne 0 -or $child.launch_facts[0].inherited_stdout_handles -ne 1 -or
                $child.launch_facts[0].inherited_stdin_handles -ne 0 -or $child.launch_facts[0].inherited_job_or_desktop_handles -ne 0){throw 'All matrix children must use the same exact leased station/desktop with only owned stdout inherited.'}
            $change=$changes[$index];$previousMask=if($index -eq 0){35}else{$fixed[$index-1].mask}
            if($change.index -ne $index -or $change.label -cne $expected.label -or $change.station_package_mask -ne $expected.mask -or
                $change.before_mask -ne $previousMask -or $change.previous_groups_empty_and_retired -ne $true -or
                $change.owner_system_package_only -ne $true -or $change.protected_DACL -ne $true -or $change.Low_NW_unchanged -ne $true -or
                $change.after_DACL_sha256 -notmatch '^[a-f0-9]{64}$'){throw 'Exact owned-station descriptor transition readback incomplete.'}
            if($case.label -cne $expected.label -or $case.side -cne $expected.side -or $case.station_package_mask -ne $expected.mask -or
                $case.nonce -notmatch '^[a-f0-9]{32}$' -or -not $seenNonces.Add($case.nonce) -or $case.diagnostic_execution_completed -ne $true -or
                $child.diagnostic_execution_completed -ne $true -or $child.OS_identity_proven -ne $true -or $child.initial_suspended_identity_job_verified -ne $true -or
                $child.group_empty_proven -ne $true -or $child.retired -ne $true -or $child.stdout_eof -ne $true -or $child.terminal_received -ne $true -or
                $child.observation_budget_exceeded -ne $false -or $child.cleanup.any_failure -ne $false -or @($child.failures).Count -ne 0 -or
                $child.audit.observation_gap -ne $false -or @($child.audit.Failures).Count -ne 0){throw 'Station matrix child identity, protocol or cleanup evidence incomplete.'}
            if($child.job.atomic_launch_attribute_attached -ne $true -or $child.job.limit_flags -ne 0 -or $child.job.total_processes -ne 1 -or
                $child.job.active_processes -ne 0 -or @($child.job.last_process_ids).Count -ne 0 -or $child.job.Empty -ne $true -or
                $child.job.original_handle_closed_after_empty -ne $true -or $child.job.query_failure_seen -ne $false -or $child.job.membership_failure_seen -ne $false -or
                @($child.audit.Held).Count -ne 1 -or @($child.original_thread_resume_attempts).Count -ne 1 -or $child.original_thread_resume_attempts[0].previous_suspend_count -ne 1){throw 'Station matrix original Job, process or resume evidence incomplete.'}
            $held=$child.audit.Held[0];$token=$held.token
            if($held.original_handle_signaled -ne $true -or $held.failure -or $held.image_in_new_copied_trees -ne $true -or
                $held.image_basename -cne 'AutumnOS.NativeStationReadLoader.exe' -or $token.ordinary_parent_os_query -ne $true -or $token.capability_count -ne 0){throw 'Station matrix held-process OS identity incomplete.'}
            $terminal=@($child.events|Where-Object kind -CEQ 'native_loader_complete')
            if($terminal.Count -ne 1 -or $terminal[0].nonce -cne $case.nonce -or $terminal[0].host_pid -ne $held.pid -or
                $terminal[0].detail.failed_stage -cne $child.failed_stage -or $terminal[0].detail.native_error -ne $child.native_error){throw 'Station matrix native terminal binding incomplete.'}
            if($case.loader_succeeded){
                if($child.loader_completed -ne $true -or $held.exit_code -ne 0 -or @($child.events).Count -ne 11 -or
                    $terminal[0].detail.result -cne 'passed' -or $terminal[0].detail.native_error -ne 0 -or $terminal[0].detail.failed_stage -cne 'none'){throw 'Station native success lacks complete stages and original exit0.'}
            }else{
                if($expected.side -ceq 'A' -or $child.loader_completed -ne $false -or $held.exit_code -ne 20 -or $terminal[0].detail.result -cne 'failed' -or
                    $child.failed_stage -notin @('load_user32','load_gdi32','load_shell32','user32_metadata_exports','user32_binding')){throw 'Station negative observation lacks an explicit native failure and original exit20.'}
            }
            if($expected.side -ceq 'B'){
                if($token.is_appcontainer -ne 1 -or $token.expected_package_sid_matches -ne $true -or $token.integrity_rid -ne 4096 -or
                    $token.package_sid_sha256 -cne $report.profile_sid_sha256){throw 'Station B case does not match the one exact zero-capability profile.'}
            }elseif($token.is_appcontainer -ne 0 -or $token.integrity_rid -ne 8192){throw 'Station A ordinary positive control failed.'}
        }
    }elseif($nativeTrial){
        if($report.trial_mode -cne 'native_loader_diagnostic' -or $report.browser_execution -cne 'not_run' -or $report.clipboard_operations -cne 'not_run' -or
            @($report.children).Count -ne 2 -or @($report.cases).Count -ne 2){throw 'Native diagnostic scope or matrix differs.'}
        foreach($side in @('A','B')){
            $case=@($report.cases|Where-Object{$_.side -ceq $side -and $_.kind -ceq 'native_loader_diagnostic_only'})
            if($case.Count -ne 1 -or -not $case[0].native_complete.completed -or $case[0].native_complete.result -cne 'passed'){throw 'Native DLL layer completion is not proven.'}
        }
    }else{
      if($readControlBrowserTrial){
        $boundary=$report.readcontrol_boundary
        if($report.trial_mode -cne 'clipboard_readcontrol_sixcase' -or $boundary.readcontrol_only_applied -ne $true -or
            $boundary.restored_original_DACL -ne $true -or $boundary.current_station_package_mask -ne 35 -or $boundary.desktop_package_mask -ne 135 -or
            $boundary.initial_DACL_sha256 -notmatch '^[a-f0-9]{64}$' -or $boundary.runtime_DACL_sha256 -notmatch '^[a-f0-9]{64}$' -or
            $boundary.final_DACL_sha256 -cne $boundary.initial_DACL_sha256 -or $boundary.runtime_DACL_sha256 -ceq $boundary.initial_DACL_sha256 -or
            $report.lease.station_name -cne ('AutumnOS_ClipboardProbe_'+$done[0].detail.nonce) -or
            $report.lease.desktop_name -cnotmatch '^AutumnOS_ClipboardDesktop_[a-f0-9]{32}$' -or $report.lease.noninteractive -ne $true -or
            $report.lease.desktop_DACL_verified -ne $true -or $report.lease.desktop_label_low -ne $true -or $report.lease.station_label_low -ne $true -or
            $report.cleanup_journal.any_failure -ne $false -or $report.lease.cleanup.any_failure -ne $false){throw 'Exact READ_CONTROL-only browser boundary or original DACL restoration is incomplete.'}
        $observations=@($boundary.observations)
        $initial=@($observations|Where-Object stage -CEQ 'initial');$runtime=@($observations|Where-Object stage -CEQ 'runtime');$final=@($observations|Where-Object stage -CEQ 'final')
        if($initial.Count -ne 1 -or $runtime.Count -ne 1 -or $final.Count -ne 1 -or $initial[0].actual_child_count -ne 0 -or
            $initial[0].group_state_gate_satisfied -ne $true -or $final[0].actual_child_count -ne 14 -or
            $final[0].group_exit_gate_required -ne $true -or $final[0].group_state_gate_satisfied -ne $true){throw 'Boundary activation before any child or restoration after all 14 groups is not proven.'}
        foreach($observation in $observations){
            $originalPhase=$observation.stage -in 'initial','final'
            $expectedMask=if($originalPhase){35}else{131107};$expectedHash=if($originalPhase){$boundary.initial_DACL_sha256}else{$boundary.runtime_DACL_sha256}
            if($observation.station_package_mask -ne $expectedMask -or $observation.DACL_sha256 -cne $expectedHash -or
                $observation.boundary_readback_verified -ne $true -or $observation.original_handle_and_exact_sid_verified -ne $true -or
                $observation.owner_system_package_only -ne $true -or $observation.protected_DACL -ne $true -or $observation.Low_NW_unchanged -ne $true -or
                $observation.desktop_package_mask -ne 135 -or $observation.existing_object_ACL_changes -cne 'none'){throw 'A live boundary observation differs from the exact READ_CONTROL-only descriptor.'}
        }
        if(@($final[0].groups).Count -ne 14 -or @($final[0].groups|Where-Object{$_.retired -ne $true -or $_.stdio_complete -ne $true -or
            $_.resource_cleanup_proven -ne $true -or $_.original_handles_signaled -ne $true -or $_.job_active_processes -ne 0 -or
            @($_.job_process_ids).Count -ne 0 -or $_.job_empty -ne $true}).Count){throw 'Original DACL was not restored after every actual owned process group retired.'}
      }
      $cases=@($report.cases|Where-Object kind -CEQ 'real_private_station_clipboard_AB')
      $preflights=@($report.cases|Where-Object kind -CEQ 'private_station_no_clipboard_preflight')
      if($cases.Count -ne 6 -or $preflights.Count -ne 2 -or @($report.children).Count -ne 14){throw 'Required preflight or six-case matrix is incomplete.'}
      foreach($side in @('A','B')){
        $preflight=@($preflights|Where-Object side -CEQ $side)
        if($preflight.Count -ne 1 -or $preflight[0].status -cne 'passed' -or $preflight[0].clipboard_seed_commands -ne 0){throw 'No-clipboard compatibility gate is incomplete.'}
        foreach($operation in @('copy','cut','paste')){
            $case=@($cases|Where-Object {$_.side -ceq $side -and $_.operation -ceq $operation})
            if($case.Count -ne 1 -or $case[0].status -cne 'passed' -or -not $case[0].baseline_never_sent_to_browser -or
                -not $case[0].collector_final.passed -or $case[0].collector_final.sample_count -ne 10){throw 'Six-case positive or negative control is incomplete.'}
        }
      }
    }
    foreach($child in $report.children){
        if(-not $child.original_group_exit_proven -or -not $child.host_exit_zero -or -not $child.retired -or -not $child.output_drained_after_EOF -or
            @($child.failures).Count -ne 0 -or $child.audit.ObservationGap -or
            -not $child.job.atomic_launch_attribute_attached -or $child.job.active_processes -ne 0 -or @($child.job.last_process_ids).Count -ne 0 -or
            $child.job.query_failure_seen -or $child.job.membership_failure_seen -or -not $child.job.original_handle_closed_after_empty){throw 'A child has unresolved audit or original-process cleanup evidence.'}
        if($child.kind -ceq 'browser' -and (-not $child.browser_normal_event -or -not $child.final_sdk_empty -or -not $child.host_complete_received -or
            -not $child.browser_token_verified_before_load)){throw 'Browser normal completion proof is incomplete.'}
        if($child.kind -ceq 'browser' -and $child.operation -ceq 'paste'){
            if(-not $child.paste_capture_complete -or -not $child.paste_final_snapshot_observed -or -not $child.paste_terminal_summary_matches){throw 'Paste evidence is incomplete at browser shutdown.'}
            $pasteCanonical=(@($child.paste_events)|ForEach-Object{
                $trusted=if($_.trusted){1}else{0};$empty=if($_.text_empty){1}else{0}
                "$($_.event_index)|$trusted|$empty|$($_.text_sha256)`n"
            }) -join ''
            $pasteHash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($pasteCanonical)))
            if($pasteHash -cne $child.paste_event_digest_sha256){throw 'Paste stream digest differs from the terminal evidence.'}
            if($child.side -ceq 'B' -and @($child.paste_events|Where-Object{-not $_.trusted -or -not $_.text_empty}).Count -ne 0){throw 'Restricted paste observed a nonempty or untrusted event.'}
            if($child.side -ceq 'A' -and @($child.paste_events|Where-Object{$_.trusted -and -not $_.text_empty -and $_.text_sha256 -ceq $child.expected_paste_baseline_sha256}).Count -lt 1){throw 'Ordinary native paste positive control is missing.'}
            $emptyTextHash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([byte[]]@()))
            if($child.side -ceq 'B' -and ($child.paste_final_value_empty -cne $true -or $child.paste_final_value_sha256 -cne $emptyTextHash)){throw 'Restricted final paste DOM is not explicitly empty.'}
            if($child.side -ceq 'A' -and ($child.paste_final_value_empty -cne $false -or $child.paste_final_value_sha256 -cne $child.expected_paste_baseline_sha256)){throw 'Ordinary final paste DOM does not match the native positive control.'}
        }
        foreach($process in $child.audit.Held){
            if(-not $process.original_handle_signaled -or $process.failure){throw 'A held process has unresolved exit or token evidence.'}
            if($child.side -ceq 'B' -and $child.kind -in 'browser','native' -and
                ($process.token.is_appcontainer -ne 1 -or -not $process.token.expected_package_sid_matches -or
                $process.token.integrity_rid -gt 4096 -or $process.token.capability_count -ne 0)){throw 'Final restricted-process token proof is incomplete.'}
        }
    }
    $execution.status=if($stationReadTrial){'completed_native_station_read_matrix_only'}elseif($nativeTrial){'passed_native_loader_diagnostic_only'}elseif($readControlBrowserTrial){'passed_readcontrol_browser_sixcase_mechanism_only'}else{'passed_mechanism_evidence_only'}
    $execution.controller_report=$reportPath
    $execution.controller_report_sha256=(Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $execution.complete_isolation='not_claimed'
}catch{
    $execution.status='failed_evidence_validation';$execution.validation_error_type=$_.Exception.GetType().Name
    throw
}finally{
    $execution|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $executionPath -Encoding utf8NoBOM
}
