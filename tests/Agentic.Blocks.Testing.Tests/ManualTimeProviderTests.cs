using Agentic.Blocks.Testing;
using Xunit;

namespace Agentic.Blocks.Testing.Tests;

public class ManualTimeProviderTests
{
    private static readonly DateTimeOffset Start = new(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Clock_only_moves_when_advanced()
    {
        var clock = new ManualTimeProvider(Start);
        Assert.Equal(Start, clock.GetUtcNow());
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(Start.AddMinutes(5), clock.GetUtcNow());
    }

    [Fact]
    public void Cannot_go_backwards()
    {
        var clock = new ManualTimeProvider(Start);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.SetUtcNow(Start.AddSeconds(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.Advance(TimeSpan.FromSeconds(-1)));
    }

    [Fact]
    public void One_shot_timer_fires_once_when_due()
    {
        var clock = new ManualTimeProvider(Start);
        var fired = 0;
        using var timer = clock.CreateTimer(_ => fired++, null, TimeSpan.FromSeconds(10), Timeout.InfiniteTimeSpan);
        clock.Advance(TimeSpan.FromSeconds(9));
        Assert.Equal(0, fired);
        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, fired);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Periodic_timer_fires_each_period()
    {
        var clock = new ManualTimeProvider(Start);
        var times = new List<DateTimeOffset>();
        using var timer = clock.CreateTimer(_ => times.Add(clock.GetUtcNow()), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal([Start.AddSeconds(1), Start.AddSeconds(2), Start.AddSeconds(3)], times);
    }

    [Fact]
    public void Disposed_timer_does_not_fire()
    {
        var clock = new ManualTimeProvider(Start);
        var fired = false;
        var timer = clock.CreateTimer(_ => fired = true, null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);
        timer.Dispose();
        clock.Advance(TimeSpan.FromSeconds(5));
        Assert.False(fired);
        Assert.Equal(0, clock.ActiveTimerCount);
    }

    [Fact]
    public async Task Task_delay_completes_when_time_advances()
    {
        var clock = new ManualTimeProvider(Start);
        var delay = Task.Delay(TimeSpan.FromMinutes(1), clock);
        Assert.False(delay.IsCompleted);
        clock.Advance(TimeSpan.FromMinutes(1));
        await delay;
        Assert.True(delay.IsCompletedSuccessfully);
    }

    [Fact]
    public void Timestamps_follow_manual_clock()
    {
        var clock = new ManualTimeProvider(Start);
        var t0 = clock.GetTimestamp();
        clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(2), clock.GetElapsedTime(t0));
    }
}
