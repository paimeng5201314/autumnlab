[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [string]$ReportDirectory,
    [ValidateRange(10,90)][int]$WindowTimeoutSeconds = 45,
    [switch]$ProductionOnly
)

# UI-only product operations. No app/config injection, no direct install registry writes, no process killing.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/Smoke-NativeAppInput.ps1"
$smokeId = 'T04-store-smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,8)
$testScope = [IO.Path]::GetFullPath((Join-Path $projectRoot "artifacts/smoke/$smokeId"))
$stageDirectory = Join-Path $testScope '隔离商店 原生实测'
if (-not $ReportDirectory) { $ReportDirectory = $testScope }
$ReportDirectory = [IO.Path]::GetFullPath($ReportDirectory)
$reportPath = Join-Path $ReportDirectory 'store-smoke.json'
if (Test-Path -LiteralPath $reportPath) { throw 'Choose a new ReportDirectory; previous evidence is preserved.' }
New-Item -ItemType Directory -Path $ReportDirectory -Force | Out-Null
$checks = [Collections.Generic.List[object]]::new()
$screenshots = [Collections.Generic.List[object]]::new()
$inputActions = [Collections.Generic.List[object]]::new()
$ownedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$ownedEntryProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$script:initialPointerPosition = $null
$product = $null
$report = [ordered]@{
    schema_version=1;task_id='T04';checkpoint='public_store_and_explicit_local_fixture_native_flow'
    smoke_id=$smokeId;status='failed';started_utc=[DateTimeOffset]::UtcNow.ToString('o')
    environment=[Environment]::OSVersion.VersionString;build_id='local-untracked';source_snapshot_id='not_recorded'
    source_executable=$null;tested_executable=$null;source_executable_sha256=$null
    driver_sha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    imported_helpers_sha256=@{};process_ids=@();bootstrap_process_ids=@();checks=$checks;screenshots=$screenshots;input_actions=$inputActions
    public_github=[ordered]@{status='not_run';query='topic:autumnos-app is:public';observed_ui=$null;real_release_install='not_run'}
    network_settings=[ordered]@{persistence='not_run';connection='not_run';observed_ui=$null;asset_connection='not_run'}
    controlled_source=[ordered]@{status='not_run';identity='cn.labchronicles.storeprobe';repository_id=90004001;public_github_data=$false}
    first_instance=$null;resumed_instance=$null;new_instance=$null;saved_text_sha256=$null;unsaved_text_sha256=$null
    installed_policy_restart='not_run';repair='not_run';uninstall_preserves_saves='not_run';running_update='not_run'
    malformed_hash='not_run';running_update_evidence=$null;system_store_notice='not_run';windows_toast='not_run';full_t04_acceptance='not_run';t03_closeout='deferred_by_user';t05='not_run'
    physical_input='not_run';cleanup='not_run';test_data_retained=$true;visual_review='not_run'
}

# Reuse only named function definitions and the inert native type declaration from the existing maintained driver.
# The T02 driver's executable test body and its historical forced-cleanup branch are never executed.
$helperPath = Join-Path $PSScriptRoot 'Test-DesktopInteractionSmoke.ps1'
$tokens=$null;$parseErrors=$null
$helperAst=[Management.Automation.Language.Parser]::ParseFile($helperPath,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'The maintained UI smoke helper source cannot be parsed.'}
$helperNames=@('Add-SmokeCheck','Wait-SmokeCondition','Find-SmokeElement','Find-VisibleSmokeElement','Invoke-SmokeButton',
    'Test-SmokeForeground','Focus-SmokeProduct','Assert-SmokeCaptureUnoccluded','Invoke-SmokePointer','Wait-SmokeDesktop',
    'Read-SmokeEvents','Wait-SmokeNewForeground','Start-SmokeProduct','Close-SmokeProduct','Save-SmokeScreenshot')
