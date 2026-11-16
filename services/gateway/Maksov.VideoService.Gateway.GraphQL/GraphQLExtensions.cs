using HotChocolate.Types;
using Maksov.VideoService.Gateway.GraphQL.Clients;
using Maksov.VideoService.Gateway.GraphQL.Schema;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Maksov.VideoService.Gateway.GraphQL;

public static class GraphQLExtensions
{
    public const string Path = "/graphql";

    /// <summary>Highest nesting of a query (feed { owner { videos { owner ... } } } style cycles are cut here).</summary>
    public const int MaxExecutionDepth = 8;

    /// <summary>Requests are small documents; larger bodies are rejected before parsing.</summary>
    public const int MaxRequestBytes = 64 * 1024;

    /// <summary>
    /// Registers the schema and the typed clients. Base addresses come from <c>GraphQL:Services:&lt;name&gt;</c>
    /// (default <c>http://&lt;name&gt;:8080</c>); introspection is on only when <c>GraphQL:AllowIntrospection</c> is true.
    /// </summary>
    public static IServiceCollection AddGatewayGraphQL(this IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddTransient<ForwardAuthorizationHandler>();
        AddClient<UsersClient>(services, "users");
        AddClient<VideosClient>(services, "videos");
        AddClient<FriendsClient>(services, "friends");
        AddClient<FeedClient>(services, "feed");
        AddClient<CommentsClient>(services, "comments");
        AddClient<LikesClient>(services, "likes");
        AddClient<AnalysisClient>(services, "analysis");

        services
            .AddGraphQLServer()
            .AddQueryType(static d => d.Name(OperationTypeNames.Query))
            .AddMutationType(static d => d.Name(OperationTypeNames.Mutation))
            .AddTypeExtension(typeof(QueryResolvers))
            .AddTypeExtension(typeof(MutationResolvers))
            .AddTypeExtension(typeof(UserFields))
            .AddTypeExtension(typeof(VideoFields))
            .AddTypeExtension(typeof(CommentFields))
            .AddTypeExtension(typeof(RelationFields))
            .AddTypeExtension(typeof(RecommendationFields))
            .AddDataLoader<UserByIdDataLoader>()
            .AddDataLoader<VideoCardByIdDataLoader>()
            .AddDataLoader<VideoLikesDataLoader>()
            .AddDataLoader<CommentLikesDataLoader>()
            .AddDataLoader<FriendStatsDataLoader>()
            .AddErrorFilter<DownstreamErrorFilter>()
            .AddMaxExecutionDepthRule(MaxExecutionDepth)
            .DisableIntrospection(static (sp, _) => !sp.GetRequiredService<IConfiguration>().GetValue<bool>("GraphQL:AllowIntrospection"))
            .ModifyRequestOptions(static o => o.ExecutionTimeout = TimeSpan.FromSeconds(20));
        return services;
    }

    /// <summary>Maps <c>POST /graphql</c> (no IDE page) behind the given authorization policy and rate limiter policy.</summary>
    public static IEndpointConventionBuilder MapGatewayGraphQL(this IEndpointRouteBuilder endpoints, string authorizationPolicy, string rateLimiterPolicy) =>
        endpoints.MapGraphQLHttp(Path)
            .RequireAuthorization(authorizationPolicy)
            .RequireRateLimiting(rateLimiterPolicy);

    /// <summary>Caps the request body of the GraphQL endpoint.</summary>
    public static IApplicationBuilder UseGraphQLRequestLimits(this IApplicationBuilder app) =>
        app.Use(static (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase))
            {
                var feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
                if (feature is { IsReadOnly: false })
                {
                    feature.MaxRequestBodySize = MaxRequestBytes;
                }
            }

            return next(context);
        });

    // Addresses are read when a client is created, so configuration overrides applied late (tests, env vars) are honored.
    private static void AddClient<TClient>(IServiceCollection services, string name)
        where TClient : class
    {
        services.AddHttpClient<TClient>((sp, client) =>
            {
                var address = sp.GetRequiredService<IConfiguration>()[$"GraphQL:Services:{name}"] ?? $"http://{name}:8080";
                client.BaseAddress = new Uri(address.TrimEnd('/') + "/");
                client.Timeout = TimeSpan.FromSeconds(10);
            })
            .AddHttpMessageHandler<ForwardAuthorizationHandler>();
    }
}

/// <summary>Services behind the gateway validate the token themselves (zero trust): the caller's header is forwarded as is.</summary>
public sealed class ForwardAuthorizationHandler(IHttpContextAccessor accessor) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var authorization = accessor.HttpContext?.Request.Headers.Authorization.ToString();
        if (!string.IsNullOrEmpty(authorization))
        {
            request.Headers.TryAddWithoutValidation("Authorization", authorization);
        }

        return base.SendAsync(request, cancellationToken);
    }
}
