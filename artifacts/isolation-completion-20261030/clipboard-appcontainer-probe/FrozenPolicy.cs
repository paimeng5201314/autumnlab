namespace AutumnOS.ClipboardAppContainerProbe;

// Copied from the production policy and checked by the owner's build script.
// This host does not execute the production RTC challenge or claim admission.
internal static class FrozenPolicy
{
    internal const string PolicyId = "autumn-local-web-v2";
    internal const string RequiredBrowserVersion = "154.0.4258.53";
    internal const string BrowserArguments = "--force-renderer-accessibility --disable-features=WebRtcHideLocalIpsWithMdns";
    internal const string ContentSecurityPolicy = "default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self'; font-src 'self'; connect-src 'none'; frame-src 'none'; child-src 'none'; worker-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
    internal const string ConnectionAllowlist = "(response-origin); redirects=block; webrtc=block";
    internal const string DnsPrefetchControl = "off";
    internal const string PermissionsPolicy = "camera=(), microphone=(), geolocation=(), usb=(), serial=(), bluetooth=(), display-capture=(), clipboard-read=(), clipboard-write=()";
    internal const string ResponseHeaders = "Content-Security-Policy: " + ContentSecurityPolicy
        + "\r\nConnection-Allowlist: " + ConnectionAllowlist + "\r\nX-DNS-Prefetch-Control: " + DnsPrefetchControl
        + "\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer"
        + "\r\nPermissions-Policy: " + PermissionsPolicy;
}
