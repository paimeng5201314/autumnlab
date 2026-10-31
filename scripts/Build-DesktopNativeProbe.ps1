[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=Join-Path $workspace 'artifacts/isolation-completion-20261030'
$build=Join-Path $root ('desktop-native-builds/probe-'+[Guid]::NewGuid().ToString('N'))
$project=Join-Path $root 'desktop-native-controller/DesktopNativeController.csproj'
$nativeSource=Join-Path $root 'native-user32-loader/NativeDesktopLoader.c'
$dotnet=Join-Path $workspace '.tools/dotnet/dotnet.exe'
$zigRoot=Join-Path $workspace '.tools/zig-0.15.2/zig-x86_64-windows-0.15.2'
$zig=Join-Path $zigRoot 'zig.exe'
foreach($path in @($project,$nativeSource,$dotnet,$zig)){if(-not [IO.File]::Exists($path)){throw "Required local input missing: $path"}}
[void][IO.Directory]::CreateDirectory($build)
$settings=@{
    TEMP=(Join-Path $build 'temp');TMP=(Join-Path $build 'temp')
    DOTNET_ROOT=(Join-Path $workspace '.tools/dotnet');DOTNET_CLI_HOME=(Join-Path $workspace '.tools/dotnet-home')
    NUGET_PACKAGES=(Join-Path $workspace '.tools/nuget/packages');NUGET_HTTP_CACHE_PATH=(Join-Path $build 'nuget-http')
    NUGET_SCRATCH=(Join-Path $build 'nuget-scratch');DOTNET_CLI_TELEMETRY_OPTOUT='1';DOTNET_NOLOGO='1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';DOTNET_GENERATE_ASPNET_CERTIFICATE='false';DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
    ZIG_GLOBAL_CACHE_DIR=(Join-Path $build 'zig-global');ZIG_LOCAL_CACHE_DIR=(Join-Path $build 'zig-local')
}
$prior=@{};foreach($name in $settings.Keys){$prior[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
function Invoke-LocalBuild([string[]]$Arguments,[string]$Log){
    & $dotnet @Arguments 2>&1|Tee-Object -FilePath (Join-Path $build $Log)|Out-Host
    if($LASTEXITCODE -ne 0){throw "Desktop native build failed: $Log ($LASTEXITCODE)"}
}
function Get-ProbeSources {
    $paths=@($PSCommandPath,(Join-Path $workspace 'scripts/Run-DesktopNativeProbe.ps1'),
        (Join-Path $root 'native-user32-loader/DESKTOP-ONLY-REVIEW.md'),
        (Join-Path $workspace 'scripts/Inspect-NativeLoaderImports.ps1'),$nativeSource,
        (Join-Path $workspace 'global.json'),(Join-Path $workspace 'NuGet.Config'),
        (Join-Path $workspace 'Directory.Build.props'),(Join-Path $workspace 'Directory.Packages.props'),(Join-Path $workspace 'build/Brand.props'))
    $directory=Split-Path $project -Parent
    $paths+=@(Get-ChildItem -LiteralPath $directory -File|Where-Object{$_.Extension -in '.cs','.csproj','.props','.md' -or $_.Name -eq 'packages.lock.json'}|Select-Object -ExpandProperty FullName)
    [xml]$xml=Get-Content -LiteralPath $project -Raw
    foreach($compile in $xml.SelectNodes('/Project/ItemGroup/Compile[@Include]')){
        if($compile.Include -match '[*?$]'){throw 'Explicit source inputs required.'}
        $paths+=[IO.Path]::GetFullPath((Join-Path $directory $compile.Include))
    }
    foreach($path in ($paths|Sort-Object -Unique)){
        $item=Get-Item -LiteralPath $path
        if(-not $item.FullName.StartsWith($workspace+'\',[StringComparison]::OrdinalIgnoreCase) -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Plain workspace source required.'}
        [ordered]@{path=[IO.Path]::GetRelativePath($workspace,$item.FullName).Replace('\','/');bytes=$item.Length;sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
}
function Get-OutputBindings([string]$Directory){
    @(Get-ChildItem -LiteralPath $Directory -Recurse -File|Sort-Object FullName|ForEach-Object{
        [ordered]@{path=[IO.Path]::GetRelativePath($Directory,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
}
$record=[ordered]@{schema_version=1;producer='派蒙';status='not_run';build_id=(Split-Path $build -Leaf);
    source_snapshot_id=$null;source_files=@();outputs=@();uac='not_run';profile_creation='not_run';desktop_creation='not_run';
    existing_object_ACL_changes='none';clipboard='not_run';browser_execution='not_run';product_acceptance='not_claimed';
    canonical_format='UTF-8 sorted workspace-relative paths; sha256 + two spaces + byte count + two spaces + path; LF; no final LF'}
try{
    foreach($name in $settings.Keys){[Environment]::SetEnvironmentVariable($name,$settings[$name],'Process')}
    foreach($name in @('TEMP','NUGET_HTTP_CACHE_PATH','NUGET_SCRATCH','ZIG_GLOBAL_CACHE_DIR','ZIG_LOCAL_CACHE_DIR')){[void][IO.Directory]::CreateDirectory($settings[$name])}
    $arguments=@('restore',$project,'--configfile',(Join-Path $workspace 'NuGet.Config'),'--nologo')
    if([IO.File]::Exists((Join-Path (Split-Path $project -Parent) 'packages.lock.json'))){$arguments+='--locked-mode'}else{$arguments+='--use-lock-file'}
    Invoke-LocalBuild $arguments 'restore.log'
    Invoke-LocalBuild @('restore',$project,'--locked-mode','--configfile',(Join-Path $workspace 'NuGet.Config'),'--nologo') 'locked-restore.log'
    $sources=@(Get-ProbeSources)
    $canonical=($sources|ForEach-Object{"$($_.sha256)  $($_.bytes)  $($_.path)"}) -join "`n"
    $snapshot='sha256:'+[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
    $record.source_snapshot_id=$snapshot;$record.source_files=$sources;$record.status='building'
    $output=Join-Path $build 'controller'
    Invoke-LocalBuild @('publish',$project,'-c','Release','-r','win-x64','--self-contained','true','--no-restore','--nologo',
        '-p:UseSharedCompilation=false',"-p:BuildId=$($record.build_id)","-p:SourceSnapshotId=$snapshot",'-o',$output) 'publish.log'
    $record.outputs+=@([ordered]@{kind='controller';directory=$output;files=(Get-OutputBindings $output)})
    $nativeOutput=Join-Path $build 'native';[void][IO.Directory]::CreateDirectory($nativeOutput)
    $nativeExe=Join-Path $nativeOutput 'AutumnOS.NativeDesktopLoader.exe'
    & $zig cc -target x86_64-windows-gnu -O1 -ffreestanding -fno-builtin -fno-stack-protector -nostdlib `
        -isystem (Join-Path $zigRoot 'lib/libc/include/any-windows-any') '-Wl,--entry=probe_entry' '-Wl,--subsystem,windows' `
        '-Wl,--dynamicbase' '-Wl,--nxcompat' -lkernel32 -Wall -Wextra -Werror $nativeSource -o $nativeExe 2>&1|
        Tee-Object -FilePath (Join-Path $build 'native-publish.log')|Out-Host
    if($LASTEXITCODE -ne 0){throw 'Native desktop DLL loader compilation failed.'}
    & (Join-Path $workspace 'scripts/Inspect-NativeLoaderImports.ps1') -ExecutablePath $nativeExe -ReportPath (Join-Path $build 'native-pe-inspection.json')
    $record.outputs+=@([ordered]@{kind='native';directory=$nativeOutput;files=(Get-OutputBindings $nativeOutput)})
    $record.native_compiler=[ordered]@{version=(& $zig version);sha256=(Get-FileHash -LiteralPath $zig -Algorithm SHA256).Hash.ToLowerInvariant()}
    if((@(Get-ProbeSources)|ConvertTo-Json -Depth 6 -Compress) -cne ($sources|ConvertTo-Json -Depth 6 -Compress)){throw 'Source bytes changed during build.'}
    $archive=Join-Path $build 'probe-source.zip'
    $zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
    try{foreach($file in $sources){[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $workspace $file.path),$file.path,[IO.Compression.CompressionLevel]::Optimal)}}finally{$zip.Dispose()}
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try{
        if($zip.Entries.Count -ne $sources.Count){throw 'Source ZIP file count mismatch.'}
        foreach($file in $sources){
            $entry=$zip.GetEntry($file.path);if(-not $entry -or $entry.Length -ne $file.bytes){throw 'Source ZIP entry mismatch.'}
            $stream=$entry.Open();try{$sha=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
            if($sha -cne $file.sha256){throw 'Source ZIP actual-byte hash mismatch.'}
        }
    }finally{$zip.Dispose()}
    $record.source_archive=[ordered]@{path=$archive;sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant();readback='passed'}
    $record.status='passed_compilation_only'
}catch{$record.status='failed';$record.failure_type=$_.Exception.GetType().Name;throw}
finally{
    $record|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $build 'build-result.json') -Encoding utf8NoBOM
    foreach($name in $prior.Keys){[Environment]::SetEnvironmentVariable($name,$prior[$name],'Process')}
    Write-Host "Desktop native build record: $build"
}
