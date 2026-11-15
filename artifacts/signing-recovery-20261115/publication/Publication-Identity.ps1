$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$buildId='T06-20261115-signing-recovery-prod-01'
$snapshotId='sha256:a7699603f7e068739b4adf5a2db7508cd3bedc247c36086315604fe7c6d15a2e'
$releaseTag='plus-v1.0.0-20261115'
$keyId='autumnos-release-20261115-v2'
$trustSha256='7609941f0705632b4353d350282388b18a2901a18c03a95d2677de82b40180dc'
$buildRoot=Join-Path $workspace "artifacts/builds/$buildId"
$utf8=[Text.UTF8Encoding]::new($false,$true)
. (Join-Path $workspace 'scripts/UpdatePreparationFiles.ps1')
function Assert-PublicationPlain([string]$path){
    for($cursor=[IO.Path]::GetFullPath($path);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Redirected publication input/output forbidden.'}
    }
}
function Get-PublicationHash([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
function Read-PublicationBuild {
    $p=Join-Path $buildRoot 'build-result.json';Assert-PublicationPlain $p
    $b=Get-Content -LiteralPath $p -Raw|ConvertFrom-Json
    Assert-ProductionUpdateBuild -Build $b
    if($b.status -cne 'passed' -or $b.build_id -cne $buildId -or $b.source_snapshot_id -cne $snapshotId -or
       $b.version -cne '1.0.0' -or $b.release_label -cne $releaseTag -or $b.temporary_platform_acceptance -or $b.test_build){throw 'Expected ordinary v2 production build required.'}
    $props=@($b.build_properties)
    if(@($props|Where-Object{$_ -like '-p:TestUpdate*' -or $_ -eq '-p:AutumnPlatformAcceptance=true'}).Count){throw 'Test or temporary build forbidden.'}
    $root=Join-Path $workspace 'build/update-trust.production.json';Assert-PublicationPlain $root
    if((Get-PublicationHash $root) -cne $trustSha256){throw 'New production public root bytes differ.'}
    $trust=Get-Content -LiteralPath $root -Raw|ConvertFrom-Json
    if($trust.purpose -cne 'production' -or $trust.version -ne 1 -or @($trust.keys).Count -ne 1 -or $trust.keys[0].keyId -cne $keyId -or $trust.keys[0].revoked){throw 'New production public identity differs.'}
    return $b
}
function Read-PublicationAutomatic {
    $result=@(foreach($name in @('tests','sdk-tests','machine-contracts','sample-tests','chemleap-model-tests','native-client-tests','server-http-tests','t04-tools-tests','production-update-tools')){
        $p=Join-Path $buildRoot ($name+'.json');Assert-PublicationPlain $p
        $j=Get-Content -LiteralPath $p -Raw|ConvertFrom-Json
        if($j.status -cne 'passed' -or $j.build_id -cne $buildId -or $j.source_snapshot_id -cne $snapshotId){throw 'Required current automatic report differs.'}
        [pscustomobject]@{name=$name;status=$j.status;total=$(if($j.PSObject.Properties['total']){$j.total}else{$null});failed=$(if($j.PSObject.Properties['failed']){$j.failed}else{$null});original_report_sha256=(Get-PublicationHash $p)}
    })
    return $result
}
function Read-PublicationPrepared {
    $p=Join-Path $buildRoot 'package-prepared.json';Assert-PublicationPlain $p
    $j=Get-Content -LiteralPath $p -Raw|ConvertFrom-Json
    if($j.status -cne 'prepared' -or $j.build_id -cne $buildId -or $j.source_snapshot_id -cne $snapshotId -or $j.test_build -or $j.temporary_platform_acceptance){throw 'Expected new prepared package required.'}
    return $j
}
function Read-PublicationSourceBundle {
    $manifestPath=Join-Path $buildRoot 'source-snapshot.json'
    $archive=Join-Path $buildRoot 'source-bytes.zip'
    $receiptPath=$archive+'.json'
    foreach($p in @($manifestPath,$archive,$receiptPath)){Assert-PublicationPlain $p}
    $manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
    $canonical=($manifest.files|ForEach-Object{"$($_.sha256)  $($_.path)"}) -join "`n"
    $actual='sha256:'+[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
    $receipt=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json
    if($actual -cne $snapshotId -or $manifest.source_snapshot_id -cne $snapshotId -or $manifest.files.Count -ne 600 -or
       $receipt.status -cne 'passed' -or $receipt.build_id -cne $buildId -or $receipt.source_snapshot_id -cne $snapshotId -or $receipt.files -ne 600 -or (Get-PublicationHash $archive) -cne $receipt.zip_sha256){throw 'New frozen source bundle differs.'}
    return [pscustomobject]@{manifest=$manifest;manifest_path=$manifestPath;zip=$archive;zip_sha256=$receipt.zip_sha256}
}
