param([Parameter(Mandatory=$true)][ValidatePattern('^[a-f0-9]{64}$')][string]$ExpectedTableSha256)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$buildId = 'T06-20261114-sdk-runtime-02'
$buildRoot = Join-Path $workspace "artifacts/builds/$buildId"
$build = Get-Content -LiteralPath (Join-Path $buildRoot 'build-result.json') -Raw | ConvertFrom-Json
if ($build.status -ne 'passed' -or $build.build_id -ne $buildId) { throw 'A successful tracked sdk02 build is required.' }
if ($build.source_snapshot_id -ne 'sha256:da3b718ae777d0a8e9ab166044e7d025df89107bf4b4561fac42c6b6bcbdada4' -or
    $build.version -ne '1.0.0-rc.6' -or $build.release_label -ne '1.0.0-platform-test.7') { throw 'The reviewed sdk02 build identity is required.' }
if ($build.native_payload_table_sha256 -ne $ExpectedTableSha256) { throw 'Successful build must record the independently reviewed final table hash.' }
$complete = Get-Content -LiteralPath (Join-Path $buildRoot 'package-prepared.json') -Raw | ConvertFrom-Json
if ($complete.status -ne 'prepared' -or $complete.build_id -ne $buildId -or
    $complete.source_snapshot_id -ne $build.source_snapshot_id) { throw 'Final matching Prepare receipt is required.' }
$tablePath = Join-Path $buildRoot 'native-payload-table.json'
$table = Get-Content -LiteralPath $tablePath -Raw | ConvertFrom-Json
if ($table.buildId -ne $buildId -or $table.sourceSnapshotId -ne $build.source_snapshot_id) { throw 'sdk02 source/table identity mismatch.' }
$prepared = Join-Path $workspace "artifacts/packages/$buildId/AutumnOS-1.0.0-rc.6-win-x64-development"
if ($table.acceptanceRoot -ne $prepared) { throw 'sdk02 prepared path mismatch.' }
if ($complete.executable -ne (Join-Path $prepared 'AutumnOS.exe')) { throw 'Prepare receipt must bind the exact sdk02 directory.' }
$deliveryPath = Join-Path $buildRoot 'delivery-hashes.json'
$deliveryRows = @(Get-Content -LiteralPath $deliveryPath -Raw | ConvertFrom-Json)
if ($deliveryRows.Count -lt 1 -or @($deliveryRows.path | Sort-Object -Unique).Count -ne $deliveryRows.Count) { throw 'Invalid final delivery manifest.' }
$preparedPrefix = [IO.Path]::GetFullPath($prepared).TrimEnd('\') + '\'
foreach ($row in $deliveryRows) {
    $absolute = [IO.Path]::GetFullPath((Join-Path $prepared ([string]$row.path)))
    if (-not $absolute.StartsWith($preparedPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Delivery path escaped prepared root.' }
    $item = Get-Item -LiteralPath $absolute
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
        $item.Length -ne $row.bytes -or (Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash.ToLowerInvariant() -ne $row.sha256) { throw "Final delivery bytes differ: $($row.path)" }
}
foreach ($file in 'AutumnOS.NativeHost.Controller.dll','AutumnOS.Runtime.dll','AutumnOS.Contracts.dll','AutumnOS.Storage.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $prepared $file) -PathType Leaf)) { throw "Prepared sdk02 component missing: $file" }
}
$oldRoot = Join-Path $workspace 'artifacts/native-negative-20261113/sdk01-harness'
$newRoot = Join-Path $PSScriptRoot 'sdk02-harness'
if (Test-Path -LiteralPath $newRoot) { throw 'New harness directory already exists; review it instead of overwriting.' }
$templatePath = Join-Path $oldRoot 'Program.cs'
if ((Get-FileHash -LiteralPath $templatePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne '2f9a8298a4e75a1e49be02f3ebbcb6ae61bea83dd74c26d5c1cf8e291ec4562f') { throw 'Reviewed SDK01 harness template changed.' }
$tableHash = (Get-FileHash -LiteralPath $tablePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($tableHash -ne $ExpectedTableSha256) { throw 'The independently reviewed SDK02 payload table is required.' }
$source = [string]$build.source_snapshot_id
if ($source -notmatch '^sha256:[a-f0-9]{64}$') { throw 'Invalid sdk02 source identity.' }
$null = New-Item -ItemType Directory -Path $newRoot
$utf8 = [Text.UTF8Encoding]::new($false)
$program = [IO.File]::ReadAllText($templatePath).
    Replace('T06-20261113-sdk-runtime-01', $buildId).
    Replace('sha256:a14d906a732c325987c48657abd33843cea1de3e47b73247ed9695be82adfeba', $source).
    Replace('d0a0178121e4368da8ac08d449d8a9d1da6dc8485a0aebb39f69fc7d6ede4a5c', $tableHash).
    Replace('sdk01-negative-01-sdk-runtime', 'sdk02-negative-01-desktop').
    Replace('AutumnOS-1.0.0-rc.5-win-x64-development', 'AutumnOS-1.0.0-rc.6-win-x64-development').
    Replace('native-negative-20261113', 'native-negative-20261114').
    Replace('SDK01', 'SDK02').Replace('sdk01', 'sdk02')
if ($program.Contains('sdk01') -or $program.Contains('SDK01') -or $program.Contains('a14d906a732c3259')) { throw 'Stale product identity in translated harness.' }
[IO.File]::WriteAllText((Join-Path $newRoot 'Program.cs'), $program, $utf8)
$project = [IO.File]::ReadAllText((Join-Path $oldRoot 'Sdk01.NativeNegative.csproj')).
    Replace('T06-20261113-sdk-runtime-01', $buildId).
    Replace('AutumnOS-1.0.0-rc.5-win-x64-development', 'AutumnOS-1.0.0-rc.6-win-x64-development').Replace('Sdk01', 'Sdk02')
[IO.File]::WriteAllText((Join-Path $newRoot 'Sdk02.NativeNegative.csproj'), $project, $utf8)
$props = [IO.File]::ReadAllText((Join-Path $oldRoot 'Directory.Build.props')).Replace('SDK01', 'SDK02')
[IO.File]::WriteAllText((Join-Path $newRoot 'Directory.Build.props'), $props, $utf8)
Copy-Item -LiteralPath (Join-Path $oldRoot 'packages.lock.json') -Destination (Join-Path $newRoot 'packages.lock.json')
$files = foreach ($name in 'Program.cs','Sdk02.NativeNegative.csproj','Directory.Build.props','packages.lock.json') {
    $path = Join-Path $newRoot $name
    [ordered]@{ path=$name; bytes=(Get-Item -LiteralPath $path).Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[ordered]@{ status='prepared_not_compiled_or_executed'; driver_revision='sdk02-negative-01-desktop'; frozen_product=$buildId; source_snapshot_id=$source; table_sha256=$tableHash;
    independently_reviewed_table_sha256=$ExpectedTableSha256; template_program_sha256='2f9a8298a4e75a1e49be02f3ebbcb6ae61bea83dd74c26d5c1cf8e291ec4562f';
    prepared_root=$prepared; delivery_files_verified=$deliveryRows.Count; delivery_hashes_sha256=(Get-FileHash -LiteralPath $deliveryPath -Algorithm SHA256).Hash.ToLowerInvariant();
    harness_files=@($files); owner_started=$false; matrix_status='not_run'; shared_product_outputs_modified=$false } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $newRoot 'preparation.json') -Encoding utf8
Write-Output (Join-Path $newRoot 'preparation.json')
