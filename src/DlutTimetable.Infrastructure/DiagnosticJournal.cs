using System.Text.Json;
using System.Threading.Channels;
using DlutTimetable.Domain;

namespace DlutTimetable.Infrastructure;

public enum DiagnosticCode
{
    SessionStarted, SessionExiting, SceneChanged, MaterialSample,
    SettingsUnreadable, SettingsWriteFailed, DatabaseReadFailed, DataOperationFailed,
    ShellRecoveryFailed, StartupOutOfSync, SchoolRefreshStarted, SchoolRefreshSucceeded,
    SchoolRefreshCancelled, SchoolRefreshTimedOut, SchoolRefreshFailed,
    ShellRecoveryRequested, ShellRecoverySucceeded, MaterialRecoveryRequested, MaterialRecoveryFinished,
    RecoveryFailed, SchoolCancelRequested, StartupFailed, ApplicationUnhandled, BackgroundUnhandled,
    UnobservedTask, CalendarSaveFailed, SessionClearFailed, StartupConfigFailed,
    ExitCancellationFailed, ExitClockFailed, ExitWorkerFailed, ExitSchoolReadFailed,
    ExitSessionClearFailed, ExitDataFailed, ExitCalendarFailed, ExitStartupFailed,
    ExitSettingsFailed, ExitDeadlineFailed, ExitResourceFailed, ExitInstanceFailed,
    InstallationFailed, InstallationLaunchFailed, UninstallationFailed
}
// Deliberately no free-text fields: callers cannot pass exception messages, URLs or payloads.
public sealed record DiagnosticEntry(long Sequence,DateTimeOffset Utc,Guid CorrelationId,DiagnosticCode Code,
    TimetableState? State,double? ElapsedMilliseconds,long? Bytes,int? Count);
public sealed record DiagnosticSnapshot(DiagnosticEntry[] Entries,long Evicted,long LogWriteFailures,long LogQueueDropped);

public sealed class DiagnosticJournal : IAsyncDisposable
{
    public static async Task<Guid> WriteFaultAsync(string directory,DiagnosticCode code)
    {
        if(!Enum.IsDefined(code))throw new ArgumentOutOfRangeException(nameof(code));
        var id=Guid.NewGuid();
        try
        {
            var journal=new DiagnosticJournal(directory);journal.Record(code,id);
            await journal.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or ArgumentException or TimeoutException or System.Security.SecurityException){}
        return id;
    }
    public const int Capacity=4096,LogFiles=3,MaximumLogBytes=10*1024*1024;
    private readonly object gate=new();
    private readonly Queue<DiagnosticEntry> entries=new();
    private readonly Channel<DiagnosticEntry> pending=Channel.CreateBounded<DiagnosticEntry>(new BoundedChannelOptions(256)
        {SingleReader=true,SingleWriter=false,FullMode=BoundedChannelFullMode.Wait});
    private readonly string file;
    private readonly int logBytes;
    private readonly Task writer;
    private long sequence,evicted,writeFailures,queueDropped;
    private bool closed;
    private static readonly JsonSerializerOptions Options=new(){Converters={new System.Text.Json.Serialization.JsonStringEnumConverter()}};
    public DiagnosticJournal(string directory,int logByteLimit=MaximumLogBytes)
    {
        if(logByteLimit is <512 or >MaximumLogBytes)throw new ArgumentOutOfRangeException(nameof(logByteLimit));
        file=Path.Combine(Path.GetFullPath(directory),"runtime-diagnostics.jsonl");logBytes=logByteLimit;
        writer=Task.Run(WritePendingAsync);
    }
    public Guid Record(DiagnosticCode code,Guid? correlation=null,TimetableState? state=null,
        double? elapsedMilliseconds=null,long? bytes=null,int? count=null,bool persist=true)
    {
        if(!Enum.IsDefined(code) || state is {} s && !Enum.IsDefined(s)
            || elapsedMilliseconds is {} ms && (!double.IsFinite(ms) || ms<0 || ms>86400000)
            || bytes is <0 or >4L*1024*1024*1024*1024 || count is <0 or >1000000)
            throw new ArgumentOutOfRangeException(nameof(code));
        var id=correlation is {} supplied && supplied!=Guid.Empty?supplied:Guid.NewGuid();
        lock(gate)
        {
            if(closed)return id;
            var item=new DiagnosticEntry(++sequence,DateTimeOffset.UtcNow,id,code,state,elapsedMilliseconds,bytes,count);
            if(entries.Count==Capacity){entries.Dequeue();evicted++;}
            entries.Enqueue(item);
            if(persist && !pending.Writer.TryWrite(item))queueDropped++;
        }
        return id;
    }
    public DiagnosticSnapshot Snapshot()
    {lock(gate)return new(entries.ToArray(),evicted,Interlocked.Read(ref writeFailures),queueDropped);}
    private async Task WritePendingAsync()
    {
        await foreach(var item in pending.Reader.ReadAllAsync())
        {
            try
            {
                var bytes=JsonSerializer.SerializeToUtf8Bytes(item,Options);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                if(File.Exists(file) && new FileInfo(file).Length+bytes.Length+1>logBytes)
                {
                    if(File.Exists(file+".1"))File.Move(file+".1",file+".2",true);
                    File.Move(file,file+".1",true);
                }
                await using var stream=new FileStream(file,FileMode.Append,FileAccess.Write,FileShare.Read,4096,FileOptions.Asynchronous);
                await stream.WriteAsync(bytes);await stream.WriteAsync(new byte[]{(byte)'\n'});
            }
            catch(Exception ex) when(ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {Interlocked.Increment(ref writeFailures);}
        }
    }
    public ValueTask DisposeAsync()
    {
        lock(gate){closed=true;pending.Writer.TryComplete();}
        return new(writer);
    }
}
