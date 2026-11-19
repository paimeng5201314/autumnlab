#requires -Version 7.6
# Runs the publication state machine with a local in-memory release; no network or production keys.
Set-StrictMode -Version Latest
$ErrorActionPreference='Stop'
$tmp=[IO.Directory]::CreateDirectory((Join-Path ([IO.Path]::GetTempPath()) ('AutumnOS-inplace-mock-'+[guid]::NewGuid().ToString('N')))).FullName
$build='T06-mock-01'
$gate=Join-Path $tmp "artifacts/builds/$build"
[void][IO.Directory]::CreateDirectory($gate)
foreach ($name in 'Publish-SignedUpdate.ps1','GitHubPublishTransport.ps1','PublishReleaseAssets.ps1') {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $tmp $name)
}
$check=[Collections.Generic.List[string]]::new()
function Expect([bool]$Ok,[string]$Label) {if(-not $Ok){throw "Failed mock check: $Label"};$check.Add($Label)}
. (Join-Path $PSScriptRoot 'GitHubPublishTransport.ps1')
$files=@{}
foreach ($name in $FixedAssets) {
    $path=Join-Path $tmp $name
    [IO.File]::WriteAllText($path,'synthetic fixture '+$name)
    $files[$name]=[pscustomobject]@{name=$name;path=$path;bytes=(Get-Item $path).Length;sha=(Get-FileHash $path).Hash.ToLowerInvariant()}
}
$localStub=@'
param($Workspace,$BuildId,$PreviousBuildId,$PreviousSequence,$PreviousVersion,$ReleaseTag,$GhPath,$ExecutablePath,$Mode,$PrivateDirectory,$PayloadAssetId)
if ($badInputs){throw 'unverified input'}
if ($Mode -eq 'Bind') {
    $candidate=@($m.release.assets|Where-Object id -EQ $PayloadAssetId)
    if ($candidate.Count -ne 1 -or $candidate[0].digest -cne ('sha256:'+$files[$FixedAssets[1]].sha)) {throw 'wrong payload to sign'}
    $m.events.Add('verified-signature')
}
[pscustomobject]@{version=$m.version;channel='plus';sequence=9L;build_id=$BuildId;snapshot='fixture';old_v2_rejected=$m.rejection;
    asset_id=$PayloadAssetId;exe=$files['AutumnOS.exe'];payload=$files[$FixedAssets[1]];manifest=$files['autumn.update.json'];signature=$files['autumn.update.sig']}
