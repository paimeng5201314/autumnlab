#requires -Version 7.6
<#
The default operation is a read-only eligibility report. Only -Apply can remove
the two explicitly identified, byte-backed originals. Any historical reference
to either asset ID holds both, including references in a manual release record.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BackupDirectory,
    [Parameter(Mandatory)][string]$SlotJournal,
    [string]$PublicationReceipt,[string]$PreviousClientReceipt,[string]$CurrentClientReceipt,
    [string]$GhPath,[switch]$Apply
)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'GitHubPublishTransport.ps1')
. (Join-Path $PSScriptRoot 'PublishReleaseAssets.ps1')
Assert-RepositoryIdentity $true
Assert-DistributionTag
Assert-OriginalBackups $BackupDirectory
$r=Get-ExactRelease $FixedReleaseId $true
$journal=[IO.File]::ReadAllText((Get-PublishPath $SlotJournal))|ConvertFrom-Json
if ($journal.repository -ne $DistributionRepoId -or $journal.release -ne $FixedReleaseId -or $journal.tag -cne $FixedTag) {throw 'Different release journal.'}
Assert-OtherAssets $r $journal
$originals=@(Get-OriginalAssetSpecs)
function Get-PublicMetadataSet($Release) {
    @($Release.assets|Where-Object name -Like '*.json'|Sort-Object id|Select-Object id,name,size,digest)|ConvertTo-Json -Compress -AsArray
}
$checkedMetadataSet=Get-PublicMetadataSet $r
$references=@(foreach ($json in $r.assets|Where-Object name -Like '*.json') {
    if ($json.size -le 0 -or $json.size -gt 16MB) {throw 'Historical metadata exceeds the verification bound.'}
    $file=[pscustomobject]@{name=$json.name;bytes=$json.size;sha=$json.digest.Substring(7)}
    $actual=Get-VerifiedAsset $r $file $true
    $memory=[IO.MemoryStream]::new()
    try {
        if ((Get-PublicFileHash $actual.browser_download_url $actual.size $memory) -cne $file.sha) {throw 'Historical metadata bytes differ.'}
        $raw=[Text.UTF8Encoding]::new($false,$true).GetString($memory.ToArray()).TrimStart([char]0xfeff)
        $null=$raw|ConvertFrom-Json -Depth 100
        foreach ($old in $originals) {
            if ($raw -match ('(?<![0-9])'+$old.id+'(?![0-9])')) {[pscustomobject]@{metadata=$json.name;metadata_id=$json.id;sha256=$file.sha;original_id=$old.id}}
        }
    } finally {$memory.Dispose()}
})
if ($references.Count) {return [pscustomobject]@{status='retained_referenced_originals';release_id=$FixedReleaseId;references=$references;deleted=@()}}
if (-not $PublicationReceipt -or -not $PreviousClientReceipt -or -not $CurrentClientReceipt) {
    return [pscustomobject]@{status='retained_pending_publication_and_client_receipts';deleted=@()}
}
$pub=[IO.File]::ReadAllText((Get-PublishPath $PublicationReceipt))|ConvertFrom-Json
if ($pub.status -cne 'published_anonymously_verified' -or $pub.repository_id -ne $DistributionRepoId -or $pub.release_id -ne $FixedReleaseId -or
    $pub.tag -cne $FixedTag -or $pub.title -cne $FixedTitle -or $pub.version -cne '1.0.0' -or $pub.commit -cne $FixedCommit -or
    $pub.build_id -cne $journal.build -or -not $pub.old_v2_rejected -or -not $pub.other_assets_unchanged -or @($pub.assets).Count -ne 4) {throw 'Four verified release assets required.'}
