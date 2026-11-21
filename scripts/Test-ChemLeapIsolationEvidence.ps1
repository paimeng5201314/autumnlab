#requires -Version 7.4
<#
Read only the build's exact, dedicated acceptance installation. Use CaptureAfterSave after a
guest saves a played game, before launching another instance. Verify using the checkpoint
ID after closing that instance, opening a different instance, and reading the saved slot.
Both modes write a new report under artifacts/builds/<BuildId>. No save contents are exported.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^(?:v[0-9]+\.[0-9]+\.[0-9]+-[0-9]{8}|T06-[A-Za-z0-9_.-]{1,85})$')][string]$BuildId,
    [ValidateSet('CaptureAfterSave','Verify')][string]$Mode='Verify',
    [ValidatePattern('^[a-f0-9]{32}$')][string]$CheckpointId
)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$evidence=Join-Path $workspace "artifacts/builds/$BuildId"
$appId='cn.labchronicles.chemleap'
$packageSha='e00ecf58d1c4a5d1190163cd02a13199135809ad0a782d5b830274e9642da0a2'
$utf8=[Text.UTF8Encoding]::new($false,$true)
$checks=[ordered]@{first_frame='not_run';saved_on_disk_with_native_receipt='not_run';background_and_resume='not_run';
    normal_group_exit_and_leases='not_run';new_instance_sdk_read_original_value='not_run';original_save_bytes_preserved='not_run'}
$runId=[Guid]::NewGuid().ToString('N')
$destination=Join-Path $evidence ('chemleap-evidence-'+$runId)
$report=[ordered]@{schema_version=1;build_id=$BuildId;mode=$Mode;checkpoint_id=$null;checks=$checks;status='not_run';
    app_id=$appId;package_sha256=$packageSha;native_trial_started=$false;complete_isolation=$false}
