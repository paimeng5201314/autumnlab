#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$GameProjectPath,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [ValidateRange(15,90)][int]$WindowTimeoutSeconds = 45
)

# Explicitly invoked native developer-preview test. Does not publish, modify the sandbox,
# change community execution policy, drive another launcher, or forcibly stop a process.
$ErrorActionPreference = 'Stop'
$ExecutablePath = [IO.Path]::GetFullPath($ExecutablePath)
$GameProjectPath = [IO.Path]::GetFullPath($GameProjectPath)
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
if (Test-Path -LiteralPath $ReportDirectory) { throw 'Choose a new report directory; historical evidence is preserved.' }
if ($ReportDirectory -match '[\\/]AutumnOS_Data(?:[\\/]|$)') { throw 'Reports cannot be placed in user data.' }
. (Join-Path $PSScriptRoot 'Smoke-NativeAppInput.ps1')
. (Join-Path $PSScriptRoot 'T06-NativeHelpers.ps1')
$desktopAst = Import-T06Functions (Join-Path $PSScriptRoot 'Test-DesktopInteractionSmoke.ps1')
$script:t05LegacyFocus = (Get-Item Function:Focus-SmokeProduct).ScriptBlock
$null = Import-T06Functions (Join-Path $PSScriptRoot 'Test-T05UpdateSmoke.ps1') -Names @(
    'Assert-T05OwnedWindow','Get-T05WindowGeometry','Focus-SmokeProduct','Initialize-T05FocusNative',
    'Assert-T05PlainPath','Invoke-T05SharingRead','Get-T05Hash')
$null = Import-T06Functions (Join-Path $PSScriptRoot 'Test-T04StoreSmoke.ps1') -Names @(
    'Show-Element','Click','Find','Visible','Require','Toggle','Click-Named')
$checks = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[object]]::new()
$inputActions = [Collections.Generic.List[object]]::new()
$windowGeometry = [Collections.Generic.List[object]]::new()
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$ownedEntryProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$trackedProcesses = [Collections.Generic.List[object]]::new()
$cliProcesses = [Collections.Generic.List[object]]::new()
$commands = [Collections.Generic.List[object]]::new()
$script:initialPointerPosition = $null
$script:product = $null
$scope = Join-Path $ReportDirectory 'isolated-chemleap'
$script:stageDirectory = Join-Path $scope 'AutumnOS'
$projectCopy = Join-Path $scope 'ChemLeap-project'
$cwd = Join-Path $scope 'unrelated-cwd'
$dataDirectory = Join-Path $stageDirectory 'AutumnOS_Data'
$runtimeLog = Join-Path $dataDirectory 'Logs/runtime-events.jsonl'
$report = [ordered]@{
    schema_version=1; checkpoint='chemleap_cli_build_and_real_native_developer_preview'; status='failed'
    started_utc=[DateTimeOffset]::UtcNow.ToString('o'); finished_utc=$null
    source_executable=$ExecutablePath; source_project=$GameProjectPath
    tested_executable=(Join-Path $stageDirectory 'AutumnOS.exe'); tested_project=$projectCopy
    driver_sha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    build_id='not_recorded'; source_snapshot_id='not_recorded'; imported_helpers_sha256=@{}
    app_id='cn.labchronicles.chemleap'; scope='explicit_developer_preview_only'
    public_store_download='not_run'; public_store_execution='not_run'; community_gate='unchanged'
    sandbox_policy='unchanged'; external_publication='not_performed'; real_logto='not_run'
    project_selection='not_run_native_folder_picker_first_driver_failed'; project_validation='not_run'; package_build='not_run'
    developer_workflow='official_local_CLI_validate_pack_preview_with_native_mode_and_confirmation'; commands=$commands
    native_gameplay='not_run'; sdk_save='not_run'; new_preview_readback='not_run'
    checks=$checks; screenshots=$screenshots; input_actions=$inputActions; window_geometry=$windowGeometry
    process_ids=@(); bootstrap_process_ids=@(); remaining_processes=@(); cleanup='not_run'
}

