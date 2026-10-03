[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$latest = (Get-Content -LiteralPath (Join-Path $projectRoot 'artifacts/latest-build.txt') -Raw).Trim()
$report = Get-Content -LiteralPath (Join-Path $projectRoot "artifacts/builds/$latest/build-result.json") -Raw | ConvertFrom-Json
if ($report.status -ne 'passed') { throw 'Run Build.ps1 first.' }
$executable = Join-Path $projectRoot $report.output_path
if (-not (Test-Path -LiteralPath $executable)) { throw 'Build output is missing. Run Build.ps1.' }
# The visible window is the product the user requested to run.
Start-Process -FilePath $executable -WorkingDirectory $projectRoot
