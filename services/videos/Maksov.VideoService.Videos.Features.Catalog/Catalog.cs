using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.Videos.Bll.Ports;
using Maksov.VideoService.Web;

namespace Maksov.VideoService.Videos.Features.Catalog;

public sealed record FeedPlaybackDto(string Dash, string Hls, string Poster);

/// <summary>Video card in the feed and in the profile grid.</summary>
public sealed record FeedItemDto(
    Guid Id,
    string Title,
    string OwnerId,
    string OwnerUsername,
    DateTimeOffset CreatedAt,
    int DurationMs,
    int Width,
    int Height,
    FeedPlaybackDto Playback)
{
    internal static FeedItemDto From(VideoRecord v) => new(
        v.Id, v.Title, v.OwnerId, v.OwnerUsername, v.CreatedAt, v.DurationMs ?? 0, v.Width ?? 0, v.Height ?? 0,
        new FeedPlaybackDto(
            Dash: $"/api/videos/{v.Id}/stream/{MediaKeys.DashManifest}",
            Hls: $"/api/videos/{v.Id}/stream/{MediaKeys.HlsMaster}",
            Poster: $"/api/videos/{v.Id}/poster"));
}

public sealed record CardsResponse(IReadOnlyList<FeedItemDto> Items);

public sealed record ReadyByOwnersRequest(IReadOnlyList<string>? OwnerIds, string? Cursor, int? Limit);

public sealed record VideoIdDto(Guid Id);

public sealed record ReadyByOwnersResponse(IReadOnlyList<VideoIdDto> Items, string? NextCursor);

public sealed record FeedPage(IReadOnlyList<FeedItemDto> Items, string? NextCursor);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(FeedPage))]
[JsonSerializable(typeof(CardsResponse))]
[JsonSerializable(typeof(ReadyByOwnersRequest))]
[JsonSerializable(typeof(ReadyByOwnersResponse))]
[JsonSerializable(typeof(FeedItemDto))]
internal sealed partial class CatalogJsonContext : JsonSerializerContext;

internal static class Paging
{
    public const int DefaultLimit = 12;
    public const int MaxLimit = 30;

    /// <summary>Fetch one extra record to tell whether there is a next page, without COUNT(*).</summary>
    public static FeedPage ToPage(IReadOnlyList<VideoRecord> rows, int take)
    {
        var hasMore = rows.Count > take;
        var page = hasMore ? rows.Take(take).ToList() : rows;
        var next = hasMore ? new PageCursor(page[^1].CreatedAt, page[^1].Id).Encode() : null;
        return new FeedPage(page.Select(FeedItemDto.From).ToList(), next);
    }

    public static int Take(int? limit) => Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);
}

/// <summary>ListUserVideos slice: a person's ready videos (profile grid), public to authenticated users.</summary>
internal sealed class ListUserVideosHandler(IVideoRepository videos)
{
    public async Task<FeedPage> HandleAsync(string userId, string? cursor, int? limit, CancellationToken ct)
    {
        var take = Paging.Take(limit);
        var rows = await videos.ListReadyByOwnersAsync([userId], PageCursor.TryDecode(cursor), take + 1, ct);
        return Paging.ToPage(rows, take);
    }
}

/// <summary>GetCards slice: cards of ready videos by ids, in the requested order (unknown or not ready ids are skipped).</summary>
internal sealed class GetCardsHandler(IVideoRepository videos)
{
    public const int MaxIds = 30;

    public static bool TryParseIds(string? ids, out IReadOnlyList<Guid> parsed)
    {
        var list = new List<Guid>();
        parsed = list;
        if (string.IsNullOrWhiteSpace(ids))
        {
            return true;
        }

        foreach (var part in ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!Guid.TryParse(part, out var id))
            {
                return false;
            }

            if (!list.Contains(id))
            {
                list.Add(id);
            }

            if (list.Count > MaxIds)
            {
                return false;
            }
        }

        return true;
    }

    public async Task<CardsResponse> HandleAsync(IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var rows = (await videos.GetReadyByIdsAsync(ids, ct)).ToDictionary(static v => v.Id);
        return new CardsResponse(ids.Where(rows.ContainsKey).Select(id => FeedItemDto.From(rows[id])).ToList());
    }
}

/// <summary>ReadyByOwners slice (internal): ids of the newest ready videos of the given owners, for the feed service.</summary>
internal sealed class ReadyByOwnersHandler(IVideoRepository videos)
{
    private const int MaxOwners = 2000;

    public async Task<ReadyByOwnersResponse> HandleAsync(ReadyByOwnersRequest request, CancellationToken ct)
    {
        var take = Paging.Take(request.Limit);
        var owners = (request.OwnerIds ?? []).Distinct(StringComparer.Ordinal).Take(MaxOwners).ToArray();
        var rows = await videos.ListReadyByOwnersAsync(owners, PageCursor.TryDecode(request.Cursor), take + 1, ct);
        var hasMore = rows.Count > take;
        var page = hasMore ? rows.Take(take).ToList() : rows;
        var next = hasMore ? new PageCursor(page[^1].CreatedAt, page[^1].Id).Encode() : null;
        return new ReadyByOwnersResponse(page.Select(static v => new VideoIdDto(v.Id)).ToList(), next);
    }
}

/// <summary>Registration and routes of the "Catalog" feature.</summary>
public static class CatalogFeatures
{
    public static IServiceCollection AddCatalogFeatures(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, CatalogJsonContext.Default));
        services.AddSingleton<ListUserVideosHandler>();
        services.AddSingleton<GetCardsHandler>();
        services.AddSingleton<ReadyByOwnersHandler>();
        return services;
    }

    /// <param name="api">The <c>/api</c> group with mandatory authorization.</param>
    public static RouteGroupBuilder MapCatalogFeatures(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        api.MapGet("/users/{id}/videos", async (string id, string? cursor, int? limit, ListUserVideosHandler handler, CancellationToken ct) =>
                TypedResults.Ok(await handler.HandleAsync(id, cursor, limit, ct)))
            .WithTags("Catalog")
            .WithName("ListUserVideos");

        api.MapGet("/videos/cards", async (string? ids, GetCardsHandler handler, CancellationToken ct) =>
            {
                if (!GetCardsHandler.TryParseIds(ids, out var parsed))
                {
                    return Results.ValidationProblem(new Dictionary<string, string[]> { ["ids"] = [$"Pass up to {GetCardsHandler.MaxIds} comma-separated video ids."] });
                }

                return TypedResults.Ok(await handler.HandleAsync(parsed, ct));
            })
            .WithTags("Catalog")
            .WithName("GetCards");

        return api;
    }

    /// <summary>Internal routes: only with the key, not published through the gateway.</summary>
    public static IEndpointRouteBuilder MapCatalogInternalFeatures(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapGroup("/internal/v1").WithTags("Internal")
            .MapPost("/videos/ready-by-owners", async (ReadyByOwnersRequest request, ReadyByOwnersHandler handler, CancellationToken ct) =>
                TypedResults.Ok(await handler.HandleAsync(request, ct)))
            .RequireInternalKey()
            .WithName("ReadyByOwners");

        return app;
    }
}
