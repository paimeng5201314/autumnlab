# Generates an artifact-only variant; never edits tracked source/scripts/docs or starts UI.
[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProjectRoot,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop'
$ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot)
$source=Join-Path $ProjectRoot 'scripts/Test-T05UpdateSmoke.ps1'
$output=Join-Path $OutputDirectory 'Test-T05UpdaterCrash.ps1'
$text=Get-Content -LiteralPath $source -Raw
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Maintained driver has parsing errors.'}
$target=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Wait-T05Transaction'},$false))
if($target.Count -ne 1){throw 'The expected maintained transaction wait function is ambiguous.'}
$replacement=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Wait-T05FaultTransaction.ps1') -Raw
$span=$target[0].Extent
$text=$text.Remove($span.StartOffset,$span.EndOffset-$span.StartOffset).Insert($span.StartOffset,$replacement)
$text=$text.Replace('[string]$BadPayloadDirectory,','[string]$BadPayloadDirectory,'+"`n"+'    [ValidateSet(''applying'',''health'')][string]$FaultPhase = ''applying'',')
$quotedRoot="'"+$ProjectRoot.Replace("'","''")+"'"
$text=$text.Replace('$projectRoot=Split-Path $PSScriptRoot -Parent','$projectRoot='+$quotedRoot)
$text=$text.Replace('. "$PSScriptRoot/Smoke-NativeAppInput.ps1"','. (Join-Path $projectRoot ''scripts/Smoke-NativeAppInput.ps1'')')
$text=$text.Replace('$helperPath=Join-Path $PSScriptRoot ''Test-DesktopInteractionSmoke.ps1''','$helperPath=Join-Path $projectRoot ''scripts/Test-DesktopInteractionSmoke.ps1''')
$text=$text.Replace('checkpoint=''real_delivered_client_signed_local_feed_upgrade_and_optional_rollback''','checkpoint=''real_delivered_A_owned_updater_termination_then_original_entry_recovery''')
$text=$text.Replace('# native UI/game SDK actions. It never writes a save/config/receipt/journal or kills a process.','# native UI/game SDK actions. This artifact-only variant terminates only the exact held test-owned updater at the selected journal phase; no business/user process is killed.')
$old=@'
    Invoke-T05Scenario 'A-to-B' $BPayloadDirectory $false
    if($BadPayloadDirectory){Invoke-T05Scenario 'A-bad-to-A' ([IO.Path]::GetFullPath($BadPayloadDirectory)) $true}
    else{$report.rollback='not_run_no_bad_payload_supplied'}
'@
$new=@'
    Invoke-T05Scenario ('A-updater-crash-'+$FaultPhase) $BPayloadDirectory $true
    $report.fault_injection.status='passed_real_A_build_original_shortcut_and_actual_game_save_restored'
'@
$normalized=$text.Replace("`r`n","`n")
if(-not $normalized.Contains($old.Replace("`r`n","`n"))){throw 'Maintained scenario invocation changed; inspect before generating the derivative.'}
$text=$normalized.Replace($old.Replace("`r`n","`n"),$new.Replace("`r`n","`n"))
$tokens=$null;$errors=$null
$null=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
if($errors.Count){$errors|Out-String|Write-Error;throw 'Generated fault driver failed syntax validation.'}
if($text.Contains('$PSScriptRoot')){throw 'Unresolved script-root reference in generated driver.'}
# Keep test nicknames inside the real sample game's 24-character contract.
# UIA ValuePattern can otherwise bypass HTML maxlength for these longer scenario names.
$oldNickname='$scenario.saved_nickname=''派蒙·T05-''+$Name'
$oldUnsaved='$unsaved=''派蒙·T05待保存-''+$Name;'
if(-not $text.Contains($oldNickname) -or -not $text.Contains($oldUnsaved)){throw 'Nickname fixture changed; review the bounded test value adaptation.'}
$text=$text.Replace($oldNickname,'$scenario.saved_nickname=''派蒙·T05-''+$Name.Substring(0,[Math]::Min(10,$Name.Length))')
$text=$text.Replace($oldUnsaved,'$unsaved=''派蒙·T05待保存-''+$Name.Substring(0,[Math]::Min(10,$Name.Length));')
# Replace only the observer's reader, preserving the latest sharing retry classifier.
$readerTokens=$null;$readerErrors=$null
$readerAst=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$readerTokens,[ref]$readerErrors)
if($readerErrors.Count){throw 'Derivative parse failed before observation adaptation.'}
$reader=@($readerAst.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Read-T05Json'},$false))
if($reader.Count -ne 1){throw 'Expected one JSON observer.'}
$readerSource=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Read-T05Observation.ps1') -Raw
$span=$reader[0].Extent
$text=$text.Remove($span.StartOffset,$span.EndOffset-$span.StartOffset).Insert($span.StartOffset,$readerSource)
$text=$text.Replace('Copy-Item -LiteralPath $journalPath -Destination','Copy-T05ObservedFile -LiteralPath $journalPath -Destination')
$text=$text.Replace('Copy-Item -LiteralPath (Join-Path $stageDirectory ''.autumnos-update/journal.json'') -Destination','Copy-T05ObservedFile -LiteralPath (Join-Path $stageDirectory ''.autumnos-update/journal.json'') -Destination')
$text=$text.Replace('Copy-Item -LiteralPath (Join-Path $stageDirectory ''.autumnos-update/last-error.json'') -Destination','Copy-T05ObservedFile -LiteralPath (Join-Path $stageDirectory ''.autumnos-update/last-error.json'') -Destination')
# Binding text can appear one dispatcher turn after the About navigation completes.
$aboutChecks=@("    Add-SmokeCheck (`$Name+'_native_about_reports_actual_expected_build')", "    Add-SmokeCheck (`$Name+'_original_shortcut_reopens_expected_build')")
foreach($aboutCheck in $aboutChecks){
    if(-not $text.Contains($aboutCheck)){throw 'About assertion changed; review derivative adaptation.'}
    $wait='    $null=Wait-SmokeCondition {$about=Find-T05 ''AboutBuild''; $about -and $about.Current.Name -eq $expectedBuild} ''native About binding to actual expected build''' + "`n"
    $text=$text.Replace($aboutCheck,$wait+$aboutCheck)
}
[IO.File]::WriteAllText($output,$text,[Text.UTF8Encoding]::new($false))
[ordered]@{status='prepared_not_run';source_driver=$source;source_sha256=(Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant();generated_driver=$output;generated_sha256=(Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant();syntax_validation='passed';real_UI_execution='not_run';physical_power_loss='not_run'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $OutputDirectory 'driver-preparation.json') -Encoding utf8
Write-Host $output

