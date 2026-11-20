using System.Net;
using System.Text.Json;
using Agentic.Blocks.Idempotency;
using Agentic.Blocks.Testing;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Agentic.Blocks.Idempotency.Tests;

public sealed record OrderRequest(string Sku, int Quantity);

public class IdempotencyEndpointTests
{
    private sealed class Counter { public int Calls; }

    private static Task<KestrelTestHost> StartAsync(ManualTimeProvider clock, Counter counter, Action<IdempotencyOptions>? configure = null) =>
        KestrelTestHost.StartAsync(
            b => b.Services.AddSingleton<TimeProvider>(clock).AddSingleton(counter).AddProblemDetails().AddBlocksIdempotency(configure),
            app =>
            {
                app.MapPost("/orders", (OrderRequest req, Counter c) =>
                {
                    var n = Interlocked.Increment(ref c.Calls);
                    return Results.Created($"/orders/{n}", new { id = n, req.Sku, req.Quantity });
                }).RequireIdempotencyKey();
                app.MapPost("/plain", (Counter c) => $"call {Interlocked.Increment(ref c.Calls)}").RequireIdempotencyKey();
                app.MapPost("/object", (Counter c) => new { call = Interlocked.Increment(ref c.Calls) }).RequireIdempotencyKey();
                app.MapPost("/fail", (Counter c) =>
                {
                    Interlocked.Increment(ref c.Calls);
                    return Results.Problem(statusCode: 503);
                }).RequireIdempotencyKey();
                app.MapPost("/invalid", (Counter c) =>
                {
                    Interlocked.Increment(ref c.Calls);
                    return Results.BadRequest(new { error = "nope" });
                }).RequireIdempotencyKey();
                app.MapPost("/slow", async (Counter c) =>
                {
                    Interlocked.Increment(ref c.Calls);
                    await Task.Delay(500);
                    return Results.Ok();
                }).RequireIdempotencyKey();
            });

    private static Dictionary<string, string> Key(string key) => new() { ["Idempotency-Key"] = key };

