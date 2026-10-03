[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $projectRoot 'artifacts/store-fixtures' }
$output = [IO.Path]::GetFullPath($OutputDirectory)
$workspace = [IO.Path]::GetFullPath($projectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if (-not $output.StartsWith($workspace,[StringComparison]::OrdinalIgnoreCase)) { throw 'Fixture output must remain within workspace.' }
New-Item -ItemType Directory -Force -Path $output | Out-Null
$sample = Join-Path $projectRoot 'samples/store-probe'
$base = Get-Content -LiteralPath (Join-Path $sample 'manifest.json') -Raw | ConvertFrom-Json -AsHashtable
$versions = @(@{version='1.0.0';format=1},@{version='1.1.0';format=1},@{version='1.2.0-preview.1';format=1},@{version='2.0.0';format=2},@{version='1.9.0';format=1;bad=$true})
$records = @(); $utf8 = [Text.UTF8Encoding]::new($false)
foreach ($variant in $versions) {
    $manifest = [ordered]@{}; foreach ($key in $base.Keys) { $manifest[$key] = $base[$key] }
    $manifest.version = $variant.version; $manifest.saveFormatVersion = $variant.format
    $name = 'store-probe-' + $variant.version + '.autumn'
    $path = Join-Path $output $name; $temporary = Join-Path $output ('fixture-' + [Guid]::NewGuid().ToString('N') + '.tmp')
    $resources = [ordered]@{}
    foreach ($file in (Get-ChildItem -LiteralPath $sample -File | Where-Object Name -ne 'manifest.json' | Sort-Object Name)) {
        if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Fixture links are forbidden.' }
        $resources[$file.Name] = [IO.File]::ReadAllBytes($file.FullName)
        if ($file.Name -eq 'index.html') {
            $html = $utf8.GetString($resources[$file.Name]).Replace('data-save-format="1"',('data-save-format="' + $variant.format + '"')).Replace('__APP_VERSION__',$variant.version).Replace('__SAVE_FORMAT__',[string]$variant.format)
            $resources[$file.Name] = $utf8.GetBytes($html)
        }
    }
    $resources['manifest.json'] = $utf8.GetBytes(($manifest | ConvertTo-Json -Depth 12 -Compress))
    $resources['autumn-sdk.js'] = [IO.File]::ReadAllBytes((Join-Path $projectRoot 'sdk/autumn-sdk.js'))
    $padding = New-Object byte[] 196608
    for ($i=0; $i -lt $padding.Length; $i++) { $padding[$i] = [byte](($i * 73 + [math]::Floor($i / 251)) % 256) }
    $resources['fixture-padding.json'] = $utf8.GetBytes('{"purpose":"isolated download progress fixture","bytes":"' + [Convert]::ToBase64String($padding) + '"}')
    try {
        $archive = [IO.Compression.ZipFile]::Open($temporary,[IO.Compression.ZipArchiveMode]::Create)
        try {
            foreach ($entryName in ($resources.Keys | Sort-Object)) {
                $compression = if ($entryName -eq 'fixture-padding.json') { [IO.Compression.CompressionLevel]::NoCompression } else { [IO.Compression.CompressionLevel]::Optimal }
                $entry = $archive.CreateEntry($entryName,$compression); $entry.LastWriteTime = [DateTimeOffset]::new(2026,1,1,0,0,0,[TimeSpan]::Zero)
                $stream = $entry.Open(); try { $stream.Write($resources[$entryName]) } finally { $stream.Dispose() }
            }
        } finally { $archive.Dispose() }
        $hash = (Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash.ToLowerInvariant()
        if ((Test-Path -LiteralPath $path) -and (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ne $hash) {
            Move-Item -LiteralPath $path -Destination ($path + '.previous-' + [Guid]::NewGuid().ToString('N'))
        }
        if (-not (Test-Path -LiteralPath $path)) { Move-Item -LiteralPath $temporary -Destination $path } else { Remove-Item -LiteralPath $temporary }
        $size = (Get-Item -LiteralPath $path).Length
        $release = [ordered]@{schemaVersion=1;appId=$manifest.appId;version=$manifest.version;channel=$(if($manifest.version.Contains('-')){'preview'}else{'stable'});runtime='web';minHostVersion='0.3.0';minSdkVersion='0.3.0';entry='index.html';asset=$name;bytes=$size;sha256=$(if($variant.bad){'0' * 64}else{$hash});permissions=@($manifest.permissions);saveFormatVersion=$variant.format}
        $metadata = 'release-' + $variant.version + '.json'; $metadataPath = Join-Path $output $metadata
        $metadataBytes = $utf8.GetBytes(($release | ConvertTo-Json -Depth 12 -Compress))
        if ((Test-Path -LiteralPath $metadataPath) -and -not [Linq.Enumerable]::SequenceEqual[byte]([IO.File]::ReadAllBytes($metadataPath),$metadataBytes)) { Move-Item -LiteralPath $metadataPath -Destination ($metadataPath + '.previous-' + [Guid]::NewGuid().ToString('N')) }
        [IO.File]::WriteAllBytes($metadataPath,$metadataBytes)
        $records += [ordered]@{version=$variant.version;file=$name;sha256=$hash;bytes=$size;metadata=$metadata;badHash=[bool]$variant.bad;saveFormatVersion=$variant.format}
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary } }
}
$store = [ordered]@{schemaVersion=1;appId=$base.appId;name=$base.name;description='本地集成测试数据，不是 GitHub 实时结果。独立样例通过同一解析、下载、验证与安装路径。';category='sq';developer=[ordered]@{name='派蒙 · 本地测试'};screenshots=@();offlineCapable=$true}
[IO.File]::WriteAllBytes((Join-Path $output 'autumn.store.json'),$utf8.GetBytes(($store | ConvertTo-Json -Depth 12 -Compress)))
$index = [ordered]@{schemaVersion=1;appId=$base.appId;repositoryId=90004001;owner='autumnos-isolated-test';repository='store-probe';versions=$records}
[IO.File]::WriteAllBytes((Join-Path $output 'fixture-index.json'),$utf8.GetBytes(($index | ConvertTo-Json -Depth 12 -Compress)))
Write-Host "Generated isolated Store fixtures: $output"