$lookup=@{}
foreach ($name in $FixedAssets) {
    $a=@($pub.assets|Where-Object name -CEQ $name)
    if ($a.Count -ne 1) {throw 'Incomplete public attachment readback.'}
    $asset=Get-VerifiedAsset $r ([pscustomobject]@{name=$name;bytes=$a[0].bytes;sha=$a[0].sha256}) $true
    if ($asset.id -ne $a[0].id) {throw 'A verified final ID changed.'}
    $lookup[$name]=$a[0]
}
foreach ($item in @(@($PreviousClientReceipt,'Available'),@($CurrentClientReceipt,'NoCompatibleUpdate'))) {
    $clientReport=[IO.File]::ReadAllText((Get-PublishPath $item[0]))|ConvertFrom-Json
    if ($clientReport.status -cne 'passed' -or $clientReport.kind -cne 'real_UpdateService_anonymous_public_feed_and_isolated_data') {throw 'Expected an actual public UpdateService result.'}
    $c=$clientReport.readback
    if ($c.repository -cne $DistributionRepo -or $c.installed_version -cne '1.0.0' -or $c.embedded_trust_sha256 -cne 'd7696acf884dcd683fada3b0a4134ad876bd7aeec7e8fa2a0049bed5cb2a42cb' -or
        $c.state -cne $item[1] -or -not $c.check_complete -or -not $c.unattended_configured -or $c.channel -cne 'plus' -or $c.tag -cne $FixedTag -or
        $c.release_id -ne $FixedReleaseId -or $c.asset_id -ne $lookup[$FixedAssets[1]].id -or $c.target_build_id -cne $pub.build_id -or
        $c.sequence -ne $pub.sequence -or $c.version -cne '1.0.0' -or $c.key_id -cne 'autumnos-release-20261119-v3' -or
        $c.manifest_sha256 -cne $lookup['autumn.update.json'].sha256 -or $c.signature_sha256 -cne $lookup['autumn.update.sig'].sha256 -or
        $c.payload_sha256 -cne $lookup[$FixedAssets[1]].sha256 -or $c.payload_bytes -ne $lookup[$FixedAssets[1]].bytes) {throw 'Independent public client evidence differs.'}
    if ($item[1] -ceq 'Available' -and ($clientReport.independent_cli_verify.status -cne 'passed' -or $c.staging -cne 'passed' -or $c.independent_source_recheck -cne 'passed' -or
        $c.installed_sequence -lt 1 -or $c.installed_sequence -ge $pub.sequence -or $c.build_id -ceq $pub.build_id)) { throw 'Lower same-version client did not stage the update.' }
    if ($item[1] -ceq 'NoCompatibleUpdate' -and ($c.history_reason -cne 'UPDATE_CURRENT_VERSION' -or $c.installed_sequence -ne $pub.sequence -or
        $c.build_id -cne $pub.build_id)) { throw 'Current client did not suppress the installed revision.' }
}
$deleted=@()
if ($Apply) {Assert-RepositoryIdentity}
foreach ($old in $originals) {
    $live=Get-ExactRelease $FixedReleaseId $true
    Assert-OtherAssets $live $journal
    if ((Get-PublicMetadataSet $live) -cne $checkedMetadataSet) {throw 'Public JSON attachments changed after the reference scan.'}
    $match=@($live.assets|Where-Object id -EQ $old.id)
    if (-not $match.Count) {continue}
    $prior=[pscustomobject]@{name="previous-$($old.id)-$($old.name)";sha=$old.sha;bytes=$old.bytes}
    $identity=Get-VerifiedAsset $live $prior $true
    if ($identity.id -ne $old.id) {throw 'Unrecognized prior asset.'}
    if ($Apply) {
        $null=Invoke-PublishApi DELETE "$RepoApi/releases/assets/$($old.id)"
        if (@((Get-ExactRelease $FixedReleaseId $true).assets|Where-Object id -EQ $old.id).Count) {throw 'Original asset retirement has not been confirmed.'}
        $deleted+=$old.id
    }
}
[pscustomobject]@{status=$(if($Apply){'authorized_originals_retired'}else{'verified_eligible'});release_id=$FixedReleaseId;deleted=$deleted}