    [Fact]
    public async Task Same_key_and_body_replays_stored_response()
    {
        var counter = new Counter();
        await using var host = await StartAsync(new ManualTimeProvider(), counter);
        var first = await host.SendAsync(HttpMethod.Post, "/orders", new OrderRequest("tea", 2), headers: Key("k-1"));
        var second = await host.SendAsync(HttpMethod.Post, "/orders", new OrderRequest("tea", 2), headers: Key("k-1"));

        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        Assert.Equal(HttpStatusCode.Created, second.StatusCode);
        Assert.Equal(await first.Content.ReadAsStringAsync(), await second.Content.ReadAsStringAsync());
        Assert.Equal("/orders/1", second.Headers.Location?.ToString());
        Assert.Equal("true", second.Headers.GetValues(IdempotencyExtensions.ReplayedHeader).Single());
        Assert.False(first.Headers.Contains(IdempotencyExtensions.ReplayedHeader));
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task Same_key_with_different_body_is_409()
    {
        var counter = new Counter();
        await using var host = await StartAsync(new ManualTimeProvider(), counter);
        await host.SendAsync(HttpMethod.Post, "/orders", new OrderRequest("tea", 2), headers: Key("k-2"));
        var conflict = await host.SendAsync(HttpMethod.Post, "/orders", new OrderRequest("tea", 3), headers: Key("k-2"));

        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.Equal("application/problem+json", conflict.Content.Headers.ContentType?.MediaType);
        var body = JsonDocument.Parse(await conflict.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal("idempotency.key_reused", body.GetProperty("code").GetString());
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task Missing_key_is_400_and_handler_not_called()
    {
        var counter = new Counter();
        await using var host = await StartAsync(new ManualTimeProvider(), counter);
        var response = await host.SendAsync(HttpMethod.Post, "/orders", new OrderRequest("tea", 1));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("idempotency.key_missing", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, counter.Calls);
    }

    [Fact]
    public async Task Overlong_key_is_400()
    {
        var counter = new Counter();
        await using var host = await StartAsync(new ManualTimeProvider(), counter, o => o.MaxKeyLength = 8);
        var response = await host.SendAsync(HttpMethod.Post, "/orders", new OrderRequest("tea", 1), headers: Key("123456789"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("idempotency.key_invalid", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Different_keys_run_handler_each_time()
    {
        var counter = new Counter();
        await using var host = await StartAsync(new ManualTimeProvider(), counter);
        await host.SendAsync(HttpMethod.Post, "/orders", new OrderRequest("tea", 1), headers: Key("a"));
        await host.SendAsync(HttpMethod.Post, "/orders", new OrderRequest("tea", 1), headers: Key("b"));
        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public async Task Key_expires_after_configured_time()
    {
        var clock = new ManualTimeProvider();
        var counter = new Counter();
        await using var host = await StartAsync(clock, counter, o => o.Expiry = TimeSpan.FromHours(1));
        await host.SendAsync(HttpMethod.Post, "/orders", new OrderRequest("tea", 1), headers: Key("exp"));
        clock.Advance(TimeSpan.FromMinutes(59));
        await host.SendAsync(HttpMethod.Post, "/orders", new OrderRequest("tea", 1), headers: Key("exp"));
        Assert.Equal(1, counter.Calls);
        clock.Advance(TimeSpan.FromMinutes(1));
        var after = await host.SendAsync(HttpMethod.Post, "/orders", new OrderRequest("tea", 1), headers: Key("exp"));
        Assert.Equal(2, counter.Calls);
        Assert.False(after.Headers.Contains(IdempotencyExtensions.ReplayedHeader));
    }

    [Fact]
    public async Task Server_errors_are_not_stored_so_retry_runs_again()
    {
        var counter = new Counter();
        await using var host = await StartAsync(new ManualTimeProvider(), counter);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await host.SendAsync(HttpMethod.Post, "/fail", headers: Key("f"))).StatusCode);
        await host.SendAsync(HttpMethod.Post, "/fail", headers: Key("f"));
        Assert.Equal(2, counter.Calls);
    }

    [Fact]
    public async Task Client_errors_are_stored_and_replayed()
    {
        var counter = new Counter();
        await using var host = await StartAsync(new ManualTimeProvider(), counter);
        await host.SendAsync(HttpMethod.Post, "/invalid", headers: Key("i"));
        var replay = await host.SendAsync(HttpMethod.Post, "/invalid", headers: Key("i"));
        Assert.Equal(HttpStatusCode.BadRequest, replay.StatusCode);
        Assert.Equal(1, counter.Calls);
    }

    [Fact]
    public async Task String_and_object_returns_are_replayed()
    {
        var counter = new Counter();
        await using var host = await StartAsync(new ManualTimeProvider(), counter);
        var a = await (await host.SendAsync(HttpMethod.Post, "/plain", headers: Key("p"))).Content.ReadAsStringAsync();
        var b = await (await host.SendAsync(HttpMethod.Post, "/plain", headers: Key("p"))).Content.ReadAsStringAsync();
        Assert.Equal("call 1", a);
        Assert.Equal(a, b);
        var o1 = await (await host.SendAsync(HttpMethod.Post, "/object", headers: Key("o"))).Content.ReadAsStringAsync();
        var o2 = await (await host.SendAsync(HttpMethod.Post, "/object", headers: Key("o"))).Content.ReadAsStringAsync();
        Assert.Equal("{\"call\":2}", o1);
        Assert.Equal(o1, o2);
    }

    [Fact]
    public async Task Concurrent_duplicate_gets_409_in_progress()
    {
        var counter = new Counter();
        await using var host = await StartAsync(new ManualTimeProvider(), counter);
        var first = host.SendAsync(HttpMethod.Post, "/slow", headers: Key("s"));
        await Task.Delay(150);
        var second = await host.SendAsync(HttpMethod.Post, "/slow", headers: Key("s"));
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Contains("idempotency.in_progress", await second.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, (await first).StatusCode);
        Assert.Equal(1, counter.Calls);
    }
}

public class InMemoryIdempotencyStoreTests
{
    private static readonly IdempotentResponse Ok = new(200, "text/plain", "ok"u8.ToArray(), new Dictionary<string, string>());

    [Fact]
    public async Task First_begin_owns_key_second_sees_record()
    {
        var clock = new ManualTimeProvider();
        var store = new InMemoryIdempotencyStore(clock);
        Assert.Null(await store.TryBeginAsync("k", "h", clock.GetUtcNow().AddMinutes(1)));
        var existing = await store.TryBeginAsync("k", "h", clock.GetUtcNow().AddMinutes(1));
        Assert.NotNull(existing);
        Assert.False(existing!.IsCompleted);
    }

    [Fact]
    public async Task Expired_reservation_can_be_taken_again()
    {
        var clock = new ManualTimeProvider();
        var store = new InMemoryIdempotencyStore(clock);
        await store.TryBeginAsync("k", "h", clock.GetUtcNow().AddMinutes(1));
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(await store.TryBeginAsync("k", "h", clock.GetUtcNow().AddMinutes(1)));
    }

    [Fact]
    public async Task Release_does_not_remove_completed_record()
    {
        var clock = new ManualTimeProvider();
        var store = new InMemoryIdempotencyStore(clock);
        await store.TryBeginAsync("k", "h", clock.GetUtcNow().AddMinutes(1));
        await store.CompleteAsync("k", "h", Ok, clock.GetUtcNow().AddHours(1));
        await store.ReleaseAsync("k");
        Assert.True((await store.TryBeginAsync("k", "h", clock.GetUtcNow()))!.IsCompleted);
    }

    [Fact]
    public async Task Purge_removes_only_expired()
    {
        var clock = new ManualTimeProvider();
        var store = new InMemoryIdempotencyStore(clock);
        await store.TryBeginAsync("old", "h", clock.GetUtcNow().AddMinutes(1));
        await store.TryBeginAsync("new", "h", clock.GetUtcNow().AddMinutes(10));
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, store.PurgeExpired());
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public async Task Parallel_begins_have_exactly_one_owner()
    {
        var clock = new ManualTimeProvider();
        var store = new InMemoryIdempotencyStore(clock);
        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ =>
            Task.Run(async () => await store.TryBeginAsync("race", "h", clock.GetUtcNow().AddMinutes(1)))));
        Assert.Single(results, (IdempotencyRecord?)null);
    }
}
