namespace Maksov.VideoService.BuildingBlocks.Outbox;

/// <summary>Convention for internal event intake routes: topic <c>comment.added.v1</c> is delivered to <c>/internal/v1/events/comment-added</c>.</summary>
public static class EventPaths
{
    public static string Default(string topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        var parts = topic.Split('.');
        // Drop the trailing version segment (v1, v2, ...).
        var name = parts.Length > 1 && parts[^1].Length > 1 && parts[^1][0] == 'v' && char.IsDigit(parts[^1][1])
            ? string.Join('-', parts[..^1])
            : string.Join('-', parts);
        return "/internal/v1/events/" + name;
    }
}
