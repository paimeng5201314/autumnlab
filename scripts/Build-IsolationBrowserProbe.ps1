[CmdletBinding()]
param([switch]$JobBound)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$sourceRoot = Join-Path $workspace 'artifacts/isolation-completion-20261030'
$buildRoot = Join-Path $sourceRoot ('builds/probe-' + [Guid]::NewGuid().ToString('N'))
$dotnet = Join-Path $workspace '.tools/dotnet/dotnet.exe'
$projects = [ordered]@{
    host = (Join-Path $sourceRoot 'appcontainer-browser-host/OwnedBrowserHost.csproj')
    parent = (Join-Path $sourceRoot 'appcontainer-browser-parent/BrowserParent.csproj')
}
if ($JobBound) { $projects.parent = Join-Path $sourceRoot 'job-bound-browser-parent/BrowserParent.csproj' }
foreach ($project in $projects.Values) {
    if (-not [IO.File]::Exists($project)) { throw "Probe project is not ready: $project" }
}
[void][IO.Directory]::CreateDirectory($buildRoot)
$settings = @{
    TEMP = (Join-Path $buildRoot 'temp'); TMP = (Join-Path $buildRoot 'temp')
    DOTNET_CLI_HOME = (Join-Path $workspace '.tools/dotnet-home')
    DOTNET_ROOT = (Join-Path $workspace '.tools/dotnet')
    NUGET_PACKAGES = (Join-Path $workspace '.tools/nuget/packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $buildRoot 'nuget-http')
    NUGET_SCRATCH = (Join-Path $buildRoot 'nuget-scratch')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'; DOTNET_NOLOGO = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'; DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
}
$prior = @{}
foreach ($name in $settings.Keys) { $prior[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
function Invoke-ProbeBuild([string[]]$Arguments, [string]$LogName) {
    & $dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $buildRoot $LogName) | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Probe compilation command failed: $LogName ($LASTEXITCODE)" }
}
function Get-ProbeSources {
    $paths = @($PSCommandPath, (Join-Path $workspace 'scripts/Run-IsolationBrowserProbe.ps1'),
        (Join-Path $workspace 'NuGet.Config'), (Join-Path $workspace 'global.json'),
        (Join-Path $workspace 'Directory.Build.props'), (Join-Path $workspace 'Directory.Packages.props'),
        (Join-Path $workspace 'build/Brand.props'),
        (Join-Path $workspace 'src/AutumnOS.Runtime/RuntimeIsolationPolicy.cs'))
    foreach ($project in $projects.Values) {
        $projectDirectory = Split-Path $project -Parent
        $paths += @(Get-ChildItem -LiteralPath $projectDirectory -File |
            Where-Object { $_.Extension -in '.cs', '.csproj', '.props', '.js', '.md' -or $_.Name -eq 'packages.lock.json' } |
            Select-Object -ExpandProperty FullName)
        [xml]$projectXml = Get-Content -LiteralPath $project -Raw
        foreach ($compile in $projectXml.SelectNodes('/Project/ItemGroup/Compile[@Include]')) {
            if ($compile.Include -match '[*?$]') { throw 'Use explicit probe source paths for the tracked snapshot.' }
            $paths += [IO.Path]::GetFullPath((Join-Path $projectDirectory $compile.Include))
        }
    }
    foreach ($path in ($paths | Sort-Object -Unique)) {
        $item = Get-Item -LiteralPath $path
        if (-not $item.FullName.StartsWith($workspace+'\',[StringComparison]::OrdinalIgnoreCase) -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Redirected or external probe source rejected.' }
        [ordered]@{path=[IO.Path]::GetRelativePath($workspace, $item.FullName).Replace('\','/');
            bytes=$item.Length; sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
}
$result = [ordered]@{schema_version=1;producer='派蒙';status='not_run';build_id=(Split-Path $buildRoot -Leaf);
    source_snapshot_id=$null;source_files=@();outputs=@();executed_probes='not_run';profile_creation='not_run';
    browser_execution='not_run';clipboard_execution='not_run';product_integration='not_run';
    job_bound_variant=[bool]$JobBound;
    canonical_format='UTF-8 sorted workspace-relative paths; sha256 + two spaces + byte count + two spaces + path; LF; no final LF'}
try {
    foreach ($name in $settings.Keys) { [Environment]::SetEnvironmentVariable($name, $settings[$name], 'Process') }
    foreach ($name in @('TEMP','NUGET_HTTP_CACHE_PATH','NUGET_SCRATCH')) { [void][IO.Directory]::CreateDirectory($settings[$name]) }
    $policyPath = Join-Path $workspace 'src/AutumnOS.Runtime/RuntimeIsolationPolicy.cs'
    $frozenPath = Join-Path $sourceRoot 'appcontainer-browser-host/FrozenPolicy.cs'
    $currentPolicy = [IO.File]::ReadAllText($policyPath)
    $frozenPolicy = [IO.File]::ReadAllText($frozenPath)
    foreach ($name in @('PolicyId','RequiredBrowserVersion','BrowserArguments','ContentSecurityPolicy','ConnectionAllowlist','DnsPrefetchControl','PermissionsPolicy')) {
        $pattern = '(?m)const string ' + [regex]::Escape($name) + ' = ("[^\r\n]*");'
        $current = [regex]::Match($currentPolicy, $pattern)
        $frozen = [regex]::Match($frozenPolicy, $pattern)
        if (-not $current.Success -or -not $frozen.Success -or $current.Groups[1].Value -cne $frozen.Groups[1].Value) {
            throw "Frozen production policy differs: $name"
        }
    }
    foreach ($name in $projects.Keys) {
        $project = $projects[$name]
        $lock = Join-Path (Split-Path $project -Parent) 'packages.lock.json'
        $restore = @('restore',$project,'--configfile',(Join-Path $workspace 'NuGet.Config'),'--nologo')
        if ([IO.File]::Exists($lock)) { $restore += '--locked-mode' } else { $restore += '--use-lock-file' }
        Invoke-ProbeBuild $restore "$name-restore.log"
        Invoke-ProbeBuild @('restore',$project,'--configfile',(Join-Path $workspace 'NuGet.Config'),'--locked-mode','--nologo') "$name-locked-restore.log"
    }
    $sources = @(Get-ProbeSources)
    $canonical = ($sources | ForEach-Object { "$($_.sha256)  $($_.bytes)  $($_.path)" }) -join "`n"
    $snapshot = 'sha256:' + [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
    $result.source_snapshot_id = $snapshot
    $result.source_files = $sources
    $result.status = 'building'
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $buildRoot 'build-result.json') -Encoding utf8NoBOM
    foreach ($name in $projects.Keys) {
        $output = Join-Path $buildRoot $name
        Invoke-ProbeBuild @('publish',$projects[$name],'-c','Release','-r','win-x64','--self-contained','true','--no-restore','--nologo',
            '-p:UseSharedCompilation=false',"-p:BuildId=$($result.build_id)","-p:SourceSnapshotId=$snapshot",'-o',$output) "$name-publish.log"
        $result.outputs += [ordered]@{kind=$name;directory=$output;files=@(Get-ChildItem -LiteralPath $output -Recurse -File |
            Sort-Object FullName | ForEach-Object { [ordered]@{path=[IO.Path]::GetRelativePath($output,$_.FullName).Replace('\','/');
                bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()} })}
    }
    $after = @(Get-ProbeSources)
    if (($after | ConvertTo-Json -Compress -Depth 5) -cne ($sources | ConvertTo-Json -Compress -Depth 5)) { throw 'Probe sources changed during compilation.' }
    $sourceZip = Join-Path $buildRoot 'probe-source.zip'
    $zip = [IO.Compression.ZipFile]::Open($sourceZip, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in $sources) {
            $path = Join-Path $workspace $file.path
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $path, $file.path, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $zip.Dispose() }
    $zip = [IO.Compression.ZipFile]::OpenRead($sourceZip)
    try {
        if ($zip.Entries.Count -ne $sources.Count) { throw 'Source archive entry count differs.' }
        foreach ($file in $sources) {
            $entry = $zip.GetEntry($file.path)
            if (-not $entry -or $entry.Length -ne $file.bytes) { throw 'Source archive entry missing or changed.' }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
            if ($hash -cne $file.sha256) { throw 'Source archive readback hash differs.' }
        }
    } finally { $zip.Dispose() }
    $result.source_archive = [ordered]@{path=$sourceZip;sha256=(Get-FileHash -LiteralPath $sourceZip -Algorithm SHA256).Hash.ToLowerInvariant();readback='passed'}
    $result.status = 'passed_compilation_only'
} catch {
    $result.status = 'failed'
    $result.failure_type = $_.Exception.GetType().Name
    throw
} finally {
    $result | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $buildRoot 'build-result.json') -Encoding utf8NoBOM
    foreach ($name in $prior.Keys) { [Environment]::SetEnvironmentVariable($name,$prior[$name],'Process') }
    Write-Host "Compilation record: $buildRoot"
}
