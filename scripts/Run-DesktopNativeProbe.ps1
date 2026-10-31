[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=Join-Path $workspace 'artifacts/isolation-completion-20261030'
$build=[IO.Path]::GetFullPath($BuildDirectory)
if(-not $build.StartsWith((Join-Path $root 'desktop-native-builds')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Tracked local desktop-native build required.'}
$record=Get-Content -LiteralPath (Join-Path $build 'build-result.json') -Raw|ConvertFrom-Json
if($record.status -cne 'passed_compilation_only' -or $record.source_snapshot_id -notmatch '^sha256:[0-9a-f]{64}$'){throw 'Successful source-bound compilation record required.'}
$controller=@($record.outputs|Where-Object kind -CEQ 'controller');$native=@($record.outputs|Where-Object kind -CEQ 'native')
if($controller.Count -ne 1 -or $native.Count -ne 1 -or $controller[0].directory -cne (Join-Path $build 'controller') -or $native[0].directory -cne (Join-Path $build 'native')){throw 'Exact two output directories required.'}
function Require-PlainAncestors([string]$Path){
    $full=[IO.Path]::GetFullPath($Path);$drive=[IO.Path]::GetPathRoot($full);$current=$drive
    foreach($part in $full.Substring($drive.Length).Split('\',[StringSplitOptions]::RemoveEmptyEntries)){
        $current=Join-Path $current $part
        if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Reparse point input rejected.'}
    }
}
function Verify-Bindings([string]$Directory,$Files,[bool]$ExactSet){
    Require-PlainAncestors $Directory
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($binding in $Files){
        if([IO.Path]::IsPathRooted($binding.path) -or $binding.path -match '[:\x00]' -or @($binding.path.Split([char[]]@('/','\'),[StringSplitOptions]::None)|Where-Object{$_ -in '', '.', '..' -or $_.EndsWith(' ') -or $_.EndsWith('.')}).Count){throw 'Plain relative file binding required.'}
        $file=[IO.Path]::GetFullPath((Join-Path $Directory $binding.path))
        if(-not $file.StartsWith($Directory.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or -not $seen.Add($file)){throw 'Duplicate or escaped file binding.'}
        Require-PlainAncestors $file
        $item=Get-Item -LiteralPath $file
        if($item.PSIsContainer -or $item.Length -ne $binding.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $binding.sha256){throw 'Frozen file bytes differ.'}
    }
    if($ExactSet){
        $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($Directory)
        $actual=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        while($pending.Count){
            $directoryPath=$pending.Pop();Require-PlainAncestors $directoryPath
            foreach($entry in (Get-ChildItem -LiteralPath $directoryPath -Force)){
                Require-PlainAncestors $entry.FullName
                if($entry.PSIsContainer){$pending.Push($entry.FullName)}else{[void]$actual.Add($entry.FullName)}
            }
        }
        if(-not $seen.SetEquals($actual)){throw 'Output file set differs from build record.'}
    }
}
Verify-Bindings $workspace $record.source_files $false
$canonical=($record.source_files|ForEach-Object{"$($_.sha256)  $($_.bytes)  $($_.path)"}) -join "`n"
if('sha256:'+[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))) -cne $record.source_snapshot_id){throw 'Source snapshot ID differs.'}
Verify-Bindings $controller[0].directory $controller[0].files $true
Verify-Bindings $native[0].directory $native[0].files $true
if((Get-FileHash -LiteralPath $record.source_archive.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $record.source_archive.sha256 -or $record.source_archive.readback -cne 'passed'){throw 'Actual source archive differs.'}
$caseRoot=Join-Path $root ('desktop-native-runs/invocation-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($caseRoot)
$manifestPath=Join-Path $caseRoot 'input-manifest.json'
$system32=[Environment]::GetFolderPath([Environment+SpecialFolder]::System)
$systemDlls=@(foreach($name in @('user32.dll','gdi32.dll','shell32.dll')){
    $path=Join-Path $system32 $name;$item=Get-Item -LiteralPath $path
    [ordered]@{name=$name;path=$path;bytes=$item.Length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant();file_version=$item.VersionInfo.FileVersion}
})
[ordered]@{schema_version=1;producer='派蒙';source_snapshot_id=$record.source_snapshot_id;case_root=$caseRoot;
    child_directory=$native[0].directory;child_executable='AutumnOS.NativeDesktopLoader.exe';child_files=$native[0].files;
    controller_directory=$controller[0].directory;controller_executable='AutumnOS.OwnedDesktopNativeController.exe';controller_files=$controller[0].files;
    system_dlls=$systemDlls;scope='Three fixed native DLL-load cases on fresh desktops in WinSta0; desktop masks0x87/0x200C7 only; no existing ACL changes, UAC, clipboard or browser.'}|
    ConvertTo-Json -Depth 8|Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$execution=[ordered]@{schema_version=1;producer='派蒙';status='not_run';source_snapshot_id=$record.source_snapshot_id;
    manifest_sha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();uac_requests=0;
    clipboard_operations='not_run';browser_execution='not_run';isolation_acceptance='not_claimed';started_utc=[DateTimeOffset]::UtcNow}
$locked=[Collections.Generic.List[IO.FileStream]]::new();$process=$null;$started=$false
try{
    foreach($output in @($controller[0],$native[0])){foreach($binding in $output.files){$locked.Add([IO.FileStream]::new((Join-Path $output.directory $binding.path),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read))}}
    $locked.Add([IO.FileStream]::new($manifestPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read))
    Verify-Bindings $controller[0].directory $controller[0].files $true;Verify-Bindings $native[0].directory $native[0].files $true
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $controller[0].directory 'AutumnOS.OwnedDesktopNativeController.exe'))
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.ArgumentList.Add($manifestPath)
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    if(-not $process.Start()){throw 'Desktop diagnostic controller failed to start.'}
    $started=$true;$execution.original_controller_pid=$process.Id;$execution.status='running'
    $execution|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $caseRoot 'execution.json') -Encoding utf8NoBOM
    Write-Host "Desktop-only diagnostic invocation: $caseRoot"
    $process.WaitForExit();$execution.original_controller_exit_observed=$process.HasExited;$execution.controller_exit_code=$process.ExitCode
    $reports=@(Get-ChildItem -LiteralPath $caseRoot -Directory -Filter 'desktop-native-case-*'|ForEach-Object{Join-Path $_.FullName 'report.json'})
    if($reports.Count -ne 1 -or -not [IO.File]::Exists($reports[0])){throw 'One retained diagnostic report required.'}
    $execution.report_path=$reports[0];$execution.report_sha256=(Get-FileHash -LiteralPath $reports[0] -Algorithm SHA256).Hash.ToLowerInvariant()
    $result=Get-Content -LiteralPath $reports[0] -Raw|ConvertFrom-Json
    $execution.report_status=$result.status
    if($process.ExitCode -ne 0){throw 'Desktop diagnostic failed; inspect retained stage and cleanup evidence.'}
    if($result.schema_version -ne 1 -or $result.kind -cne 'ordinary_desktop_only_native_loader_fixed_matrix' -or
        $result.status -cne 'completed_diagnostic_matrix' -or $result.stage -cne 'complete' -or $result.report_error -or $result.failure -or
        $result.source_snapshot_id -cne $record.source_snapshot_id -or $result.manifest_sha256 -cne $execution.manifest_sha256 -or
        $result.cleanup_proven -ne $true -or $result.diagnostic_execution_completed -ne $true -or $result.original_context_unchanged -ne $true -or
        $result.original_station_before -cne 'WinSta0' -or $result.original_station_after -cne 'WinSta0' -or
        $result.original_thread_desktop_before -cne $result.original_thread_desktop_after -or
        $result.clipboard_operations -cne 'not_run' -or $result.browser_execution -cne 'not_run' -or
        $result.existing_WinSta0_or_Default_ACL_changes -cne 'none' -or $result.UAC_requests -ne 0 -or $result.SwitchDesktop_calls -ne 0 -or
        $result.force_termination -cne 'none' -or $result.isolation_acceptance -cne 'not_claimed'){throw 'Final report binding, scope or exact cleanup gate failed.'}
    $fixed=@(@{label='A87';side='A';mask=135},@{label='B87';side='B';mask=135},@{label='B200C7';side='B';mask=131271})
    if(@($result.cases).Count -ne $fixed.Count){throw 'Fixed three-case native matrix incomplete.'}
    for($index=0;$index -lt $fixed.Count;$index++){
        $case=$result.cases[$index];$expected=$fixed[$index];$child=$case.child
        if($case.label -cne $expected.label -or $case.side -cne $expected.side -or $case.desktop_package_mask -ne $expected.mask -or
            $case.diagnostic_execution_completed -ne $true -or $case.cleanup_proven -ne $true -or $case.primary_failure -or
            $case.cleanup.any_failure -ne $false -or $case.desktop.cleanup.any_failure -ne $false -or
            $case.desktop.exact_descriptor_verified -ne $true -or $case.desktop.low_label_verified -ne $true -or
            $case.desktop.original_handle_closed -ne $true -or $case.desktop.post_close_not_found -ne $true -or
            $case.desktop.creation.worker_joined -ne $true -or $case.desktop.creation.worker_still_alive -ne $false -or
            @($case.desktop.freshness).Count -ne 2 -or @($case.desktop.freshness|Where-Object{$_.fresh_not_found -ne $true}).Count){throw 'Case identity, exact new desktop or cleanup evidence incomplete.'}
        if($child.diagnostic_execution_completed -ne $true -or $child.OS_identity_proven -ne $true -or
            $child.initial_suspended_identity_job_verified -ne $true -or $child.stdout_eof -ne $true -or
            $child.group_empty_proven -ne $true -or $child.retired -ne $true -or $child.terminal_received -ne $true -or $child.observation_budget_exceeded -ne $false -or
            $child.cleanup.any_failure -ne $false -or @($child.failures).Count -ne 0 -or $child.audit.observation_gap -ne $false -or
            @($child.audit.Failures).Count -ne 0 -or $child.job.total_processes -ne 1 -or $child.job.active_processes -ne 0 -or
            @($child.job.last_process_ids).Count -ne 0 -or $child.job.Empty -ne $true -or $child.job.limit_flags -ne 0 -or
            $child.job.atomic_launch_attribute_attached -ne $true -or $child.job.original_handle_closed_after_empty -ne $true -or
            $child.job.query_failure_seen -ne $false -or $child.job.membership_failure_seen -ne $false){throw 'Original child token, Job0, EOF or retirement evidence incomplete.'}
        if(@($child.original_thread_resume_attempts).Count -ne 1 -or $child.original_thread_resume_attempts[0].previous_suspend_count -ne 1 -or
            $child.original_thread_resume_attempts[0].native_error -ne 0 -or @($child.audit.Held).Count -ne 1){throw 'Original suspended child identity or resume evidence incomplete.'}
        $held=$child.audit.Held[0];$token=$held.token
        if($held.original_handle_signaled -ne $true -or $held.image_in_new_copied_trees -ne $true -or
            $held.image_basename -cne 'AutumnOS.NativeDesktopLoader.exe' -or $held.failure -or
            $token.ordinary_parent_os_query -ne $true -or $token.capability_count -ne 0){throw 'Held original native process evidence incomplete.'}
        $terminal=@($child.events|Where-Object kind -CEQ 'native_loader_complete')
        if($terminal.Count -ne 1 -or $terminal[0].detail.failed_stage -cne $child.failed_stage -or $terminal[0].detail.native_error -ne $child.native_error){throw 'Exactly one matching real native terminal required.'}
        if($case.loader_succeeded){
            if($child.loader_completed -ne $true -or $held.exit_code -ne 0 -or @($child.events).Count -ne 11 -or
                $terminal[0].detail.result -cne 'passed' -or $terminal[0].detail.failed_stage -cne 'none' -or $terminal[0].detail.native_error -ne 0){throw 'Successful native case lacks complete stages and original exit0.'}
        }else{
            if($expected.side -ceq 'A' -or $child.loader_completed -ne $false -or $held.exit_code -ne 20 -or
                $terminal[0].detail.result -cne 'failed' -or $terminal[0].detail.failed_stage -notin @('load_user32','load_gdi32','load_shell32','user32_metadata_exports','user32_binding')){throw 'Failed native case lacks an explicit DLL or GUI-binding failure and original exit20.'}
        }
        if($expected.side -ceq 'B'){
            if($token.is_appcontainer -ne 1 -or $token.integrity_rid -ne 4096 -or $token.expected_package_sid_matches -ne $true -or
                $token.package_sid_sha256 -cne $case.package_sid_sha256 -or $case.profile_created -ne $true -or
                $case.profile_delete_hresult -ne 0 -or $case.profile_path_not_found -ne $true -or $case.profile_sid_not_found -ne $true){throw 'Exact zero-capability AppContainer or profile deletion postchecks failed.'}
        }elseif($token.is_appcontainer -ne 0 -or $token.integrity_rid -ne 8192 -or $case.profile_created -ne $false){throw 'Ordinary A positive-control OS identity failed.'}
    }
    $execution.status='completed_native_diagnostic_only'
}catch{$execution.status='failed';$execution.failure_type=$_.Exception.GetType().Name;throw}
finally{
    if($started -and -not $process.HasExited){$process.WaitForExit()}
    if($process){$process.Dispose()};foreach($stream in $locked){$stream.Dispose()}
    $execution.completed_utc=[DateTimeOffset]::UtcNow
    $execution|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $caseRoot 'execution.json') -Encoding utf8NoBOM
    Write-Host "Desktop diagnostic evidence retained: $caseRoot"
}
