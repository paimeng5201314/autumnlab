[CmdletBinding()]
param(
    [string]$ReportDirectory,
    [switch]$BuildOnly,
    [switch]$SyntheticLuaBaseline,
    [switch]$IncludeRestrictedDesktop
)
. "$PSScriptRoot/Common.ps1"
if (-not $ReportDirectory) {
    $ReportDirectory = Join-Path $ProjectRoot ('artifacts/isolation-mechanisms/' + [Guid]::NewGuid().ToString('N'))
}
$reportRoot = [IO.Path]::GetFullPath($ReportDirectory)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $ProjectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (-not $reportRoot.StartsWith($artifactsRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Reports must stay within workspace artifacts.' }
if (Test-Path -LiteralPath $reportRoot) { throw 'Use a new report directory; previous evidence is retained.' }
for ($cursor=[IO.Path]::GetDirectoryName($reportRoot); $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
    if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected report directories are forbidden.' }
}
New-Item -ItemType Directory -Path $reportRoot | Out-Null
$summary = [ordered]@{
    schema_version=1; status='running'; started_utc=[DateTimeOffset]::UtcNow.ToString('o')
    baseline=$(if($SyntheticLuaBaseline){'synthetic-lua-medium'}else{'ordinary-user'})
    build_only=[bool]$BuildOnly; steps=@(); browser='not_run'; clipboard_content_tests='not_run'
    product_admission='unchanged'; real_user_desktop_acceptance='not_run'
    scope='Independent owned-process token and window-station-handle mechanisms only. No product or complete clipboard acceptance.'
}
$savedDotnetRoot=[Environment]::GetEnvironmentVariable('DOTNET_ROOT','Process')
try {
    Push-Location $ProjectRoot
    $context = & "$PSScriptRoot/Test-ExecutionContext.ps1" -ReportPath (Join-Path $reportRoot 'execution-context.json')
    if (-not $BuildOnly -and -not $SyntheticLuaBaseline -and $context.status -ne 'passed_prerequisites_only') {
        $summary.status='blocked_execution_context'
        throw 'Run from an ordinary non-elevated Windows user. An explicit synthetic LUA baseline is available for mechanism research only.'
    }
    $projects=@('tools/AutumnOS.Lpac.Probe/AutumnOS.Lpac.Probe.csproj')
    if ($IncludeRestrictedDesktop) { $projects+='research/isolation-20261103/RestrictedDesktopLaunchProbe/RestrictedDesktopLaunchProbe.csproj' }
    if ($SyntheticLuaBaseline) { $projects+='research/isolation-20261103/SyntheticLuaLauncher/SyntheticLuaLauncher.csproj' }
    foreach ($project in $projects) {
        $name=[IO.Path]::GetFileNameWithoutExtension($project)
        Invoke-Dotnet -Arguments @('build',$project,'-c','Release','-p:Platform=x64','-p:RestoreLockedMode=true') -LogPath (Join-Path $reportRoot ($name+'.build.log'))
        $summary.steps+= [ordered]@{name=$name;build='passed';mechanism='not_run'}
    }
    if ($BuildOnly) { $summary.status='built_native_not_run'; return }
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT',(Split-Path $Dotnet -Parent),'Process')
    $lpac=Join-Path $ProjectRoot 'tools/AutumnOS.Lpac.Probe/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.Lpac.Probe.exe'
    $cases=@([pscustomobject]@{name='lpac';executable=$lpac;arguments=@('--report',(Join-Path $reportRoot 'lpac/report.json'))})
    if ($SyntheticLuaBaseline) { $cases[0].arguments+=@('--baseline','synthetic-lua-medium') }
    if ($IncludeRestrictedDesktop) {
        $cases+= [pscustomobject]@{name='restricted-desktop';executable=(Join-Path $ProjectRoot 'research/isolation-20261103/RestrictedDesktopLaunchProbe/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.RestrictedDesktopLaunchProbe.exe');arguments=@('--report',(Join-Path $reportRoot 'restricted-desktop/report.json'))}
    }
    foreach ($case in $cases) {
        if ($SyntheticLuaBaseline) {
            $launcher=Join-Path $ProjectRoot 'research/isolation-20261103/SyntheticLuaLauncher/bin/x64/Release/net10.0-windows/win-x64/AutumnOS.SyntheticLuaLauncher.exe'
            & $launcher --workspace $ProjectRoot --report (Join-Path $reportRoot ($case.name+'-launcher.json')) --executable $case.executable --timeout-seconds 180 -- @($case.arguments)
        } else { & $case.executable @($case.arguments) }
        $nativeExit=$LASTEXITCODE
        $summary.steps+= [ordered]@{name=$case.name;exit_code=$nativeExit;mechanism=$(if($nativeExit -eq 0){'passed'}else{'failed'})}
        if ($nativeExit -ne 0) { throw ('Mechanism failed: '+$case.name+'. Preserve this run and inspect its native report.') }
    }
    $summary.status='passed_mechanisms_only'
} catch {
    if ($summary.status -eq 'running') { $summary.status='failed' }
    $summary.failure=$_.Exception.Message
    throw
} finally {
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT',$savedDotnetRoot,'Process')
    Pop-Location
    $summary.finished_utc=[DateTimeOffset]::UtcNow.ToString('o')
    $summary | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $reportRoot 'summary.json') -Encoding utf8
}
