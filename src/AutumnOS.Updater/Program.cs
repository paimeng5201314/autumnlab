using System.Diagnostics;
using AutumnOS.Update;
using AutumnOS.UpdateProtocol;

namespace AutumnOS.Updater;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string root = UpdateFiles.Root(AppContext.BaseDirectory);
        try
        {
            if (args.Length == 2 && args[0] == "--handoff") UpdateTransaction.RunHandoff(root, args[1]);
            else if (args.Length == 1 && args[0] == "--recover") UpdateTransaction.Recover(root);
            else throw new IOException("UPDATE_UPDATER_ARGUMENT_REJECTED");
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            string code = error is UpdateException updateError ? updateError.Code : error is IOException && error.Message.StartsWith("UPDATE_", StringComparison.Ordinal) ? error.Message : "UPDATE_FAILED";
            var failure = UpdateTransaction.LastFailure;
            try { UpdateFiles.Write(Path.Combine(UpdateFiles.Work(root), "last-error.json"), new { code, at = DateTimeOffset.UtcNow,
                errorType = failure?.ErrorType ?? error.GetBaseException().GetType().Name,
                hresult = failure?.HResult ?? $"0x{error.GetBaseException().HResult:X8}",
                phase = failure?.Phase ?? "handoff-or-health", file = failure?.File, transactionId = failure?.TransactionId }); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            Console.Error.WriteLine("AUTUMNOS_UPDATER " + code);
            if (code is "UPDATE_ROLLED_BACK" or "UPDATE_APPLY_ABORTED_FILES_BUSY")
            {
                // The transaction has restored the recorded old files and released all
                // ownership. Relaunch through the original stable user entry.
                try { Process.Start(new ProcessStartInfo(Path.Combine(root, "AutumnOS.exe")) { UseShellExecute = false, WorkingDirectory = root }); }
                catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception) { }
            }
            return code == "UPDATE_ROLLED_BACK" ? 32 : 31;
        }
    }
}
