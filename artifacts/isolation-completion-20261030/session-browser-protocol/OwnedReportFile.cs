using System.Diagnostics;
using System.Text.Json;

namespace AutumnOS.SessionBrowserProtocol;

internal sealed class OwnedReportWriteException : IOException
{
    public string Stage { get; }
    public string InnerType { get; }
    public int InnerHResult { get; }
    public int Attempts { get; }
    internal OwnedReportWriteException(string stage, Exception error, int attempts)
        : base("OWNED_REPORT_WRITE_FAILED_" + stage)
    {
        Stage = stage; string type = error.GetType().Name;
        InnerType = type.Length is > 0 and <= 128 && type.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') ? type : "OtherException";
        InnerHResult = error.HResult; HResult = error.HResult; Attempts = attempts;
        // Do not retain InnerException: its Message/ToString can contain a
        // real user path. Only these safe scalar facts cross the report wire.
    }
}

internal static class OwnedReportFile
{
    private const int ReplaceBudgetMilliseconds = 2000, ReplaceDelayMilliseconds = 20, MaximumAttempts = 101;
    // The caller owns this existing report and serializes its writes. A
    // successful replacement leaves readers of the previous object intact.
    // No delete/move/create-target fallback is permitted on any failure.
    internal static int SaveExisting(string path, object value)
    {
        string stage = "replace"; int attempts = 0; FileStream? file = null;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.Directory) != 0)
                throw new IOException("OWNED_REPORT_EXISTING_FILE_REQUIRED");
            stage = "temp_open";
            string pending = path + ".pending";
            file = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.Read);
            stage = "serialize"; JsonSerializer.Serialize(file, value, new JsonSerializerOptions { WriteIndented = true });
            stage = "flush"; file.Flush(true);
            stage = "close"; file.Dispose(); file = null;
            stage = "replace"; var budget = Stopwatch.StartNew(); Exception? lastReplaceFailure = null;
            while (true)
            {
                if (lastReplaceFailure is not null && budget.ElapsedMilliseconds >= ReplaceBudgetMilliseconds) throw lastReplaceFailure;
                attempts++;
                try { File.Replace(pending, path, null, false); return attempts; }
                catch (Exception error) when (RetryableReplace(error) && attempts < MaximumAttempts && budget.ElapsedMilliseconds < ReplaceBudgetMilliseconds)
                {
                    lastReplaceFailure = error; Thread.Sleep(ReplaceDelayMilliseconds);
                }
            }
        }
        catch (Exception error)
        {
            var failure = new OwnedReportWriteException(stage, error, attempts);
            if (file is not null) { try { file.Dispose(); } catch (Exception) { /* Preserve the original failed stage and HRESULT. */ } }
            throw failure;
        }
    }
    private static bool RetryableReplace(Exception error)
    {
        uint code = unchecked((uint)error.HResult);
        return (code & 0xffff0000U) == 0x80070000U && (code & 0xffffU) is 5 or 32 or 33;
    }
}
