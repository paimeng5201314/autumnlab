[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$projectRoot = Split-Path $PSScriptRoot -Parent
$version = '10.0.401'
$expectedHash = '24b670ad3d923bfcf47df6c3b034152398b42f6dbc388e10d783aee1cfb5e5817d399fc0ae2a12cfa822a55e61d34830ccb15c50ef6efee437ab874bb7c79430'
$toolRoot = Join-Path $projectRoot '.tools'
$destination = Join-Path $toolRoot 'dotnet'
$archive = Join-Path $toolRoot "dotnet-sdk-$version-win-x64.zip"
New-Item -ItemType Directory -Force -Path $toolRoot | Out-Null
if (Test-Path -LiteralPath (Join-Path $destination "sdk\$version")) {
    & (Join-Path $destination 'dotnet.exe') --info
    exit $LASTEXITCODE
}
if (-not (Test-Path -LiteralPath $archive)) {
    Invoke-WebRequest -Uri "https://builds.dotnet.microsoft.com/dotnet/Sdk/$version/dotnet-sdk-$version-win-x64.zip" -OutFile $archive -TimeoutSec 600
}
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash.ToLowerInvariant()
if ($actual -ne $expectedHash) { throw 'SDK SHA-512 mismatch; archive retained for inspection, not executed.' }
Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
& (Join-Path $destination 'dotnet.exe') --info
if ($LASTEXITCODE -ne 0) { throw 'Extracted SDK failed --info.' }
