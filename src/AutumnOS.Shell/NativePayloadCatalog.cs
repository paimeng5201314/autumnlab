using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.NativeHost.Windows;
using AutumnOS.Packages;
using AutumnOS.Storage;

namespace AutumnOS.Shell;

// Compiled from the fresh native publish and the fixed runtime at build time. An installed
// package, JSON in the data directory, command line or environment cannot grant eligibility.
internal sealed record NativePayloadCatalog(string? AcceptanceRoot, NativeHostFileBinding[] Host, NativeHostFileBinding[] Runtime)
{
    internal static NativePayloadCatalog ForLaunch(InstallationRoot root)
    {
        using var data = typeof(NativePayloadCatalog).Assembly.GetManifestResourceStream("AutumnOS.NativePayloadTable");
        var table = data is { Length: > 0 and <= 4 * 1024 * 1024 }
            ? JsonSerializer.Deserialize<NativePayloadCatalog>(data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
        // Before the full ordinary-user matrix is certified, only the exact private acceptance
        // installation built for this run can exercise the production route with third-party bytes.
        string actual = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.InstallationDirectory));
        // A private candidate can also be unpacked without the developer's drive/path on a
        // target workstation. This is a separate per-user installation, never the normal data root.
        string target = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AutumnOS-Acceptance", BrandInfo.BuildId);
        if (table?.AcceptanceRoot is not { Length: > 0 } path ||
            !(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).Equals(actual, StringComparison.OrdinalIgnoreCase) ||
              target.Equals(actual, StringComparison.OrdinalIgnoreCase)))
            throw new PackageException("RUNTIME_CLIPBOARD_BOUNDARY_UNAVAILABLE");
        var version = new OsVersion { Size = Marshal.SizeOf<OsVersion>(), ServicePack = "" };
        if (RtlGetVersion(ref version) != 0 || !NativePlatformPolicy.IsVersionAllowed(version.ProductType, version.Build) ||
            !Environment.UserInteractive || !Environment.Is64BitProcess)
            throw new PackageException("RUNTIME_NATIVE_DESKTOP_REQUIRED");
        using var user = WindowsIdentity.GetCurrent();
        if (user.User is null || user.Owner != user.User || new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))
            throw new PackageException("RUNTIME_STANDARD_USER_REQUIRED");
        return table;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersion
    {
        internal int Size, Major, Minor, Build, Platform;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] internal string ServicePack;
        internal ushort ServiceMajor, ServiceMinor, Suite;
        internal byte ProductType, Reserved;
    }
    [DllImport("ntdll.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int RtlGetVersion(ref OsVersion version);
}
