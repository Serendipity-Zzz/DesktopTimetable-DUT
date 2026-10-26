using DlutTimetable.Domain;

namespace DlutTimetable.Application;

public interface ICurrentDatasetStore
{
    Task<ValidatedDataset?> ReadAsync(string? accountId = null, CancellationToken ct = default);
    Task ReplaceAsync(ValidatedDataset dataset, CancellationToken ct = default);
}
public interface IPrivateNoteStore
{
    Task<PrivateNote?> ReadNoteAsync(PrivateNoteKey key,CancellationToken ct=default);
    Task SaveNoteAsync(PrivateNote note,CancellationToken ct=default);
    Task DeleteNoteAsync(PrivateNoteKey key,CancellationToken ct=default);
}
public enum SyncIntent { Automatic, Manual }
public enum SyncStatus { Idle, Running, Succeeded, Failed, NeedsLogin, Cancelled, Interrupted }
public enum AdmissionReason { Admitted, NoAccount, Offline, ImportOnly, AlreadyAttempted, Busy, RateLimited }
public sealed record SyncContext(string? AccountId, string TermId, bool NetworkAvailable, bool ImportOnly = false);
public sealed record SyncAdmission(AdmissionReason Reason, DateTimeOffset? RetryAt = null)
{
    public bool Allowed => Reason == AdmissionReason.Admitted;
}
public sealed record SyncCheckpoint(string AccountId, DateOnly? LastAutoDate, string? LastAutoTermId,
    DateTimeOffset? LastAttemptAtUtc, DateTimeOffset? LastSuccessAtUtc, DateTimeOffset? LastManualAtUtc,
    DateTimeOffset? RetryAfterUtc, SyncStatus Status, string? ReasonCode);
public interface ISyncCheckpointStore
{
    Task<SyncAdmission> TryBeginAsync(string accountId, string termId, SyncIntent intent,
        DateOnly today, DateTimeOffset now, CancellationToken ct = default);
    Task FinishAsync(string accountId, SyncStatus status, string? reasonCode = null,
        DateTimeOffset? retryAfterUtc = null, CancellationToken ct = default);
    Task<SyncCheckpoint?> ReadCheckpointAsync(string accountId, CancellationToken ct = default);
    Task RecoverInterruptedAsync(CancellationToken ct = default);
}
