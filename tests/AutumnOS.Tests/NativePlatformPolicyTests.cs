using AutumnOS.NativeHost.Windows;

namespace AutumnOS.Tests;

// Pure build-variant and compatibility-boundary checks. They do not create a
// process or prove that an admitted OS supports the native isolation chain.
internal static class NativePlatformPolicyTests
{
#if AUTUMN_PLATFORM_ACCEPTANCE
    private static readonly bool ExpectedTemporary = true;
#else
    private static readonly bool ExpectedTemporary = false;
#endif

    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("native.platform.compiled_marker_matches_explicit_variant", CompiledMarker);
        yield return ("native.platform.windows10_22h2_boundary", Windows10Boundary);
        yield return ("native.platform.windows11_clients_remain_supported", Windows11Clients);
        yield return ("native.platform.server20348_requires_temporary_variant", Server);
        yield return ("native.platform.domain_controller_requires_temporary_variant", DomainController);
    }

    private static void CompiledMarker() => Check(
        NativePlatformPolicy.VersionRestrictionTemporarilyDisabled, ExpectedTemporary, "compiled variant");

    private static void Windows10Boundary()
    {
        Check(NativePlatformPolicy.IsVersionAllowed(1, 19044), ExpectedTemporary, "Windows 10 before 22H2");
        Check(NativePlatformPolicy.IsVersionAllowed(1, 19045), true, "Windows 10 22H2");
    }

    private static void Windows11Clients()
    {
        foreach (int build in new[] { 22000, 22631, 26100 })
            Check(NativePlatformPolicy.IsVersionAllowed(1, build), true, "Windows 11 " + build);
    }

    private static void Server() => Check(
        NativePlatformPolicy.IsVersionAllowed(3, 20348), ExpectedTemporary, "Windows Server 20348");

    private static void DomainController() => Check(
        NativePlatformPolicy.IsVersionAllowed(2, 20348), ExpectedTemporary, "domain-controller product type");

    private static void Check(bool actual, bool expected, string scenario)
    {
        if (actual != expected)
            throw new InvalidOperationException($"Native platform policy mismatch for {scenario}: expected {expected}, got {actual}.");
    }
}
