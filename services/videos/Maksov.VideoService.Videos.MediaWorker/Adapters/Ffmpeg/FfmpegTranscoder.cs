using System.Globalization;
using Maksov.VideoService.BuildingBlocks.Processes;
using Maksov.VideoService.Videos.MediaWorker.Ports;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Videos.MediaWorker.Adapters.Ffmpeg;

internal sealed class FfmpegTranscoder(IOptions<TranscodeOptions> options) : ITranscoder
{
    public async Task TranscodeAsync(TranscodeRequest request, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Timeout is proportional to duration: veryfast on 2 CPUs runs at ~1–2× real time.
        var timeout = TimeSpan.FromSeconds(request.DurationSeconds * 6 + 120);
        var durationUs = request.DurationSeconds * 1_000_000;
        var lastReported = -1;

        void OnProgress(string line)
        {
            // -progress format: key=value per line; out_time_us is the position in microseconds.
            if (progress is null || !line.StartsWith("out_time_us=", StringComparison.Ordinal))
            {
                return;
            }

            if (long.TryParse(line.AsSpan("out_time_us=".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && durationUs > 0)
            {
                var percent = (int)Math.Clamp(us / durationUs * 100, 0, 100);
                if (percent / 10 != lastReported / 10)
                {
                    lastReported = percent;
                    progress.Report(percent / 100.0);
                }
            }
        }

        var result = await ProcessRunner.RunAsync(
            options.Value.FfmpegPath, FfmpegArgsBuilder.Transcode(request), timeout, OnProgress, captureStdOut: false, cancellationToken);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg exited with {result.ExitCode}: {result.StdErrTail}");
        }

        if (!File.Exists(Path.Combine(request.OutputDirectory, "manifest.mpd")) || !File.Exists(Path.Combine(request.OutputDirectory, "master.m3u8")))
        {
            throw new InvalidOperationException("ffmpeg finished without DASH/HLS manifests");
        }
    }

    public async Task CapturePosterAsync(string sourcePath, string posterPath, double atSeconds, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            options.Value.FfmpegPath, FfmpegArgsBuilder.Poster(sourcePath, posterPath, atSeconds), TimeSpan.FromSeconds(60),
            null, captureStdOut: false, cancellationToken);

        if (result.ExitCode != 0 || !File.Exists(posterPath))
        {
            throw new InvalidOperationException($"poster extraction failed: {result.StdErrTail}");
        }
    }
}
