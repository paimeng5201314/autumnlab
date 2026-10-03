#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AExecutablePath,
    [Parameter(Mandatory)][string]$BPayloadDirectory,
    [Parameter(Mandatory)][string]$FeedDirectory,
    [Parameter(Mandatory)][string]$ReportDirectory,
    [string]$BadPayloadDirectory,
    [string]$InstalledTestRoot,
    [ValidateRange(15,90)][int]$WindowTimeoutSeconds = 45,
    [ValidateRange(35,120)][int]$BlockedObservationSeconds = 35,
    [ValidateRange(120,600)][int]$UpdateTimeoutSeconds = 360
)

# This driver uses signed, explicitly isolated test-feed assets. Product data is created only by
# native UI/game SDK actions. It never writes a save/config/receipt/journal or kills a process.
$ErrorActionPreference='Stop'
$projectRoot=Split-Path $PSScriptRoot -Parent
. "$PSScriptRoot/Smoke-NativeAppInput.ps1"
$AExecutablePath=[IO.Path]::GetFullPath($AExecutablePath)
$BPayloadDirectory=[IO.Path]::GetFullPath($BPayloadDirectory)
$FeedDirectory=[IO.Path]::GetFullPath($FeedDirectory)
$ReportDirectory=[IO.Path]::GetFullPath($ReportDirectory)
if(Test-Path -LiteralPath $ReportDirectory){throw 'ReportDirectory already exists; preserve historical evidence and use a new directory.'}
$smokeId='T05-update-smoke-'+[Guid]::NewGuid().ToString('N')
$testScope=Join-Path $projectRoot "artifacts/smoke/$smokeId"
New-Item -ItemType Directory -Path $ReportDirectory,$testScope | Out-Null
$reportPath=Join-Path $ReportDirectory 'update-smoke.json'
$checks=[Collections.Generic.List[object]]::new()
$screenshots=[Collections.Generic.List[object]]::new()
$inputActions=[Collections.Generic.List[object]]::new()
$ownedProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$ownedEntryProcesses=[Collections.Generic.List[Diagnostics.Process]]::new()
$trackedProcesses=[Collections.Generic.List[object]]::new()
$repeatLaunches=[Collections.Generic.List[object]]::new()
$scenarioResults=[Collections.Generic.List[object]]::new()
$windowGeometry=[Collections.Generic.List[object]]::new()
$script:initialPointerPosition=$null
$script:product=$null
$script:stageDirectory=$null
$script:scenario=$null
$report=[ordered]@{
    schema_version=1;task_id='T05';checkpoint='real_delivered_client_signed_local_feed_upgrade_and_optional_rollback'
    status='failed';smoke_id=$smokeId;started_utc=[DateTimeOffset]::UtcNow.ToString('o')
    a_executable=$AExecutablePath;test_scope=$testScope;report_directory=$ReportDirectory
    driver_sha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    window_geometry=$windowGeometry;input_guard_failure='not_observed'
    installed_test_root=$null;installation_mode='isolated_copy_of_delivered_A'
    feed_directory=$FeedDirectory;feed_transport='compile-time isolated FileStream/ByteArrayContent; no HTTP server or real GitHub release'
    production_github='not_run';real_identity='not_run';physical_power_loss='not_run';windows10='not_run'
    maintenance_repeat_launch='not_run';starting_suspended_closing_races='covered_by_separate_coordinator_tests_not_this_UI_drill'
    synthetic_release_list=$true;test_signatures='supplied_by_caller_not_generated_by_this_driver'
    process_ids=@();bootstrap_process_ids=@();checks=$checks;screenshots=$screenshots;input_actions=$inputActions;scenarios=$scenarioResults
    tested_executable=$null;build_id=$null;source_snapshot_id='not_recorded';cleanup='not_run';remaining_processes=@()
    user_data_writes='native UI and real game SDK only';private_keys_read=$false;public_release=$false
}

# Import maintained function definitions only, never the old driver's executable body or cleanup.
$helperPath=Join-Path $PSScriptRoot 'Test-DesktopInteractionSmoke.ps1'
$tokens=$null;$parseErrors=$null
$helperAst=[Management.Automation.Language.Parser]::ParseFile($helperPath,[ref]$tokens,[ref]$parseErrors)
if($parseErrors.Count){throw 'Existing desktop input helper does not parse.'}
foreach($definition in $helperAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$false)){
    . ([ScriptBlock]::Create($definition.Extent.Text))
}
$script:t05LegacyFocus=(Get-Item Function:Focus-SmokeProduct).ScriptBlock
$report.desktop_helper_sha256=(Get-FileHash -LiteralPath $helperPath -Algorithm SHA256).Hash.ToLowerInvariant()

