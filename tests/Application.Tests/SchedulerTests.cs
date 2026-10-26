using DlutTimetable.Application;

namespace DlutTimetable.Tests;

public sealed class SchedulerTests
{
    [Fact]
    public async Task SchedulesOnlyOneTimerAtShanghaiMidnightAndDoesNotReplayMissedDays()
    {
        var clock = new ManualClock(new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        var triggers = new List<SyncTrigger>();
        await using var scheduler = new DailySyncScheduler(clock, (trigger, _) =>
        { triggers.Add(trigger); return Task.CompletedTask; });
        await scheduler.StartAsync();
        Assert.Equal(SyncTrigger.Startup, Assert.Single(triggers));
        Assert.Equal(1, clock.TimerCount);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 16, 0, 0, TimeSpan.Zero), clock.NextDue);
        clock.Advance(TimeSpan.FromDays(9));
        await scheduler.WhenIdleAsync();
        Assert.Equal(2, triggers.Count);
        Assert.Equal(SyncTrigger.ShanghaiMidnight, triggers[1]);
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 16, 0, 0, TimeSpan.Zero), clock.NextDue);
        Assert.Equal(1, clock.TimerCount);
    }
    [Fact]
    public async Task CoalescesWakeAndNetworkEventsAndNeverEvaluatesConcurrently()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var triggers = new List<SyncTrigger>();
        await using var scheduler = new DailySyncScheduler(clock, async (trigger, ct) =>
        {
            triggers.Add(trigger);
            if (triggers.Count == 1) { entered.SetResult(); await release.Task.WaitAsync(ct); }
        });
        var start = scheduler.StartAsync(); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var events = Enumerable.Range(0, 50).Select(i => scheduler.SignalAsync(i % 2 == 0 ? SyncTrigger.NetworkRestored : SyncTrigger.Resumed)).ToArray();
        release.SetResult(); await Task.WhenAll(events.Append(start));
        Assert.Equal(2, triggers.Count);
        Assert.Equal(SyncTrigger.NetworkRestored | SyncTrigger.Resumed, triggers[1]);
    }
    [Fact]
    public async Task DisposeCancelsInFlightEvaluationAndDropsLateCallbacks()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var scheduler = new DailySyncScheduler(clock, async (_, ct) =>
        { calls++; entered.SetResult(); await Task.Delay(Timeout.Infinite, ct); });
        var startup = scheduler.StartAsync();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await scheduler.DisposeAsync(); await startup;
        clock.Advance(TimeSpan.FromDays(2));
        await scheduler.SignalAsync(SyncTrigger.Resumed);
        Assert.Equal(1, calls); Assert.Null(clock.NextDue);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => scheduler.StartAsync());
    }
    [Fact]
    public async Task ResumeAndClockChangesRearmTheSameTimerForTheCurrentDate()
    {
        var clock = new ManualClock(new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
        await using var scheduler = new DailySyncScheduler(clock, (_, _) => Task.CompletedTask);
        await scheduler.StartAsync();
        // Simulate a resume with a changed wall clock, without firing the old timer first.
        clock.SetWallClock(new(2026, 11, 2, 1, 0, 0, TimeSpan.Zero));
        await scheduler.SignalAsync(SyncTrigger.Resumed);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 16, 0, 0, TimeSpan.Zero), clock.NextDue);
        clock.SetWallClock(new(2026, 10, 21, 1, 0, 0, TimeSpan.Zero));
        await scheduler.SignalAsync(SyncTrigger.ClockChanged);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 16, 0, 0, TimeSpan.Zero), clock.NextDue);
        Assert.Equal(1, clock.TimerCount);
    }
    [Fact]
    public async Task EvaluationFailureDoesNotCreateRetryTimer()
    {
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        await using var scheduler = new DailySyncScheduler(clock, (_, _) => throw new IOException("injected"));
        await scheduler.StartAsync();
        Assert.IsType<IOException>(scheduler.LastFailure);
        Assert.Equal(1, clock.TimerCount);
    }
}
