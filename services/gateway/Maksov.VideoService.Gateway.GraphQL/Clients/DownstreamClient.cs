using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Maksov.VideoService.Gateway.GraphQL.Clients;

/// <summary>A service answered with a non-success status code; mapped to a GraphQL error code by the error filter.</summary>
public sealed class DownstreamException : Exception
{
    public DownstreamException()
    {
    }

    public DownstreamException(string message)
        : base(message)
    {
    }

    public DownstreamException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public DownstreamException(string service, HttpStatusCode statusCode)
        : base($"{service} answered {(int)statusCode}")
    {
        Service = service;
        StatusCode = statusCode;
    }

    public string? Service { get; }

    public HttpStatusCode StatusCode { get; }
}

/// <summary>Base of the typed clients: JSON in and out, non-success statuses become <see cref="DownstreamException"/>.</summary>
public abstract class DownstreamClient(HttpClient http, string service)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    protected static string Escape(string value) => Uri.EscapeDataString(value);

    protected static string Join(IEnumerable<Guid> ids) => string.Join(',', ids);

    protected static string Join(IEnumerable<string> ids) => string.Join(',', ids.Select(Uri.EscapeDataString));

    /// <summary>Appends the non-null query parameters to a relative path.</summary>
    protected static string WithQuery(string path, params (string Name, string? Value)[] query)
    {
        var parts = query.Where(static q => q.Value is not null).Select(static q => $"{q.Name}={Uri.EscapeDataString(q.Value!)}").ToArray();
        return parts.Length == 0 ? path : $"{path}?{string.Join('&', parts)}";
    }

    protected async Task<T> GetAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    protected async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, path, body, cancellationToken);
        return await ReadAsync<T>(response, cancellationToken);
    }

    protected async Task SendAsync(HttpMethod method, string path, CancellationToken cancellationToken)
    {
        using var response = await SendCoreAsync(method, path, null, cancellationToken);
        Ensure(response);
    }

    /// <summary>Like <see cref="GetAsync{T}"/> but a 404 yields null.</summary>
    protected async Task<T?> GetOrNullAsync<T>(string path, CancellationToken cancellationToken)
        where T : class
    {
        using var response = await http.GetAsync(new Uri(path, UriKind.Relative), cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadAsync<T>(response, cancellationToken);
    }

    private async Task<HttpResponseMessage> SendCoreAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, new Uri(path, UriKind.Relative));
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        }

        return await http.SendAsync(request, cancellationToken);
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        Ensure(response);
        var value = await response.Content.ReadFromJsonAsync<T>(Json, cancellationToken);
        return value ?? throw new DownstreamException(service, HttpStatusCode.BadGateway);
    }

    private void Ensure(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new DownstreamException(service, response.StatusCode);
        }
    }
}
