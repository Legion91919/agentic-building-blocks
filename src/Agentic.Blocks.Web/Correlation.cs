using System.Diagnostics;
using Agentic.Blocks.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Agentic.Blocks.Web;

/// <summary>Holds the correlation id of the current async flow (HTTP request or background message).</summary>
public sealed class CorrelationContext : ICorrelationContext
{
    private static readonly AsyncLocal<string?> Current = new();

    /// <summary>HTTP header used to read and return the correlation id.</summary>
    public const string HeaderName = "X-Correlation-Id";

    /// <summary>Name of the logging-scope property.</summary>
    public const string LogProperty = "CorrelationId";

    /// <inheritdoc />
    public string? CorrelationId => Current.Value;

    /// <summary>Sets the correlation id for the current async flow and returns a handle that restores the previous one.</summary>
    public static IDisposable Begin(string correlationId)
    {
        var previous = Current.Value;
        Current.Value = correlationId;
        return new Restore(previous);
    }

    /// <summary>True when the value is safe to echo and log: 1-128 chars of letters, digits, '-', '_', '.', ':'.</summary>
    public static bool IsValid(string? value) =>
        !string.IsNullOrEmpty(value) && value.Length <= 128 &&
        value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' or ':');

    private sealed class Restore(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}

/// <summary>
/// Reads <c>X-Correlation-Id</c> (or creates one), returns it on the response, opens a logging scope with it and
/// tags the current activity. Invalid incoming values are replaced, never echoed.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    /// <summary>Runs the middleware.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var incoming = context.Request.Headers[CorrelationContext.HeaderName].ToString();
        var id = CorrelationContext.IsValid(incoming)
            ? incoming
            : Activity.Current?.TraceId.ToHexString() ?? Guid.NewGuid().ToString("N");
        if (!string.IsNullOrEmpty(incoming) && !CorrelationContext.IsValid(incoming)) LogInvalid(logger);

        context.Items[CorrelationContext.LogProperty] = id;
        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationContext.HeaderName] = id;
            return Task.CompletedTask;
        });
        Activity.Current?.SetTag("correlation.id", id);
        using (CorrelationContext.Begin(id))
        using (logger.BeginScope(new Dictionary<string, object> { [CorrelationContext.LogProperty] = id }))
        {
            await next(context).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Ignored an invalid X-Correlation-Id header and generated a new id.")]
    private static partial void LogInvalid(ILogger logger);
}
