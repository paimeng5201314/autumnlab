# Artifact-only U10: one read-only handle on this isolated test copy's App.xbf
# denies updater write admission. No process termination and no install/setup work.
function Wait-T05Transaction([string]$TargetBuild,[bool]$Rollback){
    if(-not $Rollback){throw 'Busy-file exercise expects the original A version.'}
    $journalPath=Join-Path $stageDirectory '.autumnos-update/journal.json'
    $lockedPath=Join-Path $stageDirectory 'App.xbf';Assert-T05PlainPath $lockedPath
    $inventory=Read-T05Json (Join-Path $stageDirectory 'autumn.install.json')
    $file=@($inventory.files|Where-Object path -eq 'App.xbf')
    if($file.Count -ne 1 -or (Get-T05Hash $lockedPath) -ne $file[0].sha256){throw 'Busy-file fixture must bind the actual managed A App.xbf bytes.'}
    $report['busy_file']=[ordered]@{
        status='not_run';path=$lockedPath;initial_sha256=$file[0].sha256;handle_owner_pid=$PID
        access='Read';share='Read';process_termination=$false;program_bytes_modified_by_driver=$false
        blocked_update_code=$null;transaction_id=$null;post_restart_observation_seconds=0;handle_released=$false
    }
    $hold=[IO.FileStream]::new($lockedPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $report.busy_file.status='holding_owned_fixture_file_read_handle'
    try{
        $timer=[Diagnostics.Stopwatch]::StartNew();$lastJournal=$null
        while($timer.Elapsed.TotalSeconds -lt $UpdateTimeoutSeconds){
            Observe-T05OwnedDescendants
            if(Test-Path -LiteralPath $journalPath){
                $journal=Read-T05Json $journalPath;$lastJournal=$journal
                if($journal.root -ne $stageDirectory -or $journal.toBuildId -ne $TargetBuild){throw 'Busy-file journal identity differs.'}
                if($journal.phase -in @('applying','health','committed')){throw 'Updater crossed program replacement despite the real unshareable-write file handle.'}
                if($journal.phase -eq 'aborted'){
                    if($journal.errorCode -ne 'UPDATE_FILES_BUSY'){throw 'Transaction aborted for an unrelated cause.'}
                    if(-not $product.Process.HasExited){throw 'The original A did not complete the real process handoff.'}
                    $next=@($trackedProcesses|Where-Object {$_.Root -eq $stageDirectory -and $_.Kind -eq 'AutumnOS.Client.exe' -and $_.Id -ne $scenario.a_business_pid -and -not $_.Process.HasExited})
                    if($next.Count -eq 1){
                        $next[0].Process.Refresh()
                        if($next[0].Process.MainWindowHandle -ne [IntPtr]::Zero){
                            $candidate=Product-T05FromTrackedBusiness $next[0]
                            $diagnostic=Wait-SmokeCondition {if(Test-Path -LiteralPath (Join-Path $stageDirectory '.autumnos-update/last-error.json')){Read-T05Json (Join-Path $stageDirectory '.autumnos-update/last-error.json')}} 'fixed updater busy-file diagnostic'
                            Add-SmokeCheck 'U10_real_file_occupancy_aborted_before_replacement' ($diagnostic.code -eq 'UPDATE_APPLY_ABORTED_FILES_BUSY' -and $diagnostic.phase -eq 'occupancy' -and $diagnostic.file -eq 'App.xbf')
                            Copy-Item -LiteralPath (Join-Path $stageDirectory '.autumnos-update/last-error.json') -Destination (Join-Path $ReportDirectory 'busy-file-updater-diagnostic.json')
                            $report.busy_file.blocked_update_code=$diagnostic.code;$report.busy_file.transaction_id=$journal.transactionId
                            $observation=[Diagnostics.Stopwatch]::StartNew()
                            while($observation.Elapsed.TotalSeconds -lt $BlockedObservationSeconds){
                                Observe-T05OwnedDescendants
                                if($candidate.Process.HasExited){throw 'Automatic retry exited the recovered A while file-busy recovery hold should persist.'}
                                $current=Read-T05Json $journalPath
                                if($current.transactionId -ne $journal.transactionId -or $current.phase -ne 'aborted'){throw 'File-busy restart entered another automatic update transaction.'}
                                Start-Sleep -Milliseconds 200
                            }
                            $report.busy_file.post_restart_observation_seconds=$observation.Elapsed.TotalSeconds
                            Add-SmokeCheck 'U10_real_busy_handle_and_persisted_hold_prevent_restart_loop_beyond_countdown' ((Get-T05Hash $lockedPath) -eq $file[0].sha256 -and (Get-T05Hash (Join-Path $stageDirectory 'AutumnOS.Client.dll')) -eq $scenario.a_client_hash)
                            $scenario.transaction=$journal;Copy-Item -LiteralPath $journalPath -Destination (Join-Path $ReportDirectory ($scenario.name+'-journal.json'))
                            $report.busy_file.status='owned_handle_released_after_stable_A_observation_pending_UI_data_assertions'
                            return $candidate
                        }
                    }
                }
            }
            Start-Sleep -Milliseconds 100
        }
        throw ('Busy-file transaction did not safely reopen A; last phase: '+$lastJournal.phase)
    }finally{$hold.Dispose();$report.busy_file.handle_released=$true}
}

