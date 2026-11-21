using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AutumnOS.NativeHost.Windows;

// Temporary, explicitly selected local acceptance build. Runtime configuration,
// packages and environment variables cannot change this compiled policy.
public static class NativePlatformPolicy
{
#if AUTUMN_PLATFORM_ACCEPTANCE
    public const bool VersionRestrictionTemporarilyDisabled = true;
#else
    public const bool VersionRestrictionTemporarilyDisabled = false;
#endif

    public static bool IsCurrentDesktopAllowed()
    {
        if (!OperatingSystem.IsWindows()) return false;
        var os = new WindowsVersion { Size = Marshal.SizeOf<WindowsVersion>(), ServicePack = "" };
        if (RtlGetVersion(ref os) != 0) return false;
        string? installationType = null;
        if (os.ProductType is 2 or 3)
        {
            try
            {
                installationType = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion",
                    "InstallationType", null) as string;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return false;
            }
        }
        return IsDesktopAllowed(os.ProductType, os.Build, RuntimeInformation.OSArchitecture,
            RuntimeInformation.ProcessArchitecture, Environment.UserInteractive, installationType);
    }

    public static bool IsDesktopAllowed(int productType, int buildNumber, Architecture osArchitecture,
        Architecture processArchitecture, bool interactive, string? installationType)
    {
        if (!interactive || processArchitecture != Architecture.X64) return false;
        const int workstation = 1, domainController = 2, server = 3;
        bool isServer = productType is domainController or server;
        if (isServer && !string.Equals(installationType, "Server", StringComparison.OrdinalIgnoreCase)) return false;
        return VersionRestrictionTemporarilyDisabled || productType == workstation && buildNumber >= 19045 ||
            isServer && buildNumber == 20348 && osArchitecture == Architecture.X64;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowsVersion
    {
        internal int Size, Major, Minor, Build, Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string ServicePack;
        internal ushort ServicePackMajor, ServicePackMinor, SuiteMask;
        internal byte ProductType, Reserved;
    }
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int RtlGetVersion(ref WindowsVersion version);
}
