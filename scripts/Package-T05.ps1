[CmdletBinding()]
param([ValidateSet('Prepare','Finalize','Full')][string]$Stage='Full', [switch]$SkipNativeRegression, [switch]$IncludeSetup)
. "$PSScriptRoot/Common.ps1"
Push-Location $ProjectRoot
try {
    $buildId=(Get-Content artifacts/latest-build.txt -Raw).Trim()
    $evidence=Join-Path $ProjectRoot "artifacts/builds/$buildId"
    $build=Get-Content (Join-Path $evidence 'build-result.json') -Raw|ConvertFrom-Json
    if($build.status -ne 'passed' -or $buildId -notmatch '^T0[56]-'){throw 'Matching successful T05/T06 build required.'}
    $run=Join-Path $evidence ('package-t05-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $run | Out-Null
    $snapshot=& "$PSScriptRoot/New-SourceSnapshot.ps1" -OutputDirectory $run -AdditionalInputs @($build.additional_inputs)
    if($snapshot -ne $build.source_snapshot_id){throw 'Sources changed since build.'}
    $variant=if($build.test_build){'local-update-drill'}else{'development'}
    $destination=Join-Path $ProjectRoot "artifacts/packages/$buildId/AutumnOS-$($build.version)-win-x64-$variant"
    $exe=Join-Path $destination 'AutumnOS.exe'
    $properties=@($build.build_properties)+@("-p:BuildId=$buildId","-p:SourceSnapshotId=$snapshot")
    if($Stage -ne 'Finalize'){
        if(Test-Path -LiteralPath $destination){throw 'Existing delivery directory preserved; use a new BuildId.'}
        $binary=Join-Path $ProjectRoot (Split-Path $build.output_path)
        $hashes=Get-Content (Join-Path $evidence 'artifact-hashes.json') -Raw|ConvertFrom-Json
        foreach($file in $hashes){if((Get-FileHash -LiteralPath (Join-Path $ProjectRoot $file.path)).Hash.ToLowerInvariant() -ne $file.sha256){throw 'Tracked output changed.'}}
        Invoke-Dotnet -Arguments (@('publish','src/AutumnOS.Shell/AutumnOS.Shell.csproj','-c','Release','-p:Platform=x64','--no-build','--no-restore','-o',$destination)+$properties) -LogPath (Join-Path $run 'publish-client.log')
        foreach($file in $hashes|Where-Object {[IO.Path]::GetExtension($_.path) -in @('.pri','.xbf') -and [IO.Path]::GetFileName($_.path) -ne 'AutumnOS.pri'}){
            $source=Join-Path $ProjectRoot $file.path;$relative=[IO.Path]::GetRelativePath($binary,$source)
            if($relative.StartsWith('..')){throw 'Resource outside tracked client.'}
            $target=Join-Path $destination $relative;New-Item -ItemType Directory -Force -Path (Split-Path $target)|Out-Null
            Copy-Item -LiteralPath $source -Destination $target
        }
        foreach($file in Get-ChildItem -LiteralPath $destination -Recurse -File){
            $source=Join-Path $binary ([IO.Path]::GetRelativePath($destination,$file.FullName))
            if((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash){throw 'Client publish byte mismatch.'}
        }
        foreach($part in @('Bootstrap','Updater')){
            $out=Join-Path $run $part
            Invoke-Dotnet -Arguments (@('publish',"src/AutumnOS.$part/AutumnOS.$part.csproj",'-c','Release','-p:Platform=x64','--no-build','--no-restore','-o',$out)+$properties) -LogPath (Join-Path $run "publish-$part.log")
            $name=if($part -eq 'Bootstrap'){'AutumnOS.exe'}else{'AutumnOS.Updater.exe'}
            Copy-Item -LiteralPath (Join-Path $out $name) -Destination (Join-Path $destination $name)
        }
        $runtime=Join-Path $ProjectRoot '.tools/webview2/154.0.4258.53/Microsoft.WebView2.FixedVersionRuntime.154.0.4258.53.x64'
        if(-not (Test-Path -LiteralPath (Join-Path $runtime 'msedgewebview2.exe'))){throw 'Fixed WebView2 runtime missing: run Cache-WebViewRuntime.ps1 (local extraction only).'}
        if((Get-AuthenticodeSignature -LiteralPath (Join-Path $runtime 'msedgewebview2.exe')).Status -ne 'Valid'){throw 'Fixed WebView2 signature invalid.'}
        Copy-Item -LiteralPath $runtime -Destination (Join-Path $destination 'WebView2Runtime') -Recurse
        $licenses=Join-Path $destination 'ThirdPartyLicenses';New-Item -ItemType Directory -Path $licenses|Out-Null
        foreach($package in Get-ChildItem -LiteralPath $env:NUGET_PACKAGES -Directory){foreach($version in Get-ChildItem -LiteralPath $package.FullName -Directory){foreach($license in Get-ChildItem -LiteralPath $version.FullName -File|Where-Object Name -Match '^(license|third.?party|notice)'){
            Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $licenses ($package.Name+'-'+$version.Name+'-'+$license.Name))
        }}}
        Copy-Item -LiteralPath '.tools/dotnet/LICENSE.txt','.tools/dotnet/ThirdPartyNotices.txt','docs/dependencies.md' -Destination $licenses
        if($buildId.StartsWith('T06-')){
            & "$PSScriptRoot/Package-DeveloperKit.ps1" -Destination (Join-Path $destination 'Developer') -EvidenceDirectory $run -Build $build
        }
        @"
Lab Chronicles AutumnOS · 制作人：派蒙
$($buildId.Split('-')[0]) $variant · 本地联调审阅包 · Windows Authenticode 未签名
Build $buildId
Source $snapshot
保留整个目录，双击 AutumnOS.exe；AutumnOS.Client.exe 由稳定入口启动。
数据在原目录的 AutumnOS_Data。升级仅修改清单内程序文件，备份保存在 .autumnos-update。
设置 > 系统更新。自动更新默认开，预览默认关；正式公钥未配置时拒绝自动安装。
本目录包含 .NET / Windows App SDK / 固定 WebView2 Runtime。没有安装系统组件。
本地更新演练仅在明确标识的独立构建启用；生产来源固定 paimeng5201314/autumnlab。
完整 T03 收尾及安装版暂缓；T01/T02/T04 未完成验收保留；无公开发布授权。
"@ | Set-Content -LiteralPath (Join-Path $destination 'READ-ME.txt') -Encoding utf8
        # This allowlist comes from the exact delivery bytes, excluding stable and private paths.
        $inventory=Join-Path $run 'managed-files.json'
        Invoke-Dotnet -Arguments @('tools/AutumnOS.Update.Cli/bin/x64/Release/net10.0/win-x64/AutumnOS.Update.Cli.dll','inventory','--source',$destination,'--output',$inventory) -LogPath (Join-Path $run 'inventory.log')
        $managed=@(Get-Content -LiteralPath $inventory -Raw|ConvertFrom-Json)
        [ordered]@{schemaVersion=1;version=$build.version;buildId=$buildId;files=$managed}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $destination 'autumn.install.json') -Encoding utf8NoBOM
        $delivery=@(Get-ChildItem -LiteralPath $destination -Recurse -File|ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($destination,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})
        $delivery|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $evidence 'delivery-hashes.json') -Encoding utf8
    }
    foreach($required in @('AutumnOS.exe','AutumnOS.Updater.exe','AutumnOS.Client.exe','AutumnOS.Client.pri','App.xbf','MainWindow.xbf','coreclr.dll','Microsoft.UI.Xaml.dll','WebView2Runtime/msedgewebview2.exe','autumn.install.json')){
        if(-not(Test-Path -LiteralPath (Join-Path $destination $required))){throw "Missing delivery dependency: $required"}
    }
    $delivery=Get-Content (Join-Path $evidence 'delivery-hashes.json') -Raw|ConvertFrom-Json
    foreach($file in $delivery){if((Get-FileHash -LiteralPath (Join-Path $destination $file.path)).Hash.ToLowerInvariant() -ne $file.sha256){throw 'Frozen delivery bytes changed.'}}
    $report=[ordered]@{status='prepared';build_id=$buildId;source_snapshot_id=$snapshot;executable=$exe;executable_sha256=(Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant();test_build=$build.test_build;native_tests='not_run';zip='not_created';setup='not_created';public_release=$false;authenticode='not_signed';fixed_webview2='154.0.4258.53'}
    $report|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $evidence 'package-prepared.json') -Encoding utf8
    if($Stage -eq 'Prepare'){$report|ConvertTo-Json;return}
    foreach($name in @('tests','sdk-tests','sample-tests','native-client-tests','server-http-tests','t04-tools-tests')){
        $test=Get-Content (Join-Path $evidence "$name.json") -Raw|ConvertFrom-Json
        if($test.status -ne 'passed' -or $test.build_id -ne $buildId -or $test.source_snapshot_id -ne $snapshot){throw "Passing current build tests required: $name"}
    }
    if($buildId.StartsWith('T06-')){
        $contracts=Get-Content (Join-Path $evidence 'machine-contracts.json') -Raw|ConvertFrom-Json
        if($contracts.status -ne 'passed' -or $contracts.build_id -ne $buildId -or $contracts.source_snapshot_id -ne $snapshot){throw 'Passing actual T06 machine contract checks required.'}
    }
    if(-not $SkipNativeRegression){
        & "$PSScriptRoot/Test-T04StoreSmoke.ps1" -ExecutablePath $exe -ReportDirectory (Join-Path $run 'store')
        & "$PSScriptRoot/Test-DesktopInteractionSmoke.ps1" -ExecutablePath $exe -ReportDirectory (Join-Path $run 'desktop')
        & "$PSScriptRoot/Test-DeliveredLauncher.ps1" -ExecutablePath $exe -ReportDirectory (Join-Path $run 'final-path')
        $report.native_tests='passed'
    }
    if(-not $build.test_build -and $SkipNativeRegression){throw 'Ordinary deliverable requires native regression; test variants may be exercised by dedicated update drill.'}
    $zipPath="$destination.zip";if(Test-Path -LiteralPath $zipPath){throw 'Existing ZIP preserved.'}
    $zip=[IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
    try{foreach($file in $delivery){
        if($file.path -match '(^|/)(AutumnOS_Data|\.autumnos-update)(/|$)|\.(pk8|pem|key|pfx|p12|snk|dpapi)$'){throw 'Private data in distribution allowlist.'}
        if((Get-FileHash -LiteralPath (Join-Path $destination $file.path)).Hash.ToLowerInvariant() -ne $file.sha256){throw 'Byte mismatch before ZIP.'}
        $null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $destination $file.path),$file.path,[IO.Compression.CompressionLevel]::Optimal)
    }}finally{$zip.Dispose()}
    $zip=[IO.Compression.ZipFile]::OpenRead($zipPath)
    try{if($zip.Entries.Count -ne $delivery.Count){throw 'ZIP file count mismatch.'};foreach($file in $delivery){$stream=$zip.GetEntry($file.path).Open();try{$sha=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()};if($sha -ne $file.sha256){throw 'ZIP readback mismatch.'}}}finally{$zip.Dispose()}
    $report.status='packaged';$report.zip=$zipPath;$report.zip_sha256=(Get-FileHash -LiteralPath $zipPath).Hash.ToLowerInvariant();$report.zip_entries=$delivery.Count
    # Producer deferred installer delivery on 2026-10-02. Keep the existing implementation
    # available for a later explicitly resumed installer task; portable delivery is the default.
    $report.setup='deferred_by_user';$report.setup_execution='not_run_user_deferred';$report.evidence=$run
    if($IncludeSetup){
        $setupOut=Join-Path $run 'setup'
        Invoke-Dotnet -Arguments (@('publish','src/AutumnOS.Setup/AutumnOS.Setup.csproj','-c','Release','-p:Platform=x64','--no-restore','-o',$setupOut,"-p:SetupPayloadPath=$zipPath","-p:SetupPayloadSha256=$($report.zip_sha256)")+$properties) -LogPath (Join-Path $run 'setup-build.log')
        $setup=Join-Path (Split-Path $destination) "AutumnOS-$($build.version)-$variant-setup.exe"
        Copy-Item -LiteralPath (Join-Path $setupOut 'AutumnOS.Setup.exe') -Destination $setup
        $report.setup=$setup;$report.setup_sha256=(Get-FileHash -LiteralPath $setup).Hash.ToLowerInvariant();$report.setup_execution='not_run'
    }
    $report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $evidence 'package-result.json') -Encoding utf8
    $report|ConvertTo-Json -Depth 6
} catch {
    if($run){[ordered]@{status='failed';error=$_.Exception.Message;utc=[DateTimeOffset]::UtcNow}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $run 'failure.json') -Encoding utf8}
    throw
} finally {Pop-Location}
