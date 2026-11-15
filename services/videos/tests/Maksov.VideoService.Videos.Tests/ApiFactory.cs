using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.TestSupport;
using Maksov.VideoService.Videos.Bll.Ports;
using Maksov.VideoService.Videos.Domain;
using Maksov.VideoService.Videos.WebApi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Videos.Tests;

/// <summary>API with replaced adapters (ports → in-memory) and test authentication.</summary>
public sealed class ApiFactory : WebApplicationFactory<VideosApiMarker>
{
    public const string InternalKey = "test-internal-key";

    public InMemoryVideoRepository Videos { get; } = new();
    public FakeObjectStorage Storage { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", "Host=unused;Database=unused");
        builder.UseSetting("Storage:ServiceUrl", "http://minio.test:9000");
        builder.UseSetting("Storage:PublicUrl", "http://localhost:9000");
        builder.UseSetting("Storage:AccessKey", "test");
        builder.UseSetting("Storage:SecretKey", "test-secret");
        builder.UseSetting("Upload:MaxBytes", "1048576");
        builder.UseSetting("Internal:ApiKey", InternalKey);

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IVideoRepository>();
            services.AddSingleton<IVideoRepository>(Videos);
            services.RemoveAll<IObjectStorage>();
            services.AddSingleton<IObjectStorage>(Storage);

            services.AddTestAuthentication();
        });
    }

    public HttpClient ClientFor(string userId)
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHandler.UserHeader, userId);
        return client;
    }
}

public sealed class InMemoryVideoRepository : IVideoRepository
{
    private readonly ConcurrentDictionary<Guid, VideoRecord> _videos = new();
    public ConcurrentBag<Guid> EnqueuedJobs { get; } = [];

    public Task InsertAsync(NewVideo video, CancellationToken cancellationToken)
    {
        var v = video;
        _videos[v.Id] = new VideoRecord(v.Id, v.OwnerId, v.Title, VideoStatus.AwaitingUpload, v.SourceKey, v.ContentType,
            null, null, null, null, null, null, v.CreatedAt, v.CreatedAt, v.OwnerUsername);
        return Task.CompletedTask;
    }

    public Task<VideoRecord?> GetOwnedAsync(Guid id, string ownerId, CancellationToken cancellationToken) =>
        Task.FromResult(_videos.TryGetValue(id, out var v) && v.OwnerId == ownerId ? v : null);

    public Task<VideoRecord?> GetVisibleAsync(Guid id, string viewerId, CancellationToken cancellationToken) =>
        Task.FromResult(_videos.TryGetValue(id, out var v) && (v.OwnerId == viewerId || v.Status == VideoStatus.Ready) ? v : null);

    public Task<IReadOnlyList<VideoRecord>> GetReadyByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        IReadOnlyList<VideoRecord> list = _videos.Values.Where(v => v.Status == VideoStatus.Ready && ids.Contains(v.Id)).ToList();
        return Task.FromResult(list);
    }

    public Task<IReadOnlyList<VideoRecord>> ListReadyByOwnersAsync(IReadOnlyCollection<string> ownerIds, PageCursor? after, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<VideoRecord> list = _videos.Values
            .Where(v => v.Status == VideoStatus.Ready && ownerIds.Contains(v.OwnerId))
            .Where(v => after is null || v.CreatedAt < after.CreatedAt || (v.CreatedAt == after.CreatedAt && v.Id.CompareTo(after.Id) < 0))
            .OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id)
            .Take(limit)
            .ToList();
        return Task.FromResult(list);
    }

    public ConcurrentBag<Guid> Deleted { get; } = [];

    public Task<bool> DeleteOwnedAsync(Guid id, string ownerId, CancellationToken cancellationToken)
    {
        if (_videos.TryGetValue(id, out var v) && v.OwnerId == ownerId && _videos.TryRemove(id, out _))
        {
            Deleted.Add(id);
            return Task.FromResult(true);
        }
        return Task.FromResult(false);
    }

    public Task<IReadOnlyList<VideoRecord>> ListOwnedAsync(string ownerId, PageCursor? after, int limit, CancellationToken cancellationToken)
    {
        IReadOnlyList<VideoRecord> list = _videos.Values
            .Where(v => v.OwnerId == ownerId)
            .Where(v => after is null || v.CreatedAt < after.CreatedAt || (v.CreatedAt == after.CreatedAt && v.Id.CompareTo(after.Id) < 0))
            .OrderByDescending(v => v.CreatedAt).ThenByDescending(v => v.Id)
            .Take(limit)
            .ToList();
        return Task.FromResult(list);
    }

    public Task<bool> MarkUploadedAndEnqueueAsync(Guid id, long sizeBytes, string? traceParent, CancellationToken cancellationToken)
    {
        var v = _videos[id];
        if (v.Status != VideoStatus.AwaitingUpload)
        {
            return Task.FromResult(false);
        }
        _videos[id] = v with { Status = VideoStatus.Uploaded, SizeBytes = sizeBytes };
        EnqueuedJobs.Add(id);
        return Task.FromResult(true);
    }

    public Task MarkFailedAsync(Guid id, string reason, CancellationToken cancellationToken)
    {
        _videos[id] = _videos[id] with { Status = VideoStatus.Failed, FailureReason = reason };
        return Task.CompletedTask;
    }

    public void MarkReady(Guid id, string renditionsJson) =>
        _videos[id] = _videos[id] with { Status = VideoStatus.Ready, DurationMs = 3000, Width = 1280, Height = 720, RenditionsJson = renditionsJson };

    public VideoRecord Get(Guid id) => _videos[id];
}

public sealed class FakeObjectStorage : IObjectStorage
{
    public ConcurrentDictionary<string, (byte[] Data, string ContentType)> Objects { get; } = new();

    public Task EnsureBucketAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<Uri> PresignUploadAsync(string key, string contentType, TimeSpan ttl) =>
        Task.FromResult(new Uri($"http://localhost:9000/media/{key}?X-Amz-Signature=test"));

    public Task<ObjectMetadata?> HeadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Objects.TryGetValue(key, out var o) ? new ObjectMetadata(o.Data.Length, o.ContentType) : null);

    public Task<StoredObject?> OpenReadAsync(string key, CancellationToken cancellationToken) =>
        Task.FromResult(Objects.TryGetValue(key, out var o)
            ? new StoredObject(new MemoryStream(o.Data), o.Data.Length, o.ContentType, new MemoryStream())
            : null);

    public Task DownloadToFileAsync(string key, string filePath, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task UploadFileAsync(string key, string filePath, string contentType, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task DeleteAsync(string key, CancellationToken cancellationToken)
    {
        Objects.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public ConcurrentBag<string> DeletedPrefixes { get; } = [];

    public Task DeletePrefixAsync(string prefix, CancellationToken cancellationToken)
    {
        DeletedPrefixes.Add(prefix);
        foreach (var k in Objects.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)))
        {
            Objects.TryRemove(k, out _);
        }
        return Task.CompletedTask;
    }

    public void Put(string key, string content, string contentType = "application/octet-stream") =>
        Objects[key] = (Encoding.UTF8.GetBytes(content), contentType);
}
