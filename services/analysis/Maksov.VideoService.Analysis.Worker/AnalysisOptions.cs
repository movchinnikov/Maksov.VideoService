namespace Maksov.VideoService.Analysis.Worker;

public sealed class AnalysisOptions
{
    public const string SectionName = "Analysis";

    public string WorkDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "videoservice-analysis");
    public string FfmpegPath { get; set; } = "ffmpeg";
    public string FfprobePath { get; set; } = "ffprobe";

    /// <summary>Concurrent jobs per worker. Scale with replicas, not threads.</summary>
    public int MaxConcurrentJobs { get; set; } = 1;

    public int LeaseSeconds { get; set; } = 300;

    /// <summary>Fallback queue polling in case NOTIFY is lost (e.g. on reconnect).</summary>
    public int PollSeconds { get; set; } = 15;

    /// <summary>Heartbeat file for the container healthcheck.</summary>
    public string HeartbeatFile { get; set; } = Path.Combine(Path.GetTempPath(), "worker-alive");

    public CaptionerOptions Captioner { get; set; } = new();
}

/// <summary>
/// Frame captioning by a local vision model (Ollama). Disabled by default: <c>Provider = none</c>.
/// Enable with <c>Analysis__Captioner__Provider=ollama</c> and the compose profile <c>ml</c> (ollama container + model).
/// </summary>
public sealed class CaptionerOptions
{
    public string Provider { get; set; } = "none";
    public string Endpoint { get; set; } = "http://ollama:11434";
    public string Model { get; set; } = "moondream";
    public int Frames { get; set; } = 3;
    public int TimeoutSeconds { get; set; } = 120;
    public string Prompt { get; set; } = "Describe in one short sentence what is happening in the frame. No preamble.";
}
