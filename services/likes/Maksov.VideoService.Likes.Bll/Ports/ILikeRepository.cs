using System.Buffers.Binary;
using System.Buffers.Text;
using System.Text;

namespace Maksov.VideoService.Likes.Bll.Ports;

public enum LikeTargetType
{
    Video,
    Comment,
}

/// <summary>
/// A likeable thing. <see cref="VideoId"/> is the video itself or the video of the comment;
/// <see cref="OwnerId"/> is the video owner or the comment author (goes into the outbound event).
/// </summary>
public sealed record LikeTarget(LikeTargetType Type, Guid Id, Guid VideoId, string OwnerId);

public sealed record KnownComment(Guid CommentId, Guid VideoId, string AuthorId);

public sealed record LikeCount(Guid Id, int Count, bool LikedByMe);

public sealed record Liker(string UserId, DateTimeOffset LikedAt);

/// <summary>Port for likes, counters and the "known videos/comments" replicas. Adapter: Postgres.</summary>
public interface ILikeRepository
{
    /// <summary>Idempotently remembers a video and its owner. Does not resurrect a deleted video.</summary>
    Task UpsertVideoAsync(Guid videoId, string ownerId, CancellationToken cancellationToken);

    /// <summary>Removes likes of the video and of its comments, counters and known comments; marks the video deleted. A repeat is a no-op.</summary>
    Task ForgetVideoAsync(Guid videoId, string? ownerId, CancellationToken cancellationToken);

    /// <summary>Owner of a live video; unknown or deleted yields <c>null</c>.</summary>
    Task<string?> GetVideoOwnerAsync(Guid videoId, CancellationToken cancellationToken);

    /// <summary>Idempotently remembers a comment. Ignored when the video or the comment is deleted.</summary>
    Task UpsertCommentAsync(Guid commentId, Guid videoId, string authorId, CancellationToken cancellationToken);

    /// <summary>Removes likes and the counter of a comment and leaves a tombstone. A repeat is a no-op.</summary>
    Task ForgetCommentAsync(Guid commentId, Guid videoId, CancellationToken cancellationToken);

    /// <summary>A live known comment; unknown or deleted yields <c>null</c>.</summary>
    Task<KnownComment?> GetCommentAsync(Guid commentId, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a like, increments the counter and enqueues the liked event in one transaction.
    /// Returns <c>false</c> (and writes nothing) when the like already exists or the target is gone.
    /// </summary>
    Task<bool> AddLikeAsync(LikeTarget target, string userId, CancellationToken cancellationToken);

    /// <summary>Removes a like, decrements the counter and enqueues the unliked event in one transaction. Returns <c>false</c> when there was no like.</summary>
    Task<bool> RemoveLikeAsync(LikeTarget target, string userId, CancellationToken cancellationToken);

    /// <summary>Counters for the ids (unknown ids yield 0) and whether <paramref name="userId"/> liked each.</summary>
    Task<IReadOnlyList<LikeCount>> GetCountsAsync(LikeTargetType type, IReadOnlyCollection<Guid> ids, string userId, CancellationToken cancellationToken);

    /// <summary>Video likers, newest first. Keyset pagination by (liked_at, user_id).</summary>
    Task<IReadOnlyList<Liker>> ListLikersAsync(Guid videoId, LikerCursor? after, int limit, CancellationToken cancellationToken);

    Task<int> CountVideoLikesAsync(Guid videoId, CancellationToken cancellationToken);
}

/// <summary>Keyset pagination position for likers; passed as an opaque string in the API.</summary>
public sealed record LikerCursor(DateTimeOffset LikedAt, string UserId)
{
    private const int MaxEncodedLength = 600;

    /// <summary>base64url(ticks:8 + utf8 user id).</summary>
    public string Encode()
    {
        var user = Encoding.UTF8.GetBytes(UserId);
        var buffer = new byte[8 + user.Length];
        BinaryPrimitives.WriteInt64BigEndian(buffer, LikedAt.UtcTicks);
        user.CopyTo(buffer, 8);
        return Base64Url.EncodeToString(buffer);
    }

    public static LikerCursor? TryDecode(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxEncodedLength)
        {
            return null;
        }

        var buffer = new byte[Base64Url.GetMaxDecodedLength(value.Length)];
        int written;
        try
        {
            if (!Base64Url.TryDecodeFromChars(value, buffer, out written) || written <= 8)
            {
                return null;
            }
        }
        catch (FormatException)
        {
            return null;
        }

        var ticks = BinaryPrimitives.ReadInt64BigEndian(buffer);
        if (ticks < DateTimeOffset.MinValue.UtcTicks || ticks > DateTimeOffset.MaxValue.UtcTicks)
        {
            return null;
        }

        try
        {
            var user = new UTF8Encoding(false, true).GetString(buffer, 8, written - 8);
            return new LikerCursor(new DateTimeOffset(ticks, TimeSpan.Zero), user);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
