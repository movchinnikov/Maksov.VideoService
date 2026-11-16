using HotChocolate;

namespace Maksov.VideoService.Gateway.GraphQL.Schema;

// Shapes returned by the services (camelCase JSON) double as GraphQL object types.
// Foreign keys the services return (owner, author, user ids) are hidden and resolved to objects through DataLoaders.

public sealed record User(string Id, string Username, string DisplayName, string Bio);

public sealed record Playback(string Dash, string Hls, string Poster);

public sealed record Video(
    Guid Id,
    string Title,
    [property: GraphQLIgnore] string OwnerId,
    [property: GraphQLIgnore] string OwnerUsername,
    DateTimeOffset CreatedAt,
    int DurationMs,
    int Width,
    int Height,
    Playback Playback);

public sealed record VideoPage(IReadOnlyList<Video> Items, string? NextCursor);

public sealed record Comment(
    Guid Id,
    Guid VideoId,
    [property: GraphQLIgnore] string AuthorId,
    [property: GraphQLIgnore] string AuthorUsername,
    string Text,
    DateTimeOffset CreatedAt,
    bool CanDelete);

public sealed record CommentPage(IReadOnlyList<Comment> Items, string? NextCursor, int Total);

public sealed record LikeInfo(int Count, bool LikedByMe);

public sealed record FriendStats(int FollowersCount, int FollowingCount, bool IsFollowing, bool FollowsYou, bool IsMutual);

public sealed record Relation(
    [property: GraphQLIgnore] string UserId,
    DateTimeOffset FollowedAt,
    bool IsFollowing,
    bool FollowsYou,
    bool IsMutual);

public sealed record RelationPage(IReadOnlyList<Relation> Items, string? NextCursor, int Total);

public sealed record Recommendation([property: GraphQLIgnore] string UserId, int MutualFriends, double Similarity);

public sealed record Description(Guid VideoId, string Status, string? Text, IReadOnlyList<string> Tags, DateTimeOffset UpdatedAt);

// Wire-only shapes (not exposed in the schema).
public sealed record UserList(IReadOnlyList<User> Items);

public sealed record VideoCards(IReadOnlyList<Video> Items);

public sealed record FeedIds(IReadOnlyList<Guid> Ids, string? NextCursor);

public sealed record LikeItem(Guid Id, int Count, bool LikedByMe);

public sealed record LikeItems(IReadOnlyList<LikeItem> Items);

public sealed record RecommendationList(IReadOnlyList<Recommendation> Items);

public sealed record AddCommentBody(string Text);

/// <summary>Holder that lets a DataLoader return "not found" without nulls.</summary>
public sealed class Maybe<T>
    where T : class
{
    public static readonly Maybe<T> None = new(null);

    public Maybe(T? value) => Value = value;

    public T? Value { get; }
}