function Copy-ChemLeapPlainTree([string]$Source,[string]$Destination,[string[]]$Exclude=@(),[switch]$ProjectResourcesOnly) {
    Assert-T05PlainPath $Source; Assert-T05PlainPath $Destination
    if (-not(Test-Path -LiteralPath $Source -PathType Container) -or (Test-Path -LiteralPath $Destination)) { throw 'A real source and a new isolated destination are required.' }
    if ($Destination.StartsWith($Source.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'An isolated destination cannot be inside its copied source.' }
    New-Item -ItemType Directory -Path $Destination | Out-Null
    if ($ProjectResourcesOnly) {
        # The submitted project is the actual local game. Development documents stay at the
        # source; only supported package resources enter this test's CLI-selected copy.
        $allowed=@('.html','.js','.css','.json','.png','.jpg','.svg','.woff2','.wasm')
        foreach ($file in Get-ChildItem -LiteralPath $Source -Recurse -File -Force) {
            Assert-T05PlainPath $file.FullName
            if ($file.Extension.ToLowerInvariant() -notin $allowed) { continue }
            $target=Join-Path $Destination ([IO.Path]::GetRelativePath($Source,$file.FullName))
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
        return
    }
    foreach ($entry in Get-ChildItem -LiteralPath $Source -Force) {
        if ($entry.Name -in $Exclude) { continue }
        Assert-T05PlainPath $entry.FullName
        if ($entry.PSIsContainer) { foreach ($child in Get-ChildItem -LiteralPath $entry.FullName -Force -Recurse) { Assert-T05PlainPath $child.FullName } }
        Copy-Item -LiteralPath $entry.FullName -Destination $Destination -Recurse
    }
}
function Get-ChemLeapSourceHashes([string]$Root) {
    return @(foreach ($file in Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Sort-Object FullName) {
        Assert-T05PlainPath $file.FullName
        [pscustomobject]@{path=[IO.Path]::GetRelativePath($Root,$file.FullName); sha256=Get-T05Hash $file.FullName}
    })
}
function Get-ChemLeapScopes {
    $result = [Collections.Generic.List[object]]::new(); $result.Add($product.Element)
    $condition = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Document),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'ChemLeap · 化学跳一跳'))
    $window = $product.Element.Current.BoundingRectangle
    foreach ($document in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants,$condition)) {
        $bounds=$document.Current.BoundingRectangle
        if (-not $bounds.IsEmpty -and $bounds.Left -ge $window.Left -and $bounds.Top -ge $window.Top -and $bounds.Right -le $window.Right -and $bounds.Bottom -le $window.Bottom -and
            (Test-AutumnOwnedInputProcess $document.Current.ProcessId $product.Process)) { $result.Add($document) }
    }
    return @($result)
}
function Find-ChemLeapElement([string]$Id,[string]$NamePattern,$ControlType) {
    foreach ($elementScope in Get-ChemLeapScopes) {
        $found = if ($Id) { Find-SmokeElement $elementScope $Id } else { $null }
        if ($found -and (Test-AutumnOwnedInputProcess $found.Current.ProcessId $product.Process)) { return $found }
        $condition=if ($ControlType) {
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,$ControlType)
        } else { [Windows.Automation.Condition]::TrueCondition }
        foreach ($candidate in $elementScope.FindAll([Windows.Automation.TreeScope]::Descendants,$condition)) {
            if ($candidate.Current.Name -match $NamePattern -and (Test-AutumnOwnedInputProcess $candidate.Current.ProcessId $product.Process)) { return $candidate }
        }
    }
    return $null
}
function Click-ChemLeap([string]$Id,[string]$NamePattern) {
    $element=Wait-SmokeCondition {
        $candidate=Find-ChemLeapElement $Id $NamePattern ([Windows.Automation.ControlType]::Button)
        if ($candidate -and $candidate.Current.IsEnabled) { return $candidate }
    } ('actual game button '+$Id) 20
    $inputActions.Add((Invoke-AutumnNativeElementClick -WindowElement $product.Element -Element $element -Process $product.Process))
}
function Get-ChemLeapScore {
    # Chromium may expose a labelled <strong> as Group or Custom rather than Text.
    $score=Find-ChemLeapElement 'score' '^得分\s*\d+$' $null
    if ($score -and $score.Current.Name -match '\d+') { return [int]$Matches[0] }
    return $null
}
function Get-ChemLeapSubstance {
    $substance=Find-ChemLeapElement 'current-substance' '^当前物质 ' $null
    if ($substance) { return $substance.Current.Name }
    return $null
}
function Wait-ChemLeapStatus([string]$Pattern,[switch]$MayAuthorize) {
    $null=Wait-SmokeCondition {
        if ($MayAuthorize) {
            $allow=$product.Element.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.AndCondition]::new(
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'允许'),
                [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)))
            if ($allow -and -not $allow.Current.IsOffscreen -and $allow.Current.IsEnabled) { $allow.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
        }
        $status=Find-ChemLeapElement 'save-status' $Pattern ([Windows.Automation.ControlType]::Text)
        if ($status -and $status.Current.Name -match $Pattern) { return $status.Current.Name }
        foreach ($elementScope in Get-ChemLeapScopes) {
            $texts=$elementScope.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new(
                [Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Text))
            foreach ($text in $texts) { if ($text.Current.Name -match $Pattern) { return $text.Current.Name } }
        }
        return $null
    } ('real game status '+$Pattern) 25
}
function Read-ChemLeapRuntimeEvents {
    if (-not(Test-Path -LiteralPath $runtimeLog)) { return @() }
    $snapshot=Read-T06Snapshot $runtimeLog
    $text=[Text.Encoding]::UTF8.GetString($snapshot.Bytes)
    $last=$text.LastIndexOf("`n")
    if ($last -lt 0) { return @() }
    # Observe complete appended records only; malformed complete JSON still fails the test.
    return @($text.Substring(0,$last).Split("`n") | Where-Object {$_.Trim()} | ForEach-Object {$_ | ConvertFrom-Json})
}
function Start-ChemLeapCli([string[]]$Arguments) {
    $start=[Diagnostics.ProcessStartInfo]::new($script:chemLeapDotnet)
    $start.WorkingDirectory=$cwd; $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $start.ArgumentList.Add($script:chemLeapCli)
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::Start($start); $null=$process.Handle
    $entry=[pscustomobject]@{Process=$process;Arguments=$Arguments;Started=$process.StartTime.ToUniversalTime();
        Out=$process.StandardOutput.ReadToEndAsync();Err=$process.StandardError.ReadToEndAsync();Recorded=$false}
    $cliProcesses.Add($entry)
    return $entry
}
function Finish-ChemLeapCli($Entry,[ValidateRange(1,60)][int]$Seconds=45) {
    if (-not $Entry.Process.WaitForExit($Seconds*1000)) { throw 'The owned developer CLI exceeded its bounded timeout; no process is forcibly stopped.' }
    $output=$Entry.Out.GetAwaiter().GetResult(); $errors=$Entry.Err.GetAwaiter().GetResult()
    if (-not $Entry.Recorded) {
        $commands.Add([ordered]@{executable=$script:chemLeapDotnet;cli=$script:chemLeapCli;arguments=$Entry.Arguments;
            working_directory=$cwd;started_utc=$Entry.Started.ToString('o');exit_code=$Entry.Process.ExitCode;stdout=$output;stderr=$errors})
        $Entry.Recorded=$true
    }
    if ($Entry.Process.ExitCode -ne 0) { throw ('Developer CLI failed for '+$Entry.Arguments[0]+'; see the recorded output and actual exit code.') }
    return $output | ConvertFrom-Json
}
function Start-ChemLeapPreview {
    $before=@(Read-ChemLeapRuntimeEvents).Count
    # The official CLI waits for the real host confirmation; no configuration or runtime
    # messages are injected and the host re-inspects the exact submitted package hash.
    $entry=Start-ChemLeapCli @('preview',$report.package_path,'--host',$stageDirectory)
    Click-Named '确认此包并预览'
    $response=Finish-ChemLeapCli $entry
    Add-SmokeCheck 'official_CLI_preview_enters_real_confirmed_native_host' (
        $response.host -eq 'native-internal-preview' -and $response.state -eq 'Foreground' -and $response.appId -eq $report.app_id)
    $null=Wait-SmokeCondition { Find-ChemLeapElement 'start-button' '^开始跳跃' ([Windows.Automation.ControlType]::Button) } 'real ChemLeap document' 30
    $event=Wait-SmokeCondition { @(Read-ChemLeapRuntimeEvents | Select-Object -Skip $before | Where-Object {$_.eventName -eq 'navigation_completed' -and $_.state -eq 'Foreground'}) | Select-Object -Last 1 } 'new preview navigation' 20
    return $event.instanceId
}

