function Hold-ReleaseFile($File) {
    $path=Get-PublishPath $File.path
    if ([IO.Path]::GetFileName($path) -cne $File.name -or $File.sha -cnotmatch '^[a-f0-9]{64}$' -or $File.bytes -lt 1) {
        throw 'Invalid local publication file.'
    }
    $stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $held.Add($stream)
    if ($stream.Length -ne $File.bytes -or [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($stream)) -cne $File.sha) {
        throw 'Local publication source changed.'
    }
}
function Get-StageFile($File) {
    if ($File.name -cnotin $FixedAssets -or $File.sha -cnotmatch '^[0-9a-f]{64}$') { throw 'Unknown staged file.' }
    [pscustomobject]@{name=('pending-'+$File.sha.Substring(0,16)+'-'+$File.name);path=$File.path;sha=$File.sha;bytes=$File.bytes}
}
function Get-AssetForBinding($Release,$File,[long]$Id,[bool]$Anonymous) {
    $match=@($Release.assets | Where-Object id -EQ $Id)
    if ($match.Count -ne 1) { throw 'Bound payload asset is missing or ambiguous.' }
    $ref=if ($match[0].name -ceq $File.name) {$File} else {Get-StageFile $File}
    $asset=Get-VerifiedAsset $Release $ref $Anonymous
    if ($asset.id -ne $Id) {throw 'Unexpected staged or canonical payload asset ID.'}
    return $asset
}
function Test-CanonicalAsset($Release,$File) {
    $matches=@($Release.assets | Where-Object name -IEQ $File.name)
    if ($matches.Count -gt 1 -or ($matches.Count -eq 1 -and $matches[0].name -cne $File.name)) { throw 'Ambiguous canonical asset.' }
    if ($matches.Count -eq 0 -or $matches[0].digest -cne ('sha256:'+$File.sha)) { return $false }
    $null=Get-VerifiedAsset $Release $File
    return $true
}
function Ensure-ReleaseFile($Release,$File) {
    Assert-FixedRelease $Release
    $receipt=Join-Path $gate ("publish-upload-$releaseId-"+$File.name+'.json')
    $matches=@($Release.assets | Where-Object name -IEQ $File.name)
    if ($matches.Count) {
        if ($matches.Count -ne 1 -or $matches[0].state -cne 'starter') { return (Get-VerifiedAsset $Release $File) }
        if (-not [IO.File]::Exists($receipt)) { throw 'An unclaimed empty upload already occupies this name.' }
        $intent=[IO.File]::ReadAllText((Get-PublishPath $receipt)) | ConvertFrom-Json -DateKind String
        if ($intent.build_id -cne $BuildId -or $intent.release_id -ne $releaseId -or $intent.name -cne $File.name -or
            $intent.bytes -ne $File.bytes -or $intent.sha256 -cne $File.sha) { throw 'The empty upload does not match the original local upload intent.' }
        $id=$matches[0].id
        $direct=Invoke-PublishApi GET "$RepoApi/releases/assets/$id"
        foreach ($slot in @($matches[0],$direct)) {
            if ($id -isnot [long] -or $id -le 0 -or $slot.id -ne $id -or $slot.url -cne "$ReleaseApi/assets/$id" -or
                $slot.name -cne $File.name -or $slot.state -cne 'starter' -or $slot.size -ne 0 -or $slot.digest -or
                $slot.uploader.login -cne 'paimeng5201314' -or
                [DateTimeOffset]::Parse($slot.created_at) -lt [DateTimeOffset]::Parse($intent.started_utc).AddMinutes(-2)) {
                throw 'The GitHub upload slot has changed or has bytes.'
            }
        }
        $null=Invoke-PublishApi DELETE "$RepoApi/releases/assets/$id"
        $refresh=Get-ExactRelease $releaseId
        Assert-FixedRelease $refresh
        if (@($refresh.assets | Where-Object name -IEQ $File.name).Count) { throw 'The empty upload slot is still occupied.' }
    }
    if (-not [IO.File]::Exists($receipt)) {
        $intent=[ordered]@{build_id=$BuildId;release_id=$releaseId;name=$File.name;bytes=$File.bytes;sha256=$File.sha;started_utc=[DateTimeOffset]::UtcNow.ToString('o')}
        $temporary=Join-Path $gate ('publish-upload-intent-'+[Guid]::NewGuid().ToString('N')+'.tmp')
        [IO.File]::WriteAllText($temporary,($intent|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary,$receipt)
    }
    $added=Add-ReleaseFile $releaseId $File
    $current=Get-VerifiedAsset (Get-ExactRelease $releaseId) $File
    if ($current.id -ne $added.id) { throw 'Uploaded asset changed before readback.' }
    return $current
}
function New-BrowserUploadLink($File) {
    $staged=Get-StageFile $File
    $source=Get-PublishPath $File.path
    $dir=Join-Path ([IO.Path]::GetDirectoryName($source)) 'browser-uploads'
    [void][IO.Directory]::CreateDirectory($dir)
    $dir=Get-PublishPath $dir
    $path=Join-Path $dir $staged.name
    if (-not [IO.File]::Exists($path)) { $null=New-Item -ItemType HardLink -Path $path -Target $source -ErrorAction Stop }
    $path=Get-PublishPath $path
    if ((Get-Item -LiteralPath $path).Length -ne $File.bytes -or (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $File.sha) {throw 'Browser handoff file changed.'}
    [pscustomobject]@{path=$path;upload_name=$staged.name;final_name=$File.name;sha256=$File.sha;bytes=$File.bytes}
}
function Ensure-CandidateFile($Release,$File) {
    if (Test-CanonicalAsset $Release $File) { return (Get-VerifiedAsset $Release $File) }
    return (Ensure-ReleaseFile $Release (Get-StageFile $File))
}
function Get-OriginalSlotJournal($Release,[string]$Gate) {
    $file=Join-Path $Gate 'same-release-slots.json'
    if (-not [IO.File]::Exists($file)) {
        $originals=@()
        $other=@()
        foreach ($asset in $Release.assets) {
            $row=[ordered]@{id=$asset.id;name=$asset.name;sha=$asset.digest;bytes=$asset.size}
            if ($asset.name -cin $FixedAssets) { $originals+=$row }
            elseif ($asset.name -cnotmatch '^pending-[a-f0-9]{16}-(?:AutumnOS\.exe|AutumnOS-1\.0\.0-win-x64-payload\.zip|autumn\.update\.(?:json|sig))$') { $other+=$row }
        }
        $data=[ordered]@{repository=$DistributionRepoId;release=$FixedReleaseId;tag=$FixedTag;build=$BuildId;originals=$originals;other=$other}
        $temporary=Join-Path $Gate ([guid]::NewGuid().ToString('N')+'.tmp')
        [IO.File]::WriteAllText($temporary,($data|ConvertTo-Json -Depth 6))
        [IO.File]::Move($temporary,$file)
    }
    $saved=[IO.File]::ReadAllText((Get-PublishPath $file))|ConvertFrom-Json
    if ($saved.repository -ne $DistributionRepoId -or $saved.release -ne $FixedReleaseId -or $saved.tag -cne $FixedTag -or $saved.build -cne $BuildId) {throw 'Different transaction journal.'}
    Assert-OtherAssets $Release $saved
    return $saved
}
function Assert-OtherAssets($Release,$Journal) {
    foreach ($old in $Journal.other) {
        $found=@($Release.assets|Where-Object id -EQ $old.id)
        if ($found.Count -ne 1 -or $found[0].name -cne $old.name -or $found[0].digest -cne $old.sha -or $found[0].size -ne $old.bytes) {throw 'An unrelated historical attachment changed.'}
    }
}
function Move-AssetName($Asset,[string]$NewName) {
    $updated=Invoke-PublishApi PATCH "$RepoApi/releases/assets/$($Asset.id)" @('-f',("name="+$NewName))
    if ($updated.id -ne $Asset.id -or $updated.digest -cne $Asset.digest -or $updated.size -ne $Asset.size -or $updated.name -cne $NewName) {throw 'Asset identity changed while renaming.'}
}
function Set-FinalAssetName($File,$Journal,[long]$TargetId) {
    $r=Get-ExactRelease $FixedReleaseId
    if (Test-CanonicalAsset $r $File) {
        if ((Get-VerifiedAsset $r $File).id -ne $TargetId) {throw 'Unexpected canonical target ID.'}
        return
    }
    $staged=Get-VerifiedAsset $r (Get-StageFile $File)
    if ($staged.id -ne $TargetId) {throw 'Staged ID changed before promotion.'}
    $prior=@($Journal.originals|Where-Object name -CEQ $File.name)
    $canonical=@($r.assets|Where-Object name -IEQ $File.name)
    if ($canonical.Count) {
        $old=$canonical[0]
        if ($canonical.Count -ne 1 -or $prior.Count -ne 1 -or $old.name -cne $prior[0].name -or $old.id -ne $prior[0].id -or
            $old.digest -cne $prior[0].sha -or $old.size -ne $prior[0].bytes -or $old.state -cne 'uploaded') {throw 'Original canonical asset differs from the pre-switch journal.'}
        $retained="previous-$($old.id)-$($File.name)"
        if (@($r.assets|Where-Object name -IEQ $retained).Count) {throw 'Occupied old-asset recovery name.'}
        Move-AssetName $old $retained
    }
    Move-AssetName $staged $File.name
    if ((Get-VerifiedAsset (Get-ExactRelease $FixedReleaseId) $File).id -ne $TargetId) {throw 'Promotion readback failed.'}
}
function Restore-OriginalSlots($Ordered,$Journal,$Targets) {
    for ($i=$Ordered.Count-1;$i -ge 0;$i--) {
        $file=$Ordered[$i]
        $r=Get-ExactRelease $FixedReleaseId
        $old=@($Journal.originals|Where-Object name -CEQ $file.name)
        $canonical=@($r.assets|Where-Object name -IEQ $file.name)
        if ($canonical.Count -gt 1 -or $old.Count -gt 1) {throw 'Ambiguous restore slot.'}
        if ($old.Count -and $canonical.Count -and $canonical[0].id -eq $old[0].id -and $canonical[0].digest -ceq $old[0].sha) {continue}
        if ($canonical.Count) {
            $actual=Get-VerifiedAsset $r $file
            if ($actual.id -ne $Targets[$file.name].id) {throw 'Unrelated canonical asset appeared.'}
            Move-AssetName $actual (Get-StageFile $file).name
        }
        if ($old.Count) {
            $candidate=@($r.assets|Where-Object id -EQ $old[0].id)
            if ($candidate.Count -ne 1 -or $candidate[0].digest -cne $old[0].sha -or $candidate[0].size -ne $old[0].bytes -or
                $candidate[0].name -cne "previous-$($old[0].id)-$($file.name)") {throw 'Recoverable original bytes differ.'}
            Move-AssetName $candidate[0] $file.name
        }
    }
}
function Get-PublicFileHash([string]$Url,[long]$ExpectedBytes,[IO.Stream]$CopyTo=$null) {
    $handler=[Net.Http.HttpClientHandler]::new(); $handler.AllowAutoRedirect=$false; $handler.UseCookies=$false; $handler.UseDefaultCredentials=$false
    $client=[Net.Http.HttpClient]::new($handler)
    $clock=[Threading.CancellationTokenSource]::new([TimeSpan]::FromMinutes(30))
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('AutumnOS-Distribution-Readback/1.0')
    try {
        $uri=[Uri]$Url
        if ($uri.Host -cne 'github.com' -or -not $uri.AbsoluteUri.StartsWith("$ReleaseWeb/download/",[StringComparison]::Ordinal)) { throw 'Invalid public verification URL.' }
        for($hops=0;$hops -lt 6;$hops++) {
            if ($uri.Scheme -cne 'https' -or -not $uri.IsDefaultPort -or $uri.UserInfo -or $uri.Fragment) { throw 'Invalid download URI.' }
            $response=$client.GetAsync($uri,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$clock.Token).GetAwaiter().GetResult()
            try {
                if ([int]$response.StatusCode -in @(301,302,303,307,308)) {
                    if (-not $response.Headers.Location) { throw 'Missing redirect.' }
                    $uri=[Uri]::new($uri,$response.Headers.Location)
                    if ($uri.Host -cnotin @('release-assets.githubusercontent.com','objects.githubusercontent.com','github-releases.githubusercontent.com','github-production-release-asset-2e65be.s3.amazonaws.com')) {
                        throw 'Unexpected public release download host.'
                    }
                    continue
                }
                if ([int]$response.StatusCode -ne 200 -or ($null -ne $response.Content.Headers.ContentLength -and $response.Content.Headers.ContentLength -ne $ExpectedBytes)) {
                    throw 'Incomplete anonymous release response.'
                }
                $input=$response.Content.ReadAsStream($clock.Token)
                $hash=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
                try {
                    $buffer=[byte[]]::new(128KB)
                    [long]$count=0
                    while(($read=$input.ReadAsync($buffer,0,$buffer.Length,$clock.Token).GetAwaiter().GetResult()) -gt 0) {
                        $count+=$read
                        if ($count -gt $ExpectedBytes) { throw 'Oversized downloaded asset.' }
                        $hash.AppendData($buffer,0,$read)
                        if ($null -ne $CopyTo) { $CopyTo.Write($buffer,0,$read) }
                    }
                    if ($count -ne $ExpectedBytes) { throw 'Truncated downloaded asset.' }
                    return [Convert]::ToHexStringLower($hash.GetHashAndReset())
                } finally { $hash.Dispose(); $input.Dispose() }
            } finally { $response.Dispose() }
        }
        throw 'Too many public asset redirects.'
    } finally { $client.Dispose(); $clock.Dispose() }
}
