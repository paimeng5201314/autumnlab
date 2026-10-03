[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildId,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [Parameter(Mandatory)][string[]]$TestReports,
    [string[]]$Screenshots=@()
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if($BuildId -notmatch '^T06-[A-Za-z0-9_.-]+$'){throw 'Tracked T06 build ID required.'}
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory)
if(Test-Path -LiteralPath $OutputDirectory){throw 'Review output already exists; previous review material is preserved.'}
$buildRoot=Join-Path $root "artifacts/builds/$BuildId"
$build=Get-Content -LiteralPath (Join-Path $buildRoot 'build-result.json') -Raw|ConvertFrom-Json
$package=Get-Content -LiteralPath (Join-Path $buildRoot 'package-result.json') -Raw|ConvertFrom-Json
if($build.status -ne 'passed' -or $package.status -ne 'packaged' -or $package.build_id -ne $BuildId -or $package.source_snapshot_id -ne $build.source_snapshot_id){throw 'Matching real build and final portable ZIP required.'}
New-Item -ItemType Directory -Path $OutputDirectory|Out-Null
function Assert-ReviewInput([string]$Path){
    $full=[IO.Path]::GetFullPath($Path)
    $allowed=[IO.Path]::GetFullPath((Join-Path $root 'artifacts')).TrimEnd('\')+'\'
    if(-not $full.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase) -or $full -match '[\\/]AutumnOS_Data[\\/]'){throw 'Only explicit local test evidence outside user data may enter review.'}
    for($cursor=$full;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Redirected review input forbidden.'}
    }
    return $full
}
$testSummaries=@(foreach($file in $TestReports){
    $full=Assert-ReviewInput $file
    if((Get-Item -LiteralPath $full).Length -gt 16MB){throw 'Unbounded test report.'}
    $result=Get-Content -LiteralPath $full -Raw|ConvertFrom-Json -AsHashtable
    $cases=@();foreach($key in @('checks','results')){if($result.ContainsKey($key)){$cases+=@($result[$key])}}
    [ordered]@{report=[IO.Path]::GetFileName($full);sha256=(Get-FileHash -LiteralPath $full).Hash.ToLowerInvariant();
        status=$result.status;build_id=$result.build_id;source_snapshot_id=$result.source_snapshot_id;
        cases=$cases.Count;passed=@($cases|Where-Object status -eq 'passed').Count;failed=@($cases|Where-Object status -eq 'failed').Count;
        raw_report_included=$false;kind='summary_of_explicit_evidence_only'}
})
$images=@();$counter=0
foreach($file in $Screenshots){
    $full=Assert-ReviewInput $file
    if([IO.Path]::GetExtension($full) -ne '.png' -or (Get-Item -LiteralPath $full).Length -gt 20MB){throw 'Explicit bounded PNG screenshot required.'}
    $bytes=[IO.File]::ReadAllBytes($full)
    if($bytes.Length -lt 8 -or [Convert]::ToHexString($bytes[0..7]) -ne '89504E470D0A1A0A'){throw 'Invalid PNG signature.'}
    $counter++;$name=('{0:d2}-' -f $counter)+[IO.Path]::GetFileName($full)
    Copy-Item -LiteralPath $full -Destination (Join-Path $OutputDirectory $name)
    $images+=[ordered]@{file=$name;sha256=(Get-FileHash -LiteralPath $full).Hash.ToLowerInvariant();producer_approval='not_run';source='explicit_test_owned_product_capture'}
}
[ordered]@{schemaVersion=1;product='Lab Chronicles AutumnOS';producer='派蒙';buildId=$BuildId;version=$build.version;sourceSnapshotId=$build.source_snapshot_id;
    classification='local_integration_review_not_release_approval';publicRelease=$false;producerApproval='not_run';installer='deferred_by_user';
    executable=[IO.Path]::GetFileName($package.executable);executableSha256=$package.executable_sha256;zip=[IO.Path]::GetFileName($package.zip);zipSha256=$package.zip_sha256;
    tests=$testSummaries;screenshots=$images;privacy='No original logs, absolute source paths, accounts, tokens, saves or update keys are copied. Explicit test screenshots need human review before sharing.'}|
    ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $OutputDirectory 'review.json') -Encoding utf8NoBOM
@'
Lab Chronicles AutumnOS · 制作人：派蒙
本目录仅供派蒙本地审阅，不代表制作人已批准，也不构成公开发布。
review.json 汇总明确传入的真实构建和测试报告；历史和失败报告仍留在本地工程，不因本审阅包省略而删除。
截图只应传入测试创建的游客产品窗口；分享前仍需人工检查。此脚本不读取原始日志、账号、凭据或存档，不上传任何材料。
完整不足、not_run 环境和正式发布门禁以 AUTUMNOS_PROGRESS.md 与发布就绪报告为准。
'@|Set-Content -LiteralPath (Join-Path $OutputDirectory 'READ-ME.txt') -Encoding utf8NoBOM
$zip="$OutputDirectory.zip"
if(Test-Path -LiteralPath $zip){throw 'Existing review ZIP preserved.'}
[IO.Compression.ZipFile]::CreateFromDirectory($OutputDirectory,$zip,[IO.Compression.CompressionLevel]::Optimal,$false)
[ordered]@{directory=$OutputDirectory;zip=$zip;sha256=(Get-FileHash -LiteralPath $zip).Hash.ToLowerInvariant();uploaded=$false}|ConvertTo-Json
