#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^[1-9][0-9]*$')][string]$ArtifactId,
    [Parameter(Mandatory)][string]$BuildId,
    [Parameter(Mandatory)][string]$BuildVersion,
    [Parameter(Mandatory)][string]$ReleaseLabel,
    [Parameter(Mandatory)][string]$Channel,
    [Parameter(Mandatory)][string]$ReleaseNotesPath
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

# Publication is a separate job: the standard-user build never receives this token.
if ($env:GITHUB_EVENT_NAME -cne 'workflow_dispatch' -or $env:GITHUB_ACTIONS -ne 'true' -or $env:GITHUB_REPOSITORY -ne 'paimeng5201314/autumnOS' -or
    $env:GITHUB_SHA -notmatch '^[a-f0-9]{40}$' -or $env:GITHUB_RUN_ID -notmatch '^[1-9][0-9]*$' -or
    $env:GITHUB_RUN_ATTEMPT -notmatch '^[1-9][0-9]*$' -or
    $env:GITHUB_REF -notlike 'refs/heads/codex/update-first-login-*' -or -not $env:GH_TOKEN) {
    throw 'Publication requires the official Actions run, release branch and job token.'
}
$classification = & "$PSScriptRoot/Get-GitHubReleaseMetadata.ps1" -Version $BuildVersion -Channel $Channel
if ($ReleaseLabel -cne $classification.name -or
    $BuildId -cnotmatch ('^'+[regex]::Escape($ReleaseLabel)+'-[0-9]{8}$')) {
    throw 'The proposed release metadata does not match the product version or build ID.'
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
            if ($release.tag_name -ceq $Channel) {
                throw 'An existing release or draft has this identity; it will not be overwritten.'
            }
        }
    }
    $refs = @(Read-GitHub "repos/$repository/git/matching-refs/tags/$Channel")
    if (@($refs | Where-Object { $_.ref -ceq "refs/tags/$Channel" }).Count) {
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
    $publicRoot = Join-Path $projectRoot 'build/update-trust.production.json'
    $observedSnapshot = & "$PSScriptRoot/New-SourceSnapshot.ps1" -OutputDirectory (Join-Path $work 'source-check') -AdditionalInputs @($publicRoot)
    if ($observedSnapshot -cne $sourceSnapshot) { throw 'Artifact source snapshot differs from the checked out release commit.' }
    Assert-ReportIdentity $build 'build' 'passed'
    Assert-ReportIdentity $package 'single-file package' 'prepared'
    Assert-ReportIdentity $pipeline 'pipeline' 'packaged_native_not_run'
    foreach ($report in @($build, $package, $pipeline)) {
        if ($report.version -cne $BuildVersion -or $report.release_label -cne $ReleaseLabel -or $report.channel -cne $Channel -or $report.release_tag -cne $Channel) {
            throw 'Release label or internal version differs from the reviewed identity.'
        }
    }
    if ($build.test_build -or $build.temporary_platform_acceptance -or $build.isolation_acceptance_root -or @($build.additional_inputs).Count -ne 1 -or
        @($build.build_properties | Where-Object { $_ -like '-p:ProductionUpdateTrustFile=*' }).Count -ne 1 -or $build.configuration -cne 'Release' -or
        $build.rid -cne 'win-x64' -or $package.authenticode -cne 'not_signed' -or
        $package.production_trust -cne 'embedded_public_root_requires_signed_release' -or
        $pipeline.native_tests -cne 'not_run_ci_no_interactive_desktop') {
        throw 'Build or validation boundaries differ from the reviewed preview.'
    }
    foreach ($stage in @('install-pinned-sdk', 'restore-locked-contract-tools', 'cache-verified-webview', 'build',
        'test-current-build', 'prepare-complete-directory', 'package-single-file')) {
        $matches = @($pipeline.stages | Where-Object { $_.name -ceq $stage })
        if ($matches.Count -ne 1 -or $matches[0].status -cne 'passed') { throw "Required pipeline stage did not pass: $stage" }
    }
    foreach ($name in @('tests', 'sdk-tests', 'machine-contracts', 'sample-tests', 'chemleap-model-tests', 'native-client-tests', 'server-http-tests', 't04-tools-tests', 'production-update-tools')) {
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
        contract_tests='passed'; authenticode='not_signed'; production_update_trust='embedded'; channel=$Channel; update_sequence=$build.update_sequence; security_sequence=$build.security_sequence
        native_interactive_tests='not_run_ci_no_interactive_desktop'; distribution='full_portable_exe'
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $assets 'release-build.json') -Encoding utf8NoBOM
    @('AutumnOS.exe', 'release-build.json') | ForEach-Object {
        "{0}  {1}" -f (Get-FileHash -LiteralPath (Join-Path $assets $_) -Algorithm SHA256).Hash.ToLowerInvariant(), $_
    } | Set-Content -LiteralPath (Join-Path $assets 'SHA256SUMS.txt') -Encoding utf8NoBOM
    $bodyPath = Join-Path $work 'release-notes.md'
    $notes = Get-Content -LiteralPath $ReleaseNotesPath -Raw -Encoding utf8
    if ([string]::IsNullOrWhiteSpace($notes)) { throw 'Release notes are required.' }
    [IO.File]::WriteAllText($bodyPath,$notes,[Text.UTF8Encoding]::new($false))

    # Recheck immediately before creation. No --clobber, deletion or existing-release edits.
    Assert-NewRelease
    $draftRequest = Join-Path $work 'draft.json'
    @{ tag_name=$Channel; target_commitish=$sourceCommit; name=$classification.name
        body=(Get-Content -LiteralPath $bodyPath -Raw); draft=$true; prerelease=$classification.prerelease; make_latest=$classification.make_latest } |
        ConvertTo-Json | Set-Content -LiteralPath $draftRequest -Encoding utf8NoBOM
    $created = Invoke-Gh @('api', '--method', 'POST', "repos/$repository/releases", '--input', $draftRequest) | ConvertFrom-Json
    $assetNames = @('AutumnOS.exe', 'SHA256SUMS.txt', 'release-build.json')
    $uploadArguments = @('release', 'upload', $Channel, '--repo', $repository) + @($assetNames | ForEach-Object { Join-Path $assets $_ })
    Invoke-Gh $uploadArguments | Out-Null
    $release = Read-GitHub "repos/$repository/releases/$($created.id)"
    if (-not $release.draft -or $release.prerelease -ne $classification.prerelease -or $release.tag_name -cne $Channel -or
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
    Invoke-Gh @('release', 'download', $Channel, '--repo', $repository, '--dir', $verifiedDownload) | Out-Null
    foreach ($name in $assetNames) {
        if ((Get-FileHash -LiteralPath (Join-Path $verifiedDownload $name)).Hash -cne
            (Get-FileHash -LiteralPath (Join-Path $assets $name)).Hash) {
            throw "Draft asset content differs: $name. The draft remains unpublished."
        }
    }
    $publishRequest = Join-Path $work 'publish.json'
    @{ draft=$false; prerelease=$classification.prerelease; make_latest=$classification.make_latest } | ConvertTo-Json | Set-Content -LiteralPath $publishRequest -Encoding utf8NoBOM
    $published = Invoke-Gh @('api', '--method', 'PATCH', "repos/$repository/releases/$($release.id)", '--input', $publishRequest) | ConvertFrom-Json
    if ($published.draft -or $published.prerelease -ne $classification.prerelease -or $published.tag_name -cne $Channel -or
        $published.target_commitish -cne $sourceCommit) { throw 'GitHub did not confirm the requested preview publication state.' }
    if (!$classification.prerelease -and (Read-GitHub "repos/$repository/releases/latest").id -ne $published.id) { throw 'The stable release is not Latest.' }
    "Release: $($published.html_url)`nEXE SHA-256: $executableHash`nSource commit: $sourceCommit" |
        Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    Write-Host "Published release: $($published.html_url)"
} finally { Pop-Location }
