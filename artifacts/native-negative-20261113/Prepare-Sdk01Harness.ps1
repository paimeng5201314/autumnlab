$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$buildId = 'T06-20261113-sdk-runtime-01'
$buildRoot = Join-Path $workspace "artifacts/builds/$buildId"
$build = Get-Content -LiteralPath (Join-Path $buildRoot 'build-result.json') -Raw | ConvertFrom-Json
if ($build.status -ne 'passed' -or $build.build_id -ne $buildId) { throw 'A successful tracked sdk01 build is required.' }
if ($build.source_snapshot_id -ne 'sha256:a14d906a732c325987c48657abd33843cea1de3e47b73247ed9695be82adfeba' -or
    $build.version -ne '1.0.0-rc.5' -or $build.release_label -ne '1.0.0-platform-test.6') { throw 'The reviewed sdk01 build identity is required.' }
$complete = Get-Content -LiteralPath (Join-Path $buildRoot 'package-prepared.json') -Raw | ConvertFrom-Json
if ($complete.status -ne 'prepared' -or $complete.build_id -ne $buildId -or
    $complete.source_snapshot_id -ne $build.source_snapshot_id) { throw 'Final matching Prepare receipt is required.' }
$tablePath = Join-Path $buildRoot 'native-payload-table.json'
$table = Get-Content -LiteralPath $tablePath -Raw | ConvertFrom-Json
if ($table.buildId -ne $buildId -or $table.sourceSnapshotId -ne $build.source_snapshot_id) { throw 'sdk01 source/table identity mismatch.' }
$prepared = Join-Path $workspace "artifacts/packages/$buildId/AutumnOS-1.0.0-rc.5-win-x64-development"
if ($table.acceptanceRoot -ne $prepared) { throw 'sdk01 prepared path mismatch.' }
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
    if (-not (Test-Path -LiteralPath (Join-Path $prepared $file) -PathType Leaf)) { throw "Prepared sdk01 component missing: $file" }
}
$oldRoot = Join-Path $workspace 'artifacts/native-negative-20261112/qa02-harness'
$newRoot = Join-Path $PSScriptRoot 'sdk01-harness'
if (Test-Path -LiteralPath $newRoot) { throw 'New harness directory already exists; review it instead of overwriting.' }
$templatePath = Join-Path $oldRoot 'Program.cs'
if ((Get-FileHash -LiteralPath $templatePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne 'e45fe876053edf1c028aae4de74dc8c198ffb8c1d56c57298126206062592808') { throw 'Reviewed qa02 harness template changed.' }
$tableHash = (Get-FileHash -LiteralPath $tablePath -Algorithm SHA256).Hash.ToLowerInvariant()
if ($tableHash -ne 'd0a0178121e4368da8ac08d449d8a9d1da6dc8485a0aebb39f69fc7d6ede4a5c') { throw 'The reviewed sdk01 payload table is required.' }
$source = [string]$build.source_snapshot_id
if ($source -notmatch '^sha256:[a-f0-9]{64}$') { throw 'Invalid sdk01 source identity.' }
$null = New-Item -ItemType Directory -Path $newRoot
$utf8 = [Text.UTF8Encoding]::new($false)
$program = [IO.File]::ReadAllText($templatePath).
    Replace('T06-20261112-release-v1-qa02', $buildId).
    Replace('sha256:51f74ba89506bcef946eb9341d0f2c44a5c5bb0aa4ab3f1e21a412dd4ed7aa0b', $source).
    Replace('c9c15fec51c37d1e2b1de41348cc9991209b12ef5c857038f14a0bb138bcc596', $tableHash).
    Replace('qa02-negative-02-kernelbase', 'sdk01-negative-01-sdk-runtime').
    Replace('AutumnOS-1.0.0-rc.1-win-x64-development', 'AutumnOS-1.0.0-rc.5-win-x64-development').
    Replace('native-negative-20261112', 'native-negative-20261113').
    Replace('QA02', 'SDK01').Replace('qa02', 'sdk01')
if ($program.Contains('qa02') -or $program.Contains('QA02') -or $program.Contains('51f74ba89506bcef')) { throw 'Stale product identity in translated harness.' }
[IO.File]::WriteAllText((Join-Path $newRoot 'Program.cs'), $program, $utf8)
$project = [IO.File]::ReadAllText((Join-Path $oldRoot 'Qa02.NativeNegative.csproj')).
    Replace('T06-20261112-release-v1-qa02', $buildId).
    Replace('AutumnOS-1.0.0-rc.1-win-x64-development', 'AutumnOS-1.0.0-rc.5-win-x64-development').Replace('Qa02', 'Sdk01')
[IO.File]::WriteAllText((Join-Path $newRoot 'Sdk01.NativeNegative.csproj'), $project, $utf8)
$props = [IO.File]::ReadAllText((Join-Path $oldRoot 'Directory.Build.props')).Replace('QA02', 'SDK01')
[IO.File]::WriteAllText((Join-Path $newRoot 'Directory.Build.props'), $props, $utf8)
Copy-Item -LiteralPath (Join-Path $oldRoot 'packages.lock.json') -Destination (Join-Path $newRoot 'packages.lock.json')
$files = foreach ($name in 'Program.cs','Sdk01.NativeNegative.csproj','Directory.Build.props','packages.lock.json') {
    $path = Join-Path $newRoot $name
    [ordered]@{ path=$name; bytes=(Get-Item -LiteralPath $path).Length; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
[ordered]@{ status='prepared_not_compiled_or_executed'; driver_revision='sdk01-negative-01-sdk-runtime'; frozen_product=$buildId; source_snapshot_id=$source; table_sha256=$tableHash;
    prepared_root=$prepared; delivery_files_verified=$deliveryRows.Count; delivery_hashes_sha256=(Get-FileHash -LiteralPath $deliveryPath -Algorithm SHA256).Hash.ToLowerInvariant();
    harness_files=@($files); owner_started=$false; matrix_status='not_run'; shared_product_outputs_modified=$false } |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $newRoot 'preparation.json') -Encoding utf8
Write-Output (Join-Path $newRoot 'preparation.json')
