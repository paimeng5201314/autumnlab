[CmdletBinding()]
param([string]$PeerExecutablePath,[ValidateSet('T03','T04','T05')][string]$Checkpoint='T03',
    [ValidateSet('Prepare','Finalize','Full')][string]$Stage='Full')
if ($Checkpoint -eq 'T04') { & "$PSScriptRoot/Package-T04.ps1" -Stage $Stage; return }
if ($Checkpoint -eq 'T05') { & "$PSScriptRoot/Package-T05.ps1" -Stage $Stage; return }
. "$PSScriptRoot/Common.ps1"
Push-Location $ProjectRoot
try {
    if (-not (Test-Path -LiteralPath $PeerExecutablePath -PathType Leaf)) { throw 'A separately compiled supported protocol peer is required; run Build-ProtocolPeer.ps1 before the main Build.ps1.' }
    $existing = @(Get-Process -Name AutumnOS -ErrorAction SilentlyContinue | Where-Object { $_.SessionId -eq [Diagnostics.Process]::GetCurrentProcess().SessionId })
    if ($existing.Count) { throw 'Existing AutumnOS processes may be user windows. Ask the user to exit normally before package UI tests; no process was ended.' }
    $buildId = (Get-Content -LiteralPath 'artifacts/latest-build.txt' -Raw).Trim()
    $buildDirectory = Join-Path $ProjectRoot "artifacts/builds/$buildId"
    $build = Get-Content -LiteralPath (Join-Path $buildDirectory 'build-result.json') -Raw | ConvertFrom-Json
    $tests = Get-Content -LiteralPath (Join-Path $buildDirectory 'tests.json') -Raw | ConvertFrom-Json
    $sdkTests = Get-Content -LiteralPath (Join-Path $buildDirectory 'sdk-tests.json') -Raw | ConvertFrom-Json
    $sampleTests = Get-Content -LiteralPath (Join-Path $buildDirectory 'sample-tests.json') -Raw | ConvertFrom-Json
    $nativeTests = Get-Content -LiteralPath (Join-Path $buildDirectory 'native-client-tests.json') -Raw | ConvertFrom-Json
    $serverTests = Get-Content -LiteralPath (Join-Path $buildDirectory 'server-http-tests.json') -Raw | ConvertFrom-Json
    if ($build.status -ne 'passed' -or $tests.status -ne 'passed' -or $tests.build_id -ne $buildId -or $tests.source_snapshot_id -ne $build.source_snapshot_id -or
        $sdkTests.status -ne 'passed' -or $sdkTests.build_id -ne $buildId -or $sdkTests.source_snapshot_id -ne $build.source_snapshot_id) {
        throw 'A passing tracked build and matching tests are required.'
    }
    if ($sampleTests.status -ne 'passed' -or $sampleTests.build_id -ne $buildId -or $sampleTests.source_snapshot_id -ne $build.source_snapshot_id) { throw 'Matching T03 sample contract tests are required.' }
    foreach ($sampleReport in @($nativeTests, $serverTests)) {
        if ($sampleReport.status -ne 'passed' -or $sampleReport.build_id -ne $buildId -or $sampleReport.source_snapshot_id -ne $build.source_snapshot_id) { throw 'Matching independent identity sample tests are required.' }
    }
    $snapshot = & "$PSScriptRoot/New-SourceSnapshot.ps1" -OutputDirectory (Join-Path $buildDirectory 'before-package')
    if ($snapshot -ne $build.source_snapshot_id) { throw 'Sources changed. Build and test again.' }
    $artifactHashes = Get-Content -LiteralPath (Join-Path $buildDirectory 'artifact-hashes.json') -Raw | ConvertFrom-Json
    foreach ($artifact in $artifactHashes) {
        $file = Join-Path $ProjectRoot $artifact.path
        if (-not (Test-Path -LiteralPath $file) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -ne $artifact.sha256) {
            throw "Tracked build output changed: $($artifact.path). Build and test with a new ID."
        }
    }
    [xml]$brand = Get-Content -LiteralPath 'build/Brand.props' -Raw
    $version = $brand.Project.PropertyGroup.AutumnVersion
    $destination = Join-Path $ProjectRoot "artifacts/packages/$buildId/AutumnOS-$version-win-x64-development"
    if (Test-Path -LiteralPath $destination) { throw 'Package directory already exists; use a new build ID to preserve evidence.' }
    New-Item -ItemType Directory -Force -Path $destination | Out-Null
    Invoke-Dotnet -Arguments @('publish','src/AutumnOS.Shell/AutumnOS.Shell.csproj','-c','Release','-p:Platform=x64',
        '--no-build','--no-restore',"-p:BuildId=$buildId","-p:SourceSnapshotId=$snapshot",'-o',$destination) -LogPath (Join-Path $buildDirectory 'publish.log')
    if (-not (Test-Path -LiteralPath (Join-Path $destination 'AutumnOS.exe'))) { throw 'Publish did not produce AutumnOS.exe.' }
    if (Test-Path -LiteralPath (Join-Path $destination 'AutumnOS_Data')) { throw 'User data must not be included in development packages.' }
    $builtRoot = Join-Path $ProjectRoot (Split-Path $build.output_path)
    # WinUI generates these items during Build. A separate publish --no-build
    # invocation does not reconstruct all XAML resource items; deploy the actual
    # tracked resources without recompiling or changing their bytes.
    $resourceHashes = @($artifactHashes | Where-Object { [IO.Path]::GetExtension($_.path) -in @('.pri','.xbf') })
    foreach ($resource in $resourceHashes) {
        $builtFile = Join-Path $ProjectRoot $resource.path
        $relative = [IO.Path]::GetRelativePath($builtRoot,$builtFile)
        if ($relative -eq '..' -or $relative.StartsWith('..' + [IO.Path]::DirectorySeparatorChar) -or [IO.Path]::IsPathRooted($relative)) {
            throw 'Tracked WinUI resource is outside the shell output directory.'
        }
        $targetFile = Join-Path $destination $relative
        New-Item -ItemType Directory -Force -Path (Split-Path $targetFile -Parent) | Out-Null
        Copy-Item -LiteralPath $builtFile -Destination $targetFile
    }
    foreach ($requiredResource in @('AutumnOS.pri','App.xbf','MainWindow.xbf')) {
        if (-not (Test-Path -LiteralPath (Join-Path $destination $requiredResource) -PathType Leaf)) {
            throw "Missing required WinUI resource: $requiredResource"
        }
    }
    foreach ($file in (Get-ChildItem -LiteralPath $destination -Recurse -File)) {
        $relative = [IO.Path]::GetRelativePath($destination,$file.FullName)
        $builtFile = Join-Path $builtRoot $relative
        if (-not (Test-Path -LiteralPath $builtFile) -or (Get-FileHash -LiteralPath $file.FullName).Hash -ne (Get-FileHash -LiteralPath $builtFile).Hash) {
            throw "Published bytes differ from tracked build: $relative"
        }
    }
    $licenseDirectory = Join-Path $destination 'ThirdPartyLicenses'
    New-Item -ItemType Directory -Force -Path $licenseDirectory | Out-Null
    Get-ChildItem -LiteralPath $env:NUGET_PACKAGES -Directory | ForEach-Object {
        $packageId = $_.Name
        Get-ChildItem -LiteralPath $_.FullName -Directory | ForEach-Object {
            $packageVersion = $_.Name
            Get-ChildItem -LiteralPath $_.FullName -File | Where-Object Name -Match '^(license|third.?party|notice)' | ForEach-Object {
                Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $licenseDirectory "$packageId-$packageVersion-$($_.Name)")
            }
        }
    }
    Copy-Item -LiteralPath (Join-Path $ProjectRoot '.tools/dotnet/LICENSE.txt') -Destination (Join-Path $licenseDirectory 'dotnet-LICENSE.txt')
    Copy-Item -LiteralPath (Join-Path $ProjectRoot '.tools/dotnet/ThirdPartyNotices.txt') -Destination (Join-Path $licenseDirectory 'dotnet-ThirdPartyNotices.txt')
    Copy-Item -LiteralPath 'docs/dependencies.md' -Destination (Join-Path $licenseDirectory 'dependencies.md')
    @"
