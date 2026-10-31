namespace AutumnOS.SessionBrowserProtocol;

// This state contains only keys whose actual native down was accepted by the
// bounded transport. It neither creates events nor bypasses that transport.
internal sealed class NativeKeyDispatchGate
{
    private readonly HashSet<string> held = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Navigation = new(StringComparer.Ordinal)
    { "Backspace", "Delete", "ArrowLeft", "ArrowRight", "ArrowUp", "ArrowDown", "Home", "End", "PageUp", "PageDown", "Enter", "Tab", "Escape" };
    private static readonly HashSet<string> SurfaceKeys = new(Navigation, StringComparer.Ordinal)
    { "Space", "Control", "Shift", "Alt" };
    internal int HeldCount => held.Count;

    internal bool TryDispatch(bool down, string role, string? mappedKey, bool ready, bool closing, bool composing,
        bool clipboardBlocked, Func<bool> enqueue)
    {
        if (!ready || closing || role is not ("surface" or "proxy" or "other") || !AllowedKey(mappedKey)) return false;
        string key = mappedKey!;
        if (down)
        {
            if (clipboardBlocked || composing || role == "other" || role == "proxy" && !Navigation.Contains(key)) return false;
            if (!enqueue()) return false;
            held.Add(key); return true; // Repeat down is allowed; the matching up remains single.
        }
        // A physical release of a previously captured key may arrive after
        // focus changed inside this root, or while an IME transaction is active.
        // Clipboard filtering cannot swallow that already-owned release.
        if (!held.Contains(key) || !enqueue()) return false;
        held.Remove(key); return true;
    }
    private static bool AllowedKey(string? key) => key is not null && (SurfaceKeys.Contains(key) || key.Length == 1 &&
        (key[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9'));
}
