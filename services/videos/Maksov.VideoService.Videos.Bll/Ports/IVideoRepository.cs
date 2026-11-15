using System.Buffers.Binary;
using System.Buffers.Text;
using Maksov.VideoService.Videos.Domain;

namespace Maksov.VideoService.Videos.Bll.Ports;

/// <summary>Video storage port. Adapter: Postgres (Dapper).</summary>
public interface IVideoRepository
{
    Task InsertAsync(NewVideo video, CancellationToken cancellationToken);

    /// <summary>Owner's video (any status except deleted); someone else's or nonexistent: <c>null</c>.</summary>
    Task<VideoRecord?> GetOwnedAsync(Guid id, string ownerId, CancellationToken cancellationToken);

    /// <summary>A video the viewer may see: their own (any status) or someone else's ready one. Otherwise: <c>null</c>.</summary>
    Task<VideoRecord?> GetVisibleAsync(Guid id, string viewerId, CancellationToken cancellationToken);

    /// <summary>Own videos, all statuses except deleted. Keyset pagination by (created_at, id) descending.</summary>
    Task<IReadOnlyList<VideoRecord>> ListOwnedAsync(string ownerId, PageCursor? after, int limit, CancellationToken cancellationToken);

    /// <summary>Ready videos of the listed authors (feed, profile). Keyset pagination descending.</summary>
    Task<IReadOnlyList<VideoRecord>> ListReadyByOwnersAsync(IReadOnlyCollection<string> ownerIds, PageCursor? after, int limit, CancellationToken cancellationToken);

    /// <summary>
    /// In one transaction: awaiting_upload → uploaded, enqueue the transcode job, NOTIFY the worker.
    /// A repeated call is a no-op (returns false).
    /// </summary>
    Task<bool> MarkUploadedAndEnqueueAsync(Guid id, long sizeBytes, string? traceParent, CancellationToken cancellationToken);

    Task MarkFailedAsync(Guid id, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Deletes the owner's video: status deleted, cancels transcode jobs and writes the <c>video.deleted.v1</c> event to the outbox
    /// (in one transaction). <c>false</c> means the video does not exist or belongs to someone else.
    /// </summary>
    Task<bool> DeleteOwnedAsync(Guid id, string ownerId, CancellationToken cancellationToken);
}

/// <summary>Whom the user follows: IDs of the authors they are subscribed to (from the users service).</summary>
public interface IFollowingProvider
{
    Task<IReadOnlyList<string>> GetFollowingIdsAsync(string userId, CancellationToken cancellationToken);
}

public sealed record NewVideo(
    Guid Id,
    string OwnerId,
    string Title,
    string SourceKey,
    string ContentType,
    long DeclaredSizeBytes,
    DateTimeOffset CreatedAt,
    string OwnerUsername = "");

public sealed record VideoRecord(
    Guid Id,
    string OwnerId,
    string Title,
    VideoStatus Status,
    string SourceKey,
    string ContentType,
    long? SizeBytes,
    int? DurationMs,
    int? Width,
    int? Height,
    string? RenditionsJson,
    string? FailureReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string OwnerUsername = "");

/// <summary>Position in keyset pagination; passed through the API as an opaque string.</summary>
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
        if (!Base64Url.TryDecodeFromChars(value, buffer, out var written) || written != 24)
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
