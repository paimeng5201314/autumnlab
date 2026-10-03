#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [ValidateRange(15,90)][int]$WindowTimeoutSeconds=45,
    [ValidateRange(10,15)][int]$StateObservationSeconds=12
)
# Read docs/t06-performance.md before exclusive scheduling. No cache clearing, forced kill or GPU claim.
$ErrorActionPreference='Stop';$projectRoot=Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/Smoke-NativeAppInput.ps1"
. "$PSScriptRoot/T06-NativeHelpers.ps1"
$ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath);$ReportDirectory=[IO.Path]::GetFullPath($ReportDirectory)
if(Test-Path -LiteralPath $ReportDirectory){throw 'Use a new report directory; preserve previous performance evidence.'}
$smokeId='T06-performance-'+[Guid]::NewGuid().ToString('N')
$testScope=Join-Path $projectRoot "artifacts/smoke/$smokeId";$stageDirectory=Join-Path $testScope '性能 中文隔离目录'
New-Item -ItemType Directory -Path $ReportDirectory,$stageDirectory|Out-Null
$reportPath=Join-Path $ReportDirectory 'performance.json'
$checks=[Collections.Generic.List[object]]::new();$screenshots=[Collections.Generic.List[object]]::new()
$inputActions=[Collections.Generic.List[object]]::new();$windowGeometry=[Collections.Generic.List[object]]::new()
$ownedProcesses=[Collections.Generic.List[Diagnostics.Process]]::new();$ownedEntryProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$trackedProcesses=[Collections.Generic.List[object]]::new();$resourceProcesses=[Collections.Generic.List[object]]::new()
$startupResults=[Collections.Generic.List[object]]::new();$responseResults=[Collections.Generic.List[object]]::new();$resourceResults=[Collections.Generic.List[object]]::new()
$script:initialPointerPosition=$null;$script:product=$null
$dataDirectory=Join-Path $stageDirectory 'AutumnOS_Data';$runtimeLog=Join-Path $dataDirectory 'Logs/runtime-events.jsonl'
$report=[ordered]@{
    schema_version=1;task_id='T06';checkpoint='performance';status='failed';smoke_id=$smokeId
    started_utc=[DateTimeOffset]::UtcNow.ToString('o');source_executable=$ExecutablePath
    tested_executable=(Join-Path $stageDirectory 'AutumnOS.exe');test_scope=$testScope
    driver_sha256=(Get-FileHash -LiteralPath $PSCommandPath).Hash.ToLowerInvariant()
    plan_sha256=(Get-FileHash -LiteralPath (Join-Path $projectRoot 'docs/t06-performance.md')).Hash.ToLowerInvariant()
    targets=[ordered]@{first_start_seconds=10;restart_seconds=5;primary_response_seconds=2;background_cpu_percent_of_machine=5;tree_peak_working_set_mib=600;basis='predeclared engineering experiment; not producer promise'}
    methodology=[ordered]@{startup_poll_ms=150;resource_poll_ms=500;startup_includes_driver_overhead=$true;response_includes_driver_overhead=$true
        cache_state='ordinary OS cache; not cleared; not a cold-start claim';memory='sum of observed process working sets; shared pages may count more than once'
        cpu='sum process CPU deltas / wall seconds / logical CPU count';short_lived_descendants='may be missed between 500ms polls'
        physical_cold_start='not_run';gpu='not_run';frame_time='not_run';fps60='not_run';physical_touch='not_run';windows10='not_run';low_end_machine='not_run'}
    startups=$startupResults;responses=$responseResults;resources=$resourceResults;checks=$checks
    screenshots=$screenshots;input_actions=$inputActions;window_geometry=$windowGeometry
    process_ids=@();bootstrap_process_ids=@();remaining_processes=@();cleanup='not_run'
}
$helperAst=Import-T06Functions (Join-Path $PSScriptRoot 'Test-DesktopInteractionSmoke.ps1')
$script:t05LegacyFocus=(Get-Item Function:Focus-SmokeProduct).ScriptBlock
$null=Import-T06Functions (Join-Path $PSScriptRoot 'Test-T05UpdateSmoke.ps1') -Names @(
    'Assert-T05OwnedWindow','Get-T05WindowGeometry','Focus-SmokeProduct','Initialize-T05FocusNative',
    'Assert-T05PlainPath','Invoke-T05SharingRead','Get-T05Hash','Read-T05BuildMetadata','Register-T05Process')

