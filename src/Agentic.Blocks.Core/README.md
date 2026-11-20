# Agentic.Blocks.Core

Shared primitives for every block. BCL only, no other dependencies.

```bash
dotnet add package Agentic.Blocks.Core --version 0.1.0
```

| Type | What it is for |
|---|---|
| `Result`, `Result<T>`, `Error`, `ErrorType` | Expected failures without exceptions. Types map to HTTP 400/401/402/403/404/409/500 in `Agentic.Blocks.Web`. |
| `Money`, `Currencies`, `CurrencyMismatchException` | `decimal` money in an ISO 4217 currency, rounded to the minor unit, arithmetic only within one currency, `Allocate` without losing cents. |
| `PageRequest`, `CursorPage<T>`, `CursorPagination` | Keyset (cursor) pagination with opaque URL-safe cursors. |
| `IDomainEvent` | Marker for domain events (id + time). |
| `ICorrelationContext` | Read the current correlation id from any block. |
| `BlocksTelemetry` | ActivitySource/Meter naming convention: `Agentic.Blocks.<Block>`, instruments `agentic.blocks.<block>.<name>`. |

```csharp
Result<Money> Total(Money price, int qty) =>
    qty <= 0 ? Error.Validation("order.qty", "qty", "Quantity must be positive.") : price * qty;

var split = Money.Of(10m, "EUR").Allocate(3);   // 3.34, 3.33, 3.33
Money.Of(1m, "EUR") + Money.Of(1m, "USD");      // throws CurrencyMismatchException
```

Rounding: `Money.Of` uses banker's rounding (`MidpointRounding.ToEven`) to the currency's minor unit by default;
pass `MidpointRounding.AwayFromZero` when a contract asks for commercial rounding.
