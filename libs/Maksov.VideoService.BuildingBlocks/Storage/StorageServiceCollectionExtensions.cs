using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Maksov.VideoService.BuildingBlocks.Storage;

public static class StorageServiceCollectionExtensions
{
    public static IServiceCollection AddS3ObjectStorage(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<ObjectStorageOptions>()
            .Bind(configuration.GetSection(ObjectStorageOptions.SectionName))
            .Validate(o => o.ServiceUrl is not null, "Storage:ServiceUrl is required")
            .Validate(o => !string.IsNullOrWhiteSpace(o.AccessKey) && !string.IsNullOrWhiteSpace(o.SecretKey), "Storage credentials are required")
            .Validate(o => !string.IsNullOrWhiteSpace(o.Bucket), "Storage:Bucket is required")
            .ValidateOnStart();

        services.AddSingleton<IObjectStorage, S3ObjectStorage>();
        return services;
    }
}

