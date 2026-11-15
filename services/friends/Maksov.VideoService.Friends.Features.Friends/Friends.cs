using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.Friends.Bll.Ports;
using Maksov.VideoService.Primitives;
using Maksov.VideoService.Web;

namespace Maksov.VideoService.Friends.Features.Friends;

public sealed record StatsDto(int FollowersCount, int FollowingCount, bool IsFollowing, bool FollowsYou, bool IsMutual);

/// <summary>Relation of the viewer to the asked ids: whom the viewer follows and who follows the viewer.</summary>
public sealed record StatusDto(IReadOnlyList<string> Following, IReadOnlyList<string> FollowedBy);

public sealed record RelationItemDto(string UserId, DateTimeOffset FollowedAt, bool IsFollowing, bool FollowsYou, bool IsMutual);

public sealed record RelationPageDto(IReadOnlyList<RelationItemDto> Items, string? NextCursor, int Total);

public sealed record RecommendationDto(string UserId, int MutualFriends, double Similarity);

public sealed record RecommendationsDto(IReadOnlyList<RecommendationDto> Items);

/// <summary>Following identifiers for internal consumers (the feed service).</summary>
public sealed record FollowingIdsDto(IReadOnlyList<string> Ids);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(StatsDto))]
[JsonSerializable(typeof(StatusDto))]
[JsonSerializable(typeof(RelationPageDto))]
[JsonSerializable(typeof(RecommendationsDto))]
[JsonSerializable(typeof(FollowingIdsDto))]
internal sealed partial class FriendsFeatureJsonContext : JsonSerializerContext;

/// <summary>Follow slice: self -> 400, unknown profile -> 404, repeat is a no-op.</summary>
internal sealed class FollowHandler(IFriendsRepository friends, IProfileDirectory profiles)
{
    public async Task<Result<bool>> FollowAsync(string followerId, string followeeId, CancellationToken ct)
    {
        if (string.Equals(followerId, followeeId, StringComparison.Ordinal))
        {
            return DomainError.Validation("userId", "You cannot follow yourself.");
        }

        if (!await profiles.ExistsAsync(followeeId, ct))
        {
            return DomainError.NotFound("profile_not_found", "Profile not found");
        }

        await friends.FollowAsync(followerId, followeeId, ct);
        return true;
    }

    public async Task<Result<bool>> UnfollowAsync(string followerId, string followeeId, CancellationToken ct)
    {
        await friends.UnfollowAsync(followerId, followeeId, ct);
        return true;
    }
}

internal sealed class GetStatsHandler(IFriendsRepository friends)
{
    public async Task<StatsDto> HandleAsync(string userId, string viewerId, CancellationToken ct)
    {
        var s = await friends.GetStatsAsync(userId, viewerId, ct);
        return new StatsDto(s.FollowersCount, s.FollowingCount, s.IsFollowing, s.FollowsYou, s.IsMutual);
    }
}

internal sealed class GetStatusHandler(IFriendsRepository friends)
{
    public const int MaxIds = 50;

    public async Task<StatusDto> HandleAsync(string viewerId, string? ids, CancellationToken ct)
    {
        var wanted = (ids ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal)
            .Take(MaxIds)
            .ToArray();
        return new StatusDto(
            await friends.GetFollowedAmongAsync(viewerId, wanted, ct),
            await friends.GetFollowedByAmongAsync(viewerId, wanted, ct));
    }
}

/// <summary>ListFollowers / ListFollowing slices: paged lists of people with the relation to the viewer (follows you, mutual).</summary>
internal sealed class ListRelationsHandler(IFriendsRepository friends)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;

    public Task<RelationPageDto> FollowersAsync(string userId, string viewerId, string? cursor, int? limit, CancellationToken ct) =>
        PageAsync(true, userId, viewerId, cursor, limit, ct);

    public Task<RelationPageDto> FollowingAsync(string userId, string viewerId, string? cursor, int? limit, CancellationToken ct) =>
        PageAsync(false, userId, viewerId, cursor, limit, ct);

    private async Task<RelationPageDto> PageAsync(bool followers, string userId, string viewerId, string? cursor, int? limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
        var after = RelationCursor.TryDecode(cursor);

        // One extra row tells whether there is a next page, without COUNT(*) over the page query.
        var rows = followers
            ? await friends.ListFollowersAsync(userId, viewerId, after, take + 1, ct)
            : await friends.ListFollowingAsync(userId, viewerId, after, take + 1, ct);
        var hasMore = rows.Count > take;
        var page = hasMore ? rows.Take(take).ToList() : rows;
        var next = hasMore ? new RelationCursor(page[^1].FollowedAt, page[^1].UserId).Encode() : null;

        var stats = await friends.GetStatsAsync(userId, viewerId, ct);
        var total = followers ? stats.FollowersCount : stats.FollowingCount;
        return new RelationPageDto(
            page.Select(static r => new RelationItemDto(r.UserId, r.FollowedAt, r.IsFollowing, r.FollowsYou, r.IsFollowing && r.FollowsYou)).ToList(),
            next,
            total);
    }
}

