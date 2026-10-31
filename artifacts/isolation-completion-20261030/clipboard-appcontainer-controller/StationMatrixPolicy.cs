namespace AutumnOS.OwnedBrowserParent;

internal sealed record StationMatrixCase(int index, string label, string side, uint mask);

// This gate is shared by the real owned-station DACL operation and the pure
// regressions. Identity/exit arguments must come from held runtime evidence.
internal static class StationMatrixPolicy
{
    internal static IReadOnlyList<StationMatrixCase> Cases { get; } = Array.AsReadOnly<StationMatrixCase>(
    [ new(0, "A23", "A", 0x23), new(1, "B23", "B", 0x23), new(2, "B20023", "B", 0x20023),
      new(3, "B20123", "B", 0x20123), new(4, "B20323", "B", 0x20323),
      new(5, "B23-restored", "B", 0x23), new(6, "A23-restored", "A", 0x23) ]);

    internal static void RequireTransition(int nextIndex, int requestedIndex, string label, uint requestedMask,
        uint actualCurrentMask, bool exactOwnedIdentity, bool allPreviousGroupsExited)
    {
        RequireEvidence(exactOwnedIdentity, allPreviousGroupsExited);
        RequireFixedMask(actualCurrentMask);
        if (nextIndex < 0 || nextIndex >= Cases.Count) throw new OwnedAssertionException("STATION_MATRIX_NO_NEXT_CASE");
        if (requestedIndex != nextIndex) throw new OwnedAssertionException("STATION_MATRIX_EXACT_ORDER_REQUIRED");
        RequireFixedMask(requestedMask);
        StationMatrixCase next = Cases[nextIndex];
        if (!string.Equals(label, next.label, StringComparison.Ordinal) || requestedMask != next.mask)
            throw new OwnedAssertionException("STATION_MATRIX_EXACT_CASE_SPEC_REQUIRED");
        uint previousMask = nextIndex == 0 ? 0x23 : Cases[nextIndex - 1].mask;
        if (actualCurrentMask != previousMask) throw new OwnedAssertionException("STATION_MATRIX_PREVIOUS_EXACT_MASK_REQUIRED");
    }

    // The only restoration destination is the initial mask0x23. No arbitrary
    // target, SID, station name, SDDL or security handle is accepted here.
    internal static void RequireRestore(uint actualCurrentMask, bool exactOwnedIdentity, bool allPreviousGroupsExited)
    {
        RequireEvidence(exactOwnedIdentity, allPreviousGroupsExited);
        RequireFixedMask(actualCurrentMask);
    }

    private static void RequireEvidence(bool exactOwnedIdentity, bool allPreviousGroupsExited)
    {
        if (!exactOwnedIdentity) throw new OwnedAssertionException("STATION_MATRIX_EXACT_OWNED_IDENTITY_REQUIRED");
        if (!allPreviousGroupsExited) throw new OwnedAssertionException("STATION_MATRIX_ALL_PREVIOUS_GROUPS_EXITED_REQUIRED");
    }
    private static void RequireFixedMask(uint mask)
    {
        // Includes only base ENUMDESKTOPS/READATTRIBUTES/ACCESSGLOBALATOMS,
        // optional READ_CONTROL/ENUMERATE/READSCREEN. In particular no clipboard,
        // CREATEDESKTOP, WRITEATTRIBUTES, EXITWINDOWS, DAC/owner write, DELETE,
        // generic/max-allowed/SACL access, or any unnamed bit can pass.
        if (mask is not (0x23 or 0x20023 or 0x20123 or 0x20323))
            throw new OwnedAssertionException("STATION_MATRIX_FIXED_MASK_ONLY");
    }
}
