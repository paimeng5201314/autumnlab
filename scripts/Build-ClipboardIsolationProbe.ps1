[CmdletBinding()]
param([switch]$NativeLoader,[switch]$StationReadMatrix)
$ErrorActionPreference = 'Stop'
if($StationReadMatrix){$NativeLoader=$true}
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = Join-Path $workspace 'artifacts/isolation-completion-20261030'
$buildRoot = Join-Path $root ('clipboard-builds/probe-' + [Guid]::NewGuid().ToString('N'))
$projects = [ordered]@{
    lease = (Join-Path $root 'clipboard-station-lease/ClipboardStationLease.csproj')
    probe = (Join-Path $root 'clipboard-appcontainer-probe/ClipboardAppContainerProbe.csproj')
    controller = (Join-Path $root 'clipboard-appcontainer-controller/ClipboardController.csproj')
}
foreach ($project in $projects.Values) { if (-not [IO.File]::Exists($project)) { throw "Clipboard probe project not ready: $project" } }
$dotnet = Join-Path $workspace '.tools/dotnet/dotnet.exe'
[void][IO.Directory]::CreateDirectory($buildRoot)
$settings = @{
    TEMP=(Join-Path $buildRoot 'temp'); TMP=(Join-Path $buildRoot 'temp')
    DOTNET_ROOT=(Join-Path $workspace '.tools/dotnet'); DOTNET_CLI_HOME=(Join-Path $workspace '.tools/dotnet-home')
    NUGET_PACKAGES=(Join-Path $workspace '.tools/nuget/packages'); NUGET_HTTP_CACHE_PATH=(Join-Path $buildRoot 'nuget-http')
    NUGET_SCRATCH=(Join-Path $buildRoot 'nuget-scratch'); DOTNET_CLI_TELEMETRY_OPTOUT='1'; DOTNET_NOLOGO='1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1'; DOTNET_GENERATE_ASPNET_CERTIFICATE='false'; DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
    ZIG_GLOBAL_CACHE_DIR=(Join-Path $buildRoot 'zig-global'); ZIG_LOCAL_CACHE_DIR=(Join-Path $buildRoot 'zig-local')
}
$prior=@{}; foreach($name in $settings.Keys){$prior[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
function Invoke-ClipboardBuild([string[]]$Arguments,[string]$Log) {
    & $dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $buildRoot $Log) | Out-Host
    if($LASTEXITCODE -ne 0){throw "Clipboard probe compilation failed: $Log ($LASTEXITCODE)"}
}
function Get-ClipboardSources {
    $paths=@($PSCommandPath,(Join-Path $workspace 'scripts/Run-ClipboardIsolationProbe.ps1'),
        (Join-Path $workspace 'NuGet.Config'),(Join-Path $workspace 'global.json'),
        (Join-Path $workspace 'Directory.Build.props'),(Join-Path $workspace 'Directory.Packages.props'),
        (Join-Path $workspace 'build/Brand.props'),(Join-Path $workspace 'src/AutumnOS.Runtime/RuntimeIsolationPolicy.cs'))
    foreach($project in $projects.Values){
        $directory=Split-Path $project -Parent
        $paths+=@(Get-ChildItem -LiteralPath $directory -File | Where-Object{
            $_.Extension -in '.cs','.csproj','.props','.js','.html','.md' -or $_.Name -eq 'packages.lock.json'
        } | Select-Object -ExpandProperty FullName)
        [xml]$projectXml=Get-Content -LiteralPath $project -Raw
        foreach($compile in $projectXml.SelectNodes('/Project/ItemGroup/Compile[@Include]')){
            if($compile.Include -match '[*?$]'){throw 'Use explicit local source paths for this probe snapshot.'}
            $paths+=[IO.Path]::GetFullPath((Join-Path $directory $compile.Include))
        }
    }
    if($NativeLoader){
        $paths+=(Join-Path $workspace 'scripts/Inspect-NativeLoaderImports.ps1')
        $paths+=(Join-Path $workspace 'scripts/Test-NativeLoaderEntry.ps1')
        $paths+=@(Get-ChildItem -LiteralPath (Join-Path $root 'native-user32-loader') -File|Where-Object{$_.Extension -in '.c','.h','.md'}|Select-Object -ExpandProperty FullName)
    }
    foreach($path in ($paths|Sort-Object -Unique)){
        $item=Get-Item -LiteralPath $path
        if(-not $item.FullName.StartsWith($workspace+'\',[StringComparison]::OrdinalIgnoreCase) -or
            ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Only plain workspace probe sources may enter the snapshot.'}
        [ordered]@{path=[IO.Path]::GetRelativePath($workspace,$item.FullName).Replace('\','/');bytes=$item.Length;
            sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
}
$result=[ordered]@{schema_version=1;producer='派蒙';status='not_run';build_id=(Split-Path $buildRoot -Leaf);
    source_snapshot_id=$null;source_files=@();outputs=@();uac='not_run';profile_creation='not_run';station_creation='not_run';
    clipboard='not_run';browser_execution='not_run';product_integration='not_run';
    native_loader_variant=[bool]$NativeLoader;station_read_matrix_variant=[bool]$StationReadMatrix;
    canonical_format='UTF-8 sorted workspace-relative paths; sha256 + two spaces + byte count + two spaces + path; LF; no final LF'}
try{
    foreach($name in $settings.Keys){[Environment]::SetEnvironmentVariable($name,$settings[$name],'Process')}
    foreach($name in @('TEMP','NUGET_HTTP_CACHE_PATH','NUGET_SCRATCH','ZIG_GLOBAL_CACHE_DIR','ZIG_LOCAL_CACHE_DIR')){[void][IO.Directory]::CreateDirectory($settings[$name])}
    $production=[IO.File]::ReadAllText((Join-Path $workspace 'src/AutumnOS.Runtime/RuntimeIsolationPolicy.cs'))
    $frozen=[IO.File]::ReadAllText((Join-Path $root 'clipboard-appcontainer-probe/FrozenPolicy.cs'))
    foreach($name in @('PolicyId','RequiredBrowserVersion','BrowserArguments','ContentSecurityPolicy','ConnectionAllowlist','DnsPrefetchControl','PermissionsPolicy')){
        $pattern='(?m)const string '+[regex]::Escape($name)+' = ("[^\r\n]*");'
        $a=[regex]::Match($production,$pattern);$b=[regex]::Match($frozen,$pattern)
        if(-not $a.Success -or -not $b.Success -or $a.Groups[1].Value -cne $b.Groups[1].Value){throw "Frozen clipboard page policy differs: $name"}
    }
    foreach($name in $projects.Keys){
        $lock=Join-Path (Split-Path $projects[$name] -Parent) 'packages.lock.json'
        $arguments=@('restore',$projects[$name],'--configfile',(Join-Path $workspace 'NuGet.Config'),'--nologo')
        if([IO.File]::Exists($lock)){$arguments+='--locked-mode'}else{$arguments+='--use-lock-file'}
        Invoke-ClipboardBuild $arguments "$name-restore.log"
        Invoke-ClipboardBuild @('restore',$projects[$name],'--locked-mode','--configfile',(Join-Path $workspace 'NuGet.Config'),'--nologo') "$name-locked-restore.log"
    }
    $sources=@(Get-ClipboardSources)
    $canonical=($sources|ForEach-Object{"$($_.sha256)  $($_.bytes)  $($_.path)"}) -join "`n"
    $snapshot='sha256:'+[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
    $result.source_snapshot_id=$snapshot;$result.source_files=$sources;$result.status='building'
    $result|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $buildRoot 'build-result.json') -Encoding utf8NoBOM
    foreach($name in $projects.Keys){
        $output=Join-Path $buildRoot $name
        Invoke-ClipboardBuild @('publish',$projects[$name],'-c','Release','-r','win-x64','--self-contained','true','--no-restore','--nologo',
            '-p:UseSharedCompilation=false',"-p:BuildId=$($result.build_id)","-p:SourceSnapshotId=$snapshot",'-o',$output) "$name-publish.log"
        $result.outputs+=[ordered]@{kind=$name;directory=$output;files=@(Get-ChildItem -LiteralPath $output -Recurse -File|Sort-Object FullName|ForEach-Object{
            [ordered]@{path=[IO.Path]::GetRelativePath($output,$_.FullName).Replace('\','/');bytes=$_.Length;
                sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
        })}
    }
    if($NativeLoader){
        $output=Join-Path $buildRoot 'native';[void][IO.Directory]::CreateDirectory($output)
        $zigRoot=Join-Path $workspace '.tools/zig-0.15.2/zig-x86_64-windows-0.15.2'
        $zig=Join-Path $zigRoot 'zig.exe'
        $nativeEntry=if($StationReadMatrix){'AutumnOS.NativeStationReadLoader.exe'}else{'AutumnOS.NativeUser32Loader.exe'}
        $nativeSource=if($StationReadMatrix){'native-user32-loader/StationReadLoader.c'}else{'native-user32-loader/NativeUser32Loader.c'}
        $nativeExe=Join-Path $output $nativeEntry
        & $zig cc -target x86_64-windows-gnu -O1 -ffreestanding -fno-builtin -fno-stack-protector -nostdlib `
            -isystem (Join-Path $zigRoot 'lib/libc/include/any-windows-any') `
            '-Wl,--entry=probe_entry' '-Wl,--subsystem,windows' '-Wl,--dynamicbase' '-Wl,--nxcompat' `
            -lkernel32 -Wall -Wextra -Werror (Join-Path $root $nativeSource) -o $nativeExe 2>&1|
            Tee-Object -FilePath (Join-Path $buildRoot 'native-publish.log')|Out-Host
        if($LASTEXITCODE -ne 0){throw 'Native KERNEL32-only diagnostic compilation failed.'}
        & (Join-Path $workspace 'scripts/Inspect-NativeLoaderImports.ps1') -ExecutablePath $nativeExe -ReportPath (Join-Path $buildRoot 'native-pe-inspection.json')
        $result.native_compiler=[ordered]@{version=(& $zig version);sha256=(Get-FileHash -LiteralPath $zig -Algorithm SHA256).Hash.ToLowerInvariant()}
        $result.outputs+=[ordered]@{kind='native';directory=$output;files=@(Get-ChildItem -LiteralPath $output -Recurse -File|Sort-Object FullName|ForEach-Object{
            [ordered]@{path=[IO.Path]::GetRelativePath($output,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
        })}
    }
    if((@(Get-ClipboardSources)|ConvertTo-Json -Depth 5 -Compress) -cne ($sources|ConvertTo-Json -Depth 5 -Compress)){throw 'Clipboard probe sources changed while building.'}
    $archivePath=Join-Path $buildRoot 'probe-source.zip'
    $zip=[IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Create)
    try{foreach($file in $sources){[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $workspace $file.path),$file.path,[IO.Compression.CompressionLevel]::Optimal)}}finally{$zip.Dispose()}
    $zip=[IO.Compression.ZipFile]::OpenRead($archivePath)
    try{
        if($zip.Entries.Count -ne $sources.Count){throw 'Clipboard source archive count differs.'}
        foreach($file in $sources){
            $entry=$zip.GetEntry($file.path);if(-not $entry -or $entry.Length -ne $file.bytes){throw 'Clipboard source archive entry differs.'}
            $stream=$entry.Open();try{$hash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
            if($hash -cne $file.sha256){throw 'Clipboard source archive hash differs.'}
        }
    }finally{$zip.Dispose()}
    $result.source_archive=[ordered]@{path=$archivePath;sha256=(Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant();readback='passed'}
    $result.status='passed_compilation_only'
}catch{
    $result.status='failed';$result.failure_type=$_.Exception.GetType().Name;throw
}finally{
    $result|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $buildRoot 'build-result.json') -Encoding utf8NoBOM
    foreach($name in $prior.Keys){[Environment]::SetEnvironmentVariable($name,$prior[$name],'Process')}
    Write-Host "Clipboard compilation record: $buildRoot"
}
