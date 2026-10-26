using DlutTimetable.Contracts;

namespace DlutTimetable.Application;

public sealed class ManualRefreshGate
{
    private int active;
    public bool Busy=>Volatile.Read(ref active)!=0;
    public async Task<bool> RunAsync(CommandOrigin origin,Func<CancellationToken,Task> openAndRefresh,CancellationToken cancellationToken=default)
    {
        if(origin is not (CommandOrigin.Toolbar or CommandOrigin.Tray or CommandOrigin.Settings))return false;
        cancellationToken.ThrowIfCancellationRequested();
        if(Interlocked.CompareExchange(ref active,1,0)!=0)return false;
        try {await openAndRefresh(cancellationToken);return true;}
        finally {Volatile.Write(ref active,0);}
    }
}
