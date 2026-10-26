using System.IO;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace DlutTimetable.Desktop;

internal sealed class SingleInstance : IAsyncDisposable
{
    private readonly Mutex mutex;
    private readonly string pipeName;
    private readonly CancellationTokenSource lifetime = new();
    private Task? listener;
    private bool disposed;
    public bool IsOwner { get; }
    public SingleInstance(string dataRoot)
    {
        pipeName = NameFor(dataRoot);
        mutex = new(false, "Local\\" + pipeName);
        try { IsOwner = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { IsOwner = true; }
    }
    public void Listen(Action restore, Action exit)
    {
        if (!IsOwner || listener is not null) return;
        listener = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    await using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await pipe.WaitForConnectionAsync(lifetime.Token);
                    var buffer = new byte[1];
                    using var request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                    request.CancelAfter(TimeSpan.FromSeconds(1));
                    if (await pipe.ReadAsync(buffer, request.Token) == 1 && !lifetime.IsCancellationRequested)
                    { if(buffer[0]==1)restore();else if(buffer[0]==2)exit(); }
                }
                catch (OperationCanceledException) { }
                catch (IOException) when (!lifetime.IsCancellationRequested) { }
            }
        });
    }
    public async Task RestoreExistingAsync()
    {
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(1000);
            await pipe.WriteAsync(new byte[] { 1 });
        }
        catch (Exception ex) when (ex is TimeoutException or IOException) { /* Existing instance may be exiting. */ }
    }
    private static string NameFor(string dataRoot)
    {
        var sid=WindowsIdentity.GetCurrent().User?.Value??throw new InvalidOperationException("无法确定当前用户。");
        return "DlutTimetable-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid+Path.TrimEndingDirectorySeparator(Path.GetFullPath(dataRoot)).ToUpperInvariant())))[..24];
    }
    public static async Task StopExistingAsync(string dataRoot,CancellationToken ct)
    {
        var name=NameFor(dataRoot);
        // The mutex must be acquired/released on the same OS thread; never hold it across await.
        static bool Released(string name,int timeout)
        {
            Mutex mutex;
            try {mutex=Mutex.OpenExisting("Local\\"+name);}catch(WaitHandleCannotBeOpenedException){return true;}
            using(mutex)
            {
                bool acquired;
                try {acquired=mutex.WaitOne(timeout);}catch(AbandonedMutexException){acquired=true;}
                if(acquired)mutex.ReleaseMutex();return acquired;
            }
        }
        if(await Task.Run(()=>Released(name,0),ct))return;
        await using(var pipe=new NamedPipeClientStream(".",name,PipeDirection.Out,PipeOptions.Asynchronous))
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(ct);deadline.CancelAfter(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(deadline.Token);await pipe.WriteAsync(new byte[]{2},deadline.Token);
        }
        if(!await Task.Run(()=>Released(name,15000),ct))throw new IOException("课表仍在运行，请从设置正常退出程序后重试安装；不会强制结束进程。");
    }
    public async ValueTask DisposeAsync()
    {
        if (disposed) return; disposed = true;
        await lifetime.CancelAsync();
        if (listener is not null) await listener;
        if (IsOwner) mutex.ReleaseMutex();
        mutex.Dispose(); lifetime.Dispose();
    }
}
