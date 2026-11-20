using System.Security.Cryptography;
using System.Text;
using Agentic.Blocks.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agentic.Blocks.Idempotency;

/// <summary>Options for idempotent endpoints.</summary>
public sealed class IdempotencyOptions
{
    /// <summary>Request header carrying the key. Default <c>Idempotency-Key</c>.</summary>
    public string HeaderName { get; set; } = "Idempotency-Key";

    /// <summary>How long a completed response is replayed. Default 24 hours.</summary>
    public TimeSpan Expiry { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How long a reservation blocks the key while the first request runs. Default 1 minute.</summary>
    public TimeSpan InProgressTimeout { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Longest accepted key. Default 255.</summary>
    public int MaxKeyLength { get; set; } = 255;

    /// <summary>Response headers stored and replayed besides Content-Type. Default <c>Location</c>.</summary>
    public IList<string> ReplayedHeaders { get; } = ["Location"];
}

/// <summary>Endpoint metadata marking an endpoint as idempotent.</summary>
public sealed class IdempotentEndpointMetadata;

/// <summary>Extensions to register and use idempotency.</summary>
public static class IdempotencyExtensions
{
    /// <summary>Header set on replayed responses.</summary>
    public const string ReplayedHeader = "Idempotency-Replayed";

    /// <summary>Registers options and the in-memory store (replace <see cref="IIdempotencyStore"/> for production).</summary>
    public static IServiceCollection AddBlocksIdempotency(this IServiceCollection services, Action<IdempotencyOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var builder = services.AddOptions<IdempotencyOptions>();
        if (configure is not null) builder.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        return services;
    }

    /// <summary>
    /// Requires an <c>Idempotency-Key</c> header. A retry with the same key and body gets the stored response
    /// (header <c>Idempotency-Replayed: true</c>); the same key with a different body gets 409; no key gets 400.
    /// Keys are scoped per user (or anonymous) so two users cannot collide.
    /// </summary>
    public static TBuilder RequireIdempotencyKey<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpoint => endpoint.Metadata.Add(new IdempotentEndpointMetadata()));
        builder.AddEndpointFilter(new IdempotencyFilter());
        // Body binding runs before endpoint filters; buffer the body first so the filter can hash the raw bytes.
        builder.Finally(endpoint =>
        {
            var inner = endpoint.RequestDelegate;
            if (inner is null) return;
            endpoint.RequestDelegate = context =>
            {
                context.Request.EnableBuffering();
                return inner(context);
            };
        });
        return builder;
    }

    /// <summary>Computes the request fingerprint: SHA-256 over method, path and raw body.</summary>
    public static async Task<string> ComputeRequestHashAsync(HttpRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{request.Method}\n{request.Path}{request.QueryString}\n"));
        if (request.Body.CanSeek)
        {
            request.Body.Position = 0;
            var buffer = new byte[8192];
            int read;
            while ((read = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                hash.AppendData(buffer, 0, read);
            }

            request.Body.Position = 0;
        }
        else if (request.ContentLength is > 0)
        {
            throw new InvalidOperationException("The request body was consumed before the idempotency filter ran; use RequireIdempotencyKey() on the endpoint.");
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

internal sealed class IdempotencyFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var options = http.RequestServices.GetRequiredService<IOptions<IdempotencyOptions>>().Value;
        var store = http.RequestServices.GetRequiredService<IIdempotencyStore>();
        var time = http.RequestServices.GetRequiredService<TimeProvider>();

        var key = http.Request.Headers[options.HeaderName].ToString();
        if (string.IsNullOrWhiteSpace(key))
        {
            return Problem(Error.Validation("idempotency.key_missing", $"The {options.HeaderName} header is required for this request."));
        }

        if (key.Length > options.MaxKeyLength)
        {
            return Problem(Error.Validation("idempotency.key_invalid", $"The {options.HeaderName} header must be at most {options.MaxKeyLength} characters."));
        }

        var hash = await IdempotencyExtensions.ComputeRequestHashAsync(http.Request, http.RequestAborted).ConfigureAwait(false);
        var user = http.User.Identity?.IsAuthenticated == true
            ? http.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? http.User.FindFirst("sub")?.Value ?? "authenticated"
            : "anonymous";
        var scopedKey = $"{user}:{key}";
        var now = time.GetUtcNow();

        var existing = await store.TryBeginAsync(scopedKey, hash, now + options.InProgressTimeout, http.RequestAborted).ConfigureAwait(false);
        if (existing is not null)
        {
            if (!string.Equals(existing.RequestHash, hash, StringComparison.Ordinal))
            {
                return Problem(Error.Conflict("idempotency.key_reused", "This idempotency key was already used with a different request."));
            }

            if (existing.Response is null)
            {
                http.Response.Headers.RetryAfter = "1";
                return Problem(Error.Conflict("idempotency.in_progress", "A request with this idempotency key is still being processed. Retry shortly."));
            }

            return new ReplayResult(existing.Response);
        }

        IdempotentResponse captured;
        try
        {
            var result = await next(context).ConfigureAwait(false);
            captured = await CaptureAsync(http, result, options).ConfigureAwait(false);
        }
        catch
        {
            await store.ReleaseAsync(scopedKey, CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (captured.StatusCode >= 500)
        {
            await store.ReleaseAsync(scopedKey, CancellationToken.None).ConfigureAwait(false);
        }
        else
        {
            await store.CompleteAsync(scopedKey, hash, captured, time.GetUtcNow() + options.Expiry, CancellationToken.None).ConfigureAwait(false);
        }

        return Results.Empty;
    }

    private static async Task<IdempotentResponse> CaptureAsync(HttpContext http, object? result, IdempotencyOptions options)
    {
        var original = http.Response.Body;
        using var buffer = new MemoryStream();
        http.Response.Body = buffer;
        try
        {
            var asResult = result switch
            {
                IResult r => r,
                null => Results.Empty,
                string s => Results.Text(s),
                _ => Results.Json(result),
            };
            await asResult.ExecuteAsync(http).ConfigureAwait(false);
        }
        finally
        {
            http.Response.Body = original;
        }

        buffer.Position = 0;
        await buffer.CopyToAsync(original, http.RequestAborted).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in options.ReplayedHeaders)
        {
            if (http.Response.Headers.TryGetValue(name, out var value)) headers[name] = value.ToString();
        }

        return new IdempotentResponse(http.Response.StatusCode, http.Response.ContentType, buffer.ToArray(), headers);
    }

    private static IResult Problem(Error error)
    {
        var status = error.Type == ErrorType.Conflict ? StatusCodes.Status409Conflict : StatusCodes.Status400BadRequest;
        return Results.Problem(detail: error.Message, statusCode: status, extensions: new Dictionary<string, object?> { ["code"] = error.Code });
    }

    private sealed class ReplayResult(IdempotentResponse response) : IResult
    {
        public async Task ExecuteAsync(HttpContext httpContext)
        {
            httpContext.Response.StatusCode = response.StatusCode;
            if (response.ContentType is not null) httpContext.Response.ContentType = response.ContentType;
            foreach (var (name, value) in response.Headers) httpContext.Response.Headers[name] = value;
            httpContext.Response.Headers[IdempotencyExtensions.ReplayedHeader] = "true";
            httpContext.Response.ContentLength = response.Body.Length;
            await httpContext.Response.Body.WriteAsync(response.Body, httpContext.RequestAborted).ConfigureAwait(false);
        }
    }
}
