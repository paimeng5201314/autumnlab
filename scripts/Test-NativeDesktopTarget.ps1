[CmdletBinding()]
param([Parameter(Mandatory)][int]$ProductType, [Parameter(Mandatory)][int]$BuildNumber,
    [string]$InstallationType, [string]$OsArchitecture=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture,
    [string]$ProcessArchitecture=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture,
    [bool]$Interactive=[Environment]::UserInteractive)

if (!$Interactive -or $ProcessArchitecture -ne 'X64') { return $false }
if ($ProductType -eq 1) { return $BuildNumber -ge 19045 }
# Both server roles may run Desktop Experience. Server Core and unknown
# installation types remain excluded even on the matching kernel build.
return $ProductType -in @(2,3) -and $BuildNumber -eq 20348 -and
       $OsArchitecture -eq 'X64' -and $InstallationType -eq 'Server'
