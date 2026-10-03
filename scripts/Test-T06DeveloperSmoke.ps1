#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [Parameter(Mandatory)][string]$HelpersDirectory,
    [string]$DeveloperDirectory,
    [ValidateRange(15,90)][int]$WindowTimeoutSeconds=45,
    [switch]$CliOnly
)
# Maintained local driver. No product runs until this script is explicitly invoked.
# The SDK/CLI/project inputs are delivered files copied into a fresh directory. Native UI helpers are explicit test prerequisites.
$ErrorActionPreference='Stop'
$ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath)
$HelpersDirectory=[IO.Path]::GetFullPath($HelpersDirectory)
$ReportDirectory=[IO.Path]::GetFullPath($ReportDirectory)
if(-not $DeveloperDirectory){$DeveloperDirectory=Join-Path (Split-Path $ExecutablePath -Parent) 'Developer'}
$DeveloperDirectory=[IO.Path]::GetFullPath($DeveloperDirectory)
if(Test-Path -LiteralPath $ReportDirectory){throw 'A new report directory is required; no historical evidence is overwritten.'}
if($ReportDirectory -match '[\\/]AutumnOS_Data(?:[\\/]|$)'){throw 'Reports cannot be placed in user data.'}
. (Join-Path $HelpersDirectory 'Smoke-NativeAppInput.ps1')
. (Join-Path $HelpersDirectory 'T06-NativeHelpers.ps1')
$desktopAst=Import-T06Functions (Join-Path $HelpersDirectory 'Test-DesktopInteractionSmoke.ps1')
$script:t05LegacyFocus=(Get-Item Function:Focus-SmokeProduct).ScriptBlock
$null=Import-T06Functions (Join-Path $HelpersDirectory 'Test-T05UpdateSmoke.ps1') -Names @(
    'Assert-T05OwnedWindow','Get-T05WindowGeometry','Focus-SmokeProduct','Initialize-T05FocusNative','Assert-T05PlainPath','Invoke-T05SharingRead','Get-T05Hash')
$null=Import-T06Functions (Join-Path $HelpersDirectory 'Test-T04StoreSmoke.ps1') -Names @(
    'Show-Element','Click','Find','Visible','Require','Toggle','Click-Named')
