using Agentic.Blocks.Core;
using Microsoft.AspNetCore.Http;

namespace Agentic.Blocks.Web;

/// <summary>Maps <see cref="Error"/> and <see cref="Result"/> to minimal-API results with RFC 9457 ProblemDetails.</summary>
public static class ErrorHttpMapping
{
    /// <summary>The HTTP status code for an error type.</summary>
    public static int StatusCodeFor(ErrorType type) => type switch
    {
        ErrorType.Validation => StatusCodes.Status400BadRequest,
        ErrorType.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorType.PaymentRequired => StatusCodes.Status402PaymentRequired,
        ErrorType.Forbidden => StatusCodes.Status403Forbidden,
        ErrorType.NotFound => StatusCodes.Status404NotFound,
        ErrorType.Conflict => StatusCodes.Status409Conflict,
        _ => StatusCodes.Status500InternalServerError,
    };

    /// <summary>Short title for an error type.</summary>
    public static string TitleFor(ErrorType type) => type switch
    {
        ErrorType.Validation => "The request is invalid.",
        ErrorType.Unauthorized => "Authentication is required.",
        ErrorType.PaymentRequired => "A subscription or payment is required.",
        ErrorType.Forbidden => "You are not allowed to do this.",
        ErrorType.NotFound => "The resource was not found.",
        ErrorType.Conflict => "The request conflicts with the current state.",
        _ => "An unexpected error occurred.",
    };

    /// <summary>
    /// Converts an error to a ProblemDetails result. The stable code goes in the <c>code</c> extension, metadata
    /// entries become extensions, and validation errors use the <c>errors</c> dictionary.
    /// </summary>
    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = error.Code };
        foreach (var (key, value) in error.Metadata) extensions[key] = value;
        var status = StatusCodeFor(error.Type);
        if (error.Type == ErrorType.Validation && error.FieldErrors.Count > 0)
        {
            return Results.ValidationProblem(
                error.FieldErrors.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                detail: error.Message, title: TitleFor(error.Type), extensions: extensions);
        }

        return Results.Problem(detail: error.Message, statusCode: status, title: TitleFor(error.Type), extensions: extensions);
    }

    /// <summary>204 No Content on success, ProblemDetails on failure.</summary>
    public static IResult ToHttpResult(this Result result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? Results.NoContent() : result.Error.ToProblem();
    }

    /// <summary>200 OK with the value on success, ProblemDetails on failure.</summary>
    public static IResult ToHttpResult<T>(this Result<T> result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return result.IsSuccess ? Results.Ok(result.Value) : result.Error.ToProblem();
    }

    /// <summary>Custom success mapping (for example 201 Created), ProblemDetails on failure.</summary>
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.Value) : result.Error.ToProblem();
    }
}
