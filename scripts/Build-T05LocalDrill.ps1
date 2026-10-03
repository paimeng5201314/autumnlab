[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$RunId,
    [Parameter(Mandatory)][string]$PublicTrustFile,
    [Parameter(Mandatory)][string]$PrivateKeyFile,
    [switch]$RunNativeDrill,
    [ValidateSet('T05','T06')][string]$TaskId='T05'
)
. "$PSScriptRoot/Common.ps1"
if($RunId -notmatch '^[A-Za-z0-9][A-Za-z0-9-]{0,55}$'){throw 'Invalid isolated run ID.'}
$run=Join-Path $ProjectRoot "artifacts/$($TaskId.ToLowerInvariant())-drill/$RunId"
if(Test-Path -LiteralPath $run){throw 'Existing drill preserved. Choose a new RunId.'}
New-Item -ItemType Directory -Path $run | Out-Null
$trust=Get-Content -LiteralPath $PublicTrustFile -Raw|ConvertFrom-Json
if($trust.purpose -ne 'local-test' -or $trust.keys.Count -ne 1){throw 'Only a dedicated local-test public root is allowed.'}
$public=Join-Path $run 'test-public.json';Copy-Item -LiteralPath $PublicTrustFile -Destination $public
$feed=Join-Path $run 'feed';New-Item -ItemType Directory -Path $feed|Out-Null
$feedInput=Join-Path $run 'test-feed.txt';Set-Content -LiteralPath $feedInput -Value $feed -Encoding utf8NoBOM
Set-Content -LiteralPath (Join-Path $feed 'releases.json') -Value '[]' -Encoding utf8NoBOM
$cli=Join-Path $ProjectRoot 'tools/AutumnOS.Update.Cli/bin/x64/Release/net10.0/win-x64/AutumnOS.Update.Cli.dll'
$variants=@(
    @{name='B';version='0.5.1';sequence=501;release=501;asset=5010;fault=$false},
    @{name='Bad';version='0.5.2';sequence=502;release=502;asset=5020;fault=$true},
    @{name='A';version='0.5.0';sequence=500;release=500;asset=5000;fault=$false}
)
if($TaskId -eq 'T06'){
    $variants=@(
        @{name='B';version='0.5.2';sequence=602;release=602;asset=6020;fault=$false},
        @{name='Bad';version='0.5.3';sequence=603;release=603;asset=6030;fault=$true},
        @{name='A';version='0.5.1';sequence=601;release=601;asset=6010;fault=$false}
    )
}
$outputs=[ordered]@{task_id=$TaskId;run_id=$RunId;public_trust=$public;feed=$feed;variants=@();native_drill='not_run';private_key_retained_outside_delivery=$true}
$result=Join-Path $run 'drill-builds.json'
foreach($variant in $variants){
    $id=$TaskId+'-'+$RunId+'-'+$variant.name
    & "$PSScriptRoot/Build.ps1" -BuildId $id -BuildVersion $variant.version -TestUpdateTrustFile $public -TestUpdateFeedFile $feedInput -UpdateHealthFault:$variant.fault
    & "$PSScriptRoot/Package-T05.ps1" -Stage Prepare
    $prepared=Get-Content -LiteralPath (Join-Path $ProjectRoot "artifacts/builds/$id/package-prepared.json") -Raw|ConvertFrom-Json
    $delivery=Split-Path $prepared.executable -Parent
    $material=Join-Path $run ($variant.name+'-signed')
    Invoke-Dotnet -Arguments @($cli,'prepare','--source',$delivery,'--output',$material,'--version',$variant.version,'--build-id',$id,'--channel','plus','--sequence',[string]$variant.sequence,'--key-id',$trust.keys[0].keyId,'--release-id',[string]$variant.release,'--asset-id',[string]$variant.asset) -LogPath (Join-Path $run ($variant.name+'-prepare.log'))
    $manifest=Join-Path $material 'autumn.update.json';$signature=Join-Path $material 'autumn.update.sig'
    Invoke-Dotnet -Arguments @($cli,'sign','--manifest',$manifest,'--private-key-file',$PrivateKeyFile,'--output',$signature) -LogPath (Join-Path $run ($variant.name+'-sign.log'))
    $meta=Get-Content -LiteralPath $manifest -Raw|ConvertFrom-Json
    Invoke-Dotnet -Arguments @($cli,'verify','--manifest',$manifest,'--signature',$signature,'--trust',$public,'--payload',(Join-Path $material $meta.payload.asset)) -LogPath (Join-Path $run ($variant.name+'-verify.log'))
    $outputs.variants+=@{name=$variant.name;build_id=$id;version=$variant.version;source_snapshot_id=$prepared.source_snapshot_id;executable=$prepared.executable;signed_material=$material;health_fault=$variant.fault}
    $outputs|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $result -Encoding utf8
}
# The last tracked build is A. Its frozen delivered bytes are packaged before the native drill.
& "$PSScriptRoot/Test.ps1"
& "$PSScriptRoot/Package-T05.ps1" -Stage Finalize -SkipNativeRegression
if($RunNativeDrill){
    $driver=if($TaskId -eq 'T06'){'Test-T06IntegrationSmoke.ps1'}else{'Test-T05UpdateSmoke.ps1'}
    & (Join-Path $PSScriptRoot $driver) -AExecutablePath $outputs.variants[2].executable -BPayloadDirectory $outputs.variants[0].signed_material -BadPayloadDirectory $outputs.variants[1].signed_material -FeedDirectory $feed -ReportDirectory (Join-Path $run 'native-drill')
    $outputs.native_drill=Join-Path $run $(if($TaskId -eq 'T06'){'native-drill/integration-smoke.json'}else{'native-drill/update-smoke.json'})
    $outputs|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $result -Encoding utf8
}
Write-Host "Isolated drill material: $result. Remove the caller-owned temporary private key when all signing is finished."
