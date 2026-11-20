using Agentic.Blocks.Outbox;
using Agentic.Blocks.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Agentic.Blocks.Outbox.Tests;

public sealed record OrderPlaced(string OrderId, decimal Total);
public sealed record Unhandled(string Value);
public sealed record ChainStart(string Id);
public sealed record ChainNext(string Id);

public sealed class Recorder
{
    public List<(string Handler, string OrderId, int Attempt, Guid MessageId)> Calls { get; } = [];
    public int FailuresLeft { get; set; }
}

public sealed class OrderHandler(Recorder recorder) : IOutboxHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        lock (recorder)
        {
            recorder.Calls.Add(("order", message.OrderId, context.Attempt, context.MessageId));
            if (recorder.FailuresLeft > 0)
            {
                recorder.FailuresLeft--;
                throw new InvalidOperationException("downstream unavailable");
            }
        }

        return Task.CompletedTask;
    }
}

public sealed class SecondOrderHandler(Recorder recorder) : IOutboxHandler<OrderPlaced>
{
    public Task HandleAsync(OrderPlaced message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        lock (recorder) recorder.Calls.Add(("second", message.OrderId, context.Attempt, context.MessageId));
        return Task.CompletedTask;
    }
}

public sealed class ChainStartHandler(IOutbox outbox) : IOutboxHandler<ChainStart>
{
    public Task HandleAsync(ChainStart message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        outbox.Enqueue(new ChainNext(message.Id));
        return Task.CompletedTask;
    }
}

public sealed class ChainNextHandler(Recorder recorder) : IOutboxHandler<ChainNext>
{
    public Task HandleAsync(ChainNext message, OutboxMessageContext context, CancellationToken cancellationToken)
    {
        lock (recorder) recorder.Calls.Add(("chain", message.Id, context.Attempt, context.MessageId));
        return Task.CompletedTask;
    }
}

public sealed class OutboxFixture : IDisposable
{
    public OutboxFixture(Action<IServiceCollection>? extra = null, int maxAttempts = 3)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
        services.AddSingleton<TimeProvider>(Clock);
        services.AddSingleton(Recorder);
        services.AddBlocksOutbox(o =>
        {
            o.RunDispatcher = false;
            o.JitterFactor = 0;
            o.MaxAttempts = maxAttempts;
            o.BaseDelay = TimeSpan.FromSeconds(10);
            o.MaxDelay = TimeSpan.FromSeconds(25);
        });
        services.AddOutboxHandler<OrderPlaced, OrderHandler>("orders.placed.v1");
        extra?.Invoke(services);
        Provider = services.BuildServiceProvider();
    }

    public ManualTimeProvider Clock { get; } = new();

    public Recorder Recorder { get; } = new();

    public ServiceProvider Provider { get; }

    public OutboxProcessor Processor => Provider.GetRequiredService<OutboxProcessor>();

    public InMemoryOutboxStore Store => (InMemoryOutboxStore)Provider.GetRequiredService<IOutboxStore>();

    public async Task EnqueueAsync<T>(T message) where T : notnull
    {
        using var scope = Provider.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        outbox.Enqueue(message);
        await outbox.CommitAsync();
    }

    public void Dispose() => Provider.Dispose();
}

public class OutboxTests
{
    [Fact]
    public async Task Staged_messages_are_invisible_until_commit()
    {
        using var f = new OutboxFixture();
        using var scope = f.Provider.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        outbox.Enqueue(new OrderPlaced("o-1", 10m));
        Assert.Equal(1, outbox.PendingCount);
        Assert.Equal(0, await f.Processor.ProcessDueAsync());
        Assert.Equal(1, await outbox.CommitAsync());
        Assert.Equal(1, await f.Processor.ProcessDueAsync());
        Assert.Single(f.Recorder.Calls);
    }

    [Fact]
    public async Task Discard_drops_staged_messages()
    {
        using var f = new OutboxFixture();
        using var scope = f.Provider.CreateScope();
        var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
        outbox.Enqueue(new OrderPlaced("o-1", 10m));
        outbox.Discard();
        Assert.Equal(0, await outbox.CommitAsync());
        Assert.Empty(f.Store.Snapshot());
    }

    [Fact]
    public async Task Uses_mapped_type_name_and_json_payload()
    {
        using var f = new OutboxFixture();
        await f.EnqueueAsync(new OrderPlaced("o-9", 12.5m));
        var stored = Assert.Single(f.Store.Snapshot());
        Assert.Equal("orders.placed.v1", stored.Type);
        Assert.Contains("\"orderId\":\"o-9\"", stored.Payload);
    }

