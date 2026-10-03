using AutumnOS.ServerIdentity.Sample;

// No production identity defaults: missing independent resource/client registration fails before listening.
static string Required(string name) => Environment.GetEnvironmentVariable(name) is string value && !string.IsNullOrWhiteSpace(value)
    ? value : throw new InvalidOperationException("SERVER_IDENTITY_CONFIGURATION_REQUIRED: " + name);
ServerIdentityConfiguration configuration;
try
{
    configuration = new(new Uri(Required("AUTUMN_SERVER_AUTHORITY")), new Uri(Required("AUTUMN_SERVER_METADATA")),
        Required("AUTUMN_SERVER_AUDIENCE"), Required("AUTUMN_SERVER_CLIENT_ID"), Required("AUTUMN_SERVER_REQUIRED_SCOPE"));
    configuration.Validate();
}
catch (Exception error) when (error is InvalidOperationException or UriFormatException)
{ Console.Error.WriteLine("SERVER_IDENTITY_CONFIGURATION_REQUIRED: consult README; no listener was opened."); return 2; }

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:5197"); // Local verification sample only; deployment/TLS remains a separate authorized decision.
builder.Logging.ClearProviders(); // Do not log headers, claims, URLs or credentials in this minimal example.
builder.Services.ConfigureHttpJsonOptions(json => json.SerializerOptions.UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow);
builder.WebHost.ConfigureKestrel(server => { server.Limits.MaxRequestBodySize = 4096; server.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5); });
using DiscoverySigningKeys keys = new(configuration);
ServerTokenVerifier verifier = new(configuration, keys.GetAsync);
ActionChallenges challenges = new();
WebApplication app = builder.Build();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    if (context.Request.Path == "/health") { await next(context); return; }
    if (context.Request.QueryString.HasValue || context.Request.Headers.Authorization.Count != 1)
    { context.Response.StatusCode = 401; await context.Response.WriteAsJsonAsync(new { error = "INVALID_CREDENTIAL" }); return; }
    VerificationResult result = await verifier.VerifyAsync(context.Request.Headers.Authorization[0], context.RequestAborted);
    if (!result.Succeeded)
    {
        context.Response.StatusCode = result.Error == "INSUFFICIENT_SCOPE" ? 403 : result.Error == "KEYS_UNAVAILABLE" ? 503 : 401;
        await context.Response.WriteAsJsonAsync(new { error = result.Error }); return;
    }
    context.Items["player"] = result.Player;
    await next(context);
});
app.MapGet("/health", () => Results.Ok(new { service = "AutumnOS independent identity example", producer = "派蒙", status = "configured", real_provider_verified = false }));
app.MapGet("/v1/me", (HttpContext context) =>
{
    VerifiedPlayer player = (VerifiedPlayer)context.Items["player"]!;
    return Results.Ok(new { subject = player.Subject, issuer = player.Issuer, clientId = player.ClientId });
});
app.MapPost("/v1/action-challenge", (HttpContext context) =>
{
    string? nonce = challenges.Issue((VerifiedPlayer)context.Items["player"]!);
    return nonce is null ? Results.Json(new { error = "CAPACITY_REACHED" }, statusCode: 429) : Results.Ok(new { challenge = nonce, expiresIn = 60 });
});
app.MapPost("/v1/confirm-action", (HttpContext context, ActionRequest request) =>
    challenges.Consume(request.Challenge, (VerifiedPlayer)context.Items["player"]!)
        ? Results.Ok(new { accepted = true, effect = "test confirmation only; no score or game state modified" })
        : Results.Json(new { error = "INVALID_OR_REPLAYED_CHALLENGE" }, statusCode: 409));
await app.RunAsync();
return 0;
public sealed record ActionRequest(string Challenge);
