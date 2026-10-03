[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination,[Parameter(Mandatory)][string]$EvidenceDirectory,[Parameter(Mandatory)]$Build)
. "$PSScriptRoot/Common.ps1"
$Destination=[IO.Path]::GetFullPath($Destination)
if(Test-Path -LiteralPath $Destination){throw 'Developer kit destination already exists; preserve previous output.'}
New-Item -ItemType Directory -Path $Destination|Out-Null
$properties=@($Build.build_properties)+@("-p:BuildId=$($Build.build_id)","-p:SourceSnapshotId=$($Build.source_snapshot_id)")
function Copy-KitSource([string]$Source,[string]$Target){
    $sourceRoot=[IO.Path]::GetFullPath($Source)
    for($cursor=$sourceRoot;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Redirected developer source root or ancestor forbidden.'}
    }
    foreach($directory in Get-ChildItem -LiteralPath $sourceRoot -Directory -Recurse -Force){
        $relative=[IO.Path]::GetRelativePath($sourceRoot,$directory.FullName)
        if($relative -match '(^|[\\/])(bin|obj|AutumnOS_Data|node_modules|artifacts|\.tools)([\\/]|$)'){continue}
        if($directory.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Redirected developer source directory forbidden.'}
    }
    foreach($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File){
        $relative=[IO.Path]::GetRelativePath($sourceRoot,$file.FullName)
        if($relative -match '(^|[\\/])(bin|obj|AutumnOS_Data|node_modules|artifacts|\.tools)([\\/]|$)'){continue}
        if($file.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse point forbidden in developer kit.'}
        if($file.Extension -in @('.pem','.key','.pk8','.pfx','.p12','.dpapi','.snk','.log','.binlog') -or $file.Name -match '^(\.env|secrets\.json|credentials\.json)'){throw 'Sensitive source forbidden in developer kit.'}
        $out=Join-Path $Target $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $out)|Out-Null
        Copy-Item -LiteralPath $file.FullName -Destination $out
    }
}
Push-Location $ProjectRoot
try{
    Invoke-Dotnet -Arguments (@('publish','tools/AutumnOS.Developer.Cli/AutumnOS.Developer.Cli.csproj','-c','Release','-p:Platform=x64','--no-build','--no-restore','-o',$Destination)+$properties) -LogPath (Join-Path $EvidenceDirectory 'publish-developer-cli.log')
    Copy-KitSource 'sdk' (Join-Path $Destination 'SDK')
    Copy-KitSource 'schemas' (Join-Path $Destination 'Schemas')
    foreach($template in @('hello-app','identity-app','save-game','desktop-extension')){
        Copy-KitSource "samples/$template" (Join-Path $Destination "Templates/$template")
    }
    Copy-KitSource 'samples/template-shared' (Join-Path $Destination 'Templates/_shared')
    Copy-KitSource 'docs/versions/0.5.1' (Join-Path $Destination 'Docs/0.5.1')
    Copy-KitSource 'tools/contract-checks' (Join-Path $Destination 'Tools/contract-checks')
    Copy-KitSource 'tests/contracts' (Join-Path $Destination 'Tests/contracts')
    foreach($sample in @(@{name='server';project='samples/server-identity/AutumnOS.ServerIdentity.Sample.csproj'},@{name='client';project='samples/native-identity-client/AutumnOS.NativeIdentity.Sample.csproj'})){
        Invoke-Dotnet -Arguments (@('publish',$sample.project,'-c','Release','-p:Platform=x64','--no-build','--no-restore','-o',(Join-Path $Destination "BackendIdentity/$($sample.name)"))+$properties) -LogPath (Join-Path $EvidenceDirectory "publish-backend-$($sample.name).log")
    }
    Copy-Item -LiteralPath 'samples/server-identity/openapi.json','samples/server-identity/README.md' -Destination (Join-Path $Destination 'BackendIdentity/server')
    Copy-Item -LiteralPath 'samples/native-identity-client/config.schema.json','samples/native-identity-client/README.md' -Destination (Join-Path $Destination 'BackendIdentity/client')
    $sources=Join-Path $Destination 'BackendIdentity/Sources'
    foreach($directory in @('samples/server-identity','samples/native-identity-client','src/AutumnOS.Identity','src/AutumnOS.Contracts')){Copy-KitSource $directory (Join-Path $sources $directory)}
    foreach($relative in @('Directory.Build.props','Directory.Packages.props','NuGet.Config','global.json','build/Brand.props','autumnos-spec/config/logto.public.json','autumnos-spec/config/logto.registration-status.json')){
        $out=Join-Path $sources $relative;New-Item -ItemType Directory -Force -Path (Split-Path $out)|Out-Null;Copy-Item -LiteralPath $relative -Destination $out
    }
    Copy-KitSource 'samples/backend-identity' (Join-Path $Destination 'BackendIdentity')
    foreach($required in @('AutumnOS.Developer.Cli.exe','coreclr.dll','SDK/autumn-sdk.js','SDK/autumn-sdk.d.ts','Templates/_shared/sample-ui.js',
        'Docs/0.5.1/README.md','BackendIdentity/server/AutumnOS.ServerIdentity.Sample.exe','BackendIdentity/server/coreclr.dll','BackendIdentity/client/AutumnOS.NativeIdentity.Sample.exe','BackendIdentity/client/coreclr.dll')){
        if(-not(Test-Path -LiteralPath (Join-Path $Destination $required) -PathType Leaf)){throw "Incomplete developer kit: $required"}
    }
    [ordered]@{schemaVersion=1;productVersion=$Build.version;buildId=$Build.build_id;sourceSnapshotId=$Build.source_snapshot_id;sdkVersion='0.3.0';protocol=1;
        selfContainedCli=$true;backendRealLogto='not_run_registration_required';externalDeveloperAcceptance='not_run';producer='派蒙'}|
        ConvertTo-Json -Depth 4|Set-Content -LiteralPath (Join-Path $Destination 'kit.json') -Encoding utf8NoBOM
}finally{Pop-Location}
