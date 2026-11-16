using HotChocolate;
using HotChocolate.Types;
using Maksov.VideoService.Gateway.GraphQL.Clients;

namespace Maksov.VideoService.Gateway.GraphQL.Schema;

[ExtendObjectType(OperationTypeNames.Query)]
public static class QueryResolvers
{
    public static Task<User> Me([Service] UsersClient users, CancellationToken cancellationToken) =>
        users.GetMeAsync(cancellationToken);

    public static Task<User> User(string id, [Service] UsersClient users, CancellationToken cancellationToken) =>
        users.GetUserAsync(id, cancellationToken);

    /// <summary>Lookup by ids (batched through the loader) or prefix search by query.</summary>
    public static async Task<IReadOnlyList<User>> Users(
        string? query,
        IReadOnlyList<string>? ids,
        int? limit,
        [Service] UsersClient users,
        UserByIdDataLoader loader,
        CancellationToken cancellationToken)
    {
        if (ids is { Count: > 0 })
        {
            if (ids.Count > Args.MaxPageSize)
            {
                throw Args.Validation($"at most {Args.MaxPageSize} ids.");
            }

            var found = await loader.LoadAsync(ids.Distinct(StringComparer.Ordinal).ToArray(), cancellationToken);
            return found.Select(static m => m?.Value).OfType<User>().ToArray();
        }

        return await users.SearchAsync(query ?? string.Empty, Args.Limit(limit, 20), cancellationToken);
    }

    public static async Task<Video> Video(Guid id, VideoCardByIdDataLoader loader, CancellationToken cancellationToken) =>
        (await loader.LoadAsync(id, cancellationToken))?.Value ?? throw Args.NotFound("Video not found.");

    /// <summary>Feed: ids from the feed service, cards through the loader (order kept, unavailable videos skipped).</summary>
    public static async Task<VideoPage> Feed(
        string? cursor,
        int? limit,
        [Service] FeedClient feed,
        VideoCardByIdDataLoader loader,
        CancellationToken cancellationToken)
    {
        var page = await feed.GetFeedAsync(cursor, Args.Limit(limit, 12), cancellationToken);
        if (page.Ids.Count == 0)
        {
            return new VideoPage([], page.NextCursor);
        }

        var cards = await loader.LoadAsync(page.Ids, cancellationToken);
        return new VideoPage(cards.Select(static c => c?.Value).OfType<Video>().ToArray(), page.NextCursor);
    }

    public static Task<VideoPage> UserVideos(
        string userId,
        string? cursor,
        int? limit,
        [Service] VideosClient videos,
        CancellationToken cancellationToken) =>
        videos.ListUserVideosAsync(userId, cursor, Args.Limit(limit, 12), cancellationToken);

    public static Task<IReadOnlyList<Recommendation>> Recommendations(
        int? limit,
        [Service] FriendsClient friends,
        CancellationToken cancellationToken) =>
        friends.GetRecommendationsAsync(Args.Limit(limit, 10), cancellationToken);

    public static Task<RelationPage> Followers(
        string userId,
        string? cursor,
        int? limit,
        [Service] FriendsClient friends,
        CancellationToken cancellationToken) =>
        friends.ListFollowersAsync(userId, cursor, Args.Limit(limit, 20), cancellationToken);

    public static Task<RelationPage> Following(
        string userId,
        string? cursor,
        int? limit,
        [Service] FriendsClient friends,
        CancellationToken cancellationToken) =>
        friends.ListFollowingAsync(userId, cursor, Args.Limit(limit, 20), cancellationToken);
}

[ExtendObjectType(OperationTypeNames.Mutation)]
public static class MutationResolvers
{
    public static async Task<LikeInfo> LikeVideo(
        Guid videoId,
        [Service] LikesClient likes,
        CancellationToken cancellationToken)
    {
        await likes.LikeVideoAsync(videoId, cancellationToken);
        return await FreshVideoLikes(likes, videoId, cancellationToken);
    }

    public static async Task<LikeInfo> UnlikeVideo(
        Guid videoId,
        [Service] LikesClient likes,
        CancellationToken cancellationToken)
    {
        await likes.UnlikeVideoAsync(videoId, cancellationToken);
        return await FreshVideoLikes(likes, videoId, cancellationToken);
    }

    public static async Task<LikeInfo> LikeComment(
        Guid videoId,
        Guid commentId,
        [Service] LikesClient likes,
        CancellationToken cancellationToken)
    {
        await likes.LikeCommentAsync(videoId, commentId, cancellationToken);
        return await FreshCommentLikes(likes, commentId, cancellationToken);
    }

    public static async Task<LikeInfo> UnlikeComment(
        Guid videoId,
        Guid commentId,
        [Service] LikesClient likes,
        CancellationToken cancellationToken)
    {
        await likes.UnlikeCommentAsync(videoId, commentId, cancellationToken);
        return await FreshCommentLikes(likes, commentId, cancellationToken);
    }

