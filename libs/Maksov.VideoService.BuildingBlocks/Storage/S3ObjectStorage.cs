using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.BuildingBlocks.Storage;

/// <summary>S3/MinIO adapter. One instance per application: clients are thread-safe and hold a connection pool.</summary>
public sealed class S3ObjectStorage : IObjectStorage, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly AmazonS3Client _presignClient;
    private readonly string _bucket;
    private readonly bool _publicIsHttps;

    public S3ObjectStorage(IOptions<ObjectStorageOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var o = options.Value;
        var credentials = new BasicAWSCredentials(o.AccessKey, o.SecretKey);
        _bucket = o.Bucket;
        _client = new AmazonS3Client(credentials, CreateConfig(o.ServiceUrl!, o.Region));
        // The signature includes Host, so presigned URLs are signed with the public address.
        // The signature is computed locally; this client makes no network calls.
        var publicUrl = o.PublicUrl ?? o.ServiceUrl!;
        _presignClient = new AmazonS3Client(credentials, CreateConfig(publicUrl, o.Region));
        _publicIsHttps = publicUrl.Scheme == Uri.UriSchemeHttps;
    }

    private static AmazonS3Config CreateConfig(Uri url, string region) => new()
    {
        ServiceURL = url.ToString(),
        ForcePathStyle = true,
        AuthenticationRegion = region,
        // MinIO and presigned URLs: checksums only when the operation requires them.
        RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
        ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
        Timeout = TimeSpan.FromSeconds(100),
        MaxErrorRetry = 3,
    };

    public async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        if (await AmazonS3Util.DoesS3BucketExistV2Async(_client, _bucket).ConfigureAwait(false))
        {
            return;
        }

        try
        {
            await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, cancellationToken).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
        {
            // A race between two initializers is fine.
        }
    }

    public Task<Uri> PresignUploadAsync(string key, string contentType, TimeSpan ttl)
    {
        var request = new GetPreSignedUrlRequest
        {
            BucketName = _bucket,
            Key = key,
            Verb = HttpVerb.PUT,
            ContentType = contentType,
            Expires = DateTime.UtcNow.Add(ttl),
            Protocol = _publicIsHttps ? Protocol.HTTPS : Protocol.HTTP,
        };
        return PresignAsync(request);
    }

    private async Task<Uri> PresignAsync(GetPreSignedUrlRequest request) =>
        new(await _presignClient.GetPreSignedURLAsync(request).ConfigureAwait(false));

    public async Task<ObjectMetadata?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var meta = await _client.GetObjectMetadataAsync(_bucket, key, cancellationToken).ConfigureAwait(false);
            return new ObjectMetadata(meta.ContentLength, meta.Headers.ContentType);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<StoredObject?> OpenReadAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.GetObjectAsync(_bucket, key, cancellationToken).ConfigureAwait(false);
            return new StoredObject(response.ResponseStream, response.ContentLength, response.Headers.ContentType, response);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task DownloadToFileAsync(string key, string filePath, CancellationToken cancellationToken)
    {
        using var response = await _client.GetObjectAsync(_bucket, key, cancellationToken).ConfigureAwait(false);
        var file = new FileStream(filePath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 81920, useAsync: true);
        await using (file.ConfigureAwait(false))
        {
            await response.ResponseStream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task UploadFileAsync(string key, string filePath, string contentType, CancellationToken cancellationToken)
    {
        var request = new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            FilePath = filePath,
            ContentType = contentType,
        };
        await _client.PutObjectAsync(request, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        string? token = null;
        do
        {
            var list = await _client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = _bucket, Prefix = prefix, ContinuationToken = token }, cancellationToken).ConfigureAwait(false);
            if (list.S3Objects is { Count: > 0 })
            {
                var request = new DeleteObjectsRequest { BucketName = _bucket, Objects = [.. list.S3Objects.Select(static o => new KeyVersion { Key = o.Key })] };
                await _client.DeleteObjectsAsync(request, cancellationToken).ConfigureAwait(false);
            }

            token = list.IsTruncated == true ? list.NextContinuationToken : null;
        }
        while (token is not null);
    }

    public async Task DeleteAsync(string key, CancellationToken cancellationToken) =>
        await _client.DeleteObjectAsync(_bucket, key, cancellationToken).ConfigureAwait(false);

    public void Dispose()
    {
        _client.Dispose();
        _presignClient.Dispose();
    }
}