function Assert-T05OwnedWindow($Product){
    $Product.Process.Refresh()
    $identity=@($trackedProcesses|Where-Object {[object]::ReferenceEquals($_.Process,$Product.Process)})
    [uint32]$owner=0
    $null=[AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowThreadProcessId($Product.Handle,[ref]$owner)
    if($identity.Count -ne 1 -or $Product.Process.HasExited -or $owner -ne $Product.Process.Id -or
        $Product.Process.StartTime.ToUniversalTime().Ticks -ne $identity[0].StartTicks -or
        -not [string]::Equals($Product.Process.MainModule.FileName,$identity[0].Path,[StringComparison]::OrdinalIgnoreCase) -or
        $identity[0].Root -ne $stageDirectory -or $Product.Element.Current.ProcessId -ne $Product.Process.Id){
        throw 'Refusing window restoration: the held process, original HWND, start time, path or UIA owner changed.'
    }
}
function Get-T05WindowGeometry($Product,[string]$Reason){
    Assert-T05OwnedWindow $Product
    $outer=[AutumnDesktopInteractionSmoke.NativeWindows+RECT]::new()
    $client=[AutumnDesktopInteractionSmoke.NativeWindows+RECT]::new()
    $cursor=[AutumnDesktopInteractionSmoke.NativeWindows+POINT]::new()
    $hasOuter=[AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowRect($Product.Handle,[ref]$outer)
    $hasClient=[AutumnDesktopInteractionSmoke.NativeWindows]::GetClientRect($Product.Handle,[ref]$client)
    $hasCursor=[AutumnDesktopInteractionSmoke.NativeWindows]::GetCursorPos([ref]$cursor)
    $foreground=[AutumnDesktopInteractionSmoke.NativeWindows]::GetForegroundWindow();[uint32]$foregroundOwner=0
    $null=[AutumnDesktopInteractionSmoke.NativeWindows]::GetWindowThreadProcessId($foreground,[ref]$foregroundOwner)
    $uiaBounds=$Product.Element.Current.BoundingRectangle
    $state=[ordered]@{timestamp_utc=[DateTimeOffset]::UtcNow.ToString('o');reason=$Reason;pid=$Product.Process.Id;hwnd=$Product.Handle.ToInt64();
        main_window_handle=$Product.Process.MainWindowHandle.ToInt64();iconic=[AutumnT05Smoke.FocusNative]::IsIconic($Product.Handle);
        visible=[AutumnDesktopInteractionSmoke.NativeWindows]::IsWindowVisible($Product.Handle);
        outer_valid=$hasOuter;outer=@($outer.Left,$outer.Top,$outer.Right,$outer.Bottom);
        client_valid=$hasClient;client=@($client.Left,$client.Top,$client.Right,$client.Bottom);
        uia_bounds=@($uiaBounds.Left,$uiaBounds.Top,$uiaBounds.Width,$uiaBounds.Height);
        foreground_hwnd=$foreground.ToInt64();foreground_pid=$foregroundOwner;cursor_valid=$hasCursor;cursor=@($cursor.X,$cursor.Y)}
    if($windowGeometry.Count -lt 250){$windowGeometry.Add($state)}
    return $state
}
function Focus-SmokeProduct($Product){
    # T05-only wrapper: retain the maintained foreground and pointer guards. Restore the single
    # held native window once when it is actually iconic; never search for or operate on other apps.
    try{
        $before=Get-T05WindowGeometry $Product 'before_focus'
        if($before.iconic){
            Assert-T05OwnedWindow $Product
            $null=[AutumnT05Smoke.FocusNative]::ShowWindow($Product.Handle,9) # SW_RESTORE, one attempt.
            $inputActions.Add([ordered]@{kind='restore_owned_minimized_window';process_id=$Product.Process.Id;hwnd=$Product.Handle.ToInt64();attempts=1})
            $null=Wait-SmokeCondition {-not [AutumnT05Smoke.FocusNative]::IsIconic($Product.Handle)} 'owned minimized window to restore' 3
            $null=Get-T05WindowGeometry $Product 'after_single_restore'
        }
        & $script:t05LegacyFocus $Product
        $ready=Wait-SmokeCondition {
            Assert-T05OwnedWindow $Product
            $client=[AutumnDesktopInteractionSmoke.NativeWindows+RECT]::new()
            return -not [AutumnT05Smoke.FocusNative]::IsIconic($Product.Handle) -and
                [AutumnDesktopInteractionSmoke.NativeWindows]::IsWindowVisible($Product.Handle) -and
                [AutumnDesktopInteractionSmoke.NativeWindows]::GetClientRect($Product.Handle,[ref]$client) -and
                $client.Right -gt $client.Left -and $client.Bottom -gt $client.Top
        } 'owned foreground window with nonzero client bounds' 3
        $null=Get-T05WindowGeometry $Product 'after_focus'
    }catch{
        try{$null=Get-T05WindowGeometry $Product 'focus_failed'}catch{}
        throw
    }
}

function Assert-T05PlainPath([string]$Path){
    for($current=[IO.Path]::GetFullPath($Path);$current;$current=[IO.Path]::GetDirectoryName($current)){
        if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'The isolated drill refuses reparse-point paths.'}
    }
}
function Invoke-T05SharingRead([scriptblock]$Read){
    $budget=[Diagnostics.Stopwatch]::StartNew()
    while($true){
        try{return (& $Read)}catch{
            $errorObject=$_.Exception;$sharingViolation=$false
            for($depth=0;$errorObject -and $depth -lt 8;$depth++){
                if($errorObject -is [IO.IOException] -and (($errorObject.HResult -band 0xffff) -in @(32,33))){$sharingViolation=$true;break}
                $errorObject=$errorObject.InnerException
            }
            if(-not $sharingViolation -or $budget.Elapsed.TotalMilliseconds -ge 3000){throw}
            Start-Sleep -Milliseconds ([Math]::Min(75,[Math]::Max(1,[int](3000-$budget.Elapsed.TotalMilliseconds))))
        }
    }
}
function Read-T05Json([string]$Path){
    Assert-T05PlainPath $Path
    $json=Invoke-T05SharingRead {
        if((Get-Item -LiteralPath $Path).Length -gt 16MB){throw 'Bounded drill JSON limit exceeded.'}
        Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
    }
    # Invalid JSON is not a transient sharing violation and is deliberately never retried.
    $json | ConvertFrom-Json
}
function Assert-T05InstalledTestRoot {
    if(-not $InstalledTestRoot){return}
    if($BadPayloadDirectory){throw 'InstalledTestRoot only permits the positive A-to-B scenario; omit BadPayloadDirectory.'}
    $root=[IO.Path]::GetFullPath($InstalledTestRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $parent=[IO.Path]::GetFullPath((Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs'))
    if(-not [string]::Equals([IO.Path]::GetDirectoryName($root),$parent,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($root) -cnotmatch '^AutumnOS-Test-[A-Za-z0-9-]{1,60}$'){
        throw 'InstalledTestRoot must be a direct current-user Programs/AutumnOS-Test-<1..60 ASCII letters/digits/hyphens> directory.'
    }
    if(-not [string]::Equals($AExecutablePath,(Join-Path $root 'AutumnOS.exe'),[StringComparison]::OrdinalIgnoreCase)){
        throw 'AExecutablePath must be exactly the stable entry inside InstalledTestRoot.'
    }
    Assert-T05PlainPath $root
    if(-not(Test-Path -LiteralPath $root -PathType Container)){throw 'InstalledTestRoot must already have been created by the real test installer.'}
    foreach($item in Get-ChildItem -LiteralPath $root -Recurse -Force){
        if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'InstalledTestRoot contains a reparse point.'}
    }
    if(Test-Path -LiteralPath (Join-Path $root 'AutumnOS_Data')){throw 'InstalledTestRoot must be freshly installed with no AutumnOS_Data; existing user data is never reset by this driver.'}
    if(Test-Path -LiteralPath (Join-Path $root '.autumnos-update')){throw 'InstalledTestRoot already has update state; use a new clean installer test name.'}
    $receipt=Read-T05Json (Join-Path $root '.autumnos-install.json')
    if($receipt.Schema -ne 1 -or $receipt.Product -ne 'LabChronicles.AutumnOS' -or
        -not [string]::Equals([string]$receipt.Root,$root,[StringComparison]::OrdinalIgnoreCase)){
        throw 'InstalledTestRoot installer receipt does not match this exact product/root.'
    }
    $script:InstalledTestRoot=$root
    $report.installed_test_root=$root;$report.installation_mode='actual_fresh_per_user_test_installation_updated_in_place'
}
function Get-T05Hash([string]$Path){Invoke-T05SharingRead {(Get-FileHash -LiteralPath $Path -Algorithm SHA256 -ErrorAction Stop).Hash.ToLowerInvariant()}}
function Initialize-T05FocusNative {
    if('AutumnT05Smoke.FocusNative' -as [type]){return}
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace AutumnT05Smoke {
    public static class FocusNative {
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool ShowWindow(IntPtr window, int command);
    }
}
'@
}
function Initialize-T05DialogNative {
    if('AutumnT05Smoke.DialogNative' -as [type]){return}
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
namespace AutumnT05Smoke {
    public sealed class DialogControl {
        public long Handle { get; set; }
        public uint ProcessId { get; set; }
        public int Id { get; set; }
        public string Class { get; set; } = "";
        public string Text { get; set; } = "";
    }
    public static class DialogNative {
        private delegate bool EnumCallback(IntPtr handle, IntPtr data);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr handle, out uint processId);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr handle, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern int GetClassNameW(IntPtr handle, StringBuilder text, int max);
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] private static extern IntPtr SendMessageTimeoutW(IntPtr handle, uint message, UIntPtr size, StringBuilder text, uint flags, uint timeout, out UIntPtr result);
        [DllImport("user32.dll")] private static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr data);
        [DllImport("user32.dll")] private static extern int GetDlgCtrlID(IntPtr handle);
        [DllImport("user32.dll")] private static extern IntPtr GetDlgItem(IntPtr parent, int id);
        [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr handle, uint message, IntPtr wParam, IntPtr lParam);
        private static DialogControl Read(IntPtr handle, uint expected) {
            GetWindowThreadProcessId(handle, out uint actual);
            if(actual != expected) throw new InvalidOperationException("Owned dialog PID mismatch.");
            var text = new StringBuilder(2049); var type = new StringBuilder(257);
            GetWindowTextW(handle, text, text.Capacity); GetClassNameW(handle, type, type.Capacity);
            // Cross-process Static text is not guaranteed to be returned by GetWindowText.
            // Ask only this verified native control, with an abort-if-hung 200 ms bound.
            if(text.Length == 0 && (type.ToString() == "Static" || type.ToString() == "Button"))
                SendMessageTimeoutW(handle, 0x000D, (UIntPtr)text.Capacity, text, 0x0002, 200, out _);
            return new DialogControl { Handle=handle.ToInt64(), ProcessId=actual, Id=GetDlgCtrlID(handle), Class=type.ToString(), Text=text.ToString() };
        }
        public static DialogControl[] Snapshot(IntPtr parent, uint expected) {
            var result = new List<DialogControl> { Read(parent, expected) };
            EnumChildWindows(parent, (handle, _) => {
                if(result.Count >= 128) return false;
                GetWindowThreadProcessId(handle, out uint actual);
                if(actual == expected) result.Add(Read(handle, expected));
                return true;
            }, IntPtr.Zero);
            return result.ToArray();
        }
        public static int AcceptOwnedMessageBox(IntPtr parent, uint expected, string kind) {
            if(kind != "busy" && kind != "recovery") return 0;
            var controls=Snapshot(parent, expected);
            if(controls.Length >= 128 || controls[0].Class != "#32770") return 0;
            DialogControl button=null; int buttons=0; bool messageMatches=false;
            foreach(var control in controls) {
                if(control.Class == "Button") { button=control; buttons++; }
                if(control.Class != "Static") continue;
                if(kind == "busy" && control.Text.Contains("AutumnOS 正在安全更新或恢复")) messageMatches=true;
                if(kind == "recovery" && System.Text.RegularExpressions.Regex.IsMatch(control.Text,
                    @"\b(UPDATE_RECOVERY_REQUIRED|UPDATE_RECOVERY_START_FAILED|UPDATE_RECOVERY_WAIT_FOR_CLIENT_EXIT)\b")) messageMatches=true;
            }
            if(!messageMatches || buttons != 1 || button == null || (button.Id != 1 && button.Id != 2)) return 0;
            string label=button.Text.Replace("&", "").Trim();
            if(!System.Text.RegularExpressions.Regex.IsMatch(label, @"^(确定|確定|OK)(\([A-Za-z]\))?$",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return 0;
            IntPtr handle=new IntPtr(button.Handle);
            if(GetDlgItem(parent, button.Id) != handle || Read(handle, expected).Class != "Button") return 0;
            // Windows can expose the single acknowledgement as IDOK=1 or IDCANCEL=2.
            // Its exact owned HWND, lone action and localized confirmation label are all checked.
            return PostMessageW(handle, 0x00F5, IntPtr.Zero, IntPtr.Zero) ? button.Id : 0;
        }
    }
}
'@
}
function Read-T05BuildMetadata([string]$Root){
    # Load bytes, never LoadFrom the installation: a test-held mapped DLL must not itself block replacement.
    $assembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $Root 'AutumnOS.Contracts.dll')))
    $metadata=[ordered]@{}
    foreach($attribute in $assembly.GetCustomAttributesData()){
        if($attribute.AttributeType.FullName -eq 'System.Reflection.AssemblyMetadataAttribute'){
            $metadata[[string]$attribute.ConstructorArguments[0].Value]=$attribute.ConstructorArguments[1].Value
        }
    }
    return $metadata
}
function Write-T05FeedList($Value,[string]$EvidenceName){
    $path=Join-Path $FeedDirectory 'releases.json';Assert-T05PlainPath $path
    $temporary=Join-Path $FeedDirectory ('releases-'+[Guid]::NewGuid().ToString('N')+'.tmp')
    [IO.File]::WriteAllText($temporary,(ConvertTo-Json -InputObject $Value -Depth 10),[Text.UTF8Encoding]::new($false))
    [IO.File]::Move($temporary,$path,$true)
    Copy-Item -LiteralPath $path -Destination (Join-Path $ReportDirectory $EvidenceName)
}
function Publish-T05SignedFixture([string]$PayloadDirectory){
    Assert-T05PlainPath $PayloadDirectory
    $manifestPath=Join-Path $PayloadDirectory 'autumn.update.json'
    $manifest=Read-T05Json $manifestPath
    if($manifest.repository -ne 'paimeng5201314/autumnlab' -or $manifest.channel -ne 'plus' -or $manifest.version -notmatch '^\d+\.\d+\.\d+(\+[0-9A-Za-z.-]+)?$' -or
        $manifest.payload.asset -notmatch '^[A-Za-z0-9][A-Za-z0-9._+-]{0,199}$' -or $manifest.payload.asset -in @('autumn.update.json','autumn.update.sig')){throw 'A default-plus fixture with a valid signed manifest is required.'}
    $tag=$manifest.channel+'-v'+$manifest.version
    $tagDirectory=Join-Path $FeedDirectory $tag;Assert-T05PlainPath $tagDirectory
    New-Item -ItemType Directory -Path $tagDirectory -Force | Out-Null
    $assets=[Collections.Generic.List[object]]::new();$assetIndex=0L
    foreach($name in @('autumn.update.json','autumn.update.sig',[string]$manifest.payload.asset)){
        $source=Join-Path $PayloadDirectory $name;$target=Join-Path $tagDirectory $name
        Assert-T05PlainPath $source;Assert-T05PlainPath $target
        if(-not(Test-Path -LiteralPath $source -PathType Leaf)){throw "Missing supplied signed fixture asset: $name"}
        if(Test-Path -LiteralPath $target){if((Get-T05Hash $source) -ne (Get-T05Hash $target)){throw 'Existing feed asset differs; refusing to overwrite it.'}}
        elseif(-not [string]::Equals($source,$target,[StringComparison]::OrdinalIgnoreCase)){Copy-Item -LiteralPath $source -Destination $target}
        $assetId=if($name -eq $manifest.payload.asset){[long]$manifest.payload.assetId}else{[long]$manifest.payload.assetId+1+$assetIndex}
        if($assetId -le 0){throw 'Fixture asset ID overflow.'};$assetIndex++
        $assets.Add([ordered]@{id=$assetId;name=$name;size=(Get-Item -LiteralPath $source).Length;browser_download_url="https://github.com/paimeng5201314/autumnlab/releases/download/$tag/$name"})
    }
    Add-SmokeCheck ($scenario.name+'_supplied_payload_bytes_match_signed_manifest') ((Get-T05Hash (Join-Path $PayloadDirectory $manifest.payload.asset)) -eq $manifest.payload.sha256 -and
        (Get-Item -LiteralPath (Join-Path $PayloadDirectory $manifest.payload.asset)).Length -eq $manifest.payload.bytes)
    $release=[ordered]@{id=[long]$manifest.payload.releaseId;tag_name=$tag;draft=$false;prerelease=$false;body='仅本地 T05 真实更新演练；合成 Release 列表，真实签名与最终程序负载。';assets=$assets.ToArray()}
    Write-T05FeedList @($release) ($scenario.name+'-releases.json')
    return $manifest
}
function Find-T05([string]$Id){Find-SmokeElement $product.Element $Id}
function Click-T05([string]$Id){Invoke-SmokeButton $product.Element $Id}
function Toggle-T05([string]$Id,[bool]$Value){
    $element=Wait-SmokeCondition {Find-T05 $Id} "toggle $Id"
    $pattern=$element.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    if(($pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::On) -ne $Value){$pattern.Toggle()}
    $null=Wait-SmokeCondition {($pattern.Current.ToggleState -eq [Windows.Automation.ToggleState]::On) -eq $Value} "toggle state $Id"
}
function Show-T05Updates {
    Click-T05 'SettingsButton';Toggle-T05 'SettingsNavUpdates' $true
    $null=Wait-SmokeCondition {Find-T05 'UpdateStatus'} 'real update settings page'
}
function Save-T05Screenshot([string]$Name){$null=Save-SmokeScreenshot $product ($scenario.name+'-'+$Name) -IncludeOverlays}

