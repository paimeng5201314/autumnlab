[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [string]$ReportDirectory,
    [ValidateRange(10, 90)][int]$WindowTimeoutSeconds = 35
)

# Real WinUI account UI and a real native login transaction. No credentials or browser automation.
# This script deliberately requires an otherwise idle AutumnOS desktop; it never kills processes.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$runId = 'T03-account-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
$scope = [IO.Path]::GetFullPath((Join-Path $projectRoot "artifacts/smoke/$runId"))
if (-not $ReportDirectory) { $ReportDirectory = $scope }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
$reportPath = Join-Path $ReportDirectory 'account-smoke.json'
if (Test-Path -LiteralPath $reportPath) { throw 'Choose a new report directory; existing evidence will not be overwritten.' }
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[object]]::new()
$owned = [Collections.Generic.List[Diagnostics.Process]]::new()
$busyListener = $null
$product = $null
$window = $null
$handle = [IntPtr]::Zero
$report = [ordered]@{
    schema_version = 1; task_id = 'T03'; checkpoint = 'native_account_ui_cancel_port_conflict_single_instance'
    run_id = $runId; started_utc = [DateTimeOffset]::UtcNow.ToString('o'); status = 'failed'
    source_snapshot_id = 'not_recorded'; build_id = 'local-untracked'; environment = [Environment]::OSVersion.VersionString
    source_executable = $null; tested_executable = $null; source_executable_sha256 = $null
    driver_sha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    main_pid = $null; main_window = $null; forwarding_pid = $null; forwarding_exit_code = $null
    listener_before_repeat = @(); listener_after_repeat = @(); checks = $checks; screenshots = $screenshots
    login_roundtrip = 'not_run'; refresh = 'not_run'; provider_logout = 'not_run'; second_account = 'not_run'
    console_registration = 'unverified'; browser_credentials_automation = 'not_run'
    browser_launch_independently_observed = 'not_run'; tokens_or_callback_urls_recorded = $false
    protocol_negative_tests = 'not_run_in_this_script'; physical_input = 'not_run'
    browser_window_cleanup = 'not_attempted_shared_browser_may_be_user_owned'
    full_t03_acceptance = 'not_run'; cleanup = 'not_run'
}

function Check([string]$Name, [bool]$Passed, [string]$Detail = '') {
    $checks.Add([ordered]@{ name = $Name; status = $(if ($Passed) { 'passed' } else { 'failed' }); detail = $Detail })
    if (-not $Passed) { throw "Account smoke failed: $Name" }
    Write-Host "PASS $Name"
}
function Wait-For([scriptblock]$Condition, [string]$Description, [int]$Seconds = $WindowTimeoutSeconds) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $value = & $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds 100
    } while ($timer.Elapsed.TotalSeconds -lt $Seconds)
    throw "Timed out: $Description"
}
function Element([string]$Id) {
    $condition = [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty, $Id)
    return $window.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}