foreach($name in $helperNames){
    $definition=@($helperAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name},$true))
    if($definition.Count -ne 1){throw "Expected one maintained helper function: $name"}
    . ([ScriptBlock]::Create($definition[0].Extent.Text))
}
$report.imported_helpers_sha256['Test-DesktopInteractionSmoke.ps1']=(Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash.ToLowerInvariant()
$report.imported_helpers_sha256['Smoke-NativeAppInput.ps1']=(Get-FileHash -LiteralPath (Join-Path $PSScriptRoot 'Smoke-NativeAppInput.ps1') -Algorithm SHA256).Hash.ToLowerInvariant()

function Show-Element([string]$Id){
    $element=Wait-SmokeCondition {Find $Id} "existing element $Id" 6
    for($attempt=0;$attempt -lt 25;$attempt++){
        if(-not $element.Current.IsOffscreen -and -not $element.Current.BoundingRectangle.IsEmpty){return $element}
        $itemPattern=$null
        if($element.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern,[ref]$itemPattern)){$itemPattern.ScrollIntoView()}
        else{
            $ancestor=[Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($element)
            $scrollPattern=$null
            for($depth=0;$depth -lt 14 -and $ancestor;$depth++){
                if($ancestor.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern,[ref]$scrollPattern)){
                    $direction=if($element.Current.BoundingRectangle.Top -lt $ancestor.Current.BoundingRectangle.Top){[Windows.Automation.ScrollAmount]::LargeDecrement}else{[Windows.Automation.ScrollAmount]::LargeIncrement}
                    $scrollPattern.Scroll([Windows.Automation.ScrollAmount]::NoAmount,$direction);break
                }
                $ancestor=[Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($ancestor)
            }
            if(-not $scrollPattern){throw "Cannot scroll UIA target into the owned viewport: $Id"}
        }
        Start-Sleep -Milliseconds 90
        $element=Find $Id
    }
    throw "UIA element remained outside its scroll viewport: $Id"
}
function Click([string]$Id){
    $element=Show-Element $Id
    $null=Wait-SmokeCondition {$element.Current.IsEnabled} "enabled button $Id" 6
    $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Find([string]$Id){Find-SmokeElement $product.Element $Id}
function Visible([string]$Id){Find-VisibleSmokeElement $product.Element $Id}
function Require([string]$Id){return Wait-SmokeCondition {Find $Id} "UIA element ready: $Id" 6}
function Capture([string]$Name,[string]$ReadyId=''){
    if(-not $ReadyId){
        $ReadyId=switch($Name){
            '01-public-github-store' {'StoreHeading'}
            '02-github-network-settings' {'GitHubNetworkResult'}
            '03-labelled-test-source' {'StoreRepository-90004001'}
            '04-application-details' {'StoreHeading'}
            '05-stable-version-selection' {'StoreInstall-40001'}
            '06-download-task' {'DownloadState-1.0.0'}
            '07-installed-app-management' {'InstalledOpen-cn.labchronicles.storeprobe'}
            '08-installed-desktop' {'InstalledApp-cn.labchronicles.storeprobe'}
            '09-background-running-icon' {'InstalledApp-cn.labchronicles.storeprobe'}
            '10-background-operations' {'ManagedContinue'}
            '11-continued-internal-app' {'ManagedAppHome'}
            '12-reopened-save' {'ManagedAppHome'}
            '13-rejected-bad-hash' {'DownloadState-1.9.0'}
            '14-uninstalled-desktop-save-retained' {'StoreButton'}
            default {'StoreHeading'}
        }
    }
    $ready=Show-Element $ReadyId
    $stability=[pscustomobject]@{LastBounds=$null;Samples=0}
    $null=Wait-SmokeCondition {
        $candidate=Visible $ReadyId
        if(-not $candidate){$stability.Samples=0;return $false}
        $bounds=$candidate.Current.BoundingRectangle.ToString()
        if($bounds -eq $stability.LastBounds){$stability.Samples++}else{$stability.Samples=0;$stability.LastBounds=$bounds}
        return $stability.Samples -ge 2
    } "stable screenshot content $ReadyId" 6
    Start-Sleep -Milliseconds 180
    $path=Save-SmokeScreenshot $product $Name -IncludeOverlays
    Add-SmokeCheck "screenshot_$Name" ([bool]$path) 'Actual owned native product pixels; not a synthesized mockup.'
}
function Toggle([string]$Id,[bool]$Value){
    $element=Show-Element $Id
    $pattern=$element.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $target=if($Value){[Windows.Automation.ToggleState]::On}else{[Windows.Automation.ToggleState]::Off}
    if($pattern.Current.ToggleState -ne $target){$pattern.Toggle()}
    $null=Wait-SmokeCondition {$pattern.Current.ToggleState -eq $target} "saved toggle $Id" 6
    $category=switch($Id){
        'SettingsNavAppearance' { @{Title='外观';Ready='LightThemeButton'} }
        'SettingsNavGitHub' { @{Title='GitHub 加速';Ready='GitHubDirectFallback'} }
        'SettingsNavDiagnostics' { @{Title='开发者诊断';Ready='DeveloperModeToggle'} }
        default {$null}
    }
    if($Value -and $category){
        $null=Wait-SmokeCondition {$title=Visible 'SettingsDetailTitle';$title -and $title.Current.Name -eq $category.Title} "settings detail category $($category.Title)" 6
        $null=Wait-SmokeCondition {Find $category.Ready} "rendered category control $($category.Ready)" 6
    }
}
function Value([string]$Id,[string]$Text){
    $element=Wait-SmokeCondition {Find $Id} "edit $Id" 6
    $element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
}
function Set-Number([string]$Id,[double]$Number){
    $element=Wait-SmokeCondition {Find $Id} "number field $Id" 6
    $pattern=$null
    if($element.TryGetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern,[ref]$pattern)){$pattern.SetValue($Number);return}
    if($element.TryGetCurrentPattern([Windows.Automation.ValuePattern]::Pattern,[ref]$pattern)){$pattern.SetValue($Number.ToString([Globalization.CultureInfo]::InvariantCulture));return}
    $numberEditFilter=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Edit)
    $edit=$element.FindFirst([Windows.Automation.TreeScope]::Descendants,$numberEditFilter)
    if(-not $edit){throw "Number field has no supported UIA value: $Id"}
    $edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Number.ToString([Globalization.CultureInfo]::InvariantCulture))
}
function Find-Named([string]$Name,$ControlType){
    $namedElementFilter=[Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,$ControlType),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::IsOffscreenProperty,$false))
    $element=$product.Element.FindFirst([Windows.Automation.TreeScope]::Descendants,$namedElementFilter)
    if($element){return $element}
    $docCondition=[Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,'商店链路测试 · 本地样例'),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Document))
    $documents=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Descendants,$docCondition)
    $windowBounds=$product.Element.Current.BoundingRectangle
    foreach($document in $documents){
        $bounds=$document.Current.BoundingRectangle
        if($bounds.IsEmpty -or $bounds.Left -lt $windowBounds.Left -or $bounds.Top -lt $windowBounds.Top -or $bounds.Right -gt $windowBounds.Right -or $bounds.Bottom -gt $windowBounds.Bottom){continue}
        if(-not (Test-AutumnOwnedInputProcess $document.Current.ProcessId $product.Process)){continue}
        $element=$document.FindFirst([Windows.Automation.TreeScope]::Descendants,$namedElementFilter)
        if($element){return $element}
    }
    return $null
}
function Click-Named([string]$Name, [ValidateSet('PrimaryButton','CloseButton')][string]$DialogButton='PrimaryButton'){
    # Every caller of this helper acts on a ContentDialog. A same-name button behind
    # the dialog is not a confirmation, even if UIA briefly reports it as enabled.
    $primaryButtonFilter=[Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$DialogButton),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::IsEnabledProperty,$true),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::IsOffscreenProperty,$false))
    $element=Wait-SmokeCondition {
        $scopes=[Collections.Generic.List[object]]::new();$scopes.Add($product.Element)
        $owned=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$product.Process.Id)
        $windows=[Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$owned)
        foreach($native in $windows){$scopes.Add($native)}
        foreach($scopeElement in $scopes){
            $candidate=$scopeElement.FindFirst([Windows.Automation.TreeScope]::Descendants,$primaryButtonFilter)
            if($candidate -and $candidate.Current.ProcessId -eq $product.Process.Id -and -not $candidate.Current.BoundingRectangle.IsEmpty){return $candidate}
        }
        return $null
    } "owned ContentDialog $DialogButton named $Name" 6
    $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null=Wait-SmokeCondition {
        try{return $element.Current.IsOffscreen -or $element.Current.BoundingRectangle.IsEmpty}
        catch [Windows.Automation.ElementNotAvailableException]{return $true}
    } "ContentDialog confirmation $Name dismissed" 6
    Start-Sleep -Milliseconds 350
}
function Failure-Buttons{
    $items=[Collections.Generic.List[object]]::new()
    if(-not $product -or $product.Process.HasExited){return @()}
    $failureButtonFilter=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)
    $scopes=[Collections.Generic.List[object]]::new();$scopes.Add($product.Element)
    $owned=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$product.Process.Id)
    foreach($native in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$owned)){$scopes.Add($native)}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($scopeElement in $scopes){
        foreach($button in $scopeElement.FindAll([Windows.Automation.TreeScope]::Descendants,$failureButtonFilter)){
            if($items.Count -ge 100){break}
            if($button.Current.ProcessId -ne $product.Process.Id){continue}
            $name=[string]$button.Current.Name;$id=[string]$button.Current.AutomationId
            if($name.Length -gt 160){$name=$name.Substring(0,160)};if($id.Length -gt 160){$id=$id.Substring(0,160)}
            $identity=$id+'|'+$name+'|'+$button.Current.IsOffscreen
            if(-not $seen.Add($identity)){continue}
            $items.Add([ordered]@{name=$name;automation_id=$id;enabled=$button.Current.IsEnabled;offscreen=$button.Current.IsOffscreen;process_id=$button.Current.ProcessId})
        }
    }
    return @($items)
}
function Click-Web([string]$Name){
    $element=Wait-SmokeCondition {$item=Find-Named $Name ([Windows.Automation.ControlType]::Button);if($item -and $item.Current.IsEnabled){return $item}} "web action $Name" 6
    $inputActions.Add((Invoke-AutumnNativeElementClick -WindowElement $product.Element -Element $element -Process $product.Process))
}
function Probe-Input{
    return Wait-SmokeCondition {Find-Named '测试文字（不会发送到网络）' ([Windows.Automation.ControlType]::Edit)} 'owned internal sample editor' 6
}
function Input-Value{(Probe-Input).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value}
function Set-Input([string]$Text){(Probe-Input).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Text)}
function Wait-Download([string]$Version,[string]$Expected){
    return Wait-SmokeCondition {$row=Find "DownloadState-$Version";if($row -and $row.Current.Name -match $Expected){return $row.Current.Name}} "download $Version state $Expected" 30
}
function Save-Map{
    if(-not (Test-Path -LiteralPath (Join-Path $dataDirectory 'Saves'))){return @()}
    return @(Get-ChildItem -LiteralPath (Join-Path $dataDirectory 'Saves') -File -Recurse | Sort-Object FullName | ForEach-Object{
        [pscustomobject]@{path=[IO.Path]::GetRelativePath($dataDirectory,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
}
function Probe-Registration{
    $path=Join-Path $dataDirectory 'Apps/.autumnos-registry.json'
    $registry=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json
    $apps=@($registry.applications|Where-Object{$_.appId -eq 'cn.labchronicles.storeprobe' -and $_.isInstalled})
    if($apps.Count -ne 1){throw 'Expected exactly one live probe registration.'}
    return $apps[0]
}
function Content-Map([string]$Directory){
    $absolute=[IO.Path]::GetFullPath($Directory)
    $allowed=[IO.Path]::GetFullPath((Join-Path $dataDirectory 'Apps'))+[IO.Path]::DirectorySeparatorChar
    if(-not $absolute.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Probe content escaped this test-owned Apps directory.'}
    return @(Get-ChildItem -LiteralPath $absolute -Recurse -File|Sort-Object FullName|ForEach-Object{
        if(($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Refusing reparse point in installed probe content.'}
        [pscustomobject]@{path=[IO.Path]::GetRelativePath($absolute,$_.FullName);sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    })
}

try{
    if(-not $IsWindows){throw 'Native Store smoke requires an interactive Windows desktop.'}
    $existing=@(Get-Process -Name AutumnOS,AutumnOS.Client -ErrorAction SilentlyContinue)
    if($existing.Count){$report.status='blocked';throw 'Existing user launcher detected. Ask for normal user exit; this test will not stop it.'}
    Add-SmokeCheck 'no_existing_user_launcher' ($existing.Count -eq 0)
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    Add-SmokeCheck 'standard_user' (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
    $ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath)
    if([IO.Path]::GetFileName($ExecutablePath) -ne 'AutumnOS.exe' -or -not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)){throw 'A real delivered AutumnOS.exe is required.'}
    $report.source_executable=$ExecutablePath
    $report.source_executable_sha256=(Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    if(Test-Path -LiteralPath $stageDirectory){throw 'The isolated smoke directory already exists.'}
    New-Item -ItemType Directory -Path $stageDirectory -Force | Out-Null
    foreach($entry in Get-ChildItem -LiteralPath (Split-Path $ExecutablePath -Parent) -Force){
        if($entry.Name -eq 'AutumnOS_Data'){continue}
        if(($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Refusing redirected build output.'}
        if($entry.PSIsContainer -and @(Get-ChildItem -LiteralPath $entry.FullName -Recurse -Force | Where-Object{($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0}).Count){throw 'Refusing nested redirected build output.'}
        Copy-Item -LiteralPath $entry.FullName -Destination $stageDirectory -Recurse
    }
    $report.tested_executable=Join-Path $stageDirectory 'AutumnOS.exe'
    Add-SmokeCheck 'isolated_executable_matches_delivered_executable' ((Get-FileHash -LiteralPath $report.tested_executable -Algorithm SHA256).Hash.ToLowerInvariant() -eq $report.source_executable_sha256)
    $dataDirectory=Join-Path $stageDirectory 'AutumnOS_Data'
    Add-SmokeCheck 'user_data_excluded_from_copy' (-not (Test-Path -LiteralPath $dataDirectory))
    $businessName=if(Test-Path -LiteralPath (Join-Path $stageDirectory 'AutumnOS.Client.exe') -PathType Leaf){'AutumnOS.Client'}else{'AutumnOS'}
    $report.business_executable=Join-Path $stageDirectory ($businessName+'.exe')
    $report.source_business_sha256=(Get-FileHash -LiteralPath (Join-Path (Split-Path $ExecutablePath -Parent) ($businessName+'.exe')) -Algorithm SHA256).Hash.ToLowerInvariant()
    Add-SmokeCheck 'isolated_business_executable_matches_delivered_business' ((Get-FileHash -LiteralPath $report.business_executable -Algorithm SHA256).Hash.ToLowerInvariant() -eq $report.source_business_sha256)
    foreach($resource in @(($businessName+'.pri'),($businessName+'.deps.json'),($businessName+'.runtimeconfig.json'),'App.xbf','MainWindow.xbf')){Add-SmokeCheck "resource_$resource" (Test-Path -LiteralPath (Join-Path $stageDirectory $resource) -PathType Leaf)}
    # Metadata inspection must not leave this DLL file mapped by the PowerShell host.
    $assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $stageDirectory 'AutumnOS.Contracts.dll')))
    foreach($attribute in $assembly.GetCustomAttributesData()){
        if($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute'){
            $key=$attribute.ConstructorArguments[0].Value;$val=$attribute.ConstructorArguments[1].Value
            if($key -eq 'BuildId'){$report.build_id=$val};if($key -eq 'SourceSnapshotId'){$report.source_snapshot_id=$val}
        }
    }
    $desktopRuntime=Join-Path $projectRoot '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')){Add-Type -Path (Join-Path $desktopRuntime $name)}
    if(-not ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){
        $nativeStrings=@($helperAst.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('namespace AutumnDesktopInteractionSmoke')},$true))
        if($nativeStrings.Count -ne 1){throw 'Expected exactly one maintained native window declaration.'}
        Add-Type -TypeDefinition $nativeStrings[0].Value
    }
    $cwd=Join-Path $testScope 'different working directory';New-Item -ItemType Directory -Path $cwd -Force | Out-Null
    $product=Start-SmokeProduct $cwd
    Click 'HelloNextButton';Click 'BrandNextButton';Click 'PrepareDesktopButton';Wait-SmokeDesktop $product
    Click 'StoreButton'
    $null=Wait-SmokeCondition {Visible 'StoreHeading'} 'native store heading' 6
    $publicText=Wait-SmokeCondition {$item=Find 'StoreStatus';if($item -and $item.Current.Name -and $item.Current.Name -notmatch '正在读取'){$item.Current.Name}} 'real GitHub discovery result or honest network error' 60
    $report.public_github.observed_ui=$publicText;$report.public_github.status='observed_real_network_result'
    Add-SmokeCheck 'default_store_is_public_github' ((Require 'StoreSource').Current.Name -eq 'GitHub 公开应用')
    Capture '01-public-github-store'
    Click 'StoreHome';Wait-SmokeDesktop $product;Click 'SettingsButton';Toggle 'SettingsNavAppearance' $true
    Click 'DarkThemeButton'
    $null=Wait-SmokeCondition {$status=Find 'PreferencesStatus';$status -and $status.Current.Name -match '^已保存.*深色'} 'dark theme saved by existing appearance controls' 6
    $appearancePath=Join-Path $dataDirectory 'Config/desktop-preferences.json'
    Add-SmokeCheck 'dark_store_uses_real_saved_theme' ((Get-Content -LiteralPath $appearancePath -Raw|ConvertFrom-Json).theme -eq 'dark')
    Click 'SettingsHomeButton';Wait-SmokeDesktop $product;Click 'StoreButton'
    $null=Wait-SmokeCondition {$heading=Visible 'StoreHeading';$status=Find 'StoreStatus';$heading -and $status -and $status.Current.Name -notmatch '正在读取'} 'public store dark appearance with completed discovery state' 60
    Capture '01b-public-github-store-dark' 'StoreHeading'
    Click 'StoreHome';Wait-SmokeDesktop $product;Click 'SettingsButton';Toggle 'SettingsNavAppearance' $true;Click 'LightThemeButton'
    $null=Wait-SmokeCondition {$status=Find 'PreferencesStatus';$status -and $status.Current.Name -match '^已保存.*浅色'} 'light theme restored through real settings' 6
    Add-SmokeCheck 'light_theme_restored_without_direct_config_write' ((Get-Content -LiteralPath $appearancePath -Raw|ConvertFrom-Json).theme -eq 'light')
    Click 'SettingsHomeButton';Wait-SmokeDesktop $product;Click 'StoreButton';$null=Require 'StoreHeading'
    Click 'StoreNetworkSettings';Toggle 'SettingsNavGitHub' $true
    Toggle 'GitHubDirectFallback' $false
    Set-Number 'DownloadConcurrency' 1;Set-Number 'DownloadSpeedLimit' 128
    Value 'GitHubApiTemplate' '';Value 'GitHubAssetTemplate' ''
    Click 'GitHubSaveSettings'
    $null=Wait-SmokeCondition {(Find 'GitHubNetworkResult').Current.Name -match '^已保存'} 'network settings persisted by the real UI' 6
    $settingsPath=Join-Path $dataDirectory 'Config/store-network.json'
    $networkBefore=Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    Add-SmokeCheck 'network_limits_written_by_settings' ($networkBefore.MaximumConcurrentDownloads -eq 1 -and $networkBefore.BytesPerSecond -eq 131072 -and -not $networkBefore.AllowDirectFallback)
    Click 'GitHubTestConnection'
    $connectionText=Wait-SmokeCondition {$item=Find 'GitHubNetworkResult';if($item -and $item.Current.Name -notmatch '正在检查|^已保存'){$item.Current.Name}} 'honest API connection result' 60
    $report.network_settings.connection='observed_real_network_result';$report.network_settings.observed_ui=$connectionText
    Capture '02-github-network-settings'
    $networkHash=(Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash
    Close-SmokeProduct $product
    $product=Start-SmokeProduct $cwd;Wait-SmokeDesktop $product
    Add-SmokeCheck 'network_settings_restore_exact_bytes' ((Get-FileHash -LiteralPath $settingsPath -Algorithm SHA256).Hash -eq $networkHash)
    Click 'SettingsButton';Toggle 'SettingsNavGitHub' $true
    Add-SmokeCheck 'network_fallback_toggle_restored' ((Require 'GitHubDirectFallback').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::Off)
    $report.network_settings.persistence='passed'
    if(-not $ProductionOnly){
        Toggle 'SettingsNavDiagnostics' $true;Toggle 'DeveloperModeToggle' $true
        Click 'SettingsHomeButton';Click 'DeveloperButton';Click 'DeveloperStoreTest';Click-Named '开启测试源'
        $null=Wait-SmokeCondition {Visible 'StoreHeading'} 'explicit local test source opens Store' 6
        Add-SmokeCheck 'fixture_source_has_visible_non_github_label' ((Require 'StoreSource').Current.Name -eq '本地集成测试数据，不是 GitHub 实时结果')
        $null=Wait-SmokeCondition {Visible 'StoreRepository-90004001'} 'test source repository' 6
        Capture '03-labelled-test-source'
        Click 'StoreRepository-90004001'
        $null=Wait-SmokeCondition {Find 'StoreVersionChannel'} 'real fixture details and versions' 6
        Capture '04-application-details'
        $installButton=Show-Element 'StoreInstall-40001'
        Add-SmokeCheck 'compatible_stable_asset_install_enabled' $installButton.Current.IsEnabled
        Capture '05-stable-version-selection'
        Click 'StoreInstall-40001';Click-Named '下载并安装'
        $null=Wait-Download '1.0.0' '下载中|排队|正在下载|校验|安装'
        Capture '06-download-task'
        $null=Wait-Download '1.0.0' '已安装'
        Click 'StoreNav-installed'
        $null=Wait-SmokeCondition {Find 'InstalledOpen-cn.labchronicles.storeprobe'} 'registered installed app' 6
        Capture '07-installed-app-management'
        Click 'StoreHome';Wait-SmokeDesktop $product
        $icon='InstalledApp-cn.labchronicles.storeprobe'
        $null=Wait-SmokeCondition {Visible $icon} 'installed desktop entry' 6
        Capture '08-installed-desktop'
        Click 'NotificationsButton'
        $noticeId='StoreNoticeOpen-cn.labchronicles.storeprobe'
        $null=Require $noticeId
        $noticeCondition=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$noticeId)
        Add-SmokeCheck 'installed_app_has_one_real_store_notice' ($product.Element.FindAll([Windows.Automation.TreeScope]::Descendants,$noticeCondition).Count -eq 1)
        Capture '18-native-store-install-notice' $noticeId
        Click $noticeId
        $null=Require 'InstalledOpen-cn.labchronicles.storeprobe'
        Add-SmokeCheck 'store_notice_opens_actual_installed_app_management' ([bool](Visible 'StoreHeading'))
        $report.system_store_notice='passed'
        Click 'StoreHome';Wait-SmokeDesktop $product
        $runtimeLog=Join-Path $dataDirectory 'Logs/runtime-events.jsonl'
        $before=@(Read-SmokeEvents $runtimeLog).Count
        Invoke-SmokePointer $product $icon 'single_click'
        $null=Probe-Input
        $firstEvent=Wait-SmokeNewForeground $runtimeLog $before ''
        $report.first_instance=[string]$firstEvent.instanceId
        $savedText="T04 persisted fixture $smokeId";$unsavedText="T04 unsaved retained $smokeId"
        Set-Input $savedText;Click-Web '保存测试文字'
        $permission=Wait-SmokeCondition {Find-Named '允许' ([Windows.Automation.ControlType]::Button)} 'real application saves permission' 6
        $permission.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
        $null=Wait-SmokeCondition {Find-Named '已保存到独立游客测试存档。' ([Windows.Automation.ControlType]::Text)} 'SDK save completed' 6
        $savedMap=@(Save-Map);Add-SmokeCheck 'sdk_save_created_real_data' ($savedMap.Count -gt 0)
        Set-Input $unsavedText
        $report.saved_text_sha256=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($savedText)))
        $report.unsaved_text_sha256=[Convert]::ToHexStringLower([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($unsavedText)))
        Click 'ManagedAppHome';Wait-SmokeDesktop $product
        Add-SmokeCheck 'background_icon_has_real_running_marker' ((Require $icon).Current.HelpText -match '运行中')
        Capture '09-background-running-icon'
        # Two real WebViews must appear together in the desktop-wide switcher.
        $sampleBaseline=@(Read-SmokeEvents $runtimeLog).Count
        Click 'SampleButton'
        $null=Wait-SmokeCondition {(Visible 'RuntimeStatus') -and (Visible 'RuntimeStatus').Current.Name -match '前台运行'} 'second real app foreground' 15
        $sampleInstance=(Wait-SmokeNewForeground $runtimeLog $sampleBaseline '').instanceId
        Add-SmokeCheck 'second_app_has_independent_real_instance' ($sampleInstance -ne $report.first_instance)
        Click 'BackgroundButton';Wait-SmokeDesktop $product
        Invoke-SmokePointer $product 'DesktopGestureHint' 'double_click'
        $null=Require 'RunningContinueButton';$null=Require 'ManagedContinue'
        Add-SmokeCheck 'blank_double_click_lists_two_live_instances' ((Require 'RunningSwitcherSummary').Current.Name -match '^2 个应用')
        Capture '19-two-running-apps' 'RunningSwitcherSummary'
        Click 'RunningEndButton';Click-Named '取消' 'CloseButton'
        Invoke-SmokePointer $product 'DesktopGestureHint' 'double_click'
        Add-SmokeCheck 'cancel_end_keeps_both_instances' ((Require 'RunningSwitcherSummary').Current.Name -match '^2 个应用' -and
            @(Read-SmokeEvents $runtimeLog | Where-Object {$_.instanceId -eq $sampleInstance -and $_.state -eq 'Closed'}).Count -eq 0)
        Click 'RunningEndButton';Click-Named '确认结束';Wait-SmokeDesktop $product
        Add-SmokeCheck 'ending_one_card_does_not_close_other_app' (@(Read-SmokeEvents $runtimeLog | Where-Object {$_.instanceId -eq $sampleInstance -and $_.state -eq 'Closed'}).Count -gt 0 -and
            @(Read-SmokeEvents $runtimeLog | Where-Object {$_.instanceId -eq $report.first_instance -and $_.state -eq 'Closed'}).Count -eq 0)
        $before=@(Read-SmokeEvents $runtimeLog).Count
        $forwarder=Start-Process -FilePath $report.tested_executable -WorkingDirectory $testScope -WindowStyle Hidden -PassThru
        $ownedProcesses.Add($forwarder);$report.process_ids+=$forwarder.Id
        Add-SmokeCheck 'relaunch_forwarder_exits' ($forwarder.WaitForExit(20000) -and $forwarder.ExitCode -eq 0)
        $product.Process.Refresh()
        Add-SmokeCheck 'relaunch_preserves_main_window_and_background' ($product.Process.MainWindowHandle -eq $product.Handle -and [bool](Visible 'StoreButton') -and -not (Visible 'ManagedAppHome'))
        Add-SmokeCheck 'relaunch_does_not_foreground_or_restart_game' (@(Read-SmokeEvents $runtimeLog | Select-Object -Skip $before | Where-Object{$_.state -eq 'Foreground'}).Count -eq 0)
        Invoke-SmokePointer $product 'DesktopGestureHint' 'double_click'
        $null=Wait-SmokeCondition {Visible 'ManagedContinue'} 'background operation panel' 6
        Capture '10-background-operations'
        $before=@(Read-SmokeEvents $runtimeLog).Count;Click 'ManagedContinue'
        $null=Probe-Input
        $continued=Wait-SmokeNewForeground $runtimeLog $before $report.first_instance
        $report.resumed_instance=[string]$continued.instanceId
        Add-SmokeCheck 'continue_preserves_unsaved_input_and_original_instance' ((Input-Value) -eq $unsavedText -and $report.first_instance -eq $report.resumed_instance)
        Capture '11-continued-internal-app'
        Click 'ManagedAppHome';Wait-SmokeDesktop $product
        # Download an upgrade while the 1.0 WebView and its unsaved input remain alive in the background.
        # The script reads installation evidence; it never edits registrations or copies a package into Apps.
        $oldRegistration=Probe-Registration
        Add-SmokeCheck 'running_update_starts_from_unpinned_1_0' ($oldRegistration.package.manifest.version -eq '1.0.0' -and -not $oldRegistration.pinnedVersion)
        $oldDirectory=$oldRegistration.package.directoryPath
        $oldContent=@(Content-Map $oldDirectory)|ConvertTo-Json -Depth 4 -Compress
        $oldRegistryHash=(Get-FileHash -LiteralPath (Join-Path $dataDirectory 'Apps/.autumnos-registry.json') -Algorithm SHA256).Hash
        $savesBeforeUpdate=@(Save-Map)|ConvertTo-Json -Depth 4 -Compress
        Click 'StoreButton';$null=Require 'StoreRepository-90004001';Click 'StoreRepository-90004001'
        $null=Require 'StoreInstall-40002';Click 'StoreInstall-40002';Click-Named '下载并安装'
        $blockedUpdate=Wait-Download '1.1.0' '(?s)^等待安装.*(应用仍在运行|PACKAGE_INSTALL_BUSY|PACKAGE_APP_RUNNING)'
        $stillOld=Probe-Registration
        Add-SmokeCheck 'running_upgrade_waits_without_registry_replacement' ($stillOld.package.manifest.version -eq '1.0.0' -and $stillOld.package.directoryPath -eq $oldDirectory -and
            (Get-FileHash -LiteralPath (Join-Path $dataDirectory 'Apps/.autumnos-registry.json') -Algorithm SHA256).Hash -eq $oldRegistryHash)
        Add-SmokeCheck 'running_upgrade_preserves_original_resource_and_save_bytes' ((@(Content-Map $oldDirectory)|ConvertTo-Json -Depth 4 -Compress) -eq $oldContent -and
            (@(Save-Map)|ConvertTo-Json -Depth 4 -Compress) -eq $savesBeforeUpdate)
        Add-SmokeCheck 'running_upgrade_does_not_close_old_instance' (@(Read-SmokeEvents $runtimeLog|Where-Object{$_.instanceId -eq $report.first_instance -and $_.state -eq 'Closed'}).Count -eq 0)
        Capture '15-upgrade-waits-for-background-app' 'DownloadState-1.1.0'
        Click 'StoreHome';Wait-SmokeDesktop $product;Invoke-SmokePointer $product 'DesktopGestureHint' 'double_click';Click 'ManagedContinue'
        $null=Probe-Input
        Add-SmokeCheck 'blocked_upgrade_preserves_live_unsaved_webview' ((Input-Value) -eq $unsavedText)
        Click 'ManagedAppHome';Wait-SmokeDesktop $product;Invoke-SmokePointer $product 'DesktopGestureHint' 'double_click';Click 'ManagedEnd';Click-Named '确认结束'
        Wait-SmokeDesktop $product
        $null=Wait-SmokeCondition {@(Read-SmokeEvents $runtimeLog | Where-Object{$_.instanceId -eq $report.first_instance -and $_.state -eq 'Closed' -and -not $_.blocksMaintenance}).Count -gt 0} 'real old instance closed' 6
        Add-SmokeCheck 'end_clears_running_state' ((Require $icon).Current.HelpText -notmatch '运行中')
        Click 'StoreButton';Click 'StoreNav-downloads';$null=Require 'Download-Install-1.1.0';Click 'Download-Install-1.1.0'
        $null=Wait-Download '1.1.0' '^已安装'
        $updated=Probe-Registration
        Add-SmokeCheck 'explicit_retry_after_end_commits_1_1' ($updated.package.manifest.version -eq '1.1.0' -and $updated.package.directoryPath -ne $oldDirectory)
        Add-SmokeCheck 'successful_update_keeps_old_content_and_original_saves' ((@(Content-Map $oldDirectory)|ConvertTo-Json -Depth 4 -Compress) -eq $oldContent -and
            (@(Save-Map)|ConvertTo-Json -Depth 4 -Compress) -eq $savesBeforeUpdate)
        Capture '16-upgrade-committed-after-real-end' 'DownloadState-1.1.0'
        $report.running_update='passed'
        $report.running_update_evidence=[ordered]@{blocked_ui=$blockedUpdate;old_version='1.0.0';new_version=$updated.package.manifest.version;
            old_content=[IO.Path]::GetRelativePath($dataDirectory,$oldDirectory);new_content=[IO.Path]::GetRelativePath($dataDirectory,$updated.package.directoryPath);
            original_registry_sha256=$oldRegistryHash;old_instance=$report.first_instance}
        Click 'StoreHome';Wait-SmokeDesktop $product
        $before=@(Read-SmokeEvents $runtimeLog).Count;Invoke-SmokePointer $product $icon 'single_click';$null=Probe-Input
        $newEvent=Wait-SmokeNewForeground $runtimeLog $before '';$report.new_instance=[string]$newEvent.instanceId
        Add-SmokeCheck 'opening_after_end_creates_new_instance' ($report.new_instance -ne $report.first_instance -and (Input-Value) -ne $unsavedText)
        $null=Wait-SmokeCondition {Find-Named '样例版本 1.1.0 · 存档格式 1' ([Windows.Automation.ControlType]::Text)} 'new WebView renders installed 1.1 resources' 6
        Click-Web '读取测试文字'
        $null=Wait-SmokeCondition {(Input-Value) -eq $savedText} 'new instance loads actual previous save' 6
        Capture '12-reopened-save'
        Click 'ManagedAppClose';Click-Named '确认结束';Wait-SmokeDesktop $product
        Click 'StoreButton';Click 'StoreNav-installed'
        Toggle 'AppPin-cn.labchronicles.storeprobe' $true;Toggle 'AppPreview-cn.labchronicles.storeprobe' $true
        $null=Wait-SmokeCondition {$policy=Probe-Registration;$policy.pinnedVersion -eq '1.1.0' -and $policy.allowPreview} 'updated application pin and preview persisted' 6
        Capture '17-updated-version-policy' 'AppPin-cn.labchronicles.storeprobe'
        Close-SmokeProduct $product;$product=Start-SmokeProduct $cwd;Wait-SmokeDesktop $product
        Click 'StoreButton'
        Add-SmokeCheck 'restart_defaults_to_production_not_test_source' ((Require 'StoreSource').Current.Name -eq 'GitHub 公开应用')
        Click 'StoreNav-installed'
        Add-SmokeCheck 'app_pin_and_preview_restore_on_restart' ((Require 'AppPin-cn.labchronicles.storeprobe').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::On -and
            (Require 'AppPreview-cn.labchronicles.storeprobe').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq [Windows.Automation.ToggleState]::On)
        $report.installed_policy_restart='passed'
        Click 'StoreHome';Click 'DeveloperButton';Click 'DeveloperStoreTest';Click-Named '开启测试源'
        $null=Wait-SmokeCondition {Visible 'StoreRepository-90004001'} 'explicit test source re-enabled' 6
        Click 'StoreRepository-90004001';$null=Wait-SmokeCondition {Find 'StoreInstall-40005'} 'bad hash fixture version' 6
        Click 'StoreInstall-40005';Click-Named '下载并安装'
        $bad=Wait-Download '1.9.0' 'DOWNLOAD_HASH_MISMATCH|摘要|哈希'
        Add-SmokeCheck 'bad_hash_has_visible_failure' ($bad -match '失败|DOWNLOAD_HASH_MISMATCH|摘要|哈希')
        Capture '13-rejected-bad-hash'
        $report.malformed_hash='passed'
        $beforeRepair=Probe-Registration
        $repairContent=@(Content-Map $beforeRepair.package.directoryPath)|ConvertTo-Json -Depth 4 -Compress
        $repairSaves=@(Save-Map)|ConvertTo-Json -Depth 4 -Compress
        Click 'StoreNav-installed';Click 'InstalledRepair-cn.labchronicles.storeprobe'
        $null=Wait-SmokeCondition {(Find 'StoreStatus').Current.Name -match '已提交|已修复|完整|当前版本'} 'same-version repair result' 6
        $afterRepair=Probe-Registration
        Add-SmokeCheck 'repair_preserves_pinned_version_verified_resources_and_saves' ($afterRepair.package.manifest.version -eq '1.1.0' -and
            $afterRepair.package.packageSha256 -eq $beforeRepair.package.packageSha256 -and
            (@(Content-Map $afterRepair.package.directoryPath)|ConvertTo-Json -Depth 4 -Compress) -eq $repairContent -and
            (@(Save-Map)|ConvertTo-Json -Depth 4 -Compress) -eq $repairSaves)
        $report.repair='passed'
        $beforeUninstall=@(Save-Map)|ConvertTo-Json -Depth 4 -Compress
        Click 'InstalledUninstall-cn.labchronicles.storeprobe';Click-Named '卸载并保留数据'
        $null=Wait-SmokeCondition {-not (Find 'InstalledOpen-cn.labchronicles.storeprobe')} 'registration removed on uninstall' 6
        Add-SmokeCheck 'uninstall_preserves_save_bytes' ((@(Save-Map)|ConvertTo-Json -Depth 4 -Compress) -eq $beforeUninstall)
        Click 'StoreHome';Wait-SmokeDesktop $product
        $null=Wait-SmokeCondition {-not (Find $icon)} 'desktop entry removed after unregister event' 6
        Add-SmokeCheck 'uninstall_removes_desktop_entry' (-not (Find $icon))
        Capture '14-uninstalled-desktop-save-retained'
        $report.uninstall_preserves_saves='passed';$report.controlled_source.status='passed'
    }
    Add-SmokeCheck 'different_working_directory_has_no_data_root' (-not (Test-Path -LiteralPath (Join-Path $cwd 'AutumnOS_Data')))
    Close-SmokeProduct $product
    $report.status='passed'
}catch{
    $report.failure_type=$_.Exception.GetType().FullName;$report.failure=$_.Exception.Message
    Write-Warning "T04 Store smoke failed: $($report.failure)"
    try{$report.failure_owned_buttons=@(Failure-Buttons)}catch{$report.failure_button_dump='unavailable'}
    if($product -and -not $product.Process.HasExited){$null=Save-SmokeScreenshot $product '99-failure' -IncludeOverlays}
}finally{
    $clean=$true
    foreach($process in $ownedProcesses){
        try{
            if(-not $process.HasExited){
                $process.Refresh()
                if('AutumnDesktopInteractionSmoke.NativeWindows' -as [type]){$null=[AutumnDesktopInteractionSmoke.NativeWindows]::SetWindowPos($process.MainWindowHandle,[IntPtr](-2),0,0,0,0,3)}
                $null=$process.CloseMainWindow()
                if(-not $process.WaitForExit(10000)){$clean=$false;$checks.Add([ordered]@{name='cleanup_owned_process';status='failed';process_id=$process.Id;detail='Normal close timed out; process was not killed.'})}
            }
        }catch{$clean=$false;$checks.Add([ordered]@{name='cleanup_owned_process';status='failed';process_id=$process.Id;detail=$_.Exception.Message})}
        finally{$process.Dispose()}
    }
    foreach($entry in $ownedEntryProcesses){
        try{if(-not $entry.WaitForExit(10000)){$clean=$false;$checks.Add([ordered]@{name='cleanup_owned_bootstrap';status='failed';process_id=$entry.Id;detail='Owned bootstrap did not exit; left running without termination.'})}}
        catch{$clean=$false;$checks.Add([ordered]@{name='cleanup_owned_bootstrap';status='failed';detail=$_.Exception.Message})}
        finally{$entry.Dispose()}
    }
    Restore-AutumnNativePointer
    if($script:initialPointerPosition -and ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){$null=[AutumnDesktopInteractionSmoke.NativeWindows]::SetCursorPos($script:initialPointerPosition.X,$script:initialPointerPosition.Y)}
    $report.cleanup=if($clean){'passed'}else{'failed'};if(-not $clean){$report.status='failed'}
    $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o');$report.passed_checks=@($checks|Where-Object{$_.status -eq 'passed'}).Count
    $report.check_count=$checks.Count
    $report|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "REPORT $reportPath"
}
if($report.status -ne 'passed'){throw "Store smoke did not pass. Evidence retained at $reportPath"}
