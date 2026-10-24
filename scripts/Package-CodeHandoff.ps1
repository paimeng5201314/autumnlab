[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildId)
# This creates a local code-verification handoff, never a release acceptance result.
# Native/UI acceptance stays not_run and is deliberately not bypassed in Package-T05.
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
if($BuildId -notmatch '^T06-[A-Za-z0-9_.-]+$'){throw 'Tracked T06 build required.'}
$evidence=Join-Path $projectRoot "artifacts/builds/$BuildId"
$build=Get-Content -LiteralPath (Join-Path $evidence 'build-result.json') -Raw|ConvertFrom-Json
$single=Get-Content -LiteralPath (Join-Path $evidence 'package-single-file.json') -Raw|ConvertFrom-Json
if($build.status -ne 'passed' -or $build.test_build -or $single.status -ne 'prepared' -or
    $single.build_id -cne $BuildId -or $single.source_snapshot_id -cne $build.source_snapshot_id){throw 'Matching ordinary build and prepared single-file package required.'}
function Assert-CodeHandoffPlainPath([string]$Path){
    for($cursor=[IO.Path]::GetFullPath($Path);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Redirected handoff path forbidden.'}
    }
}
$reportNames=@('tests','sdk-tests','machine-contracts','sample-tests','chemleap-model-tests','native-client-tests','server-http-tests','t04-tools-tests','production-update-tools')
foreach($name in $reportNames){
    $test=Get-Content -LiteralPath (Join-Path $evidence "$name.json") -Raw|ConvertFrom-Json
    if($test.status -ne 'passed' -or $test.build_id -cne $BuildId -or $test.source_snapshot_id -cne $build.source_snapshot_id){throw "Matching automatic checks required: $name"}
}
$sourceCheck=Get-Content -LiteralPath (Join-Path $evidence 'source-check.json') -Raw|ConvertFrom-Json
if($sourceCheck.status -ne 'passed'){throw 'Passing static source checks required.'}
$output=Join-Path $projectRoot "artifacts/code-handoff/$BuildId"
Assert-CodeHandoffPlainPath $output
if(Test-Path -LiteralPath $output){throw 'Existing handoff preserved; choose a new build ID.'}
New-Item -ItemType Directory -Path $output|Out-Null
$current=& "$PSScriptRoot/New-SourceSnapshot.ps1" -OutputDirectory (Join-Path $output 'source-verification') -AdditionalInputs @($build.additional_inputs)
if($current -cne $build.source_snapshot_id){throw 'Source changed since build.'}
$expectedSingle=[IO.Path]::GetFullPath((Join-Path $projectRoot "artifacts/single-file/$BuildId/AutumnOS.exe"))
if([IO.Path]::GetFullPath($single.executable) -cne $expectedSingle){throw 'Unexpected single-file input location.'}
Assert-CodeHandoffPlainPath $expectedSingle
if((Get-FileHash -LiteralPath $expectedSingle -Algorithm SHA256).Hash.ToLowerInvariant() -cne $single.executable_sha256){throw 'Single-file bytes changed.'}
$candidate=Join-Path $output 'candidate'
$reports=Join-Path $candidate 'Reports'
New-Item -ItemType Directory -Path $candidate,$reports|Out-Null
Copy-Item -LiteralPath $expectedSingle -Destination (Join-Path $candidate 'AutumnOS.exe')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/isolation-manual-verification.md') -Destination (Join-Path $candidate 'START-HERE.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/runtime-isolation.md') -Destination (Join-Path $candidate 'RUNTIME-SCOPE.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/windows-startup-signing.md') -Destination (Join-Path $candidate 'WINDOWS-STARTUP.md')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/production-update-signing.md') -Destination (Join-Path $candidate 'PRODUCTION-UPDATES.md')
foreach($name in (@('build-result','package-single-file','source-check')+$reportNames)){
    Copy-Item -LiteralPath (Join-Path $evidence "$name.json") -Destination $reports
}
$acceptance=[ordered]@{
    schema_version=1;build_id=$BuildId;source_snapshot_id=$current;producer='派蒙'
    kind='local_code_verification_handoff';release_ready=$false
    automatic_checks='passed';native_product_tests='not_run';extracted_copy_product_tests='not_run'
    authenticode='not_signed';smart_app_control_acceptance='not_run_requires_trusted_signing'
    clipboard_boundary='unimplemented_required_boundary';third_party_launch='denied_before_WebView'
    ChemLeap_normal_mode='not_run_blocked_by_missing_boundary';authorized_network_sdk='unsupported_not_run'
    permission_close_native_regression='not_run';two_document_RTC_native_regression='not_run'
    real_Logto_two_accounts='not_run';historical_failed_evidence='preserved_in_artifacts/isolation-20261018'
    executable_sha256=$single.executable_sha256;fixed_webview2='154.0.4258.53';webview2_sdk='1.0.4258.31'
    isolation_policy='autumn-local-web-v2';user_data_included=$false;global_policy_changes=$false
    meaning='Code checks and archive verification only. This manifest is never used by runtime eligibility.'
}
$acceptance|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $candidate 'ACCEPTANCE.json') -Encoding utf8NoBOM
$inventory=@(Get-ChildItem -LiteralPath $candidate -Recurse -File|ForEach-Object {
    Assert-CodeHandoffPlainPath $_.FullName
    [ordered]@{path=[IO.Path]::GetRelativePath($candidate,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
}|Sort-Object path)
$archivePath=Join-Path $output "AutumnOS-$($build.version)-code-check.zip"
$archive=[IO.Compression.ZipFile]::Open($archivePath,[IO.Compression.ZipArchiveMode]::Create)
try{foreach($entry in $inventory){
    if($entry.path -match '(^|/)(AutumnOS_Data|\.autumnos-update)(/|$)|\.(pk8|pem|key|pfx|p12|snk|dpapi)$'){throw 'Private data cannot enter handoff.'}
    $null=[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $candidate $entry.path),$entry.path,[IO.Compression.CompressionLevel]::Optimal)
}}finally{$archive.Dispose()}
$extracted=Join-Path $output 'extracted'
$archive=[IO.Compression.ZipFile]::OpenRead($archivePath)
try{
    if($archive.Entries.Count -ne $inventory.Count){throw 'ZIP entry count mismatch.'}
    foreach($entry in $inventory){
        $stored=$archive.GetEntry($entry.path)
        if(-not $stored -or $stored.Length -ne $entry.bytes){throw 'ZIP entry metadata mismatch.'}
        $stream=$stored.Open()
        try{$sha=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream))}finally{$stream.Dispose()}
        if($sha -cne $entry.sha256){throw 'ZIP readback hash mismatch.'}
    }
    # Only the exact verified inventory above is extracted into this new directory.
    [IO.Compression.ZipFileExtensions]::ExtractToDirectory($archive,$extracted)
}finally{$archive.Dispose()}
foreach($entry in $inventory){
    if((Get-FileHash -LiteralPath (Join-Path $extracted $entry.path) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256){throw 'Extracted bytes differ.'}
}
$inventory|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $output 'file-hashes.json') -Encoding utf8NoBOM
$result=[ordered]@{
    status='packaged_for_user_verification';build_id=$BuildId;source_snapshot_id=$current
    executable=(Join-Path $candidate 'AutumnOS.exe');executable_sha256=$single.executable_sha256
    zip=$archivePath;zip_sha256=(Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    extracted_executable=(Join-Path $extracted 'AutumnOS.exe');verified_file_count=$inventory.Count
    archive_roundtrip='passed';native_product_tests='not_run';extracted_copy_product_tests='not_run'
    authenticode='not_signed';smart_app_control_acceptance='not_run_requires_trusted_signing'
    release_ready=$false;uploaded=$false;root_entry_replaced=$false;existing_user_data_touched=$false
}
$result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $output 'handoff-result.json') -Encoding utf8NoBOM
$result|ConvertTo-Json -Depth 6
