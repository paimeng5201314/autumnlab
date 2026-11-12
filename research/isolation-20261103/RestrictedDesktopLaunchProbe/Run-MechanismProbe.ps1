[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ReportDirectory
)
$ErrorActionPreference = 'Stop'
$workspace = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '../../..')).Path
$sdkRoot = Join-Path $workspace '.tools/dotnet'
$executable = Join-Path $PSScriptRoot 'bin/x64/Release/net10.0-windows/win-x64/AutumnOS.RestrictedDesktopLaunchProbe.exe'
$reportRoot = [IO.Path]::GetFullPath($ReportDirectory)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $reportRoot.StartsWith($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'REPORT_MUST_BE_WITHIN_WORKSPACE_ARTIFACTS' }
if (Test-Path -LiteralPath $reportRoot) { throw 'REPORT_DIRECTORY_MUST_BE_NEW' }
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) { throw 'BUILD_PROBE_FIRST' }
if (-not (Test-Path -LiteralPath (Join-Path $sdkRoot 'dotnet.exe') -PathType Leaf)) { throw 'PROJECT_DOTNET_SDK_MISSING' }
$savedDotnetRoot = [Environment]::GetEnvironmentVariable('DOTNET_ROOT', 'Process')
try {
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $sdkRoot, 'Process')
    & $executable --report (Join-Path $reportRoot 'report.json')
    $probeExitCode = $LASTEXITCODE
} finally {
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT', $savedDotnetRoot, 'Process')
}
if ($probeExitCode -ne 0) { throw "RESTRICTED_DESKTOP_MECHANISM_FAILED_EXIT_$probeExitCode; see $reportRoot/report.json" }
Write-Output (Join-Path $reportRoot 'report.json')
