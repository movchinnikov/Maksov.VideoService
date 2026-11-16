using System.Text;
using Microsoft.Extensions.Options;

namespace Maksov.VideoService.BuildingBlocks.Outbox;

/// <summary>Maps a topic to the internal path of the receiving side, e.g. <c>comment.added.v1</c> -> <c>/internal/v1/events/comment-added</c>.</summary>
public delegate string EventPathResolver(string topic);

/// <summary>
/// Delivers an event to a subscriber by the internal API (<c>X-Internal-Key</c>). The receiving side is idempotent
/// (by <c>eventId</c>), so redelivery is safe.
/// </summary>
public sealed class HttpEventPublisher(
    IHttpClientFactory clients,
    IOptions<OutboxOptions> outbox,
    EventPathResolver pathOf,
    string internalApiKey)
{
    public const string ClientName = "outbox";

    public async Task PublishAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!outbox.Value.Subscribers.TryGetValue(message.Subscriber, out var subscriber) || string.IsNullOrWhiteSpace(subscriber.BaseUrl))
        {
            throw new HttpRequestException($"subscriber '{message.Subscriber}' is not configured");
        }

        var uri = new Uri(new Uri(subscriber.BaseUrl), pathOf(message.Topic));
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = new StringContent(message.Payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("X-Internal-Key", internalApiKey);

        using var http = clients.CreateClient(ClientName);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException($"{message.Subscriber} returned {(int)response.StatusCode}: {body}");
        }
    }
}
