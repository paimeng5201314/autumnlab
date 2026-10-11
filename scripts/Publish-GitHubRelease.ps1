#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string]$ArtifactId,
    [Parameter(Mandatory)][string]$BuildId,
    [Parameter(Mandatory)][string]$BuildVersion,
    [Parameter(Mandatory)][string]$ReleaseLabel
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

# Publication is a separate job: the standard-user build never receives this token.
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -ne 'paimeng5201314/autumnlab' -or
    $env:GITHUB_SHA -notmatch '^[a-f0-9]{40}$' -or $env:GITHUB_RUN_ID -notmatch '^[1-9][0-9]*$' -or
    $env:GITHUB_RUN_ATTEMPT -notmatch '^[1-9][0-9]*$' -or
    $env:GITHUB_REF -notlike 'refs/heads/codex/update-first-login-*' -or -not $env:GH_TOKEN) {
    throw 'Publication requires the official Actions run, release branch and job token.'
}
if ($ReleaseLabel -cne 'meta0.0.3-20261011' -or $BuildVersion -cne '0.5.1-local.6' -or
    $BuildId -cne "T06-actions-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT") {
    throw 'This publication entry point is limited to the reviewed release and exact run identity.'
}
$repository = $env:GITHUB_REPOSITORY
$sourceCommit = $env:GITHUB_SHA
$projectRoot = Split-Path $PSScriptRoot -Parent
$artifactName = "AutumnOS-$ReleaseLabel-win-x64"
function Invoke-Gh([string[]]$Arguments) {
    $output = & gh @Arguments
    if ($LASTEXITCODE -ne 0) { throw "GitHub CLI operation failed (exit $LASTEXITCODE). No later publication step was attempted." }
    return ($output -join "`n")
}
function Read-GitHub([string]$Path) { return (Invoke-Gh @('api', $Path) | ConvertFrom-Json) }
function Read-Report([string]$Path) { return (Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json) }
function Assert-ReportIdentity($Report, [string]$Name, [string]$Status) {
    if ($Report.status -cne $Status -or $Report.build_id -cne $BuildId -or
        $Report.source_snapshot_id -cne $script:sourceSnapshot) { throw "Mismatched or failing tracked report: $Name" }
}
function Assert-NewRelease {
    # Listing includes drafts visible to the job token. Any API failure aborts, never means absent.
    $pages = Invoke-Gh @('api', '--paginate', '--slurp', "repos/$repository/releases?per_page=100") | ConvertFrom-Json
    foreach ($page in $pages) {
        foreach ($release in $page) {
            if ($release.tag_name -ceq $ReleaseLabel -or $release.name -ceq "AutumnOS $ReleaseLabel") {
                throw 'An existing release or draft has this identity; it will not be overwritten.'
            }
        }
    }
    $refs = @(Read-GitHub "repos/$repository/git/matching-refs/tags/$ReleaseLabel")
    if (@($refs | Where-Object { $_.ref -ceq "refs/tags/$ReleaseLabel" }).Count) {
        throw 'An existing tag has this identity; it will not be overwritten.'
    }
}

