[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunRoot,
    [Parameter(Mandatory)][string]$BuildId,
    [string]$Workspace = 'D:\特殊\chem',
    [string]$ProbeExe = 'tools\AutumnOS.Isolation.Probe\bin\x64\Release\net10.0-windows\win-x64\AutumnOS.Isolation.Probe.exe',
    [string]$Runtime = '.tools\webview2\154.0.4258.53\Microsoft.WebView2.FixedVersionRuntime.154.0.4258.53.x64',
    [ValidateSet('runtime-ab','resources-storage','network','same-origin')][string[]]$Modes = @('runtime-ab','resources-storage','network','same-origin')
)
# Prepared for the integration owner. Authoring this script does not run any GUI.
# No build, UAC, global policy edit, existing-report replacement, or process kill.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Workspace = [IO.Path]::GetFullPath($Workspace)
function Absolute([string]$Path) { if ([IO.Path]::IsPathRooted($Path)) { [IO.Path]::GetFullPath($Path) } else { [IO.Path]::GetFullPath((Join-Path $Workspace $Path)) } }
$RunRoot = Absolute $RunRoot
$ProbeExe = Absolute $ProbeExe
$Runtime = Absolute $Runtime
if (Test-Path -LiteralPath $RunRoot) { throw 'RunRoot must be completely new; earlier evidence will not be reused.' }
if (-not $RunRoot.StartsWith((Join-Path $Workspace 'artifacts') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'RunRoot must be under workspace artifacts.' }
if (-not (Test-Path -LiteralPath $ProbeExe -PathType Leaf) -or -not (Test-Path -LiteralPath (Join-Path $Runtime 'msedgewebview2.exe') -PathType Leaf)) { throw 'Already-built probe and fixed runtime are required; this script does not build.' }
$overrideNames = @('WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS','WEBVIEW2_BROWSER_EXECUTABLE_FOLDER','WEBVIEW2_USER_DATA_FOLDER','WEBVIEW2_PIPE_FOR_SCRIPT_DEBUGGER')
$presentOverrides = @($overrideNames | Where-Object { -not [string]::IsNullOrEmpty([Environment]::GetEnvironmentVariable($_, 'Process')) })
if ($presentOverrides.Count -gt 0) { throw ('Unexpected process-level WebView overrides are present (values not logged): ' + ($presentOverrides -join ', ')) }
$sourcePaths = @(
    'src\AutumnOS.Runtime\RuntimeIsolationPolicy.cs', 'src\AutumnOS.Runtime\WebViewProfileDirectory.cs',
    'tools\AutumnOS.Isolation.Probe\Program.cs', 'tools\AutumnOS.Isolation.Probe\RuntimePolicyWindow.cs',
    'tools\AutumnOS.Isolation.Probe\ResourceStorageWindow.cs', 'tools\AutumnOS.Isolation.Probe\NetworkPolicyWindow.cs',
    'tools\AutumnOS.Isolation.Probe\SameOriginWindow.cs', 'tests\fixtures\runtime-isolation\probe.js',
    'Directory.Packages.props', 'tools\AutumnOS.Isolation.Probe\AutumnOS.Isolation.Probe.csproj'
) | ForEach-Object { Absolute $_ }
$binaryPaths = @($ProbeExe, [IO.Path]::ChangeExtension($ProbeExe,'.dll'), (Join-Path ([IO.Path]::GetDirectoryName($ProbeExe)) 'AutumnOS.Runtime.dll'), (Join-Path $Runtime 'msedgewebview2.exe'))
function Fingerprint([string]$Path) { $f=Get-Item -LiteralPath $Path; [pscustomobject]@{path=$f.FullName;sha256=(Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant();bytes=$f.Length;last_write_utc=$f.LastWriteTimeUtc.ToString('o')} }
$policy = Get-Content -LiteralPath $sourcePaths[0] -Raw
$match = [regex]::Match($policy, 'public const string BrowserArguments\s*=\s*"([^"]+)"')
if (-not $match.Success) { throw 'Cannot bind expected shared BrowserArguments to source.' }
$expectedArguments = $match.Groups[1].Value
$probeDll = Get-Item -LiteralPath ([IO.Path]::ChangeExtension($ProbeExe,'.dll'))
$newestSource = ($sourcePaths | ForEach-Object { (Get-Item -LiteralPath $_).LastWriteTimeUtc } | Sort-Object -Descending | Select-Object -First 1)
if ($probeDll.LastWriteTimeUtc -lt $newestSource) { throw 'Probe DLL predates a relevant source file. Integration owner must build it after the current source freeze.' }
New-Item -ItemType Directory -Path $RunRoot | Out-Null
$manifest = [pscustomobject]@{
    schema_version=1;kind='fresh_owned_probe_matrix_invocation';build_id=$BuildId;started_utc=[DateTimeOffset]::UtcNow.ToString('o');
    workspace=$Workspace;run_root=$RunRoot;probe_exe=$ProbeExe;runtime=$Runtime;expected_runtime='154.0.4258.53';
    expected_browser_arguments=$expectedArguments;requested_modes=$Modes;source_files=@($sourcePaths|ForEach-Object{Fingerprint $_});binary_files=@($binaryPaths|ForEach-Object{Fingerprint $_});
    scope='Independent engine probe matrix only. Not product EXE/ZIP UI, SDK save, real Logto, or clipboard A/B acceptance.'
}
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $RunRoot 'run-manifest.json') -Encoding utf8
$oldDotnetRoot=$env:DOTNET_ROOT
try {
    $env:DOTNET_ROOT=Join-Path $Workspace '.tools\dotnet'
    foreach($mode in $Modes) {
        $directory=Join-Path $RunRoot $mode
        New-Item -ItemType Directory -Path $directory | Out-Null
        $report=Join-Path $directory 'probe.json'
        $started=[DateTimeOffset]::UtcNow
        # These paths are literal arguments, not shell-evaluated command strings.
        $probeArguments=@('--runtime',('"'+$Runtime+'"'),'--report',('"'+$report+'"'),'--mode',$mode)
        $process=Start-Process -FilePath $ProbeExe -ArgumentList $probeArguments -WorkingDirectory $Workspace -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $directory 'stdout.log') -RedirectStandardError (Join-Path $directory 'stderr.log')
        $deadline=[DateTimeOffset]::UtcNow.AddSeconds(180)
        while(-not $process.WaitForExit(1000)) {
            if([DateTimeOffset]::UtcNow -gt $deadline) { throw ('Probe still running beyond deadline; it was NOT killed. Stop this matrix and inspect owned PID '+$process.Id) }
        }
        $process.Refresh()
        [pscustomobject]@{mode=$mode;started_utc=$started.ToString('o');finished_utc=[DateTimeOffset]::UtcNow.ToString('o');pid=$process.Id;exit_code=$process.ExitCode;report=$report} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'invocation.json') -Encoding utf8
        $process.Dispose()
    }
} finally { $env:DOTNET_ROOT=$oldDotnetRoot }
& (Join-Path $PSScriptRoot 'Summarize-FinalProbeMatrix.ps1') -RunRoot $RunRoot -OutputPath (Join-Path $RunRoot 'summary.json')