$($brand.Project.PropertyGroup.AutumnProductName)
$($brand.Project.PropertyGroup.AutumnProducerCredit)

LOCAL DEVELOPMENT BUILD — NOT A COMPLETE PRODUCT OR APPROVED RELEASE CANDIDATE
Build: $buildId
Source: $snapshot

Double-click AutumnOS.exe. Keep the complete directory together.
AutumnOS_Data is created beside the executable and existing data is preserved.
.NET and Windows App SDK are self-contained. The installed WebView2 Runtime
is inspected separately; offline/clean-machine runtime delivery is not yet verified.
The desktop checkpoint includes first-run screens, installed element-pairs,
long-press menus, Dock and saved theme/wallpaper. Settings has a two-column
category layout. Single-click starts an inactive game; double-click a running
game icon for Continue/End. Long-press and right-click offer the same actions.
Compatible v1 launcher copies share one main instance per Windows user/session.
Launching again recalls the original window and keeps its version, data and game.
Legacy builds without this protocol cannot participate. A forwarding timeout
does not launch a second instance. Closing the window still exits the program.
This T03 checkpoint adds host-owned browser PKCE sign-in, protected sessions,
per-application permissions and isolated data, desktop SDK and local developer tools.
Real-user provider sign-in/refresh/logout acceptance is separate from local tests.
See AUTUMNOS_PROGRESS.md in the local source workspace for exact evidence and blockers.
General third-party isolation, full desktop interactions, store, safe updates and installer remain pending.
Authenticode: not signed. Installer EXE: not_run. Public release: not performed.
"@ | Set-Content -LiteralPath (Join-Path $destination 'READ-ME.txt') -Encoding utf8
    # Test the directory being delivered, not only the original build output.
    # The smoke script makes an isolated copy, so the bundle contains no test data.
    $smokeDirectory = Join-Path $buildDirectory 'package-smoke'
    & "$PSScriptRoot/Test-DesktopInteractionSmoke.ps1" -ExecutablePath (Join-Path $destination 'AutumnOS.exe') -ReportDirectory $smokeDirectory
    $smoke = Get-Content -LiteralPath (Join-Path $smokeDirectory 'windows-smoke.json') -Raw | ConvertFrom-Json
    if ($smoke.status -ne 'passed' -or $smoke.build_id -ne $buildId -or $smoke.source_snapshot_id -ne $snapshot -or
        $smoke.internal_application -ne 'passed' -or $smoke.standard_user_window -ne 'passed' -or
        $smoke.desktop_checkpoint -ne 'passed' -or $smoke.appearance_persistence -ne 'passed' -or $smoke.same_instance_resume -ne 'passed' -or
        $smoke.settings_navigation -ne 'passed' -or $smoke.background_single_click -ne 'passed' -or
        $smoke.background_double_click -ne 'passed' -or $smoke.background_markers -ne 'passed' -or
        $smoke.long_press -ne 'passed' -or $smoke.right_click_menu -ne 'passed') {
        throw 'Delivered package must pass matching native-window and internal-application smoke tests.'
    }
    if (Test-Path -LiteralPath (Join-Path $destination 'AutumnOS_Data')) { throw 'Package smoke contaminated the bundle with user data.' }
    $singleDirectory = Join-Path $buildDirectory 'package-single-instance'
    & "$PSScriptRoot/Test-SingleInstanceSmoke.ps1" -ExecutablePath (Join-Path $destination 'AutumnOS.exe') -PeerExecutablePath $PeerExecutablePath -ReportDirectory $singleDirectory
    $single = Get-Content -LiteralPath (Join-Path $singleDirectory 'single-instance-smoke.json') -Raw | ConvertFrom-Json
    if ($single.status -ne 'passed' -or $single.build_id -ne $buildId -or $single.source_snapshot_id -ne $snapshot -or
        $single.peer_source_snapshot_id -ne $snapshot -or $single.primary_version -eq $single.peer_version -or
        $single.source_executable_sha256 -ne (Get-FileHash -LiteralPath (Join-Path $destination 'AutumnOS.exe') -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'Delivered single-instance evidence must match this EXE, source snapshot, and a distinct compiled peer version.'
    }
    foreach ($gate in @('standard_user_window','foreground_activation','normal_recall','minimized_recall','maximized_recall',
        'foreground_game_preservation','background_game_preservation','multiple_working_directories','shortcut_launch','cross_copy_recall',
        'concurrent_cold_start','clean_exit_restart','crash_restart','unresponsive_primary','forwarding_failure_no_second_root',
        'resource_failure','source_data_preserved','distinct_build_versions','transport_recovery')) {
        if ($single.$gate -ne 'passed') { throw "Delivered single-instance check missing or failed: $gate" }
    }
    if (Test-Path -LiteralPath (Join-Path $destination 'AutumnOS_Data')) { throw 'Single-instance smoke contaminated the bundle with user data.' }
    $accountDirectory = Join-Path $buildDirectory 'package-account'
    & "$PSScriptRoot/Test-T03AccountSmoke.ps1" -ExecutablePath (Join-Path $destination 'AutumnOS.exe') -ReportDirectory $accountDirectory
    $account = Get-Content -LiteralPath (Join-Path $accountDirectory 'account-smoke.json') -Raw | ConvertFrom-Json
    if ($account.status -ne 'passed' -or $account.cleanup -ne 'passed' -or $account.build_id -ne $buildId -or
        $account.source_snapshot_id -ne $snapshot -or $account.source_executable_sha256 -ne $single.source_executable_sha256) {
        throw 'T03 account UI, loopback cancellation and login single-instance checks must match the delivery EXE.'
    }
    $featuresDirectory = Join-Path $buildDirectory 'package-t03-features'
    & "$PSScriptRoot/Test-T03FeaturesSmoke.ps1" -ExecutablePath (Join-Path $destination 'AutumnOS.exe') -ReportDirectory $featuresDirectory
    $features = Get-Content -LiteralPath (Join-Path $featuresDirectory 'features-smoke.json') -Raw | ConvertFrom-Json
    if ($features.status -ne 'passed' -or $features.cleanup -ne 'passed' -or $features.build_id -ne $buildId -or
        $features.source_snapshot_id -ne $snapshot -or $features.source_executable_sha256 -ne $single.source_executable_sha256) {
        throw 'T03 real permissions, desktop extensions and developer UI must pass against this delivery EXE.'
    }
    # Also execute this exact delivery path before ZIP creation. Keep the runtime's
    # own data in place; exclude it from the archive without deleting any files.
    $directDirectory = Join-Path $buildDirectory 'delivered-executable'
    & "$PSScriptRoot/Test-DeliveredLauncher.ps1" -ExecutablePath (Join-Path $destination 'AutumnOS.exe') -ReportDirectory $directDirectory
    $direct = Get-Content -LiteralPath (Join-Path $directDirectory 'delivered-launcher.json') -Raw | ConvertFrom-Json
    if ($direct.status -ne 'passed' -or $direct.binary_after.metadata.BuildId -ne $buildId -or
        $direct.binary_after.metadata.SourceSnapshotId -ne $snapshot -or
        $direct.binary_after.executable_sha256 -ne $single.source_executable_sha256 -or $direct.remaining_processes.Count -ne 0) {
        throw 'The actual final-path EXE must pass in-place launcher verification before creating the ZIP.'
    }
    foreach ($gate in @('native_window','repeat_launch','different_working_directory','normal_exit')) {
        if ($direct.$gate -ne 'passed') { throw "Final-path EXE gate missing or failed: $gate" }
    }
    $zipPath = "$destination.zip"
    $archive = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $destination -Recurse -File) {
            $relative = [IO.Path]::GetRelativePath($destination,$file.FullName).Replace('\','/')
            if ($relative.StartsWith('AutumnOS_Data/',[StringComparison]::OrdinalIgnoreCase)) { continue }
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,$file.FullName,$relative,[IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
    $report = [ordered]@{ build_id=$buildId; source_snapshot_id=$snapshot; kind='local_development_bundle'; release_candidate=$false;
        zip=[IO.Path]::GetRelativePath($ProjectRoot,$zipPath); sha256=(Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant();
        bytes=(Get-Item -LiteralPath $zipPath).Length; package_windows_smoke='passed'; package_smoke_report=[IO.Path]::GetRelativePath($ProjectRoot,(Join-Path $smokeDirectory 'windows-smoke.json'));
        package_single_instance='passed'; single_instance_report=[IO.Path]::GetRelativePath($ProjectRoot,(Join-Path $singleDirectory 'single-instance-smoke.json'));
        peer_version=$single.peer_version; peer_build_id=$single.peer_build_id;
        account_ui='passed'; account_report=[IO.Path]::GetRelativePath($ProjectRoot,(Join-Path $accountDirectory 'account-smoke.json'));
        t03_features='passed'; t03_features_report=[IO.Path]::GetRelativePath($ProjectRoot,(Join-Path $featuresDirectory 'features-smoke.json'));
        real_provider_login='not_run'; real_provider_refresh='not_run'; real_provider_logout='not_run'; complete_t03='not_verified';
        delivered_executable='passed'; delivered_executable_report=[IO.Path]::GetRelativePath($ProjectRoot,(Join-Path $directDirectory 'delivered-launcher.json'));
        runtime_data='Retained beside tested EXE; excluded from ZIP without deletion';
        winui_resource_files=$resourceHashes.Count; installer='not_run'; public_release=$false; clean_machine_test='not_run' }
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $buildDirectory 'package-result.json') -Encoding utf8
    $report | ConvertTo-Json -Depth 5
} catch {
    if ($buildDirectory) {
        [ordered]@{status='failed';build_id=$buildId;failure=$_.Exception.Message;release_candidate=$false;public_release=$false} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $buildDirectory 'package-result.json') -Encoding utf8
    }
    throw
} finally { Pop-Location }
