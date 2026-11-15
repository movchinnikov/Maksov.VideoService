using System.Globalization;
using System.Text.RegularExpressions;

namespace Maksov.VideoService.Analysis.Worker.Adapters.Ffmpeg;

/// <summary>Result of the video pass: averaged frame metrics.</summary>
internal sealed record FrameStats(int Frames, int SceneCuts, double? Brightness, double? Saturation, double? MeanHue, double? Motion);

/// <summary>
/// Parses <c>metadata=print</c> output (key=value line by line, one block per frame, starting with a "frame:" line)
/// after the <c>select='gte(scene,0)'</c> and <c>signalstats</c> filters. Accepts lines as a stream — no output accumulation.
/// </summary>
internal sealed class FrameStatsAccumulator(double sceneThreshold = 0.3)
{
    private int _frames;
    private int _cuts;
    private double _sumBrightness;
    private int _brightnessCount;
    private double _sumSaturation;
    private int _saturationCount;
    private double _sumSin;
    private double _sumCos;
    private int _hueCount;
    private double _sumMotion;
    private int _motionCount;

    public void Feed(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.StartsWith("frame:", StringComparison.Ordinal))
        {
            _frames++;
            return;
        }

        var eq = line.IndexOf('=', StringComparison.Ordinal);
        if (eq < 0 || !double.TryParse(line.AsSpan(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            return;
        }

        switch (line[..eq])
        {
            case "lavfi.scene_score":
                // The first frame has nothing to compare with.
                if (_frames > 1 && value > sceneThreshold)
                {
                    _cuts++;
                }
                break;
            case "lavfi.signalstats.YAVG":
                _sumBrightness += value;
                _brightnessCount++;
                break;
            case "lavfi.signalstats.SATAVG":
                _sumSaturation += value;
                _saturationCount++;
                break;
            case "lavfi.signalstats.HUEMED":
                _sumSin += Math.Sin(value * Math.PI / 180);
                _sumCos += Math.Cos(value * Math.PI / 180);
                _hueCount++;
                break;
            case "lavfi.signalstats.YDIF":
                if (_frames > 1)
                {
                    _sumMotion += value;
                    _motionCount++;
                }
                break;
        }
    }

    public FrameStats Build()
    {
        double? hue = null;
        if (_hueCount > 0)
        {
            // Circular mean: hues 359° and 1° average to red, not green.
            var degrees = Math.Atan2(_sumSin, _sumCos) * 180 / Math.PI;
            hue = degrees < 0 ? degrees + 360 : degrees;
        }

        return new FrameStats(
            _frames,
            _cuts,
            _brightnessCount > 0 ? _sumBrightness / _brightnessCount : null,
            _saturationCount > 0 ? _sumSaturation / _saturationCount : null,
            hue,
            _motionCount > 0 ? _sumMotion / _motionCount : null);
    }
}

internal sealed record AudioStats(double? MeanVolumeDb, double SilenceSeconds);

/// <summary>Parses ffmpeg stderr with the <c>volumedetect</c> and <c>silencedetect</c> filters.</summary>
internal sealed partial class AudioStatsParser
{
    private double _silence;
    private double? _meanVolume;

    public void Feed(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var duration = SilenceDurationRegex().Match(line);
        if (duration.Success && double.TryParse(duration.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
        {
            _silence += seconds;
            return;
        }

        var mean = MeanVolumeRegex().Match(line);
        if (mean.Success && double.TryParse(mean.Groups[1].ValueSpan, NumberStyles.Float, CultureInfo.InvariantCulture, out var db))
        {
            _meanVolume = db;
        }
    }

    public AudioStats Build() => new(_meanVolume, _silence);

    [GeneratedRegex(@"silence_duration:\s*([0-9.]+)")]
    private static partial Regex SilenceDurationRegex();

    [GeneratedRegex(@"mean_volume:\s*(-?[0-9.]+)\s*dB")]
    private static partial Regex MeanVolumeRegex();
}
