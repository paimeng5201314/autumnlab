using System.Diagnostics;
using System.Runtime.InteropServices;
using AutumnOS.UpdateProtocol;
using AutumnOS.Startup;
using System.Reflection;

namespace AutumnOS.Bootstrap;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string stage = "validate_arguments", target = "AutumnOS.exe", data = "";
        try
        {
            if (args.Length != 0) throw new IOException("UPDATE_BOOTSTRAP_ARGUMENT_REJECTED");
            stage = "validate_root";
            string root = UpdateFiles.Root(AppContext.BaseDirectory);
            data = UpdateFiles.DataRoot(root);
            stage = "maintenance_check";
            if (UpdateMutexLease.MaintenanceActive())
            {
                MessageBoxW(0, "AutumnOS 正在安全更新或恢复。请等待完成后再次打开；用户数据将保留。", "Lab Chronicles AutumnOS · 制作人：派蒙", 0x40);
                return 30;
            }
            stage = "recovery_check";
            if (UpdateTransaction.NeedsRecovery(root))
            {
                stage = "recovery_start"; target = "AutumnOS.Updater.exe";
                var recovery = new ProcessStartInfo(Path.Combine(root, "AutumnOS.Updater.exe")) { UseShellExecute = false, WorkingDirectory = root, CreateNoWindow = true };
                recovery.ArgumentList.Add("--recover");
                using var updater = Process.Start(recovery) ?? throw new IOException("UPDATE_RECOVERY_START_FAILED");
                stage = "recovery_wait";
                if (!updater.WaitForExit(180000) || updater.ExitCode != 0) throw new IOException("UPDATE_RECOVERY_REQUIRED");
            }
            stage = "client_start"; target = "AutumnOS.Client.exe";
            var launch = new ProcessStartInfo(Path.Combine(root, target)) { UseShellExecute = false, WorkingDirectory = root };
            using var child = Process.Start(launch) ?? throw new IOException("UPDATE_CLIENT_START_FAILED");
            stage = "client_wait";
            Console.Error.WriteLine($"AUTUMNOS_BOOTSTRAP role=child pid={Environment.ProcessId} child={child.Id} startTicks={child.StartTime.ToUniversalTime().Ticks}");
            child.WaitForExit(); return child.ExitCode;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            var metadata = typeof(Program).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToArray();
            var failure = StartupFailure.Create(e, "bootstrap", stage, target,
                metadata.FirstOrDefault(x => x.Key == "BuildId")?.Value ?? "unknown",
                metadata.FirstOrDefault(x => x.Key == "SourceSnapshotId")?.Value ?? "unknown");
            string report = failure.TryWrite(data);
            try { Console.Error.WriteLine($"AUTUMNOS_BOOTSTRAP role=failed stage={stage} code={failure.Code} native={failure.NativeErrorCode} hresult={failure.HResult}"); }
            catch (Exception outputError) when (outputError is not OutOfMemoryException) { }
            MessageBoxW(0, failure.Message(report), "Lab Chronicles AutumnOS · 制作人：派蒙", 0x10);
            return 31;
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(nint owner, string text, string title, uint type);
}
