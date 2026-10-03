using System.Security.Cryptography;
using System.Text;
using System.Globalization;

namespace AutumnOS.Contracts;

/// <summary>Frozen host-only account binding. Never deserialize this type from application messages.</summary>
public sealed record RuntimeAccountContext(string AccountKey, long Epoch, bool IsGuest,
    Func<bool> IsCurrent, CancellationToken Invalidated, Func<IDisposable>? EnterCommitLease = null)
{
    public void EnsureCurrent()
    {
        if (Invalidated.IsCancellationRequested || !IsCurrent()) throw new RuntimeCapabilityException("SESSION_EXPIRED");
        if (Epoch < 0 || string.IsNullOrWhiteSpace(AccountKey) || AccountKey.Length > 256)
            throw new RuntimeCapabilityException("SESSION_EXPIRED");
    }
}

public enum PermissionDecision { Prompt, Granted, Denied, Revoked }
public sealed record PermissionRecord(string AccountKey, string BindingKey, string AppId, string DisplayName,
    string Source, string Permission, PermissionDecision Decision);
public sealed record PermissionChange(string AccountKey, string BindingKey, string Permission, PermissionDecision Decision);
public interface IPermissionService
{
    event Action<PermissionChange>? Changed;
    PermissionDecision Query(RuntimeAccountContext account, RuntimeApplication app, string permission);
    IReadOnlyList<PermissionRecord> List(RuntimeAccountContext account);
    void Demand(RuntimeAccountContext account, RuntimeApplication app, string permission);
    /// <summary>Monotonic in-process binding revision, published atomically with decisions, independent of event dispatch.</summary>
    long GetRevision(RuntimeAccountContext account, RuntimeApplication app);
    IDisposable EnterUsageLease(RuntimeAccountContext account, RuntimeApplication app, string permission);
    IDisposable EnterDecisionLease(RuntimeAccountContext account, RuntimeApplication app, string permission);
    Task<PermissionDecision> RequestAsync(RuntimeAccountContext account, RuntimeApplication app, string permission,
        bool userGesture, Func<RuntimePermissionPrompt, CancellationToken, Task<bool>> prompt, CancellationToken cancellationToken);
    void SetDecision(RuntimeAccountContext account, RuntimeApplication app, string permission, PermissionDecision decision);
}

public sealed record RuntimeApplication(AppIdentity Identity, string Source, string DisplayName,
    IReadOnlyCollection<string> DeclaredPermissions, IReadOnlyDictionary<string, string>? PermissionPurposes = null)
{
    public string BindingKey => Hash($"autumnos.app-binding.v1\0{Identity.AppId}\0{Identity.RepositoryId?.ToString(CultureInfo.InvariantCulture)}\0{Identity.SigningKeyFingerprint}\0{Source}");
    public static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record RuntimePermissionPrompt(string AppId, string DisplayName, string Source, string Permission,
    string Purpose, IReadOnlyList<string> Fields);
public sealed class RuntimeCapabilityException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
