using Maksov.VideoService.Analysis.Bll.Ports;
using Maksov.VideoService.Analysis.Worker;
using Maksov.VideoService.Analysis.Worker.Adapters.Ffmpeg;
using Maksov.VideoService.Analysis.Worker.Domain;
using Maksov.VideoService.BuildingBlocks.Processes;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Analysis.Tests;

/// <summary>
/// ffmpeg analysis tools on real ffmpeg and reference vertical videos (lavfi).
/// Skipped if ffmpeg is not installed (make test runs them in a container with ffmpeg).
/// </summary>
public sealed class FfmpegInspectorTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vs-analysis-").FullName;
    private readonly FfmpegVideoInspector _inspector = new(Options.Create(new AnalysisOptions()));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static AnalysisJob Job(int durationMs, int w, int h) => new(Guid.CreateVersion7(), "uploads/x/source", durationMs, w, h, 1, 3);

    [Fact]
    public async Task Detects_cuts_colors_and_half_silent_audio_in_vertical_clip()
    {
        await SkipIfNoFfmpegAsync();
        // 3 colored shots of 2 s each (red → blue → green) + 3 s of tone and 3 s of silence.
        var path = await GenerateAsync("cuts.mp4",
            "-f", "lavfi", "-i", "color=c=red:s=360x640:r=25:d=2",
            "-f", "lavfi", "-i", "color=c=blue:s=360x640:r=25:d=2",
            "-f", "lavfi", "-i", "color=c=green:s=360x640:r=25:d=2",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=3",
            "-f", "lavfi", "-i", "anullsrc=r=44100:cl=mono,atrim=0:3",
            "-filter_complex", "[0][1][2]concat=n=3:v=1:a=0[v];[3][4]concat=n=2:v=0:a=1[a]",
            "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest");

        var facts = await _inspector.InspectAsync(path, Job(6000, 360, 640), Ct);

        Assert.True(facts.HasAudio);
        Assert.Equal(2, facts.SceneCuts);
        Assert.InRange(facts.FrameSamples, 20, 28);
        Assert.NotNull(facts.AvgBrightness);
        Assert.NotNull(facts.AvgSaturation);
        Assert.InRange(facts.SilenceRatio, 0.35, 0.65);
        Assert.NotNull(facts.MeanVolumeDb);

        var (text, tags) = DescriptionComposer.Compose(facts, null);
        Assert.Contains("2 scene cuts", text, StringComparison.Ordinal);
        Assert.Contains("vertical", tags);
        Assert.Contains("with sound", tags);
    }

    [Fact]
    public async Task Silent_single_shot_clip_has_no_audio_and_no_cuts()
    {
        await SkipIfNoFfmpegAsync();
        var path = await GenerateAsync("single.mp4",
            "-f", "lavfi", "-i", "testsrc2=size=360x640:rate=25:duration=3",
            "-c:v", "libx264", "-pix_fmt", "yuv420p");

        var facts = await _inspector.InspectAsync(path, Job(3000, 360, 640), Ct);

        Assert.False(facts.HasAudio);
        Assert.Null(facts.MeanVolumeDb);
        Assert.Equal(0, facts.SilenceRatio);
        Assert.Equal(0, facts.SceneCuts);
        Assert.Contains("No sound.", DescriptionComposer.Compose(facts, null).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Clip_without_sar_is_inspected_like_any_other()
    {
        await SkipIfNoFfmpegAsync();
        var path = await GenerateAsync("nosar.mp4",
            "-f", "lavfi", "-i", "testsrc2=size=720x1280:rate=25:duration=2", "-vf", "setsar=0",
            "-c:v", "libx264", "-pix_fmt", "yuv420p");

        var facts = await _inspector.InspectAsync(path, Job(2000, 720, 1280), Ct);

        Assert.True(facts.FrameSamples > 0);
    }

    private async Task<string> GenerateAsync(string name, params string[] args)
    {
        var path = Path.Combine(_dir, name);
        string[] common = ["-hide_banner", "-loglevel", "error", "-y"];
        var result = await ProcessRunner.RunAsync("ffmpeg", [.. common, .. args, path], TimeSpan.FromSeconds(60), null, false, Ct);
        Assert.True(result.ExitCode == 0, result.StdErrTail);
        return path;
    }

    private static async Task SkipIfNoFfmpegAsync()
    {
        try
        {
            var r = await ProcessRunner.RunAsync("ffmpeg", ["-version"], TimeSpan.FromSeconds(10), null, false, Ct);
            if (r.ExitCode == 0)
            {
                return;
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
        Assert.Skip("ffmpeg is not installed");
    }
}
