using System.Collections.Concurrent;
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

namespace Maksov.VideoService.Gateway.GraphQL.Tests;

public sealed record StubReply(int Status, object? Body = null);

public sealed record StubCall(string Method, string Path, string Query, string Authorization);

/// <summary>Real HTTP stub of a downstream service: records calls and answers from registered handlers (404 otherwise).</summary>
public sealed class StubService : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<(string Method, string Path), Func<HttpRequest, StubReply>> _handlers = new();

    private StubService(WebApplication app, string address)
    {
        _app = app;
        Address = address;
    }

    public string Address { get; }

    public ConcurrentQueue<StubCall> Calls { get; } = new();

    public static async Task<StubService> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        StubService? self = null;
        app.Run(async ctx =>
        {
            var request = ctx.Request;
            self!.Calls.Enqueue(new StubCall(request.Method, request.Path.Value ?? string.Empty, request.QueryString.Value ?? string.Empty, request.Headers.Authorization.ToString()));
            if (!self!._handlers.TryGetValue((request.Method, request.Path.Value ?? string.Empty), out var handler))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            var reply = handler(request);
            ctx.Response.StatusCode = reply.Status;
            if (reply.Body is not null)
            {
                await ctx.Response.WriteAsJsonAsync(reply.Body, reply.Body.GetType(), cancellationToken: ctx.RequestAborted);
            }
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        self = new StubService(app, address.TrimEnd('/') + "/");
        return self;
    }

    public void On(string method, string path, Func<HttpRequest, StubReply> handler) => _handlers[(method, path)] = handler;

    public void On(string method, string path, StubReply reply) => On(method, path, _ => reply);

    public int CountOf(string method, string path) => Calls.Count(c => c.Method == method && c.Path == path);

    public void Reset() => Calls.Clear();

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

public class GraphQLFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly bool _allowIntrospection;

    public GraphQLFactory()
        : this(false)
    {
    }

    protected GraphQLFactory(bool allowIntrospection) => _allowIntrospection = allowIntrospection;

    public StubService Users { get; private set; } = null!;
    public StubService Videos { get; private set; } = null!;
    public StubService Friends { get; private set; } = null!;
    public StubService Feed { get; private set; } = null!;
    public StubService Comments { get; private set; } = null!;
    public StubService Likes { get; private set; } = null!;
    public StubService Analysis { get; private set; } = null!;

    public IReadOnlyList<StubService> All => [Users, Videos, Friends, Feed, Comments, Likes, Analysis];

    public async ValueTask InitializeAsync()
    {
        Users = await StubService.StartAsync();
        Videos = await StubService.StartAsync();
        Friends = await StubService.StartAsync();
        Feed = await StubService.StartAsync();
        Comments = await StubService.StartAsync();
        Likes = await StubService.StartAsync();
        Analysis = await StubService.StartAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        foreach (var stub in All)
        {
            await stub.DisposeAsync();
        }

        await base.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["GraphQL:AllowIntrospection"] = _allowIntrospection ? "true" : "false",
            ["GraphQL:Services:users"] = Users.Address,
            ["GraphQL:Services:videos"] = Videos.Address,
            ["GraphQL:Services:friends"] = Friends.Address,
            ["GraphQL:Services:feed"] = Feed.Address,
            ["GraphQL:Services:comments"] = Comments.Address,
            ["GraphQL:Services:likes"] = Likes.Address,
            ["GraphQL:Services:analysis"] = Analysis.Address,
        }));
        builder.ConfigureServices(services => services.AddTestAuthentication());
    }

    /// <summary>A signed-in caller; the bearer token is what services must receive back.</summary>
    public HttpClient ClientFor(string userId, string token = "test-token")
    {
        var client = CreateClient().ClientFor(userId);
        client.DefaultRequestHeaders.Add("Authorization", "Bearer " + token);
        return client;
    }

    public void ResetStubs()
    {
        foreach (var stub in All)
        {
            stub.Reset();
        }
    }

    public static async Task<JsonElement> ExecuteAsync(HttpClient client, string query, CancellationToken cancellationToken)
    {
        using var response = await client.PostAsJsonAsync("/graphql", new { query }, cancellationToken);
        return await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
    }
}

public sealed class IntrospectionFactory : GraphQLFactory
{
    public IntrospectionFactory()
        : base(true)
    {
    }
}
