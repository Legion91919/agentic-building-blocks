namespace Agentic.Blocks.Idempotency;

/// <summary>A response captured for replay.</summary>
/// <param name="StatusCode">HTTP status code.</param>
/// <param name="ContentType">Content type, or null.</param>
/// <param name="Body">Response body bytes.</param>
/// <param name="Headers">Replayed headers (for example <c>Location</c>).</param>
public sealed record IdempotentResponse(int StatusCode, string? ContentType, ReadOnlyMemory<byte> Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>The stored state of one idempotency key.</summary>
/// <param name="Key">The scoped key.</param>
/// <param name="RequestHash">SHA-256 of method, path and body of the first request.</param>
/// <param name="Response">The stored response, or null while the first request is still running.</param>
/// <param name="ExpiresAt">When the record may be forgotten.</param>
public sealed record IdempotencyRecord(string Key, string RequestHash, IdempotentResponse? Response, DateTimeOffset ExpiresAt)
{
    /// <summary>True when the first request finished and its response is stored.</summary>
    public bool IsCompleted => Response is not null;
}

/// <summary>
/// Persistence port for idempotency keys. Implementations must make <see cref="TryBeginAsync"/> atomic
/// (for example an INSERT with a unique key, or SET NX in a cache).
/// </summary>
public interface IIdempotencyStore
{
    /// <summary>
    /// Reserves <paramref name="key"/> for a new request. Returns null when the caller now owns the key, or the
    /// existing, unexpired record otherwise.
    /// </summary>
    ValueTask<IdempotencyRecord?> TryBeginAsync(string key, string requestHash, DateTimeOffset reservationExpiresAt, CancellationToken cancellationToken = default);

    /// <summary>Stores the response for a reserved key until <paramref name="expiresAt"/>.</summary>
    ValueTask CompleteAsync(string key, string requestHash, IdempotentResponse response, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);

    /// <summary>Releases a reservation so the request can be retried (used when the handler failed).</summary>
    ValueTask ReleaseAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>Thread-safe in-memory store with expiry driven by <see cref="TimeProvider"/>. For a single instance or tests.</summary>
public sealed class InMemoryIdempotencyStore(TimeProvider time) : IIdempotencyStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, IdempotencyRecord> _records = new(StringComparer.Ordinal);
    private int _operations;

    /// <summary>Number of records held, including expired ones not yet purged.</summary>
    public int Count { get { lock (_gate) { return _records.Count; } } }

    /// <inheritdoc />
    public ValueTask<IdempotencyRecord?> TryBeginAsync(string key, string requestHash, DateTimeOffset reservationExpiresAt, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(key);
        var now = time.GetUtcNow();
        lock (_gate)
        {
            if (++_operations % 256 == 0) PurgeExpiredLocked(now);
            if (_records.TryGetValue(key, out var existing) && existing.ExpiresAt > now)
            {
                return ValueTask.FromResult<IdempotencyRecord?>(existing);
            }

            _records[key] = new IdempotencyRecord(key, requestHash, null, reservationExpiresAt);
            return ValueTask.FromResult<IdempotencyRecord?>(null);
        }
    }

    /// <inheritdoc />
    public ValueTask CompleteAsync(string key, string requestHash, IdempotentResponse response, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _records[key] = new IdempotencyRecord(key, requestHash, response, expiresAt);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_records.TryGetValue(key, out var r) && !r.IsCompleted) _records.Remove(key);
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>Removes expired records now.</summary>
    public int PurgeExpired()
    {
        lock (_gate) { return PurgeExpiredLocked(time.GetUtcNow()); }
    }

    private int PurgeExpiredLocked(DateTimeOffset now)
    {
        var expired = _records.Where(kv => kv.Value.ExpiresAt <= now).Select(kv => kv.Key).ToList();
        foreach (var k in expired) _records.Remove(k);
        return expired.Count;
    }
}
