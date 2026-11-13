#requires -Version 7.4
[CmdletBinding()]
param([string]$BuildId, [string]$SourceSnapshotId, [string]$ReportPath)

$ErrorActionPreference = 'Stop'
# Missing build fields must be rejected by the helper itself, independently of a
# caller's strict-mode setting. Do not mask a missing-field guard with StrictMode.
Set-StrictMode -Off
$projectRoot = Split-Path $PSScriptRoot -Parent
$payloadHelper = Join-Path $projectRoot 'scripts/SingleFilePayloadFiles.ps1'
$productionHelper = Join-Path $projectRoot 'scripts/UpdatePreparationFiles.ps1'
$helperInputs=@(
    @{path='scripts/SingleFilePayloadFiles.ps1';sha256=(Get-FileHash -LiteralPath $payloadHelper).Hash.ToLowerInvariant()},
    @{path='scripts/UpdatePreparationFiles.ps1';sha256=(Get-FileHash -LiteralPath $productionHelper).Hash.ToLowerInvariant()})
. $payloadHelper
. $productionHelper
if (($BuildId -and !$SourceSnapshotId) -or ($SourceSnapshotId -and !$BuildId) -or
    ($BuildId -and $BuildId -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,90}$') -or
    ($SourceSnapshotId -and $SourceSnapshotId -notmatch '^sha256:[a-f0-9]{64}$')) {
    throw 'Supply a valid BuildId and SourceSnapshotId together, or omit both for review.'
}
if ($ReportPath) {
    $ReportPath = [IO.Path]::GetFullPath($ReportPath, $projectRoot)
    if ($ReportPath -match '[\\/]AutumnOS_Data(?:[\\/]|$)' -or (Test-Path -LiteralPath $ReportPath)) {
        throw 'Choose a new report path outside user data.'
    }
    for ($cursor = $ReportPath; $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and
            ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Redirected report paths are forbidden.'
        }
    }
}

$checks = [Collections.Generic.List[object]]::new()
$started = [DateTimeOffset]::UtcNow
function Assert-True([bool]$Condition, [string]$Reason) {
    if (!$Condition) { throw $Reason }
}
function Assert-Rejected([scriptblock]$Action, [string]$ExpectedReason) {
    $rejected = $false
    try { $null = & $Action }
    catch {
        $rejected = $true
        if ($ExpectedReason -and $_.Exception.Message -cne $ExpectedReason) {
            throw "Wrong rejection: $($_.Exception.Message)"
        }
    }
    if (!$rejected) { throw 'Expected helper rejection, but the input was accepted.' }
}
function Test-Case([string]$Name, [scriptblock]$Action) {
    try {
        $null = & $Action
        $checks.Add([ordered]@{name=$Name;status='passed'})
        Write-Host "PASS $Name"
    } catch {
        $checks.Add([ordered]@{name=$Name;status='failed';reason=$_.Exception.Message})
        Write-Host "FAIL $Name"
    }
}
function New-FileRecord([string]$Path, [long]$Bytes, [string]$HashDigit) {
    [pscustomobject]@{path=$Path;bytes=$Bytes;sha256=($HashDigit * 64)}
}
function New-PayloadFixture {
    $catalog = [pscustomobject]@{
        host=@((New-FileRecord 'AutumnOS.NativeHost.Windows.exe' 100 'a'),
            (New-FileRecord 'AutumnOS.NativeHost.Windows.pdb' 80 'b'))
        runtime=@((New-FileRecord 'msedgewebview2.exe' 200 'c'))
    }
    $files = @(
        (New-FileRecord 'AutumnOS.exe' 50 'd'),
        (New-FileRecord 'AutumnOS.Client.exe' 150 'e'),
        (New-FileRecord 'AutumnOS.Shell.pdb' 70 'f'),
        (New-FileRecord 'nested/SHELL.PDB' 60 'a'),
        (New-FileRecord 'NativeHost/AutumnOS.NativeHost.Windows.exe' 100 'a'),
        (New-FileRecord 'NativeHost/AutumnOS.NativeHost.Windows.pdb' 80 'b'),
        (New-FileRecord 'WebView2Runtime/msedgewebview2.exe' 200 'c'))
    [pscustomobject]@{Catalog=$catalog;Files=$files}
}
function New-ProductionBuild {
    [pscustomobject]@{
        status='passed';test_build=$false;temporary_platform_acceptance=$false
        isolation_acceptance_root=$null
        build_properties=@('-p:AutumnPlatformAcceptance=false','-p:AutumnVersion=1.0.0',
            '-p:ProductionUpdateTrustFile=C:/synthetic-public-root.json')
    }
}
$nativeMismatch = 'Native payload differs from compiled catalog.'
$productionRejected = 'Production update rejects test or private platform/isolation acceptance builds.'

