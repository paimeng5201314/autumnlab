[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildId,
    [Parameter(Mandatory)][string]$Channel,
    [Parameter(Mandatory)][string]$ReleaseTag,
    [Parameter(Mandatory)][ValidateRange(1,[long]::MaxValue)][long]$Sequence,[switch]$ShareVerifiedInputs)
. "$PSScriptRoot/Common.ps1"
. "$PSScriptRoot/UpdatePreparationFiles.ps1"
if($BuildId -cnotmatch '^v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)-[0-9]{8}$'){throw 'Expected vX.Y.Z-yyyyMMdd.'}
$evidence=Join-Path $ProjectRoot "artifacts/builds/$BuildId"
$build=Get-Content -LiteralPath (Join-Path $evidence 'build-result.json') -Raw|ConvertFrom-Json
Assert-ProductionUpdateBuild -Build $build
$package=Get-Content -LiteralPath (Join-Path $evidence 'package-prepared.json') -Raw|ConvertFrom-Json
if($build.update_sequence -isnot [long] -or $build.update_sequence -ne $Sequence -or $package.update_sequence -ne $Sequence -or
    $build.release_label -cne ('v'+$build.version) -or $ReleaseTag -cnotin @(('v'+$build.version),('metav'+$build.version)) -or
    $Channel -cne $ReleaseTag -or $build.channel -cne $Channel -or $build.release_tag -cne $ReleaseTag -or
    $build.security_sequence -isnot [long] -or $build.security_sequence -le 0 -or $package.security_sequence -ne $build.security_sequence){throw 'Release identity and both sequence fields must match the factory build and package.'}
$tests=Get-Content -LiteralPath (Join-Path $evidence 'tests.json') -Raw|ConvertFrom-Json
if($build.status -ne 'passed' -or $build.test_build -or $tests.status -ne 'passed' -or $package.status -cne 'prepared' -or
    $package.build_id -cne $BuildId -or $tests.build_id -cne $BuildId -or
    $package.source_snapshot_id -cne $build.source_snapshot_id -or $tests.source_snapshot_id -cne $build.source_snapshot_id){throw 'Matching ordinary build, tests and prepared package required.'}
