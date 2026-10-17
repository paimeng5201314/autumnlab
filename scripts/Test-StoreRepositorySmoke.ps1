#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ExecutablePath,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [ValidateRange(10,90)][int]$WindowTimeoutSeconds=60
)

# Actual public GitHub UI only. No fixture, registry injection, source download or process termination.
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/Smoke-NativeAppInput.ps1"
. "$PSScriptRoot/T06-NativeHelpers.ps1"
$helperPath=Join-Path $PSScriptRoot 'Test-DesktopInteractionSmoke.ps1'
$helperNames=@('Add-SmokeCheck','Wait-SmokeCondition','Find-SmokeElement','Find-VisibleSmokeElement',
    'Invoke-SmokeButton','Test-SmokeForeground','Focus-SmokeProduct','Assert-SmokeCaptureUnoccluded',
    'Wait-SmokeDesktop','Start-SmokeProduct','Close-SmokeProduct','Save-SmokeScreenshot')
$helperAst=Import-T06Functions $helperPath -Names $helperNames
$ReportDirectory=[IO.Path]::GetFullPath($ReportDirectory)
if(Test-Path -LiteralPath $ReportDirectory){throw 'Use a new ReportDirectory; previous evidence is preserved.'}
New-Item -ItemType Directory -Path $ReportDirectory|Out-Null
$reportPath=Join-Path $ReportDirectory 'repository-smoke.json'
$smokeId='aos-repo-'+[Guid]::NewGuid().ToString('N').Substring(0,12)
# A short, unique path avoids the known Framework extraction/path-depth limitation.
$testScope=Join-Path ([IO.Path]::GetPathRoot($projectRoot)) $smokeId
$stageDirectory=Join-Path $testScope 'app'
$dataDirectory=Join-Path $stageDirectory 'AutumnOS_Data'
$checks=[Collections.Generic.List[object]]::new()
$screenshots=[Collections.Generic.List[object]]::new()
$inputActions=[Collections.Generic.List[object]]::new()
$ownedProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$ownedEntryProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$copiedFiles=[Collections.Generic.List[object]]::new()
$excludedDirectories=[Collections.Generic.List[string]]::new()
$script:initialPointerPosition=$null
$product=$null
$report=[ordered]@{
    schema_version=1;task_id='T04';checkpoint='public_repository_reference_native_ui';status='failed'
    started_utc=[DateTimeOffset]::UtcNow.ToString('o');environment=[Environment]::OSVersion.VersionString
    source_executable=$null;source_executable_sha256=$null;tested_executable=$null;tested_executable_sha256=$null
    build_id='not_recorded';source_snapshot_id='not_recorded';release_label=$null
    test_scope=$testScope;test_data_retained=$true;source_data_access='excluded_from_copy'
    driver_sha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    imported_helpers_sha256=@{};checks=$checks;screenshots=$screenshots;input_actions=$inputActions
    process_ids=@();bootstrap_process_ids=@();remaining_processes=@();cleanup='not_run'
    public_github=[ordered]@{repository='paimeng5201314/chemleap';url='https://github.com/paimeng5201314/chemleap';status='not_run';repository_id=$null;warnings=@();observed_ui=$null}
    public_download='not_run_missing_release';public_install='not_run_missing_release';game_launch='not_run_missing_release'
    scope='Prepared native Windows release directory, real public repository lookup, cancel, invalid URL, honest missing publication requirements, refresh; no install or game claim.'
    full_t04_acceptance='not_run';full_t06='in_progress';physical_input='not_run';visual_review='not_run'
}
foreach($path in @($helperPath,(Join-Path $PSScriptRoot 'Smoke-NativeAppInput.ps1'),(Join-Path $PSScriptRoot 'T06-NativeHelpers.ps1'))){
    $report.imported_helpers_sha256[[IO.Path]::GetFileName($path)]=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Assert-PlainPath([string]$Path){
    $current=[IO.Path]::GetFullPath($Path)
    while($current){
        if(Test-Path -LiteralPath $current){
            if(((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw "Refusing redirected test/build path: $current"}
        }
        $parent=Split-Path $current -Parent
        if(-not $parent -or $parent -eq $current){break};$current=$parent
    }
}
function Copy-PlainTree([string]$Source,[string]$Destination){
    foreach($entry in Get-ChildItem -LiteralPath $Source -Force){
        if($entry.PSIsContainer -and $entry.Name -in @('AutumnOS_Data','Cache','Caches','.cache','Temp','.git')){
            $excludedDirectories.Add($entry.FullName);continue
        }
        if(($entry.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'Refusing redirected files in prepared release.'}
        $target=Join-Path $Destination $entry.Name
        if($entry.PSIsContainer){New-Item -ItemType Directory -Path $target|Out-Null;Copy-PlainTree $entry.FullName $target}
        else{
            Copy-Item -LiteralPath $entry.FullName -Destination $target
            $sourceHash=(Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $targetHash=(Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash.ToLowerInvariant()
            if($sourceHash -ne $targetHash){throw 'Prepared release copy byte mismatch.'}
            $copiedFiles.Add([ordered]@{path=[IO.Path]::GetRelativePath($stageDirectory,$target);sha256=$targetHash;bytes=$entry.Length})
        }
    }
}
function Find([string]$Id){Find-SmokeElement $product.Element $Id}
function Visible([string]$Id){Find-VisibleSmokeElement $product.Element $Id}
function Show-Element([string]$Id){
    $element=Wait-SmokeCondition {Find $Id} "existing element $Id" 8
    for($attempt=0;$attempt -lt 25;$attempt++){
        if(-not $element.Current.IsOffscreen -and -not $element.Current.BoundingRectangle.IsEmpty){return $element}
        $pattern=$null
        if($element.TryGetCurrentPattern([Windows.Automation.ScrollItemPattern]::Pattern,[ref]$pattern)){$pattern.ScrollIntoView()}
        else{
            $ancestor=[Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($element);$scroll=$null
            for($depth=0;$depth -lt 14 -and $ancestor;$depth++){
                if($ancestor.TryGetCurrentPattern([Windows.Automation.ScrollPattern]::Pattern,[ref]$scroll)){
                    $direction=if($element.Current.BoundingRectangle.Top -lt $ancestor.Current.BoundingRectangle.Top){[Windows.Automation.ScrollAmount]::LargeDecrement}else{[Windows.Automation.ScrollAmount]::LargeIncrement}
                    $scroll.Scroll([Windows.Automation.ScrollAmount]::NoAmount,$direction);break
                }
                $ancestor=[Windows.Automation.TreeWalker]::ControlViewWalker.GetParent($ancestor)
            }
            if(-not $scroll){throw "Cannot scroll owned target into view: $Id"}
        }
        Start-Sleep -Milliseconds 90;$element=Find $Id
    }
    throw "Target remained outside the owned viewport: $Id"
}
function Click([string]$Id){
    $element=Show-Element $Id
    $null=Wait-SmokeCondition {$element.Current.IsEnabled} "enabled button $Id" 8
    if($element.Current.ProcessId -ne $product.Process.Id){throw 'Refusing UI action outside owned product.'}
    $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Set-RepositoryReference([string]$Text){
    $element=Show-Element 'StoreRepositoryInput'
    if($element.Current.ProcessId -ne $product.Process.Id){throw 'Repository dialog is not owned by this test.'}
    $element.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($Text)
}
function Click-Dialog([string]$Name,[ValidateSet('PrimaryButton','CloseButton')][string]$Id='PrimaryButton'){
    $filter=[Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::AutomationIdProperty,$Id),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::IsOffscreenProperty,$false),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::IsEnabledProperty,$true))
    $button=Wait-SmokeCondition {
        $scopes=[Collections.Generic.List[object]]::new();$scopes.Add($product.Element)
        $owned=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$product.Process.Id)
        foreach($native in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$owned)){$scopes.Add($native)}
        foreach($scope in $scopes){$candidate=$scope.FindFirst([Windows.Automation.TreeScope]::Descendants,$filter);if($candidate -and $candidate.Current.ProcessId -eq $product.Process.Id){return $candidate}}
    } "owned ContentDialog $Id $Name" 8
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null=Wait-SmokeCondition {-not (Visible 'StoreRepositoryInput')} 'repository ContentDialog dismissed' 8
    # ShowAsync completes after the closing animation; let its owning event handler finish.
    Start-Sleep -Milliseconds 350
}
function Capture([string]$Name,[string]$ReadyId){
    $null=Show-Element $ReadyId;Start-Sleep -Milliseconds 250
    Add-SmokeCheck ('screenshot_'+$Name) ([bool](Save-SmokeScreenshot $product $Name -IncludeOverlays)) 'Actual owned native window pixels.'
}
function Get-RepositoryIdentity{
    $filter=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Text)
    $texts=@($product.Element.FindAll([Windows.Automation.TreeScope]::Descendants,$filter)|ForEach-Object {$_.Current.Name}) -join "`n"
    if($texts -notmatch '来源：paimeng5201314/chemleap'){throw 'Repository details show an unexpected source.'}
    $identity=[regex]::Match($texts,'repositoryId：(\d+)')
    if(-not $identity.Success){throw 'Native repository details do not expose their real repository ID.'}
    return $identity.Groups[1].Value
}
function Assert-RealRepository([string]$Label){
    $null=Wait-SmokeCondition {
        $heading=Find 'StoreHeading';$status=Find 'StoreStatus'
        if($status -and $status.Current.Name -match '无法连接 GitHub|请求暂时受限|服务暂时不可用|未找到此公开仓库'){
            $report.public_github.observed_ui=$status.Current.Name;throw "Real public repository lookup failed: $($status.Current.Name)"
        }
        return $heading -and $heading.Current.Name -eq 'chemleap' -and (Find 'StoreRefreshDetails') -and (Find 'StoreWarning-STORE_NO_RELEASES')
    } 'actual public chemleap repository details and missing release warning' 60
    Add-SmokeCheck ($Label+'_real_repository_heading') ((Find 'StoreHeading').Current.Name -eq 'chemleap')
    foreach($code in @('STORE_TOPIC_MISSING','STORE_METADATA_MISSING','STORE_NO_RELEASES')){
        $warning=Find ('StoreWarning-'+$code)
        Add-SmokeCheck ($Label+'_'+$code) ($null -ne $warning -and $warning.Current.Name -match '[\p{IsCJKUnifiedIdeographs}]')
    }
    $filter=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::Button)
    $enabled=@($product.Element.FindAll([Windows.Automation.TreeScope]::Descendants,$filter)|Where-Object {$_.Current.AutomationId -like 'StoreInstall-*' -and $_.Current.IsEnabled})
    Add-SmokeCheck ($Label+'_no_enabled_install_without_release') ($enabled.Count -eq 0)
    $report.public_github.observed_ui=(Find 'StoreStatus').Current.Name
    return Get-RepositoryIdentity
}
try{
    if(-not $IsWindows){throw 'Requires an interactive Windows desktop and prepared native release.'}
    $existing=@(Get-Process -Name AutumnOS,AutumnOS.Client -ErrorAction SilentlyContinue)
    if($existing.Count){$report.status='blocked';throw 'Existing user launcher detected; this script refuses to activate or close it.'}
    Add-SmokeCheck 'no_existing_user_launcher' ($existing.Count -eq 0)
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    Add-SmokeCheck 'standard_user' (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
    $ExecutablePath=[IO.Path]::GetFullPath($ExecutablePath);Assert-PlainPath $ExecutablePath;Assert-PlainPath $ReportDirectory;Assert-PlainPath $testScope
    if([IO.Path]::GetFileName($ExecutablePath) -ne 'AutumnOS.exe' -or -not (Test-Path -LiteralPath $ExecutablePath -PathType Leaf)){throw 'A prepared release AutumnOS.exe is required.'}
    $sourceDirectory=Split-Path $ExecutablePath -Parent
    foreach($resource in @('AutumnOS.Client.exe','AutumnOS.Contracts.dll','App.xbf','MainWindow.xbf')){
        if(-not (Test-Path -LiteralPath (Join-Path $sourceDirectory $resource) -PathType Leaf)){throw "Prepared full release missing $resource; single-file outer entry is not accepted."}
    }
    if(Test-Path -LiteralPath $testScope){throw 'Unique short smoke directory already exists.'}
    New-Item -ItemType Directory -Path $stageDirectory|Out-Null
    $report.source_executable=$ExecutablePath;$report.source_executable_sha256=(Get-FileHash -LiteralPath $ExecutablePath -Algorithm SHA256).Hash.ToLowerInvariant()
    Copy-PlainTree $sourceDirectory $stageDirectory
    $report.tested_executable=Join-Path $stageDirectory 'AutumnOS.exe'
    $report.tested_executable_sha256=(Get-FileHash -LiteralPath $report.tested_executable -Algorithm SHA256).Hash.ToLowerInvariant()
    Add-SmokeCheck 'isolated_entry_matches_source_bytes' ($report.tested_executable_sha256 -eq $report.source_executable_sha256)
    Add-SmokeCheck 'fresh_data_excludes_source_data_and_caches' (-not (Test-Path -LiteralPath $dataDirectory))
    $report.copied_files=$copiedFiles.Count;$report.excluded_directories=@($excludedDirectories)
    $copiedFiles|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $ReportDirectory 'copied-file-hashes.json') -Encoding utf8
    $assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $stageDirectory 'AutumnOS.Contracts.dll')))
    foreach($attribute in $assembly.GetCustomAttributesData()){
        if($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute'){
            $key=$attribute.ConstructorArguments[0].Value;$value=$attribute.ConstructorArguments[1].Value
            if($key -eq 'BuildId'){$report.build_id=$value};if($key -eq 'SourceSnapshotId'){$report.source_snapshot_id=$value}
        }
    }
    Add-SmokeCheck 'binary_records_build_and_source' ($report.build_id -ne 'not_recorded' -and $report.source_snapshot_id -ne 'not_recorded')
    $report.release_label=(Get-Item -LiteralPath $report.tested_executable).VersionInfo.ProductVersion
    $desktopRuntime=Join-Path $projectRoot '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')){Add-Type -Path (Join-Path $desktopRuntime $name)}
    $native=@($helperAst.FindAll({param($n) $n -is [Management.Automation.Language.StringConstantExpressionAst] -and $n.Value.Contains('namespace AutumnDesktopInteractionSmoke')},$true))
    if($native.Count -ne 1){throw 'Expected exactly one maintained native window declaration.'}
    if(-not ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){Add-Type -TypeDefinition $native[0].Value}
    $working=Join-Path $testScope 'cwd';New-Item -ItemType Directory -Path $working|Out-Null
    $product=Start-SmokeProduct $working
    Click 'HelloNextButton';Click 'BrandNextButton';Click 'WelcomeLightButton';Click 'WelcomeAppearanceNext';Click 'FirstRunSkip';Click 'PrepareDesktopButton'
    Wait-SmokeDesktop $product;Click 'StoreButton'
    $null=Wait-SmokeCondition {Visible 'StoreHeading'} 'native Store opens' 10
    Add-SmokeCheck 'default_store_is_public_github' ((Find 'StoreSource').Current.Name -eq 'GitHub 公开应用')
    $beforeHeading=(Find 'StoreHeading').Current.Name
    Click 'StoreOpenRepository';Set-RepositoryReference $report.public_github.url
    Capture '01-repository-dialog' 'StoreRepositoryInput'
    Click-Dialog '取消' 'CloseButton'
    Add-SmokeCheck 'cancel_preserves_discovery_navigation' ((Find 'StoreHeading').Current.Name -eq $beforeHeading -and -not (Find 'StoreBack') -and [bool](Visible 'StoreSearch'))
    $null=Wait-SmokeCondition {$refresh=Find 'StoreRefresh';$refresh -and $refresh.Current.IsEnabled} 'discovery refresh enabled after repository dialog cancel' 60
    Add-SmokeCheck 'cancel_does_not_leave_discovery_busy' ((Find 'StoreRefresh').Current.IsEnabled)
    Click 'StoreOpenRepository';Set-RepositoryReference 'https://example.com/paimeng5201314/chemleap';Click-Dialog '查看应用'
    $invalid=Wait-SmokeCondition {$status=Find 'StoreStatus';if($status -and $status.Current.Name -match '^请输入 GitHub 公开仓库链接或 owner/name'){$status.Current.Name}} 'explicit Chinese rejection of a non-GitHub URL' 10
    $report.invalid_url_observed_ui=$invalid
    Add-SmokeCheck 'invalid_url_rejected_in_chinese' ($invalid -match 'GitHub 公开仓库')
    Capture '02-invalid-repository-rejected' 'StoreStatus'
    Click 'StoreOpenRepository';Set-RepositoryReference $report.public_github.url;Click-Dialog '查看应用'
    $repositoryId=Assert-RealRepository 'lookup'
    $report.public_github.repository_id=$repositoryId
    $report.public_github.warnings=@('STORE_TOPIC_MISSING','STORE_METADATA_MISSING','STORE_NO_RELEASES')
    Capture '03-real-chemleap-missing-release' 'StoreWarning-STORE_NO_RELEASES'
    $previousRefreshIdentity=((Find 'StoreRefreshDetails').GetRuntimeId() -join ',')
    Click 'StoreRefreshDetails'
    $null=Wait-SmokeCondition {$refresh=Find 'StoreRefreshDetails';$refresh -and ($refresh.GetRuntimeId() -join ',') -ne $previousRefreshIdentity} 'refresh rebuilds completed repository details' 60
    $refreshedId=Assert-RealRepository 'refresh'
    Add-SmokeCheck 'refresh_preserves_real_repository_id' ($refreshedId -eq $repositoryId)
    Capture '04-refreshed-public-repository' 'StoreWarning-STORE_NO_RELEASES'
    $report.public_github.status='passed_real_public_repository_without_installable_release'
    Add-SmokeCheck 'working_directory_receives_no_data' (-not (Test-Path -LiteralPath (Join-Path $working 'AutumnOS_Data')))
    Add-SmokeCheck 'tested_entry_bytes_unchanged' ((Get-FileHash -LiteralPath $report.tested_executable -Algorithm SHA256).Hash.ToLowerInvariant() -eq $report.source_executable_sha256)
    Close-SmokeProduct $product
    $report.status='passed'
}catch{
    $report.failure_type=$_.Exception.GetType().FullName;$report.failure=$_.Exception.Message
    if($report.public_github.status -eq 'not_run'){$report.public_github.status='failed_or_not_reached'}
    Write-Warning $report.failure
    if($product -and -not $product.Process.HasExited){try{$null=Save-SmokeScreenshot $product '99-failure' -IncludeOverlays}catch{Write-Warning $_.Exception.Message}}
}finally{
    # Only held self-started process objects are addressed; no process discovery/termination in cleanup.
    foreach($process in $ownedProcesses){
        try{
            if(-not $process.HasExited){$null=$process.CloseMainWindow();$null=$process.WaitForExit(10000)}
            if(-not $process.HasExited){$report.status='failed';$checks.Add([ordered]@{name='normal_owned_exit';status='failed';process_id=$process.Id;detail='Normal close timed out; process left running.'})}
        }catch{$report.status='failed';$checks.Add([ordered]@{name='normal_owned_exit';status='failed';detail=$_.Exception.Message})}
    }
    foreach($entry in $ownedEntryProcesses){try{$null=$entry.WaitForExit(10000)}catch{$report.status='failed'}}
    $report.remaining_processes=@(@($ownedProcesses)+@($ownedEntryProcesses)|Where-Object {-not $_.HasExited}|ForEach-Object {$_.Id})
    $report.cleanup=if($report.remaining_processes.Count -eq 0){'passed'}else{'failed'}
    if($report.cleanup -ne 'passed'){$report.status='failed'}
    Restore-AutumnNativePointer
    if($script:initialPointerPosition -and ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){$null=[AutumnDesktopInteractionSmoke.NativeWindows]::SetCursorPos($script:initialPointerPosition.X,$script:initialPointerPosition.Y)}
    $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o');$report.check_count=$checks.Count
    $report.passed_checks=@($checks|Where-Object status -eq 'passed').Count
    $report|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $reportPath -Encoding utf8
    foreach($process in @($ownedProcesses)+@($ownedEntryProcesses)){$process.Dispose()}
    Write-Host "REPORT $reportPath"
}
if($report.status -ne 'passed'){throw "Public repository native smoke did not pass. Evidence retained at $reportPath"}
