#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$ReportDirectory
)

$ErrorActionPreference = 'Stop'
. "$PSScriptRoot/Smoke-NativeAppInput.ps1"
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
$entryRoot = [IO.Path]::GetDirectoryName($ExecutablePath)
$entryPrefix = $entryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($ReportDirectory.Equals($entryRoot, [StringComparison]::OrdinalIgnoreCase) -or
    $ReportDirectory.StartsWith($entryPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'ReportDirectory must be outside the delivered application directory.'
}
if (Test-Path -LiteralPath $ReportDirectory) { throw 'ReportDirectory already exists. Existing evidence will not be overwritten.' }
New-Item -ItemType Directory -Path $ReportDirectory | Out-Null
$reportPath = Join-Path $ReportDirectory 'delivered-launcher.json'
$dataPath = Join-Path $entryRoot 'AutumnOS_Data'
$launcherLog = Join-Path $dataPath 'Logs/launcher-events.jsonl'
$checks = [Collections.Generic.List[object]]::new()
$launches = [Collections.Generic.List[object]]::new()
$ownedLaunches = [Collections.Generic.List[object]]::new()
$primary = $null
$baselineLogLength = 0L
$report = [ordered]@{
    schema_version=1; task_id='T02'; checkpoint='actual_delivered_executable_launcher'
    status='failed'; started_utc=[DateTimeOffset]::UtcNow.ToString('o')
    executable=$ExecutablePath; report_directory=$ReportDirectory; data_directory=$dataPath
    environment=[Environment]::OSVersion.VersionString; session_id=$null; is_elevated=$null
    binary_before=$null; binary_after=$null; data_before=$null; data_after=$null; data_changes=@()
    data_initialization='not_run'; data_cleanup='never_requested_or_performed'
    existing_processes=@(); launches=$launches; checks=$checks; launcher_events=@(); remaining_processes=@()
    native_window='not_run'; repeat_launch='not_run'; different_working_directory='not_run'; normal_exit='not_run'
    foreground_activation='not_run'; screenshots='not_run'; onboarding_completion='not_run'; game_lifecycle='not_run'
    windows10_22h2='not_run'; elevated_process_matrix='not_run'
    t05_updates=[ordered]@{status='not_run';condition='Client entry or discoverable SettingsNavUpdates';network_check='not_required_not_asserted';identity=$null;trust=$null}
    execution_policy='Run the supplied real EXE in place; no copy, forced termination or data deletion. Conditional T05 assertions use only the owned native window to complete onboarding and open Settings > Updates. Only the held self-started primary or its verified exact-path business child may receive CloseMainWindow.'
}

function Write-NewEvidence([string]$Path, [string]$Text) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try { $bytes = [Text.UTF8Encoding]::new($false).GetBytes($Text); $stream.Write($bytes, 0, $bytes.Length) }
    finally { $stream.Dispose() }
}

function Add-DeliveredCheck([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $checks.Add([ordered]@{name=$Name; status=$(if ($Passed) {'passed'} else {'failed'}); detail=$Detail})
    if (-not $Passed) { throw "Delivered launcher check failed: $Name. $Detail" }
    Write-Host "PASS $Name"
}

function Wait-DeliveredCondition([scriptblock]$Condition, [string]$Description, [int]$Seconds = 30) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 150
    } while ($timer.Elapsed.TotalSeconds -lt $Seconds)
    throw "Timed out waiting for $Description after $Seconds seconds."
}

function Assert-DeliveredPlainPath([string]$Path) {
    $current = [IO.Path]::GetFullPath($Path)
    while ($current) {
        try { $attributes = [IO.File]::GetAttributes($current) }
        catch [IO.FileNotFoundException] { $current = [IO.Path]::GetDirectoryName($current); continue }
        catch [IO.DirectoryNotFoundException] { $current = [IO.Path]::GetDirectoryName($current); continue }
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Evidence refuses a reparse-point path: $current" }
        $current = [IO.Path]::GetDirectoryName($current)
    }
}

