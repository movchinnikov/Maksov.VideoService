using System.Buffers;

namespace Maksov.VideoService.BuildingBlocks.Storage;

/// <summary>
/// Layout of video objects in storage.
/// <code>
/// uploads/{id}/source            — source file (presigned PUT from the browser)
/// videos/{id}/stream/manifest.mpd — DASH
/// videos/{id}/stream/master.m3u8  — HLS
/// videos/{id}/stream/*.m4s        — shared CMAF segments
/// videos/{id}/poster.jpg
/// </code>
/// </summary>
public static class MediaKeys
{
    public const string DashManifest = "manifest.mpd";
    public const string HlsMaster = "master.m3u8";
    public const string PosterFile = "poster.jpg";

    private static readonly SearchValues<char> AllowedChars =
        SearchValues.Create("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789._-");

    public static string Source(Guid videoId) => $"uploads/{videoId:N}/source";

    public static string StreamFile(Guid videoId, string fileName) => $"videos/{videoId:N}/stream/{fileName}";

    public static string Poster(Guid videoId) => $"videos/{videoId:N}/{PosterFile}";

    /// <summary>Source prefix: <c>uploads/{id}/</c> (for deletion).</summary>
    public static string SourcePrefix(Guid videoId) => $"uploads/{videoId:N}/";

    /// <summary>Processing result prefix: <c>videos/{id}/</c> (for deletion).</summary>
    public static string VideoPrefix(Guid videoId) => $"videos/{videoId:N}/";

    /// <summary>
    /// Stream file name from a URL: flat (no directories), safe characters, known extension.
    /// Protects against path traversal and reading other keys.
    /// </summary>
    public static bool IsValidStreamFile(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName) || fileName.Length > 128 || fileName[0] == '.')
        {
            return false;
        }

        if (fileName.AsSpan().ContainsAnyExcept(AllowedChars) || fileName.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        return ContentTypeFor(fileName) is not null;
    }

    public static string? ContentTypeFor(string fileName) => Path.GetExtension(fileName) switch
    {
        ".mpd" => "application/dash+xml",
        ".m3u8" => "application/vnd.apple.mpegurl",
        ".m4s" => "video/iso.segment",
        ".mp4" => "video/mp4",
        ".jpg" => "image/jpeg",
        _ => null,
    };

    /// <summary>Manifests are cached briefly; segments are immutable after Ready.</summary>
    public static bool IsManifest(string fileName) =>
        fileName.EndsWith(".mpd", StringComparison.Ordinal) || fileName.EndsWith(".m3u8", StringComparison.Ordinal);
}

