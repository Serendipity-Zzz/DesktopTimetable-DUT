using System.Diagnostics;
using System.IO;
using System.Text.Json;
using DlutTimetable.Contracts;
using DlutTimetable.Windows;

namespace DlutTimetable.Windows.IntegrationTests;

public sealed class AuthWorkerTests
{
    private static string Root()
    {
        var path=new DirectoryInfo(AppContext.BaseDirectory);
        while(path is not null && !File.Exists(Path.Combine(path.FullName,"DlutTimetable.slnx")))path=path.Parent;
        return path?.FullName??throw new InvalidOperationException("Repository missing");
    }
    private static string Worker()=>Path.Combine(Root(),"src/DlutTimetable.AuthWorker/bin/Release/net10.0-windows/DlutTimetable.AuthWorker.exe");
    [Fact]
    public async Task WorkerTraceUsesTypedStagesAndTheParentCorrelationWithoutLoggingTheNonce()
    {
        var id=Guid.NewGuid();await using var supervisor=new AuthWorkerSupervisor(Worker(),()=>new(2));
        var run=await supervisor.RunAsync(WorkerTaskKind.ReadTimetable,new(2),TimeSpan.FromSeconds(10),correlationId:id);
        Assert.Equal(id,run.Response!.RequestId);
        var path=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"DLUTDesktopTimetable","logs","auth-worker.log");
        var lines=File.ReadAllLines(path).Where(line=>line.Contains($"pid={run.ProcessId} ",StringComparison.Ordinal)).ToArray();
        Assert.Contains(lines,line=>line.Contains($"request={id:N} RequestValidated",StringComparison.Ordinal));
        Assert.DoesNotContain(lines,line=>line.Contains(run.Response.Nonce,StringComparison.Ordinal));
        var assembly=System.Reflection.Assembly.LoadFrom(Path.ChangeExtension(Worker(),".dll"));
        var stage=assembly.GetType("DlutTimetable.AuthWorker.WorkerTrace")!.GetMethod("Stage")!;
        Assert.True(stage.GetParameters()[0].ParameterType.IsEnum);
    }
    [Fact]
    public async Task WorkerRejectsDirectLaunchWithoutRestrictedPipe()
    {
        using var process=Process.Start(new ProcessStartInfo(Worker()){UseShellExecute=false,CreateNoWindow=true})!;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));await process.WaitForExitAsync(deadline.Token);
        Assert.Equal(78,process.ExitCode);
    }
    [Fact]
    public async Task RestrictedRoundtripReturnsUnsupportedWithoutInventingDatasetOrBrowser()
    {
        await using var supervisor=new AuthWorkerSupervisor(Worker(),()=>new(2));
        var run=await supervisor.RunAsync(WorkerTaskKind.ReadTimetable,new(2),TimeSpan.FromSeconds(10));
        Assert.Equal(0,run.ExitCode);Assert.False(run.Forced);Assert.False(run.Cancelled);Assert.False(run.TimedOut);
        Assert.Equal(WorkerStatus.UnsupportedSchema,run.Response!.Status);Assert.Null(run.Response.DatasetJson);Assert.Null(run.Response.Diagnostics);
    }
    [Fact]
    public async Task ChangedGenerationRejectsEvenAnOtherwiseValidWorkerResult()
    {
        await using var supervisor=new AuthWorkerSupervisor(Worker(),()=>new(3));
        await Assert.ThrowsAsync<InvalidDataException>(()=>supervisor.RunAsync(WorkerTaskKind.ReadTimetable,new(2),TimeSpan.FromSeconds(10)));
        Assert.False(supervisor.Busy);
    }
    [Fact]
    [Trait("Category","InteractiveBrowser")]
    public async Task RunningBrowserDeadlineCancelsAndTerminatesObservedGroupWithinExitBudget()
    {
        await using var supervisor=new AuthWorkerSupervisor(Worker(),()=>new(2));var watch=Stopwatch.StartNew();
        var run=await supervisor.RunAsync(WorkerTaskKind.InteractiveLogin,new(2),TimeSpan.FromSeconds(2));
        Assert.True(run.TimedOut);Assert.False(run.Cancelled);Assert.False(supervisor.Busy);Assert.Null(supervisor.ActiveProcessId);
        Assert.True(watch.Elapsed<TimeSpan.FromSeconds(10));
        foreach(var id in run.Response?.Diagnostics?.ProcessIds??[])
        {Assert.Throws<ArgumentException>(()=>Process.GetProcessById(id));}
    }
    [Fact]
    public async Task PreCancelledBatchDoesNotStartWorker()
    {
        await using var supervisor=new AuthWorkerSupervisor(Worker(),()=>new(2));using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>supervisor.RunAsync(WorkerTaskKind.InteractiveLogin,new(2),TimeSpan.FromSeconds(10),cancel.Token));
    }
    [Fact]
    public void DesktopHasNoBrowserAssemblyReferenceAndWorkerIsDeployedSeparately()
    {
        var directory=Path.Combine(Root(),"src/DlutTimetable.Desktop/bin/Release/net10.0-windows10.0.19041.0");
        Assert.True(File.Exists(Path.Combine(directory,"AuthWorker/DlutTimetable.AuthWorker.exe")));
        Assert.Empty(Directory.GetFiles(directory,"Microsoft.Web.WebView2*.dll",SearchOption.TopDirectoryOnly));
        using var deps=JsonDocument.Parse(File.ReadAllText(Path.Combine(directory,"DlutTimetable.Desktop.deps.json")));
        Assert.DoesNotContain(deps.RootElement.GetProperty("libraries").EnumerateObject(),entry=>entry.Name.StartsWith("Microsoft.Web.WebView2/",StringComparison.Ordinal));
    }
}
