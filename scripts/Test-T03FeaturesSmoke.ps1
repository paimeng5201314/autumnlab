[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExecutablePath,[string]$ReportDirectory,[ValidateRange(10,90)][int]$WindowTimeoutSeconds=40)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$smokeId='T03-features-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$scope=Join-Path $projectRoot "artifacts/smoke/$smokeId"
$stageDirectory=Join-Path $scope '受控样例 原生权限检查'
if(-not $ReportDirectory){$ReportDirectory=$scope}
$ReportDirectory=[IO.Path]::GetFullPath($ReportDirectory)
$reportPath=Join-Path $ReportDirectory 'features-smoke.json'
if(Test-Path -LiteralPath $reportPath){throw 'Existing evidence is preserved; choose a new report directory.'}
New-Item -ItemType Directory -Path $ReportDirectory -Force|Out-Null
$checks=[Collections.Generic.List[object]]::new();$screenshots=[Collections.Generic.List[object]]::new()
$inputActions=[Collections.Generic.List[object]]::new();$ownedProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$script:initialPointerPosition=$null;$product=$null
$report=[ordered]@{
 schema_version=1;task_id='T03';checkpoint='controlled_sample_permissions_desktop_extensions_developer_ui'
 status='failed';smoke_id=$smokeId;started_utc=[DateTimeOffset]::UtcNow.ToString('o');environment=[Environment]::OSVersion.VersionString
 source_snapshot_id='not_recorded';build_id='local-untracked';source_executable=$null;tested_executable=$null;source_executable_sha256=$null
 process_ids=@();checks=$checks;screenshots=$screenshots;input_actions=$inputActions;game_instance_id=$null
 account_mode='guest';real_logto_login='not_run';second_account='not_run';external_picker_manual_matrix='not_run'
 complete_t03='not_run';arbitrary_third_party_isolation='not_run';physical_input='not_run'
 developer_checks_kind='explicit_isolated_synthetic_runtime_checks_no_real_identity';helper_hashes=@();cleanup='not_run'
}

# Reuse reviewed local helper definitions, never execute the old smoke script's test body.
$helperPath=Join-Path $PSScriptRoot 'Test-DesktopInteractionSmoke.ps1'
$helperTokens=$null;$helperErrors=$null
$helperAst=[Management.Automation.Language.Parser]::ParseFile($helperPath,[ref]$helperTokens,[ref]$helperErrors)
if($helperErrors.Count){throw 'Existing desktop helper source did not parse.'}
$names=@('Add-SmokeCheck','Wait-SmokeCondition','Find-SmokeElement','Find-VisibleSmokeElement','Invoke-SmokeButton',
 'Find-SmokeMenuItem','Invoke-SmokeOwnedButton','Find-SmokeNamedElement','Invoke-SmokeNamedButton','Test-SmokeForeground',
 'Focus-SmokeProduct','Assert-SmokeCaptureUnoccluded','Start-SmokeProduct','Close-SmokeProduct','Save-SmokeScreenshot',
 'Invoke-SmokePointer','Wait-SmokeRunningPanel','Read-SmokeEvents')
