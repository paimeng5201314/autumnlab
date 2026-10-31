[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReportPath)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -or -not [Environment]::Is64BitProcess) { throw 'Existing Windows x64 PowerShell required.' }
if (-not [IO.Path]::IsPathFullyQualified($ReportPath)) { throw 'Absolute fresh report path required.' }
$report = [IO.Path]::GetFullPath($ReportPath)
$leaf = [IO.Path]::GetFileName($report)
if ([string]::IsNullOrWhiteSpace($leaf) -or $leaf.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or $leaf.EndsWith('.') -or $leaf.EndsWith(' ')) { throw 'Plain fresh report filename required; alternate streams rejected.' }
$parent = [IO.Path]::GetDirectoryName($report)
if (-not [IO.Directory]::Exists($parent)) { throw 'Report parent must already exist.' }
foreach ($path in @($parent, $PSScriptRoot)) {
    $drive = [IO.Path]::GetPathRoot($path)
    if ($drive.Length -ne 3 -or [IO.DriveInfo]::new($drive).DriveType -ne [IO.DriveType]::Fixed) { throw 'Fixed local volume required.' }
    $current = $drive
    foreach ($part in $path.Substring($drive.Length).Split('\', [StringSplitOptions]::RemoveEmptyEntries)) {
        $current = [IO.Path]::Combine($current, $part)
        if (([IO.File]::GetAttributes($current) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Plain local ancestors required.' }
    }
}
$source = Join-Path $PSScriptRoot 'WinSta0MetadataInspector.cs'
if (([IO.File]::GetAttributes($source) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Plain inspector source required.' }
if ('AutumnOS.ReadOnlyWinSta0Metadata.Inspector' -as [type]) { throw 'Fresh PowerShell process required; an inspector type is already loaded.' }
# Reserve the fresh report before calling any station/token metadata API.
$output = [IO.FileStream]::new($report, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
try {
    $sourceStream = [IO.FileStream]::new($source, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $hash = [Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData($sourceStream))
        $sourceStream.Position = 0
        $reader = [IO.StreamReader]::new($sourceStream, [Text.Encoding]::UTF8, $true, 4096, $true)
        try { $text = $reader.ReadToEnd() } finally { $reader.Dispose() }
        Add-Type -TypeDefinition $text -Language CSharp -ErrorAction Stop
        $result = [AutumnOS.ReadOnlyWinSta0Metadata.Inspector]::Inspect()
        $result['source_sha256'] = $hash
        $result['driver_sha256'] = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $result['observed_utc'] = [DateTimeOffset]::UtcNow
        $bytes = [Text.Encoding]::UTF8.GetBytes(($result | ConvertTo-Json -Depth 12))
        $output.Write($bytes); $output.Flush($true)
        if ($result['status'] -cne 'completed_readonly_filtered_metadata') { throw 'Read-only metadata inspection failed; retained fixed-stage report.' }
    } finally { $sourceStream.Dispose() }
} finally { $output.Dispose() }
Write-Host 'Completed read-only WinSta0 filtered metadata; effective access and isolation are not claimed.'
