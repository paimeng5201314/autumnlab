using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using AutumnOS.Launcher;
using AutumnOS.UpdateProtocol;
using Microsoft.Win32;

namespace AutumnOS.Setup;

internal static class Program
{
    private const string Product = "Lab Chronicles AutumnOS · 制作人：派蒙";

    [STAThread]
    private static int Main(string[] args)
    {
        bool silent = args.Contains("--silent");
        try
        {
            if (!OperatingSystem.IsWindows()) return 2;
            bool uninstall = args.Contains("--uninstall");
            string leaf = "AutumnOS";
            int index = Array.IndexOf(args, "--test-name");
            if (index >= 0)
            {
                if (index + 1 >= args.Length || args[index + 1].Length is < 1 or > 60 || args[index + 1].Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
                    throw new IOException("SETUP_INVALID_TEST_NAME");
                leaf = "AutumnOS-Test-" + args[index + 1];
            }
            if (args.Any(a => !new[] { "--silent", "--uninstall", "--test-name", index >= 0 && index + 1 < args.Length ? args[index + 1] : "" }.Contains(a)))
                throw new IOException("SETUP_ARGUMENT_REJECTED");
            string parent = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            string root = Path.GetFullPath(Path.Combine(parent, leaf));
            Safe(root);
            if (!silent && MessageBoxW(0, (uninstall ? "卸载受管理的程序文件，保留 AutumnOS_Data 和未知文件。" : "安装本地开发版到当前用户目录；不安装系统组件。") + "\n\n" + root + "\n\n此构建没有 Windows 发布者签名。继续？", Product, 0x24) != 6) return 1;
            // No business process may be alive while installation files are changed.
            using var maintenance = UpdateMutexLease.AcquireMaintenance();
            using var business = new Mutex(false, LauncherCoordinator.ScopeName(), new NamedWaitHandleOptions { CurrentUserOnly = true, CurrentSessionOnly = true });
            bool owned;
            try { owned = business.WaitOne(0); } catch (AbandonedMutexException) { owned = true; }
            if (!owned) throw new IOException("SETUP_CLOSE_AUTUMNOS_FIRST");
            try
            {
                Directory.CreateDirectory(root); Safe(root);
                using var rootLease = UpdateRootLease.AcquireExclusive(root);
                if (UpdateTransaction.NeedsRecovery(root)) throw new IOException("SETUP_FINISH_UPDATE_RECOVERY_FIRST");
                if (uninstall) Uninstall(root, leaf);
                else Install(root, leaf);
            }
            finally { business.ReleaseMutex(); }
            if (!silent) MessageBoxW(0, uninstall ? "卸载完成。存档、账号数据和未知文件已保留。" : "安装完成。请从开始菜单打开 AutumnOS。", Product, 0x40);
            return 0;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            string code = e is IOException && e.Message.StartsWith("SETUP_", StringComparison.Ordinal) ? e.Message : "SETUP_FAILED_OR_PATH_BUSY";
            if (!silent) MessageBoxW(0, code + "\n已保留原有数据。请正常关闭客户端后重试；不需要管理员权限。", Product, 0x10);
            Console.Error.WriteLine(code);
            return 2;
        }
    }

    private static void Install(string root, string leaf)
    {
        using Stream payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("AutumnOS.Setup.payload.zip") ?? throw new IOException("SETUP_PAYLOAD_MISSING");
        string expected = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == "SetupPayloadSha256").Value ?? "";
        SetupTransaction.InstallFiles(root, payload, expected, Environment.ProcessPath!);
        string uninstaller = Path.Combine(root, "AutumnOS.Uninstall.exe");
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + leaf);
        if (key.GetValue("InstallLocation") is string prior && !string.Equals(prior, root, StringComparison.OrdinalIgnoreCase)) throw new IOException("SETUP_REGISTRATION_COLLISION");
        key.SetValue("DisplayName", Product); key.SetValue("Publisher", "派蒙（制作人，未签名开发版）");
        key.SetValue("InstallLocation", root);
        key.SetValue("UninstallString", "\"" + uninstaller + "\" --uninstall" + (leaf == "AutumnOS" ? "" : " --test-name " + leaf[14..]));
        key.SetValue("NoModify", 1); key.SetValue("NoRepair", 1);
        // An ordinary .url shortcut avoids script execution and COM automation at install time.
        string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), leaf + ".url");
        Safe(menu);
        if (!File.Exists(menu)) File.WriteAllText(menu, "[InternetShortcut]\r\nURL=" + new Uri(Path.Combine(root, "AutumnOS.exe")).AbsoluteUri + "\r\n");
        SetupTransaction.MarkRegistrationComplete(root);
    }

    private static void Uninstall(string root, string leaf)
    {
        SetupTransaction.UninstallFiles(root);
        using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + leaf))
            if (key?.GetValue("InstallLocation") is string prior && !string.Equals(prior, root, StringComparison.OrdinalIgnoreCase)) throw new IOException("SETUP_REGISTRATION_COLLISION");
        Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\" + leaf, false);
        string menu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), leaf + ".url"); Safe(menu);
        string expected = "[InternetShortcut]\r\nURL=" + new Uri(Path.Combine(root, "AutumnOS.exe")).AbsoluteUri + "\r\n";
        if (File.Exists(menu) && File.ReadAllText(menu) == expected) File.Delete(menu);
        // Preserve receipt and the running uninstaller: no delayed arbitrary command or recursive root delete.
    }

    private static void Safe(string path) => SetupTransaction.Safe(path);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(nint window, string text, string caption, uint type);
}

