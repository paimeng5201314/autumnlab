namespace AutumnOS.SessionBrowserProtocol;

// No default false mode: a uniform real native control must be measured first.
internal static class NativeEndTrustComparison
{
    internal static bool Measure(int totalEnds, int trustedEnds)
    {
        if (totalEnds is < 1 or > 1024 || trustedEnds < 0 || trustedEnds > totalEnds)
            throw new InvalidDataException("NATIVE_END_CONTROL_OBSERVATION_REQUIRED");
        if (trustedEnds == 0) return false;
        if (trustedEnds == totalEnds) return true;
        throw new InvalidDataException("NATIVE_END_CONTROL_MIXED_TRUST_UNPROVEN");
    }
    internal static bool Matches(bool measuredControlMode, int bridgeTotalEnds, int bridgeTrustedEnds)
    {
        if (bridgeTotalEnds is < 1 or > 1024 || bridgeTrustedEnds < 0 || bridgeTrustedEnds > bridgeTotalEnds) return false;
        return measuredControlMode ? bridgeTrustedEnds == bridgeTotalEnds : bridgeTrustedEnds == 0;
    }
}