function Visible([string]$Id) {
    $item = Element $Id
    if ($item -and -not $item.Current.IsOffscreen -and -not $item.Current.BoundingRectangle.IsEmpty) { return $item }
    return $null
}
function Invoke-Button([string]$Id) {
    $item = Wait-For {
        $candidate = Element $Id
        if (-not $candidate -or -not $candidate.Current.IsEnabled) { return $null }
        $scroll = $null
        if ($candidate.Current.IsOffscreen -and $candidate.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern, [ref]$scroll)) { $scroll.ScrollIntoView() }
        if (-not $candidate.Current.IsOffscreen) { return $candidate }
        return $null
    } "enabled button $Id"
    $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function State-Equals([string]$State) {
    $item = Element 'AccountState'
    return $item -and $item.Current.Name -eq $State
}
function Listener-Evidence {
    return @(Get-NetTCPConnection -State Listen -LocalPort 17853 -ErrorAction SilentlyContinue |
        Select-Object @{Name='address';Expression={$_.LocalAddress}}, @{Name='port';Expression={$_.LocalPort}}, @{Name='owner_pid';Expression={$_.OwningProcess}})
}
function Port-IsFree {
    $probe = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 17853)
    try { $probe.Server.ExclusiveAddressUse = $true; $probe.Start(1); return $true }
    catch [Net.Sockets.SocketException] { return $false }
    finally { $probe.Stop() }
}
function Assert-OwnedWindow {
    if (-not $product -or $product.HasExited) { throw 'The test-owned launcher is no longer running.' }
    $product.Refresh()
    [uint32]$owner = 0
    $null = [AutumnAccountSmoke.Native]::GetWindowThreadProcessId($handle, [ref]$owner)
    if ($owner -ne $product.Id -or $product.MainWindowHandle -ne $handle -or $window.Current.ProcessId -ne $product.Id) {
        throw 'Window ownership changed; refusing input/capture.'
    }
}
function Capture([string]$Name, [string]$ExpectedAccountState) {
    # Product page transition is 220 ms; capture the settled real client.
    Start-Sleep -Milliseconds 350
    Assert-OwnedWindow
    if (-not (State-Equals $ExpectedAccountState)) { throw 'Refusing screenshot outside the expected non-personal account state.' }
    $path = Join-Path $ReportDirectory "$Name.png"
    $rect = [AutumnAccountSmoke.Native+RECT]::new()
    if (-not [AutumnAccountSmoke.Native]::GetClientRect($handle, [ref]$rect)) { throw 'Unable to obtain client bounds.' }
    $width = $rect.Right - $rect.Left; $height = $rect.Bottom - $rect.Top
    if ($width -lt 100 -or $height -lt 100 -or $width -gt 10000 -or $height -gt 10000) { throw 'Unexpected client bounds.' }
    $bitmap = [Drawing.Bitmap]::new($width, $height)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        # Native window-only capture excludes browser/desktop. No full-screen fallback is allowed.
        $hdc = $graphics.GetHdc()
        try { $captured = [AutumnAccountSmoke.Native]::PrintWindow($handle, $hdc, 3) }
        finally { $graphics.ReleaseHdc($hdc) }
        if (-not $captured) { throw 'Native client capture failed; no browser/desktop capture fallback.' }
        Assert-OwnedWindow
        if (-not (State-Equals $ExpectedAccountState)) { throw 'Account state changed during capture; refusing to persist pixels.' }
        $bitmap.Save($path, [Drawing.Imaging.ImageFormat]::Png)
        $screenshots.Add([ordered]@{ name=$Name; path=$path; process_id=$product.Id; width=$width; height=$height
            capture_mode='PrintWindow_CLIENTONLY_RENDERFULLCONTENT'; safe_account_state=$ExpectedAccountState
            status='captured_not_visually_reviewed'; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() })
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
}

