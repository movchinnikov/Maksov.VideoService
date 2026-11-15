using System.Text.Json.Serialization;
using Maksov.VideoService.BuildingBlocks.Observability;
using Maksov.VideoService.BuildingBlocks.Postgres;
using Maksov.VideoService.Comments.Dal;
using Maksov.VideoService.Comments.Features.Comments;
using Maksov.VideoService.Comments.WebApi;
using Maksov.VideoService.Web;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

// Image utility commands: --health (container healthcheck), --migrate, --rollback-last.
var command = args.FirstOrDefault(static a => a is "--health" or "--migrate" or "--rollback-last");
if (command == "--health")
{
    return await HealthProbe.RunAsync();
}

var builder = WebApplication.CreateSlimBuilder(args.Where(a => a != command).ToArray());

builder.AddServiceTelemetry(
    "videoservice-comments",
    tracing: static t => t.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation(),
    metrics: static m => m.AddAspNetCoreInstrumentation().AddHttpClientInstrumentation());

builder.Services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, CommentsJsonContext.Default));
builder.Services.AddProblemDetails();
builder.Services.AddPostgres(builder.Configuration, "videoservice-comments");
builder.Services.AddSingleton<SqlMigrator>();
builder.Services.AddDal();
builder.AddKeycloakJwtAuthentication();
builder.Services.AddInternalApiKey(builder.Configuration);
builder.Services.AddCommentsFeatures();

var app = builder.Build();

if (command is "--migrate" or "--rollback-last")
{
    var migrator = app.Services.GetRequiredService<SqlMigrator>();
    if (command == "--rollback-last")
    {
        await migrator.RollbackLastAsync(DalServiceCollectionExtensions.MigrationsAssembly, app.Lifetime.ApplicationStopping);
    }
    else
    {
        await migrator.MigrateAsync(DalServiceCollectionExtensions.MigrationsAssembly, app.Lifetime.ApplicationStopping);
    }
    return 0;
}

app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseAuthentication();
app.UseAuthorization();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = static _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = static c => c.Tags.Contains("ready") });

app.MapCommentsFeatures();

await app.RunAsync();
return 0;

namespace Maksov.VideoService.Comments.WebApi
{
    /// <summary>Empty context: the host serializes nothing of its own, but the resolver chain must exist.</summary>
    [JsonSerializable(typeof(string))]
    internal sealed partial class CommentsJsonContext : JsonSerializerContext;
}

public partial class Program;