internal sealed class RecommendHandler(IFriendsRepository friends)
{
    public const int DefaultLimit = 10;
    public const int MaxLimit = 30;

    public async Task<RecommendationsDto> HandleAsync(string viewerId, int? limit, CancellationToken ct)
    {
        var rows = await friends.RecommendAsync(viewerId, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), ct);
        return new RecommendationsDto(rows.Select(static r => new RecommendationDto(r.UserId, r.MutualFriends, Math.Round(r.Similarity, 4))).ToList());
    }
}

internal sealed class GetFollowingIdsHandler(IFriendsRepository friends)
{
    private const int Max = 5000;

    public async Task<FollowingIdsDto> HandleAsync(string userId, CancellationToken ct) =>
        new(await friends.GetFollowingIdsAsync(userId, Max, ct));
}

/// <summary>Registration and routes of the "Friends" feature (public and internal).</summary>
public static class FriendsFeatures
{
    public static IServiceCollection AddFriendsFeatures(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, FriendsFeatureJsonContext.Default));
        services.AddSingleton<FollowHandler>();
        services.AddSingleton<GetStatsHandler>();
        services.AddSingleton<GetStatusHandler>();
        services.AddSingleton<ListRelationsHandler>();
        services.AddSingleton<RecommendHandler>();
        services.AddSingleton<GetFollowingIdsHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapFriendsFeatures(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Public routes: the user's JWT (through the gateway).
        var api = app.MapGroup("/api").RequireAuthorization().WithTags("Friends");

        api.MapPut("/users/{id}/follow", async (string id, HttpContext http, FollowHandler handler, CancellationToken ct) =>
        {
            try
            {
                var result = await handler.FollowAsync(http.User.UserId(), id, ct);
                return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
            }
            catch (HttpRequestException)
            {
                // The users service cannot confirm that the profile exists: do not guess.
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "Profile lookup is temporarily unavailable");
            }
        }).WithName("Follow");

        api.MapDelete("/users/{id}/follow", async (string id, HttpContext http, FollowHandler handler, CancellationToken ct) =>
        {
            var result = await handler.UnfollowAsync(http.User.UserId(), id, ct);
            return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
        }).WithName("Unfollow");

        api.MapGet("/friends/{id}/stats", async (string id, HttpContext http, GetStatsHandler handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(id, http.User.UserId(), ct))).WithName("GetStats");

        api.MapGet("/friends/{id}/followers", async (string id, string? cursor, int? limit, HttpContext http, ListRelationsHandler handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.FollowersAsync(id, http.User.UserId(), cursor, limit, ct))).WithName("ListFollowers");

        api.MapGet("/friends/{id}/following", async (string id, string? cursor, int? limit, HttpContext http, ListRelationsHandler handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.FollowingAsync(id, http.User.UserId(), cursor, limit, ct))).WithName("ListFollowing");

        api.MapGet("/friends/status", async (string? ids, HttpContext http, GetStatusHandler handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(http.User.UserId(), ids, ct))).WithName("GetStatus");

        api.MapGet("/friends/recommendations", async (int? limit, HttpContext http, RecommendHandler handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(http.User.UserId(), limit, ct))).WithName("Recommend");

        // Internal route: only with the key, not published through the gateway.
        app.MapGroup("/internal/v1").WithTags("Internal")
            .MapGet("/friends/{userId}/following-ids", async (string userId, GetFollowingIdsHandler handler, CancellationToken ct) =>
                TypedResults.Ok(await handler.HandleAsync(userId, ct)))
            .RequireInternalKey()
            .WithName("GetFollowingIds");

        return app;
    }
}
