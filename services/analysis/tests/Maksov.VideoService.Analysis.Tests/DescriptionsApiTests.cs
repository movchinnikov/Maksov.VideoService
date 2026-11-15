using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maksov.VideoService.Analysis.Bll.Ports;
using Maksov.VideoService.Analysis.WebApi;
using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Maksov.VideoService.Analysis.Tests;

public sealed class AnalysisFactory : WebApplicationFactory<AnalysisApiMarker>
{
    public const string InternalKey = "test-internal-key";

    public InMemoryAnalyses Analyses { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("ConnectionStrings:Postgres", "Host=unused;Database=unused");
        builder.UseSetting("Internal:ApiKey", InternalKey);
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<IAnalysisRepository>();
            services.AddSingleton<IAnalysisRepository>(Analyses);
            services.AddTestAuthentication();
        });
    }

    public HttpClient ClientFor(string userId) => CreateClient().ClientFor(userId);
}

public sealed class InMemoryAnalyses : IAnalysisRepository
{
    private readonly ConcurrentDictionary<Guid, AnalysisRecord> _items = new();

    public int EnqueueCalls { get; private set; }

    public Task<bool> EnqueueAsync(AnalysisRequest request, CancellationToken cancellationToken)
    {
        EnqueueCalls++;
        var added = _items.TryAdd(request.VideoId, new AnalysisRecord(request.VideoId, request.OwnerId, AnalysisStatus.Queued, null, [], DateTimeOffset.UtcNow));
        return Task.FromResult(added);
    }

    public Task<AnalysisRecord?> GetAsync(Guid videoId, CancellationToken cancellationToken) =>
        Task.FromResult(_items.TryGetValue(videoId, out var a) ? a : null);

    public Task<bool> DeleteAsync(Guid videoId, CancellationToken cancellationToken) =>
        Task.FromResult(_items.TryRemove(videoId, out _));

    public void Set(Guid videoId, string owner, AnalysisStatus status, string? text, params string[] tags) =>
        _items[videoId] = new AnalysisRecord(videoId, owner, status, text, tags, DateTimeOffset.UtcNow);
}

