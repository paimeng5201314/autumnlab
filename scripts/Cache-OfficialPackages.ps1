[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/Common.ps1"
$sdkExecutable = $Dotnet
$feed = Join-Path $projectRoot '.tools/nuget-feed'
New-Item -ItemType Directory -Force -Path $feed | Out-Null
# Transport fallback only. Use the delivered resolved lock. TLS stays enabled.
$lock = Get-Content -LiteralPath (Join-Path $projectRoot 'src/AutumnOS.Shell/packages.lock.json') -Raw | ConvertFrom-Json -AsHashtable
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$packages = foreach ($framework in $lock.dependencies.Values) {
    foreach ($entry in $framework.GetEnumerator()) {
        if ($entry.Value.type -eq 'Project') { continue }
        $id=$entry.Key.ToLowerInvariant(); $version=$entry.Value.resolved
        if ($seen.Add("$id/$version")) { [pscustomobject]@{id=$id;version=$version;feed=$feed;hash=$entry.Value.contentHash} }
    }
}
$report = @($packages | ForEach-Object -Parallel {
    $ErrorActionPreference = 'Stop'
    $id=$_.id; $version=$_.version; $target=Join-Path $_.feed "$id.$version.nupkg"
    $url="https://api.nuget.org/v3-flatcontainer/$id/$version/$id.$version.nupkg"
    if (-not (Test-Path -LiteralPath $target)) {
        Write-Host "Official package: $id $version"
        Invoke-WebRequest -Uri $url -OutFile "$target.part" -TimeoutSec 240 -MaximumRetryCount 2 -RetryIntervalSec 2
        $zip=[IO.Compression.ZipFile]::OpenRead("$target.part"); $zip.Dispose()
        Move-Item -LiteralPath "$target.part" -Destination $target
    }
    # Signed NuGet contentHash excludes signature metadata; a raw archive hash differs.
    $verification = & $using:sdkExecutable nuget verify $target --all 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (($verification -join "`n").Contains($_.hash))) {
        throw "Signature or locked NuGet content hash verification failed: $id $version"
    }
    [ordered]@{id=$id;version=$version;source=$url;sha256=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant();lock_hash_matched=$true}
} -ThrottleLimit 4)
$report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $feed 'official-downloads.json') -Encoding utf8
Write-Host "Cached and verified $($report.Count) official packages."
