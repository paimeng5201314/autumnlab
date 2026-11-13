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
internal sealed record NativePayloadCatalog(int SchemaVersion, string BuildId, string SourceSnapshotId, string RuntimePolicy,
    string? AcceptanceRoot, NativeHostFileBinding[] Host, NativeHostFileBinding[] Runtime)
{
    internal static NativePayloadCatalog ForLaunch(InstallationRoot root)
    {
        using var data = typeof(NativePayloadCatalog).Assembly.GetManifestResourceStream("AutumnOS.NativePayloadTable");
        var table = data is { Length: > 0 and <= 4 * 1024 * 1024 }
            ? JsonSerializer.Deserialize<NativePayloadCatalog>(data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) : null;
        if (table is not { SchemaVersion: 2, RuntimePolicy: "autumn-native-owned-v1", Host.Length: > 0, Runtime.Length: > 0 } ||
            table.BuildId != BrandInfo.BuildId || table.SourceSnapshotId != BrandInfo.SourceSnapshotId)
            throw new PackageException("RUNTIME_NATIVE_CATALOG_INVALID");
        // Support portable/installed layouts using the running build's identity, not a developer
        // drive name. The receipt cannot alter the compiled catalog or grant execution on its own:
        // RemoteApplicationView still pins all files, verifies the original isolated processes and
        // keeps the document parser blocked until the per-instance native challenge succeeds.
        string program = NativeHostLaunchManifest.PlainAbsolute(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root.ProgramDirectory)), true);
        string current = NativeHostLaunchManifest.PlainAbsolute(Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory)), true);
        if (!program.Equals(current, StringComparison.OrdinalIgnoreCase))
            throw new PackageException("RUNTIME_NATIVE_INSTALLATION_IDENTITY_INVALID");
        for (string? ancestor = program; ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if ((File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0)
                throw new PackageException("RUNTIME_NATIVE_INSTALLATION_IDENTITY_INVALID");
        string receiptPath = NativeHostLaunchManifest.PlainAbsolute(Path.Combine(program, "autumn.install.json"), false);
        using (var receipt = new FileStream(receiptPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            if (receipt.Length is < 1 or > NativeDeploymentIdentity.MaximumReceiptBytes)
                throw new PackageException("RUNTIME_NATIVE_INSTALLATION_IDENTITY_INVALID");
            var bytes = new byte[(int)receipt.Length]; receipt.ReadExactly(bytes);
            NativeDeploymentIdentity.ValidateReceipt(bytes, BrandInfo.BuildId, BrandInfo.Version);
        }
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
