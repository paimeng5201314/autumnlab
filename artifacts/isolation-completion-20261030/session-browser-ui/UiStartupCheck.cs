using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;

namespace AutumnOS.SessionBrowserUI;

// Explicit UI-only mode. It never calls SessionClient.RunAsync, starts Core,
// creates a session case, requests logon, or loads a browser/profile.
internal sealed class UiStartupCheck : IDisposable
{
    private readonly FileStream output;
    private readonly StartupReport report = new();
    internal bool Passed => report.status == "completed_native_WinUI_startup_only" && report.failure_stage is null;
    internal UiStartupCheck(string path)
    {
        if (!Path.IsPathFullyQualified(path) || Path.GetFileName(path).IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException("ABSOLUTE_PLAIN_FRESH_STARTUP_REPORT_REQUIRED");
        AutumnOS.OwnedBrowserParent.OwnedFiles.FixedLocal(path); AutumnOS.OwnedBrowserParent.OwnedFiles.PlainAncestors(path);
        output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        Stage("report_created_before_WinUI_start");
    }
    internal void Bind(string source, string manifest) { report.source_snapshot_id = source; report.manifest_sha256 = manifest; Save(); }
    internal void Stage(string stage) { report.stage = stage; Save(); }
    internal void WindowCreated() { report.window_created = true; Stage("window_created"); }
    internal void Fail(string stage, Exception error)
    {
        report.failure_stage ??= stage; report.failure_type ??= error.GetType().Name; report.failure_hresult ??= error.HResult;
        report.status = "failed_native_WinUI_startup_only"; Save();
    }
    internal async Task VerifyLoadedAsync(Window window)
    {
        report.root_loaded = true; Stage("root_loaded");
        report.metadata_provider_available = Application.Current is IXamlMetadataProvider;
        report.own_APP_XBF_present = File.Exists(Path.Combine(AppContext.BaseDirectory, "App.xbf"));
        report.own_PRI_present = File.Exists(Path.Combine(AppContext.BaseDirectory, "AutumnOS.SessionBrowserUI.pri"));
        if (!report.metadata_provider_available || !report.own_APP_XBF_present || !report.own_PRI_present)
            throw new InvalidDataException("COMPILED_APPLICATION_METADATA_AND_RESOURCES_REQUIRED");
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<object> handler = null!;
        handler = (_, _) => { CompositionTarget.Rendering -= handler; rendered.TrySetResult(); };
        Stage("await_real_Rendering");
        try
        {
            CompositionTarget.Rendering += handler;
            if (await Task.WhenAny(rendered.Task, Task.Delay(5000)) != rendered.Task) throw new TimeoutException("REAL_RENDERING_NOT_OBSERVED");
            await rendered.Task; report.rendering_observed = true;
        }
        finally { CompositionTarget.Rendering -= handler; }
        Stage("verify_own_visible_HWND");
        IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        uint thread = GetWindowThreadProcessId(hwnd, out uint pid);
        report.original_hwnd_visible = hwnd != IntPtr.Zero && thread != 0 && pid == Environment.ProcessId && IsWindowVisible(hwnd);
        if (!report.original_hwnd_visible) throw new InvalidDataException("ORIGINAL_OWN_VISIBLE_HWND_REQUIRED");
        if (window.Content is not FrameworkElement element || element.ActualWidth <= 0 || element.ActualHeight <= 0)
            throw new InvalidDataException("ACTUAL_LOADED_ROOT_LAYOUT_REQUIRED");
        Stage("normal_window_close_requested");
    }
    internal void WindowClosed()
    {
        report.window_closed = true;
        bool passed = report.failure_stage is null && report.window_created && report.root_loaded && report.rendering_observed && report.original_hwnd_visible &&
            report.metadata_provider_available && report.own_APP_XBF_present && report.own_PRI_present;
        report.status = passed ? "completed_native_WinUI_startup_only" : "failed_native_WinUI_startup_only"; Stage("window_closed");
    }
    private void Save()
    {
        report.updated_utc = DateTimeOffset.UtcNow;
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions { WriteIndented = true });
        output.Position = 0; output.SetLength(0); output.Write(bytes); output.Flush(true);
    }
    public void Dispose() => output.Dispose();
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll", SetLastError = true)] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(IntPtr hwnd);
}
internal sealed class StartupReport
{
    public int schema_version => 1;
    public string producer => "派蒙";
    public string status { get; set; } = "running_native_WinUI_startup_only";
    public string source_snapshot_id { get; set; } = "not_bound";
    public string manifest_sha256 { get; set; } = "not_bound";
    public int original_process_id => Environment.ProcessId;
    public string stage { get; set; } = "report_created";
    public string? failure_stage { get; set; }
    public string? failure_type { get; set; }
    public int? failure_hresult { get; set; }
    public DateTimeOffset updated_utc { get; set; }
    public bool window_created { get; set; }
    public bool root_loaded { get; set; }
    public bool rendering_observed { get; set; }
    public bool original_hwnd_visible { get; set; }
    public bool window_closed { get; set; }
    public bool metadata_provider_available { get; set; }
    public bool own_APP_XBF_present { get; set; }
    public bool own_PRI_present { get; set; }
    public bool core_started => false;
    public int netonly_calls => 0;
    public int browser_starts => 0;
    public int appcontainer_profiles => 0;
    public int clipboard_calls => 0;
    public int UAC_requests => 0;
    public string scope => "same_native_WinUI_ViewerWindow_root_only_no_SessionClient_Run_or_Core_or_logon";
    public string production_gate => "blocked";
}
