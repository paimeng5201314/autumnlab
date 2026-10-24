[CmdletBinding()]
param([string]$BuildId, [string]$SourceSnapshotId, [string]$ReportPath)

. "$PSScriptRoot/Common.ps1"
$PSNativeCommandUseErrorActionPreference = $false
if (-not $BuildId) { $BuildId = (Get-Content -LiteralPath (Join-Path $ProjectRoot 'artifacts/latest-build.txt') -Raw).Trim() }
if ($BuildId -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,90}$') { throw 'Invalid BuildId.' }
$buildDirectory = Join-Path $ProjectRoot "artifacts/builds/$BuildId"
$build = Get-Content -LiteralPath (Join-Path $buildDirectory 'build-result.json') -Raw | ConvertFrom-Json
if (-not $SourceSnapshotId) { $SourceSnapshotId = $build.source_snapshot_id }
if ($build.status -ne 'passed' -or $build.build_id -cne $BuildId -or
    $SourceSnapshotId -notmatch '^sha256:[a-f0-9]{64}$' -or $build.source_snapshot_id -cne $SourceSnapshotId) {
    throw 'Matching successful tracked build and source identity required.'
}
if (-not $ReportPath) { $ReportPath = Join-Path $buildDirectory 'production-update-tools/report.json' }
$ReportPath = [IO.Path]::GetFullPath($ReportPath, $ProjectRoot)
function Assert-PlainPath([string]$Path) {
    for ($cursor = [IO.Path]::GetFullPath($Path); $cursor; $cursor = [IO.Path]::GetDirectoryName($cursor)) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Redirected test path forbidden.'
        }
    }
}
Assert-PlainPath $ReportPath
if ($ReportPath -match '[\\/]AutumnOS_Data(?:[\\/]|$)' -or (Test-Path -LiteralPath $ReportPath)) {
    throw 'Use a new report path outside user data; previous reports are preserved.'
}
$reportDirectory = Split-Path $ReportPath -Parent
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
$fixture = Join-Path $reportDirectory ('production-update-fixture-' + [Guid]::NewGuid().ToString('N'))
$temporaryParent = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath([IO.Path]::GetTempPath()))
$temporaryName = 'AutumnOS-update-tools-test-' + [Guid]::NewGuid().ToString('N')
$temporaryRoot = [IO.Path]::GetFullPath((Join-Path $temporaryParent $temporaryName))
if ($temporaryRoot -match '[\\/]artifacts(?:[\\/]|$)|[\\/]AutumnOS_Data(?:[\\/]|$)') {
    throw 'Temporary key fixture must remain outside artifacts and user data.'
}
$privateDirectory = Join-Path $temporaryRoot 'owned-synthetic-production-key'
$cliDirectory = Join-Path $ProjectRoot 'tools/AutumnOS.Update.Cli/bin/x64/Release/net10.0/win-x64'
$cli = Join-Path $cliDirectory 'AutumnOS.Update.Cli.dll'
$checks = [Collections.Generic.List[object]]::new()
$commands = [Collections.Generic.List[object]]::new()
$assemblies = [Collections.Generic.List[object]]::new()
$started = [DateTimeOffset]::UtcNow
$phase = 'preconditions'
$status = 'not_run'
$failure = $null
$temporaryCreated = $false
$cleanup = 'not_created'

