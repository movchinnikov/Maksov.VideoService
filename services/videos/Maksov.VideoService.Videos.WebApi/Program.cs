using System.Text.Json.Serialization;
using Maksov.VideoService.BuildingBlocks.Observability;
using Maksov.VideoService.BuildingBlocks.Postgres;
using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.Videos.Dal;
using Maksov.VideoService.Videos.Features.Catalog;
using Maksov.VideoService.Videos.Features.Videos;
using Maksov.VideoService.Videos.WebApi;
using Maksov.VideoService.Web;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// Image service commands: --health (container healthcheck), --migrate, --rollback-last (migrator).
var command = args.FirstOrDefault(static a => a is "--health" or "--migrate" or "--rollback-last");
if (command == "--health")
{
    return await HealthProbe.RunAsync();
}

var builder = WebApplication.CreateSlimBuilder(args.Where(a => a != command).ToArray());

builder.AddServiceTelemetry(
    "videoservice-api",
    tracing: static t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation(),
    metrics: static m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation());

builder.Services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, WebApiJsonContext.Default));
builder.Services.AddProblemDetails();

// Infrastructure and port adapters (Bll → Dal / BuildingBlocks)
builder.Services.AddPostgres(builder.Configuration, "videoservice-api");
builder.Services.AddSingleton<SqlMigrator>();
builder.Services.AddS3ObjectStorage(builder.Configuration);
builder.Services.AddDal();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddMemoryCache(static o => o.SizeLimit = 10_000);

// Authorization: Keycloak JWT (verified both here and at the gateway: zero trust). External SSO and phone login are configured in Keycloak.
builder.AddKeycloakJwtAuthentication();
builder.Services.AddInternalApiKey(builder.Configuration);

// Features (vertical slices in separate assemblies)
builder.Services.AddVideoFeatures(builder.Configuration);
builder.Services.AddCatalogFeatures();

var app = builder.Build();

if (command is "--migrate" or "--rollback-last")
{
    return await MigrationCommand.RunAsync(app.Services, command, app.Lifetime.ApplicationStopping);
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = static c => c.Tags.Contains("ready") });

// Root without authorization: a hint for anyone who opens the API address in a browser.
var webUrl = builder.Configuration["Web:PublicUrl"] ?? "http://localhost:3000";
app.MapGet("/", () => TypedResults.Ok(new ServiceInfo(
        "Maksov.VideoService.Videos.WebApi",
        "This is the API. The web client is at the address in the web field; API methods are under /api and require a Keycloak token.",
        webUrl,
        "/health/ready")));

app.MapGroup("/api")
    .RequireAuthorization()
    .MapVideoFeatures()
    .MapCatalogFeatures();

app.MapCatalogInternalFeatures();

await app.RunAsync();
return 0;

namespace Maksov.VideoService.Videos.WebApi
{
    /// <summary>Assembly anchor for WebApplicationFactory in tests (the worker also has a top-level Program).</summary>
    public sealed class VideosApiMarker;

    internal sealed record ServiceInfo(string Service, string Hint, string Web, string Health);

    [JsonSourceGenerationOptions(System.Text.Json.JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(ServiceInfo))]
    internal sealed partial class WebApiJsonContext : JsonSerializerContext;
}

public partial class Program;