Push-Location $projectRoot
try {
    $head = & git rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $head -cne $sourceCommit) { throw 'Checkout does not match the exact Actions source commit.' }
    $run = Read-GitHub "repos/$repository/actions/runs/$env:GITHUB_RUN_ID"
    $artifact = Read-GitHub "repos/$repository/actions/artifacts/$ArtifactId"
    if ($run.head_sha -cne $sourceCommit -or $run.run_attempt -ne [int]$env:GITHUB_RUN_ATTEMPT -or
        $run.path -cne '.github/workflows/windows-exe.yml' -or
        $artifact.id -ne [long]$ArtifactId -or $artifact.name -cne $artifactName -or $artifact.expired -or
        $artifact.workflow_run.id -ne [long]$env:GITHUB_RUN_ID -or $artifact.workflow_run.head_sha -cne $sourceCommit) {
        throw 'Artifact provenance does not match the exact workflow run and source.'
    }
    Assert-NewRelease
    $work = Join-Path $env:RUNNER_TEMP "autumnos-release-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
    if (Test-Path -LiteralPath $work) { throw 'Existing publication evidence is preserved; do not reuse this directory.' }
    $download = Join-Path $work 'artifact'
    $assets = Join-Path $work 'assets'
    New-Item -ItemType Directory -Path $download, $assets | Out-Null
    Invoke-Gh @('run', 'download', $env:GITHUB_RUN_ID, '--repo', $repository, '--name', $artifactName, '--dir', $download) | Out-Null
    $reports = Join-Path $download "builds/$BuildId"
    $package = Read-Report (Join-Path $reports 'package-single-file.json')
    $build = Read-Report (Join-Path $reports 'build-result.json')
    $snapshot = Read-Report (Join-Path $reports 'source-snapshot.json')
    $pipeline = Read-Report (Join-Path $download "build-pipelines/$BuildId/pipeline.json")
    $script:sourceSnapshot = [string]$snapshot.source_snapshot_id
    if ($sourceSnapshot -notmatch '^sha256:[a-f0-9]{64}$') { throw 'Missing source snapshot identity.' }
    $observedSnapshot = & "$PSScriptRoot/New-SourceSnapshot.ps1" -OutputDirectory (Join-Path $work 'source-check')
    if ($observedSnapshot -cne $sourceSnapshot) { throw 'Artifact source snapshot differs from the checked out release commit.' }
    Assert-ReportIdentity $build 'build' 'passed'
    Assert-ReportIdentity $package 'single-file package' 'prepared'
    Assert-ReportIdentity $pipeline 'pipeline' 'packaged_native_not_run'
    foreach ($report in @($build, $package, $pipeline)) {
        if ($report.version -cne $BuildVersion -or $report.release_label -cne $ReleaseLabel) {
            throw 'Release label or internal version differs from the reviewed identity.'
        }
    }
    if ($build.test_build -or @($build.additional_inputs).Count -ne 0 -or $build.configuration -cne 'Release' -or
        $build.rid -cne 'win-x64' -or $package.authenticode -cne 'not_signed' -or
        $package.production_trust -cne 'unconfigured_signed_install_disabled' -or
        $pipeline.native_tests -cne 'not_run_ci_no_interactive_desktop') {
        throw 'Build or validation boundaries differ from the reviewed preview.'
    }
    foreach ($stage in @('install-pinned-sdk', 'restore-locked-contract-tools', 'cache-verified-webview', 'build',
        'test-current-build', 'prepare-complete-directory', 'package-single-file')) {
        $matches = @($pipeline.stages | Where-Object { $_.name -ceq $stage })
        if ($matches.Count -ne 1 -or $matches[0].status -cne 'passed') { throw "Required pipeline stage did not pass: $stage" }
    }
    foreach ($name in @('tests', 'sdk-tests', 'machine-contracts', 'sample-tests', 'native-client-tests', 'server-http-tests', 't04-tools-tests')) {
        Assert-ReportIdentity (Read-Report (Join-Path $reports "$name.json")) $name 'passed'
    }
    $tests = Read-Report (Join-Path $reports 'tests.json')
    if ($tests.total -le 0 -or $tests.failed -ne 0 -or @($tests.filters).Count -ne 0 -or
        @($tests.results).Count -ne $tests.total -or @($tests.results | Where-Object status -ne 'passed').Count) {
        throw 'The complete unfiltered C# test suite must pass.'
    }
    $executable = Join-Path $download "single-file/$BuildId/AutumnOS.exe"
    $executableHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash.ToLowerInvariant()
    $executableBytes = (Get-Item -LiteralPath $executable).Length
    if ($executableHash -cne $package.executable_sha256 -or $executableHash -cne $pipeline.executable_sha256 -or
        $executableBytes -ne $package.bytes -or $executableBytes -le 0 -or
        (Get-Item -LiteralPath $executable).VersionInfo.ProductVersion -cne $ReleaseLabel) {
        throw 'EXE bytes or embedded display version differ from tracked packaging evidence.'
    }
    Copy-Item -LiteralPath $executable -Destination (Join-Path $assets 'AutumnOS.exe')
    $workflowUrl = "https://github.com/$repository/actions/runs/$env:GITHUB_RUN_ID"
    [ordered]@{
        schema_version=1; release_label=$ReleaseLabel; internal_version=$BuildVersion; build_id=$BuildId
        source_commit=$sourceCommit; source_snapshot_id=$sourceSnapshot; workflow_url=$workflowUrl
        workflow_run_attempt=[int]$env:GITHUB_RUN_ATTEMPT; artifact_id=[long]$ArtifactId
        executable='AutumnOS.exe'; executable_sha256=$executableHash; executable_bytes=$executableBytes
        csharp_tests=[ordered]@{ total=$tests.total; passed=$tests.total; failed=0; filtered=$false }
        contract_tests='passed'; authenticode='not_signed'; production_update_trust='unconfigured'
        native_interactive_tests='not_run_ci_no_interactive_desktop'; distribution='full_portable_exe_preview'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $assets 'release-build.json') -Encoding utf8NoBOM
    @('AutumnOS.exe', 'release-build.json') | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash -LiteralPath (Join-Path $assets $_) -Algorithm SHA256).Hash.ToLowerInvariant(), $_
    } | Set-Content -LiteralPath (Join-Path $assets 'SHA256SUMS.txt') -Encoding utf8NoBOM
    $bodyPath = Join-Path $work 'release-notes.md'
    @"
