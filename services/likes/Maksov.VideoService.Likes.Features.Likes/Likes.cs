using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.Likes.Bll.Ports;
using Maksov.VideoService.Primitives;
using Maksov.VideoService.Web;

namespace Maksov.VideoService.Likes.Features.Likes;

public sealed record LikeCountDto(Guid Id, int Count, bool LikedByMe);

public sealed record LikeCountsResponse(IReadOnlyList<LikeCountDto> Items);

public sealed record LikerDto(string UserId, DateTimeOffset LikedAt);

public sealed record LikerPage(IReadOnlyList<LikerDto> Items, string? NextCursor, int Total);

/// <summary>"Video processed" event: likes need only videoId and the owner (contract: contracts/events/video-processed.v1.json).</summary>
public sealed record VideoProcessedEvent(Guid EventId, DateTimeOffset OccurredAt, int SchemaVersion, string? TraceId, Guid VideoId, string? OwnerId);

/// <summary>"Video deleted" event (contract: contracts/events/video-deleted.v1.json).</summary>
public sealed record VideoDeletedEvent(Guid EventId, DateTimeOffset OccurredAt, int SchemaVersion, string? TraceId, Guid VideoId, string? OwnerId);

/// <summary>"Comment added" event (contract: contracts/events/comment-added.v1.json); the text is never published.</summary>
public sealed record CommentAddedEvent(
    Guid EventId, DateTimeOffset OccurredAt, int SchemaVersion, string? TraceId, Guid CommentId, Guid VideoId, string? AuthorId, string? VideoOwnerId, int TextLength);

/// <summary>"Comment deleted" event (contract: contracts/events/comment-deleted.v1.json).</summary>
public sealed record CommentDeletedEvent(
    Guid EventId, DateTimeOffset OccurredAt, int SchemaVersion, string? TraceId, Guid CommentId, Guid VideoId, string? AuthorId, string? DeletedBy);

public sealed record AcceptedDto(bool Ok);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(LikeCountsResponse))]
[JsonSerializable(typeof(LikerPage))]
[JsonSerializable(typeof(VideoProcessedEvent))]
[JsonSerializable(typeof(VideoDeletedEvent))]
[JsonSerializable(typeof(CommentAddedEvent))]
[JsonSerializable(typeof(CommentDeletedEvent))]
[JsonSerializable(typeof(AcceptedDto))]
internal sealed partial class LikesFeatureJsonContext : JsonSerializerContext;

internal static class Resolve
{
    public static async Task<Result<LikeTarget>> VideoAsync(ILikeRepository likes, Guid videoId, CancellationToken ct)
    {
        var owner = await likes.GetVideoOwnerAsync(videoId, ct);
        return owner is null
            ? DomainError.NotFound("video_not_found", "Video not found")
            : new LikeTarget(LikeTargetType.Video, videoId, videoId, owner);
    }

    /// <summary>The video in the route must be the video of the known comment, otherwise the comment does not exist for the caller.</summary>
    public static async Task<Result<LikeTarget>> CommentAsync(ILikeRepository likes, Guid videoId, Guid commentId, CancellationToken ct)
    {
        var comment = await likes.GetCommentAsync(commentId, ct);
        return comment is null || comment.VideoId != videoId
            ? DomainError.NotFound("comment_not_found", "Comment not found")
            : new LikeTarget(LikeTargetType.Comment, commentId, videoId, comment.AuthorId);
    }
}

/// <summary>Like or unlike a video or a comment. Idempotent: a repeat changes nothing and emits no event.</summary>
internal sealed class SetLikeHandler(ILikeRepository likes)
{
    public async Task<Result<bool>> HandleVideoAsync(Guid videoId, string userId, bool liked, CancellationToken ct)
    {
        var target = await Resolve.VideoAsync(likes, videoId, ct);
        return target.IsSuccess ? await ApplyAsync(target.Value!, userId, liked, ct) : target.Error!;
    }

    public async Task<Result<bool>> HandleCommentAsync(Guid videoId, Guid commentId, string userId, bool liked, CancellationToken ct)
    {
        var target = await Resolve.CommentAsync(likes, videoId, commentId, ct);
        return target.IsSuccess ? await ApplyAsync(target.Value!, userId, liked, ct) : target.Error!;
    }

    private async Task<Result<bool>> ApplyAsync(LikeTarget target, string userId, bool liked, CancellationToken ct)
    {
        _ = liked
            ? await likes.AddLikeAsync(target, userId, ct)
            : await likes.RemoveLikeAsync(target, userId, ct);
        return true;
    }
}

internal sealed class GetCountsHandler(ILikeRepository likes)
{
    public const int MaxIds = 50;

