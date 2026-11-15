[CmdletBinding()]
param([switch]$Execute)
if(-not $Execute){throw 'Explicit -Execute is required after root confirms new Prepare READY. No SDK package was created.'}
. (Join-Path $PSScriptRoot 'Publication-Identity.ps1')
$null=Read-PublicationBuild
$automatic=@(Read-PublicationAutomatic)
$null=Read-PublicationPrepared
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$buildId='T06-20261115-signing-recovery-prod-01'
$buildRoot=Join-Path $workspace "artifacts/builds/$buildId"
$zipPath=Join-Path $PSScriptRoot 'AutumnOS-SDK-0.4.0.zip'
$reportPath=Join-Path $PSScriptRoot 'AutumnOS-SDK-0.4.0.zip.json'
$inputPath=Join-Path $PSScriptRoot 'sdk-package-inputs.json'
if((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $reportPath) -or (Test-Path -LiteralPath $inputPath)){throw 'Existing SDK archive and evidence preserved.'}
function Assert-Plain([string]$path){
    for($cursor=[IO.Path]::GetFullPath($path);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Redirected SDK input forbidden.'}
    }
}
function Hash-File([string]$path){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
$preparedPath=Join-Path $buildRoot 'package-prepared.json'
$prepared=Get-Content -LiteralPath $preparedPath -Raw|ConvertFrom-Json
if($prepared.status -cne 'prepared' -or $prepared.build_id -cne $buildId -or $prepared.source_snapshot_id -cne 'sha256:a7699603f7e068739b4adf5a2db7508cd3bedc247c36086315604fe7c6d15a2e' -or $prepared.temporary_platform_acceptance){throw 'Matching ordinary production prepared SDK required.'}
$packageRoot=Split-Path $prepared.executable -Parent
Assert-Plain $packageRoot
$kit=Get-Content -LiteralPath (Join-Path $packageRoot 'Developer/kit.json') -Raw|ConvertFrom-Json
if($kit.sdkVersion -cne '0.4.0' -or $kit.buildId -cne $buildId -or $kit.sourceSnapshotId -cne $prepared.source_snapshot_id -or -not $kit.selfContainedCli){throw 'Actual developer kit identity differs.'}
$deliveryPath=Join-Path $buildRoot 'delivery-hashes.json'
$all=@(Get-Content -LiteralPath $deliveryPath -Raw|ConvertFrom-Json)
$backendKeeps=@('Developer/BackendIdentity/client/README.md','Developer/BackendIdentity/client/config.example.json','Developer/BackendIdentity/client/config.schema.json','Developer/BackendIdentity/client/config/logto.public.json','Developer/BackendIdentity/server/README.md','Developer/BackendIdentity/server/openapi.json')
$selected=@($all|Where-Object {
    ($_.path.StartsWith('Developer/',[StringComparison]::Ordinal) -and
        ((-not $_.path.StartsWith('Developer/BackendIdentity/client/',[StringComparison]::Ordinal) -and -not $_.path.StartsWith('Developer/BackendIdentity/server/',[StringComparison]::Ordinal)) -or $_.path -cin $backendKeeps)) -or
    $_.path.StartsWith('ThirdPartyLicenses/',[StringComparison]::Ordinal)
}|Sort-Object path)
$expected=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
$sources=@(foreach($row in $selected){
    if($row.path -match '(^|/)(AutumnOS_Data|Saves|Accounts|credentials|tokens|private[-_]?keys|WebView2Runtime|NativeHost)(/|$)|\.(dpapi|pem|key|pfx|pk8|p12)$' -or
        [IO.Path]::IsPathRooted($row.path) -or $row.path.Contains('\') -or $row.path.Contains(':') -or @($row.path.Split('/')|Where-Object {$_ -in @('','.','..')}).Count -or -not $expected.TryAdd($row.path,$row)){throw 'Private, runtime-host or duplicate SDK path forbidden.'}
    $path=[IO.Path]::GetFullPath((Join-Path $packageRoot $row.path))
    if(-not $path.StartsWith($packageRoot+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'SDK file escaped prepared directory.'}
    Assert-Plain $path
    if((Get-Item -LiteralPath $path).Length -ne $row.bytes -or (Hash-File $path) -cne $row.sha256){throw 'SDK file differs from frozen delivery table.'}
    [pscustomobject]@{path=$row.path;source_path=$path;bytes=[long]$row.bytes;sha256=$row.sha256;origin='frozen_prepared_delivery'}
})
$projectLicense=Join-Path $workspace 'LICENSE'
Assert-Plain $projectLicense
if((Hash-File $projectLicense) -cne '8486a10c4393cee1c25392769ddd3b2d6c242d6ec7928e1414efff7dfb2f07ef'){throw 'Project license changed.'}
$sources += [pscustomobject]@{path='LICENSE';source_path=$projectLicense;bytes=(Get-Item -LiteralPath $projectLicense).Length;sha256=(Hash-File $projectLicense);origin='project_license_outside_build_input_snapshot'}
if($sources.Count -ne 354 -or @($sources|Where-Object path -like 'Developer/*').Count -ne 315){throw 'Reviewed SDK subset differs.'}
$readme=@'
# AutumnOS SDK 0.4.0

制作人：派蒙。SDK 0.4.0 / 协议 1，产品版本 1.0.0。
对应构建 T06-20261115-signing-recovery-prod-01，源码 sha256:a7699603f7e068739b4adf5a2db7508cd3bedc247c36086315604fe7c6d15a2e。

本附件保留 Developer 中 SDK JS/TypeScript、JSON Schema、文档、四个模板与共享资源、网络示例、契约工具/测试和完整自包含开发 CLI；LICENSE 与 ThirdPartyLicenses 同时保留。它不包含完整 AutumnOS 客户端、固定 WebView2 或 NativeHost。

CLI 位于 Developer/AutumnOS.Developer.Cli.exe。create、validate、pack 所需的自包含 .NET 文件保持完整；不要只复制 EXE 或几个 AutumnOS DLL。入口文档为 Developer/Docs/0.5.1/first-app.md，网络能力见 Developer/Docs/sdk-network.md。SDK 使用适配的 .autumn Web/WASM 应用，不支持任意 Windows EXE。

preview/debug 仍需要单独的完整 AutumnOS 客户端正常运行并启用开发者模式。必须显式使用 --host，指向实际运行的 AutumnOS.Client.exe 所在程序目录。独立 SDK 不满足 first-app.md 中“Developer 父目录就是完整宿主”的默认前提；不能把本 SDK 解压根当作宿主。普通浏览器无宿主时返回 HOST_UNAVAILABLE，不会自动模拟真实宿主。

为减小附件，本包省略 BackendIdentity/client 和 server 的预编译 EXE、DLL 与重复自包含运行时；保留后端完整 Sources、依赖锁、Build-Sources.ps1、README、公开配置及 OpenAPI。该处文档中直接运行预编译 EXE 的步骤只适用于完整产品 Developer 包，在本附件应先从源码重建。需要既有精确 .NET SDK 10.0.401，在新目录运行：

    ./Developer/BackendIdentity/Build-Sources.ps1 -Destination '<新的输出绝对路径>' -DotnetPath '<.NET SDK 10.0.401 的 dotnet.exe 绝对路径>'

随后按保留的来源 README 和生成输出进行配置。脚本只向官方源按锁恢复依赖。不要填写 Client Secret；真实 Logto Native 类型、回调登记和后端往返仍需真实验收，未完成项不因本包生成而视为通过。

SDK-CONTENTS.json 列出实际入包文件的大小与 SHA256；外部 sidecar 记录 ZIP 摘要及逐条解压流核验结果。此次只验证打包和字节，不宣称独立解压目录 CLI/UI 已实测。Win10/11、完整隔离、外部开发者接入及普通安装网络持久许可 GUI 等仍以各自实际报告为准。
'@
$readmeBytes=[Text.UTF8Encoding]::new($false).GetBytes($readme.Replace("`r`n","`n"))
$readmeHash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($readmeBytes))
$files=@($sources|Sort-Object path|ForEach-Object {[ordered]@{path=$_.path;bytes=$_.bytes;sha256=$_.sha256;origin=$_.origin}})
$files += [ordered]@{path='README-SDK.md';bytes=$readmeBytes.Length;sha256=$readmeHash;origin='standalone_sdk_scope_note'}
$manifest=[ordered]@{schema_version=1;kind='standalone_sdk_subset';sdk_version='0.4.0';protocol=1;producer='派蒙';product_version='1.0.0';build_id=$buildId;source_snapshot_id=$prepared.source_snapshot_id;self_contained_cli=$true;prebuilt_backend_runtime_included=$false;backend_sources_included=$true;files=$files}
$manifestBytes=[Text.UTF8Encoding]::new($false).GetBytes(($manifest|ConvertTo-Json -Depth 7))
$manifestHash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($manifestBytes))
$inputRecord=[ordered]@{status='all_selected_source_hashes_verified';build_id=$buildId;source_snapshot_id=$prepared.source_snapshot_id;prepared_report=$preparedPath;delivery_manifest=$deliveryPath;delivery_manifest_sha256=(Hash-File $deliveryPath);selected_files=$sources;generated_files=@([ordered]@{path='README-SDK.md';bytes=$readmeBytes.Length;sha256=$readmeHash},[ordered]@{path='SDK-CONTENTS.json';bytes=$manifestBytes.Length;sha256=$manifestHash});omitted_backend_prebuilt_files=@($all|Where-Object {($_.path.StartsWith('Developer/BackendIdentity/client/',[StringComparison]::Ordinal) -or $_.path.StartsWith('Developer/BackendIdentity/server/',[StringComparison]::Ordinal)) -and $_.path -cnotin $backendKeeps});private_data_included=$false;native_or_product_runtime_included=$false;cli_runtime_included=$true}
$inputRecord|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $inputPath -Encoding utf8NoBOM
$zip=[IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
try{
    foreach($file in $sources|Sort-Object path){
        $entry=$zip.CreateEntry($file.path,[IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime=[DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
        $input=[IO.File]::OpenRead($file.source_path);$output=$entry.Open()
        try{$input.CopyTo($output)}finally{$output.Dispose();$input.Dispose()}
    }
    foreach($extra in @([pscustomobject]@{path='README-SDK.md';data=$readmeBytes},[pscustomobject]@{path='SDK-CONTENTS.json';data=$manifestBytes})){
        $entry=$zip.CreateEntry($extra.path,[IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime=[DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
        $stream=$entry.Open();try{$stream.Write($extra.data,0,$extra.data.Length)}finally{$stream.Dispose()}
    }
}finally{$zip.Dispose()}
$entryExpected=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($row in $files){if(-not $entryExpected.TryAdd($row.path,$row)){throw 'Duplicate generated SDK entry.'}}
$entryExpected.Add('SDK-CONTENTS.json',[pscustomobject]@{path='SDK-CONTENTS.json';bytes=$manifestBytes.Length;sha256=$manifestHash})
$verified=@();$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$zip=[IO.Compression.ZipFile]::OpenRead($zipPath)
try{
    if($zip.Entries.Count -ne $entryExpected.Count){throw 'SDK ZIP entry count differs.'}
    $verified=@(foreach($entry in $zip.Entries){
        $row=$null
        if(-not $seen.Add($entry.FullName) -or -not $entryExpected.TryGetValue($entry.FullName,[ref]$row) -or $entry.FullName -cne $row.path -or $entry.Length -ne $row.bytes){throw 'SDK ZIP path or size differs.'}
        $stream=$entry.Open();try{$hash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
        if($hash -cne $row.sha256){throw 'SDK ZIP decompressed entry hash differs.'}
        [ordered]@{path=$entry.FullName;bytes=$entry.Length;sha256=$hash;verified=$true}
    })
}finally{$zip.Dispose()}
[long]$inputBytes=0;foreach($source in $sources){$inputBytes += $source.bytes}
$report=[ordered]@{status='passed_all_zip_entries_verified';sdk_version='0.4.0';protocol=1;build_id=$buildId;source_snapshot_id=$prepared.source_snapshot_id;zip=$zipPath;bytes=(Get-Item -LiteralPath $zipPath).Length;sha256=(Hash-File $zipPath);entries=$verified.Count;selected_existing_files=$sources.Count;selected_existing_bytes=$inputBytes;generated_scope_and_contents_files=2;private_data_included=$false;self_contained_cli=$true;prebuilt_backend_runtime_included=$false;backend_source_rebuild_instructions_included=$true;entry_verification='every_decompressed_entry_size_and_sha256';verified_files=$verified;product_or_GUI_or_CLI_execution='not_run_for_this_standalone_zip';Windows10_11='paused_by_user_not_run';remote_actions=$false;completed_utc=[DateTimeOffset]::UtcNow.ToString('o')}
$report|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
[pscustomobject]@{status=$report.status;zip=$zipPath;bytes=$report.bytes;sha256=$report.sha256;entries=$report.entries}
