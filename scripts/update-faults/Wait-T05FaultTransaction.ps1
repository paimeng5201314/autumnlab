# Maintained replacement for the isolated fault driver's wait function.
# Only Kill(false) below injects a failure, and only into a held, exact updater
# descendant of the A client this driver itself started in its isolated copy.
function Wait-T05Transaction([string]$TargetBuild,[bool]$Rollback){
    if(-not $Rollback){throw 'The fault driver must verify restoration of A.'}
    $timer=[Diagnostics.Stopwatch]::StartNew()
    $journalPath=Join-Path $stageDirectory '.autumnos-update/journal.json'
    $injected=$false;$recoveryStarted=$false;$lastJournal=$null
    $report['fault_injection']=[ordered]@{
        requested_phase=$FaultPhase;kind='TerminateProcess of exact held updater process only; not physical power loss'
        status='not_run';target_build=$TargetBuild;updater_pid=$null;updater_start_utc_ticks=$null
        transaction_id=$null;observed_phase_before_kill=$null;persisted_phase_after_kill=$null
        user_processes_killed=$false;business_processes_killed=$false;recovery_entry=$null;health_child_observed=$null
    }
    while($timer.Elapsed.TotalSeconds -lt $UpdateTimeoutSeconds){
        Observe-T05OwnedDescendants
        if(Test-Path -LiteralPath $journalPath){
            $journal=Read-T05Json $journalPath;$lastJournal=$journal
            if($journal.root -ne $stageDirectory -or $journal.toBuildId -ne $TargetBuild){throw 'Fault journal does not bind the isolated root and intended signed target.'}
            if(-not $injected -and $journal.phase -eq $FaultPhase){
                # A health-phase assertion requires the real updater-created child,
                # rather than just the earlier journal transition before Process.Start.
                if($FaultPhase -eq 'health' -and (-not $journal.childPid -or -not $journal.childStartUtcTicks)){
                    Start-Sleep -Milliseconds 25;continue
                }
                if(-not $product.Process.HasExited){throw 'Refusing injection before the original A business process has exited normally.'}
                $updaters=@($trackedProcesses|Where-Object {
                    $_.Root -eq $stageDirectory -and $_.Kind -eq 'AutumnOS.Updater.exe' -and
                    $_.ParentId -eq $scenario.a_business_pid -and -not $_.Process.HasExited
                })
                if($updaters.Count -ne 1){throw 'A unique held updater descendant of the self-started A process is required.'}
                $owner=$updaters[0];$process=$owner.Process;$process.Refresh()
                if($process.HasExited -or $process.Id -ne $owner.Id -or $process.StartTime.ToUniversalTime().Ticks -ne $owner.StartTicks -or
                    $process.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId -or
                    -not [string]::Equals($process.MainModule.FileName,(Join-Path $stageDirectory 'AutumnOS.Updater.exe'),[StringComparison]::OrdinalIgnoreCase)){
                    throw 'Refusing termination: held updater process identity/path/session changed.'
                }
                $confirm=Read-T05Json $journalPath
                if($confirm.transactionId -ne $journal.transactionId -or $confirm.phase -ne $FaultPhase){continue}
                if($FaultPhase -eq 'health'){
                    $children=@($trackedProcesses|Where-Object {$_.Root -eq $stageDirectory -and $_.Kind -eq 'AutumnOS.Client.exe' -and
                        $_.ParentId -eq $owner.Id -and $_.Id -eq $confirm.childPid -and $_.StartTicks -eq $confirm.childStartUtcTicks -and -not $_.Process.HasExited})
                    if($children.Count -ne 1){Start-Sleep -Milliseconds 25;continue}
                    $report.fault_injection.health_child_observed=[ordered]@{pid=$children[0].Id;start_utc_ticks=$children[0].StartTicks;parent_updater_pid=$owner.Id;path=$children[0].Path}
                }
                Copy-Item -LiteralPath $journalPath -Destination (Join-Path $ReportDirectory 'journal-immediately-before-updater-termination.json')
                $report.fault_injection.updater_pid=$owner.Id
                $report.fault_injection.updater_start_utc_ticks=$owner.StartTicks
                $report.fault_injection.updater_path=$owner.Path
                $report.fault_injection.parent_A_pid=$owner.ParentId
                $report.fault_injection.transaction_id=$confirm.transactionId
                $report.fault_injection.observed_phase_before_kill=$confirm.phase
                $report.fault_injection.termination_requested_utc=[DateTimeOffset]::UtcNow.ToString('o')
                # User-authorized fault injection. Never kill by name, tree or an unheld PID.
                $process.Kill($false)
                if(-not $process.WaitForExit(15000)){throw 'The exact injected updater did not terminate within its bounded wait.'}
                $injected=$true;$after=Read-T05Json $journalPath
                $report.fault_injection.persisted_phase_after_kill=$after.phase
                $report.fault_injection.updater_exit_code=$process.ExitCode
                Copy-Item -LiteralPath $journalPath -Destination (Join-Path $ReportDirectory 'journal-after-updater-termination.json')
                if($after.transactionId -ne $confirm.transactionId -or $after.phase -ne $FaultPhase){
                    $report.fault_injection.status='not_run_requested_interval_missed'
                    throw 'The requested injection interval was crossed before termination; do not count this run as that phase coverage.'
                }
                Add-SmokeCheck ('U09_exact_owned_updater_terminated_during_'+$FaultPhase) $true
                $report.fault_injection.status='injected_waiting_for_original_entry_recovery'
            }
            elseif(-not $injected -and $journal.phase -in @('committed','rolledBack','aborted','rollingBack')){
                $report.fault_injection.status='not_run_requested_interval_missed'
                throw ('Target phase was not captured; actual transaction reached '+$journal.phase+'. Preserve this missed-window evidence.')
            }
            if($injected -and -not $recoveryStarted){
                # A pre-health B may have been created before its updater died. Broken IPC
                # must make it exit by its own normal startup failure path. Never kill it.
                $alive=@($trackedProcesses|Where-Object {$_.Root -eq $stageDirectory -and $_.Kind -eq 'AutumnOS.Client.exe' -and -not $_.Process.HasExited})
                if($alive.Count){Start-Sleep -Milliseconds 100;continue}
                if($journal.childPid -and $journal.childStartUtcTicks){
                    $recorded=$null
                    try{$recorded=[Diagnostics.Process]::GetProcessById([int]$journal.childPid);$null=$recorded.Handle}catch [ArgumentException]{}
                    if($recorded){
                        try{
                            if(-not $recorded.HasExited -and $recorded.StartTime.ToUniversalTime().Ticks -eq [long]$journal.childStartUtcTicks){
                                if(-not [string]::Equals($recorded.MainModule.FileName,(Join-Path $stageDirectory 'AutumnOS.Client.exe'),[StringComparison]::OrdinalIgnoreCase)){throw 'Recorded health child path differs; no process is touched.'}
                                Start-Sleep -Milliseconds 100;continue
                            }
                        }finally{$recorded.Dispose()}
                    }
                }
                $entry=Start-Process -FilePath $scenario.shortcut -WindowStyle Normal -PassThru
                if(-not $entry){throw 'Original stable shortcut failed to return a held entry process.'}
                $null=$entry.Handle;$ownedEntryProcesses.Add($entry);$report.bootstrap_process_ids+=$entry.Id
                Register-T05Process $entry 'AutumnOS.exe'
                $report.fault_injection.recovery_entry=[ordered]@{pid=$entry.Id;start_utc_ticks=$entry.StartTime.ToUniversalTime().Ticks;path=$entry.MainModule.FileName;shortcut=$scenario.shortcut}
                $recoveryStarted=$true
            }
            if($injected -and $recoveryStarted -and $journal.phase -eq 'rolledBack'){
                $next=@($trackedProcesses|Where-Object {$_.Root -eq $stageDirectory -and $_.Kind -eq 'AutumnOS.Client.exe' -and $_.Id -ne $scenario.a_business_pid -and -not $_.Process.HasExited})
                if($next.Count -gt 1){throw 'Multiple business instances appeared during original-entry recovery.'}
                if($next.Count -eq 1){
                    $next[0].Process.Refresh()
                    if($next[0].Process.MainWindowHandle -ne [IntPtr]::Zero){
                        $scenario.transaction=$journal
                        Copy-Item -LiteralPath $journalPath -Destination (Join-Path $ReportDirectory ($scenario.name+'-journal.json'))
                        Add-SmokeCheck ('U09_'+$FaultPhase+'_original_entry_recovered_without_manual_file_repair') ($journal.errorCode -eq 'UPDATE_INTERRUPTED')
                        $report.fault_injection.status='recovered_pending_real_A_and_game_save_assertions'
                        return Product-T05FromTrackedBusiness $next[0]
                    }
                }
            }
        }
        if(-not $product.Process.HasExited){
            $wait=Find-T05 'UpdateWaitingReason'
            if($wait -and $wait.Current.Name -match 'UPDATE_\w+.*更新尚未提交'){throw $wait.Current.Name}
        }
        Start-Sleep -Milliseconds 75
    }
    throw ('Fault recovery did not complete within budget; last phase: '+$lastJournal.phase)
}

