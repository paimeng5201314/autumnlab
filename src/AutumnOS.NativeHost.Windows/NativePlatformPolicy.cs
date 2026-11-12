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

    public static bool IsVersionAllowed(int productType, int buildNumber) =>
        VersionRestrictionTemporarilyDisabled || productType == 1 && buildNumber >= 19045;
}
