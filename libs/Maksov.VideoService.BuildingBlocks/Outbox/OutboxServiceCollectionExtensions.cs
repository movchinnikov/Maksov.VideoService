using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Maksov.VideoService.BuildingBlocks.Outbox;

public static class OutboxServiceCollectionExtensions
{
    /// <summary>
    /// Registers the outbox: store, HTTP publisher and (only if at least one subscriber has an address) the background dispatcher.
    /// The internal key comes from <c>Internal:ApiKey</c>. Requires <c>AddPostgres</c> (NpgsqlDataSource).
    /// </summary>
    public static IServiceCollection AddOutbox(this IServiceCollection services, IConfiguration configuration, EventPathResolver pathOf)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(pathOf);

        services.Configure<OutboxOptions>(configuration.GetSection(OutboxOptions.SectionName));
        services.AddSingleton<OutboxStore>();
        services.AddHttpClient(HttpEventPublisher.ClientName, static client => client.Timeout = TimeSpan.FromSeconds(10));
        var key = configuration["Internal:ApiKey"] ?? string.Empty;
        services.AddSingleton(sp => new HttpEventPublisher(
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<OutboxOptions>>(),
            pathOf,
            key));

        var options = configuration.GetSection(OutboxOptions.SectionName).Get<OutboxOptions>() ?? new OutboxOptions();
        if (options.ConfiguredSubscribers().Count > 0)
        {
            services.AddHostedService<OutboxDispatcher>();
        }

        return services;
    }
}
