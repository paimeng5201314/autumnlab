namespace AutumnOS.Shell;

// Managed event contracts only: this file can be linked into net10.0 tests
// without WinUI/WebView2. Native getters are never evaluated by construction.
// The parent's original guard/try controls whether and when each getter runs;
// getter exceptions deliberately propagate unchanged to that same boundary.
internal enum RuntimeViewResourceContext { Document, Script, Other }
internal interface IRuntimeViewDeferral { void Complete(); }
internal abstract class RuntimeViewResourceRequestedEventArgs : EventArgs
{
    public abstract string Uri { get; }
    public abstract string Method { get; }
    public abstract RuntimeViewResourceContext ResourceContext { get; }
    public abstract IRuntimeViewDeferral GetDeferral();
    public abstract void Respond(byte[]? body, int status, string reason, string headers);
}
internal sealed class RuntimeViewNavigationStartingEventArgs(Func<string> readUri, Func<ulong> readNavigationId,
    Func<bool> getCancel, Action<bool> setCancel) : EventArgs
{
    public string Uri => readUri();
    public ulong NavigationId => readNavigationId();
    public bool Cancel { get => getCancel(); set => setCancel(value); }
}
internal sealed class RuntimeViewSourceChangedEventArgs(Func<string?> readSource, Func<bool> readIsNewDocument) : EventArgs
{
    public string? Source => readSource();
    public bool IsNewDocument => readIsNewDocument();
}
internal sealed class RuntimeViewContentLoadingEventArgs(Func<ulong> readNavigationId, Func<bool> readIsErrorPage) : EventArgs
{
    public ulong NavigationId => readNavigationId();
    public bool IsErrorPage => readIsErrorPage();
}
internal sealed class RuntimeViewNavigationCompletedEventArgs(Func<ulong> readNavigationId, Func<bool> readIsSuccess) : EventArgs
{
    public ulong NavigationId => readNavigationId();
    public bool IsSuccess => readIsSuccess();
}
internal sealed class RuntimeViewProcessFailedEventArgs(string kind, string reason, int exitCode) : EventArgs
{ public string Kind { get; } = kind; public string Reason { get; } = reason; public int ExitCode { get; } = exitCode; }
internal sealed class RuntimeViewWebMessageEventArgs(Func<string> readSource, Func<string> readJson) : EventArgs
{
    public string Source => readSource();
    public string WebMessageAsJson => readJson();
}
internal sealed class RuntimeViewBrowserExitedEventArgs(uint browserProcessId, string exitKind) : EventArgs
{ public uint BrowserProcessId { get; } = browserProcessId; public string ExitKind { get; } = exitKind; }
