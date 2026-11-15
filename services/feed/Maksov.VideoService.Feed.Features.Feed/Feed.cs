using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.Feed.Bll.Ports;
using Maksov.VideoService.Web;

namespace Maksov.VideoService.Feed.Features.Feed;

/// <summary>A page of the feed: video ids only (cards are loaded from the videos service by the client).</summary>
public sealed record FeedIdsPage(IReadOnlyList<Guid> Ids, string? NextCursor);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FeedIdsPage))]
internal sealed partial class FeedFeatureJsonContext : JsonSerializerContext;

/// <summary>
/// GetFeed slice: the newest ready videos of the people the viewer follows plus the viewer's own.
/// If the friends service is unavailable the feed degrades to the viewer's own videos; if videos is unavailable the call fails (503).
/// </summary>
internal sealed class GetFeedHandler(IFollowingSource following, IVideoCatalog catalog)
{
    public const int DefaultLimit = 12;
    public const int MaxLimit = 30;
    private const int MaxOwners = 2000;

    public async Task<FeedIdsPage> HandleAsync(string viewerId, string? cursor, int? limit, CancellationToken ct)
    {
        IReadOnlyList<string> followed;
        try
        {
            followed = await following.GetFollowingIdsAsync(viewerId, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            followed = [];
        }

        var owners = followed.Append(viewerId).Distinct(StringComparer.Ordinal).Take(MaxOwners).ToArray();
        var page = await catalog.ReadyByOwnersAsync(owners, cursor, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), ct);
        return new FeedIdsPage(page.Ids, page.NextCursor);
    }
}

/// <summary>Registration and routes of the "Feed" feature.</summary>
public static class FeedFeatures
{
    public static IServiceCollection AddFeedFeatures(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, FeedFeatureJsonContext.Default));
        services.AddSingleton<GetFeedHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapFeedFeatures(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGroup("/api").RequireAuthorization().WithTags("Feed")
            .MapGet("/feed", async (string? cursor, int? limit, HttpContext http, GetFeedHandler handler, CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(await handler.HandleAsync(http.User.UserId(), cursor, limit, ct));
                }
                catch (HttpRequestException)
                {
                    return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The feed is temporarily unavailable");
                }
            })
            .WithName("GetFeed");

        return app;
    }
}