AutumnOS 更新与首次登录改造预览版。版本沿用实际 GitHub 发布顺序，显示版本为 $ReleaseLabel，内部版本为 $BuildVersion。

- 启动时仅检查更新；确认后才下载，下载完成后主动选择重启安装。增加历史版本入口、版本固定和安全回退校验。
- 首次进入桌面前可通过浏览器登录、取消或暂时跳过；后续可在设置中登录。
- 保留上一预览版的数据存储、SDK 与诊断改进。

请下载 AutumnOS.exe。首次运行会在旁边创建 AutumnOS_Data。旧版本升级需退出应用后替换完整 EXE，并保留同目录的 AutumnOS_Data；本次包含稳定启动器与更新器协议改造，旧 EXE 无法通过替换内部载荷完成过渡。

此 EXE 由 [GitHub Actions]($workflowUrl) 构建，源码提交为 $sourceCommit。完整 C# 测试 $($tests.total)/$($tests.total) 以及非交互契约测试通过；SHA-256 见 SHA256SUMS.txt，脱敏构建身份见 release-build.json。

验证边界：EXE 未进行 Authenticode 签名；正式自更新信任公钥尚未配置，本次未发布签名更新清单或自更新 ZIP，可信自动安装尚不可用；Actions 没有执行 Windows 交互界面测试、真实浏览器登录或真实更新重启验收。本发行是预览版，完整正式发布验收尚未完成。
"@ | Set-Content -LiteralPath $bodyPath -Encoding utf8NoBOM

    # Recheck immediately before creation. No --clobber, deletion or existing-release edits.
    Assert-NewRelease
    $draftRequest = Join-Path $work 'draft.json'
    @{ tag_name=$ReleaseLabel; target_commitish=$sourceCommit; name="AutumnOS $ReleaseLabel"
        body=(Get-Content -LiteralPath $bodyPath -Raw); draft=$true; prerelease=$true; make_latest='false' } |
        ConvertTo-Json | Set-Content -LiteralPath $draftRequest -Encoding utf8NoBOM
    $created = Invoke-Gh @('api', '--method', 'POST', "repos/$repository/releases", '--input', $draftRequest) | ConvertFrom-Json
    $assetNames = @('AutumnOS.exe', 'SHA256SUMS.txt', 'release-build.json')
    $uploadArguments = @('release', 'upload', $ReleaseLabel, '--repo', $repository) + @($assetNames | ForEach-Object { Join-Path $assets $_ })
    Invoke-Gh $uploadArguments | Out-Null
    $release = Read-GitHub "repos/$repository/releases/$($created.id)"
    if (-not $release.draft -or -not $release.prerelease -or $release.tag_name -cne $ReleaseLabel -or
        $release.target_commitish -cne $sourceCommit -or @($release.assets).Count -ne $assetNames.Count) {
        throw 'The draft does not have the exact release identity and complete asset set; it remains unpublished.'
    }
    foreach ($name in $assetNames) {
        $remoteAssets = @($release.assets | Where-Object { $_.name -ceq $name })
        if ($remoteAssets.Count -ne 1 -or $remoteAssets[0].state -cne 'uploaded' -or
            $remoteAssets[0].size -ne (Get-Item -LiteralPath (Join-Path $assets $name)).Length) {
            throw "Draft asset upload is incomplete: $name. The draft remains unpublished."
        }
    }
    $verifiedDownload = Join-Path $work 'verified-draft'
    Invoke-Gh @('release', 'download', $ReleaseLabel, '--repo', $repository, '--dir', $verifiedDownload) | Out-Null
    foreach ($name in $assetNames) {
        if ((Get-FileHash -LiteralPath (Join-Path $verifiedDownload $name)).Hash -cne
            (Get-FileHash -LiteralPath (Join-Path $assets $name)).Hash) {
            throw "Draft asset content differs: $name. The draft remains unpublished."
        }
    }
    $publishRequest = Join-Path $work 'publish.json'
    @{ draft=$false; prerelease=$true; make_latest='false' } | ConvertTo-Json | Set-Content -LiteralPath $publishRequest -Encoding utf8NoBOM
    $published = Invoke-Gh @('api', '--method', 'PATCH', "repos/$repository/releases/$($release.id)", '--input', $publishRequest) | ConvertFrom-Json
    if ($published.draft -or -not $published.prerelease -or $published.tag_name -cne $ReleaseLabel -or
        $published.target_commitish -cne $sourceCommit) { throw 'GitHub did not confirm the requested preview publication state.' }
    "Preview Release: $($published.html_url)`nEXE SHA-256: $executableHash`nSource commit: $sourceCommit" |
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    Write-Host "Published preview: $($published.html_url)"
} finally { Pop-Location }
