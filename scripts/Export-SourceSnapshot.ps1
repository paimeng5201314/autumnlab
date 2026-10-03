[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildId,[Parameter(Mandatory)][string]$OutputPath)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if($BuildId -notmatch '^T06-[A-Za-z0-9_.-]+$'){throw 'Tracked T06 build required.'}
$evidence=Join-Path $root "artifacts/builds/$BuildId"
$build=Get-Content -LiteralPath (Join-Path $evidence 'build-result.json') -Raw|ConvertFrom-Json
$snapshot=Get-Content -LiteralPath (Join-Path $evidence 'source-snapshot.json') -Raw|ConvertFrom-Json
if($build.status -ne 'passed' -or $build.source_snapshot_id -ne $snapshot.source_snapshot_id){throw 'Matching successful build and snapshot required.'}
$OutputPath=[IO.Path]::GetFullPath($OutputPath)
if([IO.Path]::GetExtension($OutputPath) -ne '.zip' -or (Test-Path -LiteralPath $OutputPath) -or (Test-Path -LiteralPath ($OutputPath+'.json'))){throw 'New ZIP output and sidecar required; existing evidence is retained.'}
function Assert-PlainSnapshotPath([string]$Path){
    for($cursor=$Path;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Redirected snapshot path forbidden.'}
    }
}
Assert-PlainSnapshotPath $OutputPath
$rootPrefix=[IO.Path]::GetFullPath($root).TrimEnd('\')+'\'
$inputs=@{}
foreach($input in @($build.additional_inputs)){
    if($input){$inputs['build-inputs/'+[IO.Path]::GetFileName($input)]=[IO.Path]::GetFullPath($input)}
}
$files=@(foreach($entry in $snapshot.files){
    if($entry.path -match '(^|/)(AutumnOS_Data|artifacts|bin|obj|\.tools|node_modules)(/|$)|\.(pk8|pem|key|pfx|p12|snk|dpapi|log|binlog)$|(^|/)(\.env($|\.)|credentials\.json$|secrets\.json$)' -or $entry.path.Contains('\')){throw 'Private or generated snapshot entry forbidden.'}
    if($entry.path.StartsWith('build-inputs/')){
        if(-not $inputs.ContainsKey($entry.path)){throw 'Unbound public build input.'}
        $path=$inputs[$entry.path]
        if([IO.Path]::GetExtension($path) -notin @('.json','.txt') -or (Get-Item -LiteralPath $path).Length -gt 65536 -or (Get-Content -LiteralPath $path -Raw) -match 'PRIVATE KEY|privateKey|refresh_token|access_token'){throw 'Unsafe public build input.'}
    }else{
        $path=[IO.Path]::GetFullPath((Join-Path $root $entry.path))
        if(-not $path.StartsWith($rootPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Snapshot entry leaves project.'}
    }
    Assert-PlainSnapshotPath $path
    $item=Get-Item -LiteralPath $path
    if($item.Length -ne $entry.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $entry.sha256){throw 'Sources changed since tracked build; do not archive different bytes.'}
    [pscustomobject]@{path=$path;entry=$entry}
})
New-Item -ItemType Directory -Force -Path (Split-Path $OutputPath)|Out-Null
$zip=[IO.Compression.ZipFile]::Open($OutputPath,[IO.Compression.ZipArchiveMode]::Create)
try{
    foreach($file in $files){$null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$file.path,$file.entry.path,[IO.Compression.CompressionLevel]::Optimal)}
    $null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $evidence 'source-snapshot.json'),'source-snapshot.json',[IO.Compression.CompressionLevel]::Optimal)
}finally{$zip.Dispose()}
$archive=[IO.Compression.ZipFile]::OpenRead($OutputPath)
try{
    if($archive.Entries.Count -ne $files.Count+1){throw 'Unexpected source ZIP entries.'}
    foreach($file in $files){
        $entry=$archive.GetEntry($file.entry.path)
        if(-not $entry -or $entry.Length -ne $file.entry.bytes){throw 'Source archive length mismatch.'}
        $stream=$entry.Open()
        try{$hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()}finally{$stream.Dispose()}
        if($hash -ne $file.entry.sha256){throw 'Source archive bytes mismatch.'}
    }
}finally{$archive.Dispose()}
$report=[ordered]@{status='passed';task_id='T06';build_id=$BuildId;source_snapshot_id=$snapshot.source_snapshot_id;zip=$OutputPath;files=$files.Count;zip_sha256=(Get-FileHash -LiteralPath $OutputPath).Hash.ToLowerInvariant();private_data_included=$false;git='not_used';uploaded=$false}
$report|ConvertTo-Json|Set-Content -LiteralPath "$OutputPath.json" -Encoding utf8NoBOM
$report|ConvertTo-Json
