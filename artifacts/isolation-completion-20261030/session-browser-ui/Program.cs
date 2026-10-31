using AutumnOS.OwnedBrowserParent;
using Microsoft.UI.Xaml;

namespace AutumnOS.SessionBrowserUI;

internal static class Program
{
    internal static int ExitCode = 1;
    [STAThread]
    private static int Main(string[] args)
    {
        // No WinRT setup, native window, report, CPWL or probe for no arguments.
        if (args.Length == 0) return 0;
        bool startupOnly = args.Length == 3 && args[0] == "--ui-startup-check" && Path.IsPathFullyQualified(args[1]) && Path.IsPathFullyQualified(args[2]);
        if (!startupOnly && (args.Length != 5 || args[0] != "--execute" || !Path.IsPathFullyQualified(args[1]) || !Path.IsPathFullyQualified(args[2]))) return 2;
        UiStartupCheck? check = null;
        NativeEndTrustControl? nativeControl = null;
        try
        {
            if (startupOnly) check = new UiStartupCheck(args[2]);
            check?.Stage("manifest_load");
            using SessionInputs input = ManifestInputs.LoadForViewer(args[1]);
            if (!startupOnly) nativeControl = NativeEndTrustControl.Load(input, args[1], args[2], args[3], args[4]);
            check?.Bind(input.source_snapshot_id, input.manifest_sha256);
            check?.Stage("initialize_COM_wrappers");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            check?.Stage("before_Application_Start");
            Application.Start(parameters =>
            {
                check?.Stage("Application_Start_callback");
                SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                    Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
                _ = new ViewerApp(input, Path.GetFullPath(args[1]), check, nativeControl);
            });
            if (check is not null) return check.Passed ? 0 : 1;
            return ExitCode;
        }
        catch (Exception error) { check?.Fail("managed_startup", error); return 3; }
        finally { nativeControl?.Dispose(); check?.Dispose(); }
    }
}
public sealed partial class ViewerApp : Application
{
    private readonly SessionInputs input;
    private readonly string manifest;
    private ViewerWindow? window;
    private readonly UiStartupCheck? check;
    private readonly NativeEndTrustControl? nativeControl;
    internal ViewerApp(SessionInputs input, string manifest, UiStartupCheck? check, NativeEndTrustControl? nativeControl)
    {
        this.input = input; this.manifest = manifest; this.check = check; this.nativeControl = nativeControl;
        UnhandledException += (_, args) => check?.Fail("WinUI_unhandled_exception", args.Exception);
        check?.Stage("before_InitializeComponent");
        InitializeComponent();
        check?.Stage("InitializeComponent_completed");
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    { check?.Stage("before_ViewerWindow_constructor"); window = new ViewerWindow(input, manifest, check, nativeControl); check?.WindowCreated(); window.Activate(); }
}
