using System.Text.Json;

namespace AutumnOS.Shell;

// Installation metadata binds the running build/layout, never grants a package permission.
// Executable host/runtime bytes are separately pinned against the compiled catalog before use.
internal static class NativeDeploymentIdentity
{
    internal const int MaximumReceiptBytes = 4 * 1024 * 1024;

    internal static void ValidateReceipt(byte[] bytes, string buildId, string version)
    {
        if (bytes.Length is < 1 or > MaximumReceiptBytes) throw Invalid();
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name) || property.Name is not ("schemaVersion" or "version" or "buildId" or "files" or "dataSchemaVersion")) throw Invalid();
        if (!new[] { "schemaVersion", "version", "buildId", "files" }.All(names.Contains) ||
            root.GetProperty("schemaVersion").ValueKind != JsonValueKind.Number ||
            !root.GetProperty("schemaVersion").TryGetInt32(out int schema) || schema != 1 ||
            root.GetProperty("buildId").ValueKind != JsonValueKind.String || root.GetProperty("version").ValueKind != JsonValueKind.String ||
            root.GetProperty("buildId").GetString() != buildId || root.GetProperty("version").GetString() != version ||
            root.GetProperty("files").ValueKind != JsonValueKind.Array || root.GetProperty("files").GetArrayLength() is < 1 or > 32768)
            throw Invalid();
        if (root.TryGetProperty("dataSchemaVersion", out var dataSchema) &&
            (dataSchema.ValueKind != JsonValueKind.Number || !dataSchema.TryGetInt32(out int dataVersion) || dataVersion != 1)) throw Invalid();
    }

    private static IOException Invalid() => new("RUNTIME_NATIVE_INSTALLATION_IDENTITY_INVALID");
}
