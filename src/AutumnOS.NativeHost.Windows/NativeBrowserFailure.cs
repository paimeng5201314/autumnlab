using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AutumnOS.NativeHost.Windows;

public enum NativeBrowserFailureStage { Request, ReadPackage, Navigate, WaitCommit, WaitGate, Probe, Binding, Release, Poll, Delivery, Input, ResetInput }

// This is a diagnostic envelope, never an admission result. No exception text,
// document URL, nonce, package content or user input crosses this boundary.
public static class NativeBrowserFailure
{
    public const string Fallback = "NATIVE_BROWSER_REQUEST_FAILED";
    private static readonly string[] Stages = ["request", "read_package", "navigate", "wait_commit", "wait_gate", "probe", "binding", "release", "poll", "delivery", "input", "reset_input"];
    private static readonly Dictionary<string, string> Assertions = new(StringComparer.Ordinal)
    {
        ["BROWSER_DOCUMENT_ONCE"] = "document_once", ["BROWSER_ORIGIN_REQUIRED"] = "origin",
        ["BROWSER_CHALLENGE_PATH_COLLISION"] = "path_collision", ["BROWSER_STAGED_RESOURCE_LENGTH"] = "package_length",
        ["BROWSER_STAGED_RESOURCE_HASH"] = "package_hash", ["BROWSER_STAGED_RESOURCE_IDENTITY"] = "package_identity",
        ["BROWSER_BOOTSTRAP_NOT_HELD"] = "bootstrap_not_held",
        ["BROWSER_DOCUMENT_BINDING_INVALID"] = "binding_invalid", ["BROWSER_GATE_BINDING_LOST"] = "gate_lost",
        ["BROWSER_DOCUMENT_LOST"] = "document_lost", ["BROWSER_APPLICATION_NAVIGATION_FAILED"] = "application_navigation",
        ["BROWSER_GATE_ALREADY_COMPLETED"] = "gate_completed", ["BROWSER_MESSAGE_FIELDS"] = "message_fields",
        ["BROWSER_VIEW_SEQUENCE"] = "sequence", ["BROWSER_VIEW_REQUEST_UNSUPPORTED"] = "request_kind",
        ["BROWSER_POINTER_COMMAND_INVALID"] = "pointer_invalid",
        ["BROWSER_KEY_COMMAND_INVALID"] = "key_invalid", ["BROWSER_KEY_SEQUENCE"] = "key_sequence",
        ["BROWSER_RESPONSE_STREAM_LIMIT"] = "response_limit", ["ORIGINAL_STATION_HOST_NOT_LIVE"] = "station_lost",
        ["EXACT_PRIVATE_BROWSER_CONTEXT_REQUIRED"] = "private_context", ["NONINTERACTIVE_STATION_REQUIRED"] = "interactive_station",
        ["BROWSER_REVOKED"] = "revoked", ["BROWSER_NAVIGATION_REVOKED"] = "navigation_revoked",
        ["BROWSER_SOURCE_REVOKED"] = "source_revoked", ["BROWSER_CONTENT_REVOKED"] = "content_revoked",
        ["BROWSER_COMPLETION_REVOKED"] = "completion_revoked", ["BROWSER_PROCESS_REVOKED"] = "process_revoked",
        ["BROWSER_RESOURCE_REVOKED"] = "resource_revoked", ["BROWSER_MESSAGE_REVOKED"] = "message_revoked",
        ["BROWSER_OWNER_REVOKED"] = "owner_revoked", ["BROWSER_PIPE_REVOKED"] = "pipe_revoked"
    };
    private static readonly HashSet<string> Codes = new(Assertions.Values.Concat(
        ["canceled", "timeout", "json", "io", "com", "argument", "invalid_data", "error"]), StringComparer.Ordinal);

    public static byte[] Encode(NativeBrowserFailureStage stage, Exception error, string? probeSummary = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        string code = error is InvalidDataException && Assertions.TryGetValue(error.Message, out var assertion) ? assertion : error switch
        {
            OperationCanceledException => "canceled", TimeoutException => "timeout", JsonException => "json",
            InvalidDataException => "invalid_data", IOException => "io", System.Runtime.InteropServices.COMException => "com",
            System.ComponentModel.Win32Exception native => "w32_" + unchecked((uint)native.NativeErrorCode).ToString(CultureInfo.InvariantCulture),
            ArgumentException => "argument", _ => "error"
        };
        int index = (int)stage;
        string text = "NBF1|" + (index >= 0 && index < Stages.Length ? Stages[index] : Stages[0]) + "|" + code + "|"
            + unchecked((uint)error.HResult).ToString("x8", CultureInfo.InvariantCulture) + "|"
            + (ValidSummary(probeSummary) ? probeSummary : "none");
        return Encoding.ASCII.GetBytes(Sanitize(Encoding.ASCII.GetBytes(text)));
    }

