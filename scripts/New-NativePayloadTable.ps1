[CmdletBinding()]
param([Parameter(Mandatory)][string]$HostDirectory,[Parameter(Mandatory)][string]$FixedRuntime,
    [Parameter(Mandatory)][string]$OutputFile,[string]$AcceptanceRoot,
    [Parameter(Mandatory)][string]$BuildId,[Parameter(Mandatory)][string]$SourceSnapshotId)
$ErrorActionPreference='Stop'
function Get-PayloadTable([string]$directory) {
    $files=@(Get-ChildItem -LiteralPath $directory -Recurse -File | Sort-Object FullName)
    foreach($f in $files){
        if(($f.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Unexpected link in native payload.'}
        [ordered]@{path=[IO.Path]::GetRelativePath($directory,$f.FullName).Replace('\','/');bytes=$f.Length;sha256=(Get-FileHash -LiteralPath $f.FullName).Hash.ToLowerInvariant()}
    }
}
$nativeExe=Join-Path $HostDirectory 'AutumnOS.NativeHost.Windows.exe'
$browserExe=Join-Path $FixedRuntime 'msedgewebview2.exe'
$sig=Get-AuthenticodeSignature -LiteralPath $browserExe
if(!(Test-Path -LiteralPath $nativeExe) -or $sig.Status -ne 'Valid' -or $sig.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation' -or
    (Get-Item -LiteralPath $browserExe).VersionInfo.FileVersion -notlike '154.0.4258.53*'){throw 'Expected fixed Microsoft runtime and fresh native host publish.'}
[ordered]@{schemaVersion=2;buildId=$BuildId;sourceSnapshotId=$SourceSnapshotId;runtimePolicy='autumn-native-owned-v1';
    acceptanceRoot=$AcceptanceRoot;host=@(Get-PayloadTable $HostDirectory);runtime=@(Get-PayloadTable $FixedRuntime)} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $OutputFile -Encoding utf8NoBOM
