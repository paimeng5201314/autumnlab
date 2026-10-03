using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace AutumnOS.Identity;

/// <summary>One transaction, one exclusive IPv4 loopback socket. Never logs callback material.</summary>
internal sealed class LoopbackCallback : IDisposable
{
    private readonly TcpListener _listener;
    private readonly Uri _redirect;
    private int _consumed;
    public LoopbackCallback(Uri redirect, int permittedPort = 17853)
    {
        if (permittedPort is not (17853 or 17854) || redirect.Scheme != "http" || redirect.Host != "127.0.0.1" || redirect.Port != permittedPort ||
            redirect.AbsolutePath is not ("/callback/" or "/logout-callback/") || redirect.Query.Length != 0 || redirect.Fragment.Length != 0)
            throw new IdentityFlowException("AUTH_CONFIGURATION_ERROR");
        _redirect = redirect;
        _listener = new TcpListener(IPAddress.Loopback, redirect.Port);
        _listener.Server.ExclusiveAddressUse = true;
        try { _listener.Start(4); }
        catch (SocketException) { _listener.Stop(); throw new IdentityFlowException("AUTH_PORT_IN_USE"); }
    }

    public async Task<string> ReceiveAsync(string expectedState, bool logout, CancellationToken cancellationToken)
    {
        int rejected = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using TcpClient client = await _listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            using CancellationTokenSource request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            request.CancelAfter(TimeSpan.FromSeconds(3));
            await using NetworkStream stream = client.GetStream();
            string? target = null;
            try
            {
                byte[] bytes = new byte[8192];
                int used = 0;
                while (used < bytes.Length)
                {
                    int read = await stream.ReadAsync(bytes.AsMemory(used, bytes.Length - used), request.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    used += read;
                    if (Encoding.ASCII.GetString(bytes, 0, used).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
                }
                target = ValidateRequest(Encoding.ASCII.GetString(bytes, 0, used), expectedState, _redirect, logout);
                if (target is not null && Interlocked.CompareExchange(ref _consumed, 1, 0) != 0) target = null;
                CallbackPageContent page = CallbackResponsePage.Create(target is not null);
                byte[] body = Encoding.UTF8.GetBytes(page.Html);
                string headers = $"HTTP/1.1 {(target is null ? "400 Bad Request" : "200 OK")}\r\nContent-Type: text/html; charset=utf-8\r\nContent-Length: {body.Length}\r\nCache-Control: no-store\r\nPragma: no-cache\r\nContent-Security-Policy: {page.ContentSecurityPolicy}\r\nReferrer-Policy: no-referrer\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers), request.Token).ConfigureAwait(false);
                await stream.WriteAsync(body, request.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or SocketException or OperationCanceledException)
            { cancellationToken.ThrowIfCancellationRequested(); }
            // A closed browser connection must not undo an already consumed valid callback.
            if (target is not null) return _redirect.GetLeftPart(UriPartial.Authority) + target;
            if (++rejected >= 8) await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string? ValidateRequest(string request, string expectedState, Uri redirect, bool logout)
    {
        if (request.Length is 0 or >= 8192 || !request.EndsWith("\r\n\r\n", StringComparison.Ordinal)) return null;
        string[] lines = request.Split("\r\n", StringSplitOptions.None);
        string[] first = lines[0].Split(' ');
        if (first.Length != 3 || first[0] != "GET" || first[2] != "HTTP/1.1") return null;
        string target = first[1];
        if (target.Length > 6144 || target.Any(c => c < 33 || c > 126) || target.Contains('#')) return null;
        int question = target.IndexOf('?');
        if (question < 0 || target[..question] != redirect.AbsolutePath) return null;
        string[] hosts = lines.Skip(1).Where(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (hosts.Length != 1 || hosts[0][5..].Trim() != "127.0.0.1:" + redirect.Port) return null;
        if (lines.Skip(1).Any(l => l.StartsWith("Transfer-Encoding:", StringComparison.OrdinalIgnoreCase) ||
            l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))) return null;
        Dictionary<string, string>? parameters = ParseQuery(target[(question + 1)..]);
        if (parameters is null || !parameters.TryGetValue("state", out string? state) || !FixedEquals(state, expectedState)) return null;
        string[] allowed = logout ? ["state"] : ["state", "code", "error", "error_description", "error_uri", "iss", "session_state"];
        if (parameters.Keys.Any(k => !allowed.Contains(k, StringComparer.Ordinal))) return null;
        if (!logout && (parameters.ContainsKey("code") == parameters.ContainsKey("error"))) return null;
        if (!logout && parameters.TryGetValue("code", out string? code) && code.Length is 0 or > 2048) return null;
        return target;
    }

    internal static Dictionary<string, string>? ParseQuery(string query)
    {
        string[] pairs = query.Split('&');
        if (pairs.Length is 0 or > 8) return null;
        Dictionary<string, string> result = new(StringComparer.Ordinal);
        foreach (string pair in pairs)
        {
            int split = pair.IndexOf('=');
            if (split < 1) return null;
            if (!ValidEscapes(pair)) return null;
            string key = Uri.UnescapeDataString(pair[..split].Replace('+', ' '));
            string value = Uri.UnescapeDataString(pair[(split + 1)..].Replace('+', ' '));
            if (key.Length > 64 || value.Length > 4096 || value.Any(char.IsControl) || !result.TryAdd(key, value)) return null;
        }
        return result;
    }
    private static bool ValidEscapes(string text)
    {
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '%' && (i + 2 >= text.Length || !Uri.IsHexDigit(text[++i]) || !Uri.IsHexDigit(text[++i]))) return false;
        return true;
    }
    internal static bool FixedEquals(string actual, string expected) => CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(actual), Encoding.UTF8.GetBytes(expected));
    public void Dispose() => _listener.Stop();
}

internal sealed class IdentityFlowException(string code) : Exception(code)
{
    public string Code { get; } = code;
}
