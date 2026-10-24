function Assert-UpdatePreparationFileTable {
    param([Parameter(Mandatory)][object[]]$ExpectedFiles, [Parameter(Mandatory)][object[]]$ActualFiles)
    $expected = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $ExpectedFiles) {
        if (-not $file.path -or $file.bytes -lt 0 -or $file.sha256 -cnotmatch '^[a-f0-9]{64}$' -or
            -not $expected.TryAdd([string]$file.path,$file)) { throw 'Invalid or duplicate expected update file.' }
    }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($file in $ActualFiles) {
        if (-not $file.path -or -not $seen.Add([string]$file.path) -or -not $expected.ContainsKey([string]$file.path)) {
            throw 'Extra or duplicate update file.'
        }
        $original = $expected[[string]$file.path]
        if ($file.path -cne $original.path -or $file.bytes -ne $original.bytes -or $file.sha256 -cne $original.sha256) {
            throw 'Update file identity changed.'
        }
    }
    if ($seen.Count -ne $expected.Count) { throw 'Missing update file.' }
}
