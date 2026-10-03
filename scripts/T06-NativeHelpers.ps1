# Shared test helpers only. Dot-sourcing this file does not start any product or test body.
function Import-T06Functions([string]$Path,[string[]]$Names,[string[]]$Exclude=@()){
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($Path,[ref]$tokens,[ref]$errors)
    if($errors.Count){throw "Cannot parse maintained helper: $Path"}
    $definitions=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$false))
    foreach($definition in $definitions){
        if($definition.Name -in $Exclude -or ($Names -and $definition.Name -notin $Names)){continue}
        $body=$definition.Body.Extent.Text
        $body=$body.Substring(1,$body.Length-2)
        if($definition.Parameters.Count){$body='param('+ (($definition.Parameters|ForEach-Object {$_.Extent.Text}) -join ',')+")`n"+$body}
        # Preserve header parameter declarations when importing only function bodies.
        Set-Item -LiteralPath ('Function:script:'+$definition.Name) -Value ([ScriptBlock]::Create($body))
    }
    if($Names){foreach($name in $Names){if(@($definitions|Where-Object Name -eq $name).Count -ne 1){throw "Maintained helper must be unique: $name"}}}
    return $ast
}
function Read-T06Snapshot([string]$Path){
    Assert-T05PlainPath $Path
    Invoke-T05SharingRead {
        $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $memory=[IO.MemoryStream]::new()
        try{
            if($stream.Length -gt 16MB){throw 'Bounded native test record limit exceeded.'}
            $stream.CopyTo($memory)
            if($memory.Length -gt 16MB){throw 'Bounded native test record limit exceeded.'}
            return [pscustomobject]@{Bytes=$memory.ToArray()}
        }finally{$memory.Dispose();$stream.Dispose()}
    }
}
function Read-T05Json([string]$Path){
    # Close the observation handle before parsing. Invalid JSON is never retried.
    $snapshot=Read-T06Snapshot $Path
    [Text.Encoding]::UTF8.GetString($snapshot.Bytes)|ConvertFrom-Json
}
function Copy-T06ObservedFile([string]$Source,[string]$Destination){
    $snapshot=Read-T06Snapshot $Source
    [IO.File]::WriteAllBytes($Destination,$snapshot.Bytes)
}
function Read-SmokeEvents([string]$Path){
    if(-not(Test-Path -LiteralPath $Path)){return @()}
    $snapshot=Read-T06Snapshot $Path
    $text=[Text.Encoding]::UTF8.GetString($snapshot.Bytes)
    # A trailing append fragment is uncommitted. Never swallow malformed complete records.
    $last=$text.LastIndexOf("`n");if($last -lt 0){return @()}
    return @($text.Substring(0,$last).Split("`n")|Where-Object {$_.Trim()}|ForEach-Object {$_|ConvertFrom-Json})
}
function Get-T06Registration {
    $registry=Read-T05Json (Join-Path $dataDirectory 'Apps/.autumnos-registry.json')
    $apps=@($registry.applications|Where-Object {$_.appId -eq 'cn.labchronicles.storeprobe' -and $_.isInstalled})
    if($apps.Count -ne 1){throw 'Expected exactly one installed controlled Store app.'}
    return $apps[0]
}
function Get-T06ProtectedData {
    $files=[Collections.Generic.List[string]]::new()
    foreach($name in @('Config','Saves')){
        $directory=Join-Path $dataDirectory $name
        if(Test-Path -LiteralPath $directory){foreach($file in Get-ChildItem -LiteralPath $directory -Recurse -File -Filter '*.json'){$files.Add($file.FullName)}}
    }
    $files.Add((Join-Path $dataDirectory 'Apps/.autumnos-registry.json'))
    foreach($file in Get-ChildItem -LiteralPath (Join-Path $dataDirectory 'Apps') -Recurse -File -Filter '.autumnos-install.json'){$files.Add($file.FullName)}
    $registration=Get-T06Registration
    $appRoot=[IO.Path]::GetFullPath($registration.package.directoryPath)
    $allowed=[IO.Path]::GetFullPath((Join-Path $dataDirectory 'Apps')).TrimEnd('\')+'\'
    if(-not $appRoot.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)){throw 'Installed app content escaped this isolated data root.'}
    foreach($file in Get-ChildItem -LiteralPath $appRoot -Recurse -File){$files.Add($file.FullName)}
    return @(foreach($path in $files|Sort-Object -Unique){
        Assert-T05PlainPath $path
        [pscustomobject]@{path=[IO.Path]::GetRelativePath($dataDirectory,$path).Replace('\','/');sha256=Get-T05Hash $path}
    })
}
function Assert-T06ProtectedData($Before,[string]$Label){
    foreach($file in $Before){
        Add-SmokeCheck ($Label+'_preserved_'+$file.path.Replace('/','_')) ((Get-T05Hash (Join-Path $dataDirectory $file.path)) -eq $file.sha256)
    }
}
function Select-T06StoreChannel([string]$Name){
    $combo=Show-Element 'StoreVersionChannel'
    $combo.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand()
    $item=Wait-SmokeCondition {
        $filter=[Windows.Automation.AndCondition]::new(
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty,$Name),
            [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ControlTypeProperty,[Windows.Automation.ControlType]::ListItem))
        $scopes=[Collections.Generic.List[object]]::new();$scopes.Add($product.Element)
        $owned=[Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty,$product.Process.Id)
        foreach($native in [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,$owned)){$scopes.Add($native)}
        foreach($scope in $scopes){
            $found=$scope.FindFirst([Windows.Automation.TreeScope]::Descendants,$filter)
            if($found -and $found.Current.ProcessId -eq $product.Process.Id -and -not $found.Current.IsOffscreen){return $found}
        }
    } ('Store version choice '+$Name) 8
    $item.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
}
function Enable-T06StoreSource {
    Click 'SettingsButton';Toggle 'SettingsNavDiagnostics' $true;Toggle 'DeveloperModeToggle' $true
    Click 'SettingsHomeButton';Click 'DeveloperButton';Click 'DeveloperStoreTest';Click-Named '开启测试源'
    $null=Wait-SmokeCondition {(Find 'StoreSource').Current.Name -eq '本地集成测试数据，不是 GitHub 实时结果'} 'explicit controlled Store label' 10
    $null=Require 'StoreRepository-90004001'
}
function Wait-T06ProbeText([string]$Text){
    $null=Wait-SmokeCondition {(Input-Value) -eq $Text} 'exact SDK-restored controlled app text' 15
}
function Close-T06ManagedApp {
    Click 'ManagedAppClose';Click-Named '确认结束';Wait-SmokeDesktop $product
    $instance=$scenario.game_instance
    $null=Wait-SmokeCondition {@(Read-SmokeEvents $runtimeLog|Where-Object {$_.instanceId -eq $instance -and $_.state -eq 'Closed' -and -not $_.blocksMaintenance}).Count -gt 0} 'actual managed WebView resources released' 15
}
function Assert-T06AppBlocks([string]$State){
    $journalPath=Join-Path $stageDirectory '.autumnos-update/journal.json'
    $beforeId=if(Test-Path -LiteralPath $journalPath){(Read-T05Json $journalPath).transactionId}else{$null}
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while($timer.Elapsed.TotalSeconds -lt $BlockedObservationSeconds){
        $product.Process.Refresh()
        if($product.Process.HasExited){throw "Host exited while the installed app remained $State."}
        $events=@(Read-SmokeEvents $runtimeLog|Where-Object instanceId -eq $scenario.game_instance)
        if(-not $events.Count -or $events[-1].state -ne $State -or -not $events[-1].blocksMaintenance){throw 'Real installed-app state does not match the maintenance blocker.'}
        $currentId=if(Test-Path -LiteralPath $journalPath){(Read-T05Json $journalPath).transactionId}else{$null}
        if($currentId -ne $beforeId){throw 'Host submitted an update with a real installed app still alive.'}
        Start-Sleep -Milliseconds 250
    }
    Add-SmokeCheck ($scenario.name+'_'+$State.ToLowerInvariant()+'_installed_app_blocks_host_commit') ((Get-T05Hash (Join-Path $stageDirectory 'AutumnOS.Client.dll')) -eq $scenario.a_client_hash)
}
function Test-T06NormalRecall {
    $before=@(Read-SmokeEvents $runtimeLog).Count
    $stderr=Join-Path $ReportDirectory ($scenario.name+'-background-recall-stderr.log')
    $entry=Start-Process -FilePath $report.tested_executable -WorkingDirectory $testScope -WindowStyle Hidden -PassThru -RedirectStandardError $stderr
    $null=$entry.Handle;$startTicks=$entry.StartTime.ToUniversalTime().Ticks
    $ownedEntryProcesses.Add($entry);$report.bootstrap_process_ids+=$entry.Id
    Register-T05Process $entry 'AutumnOS.exe'
    Add-SmokeCheck ($scenario.name+'_repeat_entry_forwards_with_zero_exit') ($entry.WaitForExit(20000) -and $entry.ExitCode -eq 0)
    $diagnostic=Get-Content -LiteralPath $stderr -Raw
    $match=[regex]::Match($diagnostic,'AUTUMNOS_BOOTSTRAP role=child pid=(\d+) child=(\d+) startTicks=(\d+)')
    if(-not $match.Success -or [int]$match.Groups[1].Value -ne $entry.Id -or [long]$match.Groups[3].Value -lt $startTicks){throw 'Repeat entry diagnostic did not bind its own exact child.'}
    $childId=[int]$match.Groups[2].Value
    $recall=Wait-SmokeCondition {@(Read-SmokeEvents (Join-Path $dataDirectory 'Logs/launcher-events.jsonl')|Where-Object {$_.eventName -eq 'recall_handled' -and $_.primaryPid -eq $product.Process.Id -and $_.requestPid -eq $childId -and $_.windowHandle -eq $product.Handle.ToInt64()}).Count -gt 0} 'original primary acknowledges the exact repeat child' 15
    $product.Process.Refresh()
    Add-SmokeCheck ($scenario.name+'_repeat_entry_retains_original_PID_HWND_and_settings_page') ($recall -and $product.Process.MainWindowHandle -eq $product.Handle -and [bool](Find-T05 'UpdateStatus') -and -not(Visible 'HelloNextButton'))
    Add-SmokeCheck ($scenario.name+'_repeat_entry_does_not_relaunch_background_app') (@(Read-SmokeEvents $runtimeLog|Select-Object -Skip $before|Where-Object {$_.instanceId -eq $scenario.game_instance -and $_.state -in @('Starting','Foreground','Closed')}).Count -eq 0)
    $scenario.background_recall=[ordered]@{entry_pid=$entry.Id;start_ticks=$startTicks;forwarder_pid=$childId;primary_pid=$product.Process.Id;hwnd=$product.Handle.ToInt64();game_instance=$scenario.game_instance;status='passed'}
}