try {
    if (-not $IsWindows) { throw 'An interactive Windows desktop is required.' }
    if (@(Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('AutumnOS.exe','AutumnOS.Client.exe','AutumnOS.Updater.exe')}).Count) { throw 'Another AutumnOS product is running; it is not taken over or closed.' }
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this native test as a standard user.' }
    Assert-T05PlainPath $ReportDirectory; Assert-T05PlainPath $ExecutablePath; Assert-T05PlainPath $GameProjectPath
    if ([IO.Path]::GetFileName($ExecutablePath) -ne 'AutumnOS.exe' -or -not(Test-Path -LiteralPath $ExecutablePath -PathType Leaf)) { throw 'A real AutumnOS.exe from a complete ordinary directory is required.' }
    $hostRoot=Split-Path $ExecutablePath -Parent
    if (-not(Test-Path -LiteralPath (Join-Path $hostRoot 'Microsoft.UI.Xaml.dll'))) { throw 'Use a complete ordinary Windows build directory, not the single-file bootstrap alone.' }
    $manifest=Read-T05Json (Join-Path $GameProjectPath 'manifest.json')
    if ($manifest.appId -ne 'cn.labchronicles.chemleap' -or $manifest.runtime -ne 'web' -or 'saves' -notin $manifest.permissions) { throw 'The real ChemLeap web project with the saves permission is required.' }
    $originalHashes=Get-ChemLeapSourceHashes $GameProjectPath
    New-Item -ItemType Directory -Path $ReportDirectory,$scope,$cwd | Out-Null
    Copy-ChemLeapPlainTree $hostRoot $stageDirectory @('AutumnOS_Data','.autumnos-update')
    Copy-ChemLeapPlainTree $GameProjectPath $projectCopy -ProjectResourcesOnly
    $script:chemLeapDotnet=Join-Path (Split-Path $PSScriptRoot -Parent) '.tools/dotnet/dotnet.exe'
    $script:chemLeapCli=Join-Path $stageDirectory 'Developer/AutumnOS.Developer.Cli.dll'
    if (-not(Test-Path -LiteralPath $script:chemLeapDotnet -PathType Leaf) -or -not(Test-Path -LiteralPath $script:chemLeapCli -PathType Leaf)) {
        throw 'The project-local .NET executable and copied delivered developer CLI are required.'
    }
    $report.tested_cli=$script:chemLeapCli; $report.tested_cli_sha256=Get-T05Hash $script:chemLeapCli
    $report.project_files=$originalHashes
    Add-SmokeCheck 'isolated_host_starts_without_user_data' (-not(Test-Path -LiteralPath $dataDirectory))
    foreach ($name in @('Smoke-NativeAppInput.ps1','T06-NativeHelpers.ps1','Test-DesktopInteractionSmoke.ps1','Test-T05UpdateSmoke.ps1','Test-T04StoreSmoke.ps1')) {
        $report.imported_helpers_sha256[$name]=Get-T05Hash (Join-Path $PSScriptRoot $name)
    }
    $desktopRuntime=Join-Path (Split-Path $PSScriptRoot -Parent) '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach ($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')) { Add-Type -Path (Join-Path $desktopRuntime $name) }
    $strings=@($desktopAst.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('namespace AutumnDesktopInteractionSmoke')},$true))
    if ($strings.Count -ne 1) { throw 'Maintained native helper declaration is missing.' }
    if (-not('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])) { Add-Type -TypeDefinition $strings[0].Value }
    Initialize-T05FocusNative
    $script:product=Start-SmokeProduct $cwd
    $trackedProcesses.Add([pscustomobject]@{Process=$product.Process;StartTicks=$product.Process.StartTime.ToUniversalTime().Ticks;Path=$product.Process.MainModule.FileName;Root=$stageDirectory})
    Add-SmokeCheck 'standard_user_owned_native_window' ($product.Element.Current.ProcessId -eq $product.Process.Id)
    if (Test-Path -LiteralPath (Join-Path $stageDirectory 'autumn.install.json')) {
        $receipt=Read-T05Json (Join-Path $stageDirectory 'autumn.install.json'); $report.build_id=$receipt.buildId
    }
    Click 'HelloNextButton'; Click 'BrandNextButton'; Click 'WelcomeAppearanceNext'; Click 'FirstRunSkip'; Click 'PrepareDesktopButton'; Wait-SmokeDesktop $product
    Click 'SettingsButton'; Toggle 'SettingsNavDiagnostics' $true; Toggle 'DeveloperModeToggle' $true
    Click 'SettingsHomeButton'; Click 'DeveloperButton'; $null=Require 'DeveloperPreview'
    $validated=Finish-ChemLeapCli (Start-ChemLeapCli @('validate',$projectCopy))
    Add-SmokeCheck 'official_CLI_validates_the_actual_isolated_game_project' ($validated.status -eq 'passed' -and $validated.appId -eq $report.app_id -and -not $validated.outputCreated)
    $report.project_validation='passed_cli'
    $packed=Finish-ChemLeapCli (Start-ChemLeapCli @('pack',$projectCopy,(Join-Path $scope 'packages')))
    $report.package_path=[IO.Path]::GetFullPath($packed.packagePath)
    $report.package_sha256=Get-T05Hash $report.package_path
    Add-SmokeCheck 'official_CLI_builds_real_package_with_matching_identity_and_bytes' (
        $packed.status -eq 'passed' -and $packed.appId -eq $report.app_id -and $packed.sha256 -eq $report.package_sha256 -and
        (Get-Item -LiteralPath $report.package_path).Length -eq $packed.bytes -and
        $report.package_path.StartsWith($scope.TrimEnd('\')+'\',[StringComparison]::OrdinalIgnoreCase))
    $report.package_build='passed_cli'
    $firstInstance=Start-ChemLeapPreview; $report.first_instance=$firstInstance
    Click-ChemLeap 'start-button' '^开始跳跃'
    $null=Wait-SmokeCondition { $score=Get-ChemLeapScore; $null -ne $score -and $score -eq 0 } 'initial real score zero' 15
    $option=Wait-SmokeCondition {
        $candidate=Find-ChemLeapElement '' '^\d\s+(?:O₂|O2|Cl₂|Cl2)\s' ([Windows.Automation.ControlType]::Button)
        if ($candidate -and $candidate.Current.IsEnabled) { return $candidate }
    } 'an actual compatible first chemical platform' 15
    $report.chosen_first_platform=$option.Current.Name
    $inputActions.Add((Invoke-AutumnNativeElementClick -WindowElement $product.Element -Element $option -Process $product.Process))
    $savedScore=Wait-SmokeCondition { $score=Get-ChemLeapScore; if ($null -ne $score -and $score -gt 0) { return $score } } 'score after actual chemical jump' 15
    $savedSubstance=Wait-SmokeCondition { $substance=Get-ChemLeapSubstance; if ($substance -match 'H₂O|H2O|HCl') { return $substance } } 'actual water or hydrogen chloride product' 15
    Add-SmokeCheck 'real_native_jump_changes_score_and_chemical_product' ($savedScore -gt 0 -and $savedSubstance -match 'H₂O|H2O|HCl')
    $report.saved_score=$savedScore; $report.saved_substance=$savedSubstance; $report.native_gameplay='passed'
    $null=Save-SmokeScreenshot $product 'chemleap-after-real-jump' -IncludeOverlays
    Click-ChemLeap 'save-button' '^保存进度$'
    Click-Named '允许'
    Wait-ChemLeapStatus '已保存'
    $saveRoot=Join-Path $dataDirectory 'Saves/cn.labchronicles.chemleap'
    # AccountDataStore stores preview saves below appId/SourceKey/AccountDirectory.
    # Discover the actual key, then verify the on-disk record and path binding; do not
    # assume the bundled element-pairs legacy appId/guest layout for this application.
    $saveFiles=Wait-SmokeCondition {
        if (Test-Path -LiteralPath $saveRoot) { $items=@(Get-ChildItem -LiteralPath $saveRoot -Filter '*.json' -Recurse -File); if ($items.Count -gt 0) { return ,$items } }
        return $null
    } 'actual SDK save file in isolated host data' 15
    foreach ($file in $saveFiles) {
        $record=Read-T05Json $file.FullName
        $relative=[IO.Path]::GetRelativePath($saveRoot,$file.FullName).Replace('\','/')
        Add-SmokeCheck 'SDK_save_record_matches_own_source_guest_slot_and_score' (
            $record.schemaVersion -eq 2 -and $record.appId -eq $report.app_id -and $record.sourceKey -match '^[a-f0-9]{64}$' -and
            $record.accountKey -eq 'guest' -and $record.slot -eq 'game' -and $record.formatVersion -eq 1 -and
            $relative -eq ($record.sourceKey+'/guest/game.json') -and $record.value.score -eq $savedScore)
    }
    $report.save_files=@(foreach ($file in $saveFiles) { [ordered]@{path=[IO.Path]::GetRelativePath($dataDirectory,$file.FullName);sha256=Get-T05Hash $file.FullName;bytes=$file.Length} })
    Add-SmokeCheck 'SDK_save_creates_real_source_bound_game_data' ($saveFiles.Count -gt 0)
    $report.sdk_save='passed'
    Click 'DeveloperPreviewClose'
    $null=Wait-SmokeCondition { @(Read-ChemLeapRuntimeEvents | Where-Object {$_.instanceId -eq $firstInstance -and $_.eventName -eq 'closed' -and $_.state -eq 'Closed' -and -not $_.blocksMaintenance}).Count -gt 0 } 'first real preview releases its resources' 15
    $secondInstance=Start-ChemLeapPreview; $report.second_instance=$secondInstance
    Add-SmokeCheck 'reopening_same_package_creates_new_real_instance' ($secondInstance -ne $firstInstance)
    Click-ChemLeap 'load-button' '^读取存档$'
    Wait-ChemLeapStatus '已读取|已恢复' -MayAuthorize
    $null=Wait-SmokeCondition { (Get-ChemLeapScore) -eq $savedScore -and (Get-ChemLeapSubstance) -eq $savedSubstance } 'score and product restored through actual SDK read' 20
    Add-SmokeCheck 'new_preview_reads_original_saved_score_and_product' $true
    $report.new_preview_readback='passed'
    $null=Save-SmokeScreenshot $product 'chemleap-new-preview-readback' -IncludeOverlays
    Click 'DeveloperPreviewClose'
    $afterHashes=Get-ChemLeapSourceHashes $GameProjectPath
    Add-SmokeCheck 'original_game_project_bytes_preserved' (($originalHashes | ConvertTo-Json -Compress) -eq ($afterHashes | ConvertTo-Json -Compress))
    Add-SmokeCheck 'unrelated_working_directory_contains_no_host_data' (-not(Test-Path -LiteralPath (Join-Path $cwd 'AutumnOS_Data')))
    Close-SmokeProduct $product
    $report.status='passed'
} catch {
    $report.failure=$_.Exception.Message; $report.failure_type=$_.Exception.GetType().FullName; $report.failure_stack=$_.ScriptStackTrace
    Write-Warning ('ChemLeap native preview smoke: '+$report.failure)
    if ($product -and -not $product.Process.HasExited) { try { $null=Save-SmokeScreenshot $product 'chemleap-failure' -IncludeOverlays } catch { } }
} finally {
    $clean=$true
    foreach ($process in $ownedProcesses) {
        try { if (-not $process.HasExited -and (-not $process.CloseMainWindow() -or -not $process.WaitForExit(10000))) { $clean=$false; $report.remaining_processes+=@{pid=$process.Id;action='left_running_no_forced_termination'} } } catch { $clean=$false }
    }
    foreach ($entry in $ownedEntryProcesses) {
        try { if (-not $entry.WaitForExit(10000)) { $clean=$false; $report.remaining_processes+=@{pid=$entry.Id;action='bootstrap_left_running'} } } catch { $clean=$false }
    }
    foreach ($entry in $cliProcesses) {
        try {
            if (-not $entry.Process.WaitForExit(5000)) { $clean=$false; $report.remaining_processes+=@{pid=$entry.Process.Id;action='owned_CLI_left_running_no_forced_termination'} }
            elseif (-not $entry.Recorded) {
                $commands.Add([ordered]@{executable=$script:chemLeapDotnet;cli=$script:chemLeapCli;arguments=$entry.Arguments;
                    started_utc=$entry.Started.ToString('o');exit_code=$entry.Process.ExitCode;stdout=$entry.Out.GetAwaiter().GetResult();stderr=$entry.Err.GetAwaiter().GetResult()})
                $entry.Recorded=$true
            }
        } catch { $clean=$false }
    }
    Restore-AutumnNativePointer
    $report.cleanup=if($clean){'passed'}else{'failed'}
    if (-not $clean) { $report.status='failed' }
    $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o'); $report.check_count=$checks.Count; $report.passed_checks=@($checks | Where-Object status -eq 'passed').Count
    if (Test-Path -LiteralPath $ReportDirectory) { $report | ConvertTo-Json -Depth 25 | Set-Content -LiteralPath (Join-Path $ReportDirectory 'chemleap-native-smoke.json') -Encoding utf8 }
    foreach ($process in $ownedProcesses) { $process.Dispose() }; foreach ($entry in $ownedEntryProcesses) { $entry.Dispose() }; foreach ($entry in $cliProcesses) { $entry.Process.Dispose() }
}
if ($report.status -ne 'passed') { throw "ChemLeap native preview test failed; evidence preserved at $ReportDirectory" }
