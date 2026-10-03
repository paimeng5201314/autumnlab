# Artifact-only U06 extension. The old v1 binary is a real isolated T04 copy.
# No process is killed. If this self-started old copy wins the release/reacquire
# gap, its empty welcome window is closed normally and the current transaction
# must either confirm B or restore A using its existing product recovery code.
function Initialize-T05LegacyCopy {
    $legacySource=[IO.Path]::GetFullPath($LegacyExecutablePath)
    Assert-T05PlainPath $legacySource
    if([IO.Path]::GetFileName($legacySource) -ne 'AutumnOS.exe'){throw 'Use the real old v1 AutumnOS.exe entry.'}
    $legacySourceRoot=Split-Path $legacySource -Parent
    if(Test-Path -LiteralPath (Join-Path $legacySourceRoot 'AutumnOS.Client.exe')){throw 'This U06 scenario requires the original non-Bootstrap v1 package.'}
    $metadata=Read-T05BuildMetadata $legacySourceRoot
    if($metadata.BuildId -ne 'T04-20261002-desktop-02'){throw 'The requested recorded T04 real binary baseline does not match.'}
    $script:legacyDirectory=Join-Path $testScope '真实旧 T04 v1 隔离副本'
    New-Item -ItemType Directory -Path $legacyDirectory | Out-Null
    foreach($item in Get-ChildItem -LiteralPath $legacySourceRoot -Force){
        if($item.Name -eq 'AutumnOS_Data' -or $item.Name.StartsWith('.autumnos',[StringComparison]::OrdinalIgnoreCase)){continue}
        Assert-T05PlainPath $item.FullName
        if($item.PSIsContainer){foreach($child in Get-ChildItem -LiteralPath $item.FullName -Recurse -Force){if($child.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Legacy source contains a reparse-point child.'}}}
        Copy-Item -LiteralPath $item.FullName -Destination $legacyDirectory -Recurse
    }
    $script:legacyEntry=Join-Path $legacyDirectory 'AutumnOS.exe'
    $script:legacyProcess=$null;$script:legacyOwner=$null;$script:legacyResult=$null
    $script:legacyForcedRollback=$false
    $report['legacy_v1']=[ordered]@{
        status='not_run';source_entry=$legacySource;source_build_id=$metadata.BuildId;source_snapshot_id=$metadata.SourceSnapshotId
        source_sha256=Get-T05Hash $legacySource;isolated_entry=$legacyEntry;isolated_sha256=Get-T05Hash $legacyEntry
        launched_phase=$null;transaction_id=$null;pid=$null;start_utc_ticks=$null
        outcome=$null;exit_code=$null;forced_termination=$false;normal_close_of_owned_empty_legacy_window=$false
        maximum_live_business_instances_across_both_roots=1;physical_same_root_cross_session='not_run'
    }
    Add-SmokeCheck 'U06_original_T04_v1_bytes_copied_without_user_data' (
        $report.legacy_v1.source_sha256 -eq $report.legacy_v1.isolated_sha256 -and
        -not(Test-Path -LiteralPath (Join-Path $legacyDirectory 'AutumnOS_Data')))
}
function Start-T05LegacyAtApplying($Journal){
    $updaters=@($trackedProcesses|Where-Object {$_.Root -eq $stageDirectory -and $_.Kind -eq 'AutumnOS.Updater.exe' -and $_.ParentId -eq $scenario.a_business_pid -and -not $_.Process.HasExited})
    if($updaters.Count -ne 1 -or -not $product.Process.HasExited){throw 'Legacy competition requires the held updater and a normally exited A.'}
    $updater=$updaters[0];$updater.Process.Refresh()
    if($updater.Process.StartTime.ToUniversalTime().Ticks -ne $updater.StartTicks -or -not [string]::Equals($updater.Process.MainModule.FileName,(Join-Path $stageDirectory 'AutumnOS.Updater.exe'),[StringComparison]::OrdinalIgnoreCase)){throw 'Updater ownership changed before legacy launch.'}
    $confirm=Read-T05Json (Join-Path $stageDirectory '.autumnos-update/journal.json')
    if($confirm.transactionId -ne $Journal.transactionId -or $confirm.phase -ne 'applying'){return $false}
    $stderr=Join-Path $ReportDirectory 'legacy-v1-stderr.log';$stdout=Join-Path $ReportDirectory 'legacy-v1-stdout.log'
    $script:legacyProcess=Start-Process -FilePath $legacyEntry -WorkingDirectory $legacyDirectory -WindowStyle Normal -PassThru -RedirectStandardError $stderr -RedirectStandardOutput $stdout
    if(-not $legacyProcess){throw 'No held legacy process was returned.'}
    $null=$legacyProcess.Handle
    $script:legacyOwner=[ordered]@{pid=$legacyProcess.Id;start_ticks=$legacyProcess.StartTime.ToUniversalTime().Ticks;path=$legacyProcess.MainModule.FileName;session=$legacyProcess.SessionId}
    if(-not [string]::Equals($legacyOwner.path,$legacyEntry,[StringComparison]::OrdinalIgnoreCase) -or $legacyOwner.session -ne [Diagnostics.Process]::GetCurrentProcess().SessionId){throw 'Self-started legacy identity mismatch.'}
    $ownedProcesses.Add($legacyProcess);$report.process_ids+=$legacyProcess.Id
    $report.legacy_v1.pid=$legacyOwner.pid;$report.legacy_v1.start_utc_ticks=$legacyOwner.start_ticks
    $report.legacy_v1.launched_phase=$confirm.phase;$report.legacy_v1.transaction_id=$confirm.transactionId
    $report.legacy_v1.launch_updater_identity=[ordered]@{pid=$updater.Id;start_utc_ticks=$updater.StartTicks;path=$updater.Path}
    $report.legacy_v1.stderr=$stderr;$report.legacy_v1.stdout=$stdout;$report.legacy_v1.status='observing_real_old_protocol'
    $after=Read-T05Json (Join-Path $stageDirectory '.autumnos-update/journal.json')
    $report.legacy_v1.phase_after_process_start=$after.phase
    if($after.transactionId -ne $confirm.transactionId -or $after.phase -ne 'applying'){
        $report.legacy_v1.status='not_run_applying_interval_crossed_during_launch'
        throw 'The old entry was started across a phase boundary; do not claim it was launched while applying ownership still existed.'
    }
    Copy-Item -LiteralPath (Join-Path $stageDirectory '.autumnos-update/journal.json') -Destination (Join-Path $ReportDirectory 'legacy-launch-journal.json')
    return $true
}
function Poll-T05Legacy {
    if(-not $legacyProcess -or $script:legacyResult){return}
    $legacyProcess.Refresh()
    if($legacyProcess.HasExited){
        $report.legacy_v1.exit_code=$legacyProcess.ExitCode
        if($legacyProcess.ExitCode -notin @(0,20,21)){throw ('Old v1 entry failed outside the documented forwarding/bounded-rejection outcomes: '+$legacyProcess.ExitCode)}
        if(Test-Path -LiteralPath (Join-Path $legacyDirectory 'AutumnOS_Data')){throw 'Unclassified old-v1 business startup occurred; do not report it as a forwarding-only pass.'}
        $script:legacyResult=if($legacyProcess.ExitCode -eq 0){'forwarded_after_maintenance_without_own_data'}else{'bounded_refusal_while_maintenance_without_own_data'}
        $report.legacy_v1.outcome=$legacyResult
        Add-SmokeCheck 'U06_real_old_v1_no_second_business_or_own_data_on_forward_or_timeout' $true
        return
    }
    if($legacyProcess.StartTime.ToUniversalTime().Ticks -ne $legacyOwner.start_ticks -or -not [string]::Equals($legacyProcess.MainModule.FileName,$legacyOwner.path,[StringComparison]::OrdinalIgnoreCase)){throw 'Refusing legacy interaction: held identity changed.'}
    $legacyPrimary=@(Read-SmokeEvents (Join-Path $legacyDirectory 'AutumnOS_Data/Logs/launcher-events.jsonl')|Where-Object {$_.eventName -eq 'primary_started' -and $_.primaryPid -eq $legacyOwner.pid}).Count -gt 0
    $primaryIds=@(Read-SmokeEvents (Join-Path $dataDirectory 'Logs/launcher-events.jsonl')|Where-Object eventName -eq 'primary_started'|Select-Object -ExpandProperty primaryPid)
    $current=@($trackedProcesses|Where-Object {
        if($_.Root -ne $stageDirectory -or $_.Kind -ne 'AutumnOS.Client.exe' -or $_.Process.HasExited){return $false}
        $_.Process.Refresh();return $_.Id -in $primaryIds -or $_.Process.MainWindowHandle -ne [IntPtr]::Zero
    })
    $liveTotal=$current.Count+$(if($legacyPrimary -or $legacyProcess.MainWindowHandle -ne [IntPtr]::Zero){1}else{0})
    $report.legacy_v1.maximum_live_business_instances_across_both_roots=[Math]::Max($report.legacy_v1.maximum_live_business_instances_across_both_roots,$liveTotal)
    if($liveTotal -gt 1){throw 'Two live business ownership admissions exist across the legacy and updated roots.'}
    if($legacyProcess.MainWindowHandle -ne [IntPtr]::Zero){
        $window=[Windows.Automation.AutomationElement]::FromHandle($legacyProcess.MainWindowHandle)
        if($window.Current.ProcessId -ne $legacyOwner.pid){throw 'Legacy native window is not owned by the held process.'}
        $hello=Find-SmokeElement $window 'HelloNextButton'
        if(-not $hello -or $hello.Current.IsOffscreen){throw 'The isolated legacy copy has not reached its expected empty welcome window; no arbitrary UI is closed.'}
        $count=1+$current.Count
        $report.legacy_v1.maximum_live_business_instances_across_both_roots=[Math]::Max($report.legacy_v1.maximum_live_business_instances_across_both_roots,$count)
        if($count -gt 1){throw 'Old-v1 and new actual business windows coexist; U06 failed.'}
        $journal=Read-T05Json (Join-Path $stageDirectory '.autumnos-update/journal.json')
        $report.legacy_v1.old_primary_observed_phase=$journal.phase
        $report.legacy_v1.outcome='old_v1_won_single_business_gap_then_owned_empty_window_closed_normally'
        # This is our exact empty T04 welcome window, with no copied data and no game.
        if(-not $legacyProcess.CloseMainWindow() -or -not $legacyProcess.WaitForExit(15000)){throw 'Owned legacy welcome window did not close normally; it is left running, never killed.'}
        $report.legacy_v1.normal_close_of_owned_empty_legacy_window=$true
        $report.legacy_v1.exit_code=$legacyProcess.ExitCode
        $script:legacyResult='old_v1_owned_then_normally_closed'
        Add-SmokeCheck 'U06_old_v1_gap_winner_never_coexisted_with_a_new_business_window' ($count -eq 1)
    }
}
function Wait-T05Transaction([string]$TargetBuild,[bool]$Rollback){
    $timer=[Diagnostics.Stopwatch]::StartNew();$journalPath=Join-Path $stageDirectory '.autumnos-update/journal.json';$lastJournal=$null
    while($timer.Elapsed.TotalSeconds -lt $UpdateTimeoutSeconds){
        Observe-T05OwnedDescendants;Poll-T05Legacy
        if(Test-Path -LiteralPath $journalPath){
            $journal=Read-T05Json $journalPath;$lastJournal=$journal
            if($journal.root -ne $stageDirectory -or $journal.toBuildId -ne $TargetBuild){throw 'Legacy test journal target identity differs.'}
            if(-not $legacyProcess -and $journal.phase -eq 'applying'){$null=Start-T05LegacyAtApplying $journal}
            if(-not $legacyProcess -and $journal.phase -in @('health','committed','rolledBack','aborted')){
                $report.legacy_v1.status='not_run_applying_interval_missed';throw 'Old v1 was not launched inside applying; preserve missed interval evidence.'
            }
            if($journal.phase -in @('committed','rolledBack') -and $legacyResult){
                $next=@($trackedProcesses|Where-Object {$_.Root -eq $stageDirectory -and $_.Kind -eq 'AutumnOS.Client.exe' -and $_.Id -ne $scenario.a_business_pid -and -not $_.Process.HasExited})
                if($next.Count -eq 1){
                    $next[0].Process.Refresh()
                    if($next[0].Process.MainWindowHandle -ne [IntPtr]::Zero){
                        $script:legacyForcedRollback=$journal.phase -eq 'rolledBack'
                        if(-not $legacyForcedRollback -and ($journal.childPid -ne $next[0].Id -or $journal.childStartUtcTicks -ne $next[0].StartTicks)){throw 'Confirmed B identity is not the updater-created health child.'}
                        $scenario.transaction=$journal;Copy-Item -LiteralPath $journalPath -Destination (Join-Path $ReportDirectory ($scenario.name+'-journal.json'))
                        $report.legacy_v1.final_transaction_phase=$journal.phase
                        $report.legacy_v1.status='safe_outcome_pending_binary_data_and_game_read_assertions'
                        return Product-T05FromTrackedBusiness $next[0]
                    }
                }
            }
        }
        Start-Sleep -Milliseconds 75
    }
    throw ('Real old-v1 competition did not settle safely in time; phase: '+$lastJournal.phase)
}

