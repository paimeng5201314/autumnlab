using AutumnOS.Contracts;

namespace AutumnOS.Runtime;

/// <summary>Host-only browser cache identity. Persistent application data stays in AccountDataStore.</summary>
public static class WebViewProfileDirectory
{
    public static string ForBinding(string absoluteRuntimeDirectory, string accountKey, string applicationBindingKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(absoluteRuntimeDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(accountKey);
        if (!Path.IsPathFullyQualified(absoluteRuntimeDirectory)) throw new ArgumentException("An absolute runtime directory is required.", nameof(absoluteRuntimeDirectory));
        if (accountKey.Length > 256) throw new ArgumentException("Invalid host account binding.", nameof(accountKey));
        if (applicationBindingKey is not { Length: 64 } || applicationBindingKey.Any(c => c is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
            throw new ArgumentException("Invalid host application binding.", nameof(applicationBindingKey));

        // Two complete hashes, with a versioned domain separator, retain account/source/app isolation
        // without nesting two 64-character directory names. Never use names or untrusted paths here.
        string identity = RuntimeApplication.Hash("autumnos.webview-profile.v1\0" + RuntimeApplication.Hash(accountKey) + "\0" + applicationBindingKey);
        return Path.Combine(Path.GetFullPath(absoluteRuntimeDirectory), "wv", identity);
    }
}
