[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=Join-Path $workspace 'artifacts/isolation-completion-20261030'
$projects=[ordered]@{
    core=(Join-Path $root 'session-browser-prototype/SessionBrowserPrototype.csproj')
    ui=(Join-Path $root 'session-browser-ui/SessionBrowserUI.csproj')
}
foreach($project in $projects.Values){if(-not [IO.File]::Exists($project)){throw "Session prototype source project missing: $project"}}
$build=Join-Path $root ('session-browser-builds/probe-'+[Guid]::NewGuid().ToString('N'))
$dotnet=Join-Path $workspace '.tools/dotnet/dotnet.exe'
[void][IO.Directory]::CreateDirectory($build)
$settings=@{
    TEMP=(Join-Path $build 'temp');TMP=(Join-Path $build 'temp')
    DOTNET_ROOT=(Join-Path $workspace '.tools/dotnet');DOTNET_CLI_HOME=(Join-Path $workspace '.tools/dotnet-home')
    NUGET_PACKAGES=(Join-Path $workspace '.tools/nuget/packages');NUGET_HTTP_CACHE_PATH=(Join-Path $build 'nuget-http')
    NUGET_SCRATCH=(Join-Path $build 'nuget-scratch');DOTNET_CLI_TELEMETRY_OPTOUT='1';DOTNET_NOLOGO='1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE='1';DOTNET_GENERATE_ASPNET_CERTIFICATE='false';DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
}
$prior=@{};foreach($name in $settings.Keys){$prior[$name]=[Environment]::GetEnvironmentVariable($name,'Process')}
function Invoke-LocalBuild([string[]]$Arguments,[string]$Log){
    & $dotnet @Arguments 2>&1|Tee-Object -FilePath (Join-Path $build $Log)|Out-Host
    if($LASTEXITCODE -ne 0){throw "Session prototype build failed: $Log ($LASTEXITCODE)"}
}
function Get-ProbeSources {
    $paths=@($PSCommandPath,(Join-Path $workspace 'scripts/Run-SessionBrowserPrototype.ps1'),(Join-Path $workspace 'scripts/Read-SessionBrowserLiveReport.ps1'),
        (Join-Path $workspace 'global.json'),(Join-Path $workspace 'NuGet.Config'),(Join-Path $workspace 'Directory.Build.props'),
        (Join-Path $workspace 'Directory.Packages.props'),(Join-Path $workspace 'build/Brand.props'),
        (Join-Path $workspace 'docs/adr-0010-native-ime-control.md'),
        (Join-Path $workspace 'src/AutumnOS.Runtime/RuntimeIsolationPolicy.cs'))
    foreach($project in $projects.Values){
        $directory=Split-Path $project -Parent
        $paths+=@(Get-ChildItem -LiteralPath $directory -File|Where-Object{$_.Extension -in '.cs','.csproj','.props','.md','.xaml','.manifest','.js','.css','.html' -or $_.Name -eq 'packages.lock.json'}|Select-Object -ExpandProperty FullName)
        [xml]$xml=Get-Content -LiteralPath $project -Raw
        foreach($node in $xml.SelectNodes('/Project/ItemGroup/Compile[@Include] | /Project/ItemGroup/EmbeddedResource[@Include] | /Project/ItemGroup/Content[@Include] | /Project/ItemGroup/ApplicationDefinition[@Include]')){
            if($node.Include -match '[*?$]'){throw 'Explicit local source paths required for prototype snapshot.'}
            $paths+=[IO.Path]::GetFullPath((Join-Path $directory $node.Include))
        }
    }
    foreach($path in ($paths|Sort-Object -Unique)){
        $item=Get-Item -LiteralPath $path
        if(-not $item.FullName.StartsWith($workspace+'\',[StringComparison]::OrdinalIgnoreCase) -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Plain workspace prototype source required.'}
        [ordered]@{path=[IO.Path]::GetRelativePath($workspace,$item.FullName).Replace('\','/');bytes=$item.Length;sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
}
$record=[ordered]@{schema_version=1;producer='派蒙';status='not_run';build_id=(Split-Path $build -Leaf);
    source_snapshot_id=$null;source_files=@();outputs=@();netonly_execution='not_run';profile_creation='not_run';
    window_station_creation='not_run';browser_execution='not_run';interactive_ui='not_run';clipboard_operations='not_run';
    production_integration='not_run';complete_isolation='not_claimed';
    canonical_format='UTF-8 sorted workspace-relative paths; sha256 + two spaces + byte count + two spaces + path; LF; no final LF'}
try{
    foreach($name in $settings.Keys){[Environment]::SetEnvironmentVariable($name,$settings[$name],'Process')}
    foreach($name in @('TEMP','NUGET_HTTP_CACHE_PATH','NUGET_SCRATCH')){[void][IO.Directory]::CreateDirectory($settings[$name])}
    foreach($name in $projects.Keys){
        $arguments=@('restore',$projects[$name],'--configfile',(Join-Path $workspace 'NuGet.Config'),'--nologo')
        if([IO.File]::Exists((Join-Path (Split-Path $projects[$name] -Parent) 'packages.lock.json'))){$arguments+='--locked-mode'}else{$arguments+='--use-lock-file'}
        Invoke-LocalBuild $arguments "$name-restore.log"
        Invoke-LocalBuild @('restore',$projects[$name],'--locked-mode','--configfile',(Join-Path $workspace 'NuGet.Config'),'--nologo') "$name-locked-restore.log"
    }
    $production=[IO.File]::ReadAllText((Join-Path $workspace 'src/AutumnOS.Runtime/RuntimeIsolationPolicy.cs'))
    $policy=Join-Path $root 'clipboard-appcontainer-probe/FrozenPolicy.cs'
    $frozen=[IO.File]::ReadAllText($policy)
    foreach($name in @('PolicyId','RequiredBrowserVersion','BrowserArguments','ContentSecurityPolicy','ConnectionAllowlist','DnsPrefetchControl','PermissionsPolicy')){
        $pattern='(?m)const string '+[regex]::Escape($name)+' = ("[^\r\n]*");'
        $a=[regex]::Match($production,$pattern);$b=[regex]::Match($frozen,$pattern)
        if(-not $a.Success -or -not $b.Success -or $a.Groups[1].Value -cne $b.Groups[1].Value){throw "Prototype fixed policy differs: $name"}
    }
    $sources=@(Get-ProbeSources)
    $canonical=($sources|ForEach-Object{"$($_.sha256)  $($_.bytes)  $($_.path)"}) -join "`n"
    $snapshot='sha256:'+[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
    $record.source_snapshot_id=$snapshot;$record.source_files=$sources;$record.status='building'
    foreach($name in $projects.Keys){
        $output=Join-Path $build $name
        Invoke-LocalBuild @('publish',$projects[$name],'-c','Release','-r','win-x64','--self-contained','true','--no-restore','--nologo',
            '-p:UseSharedCompilation=false',"-p:BuildId=$($record.build_id)","-p:SourceSnapshotId=$snapshot",'-o',$output) "$name-publish.log"
        $runtimeConfig=Get-ChildItem -LiteralPath $output -File -Filter '*.runtimeconfig.json'
        if(@($runtimeConfig).Count -ne 1){throw 'One exact runtime configuration required.'}
        $frameworks=(Get-Content -LiteralPath $runtimeConfig.FullName -Raw|ConvertFrom-Json).runtimeOptions.includedFrameworks
        $coreFramework=@($frameworks|Where-Object name -CEQ 'Microsoft.NETCore.App')
        if($coreFramework.Count -ne 1 -or $coreFramework[0].version -cne '10.0.12'){throw 'Actual published .NET10.0.12 runtime required.'}
        if($name -ceq 'ui'){
            foreach($resource in @('AutumnOS.SessionBrowserUI.pri','App.xbf')){
                $resourcePath=Join-Path $output $resource
                if(-not [IO.File]::Exists($resourcePath) -or (Get-Item -LiteralPath $resourcePath).Length -lt 1){throw "Generated native WinUI application resource missing: $resource"}
            }
        }
        $record.outputs+=@([ordered]@{kind=$name;directory=$output;files=@(Get-ChildItem -LiteralPath $output -Recurse -File|Sort-Object FullName|ForEach-Object{
            [ordered]@{path=[IO.Path]::GetRelativePath($output,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
        })})
    }
    $checks=@()
    foreach($name in @('core','ui')){
        $entry=if($name -ceq 'core'){'AutumnOS.SessionBrowserPrototype.exe'}else{'AutumnOS.SessionBrowserUI.exe'}
        $start=[Diagnostics.ProcessStartInfo]::new((Join-Path (Join-Path $build $name) $entry));$start.UseShellExecute=$false;$start.CreateNoWindow=$true
        $process=[Diagnostics.Process]::new();$process.StartInfo=$start
        try{if(-not $process.Start()){throw 'Compiled-only entry failed to launch.'};$process.WaitForExit();if($process.ExitCode -ne 0){throw "Compiled-only $name entry failed ($($process.ExitCode))."};$checks+=@([ordered]@{kind=$name;no_argument_exit=$process.ExitCode})}finally{$process.Dispose()}
    }
    $testPath=Join-Path $build 'protocol-self-tests.json'
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $build 'core/AutumnOS.SessionBrowserPrototype.exe'));$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.ArgumentList.Add('--self-test');$start.ArgumentList.Add($testPath)
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    try{if(-not $process.Start()){throw 'Protocol self-test entry failed to launch.'};$process.WaitForExit();if($process.ExitCode -ne 0){throw 'Protocol self-tests failed.'}}finally{$process.Dispose()}
    $tests=Get-Content -LiteralPath $testPath -Raw|ConvertFrom-Json
    $fileChecks=@($tests.cases|Where-Object{$_.name -clike 'owned_report_*'})
    $pureChecks=@($tests.cases|Where-Object{$_.name -cnotlike 'owned_report_*'})
    $keyChecks=@($pureChecks|Where-Object{$_.name -clike 'native_key_capture_*'})
    $basePureChecks=@($pureChecks|Where-Object{$_.name -cnotlike 'native_key_capture_*'})
    if($tests.status -cne 'passed' -or $tests.failed -ne 0 -or $tests.passed -lt 43 -or $fileChecks.Count -lt 8 -or $pureChecks.Count -lt 35 -or $basePureChecks.Count -lt 29 -or $keyChecks.Count -lt 6){throw 'Actual protocol, native-key capture, composition, native-control comparison and owned file-publication tests required.'}
    $record.verification=[ordered]@{compiled_only=$checks;protocol_test_path=$testPath;protocol_test_sha256=(Get-FileHash -LiteralPath $testPath -Algorithm SHA256).Hash.ToLowerInvariant();passed=$tests.passed;failed=$tests.failed;pure_checks=$pureChecks.Count;base_pure_checks=$basePureChecks.Count;native_key_capture_checks=$keyChecks.Count;owned_file_IO_checks=$fileChecks.Count}
    if((@(Get-ProbeSources)|ConvertTo-Json -Depth 6 -Compress) -cne ($sources|ConvertTo-Json -Depth 6 -Compress)){throw 'Prototype sources changed during compilation.'}
    $archive=Join-Path $build 'probe-source.zip';$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
    try{foreach($file in $sources){[void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $workspace $file.path),$file.path,[IO.Compression.CompressionLevel]::Optimal)}}finally{$zip.Dispose()}
    $zip=[IO.Compression.ZipFile]::OpenRead($archive)
    try{
        if($zip.Entries.Count -ne $sources.Count){throw 'Prototype source ZIP count differs.'}
        foreach($file in $sources){$entry=$zip.GetEntry($file.path);if(-not $entry -or $entry.Length -ne $file.bytes){throw 'Prototype source ZIP entry differs.'};$stream=$entry.Open();try{$sha=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()};if($sha -cne $file.sha256){throw 'Prototype source ZIP byte hash differs.'}}
    }finally{$zip.Dispose()}
    $record.source_archive=[ordered]@{path=$archive;sha256=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant();readback='passed'}
    $record.status='passed_compilation_only'
}catch{$record.status='failed';$record.failure_type=$_.Exception.GetType().Name;throw}
finally{
    $record|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $build 'build-result.json') -Encoding utf8NoBOM
    foreach($name in $prior.Keys){[Environment]::SetEnvironmentVariable($name,$prior[$name],'Process')}
    Write-Host "Session browser prototype build record: $build"
}
