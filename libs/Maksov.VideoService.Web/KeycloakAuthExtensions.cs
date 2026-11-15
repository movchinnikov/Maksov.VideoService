using System.ComponentModel.DataAnnotations;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;

namespace Maksov.VideoService.Web;

public sealed class KeycloakOptions
{
    public const string SectionName = "Auth";

    /// <summary>Public issuer as the browser sees it: http://localhost:8080/realms/videoservice.</summary>
    [Required]
    public string Issuer { get; set; } = "";

    /// <summary>Discovery document address from inside the service network (http://keycloak:8080/realms/videoservice/.well-known/openid-configuration).</summary>
    public string? MetadataAddress { get; set; }

    /// <summary>API audience (audience mapper in Keycloak).</summary>
    [Required]
    public string Audience { get; set; } = "videoservice-api";

    public bool RequireHttpsMetadata { get; set; }
}

public static class KeycloakAuthExtensions
{
    public static WebApplicationBuilder AddKeycloakJwtAuthentication(this WebApplicationBuilder builder)
    {
        var options = builder.Configuration.GetSection(KeycloakOptions.SectionName).Get<KeycloakOptions>() ?? new KeycloakOptions();
        builder.Services.AddOptions<KeycloakOptions>()
            .Bind(builder.Configuration.GetSection(KeycloakOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        builder.Services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                jwt.Authority = options.Issuer;
                if (!string.IsNullOrWhiteSpace(options.MetadataAddress))
                {
                    jwt.MetadataAddress = options.MetadataAddress;
                }
                jwt.RequireHttpsMetadata = options.RequireHttpsMetadata;
                jwt.MapInboundClaims = false; // keep the original names: sub, preferred_username, realm_access
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuer = options.Issuer,
                    ValidAudience = options.Audience,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = JwtRegisteredClaimNames.PreferredUsername,
                };
            });

        builder.Services.AddAuthorization();
        return builder;
    }
}

