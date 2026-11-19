# All REST paths and upload destinations are derived locally. Authentication
# stays inside the official GitHub CLI. Anonymous reads disable redirects.
$DistributionRepo = 'paimeng5201314/autumnOS'
$DistributionRepoId = 1387171708L
$RepoApi = "repos/$DistributionRepo"
$ReleaseApi = "https://api.github.com/$RepoApi/releases"
$ReleaseWeb = "https://github.com/$DistributionRepo/releases"
$FixedReleaseId = 422539615L
$FixedTag = 'plus-v1.0.0-20261115'
$FixedTitle = 'autumn正式版v1.0.0'
$FixedCommit = 'e9abaa5d141e4b52f95d971cb08955e8910e5720'
$FixedAssets = @('AutumnOS.exe','AutumnOS-1.0.0-win-x64-payload.zip','autumn.update.json','autumn.update.sig')

function Get-OriginalAssetSpecs {
    [pscustomobject]@{name='AutumnOS.exe';id=763938577L;bytes=504265629L;sha='f76a6cc05696aee693d94b1ba69dfba6c21f0bf83504b4c3d4be6952e59d84f9'}
    [pscustomobject]@{name='AutumnOS-1.0.0-win-x64-payload.zip';id=764006099L;bytes=588527296L;sha='92dd24723500348e78141793b2b25227fb3ef82b02a8093d1aacc4c319e5263f'}
}
function Assert-OriginalBackups([string]$Directory) {
    $Directory=Get-PublishPath $Directory
    $receipt=[IO.File]::ReadAllText((Get-PublishPath (Join-Path $Directory 'originals.json'))) | ConvertFrom-Json
    if ($receipt.release_id -ne $FixedReleaseId -or $receipt.repository_id -ne $DistributionRepoId -or $receipt.tag -cne $FixedTag -or @($receipt.assets).Count -ne 2) {throw 'Original backup receipt differs.'}
    foreach ($spec in (Get-OriginalAssetSpecs)) {
        $row=@($receipt.assets | Where-Object name -CEQ $spec.name)
        $path=Get-PublishPath (Join-Path $Directory $spec.name)
        $item=Get-Item -LiteralPath $path
        if ($row.Count -ne 1 -or $row[0].id -ne $spec.id -or $row[0].path -ine $path -or $row[0].bytes -ne $spec.bytes -or
            $row[0].sha -cne $spec.sha -or $item.Length -ne $spec.bytes -or -not $item.IsReadOnly -or (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $spec.sha) {throw 'Original backup bytes differ.'}
    }
}

function Get-PublishPath([string]$Path) {
    if (-not [IO.Path]::IsPathFullyQualified($Path)) { throw 'An absolute local path is required.' }
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -cnotmatch '^[A-Za-z]:\\' -or $full.Substring(2).Contains(':')) { throw 'Use a local drive path.' }
    for ($cursor=$full; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
        if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Redirected publish input.' }
    }
    return $full
}
function Get-FrozenReleaseExecutable([string]$Workspace,[string]$BuildId,$Record,[string]$Version,[string]$ReleaseTag,[string]$ExplicitPath) {
    $selected=if ($ExplicitPath) {Get-PublishPath $ExplicitPath} else {Get-PublishPath (Join-Path $Workspace "artifacts/single-file/$BuildId/AutumnOS.exe")}
    $recorded=Get-PublishPath $Record.executable
    if ([IO.Path]::GetFileName($selected) -cne 'AutumnOS.exe' -or $recorded -ine $selected -or $Record.build_id -cne $BuildId -or
        $Record.version -cne $Version -or $Record.release_label -cne $ReleaseTag -or $Record.delivery_files -ne 1 -or
        $Record.bytes -ne (Get-Item -LiteralPath $selected).Length -or $Record.executable_sha256 -cne (Get-FileHash -LiteralPath $selected -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'The full single-file EXE differs from its production build record.'
    }
    return $selected
}
function Invoke-GitHubCli([string[]]$CliArguments) {
    $candidate=if ($GhPath) { $GhPath } else { (Get-Command gh.exe -CommandType Application | Select-Object -First 1).Source }
    $exe=Get-PublishPath $candidate
    if ([IO.Path]::GetFileName($exe) -ine 'gh.exe') { throw 'Select the official GitHub CLI executable gh.exe.' }
    $processInfo = [Diagnostics.ProcessStartInfo]::new($exe)
    $processInfo.UseShellExecute = $false
    $processInfo.RedirectStandardOutput = $true
    $processInfo.RedirectStandardError = $true
    $processInfo.StandardOutputEncoding = [Text.UTF8Encoding]::new($false)
    $processInfo.CreateNoWindow = $true
    $processInfo.Environment['GH_HOST'] = 'github.com'
    $processInfo.Environment['GH_PROMPT_DISABLED'] = '1'
    $processInfo.Environment['GH_NO_UPDATE_NOTIFIER'] = '1'
    $null=$processInfo.Environment.Remove('GH_DEBUG')
    foreach ($argument in $CliArguments) { $processInfo.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::Start($processInfo)
    try {
        $out=$process.StandardOutput.ReadToEndAsync(); $err=$process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(1800000)) { $process.Kill($true); throw 'GitHub CLI timed out.' }
        $null=$err.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "GitHub CLI failed with code $($process.ExitCode). The release can be checked and resumed." }
        $text=$out.GetAwaiter().GetResult()
        if ($text.Length -gt 16MB) { throw 'Oversized GitHub CLI response.' }
        if ($text.Trim()) { return ($text | ConvertFrom-Json -DateKind String -NoEnumerate) }
    } finally { $process.Dispose() }
}
function Invoke-PublishApi([string]$Method,[string]$Path,[string[]]$Fields=@()) {
    if ($Path -cne $RepoApi -and -not $Path.StartsWith("$RepoApi/",[StringComparison]::Ordinal) -and $Path -cne 'user') { throw 'Unknown API destination.' }
    if ($Method -cne 'GET' -and ($Method -cnotin @('PATCH','DELETE') -or $Path -cnotmatch ('^'+[Regex]::Escape($RepoApi)+'/releases/assets/[1-9][0-9]*$'))) {
        throw 'Only existing release attachments can be modified.'
    }
    $arguments=@('api','--hostname','github.com','--method',$Method,'-H','Accept: application/vnd.github+json','-H','Cache-Control: no-cache',$Path)
    Invoke-GitHubCli ($arguments+$Fields)
}
function Get-AnonymousJson([string]$Path) {
    if ($Path -cne $RepoApi -and -not $Path.StartsWith("$RepoApi/",[StringComparison]::Ordinal)) { throw 'Unknown anonymous API destination.' }
    $handler=[Net.Http.HttpClientHandler]::new()
    $handler.AllowAutoRedirect=$false; $handler.UseCookies=$false; $handler.UseDefaultCredentials=$false
    $http=[Net.Http.HttpClient]::new($handler)
    try {
        $http.Timeout=[TimeSpan]::FromSeconds(45); $http.MaxResponseContentBufferSize=8MB
        $http.DefaultRequestHeaders.UserAgent.ParseAdd('AutumnOS-Distribution-Verification/1.0')
        $http.DefaultRequestHeaders.Accept.ParseAdd('application/vnd.github+json')
        $http.DefaultRequestHeaders.Add('Cache-Control','no-cache')
        $body=$http.GetStringAsync("https://api.github.com/$Path").GetAwaiter().GetResult()
        return ($body | ConvertFrom-Json -DateKind String -NoEnumerate)
    } finally { $http.Dispose() }
}
function Assert-RepositoryIdentity([bool]$Anonymous=$false) {
    $copies=@(Get-AnonymousJson $RepoApi)
    if (-not $Anonymous) { $copies+=@(Invoke-PublishApi GET $RepoApi) }
    foreach ($actual in $copies) {
        if ($actual.id -ne $DistributionRepoId -or $actual.full_name -cne $DistributionRepo -or
            $actual.url -cne "https://api.github.com/$RepoApi" -or $actual.private -isnot [bool] -or $actual.private) {
            throw 'The canonical public repository name, ID, or visibility differs.'
        }
    }
    if (-not $Anonymous) {
        $actor=Invoke-PublishApi GET 'user'
        if ($actor.login -cne 'paimeng5201314') { throw 'Unexpected authenticated GitHub account.' }
    }
}
function Assert-DistributionTag {
    $ref=Get-AnonymousJson "$RepoApi/git/ref/tags/$FixedTag"
    if ($ref.ref -cne "refs/tags/$FixedTag" -or $ref.object.type -cne 'commit' -or $ref.object.sha -cne $FixedCommit) { throw 'Original tag or commit differs.' }
    $r=Get-AnonymousJson "$RepoApi/releases/tags/$FixedTag"
    Assert-FixedRelease $r
}
function Assert-FixedRelease($Release) {
    if ($Release.id -ne $FixedReleaseId -or $Release.url -cne "$ReleaseApi/$FixedReleaseId" -or $Release.html_url -cne "$ReleaseWeb/tag/$FixedTag" -or
        $Release.tag_name -cne $FixedTag -or $Release.name -cne $FixedTitle -or $Release.draft -isnot [bool] -or $Release.draft -or
        $Release.prerelease -isnot [bool] -or $Release.prerelease -or -not $Release.published_at) { throw 'Original published 1.0.0 release identity differs.' }
}
function Get-ExactRelease([long]$Id,[bool]$Anonymous=$false) {
    if ($Id -ne $FixedReleaseId) { throw 'Invalid release ID.' }
    $path="$RepoApi/releases/$Id"
    $r=if ($Anonymous) {Get-AnonymousJson $path} else {Invoke-PublishApi GET $path}
    Assert-FixedRelease $r
    $assets=[Collections.Generic.List[object]]::new()
    for ($page=1;;$page++) {
        $assetPath="$path/assets?per_page=100&page=$page"
        $batch=if ($Anonymous){Get-AnonymousJson $assetPath}else{Invoke-PublishApi GET $assetPath}
        foreach ($asset in $batch) { if ($null -ne $asset) { $assets.Add($asset) } }
        if (@($batch | Where-Object { $null -ne $_ }).Count -lt 100) { break }
    }
    $r.assets=$assets.ToArray()
    return $r
}
function Assert-AssetBytes($Asset,$Local,[string]$Tag,[bool]$Public=$false) {
    if ($Asset.id -isnot [long] -or $Asset.id -lt 1 -or $Asset.name -cne $Local.name -or
        $Asset.url -cne "$ReleaseApi/assets/$($Asset.id)" -or $Asset.state -cne 'uploaded' -or
        $Asset.size -ne $Local.bytes -or $Asset.digest -cne ('sha256:'+$Local.sha)) { throw "Asset identity or bytes differ for $($Local.name)." }
    if ($Public -and $Asset.browser_download_url -cne "$ReleaseWeb/download/$([Uri]::EscapeDataString($Tag))/$([Uri]::EscapeDataString($Local.name))") {
        throw 'Noncanonical public download URL.'
    }
}
function Get-VerifiedAsset($Release,$Local,[bool]$Anonymous=$false) {
    $matches=@($Release.assets | Where-Object name -IEQ $Local.name)
    if ($matches.Count -ne 1) { throw "Expected exactly one asset $($Local.name)." }
    Assert-AssetBytes $matches[0] $Local $Release.tag_name $Anonymous
    $path="$RepoApi/releases/assets/$($matches[0].id)"
    $direct=if ($Anonymous){Get-AnonymousJson $path}else{Invoke-PublishApi GET $path}
    Assert-AssetBytes $direct $Local $Release.tag_name $Anonymous
    if ($direct.id -ne $matches[0].id) { throw 'Asset ID changed.' }
    return $direct
}
function Add-ReleaseFile([long]$ReleaseId,$Local) {
    if ($ReleaseId -ne $FixedReleaseId -or $Local.name -cnotin @($FixedAssets | ForEach-Object {"pending-"+$Local.sha.Substring(0,16)+"-$_"})) {
        throw 'Only one of the four content-bound attachments can be uploaded to the original release.'
    }
    $name=[Uri]::EscapeDataString($Local.name)
    $uri="https://uploads.github.com/repos/$DistributionRepo/releases/$ReleaseId/assets?name=$name"
    $type=if ($Local.name.EndsWith('.zip')){'application/zip'}elseif($Local.name.EndsWith('.json')){'application/json'}else{'application/octet-stream'}
    $created=Invoke-GitHubCli @('api','--hostname','github.com','--method','POST','-H',("Content-Type: $type"),'-H',("Content-Length: "+$Local.bytes),'--input',$Local.path,$uri)
    Assert-AssetBytes $created $Local '' $false
    return $created
}
