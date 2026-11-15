using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.Primitives;
using Maksov.VideoService.Users.Bll.Ports;
using Maksov.VideoService.Web;

namespace Maksov.VideoService.Users.Features.Profiles;

/// <summary>Own profile ("me"): with login and counters.</summary>
public sealed record ProfileDto(string Id, string Username, string DisplayName, string Bio, DateTimeOffset CreatedAt, int FollowersCount, int FollowingCount);

/// <summary>Another person's profile: login is visible (as on Instagram), plus counters and the "you follow" flag.</summary>
public sealed record PublicProfileDto(string Id, string Username, string DisplayName, string Bio, int FollowersCount, int FollowingCount, bool IsFollowing);

public sealed record UserSummaryDto(string Id, string Username, string DisplayName, string Bio, bool IsFollowing);

public sealed record UserSearchResponse(IReadOnlyList<UserSummaryDto> Items);

public sealed record UpdateProfileRequest(string? DisplayName, string? Bio);

/// <summary>Followee identifiers for the internal consumer (the feed in the videos service).</summary>
public sealed record FollowingIdsResponse(IReadOnlyList<string> Ids);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ProfileDto))]
[JsonSerializable(typeof(PublicProfileDto))]
[JsonSerializable(typeof(UserSearchResponse))]
[JsonSerializable(typeof(UpdateProfileRequest))]
[JsonSerializable(typeof(FollowingIdsResponse))]
internal sealed partial class ProfilesJsonContext : JsonSerializerContext;

internal sealed class GetMeHandler(IProfileRepository profiles)
{
    public async Task<ProfileDto> HandleAsync(string userId, string username, CancellationToken ct)
    {
        await profiles.GetOrCreateAsync(userId, username, ct);
        var v = (await profiles.GetViewAsync(userId, userId, ct))!;
        return new ProfileDto(v.Profile.UserId, v.Profile.Username, v.Profile.DisplayName, v.Profile.Bio, v.Profile.CreatedAt, v.FollowersCount, v.FollowingCount);
    }
}

internal sealed class UpdateMeHandler(IProfileRepository profiles, GetMeHandler me)
{
    public const int MaxDisplayName = 60;
    public const int MaxBio = 300;

    public static IReadOnlyList<DomainError> Validate(UpdateProfileRequest request)
    {
        var errors = new List<DomainError>(2);
        var name = request.DisplayName?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            errors.Add(DomainError.Validation("displayName", "Enter a name."));
        }
        else if (name.Length > MaxDisplayName)
        {
            errors.Add(DomainError.Validation("displayName", $"Name is longer than {MaxDisplayName} characters."));
        }

        if ((request.Bio?.Trim().Length ?? 0) > MaxBio)
        {
            errors.Add(DomainError.Validation("bio", $"The \"About\" text is longer than {MaxBio} characters."));
        }

        return errors;
    }

    public async Task<Result<ProfileDto>> HandleAsync(string userId, string username, UpdateProfileRequest request, CancellationToken ct)
    {
        await profiles.GetOrCreateAsync(userId, username, ct);
        var p = await profiles.UpdateAsync(userId, request.DisplayName!.Trim(), (request.Bio ?? "").Trim(), ct);
        return p is null ? DomainError.NotFound() : await me.HandleAsync(userId, username, ct);
    }
}

internal sealed class GetProfileHandler(IProfileRepository profiles)
{
    public async Task<Result<PublicProfileDto>> HandleAsync(string userId, string viewerId, CancellationToken ct)
    {
        var v = await profiles.GetViewAsync(userId, viewerId, ct);
        return v is null
            ? DomainError.NotFound("profile_not_found", "Profile not found")
            : new PublicProfileDto(v.Profile.UserId, v.Profile.Username, v.Profile.DisplayName, v.Profile.Bio, v.FollowersCount, v.FollowingCount, v.IsFollowing);
    }
}

