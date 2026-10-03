[CmdletBinding()]
param()
. "$PSScriptRoot/Common.ps1"
$version='154.0.4258.53'
# Exact x64 fixed-runtime URL read from Microsoft's official download page on 2026-10-02.
$uri='https://msedge.sf.dl.delivery.mp.microsoft.com/filestreamingservice/files/0b89c3a3-0043-4746-b39e-65830da7744d/Microsoft.WebView2.FixedVersionRuntime.154.0.4258.53.x64.cab'
$cache=Join-Path $ProjectRoot ".tools/webview2/$version"
$cab=Join-Path $cache "Microsoft.WebView2.FixedVersionRuntime.$version.x64.cab"
$runtime=Join-Path $cache "Microsoft.WebView2.FixedVersionRuntime.$version.x64"
New-Item -ItemType Directory -Path $cache -Force | Out-Null
if(-not (Test-Path -LiteralPath $cab)) {
    Invoke-WebRequest -Uri $uri -OutFile ($cab+'.download') -TimeoutSec 600
    Move-Item -LiteralPath ($cab+'.download') -Destination $cab
}
if(-not (Test-Path -LiteralPath (Join-Path $runtime 'msedgewebview2.exe'))) {
    & "$env:SystemRoot/System32/expand.exe" $cab '-F:*' $cache | Out-Null
    if($LASTEXITCODE -ne 0){throw 'Fixed Runtime cabinet extraction failed.'}
}
$exe=Join-Path $runtime 'msedgewebview2.exe'
$signature=Get-AuthenticodeSignature -LiteralPath $exe
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation'){throw 'Microsoft Fixed Runtime signature validation failed.'}
if((Get-Item -LiteralPath $exe).VersionInfo.FileVersion -notlike "$version*"){throw 'Fixed Runtime version mismatch.'}
[ordered]@{source=$uri;official_page='https://developer.microsoft.com/en-us/microsoft-edge/webview2/';version=$version;architecture='x64';cab_sha256=(Get-FileHash -LiteralPath $cab).Hash.ToLowerInvariant();microsoft_authenticode=$signature.Status.ToString();runtime=$runtime;global_install=$false} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $cache 'provenance.json') -Encoding utf8
Write-Output $runtime
