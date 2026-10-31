[CmdletBinding()]
param([Parameter(Mandatory)][string]$ExecutablePath,[string]$ReportDirectory,[ValidateRange(10,90)][int]$WindowTimeoutSeconds=40,[ValidateRange(60,900)][int]$MaximumDurationSeconds=300)
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
$ownedEntryProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$pickerActions=[Collections.Generic.List[object]]::new();$recoveryDeadline=[Diagnostics.Stopwatch]::StartNew()
$script:initialPointerPosition=$null;$product=$null
$report=[ordered]@{
 schema_version=1;task_id='T03';checkpoint='native_save_configuration_recovery_and_file_picker'
 driver_sha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
 status='failed';smoke_id=$smokeId;started_utc=[DateTimeOffset]::UtcNow.ToString('o');environment=[Environment]::OSVersion.VersionString
 source_snapshot_id='not_recorded';build_id='local-untracked';source_executable=$null;tested_executable=$null;source_executable_sha256=$null
 process_ids=@();checks=$checks;screenshots=$screenshots;input_actions=$inputActions;game_instance_id=$null;return_game_phase=$null
 account_mode='guest';real_logto_login='not_run';second_account='not_run';external_picker_manual_matrix='not_run'
 complete_t03='not_run';arbitrary_third_party_isolation='not_run';physical_input='not_run'
 recovery_kind='actual_UI_confirmations_and_real_storage_no_mock_identity';picker_cases='not_run';picker_actions=$pickerActions;picker_phase=$null;maximum_duration_seconds=$MaximumDurationSeconds;helper_hashes=@();cleanup='not_run'
}

# Reuse reviewed local helper definitions, never execute the old smoke script's test body.
$helperPath=Join-Path $PSScriptRoot 'Test-DesktopInteractionSmoke.ps1'
$helperTokens=$null;$helperErrors=$null
$helperAst=[Management.Automation.Language.Parser]::ParseFile($helperPath,[ref]$helperTokens,[ref]$helperErrors)
if($helperErrors.Count){throw 'Existing desktop helper source did not parse.'}
$names=@('Add-SmokeCheck','Wait-SmokeCondition','Find-SmokeElement','Find-VisibleSmokeElement','Invoke-SmokeButton',
 'Find-SmokeMenuItem','Invoke-SmokeOwnedButton','Find-SmokeNamedElement','Invoke-SmokeNamedButton','Test-SmokeForeground',
 'Focus-SmokeProduct','Assert-SmokeCaptureUnoccluded','Start-SmokeProduct','Close-SmokeProduct','Save-SmokeScreenshot',
 'Invoke-SmokePointer','Wait-SmokeRunningPanel','Wait-SmokeNewForeground','Read-SmokeEvents')
