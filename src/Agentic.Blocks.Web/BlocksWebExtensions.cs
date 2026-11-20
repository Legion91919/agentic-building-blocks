using System.Globalization;
using System.Security.Claims;
using System.Threading.RateLimiting;
using Agentic.Blocks.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace Agentic.Blocks.Web;

/// <summary>Names of the rate-limit policies registered by <see cref="BlocksWebExtensions.AddBlocksWeb"/>.</summary>
public static class BlocksRateLimitPolicies
{
    /// <summary>Strict, per client IP: for login, register and token refresh (brute-force protection).</summary>
    public const string Auth = "blocks-auth";

    /// <summary>General API traffic, per user when signed in, otherwise per client IP.</summary>
    public const string Api = "blocks-api";

    /// <summary>Inbound webhooks, per client IP.</summary>
    public const string Webhooks = "blocks-webhooks";
}

/// <summary>Health-check tags used by <c>/health/live</c> and <c>/health/ready</c>.</summary>
public static class BlocksHealthTags
{
    /// <summary>Checks that only prove the process is running.</summary>
    public const string Live = "live";

    /// <summary>Checks that prove dependencies are reachable. Add yours with <c>.AddCheck(..., tags: [BlocksHealthTags.Ready])</c>.</summary>
    public const string Ready = "ready";
}

/// <summary>Options for <see cref="BlocksWebExtensions.AddBlocksWeb"/>.</summary>
public sealed class BlocksWebOptions
{
    /// <summary>Requests per window for <see cref="BlocksRateLimitPolicies.Auth"/>. Default 10.</summary>
    public int AuthPermitLimit { get; set; } = 10;

    /// <summary>Window for <see cref="BlocksRateLimitPolicies.Auth"/>. Default 1 minute.</summary>
    public TimeSpan AuthWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Requests per window for <see cref="BlocksRateLimitPolicies.Api"/>. Default 120.</summary>
    public int ApiPermitLimit { get; set; } = 120;

    /// <summary>Window for <see cref="BlocksRateLimitPolicies.Api"/>. Default 1 minute.</summary>
    public TimeSpan ApiWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Requests per window for <see cref="BlocksRateLimitPolicies.Webhooks"/>. Default 300.</summary>
    public int WebhookPermitLimit { get; set; } = 300;

    /// <summary>Paths of the health endpoints. Defaults <c>/health/live</c> and <c>/health/ready</c>.</summary>
    public string LivePath { get; set; } = "/health/live";

    /// <summary>Path of the readiness endpoint.</summary>
    public string ReadyPath { get; set; } = "/health/ready";
}

/// <summary>Marker that proves <see cref="BlocksWebExtensions.AddBlocksWeb"/> was called (other blocks check it).</summary>
public sealed class BlocksWebMarker;

/// <summary>Registration and pipeline extensions for the Web block.</summary>
public static class BlocksWebExtensions
{
    /// <summary>
    /// Registers ProblemDetails (with <c>traceId</c> and <c>correlationId</c>), health checks, the rate-limit
    /// policies in <see cref="BlocksRateLimitPolicies"/>, <see cref="ICorrelationContext"/> and a <see cref="TimeProvider"/>.
    /// </summary>
    public static IServiceCollection AddBlocksWeb(this IServiceCollection services, Action<BlocksWebOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var optionsBuilder = services.AddOptions<BlocksWebOptions>();
        if (configure is not null) optionsBuilder.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ICorrelationContext, CorrelationContext>();
        services.TryAddSingleton<BlocksWebMarker>();

        services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
        {
            ctx.ProblemDetails.Instance ??= ctx.HttpContext.Request.Path;
            ctx.ProblemDetails.Extensions.TryAdd("traceId", System.Diagnostics.Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier);
            if (ctx.HttpContext.Items.TryGetValue(CorrelationContext.LogProperty, out var id) && id is not null)
            {
                ctx.ProblemDetails.Extensions.TryAdd("correlationId", id);
            }
        });

