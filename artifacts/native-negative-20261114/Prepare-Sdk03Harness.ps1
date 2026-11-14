param([switch]$TemplateOnly)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$buildId = 'T06-20261114-sdk-runtime-03'
$source = 'sha256:b776cc0ec00fca576a075e5d80be09749fdf6b5139d0515c973661a9241fd536'
$tableHash = 'da12bf0e85d40ea4221d806b0d0ddcdb6275ae2a6817439e2c3c21ce6bce3aed'
$buildRoot = Join-Path $workspace "artifacts/builds/$buildId"
$prepared = Join-Path $workspace "artifacts/packages/$buildId/AutumnOS-1.0.0-rc.7-win-x64-development"
$newRoot = Join-Path $PSScriptRoot 'sdk03-harness'
$receiptName = if ($TemplateOnly) { 'template-preparation.json' } else { 'preparation.json' }
$receiptPath = Join-Path $newRoot $receiptName
if (Test-Path -LiteralPath $receiptPath) { throw 'This preparation receipt already exists; inspect it instead of overwriting.' }
$deliveryRows = @()
$deliveryHash = $null
if (-not $TemplateOnly) {
    # Invoke this final validation only after root READY; it never compiles or starts owners.
    $build = Get-Content -LiteralPath (Join-Path $buildRoot 'build-result.json') -Raw | ConvertFrom-Json
    if ($build.status -ne 'passed' -or $build.build_id -ne $buildId -or $build.source_snapshot_id -ne $source -or
        $build.version -ne '1.0.0-rc.7' -or $build.release_label -ne '1.0.0-platform-test.8' -or
        $build.native_payload_table_sha256 -ne $tableHash) { throw 'The reviewed successful SDK03 build identity is required.' }
    $complete = Get-Content -LiteralPath (Join-Path $buildRoot 'package-prepared.json') -Raw | ConvertFrom-Json
    if ($complete.status -ne 'prepared' -or $complete.build_id -ne $buildId -or $complete.source_snapshot_id -ne $source -or
        $complete.executable -ne (Join-Path $prepared 'AutumnOS.exe')) { throw 'Final matching SDK03 Prepare receipt required.' }
    $tablePath = Join-Path $buildRoot 'native-payload-table.json'
    if ((Get-FileHash -LiteralPath $tablePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $tableHash) { throw 'SDK03 frozen payload table hash differs.' }
    $table = Get-Content -LiteralPath $tablePath -Raw | ConvertFrom-Json
    if ($table.buildId -ne $buildId -or $table.sourceSnapshotId -ne $source -or $table.acceptanceRoot -ne $prepared) { throw 'SDK03 table source/path identity differs.' }
    $deliveryPath = Join-Path $buildRoot 'delivery-hashes.json'
    $deliveryRows = @(Get-Content -LiteralPath $deliveryPath -Raw | ConvertFrom-Json)
    if ($deliveryRows.Count -lt 1 -or @($deliveryRows.path | Sort-Object -Unique).Count -ne $deliveryRows.Count) { throw 'Invalid final delivery manifest.' }
    $prefix = [IO.Path]::GetFullPath($prepared).TrimEnd('\') + '\'
    foreach ($row in $deliveryRows) {
        $absolute = [IO.Path]::GetFullPath((Join-Path $prepared ([string]$row.path)))
        if (-not $absolute.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Delivery path escaped SDK03 prepared root.' }
        $item = Get-Item -LiteralPath $absolute -Force
        if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -ne $row.bytes -or
            (Get-FileHash -LiteralPath $absolute -Algorithm SHA256).Hash.ToLowerInvariant() -ne $row.sha256) { throw "Final SDK03 delivery bytes differ: $($row.path)" }
    }
    $deliveryHash = (Get-FileHash -LiteralPath $deliveryPath -Algorithm SHA256).Hash.ToLowerInvariant()
    foreach ($name in 'AutumnOS.NativeHost.Controller.dll','AutumnOS.Runtime.dll','AutumnOS.Contracts.dll','AutumnOS.Storage.dll') {
        if (-not (Test-Path -LiteralPath (Join-Path $prepared $name) -PathType Leaf)) { throw "Prepared SDK03 component missing: $name" }
    }
}
$oldRoot = Join-Path $PSScriptRoot 'sdk02-harness'
$templatePath = Join-Path $oldRoot 'Program.cs'
$templateHash = 'd8cf15021817a6e3795fb373af0bf4339ca2b13631d90866d4b9c40ada03efdb'
if ((Get-FileHash -LiteralPath $templatePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $templateHash) { throw 'Reviewed SDK02 driver template changed.' }
$oldReceipt = Get-Content -LiteralPath (Join-Path $oldRoot 'preparation.json') -Raw | ConvertFrom-Json
foreach ($file in $oldReceipt.harness_files) {
    if ((Get-FileHash -LiteralPath (Join-Path $oldRoot $file.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.sha256) { throw 'SDK02 template input no longer matches its frozen source manifest.' }
}
if (-not (Test-Path -LiteralPath $newRoot)) { New-Item -ItemType Directory -Path $newRoot | Out-Null }
$utf8 = [Text.UTF8Encoding]::new($false)
function Write-ImmutableBytes([string]$name, [byte[]]$bytes) {
    $path = Join-Path $newRoot $name
    if (Test-Path -LiteralPath $path) {
        $expected = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes))
        if ((Get-Item -LiteralPath $path).Length -ne $bytes.Length -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Prepared driver source differs; do not overwrite it.' }
    } else { [IO.File]::WriteAllBytes($path, $bytes) }
}
$program = [IO.File]::ReadAllText($templatePath).
    Replace('T06-20261114-sdk-runtime-02', $buildId).
    Replace('sha256:da3b718ae777d0a8e9ab166044e7d025df89107bf4b4561fac42c6b6bcbdada4', $source).
    Replace('c3c081994fea668703b57ab1c4643be77ec669b2655552fcb17e96ab7f33551b', $tableHash).
    Replace('sdk02-negative-01-desktop', 'sdk03-negative-01-label-height').
    Replace('AutumnOS-1.0.0-rc.6-win-x64-development', 'AutumnOS-1.0.0-rc.7-win-x64-development').
    Replace('SDK02', 'SDK03').Replace('sdk02', 'sdk03')
if ($program.Contains('sdk02') -or $program.Contains('SDK02') -or $program.Contains('da3b718ae777d0a8')) { throw 'Stale product identity in translated SDK03 driver.' }
Write-ImmutableBytes 'Program.cs' $utf8.GetBytes($program)
$project = [IO.File]::ReadAllText((Join-Path $oldRoot 'Sdk02.NativeNegative.csproj')).
    Replace('T06-20261114-sdk-runtime-02', $buildId).
    Replace('AutumnOS-1.0.0-rc.6-win-x64-development', 'AutumnOS-1.0.0-rc.7-win-x64-development').Replace('Sdk02', 'Sdk03')
Write-ImmutableBytes 'Sdk03.NativeNegative.csproj' $utf8.GetBytes($project)
$props = [IO.File]::ReadAllText((Join-Path $oldRoot 'Directory.Build.props')).Replace('SDK02', 'SDK03')
Write-ImmutableBytes 'Directory.Build.props' $utf8.GetBytes($props)
Write-ImmutableBytes 'packages.lock.json' ([IO.File]::ReadAllBytes((Join-Path $oldRoot 'packages.lock.json')))
$files = @(foreach ($name in 'Program.cs','Sdk03.NativeNegative.csproj','Directory.Build.props','packages.lock.json') {
    $path = Join-Path $newRoot $name
    [ordered]@{path=$name;bytes=(Get-Item -LiteralPath $path).Length;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}
})
[ordered]@{status=$(if($TemplateOnly){'template_only_final_product_bytes_not_verified'}else{'prepared_not_compiled_or_executed'});
    frozen_product=$buildId;source_snapshot_id=$source;table_sha256=$tableHash;driver_revision='sdk03-negative-01-label-height';
    template_program_sha256=$templateHash;prepared_root=$prepared;harness_files=$files;delivery_files_verified=$deliveryRows.Count;
    delivery_hashes_sha256=$deliveryHash;prepared_bytes_read=(-not $TemplateOnly);harness_compiled=$false;owner_started=$false;
    matrix_status='not_run';shared_product_outputs_modified=$false} | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath $receiptPath -Encoding utf8
Write-Output $receiptPath
