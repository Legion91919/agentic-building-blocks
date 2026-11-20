namespace Agentic.Blocks.Testing;

/// <summary>
/// A <see cref="TimeProvider"/> whose clock only moves when a test calls <see cref="Advance"/> or <see cref="SetUtcNow"/>.
/// Timers (and therefore <c>Task.Delay(span, provider)</c>) fire synchronously, in due order, during those calls.
/// Thread-safe.
/// </summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private DateTimeOffset _now;
    private TimeZoneInfo _localTimeZone = TimeZoneInfo.Utc;

    /// <summary>Starts at 2026-01-01T00:00:00Z.</summary>
    public ManualTimeProvider() : this(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)) { }

    /// <summary>Starts at the given instant.</summary>
    public ManualTimeProvider(DateTimeOffset start) => _now = start.ToUniversalTime();

    /// <inheritdoc />
    public override DateTimeOffset GetUtcNow() { lock (_gate) { return _now; } }

    /// <inheritdoc />
    public override TimeZoneInfo LocalTimeZone => _localTimeZone;

    /// <summary>Sets the local time zone reported by this provider.</summary>
    public void SetLocalTimeZone(TimeZoneInfo zone) => _localTimeZone = zone ?? throw new ArgumentNullException(nameof(zone));

    /// <inheritdoc />
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    /// <inheritdoc />
    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    /// <summary>Moves the clock forward and fires every timer that becomes due.</summary>
    /// <exception cref="ArgumentOutOfRangeException">When <paramref name="delta"/> is negative.</exception>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);
        DateTimeOffset target;
        lock (_gate) { target = _now + delta; }
        MoveTo(target);
    }

    /// <summary>Jumps the clock to <paramref name="value"/> (must not go backwards) and fires due timers.</summary>
    public void SetUtcNow(DateTimeOffset value)
    {
        lock (_gate)
        {
            if (value < _now) throw new ArgumentOutOfRangeException(nameof(value), "Time cannot go backwards.");
        }

        MoveTo(value.ToUniversalTime());
    }

    /// <inheritdoc />
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>Number of active timers (useful to assert that a component is waiting).</summary>
    public int ActiveTimerCount { get { lock (_gate) { return _timers.Count; } } }

    private void MoveTo(DateTimeOffset target)
    {
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.DueAt <= target).OrderBy(t => t.DueAt).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                if (next.DueAt > _now) _now = next.DueAt;
                if (next.Period is { } p && p > TimeSpan.Zero) next.DueAt = _now + p;
                else _timers.Remove(next);
            }

            next.Fire();
        }
    }

    private void Schedule(ManualTimer timer, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            _timers.Remove(timer);
            if (dueTime == Timeout.InfiniteTimeSpan) return;
            timer.DueAt = _now + (dueTime < TimeSpan.Zero ? TimeSpan.Zero : dueTime);
            timer.Period = period == Timeout.InfiniteTimeSpan ? null : period;
            _timers.Add(timer);
        }

        if (dueTime == TimeSpan.Zero) MoveTo(GetUtcNow());
    }

    private void Remove(ManualTimer timer) { lock (_gate) { _timers.Remove(timer); } }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset DueAt { get; set; }

        public TimeSpan? Period { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            owner.Schedule(this, dueTime, period);
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose() => owner.Remove(this);

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