function Get-DeliveredBinaryEvidence {
    Assert-DeliveredPlainPath $ExecutablePath
    $contractPath = Join-Path $entryRoot 'AutumnOS.Contracts.dll'
    Assert-DeliveredPlainPath $contractPath
    # Read bytes so the verification host never keeps the installed DLL mapped during a later
    # update/uninstall in this same PowerShell process. AssemblyLoadContext.Unload is asynchronous.
    $assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($contractPath))
        $metadata = [ordered]@{}
        foreach ($attribute in $assembly.GetCustomAttributesData()) {
            if ($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute') {
                $metadata[[string]$attribute.ConstructorArguments[0].Value] = $attribute.ConstructorArguments[1].Value
            } elseif ($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyProductAttribute') {
                $metadata.ProductName = $attribute.ConstructorArguments[0].Value
            } elseif ($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyInformationalVersionAttribute') {
                $metadata.ProductVersion = $attribute.ConstructorArguments[0].Value
            }
        }
        return [ordered]@{
            executable_sha256=(Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
            business_executable_sha256=$(if (Test-Path -LiteralPath (Join-Path $entryRoot 'AutumnOS.Client.exe') -PathType Leaf) { (Get-FileHash -LiteralPath (Join-Path $entryRoot 'AutumnOS.Client.exe') -Algorithm SHA256).Hash.ToLowerInvariant() } else { $null })
            contracts_path=$contractPath
            contracts_sha256=(Get-FileHash -LiteralPath $contractPath -Algorithm SHA256).Hash.ToLowerInvariant()
            metadata=$metadata
        }
}

function Get-DeliveredDataManifest {
    Assert-DeliveredPlainPath $dataPath
    if (-not [IO.Directory]::Exists($dataPath)) {
        if ([IO.File]::Exists($dataPath)) { throw 'AutumnOS_Data is a file instead of a directory.' }
        return [ordered]@{exists=$false; file_count=0; manifest_sha256='absent'; entries=@()}
    }
    $entries = [Collections.Generic.List[object]]::new()
    $pending = [Collections.Generic.Queue[string]]::new()
    $pending.Enqueue($dataPath)
    while ($pending.Count) {
        $directory = $pending.Dequeue()
        foreach ($item in (Get-ChildItem -LiteralPath $directory -Force)) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Data manifest refuses a reparse point: $($item.FullName)" }
            $relative = [IO.Path]::GetRelativePath($dataPath, $item.FullName)
            if ($item.PSIsContainer) {
                $entries.Add([ordered]@{path=$relative; kind='directory'})
                $pending.Enqueue($item.FullName)
            } else {
                $entries.Add([ordered]@{path=$relative; kind='file'; length=$item.Length; sha256=(Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()})
            }
        }
    }
    $sorted = @($entries | Sort-Object { $_.path })
    $manifestText = ConvertTo-Json -InputObject $sorted -Depth 5 -Compress
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($manifestText))
    return [ordered]@{exists=$true; file_count=@($sorted | Where-Object { $_.kind -eq 'file' }).Count; manifest_sha256=[Convert]::ToHexString($hash).ToLowerInvariant(); entries=$sorted}
}

function Read-DeliveredFreshEvents {
    Assert-DeliveredPlainPath $launcherLog
    if (-not [IO.File]::Exists($launcherLog)) { return @() }
    $stream = [IO.FileStream]::new($launcherLog, [IO.FileMode]::Open, [IO.FileAccess]::Read, ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        if ($stream.Length -lt $baselineLogLength) { throw 'Launcher evidence log was truncated during this run.' }
        $count = $stream.Length - $baselineLogLength
        if ($count -gt 2MB) { throw 'Fresh launcher evidence exceeds the bounded read size.' }
        $null = $stream.Seek($baselineLogLength, [IO.SeekOrigin]::Begin)
        $bytes = [byte[]]::new([int]$count)
        $offset = 0
        while ($offset -lt $bytes.Length) {
            $read = $stream.Read($bytes, $offset, $bytes.Length - $offset)
            if ($read -eq 0) { throw 'Launcher evidence changed during reading.' }
            $offset += $read
        }
        $lines = [Text.UTF8Encoding]::new($false, $true).GetString($bytes).Split("`n")
        # An incomplete final append is never evidence until its terminating newline arrives.
        return @($lines | Select-Object -SkipLast 1 | Where-Object { $_.Trim() } | ForEach-Object { $_ | ConvertFrom-Json })
    } finally { $stream.Dispose() }
}

