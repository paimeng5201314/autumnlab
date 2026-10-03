[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$outputDirectory = Join-Path $projectRoot 'artifacts/samples'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$destination = Join-Path $outputDirectory 'element-pairs.autumn'
$temporary = Join-Path $outputDirectory ([Guid]::NewGuid().ToString('N') + '.tmp')
$files = @(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'samples/element-pairs') -File)
if (-not ($files.Name -contains 'index.html')) { throw 'Missing element-pairs entry point.' }
$files += Get-Item -LiteralPath (Join-Path $projectRoot 'sdk/autumn-sdk.js')
try {
    $archive = [IO.Compression.ZipFile]::Open($temporary, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in ($files | Sort-Object Name)) {
            if (($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Sample resource links are forbidden.' }
            $entry = $archive.CreateEntry($file.Name, [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(2026,1,1,0,0,0,[TimeSpan]::Zero)
            $stream = $entry.Open()
            try { $stream.Write([IO.File]::ReadAllBytes($file.FullName)) } finally { $stream.Dispose() }
        }
    } finally { $archive.Dispose() }
    [IO.File]::Move($temporary, $destination, $true)
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
Write-Host "Sample package: $destination"
