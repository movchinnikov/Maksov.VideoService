namespace Maksov.VideoService.BuildingBlocks.Storage;

public sealed class ObjectStorageOptions
{
    public const string SectionName = "Storage";

    /// <summary>Storage address from inside the service network (for example, http://minio:9000).</summary>
    public Uri? ServiceUrl { get; set; }

    /// <summary>Storage address visible to the browser; used to sign presigned URLs (for example, http://localhost:9000).</summary>
    public Uri? PublicUrl { get; set; }

    public string AccessKey { get; set; } = "";
    public string SecretKey { get; set; } = "";
    public string Bucket { get; set; } = "media";
    public string Region { get; set; } = "us-east-1";
}