function Read-SmokeEvents([string]$Path){
    if(-not(Test-Path -LiteralPath $Path)){return @()}
    $snapshot=Read-T06Snapshot $Path
    $text=[Text.Encoding]::UTF8.GetString($snapshot.Bytes)
    # Log entries are newline-committed. A concurrently appended final fragment is observed next poll.
    $last=$text.LastIndexOf("`n");if($last -lt 0){return @()}
    return @($text.Substring(0,$last).Split("`n")|Where-Object {$_.Trim()}|ForEach-Object {$_|ConvertFrom-Json})
}
function Measure-T06Response([string]$Name,[scriptblock]$Action,[scriptblock]$Ready){
    $watch=[Diagnostics.Stopwatch]::StartNew();& $Action
    $null=Wait-SmokeCondition $Ready ('observable interactive response '+$Name) 30
    $seconds=$watch.Elapsed.TotalSeconds
    $responseResults.Add([ordered]@{name=$Name;seconds=$seconds;poll_interval_ms=150;target_seconds=2;target_met=($seconds -le 2)})
}
function Observe-T06ResourceTree {
    foreach($seed in @($trackedProcesses)){
        if($seed.Process.HasExited){continue}
        if(-not @($resourceProcesses|Where-Object {$_.Id -eq $seed.Id -and $_.StartTicks -eq $seed.StartTicks}).Count){
            $resourceProcesses.Add([pscustomobject]@{Process=$seed.Process;Id=$seed.Id;StartTicks=$seed.StartTicks;ParentId=$seed.ParentId;Path=$seed.Path;OwnHandle=$false;LastCpu=$null})
        }
    }
    for($depth=0;$depth -lt 5;$depth++){
        $added=$false
        foreach($parent in @($resourceProcesses)){
            $end=[long]::MaxValue;if($parent.Process.HasExited){$end=$parent.Process.ExitTime.ToUniversalTime().Ticks}
            foreach($item in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($parent.Id)")){
                if(-not $item.CreationDate -or -not $item.ExecutablePath){continue}
                $ticks=$item.CreationDate.ToUniversalTime().Ticks
                if($ticks -lt $parent.StartTicks -or $ticks -gt $end -or @($resourceProcesses|Where-Object {$_.Id -eq $item.ProcessId -and [Math]::Abs($_.StartTicks-$ticks) -lt 10000}).Count){continue}
                $child=$null
                try{
                    $child=[Diagnostics.Process]::GetProcessById([int]$item.ProcessId);$null=$child.Handle
                    if($child.HasExited){$child.Dispose();continue}
                    $created=$child.StartTime.ToUniversalTime().Ticks
                    if([Math]::Abs($created-$ticks) -gt 10000 -or $created -lt $parent.StartTicks -or $created -gt $end -or
                        $child.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId -or
                        -not [string]::Equals($child.MainModule.FileName,$item.ExecutablePath,[StringComparison]::OrdinalIgnoreCase)){throw 'Observed resource descendant identity mismatch.'}
                    $resourceProcesses.Add([pscustomobject]@{Process=$child;Id=$child.Id;StartTicks=$created;ParentId=$parent.Id;Path=$item.ExecutablePath;OwnHandle=$true;LastCpu=$null});$added=$true
                }catch [ArgumentException]{if($child){$child.Dispose()}}
            }
        }
        if(-not $added){break}
    }
}
function Measure-T06ResourceState([string]$State,[string]$InstanceId){
    Observe-T06ResourceTree
    foreach($record in $resourceProcesses){try{$record.LastCpu=$record.Process.TotalProcessorTime.TotalSeconds}catch{$record.LastCpu=$null}}
    $rows=[Collections.Generic.List[object]]::new();$watch=[Diagnostics.Stopwatch]::StartNew();$cpuSeconds=0.0;$cpuErrors=0
    $windowStart=[DateTime]::UtcNow.Ticks
    while($watch.Elapsed.TotalSeconds -lt $StateObservationSeconds){
        Start-Sleep -Milliseconds 500;Observe-T06ResourceTree
        $events=@(Read-SmokeEvents $runtimeLog|Where-Object instanceId -eq $InstanceId)
        if(-not $events.Count -or $events[-1].state -ne $State -or ($events[-1].blocksMaintenance -ne ($State -ne 'Closed'))){throw 'Performance state changed or resource-release assertion failed during sampling.'}
        $working=0L;$private=0L;$live=[Collections.Generic.List[object]]::new()
        foreach($record in $resourceProcesses){
            $process=$record.Process
            try{
                $cpu=$process.TotalProcessorTime.TotalSeconds
                if($null -ne $record.LastCpu){$cpuSeconds+=[Math]::Max(0,$cpu-$record.LastCpu)}
                elseif($record.StartTicks -ge $windowStart){$cpuSeconds+=$cpu}
                $record.LastCpu=$cpu
                if($process.HasExited){continue}
                $process.Refresh();$working+=$process.WorkingSet64;$private+=$process.PrivateMemorySize64
                $live.Add([ordered]@{pid=$record.Id;start_ticks=$record.StartTicks;working_set_bytes=$process.WorkingSet64;private_bytes=$process.PrivateMemorySize64})
            }catch [InvalidOperationException]{if(-not $process.HasExited){throw};$cpuErrors++}
        }
        $rows.Add([ordered]@{elapsed_seconds=$watch.Elapsed.TotalSeconds;tree_working_set_bytes=$working;tree_private_bytes=$private;processes=$live.ToArray()})
    }
    $elapsed=$watch.Elapsed.TotalSeconds;$cpuPercent=100*$cpuSeconds/$elapsed/[Environment]::ProcessorCount
    $peak=($rows|ForEach-Object {$_.tree_working_set_bytes}|Measure-Object -Maximum).Maximum/1MB
    $resourceResults.Add([ordered]@{state=$State;instance_id=$InstanceId;seconds=$elapsed;samples=$rows.ToArray();cpu_seconds=$cpuSeconds
        cpu_percent_of_machine=$cpuPercent;cpu_read_errors=$cpuErrors;peak_tree_working_set_mib=$peak
        memory_target_met=($peak -le 600);background_cpu_target_met=$(if($State -eq 'Background'){$cpuPercent -le 5 -and $cpuErrors -eq 0}else{$null})})
}

