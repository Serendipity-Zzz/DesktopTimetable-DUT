using DlutTimetable.Application;
using DlutTimetable.Infrastructure;

namespace DlutTimetable.Tests;

public sealed class SchedulerIntegrationTests
{
    [Fact]
    public async Task SystemEventBatchesConsumeQuotaOnlyWhenEligibleAndOnlyForTheCurrentDay()
    {
        var folder = Path.Combine(Path.GetTempPath(), "DlutScheduler-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new SqliteStore(Path.Combine(folder, "test.db"));
            var clock = new ManualClock(new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero));
            var policy = new DailySyncPolicy(store, clock);
            var online = false;
            var attempts = 0;
            await using var scheduler = new DailySyncScheduler(clock, async (_, ct) =>
            {
                var admission = await policy.TryBeginAsync(new("fixture-account", "term", online), SyncIntent.Automatic, ct);
                if (!admission.Allowed) return;
                attempts++;
                await store.FinishAsync("fixture-account", SyncStatus.Failed, "InjectedOffline", ct: ct);
            });
            await scheduler.StartAsync();
            Assert.Null(await store.ReadCheckpointAsync("fixture-account"));
            online = true;
            await scheduler.SignalAsync(SyncTrigger.NetworkRestored);
            await Task.WhenAll(Enumerable.Range(0, 25).Select(_ => scheduler.SignalAsync(SyncTrigger.Resumed | SyncTrigger.NetworkRestored)));
            Assert.Equal(1, attempts);
            clock.Advance(TimeSpan.FromHours(4));
            await scheduler.WhenIdleAsync();
            Assert.Equal(2, attempts);
            Assert.Equal(new DateOnly(2026, 10, 23), (await store.ReadCheckpointAsync("fixture-account"))!.LastAutoDate);
            // Nine missed dates cause one attempt for the present day, not nine replayed batches.
            clock.Advance(TimeSpan.FromDays(9));
            await scheduler.WhenIdleAsync();
            Assert.Equal(3, attempts);
            Assert.Equal(new DateOnly(2026, 11, 1), (await store.ReadCheckpointAsync("fixture-account"))!.LastAutoDate);
            Assert.Equal(1, clock.TimerCount);
        }
        finally { if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true); }
    }
}
