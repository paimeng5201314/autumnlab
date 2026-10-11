using System.Text.Json;
using AutumnOS.Contracts;
using AutumnOS.Storage;

namespace AutumnOS.Runtime;

/// <summary>The host's account-bound data adapter, shared by native composition and regression tests.</summary>
public static class AccountDataRequestHandler
{
    public const int MaximumPrivateBridgeBytes = 20 * 1024;

    public static object Handle(AccountDataStore store, string method, JsonElement parameters, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(store);
        cancellationToken.ThrowIfCancellationRequested();
        T? Require<T>(DataResult<T> result)
        {
            // Storage represents cancellation as a result. Restore the linked token so RuntimeSession
            // can distinguish host timeout, account invalidation and caller cancellation correctly.
            cancellationToken.ThrowIfCancellationRequested();
            if (!result.Success) throw new RuntimeCapabilityException(result.ErrorCode == "SESSION_STALE" ? "SESSION_EXPIRED" : result.ErrorCode);
            return result.Value;
        }
        switch (method)
        {
            case "saves.list":
                Exact(parameters);
                return new { slots = Require(store.ListSlots(cancellationToken)) };
            case "saves.read":
                Exact(parameters, "slot");
                var value = Require(store.ReadSave(Text(parameters, "slot"), cancellationToken));
                return new { exists = value.HasValue, value };
            case "saves.write":
                bool versioned = parameters.TryGetProperty("formatVersion", out var version);
                Exact(parameters, versioned ? ["slot", "value", "formatVersion"] : ["slot", "value"]);
                int formatVersion = versioned && version.TryGetInt32(out var number) ? number : versioned ? 0 : 1;
                if (formatVersion < 1) throw new RuntimeCapabilityException("INVALID_PARAMS");
                BridgeJson.EnsureReadable(parameters.GetProperty("value"));
                Require(store.WriteSave(Text(parameters, "slot"), parameters.GetProperty("value"), formatVersion, cancellationToken));
                return new { saved = true };
            case "saves.restore":
                Exact(parameters, "slot");
                Require(store.RestoreSave(Text(parameters, "slot"), cancellationToken));
                return new { restored = true };
            case "storage.read":
                Exact(parameters, "key");
                var raw = Require(store.ReadPrivate(Text(parameters, "key"), cancellationToken));
                if (raw?.Length > MaximumPrivateBridgeBytes) throw new RuntimeCapabilityException("FILE_TOO_LARGE");
                return new { exists = raw is not null, data = raw is null ? null : Convert.ToBase64String(raw), encoding = "base64" };
            case "storage.write":
                Exact(parameters, "key", "data");
                byte[] bytes = Convert.FromBase64String(Text(parameters, "data"));
                if (bytes.Length > MaximumPrivateBridgeBytes) throw new RuntimeCapabilityException("FILE_TOO_LARGE");
                Require(store.WritePrivate(Text(parameters, "key"), bytes, cancellationToken));
                return new { written = true };
            case "storage.delete":
                Exact(parameters, "key");
                return new { deleted = Require(store.DeletePrivate(Text(parameters, "key"), cancellationToken)) };
            case "preferences.get":
                Exact(parameters, "key");
                var preference = Require(store.ReadPreference(Text(parameters, "key"), cancellationToken));
                return new { exists = preference.HasValue, value = preference };
            case "preferences.set":
                Exact(parameters, "key", "value");
                BridgeJson.EnsureReadable(parameters.GetProperty("value"));
                Require(store.WritePreference(Text(parameters, "key"), parameters.GetProperty("value"), cancellationToken));
                return new { saved = true };
            default:
                throw new RuntimeCapabilityException("CAPABILITY_UNAVAILABLE");
        }
    }

    private static string Text(JsonElement parameters, string key)
    {
        if (parameters.GetProperty(key).ValueKind != JsonValueKind.String) throw new RuntimeCapabilityException("INVALID_REQUEST");
        return parameters.GetProperty(key).GetString()!;
    }
    private static void Exact(JsonElement parameters, params string[] keys)
    {
        if (!RuntimeSession.ExactProperties(parameters, keys)) throw new RuntimeCapabilityException("INVALID_REQUEST");
    }
}
