[CmdletBinding()]
param([Parameter(Mandatory)][string]$CaseDirectory)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$allowed=Join-Path $workspace 'artifacts/isolation-completion-20261030/clipboard-runs'
$case=[IO.Path]::GetFullPath($CaseDirectory)
$leaf=Split-Path $case -Leaf
if((Split-Path $case -Parent) -cne $allowed -or $leaf -cnotmatch '^clipboard-case-([0-9a-f]{32})$'){throw 'Exact owned clipboard-case directory required.'}
$nonce=$Matches[1];$station='AutumnOS_ClipboardProbe_'+$nonce
$reportPath=Join-Path $case 'report.json';$report=Get-Content -LiteralPath $reportPath -Raw|ConvertFrom-Json
if($report.lease.station_name -cne $station -or -not $report.lease.closed_owned_desktop_and_station -or
    -not $report.original_profile_path_not_found_proven -or -not $report.same_profile_SID_not_found_proven){throw 'Recorded owned-object retirement required before independent postcheck.'}
if(@($report.children|Where-Object{-not $_.original_group_exit_proven -or -not $_.kernel_group_empty_proven -or -not $_.output_drained_after_EOF -or -not $_.resource_cleanup_proven}).Count){throw 'Every original child group must already be proven retired.'}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class AutumnStationAbsenceMetadata {
 [DllImport("user32.dll", EntryPoint="OpenWindowStationW", ExactSpelling=true, CharSet=CharSet.Unicode, SetLastError=true)]
 public static extern IntPtr Open(string name, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
 [DllImport("user32.dll", EntryPoint="CloseWindowStation", ExactSpelling=true, SetLastError=true)]
 [return: MarshalAs(UnmanagedType.Bool)] public static extern bool Close(IntPtr handle);
 [DllImport("kernel32.dll", ExactSpelling=true)] public static extern void SetLastError(uint error);
}
'@
$fact=[ordered]@{schema_version=1;producer='派蒙';kind='independent_exact_owned_station_absence_metadata_only';status='not_run';
    original_report_sha256=(Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant();
    script_sha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant();
    station_name=$station;requested_access=2;inherit=$false;uac_requests=0;profile_operations=0;
    clipboard_calls=0;existing_object_ACL_changes='none';cleanup_operations='close_only_if_probe_handle_returned';isolation_acceptance='not_claimed';observed_utc=[DateTimeOffset]::UtcNow}
[AutumnStationAbsenceMetadata]::SetLastError(0)
$handle=[AutumnStationAbsenceMetadata]::Open($station,$false,2)
$nativeError=[Runtime.InteropServices.Marshal]::GetLastWin32Error()
$fact.returned_handle=$handle -ne [IntPtr]::Zero;$fact.native_error=$nativeError;$fact.probe_handle_closed=$false
if($handle -ne [IntPtr]::Zero){$fact.probe_handle_closed=[AutumnStationAbsenceMetadata]::Close($handle)}
$fact.exact_station_not_found_proven=$handle -eq [IntPtr]::Zero -and $nativeError -in 2,3
$fact.status=if($fact.exact_station_not_found_proven){'passed_exact_station_absence_only'}else{'failed_station_absence_not_proven'}
$output=Join-Path $case ('station-absence-'+[Guid]::NewGuid().ToString('N')+'.json')
$fact|ConvertTo-Json -Depth 5|Set-Content -LiteralPath $output -Encoding utf8NoBOM
Write-Host "Owned station postcheck: $output"
if(-not $fact.exact_station_not_found_proven){throw 'Exact owned station absence not proven; original report retained.'}