    public static (IReadOnlyList<Guid> Ids, IReadOnlyList<DomainError> Errors) Parse(string? ids)
    {
        if (string.IsNullOrWhiteSpace(ids))
        {
            return ([], [DomainError.Validation("ids", "ids is required (comma-separated ids).")]);
        }

        var result = new List<Guid>();
        foreach (var part in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var id) || id == Guid.Empty)
            {
                return ([], [DomainError.Validation("ids", "ids must be comma-separated UUIDs.")]);
            }

            if (!result.Contains(id))
            {
                result.Add(id);
            }
        }

        return result.Count is 0 or > MaxIds
            ? ([], [DomainError.Validation("ids", $"Pass from 1 to {MaxIds} ids.")])
            : (result, []);
    }

    public async Task<LikeCountsResponse> HandleAsync(LikeTargetType type, IReadOnlyList<Guid> ids, string userId, CancellationToken ct)
    {
        var rows = await likes.GetCountsAsync(type, ids, userId, ct);
        return new LikeCountsResponse([.. rows.Select(static r => new LikeCountDto(r.Id, r.Count, r.LikedByMe))]);
    }
}

internal sealed class ListLikersHandler(ILikeRepository likes)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    public async Task<Result<LikerPage>> HandleAsync(Guid videoId, string? cursor, int? limit, CancellationToken ct)
    {
        if (await likes.GetVideoOwnerAsync(videoId, ct) is null)
        {
            return DomainError.NotFound("video_not_found", "Video not found");
        }

        // Fetch one extra record to learn whether there is a next page.
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var rows = await likes.ListLikersAsync(videoId, LikerCursor.TryDecode(cursor), take + 1, ct);
        var hasMore = rows.Count > take;
        var page = hasMore ? rows.Take(take).ToList() : rows;
        var next = hasMore ? new LikerCursor(page[^1].LikedAt, page[^1].UserId).Encode() : null;
        var total = await likes.CountVideoLikesAsync(videoId, ct);
        return new LikerPage([.. page.Select(static l => new LikerDto(l.UserId, l.LikedAt))], next, total);
    }
}

/// <summary>Event intake: validation and idempotent application of the replicas.</summary>
internal sealed class EventIntakeHandler(ILikeRepository likes)
{
    public const int SupportedSchemaVersion = 1;
    private const int MaxUserIdLength = 128;

    public static IReadOnlyList<DomainError> Validate(VideoProcessedEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var errors = Common(e.SchemaVersion, e.EventId, [("videoId", e.VideoId)]);
        RequireUser(errors, "ownerId", e.OwnerId);
        return errors;
    }

    public static IReadOnlyList<DomainError> Validate(VideoDeletedEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Common(e.SchemaVersion, e.EventId, [("videoId", e.VideoId)]);
    }

    public static IReadOnlyList<DomainError> Validate(CommentAddedEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var errors = Common(e.SchemaVersion, e.EventId, [("commentId", e.CommentId), ("videoId", e.VideoId)]);
        RequireUser(errors, "authorId", e.AuthorId);
        return errors;
    }

    public static IReadOnlyList<DomainError> Validate(CommentDeletedEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        return Common(e.SchemaVersion, e.EventId, [("commentId", e.CommentId), ("videoId", e.VideoId)]);
    }

    public Task HandleAsync(VideoProcessedEvent e, CancellationToken ct) => likes.UpsertVideoAsync(e.VideoId, e.OwnerId!, ct);

    public Task HandleAsync(VideoDeletedEvent e, CancellationToken ct) => likes.ForgetVideoAsync(e.VideoId, e.OwnerId, ct);

    public Task HandleAsync(CommentAddedEvent e, CancellationToken ct) => likes.UpsertCommentAsync(e.CommentId, e.VideoId, e.AuthorId!, ct);

    public Task HandleAsync(CommentDeletedEvent e, CancellationToken ct) => likes.ForgetCommentAsync(e.CommentId, e.VideoId, ct);

    private static List<DomainError> Common(int schemaVersion, Guid eventId, (string Name, Guid Value)[] ids)
    {
        var errors = new List<DomainError>();
        if (schemaVersion != SupportedSchemaVersion)
        {
            errors.Add(DomainError.Validation("schemaVersion", $"schemaVersion supported: {SupportedSchemaVersion}."));
        }

        if (eventId == Guid.Empty)
        {
            errors.Add(DomainError.Validation("eventId", "eventId is required."));
        }

        foreach (var (name, value) in ids)
        {
            if (value == Guid.Empty)
            {
                errors.Add(DomainError.Validation(name, $"{name} is required."));
            }
        }

        return errors;
    }

