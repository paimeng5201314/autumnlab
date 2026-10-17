[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReportDirectory,[switch]$CompileOnly)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$reportRoot = [IO.Path]::GetFullPath($ReportDirectory)
if (Test-Path -LiteralPath $reportRoot) { throw 'Choose a new report directory to preserve prior evidence.' }
New-Item -ItemType Directory -Path $reportRoot | Out-Null
$framework = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler = Join-Path $framework 'csc.exe'
$testExecutable = Join-Path $reportRoot 'PreparationTests.exe'
$arguments = @('/nologo', '/utf8output', '/target:exe', '/main:PreparationTests', '/platform:x64', '/optimize+', '/warnaserror+',
    "/out:$testExecutable", '/reference:System.dll', '/reference:System.Core.dll',
    "/reference:$(Join-Path $framework 'System.IO.Compression.dll')", "/reference:$(Join-Path $framework 'System.Runtime.Serialization.dll')",
    '/reference:System.Windows.Forms.dll', '/reference:System.Drawing.dll',
    (Join-Path $projectRoot 'src/AutumnOS.SingleFile/Program.cs'), (Join-Path $PSScriptRoot 'PreparationTests.cs'))
& $compiler @arguments 2>&1 | Tee-Object -FilePath (Join-Path $reportRoot 'compile.log')
if ($LASTEXITCODE -ne 0) { throw 'Preparation test compilation failed.' }
if ($CompileOnly) {
    [ordered]@{status='compiled';tests='not_run';executable=$testExecutable;source_sha256=(Get-FileHash -LiteralPath (Join-Path $projectRoot 'src/AutumnOS.SingleFile/Program.cs')).Hash.ToLowerInvariant()} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportRoot 'compile-result.json') -Encoding UTF8
    return
}
& $testExecutable $reportRoot 2>&1 | Tee-Object -FilePath (Join-Path $reportRoot 'tests.log')
$result = $LASTEXITCODE
[ordered]@{
    status = $(if ($result -eq 0) { 'passed' } else { 'failed' })
    source_sha256 = (Get-FileHash -LiteralPath (Join-Path $projectRoot 'src/AutumnOS.SingleFile/Program.cs')).Hash.ToLowerInvariant()
    scope = 'Actual inbox Framework preparation window, worker/UI separation, measured hashing/copy, joined cancellation with retained file and main-owned mutex, exception propagation, and portable reentry; UI values are explicit fixtures, not extraction evidence.'
    full_bundle_first_start = 'not_run'
    manual_screen_reader = 'not_run'
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportRoot 'result.json') -Encoding UTF8
if ($result -ne 0) { throw 'Preparation tests failed; see the retained report.' }
