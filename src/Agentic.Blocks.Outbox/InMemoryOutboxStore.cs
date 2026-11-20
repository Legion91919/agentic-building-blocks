namespace Agentic.Blocks.Outbox;

/// <summary>Thread-safe in-memory outbox store. Messages are lost on restart: use it for tests, demos and single-node prototypes.</summary>
public sealed class InMemoryOutboxStore : IOutboxStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, OutboxMessage> _messages = [];
    private readonly List<Guid> _order = [];

    /// <summary>A snapshot of every message, in enqueue order.</summary>
    public IReadOnlyList<OutboxMessage> Snapshot()
    {
        lock (_gate) { return _order.Select(id => _messages[id]).ToList(); }
    }

    /// <inheritdoc />
    public Task AppendAsync(IReadOnlyCollection<OutboxMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        lock (_gate)
        {
            if (messages.Any(m => _messages.ContainsKey(m.Id))) throw new InvalidOperationException("Duplicate outbox message id.");
            foreach (var m in messages)
            {
                _messages[m.Id] = m;
                _order.Add(m.Id);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<OutboxMessage>> LeaseDueAsync(DateTimeOffset now, int max, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
    {
        var leased = new List<OutboxMessage>();
        lock (_gate)
        {
            foreach (var id in _order)
            {
                if (leased.Count >= max) break;
                var m = _messages[id];
                var due = (m.Status == OutboxMessageStatus.Pending && m.NextAttemptAt <= now) ||
                          (m.Status == OutboxMessageStatus.Processing && m.LeasedUntil <= now);
                if (!due) continue;
                var updated = m with { Status = OutboxMessageStatus.Processing, LeasedUntil = now + leaseDuration };
                _messages[id] = updated;
                leased.Add(updated);
            }
        }

        return Task.FromResult<IReadOnlyList<OutboxMessage>>(leased);
    }

    /// <inheritdoc />
    public Task MarkSucceededAsync(Guid id, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        Update(id, m => m with { Status = OutboxMessageStatus.Succeeded, Attempts = m.Attempts + 1, CompletedAt = at, LeasedUntil = null, LastError = null });

    /// <inheritdoc />
    public Task MarkFailedAsync(Guid id, string reason, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default) =>
        Update(id, m => m with { Status = OutboxMessageStatus.Pending, Attempts = m.Attempts + 1, NextAttemptAt = nextAttemptAt, LeasedUntil = null, LastError = reason });

    /// <inheritdoc />
    public Task MarkDeadLetteredAsync(Guid id, string reason, DateTimeOffset at, CancellationToken cancellationToken = default) =>
        Update(id, m => m with { Status = OutboxMessageStatus.DeadLettered, Attempts = m.Attempts + 1, CompletedAt = at, LeasedUntil = null, LastError = reason });

    /// <inheritdoc />
    public Task<IReadOnlyList<OutboxMessage>> GetDeadLettersAsync(int max, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<OutboxMessage> list = _order.Select(id => _messages[id]).Where(m => m.Status == OutboxMessageStatus.DeadLettered).Take(max).ToList();
            return Task.FromResult(list);
        }
    }

    /// <inheritdoc />
    public Task<bool> RequeueAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_messages.TryGetValue(id, out var m) || m.Status != OutboxMessageStatus.DeadLettered) return Task.FromResult(false);
            _messages[id] = m with { Status = OutboxMessageStatus.Pending, Attempts = 0, NextAttemptAt = now, CompletedAt = null };
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public Task<OutboxMessage?> GetAsync(Guid id, CancellationToken cancellationToken = default)
    {
        lock (_gate) { return Task.FromResult(_messages.TryGetValue(id, out var m) ? m : null); }
    }

    private Task Update(Guid id, Func<OutboxMessage, OutboxMessage> change)
    {
        lock (_gate)
        {
            if (_messages.TryGetValue(id, out var m)) _messages[id] = change(m);
        }

        return Task.CompletedTask;
    }
}