    public static Task<Comment> AddComment(
        Guid videoId,
        string text,
        [Service] CommentsClient comments,
        CancellationToken cancellationToken) =>
        comments.AddAsync(videoId, text, cancellationToken);

    public static async Task<bool> DeleteComment(
        Guid videoId,
        Guid commentId,
        [Service] CommentsClient comments,
        CancellationToken cancellationToken)
    {
        await comments.DeleteAsync(videoId, commentId, cancellationToken);
        return true;
    }

    /// <summary>Follows (idempotent) and returns the fresh counters of the followed person.</summary>
    public static async Task<FriendStats> Follow(
        string userId,
        [Service] FriendsClient friends,
        CancellationToken cancellationToken)
    {
        await friends.FollowAsync(userId, cancellationToken);
        return await friends.GetStatsAsync(userId, cancellationToken);
    }

    public static async Task<FriendStats> Unfollow(
        string userId,
        [Service] FriendsClient friends,
        CancellationToken cancellationToken)
    {
        await friends.UnfollowAsync(userId, cancellationToken);
        return await friends.GetStatsAsync(userId, cancellationToken);
    }

    // The mutation reads the batch endpoint directly: the request-scoped DataLoader cache could hold the pre-mutation value.
    private static async Task<LikeInfo> FreshVideoLikes(LikesClient likes, Guid id, CancellationToken cancellationToken)
    {
        var items = await likes.GetVideoLikesAsync([id], cancellationToken);
        return LikeLookup.ToInfo([id], [.. items])[id];
    }

    private static async Task<LikeInfo> FreshCommentLikes(LikesClient likes, Guid id, CancellationToken cancellationToken)
    {
        var items = await likes.GetCommentLikesAsync([id], cancellationToken);
        return LikeLookup.ToInfo([id], [.. items])[id];
    }
}

[ExtendObjectType(typeof(User))]
public static class UserFields
{
    public static async Task<FriendStats> Stats([Parent] User user, FriendStatsDataLoader loader, CancellationToken cancellationToken) =>
        (await loader.LoadAsync(user.Id, cancellationToken))!;

    public static Task<VideoPage> Videos(
        [Parent] User user,
        string? cursor,
        int? limit,
        [Service] VideosClient videos,
        CancellationToken cancellationToken) =>
        videos.ListUserVideosAsync(user.Id, cursor, Args.Limit(limit, 12), cancellationToken);
}

[ExtendObjectType(typeof(Video))]
public static class VideoFields
{
    public static async Task<User?> Owner([Parent] Video video, UserByIdDataLoader loader, CancellationToken cancellationToken) =>
        (await loader.LoadAsync(video.OwnerId, cancellationToken))?.Value;

    public static async Task<LikeInfo> Likes([Parent] Video video, VideoLikesDataLoader loader, CancellationToken cancellationToken) =>
        (await loader.LoadAsync(video.Id, cancellationToken))!;

    public static Task<CommentPage> Comments(
        [Parent] Video video,
        string? cursor,
        int? limit,
        [Service] CommentsClient comments,
        CancellationToken cancellationToken) =>
        comments.ListAsync(video.Id, cursor, Args.Limit(limit, 20), cancellationToken);

    /// <summary>Null while the description is not visible to the caller (the analysis service answers 404).</summary>
    public static Task<Description?> Description([Parent] Video video, [Service] AnalysisClient analysis, CancellationToken cancellationToken) =>
        analysis.GetDescriptionAsync(video.Id, cancellationToken);
}

[ExtendObjectType(typeof(Comment))]
public static class CommentFields
{
    public static async Task<User?> Author([Parent] Comment comment, UserByIdDataLoader loader, CancellationToken cancellationToken) =>
        (await loader.LoadAsync(comment.AuthorId, cancellationToken))?.Value;

    public static async Task<LikeInfo> Likes([Parent] Comment comment, CommentLikesDataLoader loader, CancellationToken cancellationToken) =>
        (await loader.LoadAsync(comment.Id, cancellationToken))!;
}

[ExtendObjectType(typeof(Relation))]
public static class RelationFields
{
    public static async Task<User?> User([Parent] Relation relation, UserByIdDataLoader loader, CancellationToken cancellationToken) =>
        (await loader.LoadAsync(relation.UserId, cancellationToken))?.Value;
}

[ExtendObjectType(typeof(Recommendation))]
public static class RecommendationFields
{
    public static async Task<User?> User([Parent] Recommendation recommendation, UserByIdDataLoader loader, CancellationToken cancellationToken) =>
        (await loader.LoadAsync(recommendation.UserId, cancellationToken))?.Value;
}
