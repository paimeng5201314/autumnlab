[CmdletBinding()]
param([string]$NodePath)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
if(-not $NodePath){$node=Get-Command node -ErrorAction SilentlyContinue;if(-not $node){throw 'Node.js >=22.9 must be explicitly available; no global component is installed.'};$NodePath=$node.Source}
$NodePath=(Get-Item -LiteralPath $NodePath).FullName
$configuration=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'toolchain.json') -Raw|ConvertFrom-Json
$toolRoot=Join-Path $root '.tools'
$local=Join-Path $toolRoot 'contracts'
$npmRoot=Join-Path $toolRoot ('npm-'+$configuration.npm.version)
$archive=Join-Path $npmRoot 'npm.tgz'
foreach($directory in @($toolRoot,$local,$npmRoot)){
    for($cursor=[IO.Path]::GetFullPath($directory);$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Redirected tooling directories are forbidden.'}
    }
    New-Item -ItemType Directory -Path $directory -Force|Out-Null
}
if(-not(Test-Path -LiteralPath $archive)){Invoke-WebRequest -Uri $configuration.npm.url -OutFile $archive -TimeoutSec 60}
$digest='sha512-'+[Convert]::ToBase64String([Security.Cryptography.SHA512]::HashData([IO.File]::ReadAllBytes($archive)))
if($digest -cne $configuration.npm.integrity){throw 'Official npm package digest mismatch; existing evidence retained.'}
$cli=Join-Path $npmRoot 'package/bin/npm-cli.js'
if(-not(Test-Path -LiteralPath $cli)){
    $entries=& tar.exe -tzf $archive
    if($LASTEXITCODE -ne 0 -or @($entries|Where-Object{$_ -notmatch '^package/' -or $_ -match '(^|/)\.\.(/|$)'}).Count){throw 'npm archive path validation failed.'}
    & tar.exe -xzf $archive -C $npmRoot
    if($LASTEXITCODE -ne 0){throw 'npm local extraction failed.'}
}
foreach($name in @('package.json','package-lock.json')){Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $local $name) -Force}
# npm ci restores exactly package-lock, never runs lifecycle scripts and writes only this local cache.
& $NodePath $cli ci --prefix $local --ignore-scripts --no-audit --no-fund --registry $configuration.registry --cache (Join-Path $toolRoot 'npm-cache')
if($LASTEXITCODE -ne 0){throw 'Locked local contract tool restore failed.'}
& $NodePath (Join-Path $local 'node_modules/typescript/bin/tsc') --version
if($LASTEXITCODE -ne 0){throw 'TypeScript executable check failed.'}
Write-Output ('Local contract tools restored: '+$local)
