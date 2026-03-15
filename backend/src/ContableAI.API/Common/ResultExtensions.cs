using ContableAI.Application.Common;

namespace ContableAI.API.Common;

/// <summary>
/// Translates a <see cref="Result{T}"/> to an ASP.NET Core <see cref="IResult"/>
/// following the HTTP semantics of each status code.
/// </summary>
public static class ResultExtensions
{
    private static string TitleForStatusCode(int statusCode) => statusCode switch
    {
        400 => "Bad Request",
        401 => "Unauthorized",
        402 => "Payment Required",
        403 => "Forbidden",
        404 => "Not Found",
        409 => "Conflict",
        422 => "Unprocessable Entity",
        _   => "Request Failed",
    };

    public static IResult ToHttpResult<T>(this Result<T> result)
    {
        if (result.IsSuccess)
            return Results.Ok(result.Value);

        if (result.StatusCode == 402)
        {
            var code = result.Error?.Split('|').FirstOrDefault();
            var message = result.Error?.Split('|').ElementAtOrDefault(1) ?? result.Error;
            return Results.Problem(
                title: TitleForStatusCode(402),
                detail: message,
                statusCode: 402,
                extensions: code is null ? null : new Dictionary<string, object?> { ["code"] = code });
        }

        return Results.Problem(
            title: TitleForStatusCode(result.StatusCode),
            detail: result.Error,
            statusCode: result.StatusCode);
    }

    public static IResult ToCreatedResult<T>(this Result<T> result, string? location)
    {
        if (result.IsSuccess)
            return Results.Created(location, result.Value);

        return result.ToHttpResult();
    }
}
