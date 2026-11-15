[CmdletBinding()]
param([switch]$Execute)
if(-not $Execute){throw 'Explicit -Execute is required after root confirms new Prepare READY. No source stage was created.'}
. (Join-Path $PSScriptRoot 'Publication-Identity.ps1')
$build=Read-PublicationBuild
$groups=@(Read-PublicationAutomatic)
$prepared=Read-PublicationPrepared
$bundle=Read-PublicationSourceBundle
$stage=Join-Path $PSScriptRoot 'source'
if(Test-Path -LiteralPath $stage){throw 'Existing source stage preserved; do not overwrite.'}
Assert-PublicationPlain $PSScriptRoot
$relativeOutput='artifacts/signing-recovery-20261115/publication'
$manifestRelative="artifacts/builds/$buildId/source-snapshot.json"
$summaryRelative="$relativeOutput/PUBLIC-REKEY-BUILD-AND-TESTS.json"
$identityRelative="$relativeOutput/PUBLIC-SIGNING-IDENTITY.json"
$oldRoot=Join-Path $workspace 'artifacts/publication-sdk03-20261114'
$identityPath=Join-Path $workspace 'artifacts/signing-recovery-20261115/new-identity-verification.json'
Assert-PublicationPlain $identityPath
$identity=Get-Content -LiteralPath $identityPath -Raw|ConvertFrom-Json
if($identity.status -cne 'passed_new_identity_creation_and_two_fresh_process_open_sign_verify' -or
   $identity.key_id -cne $keyId -or $identity.root_version -ne 1 -or $identity.public_trust_sha256 -cne $trustSha256 -or
   @($identity.independent_challenge_processes).Count -ne 2 -or @($identity.independent_challenge_processes|Where-Object{$_.status -cne 'passed_new_process_open_sign_verify_old_roots_reject'}).Count){throw 'New identity challenge evidence differs.'}
$publicIdentity=[ordered]@{schema_version=1;producer='派蒙';key_id=$keyId;root_version=1;purpose='production';key_bits=$identity.key_bits;
    public_trust_sha256=$trustSha256;public_key_sha256=$identity.public_key_sha256;
    two_fresh_process_open_sign_verify='passed';old_roots_reject_new_challenge_signature='passed';
    challenge_was_random_nonproduction_data=$true;production_update_signature='not_claimed_by_identity_challenge';
    private_key_exported=$false;private_directory_and_process_identity_omitted=$true;source_build_id=$buildId;source_snapshot_id=$snapshotId}
$binaryPath=Join-Path $buildRoot 'package-single-file.json'
$binarySummary=[ordered]@{status='pending';build_id=$buildId;source_snapshot_id=$snapshotId;native_tests='not_run';uploaded=$false}
if(Test-Path -LiteralPath $binaryPath){
    Assert-PublicationPlain $binaryPath
    $b=Get-Content -LiteralPath $binaryPath -Raw|ConvertFrom-Json
    if($b.build_id -cne $buildId -or $b.source_snapshot_id -cne $snapshotId -or $b.version -cne '1.0.0' -or $b.release_label -cne $releaseTag){throw 'New binary packaging report identity differs.'}
    if($b.status -cne 'prepared'){throw 'Incomplete/failed new binary report must be reviewed before source publication.'}
    $binarySummary=[ordered]@{status=$b.status;build_id=$buildId;source_snapshot_id=$snapshotId;bytes=$b.bytes;sha256=$b.executable_sha256;bundle_payload_sha256=$b.payload_sha256;logical_files=$b.logical_files;unique_blobs=$b.unique_blobs;native_tests=$b.native_tests;uploaded=$false}
}
$summary=[ordered]@{schema_version=1;producer='派蒙';scope='New v2 ordinary production build and its own automatic reports; no inherited GUI/native result';
    build=[ordered]@{build_id=$buildId;source_snapshot_id=$snapshotId;status=$build.status;version=$build.version;release_label=$build.release_label;
        configuration=$build.configuration;rid=$build.rid;sdk=$build.sdk;environment=$build.environment;temporary_platform_acceptance=$false;test_build=$false;
        native_payload_table_sha256=$build.native_payload_table_sha256;started_utc=$build.started_utc;finished_utc=$build.finished_utc;
        original_report_sha256=(Get-PublicationHash (Join-Path $buildRoot 'build-result.json'))};
    source_manifest=[ordered]@{path=$manifestRelative;files=$bundle.manifest.files.Count;sha256=(Get-PublicationHash $bundle.manifest_path);frozen_archive_sha256=$bundle.zip_sha256};
    automatic=[ordered]@{status='all_nine_current_groups_passed';groups=$groups};prepared_package=$prepared.status;binary_packaging=$binarySummary;
    signing_identity=[ordered]@{path=$identityRelative;key_id=$keyId;root_version=1;public_trust_sha256=$trustSha256;challenge_status=$publicIdentity.two_fresh_process_open_sign_verify};
    formal_native='not_run';formal_GUI='not_run';full_isolation='not_completed';windows10_11='paused_by_user_not_run';
    ordinary_third_party_required_boundaries='remain_fail_closed_until_verified';signature_and_release='pending_separate_real_asset_binding_sign_verify_and_root_publication';
    old_plus_v1_0_0_tag_commit_to_preserve='f8aac024717984914ce26ce0744201f8b5e934cc';
    old_SDK03_evidence='Historical b776 candidate only; never transferred to new v2 bytes';
    raw_logs_Data_private_keys_tokens_process_IDs_or_account_namespaces_included=$false}
