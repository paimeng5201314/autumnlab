[CmdletBinding()]
param([Parameter(Mandatory)][string]$Version, [Parameter(Mandatory)][string]$Channel)

if ($Version -cnotmatch '^(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z') {
    throw 'Expected a three-part product version, for example 1.0.1.'
}
$display = 'v' + $Version
$preview = $Channel -ceq ('meta' + $display)
if (!$preview -and $Channel -cne $display) { throw 'Channel must be vX.Y.Z or metavX.Y.Z for this product version.' }
[ordered]@{
    tag_name = $Channel
    name = $display
    prerelease = $preview
    make_latest = if ($preview) { 'false' } else { 'true' }
}
