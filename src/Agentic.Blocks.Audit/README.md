# Agentic.Blocks.Audit

An append-only record of who did what, to which entity, why and when.

```bash
dotnet add package Agentic.Blocks.Audit --version 0.1.0
```

```csharp
builder.Services.AddBlocksAudit();
app.MapBlocksAudit("/audit", requiredRole: "admin");   // read-only queries

await audit.AppendAsync(new AuditRecord(actor: userId, action: "subscription.cancelled",
    entityType: "subscription", entityId: sub.Id, reason: "requested by user"));
var history = await audit.GetByEntityAsync("subscription", sub.Id, new PageRequest(limit: 20));
```

- Each entry gets a gap-free `Sequence`, UTC `Timestamp` (from `TimeProvider`), the current correlation id, and a
  SHA-256 `Hash` over its fields plus the previous entry's hash. `VerifyIntegrityAsync` (and `GET /audit/verify`)
  re-computes the chain and reports the first broken sequence, so edits made directly in storage are detectable.
- There is no update or delete in `IAuditLog`, `IAuditStore` or the endpoints. Corrections are new entries.
- Queries are newest first with cursor paging: by entity (`/audit/entities/{type}/{id}`) and by actor (`/audit/actors/{actor}`).
- Keep `Reason` free of personal data; use ids for actors. Retention and erasure rules belong in your store adapter.

A production `IAuditStore` should enforce `Sequence` uniqueness and grant the app's database user INSERT and SELECT only.
