using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace Agentic.Blocks.Outbox;

/// <summary>Registration extensions for the outbox.</summary>
public static class OutboxExtensions
{
    /// <summary>Registers the outbox, the in-memory store (replace <see cref="IOutboxStore"/> for production) and the dispatcher.</summary>
    public static IServiceCollection AddBlocksOutbox(this IServiceCollection services, Action<OutboxOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var builder = services.AddOptions<OutboxOptions>()
            .Validate(o => o.MaxAttempts >= 1 && o.BatchSize >= 1 && o.BaseDelay >= TimeSpan.Zero && o.JitterFactor is >= 0 and <= 1,
                "Outbox options are invalid.");
        if (configure is not null) builder.Configure(configure);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IOutboxStore, InMemoryOutboxStore>();
        services.TryAddSingleton(GetOrAddRegistry(services));
        services.TryAddSingleton<OutboxSignal>();
        services.TryAddSingleton<OutboxMetrics>();
        services.TryAddScoped<IOutbox>(sp => ActivatorUtilities.CreateInstance<OutboxSession>(sp));
        services.TryAddSingleton(sp => new OutboxProcessor(
            sp.GetRequiredService<IOutboxStore>(), sp.GetRequiredService<IServiceScopeFactory>(), sp.GetRequiredService<OutboxTypeRegistry>(),
            sp.GetRequiredService<IOptions<OutboxOptions>>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<OutboxProcessor>>(), sp.GetRequiredService<OutboxMetrics>()));
        services.AddHostedService<OutboxDispatcher>();
        return services;
    }

    /// <summary>
    /// Registers a handler for <typeparamref name="TMessage"/> and maps its logical type name. Give a stable
    /// <paramref name="typeName"/> (for example <c>orders.placed.v1</c>) so renaming the class never strands messages.
    /// </summary>
    public static IServiceCollection AddOutboxHandler<TMessage, THandler>(this IServiceCollection services, string? typeName = null)
        where THandler : class, IOutboxHandler<TMessage>
    {
        ArgumentNullException.ThrowIfNull(services);
        GetOrAddRegistry(services).Map<TMessage>(typeName);
        services.AddScoped<IOutboxHandler<TMessage>, THandler>();
        return services;
    }

    /// <summary>
    /// Makes each HTTP request a unit of work: staged messages are committed when the endpoint finishes with a status
    /// below 400 and discarded on errors or exceptions.
    /// </summary>
    public static IApplicationBuilder UseBlocksOutbox(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.Use(async (context, next) =>
        {
            var outbox = context.RequestServices.GetRequiredService<IOutbox>();
            try
            {
                await next(context).ConfigureAwait(false);
            }
            catch
            {
                outbox.Discard();
                throw;
            }

            if (context.Response.StatusCode < StatusCodes.Status400BadRequest) await outbox.CommitAsync(context.RequestAborted).ConfigureAwait(false);
            else outbox.Discard();
        });
    }

    private static OutboxTypeRegistry GetOrAddRegistry(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(d => d.ServiceType == typeof(OutboxTypeRegistry))?.ImplementationInstance as OutboxTypeRegistry;
        if (existing is not null) return existing;
        var registry = new OutboxTypeRegistry();
        services.AddSingleton(registry);
        return registry;
    }
}
