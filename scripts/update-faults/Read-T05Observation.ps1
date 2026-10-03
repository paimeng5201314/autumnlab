# Observation must not block the updater's atomic journal rename. Read exact bytes,
# dispose the live handle, and only then parse or write the report snapshot.
function Read-T05ObservationSnapshot([string]$Path){
    Assert-T05PlainPath $Path
    Invoke-T05SharingRead {
        $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $memory=[IO.MemoryStream]::new()
        try{
            if($stream.Length -gt 16MB){throw 'Bounded drill JSON limit exceeded.'}
            $stream.CopyTo($memory)
            if($memory.Length -gt 16MB){throw 'Bounded drill JSON limit exceeded.'}
            return [pscustomobject]@{Bytes=$memory.ToArray()}
        }finally{$memory.Dispose();$stream.Dispose()}
    }
}
function Read-T05Json([string]$Path){
    $snapshot=Read-T05ObservationSnapshot $Path
    # Invalid JSON remains an error, outside the bounded sharing-violation retry.
    [Text.Encoding]::UTF8.GetString($snapshot.Bytes)|ConvertFrom-Json
}
function Copy-T05ObservedFile([string]$LiteralPath,[string]$Destination){
    $snapshot=Read-T05ObservationSnapshot $LiteralPath
    [IO.File]::WriteAllBytes($Destination,$snapshot.Bytes)
}

