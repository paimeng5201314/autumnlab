namespace AutumnOS.Contracts;

// T00 internal protocol draft. These types are not a public JavaScript SDK.
public sealed record AppIdentity(string AppId, long? RepositoryId, string? SigningKeyFingerprint);
public readonly record struct AppInstanceId(Guid Value);
public readonly record struct SessionEpoch(long Value);
public enum AppLifecycleState { Starting, Foreground, Background, Suspended, Closing, Closed, Crashed }
public sealed record AppInstance(AppInstanceId Id, AppIdentity Identity, bool IsGame, AppLifecycleState State, SessionEpoch Epoch)
{
    public bool BlocksMaintenance => IsGame && State is AppLifecycleState.Starting or AppLifecycleState.Foreground
        or AppLifecycleState.Background or AppLifecycleState.Suspended or AppLifecycleState.Closing;
}
public sealed record ServiceError(string Code, string Message, bool Retryable, Guid CorrelationId);
public static class ProtocolVersion
{
    public const int Draft = 1;
    public const string Availability = "CAPABILITY_UNAVAILABLE";
}
