using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.Analysis.Bll.Ports;
using Maksov.VideoService.Analysis.Worker.Ports;
using Maksov.VideoService.BuildingBlocks.Processes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Analysis.Worker.Adapters.Ollama;

internal sealed record OllamaRequest(string Model, string Prompt, string[] Images, bool Stream);

internal sealed record OllamaResponse(string? Response);

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
[JsonSerializable(typeof(OllamaRequest))]
[JsonSerializable(typeof(OllamaResponse))]
internal sealed partial class OllamaJsonContext : JsonSerializerContext;

/// <summary>HTTP client for local Ollama (<c>POST /api/generate</c> with the image in base64).</summary>
internal sealed class OllamaClient(HttpClient http, IOptions<AnalysisOptions> options)
{
    public async Task<string?> DescribeAsync(byte[] jpeg, CancellationToken cancellationToken)
    {
        var o = options.Value.Captioner;
        var request = new OllamaRequest(o.Model, o.Prompt, [Convert.ToBase64String(jpeg)], Stream: false);
        using var response = await http.PostAsJsonAsync(new Uri("/api/generate", UriKind.Relative), request, OllamaJsonContext.Default.OllamaRequest, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync(OllamaJsonContext.Default.OllamaResponse, cancellationToken).ConfigureAwait(false);
        var text = body?.Response?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }
}

/// <summary>Takes several frames of the video, asks the model to describe each and joins the answers. A model failure does not break the analysis.</summary>
internal sealed partial class OllamaFrameCaptioner(
    OllamaClient client,
    IOptions<AnalysisOptions> options,
    ILogger<OllamaFrameCaptioner> logger) : IFrameCaptioner
{
    public async Task<string?> CaptionAsync(string filePath, AnalysisJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var o = options.Value;
        var frames = Math.Clamp(o.Captioner.Frames, 1, 6);
        var captions = new List<string>(frames);
        var dir = Path.GetDirectoryName(filePath)!;

        for (var i = 0; i < frames; i++)
        {
            // Frames evenly spaced within the video: 1/(n+1), 2/(n+1) …
            var at = job.DurationMs / 1000.0 * (i + 1) / (frames + 1);
            var jpg = Path.Combine(dir, $"frame-{i}.jpg");
            var extract = await ProcessRunner.RunAsync(
                o.FfmpegPath,
                ["-hide_banner", "-nostdin", "-v", "error", "-protocol_whitelist", "file",
                 "-ss", at.ToString("0.###", CultureInfo.InvariantCulture), "-i", filePath,
                 "-frames:v", "1", "-vf", "scale=-2:448", "-q:v", "5", "-y", jpg],
                TimeSpan.FromSeconds(30), null, captureStdOut: false, cancellationToken).ConfigureAwait(false);
            if (extract.ExitCode != 0 || !File.Exists(jpg))
            {
                continue;
            }

            try
            {
                var bytes = await File.ReadAllBytesAsync(jpg, cancellationToken).ConfigureAwait(false);
                var caption = await client.DescribeAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (caption is not null && !captions.Contains(caption, StringComparer.OrdinalIgnoreCase))
                {
                    captions.Add(caption.TrimEnd('.'));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // The model is not running or not responding — the description is built without frame captions.
                LogCaptionerUnavailable(logger, ex);
                return null;
            }
        }

        return captions.Count == 0 ? null : string.Join("; ", captions);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Captioner unavailable, continuing without frame captions")]
    private static partial void LogCaptionerUnavailable(ILogger logger, Exception ex);
}

internal sealed class NullFrameCaptioner : IFrameCaptioner
{
    public Task<string?> CaptionAsync(string filePath, AnalysisJob job, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
}
