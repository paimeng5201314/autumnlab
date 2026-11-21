[CmdletBinding()]
param([string]$BuildVersion, [string]$BuildId, [string]$ReleaseLabel, [string]$Channel)

[xml]$brand = Get-Content -LiteralPath (Join-Path $PSScriptRoot '../build/Brand.props') -Raw
if (!$BuildVersion) { $BuildVersion = $brand.SelectSingleNode('//AutumnVersion').InnerText }
if ($BuildVersion -cnotmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
    @($BuildVersion.Split('.') | Where-Object { [decimal]$_ -gt 65534 }).Count) {
    throw 'Use a numeric major.minor.patch version representable in Windows assembly metadata.'
}
$display = "v$BuildVersion"
$date = [DateTime]::Now.ToString('yyyyMMdd', [Globalization.CultureInfo]::InvariantCulture)
$expectedId = "$display-$date"
if ($BuildId -and $BuildId -cne $expectedId) { throw "The product build ID for today is $expectedId." }
if ($ReleaseLabel -and $ReleaseLabel -cne $display) { throw "The product display version must be $display." }
if (!$Channel) { $Channel = $display }
if ($Channel -cnotin @($display, "meta$display")) { throw "Use $display or meta$display as the release channel." }
[pscustomobject]@{
    Version = $BuildVersion
    DisplayVersion = $display
    BuildId = $expectedId
    Channel = $Channel
    UpdateSequence = [long]$brand.SelectSingleNode('//AutumnUpdateSequence').InnerText
    SecuritySequence = [long]$brand.SelectSingleNode('//AutumnSecuritySequence').InnerText
}