    public static string Sanitize(ReadOnlySpan<byte> payload)
    {
        if (payload.Length is < 1 or > 128) return Fallback;
        foreach (byte value in payload) if (value is < 0x20 or > 0x7e) return Fallback;
        string text = Encoding.ASCII.GetString(payload);
        string[] fields = text.Split('|');
        if (fields.Length == 8 && fields[0] == "NBC1") return ValidClose(fields) ? text : Fallback;
        if (fields.Length != 5 || fields[0] != "NBF1" || !Stages.Contains(fields[1], StringComparer.Ordinal) ||
            !ValidCode(fields[2]) || fields[3].Length != 8 || fields[3].Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')) ||
            !ValidSummary(fields[4])) return Fallback;
        return text;
    }

    private static bool ValidSummary(string? summary) => summary is "none" or "pending" or "invalid" or "valid";
    private static bool ValidClose(string[] f) => NativeBrowserCloseObservation.Steps.Contains(f[1], StringComparer.Ordinal) &&
        f[2] is "error" or "timeout" or "unconfirmed" && Hex(f[3]) && f[4] is "unknown" or "live" or "exited" &&
        (f[5] == "-" ? f[6] == "-" || Hex(f[6]) && f[6] != "00000000" :
            int.TryParse(f[5], NumberStyles.None, CultureInfo.InvariantCulture, out int count) && count >= 0 &&
            f[5] == count.ToString(CultureInfo.InvariantCulture) && f[6] == "00000000") &&
        f[7] is "none" or "normal" or "failed" or "other";
    private static bool Hex(string value) => value.Length == 8 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool ValidCode(string code) => Codes.Contains(code) ||
        code.StartsWith("w32_", StringComparison.Ordinal) && uint.TryParse(code.AsSpan(4), NumberStyles.None, CultureInfo.InvariantCulture, out uint value) &&
        code[4..] == value.ToString(CultureInfo.InvariantCulture);
}

internal enum NativeBrowserCloseStep { Revoke, DeliveryDrain, Controller, Streams, Observe, Reader, Reply, Release }
internal enum NativeBrowserCloseExit { None, Normal, Failed, Other }

// Local browser observations only. This cannot retire the parent-owned group.
// Unknown and SDK exceptions are distinct from an observed empty process set.
internal sealed class NativeBrowserCloseObservation
{
    internal static readonly string[] Steps = ["revoke", "delivery_drain", "controller", "streams", "observe", "reader", "reply", "release"];
    internal NativeBrowserCloseStep Step { get; set; }
    internal bool? OriginalBrowserSignaled { get; set; }
    internal int? SdkProcessCount { get; set; }
    internal int? SdkQueryHresult { get; set; }
    internal NativeBrowserCloseExit ExitKind { get; set; }
    internal bool TimedOut { get; set; }
    internal bool Clean => OriginalBrowserSignaled == true && SdkProcessCount == 0 && SdkQueryHresult == 0 && ExitKind == NativeBrowserCloseExit.Normal;
    internal byte[] Failure(Exception? error = null)
    {
        int step = (int)Step;
        string text = "NBC1|" + (step >= 0 && step < Steps.Length ? Steps[step] : Steps[0]) + "|" +
            (error is not null ? "error" : TimedOut ? "timeout" : "unconfirmed") + "|" +
            unchecked((uint)(error?.HResult ?? 0)).ToString("x8", CultureInfo.InvariantCulture) + "|" +
            (OriginalBrowserSignaled is { } signaled ? signaled ? "exited" : "live" : "unknown") + "|" +
            (SdkProcessCount?.ToString(CultureInfo.InvariantCulture) ?? "-") + "|" +
            (SdkQueryHresult is { } hr ? unchecked((uint)hr).ToString("x8", CultureInfo.InvariantCulture) : "-") + "|" +
            (ExitKind switch { NativeBrowserCloseExit.Normal => "normal", NativeBrowserCloseExit.Failed => "failed", NativeBrowserCloseExit.Other => "other", _ => "none" });
        return Encoding.ASCII.GetBytes(NativeBrowserFailure.Sanitize(Encoding.ASCII.GetBytes(text)));
    }
}
