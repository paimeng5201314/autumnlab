[CmdletBinding()]
param([switch]$Locked)
. "$PSScriptRoot/Common.ps1"
Push-Location $ProjectRoot
try {
    New-Item -ItemType Directory -Force 'artifacts/reports' | Out-Null
    $arguments = @('restore','AutumnOS.slnx','-p:Platform=x64','--configfile','NuGet.Config','--use-lock-file')
    if ($Locked) { $arguments += '--locked-mode' }
    Invoke-Dotnet -Arguments $arguments -LogPath (Join-Path $ProjectRoot 'artifacts/reports/restore.log')
} finally { Pop-Location }