function Test-T05TamperedFeedSignature($Manifest,[string]$PayloadDirectory){
    # Tamper only the already copied public signature in the compiled local FileStream feed.
    # The caller's signed fixture, manifest, payload and asset byte counts remain untouched.
    $source=Join-Path $PayloadDirectory 'autumn.update.sig'
    $path=Join-Path $FeedDirectory ($Manifest.channel+'-v'+$Manifest.version+'/autumn.update.sig')
    Assert-T05PlainPath $source;Assert-T05PlainPath $path
    if([string]::Equals([IO.Path]::GetFullPath($source),[IO.Path]::GetFullPath($path),[StringComparison]::OrdinalIgnoreCase)){
        throw 'Signature rejection requires a feed copy separate from the supplied original signed fixture.'
    }
    $original=[IO.File]::ReadAllBytes($path);$originalHash=Get-T05Hash $source
    $encoding=[Text.UTF8Encoding]::new($false,$true);$text=$encoding.GetString($original)
    $match=[regex]::Match($text,'"signature"\s*:\s*"(?:\\u[0-9a-fA-F]{4})*(?<first>[A-Za-z0-9+/])')
    if(-not $match.Success){throw 'Fixture has no base64 signature to tamper.'}
    $offset=$match.Groups['first'].Index
    $replacement=if($text[$offset] -eq 'A'){'B'}else{'A'}
    $tampered=$encoding.GetBytes($text.Remove($offset,1).Insert($offset,$replacement))
    if($tampered.Length -ne $original.Length){throw 'Signature tamper must preserve the declared asset length.'}
    function Write-T05SignatureBytes([byte[]]$Bytes){
        $temporary=$path+'.drill-'+[Guid]::NewGuid().ToString('N')
        [IO.File]::WriteAllBytes($temporary,$Bytes)
        $null=Wait-SmokeCondition {
            try{[IO.File]::Move($temporary,$path,$true);return $true}catch [IO.IOException]{return $false}
        } 'atomic local-feed signature replacement' 20
    }
    $scenario.signature_rejection=[ordered]@{status='failed';transport='tampered local FileStream feed copy; not a public proxy';original_sha256=$originalHash;status_text=$null;candidate_text=$null}
    try{
        Write-T05SignatureBytes $tampered
        $scenario.signature_rejection.tampered_sha256=Get-T05Hash $path
        Copy-Item -LiteralPath $path -Destination (Join-Path $ReportDirectory ($scenario.name+'-tampered-public-signature.json'))
        Click-T05 'CheckSystemUpdate' # Invoke-SmokeButton waits for visible + enabled, including the initial automatic check.
        $null=Wait-SmokeCondition {
            $status=Find-T05 'UpdateStatus';$button=Find-T05 'CheckSystemUpdate'
            return $status -and $button -and $button.Current.IsEnabled -and $status.Current.Name.Contains('UPDATE_SIGNATURE_INVALID')
        } 'real client rejection of the modified signed asset' 90
        $scenario.signature_rejection.status_text=(Find-T05 'UpdateStatus').Current.Name
        $scenario.signature_rejection.candidate_text=(Find-T05 'UpdateCandidate').Current.Name
        Add-SmokeCheck ($scenario.name+'_actual_client_rejects_tampered_signature') (
            $scenario.signature_rejection.candidate_text -eq '没有已验证候选。' -and
            -not (Find-T05 'RestartSystemUpdate').Current.IsEnabled -and
            -not (Test-Path -LiteralPath (Join-Path $stageDirectory '.autumnos-update/journal.json')) -and
            -not $product.Process.HasExited)
        Save-T05Screenshot '00-tampered-signature-rejected'
        $scenario.signature_rejection.status='passed'
    }finally{
        Write-T05SignatureBytes $original
        $scenario.signature_rejection.restored_sha256=Get-T05Hash $path
        Add-SmokeCheck ($scenario.name+'_restored_feed_signature_and_original_fixture_unchanged') (
            (Get-T05Hash $source) -eq $originalHash -and $scenario.signature_rejection.restored_sha256 -eq $originalHash)
    }
}

