namespace DlutTimetable.Application;

/// <summary>All exit origins observe the same completion, including repeated requests during cleanup.</summary>
public sealed class ExitCoordinator
{
    private readonly object gate=new();
    private TaskCompletionSource? completion;
    public Task RunAsync(Func<Task> shutdown)
    {
        TaskCompletionSource owner;
        lock(gate)
        {
            if(completion is not null)return completion.Task;
            completion=owner=new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        _=CompleteAsync(shutdown,owner);
        return owner.Task;
    }
    private static async Task CompleteAsync(Func<Task> shutdown,TaskCompletionSource completion)
    {
        try {await shutdown();completion.SetResult();}
        catch(Exception ex){completion.SetException(ex);}
    }
}
