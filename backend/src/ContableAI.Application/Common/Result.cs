namespace ContableAI.Application.Common;

/// <summary>
/// Discriminated union that wraps either a successful value or a business-rule error.
/// Handlers return Result&lt;T&gt;; endpoints translate it to an HTTP response via
/// <c>ResultExtensions</c> (defined in the API project).
/// </summary>
public sealed class Result<T>
{
    public bool    IsSuccess   { get; private init; }
    public T?      Value       { get; private init; }
    public string? Error       { get; private init; }
    public int     StatusCode  { get; private init; }

    private Result() { }

    public static Result<T> Success(T value, int statusCode = 200) =>
        new() { IsSuccess = true, Value = value, StatusCode = statusCode };

    public static Result<T> Failure(string error, int statusCode = 400) =>
        new() { IsSuccess = false, Error = error, StatusCode = statusCode };

    public static Result<T> NotFound(string error = "Resource not found.") =>
        Failure(error, 404);

    public static Result<T> Conflict(string error) =>
        Failure(error, 409);

    public static Result<T> Forbidden(string error = "Access denied.") =>
        Failure(error, 403);

    public static Result<T> PaymentRequired(string code, string message) =>
        new() { IsSuccess = false, Error = $"{code}|{message}", StatusCode = 402 };
}

