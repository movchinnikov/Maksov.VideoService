using Maksov.VideoService.Analysis.Bll.Ports;
using Maksov.VideoService.Analysis.Worker.Domain;
using Maksov.VideoService.Analysis.Worker.Ports;
using Maksov.VideoService.BuildingBlocks.Processes;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Analysis.Worker.Adapters.Ffmpeg;

/// <summary>
/// ffmpeg analysis tools: one pass over the downscaled video (scene cuts, brightness, color, motion)
/// and one over the audio (volume, pauses). Encodes nothing — only measures, so it is cheap on CPU.
/// </summary>
internal sealed class FfmpegVideoInspector(IOptions<AnalysisOptions> options) : IVideoInspector
{
    public async Task<VideoFacts> InspectAsync(string filePath, AnalysisJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var o = options.Value;
        var seconds = Math.Max(1, job.DurationMs / 1000.0);
        var timeout = TimeSpan.FromSeconds(Math.Max(60, (seconds * 2) + 60));

        var hasAudio = await HasAudioAsync(o, filePath, cancellationToken).ConfigureAwait(false);

        var frames = new FrameStatsAccumulator();
        var video = await ProcessRunner.RunAsync(
            o.FfmpegPath,
            [
                "-hide_banner", "-nostdin", "-v", "error",
                "-protocol_whitelist", "file",
                "-i", filePath,
                "-an",
                // 4 frames/s at 180 px height: enough for statistics, almost no CPU spent.
                "-vf", "fps=4,scale=-2:180,select='gte(scene,0)',signalstats,metadata=print:file=-",
                "-f", "null", "-",
            ],
            timeout, frames.Feed, captureStdOut: false, cancellationToken).ConfigureAwait(false);
        if (video.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg video pass exited with {video.ExitCode}: {video.StdErrTail}");
        }

        AudioStats? audio = null;
        if (hasAudio)
        {
            var parser = new AudioStatsParser();
            var pass = await ProcessRunner.RunAsync(
                o.FfmpegPath,
                [
                    "-hide_banner", "-nostdin", "-protocol_whitelist", "file",
                    "-i", filePath, "-vn",
                    "-af", "volumedetect,silencedetect=n=-45dB:d=0.5",
                    "-f", "null", "-",
                ],
                timeout, null, captureStdOut: false, cancellationToken, onStdErrLine: parser.Feed).ConfigureAwait(false);
            if (pass.ExitCode != 0)
            {
                throw new InvalidOperationException($"ffmpeg audio pass exited with {pass.ExitCode}: {pass.StdErrTail}");
            }
            audio = parser.Build();
        }

        var stats = frames.Build();
        return new VideoFacts(
            job.DurationMs, job.Width, job.Height, hasAudio,
            stats.Frames, stats.SceneCuts, stats.Brightness, stats.Saturation, stats.MeanHue, stats.Motion,
            audio?.MeanVolumeDb,
            audio is null ? 0 : Math.Clamp(audio.SilenceSeconds / seconds, 0, 1));
    }

    private static async Task<bool> HasAudioAsync(AnalysisOptions o, string filePath, CancellationToken ct)
    {
        var probe = await ProcessRunner.RunAsync(
            o.FfprobePath,
            ["-v", "error", "-protocol_whitelist", "file", "-select_streams", "a:0", "-show_entries", "stream=codec_name", "-of", "csv=p=0", filePath],
            TimeSpan.FromSeconds(30), null, captureStdOut: true, ct).ConfigureAwait(false);
        return probe.ExitCode == 0 && !string.IsNullOrWhiteSpace(probe.StdOut);
    }
}
