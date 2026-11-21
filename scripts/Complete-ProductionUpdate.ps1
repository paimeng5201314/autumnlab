#requires -Version 7.5
<#
.SYNOPSIS
Validates a prepared production update against its public GitHub release. With
-PrivateDirectory it also signs locally. No GitHub credentials are used.
.DESCRIPTION
PreviousVersion and PreviousSequence are the installed version and highest
accepted sequence of the clients targeted by this upgrade. Pass the real floor.
Run in the original build workspace, after Prepare-ProductionUpdate and after
its unchanged payload appears in the published Release. -ValidateOnly exercises
the same release binding and verifies the ZIP without opening a signing key.
The verified pair is handed off in artifacts/update-completion/<BuildId>/verified.
Existing outputs and failed attempts are preserved.
#>
[CmdletBinding(DefaultParameterSetName='Check')]
param(
    [Parameter(Mandatory)][ValidatePattern('^v[0-9]+\.[0-9]+\.[0-9]+-[0-9]{8}$')][string]$BuildId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$')][string]$ReleaseTag,
    [Parameter(Mandatory)][ValidateLength(5,128)][string]$PreviousVersion,
    [Parameter(Mandatory)][ValidateRange(0,[long]::MaxValue)][long]$PreviousSequence,
    [Parameter(Mandatory)][ValidateRange(0,9223372036854775806)][long]$PreviousSecuritySequence,
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$')][string]$PreviousBuildId,
    [Parameter(Mandatory,ParameterSetName='Check')][switch]$ValidateOnly,
    [Parameter(Mandatory,ParameterSetName='Sign')][string]$PrivateDirectory
)
. "$PSScriptRoot/Common.ps1"
. "$PSScriptRoot/UpdatePreparationFiles.ps1"
Set-StrictMode -Version Latest
$repository = 'paimeng5201314/autumnOS'
$releaseApi = "https://api.github.com/repos/$repository/releases"
$webRelease = "https://github.com/$repository/releases"

