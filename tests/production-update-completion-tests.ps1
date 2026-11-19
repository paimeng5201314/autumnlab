#requires -Version 7.5
[CmdletBinding()]
param([string]$ReportPath)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$project=Split-Path $PSScriptRoot -Parent
$entry=Join-Path $project 'scripts/Complete-ProductionUpdate.ps1'
$tokens=$null
$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($entry,[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw $errors[0] }
# Import only the production predicates: no CLI, signing key or external
# endpoint is contacted. The release and asset fixtures are synthetic.
foreach ($name in @('Assert-ReleaseTime','Assert-UpgradeOrder','Assert-PublishedAsset','Get-ReleaseBinding')) {
    $definition=$ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name},$false)
    if (-not $definition) { throw "Missing production predicate $name." }
    . ([scriptblock]::Create($definition.Extent.Text))
}
$store=Join-Path $project 'tools/AutumnOS.Update.Cli/bin/x64/Release/net10.0/win-x64/AutumnOS.Store.dll'
$semVer=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes($store)).GetType('AutumnOS.Store.SemanticVersion',$true)
$now=[DateTimeOffset]::Parse('2030-02-01T10:15:30Z')
$repository='paimeng5201314/autumnOS'
$releaseApi="https://api.github.com/repos/$repository/releases"
$webRelease="https://github.com/$repository/releases"
$ReleaseTag='plus-v2.0.1'
$payload=[pscustomobject]@{asset='fixture.zip';bytes=[long]123;sha256='a'*64}
$manifest=[pscustomobject]@{version='2.0.1';sequence=[long]8;channel='plus';buildId='fixture-next';minimumUpdaterVersion='1.2.0';issuedAtUtc=$now.ToString('o');expiresAtUtc=$now.AddDays(7).ToString('o');payload=$payload}
$asset=[pscustomobject]@{id=[long]220;size=$payload.bytes;name=$payload.asset;state='uploaded';digest='sha256:'+$payload.sha256
    url="$releaseApi/assets/220";browser_download_url="$webRelease/download/$ReleaseTag/fixture.zip"}
$direct=$asset.PSObject.Copy()
$release=[pscustomobject]@{id=[long]110;url="$releaseApi/110";html_url="$webRelease/tag/$ReleaseTag";tag_name=$ReleaseTag
    draft=$false;prerelease=$false;published_at='2024-01-01T00:00:00Z';assets=@($asset)}
