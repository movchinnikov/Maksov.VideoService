using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Maksov.VideoService.TestSupport;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Maksov.VideoService.Gateway.Tests;

/// <summary>Real stub HTTP server behind the gateway: replies with its own name and what it received.</summary>
public sealed class StubService : IAsyncDisposable
{
    private readonly WebApplication _app;

    private StubService(WebApplication app, string name, string address)
    {
        _app = app;
        Name = name;
        Address = address;
    }

    public string Name { get; }
    public string Address { get; }

    public static async Task<StubService> StartAsync(string name)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Run(async ctx =>
        {
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsJsonAsync(new
            {
                service = name,
                method = ctx.Request.Method,
                path = ctx.Request.Path.Value,
                authorization = ctx.Request.Headers.Authorization.ToString(),
            });
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new StubService(app, name, address.TrimEnd('/') + "/");
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public sealed class GatewayFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public StubService Videos { get; private set; } = null!;
    public StubService Users { get; private set; } = null!;
    public StubService Analysis { get; private set; } = null!;
    public StubService Comments { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        Videos = await StubService.StartAsync("videos");
        Users = await StubService.StartAsync("users");
        Analysis = await StubService.StartAsync("analysis");
        Comments = await StubService.StartAsync("comments");
    }

    public override async ValueTask DisposeAsync()
    {
        await Videos.DisposeAsync();
        await Users.DisposeAsync();
        await Analysis.DisposeAsync();
        await Comments.DisposeAsync();
        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ReverseProxy:Clusters:videos:Destinations:d1:Address"] = Videos.Address,
            ["ReverseProxy:Clusters:users:Destinations:d1:Address"] = Users.Address,
            ["ReverseProxy:Clusters:analysis:Destinations:d1:Address"] = Analysis.Address,
            ["ReverseProxy:Clusters:comments:Destinations:d1:Address"] = Comments.Address,
        }));
        builder.ConfigureServices(services => services.AddTestAuthentication());
    }

    public HttpClient ClientFor(string userId) => CreateClient().ClientFor(userId);
}

public sealed class GatewayTests : IClassFixture<GatewayFactory>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly GatewayFactory _factory;

    public GatewayTests(GatewayFactory factory) => _factory = factory;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("GET", "/api/videos")]
    [InlineData("POST", "/api/videos")]
    [InlineData("GET", "/api/videos/0190f5b4-0000-7000-8000-000000000001")]
    [InlineData("GET", "/api/videos/0190f5b4-0000-7000-8000-000000000001/description")]
    [InlineData("GET", "/api/users/me")]
    [InlineData("GET", "/api/feed")]
    [InlineData("GET", "/api/users/u1/videos")]
    [InlineData("GET", "/api/videos/0190f5b4-0000-7000-8000-000000000001/comments")]
    [InlineData("DELETE", "/api/videos/0190f5b4-0000-7000-8000-000000000001")]
    public async Task AC_gateway_01_anonymous_requests_are_rejected_before_reaching_services(string method, string path)
    {
        using var client = _factory.CreateClient();

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Ct);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("GET", "/api/videos", "videos")]
    [InlineData("POST", "/api/videos", "videos")]
    [InlineData("GET", "/api/videos/0190f5b4-0000-7000-8000-000000000001", "videos")]
    [InlineData("POST", "/api/videos/0190f5b4-0000-7000-8000-000000000001/complete", "videos")]
    [InlineData("GET", "/api/videos/0190f5b4-0000-7000-8000-000000000001/stream/manifest.mpd", "videos")]
    [InlineData("GET", "/api/videos/0190f5b4-0000-7000-8000-000000000001/description", "analysis")]
    [InlineData("GET", "/api/users/me", "users")]
    [InlineData("PUT", "/api/users/me", "users")]
    [InlineData("GET", "/api/users/u1", "users")]
    [InlineData("PUT", "/api/users/u1/follow", "users")]
    [InlineData("GET", "/api/users", "users")]
    [InlineData("GET", "/api/feed", "videos")]
    [InlineData("GET", "/api/users/u1/videos", "videos")]
    [InlineData("DELETE", "/api/videos/0190f5b4-0000-7000-8000-000000000001", "videos")]
    [InlineData("GET", "/api/videos/0190f5b4-0000-7000-8000-000000000001/comments", "comments")]
    [InlineData("POST", "/api/videos/0190f5b4-0000-7000-8000-000000000001/comments", "comments")]
    [InlineData("DELETE", "/api/videos/0190f5b4-0000-7000-8000-000000000001/comments/0190f5b4-0000-7000-8000-000000000002", "comments")]
    public async Task AC_gateway_02_routes_to_the_owning_service(string method, string path, string expectedService)
    {
        using var client = _factory.ClientFor("alice");

        using var response = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path), Ct);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedService, body.GetProperty("service").GetString());
        Assert.Equal(path, body.GetProperty("path").GetString());
        Assert.Equal(method, body.GetProperty("method").GetString());
    }

    [Theory]
    [InlineData("/internal/v1/events/video-processed")]
    [InlineData("/internal/anything")]
    [InlineData("/health/../internal/v1")]
    public async Task AC_gateway_03_internal_endpoints_are_not_published(string path)
    {
        using var client = _factory.ClientFor("alice");

        using var response = await client.PostAsync(path, null, Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task AC_gateway_04_root_and_health_are_public()
    {
        using var client = _factory.CreateClient();

        var info = await client.GetFromJsonAsync<JsonElement>("/", Json, Ct);
        using var live = await client.GetAsync("/health/live", Ct);

        Assert.Equal("Maksov.VideoService.Gateway", info.GetProperty("service").GetString());
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task AC_gateway_05_create_upload_is_rate_limited_per_user()
    {
        using var spammer = _factory.ClientFor("spammer-" + Guid.NewGuid().ToString("N"));
        using var other = _factory.ClientFor("other-" + Guid.NewGuid().ToString("N"));

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 25; i++)
        {
            using var r = await spammer.PostAsync("/api/videos", null, Ct);
            statuses.Add(r.StatusCode);
        }
        using var otherResponse = await other.PostAsync("/api/videos", null, Ct);

        Assert.Equal(20, statuses.Count(s => s == HttpStatusCode.OK));
        Assert.Equal(5, statuses.Count(s => s == HttpStatusCode.TooManyRequests));
        Assert.Equal(HttpStatusCode.OK, otherResponse.StatusCode);
    }
}