    private static void RequireUser(List<DomainError> errors, string field, string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxUserIdLength)
        {
            errors.Add(DomainError.Validation(field, $"{field} is required (up to {MaxUserIdLength} characters)."));
        }
    }
}

/// <summary>Registration and routes of the Likes feature (public and internal).</summary>
public static class LikesFeatures
{
    public static IServiceCollection AddLikesFeatures(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, LikesFeatureJsonContext.Default));
        services.AddSingleton<SetLikeHandler>();
        services.AddSingleton<GetCountsHandler>();
        services.AddSingleton<ListLikersHandler>();
        services.AddSingleton<EventIntakeHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapLikesFeatures(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Public routes: user JWT (via the gateway).
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Likes");

        api.MapPut("/videos/{videoId:guid}/like", async (Guid videoId, HttpContext http, SetLikeHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleVideoAsync(videoId, http.User.UserId(), liked: true, ct);
            return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
        }).WithName("LikeVideo");

        api.MapDelete("/videos/{videoId:guid}/like", async (Guid videoId, HttpContext http, SetLikeHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleVideoAsync(videoId, http.User.UserId(), liked: false, ct);
            return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
        }).WithName("UnlikeVideo");

        api.MapPut("/videos/{videoId:guid}/comments/{commentId:guid}/like", async (Guid videoId, Guid commentId, HttpContext http, SetLikeHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleCommentAsync(videoId, commentId, http.User.UserId(), liked: true, ct);
            return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
        }).WithName("LikeComment");

        api.MapDelete("/videos/{videoId:guid}/comments/{commentId:guid}/like", async (Guid videoId, Guid commentId, HttpContext http, SetLikeHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleCommentAsync(videoId, commentId, http.User.UserId(), liked: false, ct);
            return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
        }).WithName("UnlikeComment");

        api.MapGet("/likes/videos", (string? ids, HttpContext http, GetCountsHandler handler, CancellationToken ct) =>
            CountsAsync(LikeTargetType.Video, ids, http, handler, ct)).WithName("GetVideoLikeCounts");

        api.MapGet("/likes/comments", (string? ids, HttpContext http, GetCountsHandler handler, CancellationToken ct) =>
            CountsAsync(LikeTargetType.Comment, ids, http, handler, ct)).WithName("GetCommentLikeCounts");

        api.MapGet("/videos/{videoId:guid}/likes", async (Guid videoId, string? cursor, int? limit, ListLikersHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(videoId, cursor, limit, ct);
            return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
        }).WithName("ListLikers");

        // Internal routes: key only, not published through the gateway.
        var internalApi = app.MapGroup("/internal/v1").WithTags("Internal");

        internalApi.MapPost("/events/video-processed", (VideoProcessedEvent e, EventIntakeHandler handler, CancellationToken ct) =>
            AcceptAsync(EventIntakeHandler.Validate(e), () => handler.HandleAsync(e, ct))).RequireInternalKey().WithName("AcceptVideoProcessed");

        internalApi.MapPost("/events/video-deleted", (VideoDeletedEvent e, EventIntakeHandler handler, CancellationToken ct) =>
            AcceptAsync(EventIntakeHandler.Validate(e), () => handler.HandleAsync(e, ct))).RequireInternalKey().WithName("AcceptVideoDeleted");

        internalApi.MapPost("/events/comment-added", (CommentAddedEvent e, EventIntakeHandler handler, CancellationToken ct) =>
            AcceptAsync(EventIntakeHandler.Validate(e), () => handler.HandleAsync(e, ct))).RequireInternalKey().WithName("AcceptCommentAdded");

        internalApi.MapPost("/events/comment-deleted", (CommentDeletedEvent e, EventIntakeHandler handler, CancellationToken ct) =>
            AcceptAsync(EventIntakeHandler.Validate(e), () => handler.HandleAsync(e, ct))).RequireInternalKey().WithName("AcceptCommentDeleted");

        return app;
    }

    private static async Task<IResult> CountsAsync(LikeTargetType type, string? ids, HttpContext http, GetCountsHandler handler, CancellationToken ct)
    {
        var (parsed, errors) = GetCountsHandler.Parse(ids);
        if (errors.Count > 0)
        {
            return errors.ToValidationProblem();
        }

        return TypedResults.Ok(await handler.HandleAsync(type, parsed, http.User.UserId(), ct));
    }

    private static async Task<IResult> AcceptAsync(IReadOnlyList<DomainError> errors, Func<Task> apply)
    {
        if (errors.Count > 0)
        {
            return errors.ToValidationProblem();
        }

        await apply();
        return TypedResults.Accepted((string?)null, new AcceptedDto(true));
    }
}
