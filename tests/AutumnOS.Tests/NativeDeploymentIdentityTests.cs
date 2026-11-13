using System.Text;
using System.Text.Json;
using AutumnOS.Shell;
using AutumnOS.UpdateProtocol;

namespace AutumnOS.Tests;

internal static class NativeDeploymentIdentityTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("native.installation.current_build_receipt_is_accepted", () => Validate(Receipt));
        yield return ("native.installation.actual_updater_receipt_with_data_schema_is_accepted", () =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The actual updater assembly targets Windows.");
            var installed = new InstalledBuild(1, "1.0.0", "owned-build",
                [new("AutumnOS.Client.exe", 1, new string('a', 64)),
                    new("NativeHost/AutumnOS.NativeHost.Windows.exe", 2, new string('b', 64)),
                    new("WebView2Runtime/msedgewebview2.exe", 3, new string('c', 64))], 1);
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(installed, UpdateFiles.Json);
            using var json = JsonDocument.Parse(bytes);
            if (json.RootElement.GetProperty("dataSchemaVersion").GetInt32() != 1)
                throw new InvalidOperationException("The real updater receipt must exercise its dataSchemaVersion field.");
            NativeDeploymentIdentity.ValidateReceipt(bytes, "owned-build", "1.0.0");
        });
        yield return ("native.installation.unsupported_or_untyped_data_schema_is_rejected", () =>
        {
            foreach (string value in new[] { "0", "2", "null", "\"1\"", "true", "1.5" })
                Reject(Receipt.Replace("\"files\":", "\"dataSchemaVersion\":" + value + ",\"files\":"));
            Reject(Receipt.Replace("\"files\":", "\"dataSchemaVersion\":1,\"dataSchemaVersion\":1,\"files\":"));
        });
        yield return ("native.installation.other_build_is_rejected", () => Reject(Receipt.Replace("owned-build", "older-build")));
        yield return ("native.installation.other_version_is_rejected", () => Reject(Receipt.Replace("1.0.0", "0.5.2")));
        yield return ("native.installation.duplicate_identity_is_rejected", () => Reject(Receipt.Replace("\"buildId\":", "\"buildId\":\"owned-build\",\"buildId\":")));
        yield return ("native.installation.missing_or_untyped_identity_is_rejected", () =>
        {
            Reject("{}"); Reject(Receipt.Replace("\"owned-build\"", "null"));
            Reject(Receipt.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\""));
        });
        yield return ("native.installation.empty_inventory_is_rejected", () => Reject(Receipt.Replace("[{}]", "[]")));
        yield return ("native.installation.receipt_cannot_add_authority", () => Reject(Receipt.Replace("\"files\":", "\"isolationVerified\":true,\"files\":")));
        yield return ("native.installation.receipt_size_is_bounded", () =>
        {
            try { NativeDeploymentIdentity.ValidateReceipt(new byte[NativeDeploymentIdentity.MaximumReceiptBytes + 1], "owned-build", "1.0.0"); }
            catch (IOException) { return; }
            throw new InvalidOperationException("Oversized receipt was accepted.");
        });
    }
    private const string Receipt = """{"schemaVersion":1,"version":"1.0.0","buildId":"owned-build","files":[{}]}""";
    private static void Validate(string json) => NativeDeploymentIdentity.ValidateReceipt(Encoding.UTF8.GetBytes(json), "owned-build", "1.0.0");
    private static void Reject(string json)
    {
        try { Validate(json); } catch (IOException) { return; }
        throw new InvalidOperationException("Invalid deployment identity was accepted.");
    }
}
