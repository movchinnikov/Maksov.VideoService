using Maksov.VideoService.Primitives;
namespace Maksov.VideoService.Videos.Domain;

/// <summary>Upload acceptance rules. Client-side checks are UX only; the source of truth is here.</summary>
public sealed class UploadPolicy(long maxBytes, IReadOnlyCollection<string> allowedContentTypes)
{
    public const int MaxTitleLength = 200;

    public static readonly IReadOnlyCollection<string> DefaultContentTypes =
        ["video/mp4", "video/quicktime", "video/webm", "video/x-matroska"];

    public long MaxBytes { get; } = maxBytes;

    public IReadOnlyCollection<string> AllowedContentTypes { get; } = allowedContentTypes;

    public IReadOnlyList<DomainError> Validate(string? title, string? contentType, long sizeBytes)
    {
        var errors = new List<DomainError>(3);
        var trimmed = title?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            errors.Add(DomainError.Validation("title", "Enter a title."));
        }
        else if (trimmed.Length > MaxTitleLength)
        {
            errors.Add(DomainError.Validation("title", $"The title is longer than {MaxTitleLength} characters."));
        }

        if (contentType is null || !AllowedContentTypes.Contains(contentType, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add(DomainError.Validation("contentType", "MP4, MOV, WebM and MKV are supported."));
        }

        if (sizeBytes <= 0)
        {
            errors.Add(DomainError.Validation("sizeBytes", "The file is empty."));
        }
        else if (sizeBytes > MaxBytes)
        {
            errors.Add(DomainError.Validation("sizeBytes", $"The file is larger than {MaxBytes / (1024 * 1024)} MB."));
        }

        return errors;
    }
}
