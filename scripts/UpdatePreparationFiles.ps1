function Assert-ProductionUpdateBuild {
    param([Parameter(Mandatory)]$Build)
    $reject='Production update rejects test or private platform/isolation acceptance builds.'
    foreach($name in @('status','test_build','temporary_platform_acceptance','isolation_acceptance_root','build_properties')){
        if($Build.PSObject.Properties.Name -notcontains $name){throw $reject}
    }
    if($Build.status -cne 'passed' -or $Build.test_build -isnot [bool] -or $Build.test_build -or
        $Build.temporary_platform_acceptance -isnot [bool] -or $Build.temporary_platform_acceptance -or
        ($null -ne $Build.isolation_acceptance_root -and $Build.isolation_acceptance_root -isnot [string]) -or
        -not [string]::IsNullOrEmpty([string]$Build.isolation_acceptance_root) -or $Build.build_properties -isnot [array]){
        throw $reject
    }
    foreach($property in $Build.build_properties){
        if($property -isnot [string] -or $property -match '(?:^-p:|;)(TestUpdateTrustFile|TestUpdateFeedFile|UpdateHealthFault)=' -or
            ($property -match '(?:^-p:|;)AutumnPlatformAcceptance=' -and $property -ine '-p:AutumnPlatformAcceptance=false')){
            throw $reject
        }
    }
    if(@($Build.build_properties|Where-Object {$_ -match '^-p:AutumnPlatformAcceptance='}).Count -ne 1){
        throw 'Production update rejects test or private platform/isolation acceptance builds.'
    }
}

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
