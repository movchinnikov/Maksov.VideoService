using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.Comments.Bll.Ports;
using Maksov.VideoService.Primitives;
using Maksov.VideoService.Web;

namespace Maksov.VideoService.Comments.Features.Comments;

public sealed record CommentDto(Guid Id, Guid VideoId, string AuthorId, string AuthorUsername, string Text, DateTimeOffset CreatedAt, bool CanDelete);

public sealed record CommentPage(IReadOnlyList<CommentDto> Items, string? NextCursor, int Total);

public sealed record AddCommentRequest(string? Text);

/// <summary>"Video processed" event: comments need only videoId and the owner (contract: contracts/events/video-processed.v1.json).</summary>
public sealed record VideoProcessedEvent(Guid EventId, DateTimeOffset OccurredAt, int SchemaVersion, string? TraceId, Guid VideoId, string? OwnerId);

/// <summary>"Video deleted" event (contract: contracts/events/video-deleted.v1.json).</summary>
public sealed record VideoDeletedEvent(Guid EventId, DateTimeOffset OccurredAt, int SchemaVersion, string? TraceId, Guid VideoId, string? OwnerId);

public sealed record AcceptedDto(bool Ok);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(CommentDto))]
[JsonSerializable(typeof(CommentPage))]
[JsonSerializable(typeof(AddCommentRequest))]
[JsonSerializable(typeof(VideoProcessedEvent))]
[JsonSerializable(typeof(VideoDeletedEvent))]
[JsonSerializable(typeof(AcceptedDto))]
internal sealed partial class CommentsFeatureJsonContext : JsonSerializerContext;

internal static class Mapping
{
    /// <summary>The comment author or the video owner can delete.</summary>
    public static CommentDto ToDto(CommentRecord c, string viewerId, string videoOwnerId) => new(
        c.Id, c.VideoId, c.AuthorId, c.AuthorUsername, c.Text, c.CreatedAt,
        CanDelete: string.Equals(c.AuthorId, viewerId, StringComparison.Ordinal) || string.Equals(videoOwnerId, viewerId, StringComparison.Ordinal));
}

internal sealed class ListCommentsHandler(ICommentRepository comments)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    public async Task<Result<CommentPage>> HandleAsync(Guid videoId, string viewerId, string? cursor, int? limit, CancellationToken ct)
    {
        var owner = await comments.GetVideoOwnerAsync(videoId, ct);
        if (owner is null)
        {
            return DomainError.NotFound("video_not_found", "Video not found");
        }

        // Fetch one extra record to learn whether there is a next page.
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var rows = await comments.ListAsync(videoId, PageCursor.TryDecode(cursor), take + 1, ct);
        var hasMore = rows.Count > take;
        var page = hasMore ? rows.Take(take).ToList() : rows;
        var next = hasMore ? new PageCursor(page[^1].CreatedAt, page[^1].Id).Encode() : null;
        var total = await comments.CountAsync(videoId, ct);
        return new CommentPage(page.Select(c => Mapping.ToDto(c, viewerId, owner)).ToList(), next, total);
    }
}

internal sealed class AddCommentHandler(ICommentRepository comments, TimeProvider clock)
{
    public const int MaxLength = 500;

    public static IReadOnlyList<DomainError> Validate(AddCommentRequest request)
    {
        var text = request.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return [DomainError.Validation("text", "Write a comment.")];
        }

        return text.Length > MaxLength ? [DomainError.Validation("text", $"Comment is longer than {MaxLength} characters.")] : [];
    }

    public async Task<Result<CommentDto>> HandleAsync(Guid videoId, string authorId, string authorUsername, AddCommentRequest request, CancellationToken ct)
    {
        var owner = await comments.GetVideoOwnerAsync(videoId, ct);
        if (owner is null)
        {
            return DomainError.NotFound("video_not_found", "Video not found");
        }

        var comment = new NewComment(Guid.CreateVersion7(), videoId, authorId, authorUsername, request.Text!.Trim(), clock.GetUtcNow());
        await comments.InsertAsync(comment, owner, ct);
        return Mapping.ToDto(new CommentRecord(comment.Id, comment.VideoId, comment.AuthorId, comment.AuthorUsername, comment.Text, comment.CreatedAt), authorId, owner);
    }
}

internal sealed class DeleteCommentHandler(ICommentRepository comments)
{
    /// <summary>Someone else's comment is indistinguishable from a non-existent one (404).</summary>
    public async Task<Result<bool>> HandleAsync(Guid videoId, Guid commentId, string viewerId, CancellationToken ct)
    {
        var owner = await comments.GetVideoOwnerAsync(videoId, ct);
        var comment = owner is null ? null : await comments.GetAsync(videoId, commentId, ct);
        if (comment is null || !Mapping.ToDto(comment, viewerId, owner!).CanDelete)
        {
            return DomainError.NotFound("comment_not_found", "Comment not found");
        }

        await comments.DeleteAsync(videoId, commentId, viewerId, ct);
        return true;
    }
}

