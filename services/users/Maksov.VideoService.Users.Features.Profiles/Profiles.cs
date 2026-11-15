using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.Primitives;
using Maksov.VideoService.Users.Bll.Ports;
using Maksov.VideoService.Web;

namespace Maksov.VideoService.Users.Features.Profiles;

/// <summary>Own profile ("me").</summary>
public sealed record ProfileDto(string Id, string Username, string DisplayName, string Bio, DateTimeOffset CreatedAt);

/// <summary>Another person's profile: login is visible (as on Instagram), plus counters and the "you follow" flag.</summary>
public sealed record PublicProfileDto(string Id, string Username, string DisplayName, string Bio);

public sealed record UserSummaryDto(string Id, string Username, string DisplayName, string Bio);

public sealed record UserSearchResponse(IReadOnlyList<UserSummaryDto> Items);

public sealed record UpdateProfileRequest(string? DisplayName, string? Bio);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(ProfileDto))]
[JsonSerializable(typeof(PublicProfileDto))]
[JsonSerializable(typeof(UserSearchResponse))]
[JsonSerializable(typeof(UpdateProfileRequest))]
internal sealed partial class ProfilesJsonContext : JsonSerializerContext;

internal sealed class GetMeHandler(IProfileRepository profiles)
{
    public async Task<ProfileDto> HandleAsync(string userId, string username, CancellationToken ct)
    {
        var p = await profiles.GetOrCreateAsync(userId, username, ct);
        return new ProfileDto(p.UserId, p.Username, p.DisplayName, p.Bio, p.CreatedAt);
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
    public async Task<Result<PublicProfileDto>> HandleAsync(string userId, CancellationToken ct)
    {
        var p = await profiles.GetAsync(userId, ct);
        return p is null
            ? DomainError.NotFound("profile_not_found", "Profile not found")
            : new PublicProfileDto(p.UserId, p.Username, p.DisplayName, p.Bio);
    }
}

internal sealed class SearchUsersHandler(IProfileRepository profiles)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 50;
    public const int MaxQueryLength = 60;
    public const int MaxIds = 50;

    public async Task<UserSearchResponse> HandleAsync(string viewerId, string? query, string? ids, int? limit, CancellationToken ct)
    {
        // Batch lookup by identifiers (e.g. recommendations from the friends service).
        if (!string.IsNullOrWhiteSpace(ids))
        {
            var wanted = ids.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Take(MaxIds)
                .ToArray();
            var found = await profiles.GetManyAsync(wanted, ct);
            return new UserSearchResponse(found.Select(static r => new UserSummaryDto(r.UserId, r.Username, r.DisplayName, r.Bio)).ToList());
        }

        // Empty query means empty result: we never return "all users".
        var q = query?.Trim();
        if (string.IsNullOrEmpty(q))
        {
            return new UserSearchResponse([]);
        }

        q = q.Length > MaxQueryLength ? q[..MaxQueryLength] : q;
        var rows = await profiles.SearchAsync(q, viewerId, Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit), ct);
        return new UserSearchResponse(rows.Select(static r => new UserSummaryDto(r.UserId, r.Username, r.DisplayName, r.Bio)).ToList());
    }
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

        users.MapGet("/", async (string? query, string? ids, int? limit, HttpContext http, SearchUsersHandler handler, CancellationToken ct) =>
            TypedResults.Ok(await handler.HandleAsync(http.User.UserId(), query, ids, limit, ct)))
            .WithName("SearchUsers");

        users.MapGet("/{id}", async (string id, HttpContext http, GetProfileHandler handler, CancellationToken ct) =>
        {
            var result = await handler.HandleAsync(id, ct);
            return result.IsSuccess ? TypedResults.Ok(result.Value) : result.Error!.ToProblem();
        }).WithName("GetProfile");

        // Internal route: only with the key, not published through the gateway.
        app.MapGroup("/internal/v1").WithTags("Internal")
            .MapGet("/users/{id}/exists", async (string id, IProfileRepository profiles, CancellationToken ct) =>
                await profiles.ExistsAsync(id, ct) ? Results.NoContent() : Results.NotFound())
            .RequireInternalKey()
            .WithName("ProfileExists");

        return app;
    }
}
