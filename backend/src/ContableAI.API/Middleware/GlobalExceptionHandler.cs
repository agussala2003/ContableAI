using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ContableAI.API.Middleware;

/// <summary>
/// Global exception handler implementing RFC 7807 Problem Details.
/// Maps well-known exception types to structured HTTP responses.
/// Registered via <c>app.UseExceptionHandler()</c> in <c>Program.cs</c>.
/// </summary>
internal sealed class GlobalExceptionHandler : IExceptionHandler
{
    private readonly ILogger<GlobalExceptionHandler> _logger;

    public GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger)
        => _logger = logger;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext      httpContext,
        Exception        exception,
        CancellationToken cancellationToken)
    {
        var (statusCode, title, errors) = exception switch
        {
            ValidationException ve => (
                StatusCodes.Status400BadRequest,
                "Validation failed.",
                (object)ve.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(
                        g => g.Key,
                        g => g.Select(e => e.ErrorMessage).ToArray())),

            UnauthorizedAccessException => (
                StatusCodes.Status401Unauthorized,
                "Unauthorized.",
                (object)"You are not authorized to access this resource."),

            KeyNotFoundException => (
                StatusCodes.Status404NotFound,
                "Resource not found.",
                (object)"The requested resource could not be found."),

            _ => (
                StatusCodes.Status500InternalServerError,
                "An unexpected error occurred.",
                (object)"An error occurred while processing your request."),
        };

        if (statusCode >= 500)
            _logger.LogError(exception, "Unhandled exception: {Message}", exception.Message);
        else
            _logger.LogWarning(exception, "Handled exception ({StatusCode}): {Message}", statusCode, exception.Message);

        var problem = new ProblemDetails
        {
            Status   = statusCode,
            Title    = title,
            Type     = $"https://httpstatuses.io/{statusCode}",
            Instance = httpContext.Request.Path,
        };

        if (exception is ValidationException)
            problem.Extensions["errors"] = errors;
        else
            problem.Detail = errors?.ToString();

        httpContext.Response.StatusCode  = statusCode;
        httpContext.Response.ContentType = "application/problem+json";

        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
        return true;
    }
}
