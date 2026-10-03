[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReportPath,[string]$BuildId='local-untracked',[string]$SourceSnapshotId='not_recorded')
$ErrorActionPreference = 'Stop'
$workspace = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $workspace '.tools/dotnet/dotnet.exe'
$assembly = Join-Path $workspace 'samples/server-identity/bin/x64/Release/net10.0/win-x64/AutumnOS.ServerIdentity.Sample.dll'
New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($ReportPath)) -Parent) -Force | Out-Null
if (Test-Path -LiteralPath $reportPath) { throw 'Refusing to overwrite an existing report.' }
$checks = [System.Collections.Generic.List[object]]::new()
$owned = [System.Collections.Generic.List[System.Diagnostics.Process]]::new()
function Start-Fixture([bool] $configured) {
    $start = [System.Diagnostics.ProcessStartInfo]::new($dotnet)
    $start.ArgumentList.Add($assembly)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.WorkingDirectory = $workspace
    foreach ($name in @('AUTHORITY','METADATA','AUDIENCE','CLIENT_ID','REQUIRED_SCOPE')) { $start.Environment.Remove('AUTUMN_SERVER_' + $name) | Out-Null }
    if ($configured) {
        # Explicit synthetic fixture; no request below has a structurally valid JWT, so no provider request is made.
        $start.Environment['AUTUMN_SERVER_AUTHORITY'] = 'https://identity-fixture.invalid/oidc'
        $start.Environment['AUTUMN_SERVER_METADATA'] = 'https://identity-fixture.invalid/oidc/.well-known/openid-configuration'
        $start.Environment['AUTUMN_SERVER_AUDIENCE'] = 'https://api-fixture.invalid'
        $start.Environment['AUTUMN_SERVER_CLIENT_ID'] = 'fixture-independent-native'
        $start.Environment['AUTUMN_SERVER_REQUIRED_SCOPE'] = 'player:read'
    }
    $process = [System.Diagnostics.Process]::Start($start)
    $owned.Add($process)
    return $process
}
try {
    $unconfigured = Start-Fixture $false
    if (-not $unconfigured.WaitForExit(10000)) { throw 'Unconfigured sample did not exit.' }
    $errorText = $unconfigured.StandardError.ReadToEnd()
    $checks.Add(@{ name='missing configuration exits before listener'; passed=($unconfigured.ExitCode -eq 2 -and $errorText -eq "SERVER_IDENTITY_CONFIGURATION_REQUIRED: consult README; no listener was opened.`r`n"); pid=$unconfigured.Id; exitCode=$unconfigured.ExitCode })
    $port = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 5197)
    $port.Server.ExclusiveAddressUse = $true
    try { $port.Start() } finally { $port.Stop() }
    $server = Start-Fixture $true
    $ready = $false
    for ($i = 0; $i -lt 60; $i++) {
        try { $health = Invoke-RestMethod -Uri 'http://127.0.0.1:5197/health' -TimeoutSec 2; $ready = $true; break }
        catch { if ($server.HasExited) { throw 'Fixture sample exited unexpectedly.' }; Start-Sleep -Milliseconds 100 }
    }
    if (-not $ready) { throw 'Fixture sample did not open owned loopback listener.' }
    $checks.Add(@{ name='real HTTP configured health does not claim provider verification'; passed=($health.status -eq 'configured' -and $health.real_provider_verified -eq $false); pid=$server.Id })
    foreach ($case in @(
        @{ name='missing bearer rejected'; path='/v1/me'; headers=@{}; status=401 },
        @{ name='bare user id rejected'; path='/v1/me'; headers=@{Authorization='Bearer fixture-user'}; status=401 },
        @{ name='nickname header rejected'; path='/v1/me'; headers=@{Authorization='fixture-nickname'}; status=401 },
        @{ name='query credentials rejected'; path='/v1/me?userId=fixture-user'; headers=@{}; status=401 },
        @{ name='forged challenge without auth rejected'; path='/v1/action-challenge'; headers=@{}; status=401; method='POST' }
    )) {
        $method = if ($case.method) { $case.method } else { 'GET' }
        $response = Invoke-WebRequest -Uri ('http://127.0.0.1:5197' + $case.path) -Method $method -Headers $case.headers -SkipHttpErrorCheck -TimeoutSec 5
        $body = $response.Content | ConvertFrom-Json
        $checks.Add(@{ name=$case.name; passed=($response.StatusCode -eq $case.status -and $body.error -eq 'INVALID_CREDENTIAL' -and $response.Content -notmatch 'fixture-user|fixture-nickname'); statusCode=[int]$response.StatusCode })
    }
}
finally {
    foreach ($process in $owned) { if (-not $process.HasExited) { $process.Kill($false); $process.WaitForExit(5000) | Out-Null } }
    $result = [ordered]@{ schema_version=1; type='isolated_synthetic_http_fixture'; executed_utc=[DateTimeOffset]::UtcNow; executable=$assembly; executable_sha256=(Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash.ToLowerInvariant(); command='scripts/Test-ServerIdentitySample.ps1';build_id=$BuildId;source_snapshot_id=$SourceSnapshotId; status= $(if ($checks.Count -eq 7 -and @($checks | Where-Object {-not $_.passed}).Count -eq 0) {'passed'} else {'failed'}); checks=$checks.ToArray(); real_logto='not_run'; cleanup='Only Process objects created by this script were terminated; no launcher/user processes touched'; source='samples/server-identity'; owner_pids=@($owned | ForEach-Object {$_.Id}) }
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
    $result | ConvertTo-Json -Depth 8
}

if ($result.status -ne 'passed') { throw 'Independent server HTTP fixture failed; existing evidence is preserved.' }
