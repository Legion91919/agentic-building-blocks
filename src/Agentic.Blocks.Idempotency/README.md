# Agentic.Blocks.Idempotency

Safe retries for commands that create records or move money.

```bash
dotnet add package Agentic.Blocks.Idempotency --version 0.1.0
```

```csharp
builder.Services.AddBlocksIdempotency(o => o.Expiry = TimeSpan.FromHours(24));
app.MapPost("/orders", (NewOrder o, OrderService s) => s.Place(o)).RequireIdempotencyKey();
```

| Request | Response |
|---|---|
| New key | Handler runs; response (status, body, `Content-Type`, `Location`) is stored. |
| Same key, same method + path + body | Stored response replayed with `Idempotency-Replayed: true`; handler does not run. |
| Same key, different body | `409` ProblemDetails, `code: idempotency.key_reused`. |
| Same key while the first request still runs | `409`, `code: idempotency.in_progress`, `Retry-After: 1`. |
| No key / key longer than 255 | `400`, `code: idempotency.key_missing` / `idempotency.key_invalid`. |
| Handler returns 5xx or throws | Nothing stored; the retry runs again. |

Keys are scoped per user (`NameIdentifier`/`sub`, or `anonymous`). The fingerprint is SHA-256 over method, path,
query and the raw body (the filter buffers the body before model binding).

**Store.** `InMemoryIdempotencyStore` is thread-safe and expires entries with `TimeProvider`; it suits one instance.
For several instances implement `IIdempotencyStore` on a shared store; `TryBeginAsync` must be atomic
(unique-key INSERT, or SET NX with expiry).
