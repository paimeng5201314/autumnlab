[CmdletBinding()]
param()
. "$PSScriptRoot/Common.ps1"
$directory = Join-Path $ProjectRoot 'artifacts/reports'
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$sdkInfo = & $Dotnet --info
$sdkInfo | Set-Content -LiteralPath (Join-Path $directory 'dotnet-info.txt') -Encoding utf8
$os = Get-CimInstance Win32_OperatingSystem
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
$webView = @(Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\*','HKCU:\Software\Microsoft\EdgeUpdate\Clients\*' -ErrorAction SilentlyContinue | Where-Object name -eq 'Microsoft Edge WebView2 Runtime' | Select-Object name,pv)
$report = [ordered]@{
    captured_utc=[DateTimeOffset]::UtcNow.ToString('o'); os=$os.Caption; version=$os.Version; build=$os.BuildNumber; architecture=$os.OSArchitecture
    powershell=$PSVersionTable.PSVersion.ToString(); elevated=$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    sdk_executable=[IO.Path]::GetRelativePath($ProjectRoot,$Dotnet); webview2_runtime=$webView
    installed_app_runtime=@(Get-AppxPackage '*WindowsAppRuntime*' | Select-Object Name,Version,Architecture)
    system_windows_sdk_installed=(Test-Path 'C:\Program Files (x86)\Windows Kits\10\Lib')
    build_strategy='project-local .NET SDK/MSBuild + pinned NuGet Windows SDK BuildTools + self-contained WinAppSDK'
    windows10_test='not_run'; clean_machine_test='not_run'; git='not_applicable'
}
$report | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $directory 'environment.json') -Encoding utf8
$report | ConvertTo-Json -Depth 6