function Start-DeliveredProcess([string]$Role, [string]$WorkingDirectory) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName=$ExecutablePath; $start.WorkingDirectory=$WorkingDirectory
    $start.UseShellExecute=$false; $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Normal
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $process = [Diagnostics.Process]::Start($start)
    if (-not $process) { throw 'The actual executable did not return an owned process handle.' }
    $record = [ordered]@{role=$Role; process_id=$process.Id; entry_process_id=$process.Id; business_process_id=$null; executable=$ExecutablePath; business_executable=$null; working_directory=$WorkingDirectory; start_utc=$null; business_start_utc=$null; exit_code=$null; entry_exit_code=$null; stdout='pending'; stderr='pending'; window_handle=0; window_title=$null; responding=$null}
    $launch = [pscustomobject]@{Process=$process; EntryProcess=$process; Record=$record; StartedTicks=$null; EntryStartedTicks=$null; IsBootstrap=$false; OutTask=$null; ErrorTask=$null}
    $ownedLaunches.Add($launch); $launches.Add($record)
    if ($Role -eq 'primary') { $script:primary=$launch }
    $launch.OutTask=$process.StandardOutput.ReadToEndAsync(); $launch.ErrorTask=$process.StandardError.ReadToEndAsync()
    $launch.StartedTicks=$process.StartTime.ToUniversalTime().Ticks
    $launch.EntryStartedTicks=$launch.StartedTicks
    $record.start_utc=$process.StartTime.ToUniversalTime().ToString('o')
    $launch.IsBootstrap=Test-Path -LiteralPath (Join-Path $entryRoot 'AutumnOS.Client.exe') -PathType Leaf
    if ($Role -eq 'primary') {
        $business=Resolve-SmokeBusinessProcess -EntryProcess $process -ExecutablePath $ExecutablePath -TimeoutSeconds 30
        $launch.Process=$business; $launch.StartedTicks=$business.StartTime.ToUniversalTime().Ticks
        $record.process_id=$business.Id; $record.business_process_id=$business.Id
        $record.business_executable=$business.MainModule.FileName
        $record.business_start_utc=$business.StartTime.ToUniversalTime().ToString('o')
    }
    return $launch
}

function Assert-DeliveredOwnedPrimary {
    $process = $primary.Process
    $expectedPath=if($primary.IsBootstrap){Join-Path $entryRoot 'AutumnOS.Client.exe'}else{$ExecutablePath}
    if ($process.HasExited -or $null -eq $primary.StartedTicks -or
        $process.StartTime.ToUniversalTime().Ticks -ne $primary.StartedTicks -or
        -not [string]::Equals($process.MainModule.FileName, $expectedPath, [StringComparison]::OrdinalIgnoreCase) -or
        $process.SessionId -ne $report.session_id) {
        throw 'Refusing normal close: held primary identity, real executable path or session no longer matches.'
    }
    if ($primary.IsBootstrap -and ($primary.EntryProcess.HasExited -or $primary.EntryProcess.StartTime.ToUniversalTime().Ticks -ne $primary.EntryStartedTicks -or
        -not [string]::Equals($primary.EntryProcess.MainModule.FileName,$ExecutablePath,[StringComparison]::OrdinalIgnoreCase))) {
        throw 'Refusing normal close: the verified bootstrap identity is no longer live.'
    }
}