function Plain([string]$Path){
    $full=[IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith($workspace+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'EVIDENCE_OUTSIDE_WORKSPACE'}
    for($part=$full;$part;$part=[IO.Path]::GetDirectoryName($part)){
        if ((Test-Path -LiteralPath $part) -and ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'EVIDENCE_REDIRECTED_PATH'}
    }
    return $full
}
function Bytes([string]$Path,[int]$Limit){
    $file=[IO.File]::Open((Plain $Path),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($file.Length -lt 1 -or $file.Length -gt $Limit){throw 'EVIDENCE_BYTE_LIMIT'}
        $buffer=[byte[]]::new([int]$file.Length);$file.ReadExactly($buffer);return ,$buffer
    }finally{$file.Dispose()}
}
function Digest([byte[]]$Buffer){return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Buffer)).ToLowerInvariant()}
function JsonRecord([string]$Path,[int]$Limit=1048576){return ($utf8.GetString((Bytes $Path $Limit))|ConvertFrom-Json -AsHashtable -Depth 70)}
function SaveNew([string]$Name,$Object){
    $p=Plain (Join-Path $destination $Name)
    $out=[IO.File]::Open($p,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try{$bytes=$utf8.GetBytes(($Object|ConvertTo-Json -Depth 12));$out.Write($bytes);$out.Flush($true)}finally{$out.Dispose()}
}
function Test-OrderedEvents($Events,[string[]]$Names){
    $next=0
    foreach($event in $Events){if($event.eventName -ceq $Names[$next]){$next++;if($next -eq $Names.Count){return $true}}}
    return $false
}
function ExactSave {
    # StorageScope.SourceKey is derived from the host-created application identity/binding.
    $source='github:1414175562'
    $bindingHash=Digest ($utf8.GetBytes(("autumnos.app-binding.v1","$appId",'1414175562','',"$source" -join [char]0)))
    $sourceJson=([ordered]@{appId=$appId;repositoryId=1414175562;signingKey=$null;source=$bindingHash}|ConvertTo-Json -Compress)
    $key=Digest ($utf8.GetBytes($sourceJson))
    $path=Plain (Join-Path $data "Saves/$appId/$key/guest/game.json")
    if(!(Test-Path -LiteralPath $path -PathType Leaf)){return $null}
    $bytes=Bytes $path 131072
    $doc=[Text.Json.JsonDocument]::Parse($utf8.GetString($bytes))
    try{
        $slot=$doc.RootElement
        if($slot.GetProperty('schemaVersion').GetInt32() -ne 2 -or $slot.GetProperty('appId').GetString() -cne $appId -or
            $slot.GetProperty('sourceKey').GetString() -cne $key -or $slot.GetProperty('slot').GetString() -cne 'game' -or
            $slot.GetProperty('accountKey').GetString() -cne 'guest' -or $slot.GetProperty('formatVersion').GetInt32() -ne 1){throw 'SAVE_IDENTITY_MISMATCH'}
        $value=$slot.GetProperty('value')
        $canonical=[Text.Json.JsonSerializer]::SerializeToUtf8Bytes($value,[Text.Json.JsonElement],[Text.Json.JsonSerializerOptions]$null)
        $content=Digest $canonical
        if($content -cne $slot.GetProperty('contentHash').GetString() -or $value.GetProperty('score').GetInt32() -le 0){throw 'PLAYED_GUEST_SAVE_REQUIRED'}
        return [ordered]@{file_sha256=(Digest $bytes);content_sha256=$content;bytes=$bytes.Length;revision=$slot.GetProperty('revision').GetInt64()}
    }finally{$doc.Dispose()}
}
function ClosedGroup($Events){
    $e=@($Events|Where-Object eventName -eq 'closed')|Select-Object -Last 1
    if(!$e -or $e.state -cne 'Closed' -or $e.blocksMaintenance -ne $false -or $e.browserCleanup.holdsRuntimeLease -ne $false -or
        $e.browserCleanup.holdsMaintenanceLease -ne $false -or !$e.nativeHost.ResourcesRetired -or !$e.nativeHost.OriginalProcessExited -or
        !$e.nativeHost.OriginalThreadExited -or !$e.nativeHost.OriginalQueryJobBound -or !$e.nativeHost.OriginalJobEmpty -or
        !$e.nativeHost.AllObservedOriginalProcessHandlesSignaled -or !$e.nativeHost.OriginalProcessId -or !$e.nativeHost.OriginalCreationFileTime){return $null}
    if(@($Events|Where-Object {$_.eventName -match 'failed|crash|disconnected|native_sdk_failed' -or $_.nativeHost.FailureCode}).Count){return $false}
    # Independent current OS lookup only supplements the recorded original-handle/Job observations.
    try{$proc=[Diagnostics.Process]::GetProcessById([int]$e.nativeHost.OriginalProcessId)}
    catch [ArgumentException]{return $true}
    try{return $proc.StartTime.ToUniversalTime().ToFileTimeUtc() -ne [int64]$e.nativeHost.OriginalCreationFileTime}finally{$proc.Dispose()}
}
$null=Plain $evidence
if(!(Test-Path -LiteralPath $evidence -PathType Container)){throw 'TRACKED_BUILD_REQUIRED'}
$null=New-Item -ItemType Directory -Path (Plain $destination)
try {
    $build=JsonRecord (Join-Path $evidence 'build-result.json')
    $prepared=JsonRecord (Join-Path $evidence 'package-prepared.json')
    if($build.build_id -cne $BuildId -or $prepared.build_id -cne $BuildId -or $build.status -cne 'passed' -or
        $prepared.status -cne 'prepared' -or $build.test_build -or $prepared.test_build -or
        $prepared.source_snapshot_id -cne $build.source_snapshot_id -or $build.source_snapshot_id -notmatch '^sha256:[0-9a-f]{64}$' -or
        $build.version -notmatch '^[A-Za-z0-9.+-]{1,90}$'){throw 'TRACKED_PREPARED_BUILD_REQUIRED'}
    $root=Plain (Join-Path $workspace ("artifacts/packages/$BuildId/AutumnOS-"+$build.version+'-win-x64-development'))
    if($build.isolation_acceptance_root -ne $root -or $prepared.executable -ne (Join-Path $root 'AutumnOS.exe')){throw 'DEDICATED_ACCEPTANCE_INSTALLATION_REQUIRED'}
    $report.source_snapshot_id=$build.source_snapshot_id
    $os=Get-CimInstance Win32_OperatingSystem
    $report.os_build=[int]$os.BuildNumber
    $report.os_product_type=[int]$os.ProductType
    $report.installation_type=Get-ItemPropertyValue 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -Name InstallationType -ErrorAction SilentlyContinue
    if(!(& (Join-Path $PSScriptRoot 'Test-NativeDesktopTarget.ps1') -ProductType $os.ProductType -BuildNumber $os.BuildNumber -InstallationType $report.installation_type)){$report.reason='TARGET_DESKTOP_EVIDENCE_REQUIRED';return}
    $data=Plain (Join-Path $root 'AutumnOS_Data')
    $log=Plain (Join-Path $data 'Logs/runtime-events.jsonl')
    if(!(Test-Path -LiteralPath $log -PathType Leaf)){$report.reason='NO_RUNTIME_EVENTS';return}
    $logBytes=Bytes $log 1100000
    if($logBytes[-1] -ne 10){throw 'INCOMPLETE_RUNTIME_LOG_RETRY'}
    $events=@(foreach($line in $utf8.GetString($logBytes).Split([char]10,[StringSplitOptions]::RemoveEmptyEntries)){
        $row=$line|ConvertFrom-Json -Depth 35 -AsHashtable
        if($row.buildId -ceq $BuildId -and $row.appId -ceq $appId -and $row.packageSha256 -ceq $packageSha){$row}
    })
    if(!$events.Count){$report.reason='NO_MATCHING_CHEMLEAP_INSTANCE';return}
    $registry=JsonRecord (Join-Path $data 'Apps/.autumnos-registry.json')
    $entry=@($registry.applications|Where-Object {$_.appId -ceq $appId -and $_.isInstalled -and $_.source.kind -eq 'github' -and
        $_.source.repositoryId -eq 1414175562 -and $_.package.packageSha256 -ceq $packageSha -and $_.package.manifest.version -ceq '0.1.1'})
    if($entry.Count -ne 1){throw 'INSTALLED_SOURCE_OR_PACKAGE_MISMATCH'}
    $history=JsonRecord (Join-Path $data 'Downloads/store-v1/history.json')
    $download=@($history.Tasks|Where-Object {$_.State -eq 6 -and $_.Request.AppId -ceq $appId -and $_.Request.Sha256 -ceq $packageSha -and
        $_.Request.RepositoryId -eq 1414175562 -and $_.Request.ReleaseId -eq 415748507 -and $_.Request.AssetId -eq 650220127})|Select-Object -Last 1
    if(!$download -or $download.Id -notmatch '^[a-f0-9]{32}$' -or (Digest (Bytes (Join-Path $data ('Downloads/store-v1/'+$download.Id+'.autumn')) 28871)) -cne $packageSha){throw 'DOWNLOAD_BYTES_OR_RELEASE_MISMATCH'}
    $checkpoint=$null
    if($Mode -eq 'Verify' -and $CheckpointId){
        $checkpoint=JsonRecord (Join-Path $evidence "chemleap-evidence-$CheckpointId/checkpoint.json")
        if($checkpoint.build_id -cne $BuildId -or $checkpoint.source_snapshot_id -cne $build.source_snapshot_id -or
            $checkpoint.package_sha256 -cne $packageSha -or $checkpoint.log_bytes -lt 1 -or $checkpoint.log_bytes -gt $logBytes.Length){throw 'CHECKPOINT_BINDING_FAILED'}
        $prefix=[byte[]]::new([int]$checkpoint.log_bytes);[Array]::Copy($logBytes,$prefix,$prefix.Length)
        if((Digest $prefix) -cne $checkpoint.log_sha256){throw 'ORIGINAL_LOG_PREFIX_CHANGED'}
        $report.checkpoint_id=$CheckpointId
    }
    if($checkpoint){$first=$checkpoint.instance_id}
    else{$first=(@($events|Where-Object eventName -eq 'saves_write_native_receipt')|Select-Object -Last 1).instanceId
        if(!$first){$first=$events[0].instanceId}}
    $guid=[Guid]::Empty
    if(![Guid]::TryParse([string]$first,[ref]$guid) -or $guid -eq [Guid]::Empty){throw 'INSTANCE_ID_REQUIRED'}
    $run=@($events|Where-Object instanceId -eq $first)
    $report.first_instance=$first
    $fg=@($run|Where-Object {$_.eventName -eq 'navigation_completed' -and $_.networkSandboxVerified -eq $true -and
        $_.state -eq 'Foreground' -and $_.nativeHost.OriginalQueryJobBound -and $_.nativeHost.CreationAttempted -and $_.nativeHost.OriginalProcessId -gt 0})|Select-Object -Last 1
    $timeline=Test-OrderedEvents $run @('native_endpoint_registered','bootstrap_gate_released','native_first_frame','navigation_completed')
    # Bind an independently captured client image to the same dedicated trial report and instance.
    if($fg -and $timeline){
        foreach($directory in (Get-ChildItem -LiteralPath $evidence -Directory -Filter 'native-store-*')){
            if($directory.Name -notmatch '^native-store-[a-f0-9]{32}$'){continue}
            $trialPath=Plain (Join-Path $directory.FullName 'acceptance.json')
            if(!(Test-Path -LiteralPath $trialPath -PathType Leaf)){continue}
            $trial=JsonRecord $trialPath
            if($trial.mode -notin @('DesktopTrial','ClientTrial') -or $trial.build_id -cne $BuildId -or $trial.source_snapshot_id -cne $build.source_snapshot_id -or
                $trial.tested_executable -ne $prepared.executable -or $trial.native_first_frame -ne 'passed' -or
                !@($trial.runtime_evidence|Where-Object {$_.instanceId -eq $first -and $_.packageSha256 -ceq $packageSha -and $_.eventName -eq 'native_first_frame'}).Count){continue}
            $png=Bytes (Join-Path $directory.FullName '01-private-first-frame.png') 33554432
            if($png.Length -lt 24 -or (Digest $png) -cne $trial.'01-private-first-frame_sha256' -or [Convert]::ToHexString($png,0,8) -cne '89504E470D0A1A0A'){throw 'FIRST_FRAME_ARTIFACT_MISMATCH'}
            $checks.first_frame='passed';break
        }
    }
    if($fg -and (Test-OrderedEvents $run @('foreground','background','foreground'))){$checks.background_and_resume='observed_by_runtime'}
    $retired=ClosedGroup $run
    if($retired -eq $true){$checks.normal_group_exit_and_leases='passed'}elseif($retired -eq $false){$checks.normal_group_exit_and_leases='failed'}
    $save=ExactSave
    $wrote=Test-OrderedEvents $run @('saves_write_ok','saves_write_native_receipt')
    if($save -and $wrote -and $fg){
        if($Mode -eq 'CaptureAfterSave'){
            if($events[-1].instanceId -ne $first){throw 'CAPTURE_BEFORE_SECOND_INSTANCE_REQUIRED'}
            SaveNew 'checkpoint.json' ([ordered]@{build_id=$BuildId;source_snapshot_id=$build.source_snapshot_id;package_sha256=$packageSha;instance_id=$first;
                log_bytes=$logBytes.Length;log_sha256=(Digest $logBytes);save=$save})
            $report.checkpoint_id=$runId
            $checks.saved_on_disk_with_native_receipt='observed_save_and_receipt'
        }elseif($checkpoint){
            $checks.original_save_bytes_preserved=if($save.file_sha256 -ceq $checkpoint.save.file_sha256 -and $save.bytes -eq $checkpoint.save.bytes -and $save.revision -eq $checkpoint.save.revision){'passed'}else{'failed'}
            if($checks.original_save_bytes_preserved -eq 'passed'){
                $checks.saved_on_disk_with_native_receipt='observed_save_and_receipt'
                $tail=$utf8.GetString($logBytes,[int]$checkpoint.log_bytes,$logBytes.Length-[int]$checkpoint.log_bytes)
                $later=@(foreach($line in $tail.Split([char]10,[StringSplitOptions]::RemoveEmptyEntries)){
                    $e=$line|ConvertFrom-Json -AsHashtable -Depth 35
                    if($e.buildId -ceq $BuildId -and $e.appId -ceq $appId -and $e.packageSha256 -ceq $packageSha -and $e.instanceId -ne $first){$e}
                })
                foreach($group in @($later|Group-Object -Property { $_.instanceId })){
                    $newRun=@($group.Group)
                    $newId=[Guid]::Empty
                    if(![Guid]::TryParse([string]$group.Name,[ref]$newId) -or $newId -eq [Guid]::Empty){continue}
                    $matching=@($newRun|Where-Object {$_.eventName -ceq 'guest_test_save_read_value_receipt' -and $_.testSaveValueSha256 -ceq $checkpoint.save.content_sha256})
                    $names=@($events|ForEach-Object { [string]$_.instanceId+'@'+[string]$_.eventName })
                    $closedIndex=[Array]::IndexOf($names,([string]$first+'@closed'))
                    $newIndex=[Array]::IndexOf($names,([string]$group.Name+'@native_endpoint_registered'))
                    if($newIndex -gt $closedIndex -and $closedIndex -ge 0 -and $matching.Count -and $retired -eq $true -and (Test-OrderedEvents $newRun @('native_first_frame','navigation_completed','saves_read_ok','saves_read_native_receipt'))){
                        $checks.new_instance_sdk_read_original_value='passed';$report.readback_instance=$group.Name;break
                    }
                }
            }
        }
    }
    if($checks.Values -contains 'failed'){$report.status='failed'}
    elseif($report.checkpoint_id -and $Mode -eq 'CaptureAfterSave'){$report.status='checkpoint_saved'}
    elseif($checks.Values -contains 'not_run'){$report.status='partial'}
    else{$report.status='evidence_recorded'}
}catch{$report.status='failed';$report.reason='EVIDENCE_VALIDATION_FAILED';throw}
finally{
    SaveNew 'report.json' $report
    Write-Host ("ChemLeap evidence "+$report.status+" at "+$destination)
}
