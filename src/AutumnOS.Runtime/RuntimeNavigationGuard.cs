namespace AutumnOS.Runtime;

public enum RuntimeNavigationCompletion { Ignored, InitialPageReady, InitialPageFailed }

/// <summary>Host-only binding of one native initial navigation to one immutable application page.</summary>
public sealed class RuntimeNavigationGuard
{
    private readonly object sync = new();
    private readonly string trustedPageUri;
    private ulong? initialNavigationId;
    private bool completed;
    private bool ready;

    public RuntimeNavigationGuard(string trustedPageUri)
    {
        if (!Uri.TryCreate(trustedPageUri, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("An absolute trusted HTTPS application page is required.", nameof(trustedPageUri));
        this.trustedPageUri = trustedPageUri;
    }

    public bool IsReady { get { lock (sync) return ready; } }

    public bool OnStarting(ulong navigationId, string requestUri)
    {
        lock (sync)
        {
            if (completed || !string.Equals(requestUri, trustedPageUri, StringComparison.Ordinal)) return false;
            initialNavigationId ??= navigationId;
            return initialNavigationId == navigationId;
        }
    }

    public RuntimeNavigationCompletion OnCompleted(ulong navigationId, bool isSuccess)
    {
        lock (sync)
        {
            // Canceling a later forbidden navigation also produces NavigationCompleted(IsSuccess=false).
            // It must not crash/recreate the already running page or release the game's resource leases.
            if (initialNavigationId != navigationId || completed) return RuntimeNavigationCompletion.Ignored;
            completed = true;
            ready = isSuccess;
            return isSuccess ? RuntimeNavigationCompletion.InitialPageReady : RuntimeNavigationCompletion.InitialPageFailed;
        }
    }
}
