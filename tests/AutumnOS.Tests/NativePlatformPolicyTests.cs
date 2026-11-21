using System.Runtime.InteropServices;
using AutumnOS.NativeHost.Windows;

namespace AutumnOS.Tests;

internal static class NativePlatformPolicyTests
{
#if AUTUMN_PLATFORM_ACCEPTANCE
    private const bool Temporary = true;
#else
    private const bool Temporary = false;
#endif

    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("native.platform.compiled_marker_matches_explicit_variant",
            () => Check(NativePlatformPolicy.VersionRestrictionTemporarilyDisabled, Temporary));
        yield return ("native.platform.windows10_22h2_boundary", () =>
        {
            Check(Allowed(1, 19044), Temporary);
            Check(Allowed(1, 19045), true);
        });
        yield return ("native.platform.windows11_clients_remain_supported", () =>
        {
            foreach (int build in new[] { 22000, 22631, 26100 }) Check(Allowed(1, build), true);
        });
        yield return ("native.platform.server2022_x64_interactive_desktop", () => Check(Allowed(3, 20348), true));
        yield return ("native.platform.other_servers_require_temporary_variant", () =>
        {
            foreach (int build in new[] { 17763, 20347, 20349, 26100 }) Check(Allowed(3, build), Temporary);
        });
        yield return ("native.platform.server2022_domain_controller_with_desktop_experience", () => Check(Allowed(2, 20348), true));
        yield return ("native.platform.server_core_nano_and_unknown_installation_excluded", () =>
        {
            foreach (int type in new[] { 2, 3 })
                foreach (string? install in new[] { "Server Core", "Nano Server", "Client", null })
                    Check(NativePlatformPolicy.IsDesktopAllowed(type, 20348, Architecture.X64, Architecture.X64, true, install), false);
        });
        yield return ("native.platform.unknown_product_types_excluded", () =>
        {
            foreach (int type in new[] { 0, 4 }) Check(Allowed(type, 20348), Temporary);
        });
        yield return ("native.platform.server2022_requires_x64_os", () =>
            Check(NativePlatformPolicy.IsDesktopAllowed(3, 20348, Architecture.Arm64, Architecture.X64, true, "Server"), Temporary));
        yield return ("native.platform.noninteractive_and_non_x64_processes_excluded", () =>
        {
            Check(NativePlatformPolicy.IsDesktopAllowed(3, 20348, Architecture.X64, Architecture.X64, false, "Server"), false);
            foreach (var arch in new[] { Architecture.X86, Architecture.Arm64 })
                Check(NativePlatformPolicy.IsDesktopAllowed(3, 20348, Architecture.X64, arch, true, "Server"), false);
        });
    }

    private static bool Allowed(int type, int build) =>
        NativePlatformPolicy.IsDesktopAllowed(type, build, Architecture.X64, Architecture.X64, true, "Server");

    private static void Check(bool actual, bool expected)
    {
        if (actual != expected) throw new InvalidOperationException($"Expected platform admission {expected}, got {actual}.");
    }
}
