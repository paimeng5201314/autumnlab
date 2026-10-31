[CmdletBinding()]
param([ValidateSet('Prepare','Execute')][string]$Stage='Prepare',[string]$BuildDirectory,[string]$PreparedDirectory)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$root=Join-Path $workspace 'artifacts/isolation-completion-20261030'
function Require-Plain([string]$Path){
    $full=[IO.Path]::GetFullPath($Path);$drive=[IO.Path]::GetPathRoot($full)
    if($drive.Length -ne 3 -or [IO.DriveInfo]::new($drive).DriveType -ne [IO.DriveType]::Fixed){throw 'Fixed local volume required.'}
    $current=$drive
    foreach($part in $full.Substring($drive.Length).Split([char]'\',[StringSplitOptions]::RemoveEmptyEntries)){
        if($part.Contains(':') -or $part.EndsWith(' ') -or $part.EndsWith('.')){throw 'Plain local path required.'}
        $current=Join-Path $current $part
        if((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Reparse point rejected.'}
    }
}
function Verify-Files([string]$Directory,$Files,[bool]$ExactSet){
    Require-Plain $Directory
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($binding in $Files){
        if([IO.Path]::IsPathRooted($binding.path) -or $binding.path -match '[:\x00]' -or
            @($binding.path.Split([char[]]@('/','\'),[StringSplitOptions]::None)|Where-Object{$_ -in '', '.', '..' -or $_.EndsWith(' ') -or $_.EndsWith('.')}).Count){throw 'Plain relative binding required.'}
        $file=[IO.Path]::GetFullPath((Join-Path $Directory $binding.path))
        if(-not $file.StartsWith($Directory.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase) -or -not $seen.Add($file)){throw 'Duplicate or escaped file binding.'}
        Require-Plain $file;$item=Get-Item -LiteralPath $file
        if($item.PSIsContainer -or $item.Length -ne $binding.bytes -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $binding.sha256){throw 'Frozen bytes differ.'}
    }
    if($ExactSet){
        $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($Directory)
        $actual=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        while($pending.Count){foreach($item in (Get-ChildItem -LiteralPath $pending.Pop() -Force)){Require-Plain $item.FullName;if($item.PSIsContainer){$pending.Push($item.FullName)}else{[void]$actual.Add($item.FullName)}}}
        if(-not $seen.SetEquals($actual)){throw 'Complete payload file set differs.'}
    }
}
if($Stage -ceq 'Execute'){
    if(-not $PreparedDirectory -or $BuildDirectory){throw 'Execute requires only the reviewed PreparedDirectory.'}
    $prepared=[IO.Path]::GetFullPath($PreparedDirectory)
    if(-not $prepared.StartsWith((Join-Path $root 'session-browser-invocations')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Tracked preparation required.'}
    Require-Plain $prepared
    $preparation=Get-Content -LiteralPath (Join-Path $prepared 'preparation.json') -Raw|ConvertFrom-Json
    if($preparation.status -cne 'prepared_not_executed'){throw 'Unexecuted preparation required.'}
    $BuildDirectory=$preparation.build_directory
}elseif(-not $BuildDirectory -or $PreparedDirectory){throw 'Prepare requires a tracked BuildDirectory and starts no runtime trial.'}
$build=[IO.Path]::GetFullPath($BuildDirectory)
if(-not $build.StartsWith((Join-Path $root 'session-browser-builds')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Tracked compilation required.'}
Require-Plain $build
$record=Get-Content -LiteralPath (Join-Path $build 'build-result.json') -Raw|ConvertFrom-Json
if($record.status -cne 'passed_compilation_only' -or $record.source_snapshot_id -notmatch '^sha256:[a-f0-9]{64}$'){throw 'Source-bound successful compilation required.'}
Verify-Files $workspace $record.source_files $false
$canonical=($record.source_files|ForEach-Object{"$($_.sha256)  $($_.bytes)  $($_.path)"}) -join "`n"
if(('sha256:'+[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical)))) -cne $record.source_snapshot_id){throw 'Source snapshot differs.'}
$outputs=@{}
foreach($output in $record.outputs){if($output.kind -cnotin 'core','ui' -or $outputs.ContainsKey($output.kind) -or $output.directory -cne (Join-Path $build $output.kind)){throw 'Exact core/UI output bindings required.'};Verify-Files $output.directory $output.files $true;$outputs[$output.kind]=$output}
if($outputs.Count -ne 2){throw 'Both native UI and core outputs required.'}
if($record.source_archive.path -cne (Join-Path $build 'probe-source.zip') -or $record.source_archive.readback -cne 'passed' -or
    (Get-FileHash -LiteralPath $record.source_archive.path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $record.source_archive.sha256){throw 'Source ZIP binding differs.'}
if($record.verification.failed -ne 0 -or $record.verification.passed -lt 43 -or $record.verification.pure_checks -lt 35 -or $record.verification.base_pure_checks -lt 29 -or $record.verification.native_key_capture_checks -lt 6 -or $record.verification.owned_file_IO_checks -lt 8 -or @($record.verification.compiled_only).Count -ne 2 -or
    @($record.verification.compiled_only|Where-Object{$_.no_argument_exit -ne 0}).Count -ne 0 -or
    (Get-FileHash -LiteralPath $record.verification.protocol_test_path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $record.verification.protocol_test_sha256){throw 'Pure tests and compiled-only entries required.'}
if($Stage -ceq 'Prepare'){
    $prepared=Join-Path $root ('session-browser-invocations/prepared-'+[Guid]::NewGuid().ToString('N'));[void][IO.Directory]::CreateDirectory($prepared)
    $owner=[Security.Principal.WindowsIdentity]::GetCurrent().User
    $preparedInfo=[IO.DirectoryInfo]::new($prepared)
    $actualOwner=[IO.FileSystemAclExtensions]::GetAccessControl($preparedInfo,[Security.AccessControl.AccessControlSections]::Owner).GetOwner([Security.Principal.SecurityIdentifier])
    if($actualOwner.Value -cne $owner.Value){throw 'New prepared directory must already belong to this ordinary caller.'}
    # Only the new directory DACL is written; unchanged owner/group sections
    # require no WRITE_OWNER permission or privilege adjustment.
    $preparedSecurity=[Security.AccessControl.DirectorySecurity]::new()
    $preparedSecurity.SetAccessRuleProtection($true,$false)
    $inheritance=[Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
    foreach($principal in @($owner,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))){
        $preparedSecurity.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($principal,[Security.AccessControl.FileSystemRights]::FullControl,$inheritance,[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
    }
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($prepared),$preparedSecurity)
    $cases=Join-Path $root 'session-browser-runs';[void][IO.Directory]::CreateDirectory($cases);Require-Plain $cases
    $runtime=Join-Path $workspace '.tools/webview2/154.0.4258.53/Microsoft.WebView2.FixedVersionRuntime.154.0.4258.53.x64';Require-Plain $runtime
    $runtimeFiles=@(Get-ChildItem -LiteralPath $runtime -Recurse -File|Sort-Object FullName|ForEach-Object{Require-Plain $_.FullName;[ordered]@{path=[IO.Path]::GetRelativePath($runtime,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
    $manifestPath=Join-Path $prepared 'input-manifest.json'
    [ordered]@{schema_version=1;source_snapshot_id=$record.source_snapshot_id;case_root=$cases;
        core_directory=$outputs.core.directory;core_executable='AutumnOS.SessionBrowserPrototype.exe';core_files=$outputs.core.files;
        ui_directory=$outputs.ui.directory;ui_executable='AutumnOS.SessionBrowserUI.exe';ui_files=$outputs.ui.files;
        runtime_directory=$runtime;runtime_version='154.0.4258.53';runtime_files=$runtimeFiles}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM
    [ordered]@{schema_version=1;producer='派蒙';status='prepared_not_executed';build_directory=$build;build_id=$record.build_id;source_snapshot_id=$record.source_snapshot_id;
        manifest_sha256=(Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant();
        scope='One bounded three-step invocation: native WinUI-only startup preflight; then visible ordinary WinSta0/Default native WebView IME control using the same fixed runtime and owned page, direct physical input with no CDP Input/NETONLY/custom profile/station creation (fresh local UDF retained as artifact); only after original control process exit0 and protected source/manifest/runtime-bound report, start one interactive viewer and at most one NETONLY private station/desktop/zero-capability AppContainer/browser trial. Measure the native control compositionend trust bit and require the private bridge to match that measured mode plus actual composition phase/data effects, native transaction/ACK/DOM hashes and full original-process cleanup. No default acceptance of an untrusted end, no JS-synthesized events. NETONLY has real LSA audit/provider effects; no authentication claim. No UAC/service/policy change or production/game launch. No retry of any step.';
        startup_preflight='not_run_native_UI_only_first_gate';
        native_ime_control='not_run_direct_native_input_second_gate';
        netonly_execution='not_run_requires_new_explicit_authorization';browser_execution='not_run';interactive_ui='not_run';physical_mouse_keyboard='not_run';physical_chinese_ime='not_run';
        UAC_requests=0;lsa_cleanup='not_claimed';authentication_acceptance='not_claimed';complete_isolation='not_claimed';production_gate='blocked'}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $prepared 'preparation.json') -Encoding utf8NoBOM
    Write-Host "Prepared only; no NETONLY, station, AppContainer, browser or native UI trial: $prepared";exit 0
}
$manifestPath=Join-Path $prepared 'input-manifest.json';Require-Plain $manifestPath
if($preparation.source_snapshot_id -cne $record.source_snapshot_id -or (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $preparation.manifest_sha256){throw 'Reviewed preparation differs.'}
$authorizationPath=Join-Path $prepared 'authorization.json';Require-Plain $authorizationPath
$authorization=Get-Content -LiteralPath $authorizationPath -Raw|ConvertFrom-Json
if($authorization.authorization -cne 'explicit_user_reply_allow_one_owned_session_browser_ui_trial' -or $authorization.prepared_id -cne (Split-Path $prepared -Leaf) -or $authorization.manifest_sha256 -cne $preparation.manifest_sha256){throw 'New explicit authorization for this exact interactive LSA/browser trial required.'}
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
if($manifest.case_root -cne (Join-Path $root 'session-browser-runs') -or $manifest.core_directory -cne $outputs.core.directory -or $manifest.ui_directory -cne $outputs.ui.directory -or
    $manifest.core_executable -cne 'AutumnOS.SessionBrowserPrototype.exe' -or $manifest.ui_executable -cne 'AutumnOS.SessionBrowserUI.exe' -or $manifest.runtime_version -cne '154.0.4258.53'){throw 'Exact execution scope differs.'}
Verify-Files $manifest.core_directory $manifest.core_files $true;Verify-Files $manifest.ui_directory $manifest.ui_files $true;Verify-Files $manifest.runtime_directory $manifest.runtime_files $true
$executionPath=Join-Path $prepared 'execution.json'
$execution=[ordered]@{schema_version=1;producer='派蒙';status='launching_authorized_interactive_trial';started_utc=[DateTimeOffset]::UtcNow;build_id=$record.build_id;source_snapshot_id=$record.source_snapshot_id;manifest_sha256=$preparation.manifest_sha256;UAC_requests=0;complete_isolation='not_claimed'}
$guard=[IO.File]::Open($executionPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read);try{$bytes=[Text.Encoding]::UTF8.GetBytes(($execution|ConvertTo-Json -Depth 7));$guard.Write($bytes,0,$bytes.Length);$guard.Flush($true)}finally{$guard.Dispose()}
$process=$null;$started=$false
try{
    $preflightPath=Join-Path $prepared 'ui-startup-preflight.json'
    if([IO.File]::Exists($preflightPath)){throw 'Fresh UI preflight report required; no retry.'}
    $preflightStart=[Diagnostics.ProcessStartInfo]::new((Join-Path $manifest.ui_directory $manifest.ui_executable));$preflightStart.UseShellExecute=$false;$preflightStart.CreateNoWindow=$true
    $preflightStart.ArgumentList.Add('--ui-startup-check');$preflightStart.ArgumentList.Add($manifestPath);$preflightStart.ArgumentList.Add($preflightPath)
    $preflight=[Diagnostics.Process]::new();$preflight.StartInfo=$preflightStart;$preflightStarted=$false
    try{
        if(-not $preflight.Start()){throw 'Native WinUI-only startup gate failed to launch.'};$preflightStarted=$true
        $execution.status='running_native_UI_only_preflight';$execution.original_preflight_pid=$preflight.Id
        $execution|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $executionPath -Encoding utf8NoBOM
        $preflight.WaitForExit();$execution.original_preflight_exit_observed=$preflight.HasExited;$execution.preflight_exit_code=$preflight.ExitCode
        Require-Plain $preflightPath
        $preflightReport=Get-Content -LiteralPath $preflightPath -Raw|ConvertFrom-Json
        $execution.preflight_report_path=$preflightPath;$execution.preflight_report_sha256=(Get-FileHash -LiteralPath $preflightPath -Algorithm SHA256).Hash.ToLowerInvariant()
        if($preflight.ExitCode -ne 0 -or $preflightReport.schema_version -ne 1 -or $preflightReport.status -cne 'completed_native_WinUI_startup_only' -or
            $preflightReport.original_process_id -ne $preflight.Id -or $preflightReport.source_snapshot_id -cne $record.source_snapshot_id -or
            $preflightReport.manifest_sha256 -cne $preparation.manifest_sha256 -or $preflightReport.failure_stage -or $preflightReport.failure_type -or
            $preflightReport.core_started -ne $false -or $preflightReport.netonly_calls -ne 0 -or $preflightReport.browser_starts -ne 0 -or $preflightReport.appcontainer_profiles -ne 0){throw 'Native UI-only startup identity/resources/normal exit gate failed; NETONLY trial not started.'}
        foreach($field in @('window_created','root_loaded','rendering_observed','original_hwnd_visible','window_closed','metadata_provider_available')){
            if($preflightReport.$field -ne $true){throw "Native UI-only positive evidence missing: $field"}
        }
        $execution.startup_preflight='passed_native_UI_only';$execution.status='launching_authorized_interactive_trial'
        $execution|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $executionPath -Encoding utf8NoBOM
    }finally{if($preflightStarted -and -not $preflight.HasExited){$preflight.WaitForExit()};$preflight.Dispose()}
    $controlReportPath=Join-Path $prepared 'native-ime-control.json'
    $controlBindingPath=Join-Path $prepared 'control-binding.json'
    if([IO.File]::Exists($controlReportPath) -or [IO.File]::Exists($controlBindingPath)){throw 'Fresh native control and binding required; no retry.'}
    $controlStart=[Diagnostics.ProcessStartInfo]::new((Join-Path $manifest.core_directory $manifest.core_executable));$controlStart.UseShellExecute=$false;$controlStart.CreateNoWindow=$true
    $controlStart.ArgumentList.Add('--native-ime-control');$controlStart.ArgumentList.Add($manifestPath);$controlStart.ArgumentList.Add($controlReportPath)
    $control=[Diagnostics.Process]::new();$control.StartInfo=$controlStart;$controlStarted=$false
    try{
        if(-not $control.Start()){throw 'Native IME control failed to launch.'};$controlStarted=$true
        $controlCreation=[uint64]$control.StartTime.ToUniversalTime().ToFileTimeUtc()
        $execution.status='running_direct_native_IME_control';$execution.original_control_pid=$control.Id;$execution.control_creation_filetime=$controlCreation
        $execution|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $executionPath -Encoding utf8NoBOM
        $control.WaitForExit();$execution.original_control_exit_observed=$control.HasExited;$execution.control_exit_code=$control.ExitCode
        Require-Plain $controlReportPath
        $controlReport=Get-Content -LiteralPath $controlReportPath -Raw|ConvertFrom-Json
        $controlSha=(Get-FileHash -LiteralPath $controlReportPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $coreEntryBinding=@($manifest.core_files|Where-Object path -CEQ $manifest.core_executable)
        $runtimeEntryBinding=@($manifest.runtime_files|Where-Object path -CEQ 'msedgewebview2.exe')
        $execution.control_report_path=$controlReportPath;$execution.control_report_sha256=$controlSha
        if($control.ExitCode -ne 0 -or $controlReport.schema_version -ne 1 -or $controlReport.status -cne 'completed_native_ime_control' -or
            $controlReport.native_control -ne $true -or $controlReport.source_snapshot_id -cne $record.source_snapshot_id -or
            $controlReport.manifest_sha256 -cne $preparation.manifest_sha256 -or $controlReport.runtime_version -cne $manifest.runtime_version -or
            $controlReport.original_control_pid -ne $control.Id -or $controlReport.control_creation_filetime -ne $controlCreation -or
            $coreEntryBinding.Count -ne 1 -or $runtimeEntryBinding.Count -ne 1 -or $controlReport.control_executable_sha256 -cne $coreEntryBinding[0].sha256 -or
            $controlReport.runtime_executable_sha256 -cne $runtimeEntryBinding[0].sha256 -or $controlReport.report_write_failed -ne $false -or $controlReport.error_stage){throw 'Same-runtime original native IME control identity or successful terminal report missing; private trial not started.'}
        foreach($field in @('initialization_succeeded','page_succeeded','final_dom_observed','native_composition_observed','normal_browser_exit','original_browser_handle_signaled','all_observed_process_handles_signaled','controller_disposed','window_closed_naturally','cleanup_confirmed')){
            if($controlReport.$field -ne $true){throw "Native control positive evidence missing: $field"}
        }
        foreach($field in @('cdp_input_calls','netonly_logon_calls','new_appcontainer_profiles','new_window_stations','new_desktops','clipboard_api_calls')){
            if($controlReport.$field -ne 0){throw "Native control scope differs: $field"}
        }
        $controlDom=$controlReport.dom_evidence
        if($controlReport.station_name -cne 'WinSta0' -or $controlReport.desktop_name -cne 'Default' -or $controlReport.navigation_start_count -ne 1 -or
            $controlReport.final_environment_process_count -ne 0 -or $controlReport.process_observation_gap -ne $false -or
            $controlReport.normal_browser_exit_pid -ne $controlReport.browser_pid -or @($controlReport.observed_processes).Count -lt 1 -or
            @($controlReport.observed_processes|Where-Object{$_.original_handle_signaled -ne $true -or $null -eq $_.exit_code -or $_.failure}).Count -ne 0 -or
            $controlDom.composition_start -lt 1 -or $controlDom.composition_update -lt 1 -or $controlDom.composition_end -lt 1 -or
            $controlDom.trusted_composition_start -lt 1 -or $controlDom.trusted_composition_update -lt 1 -or $controlDom.trusted_input -lt 1 -or
            $controlDom.composing -ne $false -or $controlDom.expected_hello -ne $true -or $controlDom.value_length -ne 2 -or
            $controlDom.document_instance_sha256 -cne $controlReport.baseline_document_instance_sha256){throw 'Actual direct-native composition or cleanup evidence incomplete.'}
        if($controlDom.trusted_composition_end -eq 0){$expectedEndTrust=$false}
        elseif($controlDom.trusted_composition_end -eq $controlDom.composition_end){$expectedEndTrust=$true}
        else{throw 'Mixed native composition-end trust pattern cannot authorize this comparison.'}
        $binding=[ordered]@{schema_version=1;kind='native_ime_control_original_process_exit_binding';source_snapshot_id=$record.source_snapshot_id;
            manifest_sha256=$preparation.manifest_sha256;control_report_sha256=$controlSha;original_control_pid=$control.Id;control_creation_filetime=$controlCreation;
            original_control_exit_observed=$true;original_control_exit_code=$control.ExitCode;control_executable_sha256=$coreEntryBinding[0].sha256}
        $bindingBytes=[Text.Encoding]::UTF8.GetBytes(($binding|ConvertTo-Json -Depth 5))
        $bindingFile=[IO.File]::Open($controlBindingPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
        try{$bindingFile.Write($bindingBytes,0,$bindingBytes.Length);$bindingFile.Flush($true)}finally{$bindingFile.Dispose()}
        $bindingSha=(Get-FileHash -LiteralPath $controlBindingPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $execution.control_binding_sha256=$bindingSha;$execution.measured_native_end_trusted=$expectedEndTrust
        $execution.native_ime_control='passed_direct_native_control';$execution.status='launching_authorized_private_bridge_comparison'
        $execution|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $executionPath -Encoding utf8NoBOM
    }finally{if($controlStarted -and -not $control.HasExited){$control.WaitForExit()};$control.Dispose()}
    $before=@(Get-ChildItem -LiteralPath $manifest.case_root -Directory -Filter 'session-browser-case-*'|Select-Object -ExpandProperty FullName)
    $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $manifest.ui_directory $manifest.ui_executable));$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.ArgumentList.Add('--execute');$start.ArgumentList.Add($manifestPath);$start.ArgumentList.Add($controlReportPath);$start.ArgumentList.Add($controlSha);$start.ArgumentList.Add($bindingSha)
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    if(-not $process.Start()){throw 'Native viewer failed to start.'};$started=$true;$execution.original_UI_pid=$process.Id
    $execution|ConvertTo-Json -Depth 7|Set-Content -LiteralPath $executionPath -Encoding utf8NoBOM
    $process.WaitForExit();$execution.original_UI_exit_observed=$process.HasExited;$execution.UI_exit_code=$process.ExitCode
    $created=@(Get-ChildItem -LiteralPath $manifest.case_root -Directory -Filter 'session-browser-case-*'|Where-Object{$_.FullName -notin $before})
    if($created.Count -ne 1){throw 'Exactly one retained session-browser case required.'}
    $execution.case_directory=$created[0].FullName;$execution.reports=@()
    $reports=@{}
    foreach($name in @('ui','core','station-host')){
        $path=Join-Path $created[0].FullName ($name+'-report.json');Require-Plain $path
        $report=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json;$reports[$name]=$report
        $execution.reports+=@([ordered]@{kind=$name;path=$path;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant();status=$report.status})
        if($report.schema_version -ne 1 -or $report.nonce -cne $created[0].Name.Substring('session-browser-case-'.Length) -or $report.manifest_sha256 -cne $preparation.manifest_sha256 -or
            $report.source_snapshot_id -cne $record.source_snapshot_id -or $report.failure_stage -or $report.failure -or $report.report_error -or $report.cleanup_proven -ne $true){throw 'Bound report or original-resource cleanup proof incomplete.'}
    }
    if($process.ExitCode -ne 0 -or $reports.ui.status -cne 'completed_native_WinUI_session_browser_prototype_mechanism_only' -or $reports.core.status -cne 'completed_owned_session_browser_prototype_mechanism_only' -or
        $reports.'station-host'.status -cne 'completed_session_station_host_mechanism_only' -or $reports.core.netonly_launch.create_process_with_logon_calls -ne 1 -or
        $reports.core.exact_station_absent -ne $true -or $reports.core.parent_station_unchanged -ne $true -or $reports.core.UI_job_witness_acknowledged -ne $true){throw 'Interactive mechanism trial failed or lacks positive cleanup/identity evidence.'}
    $ui=$reports.ui;$core=$reports.core;$hostReport=$reports.'station-host'
    foreach($field in @('original_core_kernel_pipe_verified','original_host_identity_verified','UI_job_witness_acknowledged','original_core_exit_proven','original_host_exit_proven','independent_job_closed_after_empty','native_input_evidence_complete','core_final_report_bound')){
        if($ui.$field -ne $true){throw "Native viewer positive evidence missing: $field"}
    }
    if($ui.original_core_exit_code -ne 0 -or $ui.original_host_exit_code -ne 0 -or $ui.job_witness.requested_access -ne 4 -or $ui.job_witness.HistoricalFailure -ne $false -or
        $ui.job_witness.last_query_succeeded -ne $true -or $ui.job_witness.active_processes -ne 0 -or @($ui.job_witness.process_ids).Count -ne 0 -or
        $ui.job_witness.closed_after_empty -ne $true -or $ui.input_queue_dropped -ne 0 -or $ui.displayed_frames -lt 2 -or
        $ui.native_pointer_down -lt 1 -or $ui.forwarded_pointer_acks -lt 1 -or $ui.native_key_down -lt 1 -or $ui.native_key_up -lt 1 -or $ui.forwarded_key_acks -lt 1 -or
        $ui.native_composition_started -lt 1 -or $ui.native_composition_changed -lt 1 -or $ui.native_composition_ended -lt 1 -or $ui.forwarded_composition_acks -lt 3){throw 'Native input, original exits or independent QUERY-only Job evidence incomplete.'}
    $dom=$ui.last_DOM_evidence
    if($dom.wasm_result -ne 42 -or $dom.raf_count -lt 60 -or $dom.frame_pixel_sentinel -ne $true -or $dom.different_frame_observed -ne $true -or $dom.captured_frame_count -lt 2 -or
        $dom.dom_expected_hello -ne $true -or $dom.dom_composing -ne $false -or $dom.dom_trusted_composition_start -lt 1 -or $dom.dom_trusted_composition_update -lt 1 -or
        $dom.dom_composition_end -lt 1 -or $dom.dom_trusted_input_count -lt 1 -or $dom.navigation_start_count -ne 1){throw 'Final DOM composition or actual image/WASM/single-document evidence incomplete.'}
    $reference=$ui.native_end_control
    if($reference.ProofPinned -ne $true -or $reference.ProtectedPreparedDirectoryVerified -ne $true -or $reference.ProtectedReportAndBindingVerified -ne $true -or
        $reference.OriginalControlExitBound -ne $true -or $reference.ControlReportSha256 -cne $controlSha -or $reference.BindingSha256 -cne $bindingSha -or
        $reference.SourceSnapshotId -cne $record.source_snapshot_id -or $reference.ManifestSha256 -cne $preparation.manifest_sha256 -or
        $reference.RuntimeVersion -cne $manifest.runtime_version -or $reference.OriginalControlPid -ne $execution.original_control_pid -or
        $reference.OriginalControlCreationFiletime -ne $controlCreation -or $reference.ExpectedEndIsTrusted -ne $expectedEndTrust){throw 'Same frozen native input control proof is missing or changed.'}
    if(($expectedEndTrust -and $dom.dom_trusted_composition_end -ne $dom.dom_composition_end) -or
        (-not $expectedEndTrust -and $dom.dom_trusted_composition_end -ne 0)){throw 'Composition-end trust flags differ from the actual direct-native same-runtime control.'}
    if($ui.native_composition_end_notifications -lt 1 -or $ui.native_composition_settled_commits -lt 1 -or $ui.forwarded_ime_commit_acks -lt 1 -or
        $ui.native_composition_pending_end -ne $false -or $ui.native_composition_range_errors -ne 0 -or
        $ui.last_settled_commit_length -ne $dom.dom_text_length -or $ui.last_ime_commit_ack_length -ne $dom.dom_text_length -or
        $ui.last_settled_commit_sha256 -cne $dom.dom_text_sha256 -or $ui.last_ime_commit_ack_sha256 -cne $dom.dom_text_sha256){throw 'Native End notification, settled commit, actual commit acknowledgement and final DOM do not match.'}
    $commits=@($ui.native_composition_settled|Where-Object decision -CEQ 'commit')
    if($commits.Count -lt 1 -or $commits[-1].initial_buffer_empty -ne $true -or $commits[-1].post_end_text_change_revision -lt 1 -or $commits[-1].settled_length -ne $dom.dom_text_length -or
        $commits[-1].settled_sha256 -cne $dom.dom_text_sha256){throw 'Actual post-End native text revision and final committed text proof incomplete.'}
    $imeTrace=@($ui.ime_rpc_evidence)
    if($ui.ime_rpc_evidence_overflow -ne $false -or $ui.ime_rpc_unrecorded_successful_acks -ne 0 -or $ui.ime_rpc_successful_ack_count -lt 3 -or
        $imeTrace.Count -ne $ui.ime_rpc_successful_ack_count -or $imeTrace.Count -gt 256){throw 'Complete bounded per-IME RPC evidence required.'}
    $priorImeRequest=0
    foreach($entry in $imeTrace){
        $observed=$entry.evidence
        if($entry.command -cnotin 'ime_start','ime_update','ime_set_composition','ime_commit','ime_cancel' -or $entry.request_id -le $priorImeRequest -or
            $observed.kind -cne 'input_result' -or $observed.request_id -ne $entry.request_id -or $observed.nonce -cne $ui.nonce -or $observed.epoch -ne 1 -or
            $observed.success -ne $true -or $null -ne $observed.text -or $null -ne $observed.command -or
            $null -eq $observed.before_dom_owned_text_focused -or $null -eq $observed.before_dom_document_has_focus -or
            $observed.document_instance_sha256 -notmatch '^[a-f0-9]{64}$' -or $observed.document_instance_sha256 -cne $dom.document_instance_sha256){throw 'Per-IME reply identity, same-document focus metadata or plaintext omission proof incomplete.'}
        $priorImeRequest=$entry.request_id
    }
    if($ui.core_report_sha256 -cne ($execution.reports|Where-Object kind -CEQ 'core').sha256 -or
        $core.station_host_report_sha256 -cne ($execution.reports|Where-Object kind -CEQ 'station-host').sha256 -or
        $core.station_host_final_report_bound -ne $true -or $core.station_host_cleanup_reported -ne $true -or $core.station_host_success_reported -ne $true){throw 'Final report byte chain differs.'}
    $launch=$core.netonly_launch;$parent=$launch.parent_identity;$child=$launch.child_identity
    if($launch.token_admission_passed -ne $true -or $launch.original_image_verified -ne $true -or $launch.original_process_signaled -ne $true -or $launch.original_thread_signaled -ne $true -or $launch.exit_code -ne 0 -or
        $parent.user_sid_sha256 -cne $child.user_sid_sha256 -or $parent.session_id -ne $child.session_id -or $parent.integrity_rid -ne 8192 -or $child.integrity_rid -ne 8192 -or
        $parent.elevated -ne $false -or $child.elevated -ne $false -or $parent.appcontainer -ne $false -or $child.appcontainer -ne $false -or $parent.authentication_id -ceq $child.authentication_id -or
        @($child.enabled_privileges|Where-Object{$_ -cnotin $parent.enabled_privileges}).Count -ne 0 -or @($child.present_privileges|Where-Object{$_ -cnotin $parent.present_privileges}).Count -ne 0){throw 'Ordinary NETONLY original-token/no-privilege-gain proof incomplete.'}
    foreach($report in @($ui,$core,$hostReport)){if($report.UAC_requests -ne 0 -or $report.force_termination_calls -ne 0 -or $report.lsa_cleanup -cne 'not_claimed' -or $report.production_gate -cne 'blocked_pending_G1_and_product_integration'){throw 'Prototype scope differs.'}}
    $owned=$hostReport.owned_context;$browser=$hostReport.browser_child;$job=$hostReport.job
    foreach($field in @('station_created','station_verified','desktop_verified','profile_created','original_profile_path_captured','profile_path_absent','profile_sid_absent','original_station_restored','desktop_closed','desktop_absent','station_closed','cleanup_proven')){
        if($owned.$field -ne $true){throw "Owned station/profile positive evidence missing: $field"}
    }
    if($owned.station_package_mask -ne 0x20023 -or $owned.desktop_package_mask -ne 0x87 -or $owned.profile_delete_hresult -ne 0 -or $owned.profile_path_post_error -notin 2,3 -or
        $owned.profile_sid_post_hresult -notin -2147024894,-2147024893 -or $owned.cleanup.any_failure -ne $false -or $browser.normal_acceptance -ne $true -or $browser.resource_cleanup_proven -ne $true -or
        $browser.stdout_eof -ne $true -or $browser.normal_browser_exit -ne $true -or $browser.final_sdk_empty -ne $true -or $browser.retired -ne $true -or
        $job.atomic_launch_attribute_attached -ne $true -or $job.Empty -ne $true -or $job.limit_flags -ne 0 -or $job.query_failure_seen -ne $false -or $job.membership_failure_seen -ne $false -or
        $job.original_handle_closed_after_empty -ne $true -or $core.job_witness.granted_access_requested -ne 4 -or $core.job_witness.closed_after_empty -ne $true -or $core.job_witness.groups_retired -ne $true){throw 'Exact owned ACL/profile/normal browser/Job retirement evidence incomplete.'}
    foreach($audit in @($browser.audit,$core.job_witness.audit)){
        if($audit.ObservationGap -ne $false -or @($audit.Held).Count -lt 2){throw 'Independent observed process coverage incomplete.'}
        foreach($held in $audit.Held){
            if($held.failure -or $held.original_handle_signaled -ne $true -or $null -eq $held.exit_code -or $held.token.is_appcontainer -ne 1 -or $held.token.expected_package_sid_matches -ne $true -or
                $held.token.package_sid_sha256 -cne $owned.package_sid_sha256 -or $held.token.integrity_rid -gt 4096 -or $held.token.capability_count -ne 0){throw 'Held original process/token/exit evidence incomplete.'}
        }
    }
    if(@($ui.initial_frames).Count -ne 2 -or $ui.initial_frames[0].sha256 -ceq $ui.initial_frames[1].sha256){throw 'Two actual initial frame artifacts required.'}
    if($ui.initial_frames[0].file -cne 'frame-initial-1.png' -or $ui.initial_frames[1].file -cne 'frame-initial-2.png'){throw 'Fixed initial frame names required.'}
    $frameBindings=@($ui.initial_frames|ForEach-Object{[ordered]@{path=$_.file;bytes=$_.bytes;sha256=$_.sha256}})
    Verify-Files $created[0].FullName $frameBindings $false
    $execution.status='completed_owned_session_browser_prototype_mechanism_only';$execution.physical_input_acceptance='requires_independent_review_of_actual_UI_events';$execution.production_gate='blocked'
}catch{$execution.status='failed';$execution.failure_type=$_.Exception.GetType().Name;throw}
finally{
    if($started -and -not $process.HasExited){$process.WaitForExit()}
    if($process){$process.Dispose()};$execution.completed_utc=[DateTimeOffset]::UtcNow
    $execution|ConvertTo-Json -Depth 8|Set-Content -LiteralPath $executionPath -Encoding utf8NoBOM
    Write-Host "Session-browser evidence retained: $prepared"
}
