using System.Text;
using System.Text.Json;

namespace AutumnOS.Identity;

/// <summary>Human-maintained registration notes. This type cannot establish a session or authenticate a user.</summary>
public sealed record LogtoRegistrationStatus(
    string ApplicationId,
    string ApplicationTypeObserved,
    string RedirectUriStatus,
    string PostLogoutRedirectUriStatus,
    string DiscoveryProbeStatus,
    bool ProductLoginTestExecuted)
{
    public bool IsAuthenticationEvidence => false;
    public string SafeSummary => "登记文件仅为人工核对记录；不能证明当前用户登录、令牌有效或控制台配置正确。";
}

public sealed record RegistrationStatusResult(LogtoRegistrationStatus? Status, string Code, string SafeSummary)
{
    public bool IsReadable => Status is not null;
}

public static class RegistrationStatusLoader
{
    private const int MaximumProbeHistoryEntries = 32;
    private const int MaximumNoteLength = 4096;

    public static RegistrationStatusResult Load(string path, string? expectedClientId = null)
    {
        try { return Parse(IdentityJson.ReadFile(path), expectedClientId); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or JsonException or DecoderFallbackException)
        {
            return new(null, "AUTH_REGISTRATION_UNVERIFIED", "登记记录不可读；Native 类型与回调登记仍待核对。");
        }
    }

    public static RegistrationStatusResult Parse(string json, string? expectedClientId = null)
    {
        try
        {
            using JsonDocument document = IdentityJson.Parse(json, IdentityJson.ConfigurationLimitBytes);
            JsonElement root = document.RootElement;
            if (!IdentityJson.HasOnly(root, "schema_version", "pack_version", "application_id", "public_values_source",
                    "application_type_required", "application_type_observed", "redirect_uri_status",
                    "post_logout_redirect_uri_status", "discovery_probe", "console_changes_performed", "contains_secrets",
                    "probe_history") ||
                !root.TryGetProperty("schema_version", out JsonElement schema) || !schema.TryGetInt32(out int version) ||
                version != 1 || !root.TryGetProperty("contains_secrets", out JsonElement containsSecrets) ||
                containsSecrets.ValueKind != JsonValueKind.False ||
                !IsSafeNote(IdentityJson.GetString(root, "pack_version"), 128) ||
                !IsSafeNote(IdentityJson.GetString(root, "public_values_source"), MaximumNoteLength) ||
                !HasBoolean(root, "console_changes_performed") ||
                IdentityJson.GetString(root, "application_type_required") != "Native/public client" ||
                !root.TryGetProperty("discovery_probe", out JsonElement probe) ||
                !IsProbe(probe) || !HasValidProbeHistory(root))
                return Invalid();

            string? applicationId = IdentityJson.GetString(root, "application_id");
            string? applicationType = IdentityJson.GetString(root, "application_type_observed");
            string? redirectStatus = IdentityJson.GetString(root, "redirect_uri_status");
            string? logoutStatus = IdentityJson.GetString(root, "post_logout_redirect_uri_status");
            string? probeStatus = IdentityJson.GetString(probe, "status");
            if (!IsSafeStatus(applicationId) || !IsSafeStatus(applicationType) || !IsSafeStatus(redirectStatus) ||
                !IsSafeStatus(logoutStatus) || !IsSafeStatus(probeStatus) ||
                (expectedClientId is not null && applicationId != expectedClientId))
                return Invalid();

            LogtoRegistrationStatus status = new(applicationId!, applicationType!, redirectStatus!, logoutStatus!,
                probeStatus!, probe.GetProperty("product_login_test_executed").GetBoolean());
            return new(status, "REGISTRATION_NOTES_ONLY", status.SafeSummary);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException)
        {
            return Invalid();
        }
    }

    private static bool IsSafeStatus(string? value) => value is { Length: > 0 and <= 128 } &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '/' or ' ');

    private static bool IsSafeNote(string? value, int maximumLength) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximumLength && !value.Any(char.IsControl);

    private static bool HasBoolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) &&
        value.ValueKind is JsonValueKind.True or JsonValueKind.False;

    private static bool IsProbe(JsonElement probe) =>
        IdentityJson.HasOnly(probe, "status", "attempted_read_only", "reason", "product_login_test_executed") &&
        IsSafeStatus(IdentityJson.GetString(probe, "status")) &&
        HasBoolean(probe, "attempted_read_only") &&
        IsSafeNote(IdentityJson.GetString(probe, "reason"), MaximumNoteLength) &&
        HasBoolean(probe, "product_login_test_executed");

    private static bool HasValidProbeHistory(JsonElement root)
    {
        // Older notes omit history. Historical claims have the same untrusted schema as the current probe.
        if (!root.TryGetProperty("probe_history", out JsonElement history)) return true;
        return history.ValueKind == JsonValueKind.Array && history.GetArrayLength() <= MaximumProbeHistoryEntries &&
            history.EnumerateArray().All(IsProbe);
    }

    private static RegistrationStatusResult Invalid() =>
        new(null, "AUTH_REGISTRATION_UNVERIFIED", "登记记录格式不符或与公开配置不一致；仍须核对控制台。");
}