internal sealed class AcceptVideoProcessedHandler(ICommentRepository comments)
{
    public const int SupportedSchemaVersion = 1;

    public static IReadOnlyList<DomainError> Validate(VideoProcessedEvent e)
    {
        var errors = new List<DomainError>();
        if (e.SchemaVersion != SupportedSchemaVersion)
        {
            errors.Add(DomainError.Validation("schemaVersion", $"schemaVersion supported: {SupportedSchemaVersion}."));
        }

        if (e.EventId == Guid.Empty || e.VideoId == Guid.Empty)
        {
            errors.Add(DomainError.Validation("videoId", "eventId and videoId are required."));
        }

        if (string.IsNullOrWhiteSpace(e.OwnerId) || e.OwnerId.Length > 128)
        {
            errors.Add(DomainError.Validation("ownerId", "ownerId is required (up to 128 characters)."));
        }

        return errors;
    }

    public Task HandleAsync(VideoProcessedEvent e, CancellationToken ct) => comments.UpsertVideoAsync(e.VideoId, e.OwnerId!, ct);
}

internal sealed class ForgetVideoHandler(ICommentRepository comments)
{
    public static IReadOnlyList<DomainError> Validate(VideoDeletedEvent e)
    {
        var errors = new List<DomainError>();
        if (e.SchemaVersion != AcceptVideoProcessedHandler.SupportedSchemaVersion)
        {
            errors.Add(DomainError.Validation("schemaVersion", $"schemaVersion supported: {AcceptVideoProcessedHandler.SupportedSchemaVersion}."));
        }

        if (e.EventId == Guid.Empty || e.VideoId == Guid.Empty)
        {
            errors.Add(DomainError.Validation("videoId", "eventId and videoId are required."));
        }

        return errors;
    }

    public Task HandleAsync(VideoDeletedEvent e, CancellationToken ct) => comments.ForgetVideoAsync(e.VideoId, e.OwnerId, ct);
}

/// <summary>Registration and routes of the Comments feature (public and internal).</summary>
public static class CommentsFeatures
{
    public static IServiceCollection AddCommentsFeatures(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, CommentsFeatureJsonContext.Default));
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ListCommentsHandler>();
        services.AddSingleton<AddCommentHandler>();
        services.AddSingleton<DeleteCommentHandler>();
        services.AddSingleton<AcceptVideoProcessedHandler>();
        services.AddSingleton<ForgetVideoHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapCommentsFeatures(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Public routes: user JWT (via the gateway).
        var api = app.MapGroup("/api/videos/{videoId:guid}/comments").RequireAuthorization().WithTags("Comments");

        api.MapGet("/", async (Guid videoId, string? cursor, int? limit, HttpContext http, ListCommentsHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(videoId, http.User.UserId(), cursor, limit, ct);
            return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
        }).WithName("ListComments");

        api.MapPost("/", async (Guid videoId, AddCommentRequest request, HttpContext http, AddCommentHandler handler, CancellationToken ct) =>
        {
            var errors = AddCommentHandler.Validate(request);
            if (errors.Count > 0)
            {
                return errors.ToValidationProblem();
            }

            var result = await handler.HandleAsync(videoId, http.User.UserId(), http.User.Username(), request, ct);
            return result.IsSuccess
                ? TypedResults.Created($"/api/videos/{videoId}/comments/{result.Value!.Id}", result.Value)
                : result.Error!.ToProblem();
        }).WithName("AddComment");

        api.MapDelete("/{commentId:guid}", async (Guid videoId, Guid commentId, HttpContext http, DeleteCommentHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(videoId, commentId, http.User.UserId(), ct);
            return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
        }).WithName("DeleteComment");

        // Internal routes: key only, not published through the gateway.
        var internalApi = app.MapGroup("/internal/v1").WithTags("Internal");

        internalApi.MapPost("/events/video-processed", async (VideoProcessedEvent e, AcceptVideoProcessedHandler handler, CancellationToken ct) =>
        {
            var errors = AcceptVideoProcessedHandler.Validate(e);
            if (errors.Count > 0)
            {
                return errors.ToValidationProblem();
            }

            await handler.HandleAsync(e, ct);
            return TypedResults.Accepted((string?)null, new AcceptedDto(true));
        }).RequireInternalKey().WithName("AcceptVideoProcessed");

        internalApi.MapPost("/events/video-deleted", async (VideoDeletedEvent e, ForgetVideoHandler handler, CancellationToken ct) =>
        {
            var errors = ForgetVideoHandler.Validate(e);
            if (errors.Count > 0)
            {
                return errors.ToValidationProblem();
            }

            await handler.HandleAsync(e, ct);
            return TypedResults.Accepted((string?)null, new AcceptedDto(true));
        }).RequireInternalKey().WithName("ForgetVideo");

        return app;
    }
}