foreach($name in $names){
 $definition=@($helperAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -EQ $name)
 if($definition.Count -ne 1){throw "Missing unique local helper: $name"}
 Invoke-Expression $definition[0].Extent.Text
}
$nativeDefinition=@($helperAst.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst]},$true)|Where-Object { $_.Value.Contains('namespace AutumnDesktopInteractionSmoke {') })
if($nativeDefinition.Count -ne 1){throw 'Missing reviewed local Win32 helper definition.'}
$report.helper_hashes=@(@{path=$helperPath;sha256=(Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash.ToLowerInvariant()},
 @{path=(Join-Path $PSScriptRoot 'Smoke-NativeAppInput.ps1');sha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Smoke-NativeAppInput.ps1') -Algorithm SHA256).Hash.ToLowerInvariant()})

function Select-FeatureCategory([string]$Id){
 $item=Wait-SmokeCondition {Find-SmokeElement $product.Element $Id} "settings category $Id"
 $scroll=$null;if($item.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern,[ref]$scroll)){$scroll.ScrollIntoView()}
 $toggle=$item.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
 if($toggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::On){$toggle.Toggle()}
}
function Set-FeatureToggle([string]$Id,[bool]$On){
 $item=Wait-SmokeCondition {Find-SmokeElement $product.Element $Id} "toggle $Id"
 $scroll=$null;if($item.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern,[ref]$scroll)){$scroll.ScrollIntoView()}
 $toggle=$item.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
 if(($toggle.Current.ToggleState -eq [Windows.Automation.ToggleState]::On) -ne $On){$toggle.Toggle()}
}
function Find-FeatureWebDocument {
 $condition=[Windows.Automation.AndCondition]::new(
  [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'元素配对 · Lab Chronicles'),
  [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Document))
 $documents=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants,$condition)
 $bounds=$product.Element.Current.BoundingRectangle
 foreach($document in $documents){
  $r=$document.Current.BoundingRectangle
  if($r.IsEmpty -or $r.Left -lt $bounds.Left -or $r.Top -lt $bounds.Top -or $r.Right -gt $bounds.Right -or $r.Bottom -gt $bounds.Bottom){continue}
  if(Test-AutumnOwnedInputProcess $document.Current.ProcessId $product.Process){return $document}
 }
 return $null
}
function Find-FeatureWebElement([string]$Id,[string]$Name=''){
 $document=Find-FeatureWebDocument;if(-not $document){return $null}
 if($Id){$item=Find-SmokeElement $document $Id;if($item){return $item}}
 if($Name){return $document.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name))}
 return $null
}
function Feature-TextContains([string]$Id,[string]$Text){
 $document=Find-FeatureWebDocument;if(-not $document){return $false}
 $item=Find-SmokeElement $document $Id
 if($item -and $item.Current.Name.Contains($Text)){return $true}
 $texts=$document.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Text))
 foreach($candidate in $texts){if($candidate.Current.Name.Contains($Text)){return $true}}
 return $false
}
function Click-FeatureWeb([string]$Id,[string]$Name){
 $button=Wait-SmokeCondition {Find-FeatureWebElement $Id $Name} "sample action $Name"
 $evidence=Invoke-AutumnNativeElementClick -WindowElement $product.Element -Element $button -Process $product.Process
 $inputActions.Add($evidence)
}
function Wait-FeatureText([string]$Id,[string]$Text){$null=Wait-SmokeCondition {Feature-TextContains $Id $Text} "safe sample result $Text"}
function Return-FeatureGame {
 Invoke-SmokeButton $product.Element 'SettingsHomeButton'
 Invoke-SmokePointer $product 'SampleButton' 'double_click'
 Wait-SmokeRunningPanel $product
 Invoke-SmokeOwnedButton $product 'RunningContinueButton'
 $null=Wait-SmokeCondition {Find-FeatureWebDocument} 'original game returns'
}
function Permission-Record([string]$Name,[string]$Decision){
 $path=Join-Path $stageDirectory 'AutumnOS_Data/Config/application-permissions.v1.json'
 if(-not(Test-Path -LiteralPath $path)){return $false}
 $records=(Get-Content -LiteralPath $path -Raw|ConvertFrom-Json).records
 return @($records|Where-Object {$_.accountKey -eq 'guest' -and $_.appId -eq 'cn.labchronicles.elementpairs' -and $_.permission -eq $Name -and $_.decision -eq $Decision}).Count -eq 1
}

