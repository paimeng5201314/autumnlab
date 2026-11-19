#requires -Version 7.6
[CmdletBinding(DefaultParameterSetName='Publish')]
param(
    [Parameter(Mandatory)][string]$Workspace,
    [Parameter(Mandatory)][ValidatePattern('^T06-[A-Za-z0-9_-]+$')][string]$BuildId,
    [Parameter(Mandatory)][ValidatePattern('^T06-[A-Za-z0-9_-]+$')][string]$PreviousBuildId,
    [Parameter(Mandatory)][ValidateRange(1,9223372036854775806)][long]$PreviousSequence,
    [ValidateSet('1.0.0')][string]$PreviousVersion='1.0.0',
    [ValidateSet('plus-v1.0.0-20261115')][string]$ReleaseTag='plus-v1.0.0-20261115',
    [string]$GhPath,[string]$PrivateDirectory,[string]$ExecutablePath,[string]$PreviousBuildReceipt,
    [Parameter(Mandatory,ParameterSetName='Check')][switch]$CheckLocal,
    [Parameter(ParameterSetName='Check')][switch]$PrepareBrowserFiles,
    [Parameter(Mandatory,ParameterSetName='Browser')][switch]$PrepareAfterBrowserUpload,
    [Parameter(Mandatory,ParameterSetName='Readback')][switch]$VerifyAfterBrowserPublish,
    [Parameter(ParameterSetName='Browser')][Parameter(ParameterSetName='Readback')][ValidateRange(1,9223372036854775807)][long]$BrowserPayloadId,
    [Parameter(Mandatory,ParameterSetName='Publish')][Parameter(Mandatory,ParameterSetName='Browser')][Parameter(Mandatory,ParameterSetName='Readback')][string]$BackupDirectory
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'GitHubPublishTransport.ps1')
. (Join-Path $PSScriptRoot 'PublishReleaseAssets.ps1')
$Workspace=Get-PublishPath $Workspace
$inputArgs=@{Workspace=$Workspace;BuildId=$BuildId;PreviousBuildId=$PreviousBuildId;PreviousSequence=$PreviousSequence;PreviousVersion=$PreviousVersion;ReleaseTag=$ReleaseTag;GhPath=$GhPath;ExecutablePath=$ExecutablePath;PreviousBuildReceipt=$PreviousBuildReceipt}
$helper=Get-PublishPath (Join-Path $PSScriptRoot 'Get-TestedReleaseInputs.ps1')
$local=& $helper @inputArgs -Mode Local -PrivateDirectory $PrivateDirectory
if ($local.version -cne '1.0.0' -or $local.channel -cne 'plus' -or $local.build_id -ceq $PreviousBuildId -or $local.sequence -le $PreviousSequence -or
    $local.exe.name -cne $FixedAssets[0] -or $local.payload.name -cne $FixedAssets[1]) {throw 'The original product version and newer production revision are required.'}
