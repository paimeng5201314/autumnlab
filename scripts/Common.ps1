$ErrorActionPreference = 'Stop'
$ProjectRoot = Split-Path $PSScriptRoot -Parent
$Dotnet = Join-Path $ProjectRoot '.tools/dotnet/dotnet.exe'
if (-not (Test-Path -LiteralPath $Dotnet)) {
    $candidate = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $candidate) { throw 'Missing .NET SDK 10.0.401. Run Install-LocalSdk.ps1 only after installation authorization.' }
    $Dotnet = $candidate.Source
}
$env:DOTNET_ROOT = Split-Path $Dotnet -Parent
$env:DOTNET_CLI_HOME = Join-Path $ProjectRoot '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path $ProjectRoot '.tools/nuget/packages'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
function Invoke-Dotnet {
    param([string[]]$Arguments, [string]$LogPath)
    $shown = 'dotnet ' + ($Arguments -join ' ')
    Write-Host $shown
    if ($LogPath) {
        $shown | Out-File -LiteralPath $LogPath -Encoding utf8
        & $Dotnet @Arguments 2>&1 | Tee-Object -FilePath $LogPath -Append | Out-Host
    } else { & $Dotnet @Arguments | Out-Host }
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $shown" }
}
