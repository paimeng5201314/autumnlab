using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace AutumnOS.NativeHost.Windows;

internal static class NativeBrowserRole
{
    internal static bool IsInstalled => true;
    internal static string Readiness => "private_browser_requires_original_station_job_and_verified_document";
    internal const int UnsupportedExitCode = 78;

    internal static int Run(string[] args)
    {
        if (args.Length != 9 || args[0] != "--browser-host" ||
            !NativeHostBinding.IsLowerSha256(args[1]) || !NativeHostBinding.IsLowerSha256(args[2]) ||
            !Path.IsPathFullyQualified(args[3]) || !Directory.Exists(args[3]) ||
            !uint.TryParse(args[6], NumberStyles.None, CultureInfo.InvariantCulture, out uint pid) ||
            !ulong.TryParse(args[7], NumberStyles.None, CultureInfo.InvariantCulture, out ulong created) ||
            !ulong.TryParse(args[8], NumberStyles.None, CultureInfo.InvariantCulture, out ulong wait) ||
            Thread.CurrentThread.GetApartmentState() != ApartmentState.STA) return 64;
        using var current = SessionNative.Current();
        TokenFacts facts = ProcessAudit.ReadToken(current.DangerousGetHandle(), null);
        if (facts.is_appcontainer != 1 || facts.integrity_rid != 4096 || facts.capability_count != 0)
            throw new InvalidDataException("BROWSER_LOW_ZERO_CAPABILITY_TOKEN_REQUIRED");
        using var scope = new BrowserNativeScope(args[4], args[5], pid, created, wait);
        foreach (int handleId in new[] { -10, -11, -12 })
        {
            nint standard = GetStdHandle(handleId);
            if (standard == 0 || standard == -1 || !SetHandleInformation(standard, 1, 0))
                throw new InvalidDataException("BROWSER_PRIVATE_PIPE_HANDLE_REQUIRED");
        }
        foreach (string key in Environment.GetEnvironmentVariables().Keys)
            if (key.StartsWith("WEBVIEW2_", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("BROWSER_RUNTIME_OVERRIDE_REJECTED");
        RejectPolicyOverrides();

        using var input = Console.OpenStandardInput();
        using var output = Console.OpenStandardOutput();
        var startup = NativeBrowserStartup.ReadAsync(input, args[1]).GetAwaiter().GetResult();
        scope.RequireBinding();
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware);
        using var window = new BrowserWindow(startup, args[2], args[3], pid, created, scope, input, output);
        Application.Run(window);
        return window.ExitCode;
    }

    private static void RejectPolicyOverrides()
    {
        string image = Environment.ProcessPath ?? throw new InvalidDataException("BROWSER_HOST_EXECUTABLE_REQUIRED");
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            using var root = RegistryKey.OpenBaseKey(hive, view);
            foreach (var name in new[] { "AdditionalBrowserArguments", "BrowserExecutableFolder", "UserDataFolder" })
            {
                using var setting = root.OpenSubKey(@"Software\Policies\Microsoft\Edge\WebView2\" + name, writable: false);
                if (setting is null) continue;
                foreach (string selector in new[] { "*", Path.GetFileName(image), image })
                    if (setting.GetValue(selector, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is not null)
                        throw new InvalidDataException("BROWSER_RUNTIME_OVERRIDE_REJECTED");
            }
        }
    }

    [DllImport("kernel32.dll")] private static extern nint GetStdHandle(int id);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(nint handle, uint mask, uint flags);
}
