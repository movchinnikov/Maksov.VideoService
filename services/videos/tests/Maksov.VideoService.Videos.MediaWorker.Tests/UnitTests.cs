using Maksov.VideoService.Videos.Domain;
using Maksov.VideoService.Videos.MediaWorker.Adapters.Ffmpeg;
using Maksov.VideoService.Videos.MediaWorker.Domain;
using Maksov.VideoService.Videos.MediaWorker.Ports;

namespace Maksov.VideoService.Videos.MediaWorker.Tests;

public sealed class FfmpegArgsBuilderTests
{
    private static TranscodeRequest Request(bool audio = true, int rungs = 3, bool capFps = false, bool sourceHasSar = true) => new(
        "/work/source", "/work/stream", RenditionLadder.Plan(1920, 1080, 30).Take(rungs).ToList(), sourceHasSar, audio, 128, 10, capFps, 4, 2, "veryfast");

    [Fact]
    public void Produces_dash_with_hls_playlists_from_one_pass()
    {
        var args = FfmpegArgsBuilder.Transcode(Request());

        Assert.Equal("dash", args[args.ToList().IndexOf("-f") + 1]);
        Assert.Equal("1", After(args, "-hls_playlist"));
        Assert.Equal("/work/stream/manifest.mpd", args[^1]);
        Assert.Single(args, a => a == "-i");
    }

    [Fact]
    public void Maps_every_rung_and_audio_once()
    {
        var args = FfmpegArgsBuilder.Transcode(Request(rungs: 3));

        Assert.Equal(3, args.Count(a => a.StartsWith("[v", StringComparison.Ordinal)));
        Assert.Single(args, a => a == "0:a:0");
        Assert.Equal("id=0,streams=v id=1,streams=a", After(args, "-adaptation_sets"));
        Assert.Equal("400k", After(args, "-b:v:0"));
    }

    [Fact]
    public void Without_audio_has_video_only_adaptation_set()
    {
        var args = FfmpegArgsBuilder.Transcode(Request(audio: false));

        Assert.DoesNotContain("0:a:0", args);
        Assert.DoesNotContain("-c:a", args);
        Assert.Equal("id=0,streams=v", After(args, "-adaptation_sets"));
    }

    [Fact]
    public void Restricts_input_protocols_and_aligns_keyframes()
    {
        var args = FfmpegArgsBuilder.Transcode(Request());

        Assert.Equal("file", After(args, "-protocol_whitelist"));
        Assert.True(args.ToList().IndexOf("-protocol_whitelist") < args.ToList().IndexOf("-i"));
        Assert.Equal("0", After(args, "-sc_threshold"));
        Assert.Equal("expr:gte(t,n_forced*2)", After(args, "-force_key_frames"));
    }

    [Fact]
    public void Filter_graph_splits_and_scales_each_rung()
    {
        var graph = FfmpegArgsBuilder.FilterGraph(Request(rungs: 2, capFps: true));

        Assert.Equal("[0:v]fps=60,format=yuv420p,split=2[s0][s1];[s0]scale=426:240:flags=bicubic[v0];[s1]scale=640:360:flags=bicubic[v1]", graph);
    }

