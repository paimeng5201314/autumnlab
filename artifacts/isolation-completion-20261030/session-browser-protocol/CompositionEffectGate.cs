using System.Security.Cryptography;
using System.Text;

namespace AutumnOS.SessionBrowserProtocol;

internal sealed record CompositionEffectObservation(string document_instance_sha256, bool owned_text_focused, bool document_has_focus,
    int composition_start, int composition_update, int composition_end, bool composing,
    int? update_data_length, string? update_data_sha256, int? end_data_length, string? end_data_sha256);

// These are actual DOM effects, independent of whether the CDP call returned.
// End-event trust is compared separately against a verified direct native control.
internal static class CompositionEffectGate
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    internal static bool FrameAllowsNewComposition(SessionHeader? frame, string nonce)
    {
        if (frame is null || frame.kind != "frame" || frame.nonce != nonce || frame.epoch != 1 || frame.frame_id < 1) return false;
        SessionProtocol.ValidateHeader(frame);
        return frame.dom_owned_text_focused == true && frame.dom_document_has_focus == true &&
            frame.document_instance_sha256 is string document && Hash(document);
    }
    internal static bool NativeControlEnabled(bool sessionReady, bool closing, bool active, bool pendingEnd, SessionHeader? latestFrame, string nonce)
        => sessionReady && (active || pendingEnd || !closing && FrameAllowsNewComposition(latestFrame, nonce));
    internal static void RequireAdmission(CompositionEffectObservation before, string? expectedDocument)
    {
        RequireDocument(before, expectedDocument);
        if (!before.owned_text_focused || !before.document_has_focus)
            throw new InvalidDataException("COMPOSITION_OWNED_TARGET_FOCUS_REQUIRED");
    }
    internal static void RequireDocument(CompositionEffectObservation value, string? expectedDocument)
    {
        Validate(value);
        if (expectedDocument is not null && value.document_instance_sha256 != expectedDocument)
            throw new InvalidDataException("COMPOSITION_DOCUMENT_INSTANCE_CHANGED");
    }
    internal static bool HasRequiredEffect(string command, CompositionEffectObservation baseline, CompositionEffectObservation before,
        CompositionEffectObservation after, string? requestText)
    {
        Validate(baseline); Validate(before); Validate(after);
        if (before.document_instance_sha256 != baseline.document_instance_sha256 || after.document_instance_sha256 != baseline.document_instance_sha256)
            throw new InvalidDataException("COMPOSITION_DOCUMENT_INSTANCE_CHANGED");
        if (before.composition_start < baseline.composition_start || before.composition_update < baseline.composition_update || before.composition_end < baseline.composition_end ||
            after.composition_start < before.composition_start || after.composition_update < before.composition_update || after.composition_end < before.composition_end)
            throw new InvalidDataException("COMPOSITION_EVENT_COUNTER_REGRESSION");
        if (command == "ime_cancel") return after.composition_end > before.composition_end && !after.composing && after.end_data_length.HasValue;
        RequireAdmission(before, baseline.document_instance_sha256);
        if (requestText is null) throw new InvalidDataException("COMPOSITION_REQUEST_TEXT_REQUIRED");
        string requestedHash = HashText(requestText);
        if (command == "ime_start" && requestText.Length == 0) return true;
        if (command is "ime_start" or "ime_update" or "ime_set_composition")
        {
            if (requestText.Length == 0) throw new InvalidDataException("COMPOSITION_EMPTY_UPDATE_REQUIRES_CANCEL");
            return after.owned_text_focused && after.document_has_focus && after.composing && after.composition_start > baseline.composition_start &&
                after.composition_update > before.composition_update && after.update_data_length == requestText.Length && after.update_data_sha256 == requestedHash;
        }
        if (command == "ime_commit") return after.composition_end > before.composition_end && !after.composing &&
            after.end_data_length == requestText.Length && after.end_data_sha256 == requestedHash;
        throw new InvalidDataException("COMPOSITION_EFFECT_COMMAND_NOT_ALLOWED");
    }
    internal static string HashText(string text)
    {
        if (text.Length > 1024 || text.Contains('\0')) throw new InvalidDataException("COMPOSITION_EFFECT_TEXT_BOUNDS_REQUIRED");
        return Convert.ToHexStringLower(SHA256.HashData(Utf8.GetBytes(text)));
    }
    private static void Validate(CompositionEffectObservation value)
    {
        if (!Hash(value.document_instance_sha256) || value.composition_start is < 0 or > 1024 || value.composition_update is < 0 or > 1024 || value.composition_end is < 0 or > 1024)
            throw new InvalidDataException("COMPOSITION_EFFECT_OBSERVATION_REJECTED");
        Data(value.update_data_length, value.update_data_sha256); Data(value.end_data_length, value.end_data_sha256);
    }
    private static void Data(int? length, string? hash)
    {
        if (length.HasValue != (hash is not null) || length is < 0 or > 1024 || hash is not null && !Hash(hash))
            throw new InvalidDataException("COMPOSITION_EFFECT_EVENT_DATA_REJECTED");
    }
    private static bool Hash(string text) => text.Length == 64 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