function Test-DeliveredUpdatesPage {
    $runtime=Join-Path (Split-Path $PSScriptRoot -Parent) '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll')){
        if(-not(Test-Path -LiteralPath (Join-Path $runtime $name))){throw "Missing local UI Automation runtime: $name"}
        Add-Type -Path (Join-Path $runtime $name)
    }
    Assert-DeliveredOwnedPrimary
    $ui=[Windows.Automation.AutomationElement]::FromHandle($primary.Process.MainWindowHandle)
    if($ui.Current.ProcessId -ne $primary.Process.Id){throw 'T05 UI root does not belong to the held business process.'}
    function Find-DeliveredUi([string]$Id){
        $element=$ui.FindFirst([Windows.Automation.TreeScope]::Descendants,
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id))
        if($element -and $element.Current.ProcessId -eq $primary.Process.Id){return $element}
        return $null
    }
    function Invoke-DeliveredUi([string]$Id){
        Assert-DeliveredOwnedPrimary
        $element=Wait-DeliveredCondition {
            $candidate=Find-DeliveredUi $Id
            if($candidate -and $candidate.Current.IsEnabled -and -not $candidate.Current.IsOffscreen){return $candidate}
            return $null
        } "visible enabled T05 button $Id" 45
        $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    }
    $nav=Find-DeliveredUi 'SettingsNavUpdates'
    if(-not $primary.IsBootstrap -and -not $nav){
        $report.t05_updates.status='not_run_legacy_entry_without_discoverable_update_surface'
        return
    }
    $report.t05_updates.condition=if($primary.IsBootstrap){'verified AutumnOS.exe bootstrap -> same-root AutumnOS.Client.exe'}else{'legacy direct entry exposing SettingsNavUpdates'}
    $report.t05_updates.status='failed'
    $completedOnboarding=$false
    $onboardingTimer=[Diagnostics.Stopwatch]::StartNew()
    while($true){
        if($onboardingTimer.Elapsed.TotalSeconds -gt 90){throw 'T05 native onboarding did not finish within 90 seconds.'}
        $next=Wait-DeliveredCondition {
            foreach($candidateId in @('HelloNextButton','BrandNextButton','PrepareDesktopButton','SettingsButton','SettingsNavUpdates')){
                $candidate=Find-DeliveredUi $candidateId
                if($candidate -and $candidate.Current.IsEnabled -and -not $candidate.Current.IsOffscreen){return $candidateId}
            }
            return $null
        } 'T05 onboarding or desktop' 45
        if($next -in @('SettingsButton','SettingsNavUpdates')){break}
        Invoke-DeliveredUi $next;$completedOnboarding=$true
        $null=Wait-DeliveredCondition {
            $old=Find-DeliveredUi $next
            return -not $old -or $old.Current.IsOffscreen -or -not $old.Current.IsEnabled
        } "onboarding transition after $next" 45
    }
    if($completedOnboarding){$report.onboarding_completion='passed_T05_real_native_UI'}
    $nav=Find-DeliveredUi 'SettingsNavUpdates'
    if(-not $nav -or $nav.Current.IsOffscreen){Invoke-DeliveredUi 'SettingsButton'}
    $nav=Wait-DeliveredCondition {
        $candidate=Find-DeliveredUi 'SettingsNavUpdates'
        if($candidate -and $candidate.Current.IsEnabled -and -not $candidate.Current.IsOffscreen){return $candidate}
        return $null
    } 'visible system update navigation' 45
    $toggle=$nav.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::On){$toggle.Toggle()}
    $identity=Wait-DeliveredCondition {
        foreach($element in $ui.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)){
            if($element.Current.ProcessId -eq $primary.Process.Id -and $element.Current.Name.StartsWith('当前版本 ') -and
                $element.Current.Name.Contains("`n构建 ")){return $element.Current.Name}
        }
        return $null
    } 'rendered current version and build on system update page' 45
    $lines=@($identity -split '\r?\n' | ForEach-Object {$_.Trim()})
    $report.t05_updates.identity=$identity
    $report.t05_updates.expected_version=$report.binary_before.metadata.ProductVersion
    $report.t05_updates.expected_build=$report.binary_before.metadata.BuildId
    Add-DeliveredCheck 't05_updates_current_version_matches_actual_contracts' ($lines -contains ('当前版本 '+$report.binary_before.metadata.ProductVersion))
    Add-DeliveredCheck 't05_updates_build_matches_actual_contracts' ($lines -contains ('构建 '+$report.binary_before.metadata.BuildId))
    $updateAssembly=Join-Path $entryRoot 'AutumnOS.Update.dll';Assert-DeliveredPlainPath $updateAssembly
    $assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes($updateAssembly))
    $testBuild=$assembly.GetManifestResourceNames() -contains 'AutumnOS.Update.TestFeed.txt'
    $report.t05_updates.is_test_build=$testBuild
    $status=Wait-DeliveredCondition {Find-DeliveredUi 'UpdateStatus'} 'system update state'
    $report.t05_updates.trust=$status.Current.Name
    if($testBuild){
        Add-DeliveredCheck 't05_test_build_explicitly_labels_test_trust_and_isolated_source' ($identity.Contains('仅本地更新演练 · 测试信任根 / 隔离源'))
    }else{
        Add-DeliveredCheck 't05_normal_build_reports_missing_production_trust_root' ($status.Current.Name.Contains('自动安装尚未配置：缺少生产可信公钥，不能安装未签名更新。') -and -not $identity.Contains('测试信任根'))
    }
    $report.t05_updates.status='passed'
}

