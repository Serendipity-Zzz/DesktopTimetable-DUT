namespace DlutTimetable.Tests;

// Wall-clock-controlled one-shot timers; tests advance time without sleeping.
internal sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private readonly List<Timer> timers = [];
    private long timestamp;
    public DateTimeOffset Now { get; private set; } = now;
    public int TimerCount => timers.Count;
    public DateTimeOffset? NextDue => timers.Where(t => !t.Disposed).Select(t => t.Due).Min();
    public override DateTimeOffset GetUtcNow() => Now;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => timestamp;
    public void SetWallClock(DateTimeOffset value) => Now = value;
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new Timer(this, callback, state);
        timers.Add(timer); timer.Change(dueTime, period); return timer;
    }
    public void Advance(TimeSpan elapsed)
    {
        Now += elapsed;
        timestamp += Math.Max(0, elapsed.Ticks);
        foreach (var timer in timers.ToArray())
        {
            if (!timer.Disposed && timer.Due <= Now)
            { timer.Due = null; timer.Callback(timer.State); }
        }
    }
    private sealed class Timer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        public TimerCallback Callback { get; } = callback;
        public object? State { get; } = state;
        public DateTimeOffset? Due { get; set; }
        public bool Disposed { get; private set; }
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Assert.Equal(Timeout.InfiniteTimeSpan, period);
            if (Disposed) return false;
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : clock.Now + dueTime; return true;
        }
        public void Dispose() { Disposed = true; Due = null; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
