[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildId)
. "$PSScriptRoot/Common.ps1"
$ErrorActionPreference='Stop'
$buildRoot=Join-Path $ProjectRoot "artifacts/builds/$BuildId"
$build=Get-Content -LiteralPath (Join-Path $buildRoot 'build-result.json') -Raw|ConvertFrom-Json
$prepared=Get-Content -LiteralPath (Join-Path $buildRoot 'package-prepared.json') -Raw|ConvertFrom-Json
if($build.status -ne 'passed' -or $prepared.build_id -ne $BuildId -or $build.source_snapshot_id -ne $prepared.source_snapshot_id -or $build.test_build){throw 'Matching normal prepared build required.'}
$run=Join-Path $buildRoot ('single-file-'+[Guid]::NewGuid().ToString('N'))
$destination=Join-Path $ProjectRoot "artifacts/single-file/$BuildId"
if(Test-Path -LiteralPath $destination){throw 'Existing delivery preserved; choose a new BuildId.'}
New-Item -ItemType Directory -Path $run,$destination|Out-Null
$snapshot=& "$PSScriptRoot/New-SourceSnapshot.ps1" -OutputDirectory $run
if($snapshot -ne $build.source_snapshot_id){throw 'Source changed since build.'}
$staging=Join-Path $run 'payload';New-Item -ItemType Directory -Path $staging|Out-Null
$delivery=Get-Content -LiteralPath (Join-Path $buildRoot 'delivery-hashes.json') -Raw|ConvertFrom-Json
$sourceDirectory=Split-Path $prepared.executable -Parent
$removed=@();$copied=@()
foreach($file in $delivery){
    if($file.path -match '(^|/)(AutumnOS_Data|bin|obj|node_modules|\.autumnos-update)(/|$)|\.(pk8|pem|key|pfx|p12|snk|dpapi|log|binlog|tmp)$'){throw 'Private/generated content in tracked delivery.'}
    $source=Join-Path $sourceDirectory $file.path
    for($cursor=[IO.Path]::GetFullPath($source);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Redirected packaging input.'}}
    if((Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant() -ne $file.sha256){throw 'Prepared bytes changed.'}
    if($file.path.EndsWith('.pdb',[StringComparison]::OrdinalIgnoreCase)){$removed+=,[ordered]@{path=$file.path;bytes=$file.bytes;sha256=$file.sha256;reason='debug_symbols_not_required_by_runtime'};continue}
    $target=Join-Path $staging $file.path;New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent)|Out-Null
    Copy-Item -LiteralPath $source -Destination $target
    $copied+=,$file
}
@'
Lab Chronicles AutumnOS · 制作人：派蒙
单文件便携版：只交付 AutumnOS.exe；首次启动只在旁边创建 AutumnOS_Data。
内置 .NET 10、Windows App SDK、固定 WebView2、内置游戏、开发者工具和许可证。
运行依赖展开到 AutumnOS_Data/System，存档仍在同一个 AutumnOS_Data/Saves。
启动准备器使用目标 Windows 自带的 .NET Framework 4.x，不安装全局组件。
保留 AutumnOS_Data 可保留存档；不要删除它内部正在使用的 System 运行文件。
非安装包，不创建桌面/开始菜单快捷方式，不改系统 PATH/注册表。
生产自动更新仍要求正式信任根/签名；不关闭 TLS 或验签。
本地联调包，完整 T06、T03 收尾及正式发布门禁仍未全部通过。
'@|Set-Content -LiteralPath (Join-Path $staging 'READ-ME.txt') -Encoding utf8NoBOM
$inventoryPath=Join-Path $run 'managed-files.json'
Invoke-Dotnet -Arguments @('tools/AutumnOS.Update.Cli/bin/x64/Release/net10.0/win-x64/AutumnOS.Update.Cli.dll','inventory','--source',$staging,'--output',$inventoryPath) -LogPath (Join-Path $run 'inventory.log')
$managed=@(Get-Content -LiteralPath $inventoryPath -Raw|ConvertFrom-Json)
[ordered]@{schemaVersion=1;version=$build.version;buildId=$BuildId;files=$managed}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $staging 'autumn.install.json') -Encoding utf8NoBOM
$files=@(Get-ChildItem -LiteralPath $staging -Recurse -File|ForEach-Object {[ordered]@{path=[IO.Path]::GetRelativePath($staging,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}}|Sort-Object path)
$index=Join-Path $run 'bundle-inventory.txt'
($files|ForEach-Object {"$($_.sha256)`t$($_.bytes)`t$($_.path)"})|Set-Content -LiteralPath $index -Encoding utf8NoBOM
$archive=Join-Path $run 'payload.zip';$zip=[IO.Compression.ZipFile]::Open($archive,[IO.Compression.ZipArchiveMode]::Create)
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
try{foreach($file in $files){if($seen.Add($file.sha256)){
    $entry=$zip.CreateEntry($file.sha256,[IO.Compression.CompressionLevel]::Optimal)
    $entry.LastWriteTime=[DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
    $input=[IO.File]::OpenRead((Join-Path $staging $file.path));$output=$entry.Open()
    try{$input.CopyTo($output)}finally{$output.Dispose();$input.Dispose()}
}}}finally{$zip.Dispose()}
# Read every unique stored blob back before embedding; duplicates remain necessary runtime paths.
$zip=[IO.Compression.ZipFile]::OpenRead($archive)
try{if($zip.Entries.Count -ne $seen.Count){throw 'Blob count mismatch.'};foreach($entry in $zip.Entries){$stream=$entry.Open();try{$hash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()};if($hash -ne $entry.FullName){throw 'Blob readback mismatch.'}}}finally{$zip.Dispose()}
$payloadHash=(Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant()
$metadata=Join-Path $run 'BundleIdentity.cs'
$label=if($build.release_label){$build.release_label}else{$build.version}
foreach($value in @($label,$BuildId,$build.source_snapshot_id)){if($value -match '["\\\r\n]'){throw 'Unsafe build metadata.'}}
@"
using System.Reflection;
[assembly: AssemblyTitle("AutumnOS")]
[assembly: AssemblyProduct("Lab Chronicles AutumnOS")]
[assembly: AssemblyCompany("派蒙")]
[assembly: AssemblyDescription("制作人：派蒙")]
[assembly: AssemblyVersion("0.1.0.0")]
[assembly: AssemblyFileVersion("0.1.0.0")]
[assembly: AssemblyInformationalVersion("$label")]
[assembly: AssemblyMetadata("BuildId", "$BuildId")]
[assembly: AssemblyMetadata("SourceSnapshotId", "$($build.source_snapshot_id)")]
namespace AutumnOS.SingleFile { internal static class BundleIdentity {
internal const string PayloadSha256 = "$payloadHash";
internal const string BuildId = "$BuildId";
} }
"@|Set-Content -LiteralPath $metadata -Encoding utf8NoBOM
$framework=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319'
$compiler=Join-Path $framework 'csc.exe'
if(-not(Test-Path -LiteralPath $compiler)){throw 'Missing inbox .NET Framework compiler; do not install global components.'}
$stub=Join-Path $run 'bootstrap.exe'
$arguments=@('/nologo','/utf8output','/target:winexe','/platform:x64','/optimize+','/warnaserror+',"/out:$([IO.Path]::GetFullPath($stub))","/resource:$([IO.Path]::GetFullPath($index)),AutumnOS.BundleInventory",'/reference:System.dll','/reference:System.Core.dll',"/reference:$([IO.Path]::GetFullPath((Join-Path $framework 'System.IO.Compression.dll')))","/reference:$([IO.Path]::GetFullPath((Join-Path $framework 'System.Runtime.Serialization.dll')))",'/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll',[IO.Path]::GetFullPath((Join-Path $ProjectRoot 'src/AutumnOS.SingleFile/Program.cs')),[IO.Path]::GetFullPath($metadata))
& $compiler @arguments 2>&1|Tee-Object -FilePath (Join-Path $run 'compile.log')|Out-Host
if($LASTEXITCODE -ne 0){throw 'Single-file bootstrap compilation failed.'}
$executable=Join-Path $destination 'AutumnOS.exe'
$output=[IO.FileStream]::new($executable,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{
    $input=[IO.File]::OpenRead($stub);try{$input.CopyTo($output)}finally{$input.Dispose()};$offset=$output.Position
    $input=[IO.File]::OpenRead($archive);try{$length=$input.Length;$input.CopyTo($output)}finally{$input.Dispose()}
    $output.Write([BitConverter]::GetBytes([long]$offset));$output.Write([BitConverter]::GetBytes([long]$length));$output.Write([Convert]::FromHexString($payloadHash));$output.Write([Text.Encoding]::ASCII.GetBytes('AUTUMNOSBUNDLE01'));$output.Flush($true)
}finally{$output.Dispose()}
if((Get-Item -LiteralPath $executable).VersionInfo.ProductVersion -ne $label){throw 'Compiled single-file version mismatch.'}
$files|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $run 'delivery-hashes.json') -Encoding utf8NoBOM
[ordered]@{status='passed';removed=$removed;removed_count=$removed.Count;removed_bytes=($removed|Measure-Object bytes -Sum).Sum;deduplicated_embedded_copies=$files.Count-$seen.Count;unique_blobs=$seen.Count;runtime_paths_retained=$files.Count;runtime_trimmed=$false;licenses_retained=$true;old_files_deleted=$false}|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $run 'cleanup.json') -Encoding utf8NoBOM
$report=[ordered]@{status='prepared';build_id=$BuildId;source_snapshot_id=$snapshot;release_label=$label;version=$build.version;executable=$executable;executable_sha256=(Get-FileHash -LiteralPath $executable).Hash.ToLowerInvariant();bytes=(Get-Item -LiteralPath $executable).Length;delivery_files=1;data_folder='AutumnOS_Data';program_relative_path=('AutumnOS_Data/System/Product-'+$payloadHash.Substring(0,20));payload_sha256=$payloadHash;logical_files=$files.Count;unique_blobs=$seen.Count;native_tests='not_run';setup='deferred_by_user';public_release=$false;authenticode='not_signed';production_trust='unconfigured_signed_install_disabled';compiler=$compiler;compiler_sha256=(Get-FileHash -LiteralPath $compiler).Hash.ToLowerInvariant();compiler_version=(Get-Item -LiteralPath $compiler).VersionInfo.FileVersion;evidence=$run;inbox_framework='Windows .NET Framework 4.x';net10_winappsdk_webview2='embedded_self_contained_no_global_install'}
$report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $buildRoot 'package-single-file.json') -Encoding utf8NoBOM
$report|ConvertTo-Json -Depth 6
