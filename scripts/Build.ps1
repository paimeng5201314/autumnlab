[CmdletBinding()]
param([string]$BuildId = ('T06-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6)),
    [string]$BuildVersion, [string]$ReleaseLabel, [string]$ProductionUpdateTrustFile, [string]$TestUpdateTrustFile, [string]$TestUpdateFeedFile, [switch]$UpdateHealthFault)
. "$PSScriptRoot/Common.ps1"
if ($BuildId -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,90}$') { throw 'Invalid BuildId.' }
Push-Location $ProjectRoot
$reportDirectory = Join-Path $ProjectRoot "artifacts/builds/$BuildId"
if (Test-Path -LiteralPath $reportDirectory) { Pop-Location; throw 'BuildId already exists. Use a new ID; previous evidence is immutable.' }
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
$report = [ordered]@{ build_id=$BuildId; task_id=$BuildId.Split('-')[0]; source_snapshot_id=$null; commit='not_applicable'; status='failed'; configuration='Release'; rid='win-x64'; sdk='10.0.401'; environment=[Environment]::OSVersion.VersionString; started_utc=[DateTimeOffset]::UtcNow.ToString('o'); product_tests='not_run'; output_path=$null }
try {
    if($ProductionUpdateTrustFile -and $TestUpdateTrustFile){throw 'Production and test trust roots cannot coexist.'}
    $extraInputs=@($ProductionUpdateTrustFile,$TestUpdateTrustFile,$TestUpdateFeedFile|Where-Object {$_})
    $properties=@()
    if($BuildVersion){$properties+="-p:AutumnVersion=$BuildVersion"}
    if($ReleaseLabel){
        if($ReleaseLabel -notmatch '^[A-Za-z0-9][A-Za-z0-9._+-]{0,63}$'){throw 'Invalid release label.'}
        $properties+="-p:AutumnReleaseLabel=$ReleaseLabel"
    }
    $report.release_label=if($ReleaseLabel){$ReleaseLabel}else{$BuildVersion}
    if($ProductionUpdateTrustFile){$properties+="-p:ProductionUpdateTrustFile=$([IO.Path]::GetFullPath($ProductionUpdateTrustFile))"}
    if($TestUpdateTrustFile){$properties+="-p:TestUpdateTrustFile=$([IO.Path]::GetFullPath($TestUpdateTrustFile))"}
    if($TestUpdateFeedFile){if(-not $TestUpdateTrustFile){throw 'Test feed requires a test trust build.'};$properties+="-p:TestUpdateFeedFile=$([IO.Path]::GetFullPath($TestUpdateFeedFile))"}
    if($UpdateHealthFault){if(-not $TestUpdateTrustFile){throw 'Health fault requires a test trust build.'};$properties+='-p:UpdateHealthFault=true'}
    $report.additional_inputs=$extraInputs
    $report.build_properties=$properties
    $report.test_build=[bool]$TestUpdateTrustFile
    Invoke-Dotnet -Arguments @('restore','AutumnOS.slnx','--locked-mode','-p:Platform=x64','--configfile','NuGet.Config') -LogPath (Join-Path $reportDirectory 'restore.log')
    $snapshot = & "$PSScriptRoot/New-SourceSnapshot.ps1" -OutputDirectory $reportDirectory -AdditionalInputs $extraInputs
    $report.source_snapshot_id = $snapshot
    & "$PSScriptRoot/New-SamplePackage.ps1"
    & "$PSScriptRoot/New-StoreFixturePackages.ps1"
    # A protocol peer can change the compiled version in the same generated-output tree.
    # Rebuild prevents an incremental deps.json from retaining that previous version.
    Invoke-Dotnet -Arguments (@('build','AutumnOS.slnx','-t:Rebuild','-c','Release','-p:Platform=x64','--no-restore',"-p:BuildId=$BuildId","-p:SourceSnapshotId=$snapshot")+$properties) -LogPath (Join-Path $reportDirectory 'build.log')
    $verificationDirectory = Join-Path $reportDirectory 'after-build'
    $after = & "$PSScriptRoot/New-SourceSnapshot.ps1" -OutputDirectory $verificationDirectory -AdditionalInputs $extraInputs
    if ($after -ne $snapshot) { throw 'Source changed during build. Retry with a stable source snapshot.' }
    $report.status = 'passed'
    $report.output_path = 'src/AutumnOS.Shell/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64/AutumnOS.Client.exe'
    $binaryDirectory = Join-Path $ProjectRoot (Split-Path $report.output_path)
    [xml]$brand = Get-Content -LiteralPath 'build/Brand.props' -Raw
    $version=if($BuildVersion){$BuildVersion}else{$brand.Project.PropertyGroup.AutumnVersion.InnerText}
    if(-not $version){$version=[string]$brand.Project.PropertyGroup.AutumnVersion}
    $report.version=$version
    $dependencyManifest = Get-Content -LiteralPath (Join-Path $binaryDirectory 'AutumnOS.Client.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    if (-not $dependencyManifest.libraries.ContainsKey('AutumnOS.Client/' + $version)) {
        throw 'Compiled dependency manifest has a stale application version.'
    }
    # Read metadata without mapping the file: sequential A/B builds must be able to replace it.
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $binaryDirectory 'AutumnOS.Contracts.dll')))
    $metadata = @{}
    $assembly.GetCustomAttributesData() | Where-Object { $_.AttributeType.Name -eq 'AssemblyMetadataAttribute' } | ForEach-Object {
        $metadata[$_.ConstructorArguments[0].Value] = $_.ConstructorArguments[1].Value
    }
    if ($metadata.BuildId -ne $BuildId -or $metadata.SourceSnapshotId -ne $snapshot) {
        $report.status = 'failed'
        throw 'Output binary does not match the requested build ID and source snapshot.'
    }
    Get-ChildItem -LiteralPath (Join-Path $ProjectRoot (Split-Path $report.output_path)) -File -Recurse | Where-Object { $_.FullName -notmatch '[\\/]AutumnOS_Data[\\/]' } | ForEach-Object {
        [ordered]@{ path=[IO.Path]::GetRelativePath($ProjectRoot,$_.FullName).Replace('\','/'); sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportDirectory 'artifact-hashes.json') -Encoding utf8
    $BuildId | Set-Content -LiteralPath (Join-Path $ProjectRoot 'artifacts/latest-build.txt') -Encoding utf8
} catch {
    $report.status = 'failed'
    $report.failure = $_.Exception.Message
    throw
} finally {
    $report.finished_utc = [DateTimeOffset]::UtcNow.ToString('o')
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $reportDirectory 'build-result.json') -Encoding utf8
    Pop-Location
}
