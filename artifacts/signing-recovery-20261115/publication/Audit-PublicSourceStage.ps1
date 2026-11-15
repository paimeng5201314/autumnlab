#requires -Version 7.4
[CmdletBinding()]
param([switch]$Execute)
$ErrorActionPreference = 'Stop'
if (-not $Execute) {
    [pscustomobject]@{status='prepared_not_executed';required_switch='-Execute';remote_operations=$false}
    return
}
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$stage = Join-Path $PSScriptRoot 'source'
$buildId = 'T06-20261115-signing-recovery-prod-01'
$expectedSnapshot = 'sha256:a7699603f7e068739b4adf5a2db7508cd3bedc247c36086315604fe7c6d15a2e'
$snapshotPath = Join-Path $workspace "artifacts/builds/$buildId/source-snapshot.json"
$zipPath = Join-Path $workspace "artifacts/builds/$buildId/source-bytes.zip"
$zipReceiptPath = $zipPath + '.json'
$oldRoot = Join-Path $workspace 'artifacts/publication-sdk03-20261114'
$comparisonCommit = 'f8aac024717984914ce26ce0744201f8b5e934cc'
$comparisonTree = '23f392b579eae6b71b3cef23684a4a8620c49023'
function Assert-PlainPath([string]$Path) {
    for ($cursor=[IO.Path]::GetFullPath($Path);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)) {
        if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected audit input forbidden.' }
    }
}
function Assert-RelativePath([string]$Path) {
    if ([string]::IsNullOrEmpty($Path) -or [IO.Path]::IsPathRooted($Path) -or $Path.Contains('\') -or $Path.Contains(':') -or
        @($Path.Split('/') | Where-Object { $_ -in @('','.','..') -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count) { throw 'Invalid source path.' }
}
foreach ($path in @($stage,$snapshotPath,$zipPath,$zipReceiptPath,(Join-Path $oldRoot 'public-source-inventory.json'))) { Assert-PlainPath $path }
$snapshot = Get-Content -LiteralPath $snapshotPath -Raw | ConvertFrom-Json
$canonical = ($snapshot.files | ForEach-Object { "$($_.sha256)  $($_.path)" }) -join "`n"
$canonicalId = 'sha256:' + [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))
if ($canonicalId -cne $expectedSnapshot -or $snapshot.source_snapshot_id -cne $canonicalId -or $snapshot.files.Count -ne 600) { throw 'Signing-recovery frozen source manifest differs.' }
$snapshotMap = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
$snapshotCaseNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($row in $snapshot.files) {
    Assert-RelativePath $row.path
    if ($row.sha256 -cnotmatch '^[0-9a-f]{64}$' -or $row.bytes -lt 0 -or -not $snapshotMap.TryAdd($row.path,$row) -or -not $snapshotCaseNames.Add($row.path)) { throw 'Invalid or duplicate frozen source entry.' }
}
$zipReceipt = Get-Content -LiteralPath $zipReceiptPath -Raw | ConvertFrom-Json
if ($zipReceipt.status -cne 'passed' -or $zipReceipt.build_id -cne $buildId -or $zipReceipt.source_snapshot_id -cne $expectedSnapshot -or
    $zipReceipt.files -ne 600 -or $zipReceipt.private_data_included -ne $false -or $zipReceipt.zip_sha256 -cnotmatch '^[0-9a-f]{64}$' -or
    [IO.Path]::GetFullPath($zipReceipt.zip) -cne $zipPath -or
    (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $zipReceipt.zip_sha256) { throw 'Actual successful signing-recovery source ZIP receipt differs.' }
$entries = @(Get-ChildItem -LiteralPath $stage -Recurse -Force)
if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Public stage contains a redirected entry.' }
$utf8 = [Text.UTF8Encoding]::new($false,$true)
$patterns = [ordered]@{
    private_key='-----BEGIN (?:RSA |EC |OPENSSH |ENCRYPTED )?PRIVATE KEY-----'
    github_token='\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})\b'
    openai_token='\bsk-(?:proj-)?[A-Za-z0-9_-]{32,}\b'
    jwt_token='\beyJ[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\.[A-Za-z0-9_-]{12,}\b'
}
$hits = [Collections.Generic.List[object]]::new()
$stageCaseNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$inventory = @(foreach ($file in $entries | Where-Object { -not $_.PSIsContainer } | Sort-Object FullName) {
    $relative = [IO.Path]::GetRelativePath($stage,$file.FullName).Replace('\','/')
    Assert-RelativePath $relative
    if (-not $stageCaseNames.Add($relative)) { throw 'Case-colliding public source path.' }
    if ($relative -match '(^|/)(AutumnOS_Data|bin|obj|node_modules|\.tools|\.git)(/|$)|\.(exe|dll|pfx|p12|key|pem|pk8|dpapi|snk|log|binlog|zip|lnk)$|(^|/)(\.env($|\.)|secrets\.json$|credentials\.json$)') { throw 'Private/generated/binary path in public source stage.' }
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $content = $utf8.GetString($bytes)
    foreach ($pattern in $patterns.GetEnumerator()) {
        if ([Text.RegularExpressions.Regex]::IsMatch($content,$pattern.Value)) { $hits.Add([pscustomobject]@{path=$relative;kind=$pattern.Key}) }
    }
    $header = [Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
    $blobBytes = [byte[]]::new($header.Length+$bytes.Length)
    [Array]::Copy($header,0,$blobBytes,0,$header.Length)
    [Array]::Copy($bytes,0,$blobBytes,$header.Length,$bytes.Length)
    [pscustomobject]@{path=$relative;bytes=$bytes.Length;sha256=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($bytes));git_blob=[Convert]::ToHexStringLower([Security.Cryptography.SHA1]::HashData($blobBytes))}
})
if ($hits.Count) {
    [pscustomobject]@{status='review_required_not_for_upload';hits=@($hits)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'secret-scan.json') -Encoding utf8
    throw 'Secret-shaped stage content requires review; values have not been printed.'
}
$map = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
foreach ($row in $inventory) { $map.Add($row.path,$row) }
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
$zipSeen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
try {
    if ($zip.Entries.Count -ne 601) { throw 'Frozen source ZIP must contain 600 source files and its manifest.' }
    foreach ($entry in $zip.Entries) {
        Assert-RelativePath $entry.FullName
        if (-not $zipSeen.Add($entry.FullName)) { throw 'Duplicate frozen source ZIP entry.' }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream)) } finally { $stream.Dispose() }
        if ($entry.FullName -ceq 'source-snapshot.json') {
            if ($entry.Length -ne (Get-Item -LiteralPath $snapshotPath).Length -or $hash -cne (Get-FileHash -LiteralPath $snapshotPath -Algorithm SHA256).Hash.ToLowerInvariant()) { throw 'Frozen ZIP source manifest differs.' }
            continue
        }
        $expected=$null;$source=$null
        if (-not $snapshotMap.TryGetValue($entry.FullName,[ref]$expected) -or -not $map.TryGetValue($entry.FullName,[ref]$source) -or
            $entry.Length -ne $expected.bytes -or $hash -cne $expected.sha256 -or $source.bytes -ne $entry.Length -or $source.sha256 -cne $hash) { throw 'Staged source does not match signing-recovery frozen ZIP bytes.' }
    }
    if (-not $zipSeen.Contains('source-snapshot.json') -or @($snapshot.files | Where-Object { -not $zipSeen.Contains($_.path) }).Count) { throw 'Frozen ZIP omitted a required source entry.' }
} finally { $zip.Dispose() }
$publicTrust = Get-Content -LiteralPath (Join-Path $stage 'build/update-trust.production.json') -Raw | ConvertFrom-Json
if ($publicTrust.purpose -cne 'production' -or $publicTrust.version -ne 1 -or @($publicTrust.keys).Count -ne 1 -or
    $publicTrust.keys[0].keyId -cne 'autumnos-release-20261115-v2' -or $publicTrust.keys[0].revoked) { throw 'New public production signer identity differs.' }
$old = @(Get-Content -LiteralPath (Join-Path $oldRoot 'public-source-inventory.json') -Raw | ConvertFrom-Json)
$oldMap = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
foreach ($row in $old) { $oldMap.Add($row.path,$row) }
$changed = @(foreach ($row in $inventory) {
    $previous=$null; $exists=$oldMap.TryGetValue($row.path,[ref]$previous)
    if (-not $exists -or $previous.git_blob -cne $row.git_blob) { [pscustomobject]@{path=$row.path;status=$(if($exists){'changed'}else{'new'});bytes=$row.bytes;sha256=$row.sha256;git_blob=$row.git_blob;old_git_blob=$(if($exists){$previous.git_blob}else{$null})} }
})
$omitted = @($old | Where-Object { -not $map.ContainsKey($_.path) } | Select-Object path,git_blob)
$elements = @(foreach ($row in $changed) { [pscustomobject]@{path=$row.path;mode='100644';type='blob';content=$utf8.GetString([IO.File]::ReadAllBytes((Join-Path $stage $row.path)))} })
$inventory | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'public-source-inventory.json') -Encoding utf8
$changed | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'changed-files.json') -Encoding utf8
$omitted | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'old-staged-paths-not-present.json') -Encoding utf8
$elements | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'upload-elements.json') -Encoding utf8
$chunkFiles = [Collections.Generic.List[string]]::new(); $chunk=[Collections.Generic.List[object]]::new(); $size=0; $index=0
foreach ($element in $elements) {
    $count=[Text.Encoding]::UTF8.GetByteCount(($element|ConvertTo-Json -Depth 6 -Compress))
    if ($chunk.Count -and $size+$count -gt 35000) {
        $name='upload-chunk-{0:D3}.json' -f $index++; $chunk.ToArray()|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $PSScriptRoot $name) -Encoding utf8
        $chunkFiles.Add($name);$chunk.Clear();$size=0
    }
    $chunk.Add($element);$size+=$count
}
if ($chunk.Count) { $name='upload-chunk-{0:D3}.json' -f $index; $chunk.ToArray()|ConvertTo-Json -Depth 7|Set-Content -LiteralPath (Join-Path $PSScriptRoot $name) -Encoding utf8; $chunkFiles.Add($name) }
$chunkFiles.ToArray()|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $PSScriptRoot 'upload-chunks.json') -Encoding utf8
[long]$total=0;foreach($row in $inventory){$total+=[long]$row.bytes}
[ordered]@{status='source_publication_ready_signing_recovery_frozen_bytes_verified';staging=$stage;files=$inventory.Count;bytes=$total;
    build_id=$buildId;frozen_source_snapshot_id=$canonicalId;frozen_build_source_files_verified=600;source_zip_bytes_verified=$true;
    source_zip_receipt_sha256=(Get-FileHash -LiteralPath $zipReceiptPath -Algorithm SHA256).Hash.ToLowerInvariant();source_zip_sha256=$zipReceipt.zip_sha256;
    public_source_inventory_sha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'public-source-inventory.json') -Algorithm SHA256).Hash.ToLowerInvariant();
    secret_scan='passed_no_matches';excluded='private keys, credentials, user Data, raw runtime logs, binaries, generated outputs';
    comparison_base_commit=$comparisonCommit;comparison_base_tree=$comparisonTree;changed_or_new=$changed.Count;new=@($changed|Where-Object status -eq new).Count;changed=@($changed|Where-Object status -eq changed).Count;
    old_staged_paths_absent=$omitted.Count;old_remote_historical_blobs_to_preserve=76;upload_chunks=$chunkFiles.Count;
    current_remote_HEAD_checked_by_root=$false;root_reported_HEAD=$null;root_reported_tree=$null;remote_operations_performed_here=$false;Git_initialized=$false;
    publication_ready=$true;publication_scope='Local source stage only; root must freshly verify remote HEAD before any non-force source update. No binary, signing, native, GUI or full isolation acceptance is inferred.';
    pending=@('Root alone freshly validates HEAD/tree and creates the new source commit with a non-force ref update.','Preserve the old plus-v1.0.0 tag and its f8aac024 source target; the new release uses plus-v1.0.0-20261115.','Release assets, production signing and formal native/GUI acceptance require their own actual evidence.','No commit, ref, Release, asset, tag or other remote mutation has run here.','If staged public docs change, rerun this local audit before using generated upload elements.')}
    | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'preparation-result.json') -Encoding utf8
[pscustomobject]@{files=$inventory.Count;bytes=$total;frozen_verified=600;changed_or_new=$changed.Count;new=@($changed|Where-Object status -eq new).Count;changed=@($changed|Where-Object status -eq changed).Count;old_paths_absent=$omitted.Count;secret_hits=0;chunks=$chunkFiles.Count;remote_operations=0}