    [Fact]
    public void Source_without_sar_gets_square_pixels_before_split()
    {
        var graph = FfmpegArgsBuilder.FilterGraph(Request(rungs: 2, sourceHasSar: false));

        Assert.StartsWith("[0:v]setsar=1,format=yuv420p,split=2", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Source_with_sar_keeps_it()
    {
        var graph = FfmpegArgsBuilder.FilterGraph(Request(rungs: 2, sourceHasSar: true));

        Assert.DoesNotContain("setsar", graph, StringComparison.Ordinal);
    }

    [Fact]
    public void Single_rung_does_not_split()
    {
        var graph = FfmpegArgsBuilder.FilterGraph(Request(rungs: 1));

        Assert.DoesNotContain("split", graph, StringComparison.Ordinal);
        Assert.EndsWith("[v0]", graph, StringComparison.Ordinal);
    }

    private static string After(IReadOnlyList<string> args, string key) => args[args.ToList().IndexOf(key) + 1];
}

public sealed class FfprobeParserTests
{
    [Fact]
    public void Swaps_dimensions_for_rotated_video()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "h264", "width": 1920, "height": 1080, "avg_frame_rate": "30000/1001",
              "side_data_list": [ { "side_data_type": "Display Matrix", "rotation": -90 } ] },
            { "codec_type": "audio", "codec_name": "aac" }
          ],
          "format": { "format_name": "mov,mp4,m4a,3gp,3g2,mj2", "duration": "12.345", "size": "1000" }
        }
        """;

        var media = FfprobeParser.Parse(json);

        Assert.Equal((1080, 1920), (media.Width, media.Height));
        Assert.True(media.HasAudio);
        Assert.Equal(29.97, media.FrameRate, 2);
        Assert.Equal(12.345, media.DurationSeconds, 3);
    }

    [Fact]
    public void Detects_missing_audio_and_ignores_cover_art()
    {
        const string json = """
        {
          "streams": [
            { "codec_type": "video", "codec_name": "mjpeg", "width": 600, "height": 600, "disposition": { "attached_pic": 1 } },
            { "codec_type": "video", "codec_name": "h264", "width": 1280, "height": 720, "avg_frame_rate": "25/1" }
          ],
          "format": { "format_name": "matroska,webm", "duration": "3.0" }
        }
        """;

        var media = FfprobeParser.Parse(json);

        Assert.False(media.HasAudio);
        Assert.Equal((1280, 720), (media.Width, media.Height));
    }

    [Theory]
    [InlineData("\"sample_aspect_ratio\": \"1:1\",", true)]
    [InlineData("\"sample_aspect_ratio\": \"0:1\",", false)]
    [InlineData("", false)]
    public void Detects_whether_source_has_sar(string field, bool expected)
    {
        var json = "{ \"streams\": [ { \"codec_type\": \"video\", " + field + " \"width\": 720, \"height\": 1280, \"avg_frame_rate\": \"30/1\" } ], \"format\": { \"format_name\": \"mov,mp4\", \"duration\": \"3\" } }";

        Assert.Equal(expected, FfprobeParser.Parse(json).HasSampleAspectRatio);
    }

    [Fact]
    public void Garbage_is_rejected_as_invalid_media()
    {
        var ex = Assert.Throws<MediaRejectedException>(() => FfprobeParser.Parse("not json"));
        Assert.Equal("invalid_media", ex.ReasonCode);
    }
}

public sealed class MediaPolicyTests
{
    private static readonly TranscodeOptions Options = new() { MaxDurationSeconds = 600, MaxDimension = 4096, MaxStreams = 16 };

    private static MediaInfo Valid() => new("mov,mp4,m4a,3gp,3g2,mj2", 10, 1920, 1080, 30, true, true, 2);

    [Fact]
    public void Accepts_regular_video() => MediaPolicy.EnsureAcceptable(Valid(), Options);

    [Theory]
    [InlineData("hls", 10, 1920, 1080, true, "unsupported_format")]
    [InlineData("image2", 10, 1920, 1080, true, "unsupported_format")]
    [InlineData("mov,mp4", 10, 1920, 1080, false, "no_video_stream")]
    [InlineData("mov,mp4", 601, 1920, 1080, true, "duration_exceeded")]
    [InlineData("mov,mp4", 10, 8192, 4320, true, "resolution_exceeded")]
    [InlineData("mov,mp4", 0, 1920, 1080, true, "invalid_media")]
    public void Rejects_dangerous_or_unsupported_media(string format, double duration, int w, int h, bool hasVideo, string reason)
    {
        var media = Valid() with { FormatName = format, DurationSeconds = duration, Width = w, Height = h, HasVideo = hasVideo };

        var ex = Assert.Throws<MediaRejectedException>(() => MediaPolicy.EnsureAcceptable(media, Options));

        Assert.Equal(reason, ex.ReasonCode);
    }
}

