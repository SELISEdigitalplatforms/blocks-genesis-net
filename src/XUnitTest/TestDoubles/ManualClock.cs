namespace XUnitTest.TestDoubles;

/// <summary>A manual clock. Its timers fire only when <see cref="Advance"/> moves time past their due time.</summary>
internal sealed class ManualClock : TimeProvider
{
    private readonly List<ManualTimer> _timers = [];

    public DateTimeOffset Now { get; set; } = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    /// <summary>Timers created and not yet disposed or fired.</summary>
    public int PendingTimers
    {
        get { lock (_timers) { return _timers.Count(t => t.DueAt is not null); } }
    }

    public void Advance(TimeSpan by)
    {
        Now += by;
        List<ManualTimer> due;
        lock (_timers)
        {
            due = _timers.Where(t => t.DueAt <= Now).ToList();
            foreach (var timer in due)
            {
                timer.DueAt = null;
            }
        }

        foreach (var timer in due)
        {
            timer.Fire();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public void Fire() => callback(state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._timers)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now + dueTime;
            }

            return true;
        }

        public void Dispose()
        {
            lock (clock._timers)
            {
                DueAt = null;
                clock._timers.Remove(this);
            }
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
