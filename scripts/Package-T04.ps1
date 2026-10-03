[CmdletBinding()]
param([ValidateSet('Prepare','Finalize','Full')][string]$Stage='Full')
. "$PSScriptRoot/Common.ps1"
Push-Location $ProjectRoot
try {
    $buildId=(Get-Content -LiteralPath 'artifacts/latest-build.txt' -Raw).Trim()
    $buildDirectory=Join-Path $ProjectRoot "artifacts/builds/$buildId"
    $build=Get-Content -LiteralPath (Join-Path $buildDirectory 'build-result.json') -Raw | ConvertFrom-Json
    if($build.status -ne 'passed' -or -not $buildId.StartsWith('T04-')){throw 'A tracked successful T04 build is required.'}
    foreach($name in @('tests','sdk-tests','sample-tests','native-client-tests','server-http-tests','t04-tools-tests')){
        $test=Get-Content -LiteralPath (Join-Path $buildDirectory "$name.json") -Raw | ConvertFrom-Json
        if($test.status -ne 'passed' -or $test.build_id -ne $buildId -or $test.source_snapshot_id -ne $build.source_snapshot_id){throw "Passing matching test evidence required: $name"}
    }
    $snapshot=& "$PSScriptRoot/New-SourceSnapshot.ps1" -OutputDirectory (Join-Path $buildDirectory ('package-source-'+[Guid]::NewGuid().ToString('N')))
    if($snapshot -ne $build.source_snapshot_id){throw 'Sources changed after build. Use a new build ID.'}
    $hashes=Get-Content -LiteralPath (Join-Path $buildDirectory 'artifact-hashes.json') -Raw | ConvertFrom-Json
    foreach($entry in $hashes){if((Get-FileHash -LiteralPath (Join-Path $ProjectRoot $entry.path)).Hash.ToLowerInvariant() -ne $entry.sha256){throw "Tracked output changed: $($entry.path)"}}
    [xml]$brand=Get-Content -LiteralPath 'build/Brand.props' -Raw
    $version=$brand.Project.PropertyGroup.AutumnVersion
    $checkpointName=if($version -like '*desktop-multitask*'){'桌面多任务'}else{'T04 商店'}
    $destination=Join-Path $ProjectRoot "artifacts/packages/$buildId/AutumnOS-$version-win-x64-development"
    $exe=Join-Path $destination 'AutumnOS.exe'
    $builtRoot=Join-Path $ProjectRoot (Split-Path $build.output_path)
    $resources=@($hashes | Where-Object {[IO.Path]::GetExtension($_.path) -in @('.pri','.xbf')})
    if($Stage -ne 'Finalize'){
        if(Test-Path -LiteralPath $destination){throw 'Existing packages are preserved; choose a new build ID.'}
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        Invoke-Dotnet -Arguments @('publish','src/AutumnOS.Shell/AutumnOS.Shell.csproj','-c','Release','-p:Platform=x64','--no-build','--no-restore',"-p:BuildId=$buildId","-p:SourceSnapshotId=$snapshot",'-o',$destination) -LogPath (Join-Path $buildDirectory 'publish.log')
        foreach($entry in $resources){
            $source=Join-Path $ProjectRoot $entry.path;$relative=[IO.Path]::GetRelativePath($builtRoot,$source)
            if($relative.StartsWith('..') -or [IO.Path]::IsPathRooted($relative)){throw 'WinUI resource escaped shell output.'}
            $target=Join-Path $destination $relative;New-Item -ItemType Directory -Path (Split-Path $target) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $target
        }
        if(Test-Path -LiteralPath (Join-Path $destination 'AutumnOS_Data')){throw 'Publishing included user data.'}
        foreach($file in Get-ChildItem -LiteralPath $destination -Recurse -File){
            $relative=[IO.Path]::GetRelativePath($destination,$file.FullName);$source=Join-Path $builtRoot $relative
            if((Get-FileHash -LiteralPath $source).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash){throw "Publish byte mismatch: $relative"}
        }
        $licenses=Join-Path $destination 'ThirdPartyLicenses';New-Item -ItemType Directory -Path $licenses | Out-Null
        foreach($package in Get-ChildItem -LiteralPath $env:NUGET_PACKAGES -Directory){foreach($packageVersion in Get-ChildItem -LiteralPath $package.FullName -Directory){
            foreach($license in Get-ChildItem -LiteralPath $packageVersion.FullName -File | Where-Object Name -Match '^(license|third.?party|notice)'){
                Copy-Item -LiteralPath $license.FullName -Destination (Join-Path $licenses ($package.Name+'-'+$packageVersion.Name+'-'+$license.Name))
            }
        }}
        Copy-Item -LiteralPath '.tools/dotnet/LICENSE.txt' -Destination (Join-Path $licenses 'dotnet-LICENSE.txt')
        Copy-Item -LiteralPath '.tools/dotnet/ThirdPartyNotices.txt' -Destination (Join-Path $licenses 'dotnet-ThirdPartyNotices.txt')
        Copy-Item -LiteralPath 'docs/dependencies.md' -Destination $licenses
        @"
Lab Chronicles AutumnOS · 制作人：派蒙
T04 本地开发检查包，不是正式发行候选。
Build: $buildId
Source: $snapshot

保留完整目录，双击 AutumnOS.exe。数据在 EXE 旁 AutumnOS_Data；不会搬迁旧包存档或凭据。
相同 Windows 用户/会话已有兼容协议的启动器时，EXE 只唤回它。请正常退出旧窗口后审阅本包。
桌面“商店”读取公开 GitHub，无需账号或 PAT。空结果按实际网络返回显示。
设置 > GitHub 加速：系统代理、公开 API/附件 URL 模板、限速与并发；不代理 Logto 和游戏流量。
我的应用：版本固定、预览、修复、卸载保留存档；运行中（包括后台）不替换资源。
本地文件选择/拖入仅支持 .autumn。一般第三方应用仍受 T01 运行安全验收限制，不会被放行执行。
开发者诊断打开开发模式后，可在开发者工具显式开启“本地商店集成测试”。
测试使用独立应用/游客存档和真实本机 HTTP，界面醒目标识；不冒充 GitHub，不随重启启用。
关闭开发模式前保存测试应用；关闭会撤销监听和测试运行能力。

原登录保持、DPAPI/DACL、回调页、双栏设置、后台继续/结束及长会话 SDK 保留。
0.4.1 桌面交互：单击图标打开/继续；双击桌面空白处或按 F6 查看全部运行应用。
图标可按住拖动排序，松手保存；Esc 或拖出网格取消。长按/右键保留继续与结束，并可向前/向后移动。
T03 完整收尾由用户暂缓；T04 完整状态见源工程 AUTUMNOS_PROGRESS.md。T05 未实施。
本包未进行 Authenticode 签名；安装器、干净机器、Windows 10 完整矩阵未验证。
源码、证据及文档均在本地。无 Git 写入、上传或公共 Release。
"@ | Set-Content -LiteralPath (Join-Path $destination 'READ-ME.txt') -Encoding utf8
        $deliveryHashes=@(Get-ChildItem -LiteralPath $destination -Recurse -File | ForEach-Object {
            [ordered]@{path=[IO.Path]::GetRelativePath($destination,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
        })
        $deliveryHashes | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $buildDirectory 'delivery-hashes.json') -Encoding utf8
    }
    foreach($required in @('AutumnOS.exe','AutumnOS.pri','App.xbf','MainWindow.xbf','Samples/StoreFixture/fixture-index.json')){
        if(-not (Test-Path -LiteralPath (Join-Path $destination $required) -PathType Leaf)){throw "Missing delivery resource: $required"}
    }
    $deliveryHashes=Get-Content -LiteralPath (Join-Path $buildDirectory 'delivery-hashes.json') -Raw | ConvertFrom-Json
    foreach($entry in $deliveryHashes){if((Get-FileHash -LiteralPath (Join-Path $destination $entry.path)).Hash.ToLowerInvariant() -ne $entry.sha256){throw "Delivery bytes changed: $($entry.path)"}}
    $prepareReport=[ordered]@{status='prepared';build_id=$buildId;source_snapshot_id=$snapshot;executable=$exe;executable_sha256=(Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant();winui_resources=$resources.Count;final_executable_test='not_run';zip='not_created';public_release=$false}
    if($Stage -eq 'Prepare'){$prepareReport | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $buildDirectory 'package-prepared.json');$prepareReport | ConvertTo-Json;return}
    # Each run has unique evidence. Test-owned copies exercise UI; the exact final path is tested before ZIP.
    $runDirectory=Join-Path $buildDirectory ('package-t04-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $runDirectory | Out-Null
    & "$PSScriptRoot/Test-T04StoreSmoke.ps1" -ExecutablePath $exe -ReportDirectory (Join-Path $runDirectory 'store')
    & "$PSScriptRoot/Test-DesktopInteractionSmoke.ps1" -ExecutablePath $exe -ReportDirectory (Join-Path $runDirectory 'desktop')
    & "$PSScriptRoot/Test-DeliveredLauncher.ps1" -ExecutablePath $exe -ReportDirectory (Join-Path $runDirectory 'final-path')
    foreach($path in @('store/store-smoke.json','desktop/windows-smoke.json')){
        $result=Get-Content -LiteralPath (Join-Path $runDirectory $path) -Raw | ConvertFrom-Json
        if($result.status -ne 'passed' -or $result.build_id -ne $buildId -or $result.source_snapshot_id -ne $snapshot -or $result.source_executable_sha256 -ne $prepareReport.executable_sha256){throw "Final UI evidence mismatch: $path"}
    }
    $direct=Get-Content -LiteralPath (Join-Path $runDirectory 'final-path/delivered-launcher.json') -Raw | ConvertFrom-Json
    if($direct.status -ne 'passed' -or $direct.binary_after.metadata.BuildId -ne $buildId -or $direct.binary_after.metadata.SourceSnapshotId -ne $snapshot){throw 'Final-path launcher test mismatch.'}
    $zipPath="$destination.zip"
    if(Test-Path -LiteralPath $zipPath){throw 'Existing ZIP preserved.'}
    foreach($entry in $deliveryHashes){if((Get-FileHash -LiteralPath (Join-Path $destination $entry.path)).Hash.ToLowerInvariant() -ne $entry.sha256){throw "Product file changed during test: $($entry.path)"}}
    $zip=[IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
    try{foreach($entry in $deliveryHashes){
        if($entry.path.StartsWith('AutumnOS_Data/',[StringComparison]::OrdinalIgnoreCase)){throw 'Data unexpectedly entered frozen delivery manifest.'}
        $null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $destination $entry.path),$entry.path,[IO.Compression.CompressionLevel]::Optimal)
    }}finally{$zip.Dispose()}
    $zip=[IO.Compression.ZipFile]::OpenRead($zipPath)
    try{
        if($zip.Entries.Count -ne $deliveryHashes.Count){throw 'ZIP count mismatch.'}
        foreach($entry in $deliveryHashes){$item=$zip.GetEntry($entry.path);$stream=$item.Open();try{$sha=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()};if($sha -ne $entry.sha256){throw "ZIP byte mismatch: $($entry.path)"}}
    }finally{$zip.Dispose()}
    $shortcutPath=Join-Path $ProjectRoot "AutumnOS（$checkpointName $buildId）.lnk"
    if(Test-Path -LiteralPath $shortcutPath){throw 'Existing shortcut preserved.'}
    $shell=New-Object -ComObject WScript.Shell;$shortcut=$shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath=$exe;$shortcut.WorkingDirectory=$destination;$shortcut.Description='Lab Chronicles AutumnOS · 制作人：派蒙 · T04 本地开发';$shortcut.Save()
    $report=[ordered]@{status='passed';build_id=$buildId;source_snapshot_id=$snapshot;executable=$exe;zip=$zipPath;shortcut=$shortcutPath;
        zip_sha256=(Get-FileHash -LiteralPath $zipPath).Hash.ToLowerInvariant();zip_bytes=(Get-Item -LiteralPath $zipPath).Length;zip_entries=$deliveryHashes.Count;
        executable_sha256=$prepareReport.executable_sha256;evidence=$runDirectory;winui_resources=$resources.Count;final_executable='passed';
        t03_complete='deferred_by_user';t04_complete='not_verified';t05='not_run';public_release=$false;zip_excludes_user_data=$true;
        runtime_gate='general_third_party_execution_blocked_pending_T01';real_github_release_install='not_run';authenticode='not_signed'}
    $report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $buildDirectory 'package-result.json') -Encoding utf8
    $report | ConvertTo-Json -Depth 6
}catch{
    if($buildDirectory){$failurePath=Join-Path $buildDirectory ('package-failure-'+[Guid]::NewGuid().ToString('N')+'.json');[ordered]@{status='failed';build_id=$buildId;stage=$Stage;failure=$_.Exception.Message;utc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json | Set-Content -LiteralPath $failurePath -Encoding utf8}
    throw
}finally{Pop-Location}