$checks=[Collections.Generic.List[object]]::new();$screenshots=[Collections.Generic.List[object]]::new()
$inputActions=[Collections.Generic.List[object]]::new();$windowGeometry=[Collections.Generic.List[object]]::new()
$ownedProcesses=[Collections.Generic.List[Diagnostics.Process]]::new();$ownedEntryProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$trackedProcesses=[Collections.Generic.List[object]]::new();$cliProcesses=[Collections.Generic.List[object]]::new()
$commands=[Collections.Generic.List[object]]::new();$samples=[Collections.Generic.List[object]]::new()
$script:initialPointerPosition=$null;$script:product=$null;$script:documentTitle=$null
$testScope=Join-Path $ReportDirectory '干净目录 independent developer'
$script:stageDirectory=Join-Path $testScope '便携宿主'
$cliRoot=Join-Path $testScope '独立工具'
$cwd=Join-Path $testScope 'unrelated working directory'
$script:dataDirectory=Join-Path $stageDirectory 'AutumnOS_Data'
$script:runtimeLog=Join-Path $dataDirectory 'Logs/runtime-events.jsonl'
$report=[ordered]@{
    schema_version=1;task_id='T06';checkpoint='developer_delivered_clean_directory';status='failed'
    started_utc=[DateTimeOffset]::UtcNow.ToString('o');source_executable=$ExecutablePath;source_developer=$DeveloperDirectory
    tested_executable=(Join-Path $stageDirectory 'AutumnOS.exe');tested_cli=(Join-Path $cliRoot 'AutumnOS.Developer.Cli.exe')
    driver_sha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    build_id=$null;source_snapshot_id=$null;test_scope=$testScope;checks=$checks;commands=$commands;samples=$samples
    process_ids=@();bootstrap_process_ids=@();remaining_processes=@();screenshots=$screenshots;input_actions=$inputActions;window_geometry=$windowGeometry
    developer_reproduction='self_test_not_external_newcomer';dotnet_installed_on_machine=$true;no_installed_dotnet_machine='not_run'
    cli_runtime='delivered self-contained apphost; process DOTNET_ROOT removed and PATH restricted to Windows'
    host_operations='real owned native UI and current-user developer pipe';user_data_writes='real UI/SDK only'
    setup='deferred_by_user';real_account='not_run_T03_deferred';account_switch='not_run';offline_simulation='not_run'
    permission_simulation='not_run';simulated_account_switch='not_run';physical_input='not_run';external_publication='not_performed';native_status='not_run';cleanup='not_run'
}
function Copy-PlainTree([string]$Source,[string]$Destination,[string[]]$Exclude=@()){
    Assert-T05PlainPath $Source;Assert-T05PlainPath $Destination
    if(-not(Test-Path -LiteralPath $Source -PathType Container)){throw "Delivered source directory missing: $Source"}
    if(Test-Path -LiteralPath $Destination){throw 'Isolated destination must not exist.'}
    New-Item -ItemType Directory -Path $Destination|Out-Null
    foreach($entry in Get-ChildItem -LiteralPath $Source -Force){
        if($entry.Name -in $Exclude){continue}
        Assert-T05PlainPath $entry.FullName
        if($entry.PSIsContainer){foreach($child in Get-ChildItem -LiteralPath $entry.FullName -Recurse -Force){Assert-T05PlainPath $child.FullName}}
        Copy-Item -LiteralPath $entry.FullName -Destination $Destination -Recurse
    }
}
function Start-DeveloperCli([string[]]$Arguments){
    $start=[Diagnostics.ProcessStartInfo]::new($report.tested_cli)
    $start.WorkingDirectory=$cwd;$start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in $Arguments){$start.ArgumentList.Add($argument)}
    foreach($key in @('DOTNET_ROOT','DOTNET_ROOT_X64','DOTNET_ROOT(x86)')){$null=$start.Environment.Remove($key)}
    $start.Environment['DOTNET_MULTILEVEL_LOOKUP']='0';$start.Environment['PATH']=(Join-Path $env:SystemRoot 'System32')+';'+$env:SystemRoot
    $process=[Diagnostics.Process]::Start($start);$null=$process.Handle
    $entry=[pscustomobject]@{Process=$process;Arguments=$Arguments;Started=$process.StartTime.ToUniversalTime();Out=$process.StandardOutput.ReadToEndAsync();Err=$process.StandardError.ReadToEndAsync();Recorded=$false}
    $cliProcesses.Add($entry);return $entry
}
function Finish-DeveloperCli($Entry,[int]$Expected=0,[int]$Seconds=135,[switch]$TextOnly){
    if(-not $Entry.Process.WaitForExit($Seconds*1000)){throw 'Held CLI exceeded its bounded protocol timeout; no other process is touched.'}
    $text=$Entry.Out.GetAwaiter().GetResult();$errors=$Entry.Err.GetAwaiter().GetResult()
    if(-not $Entry.Recorded){$commands.Add([ordered]@{executable=$report.tested_cli;arguments=$Entry.Arguments;working_directory=$cwd;started_utc=$Entry.Started.ToString('o');exit_code=$Entry.Process.ExitCode;expected_exit_code=$Expected;stdout=$text;stderr=$errors});$Entry.Recorded=$true}
    if($Entry.Process.ExitCode -ne $Expected){throw "Delivered CLI exit $($Entry.Process.ExitCode), expected $Expected, command $($Entry.Arguments[0]); see command evidence."}
    if($TextOnly){return $text}
    return $text|ConvertFrom-Json
}
function Run-DeveloperCli([string[]]$Arguments,[int]$Expected=0){Finish-DeveloperCli (Start-DeveloperCli $Arguments) $Expected}
function Get-DeveloperScopes {
    $list=[Collections.Generic.List[object]]::new();$list.Add($product.Element)
    if($documentTitle){
        $filter=[Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Document),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$documentTitle))
        $window=$product.Element.Current.BoundingRectangle
        foreach($doc in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants,$filter)){
            $bounds=$doc.Current.BoundingRectangle
            if(-not $bounds.IsEmpty -and $bounds.Left -ge $window.Left -and $bounds.Top -ge $window.Top -and $bounds.Right -le $window.Right -and $bounds.Bottom -le $window.Bottom -and
                (Test-AutumnOwnedInputProcess $doc.Current.ProcessId $product.Process)){$list.Add($doc)}
        }
    }
    return @($list)
}
function Find-DeveloperNamed([string]$Name,$Type){
    $filter=[Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,$Type))
    foreach($scope in Get-DeveloperScopes){
        $found=$scope.FindFirst([Windows.Automation.TreeScope]::Descendants,$filter)
        if($found -and (Test-AutumnOwnedInputProcess $found.Current.ProcessId $product.Process)){return $found}
    }
    return $null
}
function Wait-DeveloperText([string]$Pattern){
    Wait-SmokeCondition {
        $filter=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Text)
        foreach($scope in Get-DeveloperScopes){foreach($element in $scope.FindAll([Windows.Automation.TreeScope]::Descendants,$filter)){
            if($element.Current.Name -match $Pattern -and (Test-AutumnOwnedInputProcess $element.Current.ProcessId $product.Process)){return $element.Current.Name}
        }}
    } ('actual app text '+$Pattern) 20
}
function Click-DeveloperWeb([string]$Name){
    $element=Wait-SmokeCondition {$found=Find-DeveloperNamed $Name ([Windows.Automation.ControlType]::Button);if($found -and $found.Current.IsEnabled){return $found}} ('enabled app button '+$Name) 15
    Focus-SmokeProduct $product
    $inputActions.Add((Invoke-AutumnNativeElementClick -WindowElement $product.Element -Element $element -Process $product.Process))
}
function Click-DeveloperWebElement([string]$Id,[string]$Name){
    $element=Wait-SmokeCondition {
        foreach($scope in Get-DeveloperScopes){
            $found=Find-SmokeElement $scope $Id
            if(-not $found){$found=$scope.FindFirst([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name))}
            if($found -and $found.Current.IsEnabled -and (Test-AutumnOwnedInputProcess $found.Current.ProcessId $product.Process)){return $found}
        }
        return $null
    } ('real app element '+$Name) 15
    $inputActions.Add((Invoke-AutumnNativeElementClick -WindowElement $product.Element -Element $element -Process $product.Process))
}
function Invoke-DeveloperDebug([string]$Session,[string]$Action,[int]$Expected=0){Run-DeveloperCli @('debug',$Session,$Action,'--host',$stageDirectory) $Expected}
function Switch-DeveloperTestAccount([string]$Session,[string]$Action,[switch]$Cancel){
    $previousEvents=@(Read-DeveloperEvents)
    $before=$previousEvents.Count
    $oldNavigation=@($previousEvents|Where-Object {$_.eventName -eq 'navigation_completed' -and $_.state -eq 'Foreground'})|Select-Object -Last 1
    if(-not $oldNavigation){throw 'A real current preview instance is required before switching test accounts.'}
    $entry=Start-DeveloperCli @('debug',$Session,$Action,'--host',$stageDirectory)
    $null=Wait-DeveloperText '仅此独立预览会结束并重新启动'
    if($Cancel){
        Click-Named '取消' 'CloseButton'
        $result=Finish-DeveloperCli $entry 2
        Add-SmokeCheck 'cancelled_test_account_switch_keeps_current_preview_session' ($result.code -eq 'USER_CANCELLED' -and (Invoke-DeveloperDebug $Session 'status').sessionId -eq $Session)
        return $result
    }
    Click-Named '已保存，确认切换'
    $result=Finish-DeveloperCli $entry
    Add-SmokeCheck ('test_'+$Action+'_switch_restarts_real_preview_with_new_session') ($result.sessionId -ne $Session -and $result.state -eq 'Foreground' -and $result.simulation.account -eq $(if($Action -eq 'account-guest'){'guest'}elseif($Action -eq 'reset-simulation'){'live'}else{$Action}))
    $expired=Invoke-DeveloperDebug $Session 'status' 2
    Add-SmokeCheck ('test_'+$Action+'_old_debug_session_is_expired') ($expired.code -eq 'DEVELOPER_PREVIEW_SESSION_EXPIRED')
    $events=@(Read-DeveloperEvents|Select-Object -Skip $before)
    $closedIndex=-1;$newNavigationIndex=-1
    for($i=0;$i -lt $events.Count;$i++){
        if($events[$i].instanceId -eq $oldNavigation.instanceId -and $events[$i].eventName -eq 'closed' -and $events[$i].state -eq 'Closed' -and -not $events[$i].blocksMaintenance){$closedIndex=$i}
        if($events[$i].instanceId -ne $oldNavigation.instanceId -and $events[$i].eventName -eq 'navigation_completed' -and $events[$i].state -eq 'Foreground'){$newNavigationIndex=$i;break}
    }
    Add-SmokeCheck ('test_'+$Action+'_old_resources_released_before_new_navigation') ($closedIndex -ge 0 -and $newNavigationIndex -gt $closedIndex)
    return $result
}
function Read-DeveloperProfileText([string]$Name,[bool]$Cached){
    $text=Wait-DeveloperText ('昵称：'+[regex]::Escape($Name))
    if($text -notmatch 'au1_[a-f0-9]{64}') {throw 'Actual SDK profile did not expose its scoped test identifier.'}
    $id=$Matches[0]
    $null=Wait-DeveloperText ('离线缓存：'+$Cached.ToString().ToLowerInvariant())
    return $id
}
function Read-DeveloperEvents {
    if(-not(Test-Path -LiteralPath $runtimeLog)){return @()}
    $bytes=(Read-T06Snapshot $runtimeLog).Bytes
    $lines=[Text.Encoding]::UTF8.GetString($bytes).Split("`n")
    # The writer appends JSON then newline; only complete records are observations, never a half-written trailing record.
    return @(for($i=0;$i -lt $lines.Length-1;$i++){if($lines[$i].Trim()){$lines[$i]|ConvertFrom-Json}})
}
function Snapshot-DeveloperProtected {
    return @(foreach($name in @('Apps','Saves','AppData')){
        $folder=Join-Path $dataDirectory $name
        if(Test-Path -LiteralPath $folder){foreach($file in Get-ChildItem -LiteralPath $folder -Recurse -File){
            Assert-T05PlainPath $file.FullName
            [pscustomobject]@{path=[IO.Path]::GetRelativePath($dataDirectory,$file.FullName);sha256=Get-T05Hash $file.FullName}
        }}
    })
}
function Assert-DeveloperProtected($Before,[string]$Label){
    $changed=@(foreach($file in $Before){$path=Join-Path $dataDirectory $file.path;if(-not(Test-Path -LiteralPath $path) -or (Get-T05Hash $path) -ne $file.sha256){$file.path}})
    Add-SmokeCheck $Label ($Before.Count -gt 0 -and $changed.Count -eq 0) ('Compared '+$Before.Count+' existing installed program/data files; changed='+($changed -join ','))
}
function Start-ConfirmedPreview($Sample){
    $before=@(Read-DeveloperEvents).Count
    $entry=Start-DeveloperCli @('preview',$Sample.package,'--host',$stageDirectory)
    $text=Wait-DeveloperText ([regex]::Escape($Sample.sha256))
    Add-SmokeCheck ($Sample.template+'_confirmation_contains_exact_package_identity_and_sha') ($text.Contains($Sample.appId) -and $text.Contains($Sample.sha256))
    Click-Named '确认此包并预览'
    $result=Finish-DeveloperCli $entry
    Add-SmokeCheck ($Sample.template+'_real_internal_navigation_completed') ($result.host -eq 'native-internal-preview' -and $result.state -eq 'Foreground' -and $result.appId -eq $Sample.appId -and $result.sessionId -match '^[a-f0-9]{32}$')
    $Sample.session=$result.sessionId
    $event=Wait-SmokeCondition {@(Read-DeveloperEvents|Select-Object -Skip $before|Where-Object {$_.state -eq 'Foreground' -and $_.eventName -eq 'navigation_completed'})|Select-Object -Last 1} 'actual new preview runtime instance' 15
    $Sample.runtime_instance=$event.instanceId
    return $result.sessionId
}