try {
    if (-not $IsWindows) { throw 'Delivered launcher verification requires Windows.' }
    if ([IO.Path]::GetFileName($ExecutablePath) -ne 'AutumnOS.exe' -or -not [IO.File]::Exists($ExecutablePath)) { throw 'ExecutablePath must be the real delivered AutumnOS.exe.' }
    Assert-DeliveredPlainPath $ReportDirectory
    $report.session_id=[Diagnostics.Process]::GetCurrentProcess().SessionId
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    $report.is_elevated=$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    Add-DeliveredCheck 'standard_user_token' (-not $report.is_elevated)
    $existing=@(Get-Process -Name AutumnOS,AutumnOS.Client -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq $report.session_id })
    if ($existing.Count) {
        $report.existing_processes=@($existing | Select-Object Id,SessionId,MainWindowHandle)
        $report.status='blocked_existing_user_instance'
        throw 'An AutumnOS process already exists in this session. It was not activated or closed; exit it normally before rerunning.'
    }
    $report.binary_before=Get-DeliveredBinaryEvidence
    $report.data_before=Get-DeliveredDataManifest
    $metadata=$report.binary_before.metadata
    Add-DeliveredCheck 'assembly_has_build_and_source_identity' (-not [string]::IsNullOrWhiteSpace($metadata.BuildId) -and -not [string]::IsNullOrWhiteSpace($metadata.SourceSnapshotId))
    Write-NewEvidence (Join-Path $ReportDirectory 'data-before.json') (ConvertTo-Json -InputObject $report.data_before -Depth 8)
    $baselineLogLength=if ([IO.File]::Exists($launcherLog)) {(Get-Item -LiteralPath $launcherLog).Length} else {0L}
    $report.launcher_log_baseline_bytes=$baselineLogLength
    if (-not ('AutumnDeliveredLauncher.Native' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace AutumnDeliveredLauncher {
    public static class Native {
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindow(IntPtr window);
    }
}
'@
    }
    $primary=Start-DeliveredProcess 'primary' $entryRoot
    $window=Wait-DeliveredCondition {
        $primary.Process.Refresh()
        if ($primary.Process.HasExited) { throw "Actual delivered primary exited before its window, code $($primary.Process.ExitCode)." }
        $handle=$primary.Process.MainWindowHandle
        if ($handle -ne [IntPtr]::Zero -and $primary.Process.Responding) { return $handle }
        return $null
    } 'the real delivered MainWindow and a responding process'
    Assert-DeliveredOwnedPrimary
    [uint32]$windowProcess=0
    $null=[AutumnDeliveredLauncher.Native]::GetWindowThreadProcessId($window,[ref]$windowProcess)
    Add-DeliveredCheck 'actual_exe_owns_responding_native_window' ($windowProcess -eq $primary.Process.Id -and [AutumnDeliveredLauncher.Native]::IsWindow($window))
    $primary.Record.window_handle=$window.ToInt64(); $primary.Record.window_title=$primary.Process.MainWindowTitle; $primary.Record.responding=$primary.Process.Responding
    $primary.Record.observed_executable=$primary.Process.MainModule.FileName; $primary.Record.observed_session_id=$primary.Process.SessionId
    $ready=Wait-DeliveredCondition {
        $records=@(Read-DeliveredFreshEvents | Where-Object { $_.eventName -eq 'window_ready' -and $_.primaryPid -eq $primary.Process.Id })
        if ($records.Count) { return $records[-1] }
        return $null
    } 'fresh window_ready evidence from the actual delivered EXE'
    Add-DeliveredCheck 'window_ready_matches_process_window_and_build' ([long]$ready.windowHandle -eq $window.ToInt64() -and $ready.buildId -eq $metadata.BuildId -and $ready.sourceSnapshotId -eq $metadata.SourceSnapshotId)
    $report.native_window='passed'
    $secondaryCwd=Join-Path $ReportDirectory 'different-working-directory'
    New-Item -ItemType Directory -Path $secondaryCwd | Out-Null
    $secondary=Start-DeliveredProcess 'secondary' $secondaryCwd
    $null=Wait-DeliveredCondition {
        $secondary.Process.Refresh()
        if ($secondary.Process.HasExited) { return $true }
        if ($secondary.Process.MainWindowHandle -ne [IntPtr]::Zero) { throw 'The secondary delivered EXE created a second native window.' }
        return $false
    } 'the secondary real EXE to exit through forwarding' 25
    $secondary.Record.exit_code=$secondary.Process.ExitCode
    $secondaryError=$secondary.ErrorTask.GetAwaiter().GetResult()
    $secondaryBusinessId=$secondary.Process.Id
    if($secondary.IsBootstrap){
        # A forwarder can exit before a CIM poll. Its parent's inherited stderr is held by this
        # launch only; bind its reported child to that parent and interval. Never operate on this PID.
        $binding=[regex]::Matches($secondaryError,('AUTUMNOS_BOOTSTRAP\s+parent_pid='+$secondary.EntryProcess.Id+'\s+child_pid=(\d+)\s+child_start_utc_ticks=(\d+)\b'))
        if($binding.Count -eq 0){$binding=[regex]::Matches($secondaryError,('AUTUMNOS_BOOTSTRAP\s+role=child\s+pid='+$secondary.EntryProcess.Id+'\s+child=(\d+)\s+startTicks=(\d+)\b'))}
        Add-DeliveredCheck 'secondary_bootstrap_reports_one_child_bound_to_launch_interval' ($binding.Count -eq 1 -and
            [long]$binding[0].Groups[2].Value -ge $secondary.EntryStartedTicks -and
            [long]$binding[0].Groups[2].Value -le $secondary.EntryProcess.ExitTime.ToUniversalTime().Ticks)
        $secondaryBusinessId=[int]$binding[0].Groups[1].Value
        $secondary.Record.business_process_id=$secondaryBusinessId
        $secondary.Record.business_start_utc=[DateTime]::new([long]$binding[0].Groups[2].Value,[DateTimeKind]::Utc).ToString('o')
        $secondary.Record.business_executable=Join-Path $entryRoot 'AutumnOS.Client.exe'
    }
    Add-DeliveredCheck 'secondary_exits_zero_with_own_forwarded_diagnostic' ($secondary.Process.ExitCode -eq 0 -and $secondaryError -match ('AUTUMNOS_LAUNCHER\s+role=forwarded\s+pid='+$secondaryBusinessId+'\s+code=0\b'))
    $recall=Wait-DeliveredCondition {
        $records=@(Read-DeliveredFreshEvents | Where-Object { $_.eventName -eq 'recall_handled' -and $_.requestPid -eq $secondaryBusinessId -and $_.primaryPid -eq $primary.Process.Id })
        if ($records.Count) { return $records[-1] }
        return $null
    } 'primary acknowledgement for this exact secondary process'
    Add-DeliveredCheck 'secondary_acknowledges_original_window' ([long]$recall.windowHandle -eq $window.ToInt64() -and $recall.outcome -in @('foreground','attention_requested'))
    # Foreground is deliberately not asserted here; the dedicated interactive smoke owns that evidence.
    $primary.Process.Refresh()
    Add-DeliveredCheck 'primary_pid_and_hwnd_remain_responding' (-not $primary.Process.HasExited -and $primary.Process.MainWindowHandle -eq $window -and $primary.Process.Responding)
    $fresh=@(Read-DeliveredFreshEvents)
    Add-DeliveredCheck 'exactly_one_primary_and_one_window_ready' (@($fresh | Where-Object eventName -eq 'primary_started').Count -eq 1 -and @($fresh | Where-Object eventName -eq 'window_ready').Count -eq 1)
    Add-DeliveredCheck 'different_cwd_does_not_receive_data' (-not (Test-Path -LiteralPath (Join-Path $secondaryCwd 'AutumnOS_Data')))
    $report.repeat_launch='passed'; $report.different_working_directory='passed'
    Test-DeliveredUpdatesPage
    $report.status='passed'
} catch {
    $report.failure_type=$_.Exception.GetType().FullName; $report.failure=$_.Exception.Message
    Write-Warning "Delivered launcher verification: $($report.failure)"
} finally {
    # Only a process returned by this script's primary Process.Start may receive normal close.
    # An unresponsive primary or secondary is left alive with PID evidence; no force-close path exists.
    if ($primary -and -not $primary.Process.HasExited) {
        try {
            Assert-DeliveredOwnedPrimary
            $primary.Process.Refresh()
            if ($primary.Process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Owned primary has no closable window; it is left running.' }
            $closed=$primary.Process.CloseMainWindow()
            if (-not $closed -or -not $primary.Process.WaitForExit(10000)) { throw 'Owned primary did not exit after normal CloseMainWindow; it is left running.' }
            Add-DeliveredCheck 'held_primary_exits_normally' ($primary.Process.ExitCode -eq 0)
            if($primary.IsBootstrap){Add-DeliveredCheck 'held_bootstrap_propagates_business_exit_code' ($primary.EntryProcess.WaitForExit(10000) -and $primary.EntryProcess.ExitCode -eq $primary.Process.ExitCode)}
            $report.normal_exit='passed'
        } catch {
            $checks.Add([ordered]@{name='normal_close_owned_primary';status='failed';detail=$_.Exception.Message})
            $report.normal_exit='failed'; $report.status='failed'
        }
    } elseif ($primary) {
        $report.normal_exit='not_run_primary_already_exited'
        $report.status='failed'
    }
    if ($report.binary_before) {
        try {
            $report.binary_after=Get-DeliveredBinaryEvidence
            Add-DeliveredCheck 'delivered_binary_and_assembly_identity_unchanged' ($report.binary_after.executable_sha256 -eq $report.binary_before.executable_sha256 -and
                $report.binary_after.business_executable_sha256 -eq $report.binary_before.business_executable_sha256 -and
                $report.binary_after.contracts_sha256 -eq $report.binary_before.contracts_sha256 -and
                $report.binary_after.metadata.BuildId -eq $report.binary_before.metadata.BuildId -and
                $report.binary_after.metadata.SourceSnapshotId -eq $report.binary_before.metadata.SourceSnapshotId)
            $report.data_after=Get-DeliveredDataManifest
            Write-NewEvidence (Join-Path $ReportDirectory 'data-after.json') (ConvertTo-Json -InputObject $report.data_after -Depth 8)
            if ($report.data_before) {
                $report.data_initialization=if (-not $report.data_before.exists -and $report.data_after.exists) {'created_by_actual_exe_test_and_retained'} elseif ($report.data_before.exists -and $report.data_after.exists) {'preexisting_data_observed_after_test'} elseif ($report.data_before.exists) {'preexisting_data_missing_after_test'} else {'not_created'}
                Add-DeliveredCheck 'preexisting_data_root_not_removed' (-not $report.data_before.exists -or $report.data_after.exists)
                $beforeByPath=@{}; $afterByPath=@{}
                foreach ($entry in $report.data_before.entries) { $beforeByPath[$entry.path]=$entry }
                foreach ($entry in $report.data_after.entries) { $afterByPath[$entry.path]=$entry }
                $allPaths=@(@($beforeByPath.Keys)+@($afterByPath.Keys) | Sort-Object -Unique)
                $report.data_changes=@(foreach ($relative in $allPaths) {
                    $before=$beforeByPath[$relative]; $after=$afterByPath[$relative]
                    if ($null -eq $before) { [ordered]@{path=$relative;change='added'} }
                    elseif ($null -eq $after) { [ordered]@{path=$relative;change='removed'} }
                    elseif ($before.kind -ne $after.kind -or $before.sha256 -ne $after.sha256) { [ordered]@{path=$relative;change='changed'} }
                })
                Add-DeliveredCheck 'preexisting_data_entries_not_removed' (@($report.data_changes | Where-Object change -eq 'removed').Count -eq 0)
            }
            $report.launcher_events=@(Read-DeliveredFreshEvents)
            Write-NewEvidence (Join-Path $ReportDirectory 'launcher-events.json') (ConvertTo-Json -InputObject $report.launcher_events -Depth 8)
        } catch {
            $checks.Add([ordered]@{name='final_binary_data_and_log_evidence';status='failed';detail=$_.Exception.Message})
            $report.status='failed'
        }
    }
    foreach ($launch in $ownedLaunches) {
        try {
            $process=$launch.EntryProcess
            if ($process.HasExited) {
                $launch.Record.entry_exit_code=$process.ExitCode
                if($launch.Process.HasExited){$launch.Record.exit_code=$launch.Process.ExitCode}
                foreach ($output in @(@('stdout',$launch.OutTask),@('stderr',$launch.ErrorTask))) {
                    if ($output[1]) {
                        $path=Join-Path $ReportDirectory ($launch.Record.role+'-'+$output[0]+'.log')
                        Write-NewEvidence $path $output[1].GetAwaiter().GetResult()
                        $launch.Record[$output[0]]=$path
                    }
                }
            } else {
                $report.remaining_processes+=@([ordered]@{process_id=$process.Id;role=$launch.Record.role;executable=$ExecutablePath;start_utc=$launch.Record.start_utc;action='left_running_no_forced_termination'})
                $report.status='failed'
            }
        } catch {
            $checks.Add([ordered]@{name='capture_owned_process_output';status='failed';detail=$_.Exception.Message})
            $report.status='failed'
        } finally {
            $launch.Process.Dispose()
            if(-not [object]::ReferenceEquals($launch.Process,$launch.EntryProcess)){$launch.EntryProcess.Dispose()}
        }
    }
    $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o'); $report.check_count=$checks.Count
    $report.passed_checks=@($checks | Where-Object status -eq 'passed').Count
    Write-NewEvidence $reportPath (ConvertTo-Json -InputObject $report -Depth 12)
    Write-Host "REPORT $reportPath"
}
if ($report.status -ne 'passed') { exit 1 }
