[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root = Join-Path $workspace 'artifacts/isolation-completion-20261030'
$build = [IO.Path]::GetFullPath($BuildDirectory)
if (-not $build.StartsWith((Join-Path $root 'builds') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Use a tracked local isolation-probe build directory.'
}
function Assert-PlainPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    $drive = [IO.Path]::GetPathRoot($full)
    if ($drive.Length -ne 3 -or [IO.DriveInfo]::new($drive).DriveType -ne [IO.DriveType]::Fixed) { throw 'Fixed local volume required.' }
    $current = $drive
    foreach ($part in $full.Substring($drive.Length).Split([char]'\', [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = Join-Path $current $part
        $item = Get-Item -LiteralPath $current -Force
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse-point probe input rejected.' }
    }
}
Assert-PlainPath $build
$record = Get-Content -LiteralPath (Join-Path $build 'build-result.json') -Raw | ConvertFrom-Json
if ($record.status -cne 'passed_compilation_only') { throw 'Successful tracked probe compilation required.' }
foreach ($source in $record.source_files) {
    $path = [IO.Path]::GetFullPath((Join-Path $workspace $source.path))
    if (-not $path.StartsWith($workspace + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Build source binding escapes workspace.' }
    Assert-PlainPath $path
    if ((Get-Item -LiteralPath $path).Length -ne $source.bytes -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $source.sha256) { throw 'Probe source changed since compilation; rebuild.' }
}
foreach ($output in $record.outputs) {
    if ($output.directory -cne (Join-Path $build $output.kind)) { throw 'Unexpected output directory binding.' }
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $output.files) {
        $path = [IO.Path]::GetFullPath((Join-Path $output.directory $file.path))
        if (-not $path.StartsWith($output.directory + '\', [StringComparison]::OrdinalIgnoreCase) -or -not $expected.Add($path)) { throw 'Invalid output file binding.' }
        Assert-PlainPath $path
        if ((Get-Item -LiteralPath $path).Length -ne $file.bytes -or
            (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256) { throw 'Compiled probe bytes changed.' }
    }
    $actual = @(Get-ChildItem -LiteralPath $output.directory -File -Recurse | Select-Object -ExpandProperty FullName)
    if (-not $expected.SetEquals([string[]]$actual)) { throw 'Compiled probe file set differs.' }
}
$runtime = Join-Path $workspace '.tools/webview2/154.0.4258.53/Microsoft.WebView2.FixedVersionRuntime.154.0.4258.53.x64'
Assert-PlainPath $runtime
$runtimeFiles = @(Get-ChildItem -LiteralPath $runtime -Recurse -File | Sort-Object FullName | ForEach-Object {
    Assert-PlainPath $_.FullName
    [ordered]@{path=[IO.Path]::GetRelativePath($runtime,$_.FullName).Replace('\','/');bytes=$_.Length;
        sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
})
$hostOutput = @($record.outputs | Where-Object kind -CEQ 'host')
$parentOutput = @($record.outputs | Where-Object kind -CEQ 'parent')
if ($hostOutput.Count -ne 1 -or $parentOutput.Count -ne 1) { throw 'One host and one parent output required.' }
$nonce = [Guid]::NewGuid().ToString('N')
$invocation = Join-Path $root ('invocations/browser-' + $nonce)
$cases = Join-Path $root 'runs'
if ([IO.Directory]::Exists($invocation) -or [IO.File]::Exists($invocation)) { throw 'Fresh invocation required.' }
[void][IO.Directory]::CreateDirectory($invocation)
[void][IO.Directory]::CreateDirectory($cases)
Assert-PlainPath $cases
$manifestPath = Join-Path $invocation 'input-manifest.json'
[ordered]@{schema_version=1;case_root=$cases;child_directory=$hostOutput[0].directory;runtime_directory=$runtime;
    child_executable='AutumnOS.OwnedAppContainerBrowserHost.exe';runtime_version='154.0.4258.53';
    child_files=$hostOutput[0].files;runtime_files=$runtimeFiles} |
    ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
$parentExecutable = Join-Path $parentOutput[0].directory 'AutumnOS.OwnedAppContainerBrowserParent.exe'
$launchRecord = [ordered]@{schema_version=1;producer='派蒙';source_snapshot_id=$record.source_snapshot_id;
    build_id=$record.build_id;case_nonce=$nonce;status='launching';started_utc=[DateTimeOffset]::UtcNow;
    parent_sha256=(Get-FileHash -LiteralPath $parentExecutable -Algorithm SHA256).Hash.ToLowerInvariant();
    manifest_sha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();
    product_acceptance='not_claimed_mechanism_only';clipboard_operations='not_run';network_probe_operations='not_run'}
$launchPath = Join-Path $invocation 'invocation.json'
$launchRecord | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $launchPath -Encoding utf8NoBOM
Write-Host "Owned browser mechanism invocation: $invocation"
Write-Host 'A/B opens only the trusted probe window without desktop input or clipboard operations.'
& $parentExecutable --execute $nonce $manifestPath 2>&1 | Tee-Object -FilePath (Join-Path $invocation 'parent-output.log') | Out-Host
$exitCode = $LASTEXITCODE
$launchRecord.status = 'parent_returned'
$launchRecord.exit_code = $exitCode
$launchRecord.completed_utc = [DateTimeOffset]::UtcNow
$launchRecord | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $launchPath -Encoding utf8NoBOM
Write-Host "Parent exit code: $exitCode; inspect exact case report before interpreting any result."
if ($exitCode -ne 0) { throw 'Owned browser mechanism did not pass. Original evidence retained.' }
