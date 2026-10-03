[CmdletBinding()]
param([string]$BuildId, [string]$ReportDirectory)

. "$PSScriptRoot/Common.ps1"
if (-not $BuildId) { $BuildId = (Get-Content -LiteralPath (Join-Path $ProjectRoot 'artifacts/latest-build.txt') -Raw).Trim() }
if ($BuildId -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,90}$') { throw 'Invalid BuildId.' }
$buildDirectory = Join-Path $ProjectRoot "artifacts/builds/$BuildId"
$build = Get-Content -LiteralPath (Join-Path $buildDirectory 'build-result.json') -Raw | ConvertFrom-Json
if ($build.status -ne 'passed' -or $build.build_id -ne $BuildId -or $build.source_snapshot_id -notmatch '^sha256:[a-f0-9]{64}$') {
    throw 'A successful tracked build is required; developer tools tests were not run.'
}
if (-not $ReportDirectory) { $ReportDirectory = Join-Path $buildDirectory 't04-tools' }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory, $ProjectRoot)
if ($ReportDirectory -match '[\\/]AutumnOS_Data(?:[\\/]|$)' -or (Test-Path -LiteralPath $ReportDirectory)) {
    throw 'Use a new report directory outside AutumnOS_Data; existing reports and fixtures are never overwritten.'
}
New-Item -ItemType Directory -Path $ReportDirectory | Out-Null
$cliDirectory = Join-Path $ProjectRoot 'tools/AutumnOS.Developer.Cli/bin/x64/Release/net10.0/win-x64'
$cli = Join-Path $cliDirectory 'AutumnOS.Developer.Cli.dll'
$fixture = Join-Path $ReportDirectory 'fixture'
$results = [Collections.Generic.List[object]]::new()
$commands = [Collections.Generic.List[object]]::new()
$binaryHashes = [Collections.Generic.List[object]]::new()
$started = [DateTimeOffset]::UtcNow
$status = 'not_run'
$failure = $null
$snapshot = $null
$phase = 'preconditions'

