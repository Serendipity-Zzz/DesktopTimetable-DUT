using System.Windows;

namespace DlutTimetable.AuthWorker;

internal static class WorkerFaultPolicy
{
    private static int stopping;
    public static void Register(System.Windows.Application app)
    {
        app.DispatcherUnhandledException+=(_,e)=>{e.Handled=true;Stop();};
        AppDomain.CurrentDomain.UnhandledException+=(_,_)=>Stop();
        TaskScheduler.UnobservedTaskException+=(_,e)=>{e.SetObserved();Stop();};
    }
    private static void Stop()
    {
        if(Interlocked.Exchange(ref stopping,1)==0)
        {try{WorkerTrace.Stage(WorkerTraceStage.Unhandled);}catch(Exception){}}
        // Never serialize a browser exception. The parent observes pipe/worker exit
        // and closes its owned Job, including any remaining browser processes.
        Environment.Exit(74);
    }
}
