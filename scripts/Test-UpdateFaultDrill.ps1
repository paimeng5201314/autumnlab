# Maintained entry for isolated updater interruption, busy-file and legacy-entry races.
# Never run alongside another native smoke or the producer's business instance.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('applying','health','busy-file','legacy-v1')][string]$Scenario,
    [Parameter(Mandatory)][string]$DrillBuilds,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [string]$LegacyExecutablePath,
    [switch]$PrepareOnly
)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$ReportDirectory=[IO.Path]::GetFullPath($ReportDirectory)
if(Test-Path -LiteralPath $ReportDirectory){throw 'Existing fault evidence is preserved. Choose a new report directory.'}
$build=Get-Content -LiteralPath $DrillBuilds -Raw|ConvertFrom-Json
$a=@($build.variants|Where-Object name -eq 'A');$b=@($build.variants|Where-Object name -eq 'B')
if($a.Count -ne 1 -or $b.Count -ne 1 -or $a[0].health_fault -or $b[0].health_fault -or $a[0].build_id -eq $b[0].build_id){throw 'Two distinct real normal-health A/B builds required.'}
if($a[0].build_id -notmatch '^T0[56]-' -or $b[0].build_id -notmatch '^T0[56]-'){throw 'Explicit local drill builds required.'}
if($Scenario -eq 'legacy-v1' -and (-not $LegacyExecutablePath -or -not(Test-Path -LiteralPath $LegacyExecutablePath -PathType Leaf))){throw 'Supply the preserved real v1 entry. No replacement or invented legacy file is permitted.'}
$generator,$driver=switch($Scenario){
    'applying' {'Make-FaultDriver.ps1';'Test-T05UpdaterCrash.ps1'}
    'health' {'Make-FaultDriver.ps1';'Test-T05UpdaterCrash.ps1'}
    'busy-file' {'Make-BusyFileDriver.ps1';'Test-T05BusyFile.ps1'}
    'legacy-v1' {'Make-LegacyDriver.ps1';'Test-T05LegacyV1Race.ps1'}
}
$tools=Join-Path $ReportDirectory 'driver';New-Item -ItemType Directory -Path $tools|Out-Null
& (Join-Path $PSScriptRoot "update-faults/$generator") -ProjectRoot $projectRoot -OutputDirectory $tools
$driverPath=Join-Path $tools $driver
$generated=Get-Content -LiteralPath $driverPath -Raw
$task=$a[0].build_id.Split('-')[0]
$generated=$generated.Replace("task_id='T05'", "task_id='$task'")
Set-Content -LiteralPath $driverPath -Value $generated -Encoding utf8NoBOM
$tokens=$null;$errors=$null;$null=[Management.Automation.Language.Parser]::ParseFile($driverPath,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Generated fault driver syntax failed.'}
$manifest=[ordered]@{task_id=$task;scenario=$Scenario;status='prepared_not_run';drill_builds=[IO.Path]::GetFullPath($DrillBuilds);
    source_driver_sha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Test-T05UpdateSmoke.ps1')).Hash.ToLowerInvariant();
    generated_driver_sha256=(Get-FileHash -LiteralPath $driverPath).Hash.ToLowerInvariant();source_generators=@(
        Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'update-faults') -File|ForEach-Object {[ordered]@{name=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}}
    );physical_power_failure='not_run';test_owned_processes_only=$true;setup='deferred_by_user'}
$manifest|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $ReportDirectory 'invocation.json') -Encoding utf8
if($PrepareOnly){return}
$arguments=@{AExecutablePath=$a[0].executable;BPayloadDirectory=$b[0].signed_material;FeedDirectory=$build.feed;ReportDirectory=(Join-Path $ReportDirectory 'native');WindowTimeoutSeconds=90;UpdateTimeoutSeconds=600;BlockedObservationSeconds=35}
if($Scenario -in @('applying','health')){$arguments.FaultPhase=$Scenario}
if($Scenario -eq 'legacy-v1'){$arguments.LegacyExecutablePath=[IO.Path]::GetFullPath($LegacyExecutablePath)}
& $driverPath @arguments
$manifest.status='executed';$manifest.result=Join-Path $ReportDirectory 'native/update-smoke.json'
$manifest|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $ReportDirectory 'invocation.json') -Encoding utf8
