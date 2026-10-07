#requires -Version 7.6
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidatePattern('^T06-[A-Za-z0-9][A-Za-z0-9_.-]{0,86}$')][string]$BuildId,
    [Parameter(Mandatory)][string]$BuildVersion,
    [Parameter(Mandatory)][string]$ReleaseLabel
)
$ErrorActionPreference = 'Stop'
if (-not [OperatingSystem]::IsWindows() -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'This account bootstrap is restricted to disposable GitHub-hosted Windows runners. Local builds use Build-SingleFile.ps1 as a standard user.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
try {
    if (-not ([Security.Principal.WindowsPrincipal]::new($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw 'The hosted bootstrap requires administrator privileges to create its own temporary standard account.'
    }
} finally { $identity.Dispose() }
$repo = Split-Path $PSScriptRoot -Parent
$dotnet = Join-Path $repo '.tools/dotnet/dotnet.exe'
$pwshAssembly = Join-Path $repo '.tools/pwsh-ci/.store/powershell/7.6.6/powershell/7.6.6/tools/net10.0/any/win/pwsh.dll'
$node = Get-Command node -CommandType Application -ErrorAction Stop | Select-Object -First 1
foreach ($file in @($dotnet, $pwshAssembly, (Join-Path $repo 'scripts/Build-SingleFile.ps1'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Pinned build prerequisite missing: $file" }
}
$evidence = Join-Path $repo "artifacts/actions-setup/$BuildId/standard-user"
if (Test-Path -LiteralPath $evidence) { throw 'Existing standard-user evidence is preserved; use a new build ID.' }
New-Item -ItemType Directory -Path $evidence | Out-Null
$stdoutPath = Join-Path $evidence 'stdout.log'
$stderrPath = Join-Path $evidence 'stderr.log'
$bootstrap = Join-Path $evidence 'run-build.ps1'
@'
param([string]$Repo,[string]$ExpectedSid,[string]$BuildId,[string]$BuildVersion,[string]$ReleaseLabel)
$ErrorActionPreference='Stop'
try {
    $identity=[Security.Principal.WindowsIdentity]::GetCurrent()
    try {
        $principal=[Security.Principal.WindowsPrincipal]::new($identity)
        if($identity.User.Value -ne $ExpectedSid -or $identity.Owner.Value -ne $identity.User.Value -or
            $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
            throw 'The build must run under its own standard-user SID with Owner equal to User.'
        }
        Write-Output ('Standard-user identity verified: user='+$identity.User.Value+'; owner='+$identity.Owner.Value)
    } finally { $identity.Dispose() }
    Set-Location -LiteralPath $Repo
    & (Join-Path $Repo 'scripts/Build-SingleFile.ps1') -BuildId $BuildId -BuildVersion $BuildVersion -ReleaseLabel $ReleaseLabel -SkipNativeSmoke
    exit 0
} catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
'@ | Set-Content -LiteralPath $bootstrap -Encoding utf8NoBOM

# CreateProfile obtains the actual Windows profile path before constructing the child's
# environment. LoadUserProfile then loads its hive for CurrentUser DPAPI and identity tests.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class AutumnCiProfile {
    [DllImport("userenv.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern int CreateProfile(string sid, string name, StringBuilder path, uint length);
}
'@
$account = $null
$process = $null
$stdout = $null
$stderr = $null
$stdoutCopy = $null
$stderrCopy = $null
$password = [Security.SecureString]::new()
$accountName = 'autumn' + [Guid]::NewGuid().ToString('N').Substring(0, 12)
$randomPassword = 'Aa1!' + [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(36))
foreach ($character in $randomPassword.ToCharArray()) { $password.AppendChar($character) }
$password.MakeReadOnly()
$randomPassword = $null
$changedAncestors = [Collections.Generic.List[string]]::new()
$report = [ordered]@{ status='starting'; build_id=$BuildId; user_sid=$null; owner_requirement='same_as_user'; native_tests='not_run_ci_no_interactive_desktop'; process_exit_code=$null; created_user_removed=$false }
$failure = $null
function Invoke-CiAcl([string[]]$Arguments) {
    & (Join-Path $env:WINDIR 'System32/icacls.exe') @Arguments | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not grant the dedicated standard account access to its build inputs.' }
}
try {
    $account = New-LocalUser -Name $accountName -Password $password -AccountNeverExpires -PasswordNeverExpires -Description 'Temporary AutumnOS build account on a disposable hosted runner'
    $sid = $account.SID.Value
    $report.user_sid = $sid
    $usersGroup = Get-LocalGroup -SID 'S-1-5-32-545'
    try { Add-LocalGroupMember -Group $usersGroup -Member $account }
    catch { if ($_.FullyQualifiedErrorId -notlike '*MemberExists*') { throw } }
    $profileBuffer = [Text.StringBuilder]::new(1024)
    $profileResult = [AutumnCiProfile]::CreateProfile($sid, $accountName, $profileBuffer, 1024)
    if ($profileResult -ne 0) { throw ('Could not create the temporary Windows profile: HRESULT 0x' + $profileResult.ToString('X8')) }
    $profilePath = $profileBuffer.ToString()
    if (-not $profilePath -or -not (Test-Path -LiteralPath $profilePath -PathType Container)) { throw 'The temporary Windows profile was not created.' }
    # Grant only this unique SID. Existing Users/Everyone rules and product-created ACLs
    # are not rewritten. Ancestors need read/traverse for managed-path validation.
    for ($cursor=[IO.Path]::GetDirectoryName($repo); $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
        Invoke-CiAcl @($cursor, '/grant', "*${sid}:(RX)", '/Q')
        $changedAncestors.Add($cursor)
    }
    Invoke-CiAcl @($repo, '/grant', "*${sid}:(OI)(CI)M", '/T', '/Q')
    $nodeDirectory = Split-Path $node.Source -Parent
    for ($cursor=[IO.Path]::GetDirectoryName($nodeDirectory); $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
        Invoke-CiAcl @($cursor, '/grant', "*${sid}:(RX)", '/Q')
        $changedAncestors.Add($cursor)
    }
    Invoke-CiAcl @($nodeDirectory, '/grant', "*${sid}:(OI)(CI)RX", '/Q')
    $taskTemp = Join-Path $profilePath 'AppData/Local/Temp/AutumnBuild'
    New-Item -ItemType Directory -Force -Path $taskTemp | Out-Null
    Invoke-CiAcl @($taskTemp, '/grant', "*${sid}:(OI)(CI)M", '/Q')

    $start = [Diagnostics.ProcessStartInfo]::new($dotnet)
    $start.UseShellExecute = $false
    $start.UserName = $accountName
    $start.Domain = $env:COMPUTERNAME
    $start.Password = $password
    $start.LoadUserProfile = $true
    $start.WorkingDirectory = $repo
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in @($pwshAssembly, '-NoLogo', '-NoProfile', '-NonInteractive', '-File', $bootstrap,
        '-Repo', $repo, '-ExpectedSid', $sid, '-BuildId', $BuildId, '-BuildVersion', $BuildVersion, '-ReleaseLabel', $ReleaseLabel)) {
        $start.ArgumentList.Add($argument)
    }
    # A credentialed child must not inherit runner-admin TEMP/USERPROFILE or authentication
    # variables. Supply only OS/toolchain values and its actual newly created profile.
    $start.Environment.Clear()
    foreach ($name in @('SystemRoot', 'WINDIR', 'SystemDrive', 'ComSpec', 'PATHEXT', 'COMPUTERNAME', 'NUMBER_OF_PROCESSORS', 'PROCESSOR_ARCHITECTURE',
        'ProgramFiles', 'ProgramFiles(x86)', 'ProgramW6432', 'ProgramData', 'PUBLIC', 'ALLUSERSPROFILE')) {
        $value = [Environment]::GetEnvironmentVariable($name)
        if ($value) { $start.Environment[$name] = $value }
    }
    $start.Environment['USERPROFILE'] = $profilePath
    $start.Environment['USERNAME'] = $accountName
    $start.Environment['USERDOMAIN'] = $env:COMPUTERNAME
    $start.Environment['HOMEDRIVE'] = [IO.Path]::GetPathRoot($profilePath).TrimEnd('\')
    $start.Environment['HOMEPATH'] = $profilePath.Substring([IO.Path]::GetPathRoot($profilePath).Length - 1)
    $start.Environment['APPDATA'] = Join-Path $profilePath 'AppData/Roaming'
    $start.Environment['LOCALAPPDATA'] = Join-Path $profilePath 'AppData/Local'
    $start.Environment['TEMP'] = $taskTemp
    $start.Environment['TMP'] = $taskTemp
    $start.Environment['DOTNET_ROOT'] = Split-Path $dotnet -Parent
    $start.Environment['DOTNET_ROOT_X64'] = Split-Path $dotnet -Parent
    $start.Environment['DOTNET_CLI_HOME'] = Join-Path $repo '.tools/dotnet-home'
    $start.Environment['NUGET_PACKAGES'] = Join-Path $repo '.tools/nuget/packages'
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['DOTNET_NOLOGO'] = '1'
    $start.Environment['POWERSHELL_TELEMETRY_OPTOUT'] = '1'
    $start.Environment['PATH'] = @((Split-Path $dotnet -Parent), (Split-Path $node.Source -Parent),
        (Join-Path $env:WINDIR 'System32'), $env:WINDIR, (Join-Path $env:WINDIR 'System32/Wbem'),
        (Join-Path $env:WINDIR 'System32/WindowsPowerShell/v1.0')) -join [IO.Path]::PathSeparator
    $stdout = [IO.File]::Open($stdoutPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $stderr = [IO.File]::Open($stderrPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    $process = [Diagnostics.Process]::Start($start)
    # Drain both streams concurrently to files so a verbose build cannot deadlock.
    $stdoutCopy = $process.StandardOutput.BaseStream.CopyToAsync($stdout)
    $stderrCopy = $process.StandardError.BaseStream.CopyToAsync($stderr)
    if (-not $process.WaitForExit(80 * 60 * 1000)) {
        $process.Kill($true)
        $process.WaitForExit()
        throw 'The owned standard-user build exceeded 80 minutes; its process tree was stopped.'
    }
    $stdoutCopy.GetAwaiter().GetResult()
    $stderrCopy.GetAwaiter().GetResult()
    $report.process_exit_code = $process.ExitCode
    if ($process.ExitCode -ne 0) { throw "Standard-user build failed with exit code $($process.ExitCode); inspect the retained stdout/stderr and pipeline reports." }
    $report.status = 'packaged_native_not_run'
} catch {
    $report.status = 'failed'
    $failure = $_.Exception.Message
    $report.failure = $failure
} finally {
    if ($process) {
        if (-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        foreach ($copy in @($stdoutCopy, $stderrCopy)) {
            if ($copy) { try { $copy.GetAwaiter().GetResult() } catch { } }
        }
        $process.Dispose()
    }
    if ($stdout) { $stdout.Dispose() }
    if ($stderr) { $stderr.Dispose() }
    if ($account) {
        try { Remove-LocalUser -SID $account.SID -ErrorAction Stop; $report.created_user_removed=$true }
        catch { $report.status='failed'; $report.cleanup_failure='Could not remove the temporary build account.'; if (-not $failure) { $failure=$report.cleanup_failure } }
    }
    $password.Dispose()
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $evidence 'identity-run.json') -Encoding utf8NoBOM
}
if ($failure) {
    foreach ($log in @($stdoutPath, $stderrPath)) {
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 45 | Write-Host }
    }
    throw $failure
}
Write-Host 'Standard-user build completed; interactive native smoke remains not_run_ci_no_interactive_desktop.'