function Register-T05Process([Diagnostics.Process]$Process,[string]$Kind,[int]$ParentId=0){
    $null=$Process.Handle;$started=$Process.StartTime.ToUniversalTime()
    if(@($trackedProcesses|Where-Object {$_.Id -eq $Process.Id -and $_.StartTicks -eq $started.Ticks}).Count){return}
    $trackedProcesses.Add([pscustomobject]@{Process=$Process;Id=$Process.Id;StartTicks=$started.Ticks;Path=$Process.MainModule.FileName;Kind=$Kind;ParentId=$ParentId;Root=$stageDirectory})
}
function Observe-T05OwnedDescendants {
    # Hold each discovered process handle. An exited parent remains bound to its original creation/exit
    # interval, so reuse of its numeric PID cannot authorize an unrelated later process.
    for($depth=0;$depth -lt 3;$depth++){
        $added=$false
        foreach($parent in @($trackedProcesses|Where-Object {$_.Root -eq $stageDirectory})){
            $endTicks=[long]::MaxValue
            if($parent.Process.HasExited){$endTicks=$parent.Process.ExitTime.ToUniversalTime().Ticks}
            foreach($candidate in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$($parent.Id)" -ErrorAction Stop)){
                if(-not $candidate.CreationDate -or $candidate.CreationDate.ToUniversalTime().Ticks -lt $parent.StartTicks -or $candidate.CreationDate.ToUniversalTime().Ticks -gt $endTicks){continue}
                $allowed=@('AutumnOS.exe','AutumnOS.Client.exe','AutumnOS.Updater.exe')|ForEach-Object {Join-Path $stageDirectory $_}
                if(-not @($allowed|Where-Object {[string]::Equals($_,$candidate.ExecutablePath,[StringComparison]::OrdinalIgnoreCase)}).Count){continue}
                if(@($trackedProcesses|Where-Object {$_.Id -eq [int]$candidate.ProcessId}).Count){continue}
                $child=$null
                try{
                    $child=[Diagnostics.Process]::GetProcessById([int]$candidate.ProcessId);$null=$child.Handle
                    if($child.HasExited){$child.Dispose();continue}
                    $childTime=$child.StartTime.ToUniversalTime()
                    if([Math]::Abs(($childTime-$candidate.CreationDate.ToUniversalTime()).TotalMilliseconds) -gt 1 -or
                        $childTime.Ticks -lt $parent.StartTicks -or $childTime.Ticks -gt $endTicks -or
                        -not [string]::Equals($child.MainModule.FileName,$candidate.ExecutablePath,[StringComparison]::OrdinalIgnoreCase) -or
                        $child.SessionId -ne [Diagnostics.Process]::GetCurrentProcess().SessionId){throw 'Updater descendant identity mismatch.'}
                    $kind=[IO.Path]::GetFileName($child.MainModule.FileName)
                    Register-T05Process $child $kind $parent.Id
                    if($kind -eq 'AutumnOS.Client.exe'){$ownedProcesses.Add($child);$report.process_ids+=$child.Id}
                    elseif($kind -eq 'AutumnOS.exe'){$ownedEntryProcesses.Add($child);$report.bootstrap_process_ids+=$child.Id}
                    $added=$true
                }catch [ArgumentException]{if($child){$child.Dispose()}} # A pre-health failure may already have exited.
            }
        }
        if(-not $added){break}
    }
    $primaryIds=@(Read-SmokeEvents (Join-Path $dataDirectory 'Logs/launcher-events.jsonl')|Where-Object eventName -eq 'primary_started'|Select-Object -ExpandProperty primaryPid)
    $live=@($trackedProcesses|Where-Object {
        if($_.Root -ne $stageDirectory -or $_.Kind -ne 'AutumnOS.Client.exe' -or $_.Process.HasExited){return $false}
        $_.Process.Refresh()
        # A short-lived client which only forwards to the primary is not a second business instance.
        # Count a real primary admission even before its native window becomes visible.
        return $_.Id -in $primaryIds -or $_.Process.MainWindowHandle -ne [IntPtr]::Zero
    })
    $scenario.maximum_live_business_processes=[Math]::Max([int]$scenario.maximum_live_business_processes,$live.Count)
    if($live.Count -gt 1){throw 'More than one actual business process is alive in the isolated update installation.'}
}
function Start-T05MaintenanceRepeat($Journal){
    $index=@($repeatLaunches|Where-Object {$_.Root -eq $stageDirectory}).Count+1
    $prefix=$scenario.name+'-maintenance-launch-'+$index
    $stderr=Join-Path $ReportDirectory ($prefix+'-stderr.log');$stdout=Join-Path $ReportDirectory ($prefix+'-stdout.log')
    $entry=Start-Process -FilePath $report.tested_executable -WorkingDirectory $stageDirectory -WindowStyle Hidden -PassThru -RedirectStandardError $stderr -RedirectStandardOutput $stdout
    if(-not $entry){throw 'Maintenance repeat launch did not return its own process handle.'}
    $null=$entry.Handle;$started=$entry.StartTime.ToUniversalTime().Ticks
    $ownedEntryProcesses.Add($entry);$report.bootstrap_process_ids+=$entry.Id
    Register-T05Process $entry 'AutumnOS.exe'
    $record=[ordered]@{attempt=$index;pid=$entry.Id;start_utc_ticks=$started;observed_phase_before_launch=$Journal.phase;transaction_id=$Journal.transactionId;
        old_A_exited=$true;status='started';dialog='not_observed';dialog_observations=@();dialog_acknowledgement='not_run';exit_code=$null;stderr=$stderr;stdout=$stdout;business_child_pids=@()}
    $repeatLaunches.Add([pscustomobject]@{Root=$stageDirectory;Process=$entry;StartedTicks=$started;Record=$record;Finished=$false;DialogStatus=$null;DialogObservedAt=$null;Acknowledged=$false;LastDialogText=$null;Started=[Diagnostics.Stopwatch]::StartNew()})
    $scenario.maintenance_repeated_launches+=@($record)
}
function Poll-T05MaintenanceRepeats {
    foreach($repeat in @($repeatLaunches|Where-Object {$_.Root -eq $stageDirectory -and -not $_.Finished})){
        $entry=$repeat.Process;$entry.Refresh()
        if($entry.HasExited){
            $repeat.Record.exit_code=$entry.ExitCode
            $repeat.Finished=$true
            $children=@($trackedProcesses|Where-Object {$_.ParentId -eq $entry.Id -and $_.Kind -eq 'AutumnOS.Client.exe'})
            $repeat.Record.business_child_pids=@($children|ForEach-Object Id)
            if($entry.ExitCode -eq 30){
                Add-SmokeCheck ($scenario.name+'_maintenance_relaunch_'+$repeat.Record.attempt+'_returns_busy_without_business_child') ($children.Count -eq 0)
                $repeat.Record.status='passed_busy_exit_30'
            }elseif($entry.ExitCode -eq 31 -and $repeat.DialogStatus -eq 'recovery'){
                Add-SmokeCheck ($scenario.name+'_maintenance_relaunch_'+$repeat.Record.attempt+'_defers_recovery_without_business_child') ($children.Count -eq 0)
                $repeat.Record.status='passed_recovery_deferred_exit_31'
            }elseif($entry.ExitCode -eq 0){
                # The maintenance interval may have ended while the EXE was being created/loaded.
                # This is retained evidence, not a busy-path pass. The live-primary check still applies.
                $repeat.Record.status='not_run_busy_interval_missed_forwarded_after_completion'
            }else{$repeat.Record.status='failed_unexpected_exit';throw ('Maintenance repeat launch returned an unexpected failure: '+$entry.ExitCode)}
            continue
        }
        if($entry.StartTime.ToUniversalTime().Ticks -ne $repeat.StartedTicks -or
            -not [string]::Equals($entry.MainModule.FileName,$report.tested_executable,[StringComparison]::OrdinalIgnoreCase)){throw 'Refusing interaction: repeated entry identity or path changed.'}
        if($entry.MainWindowHandle -ne [IntPtr]::Zero -and -not $repeat.Acknowledged){
            if($null -eq $repeat.DialogObservedAt){$repeat.DialogObservedAt=[Diagnostics.Stopwatch]::StartNew()}
            $handle=$entry.MainWindowHandle
            $native=@([AutumnT05Smoke.DialogNative]::Snapshot($handle,[uint32]$entry.Id))
            $uia=[Collections.Generic.List[object]]::new();$window=$null;$uiaError=$null
            try{
                $window=[Windows.Automation.AutomationElement]::FromHandle($handle)
                if($window.Current.ProcessId -ne $entry.Id){throw 'Repeated entry dialog is not owned by its held process.'}
                foreach($element in $window.FindAll([Windows.Automation.TreeScope]::Descendants,[Windows.Automation.Condition]::TrueCondition)){
                    if($uia.Count -ge 128){break}
                    if($element.Current.ProcessId -ne $entry.Id){continue}
                    $name=[string]$element.Current.Name
                    $uia.Add([ordered]@{type=$element.Current.ControlType.ProgrammaticName;class=$element.Current.ClassName;id=$element.Current.AutomationId;name=$name.Substring(0,[Math]::Min(2048,$name.Length))})
                }
            }catch{$uiaError=$_.Exception.GetType().FullName}
            $text=(@($native|ForEach-Object Text)+@($uia|ForEach-Object {$_.name})) -join "`n"
            # Record bounded owned-window evidence BEFORE classification, including the empty initial
            # accessibility tree while a MessageBox is still creating its children.
            if($repeat.LastDialogText -ne $text -and $repeat.Record.dialog_observations.Count -lt 8){
                $repeat.Record.dialog_observations+=@([ordered]@{elapsed_ms=[Math]::Round($repeat.Started.Elapsed.TotalMilliseconds);hwnd=$handle.ToInt64();native=$native;uia=$uia.ToArray();uia_error_type=$uiaError})
                $repeat.LastDialogText=$text
            }
            $repeat.Record.dialog_text=$text
            if($text.Contains('AutumnOS 正在安全更新或恢复')){$repeat.DialogStatus='busy'}
            elseif($text -match '\b(UPDATE_RECOVERY_REQUIRED|UPDATE_RECOVERY_START_FAILED|UPDATE_RECOVERY_WAIT_FOR_CLIENT_EXIT)\b'){$repeat.DialogStatus='recovery'}
            elseif($text -match '\bUPDATE_[A-Z_]+\b' -and $text.Contains('原版本备份和 AutumnOS_Data 已保留')){$repeat.DialogStatus='product_error'}
            elseif($repeat.DialogObservedAt.Elapsed.TotalSeconds -lt 3){continue}
            else{$repeat.Record.status='failed_unrecognized_dialog';throw 'Owned repeated entry dialog remained unrecognized after bounded child-control observation; its native/UIA snapshot is preserved.'}
            $repeat.Record.dialog=$repeat.DialogStatus
            if($repeat.DialogStatus -notin @('busy','recovery')){$repeat.Record.status='failed_unexpected_product_dialog';throw 'Owned bootstrap reported a product error; its dialog evidence is preserved without acknowledgement.'}
            $entry.Refresh()
            if($entry.HasExited){continue}
            if($entry.StartTime.ToUniversalTime().Ticks -ne $repeat.StartedTicks -or $entry.MainWindowHandle -ne $handle -or
                -not [string]::Equals($entry.MainModule.FileName,$report.tested_executable,[StringComparison]::OrdinalIgnoreCase)){
                throw 'Refusing dialog acknowledgement: held path, start time or original HWND changed.'
            }
            # Accept only the exact owned #32770's sole localized confirmation action. Windows
            # exposes this button as ID 1 or 2; a second action or unknown label is never accepted.
            $acceptedId=[AutumnT05Smoke.DialogNative]::AcceptOwnedMessageBox($handle,[uint32]$entry.Id,$repeat.DialogStatus)
            if($acceptedId -in @(1,2)){
                $repeat.Acknowledged=$true;$repeat.Record.dialog_acknowledgement='owned_native_single_confirmation_BM_CLICK';$repeat.Record.confirmation_control_id=$acceptedId
            }elseif(-not $entry.HasExited -and $repeat.DialogObservedAt.Elapsed.TotalSeconds -ge 3){
                $repeat.Record.status='failed_dialog_confirmation';throw 'The owned MessageBox has no unique ID 1/2 localized confirmation action; no other window is touched.'
            }
        }
        if($repeat.Started.Elapsed.TotalSeconds -gt 25){$repeat.Record.status='failed_observation_timeout';throw 'Own maintenance repeat entry did not finish within its bounded observation.'}
    }
}
function Product-T05FromTrackedBusiness($Tracked){
    $process=$Tracked.Process
    $handle=Wait-SmokeCondition {$process.Refresh();if($process.HasExited){throw 'Tracked updated client exited before its window.'};if($process.MainWindowHandle -ne [IntPtr]::Zero -and $process.Responding){$process.MainWindowHandle}} 'the tracked updated native window'
    $element=[Windows.Automation.AutomationElement]::FromHandle($handle)
    Add-SmokeCheck ($scenario.name+'_updated_window_owned_by_verified_descendant') ($element.Current.ProcessId -eq $process.Id -and $process.StartTime.ToUniversalTime().Ticks -eq $Tracked.StartTicks)
    [pscustomobject]@{Process=$process;EntryProcess=$process;IsBootstrap=$false;Handle=$handle;Element=$element}
}
function Wait-T05Transaction([string]$TargetBuild,[bool]$Rollback){
    $timer=[Diagnostics.Stopwatch]::StartNew();$journalPath=Join-Path $stageDirectory '.autumnos-update/journal.json'
    $lastJournal=$null
    while($timer.Elapsed.TotalSeconds -lt $UpdateTimeoutSeconds){
        Observe-T05OwnedDescendants
        Poll-T05MaintenanceRepeats
        if(Test-Path -LiteralPath $journalPath){
            $journal=Read-T05Json $journalPath;$lastJournal=$journal
            if($journal.root -ne $stageDirectory -or $journal.toBuildId -ne $TargetBuild){throw 'Update journal does not bind this isolated root and intended signed target.'}
            if($product.Process.HasExited -and $journal.phase -in @('backedUp','applying','health') -and
                @($repeatLaunches|Where-Object {$_.Root -eq $stageDirectory}).Count -lt 2){Start-T05MaintenanceRepeat $journal}
            $scenario.transaction=$journal
            if($journal.phase -in @('aborted','rollingBack','rolledBack') -and -not $Rollback){
                Copy-Item -LiteralPath $journalPath -Destination (Join-Path $ReportDirectory ($scenario.name+'-failed-journal.json'))
                throw ('Positive update aborted: '+$journal.errorCode)
            }
            $expected=if($Rollback){'rolledBack'}else{'committed'}
            if($journal.phase -eq $expected){
                if(@($repeatLaunches|Where-Object {$_.Root -eq $stageDirectory -and -not $_.Finished}).Count){Start-Sleep -Milliseconds 150;continue}
                $next=@($trackedProcesses|Where-Object {$_.Root -eq $stageDirectory -and $_.Kind -eq 'AutumnOS.Client.exe' -and $_.Id -ne $scenario.a_business_pid -and -not $_.Process.HasExited})
                if($next.Count -eq 1){
                    if(-not $Rollback -and ($journal.childPid -ne $next[0].Id -or $journal.childStartUtcTicks -ne $next[0].StartTicks)){throw 'Health journal child identity differs from the actually observed updater descendant.'}
                    $next[0].Process.Refresh()
                    if($next[0].Process.MainWindowHandle -ne [IntPtr]::Zero){
                        $scenario.transaction=$journal
                        Copy-Item -LiteralPath $journalPath -Destination (Join-Path $ReportDirectory ($scenario.name+'-journal.json'))
                        return Product-T05FromTrackedBusiness $next[0]
                    }
                }
            }
        }
        if(-not $product.Process.HasExited){
            $wait=Find-T05 'UpdateWaitingReason'
            if($wait -and $wait.Current.Name -match 'UPDATE_\w+.*更新尚未提交'){throw $wait.Current.Name}
        }
        Start-Sleep -Milliseconds 200
    }
    throw ('Real update transaction did not complete within budget; last phase: '+$lastJournal.phase)
}
function Assert-T05GameStillBlocks([string]$State){
    $timer=[Diagnostics.Stopwatch]::StartNew();$beforeHash=Get-T05Hash $savePath
    while($timer.Elapsed.TotalSeconds -lt $BlockedObservationSeconds){
        $product.Process.Refresh()
        if($product.Process.HasExited -or (Test-Path -LiteralPath (Join-Path $stageDirectory '.autumnos-update/journal.json'))){throw "Update committed or exited during a real $State game."}
        $last=@(Read-SmokeEvents $runtimeLog|Where-Object {$_.instanceId -eq $scenario.game_instance})[-1]
        if($last.state -ne $State -or -not $last.blocksMaintenance){throw 'Real runtime state no longer matches the blocking observation.'}
        Start-Sleep -Milliseconds 300
    }
    Add-SmokeCheck ($scenario.name+'_'+$State.ToLowerInvariant()+'_blocks_beyond_idle_countdown') ((Get-T05Hash $savePath) -eq $beforeHash -and (Get-T05Hash (Join-Path $stageDirectory 'AutumnOS.Client.dll')) -eq $scenario.a_client_hash)
}
function Read-T05GameSave {
    Invoke-SmokeNamedButton $product.Element '读取存档'
    $nickname=Wait-SmokeCondition {Find-SmokeNamedElement $product.Element '玩家昵称' ([Windows.Automation.ControlType]::Edit)} 'game nickname after update'
    $null=Wait-SmokeCondition {$nickname.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $scenario.saved_nickname} 'the exact real game save read through SDK'
}
function Finish-T05Game {
    Click-T05 'CloseGameButton';Invoke-SmokeNamedButton $product.Element '确认结束';Wait-SmokeDesktop $product
}
function Start-T05Shortcut {
    $shell=New-Object -ComObject WScript.Shell;$link=$shell.CreateShortcut($scenario.shortcut)
    Add-SmokeCheck ($scenario.name+'_original_shortcut_target_unchanged') ([string]::Equals($link.TargetPath,$report.tested_executable,[StringComparison]::OrdinalIgnoreCase) -and (Get-T05Hash $scenario.shortcut) -eq $scenario.shortcut_hash)
    $entry=Start-Process -FilePath $scenario.shortcut -WindowStyle Normal -PassThru
    if(-not $entry){throw 'Original shortcut did not return a self-started entry process.'}
    $ownedEntryProcesses.Add($entry);$report.bootstrap_process_ids+=$entry.Id
    $business=Resolve-SmokeBusinessProcess -EntryProcess $entry -ExecutablePath $report.tested_executable -TimeoutSeconds $WindowTimeoutSeconds
    $ownedProcesses.Add($business);$report.process_ids+=$business.Id
    Register-T05Process $entry 'AutumnOS.exe';Register-T05Process $business 'AutumnOS.Client.exe' $entry.Id
    $record=@($trackedProcesses|Where-Object {$_.Id -eq $business.Id})[-1]
    $next=Product-T05FromTrackedBusiness $record;$next.EntryProcess=$entry;$next.IsBootstrap=$true
    return $next
}

