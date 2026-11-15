using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.Videos.Domain;

namespace Maksov.VideoService.Videos.Tests;

/// <summary>Lesson 1 acceptance criteria (docs/stories/lesson-01-upload.md) at the HTTP contract level.</summary>
public sealed class VideoEndpointsTests : IClassFixture<ApiFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ApiFactory _factory;

    public VideoEndpointsTests(ApiFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AC_upload_01_create_returns_presigned_put()
    {
        using var client = _factory.ClientFor("alice");

        using var response = await client.PostAsJsonAsync("/api/videos", new { title = "Sea", fileName = "sea.mp4", contentType = "video/mp4", sizeBytes = 1000 }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        Assert.Equal("PUT", body.GetProperty("uploadMethod").GetString());
        Assert.StartsWith("http://localhost:9000/media/uploads/", body.GetProperty("uploadUrl").GetString(), StringComparison.Ordinal);
        Assert.Equal("video/mp4", body.GetProperty("uploadHeaders").GetProperty("Content-Type").GetString());
    }

    [Theory]
    [InlineData("image/png", 1000)]
    [InlineData("video/mp4", 0)]
    [InlineData("video/mp4", 10_000_000)]
    public async Task AC_upload_02_invalid_request_is_rejected(string contentType, long size)
    {
        using var client = _factory.ClientFor("alice");

        using var response = await client.PostAsJsonAsync("/api/videos", new { title = "x", fileName = "x", contentType, sizeBytes = size }, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task AC_upload_03_complete_without_file_is_conflict()
    {
        using var client = _factory.ClientFor("alice");
        var id = await CreateAsync(client);

        using var response = await client.PostAsync($"/api/videos/{id}/complete", null, Ct);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AC_upload_04_complete_enqueues_transcoding_once()
    {
        using var client = _factory.ClientFor("alice");
        var id = await CreateAsync(client);
        _factory.Storage.Put(_factory.Videos.Get(id).SourceKey, "fake-video-bytes", "video/mp4");

        using var first = await client.PostAsync($"/api/videos/{id}/complete", null, Ct);
        using var second = await client.PostAsync($"/api/videos/{id}/complete", null, Ct);

        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, second.StatusCode);
        Assert.Single(_factory.Videos.EnqueuedJobs, j => j == id);
        var body = await second.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        Assert.Equal("uploaded", body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task AC_auth_01_other_users_video_is_not_found()
    {
        using var alice = _factory.ClientFor("alice");
        using var bob = _factory.ClientFor("bob");
        var id = await CreateAsync(alice);

        using var get = await bob.GetAsync($"/api/videos/{id}", Ct);
        using var complete = await bob.PostAsync($"/api/videos/{id}/complete", null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, complete.StatusCode);
    }

    [Fact]
    public async Task AC_auth_02_anonymous_is_unauthorized()
    {
        using var client = _factory.CreateClient();

        using var response = await client.GetAsync("/api/videos", Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task AC_library_01_lists_only_own_videos_newest_first_with_paging()
    {
        var user = $"carol-{Guid.NewGuid():N}";
        using var carol = _factory.ClientFor(user);
        using var dave = _factory.ClientFor($"dave-{Guid.NewGuid():N}");
        var first = await CreateAsync(carol, "First");
        var second = await CreateAsync(carol, "Second");
        var third = await CreateAsync(carol, "Third");
        await CreateAsync(dave, "Foreign");

        var page1 = await carol.GetFromJsonAsync<JsonElement>("/api/videos?limit=2", Json, Ct);
        var items1 = page1.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        var cursor = page1.GetProperty("nextCursor").GetString();
        var page2 = await carol.GetFromJsonAsync<JsonElement>($"/api/videos?limit=2&cursor={cursor}", Json, Ct);
        var items2 = page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();

        Assert.Equal([third, second], items1);
        Assert.Equal([first], items2);
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task AC_watch_01_ready_video_exposes_dash_and_hls_and_streams_manifest()
    {
        using var client = _factory.ClientFor("erin");
        var id = await CreateAsync(client);
        _factory.Videos.MarkReady(id, """[{"name":"720p","width":1280,"height":720,"kbps":2800}]""");
        _factory.Storage.Put(MediaKeys.StreamFile(id, "manifest.mpd"), "<MPD/>", "application/dash+xml");

        var video = await client.GetFromJsonAsync<JsonElement>($"/api/videos/{id}", Json, Ct);
        using var manifest = await client.GetAsync($"/api/videos/{id}/stream/manifest.mpd", Ct);

        Assert.Equal("ready", video.GetProperty("status").GetString());
        Assert.EndsWith("/stream/manifest.mpd", video.GetProperty("playback").GetProperty("dash").GetString(), StringComparison.Ordinal);
        Assert.EndsWith("/stream/master.m3u8", video.GetProperty("playback").GetProperty("hls").GetString(), StringComparison.Ordinal);
        Assert.Equal("720p", video.GetProperty("renditions")[0].GetProperty("name").GetString());
        Assert.Equal(HttpStatusCode.OK, manifest.StatusCode);
        Assert.Equal("application/dash+xml", manifest.Content.Headers.ContentType?.MediaType);
        Assert.Equal("<MPD/>", await manifest.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task AC_watch_02_stream_is_denied_before_ready_but_open_to_other_users_when_ready()
    {
        using var owner = _factory.ClientFor("frank");
        using var other = _factory.ClientFor("grace");
        var id = await CreateAsync(owner);
        _factory.Storage.Put(MediaKeys.StreamFile(id, "manifest.mpd"), "<MPD/>");

        using var beforeReady = await owner.GetAsync($"/api/videos/{id}/stream/manifest.mpd", Ct);
        using var foreignBeforeReady = await other.GetAsync($"/api/videos/{id}", Ct);
        _factory.Videos.MarkReady(id, "[]");
        using var foreign = await other.GetAsync($"/api/videos/{id}/stream/manifest.mpd", Ct);
        using var foreignVideo = await other.GetAsync($"/api/videos/{id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, beforeReady.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, foreignBeforeReady.StatusCode);
        Assert.Equal(HttpStatusCode.OK, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.OK, foreignVideo.StatusCode);
    }

    [Fact]
    public async Task AC_delete_01_only_owner_can_delete_and_media_is_removed()
    {
        using var owner = _factory.ClientFor("ivan");
        using var other = _factory.ClientFor("judy");
        var id = await CreateAsync(owner);
        _factory.Videos.MarkReady(id, "[]");
        _factory.Storage.Put(MediaKeys.StreamFile(id, "manifest.mpd"), "<MPD/>");

        using var foreign = await other.DeleteAsync($"/api/videos/{id}", Ct);
        using var own = await owner.DeleteAsync($"/api/videos/{id}", Ct);
        using var again = await owner.DeleteAsync($"/api/videos/{id}", Ct);
        using var get = await owner.GetAsync($"/api/videos/{id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, own.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, again.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, get.StatusCode);
        Assert.Contains(MediaKeys.VideoPrefix(id), _factory.Storage.DeletedPrefixes);
    }

    [Fact]
    public async Task AC_feed_01_cards_return_ready_videos_in_the_requested_order()
    {
        using var viewer = _factory.ClientFor($"vic-{Guid.NewGuid():N}");
        using var author = _factory.ClientFor($"aut-{Guid.NewGuid():N}");
        var first = await CreateAsync(author);
        var second = await CreateAsync(author);
        var draft = await CreateAsync(author);
        _factory.Videos.MarkReady(first, "[]");
        _factory.Videos.MarkReady(second, "[]");
        var unknown = Guid.NewGuid();

        var cards = await viewer.GetFromJsonAsync<JsonElement>($"/api/videos/cards?ids={second},{unknown},{draft},{first}", Json, Ct);
        using var bad = await viewer.GetAsync("/api/videos/cards?ids=not-a-guid", Ct);
        using var tooMany = await viewer.GetAsync($"/api/videos/cards?ids={string.Join(',', Enumerable.Range(0, 31).Select(_ => Guid.NewGuid()))}", Ct);

        Assert.Equal([second, first], cards.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.EndsWith("/stream/manifest.mpd", cards.GetProperty("items")[0].GetProperty("playback").GetProperty("dash").GetString(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, tooMany.StatusCode);
    }

    [Fact]
    public async Task AC_feed_02_profile_grid_lists_ready_videos_of_one_author()
    {
        var author = $"grid-{Guid.NewGuid():N}";
        using var authorClient = _factory.ClientFor(author);
        using var viewer = _factory.ClientFor($"vw-{Guid.NewGuid():N}");
        var ready = await CreateAsync(authorClient);
        await CreateAsync(authorClient);
        _factory.Videos.MarkReady(ready, "[]");

        var grid = await viewer.GetFromJsonAsync<JsonElement>($"/api/users/{author}/videos", Json, Ct);

        Assert.Equal([ready], grid.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
    }

    [Fact]
    public async Task AC_feed_03_internal_ready_by_owners_requires_the_key_and_pages_by_cursor()
    {
        var owner = $"own-{Guid.NewGuid():N}";
        using var ownerClient = _factory.ClientFor(owner);
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var id = await CreateAsync(ownerClient);
            _factory.Videos.MarkReady(id, "[]");
            ids.Add(id);
            await Task.Delay(5, Ct);
        }

        using var anonymous = _factory.CreateClient();

        HttpRequestMessage Post(object body, string? key)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, "/internal/v1/videos/ready-by-owners") { Content = JsonContent.Create(body) };
            if (key is not null)
            {
                request.Headers.Add("X-Internal-Key", key);
            }
            return request;
        }

        using var denied = await anonymous.SendAsync(Post(new { ownerIds = new[] { owner }, limit = 2 }, null), Ct);
        using var page1Response = await anonymous.SendAsync(Post(new { ownerIds = new[] { owner }, limit = 2 }, ApiFactory.InternalKey), Ct);
        var page1 = await page1Response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        var cursor = page1.GetProperty("nextCursor").GetString();
        using var page2Response = await anonymous.SendAsync(Post(new { ownerIds = new[] { owner }, limit = 2, cursor }, ApiFactory.InternalKey), Ct);
        var page2 = await page2Response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal([ids[2], ids[1]], page1.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal([ids[0]], page2.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(JsonValueKind.Null, page2.GetProperty("nextCursor").ValueKind);
    }

    [Theory]
    [InlineData("..%2F..%2Fuploads%2Fsource")]
    [InlineData("source")]
    [InlineData(".env")]
    public async Task AC_security_01_stream_rejects_unsafe_file_names(string file)
    {
        using var client = _factory.ClientFor("heidi");
        var id = await CreateAsync(client);
        _factory.Videos.MarkReady(id, "[]");

        using var response = await client.GetAsync($"/api/videos/{id}/stream/{file}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string title = "Video")
    {
        using var response = await client.PostAsJsonAsync("/api/videos", new { title, fileName = "v.mp4", contentType = "video/mp4", sizeBytes = 1000 }, Ct);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);
        return body.GetProperty("videoId").GetGuid();
    }
}