try{
    if(-not $CliOnly -and @(Get-CimInstance Win32_Process|Where-Object {$_.Name -in @('AutumnOS.exe','AutumnOS.Client.exe','AutumnOS.Updater.exe')}).Count){throw 'Another AutumnOS product/updater is running. This driver will not take it over or close it.'}
    Assert-T05PlainPath $ReportDirectory
    New-Item -ItemType Directory -Path $ReportDirectory,$testScope,$cwd|Out-Null
    Copy-PlainTree (Split-Path $ExecutablePath -Parent) $stageDirectory @('AutumnOS_Data','.autumnos-update')
    Copy-PlainTree $DeveloperDirectory $cliRoot @('AutumnOS_Data','.autumnos-update')
    Add-SmokeCheck 'fresh_copy_contains_no_user_or_update_data' (-not(Test-Path -LiteralPath $dataDirectory) -and -not(Test-Path -LiteralPath (Join-Path $stageDirectory '.autumnos-update')))
    foreach($file in @('AutumnOS.Developer.Cli.exe','AutumnOS.Developer.Cli.dll','hostfxr.dll','hostpolicy.dll','coreclr.dll','SDK/autumn-sdk.js')){
        Add-SmokeCheck ('delivered_cli_resource_'+$file.Replace('/','_')) (Test-Path -LiteralPath (Join-Path $cliRoot $file) -PathType Leaf)
    }
    $metadata=@{};$assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $cliRoot 'AutumnOS.Developer.Cli.dll')))
    foreach($attribute in $assembly.GetCustomAttributesData()){if($attribute.AttributeType.Name -eq 'AssemblyMetadataAttribute'){$metadata[$attribute.ConstructorArguments[0].Value]=$attribute.ConstructorArguments[1].Value}}
    $report.build_id=$metadata.BuildId;$report.source_snapshot_id=$metadata.SourceSnapshotId
    $receipt=Read-T05Json (Join-Path $stageDirectory 'autumn.install.json')
    Add-SmokeCheck 'delivered_host_and_CLI_same_tracked_build' ($receipt.buildId -eq $metadata.BuildId -and $metadata.SourceSnapshotId -match '^sha256:[a-f0-9]{64}$')
    foreach($file in $receipt.files){if((Get-T05Hash (Join-Path $stageDirectory $file.path)) -ne $file.sha256){throw 'Copied host differs from its managed file receipt.'}}
    $runtime=Read-T05Json (Join-Path $cliRoot 'AutumnOS.Developer.Cli.runtimeconfig.json')
    Add-SmokeCheck 'CLI_selfcontained_runtimeconfig' ($runtime.runtimeOptions.includedFrameworks.Count -ge 1 -and -not $runtime.runtimeOptions.framework)
    $help=Finish-DeveloperCli (Start-DeveloperCli @('--help')) -TextOnly
    Add-SmokeCheck 'delivered_help_names_real_commands' ($help -match 'create <' -and $help -match 'preview <' -and $help -match 'debug <')
    $bad=Run-DeveloperCli @('execute','arbitrary.exe') 2
    Add-SmokeCheck 'unknown_host_command_is_rejected' ($bad.code -eq 'CLI_COMMAND_UNSUPPORTED')
    foreach($template in @('hello-app','identity-app','save-game','desktop-extension')){
        # Deliberately collide with the real installed bundled application's ID to exercise source-bound isolation.
        $id=if($template -eq 'save-game'){'cn.labchronicles.elementpairs'}else{'dev.t06.'+$template.Replace('-','')}
        $project=Join-Path $testScope ('projects/'+$template)
        $created=Run-DeveloperCli @('create',$template,$project,'--app-id',$id,'--name',('派蒙独立样例 '+$template))
        $validated=Run-DeveloperCli @('validate',$project)
        Add-SmokeCheck ($template+'_create_and_validate_from_delivered_resources') ($created.appId -eq $id -and $validated.appId -eq $id -and -not $validated.outputCreated -and (Test-Path -LiteralPath (Join-Path $project 'autumn-sdk.js')))
        $packed=Run-DeveloperCli @('pack',$project,(Join-Path $testScope ('packages/'+$template)))
        Add-SmokeCheck ($template+'_actual_package_matches_reported_sha') ((Get-T05Hash $packed.packagePath) -eq $packed.sha256)
        $publication=Run-DeveloperCli @('generate-release',$packed.packagePath,(Join-Path $testScope ('publication/'+$template)),'--developer','派蒙本地自测','--description','独立目录文档复现；没有发布','--offline','true')
        $checked=Run-DeveloperCli @('validate-publication',$publication.storePath,$publication.releasePath,$publication.packagePath)
        Add-SmokeCheck ($template+'_real_three_layer_publication_check') ($checked.sha256 -eq $packed.sha256 -and $checked.publishing -eq 'not_performed')
        $samples.Add([ordered]@{template=$template;appId=$id;project=$project;package=$packed.packagePath;version=$packed.version;sha256=$packed.sha256;session=$null;runtime_instance=$null;native='not_run'})
    }
    if($CliOnly){$report.status='passed_cli_only';return}
    $projectRoot=Split-Path $HelpersDirectory -Parent
    $desktopRuntime=Join-Path $projectRoot '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')){Add-Type -Path (Join-Path $desktopRuntime $name)}
    $strings=@($desktopAst.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('namespace AutumnDesktopInteractionSmoke')},$true))
    if($strings.Count -ne 1){throw 'Maintained native helper declaration missing.'}
    if(-not('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){Add-Type -TypeDefinition $strings[0].Value}
    Initialize-T05FocusNative
    $script:product=Start-SmokeProduct $cwd
    $trackedProcesses.Add([pscustomobject]@{Process=$product.Process;StartTicks=$product.Process.StartTime.ToUniversalTime().Ticks;Path=$product.Process.MainModule.FileName;Root=$stageDirectory})
    Click 'HelloNextButton';Click 'BrandNextButton';Click 'PrepareDesktopButton';Wait-SmokeDesktop $product
    $script:documentTitle='元素配对 · Lab Chronicles'
    Click 'SampleButton'
    $businessName=Wait-SmokeCondition {Find-DeveloperNamed '玩家昵称' ([Windows.Automation.ControlType]::Edit)} 'real installed business sample input'
    $businessName.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('原业务存档必须保留')
    Click-DeveloperWeb '保存进度';Click-Named '允许';$null=Wait-DeveloperText '已保存到本机'
    Click-DeveloperWebElement 't03-features' '账号、文件与桌面联动'
    $note=Wait-SmokeCondition {Find-DeveloperNamed '笔记内容（最多 240 字符）' ([Windows.Automation.ControlType]::Edit)} 'real installed private-note input'
    $note.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('原业务私有数据必须保留')
    Click-DeveloperWeb '申请私有存储权限';Click-Named '允许';$null=Wait-DeveloperText 'storage：granted'
    Click-DeveloperWeb '保存笔记设置';$null=Wait-DeveloperText '笔记设置已保存到当前账号'
    Click-DeveloperWeb '写入私有文件';$null=Wait-DeveloperText '已原子写入本应用私有文件'
    Click 'CloseGameButton';Click-Named '确认结束';Wait-SmokeDesktop $product
    $businessBefore=@(Snapshot-DeveloperProtected)
    $productionPermissionPath=Join-Path $dataDirectory 'Config/application-permissions.v1.json'
    $productionPermissionHash=Get-T05Hash $productionPermissionPath
    $report.business_data_before=$businessBefore
    Add-SmokeCheck 'real_installed_business_save_preferences_private_file_exist_before_same_id_preview' (
        @($businessBefore|Where-Object path -match '^Saves[\\/]cn.labchronicles.elementpairs[\\/]guest[\\/]game.json$').Count -eq 1 -and
        @($businessBefore|Where-Object path -match '^AppData[\\/]cn.labchronicles.elementpairs[\\/]').Count -ge 2)
    $rejected=Run-DeveloperCli @('preview',$samples[0].package,'--host',$stageDirectory) 2
    Add-SmokeCheck 'default_disabled_developer_pipe_refuses_preview' ($rejected.code -eq 'DEVELOPER_HOST_UNAVAILABLE')
    Click 'SettingsButton';Toggle 'SettingsNavDiagnostics' $true;Toggle 'DeveloperModeToggle' $true
    Click 'SettingsHomeButton';Click 'DeveloperButton';$null=Require 'DeveloperCreateProject'
    foreach($sample in $samples){
        $script:documentTitle=switch($sample.template){'hello-app'{'Hello Autumn'}'identity-app'{'最小资料授权'}'save-game'{'元素配对 · Lab Chronicles'}'desktop-extension'{'桌面扩展'}}
        $session=Start-ConfirmedPreview $sample
        $status=Invoke-DeveloperDebug $session 'status';Add-SmokeCheck ($sample.template+'_debug_status_scoped_to_preview') ($status.appId -eq $sample.appId -and $status.state -eq 'Foreground')
        switch($sample.template){
            'hello-app'{Click-DeveloperWeb '重新读取实例状态';$null=Wait-DeveloperText 'state=foreground；blocksMaintenance=true';Add-SmokeCheck 'hello_actual_runtime_state_read' $true}
            'identity-app'{
                Click-DeveloperWeb '读取已授权资料';$null=Wait-DeveloperText '操作未报告成功：AUTH_REQUIRED';Add-SmokeCheck 'identity_real_guest_rejects_profile_without_fabrication' $true
                $denied=Invoke-DeveloperDebug $session 'deny-permissions'
                Click-DeveloperWeb '查询授权状态';$null=Wait-DeveloperText 'identity.profile：denied'
                Add-SmokeCheck 'explicit_preview_permission_denial_reaches_real_SDK_query' ($denied.simulation.isTestSimulation -and $denied.simulation.permissionsDenied)
                $restored=Invoke-DeveloperDebug $session 'restore-permissions'
                Add-SmokeCheck 'restore_preview_permissions_disables_only_test_override' (-not $restored.simulation.permissionsDenied)
                $null=Switch-DeveloperTestAccount $session 'account-a' -Cancel
                $session=(Switch-DeveloperTestAccount $session 'account-a').sessionId
                Click-DeveloperWeb '申请并读取最小资料';Click-Named '允许'
                $aId=Read-DeveloperProfileText '开发测试账号 A' $false
                $offline=Invoke-DeveloperDebug $session 'offline'
                $null=Wait-DeveloperText '身份状态发生变化：offline_cached'
                Click-DeveloperWeb '读取已授权资料';$offlineId=Read-DeveloperProfileText '开发测试账号 A' $true
                Add-SmokeCheck 'offline_test_state_reaches_real_identity_event_and_cached_SDK_profile' ($offline.simulation.offline -and $offlineId -eq $aId -and $offline.simulation.networkScope -eq 'preview_only_external_network_already_blocked')
                $null=Invoke-DeveloperDebug $session 'online'
                $null=Wait-DeveloperText '身份状态发生变化：signed_in'
                Click-DeveloperWeb '读取已授权资料';$onlineId=Read-DeveloperProfileText '开发测试账号 A' $false
                Add-SmokeCheck 'online_restores_test_profile_cache_flag_without_changing_scoped_identity' ($onlineId -eq $aId)
                $session=(Switch-DeveloperTestAccount $session 'account-b').sessionId
                Click-DeveloperWeb '申请并读取最小资料';Click-Named '允许';$bId=Read-DeveloperProfileText '开发测试账号 B' $false
                Add-SmokeCheck 'two_test_accounts_have_distinct_actual_SDK_scoped_ids' ($aId -ne $bId)
                $session=(Switch-DeveloperTestAccount $session 'account-a').sessionId
                Click-DeveloperWeb '读取已授权资料';$aAgain=Read-DeveloperProfileText '开发测试账号 A' $false
                Add-SmokeCheck 'return_to_same_test_account_reuses_its_own_scoped_identity' ($aAgain -eq $aId)
                $session=(Switch-DeveloperTestAccount $session 'account-guest').sessionId
                Click-DeveloperWeb '读取已授权资料';$null=Wait-DeveloperText '操作未报告成功：AUTH_REQUIRED'
                $reset=Switch-DeveloperTestAccount $session 'reset-simulation';$session=$reset.sessionId
                Add-SmokeCheck 'reset_simulation_returns_real_guest_without_fabricated_identity' (-not $reset.simulation.isTestSimulation -and $reset.simulation.account -eq 'live')
                Click-DeveloperWeb '读取已授权资料';$null=Wait-DeveloperText '操作未报告成功：AUTH_REQUIRED'
                $sample.session=$session;$report.permission_simulation='passed_preview_only';$report.offline_simulation='passed_preview_identity_cache_only_not_system_network';$report.simulated_account_switch='passed_preview_only_not_real_Logto'
            }
            'save-game'{
                $edit=Wait-SmokeCondition {Find-DeveloperNamed '玩家昵称' ([Windows.Automation.ControlType]::Edit)} 'owned save-game nickname'
                $savedText='派蒙 T06 真实存档';$edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($savedText)
                Click-DeveloperWeb '保存进度';Click-Named '允许';$null=Wait-DeveloperText '已保存格式 2'
                $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('尚未保存的输入')
                Click-DeveloperWeb '读取存档'
                $null=Wait-SmokeCondition {$edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $savedText} 'real SDK save readback' 15
                Add-SmokeCheck 'save_game_actual_save_and_read_restore_input' $true
                $session=(Switch-DeveloperTestAccount $session 'account-a').sessionId
                Click-DeveloperWeb '读取存档';Click-Named '允许';$null=Wait-DeveloperText '本机还没有这款游戏的存档'
                $edit=Find-DeveloperNamed '玩家昵称' ([Windows.Automation.ControlType]::Edit);$edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('模拟 A 独立存档')
                Click-DeveloperWeb '保存进度';$null=Wait-DeveloperText '已保存格式 2'
                $session=(Switch-DeveloperTestAccount $session 'account-b').sessionId
                Click-DeveloperWeb '读取存档';Click-Named '允许';$null=Wait-DeveloperText '本机还没有这款游戏的存档'
                $edit=Find-DeveloperNamed '玩家昵称' ([Windows.Automation.ControlType]::Edit);$edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('模拟 B 独立存档')
                Click-DeveloperWeb '保存进度';$null=Wait-DeveloperText '已保存格式 2'
                $session=(Switch-DeveloperTestAccount $session 'account-a').sessionId
                Click-DeveloperWeb '读取存档'
                $null=Wait-SmokeCondition {$edit=Find-DeveloperNamed '玩家昵称' ([Windows.Automation.ControlType]::Edit);$edit -and $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq '模拟 A 独立存档'} 'real same-source A save isolated from B' 15
                Add-SmokeCheck 'actual_test_account_A_B_save_writes_remain_separate_and_A_restores' $true
                $reset=Switch-DeveloperTestAccount $session 'reset-simulation';$session=$reset.sessionId
                Click-DeveloperWeb '读取存档';Click-Named '允许'
                $null=Wait-SmokeCondition {$edit=Find-DeveloperNamed '玩家昵称' ([Windows.Automation.ControlType]::Edit);$edit -and $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $savedText} 'real guest preview save survived simulated A and B writes' 15
                Add-SmokeCheck 'reset_to_real_guest_reads_its_original_preview_save' $true
                $sample.session=$session
            }
            'desktop-extension'{
                Click-DeveloperWeb '申请内部链接权限';Click-Named '允许';$null=Wait-DeveloperText 'links：granted'
                Click-DeveloperWeb '投递 focus 动作';$null=Wait-DeveloperText '收到真实动作：focus'
                Add-SmokeCheck 'desktop_extension_actual_host_event_observed' $true
            }
        }
        $background=Invoke-DeveloperDebug $session 'background';Add-SmokeCheck ($sample.template+'_debug_background_real_state') ($background.state -eq 'Background')
        $foreground=Invoke-DeveloperDebug $session 'foreground';Add-SmokeCheck ($sample.template+'_debug_foreground_same_session') ($foreground.state -eq 'Foreground' -and $foreground.sessionId -eq $session)
        $trace=Invoke-DeveloperDebug $session 'trace'
        Add-SmokeCheck ($sample.template+'_debug_trace_is_nonempty_bounded_metadata_only') ($trace.trace.Count -gt 0 -and $trace.trace.Count -le 200 -and @($trace.trace|Where-Object {@($_.PSObject.Properties.Name|Where-Object {$_ -notin @('timestamp','method','resultCode')}).Count}).Count -eq 0)
        $capture=Save-SmokeScreenshot $product ($sample.template+'-real-preview') -IncludeOverlays
        Add-SmokeCheck ($sample.template+'_actual_owned_preview_screenshot') ([bool]$capture)
        $sample.native='passed'
        if($sample.template -ne 'desktop-extension'){
            $closed=Invoke-DeveloperDebug $session 'close';Add-SmokeCheck ($sample.template+'_debug_close_only_owned_preview') ($closed.state -eq 'Closed')
            $expired=Invoke-DeveloperDebug $session 'status' 2;Add-SmokeCheck ($sample.template+'_old_session_not_reusable') ($expired.code -eq 'DEVELOPER_PREVIEW_SESSION_EXPIRED')
            if($sample.template -eq 'save-game'){
                Assert-DeveloperProtected $businessBefore 'same_app_id_preview_did_not_change_installed_business_program_save_preferences_or_files'
                $previewBefore=@(Snapshot-DeveloperProtected)
                $originalPackageHash=Get-T05Hash $sample.package
                [IO.File]::AppendAllText((Join-Path $sample.project 'index.html'),"`n<!-- actual developer edit; same app ID and version -->`n",[Text.UTF8Encoding]::new($false))
                $rebuilt=Run-DeveloperCli @('pack',$sample.project,(Join-Path $testScope 'packages/same-version-rebuild'))
                Add-SmokeCheck 'rebuild_changed_real_package_bytes_without_renaming_version_or_old_package' ($rebuilt.appId -eq $sample.appId -and $rebuilt.version -eq $sample.version -and $rebuilt.sha256 -ne $sample.sha256 -and (Get-T05Hash $sample.package) -eq $originalPackageHash)
                $variant=[ordered]@{template='save-game-same-version-rebuild';appId=$rebuilt.appId;package=$rebuilt.packagePath;sha256=$rebuilt.sha256;session=$null;runtime_instance=$null}
                $variantSession=Start-ConfirmedPreview $variant
                Click-DeveloperWeb '读取存档';Click-Named '允许';$null=Wait-DeveloperText '本机还没有这款游戏的存档'
                Add-SmokeCheck 'same_version_different_payload_preview_has_separate_cache_and_private_save_source' $true
                $variantEdit=Find-DeveloperNamed '玩家昵称' ([Windows.Automation.ControlType]::Edit)
                $variantEdit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('重新打包的独立预览')
                Click-DeveloperWeb '保存进度';$null=Wait-DeveloperText '已保存格式 2'
                $null=Invoke-DeveloperDebug $variantSession 'close'
                Assert-DeveloperProtected $previewBefore 'rebuilt_preview_preserves_old_preview_and_business_data'
                $report.same_version_rebuild=$variant
                $cache=Join-Path $dataDirectory 'Runtime/DeveloperPackages'
                Add-SmokeCheck 'both_actual_preview_cache_hash_roots_are_retained' ((Test-Path -LiteralPath (Join-Path $cache $sample.sha256)) -and (Test-Path -LiteralPath (Join-Path $cache $rebuilt.sha256)))
            }
        }
    }
    $last=$samples[-1].session
    Click 'DeveloperHome';Wait-SmokeDesktop $product;Click 'SettingsButton';Toggle 'SettingsNavDiagnostics' $true;Toggle 'DeveloperModeToggle' $false
    $disabled=Invoke-DeveloperDebug $last 'trace' 2
    Add-SmokeCheck 'disabled_mode_stops_actual_pipe_and_rejects_previous_preview_session' ($disabled.code -eq 'DEVELOPER_HOST_UNAVAILABLE')
    $closed=Wait-SmokeCondition {@(Read-DeveloperEvents|Where-Object {$_.instanceId -eq $samples[-1].runtime_instance -and $_.state -eq 'Closed' -and -not $_.blocksMaintenance}).Count -gt 0} 'disabled mode closes exact actual preview runtime' 15
    Add-SmokeCheck 'disabled_mode_closed_the_real_preview' ([bool]$closed)
    Assert-DeveloperProtected $businessBefore 'all_developer_operations_preserve_original_installed_app_and_data'
    Add-SmokeCheck 'preview_and_simulation_do_not_modify_production_permission_file' ((Get-T05Hash $productionPermissionPath) -eq $productionPermissionHash)
    Close-SmokeProduct $product
    Add-SmokeCheck 'different_working_directory_receives_no_host_data' (-not(Test-Path -LiteralPath (Join-Path $cwd 'AutumnOS_Data')))
    $report.native_status='passed';$report.status='passed'
}catch{
    $report.failure_type=$_.Exception.GetType().FullName;$report.failure=$_.Exception.Message;$report.failure_stack=$_.ScriptStackTrace
    Write-Warning ('T06 developer smoke: '+$report.failure)
    if($product -and -not $product.Process.HasExited){try{$null=Save-SmokeScreenshot $product 'failure' -IncludeOverlays}catch{}}
}finally{
    $clean=$true
    foreach($process in $ownedProcesses){try{if(-not $process.HasExited){$process.Refresh();if($process.MainWindowHandle -eq [IntPtr]::Zero -or -not $process.CloseMainWindow() -or -not $process.WaitForExit(10000)){$clean=$false;$report.remaining_processes+=@{pid=$process.Id;action='left_running_no_forced_termination'}}}}catch{$clean=$false}}
    foreach($entry in $ownedEntryProcesses){try{if(-not $entry.WaitForExit(10000)){$clean=$false;$report.remaining_processes+=@{pid=$entry.Id;action='bootstrap_left_running'}}}catch{$clean=$false}}
    foreach($entry in $cliProcesses){try{if(-not $entry.Process.WaitForExit(5000)){$clean=$false;$report.remaining_processes+=@{pid=$entry.Process.Id;action='bounded_CLI_left_running'}}}catch{$clean=$false}}
    Restore-AutumnNativePointer
    if($script:initialPointerPosition -and ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){$null=[AutumnDesktopInteractionSmoke.NativeWindows]::SetCursorPos($script:initialPointerPosition.X,$script:initialPointerPosition.Y)}
    $report.cleanup=if($clean){'passed'}else{'failed'};if(-not $clean){$report.status='failed'}
    $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o');$report.check_count=$checks.Count;$report.passed_checks=@($checks|Where-Object status -eq 'passed').Count
    if(Test-Path -LiteralPath $ReportDirectory){$report|ConvertTo-Json -Depth 20|Set-Content -LiteralPath (Join-Path $ReportDirectory 'developer-smoke.json') -Encoding utf8}
    foreach($process in $ownedProcesses){$process.Dispose()};foreach($entry in $ownedEntryProcesses){$entry.Dispose()};foreach($entry in $cliProcesses){$entry.Process.Dispose()}
}
if($report.status -notin @('passed','passed_cli_only')){throw "T06 developer smoke failed; preserved report: $ReportDirectory"}
