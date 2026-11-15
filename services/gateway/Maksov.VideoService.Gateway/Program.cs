using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Maksov.VideoService.BuildingBlocks.Observability;
using Maksov.VideoService.Gateway;
using Maksov.VideoService.Web;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// Image utility command: --health (container healthcheck).
if (args.Contains("--health"))
{
    return await HealthProbe.RunAsync();
}

var builder = WebApplication.CreateSlimBuilder(args);

builder.AddServiceTelemetry(
    "videoservice-gateway",
    tracing: static t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation(),
    metrics: static m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation());

builder.Services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, GatewayJsonContext.Default));
builder.Services.AddProblemDetails();

// JWT is validated at the gateway; services behind it validate it again (zero trust).
builder.AddKeycloakJwtAuthentication();
builder.Services.AddAuthorizationBuilder()
    .AddPolicy("authenticated", static p => p.RequireAuthenticatedUser());

// Abuse protection: upload creation at most 20 per minute per user.
builder.Services.AddRateLimiter(static o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("create-upload", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.FindFirst("sub")?.Value ?? "anonymous",
        static _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
});

builder.Services.AddHealthChecks();
builder.Services.AddReverseProxy().LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var app = builder.Build();

app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready");

// Root without authorization: a hint for anyone who opens the API address in a browser.
var webUrl = builder.Configuration["Web:PublicUrl"] ?? "http://localhost:3000";
app.MapGet("/", () => TypedResults.Ok(new GatewayInfo(
    "Maksov.VideoService.Gateway",
    "This is the API gateway. The web client is at the address in the web field; API methods are under /api and require a Keycloak token.",
    webUrl,
    "/health/ready")));

// Everything under /api goes to services per the route table (appsettings: ReverseProxy). /internal/** is not published.
app.MapReverseProxy();

await app.RunAsync();
return 0;

namespace Maksov.VideoService.Gateway
{
    internal sealed record GatewayInfo(string Service, string Hint, string Web, string Health);

    [JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(GatewayInfo))]
    internal sealed partial class GatewayJsonContext : JsonSerializerContext;
}

public partial class Program;
