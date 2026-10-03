using AutumnOS.Launcher;
using AutumnOS.UpdateProtocol;
using Microsoft.UI.Xaml;

namespace AutumnOS.Shell;

public static class Program
{
    internal static LauncherCoordinator? Launcher { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            using var updateGuard = UpdateLaunchGuard.Enter(AppContext.BaseDirectory, args);
            // Preserve the generated WinUI entry's self-contained COM setup. No App,
            // window, InstallationRoot or WebView exists before the ownership decision.
            WinRT.ComWrappersSupport.InitializeComWrappers();
            var start = LauncherCoordinator.Enter();
            if (start.Primary is null)
            {
                Console.Error.WriteLine($"AUTUMNOS_LAUNCHER role={(start.ExitCode == 0 ? "forwarded" : "failed")} pid={Environment.ProcessId} code={start.ExitCode} outcome={start.Outcome}");
                return start.ExitCode;
            }
            using (Launcher = start.Primary)
            {
                Console.Error.WriteLine($"AUTUMNOS_LAUNCHER role=primary pid={Environment.ProcessId} code=0 protocol=1");
                LauncherDiagnostics.Attach(Launcher);
                // Only the elected owner inspects business resources. A forwarding copy
                // can still recall an existing good instance without loading any XAML.
                foreach (string resource in new[] { "AutumnOS.Client.pri", "App.xbf", "MainWindow.xbf" })
                    if (!File.Exists(Path.Combine(AppContext.BaseDirectory, resource)))
                        throw new InvalidOperationException("LAUNCHER_RESOURCES_MISSING");
                Application.Start(parameters =>
                {
                    var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
                    SynchronizationContext.SetSynchronizationContext(context);
                    _ = new App();
                });
            }
            return 0;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            // Forwarders never write a log/config/data directory. A caller can capture
            // this bounded diagnostic and exit code; arbitrary exception text is omitted.
            Console.Error.WriteLine($"AUTUMNOS_LAUNCHER role=failed pid={Environment.ProcessId} code=22 error=LAUNCHER_START_FAILED");
            return 22;
        }
        finally { Launcher = null; }
    }
}
