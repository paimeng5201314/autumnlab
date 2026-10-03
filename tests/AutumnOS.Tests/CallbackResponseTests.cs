using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AutumnOS.Contracts;
using AutumnOS.Identity;

namespace AutumnOS.Tests;

/// <summary>Security and wire-format checks for fixed callback documents. No browser or provider authentication is simulated as success.</summary>
[SupportedOSPlatform("windows")]
public static class CallbackResponseTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        foreach (bool accepted in new[] { false, true })
            yield return ($"T03 callback page: {(accepted ? "received" : "invalid")} document permits only its exact inline CSS hash", () =>
            {
                CallbackPageContent page = CallbackResponsePage.Create(accepted);
                VerifyPolicy(page.Html, page.ContentSecurityPolicy);
                string decoded = WebUtility.HtmlDecode(page.Html);
                Check(decoded.Contains(BrandInfo.ProductName, StringComparison.Ordinal) && decoded.Contains(BrandInfo.ProducerCredit, StringComparison.Ordinal),
                    "Callback document lost centralized product or producer branding.");
                Check(!Regex.IsMatch(page.Html, @"<\s*(script|iframe|object|embed|form|link)\b|<[^>]+\bon[a-z]+\s*=", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                    "Callback document contains executable content, resource links or event handlers.");
                Check(!Regex.IsMatch(page.Html, @"<meta\b[^>]*http-equiv\s*=\s*['"" ]?refresh\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
                    "Callback document redirects the user without a real host activation protocol.");
            });

        yield return ("T03 callback real TCP: received response has exact UTF8 length and never reflects code or state", () => Run(async () =>
        {
            const string state = "callback-fixture-state-unique-valid-001", code = "callback-fixture-secret-code-001";
            using LoopbackCallback listener = new(IdentitySessionTests.Options().RedirectUri);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task<string> receive = listener.ReceiveAsync(state, false, timeout.Token);
            WireResponse response = await SendAsync("/callback/?code=" + code + "&state=" + state, timeout.Token);
            Check(response.Status == 200, "Valid callback did not receive HTTP 200.");
            VerifyResponse(response, true, code, state);
            string internalResponse = await receive.WaitAsync(timeout.Token);
            Check(internalResponse.Contains(code, StringComparison.Ordinal), "Valid callback was not delivered internally.");
        }));

        yield return ("T03 callback real TCP: invalid HTTP400 and provider error never reflect hostile descriptions or URLs", () => Run(async () =>
        {
            const string state = "callback-fixture-state-unique-valid-002", wrongState = "callback-fixture-secret-wrong-state-002";
            const string code = "callback-fixture-secret-code-002";
            const string description = "fixture-private-description-002 <script>fixtureMarker()</script> 派蒙的测试文本";
            const string errorUrl = "https://fixture-never-load.invalid/private-error-002";
            string extras = "&error_description=" + Uri.EscapeDataString(description) + "&error_uri=" + Uri.EscapeDataString(errorUrl);
            using LoopbackCallback listener = new(IdentitySessionTests.Options().RedirectUri);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task<string> receive = listener.ReceiveAsync(state, false, timeout.Token);
            WireResponse invalid = await SendAsync("/callback/?code=" + code + "&state=" + wrongState + extras, timeout.Token);
            Check(invalid.Status == 400 && !receive.IsCompleted, "Invalid callback consumed the login transaction or returned success status.");
            VerifyResponse(invalid, false, code, state, wrongState, description, errorUrl, "fixture-private-description-002", "fixtureMarker");
            WireResponse denied = await SendAsync("/callback/?error=access_denied&state=" + state + extras, timeout.Token);
            Check(denied.Status == 200, "Valid provider-error callback was not acknowledged.");
            VerifyResponse(denied, true, state, description, errorUrl, "access_denied", "fixture-private-description-002", "fixtureMarker");
            // HTTP200 means only receipt: the original provider error must still reach the identity coordinator.
            Check((await receive.WaitAsync(timeout.Token)).Contains("error=access_denied", StringComparison.Ordinal), "Receipt page swallowed the provider denial.");
        }));

        yield return ("T03 callback real TCP: logout uses the same neutral receipt document without exposing transaction", () => Run(async () =>
        {
            const string state = "callback-fixture-logout-state-003";
            using LoopbackCallback listener = new(IdentitySessionTests.Options().PostLogoutRedirectUri);
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(5));
            Task<string> receive = listener.ReceiveAsync(state, true, timeout.Token);
            WireResponse response = await SendAsync("/logout-callback/?state=" + state, timeout.Token);
            Check(response.Status == 200, "Valid logout callback did not receive HTTP200.");
            VerifyResponse(response, true, state);
            await receive.WaitAsync(timeout.Token);
        }));
    }

    private static void VerifyResponse(WireResponse response, bool accepted, params string[] secrets)
    {
        Check(response.Headers.TryGetValue("Content-Type", out string? media) && media.Equals("text/html; charset=utf-8", StringComparison.OrdinalIgnoreCase), "Callback must use explicit UTF8 HTML.");
        Check(response.Headers.TryGetValue("Content-Length", out string? length) && int.TryParse(length, out int declared) && declared == response.Body.Length,
            "Callback Content-Length does not match actual UTF8 wire bytes.");
        string html = new UTF8Encoding(false, true).GetString(response.Body);
        Check(Encoding.UTF8.GetByteCount(html) == response.Body.Length, "Callback encoding is inconsistent.");
        CallbackPageContent expected = CallbackResponsePage.Create(accepted);
        Check(html == expected.Html, "Wire body is not exactly the fixed boolean-selected document.");
        Check(response.Headers.TryGetValue("Content-Security-Policy", out string? csp) && csp == expected.ContentSecurityPolicy,
            "Wire CSP differs from the fixed document policy.");
        VerifyPolicy(html, csp!);
        Check(response.Headers.GetValueOrDefault("Cache-Control") == "no-store" && response.Headers.GetValueOrDefault("Pragma") == "no-cache" &&
            response.Headers.GetValueOrDefault("Referrer-Policy") == "no-referrer" && response.Headers.GetValueOrDefault("X-Content-Type-Options") == "nosniff",
            "Callback lost no-store, no-referrer or content-sniffing protections.");
        string decoded = WebUtility.HtmlDecode(html);
        string headers = string.Join('\n', response.Headers.Select(pair => pair.Key + ":" + pair.Value));
        foreach (string secret in secrets)
            Check(!html.Contains(secret, StringComparison.Ordinal) && !decoded.Contains(secret, StringComparison.Ordinal) &&
                !html.Contains(Uri.EscapeDataString(secret), StringComparison.Ordinal) && !headers.Contains(secret, StringComparison.Ordinal),
                "Callback response reflected transaction or untrusted provider material.");
    }

    private static void VerifyPolicy(string html, string policy)
    {
        var directives = policy.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Split(' ', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            .ToDictionary(parts => parts[0], parts => parts.Skip(1).ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (string directive in new[] { "default-src", "frame-ancestors", "base-uri", "form-action" })
            Check(directives.TryGetValue(directive, out var sources) && sources.SequenceEqual(["'none'"]), "Callback CSP lost a required deny-all boundary.");
        Check(directives.GetValueOrDefault("script-src", directives["default-src"]).SequenceEqual(["'none'"]), "Callback CSP permits scripts.");
        MatchCollection styles = Regex.Matches(html, @"<style\b[^>]*>([\s\S]*?)</style\s*>", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        Check(styles.Count > 0, "Callback styling is missing.");
        // HTML input preprocessing normalizes newlines before CSP hashes the element's text.
        // Verify the browser-visible source so a CRLF-only source change cannot silently hide all styling.
        string[] hashes = styles.Select(match => "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(match.Groups[1].Value.ReplaceLineEndings("\n")))) + "'")
            .Distinct(StringComparer.Ordinal).ToArray();
        Check(directives.TryGetValue("style-src", out var allowed) && allowed.Length == hashes.Length &&
            allowed.ToHashSet(StringComparer.Ordinal).SetEquals(hashes), "CSP does not authorize exactly the emitted inline CSS bytes.");
        Check(!policy.Contains("'unsafe-inline'", StringComparison.OrdinalIgnoreCase) && !policy.Contains("'unsafe-eval'", StringComparison.OrdinalIgnoreCase),
            "Callback CSP was weakened for animation.");
    }

    private static async Task<WireResponse> SendAsync(string target, CancellationToken token)
    {
        using TcpClient client = new(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, 17853, token);
        await using NetworkStream stream = client.GetStream();
        byte[] request = Encoding.ASCII.GetBytes("GET " + target + " HTTP/1.1\r\nHost: 127.0.0.1:17853\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(request, token);
        using MemoryStream bytes = new(); byte[] buffer = new byte[4096];
        while (true)
        {
            int read = await stream.ReadAsync(buffer, token); if (read == 0) break;
            Check(bytes.Length + read <= 128 * 1024, "Callback response exceeded fixture bound.");
            bytes.Write(buffer, 0, read);
        }
        byte[] response = bytes.ToArray();
        int split = response.AsSpan().IndexOf("\r\n\r\n"u8);
        Check(split > 0, "Callback response has no HTTP header boundary.");
        string[] lines = Encoding.ASCII.GetString(response, 0, split).Split("\r\n");
        int status = int.Parse(lines[0].Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            int colon = line.IndexOf(':'); Check(colon > 0, "Malformed callback response header.");
            Check(headers.TryAdd(line[..colon], line[(colon + 1)..].Trim()), "Duplicate callback response header.");
        }
        return new(status, headers, response[(split + 4)..]);
    }
    private sealed record WireResponse(int Status, Dictionary<string, string> Headers, byte[] Body);
    private static void Run(Func<Task> action) => action().GetAwaiter().GetResult();
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
