[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$target = [IO.Path]::GetFullPath($Destination)
if (Test-Path -LiteralPath $target) { throw 'Choose a new directory; existing projects and data are preserved.' }
for ($cursor = $target; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
    if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected project paths are not supported.' }
}
New-Item -ItemType Directory -Path $target | Out-Null
foreach ($source in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'samples/element-pairs') -File) {
    if ($source.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected sample files are not supported.' }
    Copy-Item -LiteralPath $source.FullName -Destination (Join-Path $target $source.Name)
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'sdk/autumn-sdk.js') -Destination (Join-Path $target 'autumn-sdk.js')
Write-Host "Local developer project (includes actual SDK): $target"
