[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExecutablePath,[string]$ReportDirectory,[ValidateRange(10,90)][int]$WindowTimeoutSeconds=40)
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
$smokeId='T03-recovery-files-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$scope=Join-Path $projectRoot "artifacts/smoke/$smokeId"
$stageDirectory=Join-Path $scope '受控样例 恢复与选择器检查'
if(-not $ReportDirectory){$ReportDirectory=$scope}
$ReportDirectory=[IO.Path]::GetFullPath($ReportDirectory)
$reportPath=Join-Path $ReportDirectory 'recovery-files-smoke.json'
if(Test-Path -LiteralPath $reportPath){throw 'Existing evidence is preserved; choose a new report directory.'}
New-Item -ItemType Directory -Path $ReportDirectory -Force|Out-Null
$checks=[Collections.Generic.List[object]]::new();$screenshots=[Collections.Generic.List[object]]::new()
$inputActions=[Collections.Generic.List[object]]::new();$ownedProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$script:initialPointerPosition=$null;$product=$null
$report=[ordered]@{
 schema_version=1;task_id='T03';checkpoint='native_save_configuration_recovery_and_file_picker'
 status='failed';smoke_id=$smokeId;started_utc=[DateTimeOffset]::UtcNow.ToString('o');environment=[Environment]::OSVersion.VersionString
 source_snapshot_id='not_recorded';build_id='local-untracked';source_executable=$null;tested_executable=$null;source_executable_sha256=$null
 process_ids=@();checks=$checks;screenshots=$screenshots;input_actions=$inputActions;game_instance_id=$null
 account_mode='guest';real_logto_login='not_run';second_account='not_run';external_picker_manual_matrix='not_run'
 complete_t03='not_run';arbitrary_third_party_isolation='not_run';physical_input='not_run'
 recovery_kind='actual_UI_confirmations_and_real_storage_no_mock_identity';picker_cases='not_run';helper_hashes=@();cleanup='not_run'
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
 if($item -and ([string]$item.Current.Name).Contains($Text)){return $true}
 $texts=$document.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Text))
 foreach($candidate in $texts){if(([string]$candidate.Current.Name).Contains($Text)){return $true}}
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

