using System.Text.Json;
using AutumnOS.Contracts;

namespace AutumnOS.Runtime;

/// <summary>Only the validated minimum profile; no claims or credentials.</summary>
public sealed record RuntimeProfile(string DisplayName, string? AvatarUrl, bool IsCached);

/// <summary>Trusted composition root adapters. Existing T01 constructor without services remains available.</summary>
public sealed class RuntimeSessionServices
{
    public required RuntimeAccountContext Account { get; init; }
    public required RuntimeApplication Application { get; init; }
    public required IPermissionService Permissions { get; init; }
    public required Func<RuntimePermissionPrompt, CancellationToken, Task<bool>> RequestPermission { get; init; }
    public Func<RuntimeAccountContext, CancellationToken, Task<RuntimeProfile>>? GetProfile { get; init; }
    public IIdentityService? Identity { get; init; }
    public Func<string, JsonElement, CancellationToken, Task<object>>? DataRequest { get; init; }
    public IReadOnlyList<string> DataCapabilities { get; init; } = ["saves", "storage", "preferences"];
    public DesktopExtensionService? Desktop { get; init; }
    public DesktopDeclarations Declarations { get; init; } = new([], [], []);
}
