[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BuildId,
    [string]$Version = '0.2.2-t02-protocol-peer.1'
)
. "$PSScriptRoot/Common.ps1"
if ($BuildId -notmatch '^[A-Za-z0-9][A-Za-z0-9_.-]{0,90}$' -or $Version -notmatch '^\d+\.\d+\.\d+-[A-Za-z0-9.-]+$') { throw 'Invalid peer build identity.' }
$directory = Join-Path $ProjectRoot "artifacts/protocol-peers/$BuildId"
if (Test-Path -LiteralPath $directory) { throw 'Peer build already exists; preserve evidence with a new ID.' }
New-Item -ItemType Directory -Path $directory | Out-Null
$report = [ordered]@{build_id=$BuildId;version=$Version;kind='local_protocol_test_peer';status='failed';source_snapshot_id=$null;product_tests='not_run';release_candidate=$false}
Push-Location $ProjectRoot
try {
    [xml]$brand = Get-Content -LiteralPath build/Brand.props -Raw
    if ($Version -eq $brand.Project.PropertyGroup.AutumnVersion) { throw 'A distinct compiled version is required.' }
    Invoke-Dotnet -Arguments @('restore','src/AutumnOS.Shell/AutumnOS.Shell.csproj','--locked-mode','-p:Platform=x64','--configfile','NuGet.Config') -LogPath (Join-Path $directory 'restore.log')
    $snapshot = & "$PSScriptRoot/New-SourceSnapshot.ps1" -OutputDirectory $directory
    $report.source_snapshot_id = $snapshot
    & "$PSScriptRoot/New-SamplePackage.ps1"
    # This is a real MSBuild version override, not a rewritten sidecar/assembly.
    Invoke-Dotnet -Arguments @('build','src/AutumnOS.Shell/AutumnOS.Shell.csproj','-t:Rebuild','-c','Release','-p:Platform=x64','--no-restore',
        "-p:AutumnVersion=$Version","-p:BuildId=$BuildId","-p:SourceSnapshotId=$snapshot") -LogPath (Join-Path $directory 'build.log')
    if ((& "$PSScriptRoot/New-SourceSnapshot.ps1" -OutputDirectory (Join-Path $directory 'after-build')) -ne $snapshot) { throw 'Sources changed while building the peer.' }
    $builtRoot = Join-Path $ProjectRoot 'src/AutumnOS.Shell/bin/x64/Release/net10.0-windows10.0.26100.0/win-x64'
    $destination = Join-Path $directory 'distribution'
    New-Item -ItemType Directory -Path $destination | Out-Null
    foreach ($item in Get-ChildItem -LiteralPath $builtRoot) {
        if ($item.Name -ne 'AutumnOS_Data') { Copy-Item -LiteralPath $item.FullName -Destination $destination -Recurse }
    }
    foreach ($required in @('AutumnOS.exe','AutumnOS.pri','App.xbf','MainWindow.xbf','AutumnOS.Launcher.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $destination $required) -PathType Leaf)) { throw "Peer missing required resource: $required" }
    }
    $dependencyManifest = Get-Content -LiteralPath (Join-Path $destination 'AutumnOS.deps.json') -Raw | ConvertFrom-Json -AsHashtable
    if (-not $dependencyManifest.libraries.ContainsKey('AutumnOS/' + $Version)) { throw 'Peer dependency manifest does not match its compiled application version.' }
    $context = [Runtime.Loader.AssemblyLoadContext]::new('protocol-peer-inspection',[bool]$true)
    try {
        $assembly = $context.LoadFromAssemblyPath((Join-Path $destination 'AutumnOS.Contracts.dll'))
        $metadata = @{}
        $assembly.GetCustomAttributesData() | Where-Object {$_.AttributeType.Name -eq 'AssemblyMetadataAttribute'} | ForEach-Object {$metadata[$_.ConstructorArguments[0].Value]=$_.ConstructorArguments[1].Value}
        $compiledVersion = ($assembly.GetCustomAttributesData() | Where-Object {$_.AttributeType.Name -eq 'AssemblyInformationalVersionAttribute'}).ConstructorArguments[0].Value
        if ($metadata.BuildId -ne $BuildId -or $metadata.SourceSnapshotId -ne $snapshot -or $compiledVersion -ne $Version) { throw 'Compiled peer identity does not match requested build/version/source.' }
    } finally { $context.Unload() }
    Get-ChildItem -LiteralPath $destination -Recurse -File | ForEach-Object {
        [ordered]@{path=[IO.Path]::GetRelativePath($destination,$_.FullName).Replace('\','/');sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $directory 'artifact-hashes.json') -Encoding utf8
    $report.status='passed';$report.executable=Join-Path $destination 'AutumnOS.exe'
} catch { $report.failure=$_.Exception.Message; throw }
finally {
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $directory 'peer-build.json') -Encoding utf8
    Pop-Location
}
Write-Host "Protocol-only peer: $($report.executable). Run Build.ps1 again before delivering the main version."