if ($CheckLocal) {
    if ($PrepareBrowserFiles) {
        $local|Add-Member -NotePropertyName browser_files -NotePropertyValue @($local.exe,$local.payload | ForEach-Object {New-BrowserUploadLink $_})
    }
    return $local
}
Assert-OriginalBackups $BackupDirectory
$releaseId=$FixedReleaseId
$gate=Get-PublishPath (Join-Path $Workspace "artifacts/builds/$BuildId")
$lockPath=Join-Path $gate 'publish-signed-update.lock'
if (Test-Path -LiteralPath $lockPath) { $null=Get-PublishPath $lockPath }
$lock=[IO.File]::Open($lockPath,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
$held=[Collections.Generic.List[IO.FileStream]]::new()
$releaseMutex=$null
$ownsReleaseMutex=$false
try {
    if ($PSCmdlet.ParameterSetName -ceq 'Publish') {
        $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $releaseMutex=[Threading.Mutex]::new($false,"Global\AutumnOS-Publisher-$DistributionRepoId-$FixedReleaseId-$sid")
        try {$ownsReleaseMutex=$releaseMutex.WaitOne(0)} catch [Threading.AbandonedMutexException] {$ownsReleaseMutex=$true}
        if (-not $ownsReleaseMutex) {throw 'Another local build is updating this same release.'}
    }
    foreach ($file in @($local.exe,$local.payload)) { Hold-ReleaseFile $file }
    $readOnly=$PrepareAfterBrowserUpload -or $VerifyAfterBrowserPublish
    Assert-RepositoryIdentity $readOnly
    Assert-DistributionTag
    $r=Get-ExactRelease $releaseId $readOnly
    if ($VerifyAfterBrowserPublish -and -not [IO.File]::Exists((Join-Path $gate 'same-release-slots.json'))) {throw 'A pre-switch journal is required for readback.'}
    $journal=Get-OriginalSlotJournal $r $gate
    if ($readOnly) {
        if (-not $BrowserPayloadId) {
            $matches=@($r.assets|Where-Object { $_.name -cin @($local.payload.name,(Get-StageFile $local.payload).name) -and $_.digest -ceq ('sha256:'+$local.payload.sha) })
            if ($matches.Count -ne 1) {throw 'Missing or ambiguous verified browser payload.'}
            $BrowserPayloadId=$matches[0].id
        }
        $null=Get-AssetForBinding $r $local.payload $BrowserPayloadId $true
        $action=if ($VerifyAfterBrowserPublish) {'Readback'} else {'Bind'}
        $signed=& $helper @inputArgs -Mode $action -PayloadAssetId $BrowserPayloadId -PrivateDirectory $PrivateDirectory
        if (-not $signed.old_v2_rejected -or $signed.asset_id -ne $BrowserPayloadId) {throw 'Unexpected signing report.'}
        if ($PrepareAfterBrowserUpload) {
            $signed|Add-Member -NotePropertyName browser_files -NotePropertyValue @($signed.signature,$signed.manifest | ForEach-Object {New-BrowserUploadLink $_})
            return $signed
        }
    } else {
        $payload=Ensure-CandidateFile $r $local.payload
        $signed=& $helper @inputArgs -Mode Bind -PayloadAssetId $payload.id -PrivateDirectory $PrivateDirectory
        if ($signed.asset_id -ne $payload.id -or -not $signed.old_v2_rejected) {throw 'Unexpected signing report.'}
    }
    foreach ($file in @($signed.manifest,$signed.signature)) { Hold-ReleaseFile $file }
    $ordered=@($local.payload,$local.exe,$signed.signature,$signed.manifest)
    $targets=@{}
    foreach ($file in $ordered) {
        $r=Get-ExactRelease $releaseId $readOnly
        $targets[$file.name]=if ($readOnly) {Get-VerifiedAsset $r $file $true} else {Ensure-CandidateFile $r $file}
    }
    Assert-DistributionTag
    if ($targets[$local.payload.name].id -ne $signed.asset_id) {throw 'The final payload ID differs from the signed asset ID.'}
    if (-not $readOnly) {
        try {
            foreach ($file in $ordered) { Set-FinalAssetName $file $journal $targets[$file.name].id }
        } catch {
            $cause=$_.Exception.Message
            try { Restore-OriginalSlots $ordered $journal $targets } catch {throw "Switch interrupted; local journal preserved for retry. $cause"}
            throw "Switch interrupted; original canonical names restored. $cause"
        }
    }
    # Public client and any later retirement use exact release and asset IDs.
    $public=Get-ExactRelease $releaseId $true
    Assert-DistributionTag
    Assert-OtherAssets $public $journal
    $assets=@(foreach ($file in $ordered) {
        $asset=Get-VerifiedAsset $public $file $true
        if ($asset.id -ne $targets[$file.name].id) { throw 'A promoted asset ID changed.' }
        $hash=Get-PublicFileHash $asset.browser_download_url $file.bytes
        if ($hash -cne $file.sha) { throw "Anonymous asset download differs for $($file.name)." }
        [ordered]@{name=$file.name;id=$asset.id;bytes=$file.bytes;sha256=$hash}
    })
    $result=[ordered]@{status='published_anonymously_verified';repository=$DistributionRepo;repository_id=$DistributionRepoId;release_id=$releaseId;
        tag=$FixedTag;title=$FixedTitle;version='1.0.0';commit=$FixedCommit;build_id=$BuildId;sequence=$local.sequence;source_snapshot_id=$local.snapshot;
        old_v2_rejected=$true;assets=$assets;other_assets_unchanged=$true;checked_utc=[DateTimeOffset]::UtcNow.ToString('o')}
    $out=Join-Path $gate ('release-public-verification-'+[Guid]::NewGuid().ToString('N')+'.json')
    [IO.File]::WriteAllText($out,($result|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
    return [pscustomobject]$result
} finally {
    foreach ($stream in $held) { $stream.Dispose() }
    if ($ownsReleaseMutex) { $releaseMutex.ReleaseMutex() }
    if ($releaseMutex) { $releaseMutex.Dispose() }
    $lock.Dispose()
}