# Limit picker automation to modal windows owned by this exact product HWND and PID.
# Never enumerate filenames or capture a system picker, which may show personal folders.
function Find-RecoveryPicker {
 if(-not $product -or $product.Process.HasExited){return $null}
 $condition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Window)
 $windows=@([Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$condition))
 $windows+=@($product.Element.FindAll([Windows.Automation.TreeScope]::Descendants,$condition))
 foreach($window in $windows){
  if($window.Current.ProcessId -ne $product.Process.Id -or $window.Current.ClassName -ne '#32770' -or $window.Current.IsOffscreen){continue}
  $handle=[IntPtr]$window.Current.NativeWindowHandle
  $owner=[AutumnDesktopInteractionSmoke.NativeWindows]::GetWindow($handle,4)
  for($depth=0;$depth -lt 16 -and $owner -ne [IntPtr]::Zero;$depth++){
   if($owner -eq $product.Handle){return $window}
   $owner=[AutumnDesktopInteractionSmoke.NativeWindows]::GetWindow($owner,4)
  }
 }
 return $null
}
function Wait-RecoveryPicker([string]$Description){
 return Wait-SmokeCondition {
  $dialog=Find-RecoveryPicker;if($dialog){return $dialog}
  foreach($code in @('FILE_PICKER_FAILED','USER_GESTURE_REQUIRED','CAPABILITY_UNAVAILABLE','PERMISSION_REVOKED','SESSION_EXPIRED','TIMEOUT')){
   if(Feature-TextContains 't03-files-result' $code){$report.picker_observed_error=$code;throw "Native picker request returned $code before any verified dialog appeared."}
  }
  return $null
 } $Description
}
function Invoke-RecoveryPickerButton($Dialog,[string]$Id){
 if($Dialog.Current.ProcessId -ne $product.Process.Id){throw 'Picker ownership changed; refusing control.'}
 $button=Wait-SmokeCondition {
  $matches=$Dialog.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.AndCondition]::new(
   [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button),
   [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id)))
  foreach($candidate in $matches){if($candidate.Current.IsEnabled -and -not $candidate.Current.IsOffscreen){return $candidate}}
  return $null
 } "ready owned native picker button $Id"
 $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Choose-RecoveryPickerFile([string]$Path){
 $full=[IO.Path]::GetFullPath($Path);$safeRoot=[IO.Path]::GetFullPath($scope).TrimEnd('\')+'\'
 if(-not $full.StartsWith($safeRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Picker test paths must remain in this script-owned scope.'}
 $dialog=Wait-RecoveryPicker 'owned native system file picker'
 $edits=$dialog.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit))
 $nameBox=Wait-SmokeCondition {
  $edits=$dialog.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit))
  foreach($edit in $edits){if(($edit.Current.AutomationId -eq '1148' -or $edit.Current.Name -match '^(文件名|File name)(\(|:|：|$)') -and $edit.Current.IsEnabled -and -not $edit.Current.IsOffscreen){return $edit}}
  return $null
 } 'ready native filename edit'
 if(-not $nameBox){throw 'Owned picker filename edit was not identified; no unrelated control was changed.'}
 $nameBox.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($full)
 Invoke-RecoveryPickerButton $dialog '1'
 $report.picker_cases='native_picker_open_save_in_progress'
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
 $dataDirectory=Join-Path $stageDirectory 'AutumnOS_Data'
 $runtimeLog=Join-Path $dataDirectory 'Logs/runtime-events.jsonl'
 $configuration=Join-Path $dataDirectory 'Config/developer-mode.json'
 $savePath=Join-Path $dataDirectory 'Saves/cn.labchronicles.elementpairs/guest/game.json'
 Invoke-SmokeButton $product.Element 'SettingsButton';Select-FeatureCategory 'SettingsNavDiagnostics'
 $null=Wait-SmokeCondition {Find-SmokeElement $product.Element 'DataRecoveryRefresh'} 'real recovery controls'
 Add-SmokeCheck 'no_fake_restore_when_no_config_backup' ($null -eq (Find-VisibleSmokeElement $product.Element 'RestoreDeveloperConfig'))
 Add-SmokeCheck 'no_fake_restore_when_no_save_backup' ($null -eq (Find-VisibleSmokeElement $product.Element 'RestoreSaveBackup'))
 Set-FeatureToggle 'DeveloperModeToggle' $true
 $null=Wait-SmokeCondition {(Test-Path -LiteralPath $configuration) -and (Get-Content -LiteralPath $configuration -Raw|ConvertFrom-Json).values.enabled} 'actual enabled configuration'
 Set-FeatureToggle 'DeveloperModeToggle' $false
 $null=Wait-SmokeCondition {-not (Get-Content -LiteralPath $configuration -Raw|ConvertFrom-Json).values.enabled -and (Test-Path -LiteralPath ($configuration+'.bak'))} 'actual disabled configuration and enabled backup'
 Invoke-SmokeButton $product.Element 'SettingsHomeButton';Invoke-SmokeButton $product.Element 'SampleButton'
 $null=Wait-SmokeCondition {Find-FeatureWebDocument} 'real sample WebView'
 $report.game_instance_id=(Read-SmokeEvents $runtimeLog|Where-Object state -eq Foreground|Select-Object -Last 1).instanceId
 $nickname=Wait-SmokeCondition {Find-FeatureWebElement 'nickname' '玩家昵称'} 'sample nickname'
 $nickname.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('派蒙·恢复前第一份')
 Click-FeatureWeb 'save-button' '保存进度';Invoke-SmokeNamedButton $product.Element '允许'
 $null=Wait-SmokeCondition {(Test-Path -LiteralPath $savePath) -and (Get-Content -LiteralPath $savePath -Raw|ConvertFrom-Json).value.nickname -eq '派蒙·恢复前第一份'} 'first real saved record'
 $firstSaveHash=(Get-FileHash -LiteralPath $savePath).Hash
 $nickname.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('派蒙·恢复前第二份')
 Click-FeatureWeb 'save-button' '保存进度'
 $null=Wait-SmokeCondition {(Get-Content -LiteralPath $savePath -Raw|ConvertFrom-Json).value.nickname -eq '派蒙·恢复前第二份' -and (Test-Path -LiteralPath ($savePath+'.bak'))} 'second real save and backup'
 $secondSaveHash=(Get-FileHash -LiteralPath $savePath).Hash
 Add-SmokeCheck 'second_save_preserves_exact_first_bytes_as_backup' ((Get-FileHash -LiteralPath ($savePath+'.bak')).Hash -eq $firstSaveHash)
 $nickname.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('派蒙·还未保存的内容')
 Invoke-SmokeButton $product.Element 'BackgroundButton';Invoke-SmokeButton $product.Element 'SettingsButton';Select-FeatureCategory 'SettingsNavDiagnostics'
 Invoke-SmokeButton $product.Element 'DataRecoveryRefresh'
 $null=Wait-SmokeCondition {$e=Find-SmokeElement $product.Element 'RestoreDeveloperConfig';$e -and $e.Current.IsEnabled} 'valid configuration backup recovery'
 $beforeConfigHash=(Get-FileHash -LiteralPath $configuration).Hash
 Invoke-SmokeButton $product.Element 'RestoreDeveloperConfig';Invoke-SmokeNamedButton $product.Element '取消'
 Add-SmokeCheck 'cancel_config_recovery_preserves_bytes' ((Get-FileHash -LiteralPath $configuration).Hash -eq $beforeConfigHash -and -not(Test-Path -LiteralPath ($configuration+'.before-restore')))
 Invoke-SmokeButton $product.Element 'RestoreDeveloperConfig';Invoke-SmokeNamedButton $product.Element '已保存预览，恢复配置'
 $null=Wait-SmokeCondition {(Get-Content -LiteralPath $configuration -Raw|ConvertFrom-Json).values.enabled -and (Test-Path -LiteralPath ($configuration+'.before-restore'))} 'restored actual developer configuration'
 Add-SmokeCheck 'config_recovery_retains_exact_before_restore' ((Get-FileHash -LiteralPath ($configuration+'.before-restore')).Hash -eq $beforeConfigHash)
 Add-SmokeCheck 'config_recovery_does_not_close_normal_game' (@(Read-SmokeEvents $runtimeLog|Where-Object {$_.instanceId -eq $report.game_instance_id -and $_.state -eq 'Closed'}).Count -eq 0)
 $null=Wait-SmokeCondition {$e=Find-SmokeElement $product.Element 'RestoreSaveBackup';$e -and $e.Current.IsEnabled} 'valid current guest save backup'
 Invoke-SmokeButton $product.Element 'RestoreSaveBackup';Invoke-SmokeNamedButton $product.Element '取消'
 Add-SmokeCheck 'cancel_save_recovery_preserves_main_backup_and_instance' ((Get-FileHash -LiteralPath $savePath).Hash -eq $secondSaveHash -and (Get-FileHash -LiteralPath ($savePath+'.bak')).Hash -eq $firstSaveHash -and @(Read-SmokeEvents $runtimeLog|Where-Object {$_.instanceId -eq $report.game_instance_id -and $_.state -eq 'Closed'}).Count -eq 0)
 Return-FeatureGame
 $nickname=Find-FeatureWebElement 'nickname' '玩家昵称'
 Add-SmokeCheck 'cancel_recovery_keeps_unsaved_input_same_instance' ($nickname.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '派蒙·还未保存的内容' -and (Read-SmokeEvents $runtimeLog|Where-Object state -eq Foreground|Select-Object -Last 1).instanceId -eq $report.game_instance_id)
 Invoke-SmokeButton $product.Element 'BackgroundButton';Invoke-SmokeButton $product.Element 'SettingsButton';Select-FeatureCategory 'SettingsNavDiagnostics'
 Invoke-SmokeButton $product.Element 'RestoreSaveBackup';Invoke-SmokeNamedButton $product.Element '继续，检查并关闭游戏';Invoke-SmokeNamedButton $product.Element '取消'
 Add-SmokeCheck 'cancel_second_confirmation_keeps_game_and_main' ((Get-FileHash -LiteralPath $savePath).Hash -eq $secondSaveHash -and @(Read-SmokeEvents $runtimeLog|Where-Object {$_.instanceId -eq $report.game_instance_id -and $_.state -eq 'Closed'}).Count -eq 0)
 Invoke-SmokeButton $product.Element 'RestoreSaveBackup';Invoke-SmokeNamedButton $product.Element '继续，检查并关闭游戏';Invoke-SmokeNamedButton $product.Element '我已保存，关闭游戏'
 $null=Wait-SmokeCondition {(Test-Path -LiteralPath ($savePath+'.before-restore')) -and (Get-Content -LiteralPath $savePath -Raw|ConvertFrom-Json).value.nickname -eq '派蒙·恢复前第一份'} 'save restored by real host operation'
 Add-SmokeCheck 'save_recovery_retains_exact_second_record' ((Get-FileHash -LiteralPath ($savePath+'.before-restore')).Hash -eq $secondSaveHash)
 Add-SmokeCheck 'save_recovery_retains_valid_backup' ((Get-FileHash -LiteralPath ($savePath+'.bak')).Hash -eq $firstSaveHash)
 Add-SmokeCheck 'save_recovery_really_closes_old_instance' (@(Read-SmokeEvents $runtimeLog|Where-Object {$_.instanceId -eq $report.game_instance_id -and $_.state -eq 'Closed'}).Count -eq 1)
 $null=Wait-SmokeCondition {(Find-SmokeElement $product.Element 'DataRecoveryResult').Current.Name.Contains('已恢复当前账号')} 'actual recovery result'
 $null=Save-SmokeScreenshot $product '01-real-save-and-configuration-recovery'
 Invoke-SmokeButton $product.Element 'SettingsHomeButton'
 Add-SmokeCheck 'recovery_clears_old_running_badge' ($null -eq (Find-VisibleSmokeElement $product.Element 'SampleRunningBadge'))
 Invoke-SmokeButton $product.Element 'SampleButton';$null=Wait-SmokeCondition {Find-FeatureWebDocument} 'new instance after restore'
 $newInstance=(Read-SmokeEvents $runtimeLog|Where-Object state -eq Foreground|Select-Object -Last 1).instanceId
 Add-SmokeCheck 'recovery_restart_creates_new_game_instance' ($newInstance -ne $report.game_instance_id)
 Click-FeatureWeb 'load-button' '读取存档'
 $null=Wait-SmokeCondition {(Find-FeatureWebElement 'nickname' '玩家昵称').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '派蒙·恢复前第一份'} 'restored save loaded through real SDK'
 Add-SmokeCheck 'game_really_loads_recovered_account_save' $true
 $null=Save-SmokeScreenshot $product '02-recovered-game-input'
 Click-FeatureWeb 't03-features' '账号、文件与桌面联动'
 Wait-FeatureText 't03-events' '当前数据空间：游客'
 Click-FeatureWeb 't03-open-permit' '申请选择读取权限';Invoke-SmokeNamedButton $product.Element '允许';Wait-FeatureText 't03-files-result' 'files.open：granted'
 Click-FeatureWeb 't03-save-permit' '申请选择保存权限';Invoke-SmokeNamedButton $product.Element '允许';Wait-FeatureText 't03-files-result' 'files.save：granted'
 $selectedPath=Join-Path $scope '受控输入.txt';$exportPath=Join-Path $scope '受控输出.json'
 [IO.File]::WriteAllText($selectedPath,'派蒙 · 由系统选择器读取的受控文本',[Text.UTF8Encoding]::new($false))
 Click-FeatureWeb 't03-file-pick' '选择文本文件'
 $picker=Wait-RecoveryPicker 'owned native open picker cancellation'
 Invoke-RecoveryPickerButton $picker '2';Wait-FeatureText 't03-files-result' 'USER_CANCELLED'
 Add-SmokeCheck 'cancel_native_picker_reports_cancel_not_success' (Feature-TextContains 't03-files-result' 'USER_CANCELLED')
 Click-FeatureWeb 't03-file-pick' '选择文本文件';Choose-RecoveryPickerFile $selectedPath
 Wait-FeatureText 't03-files-result' '已选：受控输入.txt'
 Click-FeatureWeb 't03-file-read' '读取已选文件';Wait-FeatureText 't03-file-preview' '由系统选择器读取的受控文本'
 Add-SmokeCheck 'native_picker_handle_reads_exact_selected_text' (Feature-TextContains 't03-file-preview' '派蒙 · 由系统选择器读取的受控文本')
 Click-FeatureWeb 't03-file-close' '释放读取句柄';Wait-FeatureText 't03-files-result' '已释放读取句柄'
 Click-FeatureWeb 't03-file-read' '读取已选文件';Wait-FeatureText 't03-files-result' 'FILE_HANDLE_INVALID'
 Add-SmokeCheck 'closed_read_handle_cannot_be_reused' (Feature-TextContains 't03-files-result' 'FILE_HANDLE_INVALID')
 $note=Find-FeatureWebElement 't03-note' '笔记内容（最多 240 字符）'
 $note.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('派蒙 · 实际选择器导出')
 Click-FeatureWeb 't03-file-save-pick' '选择笔记导出位置';Choose-RecoveryPickerFile $exportPath
 Wait-FeatureText 't03-files-result' '已选导出名称：受控输出.json'
 Add-SmokeCheck 'register_save_capability_does_not_create_new_target' (-not(Test-Path -LiteralPath $exportPath))
 Click-FeatureWeb 't03-file-export' '导出到已选位置';Wait-FeatureText 't03-files-result' '已导出'
 Add-SmokeCheck 'native_save_picker_exports_actual_note_bytes' ((Test-Path -LiteralPath $exportPath) -and [IO.File]::ReadAllText($exportPath) -eq '派蒙 · 实际选择器导出')
 Click-FeatureWeb 't03-file-export' '导出到已选位置';Wait-FeatureText 't03-files-result' 'FILE_HANDLE_INVALID'
 Add-SmokeCheck 'successful_save_handle_is_one_shot' (Feature-TextContains 't03-files-result' 'FILE_HANDLE_INVALID')
 # A fresh picker selection authorizes replacing this test-owned file; previous bytes must survive in a backup.
 $oldExportHash=(Get-FileHash -LiteralPath $exportPath).Hash
 $note.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('派蒙 · 第二次明确选择后导出')
 Click-FeatureWeb 't03-file-save-pick' '选择笔记导出位置';Choose-RecoveryPickerFile $exportPath
 $overwrite=Wait-SmokeCondition {Find-RecoveryPicker} 'owned overwrite confirmation'
 $yes=$overwrite.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.AndCondition]::new(
  [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button),
  [Windows.Automation.OrCondition]::new([Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,'6'),[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'是(Y)'))))
 if(-not $yes){throw 'Expected owned overwrite confirmation is unavailable; target not changed.'}
 $yes.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
 Wait-FeatureText 't03-files-result' '已选导出名称：受控输出.json'
 Add-SmokeCheck 'picking_existing_target_does_not_truncate_it' ((Get-FileHash -LiteralPath $exportPath).Hash -eq $oldExportHash)
 Click-FeatureWeb 't03-file-export' '导出到已选位置';Wait-FeatureText 't03-files-result' '原文件在同目录保留'
 $backups=@(Get-ChildItem -LiteralPath $scope -File|Where-Object {$_.Name.StartsWith('受控输出.json.AutumnOS-backup-')})
 Add-SmokeCheck 'export_replacement_preserves_exact_previous_file_backup' ($backups.Count -eq 1 -and (Get-FileHash -LiteralPath $backups[0].FullName).Hash -eq $oldExportHash -and [IO.File]::ReadAllText($exportPath) -eq '派蒙 · 第二次明确选择后导出')
 $report.picker_cases='passed_cancel_open_read_close_save_new_save_replace_backup'
 $null=Save-SmokeScreenshot $product '03-native-file-capabilities-after-picker-closed'
 Close-SmokeProduct $product
 $report.status='passed'
}catch{
 $report.error_type=$_.Exception.GetType().Name;$report.error=$_.Exception.Message;$report.failure_script_line=$_.InvocationInfo.ScriptLineNumber;$report.failure_script_stack=$_.ScriptStackTrace
 Write-Warning "T03 recovery/files smoke failed: $($_.Exception.Message)"
}finally{
 if($product -and -not $product.Process.HasExited -and (Get-Command Find-RecoveryPicker -ErrorAction SilentlyContinue)){
  for($cleanupAttempt=0;$cleanupAttempt -lt 3;$cleanupAttempt++){
   try{$ownedPicker=Find-RecoveryPicker;if(-not $ownedPicker){break};Invoke-RecoveryPickerButton $ownedPicker '2';Start-Sleep -Milliseconds 150}catch{break}
  }
 }
 $left=@()
 foreach($process in $ownedProcesses){if(-not $process.HasExited){try{if(-not $process.CloseMainWindow() -or -not $process.WaitForExit(10000)){$left+=$process.Id}}catch{$left+=$process.Id}}}
 if(Get-Command Restore-AutumnNativePointer -ErrorAction SilentlyContinue){Restore-AutumnNativePointer}
 if($script:initialPointerPosition -and ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){$null=[AutumnDesktopInteractionSmoke.NativeWindows]::SetCursorPos($script:initialPointerPosition.X,$script:initialPointerPosition.Y)}
 $report.cleanup=if($left.Count){'failed_owned_window_left_no_kill'}else{'passed'};$report.remaining_owned_process_ids=$left
 if($left.Count){$report.status='failed'}
 $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o')
 $report|ConvertTo-Json -Depth 14|Set-Content -LiteralPath $reportPath -Encoding utf8
 Write-Host "REPORT $reportPath"
}
if($report.status -ne 'passed'){throw "T03 recovery/files smoke failed; inspect $reportPath"}
