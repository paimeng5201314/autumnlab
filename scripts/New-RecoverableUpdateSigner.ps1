[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$CliPath,
    [Parameter(Mandatory)][string]$KeyId,
    [Parameter(Mandatory)][string]$PrimaryDirectory,
    [Parameter(Mandatory)][string]$RestoreDirectory,
    [Parameter(Mandatory)][string]$BackupPath,
    [Parameter(Mandatory)][string]$CandidatePublicPath
)
. "$PSScriptRoot/Common.ps1"
$ErrorActionPreference='Stop'
if([Console]::IsInputRedirected -or [Console]::IsOutputRedirected -or [Console]::IsErrorRedirected){throw 'Use a private interactive terminal.'}
function Get-NewSignerPath([string]$value){
    if($value -notmatch '^[A-Za-z]:[\\/]' -or $value.Substring(3) -match '[:\x00-\x1f]|(^|[\\/])\.{1,2}([\\/]|$)|[. ]([\\/]|$)'){throw 'Use plain absolute local paths.'}
    $full=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($value))
    if((Test-Path -LiteralPath $full) -or -not [IO.Directory]::Exists([IO.Path]::GetDirectoryName($full))){throw 'Each result needs an unused path in an existing parent directory.'}
    for($cursor=[IO.Path]::GetDirectoryName($full);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Redirected destination refused.'}
    }
    return $full
}
$primary=Get-NewSignerPath $PrimaryDirectory
$restored=Get-NewSignerPath $RestoreDirectory
$backup=Get-NewSignerPath $BackupPath
$candidate=Get-NewSignerPath $CandidatePublicPath
$cli=(Resolve-Path -LiteralPath $CliPath).Path
if((Split-Path $cli -Leaf) -cne 'AutumnOS.Update.Cli.dll'){throw 'Select the tested recovery CLI.'}
if($KeyId -notmatch '^[A-Za-z0-9_.-]{1,128}$' -or -not $backup.EndsWith('.encrypted.pk8',[StringComparison]::OrdinalIgnoreCase) -or -not $candidate.EndsWith('.json',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid key ID or destination suffix.'}
$pinned=Get-Content -LiteralPath (Join-Path $ProjectRoot 'build/update-trust.production.json') -Raw|ConvertFrom-Json
if($KeyId -in @($pinned.keys.keyId)){throw 'For the currently trusted identity use restore-production, never initialize another key.'}
$workBoundary=[IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($ProjectRoot))
foreach($item in @($primary,$restored,$backup,$candidate)){
    if($item -eq $workBoundary -or $item.StartsWith($workBoundary+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Choose destinations outside the product source tree.'}
}
if(@(@($primary,$restored,$backup,$candidate)|Sort-Object -Unique).Count -ne 4){throw 'Destinations must be distinct.'}
$staging=Join-Path ([IO.Path]::GetDirectoryName($candidate)) ('pending-'+[guid]::NewGuid().ToString('N')+'.json')
$readback=Join-Path ([IO.Path]::GetDirectoryName($candidate)) ('restored-'+[guid]::NewGuid().ToString('N')+'.json')
function Invoke-Signer([string[]]$arguments){
    & $Dotnet $cli @arguments
    if($LASTEXITCODE -ne 0){throw 'Operation failed; preserve existing files. No candidate has been approved.'}
}
Invoke-Signer @('init-production','--private-directory',$primary,'--public-output',$staging,'--key-id',$KeyId)
Invoke-Signer @('backup-production','--private-directory',$primary,'--backup',$backup,'--trust',$staging,'--key-id',$KeyId)
Invoke-Signer @('restore-production','--private-directory',$restored,'--backup',$backup,'--trust',$staging,'--key-id',$KeyId)
Invoke-Signer @('export-production-public','--private-directory',$restored,'--public-output',$readback)
if((Get-FileHash -LiteralPath $readback).Hash -cne (Get-FileHash -LiteralPath $staging).Hash){throw 'Recovered public bytes differ. No candidate has been approved.'}
[IO.File]::Move($readback,$candidate,$false)
[IO.File]::Delete($staging)
Write-Host "Same-key recovery verified. Candidate public root: $candidate. No client or Release has been changed."
