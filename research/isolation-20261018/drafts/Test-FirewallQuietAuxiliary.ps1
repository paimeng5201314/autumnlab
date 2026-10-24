[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('mdns','nonproxied')][string]$Variant)
$ErrorActionPreference='Stop'
$workspace=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../..'))
$sourceRuntime=Join-Path $workspace '.tools/webview2/154.0.4258.53/Microsoft.WebView2.FixedVersionRuntime.154.0.4258.53.x64'
$probeExe=Join-Path $PSScriptRoot 'firewall-auxiliary-probe-original/AutumnOS.Isolation.Probe.exe'
$expectedProbeDllHash='2c837da7e37544f2181a33efb70a899f5666104e2fa2817149316727ce5e4269'
if((Get-FileHash -LiteralPath ([IO.Path]::ChangeExtension($probeExe,'dll')) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expectedProbeDllHash){throw 'Frozen auxiliary probe DLL identity differs.'}
$testRoot=Join-Path $workspace ('artifacts/firewall-quiet-'+$Variant+'-'+[DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss'))
$runtime=Join-Path $testRoot 'runtime'
$arguments=if($Variant -eq 'mdns'){'--disable-features=WebRtcHideLocalIpsWithMdns'}else{'--force-webrtc-ip-handling-policy=disable_non_proxied_udp'}
if(@(Get-Process -Name AutumnOS,AutumnOS.Client,AutumnOS.Isolation.Probe -ErrorAction SilentlyContinue).Count){throw 'Another product or probe is active; no process was touched.'}
if(Test-Path -LiteralPath $testRoot){throw 'Fresh test root required.'}
if(@(Get-ChildItem -LiteralPath $sourceRuntime -Recurse -Force | Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Runtime copy must contain only plain files/directories.'}
New-Item -ItemType Directory -Path $testRoot,$runtime | Out-Null
$sourceExe=Join-Path $sourceRuntime 'msedgewebview2.exe'
$sourceHash=(Get-FileHash -LiteralPath $sourceExe -Algorithm SHA256).Hash.ToLowerInvariant()
Get-ChildItem -LiteralPath $sourceRuntime -Force | Copy-Item -Destination $runtime -Recurse
$runtimeExe=Join-Path $runtime 'msedgewebview2.exe'
$copyHash=(Get-FileHash -LiteralPath $runtimeExe -Algorithm SHA256).Hash.ToLowerInvariant()
if($copyHash -cne $sourceHash){throw 'Copied fixed Runtime executable hash differs.'}
function Read-TestRuntimeFirewallRules {
    @(Get-NetFirewallApplicationFilter -PolicyStore ActiveStore | Where-Object {[string]::Equals($_.Program,$runtimeExe,[StringComparison]::OrdinalIgnoreCase)} | ForEach-Object {
        $program=$_.Program
        $_ | Get-NetFirewallRule | ForEach-Object {[ordered]@{program=$program;name=$_.Name;action=$_.Action.ToString();direction=$_.Direction.ToString();enabled=$_.Enabled.ToString();profile=$_.Profile.ToString()}}
    })
}
$beforeRules=@(Read-TestRuntimeFirewallRules)
if($beforeRules.Count){throw 'Fresh runtime path unexpectedly already has explicit firewall rules.'}
$baselinePickerIds=@(Get-Process -Name PickerHost -ErrorAction SilentlyContinue | ForEach-Object { $_.Id.ToString()+':'+$_.StartTime.ToUniversalTime().Ticks })
$nativeReport=Join-Path $testRoot 'runtime-ab.json'
$startInfo=[Diagnostics.ProcessStartInfo]::new($probeExe)
$startInfo.UseShellExecute=$false
$startInfo.CreateNoWindow=$true
$startInfo.WorkingDirectory=$testRoot
foreach($argument in @('--runtime',$runtime,'--report',$nativeReport,'--mode','runtime-ab')){$startInfo.ArgumentList.Add($argument)}
# This process-local override is used only in the disposable probe. It does not
# modify the caller environment, registry, production source or global policy.
$startInfo.Environment['WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS']=$arguments
$startInfo.Environment['DOTNET_ROOT']=Join-Path $workspace '.tools/dotnet'
$startInfo.Environment['DOTNET_ROOT_X64']=Join-Path $workspace '.tools/dotnet'
$startInfo.RedirectStandardOutput=$true;$startInfo.RedirectStandardError=$true
$started=[DateTimeOffset]::UtcNow
$process=[Diagnostics.Process]::Start($startInfo)
$null=$process.Handle
$heldStart=$process.StartTime.ToUniversalTime()
$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
$seen=[Collections.Generic.Dictionary[string,object]]::new()
$captureAttempted=[Collections.Generic.HashSet[string]]::new()
$polls=0;$maxGapMs=0.0;$lastPoll=[DateTimeOffset]::UtcNow;$closeRequested=$false
try {
    while(-not $process.HasExited){
        $now=[DateTimeOffset]::UtcNow
        $maxGapMs=[Math]::Max($maxGapMs,($now-$lastPoll).TotalMilliseconds);$lastPoll=$now;$polls++
        foreach($picker in @(Get-Process -Name PickerHost -ErrorAction SilentlyContinue)){
            try{
                $key=$picker.Id.ToString()+':'+$picker.StartTime.ToUniversalTime().Ticks
                if($baselinePickerIds -contains $key -or $picker.StartTime.ToUniversalTime() -lt $heldStart){continue}
                if(-not $seen.ContainsKey($key)){$seen.Add($key,[ordered]@{pid=$picker.Id;start_utc=$picker.StartTime.ToUniversalTime().ToString('o');first_seen_utc=$now.ToString('o');title=$picker.MainWindowTitle;path=$picker.Path;snapshot=$null})}
                if($picker.MainWindowTitle -in @('Windows 安全中心','Windows Security') -and $captureAttempted.Add($key)){
                    $capturePath=Join-Path $testRoot ('security-picker-'+$picker.Id+'.json')
                    try{$null=& (Join-Path $PSScriptRoot 'Read-SecurityPickerText.ps1') -PickerProcessId $picker.Id -ExpectedStartTime ([DateTimeOffset]$picker.StartTime) -ReportPath $capturePath -CaptureWindowIfNoStaticText;$seen[$key].snapshot=$capturePath}
                    catch{$seen[$key].snapshot_error=$_.Exception.Message}
                }
            }finally{$picker.Dispose()}
        }
        if(($now-$started).TotalSeconds -gt 60){
            if(-not $closeRequested){$process.Refresh();if($process.StartTime.ToUniversalTime() -ne $heldStart -or $process.MainModule.FileName -ine $probeExe){throw 'Held probe identity changed.'};$closeRequested=$process.CloseMainWindow()}
            if(($now-$started).TotalSeconds -gt 70){throw 'Owned probe did not close normally; no forced termination was performed.'}
        }
        Start-Sleep -Milliseconds 75
        $process.Refresh()
    }
    $process.WaitForExit()
    $native=if(Test-Path -LiteralPath $nativeReport){Get-Content -LiteralPath $nativeReport -Raw | ConvertFrom-Json}else{$null}
    $summary=[ordered]@{
        schema='autumn-firewall-quiet-auxiliary-v1';variant=$Variant;started_utc=$started.ToString('o');finished_utc=[DateTimeOffset]::UtcNow.ToString('o')
        scope='UI initialization side effect only; no network-isolation conclusion';production_source_modified=$false;firewall_rules_modified_by_driver=$false;system_dialog_actions='none'
        runtime_source=$sourceRuntime;runtime_copy=$runtime;source_exe_sha256=$sourceHash;copied_exe_sha256=$copyHash;hash_match=($sourceHash -ceq $copyHash)
        probe_executable=$probeExe;probe_executable_sha256=(Get-FileHash -LiteralPath $probeExe -Algorithm SHA256).Hash.ToLowerInvariant()
        probe_pid=$process.Id;exit_code=$process.ExitCode;process_local_additional_browser_arguments=$arguments
        native_report=$nativeReport;native_report_status=$native.status
        native_report_metadata_override='RuntimePolicyWindow hardcodes browser_arguments=none; this wrapper records the actual per-process override. Do not cite the native field as the effective arguments.'
        cases=@($native.cases | Select-Object case,status,runtime,validator_produced_proof,proof_bound_to_instance_and_origin)
        new_picker_processes=@($seen.Values);new_picker_process_count=$seen.Count;poll_count=$polls;maximum_poll_gap_ms=$maxGapMs
        picker_limit='Polling may miss shorter-lived processes. Temporal association alone does not identify the target; inspect captured title/window evidence.'
        explicit_runtime_firewall_rules_before=$beforeRules;explicit_runtime_firewall_rules_after=@(Read-TestRuntimeFirewallRules)
        stdout=$stdout.Result;stderr=$stderr.Result;normal_close_requested=$closeRequested
    }
    $summaryJson=$summary | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText((Join-Path $testRoot 'auxiliary-observation.json'),$summaryJson,[Text.UTF8Encoding]::new($false))
    $summaryJson
}finally{$process.Dispose()}