'@
[IO.File]::WriteAllText((Join-Path $tmp 'Get-TestedReleaseInputs.ps1'),$localStub)
$mockTransport=@'
function Assert-OriginalBackups([string]$Directory) {if(-not $Directory){throw 'backup missing'}}
function Invoke-LocalTransport($Method,$Path,$Fields=@()) {
    if ($Path -ceq $RepoApi){return [pscustomobject]@{id=$DistributionRepoId;full_name=$m.repo;private=$false;url="https://api.github.com/$RepoApi"}}
    if ($Path -ceq 'user'){return [pscustomobject]@{login='paimeng5201314'}}
    if ($Method -cne 'GET' -and $Path -notmatch '/releases/assets/\d+$'){throw 'forbidden release or Git ref mutation'}
    if ($Path -ceq "$RepoApi/git/ref/tags/$FixedTag"){return [pscustomobject]@{ref="refs/tags/$FixedTag";object=[pscustomobject]@{type='commit';sha=$m.commit}}}
    if ($Path -ceq "$RepoApi/releases/tags/$FixedTag" -or $Path -ceq "$RepoApi/releases/$FixedReleaseId"){return $m.release}
    if ($Path -ceq "$RepoApi/releases/$FixedReleaseId/assets?per_page=100&page=1"){return $m.release.assets}
    if ($Path -match '/releases/assets/(\d+)$') {
        $id=[long]$Matches[1]
        $asset=@($m.release.assets|Where-Object id -EQ $id)[0]
        if ($Method -ceq 'PATCH') {
            $name=$Fields[1].Substring(5)
            if ($m.failRename -ceq $name) {$m.failRename='';throw 'simulated interruption'}
            $m.events.Add("rename:$name");$asset.name=$name;$asset.browser_download_url="$ReleaseWeb/download/$FixedTag/$name"
        } elseif ($Method -ceq 'DELETE') {
            if ($asset.state -cne 'starter' -or $asset.size -ne 0) {throw 'cannot delete a real asset in publication path'}
            $m.events.Add('recover-empty');$m.release.assets=@($m.release.assets|Where-Object id -NE $id);return
        }
        return $asset
    }
    throw "Unexpected local transport route $Path"
}
function Get-AnonymousJson([string]$Path) {Invoke-LocalTransport GET $Path}
function Invoke-PublishApi([string]$Method,[string]$Path,[string[]]$Fields=@()) {Invoke-LocalTransport $Method $Path $Fields}
function Invoke-GitHubCli([string[]]$CliArguments) {
    $uri=[uri]$CliArguments[-1]
    if ($uri.Host -cne 'uploads.github.com' -or $uri.AbsolutePath -cne "/repos/$DistributionRepo/releases/$FixedReleaseId/assets") {throw 'wrong upload location'}
    $name=$uri.Query.Substring(6); $path=$CliArguments[[Array]::IndexOf($CliArguments,'--input')+1]
    $id=++$m.next
    $asset=New-Asset $name $id (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() (Get-Item $path).Length
    if ($m.failUpload -and $name.EndsWith($m.failUpload)) {
        $asset.size=0;$asset.digest=$null;$asset.state='starter'
        $m.release.assets+=@($asset);throw 'simulated upstream failure'
    }
    $m.release.assets+=@($asset);$m.events.Add("upload:$name");return $asset
}
'@
[IO.File]::AppendAllText((Join-Path $tmp 'GitHubPublishTransport.ps1'),[Environment]::NewLine+$mockTransport)
[IO.File]::AppendAllText((Join-Path $tmp 'PublishReleaseAssets.ps1'),[Environment]::NewLine+'function Get-PublicFileHash([string]$Url,[long]$Size) { $name=([uri]$Url).Segments[-1]; $m.events.Add("download:$name"); return $files[$name].sha }')
function New-Asset([string]$Name,[long]$Id,[string]$Hash,[long]$Size) {
    [pscustomobject]@{id=$Id;name=$Name;digest="sha256:$Hash";size=$Size;state='uploaded';url="$ReleaseApi/assets/$Id";
        browser_download_url="$ReleaseWeb/download/$FixedTag/$Name";uploader=[pscustomobject]@{login='paimeng5201314'};created_at=[DateTimeOffset]::UtcNow.ToString('o')}
}
function New-MockState {
    $r=[pscustomobject]@{id=$FixedReleaseId;name=$FixedTitle;tag_name=$FixedTag;draft=$false;prerelease=$false;published_at='2026-11-18T12:00:00Z';
        url="$ReleaseApi/$FixedReleaseId";html_url="$ReleaseWeb/tag/$FixedTag";assets=@((New-Asset $FixedAssets[0] 31 ('a'*64) 8),(New-Asset $FixedAssets[1] 32 ('b'*64) 9),(New-Asset 'manual-notes.txt' 33 ('c'*64) 10))}
    @{release=$r;repo=$DistributionRepo;commit=$FixedCommit;version='1.0.0';next=1000L;failRename='';failUpload='';rejection=$true;events=[Collections.Generic.List[string]]::new()}
}
$run=Join-Path $tmp 'Publish-SignedUpdate.ps1'
$common=@{Workspace=$tmp;BuildId=$build;PreviousBuildId='T06-prev';PreviousSequence=7L;PrivateDirectory=$tmp}
$publish=$common.Clone();$publish.BackupDirectory=$tmp
$badInputs=$true;$m=New-MockState
try {& $run @publish;throw 'unexpected success'}catch{Expect ($_.Exception.Message -ceq 'unverified input' -and $m.events.Count -eq 0) 'reject untested inputs before all writes'}
$badInputs=$false
$m.version='abnormal'
try {& $run @publish;throw 'unexpected success'}catch{Expect ($_.Exception.Message -like '*original product version*' -and $m.events.Count -eq 0) 'reject different product version'}
$m=New-MockState;$m.release.name='wrong'
try {& $run @publish;throw 'unexpected success'}catch{Expect ($_.Exception.Message -like '*release identity*' -and $m.events.Count -eq 0) 'reject retitled existing release'}
$m=New-MockState;$m.commit='0'*40
try {& $run @publish;throw 'unexpected success'}catch{Expect ($_.Exception.Message -like '*Original tag*' -and $m.events.Count -eq 0) 'reject changed original commit'}
$m=New-MockState;$m.repo='unrelated/repo'
try {& $run @publish;throw 'unexpected success'}catch{Expect ($_.Exception.Message -like '*canonical public repository*' -and $m.events.Count -eq 0) 'reject another repository'}
$m=New-MockState
$local=& $run @common -CheckLocal -PrepareBrowserFiles
Expect ($local.browser_files.Count -eq 2 -and $m.events.Count -eq 0 -and $local.browser_files[1].upload_name -like 'pending-*-AutumnOS-1.0.0-win-x64-payload.zip') 'local Edge handoff contains exact neutral names'
$payload=$files[$FixedAssets[1]]
$m.release.assets+=@(New-Asset $local.browser_files[1].upload_name 800 $payload.sha $payload.bytes)
try {& $run @publish -PrepareAfterBrowserUpload -BrowserPayloadId 32;throw 'unexpected success'}catch{Expect ($_.Exception.Message -like '*bytes differ*') 'old payload asset ID cannot bind new bytes'}
$edge=& $run @publish -PrepareAfterBrowserUpload -BrowserPayloadId 800
Expect ($edge.asset_id -eq 800 -and $edge.browser_files.Count -eq 2 -and @($m.events|Where-Object {$_ -match '^(upload|rename)'}).Count -eq 0) 'browser staging signs genuine payload ID with zero mutations'
$m.rejection=$false
try {& $run @publish -PrepareAfterBrowserUpload -BrowserPayloadId 800;throw 'unexpected success'}catch{Expect ($_.Exception.Message -like '*signing report*') 'new metadata is held back without old-root rejection'}
$m.rejection=$true;$m.failUpload='autumn.update.sig'
try {& $run @publish;throw 'unexpected success'}catch{Expect ($_.Exception.Message -ceq 'simulated upstream failure' -and $m.release.assets[0].name -ceq 'AutumnOS.exe') 'interrupted upload leaves original files in place'}
$m.failUpload='';$m.failRename='autumn.update.sig'
try {& $run @publish;throw 'unexpected success'}catch{Expect ($_.Exception.Message -like '*original canonical names restored*' -and $m.release.assets[0].name -ceq 'AutumnOS.exe' -and $m.release.assets[1].name -ceq $FixedAssets[1]) 'rename interruption restores two exact original slots'}
$out=& $run @publish
Expect ($out.status -ceq 'published_anonymously_verified' -and $out.release_id -eq $FixedReleaseId -and $out.title -ceq $FixedTitle -and $out.tag -ceq $FixedTag -and $out.assets.Count -eq 4) 'same release, title and tag have four verified replacement targets'
Expect ($m.events.Contains('recover-empty') -and $m.events.IndexOf('rename:autumn.update.sig') -lt $m.events.IndexOf('rename:autumn.update.json')) 'known starter recovered and manifest promoted last'
Expect (@($out.assets|Where-Object { $_.name -ceq $FixedAssets[1] -and $_.id -eq 800 }).Count -eq 1) 'signed staged payload ID preserved after canonical rename'
Expect ($m.release.assets[2].name -ceq 'manual-notes.txt' -and $m.release.assets[2].id -eq 33) 'unrelated asset left intact'
$m.events.Clear()
$browserResult=& $run @publish -VerifyAfterBrowserPublish
Expect ($browserResult.status -ceq 'published_anonymously_verified' -and @($m.events|Where-Object {$_ -like 'download:*'}).Count -eq 4 -and @($m.events|Where-Object {$_ -match '^(rename|upload|recover)'}).Count -eq 0) 'post-Edge verification reads four complete public bodies without authenticated writes'
$m.events.Clear();$again=& $run @publish
Expect ($again.status -ceq 'published_anonymously_verified' -and @($m.events|Where-Object {$_ -match '^(rename|upload|recover)'}).Count -eq 0) 'same revision replay performs only verification'
$m.events.Clear();$same=$publish.Clone();$same.PreviousBuildId=$build
try {& $run @same;throw 'unexpected success'}catch{Expect ($_.Exception.Message -like '*original product version*' -and $m.events.Count -eq 0) 'identical source and target build rejected before modifying release'}
$same=$publish.Clone();$same.PreviousSequence=9
try {& $run @same;throw 'unexpected success'}catch{Expect ($_.Exception.Message -like '*original product version*' -and $m.events.Count -eq 0) 'sequence replay rejected before modifying release'}
$same=$publish.Clone();$same.PreviousSequence=10
try {& $run @same;throw 'unexpected success'}catch{Expect ($_.Exception.Message -like '*original product version*' -and $m.events.Count -eq 0) 'sequence downgrade rejected before modifying release'}
[pscustomobject]@{status='passed';real_github_requests=0;real_keys=0;checks=$check.Count;results=$check.ToArray()}
