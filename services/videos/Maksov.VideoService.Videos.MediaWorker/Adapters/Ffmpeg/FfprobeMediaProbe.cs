using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Maksov.VideoService.BuildingBlocks.Processes;
using Maksov.VideoService.Videos.MediaWorker.Domain;
using Maksov.VideoService.Videos.MediaWorker.Ports;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Videos.MediaWorker.Adapters.Ffmpeg;

internal sealed class FfprobeMediaProbe(IOptions<TranscodeOptions> options) : IMediaProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task<MediaInfo> ProbeAsync(string filePath, CancellationToken cancellationToken)
    {
        string[] args =
        [
            "-v", "error",
            "-protocol_whitelist", "file",   // no http/concat/... — SSRF protection via playlists
            "-print_format", "json",
            "-show_format", "-show_streams",
            filePath,
        ];

        ProcessResult result;
        try
        {
            result = await ProcessRunner.RunAsync(options.Value.FfprobePath, args, Timeout, null, captureStdOut: true, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            throw new MediaRejectedException("invalid_media", ex.Message);
        }

        if (result.ExitCode != 0)
        {
            throw new MediaRejectedException("invalid_media", $"ffprobe failed: {result.StdErrTail}");
        }

        return FfprobeParser.Parse(result.StdOut);
    }
}

/// <summary>Parses ffprobe JSON → <see cref="MediaInfo"/>. Accounts for rotation (displaymatrix / tags.rotate).</summary>
internal static class FfprobeParser
{
    public static MediaInfo Parse(string json)
    {
        FfprobeOutput? output;
        try
        {
            output = JsonSerializer.Deserialize(json, FfprobeJsonContext.Default.FfprobeOutput);
        }
        catch (JsonException ex)
        {
            throw new MediaRejectedException("invalid_media", ex.Message);
        }

        if (output?.Format is null || output.Streams is null)
        {
            throw new MediaRejectedException("invalid_media", "ffprobe returned no format/streams");
        }

        var video = output.Streams.FirstOrDefault(static s => s.CodecType == "video" && s.Disposition?.AttachedPic != 1);
        var hasAudio = output.Streams.Any(static s => s.CodecType == "audio");
        var duration = ParseDouble(output.Format.Duration) ?? ParseDouble(video?.Duration) ?? 0;

        var width = video?.Width ?? 0;
        var height = video?.Height ?? 0;
        var rotation = Rotation(video);
        if (Math.Abs(rotation) % 180 == 90)
        {
            (width, height) = (height, width);
        }

        var fps = ParseRate(video?.AvgFrameRate) ?? ParseRate(video?.RFrameRate) ?? 30;

        return new MediaInfo(
            output.Format.FormatName ?? "",
            duration,
            width,
            height,
            fps,
            HasVideo: video is not null,
            HasAudio: hasAudio,
            StreamCount: output.Streams.Count,
            HasSampleAspectRatio: IsDefinedRatio(video?.SampleAspectRatio));
    }

    private static int Rotation(FfprobeStream? stream)
    {
        if (stream is null)
        {
            return 0;
        }

        var fromSideData = stream.SideDataList?.FirstOrDefault(static d => d.Rotation is not null)?.Rotation;
        if (fromSideData is not null)
        {
            return (int)Math.Round(fromSideData.Value);
        }

        return stream.Tags is not null && stream.Tags.TryGetValue("rotate", out var rotate) && int.TryParse(rotate, CultureInfo.InvariantCulture, out var r)
            ? r
            : 0;
    }

    /// <summary>SAR is set if it is "n:m" with positive n and m (ffprobe has no field or "0:1" — not set).</summary>
    private static bool IsDefinedRatio(string? value)
    {
        var parts = value?.Split(':');
        return parts is { Length: 2 }
            && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0
            && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) && d > 0;
    }

    private static double? ParseDouble(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;

    private static double? ParseRate(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        var slash = value.IndexOf('/', StringComparison.Ordinal);
        if (slash < 0)
        {
            return ParseDouble(value);
        }

        var num = ParseDouble(value[..slash]);
        var den = ParseDouble(value[(slash + 1)..]);
        return num is not null && den is not null ? num / den : null;
    }
}

internal sealed class FfprobeOutput
{
    public List<FfprobeStream>? Streams { get; set; }
    public FfprobeFormat? Format { get; set; }
}

internal sealed class FfprobeStream
{
    public string? CodecType { get; set; }
    public string? CodecName { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
    public string? SampleAspectRatio { get; set; }
    public string? AvgFrameRate { get; set; }
    public string? RFrameRate { get; set; }
    public string? Duration { get; set; }
    public Dictionary<string, string>? Tags { get; set; }
    public List<FfprobeSideData>? SideDataList { get; set; }
    public FfprobeDisposition? Disposition { get; set; }
}

internal sealed class FfprobeSideData
{
    public string? SideDataType { get; set; }
    public double? Rotation { get; set; }
}

internal sealed class FfprobeDisposition
{
    public int? AttachedPic { get; set; }
}

internal sealed class FfprobeFormat
{
    public string? FormatName { get; set; }
    public string? Duration { get; set; }
    public string? Size { get; set; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    NumberHandling = JsonNumberHandling.AllowReadingFromString)]
[JsonSerializable(typeof(FfprobeOutput))]
internal sealed partial class FfprobeJsonContext : JsonSerializerContext;
