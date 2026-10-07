#requires -Version 7.6
[CmdletBinding()]
param(
    [string]$BuildId = ('T06-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 6)),
    [string]$BuildVersion = '0.5.1-local.5',
    [string]$ReleaseLabel = 'meta0.0.2-20261007',
    [switch]$CheckPrerequisitesOnly,
    [switch]$SkipNativeSmoke
)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$pipelineScripts = $PSScriptRoot
$checks = [Collections.Generic.List[object]]::new()
function Add-Prerequisite([string]$Name, [bool]$Passed, [string]$Detail) {
    $checks.Add([ordered]@{ name=$Name; passed=$Passed; detail=$Detail })
}
function Assert-Prerequisites {
    if (@($checks | Where-Object { -not $_.passed }).Count) {
        [ordered]@{ status='blocked'; phase='prerequisites'; checks=$checks; network_operations='not_started'; product_execution='not_run' } |
            ConvertTo-Json -Depth 6 | Write-Output
        throw 'Single-file prerequisites are incomplete. No dependency download or product execution was started.'
    }
}

Add-Prerequisite 'Windows' ([OperatingSystem]::IsWindows()) 'A Windows desktop is required for WinUI, Authenticode, the bootstrap compiler and native smoke tests.'
Add-Prerequisite 'x64 operating system and PowerShell' (
    [Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [Runtime.InteropServices.Architecture]::X64 -and
    [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -eq [Runtime.InteropServices.Architecture]::X64
) 'Use Windows x64 and x64 PowerShell; ARM and 32-bit hosts are not this build target.'
Add-Prerequisite 'PowerShell runtime' ([Environment]::Version.Major -ge 10) 'PowerShell 7.6 or newer running on .NET 10 or newer is required by the existing build and UI automation APIs.'
Assert-Prerequisites

Add-Prerequisite 'supported Windows baseline' ([Environment]::OSVersion.Version.Build -ge 19041) 'The application minimum is Windows 10 build 19041; platform compatibility still requires separate evidence.'
$currentProcess = [Diagnostics.Process]::GetCurrentProcess()
$currentSession = $currentProcess.SessionId
$currentProcess.Dispose()
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    Add-Prerequisite 'standard user' (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) 'Use a standard account for Windows pipe ownership, private-file ACL tests and native smoke. The CI wrapper creates a temporary standard account.'
    Add-Prerequisite 'user owns new Windows objects' ($identity.Owner.Value -eq $identity.User.Value) 'The current-user pipe and inherited Modify tests require the token default owner to be the user SID.'
} finally { $identity.Dispose() }
if (-not $SkipNativeSmoke) {
    Add-Prerequisite 'interactive session' ([Environment]::UserInteractive -and $currentSession -gt 0) 'Keep the desktop unlocked during the native UI test; a service/session 0 is unsupported. CI must explicitly select -SkipNativeSmoke.'
}
$activeProducts = @(Get-Process -Name AutumnOS,AutumnOS.Client -ErrorAction SilentlyContinue | Where-Object SessionId -eq $currentSession)
Add-Prerequisite 'no existing product instance' ($activeProducts.Count -eq 0) 'Save your work and close existing AutumnOS instances before invoking the pipeline; this script does not close them.'
foreach ($process in $activeProducts) { $process.Dispose() }

Add-Prerequisite 'build identity' ($BuildId -match '^T06-[A-Za-z0-9][A-Za-z0-9_.-]{0,86}$') 'Use a unique T06 build ID; existing outputs and failure evidence are retained.'
Add-Prerequisite 'build version' ($BuildVersion -match '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-[0-9A-Za-z-]+(\.[0-9A-Za-z-]+)*)?$') 'Specify a semantic build version, for example 0.5.1-local.5.'
Add-Prerequisite 'release label' ($ReleaseLabel -match '^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$') 'The display label is independent of the build version.'
foreach ($relative in @("artifacts/builds/$BuildId", "artifacts/single-file/$BuildId", "artifacts/build-pipelines/$BuildId")) {
    Add-Prerequisite ('new output: ' + $relative) (-not (Test-Path -LiteralPath (Join-Path $projectRoot $relative))) 'Choose another BuildId if this path already exists.'
}
foreach ($relative in @(
    'global.json', 'NuGet.Config', 'AutumnOS.slnx', 'docs/versions/0.5.1/README.md',
    'scripts/Install-LocalSdk.ps1', 'scripts/Get-Environment.ps1', 'scripts/New-SourceSnapshot.ps1',
    'scripts/Build.ps1', 'scripts/Test.ps1', 'scripts/Cache-WebViewRuntime.ps1',
    'scripts/Package-T05.ps1', 'scripts/Package-DeveloperKit.ps1', 'scripts/Package-SingleFile.ps1',
    'scripts/Test-SingleFileSmoke.ps1', 'tools/contract-checks/toolchain.json', 'tools/contract-checks/restore-tools.ps1'
)) {
    Add-Prerequisite ('source: ' + $relative) (Test-Path -LiteralPath (Join-Path $projectRoot $relative) -PathType Leaf) 'Use the complete source directory.'
}
for ($cursor=$projectRoot; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
    Add-Prerequisite ('plain source ancestor: ' + $cursor) (((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) 'Existing packaging tools reject junctions and other redirected source ancestors.'
}
foreach ($tool in @('tar.exe', 'Get-CimInstance', 'Get-AuthenticodeSignature')) {
    Add-Prerequisite ('tool: ' + $tool) ($null -ne (Get-Command $tool -ErrorAction SilentlyContinue)) 'Required by the existing local tooling or Windows verification scripts.'
}
foreach ($relative in @('Microsoft.NET/Framework64/v4.0.30319/csc.exe', 'System32/expand.exe')) {
    Add-Prerequisite ('Windows component: ' + $relative) (Test-Path -LiteralPath (Join-Path $env:WINDIR $relative) -PathType Leaf) 'These are existing Windows components; the pipeline does not install global components.'
}
$node = Get-Command node -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
Add-Prerequisite 'Node.js available' ($null -ne $node) 'Provide Node.js >=22.9 on this process PATH before running; npm is restored separately at its locked version.'
if ($node) {
    $nodeVersionText = (& $node.Source --version | Out-String).Trim()
    $nodeExit = $LASTEXITCODE
    $parsedNodeVersion = $null
    $validNodeVersion = [Version]::TryParse($nodeVersionText.TrimStart('v'), [ref]$parsedNodeVersion)
    Add-Prerequisite 'Node.js version' ($nodeExit -eq 0 -and $validNodeVersion -and $parsedNodeVersion -ge [Version]'22.9.0') $nodeVersionText
}
Assert-Prerequisites
if ($CheckPrerequisitesOnly) {
    [ordered]@{ status='prerequisites_passed'; checks=$checks; dependency_downloads='not_run'; build='not_run'; native_smoke='not_run'; skip_native_smoke=[bool]$SkipNativeSmoke } |
        ConvertTo-Json -Depth 6 | Write-Output
    return
}

$pipelineDirectory = Join-Path $projectRoot "artifacts/build-pipelines/$BuildId"
New-Item -ItemType Directory -Path $pipelineDirectory | Out-Null
$summaryPath = Join-Path $pipelineDirectory 'pipeline.json'
$report = [ordered]@{
    status='running'; build_id=$BuildId; version=$BuildVersion; release_label=$ReleaseLabel
    started_utc=[DateTimeOffset]::UtcNow.ToString('o'); prerequisite_checks=$checks; stages=@()
    source_snapshot_id=$null; executable=$null; executable_sha256=$null; native_smoke='not_run'
    native_tests=$(if ($SkipNativeSmoke) { 'not_run_ci_no_interactive_desktop' } else { 'not_run' })
    skip_native_smoke=[bool]$SkipNativeSmoke
    signature='not_signed'; production_update_trust='unconfigured'; complete_release_acceptance='not_run'
}
function Save-PipelineReport { $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $summaryPath -Encoding utf8NoBOM }
function Invoke-PipelineStage([string]$Name, [scriptblock]$Action) {
    $stage = [ordered]@{ name=$Name; status='running'; started_utc=[DateTimeOffset]::UtcNow.ToString('o') }
    $report.stages += $stage
    Save-PipelineReport
    Write-Host "Starting $Name"
    try { & $Action | Out-Host; $stage.status='passed' }
    catch { $stage.status='failed'; $stage.failure=$_.Exception.Message; throw }
    finally { $stage.finished_utc=[DateTimeOffset]::UtcNow.ToString('o'); Save-PipelineReport }
}
$transcriptStarted = $false
Push-Location $projectRoot
try {
    Start-Transcript -LiteralPath (Join-Path $pipelineDirectory 'pipeline.log') -NoClobber | Out-Null
    $transcriptStarted = $true
    $report.source_snapshot_id = & "$pipelineScripts/New-SourceSnapshot.ps1" -OutputDirectory (Join-Path $pipelineDirectory 'source-before')
    Save-PipelineReport
    # NuGet.Config registers this local source even on a fresh source-only checkout.
    # The directory may be empty; official network restore and frozen locks remain authoritative.
    New-Item -ItemType Directory -Force -Path (Join-Path $projectRoot '.tools/nuget-feed') | Out-Null
    Invoke-PipelineStage 'install-pinned-sdk' {
        & "$pipelineScripts/Install-LocalSdk.ps1"
        if ($LASTEXITCODE -ne 0) { throw 'Pinned SDK installation or verification failed.' }
        if (-not $SkipNativeSmoke) {
            $desktopRuntime = Join-Path $projectRoot '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
            foreach ($name in @('WindowsBase.dll', 'UIAutomationTypes.dll', 'UIAutomationClient.dll', 'System.Drawing.Common.dll')) {
                if (-not (Test-Path -LiteralPath (Join-Path $desktopRuntime $name) -PathType Leaf)) {
                    throw "Pinned SDK lacks native smoke dependency: $name in $desktopRuntime. Preserve the installation evidence and inspect the SDK; no runtime substitution was made."
                }
            }
        }
        & "$pipelineScripts/Get-Environment.ps1"
    }
    Invoke-PipelineStage 'restore-locked-contract-tools' { & (Join-Path $projectRoot 'tools/contract-checks/restore-tools.ps1') -NodePath $node.Source }
    Invoke-PipelineStage 'cache-verified-webview' { & "$pipelineScripts/Cache-WebViewRuntime.ps1" }
    Invoke-PipelineStage 'build' {
        & "$pipelineScripts/Build.ps1" -BuildId $BuildId -BuildVersion $BuildVersion -ReleaseLabel $ReleaseLabel
        $build = Get-Content -LiteralPath "artifacts/builds/$BuildId/build-result.json" -Raw | ConvertFrom-Json
        if ($build.status -ne 'passed' -or $build.source_snapshot_id -ne $report.source_snapshot_id) {
            throw 'The build must use the exact source captured before dependency setup.'
        }
    }
    Invoke-PipelineStage 'test-current-build' { & "$pipelineScripts/Test.ps1" }
    Invoke-PipelineStage 'prepare-complete-directory' { & "$pipelineScripts/Package-T05.ps1" -Stage Prepare }
    Invoke-PipelineStage 'package-single-file' { & "$pipelineScripts/Package-SingleFile.ps1" -BuildId $BuildId }
    if ($SkipNativeSmoke) {
        $report.native_smoke = 'not_run_ci_no_interactive_desktop'
        $report.stages += [ordered]@{ name='native-single-file-smoke'; status='not_run_ci_no_interactive_desktop'; reason='Explicit -SkipNativeSmoke; packaging and noninteractive tests do not verify Windows UI.' }
        Save-PipelineReport
    } else {
        Invoke-PipelineStage 'native-single-file-smoke' {
            & "$pipelineScripts/Test-SingleFileSmoke.ps1" -BuildId $BuildId -ReportDirectory (Join-Path $pipelineDirectory 'native-smoke')
            $smoke = Get-Content -LiteralPath (Join-Path $pipelineDirectory 'native-smoke/single-file-smoke.json') -Raw | ConvertFrom-Json
            if ($smoke.status -ne 'passed' -or $smoke.cleanup -ne 'passed' -or $smoke.build_id -ne $BuildId -or $smoke.source_snapshot_id -ne $report.source_snapshot_id) {
                throw 'A passing native smoke report for this exact build and source is required.'
            }
            $report.native_smoke = 'passed'
            $report.native_tests = 'passed'
        }
    }
    $after = & "$pipelineScripts/New-SourceSnapshot.ps1" -OutputDirectory (Join-Path $pipelineDirectory 'source-after')
    if ($after -ne $report.source_snapshot_id) { throw 'Source changed during the pipeline; preserve outputs and use a new build ID after review.' }
    $package = Get-Content -LiteralPath "artifacts/builds/$BuildId/package-single-file.json" -Raw | ConvertFrom-Json
    if ($package.version -ne $BuildVersion -or $package.release_label -ne $ReleaseLabel -or
        (Get-FileHash -LiteralPath $package.executable -Algorithm SHA256).Hash.ToLowerInvariant() -ne $package.executable_sha256) {
        throw 'Final executable version or digest does not match its packaging report.'
    }
    $report.executable = $package.executable
    $report.executable_sha256 = $package.executable_sha256
    $report.status = if ($SkipNativeSmoke) { 'packaged_native_not_run' } else { 'passed' }
    Write-Host "Single-file output: $($report.executable)"
    Write-Host "Native tests: $($report.native_tests)"
    Write-Host "SHA-256: $($report.executable_sha256)"
} catch {
    $report.status = 'failed'
    $report.failure = $_.Exception.Message
    throw
} finally {
    $report.finished_utc = [DateTimeOffset]::UtcNow.ToString('o')
    Save-PipelineReport
    if ($transcriptStarted) { Stop-Transcript | Out-Null }
    Pop-Location
}