function Invoke-T05Scenario([string]$Name,[string]$PayloadDirectory,[bool]$Rollback){
    $script:scenario=[ordered]@{name=$Name;status='in_progress';rollback=$Rollback;maximum_live_business_processes=1;game_instance=$null;transaction=$null;real_identity='not_run';maintenance_repeated_launches=@();maintenance_repeat_launch='not_run_pending_phase_not_observed'}
    $scenarioResults.Add($scenario)
    if($InstalledTestRoot){
        Assert-T05InstalledTestRoot
        $script:stageDirectory=$InstalledTestRoot
    }else{
        $script:stageDirectory=Join-Path $testScope ($Name+' 中文隔离目录');New-Item -ItemType Directory -Path $stageDirectory | Out-Null
        foreach($item in Get-ChildItem -LiteralPath (Split-Path $AExecutablePath -Parent) -Force){
            if($item.Name -in @('AutumnOS_Data','.autumnos-update')){continue}
            Assert-T05PlainPath $item.FullName
            if($item.PSIsContainer){foreach($child in Get-ChildItem -LiteralPath $item.FullName -Recurse -Force){if($child.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Source delivery has a reparse-point child.'}}}
            Copy-Item -LiteralPath $item.FullName -Destination $stageDirectory -Recurse
        }
    }
    $report.tested_executable=Join-Path $stageDirectory 'AutumnOS.exe';$scenario.entry=$report.tested_executable
    $script:dataDirectory=Join-Path $stageDirectory 'AutumnOS_Data';$script:runtimeLog=Join-Path $dataDirectory 'Logs/runtime-events.jsonl'
    Add-SmokeCheck ($Name+'_fresh_install_has_no_copied_user_data') (-not(Test-Path -LiteralPath $dataDirectory))
    $base=Read-T05Json (Join-Path $stageDirectory 'autumn.install.json');$report.build_id=$base.buildId;$scenario.a_build_id=$base.buildId
    $scenario.a_metadata=Read-T05BuildMetadata $stageDirectory;$report.source_snapshot_id=$scenario.a_metadata.SourceSnapshotId
    Add-SmokeCheck ($Name+'_A_actual_assembly_matches_install_identity') ($scenario.a_metadata.BuildId -eq $base.buildId -and $scenario.a_metadata.SourceSnapshotId -match '^sha256:[0-9a-f]{64}$')
    $scenario.a_client_hash=Get-T05Hash (Join-Path $stageDirectory 'AutumnOS.Client.dll');$scenario.stable_entry_hash=Get-T05Hash $report.tested_executable
    $scenario.shortcut=Join-Path $testScope ($Name+' 原入口更新演练.lnk')
    $shell=New-Object -ComObject WScript.Shell;$link=$shell.CreateShortcut($scenario.shortcut);$link.TargetPath=$report.tested_executable;$link.WorkingDirectory=$stageDirectory;$link.Description='派蒙 · 仅本地 T05 更新演练';$link.Save()
    $scenario.shortcut_hash=Get-T05Hash $scenario.shortcut
    Write-T05FeedList @() ($Name+'-empty-feed.json')
    $cwd=Join-Path $testScope ($Name+' working directory');New-Item -ItemType Directory -Path $cwd | Out-Null
    $script:product=Start-SmokeProduct $cwd
    Register-T05Process $product.EntryProcess 'AutumnOS.exe';Register-T05Process $product.Process 'AutumnOS.Client.exe' $product.EntryProcess.Id
    $scenario.a_business_pid=$product.Process.Id
    Click-T05 'HelloNextButton';Click-T05 'BrandNextButton';Click-T05 'PrepareDesktopButton';Wait-SmokeDesktop $product
    Show-T05Updates
    $automatic=(Find-T05 'AutomaticUpdates').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    $preview=(Find-T05 'PreviewUpdates').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
    Add-SmokeCheck ($Name+'_new_user_automatic_on_preview_off') ($automatic.Current.ToggleState -eq [Windows.Automation.ToggleState]::On -and $preview.Current.ToggleState -eq [Windows.Automation.ToggleState]::Off)
    Toggle-T05 'SettingsNavAppearance' $true;Click-T05 'DarkThemeButton';Click-T05 'NightWallpaperButton'
    Click-T05 'SettingsHomeButton';Wait-SmokeDesktop $product
    # One real drag creates meaningful desktop state for preservation; the full T02 gesture matrix
    # remains in its separate regression driver and is not duplicated or reclassified here.
    Invoke-SmokeDesktopDrag -Product $product -FromId 'SampleButton' -ToId 'StoreButton'
    $layoutPath=Join-Path $dataDirectory 'Config/desktop-layout.json'
    $null=Wait-SmokeCondition {if(Test-Path -LiteralPath $layoutPath){(Read-T05Json $layoutPath).revision -ge 1}} 'desktop order persisted through a real native drag'
    Add-SmokeCheck ($Name+'_real_desktop_layout_created_before_update') ((Read-T05Json $layoutPath).orderedIds -contains 'app.cn.labchronicles.elementpairs')
    Invoke-SmokePointer $product 'SampleButton' 'single_click'
    $foreground=Wait-SmokeNewForeground $runtimeLog 0 '';$scenario.game_instance=$foreground.instanceId
    $script:savePath=Join-Path $dataDirectory 'Saves/cn.labchronicles.elementpairs/guest/game.json'
    $nickname=Wait-SmokeCondition {Find-SmokeNamedElement $product.Element '玩家昵称' ([Windows.Automation.ControlType]::Edit)} 'real game nickname input'
    $scenario.saved_nickname='派蒙·T05-'+$Name
    $nickname.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($scenario.saved_nickname)
    Invoke-SmokeNamedButton $product.Element '第 1 张卡片，未翻开';Invoke-SmokeNamedButton $product.Element '第 2 张卡片，未翻开'
    Invoke-SmokeNamedButton $product.Element '保存进度';Invoke-SmokeNamedButton $product.Element '允许'
    $null=Wait-SmokeCondition {Test-Path -LiteralPath $savePath} 'real SDK save'
    $saved=Read-T05Json $savePath
    Add-SmokeCheck ($Name+'_real_game_saved_unicode_and_moves') ($saved.value.nickname -eq $scenario.saved_nickname -and $saved.value.moves -ge 1)
    $unsaved='派蒙·T05待保存-'+$Name;$nickname.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($unsaved)
    $manifest=Publish-T05SignedFixture $PayloadDirectory;$scenario.target_build_id=$manifest.buildId;$scenario.target_version=$manifest.version
    Click-T05 'BackgroundButton';Wait-SmokeDesktop $product;Show-T05Updates
    if(-not $Rollback){Test-T05TamperedFeedSignature $manifest $PayloadDirectory}
    Click-T05 'CheckSystemUpdate'
    $null=Wait-SmokeCondition {
        $text=(Find-T05 'UpdateStatus').Current.Name
        if($text -match '更新未完成|自动安装尚未配置'){throw $text}
        if($text -match '已准备，尚未安装'){return $true}
        $download=Find-T05 'DownloadSystemUpdate';if($download -and $download.Current.IsEnabled){$download.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()}
        return $false
    } 'the actual downloaded and signature-verified candidate' $UpdateTimeoutSeconds
    $scenario.staged_status=(Find-T05 'UpdateStatus').Current.Name;$scenario.candidate_text=(Find-T05 'UpdateCandidate').Current.Name
    Add-SmokeCheck ($Name+'_signed_target_selected_and_staged') ($scenario.candidate_text.Contains([string]$manifest.version))
    $null=Wait-SmokeCondition {(Find-T05 'UpdateWaitingReason').Current.Name -match '后台运行'} 'the real background game wait reason'
    Save-T05Screenshot '01-background-wait'
    if(-not $Rollback){Assert-T05GameStillBlocks 'Background'}
    Click-T05 'SettingsHomeButton';Wait-SmokeDesktop $product;Invoke-SmokePointer $product 'SampleButton' 'single_click'
    $null=Wait-SmokeCondition {(Find-T05 'RuntimeStatus').Current.Name -match '前台运行'} 'same game foreground'
    if(-not $Rollback){Assert-T05GameStillBlocks 'Foreground';Save-T05Screenshot '02-foreground-wait'}
    $nickname=Wait-SmokeCondition {Find-SmokeNamedElement $product.Element '玩家昵称' ([Windows.Automation.ControlType]::Edit)} 'same unsaved input'
    Add-SmokeCheck ($Name+'_waiting_did_not_lose_unsaved_input') ($nickname.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -eq $unsaved)
    $scenario.saved_nickname=$unsaved;Invoke-SmokeNamedButton $product.Element '保存进度'
    $null=Wait-SmokeCondition {(Read-T05Json $savePath).value.nickname -eq $unsaved} 'user explicitly saves before ending'
    $protected=@('Config/first-run.json','Config/desktop-preferences.json','Config/desktop-layout.json','Saves/cn.labchronicles.elementpairs/guest/game.json')
    $protected+=@(Get-ChildItem -LiteralPath (Join-Path $dataDirectory 'Apps') -Recurse -File -Filter '.autumnos-install.json'|ForEach-Object {[IO.Path]::GetRelativePath($dataDirectory,$_.FullName).Replace('\','/')})
    if(Test-Path -LiteralPath (Join-Path $dataDirectory 'Config/application-permissions.v1.json')){$protected+='Config/application-permissions.v1.json'}
    $scenario.data_before=@(foreach($relative in $protected){$path=Join-Path $dataDirectory $relative;[ordered]@{path=$relative;sha256=Get-T05Hash $path}})
    Finish-T05Game
    $null=Wait-SmokeCondition {@(Read-SmokeEvents $runtimeLog|Where-Object {$_.instanceId -eq $scenario.game_instance -and $_.state -eq 'Closed' -and -not $_.blocksMaintenance}).Count -gt 0} 'actual resource-complete game close'
    # No Restart button: this exercises default-on background download plus bounded idle restart.
    $script:product=Wait-T05Transaction $manifest.buildId $Rollback
    $repeatRecords=@($scenario.maintenance_repeated_launches)
    $busyRecords=@($repeatRecords|Where-Object {$_.status -like 'passed_*'})
    if($busyRecords.Count){$scenario.maintenance_repeat_launch=if($busyRecords.Count -eq $repeatRecords.Count){'passed'}else{'partial_observed_busy_with_missed_interval'}}
    Wait-SmokeDesktop $product
    $receipt=Read-T05Json (Join-Path $stageDirectory 'autumn.install.json')
    $expectedBuild=if($Rollback){$base.buildId}else{$manifest.buildId}
    Add-SmokeCheck ($Name+'_expected_build_receipt_after_health_or_recovery') ($receipt.buildId -eq $expectedBuild)
    $scenario.result_metadata=Read-T05BuildMetadata $stageDirectory
    Add-SmokeCheck ($Name+'_actual_result_assembly_has_expected_build_and_snapshot') ($scenario.result_metadata.BuildId -eq $expectedBuild -and $scenario.result_metadata.SourceSnapshotId -match '^sha256:[0-9a-f]{64}$')
    Add-SmokeCheck ($Name+'_stable_entry_bytes_unchanged') ((Get-T05Hash $report.tested_executable) -eq $scenario.stable_entry_hash)
    $clientEntry=@($manifest.files|Where-Object path -eq 'AutumnOS.Client.dll')
    if($Rollback){
        Add-SmokeCheck ($Name+'_restored_actual_A_binary_bytes') ((Get-T05Hash (Join-Path $stageDirectory 'AutumnOS.Client.dll')) -eq $scenario.a_client_hash)
        $security=Read-T05Json (Join-Path $stageDirectory '.autumnos-update/security.json')
        Add-SmokeCheck ($Name+'_bad_build_recorded_against_replay') ($security.failedBuilds -contains $manifest.buildId)
    }else{
        Add-SmokeCheck ($Name+'_actual_B_binary_replaced_and_matches_signed_payload') ($clientEntry.Count -eq 1 -and
            (Get-T05Hash (Join-Path $stageDirectory 'AutumnOS.Client.dll')) -eq $clientEntry[0].sha256 -and $clientEntry[0].sha256 -ne $scenario.a_client_hash)
        $resultPath=Join-Path $stageDirectory ('.autumnos-update/'+$scenario.transaction.transactionId+'/result.json')
        $result=Wait-SmokeCondition {if(Test-Path -LiteralPath $resultPath){Read-T05Json $resultPath}} 'independent updater core-health result'
        Add-SmokeCheck ($Name+'_core_initialization_confirmed_not_just_window') ($result.outcome -eq 'committed' -and $result.coreHealthVerified -and $result.buildId -eq $manifest.buildId)
        Copy-Item -LiteralPath $resultPath -Destination (Join-Path $ReportDirectory ($Name+'-health-result.json'))
    }
    foreach($file in $scenario.data_before){Add-SmokeCheck ($Name+'_preserved_'+$file.path.Replace('/','_')) ((Get-T05Hash (Join-Path $dataDirectory $file.path)) -eq $file.sha256)}
    Show-T05Updates;Toggle-T05 'SettingsNavAbout' $true
    Add-SmokeCheck ($Name+'_native_about_reports_actual_expected_build') ((Find-T05 'AboutBuild').Current.Name -eq $expectedBuild)
    Save-T05Screenshot '03-after-transaction'
    Click-T05 'SettingsHomeButton';Wait-SmokeDesktop $product
    Invoke-SmokePointer $product 'SampleButton' 'single_click';Read-T05GameSave;Save-T05Screenshot '04-real-save-restored';Finish-T05Game
    if($Rollback){
        Show-T05Updates;Click-T05 'CheckSystemUpdate'
        $null=Wait-SmokeCondition {(Find-T05 'CheckSystemUpdate').Current.IsEnabled} 'bad candidate rejected after explicit recheck'
        Add-SmokeCheck ($Name+'_failed_candidate_not_staged_again') (-not (Find-T05 'RestartSystemUpdate').Current.IsEnabled)
        $transactionId=$scenario.transaction.transactionId
        $observation=[Diagnostics.Stopwatch]::StartNew()
        while($observation.Elapsed.TotalSeconds -lt 20){Observe-T05OwnedDescendants;if((Read-T05Json (Join-Path $stageDirectory '.autumnos-update/journal.json')).transactionId -ne $transactionId){throw 'Failed candidate restarted another update transaction.'};Start-Sleep -Milliseconds 300}
        Add-SmokeCheck ($Name+'_no_bad_version_update_rollback_loop') $true
        Click-T05 'SettingsHomeButton';Wait-SmokeDesktop $product
    }
    Close-SmokeProduct $product
    $script:product=Start-T05Shortcut;Wait-SmokeDesktop $product
    $scenario.original_shortcut_restart='passed'
    Show-T05Updates;Toggle-T05 'SettingsNavAbout' $true
    Add-SmokeCheck ($Name+'_original_shortcut_reopens_expected_build') ((Find-T05 'AboutBuild').Current.Name -eq $expectedBuild)
    Click-T05 'SettingsHomeButton';Wait-SmokeDesktop $product
    Close-SmokeProduct $product
    Add-SmokeCheck ($Name+'_different_cwd_never_received_user_data') (-not(Test-Path -LiteralPath (Join-Path $cwd 'AutumnOS_Data')))
    Add-SmokeCheck ($Name+'_one_business_instance_through_transaction') ($scenario.maximum_live_business_processes -eq 1)
    $scenario.status='passed'
}

try{
    if(-not $IsWindows){throw 'T05 update smoke requires a real interactive Windows desktop.'}
    $existing=@(Get-Process -Name AutumnOS,AutumnOS.Client,AutumnOS.Updater -ErrorAction SilentlyContinue)
    if($existing.Count){$report.status='blocked_existing_user_instance';throw 'Existing product/update process detected. It is not activated or closed; exit normally before this isolated drill.'}
    $principal=[Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    Add-SmokeCheck 'standard_user' (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator))
    foreach($path in @($AExecutablePath,$BPayloadDirectory,$FeedDirectory,$ReportDirectory)){Assert-T05PlainPath $path}
    Assert-T05InstalledTestRoot
    if([IO.Path]::GetFileName($AExecutablePath) -ne 'AutumnOS.exe'){throw 'Use the actual delivered stable A entry.'}
    foreach($name in @('AutumnOS.Client.exe','AutumnOS.Client.dll','AutumnOS.Updater.exe','autumn.install.json')){if(-not(Test-Path -LiteralPath (Join-Path (Split-Path $AExecutablePath -Parent) $name))){throw "A lacks real update protocol delivery: $name"}}
    $feedAssembly=[Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path (Split-Path $AExecutablePath -Parent) 'AutumnOS.Update.dll')))
    $feedStream=$feedAssembly.GetManifestResourceStream('AutumnOS.Update.TestFeed.txt')
    if(-not $feedStream){throw 'A is not an explicitly compiled local-feed update drill build.'}
    $reader=[IO.StreamReader]::new($feedStream)
    try{$embeddedFeed=$reader.ReadToEnd().Trim()}finally{$reader.Dispose()}
    Add-SmokeCheck 'feed_matches_compile_time_isolated_A_source' ([string]::Equals([IO.Path]::GetFullPath($embeddedFeed),$FeedDirectory,[StringComparison]::OrdinalIgnoreCase))
    New-Item -ItemType Directory -Path $FeedDirectory -Force | Out-Null
    if(Test-Path -LiteralPath (Join-Path $FeedDirectory 'releases.json')){Copy-Item -LiteralPath (Join-Path $FeedDirectory 'releases.json') -Destination (Join-Path $ReportDirectory 'feed-before.json')}
    $desktopRuntime=Join-Path $projectRoot '.tools/dotnet/shared/Microsoft.WindowsDesktop.App/10.0.12'
    foreach($name in @('WindowsBase.dll','UIAutomationTypes.dll','UIAutomationClient.dll','System.Drawing.Common.dll')){Add-Type -Path (Join-Path $desktopRuntime $name)}
    Initialize-T05DialogNative
    Initialize-T05FocusNative
    if(-not('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){
        $nativeStrings=@($helperAst.FindAll({param($node) $node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.Contains('namespace AutumnDesktopInteractionSmoke')},$true))
        if($nativeStrings.Count -ne 1){throw 'Maintained native helper declaration missing.'};Add-Type -TypeDefinition $nativeStrings[0].Value
    }
    Invoke-T05Scenario 'A-to-B' $BPayloadDirectory $false
    if($BadPayloadDirectory){Invoke-T05Scenario 'A-bad-to-A' ([IO.Path]::GetFullPath($BadPayloadDirectory)) $true}
    else{$report.rollback='not_run_no_bad_payload_supplied'}
    $report.status='passed'
}catch{
    $report.failure_type=$_.Exception.GetType().FullName;$report.failure=$_.Exception.Message
    if($report.failure -match 'guarded drag point|cursor.*moved|outside.*owned|outside.*test process'){
        $report.input_guard_failure='observed_pointer_or_ownership_mismatch; cause_not_proven; no_drag_retry'
    }
    if($scenario){$scenario.status='failed'}
    Write-Warning ('T05 real update drill: '+$report.failure)
    if($product -and -not $product.Process.HasExited){try{$null=Get-T05WindowGeometry $product 'failure_before_any_capture_or_restore'}catch{}}
    if($product -and -not $product.Process.HasExited){try{Save-T05Screenshot '99-failure'}catch{}}
}finally{
    $clean=$true
    if($scenario){try{Poll-T05MaintenanceRepeats}catch{$clean=$false}}
    foreach($process in $ownedProcesses){
        try{
            if(-not $process.HasExited){
                $process.Refresh()
                if($process.MainWindowHandle -eq [IntPtr]::Zero -or -not $process.CloseMainWindow() -or -not $process.WaitForExit(10000)){
                    $clean=$false;$report.remaining_processes+=@([ordered]@{pid=$process.Id;path=$process.MainModule.FileName;action='left_running_no_forced_termination'})
                }
            }
        }catch{$clean=$false}
    }
    foreach($entry in $ownedEntryProcesses){try{if(-not $entry.WaitForExit(10000)){$clean=$false;$report.remaining_processes+=@([ordered]@{pid=$entry.Id;action='bootstrap_left_running'})}}catch{$clean=$false}}
    # Capture held repeat exit codes after any IDOK delivered by cleanup. An unrecognized window
    # remains a failure with evidence; a completed busy dialog is not left as a vague "started".
    if($scenario){try{Poll-T05MaintenanceRepeats}catch{$clean=$false}}
    foreach($tracked in $trackedProcesses){
        if($tracked.Kind -eq 'AutumnOS.Updater.exe'){try{if(-not $tracked.Process.WaitForExit(10000)){$clean=$false;$report.remaining_processes+=@([ordered]@{pid=$tracked.Id;action='updater_left_running'})}}catch{$clean=$false}}
    }
    Restore-AutumnNativePointer
    if($script:initialPointerPosition -and ('AutumnDesktopInteractionSmoke.NativeWindows' -as [type])){$null=[AutumnDesktopInteractionSmoke.NativeWindows]::SetCursorPos($script:initialPointerPosition.X,$script:initialPointerPosition.Y)}
    $report.cleanup=if($clean){'passed'}else{'failed'};if(-not $clean){$report.status='failed'}
    $attempts=@($repeatLaunches|ForEach-Object {$_.Record})
    $passedAttempts=@($attempts|Where-Object {$_.status -like 'passed_*'})
    $failedAttempts=@($attempts|Where-Object {$_.status -like 'failed_*' -or $_.status -eq 'started'})
    $report.maintenance_repeat_launch=if($failedAttempts.Count){'failed_or_incomplete_observation'}elseif($attempts.Count -eq 0){'not_run_pending_phase_not_observed'}elseif($passedAttempts.Count -eq $attempts.Count){'passed'}elseif($passedAttempts.Count){'partial_observed_busy_with_missed_interval'}else{'not_run_busy_interval_missed'}
    $report.maintenance_repeat_launch_attempts=$attempts
    $report.finished_utc=[DateTimeOffset]::UtcNow.ToString('o');$report.check_count=$checks.Count;$report.passed_checks=@($checks|Where-Object status -eq 'passed').Count
    $report.process_evidence=@($trackedProcesses|ForEach-Object {[ordered]@{pid=$_.Id;start_utc_ticks=$_.StartTicks;path=$_.Path;parent_pid=$_.ParentId;kind=$_.Kind}})
    $report|ConvertTo-Json -Depth 20|Set-Content -LiteralPath $reportPath -Encoding utf8
    foreach($process in $ownedProcesses){$process.Dispose()};foreach($entry in $ownedEntryProcesses){$entry.Dispose()}
    foreach($tracked in $trackedProcesses|Where-Object Kind -eq 'AutumnOS.Updater.exe'){$tracked.Process.Dispose()}
    Write-Host "REPORT $reportPath"
}
if($report.status -ne 'passed'){throw "T05 real update smoke did not pass; preserved evidence: $reportPath"}
