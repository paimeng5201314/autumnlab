using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;
using AutumnOS.SessionBrowserProtocol;

namespace AutumnOS.OwnedBrowserParent;

internal sealed class OwnedAssertionException(string code) : Exception(code) { internal string Code { get; } = code; }

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // Invalid/no-argument invocation cannot enter CPWL, USER or browser code.
        if (args.Length == 0) return 0;
        if (args.Length == 2 && args[0] == "--self-test") return SessionProtocolSelfTests.Run(args[1]);
        try
        {
            if (args.Length == 3 && args[0] == "--native-ime-control" && Path.IsPathFullyQualified(args[1]) && Path.IsPathFullyQualified(args[2]))
            { using SessionInputs input = ManifestInputs.Load(args[1]); return NativeImeControl.Run(input, args[1], args[2]); }
            if (args.Length == 5 && args[0] == "--execute" && Nonce(args[2]) && uint.TryParse(args[4], out uint uiPid))
            { using SessionInputs input = ManifestInputs.Load(args[1]); return SessionParent.Run(input, args[1], args[2], args[3], uiPid).GetAwaiter().GetResult(); }
            if (args.Length == 6 && args[0] == "--station-host" && Nonce(args[2]) && uint.TryParse(args[4], out uint corePid))
                return StationHost.Run(args[1], args[2], args[3], corePid, args[5]).GetAwaiter().GetResult();
            if (args.Length == 9 && args[0] == "--browser-host" && Nonce(args[1]) && uint.TryParse(args[6], out uint hostPid) &&
                ulong.TryParse(args[7], out ulong creation) && ulong.TryParse(args[8], out ulong waitHandle))
                return BrowserHost.Run(args[1], args[2], args[3], args[4], args[5], hostPid, creation, waitHandle);
        }
        catch { return 1; } // Never log arbitrary exception/credential/path strings.
        return 2;
    }
    internal static bool Nonce(string text) => text.Length == 32 && text.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static string FailureCode(Exception error) => error.GetType().Name + ":0x" + unchecked((uint)error.HResult).ToString("x8") +
        (error is Win32Exception native ? ":win32=" + native.NativeErrorCode : "") + (error is OwnedNativeException owned ? ":stage=" + owned.FixedStage : "") +
        (error is OwnedReportWriteException report ? ":report_stage=" + report.Stage + ":inner=" + report.InnerType + ":inner_hresult=" + unchecked((uint)report.InnerHResult).ToString("x8") + ":attempts=" + report.Attempts : "");
    internal static string HashFile(string path) { using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexStringLower(SHA256.HashData(file)); }
    internal static void SaveOwnedReport(string path, object value)
    {
        OwnedReportFile.SaveExisting(path, value);
    }
}
