using System.Text.Json.Serialization;
using Maksov.VideoService.BuildingBlocks.Observability;
using Maksov.VideoService.Feed.Dal;
using Maksov.VideoService.Feed.Features.Feed;
using Maksov.VideoService.Feed.WebApi;
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
    "videoservice-feed",
    tracing: static t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation(),
    metrics: static m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation());

builder.Services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, FeedJsonContext.Default));
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();
builder.Services.AddDal(builder.Configuration);
builder.AddKeycloakJwtAuthentication();
builder.Services.AddFeedFeatures();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = static _ => false });

app.MapFeedFeatures();

await app.RunAsync();
return 0;

namespace Maksov.VideoService.Feed.WebApi
{
    /// <summary>Empty context: the host serializes nothing of its own, but the resolver chain must exist.</summary>
    [JsonSerializable(typeof(string))]
    internal sealed partial class FeedJsonContext : JsonSerializerContext;
}

public partial class Program;
