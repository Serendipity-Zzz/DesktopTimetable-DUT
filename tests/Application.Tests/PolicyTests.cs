using DlutTimetable.Application;

namespace DlutTimetable.Tests;

public sealed class PolicyTests
{
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 22, 16, 0, 0, TimeSpan.Zero);
    }
    private sealed class Store : ISyncCheckpointStore
    {
        public int Calls { get; private set; }
        public DateOnly ReservedDate { get; private set; }
        public Task<SyncAdmission> TryBeginAsync(string accountId, string termId, SyncIntent intent,
            DateOnly today, DateTimeOffset now, CancellationToken ct = default)
        { Calls++; ReservedDate = today; return Task.FromResult(new SyncAdmission(AdmissionReason.Admitted)); }
        public Task FinishAsync(string accountId, SyncStatus status, string? reasonCode = null, DateTimeOffset? retryAfterUtc = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SyncCheckpoint?> ReadCheckpointAsync(string accountId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task RecoverInterruptedAsync(CancellationToken ct = default) => throw new NotSupportedException();
    }
    [Theory]
    [InlineData(null, true, false, AdmissionReason.NoAccount)]
    [InlineData("a", false, false, AdmissionReason.Offline)]
    [InlineData("a", true, true, AdmissionReason.ImportOnly)]
    public async Task IneligibleConditionsDoNotConsumePersistentQuota(string? account, bool online, bool importOnly, AdmissionReason reason)
    {
        var store = new Store();
        var result = await new DailySyncPolicy(store, new Clock()).TryBeginAsync(new(account, "term", online, importOnly), SyncIntent.Automatic);
        Assert.Equal(reason, result.Reason); Assert.Equal(0, store.Calls);
    }
    [Fact]
    public async Task ReservesShanghaiDateAndComputesSingleNextDayDeadline()
    {
        var store = new Store(); var policy = new DailySyncPolicy(store, new Clock());
        Assert.True((await policy.TryBeginAsync(new("a", "term", true), SyncIntent.Automatic)).Allowed);
        Assert.Equal(new DateOnly(2026, 10, 23), store.ReservedDate);
        Assert.Equal(TimeSpan.FromHours(24), policy.UntilNextDay());
    }
}
