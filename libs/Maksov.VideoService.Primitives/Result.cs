namespace Maksov.VideoService.Primitives;

public enum ErrorKind
{
    Validation,
    NotFound,
    Conflict,
}

public sealed record DomainError(ErrorKind Kind, string Code, string Message)
{
    public static DomainError Validation(string code, string message) => new(ErrorKind.Validation, code, message);
    public static DomainError NotFound(string code = "not_found", string message = "Resource not found") => new(ErrorKind.NotFound, code, message);
    public static DomainError Conflict(string code, string message) => new(ErrorKind.Conflict, code, message);
}

/// <summary>Use-case result: a value or a business error. Exceptions are only for exceptional cases.</summary>
public readonly struct Result<T>
{
    private Result(T? value, DomainError? error)
    {
        Value = value;
        Error = error;
    }

    public T? Value { get; }
    public DomainError? Error { get; }
    public bool IsSuccess => Error is null;

#pragma warning disable CA2225 // implicit conversions — sugar for handlers
    public static implicit operator Result<T>(T value) => new(value, null);
    public static implicit operator Result<T>(DomainError error) => new(default, error);
#pragma warning restore CA2225
}