public sealed class DescriptionsApiTests : IClassFixture<AnalysisFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AnalysisFactory _factory;

    public DescriptionsApiTests(AnalysisFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static object Event(Guid videoId, string owner = "alice", int schemaVersion = 1, string? sourceKey = null) => new
    {
        eventId = Guid.CreateVersion7(),
        occurredAt = DateTimeOffset.UtcNow,
        schemaVersion,
        traceId = "00-abc-def-01",
        videoId,
        ownerId = owner,
        sourceKey = sourceKey ?? MediaKeys.Source(videoId),
        durationMs = 16274,
        width = 720,
        height = 1280,
    };

    private static HttpRequestMessage InternalPost(object body, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/events/video-processed") { Content = JsonContent.Create(body) };
        if (key is not null)
        {
            request.Headers.Add("X-Internal-Key", key);
        }
        return request;
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("wrong-key")]
    public async Task AC_analysis_01_internal_endpoint_requires_the_key(string? key)
    {
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(InternalPost(Event(Guid.CreateVersion7()), key), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AC_analysis_02_event_is_accepted_once_and_repeat_is_idempotent()
    {
        using var client = _factory.CreateClient();
        var videoId = Guid.CreateVersion7();
        var body = Event(videoId);

        using var first = await client.SendAsync(InternalPost(body, AnalysisFactory.InternalKey), Ct);
        using var second = await client.SendAsync(InternalPost(body, AnalysisFactory.InternalKey), Ct);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.True((await first.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("created").GetBoolean());
        Assert.False((await second.Content.ReadFromJsonAsync<JsonElement>(Json, Ct)).GetProperty("created").GetBoolean());
    }

    [Fact]
    public async Task AC_analysis_03_invalid_events_are_rejected()
    {
        using var client = _factory.CreateClient();
        var videoId = Guid.CreateVersion7();

        using var wrongSchema = await client.SendAsync(InternalPost(Event(videoId, schemaVersion: 2), AnalysisFactory.InternalKey), Ct);
        using var foreignKey = await client.SendAsync(InternalPost(Event(videoId, sourceKey: "uploads/other/source"), AnalysisFactory.InternalKey), Ct);
        using var traversal = await client.SendAsync(InternalPost(Event(videoId, sourceKey: "../../etc/passwd"), AnalysisFactory.InternalKey), Ct);
        using var noOwner = await client.SendAsync(InternalPost(Event(videoId, owner: ""), AnalysisFactory.InternalKey), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, wrongSchema.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, foreignKey.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, traversal.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, noOwner.StatusCode);
    }

    [Fact]
    public async Task AC_analysis_04_description_requires_authentication()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync($"/api/videos/{Guid.CreateVersion7()}/description", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AC_analysis_05_owner_sees_progress_others_see_only_done()
    {
        var videoId = Guid.CreateVersion7();
        using var owner = _factory.ClientFor("alice");
        using var stranger = _factory.ClientFor("mallory");
        using var unknown = await owner.GetAsync($"/api/videos/{videoId}/description", Ct);

        _factory.Analyses.Set(videoId, "alice", AnalysisStatus.Processing, null);
        var processing = await owner.GetFromJsonAsync<JsonElement>($"/api/videos/{videoId}/description", Json, Ct);

        _factory.Analyses.Set(videoId, "alice", AnalysisStatus.Done, "Vertical video, 16 s.", "vertical", "with sound");
        var done = await owner.GetFromJsonAsync<JsonElement>($"/api/videos/{videoId}/description", Json, Ct);
        var foreign = await stranger.GetFromJsonAsync<JsonElement>($"/api/videos/{videoId}/description", Json, Ct);

        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("processing", processing.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, processing.GetProperty("text").ValueKind);
        Assert.Equal("done", done.GetProperty("status").GetString());
        Assert.Equal("Vertical video, 16 s.", done.GetProperty("text").GetString());
        Assert.Equal(["vertical", "with sound"], done.GetProperty("tags").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("Vertical video, 16 s.", foreign.GetProperty("text").GetString());
    }

    [Fact]
    public async Task AC_analysis_07_unfinished_description_is_hidden_from_others()
    {
        var videoId = Guid.CreateVersion7();
        using var stranger = _factory.ClientFor("mallory");
        _factory.Analyses.Set(videoId, "alice", AnalysisStatus.Processing, null);

        using var processing = await stranger.GetAsync($"/api/videos/{videoId}/description", Ct);

        Assert.Equal(HttpStatusCode.NotFound, processing.StatusCode);
    }

    [Fact]
    public async Task AC_analysis_08_video_deleted_removes_analysis_and_is_idempotent()
    {
        using var client = _factory.CreateClient();
        var videoId = Guid.CreateVersion7();
        _factory.Analyses.Set(videoId, "alice", AnalysisStatus.Done, "text");
        var body = new { eventId = Guid.CreateVersion7(), occurredAt = DateTimeOffset.UtcNow, schemaVersion = 1, videoId, ownerId = "alice" };

        HttpRequestMessage Delete(string? key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/events/video-deleted") { Content = JsonContent.Create(body) };
            if (key is not null)
            {
                request.Headers.Add("X-Internal-Key", key);
            }
            return request;
        }

        using var denied = await client.SendAsync(Delete(null), Ct);
        using var first = await client.SendAsync(Delete(AnalysisFactory.InternalKey), Ct);
        using var second = await client.SendAsync(Delete(AnalysisFactory.InternalKey), Ct);
        using var owner = _factory.ClientFor("alice");
        using var gone = await owner.GetAsync($"/api/videos/{videoId}/description", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
    }

    [Fact]
    public async Task AC_analysis_06_failed_analysis_has_no_text()
    {
        var videoId = Guid.CreateVersion7();
        using var owner = _factory.ClientFor("bob");
        _factory.Analyses.Set(videoId, "bob", AnalysisStatus.Failed, "must not be shown");

        var failed = await owner.GetFromJsonAsync<JsonElement>($"/api/videos/{videoId}/description", Json, Ct);

        Assert.Equal("failed", failed.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("text").ValueKind);
    }
}
