namespace DlutTimetable.Application;

/// <summary>Monotonic presentation state; the renderer owns its temporary frame subscription.</summary>
public sealed class MotionTimeline(TimeProvider clock, double initialProgress)
{
    public const int NormalMilliseconds=320,ReducedMilliseconds=80,MinimumRetargetMilliseconds=48;
    private long started;
    private double from=initialProgress, target=initialProgress;
    public long Generation { get; private set; }
    public bool Running { get; private set; }
    public TimeSpan Duration { get; private set; }
    public double Fraction => !Running ? 1 : Math.Clamp(clock.GetElapsedTime(started).TotalMilliseconds/Duration.TotalMilliseconds,0,1);
    public double Progress => from+(target-from)*DlutTimetable.Domain.BallMotionPath.Smooth(Fraction);
    public void Retarget(bool toBall,long generation,bool reduced)
    {
        var next=toBall ? 1d : 0;
        Generation=generation;
        if (target==next) return;
        var current=Progress;
        from=current;target=next;started=clock.GetTimestamp();
        // Reserve time inside the 420 ms end-to-end budget for the last native frame
        // and the input-priority handoff before committing the stable target.
        // A near-endpoint reversal must not remove its current hit target in one millisecond
        // while the following native press is still being routed to the window.
        Duration=TimeSpan.FromMilliseconds(Math.Max(MinimumRetargetMilliseconds,
            (reduced?ReducedMilliseconds:NormalMilliseconds)*Math.Abs(target-from)));
        Running=true;
    }
    public bool Advance()
    {
        if (!Running || Fraction<1) return false;
        from=target;Running=false;return true;
    }
}
