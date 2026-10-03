[CmdletBinding()]
param([Parameter(Mandatory)][string]$ProjectRoot,[Parameter(Mandatory)][string]$OutputDirectory)
$ErrorActionPreference='Stop';$ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot)
$source=Join-Path $ProjectRoot 'scripts/Test-T05UpdateSmoke.ps1';$output=Join-Path $OutputDirectory 'Test-T05BusyFile.ps1'
$text=Get-Content -LiteralPath $source -Raw;$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Maintained driver parse failure.'}
$target=@($ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Wait-T05Transaction'},$false))
if($target.Count -ne 1){throw 'Maintained wait function ambiguous.'}
$span=$target[0].Extent;$replacement=Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Wait-T05BusyFileTransaction.ps1') -Raw
$text=$text.Remove($span.StartOffset,$span.EndOffset-$span.StartOffset).Insert($span.StartOffset,$replacement)
$quotedRoot="'"+$ProjectRoot.Replace("'","''")+"'"
$text=$text.Replace('$projectRoot=Split-Path $PSScriptRoot -Parent','$projectRoot='+$quotedRoot)
$text=$text.Replace('. "$PSScriptRoot/Smoke-NativeAppInput.ps1"','. (Join-Path $projectRoot ''scripts/Smoke-NativeAppInput.ps1'')')
$text=$text.Replace('$helperPath=Join-Path $PSScriptRoot ''Test-DesktopInteractionSmoke.ps1''','$helperPath=Join-Path $projectRoot ''scripts/Test-DesktopInteractionSmoke.ps1''')
$text=$text.Replace('checkpoint=''real_delivered_client_signed_local_feed_upgrade_and_optional_rollback''','checkpoint=''real_A_file_occupancy_abort_and_persistent_retry_hold''')
$text=$text.Replace("`r`n","`n")
$security=@'
        $security=Read-T05Json (Join-Path $stageDirectory '.autumnos-update/security.json')
        Add-SmokeCheck ($Name+'_bad_build_recorded_against_replay') ($security.failedBuilds -contains $manifest.buildId)
'@
$replacementSecurity=@'
        $securityPath=Join-Path $stageDirectory '.autumnos-update/security.json'
        $failed=@();if(Test-Path -LiteralPath $securityPath){$failed=@((Read-T05Json $securityPath).failedBuilds)}
        Add-SmokeCheck ($Name+'_temporary_file_occupancy_does_not_falsely_mark_signed_B_bad') ($failed -notcontains $manifest.buildId)
'@
if(-not $text.Contains($security.Replace("`r`n","`n"))){throw 'Rollback security assertion changed; review busy-file derivative.'}
$text=$text.Replace($security.Replace("`r`n","`n"),$replacementSecurity.Replace("`r`n","`n"))
$text=$text.Replace("Add-SmokeCheck (`$Name+'_failed_candidate_not_staged_again') (-not (Find-T05 'RestartSystemUpdate').Current.IsEnabled)","Add-SmokeCheck (`$Name+'_recovered_A_stays_alive_after_explicit_check_without_restart_authorization') (-not `$product.Process.HasExited)")
$text=$text.Replace("'_no_bad_version_update_rollback_loop'","'_busy_recovery_hold_does_not_loop_after_handle_release_and_recheck'")
$text=$text.Replace("'Failed candidate restarted another update transaction.'","'File-busy recovery hold was cleared by a background or explicit check without Restart authorization.'")
$old=@'
    Invoke-T05Scenario 'A-to-B' $BPayloadDirectory $false
    if($BadPayloadDirectory){Invoke-T05Scenario 'A-bad-to-A' ([IO.Path]::GetFullPath($BadPayloadDirectory)) $true}
    else{$report.rollback='not_run_no_bad_payload_supplied'}
'@
$new=@'
    Invoke-T05Scenario 'A-busy-file-abort-to-A' $BPayloadDirectory $true
    $report.busy_file.status='passed_actual_read_handle_aborted_update_A_reopened_data_restored_and_no_automatic_retry_loop'
'@
if(-not $text.Contains($old.Replace("`r`n","`n"))){throw 'Scenario invocation changed; review derivative generator.'}
$text=$text.Replace($old.Replace("`r`n","`n"),$new.Replace("`r`n","`n"))
$tokens=$null;$errors=$null;$null=[Management.Automation.Language.Parser]::ParseInput($text,[ref]$tokens,[ref]$errors)
if($errors.Count){$errors|Out-String|Write-Error;throw 'Busy-file derivative syntax failed.'}
if($text.Contains('$PSScriptRoot')){throw 'Unresolved script-root reference.'}
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
[ordered]@{status='prepared_not_run';source_driver=$source;source_sha256=(Get-FileHash -LiteralPath $source).Hash.ToLowerInvariant();generated_driver=$output;generated_sha256=(Get-FileHash -LiteralPath $output).Hash.ToLowerInvariant();syntax_validation='passed';real_UI_execution='not_run';process_termination=$false;installer_testing='not_run_user_deferred'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $OutputDirectory 'busy-driver-preparation.json') -Encoding utf8
Write-Host $output

