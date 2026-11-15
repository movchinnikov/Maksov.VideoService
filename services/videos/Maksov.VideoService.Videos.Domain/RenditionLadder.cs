namespace Maksov.VideoService.Videos.Domain;

/// <summary>One rung of the ABR ladder.</summary>
public sealed record Rendition(string Name, int Width, int Height, int VideoKbps, int MaxRateKbps, int BufferKbps);

/// <summary>
/// ABR ladder planning by the short side of the source (works for horizontal and vertical video).
/// Never upscales; always returns at least one rung.
/// </summary>
public static class RenditionLadder
{
    private static readonly (int ShortSide, int Kbps)[] Rungs =
    [
        (240, 400),
        (360, 800),
        (480, 1400),
        (720, 2800),
        (1080, 5000),
    ];

    public const int MaxShortSide = 1080;

    public static IReadOnlyList<Rendition> Plan(int sourceWidth, int sourceHeight, double fps)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sourceHeight);

        var shortSide = Math.Min(sourceWidth, sourceHeight);
        var highFps = fps > 31;
        var result = new List<Rendition>(Rungs.Length);

        foreach (var (rung, kbps) in Rungs)
        {
            if (rung > shortSide)
            {
                break;
            }
            result.Add(Create(sourceWidth, sourceHeight, rung, highFps ? kbps * 3 / 2 : kbps));
        }

        if (result.Count == 0)
        {
            // Source smaller than 240p: a single rung at the source size.
            result.Add(Create(sourceWidth, sourceHeight, Even(shortSide), Rungs[0].Kbps));
        }

        return result;
    }

    /// <summary>Audio bitrate: save on small ladders.</summary>
    public static int AudioKbps(IReadOnlyList<Rendition> ladder)
    {
        ArgumentNullException.ThrowIfNull(ladder);
        var top = ladder[^1];
        return Math.Min(top.Width, top.Height) <= 360 ? 96 : 128;
    }

    private static Rendition Create(int w, int h, int shortSide, int kbps)
    {
        int width, height;
        if (w >= h)
        {
            height = shortSide;
            width = Even((int)Math.Round((double)w * shortSide / h));
        }
        else
        {
            width = shortSide;
            height = Even((int)Math.Round((double)h * shortSide / w));
        }

        return new Rendition($"{shortSide}p", width, height, kbps, kbps * 11 / 10, kbps * 2);
    }

    private static int Even(int value) => Math.Max(2, value - (value % 2));
}