        services.AddHealthChecks().AddCheck("self", () => HealthCheckResult.Healthy(), tags: [BlocksHealthTags.Live]);
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<BlocksWebOptions>>((o, web) => ConfigureRateLimits(o, web.Value));
        return services;
    }

    /// <summary>
    /// Adds the correlation middleware, the ProblemDetails exception handler and status-code pages, the rate limiter,
    /// and maps <c>/health/live</c> and <c>/health/ready</c>. Call it early, before your endpoints.
    /// </summary>
    public static WebApplication UseBlocksWeb(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var options = app.Services.GetRequiredService<IOptions<BlocksWebOptions>>().Value;
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseRateLimiter();
        app.MapHealthChecks(options.LivePath, new HealthCheckOptions { Predicate = c => c.Tags.Contains(BlocksHealthTags.Live) })
            .DisableRateLimiting();
        app.MapHealthChecks(options.ReadyPath, new HealthCheckOptions { Predicate = c => c.Tags.Contains(BlocksHealthTags.Ready) || c.Tags.Contains(BlocksHealthTags.Live) })
            .DisableRateLimiting();
        return app;
    }

    /// <summary>Throws a helpful error when a block that needs the Web block is used without <see cref="AddBlocksWeb"/>.</summary>
    public static void EnsureBlocksWeb(this IServiceProvider services, string block)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (services.GetService<BlocksWebMarker>() is null)
        {
            throw new InvalidOperationException($"{block} needs the Web block. Call builder.Services.AddBlocksWeb() and app.UseBlocksWeb().");
        }
    }

    private static void ConfigureRateLimits(RateLimiterOptions o, BlocksWebOptions web)
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.OnRejected = async (ctx, ct) =>
        {
            if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retry))
            {
                ctx.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retry.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
            }

            var problem = Error.Failure("rate_limit.exceeded", "Too many requests. Slow down and retry later.");
            await Results.Problem(detail: problem.Message, statusCode: StatusCodes.Status429TooManyRequests, title: "Too many requests.",
                    extensions: new Dictionary<string, object?> { ["code"] = problem.Code })
                .ExecuteAsync(ctx.HttpContext).ConfigureAwait(false);
        };

        o.AddPolicy(BlocksRateLimitPolicies.Auth, http => RateLimitPartition.GetFixedWindowLimiter(
            "ip:" + ClientIp(http),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = web.AuthPermitLimit, Window = web.AuthWindow, QueueLimit = 0 }));
        o.AddPolicy(BlocksRateLimitPolicies.Api, http => RateLimitPartition.GetTokenBucketLimiter(
            http.User.FindFirstValue(ClaimTypes.NameIdentifier) is { } user ? "user:" + user : "ip:" + ClientIp(http),
            _ => new TokenBucketRateLimiterOptions
            {
                TokenLimit = web.ApiPermitLimit,
                TokensPerPeriod = web.ApiPermitLimit,
                ReplenishmentPeriod = web.ApiWindow,
                QueueLimit = 0,
                AutoReplenishment = true,
            }));
        o.AddPolicy(BlocksRateLimitPolicies.Webhooks, http => RateLimitPartition.GetFixedWindowLimiter(
            "ip:" + ClientIp(http),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = web.WebhookPermitLimit, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
    }

    private static string ClientIp(HttpContext http) => http.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

/// <summary>Helpers for reading identity claims without depending on a specific identity provider.</summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>Claim type for the tenant id.</summary>
    public const string TenantClaim = "tenant_id";

    /// <summary>The user id from <see cref="ClaimTypes.NameIdentifier"/> or the OIDC <c>sub</c> claim, or null.</summary>
    public static string? GetUserId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (principal.Identity?.IsAuthenticated != true) return null;
        return principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("sub");
    }

    /// <summary>The tenant id claim, or null.</summary>
    public static string? GetTenantId(this ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.FindFirstValue(TenantClaim);
    }
}
