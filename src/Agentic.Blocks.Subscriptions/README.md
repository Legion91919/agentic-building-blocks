# Agentic.Blocks.Subscriptions

Plans, entitlements and a subscription lifecycle driven by time and payment events.

```bash
dotnet add package Agentic.Blocks.Subscriptions --version 0.1.0
```

```csharp
builder.Services.AddBlocksSubscriptions(o => o
    .AddPlan("plus", "Plus", Money.Of(9.99m, "EUR"), BillingInterval.Monthly, trialDays: 0, "hd", "offline")
    .AddPlan("family", "Family", Money.Of(14.99m, "EUR"), BillingInterval.Monthly, trialDays: 7, "hd", "offline", "family"));
app.MapBlocksSubscriptions("/subscriptions");
app.MapGet("/media/hd", () => ...).RequireEntitlement("hd");   // 402 ProblemDetails with renewUrl when missing
```

## States
```
Start ── trial? ──> Trialing ── payment ok ──> Active ── period ends unpaid / payment failed ──> PastDue ── grace ends ──> Expired
                       │                         │  ^                                             │
                       └── cancel ──> Cancelled <┘  └──────────── payment ok ─────────────────────┘
```
Rules live in the pure `SubscriptionStateMachine` (no I/O, time passed in). Cancel-at-period-end keeps access until the
period ends, then becomes Cancelled. PastDue keeps entitlements during the grace period (default 7 days, configurable).
Cancelled and Expired are terminal; subscribing again creates a new subscription.
Time-driven transitions are applied lazily on every read and by a background refresher, so access checks are correct
even if the job is late.

## Payments
`IPaymentProvider` creates hosted checkout sessions and verifies and parses webhooks into provider-neutral
`PaymentEvent`s. `FakePaymentProvider` returns URLs on `pay.example.com` and signs events with HMAC-SHA256
(`Fake-Signature: t=..,v1=..`, 5-minute tolerance, per-process random secret unless configured).
Webhooks are processed once per provider event id: duplicates return `{"duplicate": true}` with no side effects;
processing failures release the id so the provider's retry is handled.

| Endpoint | |
|---|---|
| `GET /subscriptions/plans` | Public plan list. |
| `GET /subscriptions/me` | Current subscription and live entitlements. |
| `POST /subscriptions/checkout {planId}` | 201 `{sessionId, url}`. |
| `POST /subscriptions/cancel {atPeriodEnd}` | Cancel now or at period end. |
| `POST /subscriptions/webhooks/{provider}` | Signed provider webhooks (anonymous, rate-limited). |
| `POST /subscriptions/dev/checkout/{sessionId}/complete` | Development + fake provider only: sends a signed `CheckoutCompleted` through the real webhook path. |
| `POST /subscriptions/dev/renewal/{succeeded|failed}` | Development only: simulates a renewal payment event. |

Implement `ISubscriptionEventSink` to react to changes (notifications, audit); the sample does both.
