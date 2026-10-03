using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AutumnOS.Identity;

namespace AutumnOS.NativeIdentity.Sample;

/// <summary>One memory-only resource credential sent to the fixed local example API; never follows redirects.</summary>
internal sealed class LocalResourceClient : IDisposable
{
    private readonly HttpClient http;
    internal LocalResourceClient(HttpMessageHandler? transport = null) => http = new(transport ?? new HttpClientHandler
    { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };
    internal async Task VerifyAndConfirmAsync(ProtectedIdentitySession session, CancellationToken token)
    {
        if (session.AccessToken.Length is < 16 or > 32768 || session.AccessToken.Any(char.IsWhiteSpace) ||
            session.AccessToken.Count(c => c == '.') != 2 || session.ExpiresAt <= DateTimeOffset.UtcNow)
            throw new IdentityFlowException("SAMPLE_RESOURCE_TOKEN_REQUIRED");
        using JsonDocument me = await SendAsync("/v1/me", HttpMethod.Get, null, session.AccessToken, token);
        JsonElement player = me.RootElement;
        if (player.GetProperty("issuer").GetString() != session.Issuer || player.GetProperty("subject").GetString() != session.Subject ||
            player.GetProperty("clientId").GetString() != session.ClientId) throw new IdentityFlowException("SAMPLE_API_IDENTITY_MISMATCH");
        using JsonDocument challenge = await SendAsync("/v1/action-challenge", HttpMethod.Post, null, session.AccessToken, token);
        string? value = challenge.RootElement.GetProperty("challenge").GetString();
        if (value is null || value.Length != 43 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_')))
            throw new IdentityFlowException("SAMPLE_API_INVALID_RESPONSE");
        using JsonDocument confirmed = await SendAsync("/v1/confirm-action", HttpMethod.Post,
            JsonSerializer.Serialize(new { challenge = value }), session.AccessToken, token);
        if (confirmed.RootElement.GetProperty("accepted").ValueKind != JsonValueKind.True)
            throw new IdentityFlowException("SAMPLE_API_INVALID_RESPONSE");
    }
    private async Task<JsonDocument> SendAsync(string path, HttpMethod method, string? body, string credential, CancellationToken token)
    {
        using HttpRequestMessage request = new(method, new Uri("http://127.0.0.1:5197" + path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new IdentityFlowException(response.StatusCode switch
        { System.Net.HttpStatusCode.Unauthorized => "SAMPLE_API_UNAUTHORIZED", System.Net.HttpStatusCode.Forbidden => "SAMPLE_API_FORBIDDEN",
            System.Net.HttpStatusCode.Conflict => "SAMPLE_API_REPLAY_REJECTED", _ => "SAMPLE_API_UNAVAILABLE" });
        if (response.Content.Headers.ContentType?.MediaType != "application/json" || response.Content.Headers.ContentLength is > 8192)
            throw new IdentityFlowException("SAMPLE_API_INVALID_RESPONSE");
        await response.Content.LoadIntoBufferAsync(8192, token).ConfigureAwait(false);
        return JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false), new JsonDocumentOptions { MaxDepth = 8 });
    }
    public void Dispose() => http.Dispose();
}
