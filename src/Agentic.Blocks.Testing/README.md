# Agentic.Blocks.Testing

Test helpers that need no packages beyond the .NET shared frameworks.

```bash
dotnet add package Agentic.Blocks.Testing --version 0.1.0
```

- `ManualTimeProvider`: a `TimeProvider` that only moves when you call `Advance`/`SetUtcNow`. Timers and
  `Task.Delay(span, provider)` fire during those calls, so lockout windows, expiry and retry back-off are tested
  without sleeping.
- `KestrelTestHost`: starts a real `WebApplication` on `127.0.0.1:0` and hands you an `HttpClient`.

```csharp
var clock = new ManualTimeProvider();
await using var host = await KestrelTestHost.StartAsync(
    b => b.Services.AddSingleton<TimeProvider>(clock).AddBlocksWeb(),
    app => { app.UseBlocksWeb(); app.MapGet("/ping", () => "pong"); });
var response = await host.Client.GetStringAsync("/ping");
clock.Advance(TimeSpan.FromMinutes(15));
```