Test-Case 'payload.catalogued_native_pdb_is_preserved_shell_pdb_is_removed' {
    $f = New-PayloadFixture
    $plan = Get-SingleFilePayloadPlan -DeliveryFiles $f.Files -NativeCatalog $f.Catalog
    Assert-True ($plan.RequiredNativeFiles -eq 3 -and @($plan.Copied).Count -eq 5 -and @($plan.Removed).Count -eq 2) 'Payload counts changed.'
    Assert-True (@($plan.Copied.path) -ccontains 'NativeHost/AutumnOS.NativeHost.Windows.pdb') 'Compiled-catalog PDB was removed.'
    Assert-True (@($plan.Removed.path) -ccontains 'AutumnOS.Shell.pdb' -and @($plan.Removed.path) -ccontains 'nested/SHELL.PDB') 'Only uncatalogued non-native PDB files may be removed.'
    Assert-True ((@($plan.Copied) | Where-Object path -CEQ 'NativeHost/AutumnOS.NativeHost.Windows.pdb').sha256 -ceq ('b' * 64)) 'Trusted native PDB bytes changed.'
    Assert-True (@($plan.Copied.path) -ccontains 'AutumnOS.exe' -and @($plan.Copied.path) -ccontains 'AutumnOS.Client.exe') 'Product entries were removed.'
}
Test-Case 'payload.missing_catalogued_pdb_is_rejected' {
    $f=New-PayloadFixture
    Assert-Rejected { Get-SingleFilePayloadPlan -DeliveryFiles @($f.Files | Where-Object path -CNE 'NativeHost/AutumnOS.NativeHost.Windows.pdb') -NativeCatalog $f.Catalog } 'Missing catalogued native payload file.'
}
foreach ($extra in @('NativeHost/extra.dll','NativeHost/extra.pdb','WebView2Runtime/extra.dll','WebView2Runtime/extra.PDB')) {
    Test-Case ('payload.uncatalogued_file_rejected.'+$extra) {
        $f=New-PayloadFixture
        Assert-Rejected { Get-SingleFilePayloadPlan -DeliveryFiles @($f.Files + (New-FileRecord $extra 1 'a')) -NativeCatalog $f.Catalog } 'Uncatalogued native payload file.'
    }
}
Test-Case 'payload.native_hash_change_is_rejected' {
    $f=New-PayloadFixture; $f.Files[5].sha256='a' * 64
    Assert-Rejected { Get-SingleFilePayloadPlan $f.Files $f.Catalog } $nativeMismatch
}
Test-Case 'payload.native_size_change_is_rejected' {
    $f=New-PayloadFixture; $f.Files[5].bytes++
    Assert-Rejected { Get-SingleFilePayloadPlan $f.Files $f.Catalog } $nativeMismatch
}
foreach ($replacement in @('NativeHost/AutumnOS.NativeHost.Windows.PDB','nativehost/AutumnOS.NativeHost.Windows.pdb')) {
    Test-Case ('payload.native_casing_change_is_rejected.'+$replacement) {
        $f=New-PayloadFixture; $f.Files[5].path=$replacement
        Assert-Rejected { Get-SingleFilePayloadPlan $f.Files $f.Catalog } $nativeMismatch
    }
}
Test-Case 'payload.runtime_prefix_casing_change_is_rejected' {
    $f=New-PayloadFixture; $f.Files[6].path='webview2runtime/msedgewebview2.exe'
    Assert-Rejected { Get-SingleFilePayloadPlan $f.Files $f.Catalog } $nativeMismatch
}
Test-Case 'payload.delivery_duplicate_case_insensitive_is_rejected' {
    $f=New-PayloadFixture
    Assert-Rejected { Get-SingleFilePayloadPlan @($f.Files + (New-FileRecord 'nativehost/AutumnOS.NativeHost.Windows.PDB' 80 'b')) $f.Catalog } 'Duplicate or empty delivery path.'
}
Test-Case 'payload.catalog_duplicate_case_insensitive_is_rejected' {
    $f=New-PayloadFixture; $f.Catalog.host += New-FileRecord 'AutumnOS.NativeHost.Windows.PDB' 80 'b'
    Assert-Rejected { Get-SingleFilePayloadPlan $f.Files $f.Catalog } 'Duplicate native payload entry.'
}
foreach ($group in @('host','runtime')) {
    Test-Case ('payload.empty_catalog_is_rejected.'+$group) {
        $f=New-PayloadFixture; $f.Catalog.$group=@()
        Assert-Rejected { Get-SingleFilePayloadPlan $f.Files $f.Catalog } 'Empty native payload catalog.'
    }
}
foreach ($badPath in @('../escape.dll','/absolute.dll','a//b.dll','a\b.dll','a:stream','a./b.dll')) {
    Test-Case ('payload.invalid_catalog_path_is_rejected.'+$badPath) {
        $f=New-PayloadFixture; $f.Catalog.host[0].path=$badPath
        Assert-Rejected { Get-SingleFilePayloadPlan $f.Files $f.Catalog } 'Invalid native payload entry.'
    }
}
Test-Case 'payload.invalid_catalog_hash_is_rejected' {
    $f=New-PayloadFixture; $f.Catalog.host[0].sha256='A' * 64
    Assert-Rejected { Get-SingleFilePayloadPlan $f.Files $f.Catalog } 'Invalid native payload entry.'
}
Test-Case 'payload.negative_catalog_size_is_rejected' {
    $f=New-PayloadFixture; $f.Catalog.host[0].bytes=-1
    Assert-Rejected { Get-SingleFilePayloadPlan $f.Files $f.Catalog } 'Invalid native payload entry.'
}
Test-Case 'production.ordinary_explicit_build_is_allowed' {
    Assert-ProductionUpdateBuild (New-ProductionBuild)
}
Test-Case 'production.explicit_empty_acceptance_root_is_allowed' {
    $b=New-ProductionBuild; $b.isolation_acceptance_root=''
    Assert-ProductionUpdateBuild $b
}
foreach ($flag in @('test_build','temporary_platform_acceptance')) {
    Test-Case ('production.enabled_flag_is_rejected.'+$flag) {
        $b=New-ProductionBuild; $b.$flag=$true
        Assert-Rejected { Assert-ProductionUpdateBuild $b } $productionRejected
    }
}
Test-Case 'production.private_acceptance_root_is_rejected' {
    $b=New-ProductionBuild; $b.isolation_acceptance_root='C:/synthetic-private-acceptance'
    Assert-Rejected { Assert-ProductionUpdateBuild $b } $productionRejected
}
foreach ($field in @('status','test_build','temporary_platform_acceptance','isolation_acceptance_root','build_properties')) {
    Test-Case ('production.missing_field_is_rejected.'+$field) {
        $b=New-ProductionBuild; $b.PSObject.Properties.Remove($field)
        Assert-Rejected { Assert-ProductionUpdateBuild $b } $productionRejected
    }
}
foreach ($field in @('test_build','temporary_platform_acceptance')) {
    foreach ($invalidFlag in @($null,'false',0)) {
        $kind=if($null -eq $invalidFlag){'null'}else{$invalidFlag.GetType().Name}
        Test-Case ('production.nonboolean_flag_is_rejected.'+$field+'.'+$kind) {
            $b=New-ProductionBuild; $b.$field=$invalidFlag
            Assert-Rejected { Assert-ProductionUpdateBuild $b } $productionRejected
        }
    }
}
foreach ($property in @('-p:TestUpdateTrustFile=C:/synthetic-test.json',
    '-p:TestUpdateFeedFile=C:/synthetic-feed.json','-p:UpdateHealthFault=true',
    '-p:AutumnPlatformAcceptance=true','-p:AutumnPlatformAcceptance=TRUE')) {
    Test-Case ('production.unsafe_build_property_is_rejected.'+$property) {
        $b=New-ProductionBuild; $b.build_properties += $property
        Assert-Rejected { Assert-ProductionUpdateBuild $b } $productionRejected
    }
}
Test-Case 'helpers.source_bytes_remain_stable_during_run' {
    foreach($inputRecord in $helperInputs){
        $path=Join-Path $projectRoot $inputRecord.path
        Assert-True ((Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -ceq $inputRecord.sha256) 'Helper source changed during the test run.'
    }
}

$failed=@($checks | Where-Object status -NE 'passed').Count
$report=[ordered]@{
    schema_version=1
    build_id=$(if($BuildId){$BuildId}else{'review-not-frozen'})
    source_snapshot_id=$(if($SourceSnapshotId){$SourceSnapshotId}else{'not_recorded'})
    status=$(if($failed){'failed'}else{'passed'})
    scope='actual_powershell_payload_and_publication_guards_with_in_memory_fixture_records'
    review_only=(!$BuildId)
    product_build='not_run';native_product_tests='not_run';private_key_access=$false
    files_deleted=0;user_data_accessed=$false
    started_utc=$started.ToString('o');finished_utc=[DateTimeOffset]::UtcNow.ToString('o')
    helpers=$helperInputs
    passed=($checks.Count-$failed);failed=$failed;tests=$checks.ToArray()
}
if ($ReportPath) {
    $parent=Split-Path $ReportPath -Parent
    if (!(Test-Path -LiteralPath $parent)) { $null=New-Item -ItemType Directory -Path $parent -Force }
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes(($report|ConvertTo-Json -Depth 9))
    $stream=[IO.FileStream]::new($ReportPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $stream.Write($bytes);$stream.Flush($true) } finally { $stream.Dispose() }
}
[pscustomobject]$report
if ($failed) { throw "Packaging/publication helper tests failed: $failed/$($checks.Count)." }
