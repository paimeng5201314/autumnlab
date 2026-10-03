using Microsoft.UI.Xaml;

namespace AutumnOS.Shell;

public partial class App : Application
{
    private Window? window;
    public App() => InitializeComponent();
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        window = new MainWindow();
        var launcher = Program.Launcher ?? throw new InvalidOperationException("Launcher ownership is required.");
        window.Closed += (_, _) => launcher.BeginStop();
        window.Activate();
        launcher.SetReady((_, token) => LauncherWindowActivation.RecallAsync(window, launcher, token));
        launcher.Observe("window_ready", 0, new(default, WinRT.Interop.WindowNative.GetWindowHandle(window).ToInt64()));
    }
}
