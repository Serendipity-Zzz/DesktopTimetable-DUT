namespace DlutTimetable.Contracts;

public enum AppCommandKind
{
    OpenSettings, TogglePin, DockToEdge, CollapseToBall, RestoreTimetable,
    ManualRefresh, LoginAndRetry, ApplySettings, SetStartup, ExitApplication
}
public enum CommandOrigin { Toolbar, Tray, Settings, System }
public sealed record AppCommand(Guid CommandId, AppCommandKind Kind, CommandOrigin Origin);
public readonly record struct AccountGeneration(long Value);
public enum WorkerTaskKind { InteractiveLogin, ReadTimetable }
public enum WorkerStatus { Completed, NeedsLogin, Cancelled, NetworkFailure, UnsupportedSchema, TooLarge }
public enum ErrorCode
{
    None, Offline, NeedsLogin, SchemaChanged, Incomplete, InvalidDataset, TooLarge,
    Cancelled, StaleGeneration, Busy, RateLimited, StorageFailure, CorruptSettings,
    UnsupportedSettingsVersion, StartupApplyFailed, WorkerTimeout
}
public static class WorkerProtocol
{
    public const int Version = 4;
    public const int MaxMessageBytes = 5 * 1024 * 1024;
}
public enum WorkerControlKind { Cancel, Shutdown }
public sealed record WorkerControl(int ProtocolVersion, Guid RequestId, AccountGeneration Generation,
    string Nonce, WorkerControlKind Kind);
// No URL execution, script evaluation, or credential fields.
public sealed record WorkerRequest(int ProtocolVersion, Guid RequestId, AccountGeneration Generation,
    string Nonce, WorkerTaskKind TaskKind, string[] AllowedHosts, DateTimeOffset DeadlineUtc,string? ProfileScopeId=null);
public sealed record WorkerResponse(int ProtocolVersion, Guid RequestId, AccountGeneration Generation,
    string Nonce, WorkerStatus Status, string? DatasetJson,WorkerDiagnostics? Diagnostics=null);
public enum BrowserProcessRole { Browser, Renderer, Utility, SandboxHelper, Gpu, Plugin, PluginBroker, Other }
// Times are monotonic observations relative to opening this worker, not OS creation/exit timestamps.
public sealed record BrowserProcessObservation(int ProcessId,BrowserProcessRole Role,long FirstSeenMilliseconds,
    long LastSeenMilliseconds,long? ExitObservedMilliseconds=null);
public sealed record WorkerDiagnostics(int BrowserProcessId,int[] ProcessIds,bool BrowserExited,bool BrowserExitEventObserved=false,
    ObservationReport? Observation=null,DateTimeOffset? RetryAfterUtc=null,BrowserProcessObservation[]? Processes=null,bool ProcessTrackingTruncated=false);
