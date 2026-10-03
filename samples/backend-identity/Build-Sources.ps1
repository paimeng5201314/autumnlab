[CmdletBinding()]
param([Parameter(Mandatory)][string]$Destination,[Parameter(Mandatory)][string]$DotnetPath)
$ErrorActionPreference='Stop'
$source=Join-Path $PSScriptRoot 'Sources'
$target=[IO.Path]::GetFullPath($Destination)
$dotnet=(Get-Item -LiteralPath $DotnetPath).FullName
if(-not(Test-Path -LiteralPath $source -PathType Container)){throw 'Run this from the delivered BackendIdentity directory containing Sources.'}
if(Test-Path -LiteralPath $target){throw 'Existing directories are preserved. Choose a new build directory.'}
for($cursor=$target;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
    if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Redirected destination forbidden.'}
}
$sourceRoot=[IO.Path]::GetFullPath($source).TrimEnd('\')+'\'
if(($target+'\').StartsWith($sourceRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Build destination must be outside delivered Sources.'}
New-Item -ItemType Directory -Path $target|Out-Null
foreach($item in Get-ChildItem -LiteralPath $source -Recurse -File){
    if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Redirected source forbidden.'}
    $relative=[IO.Path]::GetRelativePath($source,$item.FullName)
    if($relative -match '(^|[\/])(bin|obj|\.tools|AutumnOS_Data)([\/]|$)' -or $item.Extension -in @('.pk8','.pem','.key','.pfx','.p12','.dpapi','.snk')){throw 'Source distribution contains generated or private material.'}
    $out=Join-Path $target $relative
    New-Item -ItemType Directory -Path (Split-Path $out) -Force|Out-Null
    Copy-Item -LiteralPath $item.FullName -Destination $out
}
New-Item -ItemType Directory -Path (Join-Path $target '.tools/nuget-feed') -Force|Out-Null
$oldPackages=$env:NUGET_PACKAGES;$oldDotnetHome=$env:DOTNET_CLI_HOME
$savedFirstUse=@{}
foreach($name in @('DOTNET_GENERATE_ASPNET_CERTIFICATE','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_NOLOGO')){
    $savedFirstUse[$name]=[Environment]::GetEnvironmentVariable($name,'Process')
}
try{
    $env:NUGET_PACKAGES=Join-Path $target '.tools/nuget';$env:DOTNET_CLI_HOME=Join-Path $target '.tools/dotnet-home'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false';$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH='false'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_NOLOGO='1'
    Push-Location $target
    try{
        $actual=& $dotnet --version
        if($LASTEXITCODE -ne 0 -or $actual.Trim() -ne '10.0.401'){throw 'Exact .NET SDK 10.0.401 required; no SDK is installed by this script.'}
        foreach($project in @('samples/server-identity/AutumnOS.ServerIdentity.Sample.csproj','samples/native-identity-client/AutumnOS.NativeIdentity.Sample.csproj')){
            & $dotnet restore $project --locked-mode -p:Platform=x64
            if($LASTEXITCODE -ne 0){throw 'Locked official restore failed; partial evidence retained.'}
            & $dotnet build $project -c Release -p:Platform=x64 --no-restore
            if($LASTEXITCODE -ne 0){throw 'Isolated backend build failed; partial evidence retained.'}
        }
    }finally{Pop-Location}
}finally{
    $env:NUGET_PACKAGES=$oldPackages;$env:DOTNET_CLI_HOME=$oldDotnetHome
    foreach($name in $savedFirstUse.Keys){
        if($null -eq $savedFirstUse[$name]){Remove-Item -LiteralPath ('Env:\'+$name) -ErrorAction SilentlyContinue}
        else{[Environment]::SetEnvironmentVariable($name,$savedFirstUse[$name],'Process')}
    }
}
Write-Output ('Built isolated developer backend sources: '+$target)
