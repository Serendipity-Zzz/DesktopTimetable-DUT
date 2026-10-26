using DlutTimetable.Domain;

namespace DlutTimetable.Application;

// Legacy v1.3 policy, retained for old checkpoint compatibility tests. Not wired into Desktop v1.4.
// The persistent store reserves admission atomically BEFORE any caller may send a request.
public sealed class DailySyncPolicy(ISyncCheckpointStore store, TimeProvider timeProvider)
{
    public Task<SyncAdmission> TryBeginAsync(SyncContext context, SyncIntent intent, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(context.AccountId)) return Task.FromResult(new SyncAdmission(AdmissionReason.NoAccount));
        if (context.ImportOnly) return Task.FromResult(new SyncAdmission(AdmissionReason.ImportOnly));
        if (!context.NetworkAvailable) return Task.FromResult(new SyncAdmission(AdmissionReason.Offline));
        var now = timeProvider.GetUtcNow();
        return store.TryBeginAsync(context.AccountId, context.TermId, intent, SchoolCalendar.ShanghaiDate(now), now, ct);
    }
    public TimeSpan UntilNextDay()
    {
        var now = timeProvider.GetUtcNow();
        return SchoolCalendar.NextShanghaiMidnight(now) - now;
    }
}
