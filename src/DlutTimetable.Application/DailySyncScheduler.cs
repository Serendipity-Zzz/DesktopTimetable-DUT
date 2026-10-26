namespace DlutTimetable.Application;

[Flags]
public enum SyncTrigger { None = 0, Startup = 1, NetworkRestored = 2, Resumed = 4, ShanghaiMidnight = 8, AccountChanged = 16, ClockChanged = 32 }

/// <summary>Coalesces system events. The evaluator still owns admission policy and the network batch gate.</summary>
public sealed class DailySyncScheduler : IAsyncDisposable
{
    private readonly object gate = new();
    private readonly TimeProvider clock;
    private readonly Func<SyncTrigger, CancellationToken, Task> evaluate;
    private readonly CancellationTokenSource lifetime = new();
    private ITimer? midnight;
    private SyncTrigger pending;
    private Task work = Task.CompletedTask;
    private bool started, draining, disposed;
    public Exception? LastFailure { get; private set; }

    public DailySyncScheduler(TimeProvider clock, Func<SyncTrigger, CancellationToken, Task> evaluate)
    { this.clock = clock; this.evaluate = evaluate; }

    public Task StartAsync()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (started) return work;
            started = true;
            midnight = clock.CreateTimer(_ => OnMidnight(), null, DelayToMidnight(), Timeout.InfiniteTimeSpan);
            return SignalAsync(SyncTrigger.Startup);
        }
    }
    public Task SignalAsync(SyncTrigger trigger)
    {
        lock (gate)
        {
            if (disposed || !started || trigger == SyncTrigger.None) return Task.CompletedTask;
            if ((trigger & (SyncTrigger.Resumed | SyncTrigger.ClockChanged)) != 0)
                midnight?.Change(DelayToMidnight(), Timeout.InfiniteTimeSpan);
            pending |= trigger;
            if (!draining) { draining = true; work = Task.Run(DrainAsync); }
            return work;
        }
    }
    public Task WhenIdleAsync() { lock (gate) return work; }

    private TimeSpan DelayToMidnight()
    {
        var now = clock.GetUtcNow();
        return Domain.SchoolCalendar.NextShanghaiMidnight(now) - now;
    }
    private void OnMidnight()
    {
        lock (gate)
        {
            if (disposed) return;
            // Rearm once for the next current date. Sleep/resume never replays missed days.
            midnight!.Change(DelayToMidnight(), Timeout.InfiniteTimeSpan);
            _ = SignalAsync(SyncTrigger.ShanghaiMidnight);
        }
    }
    private async Task DrainAsync()
    {
        while (true)
        {
            SyncTrigger trigger;
            lock (gate)
            {
                if (disposed || pending == SyncTrigger.None) { draining = false; return; }
                trigger = pending; pending = SyncTrigger.None;
            }
            try
            {
                await evaluate(trigger, lifetime.Token).ConfigureAwait(false);
                LastFailure = null;
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (Exception ex) { LastFailure = ex; } // Keep one failure; no timed retry or unobserved task exception.
        }
    }
    public async ValueTask DisposeAsync()
    {
        Task finish;
        ITimer? timer;
        lock (gate)
        {
            if (disposed) return;
            disposed = true; pending = SyncTrigger.None;
            timer = midnight; midnight = null; finish = work;
        }
        await lifetime.CancelAsync().ConfigureAwait(false);
        if (timer is not null) await timer.DisposeAsync().ConfigureAwait(false);
        await finish.ConfigureAwait(false);
        lifetime.Dispose();
    }
}
