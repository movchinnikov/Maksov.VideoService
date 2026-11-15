using Maksov.VideoService.BuildingBlocks.Processes;
using Maksov.VideoService.BuildingBlocks.Storage;
using Maksov.VideoService.Videos.Domain;
using Maksov.VideoService.Videos.MediaWorker.Adapters.Ffmpeg;
using Maksov.VideoService.Videos.MediaWorker.Domain;
using Maksov.VideoService.Videos.MediaWorker.Ports;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Videos.MediaWorker.Tests;

/// <summary>
/// Integration with real ffmpeg on reference clips generated via lavfi.
/// Skipped if ffmpeg is not installed (make test runs them in a container with ffmpeg).
/// </summary>
public sealed class FfmpegIntegrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("vs-ffmpeg-").FullName;
    private readonly IOptions<TranscodeOptions> _options = Options.Create(new TranscodeOptions { Threads = 2 });

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Theory]
    [InlineData("landscape", "testsrc2=size=640x360:rate=30", true, 0, 640, 360)]
    [InlineData("portrait", "testsrc2=size=360x640:rate=30", true, 0, 360, 640)]
    [InlineData("no-audio", "testsrc2=size=640x360:rate=25", false, 0, 640, 360)]
    [InlineData("rotated", "testsrc2=size=640x360:rate=30", true, 90, 360, 640)]
    // Video without SAR (typical for phones): scale cannot preserve proportions via SAR, and when rounding
    // to even sizes (240x426, 360x640 …) the rungs' aspect ratios diverge — DASH does not accept that.
    [InlineData("phone-nosar-720x1280", "testsrc2=size=720x1280:rate=30,setsar=0", true, 0, 720, 1280)]
    [InlineData("nosar-landscape", "testsrc2=size=1280x720:rate=30,setsar=0", true, 0, 1280, 720)]
    [InlineData("odd-aspect-nosar", "testsrc2=size=1278x718:rate=30,setsar=0", true, 0, 1278, 718)]
    public async Task Produces_playable_dash_and_hls(string name, string videoSource, bool withAudio, int rotate, int expectedW, int expectedH)
    {
        await SkipIfNoFfmpegAsync();
        var source = await GenerateAsync(name, videoSource, withAudio, rotate);

        var media = await new FfprobeMediaProbe(_options).ProbeAsync(source, Ct);
        MediaPolicy.EnsureAcceptable(media, _options.Value);
        Assert.Equal((expectedW, expectedH), (media.Width, media.Height));
        Assert.Equal(withAudio, media.HasAudio);

        var outDir = Directory.CreateDirectory(Path.Combine(_dir, name + "-out")).FullName;
        var ladder = RenditionLadder.Plan(media.Width, media.Height, media.FrameRate);
        var transcoder = new FfmpegTranscoder(_options);
        await transcoder.TranscodeAsync(
            new TranscodeRequest(source, outDir, ladder, media.HasSampleAspectRatio, media.HasAudio, 96, media.DurationSeconds, false, 2, 2, "ultrafast"), null, Ct);
        await transcoder.CapturePosterAsync(source, Path.Combine(outDir, "poster.jpg"), 0.5, Ct);

        var mpd = await File.ReadAllTextAsync(Path.Combine(outDir, "manifest.mpd"), Ct);
        var master = await File.ReadAllTextAsync(Path.Combine(outDir, "master.m3u8"), Ct);
        Assert.Contains("<MPD", mpd, StringComparison.Ordinal);
        Assert.Equal(ladder.Count, CountOccurrences(master, "#EXT-X-STREAM-INF"));
        Assert.Equal(withAudio, master.Contains("TYPE=AUDIO", StringComparison.Ordinal));
        Assert.NotEmpty(Directory.GetFiles(outDir, "*.m4s"));
        Assert.All(Directory.GetFiles(outDir), f => Assert.True(MediaKeys.IsValidStreamFile(Path.GetFileName(f)), f));
    }

    [Fact]
    public async Task Truncated_file_is_rejected()
    {
        await SkipIfNoFfmpegAsync();
        var source = await GenerateAsync("broken", "testsrc2=size=320x240:rate=25", true, 0);
        var broken = Path.Combine(_dir, "broken-cut.mp4");
        var bytes = await File.ReadAllBytesAsync(source, Ct);
        await File.WriteAllBytesAsync(broken, bytes[..(bytes.Length / 10)], Ct);

        await Assert.ThrowsAsync<MediaRejectedException>(() => new FfprobeMediaProbe(_options).ProbeAsync(broken, Ct));
    }

    [Fact]
    public async Task Text_file_with_mp4_extension_is_rejected()
    {
        await SkipIfNoFfmpegAsync();
        var fake = Path.Combine(_dir, "fake.mp4");
        await File.WriteAllTextAsync(fake, "#EXTM3U\n#EXTINF:1,\nhttp://169.254.169.254/latest\n", Ct);

        await Assert.ThrowsAsync<MediaRejectedException>(async () =>
        {
            var media = await new FfprobeMediaProbe(_options).ProbeAsync(fake, Ct);
            MediaPolicy.EnsureAcceptable(media, _options.Value);
        });
    }

    private async Task<string> GenerateAsync(string name, string videoSource, bool withAudio, int rotate)
    {
        var path = Path.Combine(_dir, name + (rotate != 0 ? "-base.mp4" : ".mp4"));
        // "source,filter": the filter (e.g. setsar=0) is applied to the video when encoding.
        var sourceParts = videoSource.Split(',', 2);
        var args = new List<string> { "-hide_banner", "-loglevel", "error", "-y", "-f", "lavfi", "-i", sourceParts[0] + ":duration=3" };
        if (withAudio)
        {
            args.AddRange(["-f", "lavfi", "-i", "sine=frequency=440:duration=3"]);
        }
        if (sourceParts.Length > 1)
        {
            args.AddRange(["-vf", sourceParts[1]]);
        }
        args.AddRange(["-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p"]);
        if (withAudio)
        {
            args.AddRange(["-c:a", "aac", "-shortest"]);
        }
        args.Add(path);

        var result = await ProcessRunner.RunAsync("ffmpeg", args, TimeSpan.FromSeconds(60), null, false, Ct);
        Assert.True(result.ExitCode == 0, result.StdErrTail);
        if (rotate == 0)
        {
            return path;
        }

        // Phone-style rotation: leave pixels untouched, write displaymatrix (ffmpeg 7+) or the rotate tag (older versions).
        var rotated = Path.Combine(_dir, name + ".mp4");
        var angle = rotate.ToString(System.Globalization.CultureInfo.InvariantCulture);
        result = await ProcessRunner.RunAsync("ffmpeg",
            ["-hide_banner", "-loglevel", "error", "-y", "-display_rotation:v:0", angle, "-i", path, "-c", "copy", rotated],
            TimeSpan.FromSeconds(60), null, false, Ct);
        if (result.ExitCode != 0)
        {
            result = await ProcessRunner.RunAsync("ffmpeg",
                ["-hide_banner", "-loglevel", "error", "-y", "-i", path, "-c", "copy", "-metadata:s:v:0", $"rotate={angle}", rotated],
                TimeSpan.FromSeconds(60), null, false, Ct);
        }
        Assert.True(result.ExitCode == 0, result.StdErrTail);
        return rotated;
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

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
        {
            count++;
        }
        return count;
    }
}