try{
 if(-not $IsWindows){throw 'An interactive Windows desktop is required.'}
 if(@(Get-Process -Name AutumnOS -ErrorAction SilentlyContinue).Count){throw 'An existing user launcher is open; ask for normal closure. No process is stopped.'}
 Add-SmokeCheck 'no_existing_user_launcher' $true
 $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
 Add-SmokeCheck 'standard_user' (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
 $ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath)
 if([IO.Path]::GetFileName($ExecutablePath) -ne 'AutumnOS.exe' -or -not(Test-Path -LiteralPath $ExecutablePath -PathType Leaf)){throw 'An existing AutumnOS.exe is required.'}
 $report.source_executable=$ExecutablePath;$report.source_executable_sha256=(Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
 New-Item -ItemType Directory -Path $stageDirectory -Force|Out-Null
 foreach($entry in Get-ChildItem -LiteralPath (Split-Path $ExecutablePath -Parent) -Force){
  if($entry.Name -eq 'AutumnOS_Data'){continue}
  if(($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Refusing redirected build output.'}
  if($entry.PSIsContainer -and @(Get-ChildItem -LiteralPath $entry.FullName -Force -Recurse|Where-Object {($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0}).Count){throw 'Refusing redirected child resource.'}
  Copy-Item -LiteralPath $entry.FullName -Destination $stageDirectory -Recurse
 }
 $report.tested_executable=Join-Path $stageDirectory 'AutumnOS.exe'
 Add-SmokeCheck 'isolated_copy_has_no_user_data' (-not(Test-Path -LiteralPath (Join-Path $stageDirectory 'AutumnOS_Data')))
 Add-SmokeCheck 'isolated_copy_matches_exe' ((Get-FileHash -LiteralPath $report.tested_executable -Algorithm SHA256).Hash.ToLowerInvariant() -eq $report.source_executable_sha256)
 $assembly=[Reflection.Assembly]::LoadFrom((Join-Path $stageDirectory 'AutumnOS.Contracts.dll'))
 foreach($attribute in $assembly.GetCustomAttributesData()){
  if($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute'){
   if($attribute.ConstructorArguments[0].Value -eq 'BuildId'){$report.build_id=$attribute.ConstructorArguments[1].Value}
   if($attribute.ConstructorArguments[0].Value -eq 'SourceSnapshotId'){$report.source_snapshot_id=$attribute.ConstructorArguments[1].Value}
  }
 }
 $desktopRuntime=Join-Path $projectRoot '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
 foreach($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')){Add-Type -Path (Join-Path $desktopRuntime $name)}
 if(-not('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){Add-Type -TypeDefinition $nativeDefinition[0].Value}
 . (Join-Path $PSScriptRoot 'Smoke-NativeAppInput.ps1')
 $product=Start-SmokeProduct $scope
 Invoke-SmokeButton $product.Element 'HelloNextButton';Invoke-SmokeButton $product.Element 'BrandNextButton';Invoke-SmokeButton $product.Element 'PrepareDesktopButton'
 $null=Wait-SmokeCondition {Find-VisibleSmokeElement $product.Element 'SampleButton'} 'installed sample desktop'
 Add-SmokeCheck 'developer_entry_absent_by_default' ($null -eq (Find-VisibleSmokeElement $product.Element 'DeveloperButton'))
 Invoke-SmokeButton $product.Element 'SettingsButton';Select-FeatureCategory 'SettingsNavDiagnostics';Set-FeatureToggle 'DeveloperModeToggle' $true
 $configuration=Join-Path $stageDirectory 'AutumnOS_Data/Config/developer-mode.json'
 $null=Wait-SmokeCondition {(Test-Path -LiteralPath $configuration) -and (Get-Content -LiteralPath $configuration -Raw|ConvertFrom-Json).values.enabled} 'persisted developer enable'
 Add-SmokeCheck 'developer_mode_really_persisted' ((Get-Content -LiteralPath $configuration -Raw|ConvertFrom-Json).values.enabled -eq $true)
 Invoke-SmokeButton $product.Element 'SettingsHomeButton'
 $null=Wait-SmokeCondition {Find-VisibleSmokeElement $product.Element 'DeveloperButton'} 'developer desktop entry after layout'
 Add-SmokeCheck 'developer_entry_created_after_enable' ($null -ne (Find-VisibleSmokeElement $product.Element 'DeveloperButton'))
 Invoke-SmokeButton $product.Element 'SampleButton'
 $null=Wait-SmokeCondition {Find-FeatureWebDocument} 'native controlled sample WebView'
 $runtimeLog=Join-Path $stageDirectory 'AutumnOS_Data/Logs/runtime-events.jsonl'
 $events=Read-SmokeEvents $runtimeLog;$report.game_instance_id=@($events|Where-Object state -EQ 'Foreground')[-1].instanceId
 Click-FeatureWeb 't03-features' '账号、文件与桌面联动'
 Wait-FeatureText 't03-events' '当前数据空间：游客'
 Add-SmokeCheck 'sample_uses_real_guest_capability_snapshot' (Feature-TextContains 't03-events' '当前数据空间：游客')
 Click-FeatureWeb 't03-storage-permit' '申请私有存储权限'
 Invoke-SmokeNamedButton $product.Element '拒绝'
 Wait-FeatureText 't03-storage-result' 'storage：denied'
 Add-SmokeCheck 'native_permission_denial_persisted' (Permission-Record 'storage' 'Denied')
 Click-FeatureWeb 't03-private-save' '写入私有文件'
 Wait-FeatureText 't03-storage-result' 'PERMISSION_DENIED'
 Add-SmokeCheck 'denied_private_write_is_rejected' (Feature-TextContains 't03-storage-result' 'PERMISSION_DENIED')
 Click-FeatureWeb 't03-profile-read' '读取已授权资料'
 Wait-FeatureText 't03-profile-result' 'AUTH_REQUIRED'
 Add-SmokeCheck 'guest_profile_not_simulated' (Feature-TextContains 't03-profile-result' 'AUTH_REQUIRED')
 $null=Save-SmokeScreenshot $product '01-guest-profile-and-denial'
 Click-FeatureWeb 't03-notifications-permit' '申请通知权限';Invoke-SmokeNamedButton $product.Element '允许'
 Wait-FeatureText 't03-notifications-result' 'notifications：granted'
 Add-SmokeCheck 'native_notification_grant_persisted' (Permission-Record 'notifications' 'Granted')
 Click-FeatureWeb 't03-notify' '发送配对进度';Wait-FeatureText 't03-notifications-result' '通知已交给桌面通知中心'
 Click-FeatureWeb 't03-notify-duplicate' '重试上一条通知';Wait-FeatureText 't03-notifications-result' '重复通知已被去重'
 Add-SmokeCheck 'notification_dedup_is_real' (Feature-TextContains 't03-notifications-result' '重复通知已被去重')
 Click-FeatureWeb 't03-widgets-permit' '申请小组件权限';Invoke-SmokeNamedButton $product.Element '允许'
 Wait-FeatureText 't03-desktop-result' 'widgets：granted'
 Click-FeatureWeb 't03-widget' '更新配对进度小组件';Wait-FeatureText 't03-desktop-result' '已更新真实桌面小组件'
 Add-SmokeCheck 'widget_sdk_commit_success' (Feature-TextContains 't03-desktop-result' '已更新真实桌面小组件')
 $null=Save-SmokeScreenshot $product '02-real-desktop-extension-actions'
 Invoke-SmokeButton $product.Element 'BackgroundButton'
 $null=Wait-SmokeCondition {Find-VisibleSmokeElement $product.Element 'SampleButton'} 'background desktop'
 # StackPanel itself has no ControlView peer; assert the real visible native widget title instead.
 $widgetTitle=Wait-SmokeCondition {Find-SmokeNamedElement $product.Element '配对进度' ([Windows.Automation.ControlType]::Text)} 'visible native desktop widget title'
 Add-SmokeCheck 'widget_visible_on_real_native_desktop' ($null -ne $widgetTitle -and -not $widgetTitle.Current.IsOffscreen)
 Add-SmokeCheck 'notification_count_visible_in_system_status' ((Find-SmokeElement $product.Element 'NotificationsButton').Current.Name -match '1')
 $null=Save-SmokeScreenshot $product '03-desktop-widget-notification-running-game'
 Invoke-SmokeButton $product.Element 'NotificationsButton'
 $null=Wait-SmokeCondition {Find-VisibleSmokeElement $product.Element 'MuteSampleNotifications'} 'real notification settings'
 $null=Save-SmokeScreenshot $product '04-real-notification-center'
 Select-FeatureCategory 'SettingsNavPermissions'
 Invoke-SmokeButton $product.Element 'Permission-notifications-Revoked'
 Invoke-SmokeButton $product.Element 'Permission-identity.profile-Denied'
 Invoke-SmokeButton $product.Element 'Permission-storage-Granted'
 Add-SmokeCheck 'settings_revocation_persisted' (Permission-Record 'notifications' 'Revoked')
 Add-SmokeCheck 'settings_profile_denial_persisted' (Permission-Record 'identity.profile' 'Denied')
 Add-SmokeCheck 'settings_storage_allow_persisted' (Permission-Record 'storage' 'Granted')
 $null=Save-SmokeScreenshot $product '05-permissions-real-decisions'
 Return-FeatureGame
 Click-FeatureWeb 't03-notify' '发送配对进度';Wait-FeatureText 't03-notifications-result' 'PERMISSION_REVOKED'
 Add-SmokeCheck 'revoked_notification_request_rejected' (Feature-TextContains 't03-notifications-result' 'PERMISSION_REVOKED')
 $note=Wait-SmokeCondition {Find-FeatureWebElement 't03-note' '笔记内容（最多 240 字符）'} 'private note input'
 $note.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('派蒙 · T03 本账号笔记')
 Click-FeatureWeb 't03-preference-save' '保存笔记设置';Wait-FeatureText 't03-storage-result' '笔记设置已保存'
 Click-FeatureWeb 't03-private-save' '写入私有文件';Wait-FeatureText 't03-storage-result' '已原子写入本应用私有文件'
 $privateFiles=@(Get-ChildItem -LiteralPath (Join-Path $stageDirectory 'AutumnOS_Data/AppData') -Filter field_note.bin -Recurse -File)
 Add-SmokeCheck 'private_note_really_written_to_isolated_account_data' ($privateFiles.Count -eq 1 -and [IO.File]::ReadAllText($privateFiles[0].FullName) -eq '派蒙 · T03 本账号笔记')
 Invoke-SmokeButton $product.Element 'BackgroundButton';Invoke-SmokeButton $product.Element 'DeveloperButton'
 Invoke-SmokeButton $product.Element 'DeveloperRefreshTrace'
 $trace=Find-SmokeElement $product.Element 'DeveloperSdkTrace'
 $traceText=$trace.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
 Add-SmokeCheck 'developer_trace_contains_real_sdk_calls' ($traceText.Contains('notifications.show') -and $traceText.Contains('widgets.update'))
 Add-SmokeCheck 'developer_trace_excludes_user_note_content' (-not $traceText.Contains('本账号笔记'))
 Invoke-SmokeButton $product.Element 'DeveloperRunChecks'
 $null=Wait-SmokeCondition {(Find-SmokeElement $product.Element 'DeveloperResult').Current.Name.Contains('7 项通过')} 'actual isolated developer lifecycle checks'
 Add-SmokeCheck 'developer_diagnostic_runs_seven_real_isolated_checks' ((Find-SmokeElement $product.Element 'DeveloperResult').Current.Name.Contains('7 项通过'))
 $developerChecksElement=Find-SmokeElement $product.Element 'DeveloperRunChecks'
 $null=Save-SmokeScreenshot $product '06-real-developer-tools-and-redacted-trace'
 Invoke-SmokeButton $product.Element 'DeveloperHome';Invoke-SmokeButton $product.Element 'SettingsButton';Select-FeatureCategory 'SettingsNavDiagnostics'
 Set-FeatureToggle 'DeveloperModeToggle' $false
 $null=Wait-SmokeCondition {-not (Get-Content -LiteralPath $configuration -Raw|ConvertFrom-Json).values.enabled} 'persisted developer disable'
 Add-SmokeCheck 'developer_disable_really_persisted' (-not (Get-Content -LiteralPath $configuration -Raw|ConvertFrom-Json).values.enabled)
 try { Add-SmokeCheck 'developer_capability_button_disabled_when_hidden' (-not $developerChecksElement.Current.IsEnabled) }
 catch [Windows.Automation.ElementNotAvailableException] {
  $checks.Add([ordered]@{name='developer_capability_button_disabled_when_hidden';status='not_run';detail='The collapsed UI provider retired its element; service revocation is covered by separate runtime tests, not inferred here.'})
 }
 Return-FeatureGame
 Add-SmokeCheck 'developer_mode_off_does_not_end_normal_game' ((Read-SmokeEvents $runtimeLog|Where-Object state -EQ 'Foreground'|Select-Object -Last 1).instanceId -eq $report.game_instance_id)
 Click-FeatureWeb 't03-preference-read' '读取笔记设置';Wait-FeatureText 't03-storage-result' '已读取本账号笔记设置'
 $note=Find-FeatureWebElement 't03-note' '笔记内容（最多 240 字符）'
 Add-SmokeCheck 'normal_game_note_survives_developer_disable' ($note.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '派蒙 · T03 本账号笔记')
 Invoke-SmokeButton $product.Element 'BackgroundButton'
 Add-SmokeCheck 'developer_entry_removed_after_disable' ($null -eq (Find-VisibleSmokeElement $product.Element 'DeveloperButton'))
 Add-SmokeCheck 'normal_game_still_has_running_badge' ($null -ne (Find-VisibleSmokeElement $product.Element 'SampleRunningBadge'))
 $null=Save-SmokeScreenshot $product '07-developer-disabled-normal-game-preserved'
 Close-SmokeProduct $product
 $report.status='passed'
}catch{
 $report.error_type=$_.Exception.GetType().Name;$report.error=$_.Exception.Message;$report.failure_script_line=$_.InvocationInfo.ScriptLineNumber
 Write-Warning "T03 features smoke failed: $($_.Exception.Message)"
}finally{
 $left=@()
 foreach($process in $ownedProcesses){
  if(-not $process.HasExited){try{if(-not $process.CloseMainWindow() -or -not $process.WaitForExit(10000)){$left+=$process.Id}}catch{$left+=$process.Id}}
 }
 if(Get-Command Restore-AutumnNativePointer -ErrorAction SilentlyContinue){Restore-AutumnNativePointer}
 if($script:initialPointerPosition -and ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){$null=[AutumnDesktopInteractionSmoke.NativeWindows]::SetCursorPos($script:initialPointerPosition.X,$script:initialPointerPosition.Y)}
 $report.cleanup=if($left.Count){'failed_owned_window_left_no_kill'}else{'passed'};$report.remaining_owned_process_ids=$left
 if($left.Count){$report.status='failed'}
 $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o')
 $report|ConvertTo-Json -Depth 14|Set-Content -LiteralPath $reportPath -Encoding utf8
 Write-Host "REPORT $reportPath"
}
if($report.status -ne 'passed'){throw "T03 feature smoke failed; inspect $reportPath"}
