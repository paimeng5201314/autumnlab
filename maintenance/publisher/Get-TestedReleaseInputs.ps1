#requires -Version 7.6
# Frozen build, tests, ZIP and CLI checks shared in policy with Complete-ProductionUpdate.ps1.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Workspace,[Parameter(Mandatory)][ValidatePattern('^T06-[A-Za-z0-9_-]+$')][string]$BuildId,
      [ValidateSet('plus-v1.0.0-20261115')][string]$ReleaseTag='plus-v1.0.0-20261115',[ValidateSet('1.0.0')][string]$PreviousVersion='1.0.0',
      [Parameter(Mandatory)][ValidatePattern('^T06-[A-Za-z0-9_-]+$')][string]$PreviousBuildId,
      [Parameter(Mandatory)][ValidateRange(1,9223372036854775806)][long]$PreviousSequence,[Parameter(Mandatory)][ValidateSet('Local','Bind','Readback')][string]$Mode,
      [long]$PayloadAssetId,[string]$PrivateDirectory,[string]$GhPath,[string]$ExecutablePath,[string]$PreviousBuildReceipt)
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'GitHubPublishTransport.ps1')
. (Join-Path $PSScriptRoot 'PublishReleaseAssets.ps1')
. (Get-PublishPath (Join-Path $Workspace 'scripts/Common.ps1'))
. (Get-PublishPath (Join-Path $Workspace 'scripts/UpdatePreparationFiles.ps1'))
$repository = $DistributionRepo
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
function Assert-UpgradeOrder($Manifest, $SemVerType, [string]$InstalledVersion, [long]$HighestSequence) {
    $target = $SemVerType::Parse($Manifest.version)
    if ($Manifest.version -cne '1.0.0' -or $target.CompareTo($SemVerType::Parse($InstalledVersion)) -ne 0 -or
        $Manifest.buildId -ceq $PreviousBuildId -or $Manifest.sequence -isnot [long] -or $Manifest.sequence -ne $build.update_sequence -or
        $HighestSequence -lt 1 -or $Manifest.sequence -le $HighestSequence -or $Manifest.channel -cne 'plus' -or $target.IsPrerelease -or
        $SemVerType::Parse($Manifest.minimumUpdaterVersion).CompareTo($SemVerType::Parse('1.2.0')) -lt 0) {
        throw 'Same-version maintenance requires a different build and a strictly increasing, compiled production sequence.'
    }
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
$priorPath=if ($PreviousBuildReceipt) {Get-PublishPath $PreviousBuildReceipt} else {Get-PublishPath (Join-Path $ProjectRoot "artifacts/builds/$PreviousBuildId/build-result.json")}
$priorBuild=Read-Record $priorPath
Assert-ProductionUpdateBuild $priorBuild
$priorSnapshot=Read-Record (Join-Path ([IO.Path]::GetDirectoryName($priorPath)) 'source-snapshot.json')
$priorRoot=@($priorSnapshot.files|Where-Object path -CEQ 'build/update-trust.production.json')
if ($priorBuild.build_id -cne $PreviousBuildId -or $priorBuild.build_id -ceq $BuildId -or $priorBuild.version -cne '1.0.0' -or
    $priorBuild.update_sequence -ne $PreviousSequence -or $priorBuild.release_label -cne $FixedTag -or $priorBuild.rid -cne 'win-x64' -or
    $priorBuild.configuration -cne 'Release' -or $priorBuild.source_snapshot_id -cne $priorSnapshot.source_snapshot_id -or
    $priorRoot.Count -ne 1 -or $priorRoot[0].sha256 -cne 'd7696acf884dcd683fada3b0a4134ad876bd7aeec7e8fa2a0049bed5cb2a42cb') {throw 'Prior same-version baseline or production trust differs from the frozen receipt.'}
if ($sourceId -cnotmatch '^sha256:[0-9a-f]{64}$' -or $build.rid -cne 'win-x64' -or $build.configuration -cne 'Release' -or
    $build.release_label -cne $FixedTag -or $build.version -cne '1.0.0') { throw 'Expected an ordinary 1.0.0 win-x64 Release build with the original label.' }
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
    $prep.release_id -cne 'not_assigned' -or $prep.asset_id -cne 'not_assigned' -or $prep.signature -cne 'not_created') {
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
        if ($identity.BuildId -cne $BuildId -or $identity.SourceSnapshotId -cne $sourceId -or $identity.UpdateSequence -cne [string]$build.update_sequence) { throw 'Tested binary has a different embedded build identity or revision.' }
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
        $draft.channel -cne $prep.channel -or $draft.sequence -ne $prep.sequence -or $draft.keyId -cne $public.keys[0].keyId -or
        $draft.trustRootVersion -ne $public.version -or $draft.payload.releaseId -ne 0 -or $draft.payload.assetId -ne 0 -or
        $draft.payload.asset -cne $payloadName -or $draft.payload.sha256 -cne $prep.payload_sha256 -or
        $draft.payload.bytes -ne (Get-Item -LiteralPath $payloadPath).Length -or $draft.payload.sha256 -cne (Get-Sha $payloadPath) -or
        @($draft.removeFiles).Count -ne 0) { throw 'The draft or payload differs from production preparation.' }
    $updaterVersion = $policy.GetType('AutumnOS.Update.UpdateService',$true).GetField('UpdaterVersion').GetRawConstantValue()
    if ($draft.minimumUpdaterVersion -cne $updaterVersion) { throw 'Draft minimum updater differs from the tested preparation policy.' }
    Assert-ReleaseTime $draft ([DateTimeOffset]::UtcNow)
    Assert-UpgradeOrder $draft $semVer $PreviousVersion $PreviousSequence
    $exclusion = $policy.GetType('AutumnOS.Update.UpdatePaths',$true).GetMethod('IsStableOrPrivate')
    $delivery = Read-Record (Join-Path $evidence 'delivery-hashes.json')
    $managed = @($delivery | Where-Object { -not $exclusion.Invoke($null,@([string]$_.path)) })
    if ($managed.Count -ne $prep.managed_files) { throw 'Prepared file count mismatch.' }
    Assert-UpdatePreparationFileTable -ExpectedFiles $managed -ActualFiles $draft.files
    $outer = Read-Record (Join-Path $evidence 'package-single-file.json')
    Assert-BuildIdentity $outer 'prepared'
    if ($outer.update_sequence -ne $draft.sequence) {throw 'Full EXE receipt sequence differs from the prepared update.'}
    $exe=Get-FrozenReleaseExecutable $ProjectRoot $BuildId $outer $draft.version $ReleaseTag $ExecutablePath
    if ($public.keys[0].keyId -cne 'autumnos-release-20261119-v3' -or $trustHash -cne 'd7696acf884dcd683fada3b0a4134ad876bd7aeec7e8fa2a0049bed5cb2a42cb') {
        throw 'This maintenance release requires the verified production v3 root.'
    }
    $local = [ordered]@{status='local_preflight_passed';build_id=$BuildId;snapshot=$sourceId;version=$draft.version;sequence=$draft.sequence;channel=$draft.channel;
        exe=[pscustomobject]@{name='AutumnOS.exe';path=$exe;sha=$outer.executable_sha256;bytes=[long]$outer.bytes};
        payload=[pscustomobject]@{name=$payloadName;path=$payloadPath;sha=$draft.payload.sha256;bytes=[long]$draft.payload.bytes}}
    if ($Mode -ceq 'Local') {
        if ($PrivateDirectory) {
            $proofPath=Join-Path $evidence ('publisher-public-key-'+[Guid]::NewGuid().ToString('N')+'.json')
            $null=Invoke-ReleaseTool @('export-production-public','--private-directory',(Get-PublishPath $PrivateDirectory),'--public-output',$proofPath)
            if ((Get-Sha $proofPath) -cne $trustHash) { throw 'Working signer does not match the frozen production trust.' }
        }
        return [pscustomobject]$local
    }
    $ReleaseId=$FixedReleaseId
    Assert-RepositoryIdentity $true
    Assert-DistributionTag
    $remote=Get-ExactRelease $ReleaseId $true
    $asset=Get-AssetForBinding $remote $local.payload $PayloadAssetId $true
    $base=Join-Path $ProjectRoot "artifacts/update-completion/$BuildId"
    Assert-PlainLocation $base
    [void][IO.Directory]::CreateDirectory($base)
    $final=Join-Path $base "github-$ReleaseId-$($asset.id)"
    Assert-PlainLocation $final
    $attempt=Join-Path $base ('publisher-attempt-'+[Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($attempt)
    $boundFile=Join-Path $attempt 'autumn.update.json'
    $bound=Invoke-ReleaseTool @('bind-release','--manifest',$draftPath,'--payload',$payloadPath,'--output',$boundFile,
        '--release-id',[string]$ReleaseId,'--asset-id',[string]$asset.id,'--release-tag',$ReleaseTag)
    if ($bound.status -cne 'bound' -or $bound.ReleaseId -ne $ReleaseId -or $bound.AssetId -ne $asset.id -or
        $bound.ReleaseTag -cne $ReleaseTag -or $bound.Sha256 -cne $draft.payload.sha256) { throw 'Bound release identity differs.' }
    $manifest=Join-Path $final 'autumn.update.json'
    $signature=Join-Path $final 'autumn.update.sig'
    if ([IO.Directory]::Exists($final)) {
        Hold-Input $manifest
        if ((Get-Sha $manifest) -cne (Get-Sha $boundFile)) { throw 'Retained manifest differs from verified build and current asset ID.' }
    } else {
        if ($Mode -ceq 'Readback') {throw 'Published readback requires the retained, originally signed local manifest and signature.'}
        if (-not $PrivateDirectory) { throw 'Local signer is required.' }
        $manifest=$boundFile
        $signature=Join-Path $attempt 'autumn.update.sig'
        Hold-Input $manifest
        Invoke-ReleaseTool @('sign-production','--manifest',$manifest,'--private-directory',(Get-PublishPath $PrivateDirectory),'--trust',$trust,'--output',$signature)
    }
    Hold-Input $signature
    $verified=Invoke-ReleaseTool @('verify','--manifest',$manifest,'--signature',$signature,'--trust',$trust,'--payload',$payloadPath,'--minimum-sequence',[string]($PreviousSequence+1))
    if ($verified.status -cne 'passed' -or $verified.trust_purpose -cne 'production' -or $verified.BuildId -cne $BuildId -or
        $verified.ReleaseTag -cne $ReleaseTag -or $verified.ReleaseId -ne $ReleaseId -or $verified.AssetId -ne $asset.id -or $verified.Version -cne $draft.version) {
        throw 'Offline release signature or ZIP verification failed.'
    }
    $v2=Get-PublishPath (Join-Path $Workspace 'docs/update-trust.legacy-20261115-v2.json')
    if ((Get-Sha $v2) -cne '7609941f0705632b4353d350282388b18a2901a18c03a95d2677de82b40180dc') {throw 'Unexpected historical trust document.'}
    $v2Key=[Security.Cryptography.RSA]::Create()
    try {
        $v2Key.ImportFromPem((Read-Record $v2).keys[0].publicKeyPem)
        if ($v2Key.VerifyData([IO.File]::ReadAllBytes($manifest),[Convert]::FromBase64String((Read-Record $signature).signature),
            [Security.Cryptography.HashAlgorithmName]::SHA256,[Security.Cryptography.RSASignaturePadding]::Pss)) { throw 'Historical v2 key accepted the candidate.' }
    } finally { $v2Key.Dispose() }
    $confirm=Get-AssetForBinding (Get-ExactRelease $ReleaseId $true) $local.payload $PayloadAssetId $true
    if ($confirm.id -ne $asset.id) { throw 'The payload ID changed while signing.' }
    Assert-ReleaseTime $draft ([DateTimeOffset]::UtcNow)
    $manifestHash=Get-Sha $manifest; $signatureHash=Get-Sha $signature
    if ($manifest -ceq $boundFile) {
        foreach ($stream in $held) { $stream.Dispose() }
        [IO.Directory]::Move($attempt,$final)
    }
    $local.status='signed_verified_locally'; $local.old_v2_rejected=$true
    $local.release_id=$ReleaseId; $local.asset_id=$asset.id
    $local.manifest=[pscustomobject]@{name='autumn.update.json';path=(Join-Path $final 'autumn.update.json');sha=$manifestHash;bytes=(Get-Item -LiteralPath (Join-Path $final 'autumn.update.json')).Length}
    $local.signature=[pscustomobject]@{name='autumn.update.sig';path=(Join-Path $final 'autumn.update.sig');sha=$signatureHash;bytes=(Get-Item -LiteralPath (Join-Path $final 'autumn.update.sig')).Length}
    return [pscustomobject]$local
} finally { foreach ($handle in $held) { $handle.Dispose() } }
