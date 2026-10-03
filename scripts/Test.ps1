[CmdletBinding()]
param([switch]$ProbeLogto)
. "$PSScriptRoot/Common.ps1"
Push-Location $ProjectRoot
try {
    $buildId = (Get-Content -LiteralPath 'artifacts/latest-build.txt' -Raw).Trim()
    $directory = Join-Path $ProjectRoot "artifacts/builds/$buildId"
    $build = Get-Content -LiteralPath (Join-Path $directory 'build-result.json') -Raw | ConvertFrom-Json
    if ($build.status -ne 'passed') { throw 'A successful tracked Release build is required.' }
    $snapshot = & "$PSScriptRoot/New-SourceSnapshot.ps1" -OutputDirectory (Join-Path $directory 'before-test') -AdditionalInputs @($build.additional_inputs)
    if ($snapshot -ne $build.source_snapshot_id) { throw 'Sources changed since the build. Run Build.ps1 again.' }
    $runner = Join-Path $ProjectRoot 'tests/AutumnOS.Tests/bin/x64/Release/net10.0/win-x64/AutumnOS.Tests.dll'
    $attemptDirectory = Join-Path $directory ('test-runs/' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $attemptDirectory | Out-Null
    $reportPath = Join-Path $attemptDirectory 'tests.json'
    $summaryPath = Join-Path $directory 'tests.json'
    $attempt = [ordered]@{ status='not_run'; build_id=$buildId; source_snapshot_id=$snapshot; attempt=$attemptDirectory }
    $attempt | ConvertTo-Json | Set-Content -LiteralPath $summaryPath -Encoding utf8
    try {
        Invoke-Dotnet -Arguments @($runner,'--report',$reportPath) -LogPath (Join-Path $attemptDirectory 'tests.log')
        $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json
        if ($report.status -ne 'passed' -or $report.build_id -ne $buildId -or $report.source_snapshot_id -ne $snapshot) { throw 'Test binary evidence identity mismatch or failing report.' }
        Copy-Item -LiteralPath $reportPath -Destination $summaryPath -Force
    } catch {
        $attempt.status = 'failed'
        $attempt | ConvertTo-Json | Set-Content -LiteralPath $summaryPath -Encoding utf8
        throw
    }
    if ($ProbeLogto) {
        & $Dotnet $runner --probe-logto | Tee-Object -FilePath (Join-Path $directory 'logto-discovery.json') | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Warning 'Public discovery probe failed; see logto-discovery.json. This is not a login test.' }
    }
    $node = Get-Command node -ErrorAction SilentlyContinue
    if (-not $node) { throw 'Node.js is required for the local SDK contract tests; C# results are preserved.' }
    & $node.Source --test (Join-Path $ProjectRoot 'tests/sdk-tests.cjs') 2>&1 | Tee-Object -FilePath (Join-Path $attemptDirectory 'sdk-tests.log') | Out-Host
    $sdkExit = $LASTEXITCODE
    [ordered]@{build_id=$buildId;source_snapshot_id=$snapshot;status=$(if($sdkExit -eq 0){'passed'}else{'failed'});exit_code=$sdkExit;node=(& $node.Source --version)} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'sdk-tests.json') -Encoding utf8
    if ($sdkExit -ne 0) { throw 'SDK contract tests failed; see sdk-tests.json.' }
    if($buildId.StartsWith('T06-')){
        $contractsPath=Join-Path $attemptDirectory 'machine-contracts.json'
        & $node.Source (Join-Path $ProjectRoot 'tools/contract-checks/contract-check.cjs') --report $contractsPath 2>&1 |
            Tee-Object -FilePath (Join-Path $attemptDirectory 'machine-contracts.log') | Out-Host
        $contractsExit=$LASTEXITCODE
        [ordered]@{build_id=$buildId;source_snapshot_id=$snapshot;status=$(if($contractsExit -eq 0){'passed'}else{'failed'});exit_code=$contractsExit;
            kind='actual_typescript_strict_ajv2020_openapi_not_native_UI';report=$contractsPath}|
            ConvertTo-Json|Set-Content -LiteralPath (Join-Path $directory 'machine-contracts.json') -Encoding utf8
        if($contractsExit -ne 0){throw 'Machine-readable contracts failed; see machine-contracts.json.'}
    }
    & $node.Source --test (Join-Path $ProjectRoot 'tests/t03-sample-tests.cjs') 2>&1 | Tee-Object -FilePath (Join-Path $attemptDirectory 'sample-tests.log') | Out-Host
    $sampleExit = $LASTEXITCODE
    [ordered]@{build_id=$buildId;source_snapshot_id=$snapshot;status=$(if($sampleExit -eq 0){'passed'}else{'failed'});exit_code=$sampleExit;kind='explicit_mock_dom_and_sdk_not_Windows_UI'} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'sample-tests.json') -Encoding utf8
    if ($sampleExit -ne 0) { throw 'T03 sample contract tests failed; see sample-tests.json.' }
    $nativeSample = Join-Path $ProjectRoot 'samples/native-identity-client/bin/x64/Release/net10.0/win-x64/AutumnOS.NativeIdentity.Sample.dll'
    Invoke-Dotnet -Arguments @($nativeSample,'--self-test',(Join-Path $attemptDirectory 'native-client-tests.json')) -LogPath (Join-Path $attemptDirectory 'native-client-tests.log')
    $nativeReport = Get-Content -LiteralPath (Join-Path $attemptDirectory 'native-client-tests.json') -Raw | ConvertFrom-Json
    if ($nativeReport.status -ne 'passed') { throw 'Independent native identity sample fixtures failed.' }
    [ordered]@{build_id=$buildId;source_snapshot_id=$snapshot;status='passed';kind='synthetic_provider_and_resource_fixtures';
        report=[IO.Path]::GetRelativePath($ProjectRoot,(Join-Path $attemptDirectory 'native-client-tests.json'));
        assembly_sha256=(Get-FileHash -LiteralPath $nativeSample).Hash.ToLowerInvariant();real_provider='not_run'} |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'native-client-tests.json') -Encoding utf8
    & "$PSScriptRoot/Test-ServerIdentitySample.ps1" -ReportPath (Join-Path $attemptDirectory 'server-http-tests.json') -BuildId $buildId -SourceSnapshotId $snapshot
    Copy-Item -LiteralPath (Join-Path $attemptDirectory 'server-http-tests.json') -Destination (Join-Path $directory 'server-http-tests.json')
    if ($buildId.StartsWith('T04-') -or $buildId.StartsWith('T05-') -or $buildId.StartsWith('T06-')) {
        & "$PSScriptRoot/Test-T04DeveloperTools.ps1" -BuildId $buildId -ReportDirectory (Join-Path $attemptDirectory 't04-tools')
        Copy-Item -LiteralPath (Join-Path $attemptDirectory 't04-tools/report.json') -Destination (Join-Path $directory 't04-tools-tests.json')
    }
} finally { Pop-Location }
