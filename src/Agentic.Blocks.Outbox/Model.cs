namespace Agentic.Blocks.Outbox;

/// <summary>Lifecycle of an outbox message.</summary>
public enum OutboxMessageStatus
{
    /// <summary>Waiting for (another) delivery attempt.</summary>
    Pending,
    /// <summary>Leased by a dispatcher.</summary>
    Processing,
    /// <summary>All handlers succeeded.</summary>
    Succeeded,
    /// <summary>Gave up after the maximum attempts, or no handler exists. Needs a human.</summary>
    DeadLettered,
}

/// <summary>A stored outbox message. Immutable snapshot; stores return new copies on change.</summary>
public sealed record OutboxMessage
{
    /// <summary>Unique id. Handlers can use it to de-duplicate.</summary>
    public required Guid Id { get; init; }

    /// <summary>Logical message type name (stable across renames when mapped explicitly).</summary>
    public required string Type { get; init; }

    /// <summary>JSON payload.</summary>
    public required string Payload { get; init; }

    /// <summary>When it was enqueued.</summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>Correlation id of the request that produced it.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Current status.</summary>
    public OutboxMessageStatus Status { get; init; } = OutboxMessageStatus.Pending;

    /// <summary>Delivery attempts made so far.</summary>
    public int Attempts { get; init; }

    /// <summary>Earliest time of the next attempt.</summary>
    public DateTimeOffset NextAttemptAt { get; init; }

    /// <summary>Lease expiry while <see cref="OutboxMessageStatus.Processing"/>.</summary>
    public DateTimeOffset? LeasedUntil { get; init; }

    /// <summary>Last error message (no stack trace, no payload).</summary>
    public string? LastError { get; init; }

    /// <summary>When it succeeded or was dead-lettered.</summary>
    public DateTimeOffset? CompletedAt { get; init; }
}

/// <summary>Context passed to handlers.</summary>
/// <param name="MessageId">The message id (de-duplicate on it).</param>
/// <param name="Type">The logical type name.</param>
/// <param name="Attempt">1 for the first attempt.</param>
/// <param name="CorrelationId">Correlation id of the originating request.</param>
/// <param name="CreatedAt">When it was enqueued.</param>
public sealed record OutboxMessageContext(Guid MessageId, string Type, int Attempt, string? CorrelationId, DateTimeOffset CreatedAt);

/// <summary>
/// Handles one message type. Delivery is at-least-once: the same message can arrive more than once (after a crash,
/// a lease timeout or a failure in another handler of the same message), so handlers must be idempotent.
/// </summary>
public interface IOutboxHandler<in TMessage>
{
    /// <summary>Handles the message. Throw to trigger a retry.</summary>
    Task HandleAsync(TMessage message, OutboxMessageContext context, CancellationToken cancellationToken);
}

/// <summary>
/// Stage messages in the current unit of work (an HTTP request, or a handler run). Staged messages are written to
/// the store by <see cref="CommitAsync"/>; with a database adapter, commit in the same transaction as the business change.
/// </summary>
public interface IOutbox
{
    /// <summary>Stages a message. Nothing is visible to the dispatcher until <see cref="CommitAsync"/>.</summary>
    void Enqueue<TMessage>(TMessage message) where TMessage : notnull;

    /// <summary>Number of staged, uncommitted messages.</summary>
    int PendingCount { get; }

    /// <summary>Writes staged messages to the store atomically and wakes the dispatcher. Returns the number written.</summary>
    Task<int> CommitAsync(CancellationToken cancellationToken = default);

    /// <summary>Drops staged messages (the unit of work failed).</summary>
    void Discard();
}

/// <summary>Persistence port for outbox messages.</summary>
public interface IOutboxStore
{
    /// <summary>Appends messages atomically.</summary>
    Task AppendAsync(IReadOnlyCollection<OutboxMessage> messages, CancellationToken cancellationToken = default);

    /// <summary>Leases up to <paramref name="max"/> due messages (pending and due, or processing with an expired lease).</summary>
    Task<IReadOnlyList<OutboxMessage>> LeaseDueAsync(DateTimeOffset now, int max, TimeSpan leaseDuration, CancellationToken cancellationToken = default);

    /// <summary>Marks a message as delivered.</summary>
    Task MarkSucceededAsync(Guid id, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Records a failed attempt and schedules the next one.</summary>
    Task MarkFailedAsync(Guid id, string reason, DateTimeOffset nextAttemptAt, CancellationToken cancellationToken = default);

    /// <summary>Moves a message to the dead-letter state.</summary>
    Task MarkDeadLetteredAsync(Guid id, string reason, DateTimeOffset at, CancellationToken cancellationToken = default);

    /// <summary>Returns dead-lettered messages, oldest first.</summary>
    Task<IReadOnlyList<OutboxMessage>> GetDeadLettersAsync(int max, CancellationToken cancellationToken = default);

    /// <summary>Puts a dead-lettered message back in the queue with zero attempts. Returns false if not dead-lettered.</summary>
    Task<bool> RequeueAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Reads one message.</summary>
    Task<OutboxMessage?> GetAsync(Guid id, CancellationToken cancellationToken = default);
}
