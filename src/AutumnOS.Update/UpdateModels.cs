using System.Text.Json.Serialization;
namespace AutumnOS.Update;

public sealed class UpdateException(string code, Exception? inner = null) : Exception(code, inner)
{ public string Code { get; } = code; }

public sealed record UpdateFile(string Path, long Bytes, string Sha256);
public sealed record UpdatePayloadDescriptor(string Asset, long Bytes, string Sha256, [property: JsonRequired] long ReleaseId = 1, [property: JsonRequired] long AssetId = 1);
public sealed record UpdateDataCompatibility(int SchemaVersion, int MinimumReadableVersion, bool RollbackCompatible);
public sealed record UpdateManifest(int SchemaVersion, string Channel, string Version, string BuildId,
    string TargetRid, string MinimumUpdaterVersion, long Sequence, DateTimeOffset IssuedAtUtc,
    DateTimeOffset ExpiresAtUtc, string KeyId, int TrustRootVersion, UpdatePayloadDescriptor Payload,
    IReadOnlyList<UpdateFile> Files, IReadOnlyList<string> RemoveFiles, UpdateDataCompatibility Data,
    [property: JsonRequired] string ProductId = "cn.labchronicles.autumnos", [property: JsonRequired] string Repository = "paimeng5201314/autumnlab");
public sealed record UpdateSignatureEnvelope(int SchemaVersion, string Algorithm, string KeyId, int TrustRootVersion, string Signature);
public sealed record UpdateTrustKey(string KeyId, string PublicKeyPem, bool Revoked = false);
public sealed record UpdateTrustDocument(int SchemaVersion, int Version, string Purpose, IReadOnlyList<UpdateTrustKey> Keys);
public sealed record UpdateCandidate(long ReleaseId, string Tag, string Notes, UpdateManifest Manifest,
    Uri PayloadUrl, long AssetId, byte[] ManifestBytes, byte[] SignatureBytes);
public sealed record StagedUpdate(UpdateCandidate Candidate, string ManifestPath, string SignaturePath, string PayloadPath);
public enum UpdateState { Idle, Checking, NoCompatibleUpdate, Available, Downloading, Verifying, Staged, WaitingForIdle, AcquiringMaintenance, Rechecking, SavingState, Handoff, Applying, HealthCheck, Committed, Error, Recovery, RolledBack }
public sealed record UpdateSnapshot(UpdateState State, string Channel, string CurrentVersion, UpdateCandidate? Candidate,
    long BytesReceived, long TotalBytes, string? ErrorCode, IReadOnlyList<string> Warnings, StagedUpdate? Staged,
    bool AutomaticInstallConfigured, bool IsTestBuild);
