# Agentic.Blocks.Outbox

Reliable side effects: record the intent with your change, deliver it afterwards.

```bash
dotnet add package Agentic.Blocks.Outbox --version 0.1.0
```

```csharp
builder.Services.AddBlocksOutbox(o => { o.MaxAttempts = 8; o.BaseDelay = TimeSpan.FromSeconds(2); });
builder.Services.AddOutboxHandler<OrderPlaced, SendReceipt>("orders.placed.v1");
app.UseBlocksOutbox();            // each HTTP request is a unit of work

app.MapPost("/orders", (NewOrder o, IOutbox outbox) =>
{
    // ... save the order ...
    outbox.Enqueue(new OrderPlaced(id, total));   // committed only if the request succeeds (< 400)
    return Results.Created(...);
});

public sealed class SendReceipt(IEmailSender email) : IOutboxHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced m, OutboxMessageContext ctx, CancellationToken ct) =>
        email.SendOnce(ctx.MessageId, ...);   // de-duplicate on ctx.MessageId
}
```

## Delivery guarantee: at least once
A message is marked delivered only after every handler returned. If the process crashes after a handler ran but before
the mark, if a lease expires while a slow handler is still running, or if a second handler for the same message throws,
the message is delivered again. **Handlers must be idempotent**: de-duplicate on `OutboxMessageContext.MessageId`,
use upserts, or pass the id as the downstream provider's idempotency key. Exactly-once delivery is not possible across
a network boundary; exactly-once *effect* is achieved by idempotent handlers.

## Retry and dead letter
Failures retry after `BaseDelay * 2^(attempt-1)` capped at `MaxDelay`, with +/-20% jitter (`JitterFactor`).
After `MaxAttempts` the message is `DeadLettered`; unknown types and corrupt payloads are dead-lettered at once.
Inspect with `IOutboxStore.GetDeadLettersAsync` and replay with `RequeueAsync` after fixing the cause.
Messages a handler enqueues are committed only when that handler run succeeds.

## Stores
`InMemoryOutboxStore` loses messages on restart. In production implement `IOutboxStore` on the same database as your
business data and write the rows in the same transaction (for example the same `DbContext.SaveChanges`). That is what
makes the outbox transactional. `LeaseDueAsync` must be atomic across instances (`UPDATE ... WHERE status = pending
AND next_attempt_at <= now ... RETURNING`, or `SELECT ... FOR UPDATE SKIP LOCKED`).

Metrics (meter `Agentic.Blocks.Outbox`): `agentic.blocks.outbox.enqueued`, `.dispatched`, `.failed`, `.dead_lettered`.
