using DlutTimetable.Application;

namespace DlutTimetable.Tests;

public sealed class ExitCoordinatorTests
{
    [Fact]
    public async Task RepeatedExitOriginsWaitForTheSameInFlightCleanup()
    {
        var coordinator=new ExitCoordinator();
        var drain=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started=0;
        var first=coordinator.RunAsync(async()=>{Interlocked.Increment(ref started);await drain.Task;});
        var otherOrigins=await Task.WhenAll(Enumerable.Range(0,20).Select(_=>Task.Run(()=>
            new[]{coordinator.RunAsync(()=>throw new InvalidOperationException("must not run twice"))})));
        foreach(var task in otherOrigins.SelectMany(t=>t)){Assert.Same(first,task);Assert.False(task.IsCompleted);}
        Assert.Equal(1,started);
        drain.SetResult();await first;
        Assert.Same(first,coordinator.RunAsync(()=>throw new InvalidOperationException("exit is final")));
    }
    [Fact]
    public async Task CleanupFailureRemainsObservableAndCannotStartASecondShutdown()
    {
        var coordinator=new ExitCoordinator();var failure=new IOException("injected shutdown failure");
        var first=coordinator.RunAsync(()=>throw failure);
        Assert.Same(failure,await Assert.ThrowsAsync<IOException>(()=>first));
        var repeated=coordinator.RunAsync(()=>Task.CompletedTask);
        Assert.Same(first,repeated);
        Assert.Same(failure,await Assert.ThrowsAsync<IOException>(()=>repeated));
    }
}
