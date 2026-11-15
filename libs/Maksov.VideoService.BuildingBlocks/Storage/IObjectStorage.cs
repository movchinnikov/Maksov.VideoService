namespace Maksov.VideoService.BuildingBlocks.Storage;

/// <summary>Object storage port. The default implementation is S3-compatible storage (MinIO).</summary>
public interface IObjectStorage
{
    /// <summary>Creates the bucket if it does not exist. Idempotent.</summary>
    Task EnsureBucketAsync(CancellationToken cancellationToken);

    /// <summary>Signed link for uploading a single object via PUT with a fixed Content-Type.</summary>
    Task<Uri> PresignUploadAsync(string key, string contentType, TimeSpan ttl);

    /// <summary>Object metadata, or <c>null</c> if the object does not exist.</summary>
    Task<ObjectMetadata?> HeadAsync(string key, CancellationToken cancellationToken);

    /// <summary>Opens the object for reading as a stream (unbuffered), or returns <c>null</c> if it does not exist.</summary>
    Task<StoredObject?> OpenReadAsync(string key, CancellationToken cancellationToken);

    Task DownloadToFileAsync(string key, string filePath, CancellationToken cancellationToken);

    Task UploadFileAsync(string key, string filePath, string contentType, CancellationToken cancellationToken);

    Task DeleteAsync(string key, CancellationToken cancellationToken);

    /// <summary>Deletes all objects with the prefix (for example, <c>videos/{id}/</c>). Idempotent.</summary>
    Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken);
}

public sealed record ObjectMetadata(long Length, string? ContentType);

/// <summary>Object stream. Releases the storage network response on Dispose.</summary>
public sealed class StoredObject(Stream content, long length, string? contentType, IDisposable owner) : IAsyncDisposable
{
    public Stream Content { get; } = content;
    public long Length { get; } = length;
    public string? ContentType { get; } = contentType;

    public async ValueTask DisposeAsync()
    {
        await Content.DisposeAsync().ConfigureAwait(false);
        owner.Dispose();
    }
}

