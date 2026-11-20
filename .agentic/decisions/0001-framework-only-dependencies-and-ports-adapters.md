# 0001. Framework-only dependencies and ports/adapters

- Status: Accepted
- Date: 2026-11-20

## Context
These blocks are pulled into many showcase and client projects as versioned NuGet packages. Each consumer has its own
database, identity provider, payment provider and hosting. A block that drags in third-party packages forces those
choices (and their upgrade cadence, licences and vulnerabilities) on every consumer. The build environment also has no
access to nuget.org, so anything beyond the BCL and the .NET shared frameworks cannot even be restored. The team is small.

## Decision
1. Blocks depend only on the BCL and the `Microsoft.AspNetCore.App` shared framework (FrameworkReference), plus other
   blocks. No third-party packages in `src/`. Test projects reference xUnit for CI only.
2. Every piece of infrastructure is a **port** (an interface owned by the block) with an in-memory **adapter** that is
   thread-safe and driven by `TimeProvider`: `IUserStore`, `IPasswordHasher`, `IIdempotencyStore`, `IOutboxStore`,
   `IAuditStore`, `IInboxStore`, `INotificationPreferenceStore`, `INotificationRecipientResolver`, `IEmailSender`,
   `ISmsSender`, `INotificationPusher`, `ISubscriptionStore`, `IPaymentProvider`, `ISubscriptionEventSink`.
   Registration uses `TryAdd`, so a consumer registers its own adapter before or after `AddBlocks*()` and wins.
3. Blocks talk to each other through Core types (`Result`, `Error`, `Money`, `ICorrelationContext`) and standard
   claims (`NameIdentifier`/`sub`, `role`, `tenant_id`), never through each other's internals. Cross-block reactions
   go through ports implemented in the app (the sample's `ISubscriptionEventSink` sends notifications and writes audit).

## Alternatives considered
- Use the usual packages (EF Core, MediatR, Polly, a JWT library, a payment SDK): richer, but every consumer inherits
  them, and they cannot be restored here. Consumers can still use them inside their own adapters.
- Ship database adapters (EF Core) in the blocks: convenient, but ties every project to one ORM and schema style.
  Revisit as optional `Agentic.Blocks.*.EntityFrameworkCore` packages once two projects need the same adapter.
- One big "platform" package: simpler to install, but forces unrelated blocks and upgrades together.

## Consequences
- Easier: offline builds, small dependency graphs, no licence surprises, fast tests (in-memory adapters, manual time),
  swapping infrastructure without touching app code, publishing to any NuGet feed.
- Harder: we write a little more code ourselves (PBKDF2 wrapper, HMAC webhook signing, cursor paging, a manual clock,
  a Kestrel test host). In-memory adapters are not durable: production projects must write persistent adapters, and
  the docs state the atomicity each port requires (unique keys, compare-and-swap versions, atomic leases).