try{
    if(-not $IsWindows){throw 'Interactive Windows required.'}
    if(@(Get-Process -Name AutumnOS,AutumnOS.Client,AutumnOS.Updater -ErrorAction SilentlyContinue).Count){throw 'An existing product process is present; no user instance is touched.'}
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    Add-SmokeCheck 'standard_user' (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
    Assert-T05PlainPath $ExecutablePath;Assert-T05PlainPath $stageDirectory
    if([IO.Path]::GetFileName($ExecutablePath) -ne 'AutumnOS.exe'){throw 'Use the final delivered stable entry.'}
    $os=Get-CimInstance Win32_OperatingSystem;$cpu=Get-CimInstance Win32_Processor;$machine=Get-CimInstance Win32_ComputerSystem
    $report.machine=[ordered]@{os=$os.Caption;version=$os.Version;cpu=@($cpu|ForEach-Object Name);physical_cores=($cpu|Measure-Object NumberOfCores -Sum).Sum
        logical_processors=[Environment]::ProcessorCount;physical_memory_bytes=$machine.TotalPhysicalMemory}
    foreach($item in Get-ChildItem -LiteralPath (Split-Path $ExecutablePath -Parent) -Force){
        if($item.Name -in @('AutumnOS_Data','.autumnos-update')){continue}
        Assert-T05PlainPath $item.FullName
        if($item.PSIsContainer){foreach($child in Get-ChildItem -LiteralPath $item.FullName -Recurse -Force){Assert-T05PlainPath $child.FullName}}
        Copy-Item -LiteralPath $item.FullName -Destination $stageDirectory -Recurse
    }
    Add-SmokeCheck 'copy_has_no_prior_user_data' (-not(Test-Path -LiteralPath $dataDirectory))
    $receipt=Read-T05Json (Join-Path $stageDirectory 'autumn.install.json');$metadata=Read-T05BuildMetadata $stageDirectory
    Add-SmokeCheck 'delivered_identity_matches_receipt' ($receipt.buildId -eq $metadata.BuildId -and $metadata.SourceSnapshotId -match '^sha256:[0-9a-f]{64}$')
    foreach($file in $receipt.files){if((Get-T05Hash (Join-Path $stageDirectory $file.path)) -ne $file.sha256){throw 'Final delivery copy differs from its receipt.'}}
    $report.build_id=$metadata.BuildId;$report.source_snapshot_id=$metadata.SourceSnapshotId;$report.verified_program_file_count=$receipt.files.Count
    $report.stable_exe_sha256=Get-T05Hash $report.tested_executable
    $desktopRuntime=Join-Path $projectRoot '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')){Add-Type -Path (Join-Path $desktopRuntime $name)}
    Initialize-T05FocusNative
    $nativeStrings=@($helperAst.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('namespace AutumnDesktopInteractionSmoke')},$true))
    if($nativeStrings.Count -ne 1){throw 'Maintained native declaration missing.'}
    if(-not('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){Add-Type -TypeDefinition $nativeStrings[0].Value}
    Add-Type -TypeDefinition 'using System; using System.Runtime.InteropServices; namespace AutumnT06Performance { public static class Dpi { [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd); } }'
    for($iteration=0;$iteration -le 3;$iteration++){
        $timer=[Diagnostics.Stopwatch]::StartNew();$script:product=Start-SmokeProduct $testScope
        Register-T05Process $product.EntryProcess 'AutumnOS.exe';Register-T05Process $product.Process 'AutumnOS.Client.exe' $product.EntryProcess.Id
        $id=if($iteration -eq 0){'HelloNextButton'}else{'SettingsButton'}
        $null=Wait-SmokeCondition {$element=Find-VisibleSmokeElement $product.Element $id;if($element -and $element.Current.IsEnabled){return $true}} 'visible enabled startup action'
        $seconds=$timer.Elapsed.TotalSeconds;$target=if($iteration -eq 0){10}else{5}
        $startupResults.Add([ordered]@{iteration=$iteration;kind=$(if($iteration -eq 0){'first_run'}else{'restart'});seconds=$seconds;target_seconds=$target;target_met=($seconds -le $target)
            pid=$product.Process.Id;start_utc_ticks=$product.Process.StartTime.ToUniversalTime().Ticks;hwnd=$product.Handle.ToInt64();dpi=[AutumnT06Performance.Dpi]::GetDpiForWindow($product.Handle);poll_interval_ms=150})
        $null=Get-T05WindowGeometry $product ('startup-'+$iteration)
        if($iteration -eq 0){Invoke-SmokeButton $product.Element 'HelloNextButton';Invoke-SmokeButton $product.Element 'BrandNextButton';Invoke-SmokeButton $product.Element 'PrepareDesktopButton';Wait-SmokeDesktop $product}
        if($iteration -lt 3){Close-SmokeProduct $product}
    }
    $restarts=@($startupResults|Where-Object kind -eq 'restart'|ForEach-Object seconds|Sort-Object)
    $report.restart_summary=[ordered]@{count=$restarts.Count;median_seconds=$restarts[1];maximum_seconds=$restarts[-1]}
    Measure-T06Response 'open_settings' {Invoke-SmokePointer $product 'SettingsButton' 'single_click'} {Find-VisibleSmokeElement $product.Element 'SettingsHomeButton'}
    Measure-T06Response 'settings_return_desktop' {Invoke-SmokeButton $product.Element 'SettingsHomeButton'} {Find-VisibleSmokeElement $product.Element 'SampleButton'}
    $baseline=@(Read-SmokeEvents $runtimeLog).Count
    Measure-T06Response 'launch_internal_game' {Invoke-SmokePointer $product 'SampleButton' 'single_click'} {Find-SmokeNamedElement $product.Element '玩家昵称' ([Windows.Automation.ControlType]::Edit)}
    $foreground=Wait-SmokeNewForeground $runtimeLog $baseline ''; $instance=[string]$foreground.instanceId;$report.game_instance=$instance
    $null=Save-SmokeScreenshot $product 'foreground-game' -IncludeOverlays
    Measure-T06ResourceState 'Foreground' $instance
    Measure-T06Response 'background_to_desktop' {Invoke-SmokeButton $product.Element 'BackgroundButton'} {Find-VisibleSmokeElement $product.Element 'SampleButton'}
    $null=Wait-SmokeCondition {@(Read-SmokeEvents $runtimeLog|Where-Object instanceId -eq $instance)[-1].state -eq 'Background'} 'same game background'
    $null=Save-SmokeScreenshot $product 'background-desktop' -IncludeOverlays
    Measure-T06ResourceState 'Background' $instance
    $baseline=@(Read-SmokeEvents $runtimeLog).Count
    Measure-T06Response 'continue_same_game' {Invoke-SmokePointer $product 'SampleButton' 'single_click'} {Find-SmokeNamedElement $product.Element '玩家昵称' ([Windows.Automation.ControlType]::Edit)}
    $next=Wait-SmokeNewForeground $runtimeLog $baseline $instance
    Add-SmokeCheck 'foreground_background_continue_keeps_same_instance' ($next.instanceId -eq $instance)
    # The user's explicit close confirmation is included in the response measurement.
    Measure-T06Response 'end_game_and_release_resources' {
        Invoke-SmokeButton $product.Element 'CloseGameButton';Invoke-SmokeNamedButton $product.Element '确认结束'
    } {
        $events=@(Read-SmokeEvents $runtimeLog|Where-Object instanceId -eq $instance)
        (Find-VisibleSmokeElement $product.Element 'SampleButton') -and $events.Count -gt 0 -and $events[-1].state -eq 'Closed' -and -not $events[-1].blocksMaintenance
    }
    Measure-T06ResourceState 'Closed' $instance
    $null=Save-SmokeScreenshot $product 'after-game-release' -IncludeOverlays
    $report.target_result=if(@($startupResults|Where-Object {-not $_.target_met}).Count -or @($responseResults|Where-Object {-not $_.target_met}).Count -or
        @($resourceResults|Where-Object {-not $_.memory_target_met -or ($_.state -eq 'Background' -and -not $_.background_cpu_target_met)}).Count){'missed'}else{'met'}
    Close-SmokeProduct $product;$report.status='completed'
}catch{
    $report.failure_type=$_.Exception.GetType().FullName;$report.failure=$_.Exception.Message;$report.failure_stack=$_.ScriptStackTrace
    Write-Warning ('T06 performance: '+$report.failure)
    if($product -and -not $product.Process.HasExited){try{$null=Save-SmokeScreenshot $product 'failure' -IncludeOverlays}catch{}}
}finally{
    $clean=$true
    foreach($process in $ownedProcesses){try{if(-not $process.HasExited){$process.Refresh();if($process.MainWindowHandle -eq [IntPtr]::Zero -or -not $process.CloseMainWindow() -or -not $process.WaitForExit(10000)){$clean=$false;$report.remaining_processes+=@([ordered]@{pid=$process.Id;action='left_running_no_forced_termination'})}}}catch{$clean=$false}}
    foreach($entry in $ownedEntryProcesses){try{if(-not $entry.WaitForExit(10000)){$clean=$false;$report.remaining_processes+=@([ordered]@{pid=$entry.Id;action='bootstrap_left_running'})}}catch{$clean=$false}}
    $report.resource_processes=@($resourceProcesses|ForEach-Object {[ordered]@{pid=$_.Id;start_utc_ticks=$_.StartTicks;parent_pid=$_.ParentId;path=$_.Path}})
    $report.cleanup=if($clean){'passed'}else{'failed'};if(-not $clean){$report.status='failed'}
    Restore-AutumnNativePointer
    if($script:initialPointerPosition -and ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){$null=[AutumnDesktopInteractionSmoke.NativeWindows]::SetCursorPos($script:initialPointerPosition.X,$script:initialPointerPosition.Y)}
    $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o');$report.check_count=$checks.Count;$report.passed_checks=@($checks|Where-Object status -eq 'passed').Count
    $report|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $reportPath -Encoding utf8
    foreach($record in $resourceProcesses|Where-Object OwnHandle){$record.Process.Dispose()}
    foreach($process in $ownedProcesses){$process.Dispose()};foreach($entry in $ownedEntryProcesses){$entry.Dispose()}
    Write-Host "REPORT $reportPath"
}
if($report.status -ne 'completed'){throw "T06 performance execution failed; preserved report: $reportPath"}
