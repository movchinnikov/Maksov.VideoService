namespace Maksov.VideoService.Videos.Domain;

/// <summary>Video lifecycle. Transitions only go forward.</summary>
public enum VideoStatus
{
    AwaitingUpload,
    Uploaded,
    Processing,
    Ready,
    Failed,
}

public static class VideoStatusExtensions
{
    public static string ToWire(this VideoStatus status) => status switch
    {
        VideoStatus.AwaitingUpload => "awaiting_upload",
        VideoStatus.Uploaded => "uploaded",
        VideoStatus.Processing => "processing",
        VideoStatus.Ready => "ready",
        VideoStatus.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
    };

    public static VideoStatus ParseWire(string value) => value switch
    {
        "awaiting_upload" => VideoStatus.AwaitingUpload,
        "uploaded" => VideoStatus.Uploaded,
        "processing" => VideoStatus.Processing,
        "ready" => VideoStatus.Ready,
        "failed" => VideoStatus.Failed,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown video status"),
    };

    /// <summary>Allowed transitions. Reprocessing (Processing → Processing) is allowed after a worker is lost.</summary>
    public static bool CanTransitionTo(this VideoStatus from, VideoStatus to) => (from, to) switch
    {
        (VideoStatus.AwaitingUpload, VideoStatus.Uploaded) => true,
        (VideoStatus.AwaitingUpload, VideoStatus.Failed) => true,
        (VideoStatus.Uploaded, VideoStatus.Processing) => true,
        (VideoStatus.Uploaded, VideoStatus.Failed) => true,
        (VideoStatus.Processing, VideoStatus.Processing) => true,
        (VideoStatus.Processing, VideoStatus.Ready) => true,
        (VideoStatus.Processing, VideoStatus.Failed) => true,
        _ => false,
    };

    /// <summary>Upload completed (a repeated complete call is a no-op).</summary>
    public static bool IsUploadCompleted(this VideoStatus status) =>
        status is VideoStatus.Uploaded or VideoStatus.Processing or VideoStatus.Ready;
}

