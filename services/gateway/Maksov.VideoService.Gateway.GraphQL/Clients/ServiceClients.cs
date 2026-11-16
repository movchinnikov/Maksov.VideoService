using Maksov.VideoService.Gateway.GraphQL.Schema;

namespace Maksov.VideoService.Gateway.GraphQL.Clients;

public sealed class UsersClient(HttpClient http) : DownstreamClient(http, "users")
{
    public async Task<User> GetMeAsync(CancellationToken cancellationToken) =>
        await GetAsync<User>("api/users/me", cancellationToken);

    public async Task<User> GetUserAsync(string id, CancellationToken cancellationToken) =>
        await GetAsync<User>($"api/users/{Escape(id)}", cancellationToken);

    public async Task<IReadOnlyList<User>> GetManyAsync(IEnumerable<string> ids, CancellationToken cancellationToken) =>
        (await GetAsync<UserList>($"api/users?ids={Join(ids)}", cancellationToken)).Items;

    public async Task<IReadOnlyList<User>> SearchAsync(string query, int limit, CancellationToken cancellationToken) =>
        (await GetAsync<UserList>(WithQuery("api/users", ("query", query), ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture))), cancellationToken)).Items;
}

public sealed class VideosClient(HttpClient http) : DownstreamClient(http, "videos")
{
    public async Task<IReadOnlyList<Video>> GetCardsAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken) =>
        (await GetAsync<VideoCards>($"api/videos/cards?ids={Join(ids)}", cancellationToken)).Items;

    public async Task<VideoPage> ListUserVideosAsync(string userId, string? cursor, int limit, CancellationToken cancellationToken) =>
        await GetAsync<VideoPage>(
            WithQuery($"api/users/{Escape(userId)}/videos", ("cursor", cursor), ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            cancellationToken);
}

public sealed class FeedClient(HttpClient http) : DownstreamClient(http, "feed")
{
    public async Task<FeedIds> GetFeedAsync(string? cursor, int limit, CancellationToken cancellationToken) =>
        await GetAsync<FeedIds>(
            WithQuery("api/feed", ("cursor", cursor), ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            cancellationToken);
}

public sealed class FriendsClient(HttpClient http) : DownstreamClient(http, "friends")
{
    public async Task<FriendStats> GetStatsAsync(string userId, CancellationToken cancellationToken) =>
        await GetAsync<FriendStats>($"api/friends/{Escape(userId)}/stats", cancellationToken);

    public async Task<RelationPage> ListFollowersAsync(string userId, string? cursor, int limit, CancellationToken cancellationToken) =>
        await GetAsync<RelationPage>(
            WithQuery($"api/friends/{Escape(userId)}/followers", ("cursor", cursor), ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            cancellationToken);

    public async Task<RelationPage> ListFollowingAsync(string userId, string? cursor, int limit, CancellationToken cancellationToken) =>
        await GetAsync<RelationPage>(
            WithQuery($"api/friends/{Escape(userId)}/following", ("cursor", cursor), ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            cancellationToken);

    public async Task<IReadOnlyList<Recommendation>> GetRecommendationsAsync(int limit, CancellationToken cancellationToken) =>
        (await GetAsync<RecommendationList>(WithQuery("api/friends/recommendations", ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture))), cancellationToken)).Items;

    public async Task FollowAsync(string userId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Put, $"api/users/{Escape(userId)}/follow", cancellationToken);

    public async Task UnfollowAsync(string userId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Delete, $"api/users/{Escape(userId)}/follow", cancellationToken);
}

public sealed class CommentsClient(HttpClient http) : DownstreamClient(http, "comments")
{
    public async Task<CommentPage> ListAsync(Guid videoId, string? cursor, int limit, CancellationToken cancellationToken) =>
        await GetAsync<CommentPage>(
            WithQuery($"api/videos/{videoId}/comments", ("cursor", cursor), ("limit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture))),
            cancellationToken);

    public async Task<Comment> AddAsync(Guid videoId, string text, CancellationToken cancellationToken) =>
        await SendAsync<Comment>(HttpMethod.Post, $"api/videos/{videoId}/comments", new AddCommentBody(text), cancellationToken);

    public async Task DeleteAsync(Guid videoId, Guid commentId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Delete, $"api/videos/{videoId}/comments/{commentId}", cancellationToken);
}

public sealed class LikesClient(HttpClient http) : DownstreamClient(http, "likes")
{
    public async Task<IReadOnlyList<LikeItem>> GetVideoLikesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken) =>
        (await GetAsync<LikeItems>($"api/likes/videos?ids={Join(ids)}", cancellationToken)).Items;

    public async Task<IReadOnlyList<LikeItem>> GetCommentLikesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken) =>
        (await GetAsync<LikeItems>($"api/likes/comments?ids={Join(ids)}", cancellationToken)).Items;

    public async Task LikeVideoAsync(Guid videoId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Put, $"api/videos/{videoId}/like", cancellationToken);

    public async Task UnlikeVideoAsync(Guid videoId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Delete, $"api/videos/{videoId}/like", cancellationToken);

    public async Task LikeCommentAsync(Guid videoId, Guid commentId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Put, $"api/videos/{videoId}/comments/{commentId}/like", cancellationToken);

    public async Task UnlikeCommentAsync(Guid videoId, Guid commentId, CancellationToken cancellationToken) =>
        await SendAsync(HttpMethod.Delete, $"api/videos/{videoId}/comments/{commentId}/like", cancellationToken);
}

public sealed class AnalysisClient(HttpClient http) : DownstreamClient(http, "analysis")
{
    /// <summary>The description of a video; null when the service has none visible to the caller (404).</summary>
    public async Task<Description?> GetDescriptionAsync(Guid videoId, CancellationToken cancellationToken) =>
        await GetOrNullAsync<Description>($"api/videos/{videoId}/description", cancellationToken);
}
