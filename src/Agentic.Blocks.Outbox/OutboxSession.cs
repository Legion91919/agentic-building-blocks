using System.Text.Json;
using Agentic.Blocks.Core;

namespace Agentic.Blocks.Outbox;

/// <summary>Scoped <see cref="IOutbox"/>: one per HTTP request or handler run.</summary>
internal sealed class OutboxSession(
    IOutboxStore store,
    OutboxTypeRegistry registry,
    OutboxSignal signal,
    OutboxMetrics metrics,
    TimeProvider time,
    ICorrelationContext? correlation = null) : IOutbox
{
    private readonly List<OutboxMessage> _pending = [];
    private readonly object _gate = new();

    /// <summary>Overrides the correlation id (the dispatcher sets it to the parent message's id).</summary>
    public string? CorrelationOverride { get; set; }

    public int PendingCount { get { lock (_gate) { return _pending.Count; } } }

    public void Enqueue<TMessage>(TMessage message) where TMessage : notnull
    {
        ArgumentNullException.ThrowIfNull(message);
        var now = time.GetUtcNow();
        var entry = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = registry.NameOf(typeof(TMessage)),
            Payload = JsonSerializer.Serialize(message, OutboxJson.Options),
            CreatedAt = now,
            NextAttemptAt = now,
            CorrelationId = CorrelationOverride ?? correlation?.CorrelationId,
        };
        lock (_gate) { _pending.Add(entry); }
    }

    public async Task<int> CommitAsync(CancellationToken cancellationToken = default)
    {
        List<OutboxMessage> batch;
        lock (_gate)
        {
            batch = [.. _pending];
            _pending.Clear();
        }

        if (batch.Count == 0) return 0;
        await store.AppendAsync(batch, cancellationToken).ConfigureAwait(false);
        metrics.Enqueued.Add(batch.Count);
        signal.Notify();
        return batch.Count;
    }

    public void Discard()
    {
        lock (_gate) { _pending.Clear(); }
    }
}

internal static class OutboxJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}

/// <summary>Wakes the dispatcher when new messages are committed.</summary>
internal sealed class OutboxSignal
{
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Notify() => Interlocked.Exchange(ref _tcs, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();

    public Task WaitAsync() => Volatile.Read(ref _tcs).Task;
}

internal sealed class OutboxMetrics : IDisposable
{
    private readonly System.Diagnostics.Metrics.Meter _meter = BlocksTelemetry.CreateMeter("Outbox");

    public OutboxMetrics()
    {
        Enqueued = _meter.CreateCounter<long>(BlocksTelemetry.InstrumentName("Outbox", "enqueued"));
        Dispatched = _meter.CreateCounter<long>(BlocksTelemetry.InstrumentName("Outbox", "dispatched"));
        Failed = _meter.CreateCounter<long>(BlocksTelemetry.InstrumentName("Outbox", "failed"));
        DeadLettered = _meter.CreateCounter<long>(BlocksTelemetry.InstrumentName("Outbox", "dead_lettered"));
    }

    public System.Diagnostics.Metrics.Counter<long> Enqueued { get; }

    public System.Diagnostics.Metrics.Counter<long> Dispatched { get; }

    public System.Diagnostics.Metrics.Counter<long> Failed { get; }

    public System.Diagnostics.Metrics.Counter<long> DeadLettered { get; }

    public void Dispose() => _meter.Dispose();
}
