using System.IO;
using System.Text;

namespace DlutTimetable.AuthWorker;

internal enum WorkerTraceStage { Started, Failed, Exit, Connected, RequestValidated, ResponseSent, ImportRejected, WindowShown, ClosingBrowser, EnvironmentCreated, ControllerCreated, ImportResponseRejected, AcademicCasRedirect, NavigationBlocked, BrowserFailed, BrowserExitEvent, Unhandled }

internal static class WorkerTrace
{
    private static Guid correlation;
    public static void Bind(Guid requestId)
    {
        if(requestId==Guid.Empty || correlation!=Guid.Empty)throw new InvalidOperationException("Invalid diagnostic binding.");
        correlation=requestId;
    }
    public static void Stage(WorkerTraceStage stage)
    {
        if(!Enum.IsDefined(stage))throw new ArgumentOutOfRangeException(nameof(stage));
        try
        {
            var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DLUTDesktopTimetable","logs");
            Directory.CreateDirectory(folder);var file=Path.Combine(folder,"auth-worker.log");
            var line=$"{DateTimeOffset.UtcNow:O} pid={Environment.ProcessId} request={correlation:N} {stage}\n";
            if(File.Exists(file) && new FileInfo(file).Length+Encoding.UTF8.GetByteCount(line)>65536)
            {if(File.Exists(file+".1"))File.Move(file+".1",file+".2",true);File.Move(file,file+".1",true);}
            // Fixed internal stage names only. Never include exception text, URLs, nonce, headers or response bodies.
            File.AppendAllText(file,line);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException){}
    }
}