internal sealed class SearchUsersHandler(IProfileRepository profiles)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
    public const int MaxQueryLength = 60;

    public async Task<UserSearchResponse> HandleAsync(string viewerId, string? query, int? limit, CancellationToken ct)
    {
        // Empty query means empty result: we never return "all users".
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            return new UserSearchResponse([]);
        }

        q = q.Length > MaxQueryLength ? q[..MaxQueryLength] : q;
        var rows = await profiles.SearchAsync(q, viewerId, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), ct);
        return new UserSearchResponse(rows.Select(static r => new UserSummaryDto(r.UserId, r.Username, r.DisplayName, r.Bio, r.IsFollowing)).ToList());
    }
}

internal sealed class FollowHandler(IProfileRepository profiles)
{
    public async Task<Result<bool>> FollowAsync(string followerId, string followeeId, CancellationToken ct)
    {
        if (string.Equals(followerId, followeeId, StringComparison.Ordinal))
        {
            return DomainError.Validation("userId", "You cannot follow yourself.");
        }

        return await profiles.FollowAsync(followerId, followeeId, ct) ? true : DomainError.NotFound("profile_not_found", "Profile not found");
    }

    public async Task<Result<bool>> UnfollowAsync(string followerId, string followeeId, CancellationToken ct)
    {
        await profiles.UnfollowAsync(followerId, followeeId, ct);
        return true;
    }
}

internal sealed class GetFollowingIdsHandler(IProfileRepository profiles)
{
    private const int Max = 5000;

    public async Task<FollowingIdsResponse> HandleAsync(string userId, CancellationToken ct) =>
        new(await profiles.GetFollowingIdsAsync(userId, Max, ct));
}

/// <summary>Registration and routes of the Profiles feature (public and internal).</summary>
public static class ProfilesFeatures
{
    public static IServiceCollection AddProfilesFeatures(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(static o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, ProfilesJsonContext.Default));
        services.AddSingleton<GetMeHandler>();
        services.AddSingleton<UpdateMeHandler>();
        services.AddSingleton<GetProfileHandler>();
        services.AddSingleton<SearchUsersHandler>();
        services.AddSingleton<FollowHandler>();
        services.AddSingleton<GetFollowingIdsHandler>();
        return services;
    }

    public static IEndpointRouteBuilder MapProfilesFeatures(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        // Public routes: user JWT (via the gateway).
        var users = app.MapGroup("/api/users").RequireAuthorization().WithTags("Users");

        users.MapGet("/me", async (HttpContext http, GetMeHandler handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(http.User.UserId(), http.User.Username(), ct)))
            .WithName("GetMe");

        users.MapPut("/me", async (UpdateProfileRequest request, HttpContext http, UpdateMeHandler handler, CancellationToken ct) =>
        {
            var errors = UpdateMeHandler.Validate(request);
            if (errors.Count > 0)
            {
                return errors.ToValidationProblem();
            }

            var result = await handler.HandleAsync(http.User.UserId(), http.User.Username(), request, ct);
            return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
        }).WithName("UpdateMe");

        users.MapGet("/", async (string? query, int? limit, HttpContext http, SearchUsersHandler handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(http.User.UserId(), query, limit, ct)))
            .WithName("SearchUsers");

        users.MapGet("/{id}", async (string id, HttpContext http, GetProfileHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(id, http.User.UserId(), ct);
            return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
        }).WithName("GetProfile");

        users.MapPut("/{id}/follow", async (string id, HttpContext http, FollowHandler handler, CancellationToken ct) =>
        {
            var result = await handler.FollowAsync(http.User.UserId(), id, ct);
            return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
        }).WithName("Follow");

        users.MapDelete("/{id}/follow", async (string id, HttpContext http, FollowHandler handler, CancellationToken ct) =>
        {
            var result = await handler.UnfollowAsync(http.User.UserId(), id, ct);
            return result.IsSuccess ? TypedResults.NoContent() : result.Error!.ToProblem();
        }).WithName("Unfollow");

        // Internal route: key only, not published through the gateway.
        app.MapGroup("/internal/v1").WithTags("Internal")
            .MapGet("/users/{id}/following-ids", async (string id, GetFollowingIdsHandler handler, CancellationToken ct) =>
                TypedResults.Ok(await handler.HandleAsync(id, ct)))
            .RequireInternalKey()
            .WithName("GetFollowingIds");

        return app;
    }
}
