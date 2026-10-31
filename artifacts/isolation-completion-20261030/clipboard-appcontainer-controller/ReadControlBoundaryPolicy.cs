namespace AutumnOS.OwnedBrowserParent;

// Shared by the actual lease DACL operations and the pure regression fixture.
// Runtime callers derive identity and process facts from held native objects.
internal static class ReadControlBoundaryPolicy
{
    internal const uint InitialMask = 0x23;
    internal const uint RuntimeMask = 0x20023;

    internal static void RequireEnable(uint actualMask, bool exactOwnedIdentity, bool noProcessEverStarted)
    {
        RequireIdentity(exactOwnedIdentity);
        if (!noProcessEverStarted) throw new OwnedAssertionException("READCONTROL_BOUNDARY_ENABLE_BEFORE_ANY_PROCESS_REQUIRED");
        if (actualMask != InitialMask) throw new OwnedAssertionException("READCONTROL_BOUNDARY_INITIAL_EXACT_23_REQUIRED");
    }
    internal static void RequireRuntime(uint actualMask, bool exactOwnedIdentity)
    {
        RequireIdentity(exactOwnedIdentity);
        if (actualMask != RuntimeMask) throw new OwnedAssertionException("READCONTROL_BOUNDARY_RUNTIME_EXACT_20023_REQUIRED");
    }
    // Restoration has a fixed destination0x23 and accepts no target, SID,
    // station name, descriptor or handle from a caller.
    internal static void RequireRestore(uint actualMask, bool exactOwnedIdentity, bool allOwnedGroupsRetired)
    {
        RequireIdentity(exactOwnedIdentity);
        if (!allOwnedGroupsRetired) throw new OwnedAssertionException("READCONTROL_BOUNDARY_RESTORE_ALL_GROUPS_RETIRED_REQUIRED");
        if (actualMask is not (InitialMask or RuntimeMask)) throw new OwnedAssertionException("READCONTROL_BOUNDARY_RESTORE_FIXED_MASK_ONLY");
    }
    private static void RequireIdentity(bool exactOwnedIdentity)
    { if (!exactOwnedIdentity) throw new OwnedAssertionException("READCONTROL_BOUNDARY_EXACT_OWNED_IDENTITY_REQUIRED"); }
}
