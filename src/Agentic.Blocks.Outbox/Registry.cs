using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace Agentic.Blocks.Outbox;

/// <summary>Maps logical type names to CLR types and knows how to invoke their handlers.</summary>
public sealed class OutboxTypeRegistry
{
    private readonly Dictionary<string, Type> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<Type, string> _byType = [];

    /// <summary>Maps <typeparamref name="TMessage"/> to <paramref name="name"/> (default: the type's full name).</summary>
    /// <exception cref="InvalidOperationException">When the name is already used by another type.</exception>
    public void Map<TMessage>(string? name = null)
    {
        var type = typeof(TMessage);
        var logical = name ?? type.FullName ?? type.Name;
        if (_byName.TryGetValue(logical, out var existing) && existing != type)
        {
            throw new InvalidOperationException($"Outbox type name '{logical}' is already mapped to {existing}.");
        }

        if (_byType.TryGetValue(type, out var oldName) && oldName != logical)
        {
            throw new InvalidOperationException($"{type} is already mapped to '{oldName}'.");
        }

        _byName[logical] = type;
        _byType[type] = logical;
    }

    /// <summary>The logical name used when enqueuing <paramref name="type"/>.</summary>
    public string NameOf(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return _byType.TryGetValue(type, out var n) ? n : type.FullName ?? type.Name;
    }

    /// <summary>Resolves a logical name, or null when unknown.</summary>
    public Type? TypeOf(string name) => _byName.GetValueOrDefault(name);

    internal static async Task<int> InvokeHandlersAsync(IServiceProvider services, Type messageType, string payload, OutboxMessageContext context, CancellationToken ct)
    {
        var invoker = (IHandlerInvoker)Activator.CreateInstance(typeof(HandlerInvoker<>).MakeGenericType(messageType))!;
        return await invoker.InvokeAsync(services, payload, context, ct).ConfigureAwait(false);
    }

    private interface IHandlerInvoker
    {
        Task<int> InvokeAsync(IServiceProvider services, string payload, OutboxMessageContext context, CancellationToken ct);
    }

    private sealed class HandlerInvoker<T> : IHandlerInvoker
    {
        public async Task<int> InvokeAsync(IServiceProvider services, string payload, OutboxMessageContext context, CancellationToken ct)
        {
            var handlers = services.GetServices<IOutboxHandler<T>>().ToList();
            if (handlers.Count == 0) return 0;
            var message = JsonSerializer.Deserialize<T>(payload, OutboxJson.Options) ?? throw new JsonException("Payload deserialized to null.");
            foreach (var handler in handlers)
            {
                await handler.HandleAsync(message, context, ct).ConfigureAwait(false);
            }

            return handlers.Count;
        }
    }
}