function Assert-PlainLocation([string]$Path) {
    for ($cursor = [IO.Path]::GetFullPath($Path); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        try { $attributes = [IO.File]::GetAttributes($cursor) }
        catch [IO.FileNotFoundException], [IO.DirectoryNotFoundException] { continue }
        if ($attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point in update path: $cursor" }
    }
}
function Read-Record([string]$Path) {
    Assert-PlainLocation $Path
    if ((Get-Item -LiteralPath $Path).Length -gt 16MB) { throw "Oversized update evidence: $Path" }
    Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -DateKind String -NoEnumerate
}
function Get-Sha([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Assert-BuildIdentity($Report, [string]$Status) {
    if ($Report.status -cne $Status -or $Report.build_id -cne $BuildId -or $Report.source_snapshot_id -cne $sourceId) {
        throw "Evidence requires $Status for build $BuildId and source $sourceId."
    }
}
function Assert-ReleaseTime($Manifest, [DateTimeOffset]$Now) {
    $issued = [DateTimeOffset]::Parse($Manifest.issuedAtUtc, [cultureinfo]::InvariantCulture)
    $expires = [DateTimeOffset]::Parse($Manifest.expiresAtUtc, [cultureinfo]::InvariantCulture)
    if ($Now.Year -lt 2024 -or $issued.Offset -ne [TimeSpan]::Zero -or $expires.Offset -ne [TimeSpan]::Zero -or
        $issued -gt $Now.AddMinutes(5) -or $expires -le $Now -or ($expires - $issued) -ne [TimeSpan]::FromDays(7)) {
        throw 'Prepared update has expired, is future-dated, or differs from the seven-day Prepare-ProductionUpdate validity window.'
    }
}
function Assert-UpgradeOrder($Manifest, $SemVerType, [string]$InstalledVersion, [long]$HighestSequence, [string]$InstalledBuildId) {
    $target = $SemVerType::Parse($Manifest.version)
    $comparison = $target.CompareTo($SemVerType::Parse($InstalledVersion))
    if ($comparison -lt 0 -or $Manifest.sequence -isnot [long] -or $Manifest.sequence -lt 1 -or
        $Manifest.securitySequence -isnot [long] -or $Manifest.securitySequence -le $PreviousSecuritySequence -or
        $Manifest.channel -cnotin @(('v'+$Manifest.version),('metav'+$Manifest.version)) -or
        $Manifest.releaseTag -cne $Manifest.channel -or ($comparison -eq 0 -and $Manifest.sequence -le $HighestSequence)) {
        throw 'Release identity, product revision or signed security counter is invalid.'
    }
    if ($InstalledBuildId -and $Manifest.buildId -ceq $InstalledBuildId) { throw 'The target must be a different product build.' }
    if ($comparison -eq 0 -and ($Manifest.version -cne $InstalledVersion -or $HighestSequence -le 0 -or -not $InstalledBuildId -or
        $SemVerType::Parse($Manifest.minimumUpdaterVersion).CompareTo($SemVerType::Parse('1.2.0')) -lt 0)) {
        throw 'A same-version update requires the exact version, previous build ID, positive previous sequence and revision-aware updater.'
    }
}
function Get-PublicJson([string]$Url) {
    # Construct all URLs locally. Do not follow redirects or use tokens, cookies
    # or default credentials, including when this runs in a publisher session.
    $handler = [Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect = $false
    $handler.UseCookies = $false
    $handler.UseDefaultCredentials = $false
    $http = [Net.Http.HttpClient]::new($handler)
    try {
        $http.Timeout = [TimeSpan]::FromSeconds(30)
        $http.MaxResponseContentBufferSize = 4MB
        $http.DefaultRequestHeaders.UserAgent.ParseAdd('AutumnOS-Release-Verification/1.0')
        $http.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')
        $http.DefaultRequestHeaders.Add('X-GitHub-Api-Version', '2022-11-28')
        $http.DefaultRequestHeaders.Add('Cache-Control','no-cache')
        $body = $http.GetStringAsync($Url).GetAwaiter().GetResult()
        $body | ConvertFrom-Json -DateKind String
    } finally { $http.Dispose() }
}
function Assert-PublishedAsset($Asset, $Payload, [string]$Tag, [long]$Id) {
    $download = "$webRelease/download/$([Uri]::EscapeDataString($Tag))/$([Uri]::EscapeDataString($Payload.asset))"
    if ($Asset.id -isnot [long] -or $Asset.id -le 0 -or $Asset.id -ne $Id -or $Asset.name -cne $Payload.asset -or
        $Asset.state -cne 'uploaded' -or $Asset.size -isnot [long] -or $Asset.size -ne $Payload.bytes -or
        $Asset.digest -cne ('sha256:' + $Payload.sha256) -or $Asset.url -cne "$releaseApi/assets/$Id" -or
        ($Asset.browser_download_url -creplace '%2[bB]|\+','%2B') -cne $download) { throw 'GitHub asset ID, URL, state, size, or server SHA-256 differs from the frozen payload.' }
}
function Get-ReleaseBinding($Manifest) {
    $tag = [Uri]::EscapeDataString($ReleaseTag)
    $release = Get-PublicJson "$releaseApi/tags/$tag"
    if ($release.id -isnot [long] -or $release.id -le 0 -or $release.tag_name -cne $ReleaseTag -or
        $release.url -cne "$releaseApi/$($release.id)" -or ($release.html_url -creplace '%2[bB]|\+','%2B') -cne "$webRelease/tag/$tag" -or
        $release.draft -isnot [bool] -or $release.draft -or $release.prerelease -isnot [bool] -or
        $release.prerelease -ne ($Manifest.channel -ceq ('metav'+$Manifest.version)) -or [string]::IsNullOrWhiteSpace($release.published_at) -or
        [DateTimeOffset]::Parse($release.published_at, [cultureinfo]::InvariantCulture) -gt [DateTimeOffset]::UtcNow.AddMinutes(5)) {
        throw 'The requested official release must be published with the exact tag and channel.'
    }
    $matches = @($release.assets | Where-Object name -IEQ $Manifest.payload.asset)
    if ($matches.Count -ne 1) { throw 'Exactly one matching uploaded payload is required in the public release.' }
    $asset = $matches[0]
    Assert-PublishedAsset $asset $Manifest.payload $ReleaseTag $asset.id
    $direct = Get-PublicJson "$releaseApi/assets/$($asset.id)"
    Assert-PublishedAsset $direct $Manifest.payload $ReleaseTag $asset.id
    return [pscustomobject]@{release_id=$release.id; asset_id=$asset.id}
}
function Invoke-ReleaseTool([string[]]$Arguments) {
    $output = & $Dotnet $cli @Arguments 2>&1
    if ($LASTEXITCODE -ne 0) {
        $code = ($output -join ' ').Trim()
        if ($code -cnotmatch '^UPDATE_[A-Z0-9_]+(?:: [A-Za-z.]+)?$') { $code = "exit $LASTEXITCODE" }
        throw "Update CLI $($Arguments[0]) failed: $code"
    }
    if ($Arguments[0] -cne 'sign-production') { return ($output -join "`n" | ConvertFrom-Json -DateKind String) }
}

$evidence = Join-Path $ProjectRoot "artifacts/builds/$BuildId"
$prepared = Join-Path $ProjectRoot "artifacts/update-preparation/$BuildId"
$build = Read-Record (Join-Path $evidence 'build-result.json')
$sourceId = $build.source_snapshot_id
Assert-ProductionUpdateBuild $build
Assert-BuildIdentity $build 'passed'
if ($sourceId -cnotmatch '^sha256:[0-9a-f]{64}$' -or $build.rid -cne 'win-x64' -or $build.configuration -cne 'Release' -or
    $build.release_label -cne ('v'+$build.version) -or $build.release_tag -cne $ReleaseTag -or $build.channel -cne $ReleaseTag) { throw 'Expected an ordinary win-x64 Release build with this release identity.' }
$snapshot = Read-Record (Join-Path $evidence 'source-snapshot.json')
$canonical = ($snapshot.files | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n"
$computedSnapshot = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
if ($snapshot.source_snapshot_id -cne $sourceId -or "sha256:$computedSnapshot" -cne $sourceId) { throw 'Build source snapshot evidence changed.' }
$testNames = @('tests','sdk-tests','machine-contracts','sample-tests','chemleap-model-tests','native-client-tests','server-http-tests','t04-tools-tests','production-update-tools')
$reports = @{}
foreach ($name in $testNames) {
    $r = Read-Record (Join-Path $evidence ($name+'.json'))
    Assert-BuildIdentity $r 'passed'
    foreach ($count in @('exit_code','failed')) {
        if ($r.PSObject.Properties[$count] -and $r.$count -ne 0) { throw "Failing test evidence in $name." }
    }
    if ($r.PSObject.Properties['results'] -and ($r.total -lt 1 -or $r.results.Count -ne $r.total -or
        @($r.results | Where-Object status -CNE 'passed').Count)) { throw "Incomplete result set in $name." }
    $reports[$name]=$r
}
if (@($reports['tests'].filters).Count -or @($reports['tests'].filter).Count) { throw 'Full unfiltered product tests are required.' }
Assert-BuildIdentity (Read-Record (Join-Path $evidence 'package-prepared.json')) 'prepared'
$prep = Read-Record (Join-Path $prepared 'preparation.json')
Assert-BuildIdentity $prep 'prepared_unbound_unsigned'
if ($prep.tag -cne $ReleaseTag -or $prep.payload_verification -cne 'passed' -or $prep.delivery_file_table_verification -cne 'passed' -or
    $prep.release_id -cne 'not_assigned' -or $prep.asset_id -cne 'not_assigned' -or $prep.signature -cne 'not_created' -or
    $build.update_sequence -isnot [long] -or $build.update_sequence -le 0 -or $build.update_sequence -ne $prep.sequence -or
    $build.security_sequence -isnot [long] -or $build.security_sequence -ne $prep.security_sequence) {
    throw 'A complete unbound Prepare-ProductionUpdate report is required.'
}
$trust = Join-Path $ProjectRoot 'build/update-trust.production.json'
$draftPath = Join-Path $prepared 'autumn.update.draft.json'
$cliPrefix = 'tools/AutumnOS.Update.Cli/bin/x64/Release/net10.0/win-x64/'
$cliDirectory = Join-Path $ProjectRoot $cliPrefix
$cli = Join-Path $cliDirectory 'AutumnOS.Update.Cli.dll'
$held = [Collections.Generic.List[IO.FileStream]]::new()
function Hold-Input([string]$Path) {
    Assert-PlainLocation $Path
    $held.Add([IO.File]::Open($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read))
}
try {
    Hold-Input $trust
    Hold-Input $draftPath
    $trustHash = Get-Sha $trust
    $public = Read-Record $trust
    $rootInput = @($build.build_properties | Where-Object { $_ -clike '-p:ProductionUpdateTrustFile=*' })
    $frozen = @($snapshot.files | Where-Object path -CEQ 'build/update-trust.production.json')
    if ($rootInput.Count -ne 1 -or $frozen.Count -ne 1 -or $frozen[0].sha256 -cne $trustHash -or
        $prep.production_trust_sha256 -cne $trustHash -or $public.purpose -cne 'production' -or
        @($public.keys).Count -ne 1 -or $public.keys[0].revoked -or $public.keys[0].keyId -cne $prep.production_public_key_id) {
        throw 'Prepared, current and frozen production public roots must match exactly.'
    }
    $buildTrust = $rootInput[0].Substring('-p:ProductionUpdateTrustFile='.Length)
    Assert-PlainLocation $buildTrust
    if ((Get-Sha $buildTrust) -cne $trustHash) { throw 'Build public trust input changed.' }

    $tools = $reports['production-update-tools'].tool_assemblies
    $tested = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($binary in $tools) {
        if ($binary.path -cnotmatch ('^'+[regex]::Escape($cliPrefix)+'AutumnOS\.[A-Za-z.]+\.dll$') -or
            -not $tested.TryAdd($binary.path,$binary.sha256)) { throw 'Invalid or duplicate tested CLI assembly path.' }
    }
    $policyName = 'AutumnOS.Update.dll'
    $storeName = 'AutumnOS.Store.dll'
    $policy = $null
    $semVer = $null
    $actual = @(Get-ChildItem -LiteralPath $cliDirectory -File -Filter 'AutumnOS*.dll')
    if ($actual.Count -ne $tested.Count -or -not $tested.ContainsKey($cliPrefix+'AutumnOS.Update.Cli.dll')) { throw 'Tested CLI assembly inventory changed.' }
    foreach ($item in $actual) {
        Hold-Input $item.FullName
        if (-not $tested.ContainsKey($cliPrefix+$item.Name) -or (Get-Sha $item.FullName) -cne $tested[$cliPrefix+$item.Name]) {
            throw "CLI binary differs from the tested copy: $($item.Name)"
        }
        $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($item.FullName))
        $identity = @{}
        foreach ($attribute in $assembly.GetCustomAttributesData()) {
            if ($attribute.AttributeType.Name -ceq 'AssemblyMetadataAttribute') {
                $identity[$attribute.ConstructorArguments[0].Value] = $attribute.ConstructorArguments[1].Value
            }
        }
        if ($identity.BuildId -cne $BuildId -or $identity.SourceSnapshotId -cne $sourceId -or $identity.UpdateSequence -cne [string]$build.update_sequence -or $identity.SecuritySequence -cne [string]$build.security_sequence) { throw 'Tested binary has a different embedded build identity.' }
        if ($item.Name -ceq $policyName) { $policy = $assembly }
        if ($item.Name -ceq $storeName) { $semVer = $assembly.GetType('AutumnOS.Store.SemanticVersion',$true) }
    }
    $artifactHashes = Read-Record (Join-Path $evidence 'artifact-hashes.json')
    $clientPolicy = ((Split-Path $build.output_path -Parent).Replace('\','/') + '/' + $policyName)
    $tracked = @($artifactHashes | Where-Object path -CEQ $clientPolicy)
    if (-not $policy -or -not $semVer -or $tracked.Count -ne 1 -or $tracked[0].sha256 -cne $tested[$cliPrefix+$policyName]) { throw 'Tested update policy differs from the built client.' }
    $embedded = $policy.GetManifestResourceStream('AutumnOS.Update.ProductionTrust.json')
    if (-not $embedded) { throw 'Production trust missing from the tested update policy.' }
    try {
        if ([Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($embedded)) -cne $trustHash -or
            $policy.GetManifestResourceNames() -ccontains 'AutumnOS.Update.TestTrust.json') { throw 'Embedded production trust mismatch.' }
    } finally { $embedded.Dispose() }
    $draft = Read-Record $draftPath
    $payloadName = 'AutumnOS-'+$build.version.Replace('+','_')+'-win-x64-payload.zip'
    if ($payloadName -cnotmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,150}\.zip$') { throw 'Unsafe product version or asset name.' }
    $payloadPath = Join-Path $prepared $payloadName
    Hold-Input $payloadPath
    if ($prep.draft -ine $draftPath -or $prep.payload -ine $payloadPath -or $draft.repository -cne $repository -or
        $draft.buildId -cne $BuildId -or $draft.releaseTag -cne $ReleaseTag -or $draft.version -cne $build.version -or
        $draft.schemaVersion -ne 2 -or $draft.channel -cne $prep.channel -or $draft.sequence -ne $prep.sequence -or
        $draft.securitySequence -ne $prep.security_sequence -or $draft.keyId -cne $public.keys[0].keyId -or
        $draft.trustRootVersion -ne $public.version -or $draft.payload.releaseId -ne 0 -or $draft.payload.assetId -ne 0 -or
        $draft.payload.asset -cne $payloadName -or $draft.payload.sha256 -cne $prep.payload_sha256 -or
        $draft.payload.bytes -ne (Get-Item -LiteralPath $payloadPath).Length -or $draft.payload.sha256 -cne (Get-Sha $payloadPath) -or
        @($draft.removeFiles).Count -ne 0) { throw 'The draft or payload differs from production preparation.' }
    $updaterVersion = $policy.GetType('AutumnOS.Update.UpdateService',$true).GetField('UpdaterVersion').GetRawConstantValue()
    if ($draft.minimumUpdaterVersion -cne $updaterVersion) { throw 'Draft minimum updater differs from the tested preparation policy.' }
    Assert-ReleaseTime $draft ([DateTimeOffset]::UtcNow)
    Assert-UpgradeOrder $draft $semVer $PreviousVersion $PreviousSequence $PreviousBuildId
    $exclusion = $policy.GetType('AutumnOS.Update.UpdatePaths',$true).GetMethod('IsStableOrPrivate')
    $delivery = Read-Record (Join-Path $evidence 'delivery-hashes.json')
    $managed = @($delivery | Where-Object { -not $exclusion.Invoke($null,@([string]$_.path)) })
    if ($managed.Count -ne $prep.managed_files) { throw 'Prepared file count mismatch.' }
    Assert-UpdatePreparationFileTable -ExpectedFiles $managed -ActualFiles $draft.files
    $remote = Get-ReleaseBinding $draft

    $base = Join-Path $ProjectRoot "artifacts/update-completion/$BuildId"
    Assert-PlainLocation $base
    [void][IO.Directory]::CreateDirectory($base)
    $final = Join-Path $base 'verified'
    $sign = $PSCmdlet.ParameterSetName -ceq 'Sign'
    if ($sign -and (Test-Path -LiteralPath $final)) { throw "Existing verified output is preserved: $final" }
    $attempt = Join-Path $base ('attempt-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $attempt -ErrorAction Stop | Out-Null
    $manifest = Join-Path $attempt 'bound-manifest.tmp'
    $signature = Join-Path $attempt 'candidate-signature.tmp'
    $bound = Invoke-ReleaseTool @('bind-release','--manifest',$draftPath,'--payload',$payloadPath,'--output',$manifest,
        '--release-id',[string]$remote.release_id,'--asset-id',[string]$remote.asset_id,'--release-tag',$ReleaseTag)
    Hold-Input $manifest
    $manifestHandle = $held[$held.Count-1]
    if ($bound.status -cne 'bound' -or $bound.ReleaseId -ne $remote.release_id -or $bound.AssetId -ne $remote.asset_id -or
        $bound.ReleaseTag -cne $ReleaseTag -or $bound.Sha256 -cne $draft.payload.sha256) { throw 'Bound metadata differs from the checked release asset.' }
    $result = [ordered]@{status='inputs_verified_unsigned';build_id=$BuildId;source_snapshot_id=$sourceId;tag=$ReleaseTag;version=$draft.version
        sequence=$draft.sequence;security_sequence=$draft.securitySequence;previous_version=$PreviousVersion;previous_sequence=$PreviousSequence;previous_security_sequence=$PreviousSecuritySequence;previous_build_id=$PreviousBuildId;release_id=$remote.release_id;asset_id=$remote.asset_id
        payload_sha256=$draft.payload.sha256;production_trust_sha256=$trustHash;test_reports=$testNames;manifest_sha256=(Get-Sha $manifest);uploaded=$false}
    if ($sign) {
        Invoke-ReleaseTool @('sign-production','--manifest',$manifest,'--private-directory',$PrivateDirectory,'--trust',$trust,'--output',$signature)
        Hold-Input $signature
        $signatureHandle = $held[$held.Count-1]
        $verified = Invoke-ReleaseTool @('verify','--manifest',$manifest,'--signature',$signature,'--trust',$trust,'--payload',$payloadPath,
            '--minimum-sequence',[string]($PreviousSecuritySequence+1))
        if ($verified.status -cne 'passed' -or $verified.trust_purpose -cne 'production' -or $verified.BuildId -cne $BuildId -or
            $verified.ReleaseTag -cne $ReleaseTag -or $verified.ReleaseId -ne $remote.release_id -or $verified.AssetId -ne $remote.asset_id -or
            $verified.Version -cne $draft.version) { throw 'Final production signature verification or binding failed.' }
        $confirm = Get-ReleaseBinding $draft
        if ($confirm.release_id -ne $remote.release_id -or $confirm.asset_id -ne $remote.asset_id) { throw 'The public release or asset changed during signing.' }
        Assert-ReleaseTime $draft ([DateTimeOffset]::UtcNow)
        $result.status = 'signed_verified_locally'
        $result.signature_sha256 = Get-Sha $signature
        $manifestHandle.Dispose()
        $signatureHandle.Dispose()
        [IO.File]::Move($manifest,(Join-Path $attempt 'autumn.update.json'))
        [IO.File]::Move($signature,(Join-Path $attempt 'autumn.update.sig'))
    }
    $result.checked_utc = [DateTimeOffset]::UtcNow.ToString('o')
    $result | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $attempt 'verification.json') -Encoding utf8NoBOM
    if ($sign) { [IO.Directory]::Move($attempt,$final); $result.directory=$final } else { $result.directory=$attempt }
    [pscustomobject]$result
} finally { foreach ($handle in $held) { $handle.Dispose() } }
