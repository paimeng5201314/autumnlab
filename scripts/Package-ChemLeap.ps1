[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory)
. "$PSScriptRoot/Common.ps1"
$ErrorActionPreference='Stop'
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Choose a new output directory; existing publication materials are preserved.'}
$project=Join-Path $ProjectRoot 'samples/chemleap'
$sdkSource=Join-Path $ProjectRoot 'sdk/autumn-sdk.js'
if((Get-FileHash -LiteralPath (Join-Path $project 'autumn-sdk.js')).Hash -ne (Get-FileHash -LiteralPath $sdkSource).Hash){throw 'Game SDK copy differs from the current fixed SDK.'}
$cli=Join-Path $ProjectRoot 'tools/AutumnOS.Developer.Cli/bin/x64/Release/net10.0/win-x64/AutumnOS.Developer.Cli.dll'
if(-not(Test-Path -LiteralPath $cli)){throw 'Build the local solution before packaging ChemLeap.'}
New-Item -ItemType Directory -Path $OutputDirectory|Out-Null
$inventory=@(Get-ChildItem -LiteralPath $project -Recurse -File|ForEach-Object {
    if($_.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Redirected game sources are not accepted.'}
    [ordered]@{path=[IO.Path]::GetRelativePath($project,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
}|Sort-Object path)
$canonical=($inventory|ForEach-Object {"$($_.sha256)  $($_.path)"}) -join "`n"
$snapshot='sha256:'+ [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
$inventory|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $OutputDirectory 'game-source-files.json') -Encoding utf8
function Invoke-GameCli([string[]]$CliArguments,[string]$LogName){
    $lines=& $Dotnet $cli @CliArguments
    $exitCode=$LASTEXITCODE
    $lines|Set-Content -LiteralPath (Join-Path $OutputDirectory $LogName) -Encoding utf8
    if($exitCode -ne 0){throw "ChemLeap publication CLI failed ($exitCode); see $LogName"}
    return ($lines -join "`n"|ConvertFrom-Json)
}
$pack=Invoke-GameCli @('pack',$project,(Join-Path $OutputDirectory 'packed')) 'pack.json'
$namedPackage=Join-Path $OutputDirectory 'ChemLeap-0.1.0.autumn'
Copy-Item -LiteralPath $pack.packagePath -Destination $namedPackage
$publication=Join-Path $OutputDirectory 'publication'
$generated=Invoke-GameCli @('generate-release',$namedPackage,$publication,'--developer','派蒙','--description','从 H₂ 出发的 3D 化学跳一跳：选择反应平台、蓄力跳跃、收集生成物并挑战最高分。离线可玩，支持 AutumnOS 存档。','--category','sq','--offline','true','--min-host','0.5.1-local.8','--min-sdk','0.3.0') 'generate.json'
$check=Invoke-GameCli @('validate-publication',(Join-Path $publication 'autumn.store.json'),(Join-Path $publication 'autumn.release.json'),(Join-Path $publication 'ChemLeap-0.1.0.autumn')) 'validate.json'
foreach($file in $inventory){if((Get-FileHash -LiteralPath (Join-Path $project $file.path)).Hash.ToLowerInvariant() -ne $file.sha256){throw 'Game sources changed during packaging.'}}
$release=Get-Content -LiteralPath (Join-Path $publication 'autumn.release.json') -Raw|ConvertFrom-Json
$archive=[IO.Compression.ZipFile]::OpenRead((Join-Path $publication $release.asset))
try{
    if($archive.Entries.Count -ne $inventory.Count){throw 'Packaged game entry count mismatch.'}
    foreach($file in $inventory){
        $entry=$archive.GetEntry($file.path);if(-not $entry){throw 'Missing game source resource in package.'}
        $stream=$entry.Open();try{$sha=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
        if($sha -ne $file.sha256){throw 'Game package readback hash mismatch.'}
    }
}finally{$archive.Dispose()}
[ordered]@{status='passed';game_source_snapshot_id=$snapshot;app_id=$release.appId;version=$release.version;package=(Join-Path $publication $release.asset);bytes=$release.bytes;sha256=$release.sha256;
    source_files=$inventory.Count;zip_readback='passed';publication_validation=$check;publishing='not_performed';public_download='not_run';ordinary_community_execution='not_run';developer_preview='separate_native_report'}|
    ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $OutputDirectory 'package-result.json') -Encoding utf8
Get-Content -LiteralPath (Join-Path $OutputDirectory 'package-result.json') -Raw
