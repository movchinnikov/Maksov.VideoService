namespace Maksov.VideoService.Videos.MediaWorker.Domain;

/// <summary>File analysis result. Dimensions account for rotation (as the viewer sees it).</summary>
public sealed record MediaInfo(
    string FormatName,
    double DurationSeconds,
    int Width,
    int Height,
    double FrameRate,
    bool HasVideo,
    bool HasAudio,
    int StreamCount,
    bool HasSampleAspectRatio = true);

/// <summary>The file is rejected for good — retrying will not help. ReasonCode is returned to the user.</summary>
public sealed class MediaRejectedException : Exception
{
    public MediaRejectedException()
    {
        ReasonCode = "invalid_media";
    }

    public MediaRejectedException(string message) : base(message)
    {
        ReasonCode = "invalid_media";
    }

    public MediaRejectedException(string message, Exception innerException) : base(message, innerException)
    {
        ReasonCode = "invalid_media";
    }

    public MediaRejectedException(string reasonCode, string message) : base(message)
    {
        ReasonCode = reasonCode;
    }

    public string ReasonCode { get; }
}

/// <summary>Media intake policy: format allowlist and limits against "video bombs".</summary>
public static class MediaPolicy
{
    private static readonly string[] AllowedFormats = ["mov", "mp4", "matroska", "webm"];

    public static void EnsureAcceptable(MediaInfo media, TranscodeOptions options)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(options);

        var formats = media.FormatName.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (!formats.Any(f => AllowedFormats.Contains(f, StringComparer.Ordinal)))
        {
            throw new MediaRejectedException("unsupported_format", $"Format '{media.FormatName}' is not allowed");
        }

        if (!media.HasVideo)
        {
            throw new MediaRejectedException("no_video_stream", "File has no video stream");
        }

        if (media.StreamCount > options.MaxStreams)
        {
            throw new MediaRejectedException("too_many_streams", $"File has {media.StreamCount} streams");
        }

        if (media.DurationSeconds <= 0)
        {
            throw new MediaRejectedException("invalid_media", "Unknown duration");
        }

        if (media.DurationSeconds > options.MaxDurationSeconds)
        {
            throw new MediaRejectedException("duration_exceeded", $"Duration {media.DurationSeconds:F0}s exceeds {options.MaxDurationSeconds}s");
        }

        if (media.Width <= 0 || media.Height <= 0 || media.Width > options.MaxDimension || media.Height > options.MaxDimension)
        {
            throw new MediaRejectedException("resolution_exceeded", $"Resolution {media.Width}x{media.Height} is not allowed");
        }
    }
}

