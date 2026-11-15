using System.Globalization;

namespace Maksov.VideoService.Analysis.Worker.Domain;

/// <summary>
/// Composes coherent English text and tags from the "readings". A pure function — covered by unit tests.
/// A frame caption from the vision model (if enabled) is added as a separate sentence.
/// </summary>
public static class DescriptionComposer
{
    public static (string Text, IReadOnlyList<string> Tags) Compose(VideoFacts f, string? caption)
    {
        ArgumentNullException.ThrowIfNull(f);
        var sentences = new List<string>(6);
        var tags = new List<string>(6);

        var (adjective, tag) = f.Orientation switch
        {
            Orientation.Vertical => ("Vertical", "vertical"),
            Orientation.Horizontal => ("Horizontal", "horizontal"),
            _ => ("Square", "square"),
        };
        sentences.Add($"{adjective} video, {FormatDuration(f.DurationMs)}, {f.Width}×{f.Height}.");
        tags.Add(tag);

        AddEditing(f, sentences, tags);
        AddPicture(f, sentences, tags);
        AddAudio(f, sentences, tags);

        if (!string.IsNullOrWhiteSpace(caption))
        {
            sentences.Add($"In frames: {caption.Trim().TrimEnd('.')}.");
        }

        return (string.Join(' ', sentences), tags);
    }

    private static void AddEditing(VideoFacts f, List<string> sentences, List<string> tags)
    {
        if (f.FrameSamples < 2)
        {
            return;
        }

        if (f.SceneCuts == 0)
        {
            sentences.Add("Single shot with no editing cuts.");
            tags.Add("single shot");
            return;
        }

        var average = f.DurationMs / 1000.0 / (f.SceneCuts + 1);
        var pace = average switch
        {
            < 1.5 => "very fast-paced editing",
            < 4 => "fast-paced editing",
            < 8 => "calm editing",
            _ => "slow-paced editing",
        };
        sentences.Add($"{f.SceneCuts} {Plural(f.SceneCuts, "scene cut", "scene cuts")}, on average a shot lasts {Number(average)} s — {pace}.");
        tags.Add(average < 4 ? "fast-paced" : "calm");
    }

    private static void AddPicture(VideoFacts f, List<string> sentences, List<string> tags)
    {
        if (f.AvgBrightness is { } brightness && f.AvgSaturation is { } saturation)
        {
            var light = brightness switch { < 60 => "dark", < 130 => "medium brightness", _ => "bright" };
            var colors = saturation switch { < 20 => "muted", < 55 => "moderate", _ => "saturated" };
            var tone = "";
            if (saturation >= 30 && f.MeanHue is { } hue)
            {
                tone = hue is <= 60 or >= 330 ? "warm" : hue is >= 170 and <= 270 ? "cool" : "";
            }

            sentences.Add(tone.Length > 0
                ? $"The picture is {light}, with {colors} colors, {tone} tones predominate."
                : $"The picture is {light}, with {colors} colors.");

            if (brightness < 60)
            {
                tags.Add("dark");
            }
            else if (brightness >= 130)
            {
                tags.Add("bright");
            }

            if (tone.Length > 0)
            {
                tags.Add($"{tone} tones");
            }
        }

        if (f.AvgMotion is { } motion)
        {
            sentences.Add(motion switch
            {
                < 1.5 => "Little motion in frame.",
                < 8 => "Moderate motion in frame.",
                _ => "Lots of motion in frame.",
            });
            if (motion >= 8)
            {
                tags.Add("lots of motion");
            }
        }
    }

    private static void AddAudio(VideoFacts f, List<string> sentences, List<string> tags)
    {
        if (!f.HasAudio)
        {
            sentences.Add("No sound.");
            tags.Add("no sound");
            return;
        }

        tags.Add("with sound");
        var loud = f.MeanVolumeDb switch
        {
            null => "volume not determined",
            > -18 => "loud",
            > -30 => "medium volume",
            _ => "quiet",
        };
        var pauses = f.SilenceRatio switch
        {
            < 0.05 => "almost no pauses",
            < 0.6 => $"noticeable pauses (about {(int)Math.Round(f.SilenceRatio * 100)}% of the time)",
            _ => "silence most of the time",
        };
        sentences.Add($"Has sound: {loud}, {pauses}.");
    }

    internal static string FormatDuration(int durationMs)
    {
        var seconds = (int)Math.Round(durationMs / 1000.0);
        if (seconds < 60)
        {
            return $"{seconds} s";
        }

        var minutes = seconds / 60;
        var rest = seconds % 60;
        return rest == 0 ? $"{minutes} min" : $"{minutes} min {rest} s";
    }

    /// <summary>Pluralization by count: 1 scene cut, 2 scene cuts.</summary>
    internal static string Plural(int n, string one, string many) => n == 1 ? one : many;

    private static string Number(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
