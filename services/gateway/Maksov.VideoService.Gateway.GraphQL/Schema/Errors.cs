using System.Net;
using HotChocolate;
using Maksov.VideoService.Gateway.GraphQL.Clients;

namespace Maksov.VideoService.Gateway.GraphQL.Schema;

/// <summary>Error codes of the GraphQL API (extensions.code).</summary>
public static class ErrorCodes
{
    public const string NotFound = "NOT_FOUND";
    public const string Validation = "VALIDATION";
    public const string Unavailable = "UNAVAILABLE";
    public const string Forbidden = "FORBIDDEN";
}

/// <summary>Maps failures of downstream services to GraphQL errors with stable codes; internals are never leaked.</summary>
public sealed class DownstreamErrorFilter : IErrorFilter
{
    public IError OnError(IError error)
    {
        var exception = error.Exception;
        while (exception is AggregateException { InnerExceptions.Count: 1 } aggregate)
        {
            exception = aggregate.InnerExceptions[0];
        }

        (string Code, string Message)? mapped = exception switch
        {
            DownstreamException d => Map(d.StatusCode),
            HttpRequestException or TaskCanceledException or TimeoutException => (ErrorCodes.Unavailable, "A service is temporarily unavailable."),
            _ => null,
        };

        return mapped is { } m
            ? ErrorBuilder.FromError(error).SetMessage(m.Message).SetCode(m.Code).SetException(null).Build()
            : error;
    }

    private static (string Code, string Message) Map(HttpStatusCode status) => status switch
    {
        HttpStatusCode.NotFound => (ErrorCodes.NotFound, "The requested resource was not found."),
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => (ErrorCodes.Forbidden, "The operation is not allowed."),
        HttpStatusCode.TooManyRequests => (ErrorCodes.Unavailable, "A service is temporarily unavailable."),
        >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError => (ErrorCodes.Validation, "The request was rejected as invalid."),
        _ => (ErrorCodes.Unavailable, "A service is temporarily unavailable."),
    };
}

/// <summary>Argument validation shared by the resolvers.</summary>
public static class Args
{
    public const int MaxPageSize = 50;

    /// <summary>Validated page size: between 1 and <see cref="MaxPageSize"/>, or the default when not given.</summary>
    public static int Limit(int? limit, int defaultValue)
    {
        var value = limit ?? defaultValue;
        if (value is < 1 or > MaxPageSize)
        {
            throw Validation($"limit must be between 1 and {MaxPageSize}.");
        }

        return value;
    }

    public static GraphQLException Validation(string message) =>
        new(ErrorBuilder.New().SetMessage(message).SetCode(ErrorCodes.Validation).Build());

    public static GraphQLException NotFound(string message) =>
        new(ErrorBuilder.New().SetMessage(message).SetCode(ErrorCodes.NotFound).Build());
}
