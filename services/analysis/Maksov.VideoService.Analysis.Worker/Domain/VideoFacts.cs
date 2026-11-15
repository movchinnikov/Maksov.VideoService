namespace Maksov.VideoService.Analysis.Worker.Domain;

public enum Orientation
{
    Vertical,
    Horizontal,
    Square,
}

/// <summary>"Readings" of the analysis tools. Optional fields are set when measuring failed (no frames or audio).</summary>
public sealed record VideoFacts(
    int DurationMs,
    int Width,
    int Height,
    bool HasAudio,
    int FrameSamples,
    int SceneCuts,
    double? AvgBrightness,
    double? AvgSaturation,
    double? MeanHue,
    double? AvgMotion,
    double? MeanVolumeDb,
    double SilenceRatio)
{
    public Orientation Orientation => Height > Width ? Orientation.Vertical : Width > Height ? Orientation.Horizontal : Orientation.Square;
}
