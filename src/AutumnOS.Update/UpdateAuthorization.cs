using System.Security.Cryptography;
using AutumnOS.Store;

namespace AutumnOS.Update;

/// <summary>A short-lived manual rollback consent, bound to the signed target and the installed source.</summary>
public sealed record UpdateRollbackAuthorization(string Repository, long ReleaseId, string ReleaseTag,
    string Version, string BuildId, string TargetRid, long AssetId, string PayloadSha256, string ManifestSha256,
    string FromBuildId, string FromVersion, DateTimeOffset ExpiresUtc)
{
    public static UpdateRollbackAuthorization Create(UpdateCandidate candidate, string currentBuildId, string currentVersion, DateTimeOffset now)
    {
        var m = candidate.Manifest;
        var result = new UpdateRollbackAuthorization(m.Repository, candidate.ReleaseId, candidate.Tag, m.Version, m.BuildId,
            m.TargetRid, candidate.AssetId, m.Payload.Sha256, Convert.ToHexStringLower(SHA256.HashData(candidate.ManifestBytes)),
            currentBuildId, currentVersion, now.AddMinutes(30));
        result.Validate(m, candidate.ManifestBytes, currentBuildId, currentVersion, now);
        return result;
    }

    public void Validate(UpdateManifest m, byte[] bytes, string currentBuildId, string currentVersion, DateTimeOffset now)
    {
        if (ExpiresUtc <= now || ExpiresUtc > now.AddMinutes(31) || FromBuildId != currentBuildId || FromVersion != currentVersion ||
            Repository != m.Repository || ReleaseId != m.Payload.ReleaseId || Version != m.Version || BuildId != m.BuildId ||
            TargetRid != m.TargetRid || AssetId != m.Payload.AssetId || PayloadSha256 != m.Payload.Sha256 ||
            ManifestSha256 != Convert.ToHexStringLower(SHA256.HashData(bytes)) ||
            !(m.ReleaseTag is not null ? ReleaseTag == m.ReleaseTag : ReleaseTag == m.Channel + "-v" + m.Version || ReleaseTag == m.Version || ReleaseTag == "v" + m.Version) ||
            !SemanticVersion.TryParse(m.Version, out var target) || !SemanticVersion.TryParse(currentVersion, out var current) || target!.CompareTo(current) >= 0)
            throw new UpdateException("UPDATE_ROLLBACK_AUTHORIZATION_INVALID");
    }
}
