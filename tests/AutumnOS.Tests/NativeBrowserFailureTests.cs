using System.Text;
using AutumnOS.NativeHost.Windows;

namespace AutumnOS.Tests;

internal static class NativeBrowserFailureTests
{
    internal static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("native.failure.fixed_stages_and_hresult_roundtrip", RoundTrip);
        yield return ("native.failure.exception_text_never_disclosed", NoExceptionText);
        yield return ("native.failure.fixed_revocation_and_probe_status", Revocation);
        yield return ("native.failure.rejects_untrusted_payloads", RejectMalformed);
        yield return ("native.failure.win32_code_is_bounded_numeric_only", Win32Code);
        yield return ("native.close.diagnostic_distinguishes_unknown_live_empty_and_sdk_error", CloseFacts);
        yield return ("native.close.diagnostic_steps_hresult_and_no_exception_text", CloseStages);
        yield return ("native.close.diagnostic_rejects_untrusted_or_contradictory_fields", CloseMalformed);
        yield return ("native.close.clean_requires_original_handle_sdk_zero_and_normal_event", CloseClean);
    }

    private static void RoundTrip()
    {
        foreach (NativeBrowserFailureStage stage in Enum.GetValues<NativeBrowserFailureStage>())
        {
            var error = new OperationCanceledException("SECRET user path");
            byte[] payload = NativeBrowserFailure.Encode(stage, error, "pending");
            string result = NativeBrowserFailure.Sanitize(payload);
            Check(payload.Length <= 128 && result != NativeBrowserFailure.Fallback &&
                result.Contains("|canceled|" + unchecked((uint)error.HResult).ToString("x8") + "|pending", StringComparison.Ordinal));
        }
    }
    private static void NoExceptionText()
    {
        foreach (Exception error in new Exception[] { new Exception("C:\\SECRET\\token"), new InvalidDataException("BROWSER_GATE_BINDING_LOST\nSECRET") })
        {
            string result = NativeBrowserFailure.Sanitize(NativeBrowserFailure.Encode(NativeBrowserFailureStage.Probe, error, "SECRET"));
            Check(!result.Contains("SECRET", StringComparison.Ordinal) && result.EndsWith("|none", StringComparison.Ordinal));
        }
    }
    private static void Revocation()
    {
        string result = NativeBrowserFailure.Sanitize(NativeBrowserFailure.Encode(NativeBrowserFailureStage.WaitCommit,
            new InvalidDataException("BROWSER_CONTENT_REVOKED"), "none"));
        Check(result.StartsWith("NBF1|wait_commit|content_revoked|", StringComparison.Ordinal));
        foreach (string summary in new[] { "none", "pending", "invalid", "valid" })
            Check(NativeBrowserFailure.Sanitize(NativeBrowserFailure.Encode(NativeBrowserFailureStage.Probe,
                new TimeoutException(), summary)).EndsWith("|" + summary, StringComparison.Ordinal));
    }
    private static void RejectMalformed()
    {
        foreach (string value in new[] { "", "NBF1|probe|secret|80131500|none", "NBF1|secret|error|80131500|none",
            "NBF1|probe|error|nothex00|none", "NBF1|probe|error|80131500|secret", "NBF1|probe|error|80131500|none\n",
            "NBF1|probe|error|80131500|none|extra", "NBF1|probe|w32_05|80004005|none", "NBF1|probe|w32_-1|80004005|none",
            "NBF1|probe|w32_4294967296|80004005|none", "NBF1|probe|w32_SECRET|80004005|none", new string('a', 129) })
            Check(NativeBrowserFailure.Sanitize(Encoding.UTF8.GetBytes(value)) == NativeBrowserFailure.Fallback);
        Check(NativeBrowserFailure.Sanitize(new byte[] { 0xff }) == NativeBrowserFailure.Fallback);
    }
    private static void Win32Code()
    {
        foreach (int code in new[] { 0, 5, 32, int.MaxValue, -1 })
        {
            byte[] payload = NativeBrowserFailure.Encode(NativeBrowserFailureStage.ReadPackage,
                new System.ComponentModel.Win32Exception(code, "SECRET"));
            string result = NativeBrowserFailure.Sanitize(payload);
            Check(payload.Length <= 128 && result.StartsWith("NBF1|read_package|w32_" + unchecked((uint)code).ToString(System.Globalization.CultureInfo.InvariantCulture) + "|", StringComparison.Ordinal)
                && !result.Contains("SECRET", StringComparison.Ordinal));
        }
    }
    private static void CloseFacts()
    {
        var facts = new NativeBrowserCloseObservation { Step = NativeBrowserCloseStep.Observe, TimedOut = true };
        Check(NativeBrowserFailure.Sanitize(facts.Failure()) == "NBC1|observe|timeout|00000000|unknown|-|-|none");
        facts.OriginalBrowserSignaled = false; facts.SdkProcessCount = 3; facts.SdkQueryHresult = 0;
        Check(NativeBrowserFailure.Sanitize(facts.Failure()).EndsWith("|live|3|00000000|none", StringComparison.Ordinal));
        facts.OriginalBrowserSignaled = true; facts.SdkProcessCount = null; facts.SdkQueryHresult = unchecked((int)0x80004005);
        Check(NativeBrowserFailure.Sanitize(facts.Failure()).EndsWith("|exited|-|80004005|none", StringComparison.Ordinal) && !facts.Clean);
    }
    private static void CloseStages()
    {
        foreach (NativeBrowserCloseStep step in Enum.GetValues<NativeBrowserCloseStep>())
        {
            var facts = new NativeBrowserCloseObservation { Step = step };
            var error = new System.Runtime.InteropServices.COMException("SECRET token path", unchecked((int)0x80070005));
            byte[] bytes = facts.Failure(error); string text = NativeBrowserFailure.Sanitize(bytes);
            Check(bytes.Length <= 128 && text.StartsWith("NBC1|", StringComparison.Ordinal) && text.Contains("|error|80070005|", StringComparison.Ordinal) && !text.Contains("SECRET", StringComparison.Ordinal));
        }
    }
    private static void CloseMalformed()
    {
        foreach (string value in new[] { "NBC1|secret|error|80004005|unknown|-|-|none", "NBC1|observe|error|80004005|secret|-|-|none",
            "NBC1|observe|error|80004005|exited|0|80004005|normal", "NBC1|observe|error|80004005|exited|-|00000000|normal",
            "NBC1|observe|unconfirmed|00000000|exited|00|00000000|normal", "NBC1|observe|timeout|00000000|live|-|-|secret",
            "NBC1|observe|timeout|00000000|live|2147483648|00000000|none", "NBC1|observe|timeout|00000000|live|-|-|none|extra" })
            Check(NativeBrowserFailure.Sanitize(Encoding.ASCII.GetBytes(value)) == NativeBrowserFailure.Fallback);
    }
    private static void CloseClean()
    {
        foreach (bool? signaled in new bool?[] { null, false, true })
        foreach (int? count in new int?[] { null, 0, 1 })
        foreach (int? hr in new int?[] { null, 0, unchecked((int)0x80004005) })
        foreach (NativeBrowserCloseExit kind in Enum.GetValues<NativeBrowserCloseExit>())
        {
            var facts = new NativeBrowserCloseObservation { OriginalBrowserSignaled = signaled, SdkProcessCount = count, SdkQueryHresult = hr, ExitKind = kind };
            Check(facts.Clean == (signaled == true && count == 0 && hr == 0 && kind == NativeBrowserCloseExit.Normal));
        }
    }
    private static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Native browser safe diagnostic mismatch."); }
}
