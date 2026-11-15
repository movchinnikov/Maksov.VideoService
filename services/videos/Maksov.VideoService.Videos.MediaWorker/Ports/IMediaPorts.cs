using Maksov.VideoService.Videos.Domain;
using Maksov.VideoService.Videos.MediaWorker.Domain;

namespace Maksov.VideoService.Videos.MediaWorker.Ports;

public interface IMediaProbe
{
    /// <summary>File analysis. Throws <see cref="MediaRejectedException"/>, if the file is not media or is corrupt.</summary>
    Task<MediaInfo> ProbeAsync(string filePath, CancellationToken cancellationToken);
}

public interface ITranscoder
{
    /// <summary>CMAF ABR ladder: shared fMP4 segments + manifest.mpd (DASH) + master.m3u8 (HLS).</summary>
    Task TranscodeAsync(TranscodeRequest request, IProgress<double>? progress, CancellationToken cancellationToken);

    Task CapturePosterAsync(string sourcePath, string posterPath, double atSeconds, CancellationToken cancellationToken);
}

public sealed record TranscodeRequest(
    string SourcePath,
    string OutputDirectory,
    IReadOnlyList<Rendition> Ladder,
    bool SourceHasSar,
    bool HasAudio,
    int AudioKbps,
    double DurationSeconds,
    bool CapFrameRate,
    int SegmentSeconds,
    int Threads,
    string Preset);

