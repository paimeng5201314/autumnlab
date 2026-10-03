[CmdletBinding()]
param([Parameter(Mandatory)][string]$SetupPath,[Parameter(Mandatory)][string]$PortableDirectory,[Parameter(Mandatory)][string]$ReportDirectory)
. "$PSScriptRoot/Common.ps1"
$SetupPath=[IO.Path]::GetFullPath($SetupPath);$PortableDirectory=[IO.Path]::GetFullPath($PortableDirectory)
$ReportDirectory=[IO.Path]::GetFullPath($ReportDirectory)
if(Test-Path -LiteralPath $ReportDirectory){throw 'Use a new report directory; historical evidence is preserved.'}
New-Item -ItemType Directory -Path $ReportDirectory|Out-Null
$name='T05-'+[Guid]::NewGuid().ToString('N')
$root=Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) ('Programs/AutumnOS-Test-'+$name)
if(Test-Path -LiteralPath $root){throw 'Unexpected preexisting isolated install root.'}
$checks=[Collections.Generic.List[object]]::new()
$report=[ordered]@{status='failed';setup=$SetupPath;setup_sha256=(Get-FileHash -LiteralPath $SetupPath).Hash.ToLowerInvariant();install_root=$root;test_name=$name;checks=$checks;started_utc=[DateTimeOffset]::UtcNow.ToString('o');global_components_installed=$false;real_identity='not_run'}
function Check-Setup([string]$Name,[bool]$Passed){$checks.Add(@{name=$Name;status=$(if($Passed){'passed'}else{'failed'})});if(-not $Passed){throw "Setup check failed: $Name"}}
function Run-Setup([string]$Path,[bool]$Uninstall,[int]$Expected){
    $args=@('--silent','--test-name',$name);if($Uninstall){$args+='--uninstall'}
    $attempt=$checks.Count
    $process=Start-Process -FilePath $Path -ArgumentList $args -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $ReportDirectory "setup-$attempt-stdout.log") -RedirectStandardError (Join-Path $ReportDirectory "setup-$attempt-stderr.log")
    if(-not $process.WaitForExit(240000)){throw 'Owned setup process exceeded test budget; process preserved, no force kill.'}
    Check-Setup ('installer_exit_'+$Expected+'_'+$checks.Count) ($process.ExitCode -eq $Expected)
    $process.Dispose()
}
try{
    Run-Setup $SetupPath $false 0
    $stream=[IO.File]::OpenRead($SetupPath);try{$pe=$stream.ReadByte() -eq 77 -and $stream.ReadByte() -eq 90}finally{$stream.Dispose()}
    Check-Setup 'real_setup_is_PE_and_installs_stable_executable' ($pe -and (Test-Path -LiteralPath (Join-Path $root 'AutumnOS.exe')))
    foreach($entry in @('AutumnOS.exe','AutumnOS.Updater.exe','AutumnOS.Client.dll','WebView2Runtime/msedgewebview2.exe','AutumnOS.Client.pri','MainWindow.xbf')){
        Check-Setup ('same_build_bytes_'+$entry) ((Get-FileHash -LiteralPath (Join-Path $root $entry)).Hash -eq (Get-FileHash -LiteralPath (Join-Path $PortableDirectory $entry)).Hash)
    }
    & "$PSScriptRoot/Test-DeliveredLauncher.ps1" -ExecutablePath (Join-Path $root 'AutumnOS.exe') -ReportDirectory (Join-Path $ReportDirectory 'installed-native')
    $data=Join-Path $root 'AutumnOS_Data'
    $saved=@(Get-ChildItem -LiteralPath $data -Recurse -File|Where-Object {$_.FullName -notmatch '[\\/](Logs|Cache|Updates)[\\/]' -and $_.Extension -ne '.lock'}|ForEach-Object {@{path=$_.FullName;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
    $report.data_before_uninstall=$saved
    $unknown=Join-Path $root 'user-unknown-retained.txt';Set-Content -LiteralPath $unknown -Value 'T05 isolated install/uninstall preservation fixture' -Encoding utf8
    $unknownHash=(Get-FileHash -LiteralPath $unknown).Hash
    Run-Setup (Join-Path $root 'AutumnOS.Uninstall.exe') $true 0
    Check-Setup 'uninstall_removes_managed_entry_only' (-not (Test-Path -LiteralPath (Join-Path $root 'AutumnOS.exe')) -and -not (Test-Path -LiteralPath (Join-Path $root 'AutumnOS.Client.exe')))
    Check-Setup 'unknown_file_preserved_after_uninstall' ((Get-FileHash -LiteralPath $unknown).Hash -eq $unknownHash)
    foreach($file in $saved){Check-Setup ('native_created_data_preserved_'+[IO.Path]::GetRelativePath($data,$file.path)) ((Get-FileHash -LiteralPath $file.path).Hash -eq $file.sha256)}
    Run-Setup $SetupPath $false 0
    Check-Setup 'reinstall_keeps_original_data_and_unknown_file' ((Get-FileHash -LiteralPath $unknown).Hash -eq $unknownHash -and (Get-FileHash -LiteralPath (Join-Path $root 'AutumnOS.Client.dll')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $PortableDirectory 'AutumnOS.Client.dll')).Hash)
    foreach($file in $saved){Check-Setup ('reinstalled_data_preserved_'+[IO.Path]::GetRelativePath($data,$file.path)) ((Get-FileHash -LiteralPath $file.path).Hash -eq $file.sha256)}
    Run-Setup (Join-Path $root 'AutumnOS.Uninstall.exe') $true 0
    $report.status='passed'
}catch{$report.failure=$_.Exception.Message;throw}
finally{$report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o');$report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $ReportDirectory 'setup-smoke.json') -Encoding utf8}
