using System.Text.Json.Serialization;

namespace AutumnOS.NativeHost.Windows;

/// <summary>Host-created binding. No field supplied by an application is authority.</summary>
public sealed record NativeHostBinding(
    [property: JsonPropertyName("authorityId")] Guid AuthorityId,
    [property: JsonPropertyName("account")] string Account,
    [property: JsonPropertyName("accountEpoch")] long AccountEpoch,
    [property: JsonPropertyName("applicationBinding")] string ApplicationBinding,
    [property: JsonPropertyName("instanceId")] Guid InstanceId,
    [property: JsonPropertyName("instanceEpoch")] long InstanceEpoch,
    [property: JsonPropertyName("documentId")] Guid DocumentId,
    [property: JsonPropertyName("documentGeneration")] long DocumentGeneration,
    [property: JsonPropertyName("channelGeneration")] long ChannelGeneration)
{
    /// <summary>Exactly the initial B1 endpoint-binding shape; this is not native evidence.</summary>
    public void Validate()
    {
        if (AuthorityId == Guid.Empty || InstanceId == Guid.Empty || DocumentId == Guid.Empty ||
            Account is not { Length: > 0 and <= 256 } || Account.Any(char.IsControl) ||
            !IsLowerSha256(ApplicationBinding) || AccountEpoch < 0 || InstanceEpoch < 0 ||
            DocumentGeneration < 1 || ChannelGeneration < 1)
            throw new ArgumentException("NATIVE_HOST_BINDING_INVALID");
    }

    internal static bool IsLowerSha256(string? value) => value is { Length: 64 } &&
        value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}

/// <summary>A complete root-relative regular-file binding; its SHA is lowercase hexadecimal.</summary>
public sealed record NativeHostFileBinding(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("bytes")] long Bytes,
    [property: JsonPropertyName("sha256")] string Sha256);
