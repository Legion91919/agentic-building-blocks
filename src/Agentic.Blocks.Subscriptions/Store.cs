namespace Agentic.Blocks.Subscriptions;

/// <summary>A pending checkout.</summary>
/// <param name="SessionId">Provider session id.</param>
/// <param name="UserId">User.</param>
/// <param name="PlanId">Plan.</param>
/// <param name="ExpiresAt">Expiry.</param>
public sealed record PendingCheckout(string SessionId, string UserId, string PlanId, DateTimeOffset ExpiresAt);

/// <summary>Billing persistence port: subscriptions, pending checkouts and processed webhook ids.</summary>
public interface ISubscriptionStore
{
    /// <summary>Reads a subscription.</summary>
    Task<Subscription?> GetAsync(string id, CancellationToken cancellationToken = default);

    /// <summary>The user's newest non-terminal subscription, if any.</summary>
    Task<Subscription?> FindCurrentForUserAsync(string userId, CancellationToken cancellationToken = default);

    /// <summary>All non-terminal subscriptions (for the time-driven refresher).</summary>
    Task<IReadOnlyList<Subscription>> ListOpenAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates a subscription.</summary>
    Task CreateAsync(Subscription subscription, CancellationToken cancellationToken = default);

    /// <summary>Saves when the version matches; returns false on a race.</summary>
    Task<bool> TryUpdateAsync(Subscription subscription, CancellationToken cancellationToken = default);

    /// <summary>Stores a pending checkout.</summary>
    Task SaveCheckoutAsync(PendingCheckout checkout, CancellationToken cancellationToken = default);

    /// <summary>Reads a pending checkout.</summary>
    Task<PendingCheckout?> GetCheckoutAsync(string sessionId, CancellationToken cancellationToken = default);

    /// <summary>Records a provider event id; returns false when it was already recorded (duplicate delivery).</summary>
    Task<bool> TryMarkEventProcessedAsync(string provider, string eventId, CancellationToken cancellationToken = default);

    /// <summary>Forgets an event id so the provider's retry is processed (used when processing failed).</summary>
    Task UnmarkEventAsync(string provider, string eventId, CancellationToken cancellationToken = default);
}

/// <summary>Thread-safe in-memory billing store.</summary>
public sealed class InMemorySubscriptionStore : ISubscriptionStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Subscription> _subs = new(StringComparer.Ordinal);
    private readonly List<string> _order = [];
    private readonly Dictionary<string, PendingCheckout> _checkouts = new(StringComparer.Ordinal);
    private readonly HashSet<string> _events = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public Task<Subscription?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate) { return Task.FromResult(_subs.GetValueOrDefault(id)); }
    }

    /// <inheritdoc />
    public Task<Subscription?> FindCurrentForUserAsync(string userId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(Enumerable.Reverse(_order).Select(id => _subs[id]).FirstOrDefault(s => s.UserId == userId && !s.IsTerminal));
        }
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<Subscription>> ListOpenAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate) { return Task.FromResult<IReadOnlyList<Subscription>>(_subs.Values.Where(s => !s.IsTerminal).ToList()); }
    }

    /// <inheritdoc />
    public Task CreateAsync(Subscription subscription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        lock (_gate)
        {
            if (!_subs.TryAdd(subscription.Id, subscription with { Version = 1 })) throw new InvalidOperationException("Duplicate subscription id.");
            _order.Add(subscription.Id);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<bool> TryUpdateAsync(Subscription subscription, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        lock (_gate)
        {
            if (!_subs.TryGetValue(subscription.Id, out var current) || current.Version != subscription.Version) return Task.FromResult(false);
            _subs[subscription.Id] = subscription with { Version = current.Version + 1 };
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task SaveCheckoutAsync(PendingCheckout checkout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(checkout);
        lock (_gate) { _checkouts[checkout.SessionId] = checkout; }
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<PendingCheckout?> GetCheckoutAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        lock (_gate) { return Task.FromResult(_checkouts.GetValueOrDefault(sessionId)); }
    }

    /// <inheritdoc />
    public Task<bool> TryMarkEventProcessedAsync(string provider, string eventId, CancellationToken cancellationToken = default)
    {
        lock (_gate) { return Task.FromResult(_events.Add(provider + ":" + eventId)); }
    }

    /// <inheritdoc />
    public Task UnmarkEventAsync(string provider, string eventId, CancellationToken cancellationToken = default)
    {
        lock (_gate) { _events.Remove(provider + ":" + eventId); }
        return Task.CompletedTask;
    }
}
