namespace DlutTimetable.Contracts;

/// <summary>Bounded role observations only; never authorizes termination by PID or role.</summary>
public sealed class BrowserProcessTracker(TimeProvider clock)
{
    public const int MaximumProcesses=128;
    private readonly long started=clock.GetTimestamp();
    private readonly Dictionary<int,BrowserProcessObservation> entries=[];
    public bool Truncated {get;private set;}
    private long Now=>Math.Max(0,(long)clock.GetElapsedTime(started).TotalMilliseconds);
    public int[] ProcessIds=>entries.Keys.ToArray();
    public BrowserProcessObservation[] Snapshot()=>entries.Values.ToArray();
    public void Observe(int processId,BrowserProcessRole role)
    {
        if(processId<=0 || !Enum.IsDefined(role))throw new ArgumentOutOfRangeException(nameof(processId));
        var now=Now;
        if(entries.TryGetValue(processId,out var prior))
            entries[processId]=prior with{LastSeenMilliseconds=now,ExitObservedMilliseconds=null};
        else if(entries.Count<MaximumProcesses)entries.Add(processId,new(processId,role,now,now));
        else Truncated=true;
    }
    // Call only after independently observing these tracked processes exit.
    // A process disappearing from GetProcessInfos is not sufficient evidence.
    public void ConfirmExited(IEnumerable<int> observedProcessIds)
    {
        var now=Now;
        foreach(var id in observedProcessIds)
            if(entries.TryGetValue(id,out var entry))entries[id]=entry with{ExitObservedMilliseconds=now};
    }
}
