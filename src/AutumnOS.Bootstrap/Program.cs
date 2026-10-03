using System.Diagnostics;
using System.Runtime.InteropServices;
using AutumnOS.UpdateProtocol;

namespace AutumnOS.Bootstrap;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length != 0) throw new IOException("UPDATE_BOOTSTRAP_ARGUMENT_REJECTED");
            string root = UpdateFiles.Root(AppContext.BaseDirectory);
            if (UpdateMutexLease.MaintenanceActive())
            {
                MessageBoxW(0, "AutumnOS 正在安全更新或恢复。请等待完成后再次打开；用户数据将保留。", "Lab Chronicles AutumnOS · 制作人：派蒙", 0x40);
                return 30;
            }
            if (UpdateTransaction.NeedsRecovery(root))
            {
                var recovery = new ProcessStartInfo(Path.Combine(root, "AutumnOS.Updater.exe")) { UseShellExecute = false, WorkingDirectory = root, CreateNoWindow = true };
                recovery.ArgumentList.Add("--recover");
                using var updater = Process.Start(recovery) ?? throw new IOException("UPDATE_RECOVERY_START_FAILED");
                if (!updater.WaitForExit(180000) || updater.ExitCode != 0) throw new IOException("UPDATE_RECOVERY_REQUIRED");
            }
            var launch = new ProcessStartInfo(Path.Combine(root, "AutumnOS.Client.exe")) { UseShellExecute = false, WorkingDirectory = root };
            using var child = Process.Start(launch) ?? throw new IOException("UPDATE_CLIENT_START_FAILED");
            Console.Error.WriteLine($"AUTUMNOS_BOOTSTRAP role=child pid={Environment.ProcessId} child={child.Id} startTicks={child.StartTime.ToUniversalTime().Ticks}");
            child.WaitForExit(); return child.ExitCode;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            string code = e is IOException && e.Message.StartsWith("UPDATE_", StringComparison.Ordinal) ? e.Message : "UPDATE_BOOTSTRAP_FAILED";
            MessageBoxW(0, code + "\n原版本备份和 AutumnOS_Data 已保留。请查看 .autumnos-update 中的错误报告。", "Lab Chronicles AutumnOS · 制作人：派蒙", 0x10);
            return 31;
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int MessageBoxW(nint owner, string text, string title, uint type);
}