foreach($name in $names){
 $definition=@($helperAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)|Where-Object Name -EQ $name)
 if($definition.Count -ne 1){throw "Missing unique local helper: $name"}
 Invoke-Expression $definition[0].Extent.Text
}
$nativeDefinition=@($helperAst.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst]},$true)|Where-Object { $_.Value.Contains('namespace AutumnDesktopInteractionSmoke {') })
if($nativeDefinition.Count -ne 1){throw 'Missing reviewed local Win32 helper definition.'}
$report.helper_hashes=@(@{path=$helperPath;sha256=(Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash.ToLowerInvariant()},
 @{path=(Join-Path $PSScriptRoot 'Smoke-NativeAppInput.ps1');sha256=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Smoke-NativeAppInput.ps1') -Algorithm SHA256).Hash.ToLowerInvariant()})

# Scope-specific native picker automation. No text is read from file lists or breadcrumbs.
# Every mutation is revalidated against the held product process and exact owner HWND.
$pickerNativeDefinition=@'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace AutumnRecoveryPickerSmoke {
 public sealed class ButtonFacts { public long Hwnd {get;set;} public int Id {get;set;} public uint Pid {get;set;} public bool Visible {get;set;} public bool Enabled {get;set;} }
 public static class Native {
  private delegate bool Enumerate(IntPtr hwnd, IntPtr state);
  [DllImport("user32.dll")] private static extern bool EnumWindows(Enumerate callback,IntPtr state);
  [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent,Enumerate callback,IntPtr state);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd,uint command);
  [DllImport("user32.dll")] public static extern IntPtr GetDlgItem(IntPtr dialog,int id);
  [DllImport("user32.dll")] public static extern int GetDlgCtrlID(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern bool IsChild(IntPtr parent,IntPtr child);
  [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd,StringBuilder text,int size);
  [DllImport("user32.dll",SetLastError=true)] public static extern bool PostMessage(IntPtr hwnd,uint message,IntPtr wParam,IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr SetTextMessage(IntPtr hwnd,uint message,IntPtr wParam,string text,uint flags,uint timeout,out IntPtr result);
  [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr ReadTextMessage(IntPtr hwnd,uint message,IntPtr wParam,StringBuilder text,uint flags,uint timeout,out IntPtr result);
  public static string ClassName(IntPtr hwnd) { var text=new StringBuilder(128); return GetClassName(hwnd,text,text.Capacity)>0?text.ToString():""; }
  public static uint Pid(IntPtr hwnd) { uint pid;GetWindowThreadProcessId(hwnd,out pid);return pid; }
  public static bool OwnedDialog(IntPtr dialog,IntPtr main,uint pid,bool ready) {
   if(dialog==IntPtr.Zero || dialog==main || !IsWindow(dialog) || Pid(main)!=pid || Pid(dialog)!=pid || ClassName(dialog)!="#32770" || !IsWindowVisible(dialog) || (ready&&!IsWindowEnabled(dialog)))return false;
   IntPtr owner=GetWindow(dialog,4);
   for(int depth=0;depth<16 && owner!=IntPtr.Zero;depth++){if(owner==main)return true;if(Pid(owner)!=pid)return false;owner=GetWindow(owner,4);}return false;
  }
  public static IntPtr[] FindOwnedDialogs(IntPtr main,uint pid) {
   var list=new List<IntPtr>();EnumWindows((hwnd,_)=>{if(Pid(hwnd)==pid && OwnedDialog(hwnd,main,pid,true))list.Add(hwnd);return true;},IntPtr.Zero);return list.ToArray();
  }
  public static ButtonFacts[] Buttons(IntPtr dialog,uint pid) {
   var list=new List<ButtonFacts>();EnumChildWindows(dialog,(hwnd,_)=>{if(Pid(hwnd)==pid && ClassName(hwnd)=="Button" && list.Count<64)list.Add(new ButtonFacts{Hwnd=hwnd.ToInt64(),Id=GetDlgCtrlID(hwnd),Pid=pid,Visible=IsWindowVisible(hwnd),Enabled=IsWindowEnabled(hwnd)});return true;},IntPtr.Zero);return list.ToArray();
  }
  public static bool ReadyButton(IntPtr dialog,IntPtr button,int id,uint pid) {return button!=IntPtr.Zero && IsChild(dialog,button) && Pid(button)==pid && GetDlgCtrlID(button)==id && ClassName(button)=="Button" && IsWindowVisible(button) && IsWindowEnabled(button);}
  public static IntPtr FilenameEdit(IntPtr dialog,uint pid) {
   IntPtr group=GetDlgItem(dialog,1148);
   if(group==IntPtr.Zero || !IsChild(dialog,group) || Pid(group)!=pid || GetDlgCtrlID(group)!=1148)return IntPtr.Zero;
   string kind=ClassName(group);if(kind=="Edit" && IsWindowVisible(group)&&IsWindowEnabled(group))return group;
   if(kind!="ComboBoxEx32"&&kind!="ComboBox")return IntPtr.Zero;
   var edits=new List<IntPtr>();EnumChildWindows(group,(hwnd,_)=>{if(Pid(hwnd)==pid&&ClassName(hwnd)=="Edit"&&IsWindowVisible(hwnd)&&IsWindowEnabled(hwnd))edits.Add(hwnd);return true;},IntPtr.Zero);
   return edits.Count==1?edits[0]:IntPtr.Zero;
  }
  public static void SetOwnedFilename(IntPtr dialog,IntPtr main,uint pid,IntPtr edit,string text) {
   if(!OwnedDialog(dialog,main,pid,true)||FilenameEdit(dialog,pid)!=edit||text.Length==0||text.Length>32760)throw new InvalidOperationException("OWNED_FILENAME_EDIT_CHANGED");
   IntPtr result;if(SetTextMessage(edit,0x000C,IntPtr.Zero,text,2,2000,out result)==IntPtr.Zero||result==IntPtr.Zero)throw new InvalidOperationException("OWNED_FILENAME_SET_TIMED_OUT_OR_FAILED");
   // Read only after the exact controlled value has been written successfully.
   if(!OwnedDialog(dialog,main,pid,true)||FilenameEdit(dialog,pid)!=edit)throw new InvalidOperationException("OWNED_FILENAME_EDIT_CHANGED_AFTER_WRITE");
   var read=new StringBuilder(32768);if(ReadTextMessage(edit,0x000D,(IntPtr)read.Capacity,read,2,2000,out result)==IntPtr.Zero||read.ToString()!=text)throw new InvalidOperationException("OWNED_FILENAME_WRITE_READBACK_MISMATCH");
  }
 }
}
'@
function Assert-RecoveryDeadline {if($recoveryDeadline.Elapsed.TotalSeconds -ge $MaximumDurationSeconds){throw 'Recovery smoke reached its cooperative overall deadline; owned cleanup is required.'}}
# Bounds polling across this driver. Native picker text messages separately have a 2-second timeout.
function Wait-SmokeCondition([scriptblock]$Condition,[string]$Description,[int]$Seconds=$WindowTimeoutSeconds){
 $timer=[Diagnostics.Stopwatch]::StartNew()
 do{Assert-RecoveryDeadline;$value=& $Condition;if($value){return $value};Start-Sleep -Milliseconds 150}while($timer.Elapsed.TotalSeconds -lt $Seconds)
 throw "Timed out waiting for $Description after $Seconds seconds."
}

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
 Assert-RecoveryDeadline
 $report.picker_phase='before_web_action:'+ $Id
 $button=Wait-SmokeCondition {Find-FeatureWebElement $Id $Name} "sample action $Name"
 $evidence=Invoke-AutumnNativeElementClick -WindowElement $product.Element -Element $button -Process $product.Process
 $inputActions.Add($evidence)
 $report.picker_phase='web_action_returned:'+ $Id
}
function Wait-FeatureText([string]$Id,[string]$Text){$null=Wait-SmokeCondition {Feature-TextContains $Id $Text} "safe sample result $Text"}
function Return-FeatureGame([string]$Context) {
 $report.return_game_phase=$Context+':return_to_desktop'
 Invoke-SmokeButton $product.Element 'SettingsHomeButton'
 $null=Wait-SmokeCondition {Find-VisibleSmokeElement $product.Element 'SampleButton'} 'desktop after leaving settings'
 $null=Wait-SmokeCondition {Find-VisibleSmokeElement $product.Element 'DesktopGestureHint'} 'blank desktop switcher target'
 $beforeSwitcher=@(Read-SmokeEvents $runtimeLog)
 $latest=$beforeSwitcher|Select-Object -Last 1
 Add-SmokeCheck ($Context+'_original_game_is_background_before_switcher') ($latest -and $latest.instanceId -eq $report.game_instance_id -and $latest.state -eq 'Background' -and $latest.blocksMaintenance)
 # A single icon click now resumes the game, so an icon double-click loses its
 # target before the second down. The actual switcher gesture is on blank desktop.
 # Inject it once; do not retry a click after it might have changed application state.
 $report.return_game_phase=$Context+':open_blank_desktop_switcher'
 Invoke-SmokePointer $product 'DesktopGestureHint' 'double_click'
 Wait-SmokeRunningPanel $product
 $afterSwitcher=@(Read-SmokeEvents $runtimeLog)
 $latest=$afterSwitcher|Select-Object -Last 1
 $unexpectedActivation=@($afterSwitcher|Select-Object -Skip $beforeSwitcher.Count|Where-Object state -EQ 'Foreground')
 Add-SmokeCheck ($Context+'_switcher_preserves_background_instance') ($latest -and $latest.instanceId -eq $report.game_instance_id -and $latest.state -eq 'Background' -and $latest.blocksMaintenance -and $unexpectedActivation.Count -eq 0)
 $continueBaseline=$afterSwitcher.Count
 $report.return_game_phase=$Context+':continue_original_game'
 Invoke-SmokeOwnedButton $product 'RunningContinueButton'
 $foreground=Wait-SmokeNewForeground $runtimeLog $continueBaseline $report.game_instance_id
 $null=Wait-SmokeCondition {Find-FeatureWebDocument} 'original game returns'
 Add-SmokeCheck ($Context+'_continue_restores_original_instance') ($foreground.instanceId -eq $report.game_instance_id -and $foreground.blocksMaintenance)
 $report.return_game_phase=$Context+':completed'
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
 if([AutumnRecoveryPickerSmoke.Native]::Pid($product.Handle) -ne $product.Process.Id){throw 'Held product no longer owns its original HWND.'}
 $dialogs=@([AutumnRecoveryPickerSmoke.Native]::FindOwnedDialogs($product.Handle,$product.Process.Id))
 if($dialogs.Count -gt 1){throw 'Multiple enabled owned dialogs are ambiguous; no picker action was sent.'}
 if($dialogs.Count -eq 1){return [pscustomobject]@{Handle=$dialogs[0];OwnerHandle=$product.Handle;ProcessId=$product.Process.Id}}
 return $null
}
function Assert-RecoveryPicker($Dialog){
 if(-not $product -or $product.Process.HasExited -or $Dialog.ProcessId -ne $product.Process.Id -or $Dialog.OwnerHandle -ne $product.Handle -or
  -not [AutumnRecoveryPickerSmoke.Native]::OwnedDialog($Dialog.Handle,$product.Handle,$product.Process.Id,$true)){throw 'Exact held picker/owner identity changed; refusing control.'}
}
function Wait-RecoveryPicker([string]$Description){
 $report.picker_phase='wait_owned_picker:'+ $Description
 # Do not traverse the underlying WebView or the global UIA tree while a native modal opens.
 $dialog=Wait-SmokeCondition {Find-RecoveryPicker} $Description
 Assert-RecoveryPicker $dialog
 $pickerActions.Add([ordered]@{kind='owned_picker_observed';hwnd=$dialog.Handle.ToInt64();owner_hwnd=$product.Handle.ToInt64();process_id=$product.Process.Id;
  buttons=@([AutumnRecoveryPickerSmoke.Native]::Buttons($dialog.Handle,$product.Process.Id))})
 return $dialog
}
function Invoke-RecoveryPickerButton($Dialog,[ValidateSet('1','2','6')][string]$Id,[switch]$Cleanup){
 if(-not $Cleanup){Assert-RecoveryDeadline}
 Assert-RecoveryPicker $Dialog
 $report.picker_phase='owned_picker_action:'+ $Id
 if($Id -eq '2'){
  # WM_CLOSE is the dialog's normal cancellation route. Posting is not a success assertion.
  $posted=[AutumnRecoveryPickerSmoke.Native]::PostMessage($Dialog.Handle,0x0010,[IntPtr]::Zero,[IntPtr]::Zero)
  $pickerActions.Add([ordered]@{kind='owned_picker_cancel_posted';hwnd=$Dialog.Handle.ToInt64();owner_hwnd=$product.Handle.ToInt64();process_id=$product.Process.Id;posted=$posted;cleanup=[bool]$Cleanup})
  if(-not $posted){throw 'Posting cancellation to the exact owned picker failed.'}
  $timer=[Diagnostics.Stopwatch]::StartNew()
  while([AutumnRecoveryPickerSmoke.Native]::IsWindow($Dialog.Handle) -and [AutumnRecoveryPickerSmoke.Native]::IsWindowVisible($Dialog.Handle)){
   if(-not $Cleanup){Assert-RecoveryDeadline}
   if($timer.Elapsed.TotalSeconds -ge 8){throw 'Owned picker did not dismiss after normal cancellation; no repeat click or kill was attempted.'}
   Start-Sleep -Milliseconds 100
  }
  return
 }
 $button=Wait-SmokeCondition {
  Assert-RecoveryPicker $Dialog
  $candidate=[AutumnRecoveryPickerSmoke.Native]::GetDlgItem($Dialog.Handle,[int]$Id)
  if([AutumnRecoveryPickerSmoke.Native]::ReadyButton($Dialog.Handle,$candidate,[int]$Id,$product.Process.Id)){return $candidate}
  return $null
 } "ready owned native picker button $Id"
 $null=[AutumnRecoveryPickerSmoke.Native]::SetForegroundWindow($Dialog.Handle)
 $null=Wait-SmokeCondition {[AutumnRecoveryPickerSmoke.Native]::GetForegroundWindow() -eq $Dialog.Handle} 'exact owned dialog active for button click' 3
 Assert-RecoveryPicker $Dialog
 if(-not [AutumnRecoveryPickerSmoke.Native]::ReadyButton($Dialog.Handle,$button,[int]$Id,$product.Process.Id)){throw 'Owned picker button changed before posting.'}
 $posted=[AutumnRecoveryPickerSmoke.Native]::PostMessage($button,0x00F5,[IntPtr]::Zero,[IntPtr]::Zero)
 $pickerActions.Add([ordered]@{kind='owned_picker_button_posted';hwnd=$Dialog.Handle.ToInt64();button_hwnd=$button.ToInt64();control_id=[int]$Id;process_id=$product.Process.Id;posted=$posted})
 if(-not $posted){throw 'Posting one click to the verified native picker button failed.'}
}
function Choose-RecoveryPickerFile([string]$Path,[switch]$ExpectOverwrite){
 Assert-RecoveryDeadline
 $full=[IO.Path]::GetFullPath($Path);$safeRoot=[IO.Path]::GetFullPath($scope).TrimEnd('\')+'\'
 if(-not $full.StartsWith($safeRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'Picker test paths must remain in this script-owned scope.'}
 $dialog=Wait-RecoveryPicker 'owned native system file picker'
 $nameBox=Wait-SmokeCondition {Assert-RecoveryPicker $dialog;$edit=[AutumnRecoveryPickerSmoke.Native]::FilenameEdit($dialog.Handle,$product.Process.Id);if($edit -ne [IntPtr]::Zero){return $edit};return $null} 'unique ready Edit inside native filename control 1148'
 [AutumnRecoveryPickerSmoke.Native]::SetOwnedFilename($dialog.Handle,$product.Handle,$product.Process.Id,$nameBox,$full)
 $pickerActions.Add([ordered]@{kind='controlled_filename_written_and_verified';hwnd=$dialog.Handle.ToInt64();edit_hwnd=$nameBox.ToInt64();process_id=$product.Process.Id;scope_verified=$true;readback_matches=$true})
 Invoke-RecoveryPickerButton $dialog '1'
 if($ExpectOverwrite){
  $null=Wait-SmokeCondition {$confirmation=Find-RecoveryPicker;if($confirmation -and $confirmation.Handle -ne $dialog.Handle){return $confirmation};return $null} 'new owned overwrite confirmation after save selection'
 }else{
  $null=Wait-SmokeCondition {-not [AutumnRecoveryPickerSmoke.Native]::IsWindow($dialog.Handle) -or -not [AutumnRecoveryPickerSmoke.Native]::IsWindowVisible($dialog.Handle)} 'owned picker dismissed after controlled selection'
 }
 $report.picker_cases='native_picker_open_save_in_progress'
}

try{
 if(-not $IsWindows){throw 'An interactive Windows desktop is required.'}
 if(@(Get-Process -Name AutumnOS,AutumnOS.Client -ErrorAction SilentlyContinue|Where-Object SessionId -eq ([Diagnostics.Process]::GetCurrentProcess().SessionId)).Count){throw 'An existing user launcher or client is open; normal closure is required. No process is stopped.'}
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
 if(-not('AutumnRecoveryPickerSmoke.Native' -as [type])){Add-Type -TypeDefinition $pickerNativeDefinition}
 $product=Start-SmokeProduct $scope

 Invoke-SmokeButton $product.Element 'HelloNextButton';Invoke-SmokeButton $product.Element 'BrandNextButton'; Invoke-SmokeButton $product.Element 'WelcomeAppearanceNext';Invoke-SmokeButton $product.Element 'FirstRunSkip';Invoke-SmokeButton $product.Element 'PrepareDesktopButton'
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
 Invoke-AutumnGameShortcut $product 'Desktop';Invoke-SmokeButton $product.Element 'SettingsButton';Select-FeatureCategory 'SettingsNavDiagnostics'
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
 Return-FeatureGame 'after_cancelled_save_recovery'
 $nickname=Find-FeatureWebElement 'nickname' '玩家昵称'
 Add-SmokeCheck 'cancel_recovery_keeps_unsaved_input_same_instance' ($nickname.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '派蒙·还未保存的内容' -and (Read-SmokeEvents $runtimeLog|Where-Object state -eq Foreground|Select-Object -Last 1).instanceId -eq $report.game_instance_id)
 Invoke-AutumnGameShortcut $product 'Desktop';Invoke-SmokeButton $product.Element 'SettingsButton';Select-FeatureCategory 'SettingsNavDiagnostics'
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
 Click-FeatureWeb 't03-file-save-pick' '选择笔记导出位置';Choose-RecoveryPickerFile $exportPath -ExpectOverwrite
 $overwrite=Wait-SmokeCondition {Find-RecoveryPicker} 'owned overwrite confirmation'
 Invoke-RecoveryPickerButton $overwrite '6'
 $null=Wait-SmokeCondition {-not (Find-RecoveryPicker)} 'owned overwrite confirmation and picker dismissed'
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
  $cancelledPickerHandles=[Collections.Generic.HashSet[long]]::new()
  for($cleanupAttempt=0;$cleanupAttempt -lt 3;$cleanupAttempt++){
   try{$ownedPicker=Find-RecoveryPicker;if(-not $ownedPicker -or -not $cancelledPickerHandles.Add($ownedPicker.Handle.ToInt64())){break};Invoke-RecoveryPickerButton $ownedPicker '2' -Cleanup;Start-Sleep -Milliseconds 150}catch{$report.picker_cleanup_error=$_.Exception.Message;break}
  }
 }
 $left=@()
 foreach($process in $ownedProcesses){if(-not $process.HasExited){try{if(-not $process.CloseMainWindow() -or -not $process.WaitForExit(10000)){$left+=$process.Id}}catch{$left+=$process.Id}}}
 foreach($entry in $ownedEntryProcesses){
  try{if(-not $entry.HasExited -and -not $entry.WaitForExit(10000)){$left+=$entry.Id}}catch{$left+=$entry.Id}
 }
 $left=@($left|Sort-Object -Unique)
 if(Get-Command Restore-AutumnNativePointer -ErrorAction SilentlyContinue){Restore-AutumnNativePointer}
 if($script:initialPointerPosition -and ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){$null=[AutumnDesktopInteractionSmoke.NativeWindows]::SetCursorPos($script:initialPointerPosition.X,$script:initialPointerPosition.Y)}
 $report.cleanup=if($left.Count){'failed_owned_window_left_no_kill'}else{'passed'};$report.remaining_owned_process_ids=$left
 if($left.Count){$report.status='failed'}
 $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o')
 $report|ConvertTo-Json -Depth 14|Set-Content -LiteralPath $reportPath -Encoding utf8
 Write-Host "REPORT $reportPath"
}
if($report.status -ne 'passed'){throw "T03 recovery/files smoke failed; inspect $reportPath"}
