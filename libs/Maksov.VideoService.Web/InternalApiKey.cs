using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Web;

public sealed class InternalApiOptions
{
    public const string SectionName = "Internal";

    /// <summary>Shared secret for service-to-service calls (from an environment variable). Empty — internal endpoints are closed.</summary>
    public string ApiKey { get; set; } = "";
}

/// <summary>
/// Protects internal endpoints (/internal/**) with a key in a header. They are not routed publicly through the gateway;
/// the key is a second line of defense in case of a routing mistake. Constant-time comparison.
/// </summary>
public static class InternalApiKey
{
    public const string Header = "X-Internal-Key";

    public static IServiceCollection AddInternalApiKey(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<InternalApiOptions>(configuration.GetSection(InternalApiOptions.SectionName));
        return services;
    }

    public static RouteHandlerBuilder RequireInternalKey(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(static async (context, next) =>
        {
            var expected = context.HttpContext.RequestServices.GetRequiredService<IOptions<InternalApiOptions>>().Value.ApiKey;
            var provided = context.HttpContext.Request.Headers[Header].ToString();
            if (string.IsNullOrEmpty(expected) || !FixedTimeEquals(expected, provided))
            {
                return Results.Unauthorized();
            }

            return await next(context).ConfigureAwait(false);
        });

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