$requests=[Collections.Generic.List[string]]::new()
function Get-PublicJson([string]$Url) {
    $requests.Add($Url)
    if ($Url -ceq "$releaseApi/tags/$([uri]::EscapeDataString($ReleaseTag))") { return $release }
    if ($Url -ceq "$releaseApi/assets/220") { return $direct }
    throw 'Unexpected URL in synthetic release lookup.'
}
$results=[Collections.Generic.List[object]]::new()
function Check([string]$Name,[bool]$ShouldPass,[scriptblock]$Action) {
    $passed=$false
    try { $null=& $Action; $passed=$true } catch { if ($ShouldPass) { throw } }
    if ($passed -ne $ShouldPass) { throw "Unexpected acceptance in $Name." }
    $results.Add([ordered]@{name=$Name;status='passed'})
}
Check clock_valid $true { Assert-ReleaseTime $manifest $now }
Check clock_expired_at_boundary $false { Assert-ReleaseTime $manifest $now.AddDays(7) }
Check clock_future $false { Assert-ReleaseTime $manifest $now.AddMinutes(-6) }
Check clock_extended_draft $false {
    $bad=$manifest.PSObject.Copy(); $bad.expiresAtUtc=$now.AddDays(8).ToString('o'); Assert-ReleaseTime $bad $now
}
Check upgrade_advances $true { Assert-UpgradeOrder $manifest $semVer '2.0.0' 7 }
Check same_version_new_signed_revision $true { Assert-UpgradeOrder $manifest $semVer '2.0.1' 7 'fixture-installed' }
Check same_version_unknown_floor $false { Assert-UpgradeOrder $manifest $semVer '2.0.1' 0 'fixture-installed' }
Check same_version_missing_installed_build $false { Assert-UpgradeOrder $manifest $semVer '2.0.1' 7 }
Check same_version_same_build $false { Assert-UpgradeOrder $manifest $semVer '2.0.1' 7 'fixture-next' }
Check same_version_equal_sequence $false { Assert-UpgradeOrder $manifest $semVer '2.0.1' 8 'fixture-installed' }
Check same_version_older_updater_protocol $false { $bad=$manifest.PSObject.Copy();$bad.minimumUpdaterVersion='1.1.0';Assert-UpgradeOrder $bad $semVer '2.0.1' 7 'fixture-installed' }
Check equal_version_build_metadata $false { Assert-UpgradeOrder $manifest $semVer '2.0.1+previous' 7 }
Check lower_version $false { Assert-UpgradeOrder $manifest $semVer '2.1.0' 7 }
Check equal_sequence $false { Assert-UpgradeOrder $manifest $semVer '2.0.0' 8 }
Check lower_sequence $false { Assert-UpgradeOrder $manifest $semVer '2.0.0' 9 }
Check preview_in_stable_channel $false { $bad=$manifest.PSObject.Copy(); $bad.version='2.0.1-beta.1'; Assert-UpgradeOrder $bad $semVer '2.0.0' 7 }
Check official_two_reads_match $true {
    $binding=Get-ReleaseBinding $manifest
    if ($binding.release_id -ne 110 -or $binding.asset_id -ne 220 -or $requests.Count -ne 2) { throw 'Missing independent asset read.' }
}
foreach ($variant in @(
    @{name='literal_plus_in_github_tag_urls';tag='plus-v2.0.1+build.7';ok=$true},
    @{name='escaped_plus_in_github_tag_urls';tag='plus-v2.0.1%2Bbuild.7';ok=$true},
    @{name='double_escaped_plus_rejected';tag='plus-v2.0.1%252Bbuild.7';ok=$false}
)) {
    Check $variant.name $variant.ok {
        $ReleaseTag='plus-v2.0.1+build.7'
        $release=$release.PSObject.Copy(); $release.tag_name=$ReleaseTag
        $release.html_url="$webRelease/tag/$($variant.tag)"
        $asset=$asset.PSObject.Copy(); $asset.browser_download_url="$webRelease/download/$($variant.tag)/fixture.zip"
        $release.assets=@($asset); $direct=$asset.PSObject.Copy()
        Get-ReleaseBinding $manifest
    }
}
foreach ($field in @(
    @{name='wrong_repository';key='url';value='https://api.github.com/repos/unrelated/repo/releases/assets/220'},
    @{name='legacy_repository_asset';key='url';value='https://api.github.com/repos/paimeng5201314/autumnlab/releases/assets/220'},
    @{name='third_party_download';key='browser_download_url';value='https://example.invalid/fixture.zip'},
    @{name='replaced_asset';key='id';value=[long]221}, @{name='unuploaded_asset';key='state';value='new'},
    @{name='wrong_bytes';key='size';value=[long]122}, @{name='same_length_other_digest';key='digest';value=('sha256:'+'b'*64)},
    @{name='missing_digest';key='digest';value=$null}, @{name='renamed_payload';key='name';value='Fixture.zip'}
)) {
    Check $field.name $false { $direct=$asset.PSObject.Copy(); $direct.($field.key)=$field.value; Get-ReleaseBinding $manifest }
}
foreach ($field in @(
    @{name='draft_release';key='draft';value=$true}, @{name='stable_prerelease_mismatch';key='prerelease';value=$true},
    @{name='unpublished_release';key='published_at';value=$null}, @{name='different_tag';key='tag_name';value='other'},
    @{name='wrong_release_id';key='id';value=[long]111}, @{name='ambiguous_asset';key='assets';value=@($asset,$asset)}
    @{name='legacy_repository_release';key='url';value='https://api.github.com/repos/paimeng5201314/autumnlab/releases/110'}
)) {
    Check $field.name $false { $release=$release.PSObject.Copy(); $release.($field.key)=$field.value; Get-ReleaseBinding $manifest }
}
$report=[ordered]@{status='passed';total=$results.Count;results=$results
    kind='production_completion_predicates_with_synthetic_release_and_clock'
    source_sha256=(Get-FileHash -LiteralPath $entry).Hash.ToLowerInvariant()
    semver_assembly_sha256=(Get-FileHash -LiteralPath $store).Hash.ToLowerInvariant()
    live_github='not_run';production_signing='not_run';executed_utc=[DateTimeOffset]::UtcNow.ToString('o')}
$json=$report | ConvertTo-Json -Depth 5
if ($ReportPath) {
    $stream=[IO.File]::Open($ReportPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write([Text.Encoding]::UTF8.GetBytes($json)); $stream.Flush($true) } finally { $stream.Dispose() }
}
$json
