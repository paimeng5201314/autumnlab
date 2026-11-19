using AutumnOS.Store;

namespace AutumnOS.Update;

/// <summary>Compares signed targets with the installed revision, independently of the pending download watermark.</summary>
public static class UpdateRevisionPolicy
{
    public const string ProtocolVersion = "1.2.0";

    public static string? CheckOrder(UpdateManifest target, string installedVersion, string installedBuildId,
        long installedSequence, bool rollback)
    {
        int comparison = SemanticVersion.Parse(target.Version).CompareTo(SemanticVersion.Parse(installedVersion));
        if (comparison == 0 && target.BuildId == installedBuildId) return "UPDATE_CURRENT_VERSION";
        if (target.BuildId == installedBuildId) return "UPDATE_CANDIDATE_PREVIOUSLY_FAILED_OR_CURRENT";
        if (rollback) return comparison < 0 ? null : "UPDATE_ROLLBACK_REQUIRES_OLDER";
        if (comparison < 0) return "UPDATE_UPGRADE_REQUIRES_NEWER";
        if (comparison == 0)
        {
            if (installedSequence <= 0 || target.Version != installedVersion) return "UPDATE_CURRENT_VERSION";
            if (target.ReleaseTag is null || SemanticVersion.Parse(target.MinimumUpdaterVersion).CompareTo(SemanticVersion.Parse(ProtocolVersion)) < 0)
                return "UPDATE_REVISION_PROTOCOL_REQUIRED";
        }
        if (target.Sequence < installedSequence) return "UPDATE_METADATA_REPLAY";
        return target.Sequence == installedSequence ? "UPDATE_SEQUENCE_REUSED" : null;
    }
}
