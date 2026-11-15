using System.Globalization;
using System.Text;
using Maksov.VideoService.Videos.MediaWorker.Ports;

namespace Maksov.VideoService.Videos.MediaWorker.Adapters.Ffmpeg;

/// <summary>
/// Builds ffmpeg arguments. A pure function — covered by unit tests.
/// Single pass: decode the source once, split into N rungs, encode in parallel to CMAF,
/// the dash muxer writes manifest.mpd and (with -hls_playlist 1) master.m3u8 over the same segments.
/// </summary>
internal static class FfmpegArgsBuilder
{
    /// <summary>A keyframe every 2 seconds on all rungs — aligned boundaries for ABR switching.</summary>
    private const string KeyframeExpression = "expr:gte(t,n_forced*2)";

    public static IReadOnlyList<string> Transcode(TranscodeRequest r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var inv = CultureInfo.InvariantCulture;
        var n = r.Ladder.Count;

        var args = new List<string>(64)
        {
            "-hide_banner", "-nostdin", "-y",
            "-loglevel", "error",
            "-progress", "pipe:1", "-nostats",
            "-protocol_whitelist", "file",
            "-i", r.SourcePath,
            "-filter_complex", FilterGraph(r),
        };

        for (var i = 0; i < n; i++)
        {
            args.Add("-map");
            args.Add($"[v{i}]");
        }

        if (r.HasAudio)
        {
            args.AddRange(["-map", "0:a:0"]);
        }

        args.AddRange(
        [
            "-c:v", "libx264",
            "-preset", r.Preset,
            "-profile:v", "high",
            "-force_key_frames", KeyframeExpression,
            "-sc_threshold", "0",
        ]);

        for (var i = 0; i < n; i++)
        {
            var rung = r.Ladder[i];
            args.AddRange(
            [
                $"-b:v:{i}", string.Create(inv, $"{rung.VideoKbps}k"),
                $"-maxrate:v:{i}", string.Create(inv, $"{rung.MaxRateKbps}k"),
                $"-bufsize:v:{i}", string.Create(inv, $"{rung.BufferKbps}k"),
            ]);
        }

        if (r.HasAudio)
        {
            args.AddRange(["-c:a", "aac", "-b:a", string.Create(inv, $"{r.AudioKbps}k"), "-ac", "2", "-ar", "48000"]);
        }

        args.AddRange(
        [
            "-threads", r.Threads.ToString(inv),
            "-f", "dash",
            "-seg_duration", r.SegmentSeconds.ToString(inv),
            "-use_template", "1",
            "-use_timeline", "1",
            "-hls_playlist", "1",
            "-adaptation_sets", r.HasAudio ? "id=0,streams=v id=1,streams=a" : "id=0,streams=v",
            Path.Combine(r.OutputDirectory, "manifest.mpd"),
        ]);

        return args;
    }

    public static IReadOnlyList<string> Poster(string sourcePath, string posterPath, double atSeconds) =>
    [
        "-hide_banner", "-nostdin", "-y",
        "-loglevel", "error",
        "-protocol_whitelist", "file",
        "-ss", atSeconds.ToString("0.###", CultureInfo.InvariantCulture),
        "-i", sourcePath,
        "-frames:v", "1",
        "-vf", "scale=w=1280:h=1280:force_original_aspect_ratio=decrease:force_divisible_by=2",
        "-q:v", "4",
        posterPath,
    ];

    internal static string FilterGraph(TranscodeRequest r)
    {
        var inv = CultureInfo.InvariantCulture;
        var n = r.Ladder.Count;
        var sb = new StringBuilder("[0:v]");
        if (!r.SourceHasSar)
        {
            // The source has no SAR (typical for phones): without it scale cannot preserve the aspect ratio, and after
            // rounding dimensions to even values (426×240, 640×360 …) the rungs get slightly different aspect ratios.
            // DASH rejects such a set ("Conflicting stream aspect ratios values"). An explicit SAR 1:1 before split —
            // scale then adjusts each rung's SAR and the display aspect ratios match.
            sb.Append("setsar=1,");
        }
        if (r.CapFrameRate)
        {
            sb.Append("fps=60,");
        }
        sb.Append("format=yuv420p");

        if (n == 1)
        {
            var only = r.Ladder[0];
            sb.Append(inv, $",scale={only.Width}:{only.Height}:flags=bicubic[v0]");
            return sb.ToString();
        }

        sb.Append(inv, $",split={n}");
        for (var i = 0; i < n; i++)
        {
            sb.Append(inv, $"[s{i}]");
        }

        for (var i = 0; i < n; i++)
        {
            var rung = r.Ladder[i];
            sb.Append(inv, $";[s{i}]scale={rung.Width}:{rung.Height}:flags=bicubic[v{i}]");
        }

        return sb.ToString();
    }

}

