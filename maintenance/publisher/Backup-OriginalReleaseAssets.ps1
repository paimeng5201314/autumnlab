#requires -Version 7.6
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BackupDirectory,[Parameter(Mandatory)][string]$OriginalExecutablePath,[Parameter(Mandatory)][string]$OriginalPayloadPath)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'GitHubPublishTransport.ps1')
Assert-RepositoryIdentity $true
Assert-DistributionTag
$release=Get-ExactRelease $FixedReleaseId $true
if (-not [IO.Path]::IsPathFullyQualified($BackupDirectory)) { throw 'Use an absolute backup path.' }
$null=Get-PublishPath ([IO.Path]::GetDirectoryName($BackupDirectory))
[void][IO.Directory]::CreateDirectory($BackupDirectory)
$BackupDirectory=Get-PublishPath $BackupDirectory
$rows=@(foreach ($spec in (Get-OriginalAssetSpecs)) {
    $source=if ($spec.name -ceq 'AutumnOS.exe') {Get-PublishPath $OriginalExecutablePath} else {Get-PublishPath $OriginalPayloadPath}
    $remote=Get-VerifiedAsset $release $spec $true
    if ($remote.id -ne $spec.id -or (Get-Item -LiteralPath $source).Length -ne $spec.bytes -or
        (Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant() -cne $spec.sha) { throw 'Original source does not match the existing GitHub asset.' }
    $dst=Join-Path $BackupDirectory $spec.name
    if (-not [IO.File]::Exists($dst)) {
        if ([IO.Path]::GetPathRoot($dst) -ieq [IO.Path]::GetPathRoot($source)) {
            $null=New-Item -ItemType HardLink -Path $dst -Target $source -ErrorAction Stop
        } else { [IO.File]::Copy($source,$dst,$false) }
    }
    $dst=Get-PublishPath $dst
    $entry=Get-Item -LiteralPath $dst
    if ($entry.Length -ne $spec.bytes -or (Get-FileHash -LiteralPath $dst).Hash.ToLowerInvariant() -cne $spec.sha) {throw 'Original backup readback failed.'}
    $entry.IsReadOnly=$true
    [ordered]@{name=$spec.name;id=$spec.id;bytes=$spec.bytes;sha=$spec.sha;path=$dst;source=$source;readonly=(Get-Item -LiteralPath $dst).IsReadOnly}
})
$receipt=Join-Path $BackupDirectory 'originals.json'
if (-not [IO.File]::Exists($receipt)) {
    $tmp=Join-Path $BackupDirectory ([Guid]::NewGuid().ToString('N')+'.tmp')
    [IO.File]::WriteAllText($tmp,([ordered]@{repository_id=$DistributionRepoId;release_id=$FixedReleaseId;tag=$FixedTag;checked_utc=[DateTimeOffset]::UtcNow.ToString('o');assets=$rows}|ConvertTo-Json -Depth 4))
    [IO.File]::Move($tmp,$receipt)
}
Assert-OriginalBackups $BackupDirectory
Write-Output $receipt
