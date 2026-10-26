using System.IO;
using System.Windows;
using DlutTimetable.Infrastructure;

namespace DlutTimetable.Desktop;

public partial class App : System.Windows.Application
{
    private SqliteStore? store;
    private SingleInstance? instance;
    private DesktopSession? session;
    private string? startupDataRoot;
    private int faultShutdown,emergencyShutdown;
    public App()
    {
        // Draw WPF's static foreground in software. Native D3D11 glass retains its
        // own bounded GPU pipeline without a second hardware renderer for ink.
        System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        DispatcherUnhandledException+=async(_,e)=>
        {e.Handled=true;await StopAfterFaultAsync(DiagnosticCode.ApplicationUnhandled);};
        AppDomain.CurrentDomain.UnhandledException+=(_,_)=>EmergencyStop(DiagnosticCode.BackgroundUnhandled);
        TaskScheduler.UnobservedTaskException+=(_,e)=>
        {
            e.SetObserved();
            if(!Dispatcher.HasShutdownStarted)Dispatcher.BeginInvoke(()=>_ = StopAfterFaultAsync(DiagnosticCode.UnobservedTask));
        };
    }
    private async Task RecordEmergencyAsync(DiagnosticCode code)
    {
        if(startupDataRoot is null)return;
        try
        {
            var journal=new DiagnosticJournal(Path.Combine(startupDataRoot,"logs"));journal.Record(code);
            await journal.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false);
        }
        catch(Exception){} // Error reporting must not recursively print the original exception.
    }
    private void EmergencyStop(DiagnosticCode code)
    {
        if(Interlocked.Exchange(ref emergencyShutdown,1)==0)RecordEmergencyAsync(code).GetAwaiter().GetResult();
        // The owned browser Job closes with this process. SQLite transactions are
        // recovered on next open; no attempt is made to continue an unknown state.
        Environment.Exit(1);
    }
    private async Task StopAfterFaultAsync(DiagnosticCode code)
    {
        if(Interlocked.Exchange(ref faultShutdown,1)!=0){EmergencyStop(code);return;}
        try
        {
            if(session is not null)await session.ExitAfterFaultAsync(code);
            else{await RecordEmergencyAsync(code);Shutdown(1);}
        }
        catch(Exception){EmergencyStop(code);}
    }
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            if(e.Args.Contains("--uninstall"))
            {var uninstaller=new UninstallerWindow(e.Args);MainWindow=uninstaller;uninstaller.Show();return;}
            if(e.Args.Contains("--install"))
            {var installer=new InstallerWindow(e.Args);MainWindow=installer;installer.Show();return;}
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DLUTDesktopTimetable");
            var dataIndex = Array.IndexOf(e.Args, "--data-directory");
            if (dataIndex >= 0 && dataIndex + 1 < e.Args.Length) root = Path.GetFullPath(e.Args[dataIndex + 1]);
            startupDataRoot=root;store = new SqliteStore(Path.Combine(root, "timetable.db"));
            if (e.Args.Contains("--workbench"))
            { var window = new WorkbenchWindow(store); MainWindow = window; window.Show(); return; }
            instance = new(root);
            var background=e.Args.Contains("--background");
            if (!instance.IsOwner) { if(!background)await instance.RestoreExistingAsync(); await instance.DisposeAsync(); Shutdown(); return; }
            var demo=e.Args.Contains("--desktop-demo");
            session = await DesktopSession.CreateAsync(store, root, instance, demo, startupAllowed:!demo && dataIndex<0,background:background);
        }
        catch (Exception)
        {
            var id=Guid.NewGuid();
            if(startupDataRoot is not null)
            {
                try
                {
                    var journal=new DiagnosticJournal(Path.Combine(startupDataRoot,"logs"));
                    journal.Record(DiagnosticCode.StartupFailed,id);
                    await journal.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
                }
                catch(Exception logError) when(logError is IOException or UnauthorizedAccessException or ArgumentException or TimeoutException){}
            }
            if(!e.Args.Contains("--background"))MessageBox.Show($"启动失败，原数据保留。请检查文件权限或使用匹配版本。\nStartupFailed · {id:N}","大工课表");
            Shutdown(1);
        }
    }
    protected override void OnExit(ExitEventArgs e)
    {
        store?.Dispose();
        base.OnExit(e);
    }
}
