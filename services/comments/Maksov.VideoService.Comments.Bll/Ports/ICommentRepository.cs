using System.Buffers.Binary;
using System.Buffers.Text;

namespace Maksov.VideoService.Comments.Bll.Ports;

public sealed record NewComment(Guid Id, Guid VideoId, string AuthorId, string AuthorUsername, string Text, DateTimeOffset CreatedAt);

public sealed record CommentRecord(Guid Id, Guid VideoId, string AuthorId, string AuthorUsername, string Text, DateTimeOffset CreatedAt);

/// <summary>Port for storing comments and the "known videos" replica (the video owner arrives via an event). Adapter: Postgres.</summary>
public interface ICommentRepository
{
    /// <summary>Idempotently remembers a video and its owner. Does not resurrect a deleted video.</summary>
    Task UpsertVideoAsync(Guid videoId, string ownerId, CancellationToken cancellationToken);

    /// <summary>Deletes all comments of a video and marks it deleted. A repeat is a no-op.</summary>
    Task ForgetVideoAsync(Guid videoId, string? ownerId, CancellationToken cancellationToken);

    /// <summary>Owner of a live video; unknown or deleted yields <c>null</c>.</summary>
    Task<string?> GetVideoOwnerAsync(Guid videoId, CancellationToken cancellationToken);

    /// <summary>Inserts the comment and writes comment.added.v1 (one outbox row per subscriber) in the same transaction.</summary>
    Task InsertAsync(NewComment comment, string videoOwnerId, CancellationToken cancellationToken);

    /// <summary>Video comments, newest first. Keyset pagination by (created_at, id).</summary>
    Task<IReadOnlyList<CommentRecord>> ListAsync(Guid videoId, PageCursor? after, int limit, CancellationToken cancellationToken);

    Task<int> CountAsync(Guid videoId, CancellationToken cancellationToken);

    Task<CommentRecord?> GetAsync(Guid videoId, Guid commentId, CancellationToken cancellationToken);

    /// <summary>Deletes the comment and writes comment.deleted.v1 in the same transaction. False if it did not exist (no event).</summary>
    Task<bool> DeleteAsync(Guid videoId, Guid commentId, string deletedBy, CancellationToken cancellationToken);
}

/// <summary>Keyset pagination position; passed as an opaque string in the API.</summary>
public sealed record PageCursor(DateTimeOffset CreatedAt, Guid Id)
{
    /// <summary>base64url(ticks:8 + guid:16).</summary>
    public string Encode()
    {
        Span<byte> buffer = stackalloc byte[24];
        BinaryPrimitives.WriteInt64BigEndian(buffer, CreatedAt.UtcTicks);
        Id.TryWriteBytes(buffer[8..]);
        return Base64Url.EncodeToString(buffer);
    }

    public static PageCursor? TryDecode(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 64)
        {
            return null;
        }

        Span<byte> buffer = stackalloc byte[48];
        int written;
        try
        {
            if (!Base64Url.TryDecodeFromChars(value, buffer, out written) || written != 24)
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

        return new PageCursor(new DateTimeOffset(ticks, TimeSpan.Zero), new Guid(buffer[8..24]));
    }
}
