[CmdletBinding()]
param([ValidateSet('Prepare','Execute')][string]$Stage='Prepare',[string]$BuildDirectory,[string]$PreparedDirectory)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=Join-Path $workspace 'artifacts/isolation-completion-20261030'
function Require-Plain([string]$Path){
    $full=[IO.Path]::GetFullPath($Path);$drive=[IO.Path]::GetPathRoot($full)
    if($drive.Length -ne 3 -or [IO.DriveInfo]::new($drive).DriveType -ne [IO.DriveType]::Fixed){throw 'Fixed local volume required.'}
    $current=$drive
    foreach($part in $full.Substring($drive.Length).Split([char]'\',[StringSplitOptions]::RemoveEmptyEntries)){
        $current=Join-Path $current $part
        if((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse point input rejected.'}
    }
}
function Verify-Files([string]$Directory,$Files,[bool]$ExactSet){
    Require-Plain $Directory
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($binding in $Files){
        if([IO.Path]::IsPathRooted($binding.path) -or $binding.path -match '[:\x00]' -or
            @($binding.path.Split([char[]]@('/','\'),[StringSplitOptions]::None)|Where-Object{$_ -in '', '.', '..' -or $_.EndsWith(' ') -or $_.EndsWith('.')}).Count){throw 'Plain relative binding required.'}
        $file=[IO.Path]::GetFullPath((Join-Path $Directory $binding.path))
        if(-not $file.StartsWith($Directory.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or -not $seen.Add($file)){throw 'Duplicate or escaped binding.'}
        Require-Plain $file;$item=Get-Item -LiteralPath $file
        if($item.PSIsContainer -or $item.Length -ne $binding.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $binding.sha256){throw 'Frozen source or payload bytes differ.'}
    }
    if($ExactSet){
        $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($Directory)
        $actual=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        while($pending.Count){foreach($item in (Get-ChildItem -LiteralPath $pending.Pop() -Force)){Require-Plain $item.FullName;if($item.PSIsContainer){$pending.Push($item.FullName)}else{[void]$actual.Add($item.FullName)}}}
        if(-not $seen.SetEquals($actual)){throw 'Complete payload file set differs.'}
    }
}
if($Stage -ceq 'Execute'){
    if(-not $PreparedDirectory -or $BuildDirectory){throw 'Execute requires only an exact reviewed PreparedDirectory.'}
    $prepared=[IO.Path]::GetFullPath($PreparedDirectory)
    if(-not $prepared.StartsWith((Join-Path $root 'netonly-station-invocations')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Tracked preparation required.'}
    Require-Plain $prepared
    $preparation=Get-Content -LiteralPath (Join-Path $prepared 'preparation.json') -Raw|ConvertFrom-Json
    if($preparation.status -cne 'prepared_not_executed'){throw 'Unexecuted preparation required.'}
    $BuildDirectory=$preparation.build_directory
}elseif(-not $BuildDirectory -or $PreparedDirectory){throw 'Prepare requires a tracked BuildDirectory and performs no logon or station operation.'}
$build=[IO.Path]::GetFullPath($BuildDirectory)
if(-not $build.StartsWith((Join-Path $root 'netonly-station-builds')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Tracked compilation required.'}
Require-Plain $build
$record=Get-Content -LiteralPath (Join-Path $build 'build-result.json') -Raw|ConvertFrom-Json
if($record.status -cne 'passed_compilation_only' -or $record.source_snapshot_id -notmatch '^sha256:[a-f0-9]{64}$'){throw 'Source-bound successful compilation required.'}
Verify-Files $workspace $record.source_files $false
$canonical=($record.source_files|ForEach-Object{"$($_.sha256)  $($_.bytes)  $($_.path)"}) -join "`n"
if('sha256:'+[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))) -cne $record.source_snapshot_id){throw 'Source snapshot ID differs.'}
$outputs=@($record.outputs)
if($outputs.Count -ne 1 -or $outputs[0].kind -cne 'probe' -or $outputs[0].directory -cne (Join-Path $build 'probe')){throw 'One exact probe output required.'}
$output=$outputs[0];Verify-Files $output.directory $output.files $true
if($record.source_archive.path -cne (Join-Path $build 'probe-source.zip') -or $record.source_archive.readback -cne 'passed' -or
    (Get-FileHash -LiteralPath $record.source_archive.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $record.source_archive.sha256){throw 'Actual source ZIP binding differs.'}
$executable=Join-Path $output.directory 'AutumnOS.NetOnlyStationProbe.exe';Require-Plain $executable
if($Stage -ceq 'Prepare'){
    $prepared=Join-Path $root ('netonly-station-invocations/prepared-'+[Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($prepared)
    $caseRoot=Join-Path $root 'netonly-station-runs';[void][IO.Directory]::CreateDirectory($caseRoot);Require-Plain $caseRoot
    $manifestPath=Join-Path $prepared 'input-manifest.json'
    [ordered]@{schema_version=1;source_snapshot_id=$record.source_snapshot_id;case_root=$caseRoot;
        payload_directory=$output.directory;executable='AutumnOS.NetOnlyStationProbe.exe';files=$output.files}|
        ConvertTo-Json -Depth 7|Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    [ordered]@{schema_version=1;producer='派蒙';status='prepared_not_executed';build_directory=$build;build_id=$record.build_id;
        source_snapshot_id=$record.source_snapshot_id;manifest_sha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();
        scope='One ordinary CreateProcessWithLogonW NETONLY call using synthetic local network credentials; real LSA context/provider effects possible, no authentication acceptance. Held original token must keep same user/session/IL/no privilege gain and different AuthenticationId before one fresh NULL+CWF_CREATE_ONLY station metadata/read/close and exact absence postcheck. No desktop/profile/browser/clipboard or service/policy change; no UAC.';
        create_process_with_logon='not_run_requires_fresh_explicit_authorization';station_creation='not_run';UAC_requests=0;
        authentication_acceptance='not_claimed';full_isolation='not_claimed'}|
        ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $prepared 'preparation.json') -Encoding utf8NoBOM
    Write-Host "Prepared only; no logon, child, station or clipboard action: $prepared"
    exit 0
}
$manifestPath=Join-Path $prepared 'input-manifest.json';Require-Plain $manifestPath
if($preparation.source_snapshot_id -cne $record.source_snapshot_id -or (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $preparation.manifest_sha256){throw 'Reviewed preparation differs.'}
$authorizationPath=Join-Path $prepared 'authorization.json';Require-Plain $authorizationPath
$authorization=Get-Content -LiteralPath $authorizationPath -Raw|ConvertFrom-Json
if($authorization.authorization -cne 'explicit_user_reply_allow_one_netonly_station_metadata_trial' -or
    $authorization.prepared_id -cne (Split-Path $prepared -Leaf) -or $authorization.manifest_sha256 -cne $preparation.manifest_sha256){throw 'Fresh explicit authorization for this exact new LSA-context trial is required.'}
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
if($manifest.payload_directory -cne $output.directory -or $manifest.executable -cne 'AutumnOS.NetOnlyStationProbe.exe' -or
    $manifest.case_root -cne (Join-Path $root 'netonly-station-runs')){throw 'Exact prepared execution scope differs.'}
Verify-Files $manifest.payload_directory $manifest.files $true
$executionPath=Join-Path $prepared 'execution.json'
if([IO.File]::Exists($executionPath)){throw 'This single-call preparation has already been attempted; no automatic retry.'}
$execution=[ordered]@{schema_version=1;producer='派蒙';status='launching_authorized_metadata_trial';started_utc=[DateTimeOffset]::UtcNow;
    build_id=$record.build_id;source_snapshot_id=$record.source_snapshot_id;manifest_sha256=$preparation.manifest_sha256;UAC_requests=0}
$execution|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $executionPath -Encoding utf8NoBOM
$process=$null;$started=$false
try{
    $before=@(Get-ChildItem -LiteralPath $manifest.case_root -Directory -Filter 'netonly-case-*'|Select-Object -ExpandProperty FullName)
    $start=[Diagnostics.ProcessStartInfo]::new($executable);$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.ArgumentList.Add('--execute');$start.ArgumentList.Add($manifestPath)
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    if(-not $process.Start()){throw 'NETONLY metadata controller failed to start.'}
    $started=$true;$execution.original_controller_pid=$process.Id;$process.WaitForExit()
    $execution.original_controller_exit_observed=$process.HasExited;$execution.controller_exit_code=$process.ExitCode
    $created=@(Get-ChildItem -LiteralPath $manifest.case_root -Directory -Filter 'netonly-case-*'|Where-Object{$_.FullName -notin $before})
    if($created.Count -ne 1){throw 'Exactly one retained metadata case report required.'}
    $reportPath=Join-Path $created[0].FullName 'report.json';Require-Plain $reportPath
    $execution.report_path=$reportPath;$execution.report_sha256=(Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant()
    $report=Get-Content -LiteralPath $reportPath -Raw|ConvertFrom-Json;$execution.report_status=$report.status
    if($process.ExitCode -ne 0){throw 'NETONLY metadata trial failed; retain exact stage and cleanup evidence.'}
    if($report.schema_version -ne 1 -or $report.status -cne 'completed_netonly_station_metadata_only' -or
        $report.source_snapshot_id -cne $record.source_snapshot_id -or $report.manifest_sha256 -cne $preparation.manifest_sha256 -or
        $report.report_error -or $report.failure_stage -or @($report.cleanup_errors).Count -ne 0 -or $report.create_process_with_logon_calls -ne 1 -or
        $report.logon_flags -ne 2 -or $report.create_process_result -ne $true -or $report.create_process_native_error -ne 0 -or $report.create_command_sent -ne $true -or
        $report.token_admission_passed -ne $true -or $report.original_child_image_matches -ne $true -or
        $report.pipe_peer_pid_verified -ne $true -or $report.resume_return -ne 1 -or $report.resume_native_error -ne 0 -or
        $report.original_child_exit_proven -ne $true -or $report.original_thread_exit_proven -ne $true -or $report.original_child_exit_code -ne 0 -or $report.cleanup_proven -ne $true -or
        $report.parent_station_before -cne 'WinSta0' -or $report.parent_station_after -cne 'WinSta0' -or $report.parent_station_unchanged -ne $true){throw 'Original process, token, source, protocol or cleanup proof incomplete.'}
    $parent=$report.parent_identity;$child=$report.child_identity
    if($parent.user_sid_sha256 -cne $child.user_sid_sha256 -or $parent.session_id -ne $child.session_id -or
        $parent.integrity_rid -ne 8192 -or $child.integrity_rid -ne 8192 -or $parent.elevated -ne $false -or $child.elevated -ne $false -or
        $parent.appcontainer -ne $false -or $child.appcontainer -ne $false -or $parent.authentication_id -ceq $child.authentication_id -or
        @($child.enabled_privileges|Where-Object{$_ -cnotin $parent.enabled_privileges}).Count -ne 0 -or
        @($child.present_privileges|Where-Object{$_ -cnotin $parent.present_privileges}).Count -ne 0){throw 'OS-derived session identity or no-privilege-gain evidence incomplete.'}
    $result=$report.child_result;$post=$report.station_postcheck
    if($result.kind -cne 'result' -or $result.nonce -cne $report.nonce -or $result.failure_stage -cne 'none' -or $result.native_error -ne 0 -or
        $result.create_attempted -ne $true -or $result.creation_succeeded -ne $true -or $result.exact_name_verified -ne $true -or
        $result.noninteractive_verified -ne $true -or $result.original_handle_closed -ne $true -or $result.close_native_error -ne 0 -or
        $result.original_station_unchanged -ne $true -or $result.cleanup_confirmed -ne $true -or $result.descriptor_sha256 -notmatch '^[a-f0-9]{64}$' -or
        $post.name -cne $result.station_name -or $post.handle_returned -ne $false -or $post.native_error -notin 2,3 -or $post.not_found_proven -ne $true){throw 'Fresh NULL/CWF_CREATE_ONLY station metadata and independent absence proof incomplete.'}
    if($report.UAC_requests -ne 0 -or $report.policy_or_service_changes -ne 0 -or $report.force_termination_calls -ne 0 -or
        $report.profiles_created -ne 0 -or $report.desktops_created -ne 0 -or $report.clipboard_calls -ne 0 -or $report.browser_starts -ne 0 -or
        $report.application_network_calls -ne 0 -or $report.lsa_cleanup -cne 'not_claimed' -or $report.isolation_acceptance -cne 'not_claimed_metadata_only'){throw 'Metadata-only scope differs.'}
    $execution.status='completed_netonly_station_metadata_only';$execution.authentication_acceptance='not_claimed';$execution.full_isolation='not_claimed'
}catch{$execution.status='failed';$execution.failure_type=$_.Exception.GetType().Name;throw}
finally{
    if($started -and -not $process.HasExited){$process.WaitForExit()}
    if($process){$process.Dispose()};$execution.completed_utc=[DateTimeOffset]::UtcNow
    $execution|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $executionPath -Encoding utf8NoBOM
    Write-Host "NETONLY metadata evidence retained: $prepared"
}
