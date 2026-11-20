using Agentic.Blocks.Core;
using Agentic.Blocks.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Agentic.Blocks.Audit;

/// <summary>Registration and endpoint extensions for the audit log.</summary>
public static class AuditExtensions
{
    /// <summary>Registers <see cref="IAuditLog"/> and the in-memory store (replace <see cref="IAuditStore"/> for production).</summary>
    public static IServiceCollection AddBlocksAudit(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAuditStore, InMemoryAuditStore>();
        services.TryAddSingleton<IAuditLog>(sp => new AuditLog(sp.GetRequiredService<IAuditStore>(), sp.GetRequiredService<TimeProvider>(), sp.GetService<ICorrelationContext>()));
        return services;
    }

    /// <summary>
    /// Maps read-only audit endpoints for users in <paramref name="requiredRole"/>:
    /// <c>GET {prefix}/entities/{type}/{id}</c>, <c>GET {prefix}/actors/{actor}</c>, <c>GET {prefix}/verify</c>.
    /// There are no write endpoints: entries are written by application code through <see cref="IAuditLog"/>.
    /// </summary>
    public static RouteGroupBuilder MapBlocksAudit(this IEndpointRouteBuilder endpoints, string prefix = "/audit", string requiredRole = "admin")
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var group = endpoints.MapGroup(prefix).WithTags("Audit").RequireAuthorization(p => p.RequireRole(requiredRole));
        group.MapGet("/entities/{entityType}/{entityId}", async (string entityType, string entityId, string? cursor, int? limit, IAuditLog log, CancellationToken ct) =>
            (await log.GetByEntityAsync(entityType, entityId, new PageRequest(cursor, limit), ct).ConfigureAwait(false)).ToHttpResult());
        group.MapGet("/actors/{actor}", async (string actor, string? cursor, int? limit, IAuditLog log, CancellationToken ct) =>
            (await log.GetByActorAsync(actor, new PageRequest(cursor, limit), ct).ConfigureAwait(false)).ToHttpResult());
        group.MapGet("/verify", async (IAuditLog log, CancellationToken ct) =>
        {
            var result = await log.VerifyIntegrityAsync(ct).ConfigureAwait(false);
            return result.IsSuccess ? Results.Ok(new { intact = true }) : result.Error.ToProblem();
        });
        return group;
    }
}
