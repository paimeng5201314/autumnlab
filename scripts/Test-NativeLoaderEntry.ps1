[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildDirectory)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$build=[IO.Path]::GetFullPath($BuildDirectory)
$allowed=Join-Path $workspace 'artifacts/isolation-completion-20261030/clipboard-builds'
if(-not $build.StartsWith($allowed+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Tracked native diagnostic build required.'}
$record=Get-Content -LiteralPath (Join-Path $build 'build-result.json') -Raw|ConvertFrom-Json
if($record.status -cne 'passed_compilation_only' -or -not $record.native_loader_variant){throw 'Compiled native-loader variant required.'}
$native=@($record.outputs|Where-Object kind -CEQ 'native')
if($native.Count -ne 1 -or $native[0].directory -cne (Join-Path $build 'native')){throw 'One bound native output required.'}
$executable=Join-Path $native[0].directory 'AutumnOS.NativeUser32Loader.exe'
$binding=@($native[0].files|Where-Object path -CEQ 'AutumnOS.NativeUser32Loader.exe')
if($binding.Count -ne 1 -or (Get-Item -LiteralPath $executable).Length -ne $binding[0].bytes -or
    (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant() -cne $binding[0].sha256){throw 'Native diagnostic bytes changed.'}
$verification=Join-Path $build ('verification/native-entry-'+[Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($verification)
$nonce=[Guid]::NewGuid().ToString('N')
$start=[Diagnostics.ProcessStartInfo]::new($executable)
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
$start.Environment.Clear()
$start.Environment['SystemRoot']=[Environment]::GetFolderPath([Environment+SpecialFolder]::Windows)
$start.Environment['AUTUMN_PROBE_NONCE']=$nonce;$start.Environment['AUTUMN_PROBE_SIDE']='A'
# This is a deliberate wrong-context negative control. It creates NO station
# or desktop. The same actual native entry must reject the unmatched names.
$start.Environment['AUTUMN_PROBE_STATION']='AutumnOS_ClipboardProbe_'+$nonce
$start.Environment['AUTUMN_PROBE_DESKTOP']='AutumnOS_ClipboardDesktop_'+$nonce
$result=[ordered]@{schema_version=1;producer='派蒙';kind='ordinary_native_entry_and_wrong_context_rejection_only';
    status='not_run';source_snapshot_id=$record.source_snapshot_id;native_sha256=$binding[0].sha256;case_nonce=$nonce;
    expected_exit_code=20;expected_failure_stage='user32_binding';station_creation=0;desktop_creation=0;profile_operations=0;
    uac_requests=0;clipboard_calls='none_in_source';browser_execution='not_run';private_station_acceptance='not_claimed';full_isolation='not_claimed'}
$process=[Diagnostics.Process]::new();$process.StartInfo=$start;$started=$false
try{
    if(-not $process.Start()){throw 'Native entry control process did not start.'}
    $started=$true
    $originalPid=$process.Id
    $stdoutTask=$process.StandardOutput.ReadToEndAsync();$stderrTask=$process.StandardError.ReadToEndAsync()
    $process.WaitForExit()
    $stdout=$stdoutTask.GetAwaiter().GetResult();$stderr=$stderrTask.GetAwaiter().GetResult()
    $result.original_process_id=$originalPid;$result.original_process_exit_observed=$process.HasExited;$result.exit_code=$process.ExitCode
    $result.stderr_bytes=[Text.Encoding]::UTF8.GetByteCount($stderr)
    $result.stderr_sha256=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($stderr)))
    if([Text.Encoding]::UTF8.GetByteCount($stdout) -gt 16384 -or $result.stderr_bytes -ne 0){throw 'Unexpected native diagnostic output.'}
    $rows=@(foreach($line in ($stdout -split "`n"|Where-Object{$_ -ne ''})){
        if([Text.Encoding]::UTF8.GetByteCount($line) -ge 1024){throw 'Native output line exceeds bound.'}
        $row=$line|ConvertFrom-Json
        if($row.schema_version -ne 1 -or $row.nonce -cne $nonce -or $row.host_pid -ne $originalPid -or
            $row.isolation_acceptance -cne 'not_claimed_host_output_only'){throw 'Native output binding mismatch.'}
        $row
    })
    $result.events=$rows
    if($rows.Count -lt 5 -or $rows.Count -gt 16 -or $rows[0].detail.stage -cne 'native_entry' -or $rows[0].detail.result -cne 'entered'){throw 'Actual native entry was not observed.'}
    $loaded=@($rows|Where-Object{$_.detail.stage -ceq 'load_user32' -and $_.detail.result -ceq 'loaded'})
    $failure=@($rows|Where-Object{$_.detail.stage -ceq 'user32_binding' -and $_.detail.result -ceq 'station_name_mismatch'})
    $complete=@($rows|Where-Object kind -CEQ 'native_loader_complete')
    if($loaded.Count -ne 1 -or $failure.Count -ne 1 -or $complete.Count -ne 1 -or
        $complete[0].detail.result -cne 'failed' -or $complete[0].detail.failed_stage -cne 'user32_binding' -or
        $complete[0].detail.completed -or $process.ExitCode -ne 20){throw 'Deliberate wrong-context rejection did not match actual native evidence.'}
    $result.status='passed_entry_and_context_rejection_only'
}catch{
    $result.status='failed';$result.failure_type=$_.Exception.GetType().Name;throw
}finally{
    # Never terminate a diagnostic process or close an original live handle.
    if($started -and -not $process.HasExited){$process.WaitForExit()}
    $process.Dispose()
    $result|ConvertTo-Json -Depth 9|Set-Content -LiteralPath (Join-Path $verification 'report.json') -Encoding utf8NoBOM
    Write-Host "Native ordinary entry control report: $verification"
}
