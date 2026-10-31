[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-f0-9]{32}$')][string]$CaseNonce,
    [Parameter(Mandatory)][string]$PreparedDirectory)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=Join-Path $workspace 'artifacts/isolation-completion-20261030'
$prepared=[IO.Path]::GetFullPath($PreparedDirectory)
if(-not $prepared.StartsWith((Join-Path $root 'clipboard-invocations')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Exact owned preparation required.'}
$manifestPath=Join-Path $prepared 'input-manifest.json'
$preparation=Get-Content -LiteralPath (Join-Path $prepared 'preparation.json') -Raw|ConvertFrom-Json
$manifestHash=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant()
if($manifestHash -cne $preparation.manifest_sha256){throw 'Original manifest identity differs.'}
$caseRoot=Join-Path $root ('clipboard-runs/clipboard-case-'+$CaseNonce)
$reportPath=Join-Path $caseRoot 'report.json'
$report=Get-Content -LiteralPath $reportPath -Raw|ConvertFrom-Json
if($report.manifest_sha256 -cne $manifestHash -or -not $report.profile_created_by_this_invocation -or
    $report.profile_unique_name -cne ('AutumnOS.Paimon.ClipboardProbe.'+$CaseNonce) -or $report.seed_commands_sent -ne 0){throw 'Only the exact pre-clipboard owned-profile crash case is supported.'}
$leaseRows=@(foreach($line in (Get-Content -LiteralPath (Join-Path $prepared 'broker-output.log'))){
    try{$row=$line|ConvertFrom-Json -ErrorAction Stop}catch{continue}
    if($row.kind -ceq 'owned_private_clipboard_station_lease' -and $row.detail.nonce -ceq $CaseNonce -and
        $row.detail.original_controller_handle_exit_observed -and $row.detail.original_helper_handle_exit_observed){$row}
})
if($leaseRows.Count -ne 1 -or -not $leaseRows[0].detail.explicit_release_handshake -or $leaseRows[0].detail.helper_exit_code -ne 0){throw 'Original broker-held controller/helper exit proof required.'}
$children=@($report.children)
if($children.Count -ne 2 -or @($children|Where-Object{$_.kind -cne 'browser' -or $_.operation -cne 'copy'}).Count -ne 0){throw 'Only the two no-clipboard preflight children may exist.'}
foreach($child in $children){
    if(-not $child.stdio_complete -or -not $child.job.atomic_launch_attribute_attached -or $child.job.limit_flags -ne 0 -or
        $child.job.active_processes -ne 0 -or @($child.job.last_process_ids).Count -ne 0 -or
        -not $child.job.last_query_succeeded -or $child.job.query_failure_seen -or $child.job.membership_failure_seen -or
        @($child.audit.Held).Count -eq 0 -or $child.audit.process_identity_gap){throw 'Exact child Job-empty and identity proof required.'}
    foreach($process in $child.audit.Held){if(-not $process.original_handle_signaled){throw 'An original held process was not observed exited.'}}
    if(@($child.events|Where-Object{$_.kind -in 'operation_complete','seed_complete','paste_event_data','collector_ready'}).Count -ne 0){throw 'A clipboard-operation event prevents this preflight-only recovery.'}
}
$sourceReportHash=(Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant()
$recoveryPath=Join-Path $caseRoot ('owned-profile-recovery-'+[Guid]::NewGuid().ToString('N')+'.json')
$result=[ordered]@{schema_version=1;producer='派蒙';status='not_run';case_nonce=$CaseNonce;
    original_report_sha256=$sourceReportHash;manifest_sha256=$manifestHash;
    recovery_script_sha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant();
    proof='original broker-held helper/controller exits; exact atomic child Jobs empty and original held children signaled; preflight only; zero clipboard seeds';
    original_report_mutated=$false;profile_name=$report.profile_unique_name;clipboard_calls=0;uac_requests=0;
    process_launches=0;existing_ACL_changes='none';recursive_filesystem_deletion='none';profile_path='redacted_not_recorded'}
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
[assembly: System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
public static class OwnedClipboardProfileRecoveryNative {
 [DllImport("userenv.dll",CharSet=CharSet.Unicode)] public static extern int DeriveAppContainerSidFromAppContainerName(string name,out IntPtr sid);
 [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool ConvertSidToStringSidW(IntPtr sid,out IntPtr text);
 [DllImport("advapi32.dll")] public static extern IntPtr FreeSid(IntPtr sid);
 [DllImport("kernel32.dll")] public static extern IntPtr LocalFree(IntPtr value);
 [DllImport("userenv.dll",CharSet=CharSet.Unicode)] public static extern int GetAppContainerFolderPath(string sid,out IntPtr path);
 [DllImport("userenv.dll",CharSet=CharSet.Unicode)] public static extern int DeleteAppContainerProfile(string name);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern uint GetFileAttributesW(string path);
 [DllImport("user32.dll",CharSet=CharSet.Unicode,SetLastError=true)] public static extern IntPtr OpenWindowStationW(string name,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint access);
 [DllImport("user32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool CloseWindowStation(IntPtr station);
}
'@
$sid=[IntPtr]::Zero;$sidTextPointer=[IntPtr]::Zero;$folderPointer=[IntPtr]::Zero;$folder=$null
try{
    $hr=[OwnedClipboardProfileRecoveryNative]::DeriveAppContainerSidFromAppContainerName($report.profile_unique_name,[ref]$sid)
    if($hr -ne 0 -or $sid -eq [IntPtr]::Zero){throw 'Unable to derive exact synthetic profile identity.'}
    if(-not [OwnedClipboardProfileRecoveryNative]::ConvertSidToStringSidW($sid,[ref]$sidTextPointer)){throw 'Synthetic SID conversion failed.'}
    $sidText=[Runtime.InteropServices.Marshal]::PtrToStringUni($sidTextPointer)
    $sidHash=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($sidText)))
    if($sidHash -cne $report.profile_sid_sha256){throw 'Synthetic profile SID does not match the original OS-queried identity.'}
    $result.profile_sid_sha256=$sidHash
    $hr=[OwnedClipboardProfileRecoveryNative]::GetAppContainerFolderPath($sidText,[ref]$folderPointer)
    $result.before_query_hresult=$hr
    if($hr -ne 0 -or $folderPointer -eq [IntPtr]::Zero){throw 'Exact created profile folder must be present before this one recovery.'}
    $folder=[Runtime.InteropServices.Marshal]::PtrToStringUni($folderPointer)
    if(-not [IO.Path]::IsPathFullyQualified($folder)){throw 'Absolute exact OS-returned profile path required.'}
    $result.delete_profile_hresult=[OwnedClipboardProfileRecoveryNative]::DeleteAppContainerProfile($report.profile_unique_name)
    if($result.delete_profile_hresult -ne 0){throw 'Exact profile deletion API failed; no filesystem fallback permitted.'}
    $attributes=[OwnedClipboardProfileRecoveryNative]::GetFileAttributesW($folder)
    $nativeError=[Runtime.InteropServices.Marshal]::GetLastWin32Error()
    $result.original_path_attributes=$attributes;$result.original_path_native_error=$nativeError
    $result.original_path_not_found=($attributes -eq [uint32]::MaxValue -and $nativeError -in 2,3)
    $postPointer=[IntPtr]::Zero
    $postHr=[OwnedClipboardProfileRecoveryNative]::GetAppContainerFolderPath($sidText,[ref]$postPointer)
    $result.same_sid_post_query_hresult=$postHr
    $result.same_sid_post_query_no_path=($postPointer -eq [IntPtr]::Zero)
    if($postPointer -ne [IntPtr]::Zero){[Runtime.InteropServices.Marshal]::FreeCoTaskMem($postPointer)}
    $result.same_sid_not_found=($postHr -in -2147024894,-2147024893 -and $result.same_sid_post_query_no_path)
    $station=[OwnedClipboardProfileRecoveryNative]::OpenWindowStationW(('AutumnOS_ClipboardProbe_'+$CaseNonce),$false,2)
    $stationError=[Runtime.InteropServices.Marshal]::GetLastWin32Error()
    $result.station_open_returned_handle=($station -ne [IntPtr]::Zero);$result.station_open_native_error=$stationError
    if($station -ne [IntPtr]::Zero){$result.station_probe_handle_closed=[OwnedClipboardProfileRecoveryNative]::CloseWindowStation($station)}
    $result.station_name_not_found=($station -eq [IntPtr]::Zero -and $stationError -in 2,3)
    if(-not $result.original_path_not_found -or -not $result.same_sid_not_found){throw 'Post-deletion independent profile checks did not prove absence.'}
    if((Get-FileHash -LiteralPath $reportPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $sourceReportHash){throw 'Original report bytes changed.'}
    $result.status='passed_exact_owned_profile_recovery_only'
}catch{
    $result.status='failed';$result.failure_type=$_.Exception.GetType().Name;throw
}finally{
    if($folderPointer -ne [IntPtr]::Zero){[Runtime.InteropServices.Marshal]::FreeCoTaskMem($folderPointer)}
    if($sidTextPointer -ne [IntPtr]::Zero){[void][OwnedClipboardProfileRecoveryNative]::LocalFree($sidTextPointer)}
    if($sid -ne [IntPtr]::Zero){[void][OwnedClipboardProfileRecoveryNative]::FreeSid($sid)}
    $result|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $recoveryPath -Encoding utf8NoBOM
    Write-Host "Exact owned recovery report: $recoveryPath"
}
