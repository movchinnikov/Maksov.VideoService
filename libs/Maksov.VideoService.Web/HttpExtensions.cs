using System.Security.Claims;
using Maksov.VideoService.Primitives;

namespace Maksov.VideoService.Web;

public static class HttpExtensions
{
    /// <summary>Resource owner — only <c>sub</c> from the validated token, never from the request body.</summary>
    public static string UserId(this ClaimsPrincipal user) =>
        user.FindFirst("sub")?.Value ?? throw new InvalidOperationException("Authenticated user has no 'sub' claim");

    /// <summary>Display login: preferred_username from the Keycloak token (fallback — sub).</summary>
    public static string Username(this ClaimsPrincipal user) =>
        user.FindFirst("preferred_username")?.Value ?? user.UserId();

    /// <summary>Business error → Problem Details. Someone else's video is indistinguishable from a missing one (404).</summary>
    public static IResult ToProblem(this DomainError error) => error.Kind switch
    {
        ErrorKind.Validation => TypedResults.ValidationProblem(
            new Dictionary<string, string[]> { [error.Code] = [error.Message] }),
        ErrorKind.NotFound => TypedResults.Problem(statusCode: StatusCodes.Status404NotFound, title: error.Message, type: error.Code),
        ErrorKind.Conflict => TypedResults.Problem(statusCode: StatusCodes.Status409Conflict, title: error.Message, type: error.Code),
        _ => TypedResults.Problem(statusCode: StatusCodes.Status500InternalServerError),
    };

    public static IResult ToValidationProblem(this IReadOnlyList<DomainError> errors) =>
        TypedResults.ValidationProblem(errors
            .GroupBy(static e => e.Code)
            .ToDictionary(static g => g.Key, static g => g.Select(static e => e.Message).ToArray()));
}