foreach($pair in @(@{path=(Join-Path $PSScriptRoot 'PUBLIC-REKEY-BUILD-AND-TESTS.json');value=$summary},@{path=(Join-Path $PSScriptRoot 'PUBLIC-SIGNING-IDENTITY.json');value=$publicIdentity})){
    if(Test-Path -LiteralPath $pair.path){throw 'Existing public summary preserved.'}
    $pair.value|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $pair.path -Encoding utf8NoBOM
}
$artifactPaths=@(Get-Content -LiteralPath (Join-Path $oldRoot 'artifact-source-paths.json') -Raw|ConvertFrom-Json)
$artifactPaths=@($artifactPaths+$manifestRelative+$summaryRelative+$identityRelative+@(
    "$relativeOutput/Publication-Identity.ps1","$relativeOutput/Package-Sdk04.ps1","$relativeOutput/Prepare-PublicSourceStage.ps1",
    "$relativeOutput/Audit-PublicSourceStage.ps1","$relativeOutput/Export-PublicSourceZip.ps1")|Sort-Object -Unique)
foreach($relative in $artifactPaths){
    if($relative -notmatch '^artifacts/[A-Za-z0-9_./-]+\.(c|cpp|h|cs|csproj|props|slnx|xaml|manifest|ps1|json|html|js|cjs|css|md|txt)$' -or
       $relative -match '(^|/)(AutumnOS_Data|bin|obj|node_modules)(/|$)|\.\.') {throw 'Artifact allowlist has unsupported/private path.'}
}
$artifactPaths|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'artifact-source-paths.json') -Encoding utf8NoBOM
$paths=[Collections.Generic.List[string]]::new()
foreach($relative in @('.gitignore','AGENTS.md','AUTUMNOS_PROGRESS.md','AutumnOS.slnx','CHANGELOG.md','CLOUD_HANDOFF.md','CODEX_START.md','Directory.Build.props','Directory.Packages.props','global.json','LICENSE','LOCAL_WORKFLOW.md','NuGet.Config','README.md','PUBLICATION_STATUS.md')){$paths.Add($relative)}
function Add-PublicationDirectory([string]$directory){
    foreach($i in Get-ChildItem -LiteralPath $directory -Force){
        if($i.Attributes -band [IO.FileAttributes]::ReparsePoint){continue}
        if($i.PSIsContainer){if($i.Name -notin @('bin','obj','node_modules','AutumnOS_Data','.tools','artifacts','.git')){Add-PublicationDirectory $i.FullName}}
        elseif($i.Extension -notin @('.pfx','.p12','.key','.pem','.pk8','.dpapi','.snk','.log','.binlog','.exe','.dll','.zip','.lnk') -and $i.Name -notmatch '^(\.env($|\.)|secrets\.json$|credentials\.json$)'){
            $paths.Add([IO.Path]::GetRelativePath($workspace,$i.FullName).Replace('\','/'))
        }
    }
}
foreach($name in @('.github','autumnos-spec','build','build-inputs','docs','research','samples','schemas','scripts','sdk','src','tests','tools')){
    $root=Join-Path $workspace $name;if(Test-Path -LiteralPath $root){Assert-PublicationPlain $root;Add-PublicationDirectory $root}
}
foreach($relative in $artifactPaths){$paths.Add($relative)}
New-Item -ItemType Directory -Path $stage|Out-Null
function Write-PublicationFile([string]$relative,[byte[]]$bytes){
    if([IO.Path]::IsPathRooted($relative) -or $relative.Contains('\') -or $relative.Contains(':') -or @($relative.Split('/')|Where-Object{$_ -in @('','.','..')}).Count){throw 'Invalid public stage path.'}
    $target=[IO.Path]::GetFullPath((Join-Path $stage $relative))
    if(-not $target.StartsWith($stage+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Public stage path escaped.'}
    New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force|Out-Null
    Assert-PublicationPlain (Split-Path $target -Parent)
    [IO.File]::WriteAllBytes($target,$bytes)
}
foreach($relative in @($paths|Sort-Object -Unique)){
    $source=[IO.Path]::GetFullPath((Join-Path $workspace $relative))
    if(-not $source.StartsWith($workspace+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Publication source escaped workspace.'}
    Assert-PublicationPlain $source
    Write-PublicationFile $relative ([IO.File]::ReadAllBytes($source))
}
# Always overlay virtual and physical build inputs from the real new frozen ZIP.
# This prevents a stale on-disk build-inputs/public-root from entering the new source.
$expected=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
foreach($row in $bundle.manifest.files){if(-not $expected.TryAdd($row.path,$row)){throw 'Duplicate frozen source path.'}}
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$zip=[IO.Compression.ZipFile]::OpenRead($bundle.zip)
try{
    if($zip.Entries.Count -ne $expected.Count+1){throw 'Frozen source entry count differs.'}
    foreach($entry in $zip.Entries){
        if(-not $seen.Add($entry.FullName)){throw 'Duplicate frozen ZIP entry.'}
        $stream=$entry.Open();$memory=[IO.MemoryStream]::new()
        try{$stream.CopyTo($memory);$bytes=$memory.ToArray()}finally{$memory.Dispose();$stream.Dispose()}
        $hash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes))
        if($entry.FullName -ceq 'source-snapshot.json'){
            if($hash -cne (Get-PublicationHash $bundle.manifest_path)){throw 'Frozen ZIP manifest differs.'}
            continue
        }
        $row=$null
        if(-not $expected.TryGetValue($entry.FullName,[ref]$row) -or $entry.Length -ne $row.bytes -or $hash -cne $row.sha256){throw 'Frozen ZIP source bytes differ.'}
        Write-PublicationFile $entry.FullName $bytes
    }
}finally{$zip.Dispose()}
if($expected.ContainsKey('README.md') -or $expected.ContainsKey('PUBLICATION_STATUS.md')){throw 'Public doc overrides cannot modify frozen build source.'}
$csharp=@($groups|Where-Object name -ceq 'tests')[0].total
$status=@'
# AutumnOS v1.0.0 源码与验收状态

制作人：派蒙。2026-11-15。当前构建 `T06-20261115-signing-recovery-prod-01`，产品 `1.0.0`，拟发布标签 `plus-v1.0.0-20261115`，源码 `sha256:a7699603f7e068739b4adf5a2db7508cd3bedc247c36086315604fe7c6d15a2e`。

本轮因原受控签名身份无法在当前允许环境恢复，按所有者授权创建全新生产身份 `autumnos-release-20261115-v2`（rootVersion 1）。新公开根 SHA256 为 `7609941f0705632b4353d350282388b18a2901a18c03a95d2677de82b40180dc`。两个独立新进程的非生产随机挑战已验证私钥可打开、签名可验且旧根拒绝；这不等于真实更新清单已签名或远端发布已完成。私钥没有公开。

新公开根进入实际源快照，全部生产构建、九组自动报告和打包重新关联新字节，没有用新身份给旧 prod01 包补签。新正式配置未启用测试根、测试 feed 或临时平台验收开关。实际构建/九组状态与各报告摘要见 [当前构件摘要](artifacts/signing-recovery-20261115/publication/PUBLIC-REKEY-BUILD-AND-TESTS.json)，新公钥挑战的脱敏范围见 [身份摘要](artifacts/signing-recovery-20261115/publication/PUBLIC-SIGNING-IDENTITY.json)。二进制打包或远端资产若仍 pending，以该摘要和随后真实发布核验为准。

SDK 0.4 / 协议 1 的 `network.getStatus/request`、完整声明域权限、有界 HTTPS/DNS/重定向/头部/二进制预算、会话与预览策略租约、最终投递门禁已实现。原生运行时包含有界 IPC、原始进程/Job 与退出证据、资源限额、STA receipt/fence、独立关闭资源清理。桌面已修窄屏排列/滚动、拖放列数和两行名称行高。这些实现属于当前源码；它们不构成完整隔离验收通过。

SDK03（旧 b776 源、临时平台验收配置）有独立的 Server20348/普通 Medium/合成游客限定原生与 GUI 证据：[历史结果](artifacts/publication-sdk03-20261114/PUBLIC-SDK03-RESULTS.json)。历史四项原生、桌面/游戏存档/网络预览 GUI 结果不转记给本轮新根构件。本轮正式 native/GUI 仍为 `not_run`，除非后续有明确同字节报告。旧 qa03 间歇正常关闭失败根因仍未证明，不能用后续通过覆盖。

完整隔离未完成；Windows10/11 验证由用户暂停，保持 `paused_by_user_not_run`。普通第三方应用仍受权限和 `required_boundaries` 门禁约束，未验证边界继续拒绝。真实 Logto 浏览器往返、普通已安装网络应用持久许可 GUI、物理键鼠/触控/中文输入、完整 DPI/系统/安装器/失败回退矩阵仍需真实证据。预览内存许可不代表普通安装许可验收。

旧 `plus-v1.0.0` tag 应保留在 `f8aac024717984914ce26ce0744201f8b5e934cc`；本轮使用新的 tag/source commit，不覆盖旧标签。旧客户端不会自动信任新根，现有版本未实现远程根轮换；同为 1.0.0 也不能承诺自动升级。首次迁移需要新完整入口并保留原 AutumnOS_Data、存档、凭据与安全账本，稳定 Updater/Bootstrap 的实际根和路径必须一致；不能删除 ledger 或启用未签名无人值守更新绕过。生产清单只有绑定真实 Release/Asset、验签及 payload 核验通过后才可提供，未绑定草稿不能安装。Authenticode 是独立证书事项，未声明已完成。

历史冻结文档 `docs/production-update-signing.md` 和迁移说明中旧 keyId、旧发布限制或“SDK网络未实现”等内容只描述当时构件。当前源码/根及实现状态以本页、当前源清单和 SDK0.4 合同为准，其余历史失败与未执行项保留。
'@
$readme=@'
# Lab Chronicles AutumnOS

**制作人：派蒙。用户入口：AutumnOS.exe。**

AutumnOS 是 C# / .NET10 / WinUI3 原生 Windows 工程，包含内部 Web/WASM `.autumn` 应用、iPad式桌面、账号权限与数据、GitHub 商店和安全更新。它不支持任意 Windows EXE 导入。

当前生产构建 `T06-20261115-signing-recovery-prod-01` / 版本 `1.0.0` / 拟发布标签 `plus-v1.0.0-20261115`，实际源码 `sha256:a7699603f7e068739b4adf5a2db7508cd3bedc247c36086315604fe7c6d15a2e`。在原受控身份无法恢复后，按所有者授权换用全新生产公钥 `autumnos-release-20261115-v2`，重新冻结和构建测试打包；没有用新根签旧包。当前 Build 与九组自动报告全部通过，具体计数和原报告摘要见 [当前摘要](artifacts/signing-recovery-20261115/publication/PUBLIC-REKEY-BUILD-AND-TESTS.json)。

SDK 0.4 / 协议1 已实现 `network.getStatus/request`、声明域授权和有界网络代理；运行时已有退出/Job/STA/fence证据与关闭异常独立清理；桌面修复窄屏滚动/排列、拖放和两行名称。SDK03 历史限定原生/GUI只属于其自身冻结构件。本轮正式 native/GUI 为 not_run，完整隔离仍未完成，Win10/11 由用户暂停；普通第三方 required_boundaries 未验证时仍拒绝。详情与迁移限制见 [发布状态](PUBLICATION_STATUS.md)。

## 本地构建

PowerShell7；固定 SDK10.0.401，使用项目 `.tools/dotnet` 和锁文件，不修改系统 PATH。缺项按 [本地工作方式](LOCAL_WORKFLOW.md) 检查。构建使用全新唯一 ID，生产公钥只含公开数据：

```powershell
./scripts/Get-Environment.ps1
$buildId = 'T06-local-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6)
./scripts/Build.ps1 -BuildId $buildId -BuildVersion '1.0.0' -ReleaseLabel 'plus-v1.0.0-20261115' -ProductionUpdateTrustFile (Join-Path (Get-Location).Path 'build/update-trust.production.json')
./scripts/Test.ps1
./scripts/Package-T05.ps1 -Stage Prepare
./scripts/Package-SingleFile.ps1 -BuildId $buildId
```

Test 和 Package-T05 读取 `artifacts/latest-build.txt`，执行前确认对应本次 ID。完整入口在 `artifacts/single-file/<build-id>/AutumnOS.exe`，会在入口旁保留/展开 AutumnOS_Data；目录版必须带齐 DLL 与资源。源码/SDK打包不等于产品GUI通过，附件最终状态由实际发布记录说明。

SDK JS/类型/Schema 在 [sdk](sdk)，CLI 在 [tools](tools)，示例在 [samples](samples)。网络与权限合同见 [SDK网络](docs/sdk-network.md)、[接口](autumnos-spec/docs/03-interfaces.md) 和 [开发文档](autumnos-spec/docs/06-developer-docs.md)。独立 SDK 含完整自包含 CLI，preview/debug 仍需完整运行宿主；真实 Logto Native 类型、回调及浏览器往返未证实，公开配置不需要 Client Secret。

旧客户端不自动信任新根，迁移必须使用新完整入口并保留用户数据/安全账本。未签名草稿不用于无人值守更新；更新 RSA 签名与 Authenticode 分别核验。商店只读发现 GitHub topic/Release，不建设认可名单。当前源码公开不宣称全部 T01–T06 或完整隔离完成。规范与历史在 [autumnos-spec](autumnos-spec)、[工程记录](AUTUMNOS_PROGRESS.md)；许可见 [LICENSE](LICENSE)，第三方版权保持原样。
'@
Write-PublicationFile 'PUBLICATION_STATUS.md' ($utf8.GetBytes($status.Replace("`r`n","`n")))
Write-PublicationFile 'README.md' ($utf8.GetBytes($readme.Replace("`r`n","`n")))
[ordered]@{status='prepared_pending_final_audit';build_id=$buildId;source_snapshot_id=$snapshotId;frozen_source_files=$expected.Count;stage=$stage;
    ordinary_build=$true;automatic_groups=$groups.Count;csharp_current_total=$csharp;binary_packaging=$binarySummary.status;
    frozen_source_modified=$false;remote_actions=$false;next='Run Audit-PublicSourceStage.ps1 -Execute, then Export-PublicSourceZip.ps1 -Execute; root alone verifies fresh remote HEAD and uploads.'}
    |ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'stage-preparation.json') -Encoding utf8NoBOM