    [Fact]
    public async Task Successful_delivery_marks_succeeded()
    {
        using var f = new OutboxFixture();
        await f.EnqueueAsync(new OrderPlaced("o-1", 10m));
        await f.Processor.ProcessDueAsync();
        var stored = Assert.Single(f.Store.Snapshot());
        Assert.Equal(OutboxMessageStatus.Succeeded, stored.Status);
        Assert.Equal(1, stored.Attempts);
        Assert.Equal(0, await f.Processor.ProcessDueAsync());
    }

    [Fact]
    public async Task Failure_is_retried_after_exponential_backoff()
    {
        using var f = new OutboxFixture(maxAttempts: 5);
        f.Recorder.FailuresLeft = 2;
        await f.EnqueueAsync(new OrderPlaced("o-1", 10m));

        await f.Processor.ProcessDueAsync();
        var afterFirst = Assert.Single(f.Store.Snapshot());
        Assert.Equal(OutboxMessageStatus.Pending, afterFirst.Status);
        Assert.Equal(f.Clock.GetUtcNow().AddSeconds(10), afterFirst.NextAttemptAt);
        Assert.Contains("downstream unavailable", afterFirst.LastError);

        f.Clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, await f.Processor.ProcessDueAsync());
        f.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await f.Processor.ProcessDueAsync());
        Assert.Equal(f.Clock.GetUtcNow().AddSeconds(20), f.Store.Snapshot()[0].NextAttemptAt);

        f.Clock.Advance(TimeSpan.FromSeconds(20));
        await f.Processor.ProcessDueAsync();
        Assert.Equal(OutboxMessageStatus.Succeeded, f.Store.Snapshot()[0].Status);
        Assert.Equal([1, 2, 3], f.Recorder.Calls.Select(c => c.Attempt).ToList());
        Assert.Single(f.Recorder.Calls.Select(c => c.MessageId).Distinct());
    }

    [Fact]
    public async Task Gives_up_and_dead_letters_after_max_attempts()
    {
        using var f = new OutboxFixture(maxAttempts: 3);
        f.Recorder.FailuresLeft = 100;
        await f.EnqueueAsync(new OrderPlaced("o-1", 10m));
        for (var i = 0; i < 5; i++)
        {
            await f.Processor.ProcessDueAsync();
            f.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        var stored = Assert.Single(f.Store.Snapshot());
        Assert.Equal(OutboxMessageStatus.DeadLettered, stored.Status);
        Assert.Equal(3, stored.Attempts);
        Assert.Equal(3, f.Recorder.Calls.Count);
        Assert.Single(await f.Store.GetDeadLettersAsync(10));
    }

    [Fact]
    public async Task Dead_letter_can_be_requeued()
    {
        using var f = new OutboxFixture(maxAttempts: 1);
        f.Recorder.FailuresLeft = 1;
        await f.EnqueueAsync(new OrderPlaced("o-1", 10m));
        await f.Processor.ProcessDueAsync();
        var dead = Assert.Single(await f.Store.GetDeadLettersAsync(10));
        Assert.True(await f.Store.RequeueAsync(dead.Id, f.Clock.GetUtcNow()));
        Assert.False(await f.Store.RequeueAsync(dead.Id, f.Clock.GetUtcNow()));
        await f.Processor.ProcessDueAsync();
        Assert.Equal(OutboxMessageStatus.Succeeded, (await f.Store.GetAsync(dead.Id))!.Status);
    }

    [Fact]
    public async Task Unknown_type_is_dead_lettered_immediately()
    {
        using var f = new OutboxFixture();
        await f.EnqueueAsync(new Unhandled("x"));
        await f.Processor.ProcessDueAsync();
        var stored = Assert.Single(f.Store.Snapshot());
        Assert.Equal(OutboxMessageStatus.DeadLettered, stored.Status);
        Assert.Contains("No handler", stored.LastError);
    }

    [Fact]
    public async Task Corrupt_payload_is_dead_lettered_not_retried()
    {
        using var f = new OutboxFixture();
        await f.Store.AppendAsync([new OutboxMessage { Id = Guid.NewGuid(), Type = "orders.placed.v1", Payload = "{not json", CreatedAt = f.Clock.GetUtcNow(), NextAttemptAt = f.Clock.GetUtcNow() }]);
        await f.Processor.ProcessDueAsync();
        Assert.Equal(OutboxMessageStatus.DeadLettered, f.Store.Snapshot()[0].Status);
        Assert.Empty(f.Recorder.Calls);
    }

    [Fact]
    public async Task All_handlers_of_a_type_run_and_retry_together()
    {
        using var f = new OutboxFixture(s => s.AddOutboxHandler<OrderPlaced, SecondOrderHandler>("orders.placed.v1"));
        f.Recorder.FailuresLeft = 1;
        await f.EnqueueAsync(new OrderPlaced("o-1", 10m));
        await f.Processor.ProcessDueAsync();
        f.Clock.Advance(TimeSpan.FromSeconds(10));
        await f.Processor.ProcessDueAsync();
        Assert.Equal(["order", "order", "second"], f.Recorder.Calls.Select(c => c.Handler).ToList());
    }

    [Fact]
    public async Task Messages_enqueued_by_a_handler_are_committed_with_its_success()
    {
        using var f = new OutboxFixture(s =>
        {
            s.AddOutboxHandler<ChainStart, ChainStartHandler>("chain.start");
            s.AddOutboxHandler<ChainNext, ChainNextHandler>("chain.next");
        });
        await f.EnqueueAsync(new ChainStart("c-1"));
        await f.Processor.ProcessDueAsync();
        await f.Processor.ProcessDueAsync();
        Assert.Equal("c-1", Assert.Single(f.Recorder.Calls).OrderId);
        Assert.Equal(2, f.Store.Snapshot().Count);
    }

    [Fact]
    public async Task Expired_lease_is_picked_up_again()
    {
        var store = new InMemoryOutboxStore();
        var now = DateTimeOffset.UnixEpoch;
        await store.AppendAsync([new OutboxMessage { Id = Guid.NewGuid(), Type = "t", Payload = "{}", CreatedAt = now, NextAttemptAt = now }]);
        Assert.Single(await store.LeaseDueAsync(now, 10, TimeSpan.FromMinutes(1)));
        Assert.Empty(await store.LeaseDueAsync(now.AddSeconds(30), 10, TimeSpan.FromMinutes(1)));
        Assert.Single(await store.LeaseDueAsync(now.AddMinutes(1), 10, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task Concurrent_leases_never_hand_out_a_message_twice()
    {
        var store = new InMemoryOutboxStore();
        var now = DateTimeOffset.UnixEpoch;
        await store.AppendAsync(Enumerable.Range(0, 100).Select(_ => new OutboxMessage { Id = Guid.NewGuid(), Type = "t", Payload = "{}", CreatedAt = now, NextAttemptAt = now }).ToList());
        var leases = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => Task.Run(() => store.LeaseDueAsync(now, 15, TimeSpan.FromMinutes(1)))));
        var ids = leases.SelectMany(l => l).Select(m => m.Id).ToList();
        Assert.Equal(100, ids.Count);
        Assert.Equal(100, ids.Distinct().Count());
    }

    [Theory]
    [InlineData(1, 10)]
    [InlineData(2, 20)]
    [InlineData(3, 25)]
    [InlineData(10, 25)]
    public void Backoff_doubles_and_is_capped(int failedAttempts, int expectedSeconds)
    {
        var options = new OutboxOptions { BaseDelay = TimeSpan.FromSeconds(10), MaxDelay = TimeSpan.FromSeconds(25) };
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), options.BackoffFor(failedAttempts));
    }

    [Fact]
    public void Registry_rejects_conflicting_names()
    {
        var registry = new OutboxTypeRegistry();
        registry.Map<OrderPlaced>("a");
        Assert.Throws<InvalidOperationException>(() => registry.Map<Unhandled>("a"));
        Assert.Throws<InvalidOperationException>(() => registry.Map<OrderPlaced>("b"));
        Assert.Equal(typeof(OrderPlaced), registry.TypeOf("a"));
        Assert.Null(registry.TypeOf("missing"));
    }

    [Fact]
    public async Task Background_dispatcher_delivers_committed_messages()
    {
        var services = new ServiceCollection();
        var recorder = new Recorder();
        services.AddLogging();
        services.AddSingleton(recorder);
        services.AddBlocksOutbox(o => o.PollInterval = TimeSpan.FromMilliseconds(50));
        services.AddOutboxHandler<OrderPlaced, OrderHandler>();
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>().Single();
        await hosted.StartAsync(CancellationToken.None);
        using (var scope = provider.CreateScope())
        {
            var outbox = scope.ServiceProvider.GetRequiredService<IOutbox>();
            outbox.Enqueue(new OrderPlaced("bg", 1m));
            await outbox.CommitAsync();
        }

        for (var i = 0; i < 100 && recorder.Calls.Count == 0; i++) await Task.Delay(20);
        await hosted.StopAsync(CancellationToken.None);
        Assert.Equal("bg", Assert.Single(recorder.Calls).OrderId);
    }
}
