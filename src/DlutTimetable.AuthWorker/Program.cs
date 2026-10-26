using System.IO;
using System.IO.Pipes;
using System.Windows;
using DlutTimetable.Contracts;

namespace DlutTimetable.AuthWorker;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length!=4 || args[0]!="--pipe" || !args[1].StartsWith("DlutAuth-",StringComparison.Ordinal)
            || args[1].Length!=41 || !Guid.TryParseExact(args[1][9..],"N",out _)
            || args[2]!="--nonce" || !WorkerWire.SameNonce(args[3],args[3]))return 78;
        var app=new Application {ShutdownMode=ShutdownMode.OnExplicitShutdown};
        WorkerFaultPolicy.Register(app);
        app.Startup+=async (_,_)=>
        {
            var code=0;
            WorkerTrace.Stage(WorkerTraceStage.Started);
            try {await ServeAsync(args[1],args[3]);}
            catch(Exception ex) when(ex is IOException or InvalidDataException or OperationCanceledException
                or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidOperationException)
            { WorkerTrace.Stage(WorkerTraceStage.Failed);code=74; }
            WorkerTrace.Stage(WorkerTraceStage.Exit);
            app.Shutdown(code);
        };
        return app.Run();
    }
    private static async Task ServeAsync(string name,string nonce)
    {
        using var handshake=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(handshake.Token);
        WorkerTrace.Stage(WorkerTraceStage.Connected);
        var request=await WorkerWire.ReadAsync<WorkerRequest>(pipe,handshake.Token);
        WorkerWire.Validate(request,nonce,DateTimeOffset.UtcNow);
        WorkerTrace.Bind(request.RequestId);
        WorkerTrace.Stage(WorkerTraceStage.RequestValidated);
        WorkerStatus status;WorkerDiagnostics? diagnostics=null;string? datasetJson=null;
        if(request.TaskKind==WorkerTaskKind.ReadTimetable)status=WorkerStatus.UnsupportedSchema;
        else
        {
            using var deadline=new CancellationTokenSource(request.DeadlineUtc-DateTimeOffset.UtcNow);
            var browser=new LoginWindow(request);
            using var controlStop=new CancellationTokenSource();
            var control=ObserveControlAsync(pipe,request,browser,controlStop.Token);
            using var expired=deadline.Token.Register(()=>browser.Dispatcher.BeginInvoke(()=>browser.Cancel()));
            try { (status,diagnostics)=await browser.RunAsync();datasetJson=browser.DatasetJson; }
            finally {await controlStop.CancelAsync();await control;}
        }
        using var reply=new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await WorkerWire.WriteAsync(pipe,new WorkerResponse(WorkerProtocol.Version,request.RequestId,request.Generation,nonce,status,datasetJson,diagnostics),reply.Token);
        WorkerTrace.Stage(WorkerTraceStage.ResponseSent);
    }
    private static async Task ObserveControlAsync(Stream pipe,WorkerRequest request,LoginWindow browser,CancellationToken ct)
    {
        try
        {
            var control=await WorkerWire.ReadAsync<WorkerControl>(pipe,ct);WorkerWire.Validate(control,request);browser.Cancel();
        }
        catch(OperationCanceledException) when(ct.IsCancellationRequested){}
        catch(Exception ex) when(ex is IOException or InvalidDataException){browser.Cancel();}
    }
}
