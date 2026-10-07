#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BuildId,[Parameter(Mandatory)][string]$ReportDirectory,[switch]$InPlace)
$ErrorActionPreference='Stop';$projectRoot=Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/Smoke-NativeAppInput.ps1"
. "$PSScriptRoot/T06-NativeHelpers.ps1"
$helperAst=Import-T06Functions (Join-Path $PSScriptRoot 'Test-DesktopInteractionSmoke.ps1')
$native=$helperAst.FindAll({param($n) $n -is [Management.Automation.Language.StringConstantExpressionAst] -and $n.Value.Contains('namespace AutumnDesktopInteractionSmoke')},$true)
if($native.Count -ne 1){throw 'Expected one maintained native input type.'}
$desktopRuntime=Join-Path $projectRoot '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
foreach($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')){Add-Type -Path (Join-Path $desktopRuntime $name)}
if(-not ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){Add-Type -TypeDefinition $native[0].Value}
$package=Get-Content -LiteralPath (Join-Path $projectRoot "artifacts/builds/$BuildId/package-single-file.json") -Raw|ConvertFrom-Json
$expectedReleaseLabel=[string]$package.release_label
if([string]::IsNullOrWhiteSpace($expectedReleaseLabel)){throw 'Package release label is missing; an exact version assertion is required.'}
$ReportDirectory=[IO.Path]::GetFullPath($ReportDirectory)
if(Test-Path -LiteralPath $ReportDirectory){throw 'Use a new report directory; previous evidence is immutable.'}
New-Item -ItemType Directory -Path $ReportDirectory|Out-Null
$testRoot=if($InPlace){Split-Path $package.executable -Parent}else{Join-Path $ReportDirectory '中文 空格'}
if(-not $InPlace){New-Item -ItemType Directory -Path $testRoot|Out-Null;Copy-Item -LiteralPath $package.executable -Destination (Join-Path $testRoot 'AutumnOS.exe')}
$programDirectory=Join-Path $testRoot $package.program_relative_path
$dataDirectory=Join-Path $testRoot 'AutumnOS_Data'
$WindowTimeoutSeconds=90
$checks=[Collections.Generic.List[object]]::new();$screenshots=[Collections.Generic.List[object]]::new();$inputActions=[Collections.Generic.List[object]]::new();$windowGeometry=[Collections.Generic.List[object]]::new()
$ownedProcesses=[Collections.Generic.List[Diagnostics.Process]]::new();$ownedEntryProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$script:initialPointerPosition=$null;$first=$null;$second=$null
$report=[ordered]@{schema_version=1;task_id='T06';checkpoint='actual_single_file_first_start_game_save_and_recall';status='failed';build_id=$BuildId;source_snapshot_id=$package.source_snapshot_id;release_label=$package.release_label;source_executable=$package.executable;tested_executable=(Join-Path $testRoot 'AutumnOS.exe');test_scope=$testRoot;started_utc=[DateTimeOffset]::UtcNow.ToString('o');checks=$checks;screenshots=$screenshots;input_actions=$inputActions;window_geometry=$windowGeometry;process_ids=@();bootstrap_process_ids=@();remaining_processes=@();cleanup='not_run';installer='deferred_by_user';public_release=$false;full_t06='in_progress';cold_start='not_run';clean_os='not_run'}
function Start-SmokeProduct([string]$WorkingDirectory){
    $clock=[Diagnostics.Stopwatch]::StartNew()
    $entry=Start-Process -FilePath $report.tested_executable -WorkingDirectory $WorkingDirectory -WindowStyle Normal -PassThru
    $null=$entry.Handle;$ownedEntryProcesses.Add($entry);$report.bootstrap_process_ids+=$entry.Id
    $started=$entry.StartTime.ToUniversalTime();$session=$entry.SessionId;$inner=$null
    $innerPath=Join-Path $programDirectory 'AutumnOS.exe'
    $null=Wait-SmokeCondition {
        $entry.Refresh();if($entry.HasExited){throw "Held single-file entry exited: $($entry.ExitCode)"}
        if(-not [string]::Equals($entry.MainModule.FileName,$report.tested_executable,[StringComparison]::OrdinalIgnoreCase)){throw 'Single-file entry identity changed.'}
        $candidates=@(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($entry.Id)"|Where-Object {[string]::Equals($_.ExecutablePath,$innerPath,[StringComparison]::OrdinalIgnoreCase) -and $_.CreationDate.ToUniversalTime() -ge $started})
        if($candidates.Count -gt 1){throw 'Multiple inner bootstrap children.'}
        if($candidates.Count -eq 1){$script:observedInner=$candidates[0];return $true};return $false
    } 'exact self-started container child'
    $inner=[Diagnostics.Process]::GetProcessById([int]$script:observedInner.ProcessId);$null=$inner.Handle
    if($inner.HasExited -or $inner.SessionId -ne $session -or [Math]::Abs(($inner.StartTime.ToUniversalTime()-$script:observedInner.CreationDate.ToUniversalTime()).TotalMilliseconds) -gt 1 -or -not [string]::Equals($inner.MainModule.FileName,$innerPath,[StringComparison]::OrdinalIgnoreCase)){throw 'Inner bootstrap identity mismatch.'}
    $ownedEntryProcesses.Add($inner);$report.bootstrap_process_ids+=$inner.Id
    $business=Resolve-SmokeBusinessProcess -EntryProcess $inner -ExecutablePath $innerPath -TimeoutSeconds 90
    $ownedProcesses.Add($business);$report.process_ids+=$business.Id
    $null=$business.WaitForInputIdle(90000)
    $handle=Wait-SmokeCondition {$business.Refresh();if($business.HasExited){throw 'Client exited before native window.'};if($business.MainWindowHandle -ne [IntPtr]::Zero){return $business.MainWindowHandle};return $null} 'owned WinUI window'
    $element=[Windows.Automation.AutomationElement]::FromHandle($handle)
    $null=Wait-SmokeCondition {(Find-VisibleSmokeElement $element 'HelloNextButton') -or (Find-VisibleSmokeElement $element 'SampleButton')} 'actual first-run or restored desktop'
    Add-SmokeCheck 'exact_nested_client_owns_native_window' ($element.Current.ProcessId -eq $business.Id)
    $report.startup_observations=@($report.startup_observations)+@([ordered]@{seconds=$clock.Elapsed.TotalSeconds;cache_state='ordinary cache; first includes runtime extraction';working_directory=$WorkingDirectory})
    return [pscustomobject]@{Process=$business;EntryProcess=$entry;InnerProcess=$inner;IsBootstrap=$true;Handle=$handle;Element=$element}
}
function Assert-OneDataFolder([string]$Label){
    $items=@(Get-ChildItem -LiteralPath $testRoot -Force)
    Add-SmokeCheck ($Label+'_only_exe_and_one_data_folder') ($items.Count -eq 2 -and @($items|Where-Object {$_.Name -eq 'AutumnOS.exe' -and -not $_.PSIsContainer}).Count -eq 1 -and @($items|Where-Object {$_.Name -eq 'AutumnOS_Data' -and $_.PSIsContainer}).Count -eq 1)
    Add-SmokeCheck ($Label+'_no_nested_data_root') (!(Test-Path -LiteralPath (Join-Path $programDirectory 'AutumnOS_Data')))
}
try{
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent();$principal=[Security.Principal.WindowsPrincipal]::new($identity)
    Add-SmokeCheck 'standard_user' (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
    if(@(Get-Process -Name AutumnOS,AutumnOS.Client -ErrorAction SilentlyContinue|Where-Object SessionId -eq ([Diagnostics.Process]::GetCurrentProcess().SessionId)).Count){throw 'Existing user product instance; test refuses to activate or close it.'}
    Add-SmokeCheck 'source_single_exe_hash_matches_package' ((Get-FileHash -LiteralPath $report.tested_executable).Hash.ToLowerInvariant() -eq $package.executable_sha256)
    Add-SmokeCheck 'before_first_start_only_one_exe' (@(Get-ChildItem -LiteralPath $testRoot -Force).Count -eq 1 -and !(Test-Path -LiteralPath $dataDirectory))
    Add-SmokeCheck 'compiled_product_version_exact' ((Get-Item -LiteralPath $report.tested_executable).VersionInfo.ProductVersion -eq $expectedReleaseLabel)
    $working=Join-Path $ReportDirectory 'unrelated-working-directory';New-Item -ItemType Directory -Path $working|Out-Null
    $first=Start-SmokeProduct $working
    Assert-OneDataFolder 'first_start'
    Add-SmokeCheck 'working_directory_receives_no_data' (!(Test-Path -LiteralPath (Join-Path $working 'AutumnOS_Data')))
    Add-SmokeCheck 'normal_runtime_contains_no_pdb' (@(Get-ChildItem -LiteralPath $programDirectory -File -Recurse -Filter '*.pdb').Count -eq 0)
    $manifest=Get-Content -LiteralPath (Join-Path $package.evidence 'delivery-hashes.json') -Raw|ConvertFrom-Json
    foreach($item in $manifest){if((Get-FileHash -LiteralPath (Join-Path $programDirectory $item.path)).Hash.ToLowerInvariant() -ne $item.sha256){throw "Extracted file mismatch: $($item.path)"}}
    Add-SmokeCheck 'all_embedded_files_match_frozen_delivery' $true
    $report.verified_extracted_files=$manifest.Count
    foreach($dependency in @('coreclr.dll','Microsoft.UI.Xaml.dll','AutumnOS.Client.pri','App.xbf','MainWindow.xbf','WebView2Runtime/msedgewebview2.exe','ThirdPartyLicenses/LICENSE.txt','Developer/SDK/autumn-sdk.js','Developer/Templates/hello-app/manifest.json')){Add-SmokeCheck ('embedded_'+$dependency.Replace('/','_')) (Test-Path -LiteralPath (Join-Path $programDirectory $dependency))}
    $assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $programDirectory 'AutumnOS.Contracts.dll')))
    $metadata=@{};foreach($attribute in $assembly.GetCustomAttributesData()){if($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute'){$metadata[$attribute.ConstructorArguments[0].Value]=$attribute.ConstructorArguments[1].Value}}
    Add-SmokeCheck 'client_is_actual_current_build_and_source' ($metadata.BuildId -eq $BuildId -and $metadata.SourceSnapshotId -eq $package.source_snapshot_id)
    Add-SmokeCheck 'first_launch_starts_at_real_hello' ($null -ne (Find-VisibleSmokeElement $first.Element 'HelloNextButton'))
    $null=Save-SmokeScreenshot $first '01-single-file-hello'
    Invoke-SmokeButton $first.Element 'HelloNextButton';$null=Wait-SmokeCondition {Find-VisibleSmokeElement $first.Element 'BrandNextButton'} 'brand stage'
    Invoke-SmokeButton $first.Element 'BrandNextButton';$null=Wait-SmokeCondition {Find-VisibleSmokeElement $first.Element 'PrepareDesktopButton'} 'preparation stage'
    Invoke-SmokeButton $first.Element 'PrepareDesktopButton';Wait-SmokeDesktop $first
    Add-SmokeCheck 'real_first_run_committed' ((Get-Content -LiteralPath (Join-Path $dataDirectory 'Config/first-run.json') -Raw|ConvertFrom-Json).checkpoint -eq 'Completed')
    Invoke-SmokeButton $first.Element 'SettingsButton'
    $about=Wait-SmokeCondition {Find-VisibleSmokeElement $first.Element 'SettingsNavAbout'} 'about navigation'
    $about.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    $null=Wait-SmokeCondition {Find-VisibleSmokeElement $first.Element 'AboutVersion'} 'about version'
    Add-SmokeCheck 'actual_about_version_exact' ((Find-SmokeElement $first.Element 'AboutVersion').Current.Name -eq $expectedReleaseLabel)
    $null=Save-SmokeScreenshot $first '02-single-file-about'
    Invoke-SmokeButton $first.Element 'SettingsHomeButton';Wait-SmokeDesktop $first
    Invoke-SmokePointer $first 'SampleButton' 'single_click'
    $runtimeLog=Join-Path $dataDirectory 'Logs/runtime-events.jsonl'
    $null=Wait-SmokeCondition {@(Read-SmokeEvents $runtimeLog|Where-Object state -eq 'Foreground').Count -gt 0} 'real internal game'
    $instance=@(Read-SmokeEvents $runtimeLog|Where-Object state -eq 'Foreground')[-1].instanceId
    $nickname=Wait-SmokeCondition {Find-SmokeNamedElement $first.Element '玩家昵称' ([Windows.Automation.ControlType]::Edit)} 'real WebView input'
    $nickname.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('派蒙·单文件验证')
    Invoke-SmokeNamedButton $first.Element '第 1 张卡片，未翻开';Invoke-SmokeNamedButton $first.Element '第 2 张卡片，未翻开';Invoke-SmokeNamedButton $first.Element '保存进度'
    $null=Wait-SmokeCondition {Find-SmokeNamedElement $first.Element '允许' ([Windows.Automation.ControlType]::Button)} 'native permission overlay'
    $null=Save-SmokeScreenshot $first '03-single-file-permission'
    Invoke-SmokeNamedButton $first.Element '允许'
    $savePath=Join-Path $dataDirectory 'Saves/cn.labchronicles.elementpairs/guest/game.json'
    $null=Wait-SmokeCondition {Test-Path -LiteralPath $savePath} 'SDK guest save'
    $save=Get-Content -LiteralPath $savePath -Raw|ConvertFrom-Json
    Add-SmokeCheck 'guest_save_commits_to_the_only_data_folder' ($save.value.nickname -eq '派蒙·单文件验证' -and $save.value.moves -ge 1)
    $saveHash=(Get-FileHash -LiteralPath $savePath).Hash.ToLowerInvariant()
    $nickname.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('未保存的单文件输入')
    Invoke-SmokeButton $first.Element 'BackgroundButton';Wait-SmokeDesktop $first
    $events=@(Read-SmokeEvents $runtimeLog|Where-Object instanceId -eq $instance)
    Add-SmokeCheck 'background_game_still_blocks_maintenance' ($events[-1].state -eq 'Background' -and $events[-1].blocksMaintenance)
    $pidBefore=$first.Process.Id;$hwndBefore=$first.Handle
    $repeat=Start-Process -FilePath $report.tested_executable -WorkingDirectory $working -WindowStyle Normal -PassThru;$null=$repeat.Handle;$ownedEntryProcesses.Add($repeat);$report.bootstrap_process_ids+=$repeat.Id
    Add-SmokeCheck 'repeat_single_exe_forwards_and_exits_normally' ($repeat.WaitForExit(90000) -and $repeat.ExitCode -eq 0)
    $first.Process.Refresh();Add-SmokeCheck 'repeat_keeps_original_pid_and_window' (!$first.Process.HasExited -and $first.Process.Id -eq $pidBefore -and $first.Process.MainWindowHandle -eq $hwndBefore)
    Invoke-SmokePointer $first 'SampleButton' 'single_click'
    $null=Wait-SmokeCondition {Find-SmokeNamedElement $first.Element '玩家昵称' ([Windows.Automation.ControlType]::Edit)} 'same live input after continue'
    $input=Find-SmokeNamedElement $first.Element '玩家昵称' ([Windows.Automation.ControlType]::Edit)
    Add-SmokeCheck 'continue_keeps_unsaved_input' ($input.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '未保存的单文件输入')
    Add-SmokeCheck 'continue_keeps_exact_instance' (@(Read-SmokeEvents $runtimeLog|Where-Object state -eq 'Foreground')[-1].instanceId -eq $instance)
    $null=Save-SmokeScreenshot $first '04-single-file-real-game'
    Invoke-SmokeButton $first.Element 'CloseGameButton';Invoke-SmokeNamedButton $first.Element '确认结束'
    $null=Wait-SmokeCondition {@(Read-SmokeEvents $runtimeLog|Where-Object {$_.instanceId -eq $instance -and $_.state -eq 'Closed' -and !$_.blocksMaintenance}).Count -gt 0} 'real game resources released'
    Add-SmokeCheck 'true_end_releases_maintenance_blocker' $true
    Close-SmokeProduct $first
    Add-SmokeCheck 'inner_bootstrap_exits_after_normal_client_close' ($first.InnerProcess.WaitForExit(10000) -and $first.InnerProcess.ExitCode -eq 0)
    $second=Start-SmokeProduct $working;Wait-SmokeDesktop $second
    Add-SmokeCheck 'restart_retains_exact_save_bytes' ((Get-FileHash -LiteralPath $savePath).Hash.ToLowerInvariant() -eq $saveHash)
    Assert-OneDataFolder 'restart'
    Add-SmokeCheck 'single_exe_never_modified_by_first_start' ((Get-FileHash -LiteralPath $report.tested_executable).Hash.ToLowerInvariant() -eq $package.executable_sha256)
    $null=Save-SmokeScreenshot $second '05-single-file-restored-desktop'
    Close-SmokeProduct $second
    Add-SmokeCheck 'restarted_inner_bootstrap_exits_normally' ($second.InnerProcess.WaitForExit(10000) -and $second.InnerProcess.ExitCode -eq 0)
    $report.status='passed'
}catch{
    $report.failure_type=$_.Exception.GetType().FullName;$report.failure=$_.Exception.Message
    Write-Warning $report.failure
    $failed=if($first -and !$first.Process.HasExited){$first}elseif($second -and !$second.Process.HasExited){$second}else{$null}
    if($failed){$null=Save-SmokeScreenshot $failed '99-single-file-failure' -IncludeOverlays}
}finally{
    foreach($process in $ownedProcesses){if(!$process.HasExited){$null=$process.CloseMainWindow();$null=$process.WaitForExit(10000)}}
    foreach($process in $ownedEntryProcesses){$null=$process.WaitForExit(10000)}
    $remaining=@($ownedProcesses)+@($ownedEntryProcesses)|Where-Object {!$_.HasExited}
    $report.remaining_processes=@($remaining|ForEach-Object {$_.Id})
    $report.cleanup=if($report.remaining_processes.Count -eq 0){'passed'}else{'failed'}
    $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o');$report.check_count=$checks.Count;$report.passed_checks=@($checks|Where-Object status -eq 'passed').Count
    $report|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $ReportDirectory 'single-file-smoke.json') -Encoding utf8NoBOM
    foreach($process in @($ownedProcesses)+@($ownedEntryProcesses)){$process.Dispose()}
}
if($report.status -ne 'passed' -or $report.cleanup -ne 'passed'){throw "Single-file smoke failed; report preserved at $ReportDirectory"}
Write-Output "Single-file native smoke: $($checks.Count) checks passed."
