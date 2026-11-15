using Maksov.VideoService.Analysis.Worker.Adapters.Ffmpeg;
using Maksov.VideoService.Analysis.Worker.Domain;

namespace Maksov.VideoService.Analysis.Tests;

public sealed class DescriptionComposerTests
{
    private static VideoFacts Facts() => new(
        DurationMs: 16274, Width: 720, Height: 1280, HasAudio: true,
        FrameSamples: 65, SceneCuts: 4, AvgBrightness: 140, AvgSaturation: 70, MeanHue: 20, AvgMotion: 5,
        MeanVolumeDb: -25, SilenceRatio: 0.1);

    [Fact]
    public void Composes_readable_english_text_and_tags()
    {
        var (text, tags) = DescriptionComposer.Compose(Facts(), caption: null);

        Assert.Equal(
            "Vertical video, 16 s, 720×1280. " +
            "4 scene cuts, on average a shot lasts 3.3 s — fast-paced editing. " +
            "The picture is bright, with saturated colors, warm tones predominate. " +
            "Moderate motion in frame. " +
            "Has sound: medium volume, noticeable pauses (about 10% of the time).",
            text);
        Assert.Equal(["vertical", "fast-paced", "bright", "warm tones", "with sound"], tags);
    }

    [Fact]
    public void Single_shot_without_audio()
    {
        var (text, tags) = DescriptionComposer.Compose(Facts() with { SceneCuts = 0, HasAudio = false, MeanVolumeDb = null }, null);

        Assert.Contains("Single shot with no editing cuts.", text, StringComparison.Ordinal);
        Assert.EndsWith("No sound.", text, StringComparison.Ordinal);
        Assert.Contains("no sound", tags);
        Assert.Contains("single shot", tags);
    }

    [Fact]
    public void Horizontal_and_square_orientation()
    {
        Assert.StartsWith("Horizontal video", DescriptionComposer.Compose(Facts() with { Width = 1280, Height = 720 }, null).Text, StringComparison.Ordinal);
        Assert.StartsWith("Square video", DescriptionComposer.Compose(Facts() with { Width = 720, Height = 720 }, null).Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Caption_from_vision_model_is_appended()
    {
        var (text, _) = DescriptionComposer.Compose(Facts(), "  A person walks along the embankment.  ");

        Assert.EndsWith("In frames: A person walks along the embankment.", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_measurements_are_skipped_not_invented()
    {
        var facts = Facts() with { FrameSamples = 0, SceneCuts = 0, AvgBrightness = null, AvgSaturation = null, AvgMotion = null };

        var (text, _) = DescriptionComposer.Compose(facts, null);

        Assert.DoesNotContain("scene cut", text, StringComparison.Ordinal);
        Assert.DoesNotContain("The picture", text, StringComparison.Ordinal);
        Assert.DoesNotContain("motion", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1, "scene cut")]
    [InlineData(2, "scene cuts")]
    [InlineData(5, "scene cuts")]
    [InlineData(11, "scene cuts")]
    [InlineData(21, "scene cuts")]
    public void Plural_follows_english_rules(int n, string expected) =>
        Assert.Equal(expected, DescriptionComposer.Plural(n, "scene cut", "scene cuts"));

    [Theory]
    [InlineData(6000, "6 s")]
    [InlineData(75000, "1 min 15 s")]
    [InlineData(120000, "2 min")]
    public void Duration_is_formatted_for_humans(int ms, string expected) =>
        Assert.Equal(expected, DescriptionComposer.FormatDuration(ms));
}

public sealed class StatsParsersTests
{
    [Fact]
    public void Frame_stats_count_cuts_and_average_metrics()
    {
        var acc = new FrameStatsAccumulator();
        string[] lines =
        [
            "frame:0    pts:0       pts_time:0", "lavfi.scene_score=0.000000", "lavfi.signalstats.YAVG=100.0", "lavfi.signalstats.SATAVG=40.0", "lavfi.signalstats.HUEMED=350", "lavfi.signalstats.YDIF=0.0",
            "frame:1    pts:1       pts_time:0.25", "lavfi.scene_score=0.010000", "lavfi.signalstats.YAVG=110.0", "lavfi.signalstats.SATAVG=50.0", "lavfi.signalstats.HUEMED=10", "lavfi.signalstats.YDIF=2.0",
            "frame:2    pts:2       pts_time:0.5", "lavfi.scene_score=0.650000", "lavfi.signalstats.YAVG=120.0", "lavfi.signalstats.SATAVG=60.0", "lavfi.signalstats.HUEMED=10", "lavfi.signalstats.YDIF=4.0",
        ];

        foreach (var line in lines)
        {
            acc.Feed(line);
        }
        var stats = acc.Build();

        Assert.Equal(3, stats.Frames);
        Assert.Equal(1, stats.SceneCuts);
        Assert.Equal(110, stats.Brightness!.Value, 3);
        Assert.Equal(50, stats.Saturation!.Value, 3);
        Assert.Equal(3, stats.Motion!.Value, 3);
        // Hues 350°, 10°, 10° average to red (about 3°), not the arithmetic mean of 123°.
        Assert.InRange(stats.MeanHue!.Value, 0, 10);
    }

    [Fact]
    public void First_frame_is_never_a_cut()
    {
        var acc = new FrameStatsAccumulator();
        acc.Feed("frame:0    pts:0       pts_time:0");
        acc.Feed("lavfi.scene_score=0.900000");

        Assert.Equal(0, acc.Build().SceneCuts);
    }

    [Fact]
    public void Empty_input_yields_unknowns()
    {
        var stats = new FrameStatsAccumulator().Build();

        Assert.Equal(0, stats.Frames);
        Assert.Null(stats.Brightness);
        Assert.Null(stats.MeanHue);
    }

    [Fact]
    public void Audio_parser_sums_silences_and_reads_mean_volume()
    {
        var parser = new AudioStatsParser();
        string[] lines =
        [
            "[silencedetect @ 0x1] silence_start: 3",
            "[silencedetect @ 0x1] silence_end: 5.5 | silence_duration: 2.5",
            "[silencedetect @ 0x1] silence_end: 9 | silence_duration: 1.25",
            "[Parsed_volumedetect_0 @ 0x2] mean_volume: -23.4 dB",
            "[Parsed_volumedetect_0 @ 0x2] max_volume: -3.0 dB",
        ];

        foreach (var line in lines)
        {
            parser.Feed(line);
        }
        var stats = parser.Build();

        Assert.Equal(3.75, stats.SilenceSeconds, 3);
        Assert.Equal(-23.4, stats.MeanVolumeDb!.Value, 3);
    }
}
