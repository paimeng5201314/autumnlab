[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$AbsolutePath
)

# Diagnostic observation only. Final proof readers intentionally retain their
# existing immutable-file pins; this script never supplies an acceptance proof.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-SnapshotWin32Error([Exception]$Exception) {
    # PowerShell can wrap FileStream/GetAttributes failures in a method-call
    # exception. Inspect only actual I/O exceptions with FACILITY_WIN32, and
    # never turn other validation or parsing errors into an available snapshot.
    $snapshotException = $Exception
    for ($depth = 0; $null -ne $snapshotException -and $depth -lt 16; $depth++) {
        $hresult = [int64]$snapshotException.HResult -band 4294967295L
        if ($snapshotException -is [IO.IOException] -and ($hresult -band 4294901760L) -eq 2147942400L) {
            $code = [int]($hresult -band 65535)
            if ($code -in 2, 3, 32, 33) { return $code }
        }
        $snapshotException = $snapshotException.InnerException
    }
    return $null
}

$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$reportRoot = [IO.Path]::GetFullPath((Join-Path $workspace 'artifacts/isolation-completion-20261030'))
if (-not [IO.Path]::IsPathFullyQualified($AbsolutePath)) {
    throw 'SESSION_LIVE_REPORT_ABSOLUTE_PATH_REQUIRED'
}
$full = [IO.Path]::GetFullPath($AbsolutePath)
$prefix = $reportRoot.TrimEnd([char]'\') + '\'
if (-not $full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'SESSION_LIVE_REPORT_OUTSIDE_TRACKED_ROOT'
}
$relative = $full.Substring($prefix.Length)
$allowed = '\A(?:session-browser-runs\\session-browser-case-[0-9a-f]{32}\\(?:ui|core|station-host)-report\.json|session-browser-invocations\\prepared-[0-9a-f]{32}\\(?:native-ime-control|execution)\.json)\z'
if (-not [Text.RegularExpressions.Regex]::IsMatch($relative, $allowed,
        [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor [Text.RegularExpressions.RegexOptions]::CultureInvariant)) {
    throw 'SESSION_LIVE_REPORT_EXACT_TRACKED_REPORT_PATH_REQUIRED'
}

# Check the volume and every existing ancestor, including the report itself.
# Do not follow a junction/symlink into an otherwise allowed-looking location.
$drive = [IO.Path]::GetPathRoot($full)
if ($drive.Length -ne 3 -or [IO.DriveInfo]::new($drive).DriveType -ne [IO.DriveType]::Fixed) {
    throw 'SESSION_LIVE_REPORT_FIXED_LOCAL_VOLUME_REQUIRED'
}
$current = $drive
$parts = $full.Substring($drive.Length).Split([char]'\', [StringSplitOptions]::RemoveEmptyEntries)
for ($index = 0; $index -lt $parts.Length; $index++) {
    $part = $parts[$index]
    if ($part -in '.', '..' -or $part.Contains(':') -or $part.EndsWith(' ') -or $part.EndsWith('.')) {
        throw 'SESSION_LIVE_REPORT_PLAIN_PATH_REQUIRED'
    }
    $current = Join-Path $current $part
    try { $attributes = [IO.File]::GetAttributes($current) }
    catch {
        # A missing ancestor remains a rejected path. Only the final report
        # may disappear briefly while its owner replaces the live snapshot.
        if ($index -eq $parts.Length - 1) {
            $code = Get-SnapshotWin32Error $_.Exception
            if ($null -ne $code) {
                throw "SESSION_LIVE_REPORT_SNAPSHOT_UNAVAILABLE:win32=$code; observation_only=true; proof_eligible=false"
            }
        }
        throw
    }
    if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'SESSION_LIVE_REPORT_REPARSE_POINT_REJECTED'
    }
    $isDirectory = ($attributes -band [IO.FileAttributes]::Directory) -ne 0
    if ($isDirectory -ne ($index -lt $parts.Length - 1)) {
        throw 'SESSION_LIVE_REPORT_EXPECTED_PLAIN_FILE_AND_DIRECTORIES'
    }
}

$maximumBytes = 4 * 1024 * 1024
$stream = $null
try {
    $sharing = [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete
    $stream = [IO.FileStream]::new($full, [IO.FileMode]::Open, [IO.FileAccess]::Read, $sharing)
    $length = $stream.Length
    if ($length -gt $maximumBytes) {
        throw "SESSION_LIVE_REPORT_TOO_LARGE: bytes=$length; limit=$maximumBytes; observation_only=true"
    }
    if ($length -lt 2) {
        throw "SESSION_LIVE_REPORT_INCOMPLETE: bytes=$length; observation_only=true"
    }
    $bytes = [byte[]]::new([int]$length)
    $offset = 0
    while ($offset -lt $bytes.Length) {
        $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
        if ($read -eq 0) { throw 'SESSION_LIVE_REPORT_TRUNCATED_DURING_READ: observation_only=true' }
        $offset += $read
    }
    if ($stream.ReadByte() -ne -1 -or $stream.Length -ne $length) {
        throw 'SESSION_LIVE_REPORT_CHANGED_DURING_READ: observation_only=true'
    }
}
catch {
    $code = Get-SnapshotWin32Error $_.Exception
    if ($null -ne $code) {
        throw "SESSION_LIVE_REPORT_SNAPSHOT_UNAVAILABLE:win32=$code; observation_only=true; proof_eligible=false"
    }
    throw
}
finally {
    # Parsing, formatting, pipeline consumers, and terminal rendering hold no
    # report handle. Sharing flags alone are not a replacement-writer proof.
    if ($null -ne $stream) { $stream.Dispose() }
}

$text = [Text.UTF8Encoding]::new($false, $true).GetString($bytes)
if ($text.Length -gt 0 -and $text[0] -eq [char]0xfeff) { $text = $text.Substring(1) }
$report = ConvertFrom-Json -InputObject $text -AsHashtable -Depth 64
if ($report -isnot [Collections.IDictionary]) { throw 'SESSION_LIVE_REPORT_JSON_OBJECT_REQUIRED' }

function Get-ReportValue([string]$Path) {
    $value = $report
    foreach ($key in $Path.Split([char]'.')) {
        if ($value -isnot [Collections.IDictionary] -or -not $value.Contains($key)) { return $null }
        $value = $value[$key]
    }
    return $value
}

function Get-FirstReportValue([string[]]$Paths) {
    foreach ($path in $Paths) {
        $value = Get-ReportValue $path
        if ($null -ne $value) { return $value }
    }
    return $null
}

function Get-SafeScalar($Value) {
    if ($Value -is [string]) {
        $safe = [Text.RegularExpressions.Regex]::Replace($Value, '[\p{C}]', '?')
        if ($safe.Length -gt 240) { return $safe.Substring(0, 240) + '...' }
        return $safe
    }
    if ($Value -is [bool] -or $Value -is [byte] -or $Value -is [int] -or $Value -is [long] -or
        $Value -is [uint32] -or $Value -is [uint64] -or $Value -is [decimal] -or $Value -is [double]) { return $Value }
    return $null
}

$kind = [IO.Path]::GetFileNameWithoutExtension($full)
$domPrefix = if ($kind -eq 'native-ime-control') { 'dom_evidence.' }
    elseif ($null -ne (Get-ReportValue 'final_DOM_evidence')) { 'final_DOM_evidence.' }
    elseif ($null -ne (Get-ReportValue 'last_DOM_evidence')) { 'last_DOM_evidence.' }
    else { 'browser_child.complete_header.' }
$nativeDom = $kind -eq 'native-ime-control'
$ownedFocusKey = if ($nativeDom) { 'active_owned_text' } else { 'dom_owned_text_focused' }
$documentFocusKey = if ($nativeDom) { 'document_has_focus' } else { 'dom_document_has_focus' }
$phasePrefix = if ($nativeDom) { '' } else { 'dom_' }

# Intentionally exclude text, event.data, payloads, tokens, paths, file ledgers,
# full report bodies, and application/identity data from the default output.
[pscustomobject][ordered]@{
    observation_only = $true
    proof_eligible = $false
    report_kind = $kind
    status = Get-SafeScalar (Get-ReportValue 'status')
    failure_stage = Get-SafeScalar (Get-FirstReportValue @('failure_stage', 'error_stage'))
    failure = Get-SafeScalar (Get-FirstReportValue @('failure', 'error_code'))
    report_error = Get-SafeScalar (Get-ReportValue 'report_error')
    cleanup = Get-SafeScalar (Get-FirstReportValue @('cleanup_proven', 'cleanup_confirmed'))
    cleanup_stage = Get-SafeScalar (Get-ReportValue 'cleanup_stage')
    frames = Get-SafeScalar (Get-FirstReportValue @('displayed_frames', 'frames_forwarded'))
    input_acks = Get-SafeScalar (Get-FirstReportValue @('forwarded_input_acks', 'input_actions_forwarded'))
    native_key_down = Get-SafeScalar (Get-ReportValue 'native_key_down')
    native_key_up = Get-SafeScalar (Get-ReportValue 'native_key_up')
    forwarded_key_acks = Get-SafeScalar (Get-ReportValue 'forwarded_key_acks')
    dom_key_down = Get-SafeScalar (Get-ReportValue ($domPrefix + $(if ($nativeDom) { 'key_down' } else { 'dom_key_down_count' })))
    preview_key_down = Get-SafeScalar (Get-ReportValue 'native_preview_key_down_events')
    preview_key_up = Get-SafeScalar (Get-ReportValue 'native_preview_key_up_events')
    preview_surface_events = Get-SafeScalar (Get-ReportValue 'native_preview_surface_events')
    preview_proxy_events = Get-SafeScalar (Get-ReportValue 'native_preview_proxy_events')
    preview_other_events = Get-SafeScalar (Get-ReportValue 'native_preview_other_events')
    preview_prehandled_events = Get-SafeScalar (Get-ReportValue 'native_preview_prehandled_events')
    surface_focus_attempts = Get-SafeScalar (Get-ReportValue 'surface_focus_attempts')
    surface_focus_returned_true = Get-SafeScalar (Get-ReportValue 'surface_focus_returned_true')
    surface_exact_focus_observed = Get-SafeScalar (Get-ReportValue 'surface_exact_focus_observed')
    owned_text_focused = Get-SafeScalar (Get-ReportValue ($domPrefix + $ownedFocusKey))
    document_has_focus = Get-SafeScalar (Get-ReportValue ($domPrefix + $documentFocusKey))
    composition_start = Get-SafeScalar (Get-ReportValue ($domPrefix + $phasePrefix + 'composition_start'))
    composition_update = Get-SafeScalar (Get-ReportValue ($domPrefix + $phasePrefix + 'composition_update'))
    composition_end = Get-SafeScalar (Get-ReportValue ($domPrefix + $phasePrefix + 'composition_end'))
    trusted_composition_end = Get-SafeScalar (Get-ReportValue ($domPrefix + $phasePrefix + 'trusted_composition_end'))
    native_composition_pending_end = Get-SafeScalar (Get-ReportValue 'native_composition_pending_end')
    job_active_processes = Get-SafeScalar (Get-FirstReportValue @('job_witness.active_processes', 'job.active_processes', 'browser_child.job.active_processes'))
    normal_browser_exit = Get-SafeScalar (Get-FirstReportValue @('normal_browser_exit', 'browser_child.normal_browser_exit'))
}
