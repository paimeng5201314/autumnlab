function Get-SingleFilePayloadPlan {
    [CmdletBinding()]
    param([Parameter(Mandatory)][object[]]$DeliveryFiles, [Parameter(Mandatory)]$NativeCatalog)
    $required=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($group in @(@('NativeHost','host'),@('WebView2Runtime','runtime'))){
        $items=@($NativeCatalog.($group[1]))
        if($items.Count -eq 0){throw 'Empty native payload catalog.'}
        foreach($item in $items){
            $path=[string]$item.path
            if(-not $path -or $path.Contains('\') -or $path.Contains(':') -or
                @($path.Split('/')|Where-Object {$_ -in @('','.','..') -or $_.EndsWith('.') -or $_.EndsWith(' ')}).Count -or
                $item.bytes -lt 0 -or $item.sha256 -cnotmatch '^[a-f0-9]{64}$') {throw 'Invalid native payload entry.'}
            $expected=[pscustomobject]@{path=($group[0]+'/'+$path);bytes=$item.bytes;sha256=$item.sha256}
            if(-not $required.TryAdd($expected.path,$expected)){throw 'Duplicate native payload entry.'}
        }
    }
    $copied=[Collections.Generic.List[object]]::new()
    $removed=[Collections.Generic.List[object]]::new()
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($file in $DeliveryFiles){
        $path=[string]$file.path
        if(-not $path -or -not $seen.Add($path)){throw 'Duplicate or empty delivery path.'}
        if($required.ContainsKey($path)){
            $binding=$required[$path]
            if($path -cne $binding.path -or $file.bytes -ne $binding.bytes -or $file.sha256 -cne $binding.sha256){throw 'Native payload differs from compiled catalog.'}
        }elseif($path.StartsWith('NativeHost/',[StringComparison]::OrdinalIgnoreCase) -or $path.StartsWith('WebView2Runtime/',[StringComparison]::OrdinalIgnoreCase)){
            throw 'Uncatalogued native payload file.'
        }elseif($path.EndsWith('.pdb',[StringComparison]::OrdinalIgnoreCase)){
            $removed.Add($file)
            continue
        }
        $copied.Add($file)
    }
    foreach($path in $required.Keys){if(-not $seen.Contains($path)){throw 'Missing catalogued native payload file.'}}
    [pscustomobject]@{Copied=$copied.ToArray();Removed=$removed.ToArray();RequiredNativeFiles=$required.Count}
}
