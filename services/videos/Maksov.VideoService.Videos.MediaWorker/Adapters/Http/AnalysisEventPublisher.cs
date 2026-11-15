using System.Text;
using Maksov.VideoService.Videos.Domain;
using Maksov.VideoService.Videos.MediaWorker.Ports;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.Videos.MediaWorker.Adapters.Http;

public sealed class OutboxOptions
{
    public const string SectionName = "Outbox";

    /// <summary>Subscribers: name → service address on the internal network. Empty — dispatcher disabled (lesson without subscribers).</summary>
    public Dictionary<string, SubscriberOptions> Subscribers { get; set; } = [];

    public int BatchSize { get; set; } = 10;

    /// <summary>Fallback polling if NOTIFY is lost.</summary>
    public int PollSeconds { get; set; } = 30;

    public IReadOnlyList<string> ConfiguredSubscribers() =>
        [.. Subscribers.Where(static s => !string.IsNullOrWhiteSpace(s.Value.BaseUrl)).Select(static s => s.Key)];
}

public sealed class SubscriberOptions
{
    public string BaseUrl { get; set; } = "";
}

public sealed class InternalOptions
{
    public const string SectionName = "Internal";

    public string ApiKey { get; set; } = "";
}

/// <summary>
/// Delivers an event to a subscriber (analysis, comments) via the internal API at the path from <see cref="VideoEvents.PathOf"/>.
/// The receiving side is idempotent, so redelivery is safe.
/// </summary>
internal sealed class HttpEventPublisher(IHttpClientFactory clients, IOptions<OutboxOptions> outbox, IOptions<InternalOptions> internalOptions) : IEventPublisher
{
    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!outbox.Value.Subscribers.TryGetValue(message.Subscriber, out var subscriber) || string.IsNullOrWhiteSpace(subscriber.BaseUrl))
        {
            throw new HttpRequestException($"subscriber '{message.Subscriber}' is not configured");
        }

        var uri = new Uri(new Uri(subscriber.BaseUrl), VideoEvents.PathOf(message.Topic));
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(message.Payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Internal-Key", internalOptions.Value.ApiKey);

        using var http = clients.CreateClient("outbox");
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"{message.Subscriber} returned {(int)response.StatusCode}: {body}");
        }
    }
}
