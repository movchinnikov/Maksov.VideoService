using System.Diagnostics.Metrics;

namespace Maksov.VideoService.Videos.MediaWorker;

public sealed class TranscodeOptions
{
    public const string SectionName = "Transcode";

    public string WorkDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "videoservice-work");
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>x264 threads per job. Keep ≈ the container CPU limit.</summary>
    public int Threads { get; set; } = 2;

    /// <summary>Concurrent jobs per worker. Scale with replicas, not threads.</summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    public string Preset { get; set; } = "veryfast";
    public int SegmentSeconds { get; set; } = 4;
    public int LeaseSeconds { get; set; } = 120;

    /// <summary>Fallback queue polling if NOTIFY is lost (e.g. on reconnect).</summary>
    public int PollSeconds { get; set; } = 15;

    // Protection against "video bombs" and abuse.
    public int MaxDurationSeconds { get; set; } = 600;
    public int MaxDimension { get; set; } = 4096;
    public long MaxInputBytes { get; set; } = 2L * 1024 * 1024 * 1024;
    public int MaxStreams { get; set; } = 16;

    /// <summary>Heartbeat file for the container healthcheck.</summary>
    public string HeartbeatFile { get; set; } = Path.Combine(Path.GetTempPath(), "worker-alive");
}

/// <summary>Worker metrics (exported if OpenTelemetry is enabled).</summary>
public sealed class WorkerMetrics : IDisposable
{
    public const string ServiceName = "videoservice-media-worker";

    private readonly Meter _meter = new(ServiceName);
    private readonly Counter<long> _jobs;
    private readonly Histogram<double> _duration;

    public WorkerMetrics()
    {
        _jobs = _meter.CreateCounter<long>("transcode.jobs", description: "Processed jobs by outcome");
        _duration = _meter.CreateHistogram<double>("transcode.duration", unit: "s", description: "Job processing duration");
    }

    public void JobFinished(string outcome, TimeSpan elapsed)
    {
        var tag = new KeyValuePair<string, object?>("outcome", outcome);
        _jobs.Add(1, tag);
        _duration.Record(elapsed.TotalSeconds, tag);
    }

    public void Dispose() => _meter.Dispose();
}

