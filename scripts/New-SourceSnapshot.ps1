[CmdletBinding()]
param([Parameter(Mandatory)][string]$OutputDirectory, [string[]]$AdditionalInputs=@())
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$paths = [System.Collections.Generic.List[string]]::new()
foreach ($name in @('global.json','AutumnOS.slnx','Directory.Build.props','Directory.Packages.props','NuGet.Config')) {
    $paths.Add((Join-Path $projectRoot $name))
}
foreach ($folder in @('src','tests','build','scripts','docs','samples','sdk','tools','schemas')) {
    $root = Join-Path $projectRoot $folder
    if (Test-Path -LiteralPath $root) {
        Get-ChildItem -LiteralPath $root -File -Recurse | Where-Object {
            $_.FullName -notmatch '[\\/](bin|obj|AutumnOS_Data|artifacts|\.tools)[\\/]' -and
            $_.Extension -notin @('.pfx','.p12','.key','.pem','.pk8','.dpapi','.snk','.log','.binlog') -and
            $_.Name -notmatch '^(\.env($|\.)|secrets\.json$|credentials\.json$)'
        } | ForEach-Object { $paths.Add($_.FullName) }
    }
}
foreach ($name in @('logto.public.json','logto.registration-status.json')) {
    $paths.Add((Join-Path $projectRoot "autumnos-spec/config/$name"))
}
$entries = @($paths | Sort-Object -Unique | ForEach-Object {
    $item = Get-Item -LiteralPath $_
    [ordered]@{ path=[IO.Path]::GetRelativePath($projectRoot,$item.FullName).Replace('\','/'); bytes=$item.Length; sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant() }
} | Sort-Object { $_.path })
foreach($inputPath in $AdditionalInputs) {
    $item=Get-Item -LiteralPath $inputPath
    if($item.Extension -notin @('.json','.txt') -or $item.Length -gt 65536 -or (Get-Content -LiteralPath $item.FullName -Raw) -match 'PRIVATE KEY|privateKey|refresh_token|access_token'){throw 'Only bounded public build inputs may enter a source snapshot.'}
    $entries += [ordered]@{path=('build-inputs/'+$item.Name);bytes=$item.Length;sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}
$entries=@($entries|Sort-Object { $_.path })
$canonical = ($entries | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n"
$hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
$snapshot = [ordered]@{ schema_version=1; source_snapshot_id="sha256:$hash"; commit='not_applicable'; algorithm='SHA-256'; canonical_format='UTF-8, sorted relative paths, sha256 + two spaces + path, LF, no trailing LF'; files=$entries }
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$snapshot | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $OutputDirectory 'source-snapshot.json') -Encoding utf8
return $snapshot.source_snapshot_id
