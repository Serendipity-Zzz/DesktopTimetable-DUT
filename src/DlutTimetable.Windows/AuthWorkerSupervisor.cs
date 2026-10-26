using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using DlutTimetable.Contracts;
using Microsoft.Win32.SafeHandles;

namespace DlutTimetable.Windows;

public sealed record WorkerRun(WorkerResponse? Response,bool Cancelled,bool TimedOut,bool Forced,int ProcessId,int? ExitCode,int JobProcessesReaped=0);

/// <summary>Owns exactly one child worker and restricted duplex pipe. Never loads WebView2.</summary>
public sealed class AuthWorkerSupervisor(string executable,Func<AccountGeneration> currentGeneration,string? profileScopeId=null) : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime=new();
    private Task<WorkerRun>? active;
    private bool disposed;
    public bool Busy=>active is {IsCompleted:false};
    public int? ActiveProcessId {get;private set;}
    public Task<WorkerRun> RunAsync(WorkerTaskKind task,AccountGeneration generation,TimeSpan timeout,CancellationToken ct=default,Guid? correlationId=null)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(Busy)throw new InvalidOperationException("A worker is already running.");
        if(timeout<=TimeSpan.Zero || timeout>TimeSpan.FromMinutes(15))throw new ArgumentOutOfRangeException(nameof(timeout));
        if(correlationId==Guid.Empty)throw new ArgumentException("Empty diagnostic correlation.",nameof(correlationId));
        active=RunCoreAsync(task,generation,timeout,ct,correlationId??Guid.NewGuid());return active;
    }
    private async Task<WorkerRun> RunCoreAsync(WorkerTaskKind task,AccountGeneration generation,TimeSpan timeout,CancellationToken cancellation,Guid requestId)
    {
        var pipeName="DlutAuth-"+Guid.NewGuid().ToString("N");var nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var request=new WorkerRequest(WorkerProtocol.Version,requestId,generation,nonce,task,SchoolNavigation.Hosts.Append(SchoolNavigation.WebVpnHost).ToArray(),DateTimeOffset.UtcNow+timeout,profileScopeId);
        WorkerWire.Validate(request,nonce,DateTimeOffset.UtcNow);
        using var deadline=new CancellationTokenSource(timeout);
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(cancellation,lifetime.Token,deadline.Token);
        stop.Token.ThrowIfCancellationRequested();
        await using var pipe=new NamedPipeServerStream(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true};
        // Development uses the repository-local .NET runtime. Child apphosts must resolve
        // the same runtime instead of displaying an install dialog and waiting forever.
        var runtimeRoot=new DirectoryInfo(RuntimeEnvironment.GetRuntimeDirectory()).Parent?.Parent?.Parent?.FullName;
        if(runtimeRoot is not null && File.Exists(Path.Combine(runtimeRoot,"dotnet.exe")))
        {
            start.Environment["DOTNET_ROOT"]=runtimeRoot;
            start.Environment["DOTNET_ROOT_"+RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()]=runtimeRoot;
        }
        start.Environment["DOTNET_DISABLE_GUI_ERRORS"]="1";
        start.ArgumentList.Add("--pipe");start.ArgumentList.Add(pipeName);start.ArgumentList.Add("--nonce");start.ArgumentList.Add(nonce);
        using var process=Process.Start(start)??throw new IOException("Cannot start authentication worker.");
        ActiveProcessId=process.Id;
        WorkerJob? job=null;var requestSent=false;var forced=false;WorkerResponse? response=null;var cancelled=false;var timedOut=false;var reaped=0;
        try
        {
            job=new(process);
            var connection=pipe.WaitForConnectionAsync(stop.Token);
            var earlyExit=process.WaitForExitAsync(stop.Token);
            if(await Task.WhenAny(connection,earlyExit)!=connection)
            {await earlyExit;throw new IOException("Authentication worker exited before connecting.");}
            await connection;
            if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var connected) || connected!=(uint)process.Id)
                throw new InvalidDataException("Unexpected worker pipe client.");
            await WorkerWire.WriteAsync(pipe,request,stop.Token);requestSent=true;
            response=await WorkerWire.ReadAsync<WorkerResponse>(pipe,stop.Token);
            WorkerWire.Validate(response,request,currentGeneration());
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested)
        {
            cancelled=!deadline.IsCancellationRequested;timedOut=deadline.IsCancellationRequested;
            if(requestSent && pipe.IsConnected)
            {
                using var grace=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    await WorkerWire.WriteAsync(pipe,new WorkerControl(WorkerProtocol.Version,request.RequestId,generation,nonce,WorkerControlKind.Cancel),grace.Token);
                    var acknowledgement=await WorkerWire.ReadAsync<WorkerResponse>(pipe,grace.Token);
                    // Only a payload-free cancellation acknowledgement may cross a cancelled
                    // account generation. A dataset arriving late is never accepted here.
                    WorkerWire.Validate(acknowledgement,request,request.Generation);
                    if(acknowledgement.Status==WorkerStatus.Cancelled)response=acknowledgement;
                }
                catch(Exception ex) when(ex is IOException or InvalidDataException or OperationCanceledException){}
            }
        }
        finally
        {
            try
            {
                using var grace=new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await process.WaitForExitAsync(grace.Token);
            }
            catch(OperationCanceledException)
            {
                forced=true;
                // Closing the job only affects processes assigned to this owned job.
                job?.Dispose();job=null;
                if(!process.HasExited)process.Kill(entireProcessTree:false);
                using var final=new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(final.Token);
            }
            finally
            {
                if(job is not null)
                {try {reaped=job.ActiveProcessCount();forced|=reaped>0;}finally {job.Dispose();}}
                ActiveProcessId=null;
            }
        }
        return new(response,cancelled,timedOut,forced,process.Id,process.ExitCode,reaped);
    }
    public async ValueTask DisposeAsync()
    {
        if(disposed)return;disposed=true;await lifetime.CancelAsync();
        try {if(active is not null)await active;}
        catch(Exception ex) when(ex is IOException or InvalidDataException or OperationCanceledException
            or InvalidOperationException or System.ComponentModel.Win32Exception){}
        finally {lifetime.Dispose();}
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint processId);
}