function Assert-Case([string]$Name, [bool]$Condition) {
    $checks.Add([ordered]@{name=$Name;status=$(if($Condition){'passed'}else{'failed'})})
    Write-Host "$(if($Condition){'PASS'}else{'FAIL'}) $Name"
    if (-not $Condition) { throw "Production update tool assertion failed: $Name" }
}
function Invoke-FixtureTool([string[]]$Arguments, [int]$ExpectedExit = 0) {
    # Arguments only name this test's owned directories. Never call a real signer.
    $raw = & $Dotnet $cli @Arguments 2>&1
    $exitCode = $LASTEXITCODE
    $output = $raw -join [Environment]::NewLine
    $errorCode = if ($output.Trim() -match '^(UPDATE_[A-Z0-9_]+)(?:: [A-Za-z.]+)?$') { $Matches[1] } else { $null }
    $commands.Add([ordered]@{
        command=$Arguments[0];exit_code=$exitCode;expected_exit_code=$ExpectedExit;error_code=$errorCode
        arguments=@($Arguments | ForEach-Object { $_.Replace($privateDirectory, '<owned-temporary-key>') })
    })
    # Do not persist raw output: a regression must not copy private key text into evidence.
    if ($exitCode -ne $ExpectedExit) {
        $checks.Add([ordered]@{name=($phase+'.command_exit');status='failed';expected=$ExpectedExit;actual=$exitCode})
        throw 'Unexpected update tool exit; only the command name and numeric exit were recorded.'
    }
    return [pscustomobject]@{Text=$output;ErrorCode=$errorCode}
}
function Hash-File([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function Write-Json([string]$Path, $Value) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
}

Push-Location $ProjectRoot
try {
    if (-not [OperatingSystem]::IsWindows()) { throw 'DPAPI production signer tests require Windows.' }
    . "$PSScriptRoot/UpdatePreparationFiles.ps1"
    foreach ($binary in Get-ChildItem -LiteralPath $cliDirectory -Filter 'AutumnOS*.dll' -File) {
        Assert-PlainPath $binary.FullName
        $metadata = @{}
        $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($binary.FullName))
        foreach ($attribute in $assembly.GetCustomAttributesData()) {
            if ($attribute.AttributeType.Name -eq 'AssemblyMetadataAttribute') {
                $metadata[$attribute.ConstructorArguments[0].Value] = $attribute.ConstructorArguments[1].Value
            }
        }
        if ($metadata.BuildId -cne $BuildId -or $metadata.SourceSnapshotId -cne $SourceSnapshotId) {
            throw 'Update tool assembly does not match the tracked build/source identity.'
        }
        $assemblies.Add([ordered]@{path=[IO.Path]::GetRelativePath($ProjectRoot,$binary.FullName).Replace('\','/');sha256=(Hash-File $binary.FullName)})
    }
    if ($assemblies.Count -lt 2 -or -not (Test-Path -LiteralPath $cli -PathType Leaf)) { throw 'Update tool assemblies missing.' }
    Assert-PlainPath $fixture
    Assert-PlainPath $temporaryRoot
    if (Test-Path -LiteralPath $temporaryRoot) { throw 'New temporary ownership directory required.' }
    New-Item -ItemType Directory -Path $fixture,$temporaryRoot | Out-Null
    $temporaryCreated = $true
    $status = 'failed'
    $source = Join-Path $fixture 'inert-source'
    New-Item -ItemType Directory -Path $source | Out-Null
    # These names exercise the real package contract, but all three files are inert text.
    foreach ($name in @('AutumnOS.Client.exe','AutumnOS.Client.dll','owned-fixture.txt')) {
        [IO.File]::WriteAllText((Join-Path $source $name), 'Owned inert update-tool fixture, never executable: '+$name, [Text.UTF8Encoding]::new($false))
    }
    $keyId = 'owned-cli-fixture-'+[Guid]::NewGuid().ToString('N')
    $public = Join-Path $fixture 'owned-public.json'
    $phase = 'init_production'
    $initialized = (Invoke-FixtureTool @('init-production','--private-directory',$privateDirectory,'--public-output',$public,'--key-id',$keyId)).Text | ConvertFrom-Json
    $trust = Get-Content -LiteralPath $public -Raw | ConvertFrom-Json
    $privateFile = Join-Path $privateDirectory 'signing-key.dpapi'
    $protectedHash = Hash-File $privateFile
    $publicHash = Hash-File $public
    Assert-Case 'production_signer_creates_owned_DPAPI_key_and_public_only_trust' ($initialized.KeyId -ceq $keyId -and
        $trust.purpose -ceq 'production' -and @($trust.keys).Count -eq 1 -and $trust.keys[0].keyId -ceq $keyId -and
        $trust.keys[0].publicKeyPem.Contains('BEGIN PUBLIC KEY') -and
        (Get-Content -LiteralPath $public -Raw) -notmatch 'PRIVATE KEY|privateKey|ciphertext' -and
        (Get-Item -LiteralPath $privateFile).Length -gt 0)

    $phase = 'repeat_init'
    $extraPublic = Join-Path $fixture 'must-not-create-on-repeat.json'
    $repeated = Invoke-FixtureTool @('init-production','--private-directory',$privateDirectory,'--public-output',$extraPublic,'--key-id','different-fixture-key') 2
    Assert-Case 'existing_signing_identity_is_not_replaced' ($repeated.ErrorCode -ceq 'UPDATE_SIGNER_DIRECTORY_EXISTS' -and
        -not (Test-Path -LiteralPath $extraPublic) -and (Hash-File $privateFile) -ceq $protectedHash -and (Hash-File $public) -ceq $publicHash)

    $phase = 'export_public'
    $export = Join-Path $fixture 'owned-exported-public.json'
    $exported = (Invoke-FixtureTool @('export-production-public','--private-directory',$privateDirectory,'--public-output',$export)).Text | ConvertFrom-Json
    Assert-Case 'public_export_preserves_the_same_signing_identity' ($exported.KeyId -ceq $keyId -and (Hash-File $export) -ceq $publicHash -and (Hash-File $privateFile) -ceq $protectedHash)
    $repeated = Invoke-FixtureTool @('export-production-public','--private-directory',$privateDirectory,'--public-output',$export) 2
    Assert-Case 'repeated_public_export_refuses_overwrite_and_preserves_bytes' ($repeated.ErrorCode -ceq 'UPDATE_SIGNER_PUBLIC_EXISTS' -and
        (Hash-File $export) -ceq $publicHash -and (Hash-File $privateFile) -ceq $protectedHash)

    $phase = 'prepare_unbound_draft'
    $draftDirectory = Join-Path $fixture 'unbound-draft'
    $prepared = (Invoke-FixtureTool @('prepare-draft','--source',$source,'--output',$draftDirectory,'--version','2.0.0','--build-id','owned-update-fixture',
        '--channel','plus','--sequence','1','--key-id',$keyId)).Text | ConvertFrom-Json
    $draftPath = Join-Path $draftDirectory 'autumn.update.draft.json'
    $draft = Get-Content -LiteralPath $draftPath -Raw | ConvertFrom-Json
    $payload = [string]$prepared.payload
    $payloadHash = Hash-File $payload
    $draftHash = Hash-File $draftPath
    Assert-Case 'draft_has_zero_remote_identities_and_real_inert_payload_hash' ($prepared.release_binding -ceq 'not_assigned' -and
        $draft.payload.releaseId -eq 0 -and $draft.payload.assetId -eq 0 -and @($draft.files).Count -eq 3 -and
        $draft.payload.sha256 -ceq $payloadHash -and $draft.payload.bytes -eq (Get-Item -LiteralPath $payload).Length)

    $phase = 'reject_unbound_signing'
    $unboundSignature = Join-Path $fixture 'must-not-sign-unbound.sig'
    $rejected = Invoke-FixtureTool @('sign-production','--manifest',$draftPath,'--private-directory',$privateDirectory,'--trust',$public,'--output',$unboundSignature) 2
    Assert-Case 'unbound_draft_cannot_be_signed_as_installable_metadata' ($rejected.ErrorCode -ceq 'UPDATE_PAYLOAD_INVALID' -and
        -not (Test-Path -LiteralPath $unboundSignature) -and (Hash-File $draftPath) -ceq $draftHash)

    $phase = 'bind_synthetic_release'
    $manifest = Join-Path $fixture 'autumn.update.json'
    $bound = (Invoke-FixtureTool @('bind-release','--manifest',$draftPath,'--payload',$payload,'--output',$manifest,
        '--release-id','910001','--asset-id','920001','--release-tag','plus-v2.0.0')).Text | ConvertFrom-Json
    $binding = Get-Content -LiteralPath $manifest -Raw | ConvertFrom-Json
    Assert-Case 'explicit_synthetic_release_binding_keeps_frozen_payload_bytes' ($bound.ReleaseId -eq 910001 -and $bound.AssetId -eq 920001 -and
        $bound.ReleaseTag -ceq 'plus-v2.0.0' -and $binding.payload.releaseId -eq 910001 -and $binding.payload.assetId -eq 920001 -and
        (Hash-File $payload) -ceq $payloadHash -and (Hash-File $draftPath) -ceq $draftHash)

    $phase = 'sign_and_verify_owned_fixture'
    $signature = Join-Path $fixture 'autumn.update.sig'
    $null = Invoke-FixtureTool @('sign-production','--manifest',$manifest,'--private-directory',$privateDirectory,'--trust',$public,'--output',$signature)
    $verified = (Invoke-FixtureTool @('verify','--manifest',$manifest,'--signature',$signature,'--trust',$public,'--payload',$payload)).Text | ConvertFrom-Json
    Assert-Case 'production_purpose_owned_key_signs_and_verifies_bound_fixture' ($verified.status -ceq 'passed' -and $verified.trust_purpose -ceq 'production' -and
        $verified.Version -ceq '2.0.0' -and $verified.ReleaseId -eq 910001 -and $verified.AssetId -eq 920001 -and (Hash-File $privateFile) -ceq $protectedHash)

    $phase = 'reject_unknown_signer_trust'
    $unknownTrust = Get-Content -LiteralPath $public -Raw | ConvertFrom-Json
    $unknownTrust.keys[0].keyId = 'other-owned-fixture-key'
    $unknownPath = Join-Path $fixture 'unknown-trust.json'
    Write-Json $unknownPath $unknownTrust
    $wrongSignature = Join-Path $fixture 'must-not-sign-unknown-trust.sig'
    $rejected = Invoke-FixtureTool @('sign-production','--manifest',$manifest,'--private-directory',$privateDirectory,'--trust',$unknownPath,'--output',$wrongSignature) 2
    Assert-Case 'signing_requires_the_exact_expected_public_trust' ($rejected.ErrorCode -ceq 'UPDATE_SIGNER_TRUST_MISMATCH' -and -not (Test-Path -LiteralPath $wrongSignature))

    $phase = 'reject_unknown_verification_root'
    $rejected = Invoke-FixtureTool @('verify','--manifest',$manifest,'--signature',$signature,'--trust',$unknownPath,'--payload',$payload) 2
    Assert-Case 'verification_rejects_an_unknown_signing_key' ($rejected.ErrorCode -ceq 'UPDATE_UNKNOWN_KEY')

    $phase = 'reject_modified_payload'
    $changedPayload = Join-Path $fixture 'modified-payload.zip'
    $payloadBytes = [IO.File]::ReadAllBytes($payload)
    $payloadBytes[$payloadBytes.Length-1] = $payloadBytes[$payloadBytes.Length-1] -bxor 1
    [IO.File]::WriteAllBytes($changedPayload,$payloadBytes)
    $rejected = Invoke-FixtureTool @('verify','--manifest',$manifest,'--signature',$signature,'--trust',$public,'--payload',$changedPayload) 2
    Assert-Case 'verification_rejects_same_length_payload_tampering' ($rejected.ErrorCode -ceq 'UPDATE_PAYLOAD_HASH_MISMATCH' -and (Hash-File $payload) -ceq $payloadHash)

    $phase = 'reject_modified_manifest'
    # Preserve the signed UTC strings and every other byte. PowerShell's JSON
    # round-trip can normalize ISO dates into the machine's local offset, which
    # would fail metadata validation before reaching the intended signature test.
    $manifestText = [IO.File]::ReadAllText($manifest)
    $originalBuildField = '"buildId": "owned-update-fixture"'
    if ([regex]::Matches($manifestText, [regex]::Escape($originalBuildField)).Count -ne 1) {
        throw 'Owned fixture must contain exactly one expected build identity.'
    }
    $changedManifestPath = Join-Path $fixture 'modified-manifest.json'
    [IO.File]::WriteAllText($changedManifestPath,
        $manifestText.Replace($originalBuildField, '"buildId": "changed-owned-update-fixture"'), [Text.UTF8Encoding]::new($false))
    $rejected = Invoke-FixtureTool @('verify','--manifest',$changedManifestPath,'--signature',$signature,'--trust',$public,'--payload',$payload) 2
    Assert-Case 'verification_rejects_modified_signed_manifest_bytes' ($rejected.ErrorCode -ceq 'UPDATE_SIGNATURE_INVALID')

    $phase = 'prepared_file_table'
    $expectedFiles = @($binding.files)
    function Copy-OwnedFileTable {
        @($expectedFiles | ForEach-Object { [pscustomobject]@{path=$_.path;bytes=$_.bytes;sha256=$_.sha256} })
    }
    $accepted = $true
    try { Assert-UpdatePreparationFileTable -ExpectedFiles $expectedFiles -ActualFiles @(Copy-OwnedFileTable | Sort-Object path -Descending) }
    catch { $accepted = $false }
    Assert-Case 'prepared_file_table_accepts_exact_owned_files_in_different_order' $accepted

    $extraFiles = @(Copy-OwnedFileTable) + @([pscustomobject]@{path='injected-owned-fixture.dll';bytes=1;sha256=('f'*64)})
    $rejectedTable = $false
    try { Assert-UpdatePreparationFileTable -ExpectedFiles $expectedFiles -ActualFiles $extraFiles }
    catch { $rejectedTable = $true }
    Assert-Case 'prepared_file_table_rejects_an_extra_DLL' $rejectedTable

    $missingFiles = @(Copy-OwnedFileTable | Select-Object -Skip 1)
    $rejectedTable = $false
    try { Assert-UpdatePreparationFileTable -ExpectedFiles $expectedFiles -ActualFiles $missingFiles }
    catch { $rejectedTable = $true }
    Assert-Case 'prepared_file_table_rejects_a_missing_file' $rejectedTable

    $changedFiles = @(Copy-OwnedFileTable)
    $changedFiles[0].sha256 = '0'*64
    $rejectedTable = $false
    try { Assert-UpdatePreparationFileTable -ExpectedFiles $expectedFiles -ActualFiles $changedFiles }
    catch { $rejectedTable = $true }
    Assert-Case 'prepared_file_table_rejects_a_hash_change_with_unchanged_length' ($rejectedTable -and $changedFiles[0].bytes -eq $expectedFiles[0].bytes)

    $duplicateFiles = @(Copy-OwnedFileTable)
    $duplicateFiles[1].path = $duplicateFiles[0].path.ToUpperInvariant()
    $duplicateFiles[1].bytes = $duplicateFiles[0].bytes
    $duplicateFiles[1].sha256 = $duplicateFiles[0].sha256
    $rejectedTable = $false
    try { Assert-UpdatePreparationFileTable -ExpectedFiles $expectedFiles -ActualFiles $duplicateFiles }
    catch { $rejectedTable = $true }
    Assert-Case 'prepared_file_table_rejects_case_folded_duplicate_paths' ($rejectedTable -and $duplicateFiles.Count -eq $expectedFiles.Count)

    $phase = 'final_binary_identity'
    foreach ($binary in $assemblies) {
        if ((Hash-File (Join-Path $ProjectRoot $binary.path)) -cne $binary.sha256) { throw 'Update tool bytes changed during tests.' }
    }
    $status = 'passed'
} catch {
    $failure = $_.Exception.GetType().Name + ' during ' + $phase
    if ($phase -ne 'preconditions') { $status = 'failed' }
    throw
} finally {
    if ($temporaryCreated) {
        try {
            # Delete only the GUID directory allocated above after validating the
            # exact parent, name and every descendant. No caller key path is accepted.
            if ([IO.Path]::GetDirectoryName($temporaryRoot) -cne $temporaryParent -or
                [IO.Path]::GetFileName($temporaryRoot) -cne $temporaryName -or
                $temporaryName -notmatch '^AutumnOS-update-tools-test-[a-f0-9]{32}$') { throw 'Temporary ownership boundary changed.' }
            Assert-PlainPath $temporaryRoot
            foreach ($item in Get-ChildItem -LiteralPath $temporaryRoot -Force -Recurse) {
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -or
                    -not $item.FullName.StartsWith($temporaryRoot+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Temporary ownership tree redirected; preserve it for review.'
                }
            }
            Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
            $cleanup = 'owned_temporary_DPAPI_fixture_removed'
        } catch {
            $cleanup = 'failed_owned_temporary_fixture_retained'
            $status = 'failed'
            $checks.Add([ordered]@{name='owned_temporary_key_cleanup';status='failed'})
        }
    }
    [ordered]@{
        schema_version=1;build_id=$BuildId;source_snapshot_id=$SourceSnapshotId;status=$status;phase=$phase;failure=$failure
        started_utc=$started.ToString('o');finished_utc=[DateTimeOffset]::UtcNow.ToString('o')
        kind='real_local_update_CLI_synthetic_inert_payload_and_owned_DPAPI_key'
        total=$checks.Count;failed=@($checks|Where-Object status -eq 'failed').Count;results=$checks;commands=$commands
        tool_assemblies=$assemblies;fixture_directory=$fixture;private_key_material_recorded=$false;private_fixture_cleanup=$cleanup
        identifiers='910001/920001 are synthetic test identities, never contacted or published'
        live_github='not_run';GUI='not_run';Windows_Authenticode='not_run';real_production_key_used=$false;uploaded=$false
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $ReportPath -Encoding utf8NoBOM
    Pop-Location
}
if ($status -ne 'passed') { throw 'Production update tools tests failed; inspect the preserved report.' }
