# Agentic.Blocks.Web

ASP.NET Core defaults every API needs, in two calls.

```bash
dotnet add package Agentic.Blocks.Web --version 0.1.0
```

```csharp
builder.Services.AddBlocksWeb(o => o.AuthPermitLimit = 10);
var app = builder.Build();
app.UseBlocksWeb();   // correlation id, ProblemDetails, status pages, rate limiter, /health/live, /health/ready

app.MapGet("/orders/{id}", (string id, OrderService s) => s.Find(id).ToHttpResult());
app.MapPost("/orders", (NewOrder o, OrderService s) => s.Place(o).ToHttpResult(x => Results.Created($"/orders/{x.Id}", x)));
```

| Feature | Details |
|---|---|
| ProblemDetails | `Error.Type` maps to 400/401/402/403/404/409/500. `code` extension = `Error.Code`; metadata becomes extensions; validation uses `errors`. Every problem carries `traceId` and `correlationId`. Unhandled exceptions never leak messages or stack traces. |
| Correlation id | `X-Correlation-Id` is read (1-128 safe chars, else replaced), echoed on the response, put in the logging scope as `CorrelationId`, tagged on the current Activity, and readable anywhere through `ICorrelationContext`. |
| Health | `/health/live` runs checks tagged `live`; `/health/ready` runs `live` + `ready`. Add yours with `tags: [BlocksHealthTags.Ready]`. |
| Rate limits | `BlocksRateLimitPolicies.Auth` (10/min per IP), `Api` (120/min token bucket per user or IP), `Webhooks` (300/min per IP). Rejections are 429 ProblemDetails with `Retry-After`. |
| Telemetry | Follow `BlocksTelemetry` (in Core): source/meter `Agentic.Blocks.<Block>`, instruments `agentic.blocks.<block>.<name>`; subscribe with `AddSource(BlocksTelemetry.Wildcard)`. |
| Claims | `ClaimsPrincipal.GetUserId()` reads `NameIdentifier` or OIDC `sub`; `GetTenantId()` reads `tenant_id`. Blocks use these, never a specific identity provider. |

Behind a proxy, call `app.UseForwardedHeaders()` first so per-IP limits see the client address.
