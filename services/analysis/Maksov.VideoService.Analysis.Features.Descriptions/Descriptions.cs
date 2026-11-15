using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.Analysis.Bll.Ports;
using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.Primitives;
using Maksov.VideoService.Web;

namespace Maksov.VideoService.Analysis.Features.Descriptions;

/// <summary>Video description for the viewer. <c>Text</c> is non-empty only when <c>done</c>.</summary>
public sealed record DescriptionDto(Guid VideoId, string Status, string? Text, IReadOnlyList<string> Tags, DateTimeOffset UpdatedAt);

/// <summary>"Video processed" event (contract — contracts/events/video-processed.v1.json).</summary>
public sealed record VideoProcessedEvent(
    Guid EventId,
    DateTimeOffset OccurredAt,
    int SchemaVersion,
    string? TraceId,
    Guid VideoId,
    string? OwnerId,
    string? SourceKey,
    int DurationMs,
    int Width,
    int Height);

/// <summary>"Video deleted" event (contract — contracts/events/video-deleted.v1.json).</summary>
public sealed record VideoDeletedEvent(Guid EventId, DateTimeOffset OccurredAt, int SchemaVersion, string? TraceId, Guid VideoId, string? OwnerId);

public sealed record AcceptedDto(bool Created);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(DescriptionDto))]
[JsonSerializable(typeof(VideoProcessedEvent))]
[JsonSerializable(typeof(VideoDeletedEvent))]
[JsonSerializable(typeof(AcceptedDto))]
internal sealed partial class DescriptionsJsonContext : JsonSerializerContext;

/// <summary>GetDescription slice: the owner sees the description in any status, others only the finished one (done); otherwise — as if it does not exist.</summary>
internal sealed class GetDescriptionHandler(IAnalysisRepository analyses)
{
    public async Task<Result<DescriptionDto>> HandleAsync(string viewerId, Guid videoId, CancellationToken ct)
    {
        var a = await analyses.GetAsync(videoId, ct);
        if (a is null || (a.OwnerId != viewerId && a.Status != AnalysisStatus.Done))
        {
            return DomainError.NotFound("description_not_found", "No description yet");
        }

        return new DescriptionDto(a.VideoId, ToWire(a.Status), a.Status == AnalysisStatus.Done ? a.Description : null, a.Tags, a.UpdatedAt);
    }

    internal static string ToWire(AnalysisStatus status) => status switch
    {
        AnalysisStatus.Queued => "queued",
        AnalysisStatus.Processing => "processing",
        AnalysisStatus.Done => "done",
        AnalysisStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };
}

/// <summary>AcceptVideoProcessed slice: internal event intake, idempotent by video_id.</summary>
internal sealed class AcceptVideoProcessedHandler(IAnalysisRepository analyses)
{
    public const int SupportedSchemaVersion = 1;

    public static IReadOnlyList<DomainError> Validate(VideoProcessedEvent e)
    {
        var errors = new List<DomainError>();
        if (e.SchemaVersion != SupportedSchemaVersion)
        {
            errors.Add(DomainError.Validation("schemaVersion", $"Supported schemaVersion: {SupportedSchemaVersion}."));
        }

        if (e.EventId == Guid.Empty || e.VideoId == Guid.Empty)
        {
            errors.Add(DomainError.Validation("videoId", "eventId and videoId are required."));
        }

        if (string.IsNullOrWhiteSpace(e.OwnerId) || e.OwnerId.Length > 128)
        {
            errors.Add(DomainError.Validation("ownerId", "ownerId is required (up to 128 characters)."));
        }

        // The source key is built by one rule for all services; arbitrary keys are not accepted.
        if (e.VideoId != Guid.Empty && e.SourceKey != MediaKeys.Source(e.VideoId))
        {
            errors.Add(DomainError.Validation("sourceKey", "sourceKey does not match videoId."));
        }

        if (e.DurationMs <= 0 || e.Width <= 0 || e.Height <= 0)
        {
            errors.Add(DomainError.Validation("media", "durationMs, width, height must be positive."));
        }

        return errors;
    }

    public Task<bool> HandleAsync(VideoProcessedEvent e, CancellationToken ct) =>
        analyses.EnqueueAsync(new AnalysisRequest(e.EventId, e.VideoId, e.OwnerId!, e.SourceKey!, e.DurationMs, e.Width, e.Height), ct);
}

/// <summary>ForgetVideo slice: internal "video deleted" intake — the analysis is removed, a retry is safe.</summary>
internal sealed class ForgetVideoHandler(IAnalysisRepository analyses)
{
    public static IReadOnlyList<DomainError> Validate(VideoDeletedEvent e)
    {
        var errors = new List<DomainError>();
        if (e.SchemaVersion != AcceptVideoProcessedHandler.SupportedSchemaVersion)
        {
            errors.Add(DomainError.Validation("schemaVersion", $"Supported schemaVersion: {AcceptVideoProcessedHandler.SupportedSchemaVersion}."));
        }

        if (e.EventId == Guid.Empty || e.VideoId == Guid.Empty)
        {
            errors.Add(DomainError.Validation("videoId", "eventId and videoId are required."));
        }

        return errors;
    }

    public Task<bool> HandleAsync(VideoDeletedEvent e, CancellationToken ct) => analyses.DeleteAsync(e.VideoId, ct);
}

/// <summary>Registration and routes of the "Descriptions" feature.</summary>
public static class DescriptionsFeatures
{
    public static IServiceCollection AddDescriptionsFeatures(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, DescriptionsJsonContext.Default));
        services.AddSingleton<GetDescriptionHandler>();
        services.AddSingleton<AcceptVideoProcessedHandler>();
        services.AddSingleton<ForgetVideoHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapDescriptionsFeatures(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Public route: user JWT (via the gateway).
        app.MapGroup("/api/videos").RequireAuthorization().WithTags("Descriptions")
            .MapGet("/{id:guid}/description", async (Guid id, HttpContext http, GetDescriptionHandler handler, CancellationToken ct) =>
            {
                var result = await handler.HandleAsync(http.User.UserId(), id, ct);
                return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
            })
            .WithName("GetDescription");

        // Internal routes: key only, not published through the gateway.
        var internalApi = app.MapGroup("/internal/v1").WithTags("Internal");

        internalApi.MapPost("/events/video-deleted", async (VideoDeletedEvent e, ForgetVideoHandler handler, CancellationToken ct) =>
            {
                var errors = ForgetVideoHandler.Validate(e);
                if (errors.Count > 0)
                {
                    return errors.ToValidationProblem();
                }

                var deleted = await handler.HandleAsync(e, ct);
                return TypedResults.Accepted((string?)null, new AcceptedDto(deleted));
            })
            .RequireInternalKey()
            .WithName("ForgetVideo");

        internalApi
            .MapPost("/events/video-processed", async (VideoProcessedEvent e, AcceptVideoProcessedHandler handler, CancellationToken ct) =>
            {
                var errors = AcceptVideoProcessedHandler.Validate(e);
                if (errors.Count > 0)
                {
                    return errors.ToValidationProblem();
                }

                var created = await handler.HandleAsync(e, ct);
                return TypedResults.Accepted((string?)null, new AcceptedDto(created));
            })
            .RequireInternalKey()
            .WithName("AcceptVideoProcessed");

        return app;
    }
}
