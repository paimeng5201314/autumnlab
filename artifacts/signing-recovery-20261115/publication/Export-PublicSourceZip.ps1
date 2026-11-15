[CmdletBinding()]
param([switch]$Execute)
if(-not $Execute){throw 'Explicit -Execute is required after successful source-stage audit. No ZIP was created.'}
. (Join-Path $PSScriptRoot 'Publication-Identity.ps1')
$null=Read-PublicationBuild
$null=Read-PublicationAutomatic
$stage=Join-Path $PSScriptRoot 'source'
$inventoryPath=Join-Path $PSScriptRoot 'public-source-inventory.json'
$auditPath=Join-Path $PSScriptRoot 'preparation-result.json'
foreach($p in @($stage,$inventoryPath,$auditPath)){Assert-PublicationPlain $p}
$audit=Get-Content -LiteralPath $auditPath -Raw|ConvertFrom-Json
$inventoryHash=Get-PublicationHash $inventoryPath
if($audit.status -cne 'source_publication_ready_signing_recovery_frozen_bytes_verified' -or -not $audit.publication_ready -or
   $audit.build_id -cne $buildId -or $audit.frozen_source_snapshot_id -cne $snapshotId -or $audit.secret_scan -cne 'passed_no_matches' -or
   $audit.frozen_build_source_files_verified -ne 600 -or -not $audit.source_zip_bytes_verified -or $audit.public_source_inventory_sha256 -cne $inventoryHash){throw 'Matching immutable source-stage audit required.'}
$zipPath=Join-Path $PSScriptRoot 'AutumnOS-v1.0.0-source.zip'
$reportPath=$zipPath+'.json'
if((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $reportPath)){throw 'Existing source archive and evidence preserved.'}
$inventory=@(Get-Content -LiteralPath $inventoryPath -Raw|ConvertFrom-Json)
$expected=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
$caseNames=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($row in $inventory){
    if([IO.Path]::IsPathRooted($row.path) -or $row.path.Contains('\') -or $row.path.Contains(':') -or
       @($row.path.Split('/')|Where-Object{$_ -in @('','.','..') -or $_.EndsWith('.') -or $_.EndsWith(' ')}).Count -or
       -not $expected.TryAdd($row.path,$row) -or -not $caseNames.Add($row.path)){throw 'Invalid or duplicate source archive path.'}
    $p=Join-Path $stage $row.path;Assert-PublicationPlain $p
    if((Get-Item -LiteralPath $p).Length -ne $row.bytes -or (Get-PublicationHash $p) -cne $row.sha256){throw 'Source stage changed after audit.'}
}
$actual=@(Get-ChildItem -LiteralPath $stage -Recurse -Force)
if(@($actual|Where-Object{$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count -or
   @($actual|Where-Object{-not $_.PSIsContainer}).Count -ne $expected.Count -or $expected.Count -ne $audit.files){throw 'Source stage paths changed after audit.'}
$zip=[IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
try{
    foreach($row in $inventory|Sort-Object path){
        $entry=$zip.CreateEntry($row.path,[IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime=[DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero)
        $input=[IO.File]::OpenRead((Join-Path $stage $row.path));$output=$entry.Open()
        try{$input.CopyTo($output)}finally{$output.Dispose();$input.Dispose()}
    }
}finally{$zip.Dispose()}
$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$zip=[IO.Compression.ZipFile]::OpenRead($zipPath)
try{
    if($zip.Entries.Count -ne $expected.Count){throw 'Source ZIP entry count differs.'}
    foreach($entry in $zip.Entries){
        $row=$null
        if(-not $seen.Add($entry.FullName) -or -not $expected.TryGetValue($entry.FullName,[ref]$row) -or $entry.FullName -cne $row.path -or $entry.Length -ne $row.bytes){throw 'Source ZIP path or size differs.'}
        $stream=$entry.Open()
        try{$hash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
        if($hash -cne $row.sha256){throw 'Source ZIP decompressed entry hash differs.'}
    }
}finally{$zip.Dispose()}
[ordered]@{status='passed_all_public_source_entries_verified';archive='AutumnOS-v1.0.0-source.zip';bytes=(Get-Item -LiteralPath $zipPath).Length;sha256=(Get-PublicationHash $zipPath);
    files=$expected.Count;public_source_inventory_sha256=$inventoryHash;frozen_build_source_files_verified=600;source_snapshot_id=$snapshotId;
    formal_build=$buildId;formal_version='1.0.0';formal_release_label=$releaseTag;production_key_id=$keyId;production_public_trust_sha256=$trustSha256;
    formal_temporary_platform_acceptance=$false;formal_build_and_nine_automatic_groups='passed';formal_GUI='not_run';formal_native='not_run';
    full_isolation='not_completed';Windows10_11='paused_by_user_not_run';private_keys_user_Data_raw_logs_or_binaries_included=$false;remote_operations=$false;
    every_decompressed_entry_size_and_sha256_verified=$true;completed_utc=[DateTimeOffset]::UtcNow.ToString('o')}
    |ConvertTo-Json -Depth 5|Set-Content -LiteralPath $reportPath -Encoding utf8NoBOM
Get-Content -LiteralPath $reportPath -Raw|ConvertFrom-Json|Select-Object status,files,bytes,sha256
