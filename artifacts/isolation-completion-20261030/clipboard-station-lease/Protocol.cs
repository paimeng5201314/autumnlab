using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AutumnOS.OwnedClipboardStationLease;

internal static class Policy
{
    internal const string StationPrefix = "AutumnOS_ClipboardProbe_";
    internal const string PipePrefix = "AutumnOS_ClipboardLease_";
    internal const string ProfilePrefix = "AutumnOS.Paimon.ClipboardProbe.";
    internal const uint StationOwnerAccess = 0x000F037F;
    // Microsoft WINSTA_* constants: ENUMDESKTOPS | READATTRIBUTES | ACCESSGLOBALATOMS.
    // A candidate for the owned noninteractive browser trial, not a proven minimum for WebView2.
    internal const uint StationPackageAccess = 0x00000023;
    internal static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    internal static bool Nonce(string value) => Regex.IsMatch(value, "\\A[0-9a-f]{32}\\z", RegexOptions.CultureInvariant);
    internal static string HashText(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static void RequireNoRuntimeInjection()
    {
        // A request is rejected before UAC. Values are never emitted. The holder repeats
        // the check before creating an object. These are process configuration checks,
        // not writes to system or user environment variables.
        foreach (string key in new[] { "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE",
            "CORECLR_ENABLE_PROFILING", "CORECLR_PROFILER", "CORECLR_PROFILER_PATH", "CORECLR_PROFILER_PATH_64",
            "COR_ENABLE_PROFILING", "COR_PROFILER", "COR_PROFILER_PATH", "COR_PROFILER_PATH_64" })
            if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))) throw new InvalidOperationException("RUNTIME_INJECTION_OVERRIDE_PRESENT");
    }
}

internal sealed record LeaseMessage(int schema_version, string stage, string nonce, string station, string profile,
    uint helper_pid, uint broker_pid, uint session, string package_sid_sha256, uint package_station_access);

internal static class Protocol
{
    private static readonly string[] Fields = ["schema_version", "stage", "nonce", "station", "profile", "helper_pid",
        "broker_pid", "session", "package_sid_sha256", "package_station_access"];

    internal static async Task Send(Stream pipe, LeaseMessage value, CancellationToken token)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length is < 1 or > 4096) throw new InvalidDataException("LEASE_MESSAGE_SIZE_INVALID");
        byte[] length = new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        await pipe.WriteAsync(length, token); await pipe.WriteAsync(bytes, token); await pipe.FlushAsync(token);
    }
    internal static async Task<LeaseMessage> Receive(Stream pipe, CancellationToken token)
    {
        byte[] length = new byte[4]; await pipe.ReadExactlyAsync(length, token);
        int size = BinaryPrimitives.ReadInt32LittleEndian(length);
        if (size is < 1 or > 4096) throw new InvalidDataException("LEASE_MESSAGE_SIZE_INVALID");
        byte[] bytes = new byte[size]; await pipe.ReadExactlyAsync(bytes, token);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("LEASE_OBJECT_REQUIRED");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in root.EnumerateObject())
            if (!Fields.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name)) throw new InvalidDataException("LEASE_FIELDS_INVALID");
        if (names.Count != Fields.Length) throw new InvalidDataException("LEASE_FIELDS_INVALID");
        var message = new LeaseMessage(root.GetProperty("schema_version").GetInt32(), Text(root, "stage"), Text(root, "nonce"),
            Text(root, "station"), Text(root, "profile"), root.GetProperty("helper_pid").GetUInt32(), root.GetProperty("broker_pid").GetUInt32(),
            root.GetProperty("session").GetUInt32(), Text(root, "package_sid_sha256"), root.GetProperty("package_station_access").GetUInt32());
        if (message.schema_version != 1 || !Policy.Nonce(message.nonce) || message.station != Policy.StationPrefix + message.nonce ||
            message.profile != Policy.ProfilePrefix + message.nonce || message.package_station_access != Policy.StationPackageAccess ||
            !Regex.IsMatch(message.package_sid_sha256, "\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant) ||
            message.helper_pid == 0 || message.broker_pid == 0 || message.helper_pid == message.broker_pid ||
            message.stage is not ("create" or "ready" or "release" or "released")) throw new InvalidDataException("LEASE_BINDING_INVALID");
        return message;
    }
    private static string Text(JsonElement root, string key) => root.GetProperty(key).GetString() ?? throw new InvalidDataException("LEASE_TEXT_REQUIRED");
}