try {
    if (-not $IsWindows) { throw 'An interactive Windows desktop is required.' }
    $existing = @(Get-Process -Name AutumnOS -ErrorAction SilentlyContinue)
    if ($existing.Count -ne 0) { throw 'Existing user AutumnOS window detected. Ask the user to exit normally before this isolated test; no process will be stopped.' }
    Check 'no_existing_user_launcher' ($existing.Count -eq 0)
    Check 'callback_port_initially_available' (Port-IsFree) 'Only a temporary exclusive loopback bind is used; no owner is terminated.'
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    Check 'standard_user' (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
    $ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
    if ([IO.Path]::GetFileName($ExecutablePath) -ne 'AutumnOS.exe' -or -not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw 'An existing AutumnOS.exe is required.' }
    $report.source_executable = $ExecutablePath
    $report.source_executable_sha256 = (Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    $stage = Join-Path $scope '隔离账号 实测'
    if (Test-Path -LiteralPath $stage) { throw 'Isolated test directory already exists.' }
    New-Item -ItemType Directory -Path $stage -Force | Out-Null
    foreach ($entry in Get-ChildItem -LiteralPath (Split-Path $ExecutablePath -Parent) -Force) {
        if ($entry.Name -eq 'AutumnOS_Data') { continue }
        if (($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Refusing redirected build output.' }
        if ($entry.PSIsContainer -and @(Get-ChildItem -LiteralPath $entry.FullName -Recurse -Force | Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 }).Count) { throw 'Refusing nested redirected build output.' }
        Copy-Item -LiteralPath $entry.FullName -Destination $stage -Recurse
    }
    $report.tested_executable = Join-Path $stage 'AutumnOS.exe'
    Check 'isolated_copy_matches_executable' ((Get-FileHash -LiteralPath $report.tested_executable -Algorithm SHA256).Hash.ToLowerInvariant() -eq $report.source_executable_sha256)
    $data = Join-Path $stage 'AutumnOS_Data'
    Check 'no_user_data_copied' (-not (Test-Path -LiteralPath $data))
    foreach ($resource in @('AutumnOS.pri','App.xbf','MainWindow.xbf')) { Check "resource_$resource" (Test-Path -LiteralPath (Join-Path $stage $resource) -PathType Leaf) }
    $assembly = [Reflection.Assembly]::LoadFrom((Join-Path $stage 'AutumnOS.Contracts.dll'))
    foreach ($attribute in $assembly.GetCustomAttributesData()) {
        if ($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute') {
            $key = $attribute.ConstructorArguments[0].Value; $value = $attribute.ConstructorArguments[1].Value
            if ($key -eq 'BuildId') { $report.build_id = $value }
            if ($key -eq 'SourceSnapshotId') { $report.source_snapshot_id = $value }
        }
    }
    $desktopRuntime = Join-Path $projectRoot '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach ($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')) { Add-Type -Path (Join-Path $desktopRuntime $name) }
    if (-not ('AutumnAccountSmoke.Native' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace AutumnAccountSmoke {
  public static class Native {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
  }
}
'@
    }
    $cwd1 = Join-Path $scope 'first-cwd'; $cwd2 = Join-Path $scope 'repeat-cwd'
    New-Item -ItemType Directory -Path $cwd1,$cwd2 -Force | Out-Null
    $product = Start-Process -FilePath $report.tested_executable -WorkingDirectory $cwd1 -WindowStyle Normal -PassThru
    $owned.Add($product); $report.main_pid = $product.Id
    $handle = Wait-For { $product.Refresh(); if ($product.HasExited) { throw 'Launcher exited before the window was ready.' }; if ($product.MainWindowHandle -ne [IntPtr]::Zero) { return $product.MainWindowHandle } } 'native launcher window'
    $report.main_window = $handle.ToInt64()
    $window = [Windows.Automation.AutomationElement]::FromHandle($handle)
    Assert-OwnedWindow
    $null = Wait-For { Visible 'HelloNextButton' } 'first-run hello'
    Invoke-Button 'HelloNextButton'; Invoke-Button 'BrandNextButton'; Invoke-Button 'PrepareDesktopButton'
    $null = Wait-For { Visible 'SettingsButton' } 'desktop'
    Check 'first_run_and_desktop_still_work' ($null -ne (Visible 'SettingsButton'))
    Invoke-Button 'AccountButton'
    $null = Wait-For { State-Equals '未登录 · 游客' } 'system account app opens the real account category'
    Check 'account_desktop_entry_opens_real_account_details' ($null -ne (Visible 'AccountState'))
    Invoke-Button 'SettingsHomeButton'
    Invoke-Button 'SettingsButton'
    $nav = Wait-For { Visible 'SettingsNavAccount' } 'account settings category'
    $toggle = $nav.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if ($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::On) { $toggle.Toggle() }
    $null = Wait-For { State-Equals '未登录 · 游客' } 'real guest account state'
    $null = Wait-For { $b = Element 'AccountLogin'; $b -and $b.Current.IsEnabled } 'initialized identity service'
    Check 'account_is_a_selected_settings_category' ($nav.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::On)
    Check 'guest_not_configuration_success_login' (State-Equals '未登录 · 游客')
    Check 'persistent_login_requires_explicit_named_action' ((Element 'AccountLogin').Current.Name -eq '登录并保持登录')
    Check 'one_session_login_remains_available' ((Element 'AccountSessionLogin').Current.IsEnabled -and (Element 'AccountSessionLogin').Current.Name -eq '仅本次登录')
    Capture '01-account-guest' '未登录 · 游客'

    # Occupy the fixed callback port with this script's own listener. Never stop another owner.
    $busyListener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 17853)
    $busyListener.Server.ExclusiveAddressUse = $true; $busyListener.Start(1)
    Invoke-Button 'AccountLogin'
    $null = Wait-For { $result = Element 'AccountResult'; $result -and $result.Current.Name.Contains('AUTH_PORT_IN_USE') } 'truthful port conflict'
    Check 'occupied_port_reports_auth_port_in_use' ((Element 'AccountResult').Current.Name.Contains('AUTH_PORT_IN_USE'))
    Check 'occupied_port_did_not_fake_login' (State-Equals '未登录 · 游客')
    Check 'owned_conflicting_listener_is_still_alive' ($busyListener.Server.IsBound -and -not (Port-IsFree))
    Capture '02-account-port-conflict' '未登录 · 游客'
    $busyListener.Stop(); $busyListener = $null
    Check 'test_listener_released_normally' (Port-IsFree)

    # This scenario needs a pending transaction to test cancellation. An existing
    # browser SSO session can complete ordinary login before the bounded UI checks.
    # Explicit reauthentication sends prompt=login; no browser credentials are entered.
    Invoke-Button 'AccountSwitch'
    $null = Wait-For { State-Equals '等待浏览器登录' } 'real login in progress'
    $listeners = Wait-For {
        $items = @(Listener-Evidence)
        if (@($items | Where-Object { $_.address -eq '127.0.0.1' -and $_.owner_pid -eq $product.Id }).Count -eq 1) { return ,$items }
    } 'product-owned exclusive loopback listener' 10
    $report.listener_before_repeat = @($listeners)
    Check 'login_listener_is_ipv4_loopback_and_primary_owned' (@($listeners).Count -eq 1 -and $listeners[0].address -eq '127.0.0.1' -and $listeners[0].owner_pid -eq $product.Id)
    Check 'second_login_button_disabled' (-not (Element 'AccountLogin').Current.IsEnabled)
    Check 'one_session_button_disabled_during_login' (-not (Element 'AccountSessionLogin').Current.IsEnabled)
    Check 'cancel_login_button_enabled' ((Element 'AccountCancel').Current.IsEnabled)
    $configHash = (Get-FileHash -LiteralPath (Join-Path $data 'Config/first-run.json') -Algorithm SHA256).Hash
    $second = Start-Process -FilePath $report.tested_executable -WorkingDirectory $cwd2 -WindowStyle Hidden -PassThru
    $owned.Add($second); $report.forwarding_pid = $second.Id
    if (-not $second.WaitForExit(20000)) { throw 'Forwarding process did not exit in the bounded wait; it will not be killed.' }
    $report.forwarding_exit_code = $second.ExitCode
    Check 'repeat_launch_during_login_exits_successfully' ($second.ExitCode -eq 0)
    Assert-OwnedWindow
    Check 'repeat_launch_keeps_same_window_pid' (-not $product.HasExited -and $product.MainWindowHandle -eq $handle)
    Check 'repeat_launch_keeps_login_in_progress' (State-Equals '等待浏览器登录')
    $report.listener_after_repeat = @(Listener-Evidence)
    Check 'repeat_launch_has_only_original_callback_listener' ($report.listener_after_repeat.Count -eq 1 -and $report.listener_after_repeat[0].owner_pid -eq $product.Id -and $report.listener_after_repeat[0].address -eq '127.0.0.1')
    Check 'repeat_launch_does_not_reinitialize_data' ((Get-FileHash -LiteralPath (Join-Path $data 'Config/first-run.json') -Algorithm SHA256).Hash -eq $configHash -and -not (Test-Path -LiteralPath (Join-Path $cwd2 'AutumnOS_Data')))
    $launcherEvents = @(Get-Content -LiteralPath (Join-Path $data 'Logs/launcher-events.jsonl') | ForEach-Object { $_ | ConvertFrom-Json })
    Check 'only_one_business_primary_registered' (@($launcherEvents | Where-Object { $_.eventName -eq 'primary_started' }).Count -eq 1)
    Capture '03-account-login-pending' '等待浏览器登录'
    Invoke-Button 'AccountCancel'
    $null = Wait-For { State-Equals '未登录 · 游客' } 'cancelled login returns guest'
    $null = Wait-For { Port-IsFree } 'cancel releases callback port' 10
    Check 'cancel_releases_listener' (Port-IsFree)
    Check 'cancel_reports_user_cancelled' ((Element 'AccountResult').Current.Name.Contains('USER_CANCELLED'))
    Check 'cancel_enables_future_login' ((Element 'AccountLogin').Current.IsEnabled)
    Capture '04-account-login-cancelled' '未登录 · 游客'
    Check 'source_executable_unchanged' ((Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant() -eq $report.source_executable_sha256)
    $report.status = 'passed'
} catch {
    $report.error_type = $_.Exception.GetType().Name
    # Only this driver's finite error descriptions appear; never dump arbitrary UI, URLs or token material.
    $report.error = $_.Exception.Message
    Write-Warning "T03 account smoke failed: $($_.Exception.Message)"
} finally {
    if ($busyListener) { $busyListener.Stop(); $busyListener = $null }
    $cleanupProblems = [Collections.Generic.List[string]]::new()
    if ($product -and -not $product.HasExited -and $window) {
        try {
            if ((Element 'AccountCancel').Current.IsEnabled) { Invoke-Button 'AccountCancel'; Start-Sleep -Milliseconds 300 }
        } catch { $cleanupProblems.Add('Cancellation UI cleanup could not be confirmed.') }
    }
    foreach ($process in $owned) {
        try {
            if (-not $process.HasExited) {
                # Only recorded owned Process objects. Never enumerate and terminate an unrelated process.
                if (-not $process.CloseMainWindow() -or -not $process.WaitForExit(10000)) {
                    $cleanupProblems.Add("Owned process $($process.Id) remains; request normal user closure. No kill was attempted.")
                }
            }
        } catch { $cleanupProblems.Add("Owned process $($process.Id) cleanup failed without termination.") }
    }
    $report.cleanup = $(if ($cleanupProblems.Count -eq 0) { 'passed' } else { 'failed' })
    $report.cleanup_details = @($cleanupProblems)
    if ($cleanupProblems.Count) { $report.status = 'failed' }
    $report.finished_utc = [DateTimeOffset]::UtcNow.ToString('o')
    $report | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "REPORT $reportPath"
}
if ($report.status -ne 'passed') { throw "T03 account UI verification failed; inspect $reportPath" }
