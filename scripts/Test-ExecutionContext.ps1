[CmdletBinding()]
param([Parameter(Mandatory)][string]$ReportPath, [switch]$RequireStandardUser)
$ErrorActionPreference = 'Stop'
if (-not [OperatingSystem]::IsWindows()) { throw 'Windows execution context is required.' }
if (-not ('AutumnOS.Testing.ExecutionToken' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
namespace AutumnOS.Testing {
    public static class ExecutionToken {
        [DllImport("advapi32.dll", SetLastError=true)]
        public static extern bool IsTokenRestricted(SafeAccessTokenHandle token);
        [DllImport("advapi32.dll", SetLastError=true)]
        static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr value, int length, out int required);
        public static int ReadInt(SafeAccessTokenHandle token, int kind) {
            IntPtr data=Marshal.AllocHGlobal(4);
            try {
                int size;
                if(!GetTokenInformation(token,kind,data,4,out size) || size!=4) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return Marshal.ReadInt32(data);
            } finally { Marshal.FreeHGlobal(data); }
        }
        public static uint Integrity(SafeAccessTokenHandle token) {
            int size;
            GetTokenInformation(token,25,IntPtr.Zero,0,out size);
            if(size< IntPtr.Size || size>65536) throw new InvalidOperationException("TOKEN_INTEGRITY_SIZE_INVALID");
            IntPtr data=Marshal.AllocHGlobal(size);
            try {
                if(!GetTokenInformation(token,25,data,size,out size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                string sid=new SecurityIdentifier(Marshal.ReadIntPtr(data)).Value;
                return uint.Parse(sid.Substring(sid.LastIndexOf('-')+1),System.Globalization.CultureInfo.InvariantCulture);
            } finally { Marshal.FreeHGlobal(data); }
        }
    }
}
'@
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent([Security.Principal.TokenAccessLevels]::Query)
try {
    $elevated = [AutumnOS.Testing.ExecutionToken]::ReadInt($identity.AccessToken,20) -ne 0
    $elevationType = [AutumnOS.Testing.ExecutionToken]::ReadInt($identity.AccessToken,18)
    $restricted = [AutumnOS.Testing.ExecutionToken]::IsTokenRestricted($identity.AccessToken)
    $integrity = [AutumnOS.Testing.ExecutionToken]::Integrity($identity.AccessToken)
    $ownerIsUser = $identity.Owner.Value -eq $identity.User.Value
    $reasons = @(
        if ($elevated) { 'elevated_token' }
        if ($restricted) { 'restricted_token' }
        if ($integrity -ne 8192) { 'medium_integrity_required' }
        if (-not $ownerIsUser) { 'default_owner_is_not_user' }
    )
    $report = [ordered]@{
        schema_version=1; executed_utc=[DateTimeOffset]::UtcNow.ToString('o'); os=[Environment]::OSVersion.VersionString
        status=$(if($reasons.Count){'blocked_execution_context'}else{'passed_prerequisites_only'})
        elevated=$elevated; elevation_type=$elevationType; restricted=$restricted; integrity_rid=$integrity
        default_owner_is_user=$ownerIsUser; reasons=$reasons; product_tests='not_run'
        scope='Read-only caller token check. This is not browser, clipboard or product acceptance.'
    }
    $path = [IO.Path]::GetFullPath($ReportPath)
    New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
    $file = [IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read)
    try {
        $bytes=[Text.Encoding]::UTF8.GetBytes(($report|ConvertTo-Json -Depth 5))
        $file.Write($bytes,0,$bytes.Length)
    } finally { $file.Dispose() }
    if ($RequireStandardUser -and $reasons.Count) {
        throw "TEST_EXECUTION_CONTEXT_UNSUPPORTED: $($reasons -join ', '); report: $path. Run from an ordinary non-elevated, unrestricted Windows user session."
    }
    [pscustomobject]$report
} finally { $identity.Dispose() }