$trustInputs=@($build.build_properties|Where-Object {$_ -like '-p:ProductionUpdateTrustFile=*'})
if($trustInputs.Count -ne 1){throw 'Exactly one production trust input required.'}
$trustFile=$trustInputs[0].Substring('-p:ProductionUpdateTrustFile='.Length)
$trust=Get-Content -LiteralPath $trustFile -Raw|ConvertFrom-Json
if($trust.purpose -cne 'production' -or @($trust.keys).Count -ne 1 -or $trust.keys[0].revoked){throw 'Current production signer required.'}
$output=Join-Path $ProjectRoot "artifacts/update-preparation/$BuildId"
if(Test-Path -LiteralPath $output){throw 'Previous preparation preserved; choose a new build.'}
$verification=Join-Path $evidence ('before-update-preparation-'+[Guid]::NewGuid().ToString('N'))
$snapshot=& "$PSScriptRoot/New-SourceSnapshot.ps1" -OutputDirectory $verification -AdditionalInputs @($build.additional_inputs)
if($snapshot -cne $build.source_snapshot_id){throw 'Source changed after build.'}
$source=Split-Path $package.executable -Parent
$receipt=Get-Content -LiteralPath (Join-Path $source 'autumn.install.json') -Raw|ConvertFrom-Json
if($receipt.sequence -isnot [long] -or $receipt.sequence -ne $Sequence -or $receipt.securitySequence -ne $build.security_sequence -or $receipt.buildId -cne $BuildId -or $receipt.version -cne $build.version){throw 'Installed build receipt must carry the exact production revision.'}
$files=@(Get-Content -LiteralPath (Join-Path $evidence 'delivery-hashes.json') -Raw|ConvertFrom-Json)
foreach($file in $files){
    if([IO.Path]::IsPathRooted($file.path) -or $file.path.Contains('\') -or $file.path.Contains(':') -or
        @($file.path.Split('/')|Where-Object {$_ -in @('','.','..') -or $_.EndsWith('.') -or $_.EndsWith(' ')}).Count){throw 'Invalid delivery path.'}
}
# Enumerate before copying, rejecting redirected directories before descending into them.
function Get-PlainDeliveryFiles([string]$Directory){
    foreach($item in Get-ChildItem -LiteralPath $Directory -Force){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Redirected package forbidden.'}
        if($item.PSIsContainer){Get-PlainDeliveryFiles $item.FullName}else{
            [pscustomobject]@{path=[IO.Path]::GetRelativePath($source,$item.FullName).Replace('\','/');bytes=$item.Length;
                sha256=(Get-FileHash -LiteralPath $item.FullName).Hash.ToLowerInvariant()}
        }
    }
}
for($cursor=[IO.Path]::GetFullPath($source);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
    if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Redirected package forbidden.'}
}
Assert-UpdatePreparationFileTable -ExpectedFiles $files -ActualFiles @(Get-PlainDeliveryFiles $source)
$staging=Join-Path $verification 'verified-payload-input'
New-Item -ItemType Directory -Path $staging|Out-Null
$readLeases=[Collections.Generic.List[IO.FileStream]]::new()
try {
foreach($file in $files){
    $path=[IO.Path]::GetFullPath((Join-Path $source $file.path))
    if(-not $path.StartsWith([IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($source))+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Source path escape.'}
    for($cursor=[IO.Path]::GetFullPath($path);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Redirected package forbidden.'}
    }
    if((Get-Item -LiteralPath $path).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $file.sha256){throw 'Prepared package changed.'}
    $target=[IO.Path]::GetFullPath((Join-Path $staging $file.path))
    if(-not $target.StartsWith([IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($staging))+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Staging path escape.'}
    New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent)|Out-Null
    if($ShareVerifiedInputs){
        Add-VerifiedStagingLink -Source $path -Target $target -Entry $file -ReadLeases $readLeases
    }else{
        Copy-Item -LiteralPath $path -Destination $target
    }
    if((Get-Item -LiteralPath $target).Length -ne $file.bytes -or (Get-FileHash -LiteralPath $target).Hash.ToLowerInvariant() -cne $file.sha256){throw 'Staged package changed.'}
}
Push-Location $ProjectRoot
try{
    Invoke-Dotnet -Arguments @('tools/AutumnOS.Update.Cli/bin/x64/Release/net10.0/win-x64/AutumnOS.Update.Cli.dll','prepare-draft',
        '--source',$staging,'--output',$output,'--version',$build.version,'--build-id',$BuildId,'--channel',$Channel,
        '--sequence',"$Sequence",'--security-sequence',"$($build.security_sequence)",'--key-id',$trust.keys[0].keyId,'--root-version',"$($trust.version)",'--release-tag',$ReleaseTag) -LogPath (Join-Path $verification 'prepare.log')
}finally{Pop-Location}
$draft=Get-Content -LiteralPath (Join-Path $output 'autumn.update.draft.json') -Raw|ConvertFrom-Json
if($draft.payload.releaseId -ne 0 -or $draft.payload.assetId -ne 0 -or (Test-Path -LiteralPath (Join-Path $output 'autumn.update.sig'))){throw 'Draft must not claim published or signed status.'}
$policyPath=Join-Path $ProjectRoot 'tools/AutumnOS.Update.Cli/bin/x64/Release/net10.0/win-x64/AutumnOS.Update.dll'
$trackedPolicy=@(Get-Content -LiteralPath (Join-Path $evidence 'artifact-hashes.json') -Raw|ConvertFrom-Json|Where-Object {$_.path -ceq ((Split-Path $build.output_path -Parent).Replace('\','/')+'/AutumnOS.Update.dll')})
$policyBytes=[IO.File]::ReadAllBytes($policyPath)
if($trackedPolicy.Count -ne 1 -or [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($policyBytes)) -cne $trackedPolicy[0].sha256){throw 'Update policy bytes differ from tracked build.'}
$updateAssembly=[Reflection.Assembly]::Load($policyBytes)
$metadata=@{}
foreach($attribute in $updateAssembly.GetCustomAttributesData()){
    if($attribute.AttributeType.Name -eq 'AssemblyMetadataAttribute'){$metadata[$attribute.ConstructorArguments[0].Value]=$attribute.ConstructorArguments[1].Value}
}
if($metadata.BuildId -cne $BuildId -or $metadata.SourceSnapshotId -cne $snapshot -or $metadata.UpdateSequence -cne [string]$Sequence -or $metadata.SecuritySequence -cne [string]$build.security_sequence){throw 'Update policy assembly identity/sequence mismatch.'}
$isExcluded=$updateAssembly.GetType('AutumnOS.Update.UpdatePaths',$true).GetMethod('IsStableOrPrivate')
$expectedManaged=@($files|Where-Object {-not $isExcluded.Invoke($null,@([string]$_.path))})
Assert-UpdatePreparationFileTable -ExpectedFiles $expectedManaged -ActualFiles @($draft.files)
$report=[ordered]@{status='prepared_unbound_unsigned';build_id=$BuildId;source_snapshot_id=$snapshot;channel=$Channel;tag=$ReleaseTag;sequence=$Sequence;security_sequence=$build.security_sequence;
    production_public_key_id=$trust.keys[0].keyId;production_trust_sha256=(Get-FileHash -LiteralPath $trustFile).Hash.ToLowerInvariant();
    draft=(Join-Path $output 'autumn.update.draft.json');payload=(Join-Path $output $draft.payload.asset);payload_sha256=$draft.payload.sha256;
    release_id='not_assigned';asset_id='not_assigned';signature='not_created';payload_verification='passed';delivery_file_table_verification='passed';managed_files=$expectedManaged.Count;
    GUI_upgrade='not_run';public_release=$false;uploaded=$false;private_key_included=$false;
    next_step='After separately authorized release upload, bind real release/asset IDs, sign-production, verify and publish metadata. Do not upload the draft as autumn.update.json.'}
$report.shared_verified_staging_inputs=[bool]$ShareVerifiedInputs
$report.github_release = & "$PSScriptRoot/Get-GitHubReleaseMetadata.ps1" -Version $build.version -Channel $Channel
$report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'preparation.json') -Encoding utf8NoBOM
$report|ConvertTo-Json -Depth 6
} finally {foreach($lease in $readLeases){$lease.Dispose()}}