function Run-Cli([string[]]$Arguments, [int]$Expected = 0) {
    $startedCommand = [DateTimeOffset]::UtcNow
    $raw = & $Dotnet $cli @Arguments 2>&1
    $code = $LASTEXITCODE
    $text = $raw -join [Environment]::NewLine
    $commands.Add([ordered]@{
        executable = $Dotnet
        arguments = @($cli) + $Arguments
        working_directory = $ProjectRoot
        started_utc = $startedCommand.ToString('o')
        exit_code = $code
        expected_exit_code = $Expected
        output = $text
    })
    if ($code -ne $Expected) { throw "Unexpected CLI exit $code ($($Arguments[0])). See recorded command output." }
    return $text | ConvertFrom-Json
}
function Check([string]$Name, [bool]$Passed) {
    $results.Add([ordered]@{ name = $Name; status = $(if ($Passed) { 'passed' } else { 'failed' }) })
    Write-Host "$(if ($Passed) { 'PASS' } else { 'FAIL' }) $Name"
    if (-not $Passed) { throw "CLI assertion failed: $Name" }
}
function Inspect-Binary([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Frozen developer tool assembly is missing.' }
    $metadata = @{}
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($Path))
    foreach ($attribute in $assembly.GetCustomAttributesData()) {
        if ($attribute.AttributeType.Name -eq 'AssemblyMetadataAttribute') {
            $metadata[$attribute.ConstructorArguments[0].Value] = $attribute.ConstructorArguments[1].Value
        }
    }
    if ($metadata.BuildId -ne $BuildId -or $metadata.SourceSnapshotId -ne $build.source_snapshot_id) {
        throw 'Developer tool or its AutumnOS dependency does not match the frozen build ID/source snapshot.'
    }
    $binaryHashes.Add([ordered]@{
        path = [IO.Path]::GetRelativePath($ProjectRoot, $Path).Replace('\', '/')
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
        build_id = $metadata.BuildId
        source_snapshot_id = $metadata.SourceSnapshotId
    })
}

Push-Location $ProjectRoot
try {
    $snapshot = & "$PSScriptRoot/New-SourceSnapshot.ps1" -OutputDirectory (Join-Path $ReportDirectory 'before-test') -AdditionalInputs @($build.additional_inputs)
    if ($snapshot -ne $build.source_snapshot_id) { throw 'Sources changed since the frozen build; developer tool tests were not run.' }
    foreach ($file in Get-ChildItem -LiteralPath $cliDirectory -Filter 'AutumnOS*.dll' -File) { Inspect-Binary $file.FullName }
    if ($binaryHashes.Count -lt 3 -or -not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw 'Developer tool dependency set is incomplete.' }
    $phase = 'fixture'
    New-Item -ItemType Directory -Path (Join-Path $fixture 'project') | Out-Null
    $manifest = [ordered]@{
        schemaVersion = 1; appId = 'fixture.cli'; name = '本地发布工具样例'; version = '2.1.0-preview.1'
        runtime = 'web'; entry = 'index.html'; permissions = @('saves'); saveFormatVersion = 2; minReadableSaveFormatVersion = 1
    }
    [IO.File]::WriteAllText((Join-Path $fixture 'project/manifest.json'), ($manifest | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $fixture 'project/index.html'),
        '<!doctype html><html lang="zh-CN"><meta charset="utf-8"><title>fixture CLI</title><h1>隔离发布材料测试</h1></html>',
        [Text.UTF8Encoding]::new($false))
    $phase = 'commands'
    $status = 'failed'
    $first = Run-Cli @('pack', (Join-Path $fixture 'project'), (Join-Path $fixture 'packages'))
    Check 'pack_real_archive_sha' ($first.sha256 -eq (Get-FileHash -LiteralPath $first.packagePath -Algorithm SHA256).Hash.ToLowerInvariant())
    $release = Run-Cli @('generate-release', $first.packagePath, (Join-Path $fixture 'publication'),
        '--developer', '隔离测试开发者', '--description', '本地测试材料，未上传', '--offline', 'true')
    Check 'generated_store_release_real_copy' ((Test-Path -LiteralPath $release.storePath) -and
        (Test-Path -LiteralPath $release.releasePath) -and $release.sha256 -eq $first.sha256)
    $validated = Run-Cli @('validate-publication', $release.storePath, $release.releasePath, $release.packagePath)
    Check 'same_validator_accepts_generated_three_layers' ($validated.status -eq 'passed' -and
        $validated.channel -eq 'preview' -and $validated.liveGitHubRegistration -eq 'not_verified')
    $existing = Run-Cli @('generate-release', $first.packagePath, (Join-Path $fixture 'publication'),
        '--developer', 'fixture', '--description', 'fixture') 2
    Check 'refuses_existing_output_without_overwrite' ($existing.code -eq 'CLI_OUTPUT_ALREADY_EXISTS' -and
        (Get-FileHash -LiteralPath $release.packagePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $first.sha256)
    $second = Run-Cli @('pack', (Join-Path $fixture 'project'), (Join-Path $fixture 'packages'))
    Check 'repeat_pack_retains_old_package' ($second.packagePath -ne $first.packagePath -and (Test-Path -LiteralPath $first.packagePath))
    $bad = Get-Content -LiteralPath $release.releasePath -Raw | ConvertFrom-Json
    $bad.sha256 = '0' * 64
    $bad | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $fixture 'wrong-hash.json') -Encoding utf8
    $hashFailure = Run-Cli @('validate-publication', $release.storePath, (Join-Path $fixture 'wrong-hash.json'), $release.packagePath) 2
    Check 'wrong_hash_rejected' ($hashFailure.code -eq 'STORE_PACKAGE_METADATA_MISMATCH')
    $store = Get-Content -LiteralPath $release.storePath -Raw | ConvertFrom-Json
    $store.appId = 'foreign.app'
    $store | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $fixture 'wrong-store.json') -Encoding utf8
    $identityFailure = Run-Cli @('validate-publication', (Join-Path $fixture 'wrong-store.json'), $release.releasePath, $release.packagePath) 2
    Check 'cross_application_metadata_rejected' ($identityFailure.code -eq 'CLI_PUBLICATION_APP_ID_MISMATCH')
    Copy-Item -LiteralPath $release.packagePath -Destination (Join-Path $fixture 'disguised.exe')
    $extensionFailure = Run-Cli @('validate-publication', $release.storePath, $release.releasePath, (Join-Path $fixture 'disguised.exe')) 2
    Check 'exe_extension_rejected_without_execution' ($extensionFailure.code -eq 'PACKAGE_EXTENSION_INVALID')
    $duplicate = (Get-Content -LiteralPath $release.releasePath -Raw).Replace('"schemaVersion": 1', '"schemaVersion": 1,"schemaVersion": 1')
    [IO.File]::WriteAllText((Join-Path $fixture 'duplicate.json'), $duplicate, [Text.UTF8Encoding]::new($false))
    $dupFailure = Run-Cli @('validate-publication', $release.storePath, (Join-Path $fixture 'duplicate.json'), $release.packagePath) 2
    Check 'duplicate_release_fields_rejected' ($dupFailure.code -eq 'STORE_RELEASE_INVALID')
    $badOption = Run-Cli @('generate-release', $first.packagePath, (Join-Path $fixture 'invalid-output'),
        '--developer', 'fixture', '--description', 'fixture', '--offline', 'maybe') 2
    Check 'invalid_option_no_output' ($badOption.code -eq 'CLI_ARGUMENTS_INVALID' -and -not (Test-Path -LiteralPath (Join-Path $fixture 'invalid-output')))
    [IO.File]::WriteAllBytes((Join-Path $fixture 'project/native.js'), [byte[]](77, 90, 0, 0))
    $native = Run-Cli @('pack', (Join-Path $fixture 'project'), (Join-Path $fixture 'rejected')) 2
    Check 'disguised_native_payload_rejected' ($native.code -eq 'PACKAGE_FILE_TYPE_FORBIDDEN')
    Check 'original_generated_package_preserved' ((Get-FileHash -LiteralPath $release.packagePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $first.sha256)
    $phase = 'final_identity'
    $after = & "$PSScriptRoot/New-SourceSnapshot.ps1" -OutputDirectory (Join-Path $ReportDirectory 'after-test') -AdditionalInputs @($build.additional_inputs)
    if ($after -ne $snapshot) { throw 'Sources changed while developer tool tests were running.' }
    foreach ($binary in $binaryHashes) {
        if ((Get-FileHash -LiteralPath (Join-Path $ProjectRoot $binary.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $binary.sha256) {
            throw 'Developer tool binaries changed while tests were running.'
        }
    }
    $status = 'passed'
} catch {
    $failure = $_.Exception.Message
    if ($phase -eq 'preconditions') { $status = 'not_run' } else { $status = 'failed' }
    throw
} finally {
    [ordered]@{
        schema_version = 1; task_id = 'T04'; build_id = $BuildId; source_snapshot_id = $build.source_snapshot_id
        observed_source_snapshot_id = $snapshot; status = $status; total = $results.Count
        failed = @($results | Where-Object status -eq 'failed').Count; phase = $phase; failure = $failure
        started_utc = $started.ToString('o'); finished_utc = [DateTimeOffset]::UtcNow.ToString('o')
        environment = [Environment]::OSVersion.VersionString; source_kind = 'isolated_local_cli_processes_no_upload'
        live_github = 'not_run'; uploaded = $false; fixture_app_id = 'fixture.cli'
        tool_assemblies = $binaryHashes; results = $results; commands = $commands
    } | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'report.json') -Encoding utf8
    Pop-Location
}
