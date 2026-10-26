using DlutTimetable.Contracts;
using DlutTimetable.Domain;

namespace DlutTimetable.Application;

/// <summary>Called on one UI dispatcher. All deferred work is checked against the scene generation.</summary>
public sealed class WindowStateController : IDisposable
{
    private readonly TimeProvider clock;
    private readonly Action<Action> dispatch;
    private ITimer? hover;
    private bool inside, captured, disposed, presentationPaused;
    public SceneSnapshot Current { get; private set; }
    public event Action<SceneSnapshot, bool>? Changed;
    public event Action<bool, TimeSpan>? HoverTriggered;
    public WindowStateController(SceneSnapshot initial, TimeProvider clock, Action<Action> dispatch)
    {
        Current = initial with { EdgePhase = initial.State == TimetableState.EdgeDocked ? EdgePhase.Collapsed : EdgePhase.Expanded,
            Generation = 0, SettingsOpen = false, Exiting = false, BallMotion = BallMotionPhase.None };
        this.clock = clock; this.dispatch = dispatch;
    }
    private void Publish(SceneSnapshot scene, bool stable = true)
    { Current = scene; Changed?.Invoke(scene, stable); }
    private SceneSnapshot NewIntent()
    { hover?.Dispose(); hover = null; return Current with { Generation = Current.Generation + 1 }; }
    public bool Execute(AppCommandKind command)
    {
        if (disposed || Current.Exiting) return false;
        var scene = Current;
        var target = command switch
        {
            AppCommandKind.TogglePin when scene.State == TimetableState.Locked => TimetableState.Movable,
            AppCommandKind.TogglePin when scene.State == TimetableState.Movable => TimetableState.Locked,
            AppCommandKind.TogglePin when scene.State == TimetableState.EdgeDocked
                && scene.EdgePhase is EdgePhase.Expanded or EdgePhase.WaitingClose => TimetableState.Locked,
            AppCommandKind.DockToEdge when scene.State != TimetableState.Ball => TimetableState.EdgeDocked,
            AppCommandKind.CollapseToBall when scene.BallMotion == BallMotionPhase.ToBall => TimetableState.Locked,
            AppCommandKind.CollapseToBall => TimetableState.Ball,
            AppCommandKind.RestoreTimetable => TimetableState.Locked,
            _ => (TimetableState?)null
        };
        if (target is null) return false;
        var next = NewIntent() with { State = target.Value,
            EdgePhase = target == TimetableState.EdgeDocked ? EdgePhase.Collapsed : EdgePhase.Expanded };
        var motion = target == TimetableState.Ball && (scene.PanelVisible || scene.BallMotion != BallMotionPhase.None)
            ? BallMotionPhase.ToBall : target == TimetableState.Locked && (scene.State == TimetableState.Ball || scene.BallMotion != BallMotionPhase.None)
            ? BallMotionPhase.ToPanel : BallMotionPhase.None;
        next = next with { BallMotion = motion };
        captured = false; inside = false;
        Publish(next, motion == BallMotionPhase.None); return true;
    }
    public void OrbClick()
    {
        if (Current.State == TimetableState.Ball || Current.BallMotion != BallMotionPhase.None)
            Execute(Current.State == TimetableState.Ball ? AppCommandKind.RestoreTimetable : AppCommandKind.CollapseToBall);
    }
    public void CompleteBallMotion(long generation)
    {
        if (disposed || Current.Exiting || Current.Generation != generation || Current.BallMotion == BallMotionPhase.None) return;
        Publish(Current with { BallMotion = BallMotionPhase.None });
    }
    public void StabilizeBallMotion()
    {
        if (disposed || Current.Exiting || Current.BallMotion == BallMotionPhase.None) return;
        Publish(NewIntent() with { BallMotion = BallMotionPhase.None });
    }
    public void Hover(bool isInside, bool force = false)
    {
        var enteredAt = clock.GetTimestamp();
        if (disposed || presentationPaused || Current.Exiting || Current.State != TimetableState.EdgeDocked) return;
        if (inside == isInside && !force) return;
        inside = isInside;
        var next = NewIntent();
        if (next.SettingsOpen || captured)
        {
            Publish(next with { EdgePhase = next.EdgePhase == EdgePhase.WaitingClose ? EdgePhase.Expanded :
                next.EdgePhase == EdgePhase.WaitingOpen ? EdgePhase.Collapsed : next.EdgePhase }, false);
            return;
        }
        if (inside && next.EdgePhase == EdgePhase.Closing)
        { Publish(next with { EdgePhase = EdgePhase.Opening }, false); return; }
        if (inside && next.EdgePhase is EdgePhase.Collapsed or EdgePhase.WaitingOpen)
        {
            next = next with { EdgePhase = EdgePhase.WaitingOpen };
            Publish(next, false); Arm(next.Generation, true, enteredAt); return;
        }
        if (!inside && next.EdgePhase is EdgePhase.Expanded or EdgePhase.WaitingClose)
        {
            next = next with { EdgePhase = EdgePhase.WaitingClose };
            Publish(next, false); Arm(next.Generation, false, enteredAt); return;
        }
        Publish(next with { EdgePhase = next.EdgePhase == EdgePhase.WaitingOpen ? EdgePhase.Collapsed :
            next.EdgePhase == EdgePhase.WaitingClose ? EdgePhase.Expanded : next.EdgePhase }, false);
    }
    // A system presentation pause settles the user's intended state, invalidates
    // animation callbacks and cancels hover without changing settings/popups.
    public void SetPresentationPaused(bool paused)
    {
        if(disposed || Current.Exiting || presentationPaused==paused)return;
        presentationPaused=paused;
        if(!paused)return;
        inside=false;captured=false;
        Publish(NewIntent() with {BallMotion=BallMotionPhase.None,EdgePhase=Current.EdgePhase switch
        {
            EdgePhase.WaitingOpen or EdgePhase.Closing=>EdgePhase.Collapsed,
            EdgePhase.WaitingClose or EdgePhase.Opening=>EdgePhase.Expanded,
            _=>Current.EdgePhase
        }},false);
    }
    private void Arm(long generation, bool opening, long enteredAt)
    {
        if (Current.Generation != generation || disposed || Current.Exiting) return;
        hover = clock.CreateTimer(_ => dispatch(() =>
        {
            if (disposed || Current.Exiting || Current.Generation != generation || Current.SettingsOpen || captured || inside != opening) return;
            hover?.Dispose(); hover = null;
            HoverTriggered?.Invoke(opening, clock.GetElapsedTime(enteredAt));
            Publish(Current with { EdgePhase = opening ? EdgePhase.Opening : EdgePhase.Closing }, false);
        }), null, TimeSpan.FromMilliseconds(Math.Max(0, 1000 - clock.GetElapsedTime(enteredAt).TotalMilliseconds)), Timeout.InfiniteTimeSpan);
    }
    public void CompleteMotion(long generation)
    {
        if (disposed || Current.Exiting || Current.Generation != generation) return;
        var phase = Current.EdgePhase;
        if (phase is not (EdgePhase.Opening or EdgePhase.Closing)) return;
        Publish(Current with { EdgePhase = phase == EdgePhase.Opening ? EdgePhase.Expanded : EdgePhase.Collapsed });
        if (phase == EdgePhase.Opening && !inside && !Current.SettingsOpen) Hover(false, true);
    }
    public void SetSettingsOpen(bool open, bool pointerInside = false)
    {
        if (disposed || Current.Exiting) return;
        var next = NewIntent() with { SettingsOpen = open };
        Publish(next with { EdgePhase = next.EdgePhase == EdgePhase.WaitingClose ? EdgePhase.Expanded :
            next.EdgePhase == EdgePhase.WaitingOpen ? EdgePhase.Collapsed : open && next.EdgePhase == EdgePhase.Closing ? EdgePhase.Opening : next.EdgePhase }, false);
        if (!open) Hover(pointerInside, true);
    }
    public void SetCaptured(bool value)
    {
        captured = value;
        if (value) { var next = NewIntent(); Publish(next, false); }
        else Hover(inside, true);
    }
    public void SetGeometry(TimetableGeometry expanded, DipPoint ballCenter, bool stable)
    {
        if (disposed || Current.Exiting) return;
        Publish(Current with { Expanded = expanded, BallCenter = ballCenter }, stable && Current.BallMotion == BallMotionPhase.None);
    }
    public void Configure(EdgeDirection edge, double offset, WindowLevel level, double scale,
        double? stripThickness = null, double? stripLength = null)
    {
        if (disposed || Current.Exiting) return;
        if (!Enum.IsDefined(edge) || !Enum.IsDefined(level) || !double.IsFinite(offset) || offset is < 0 or > 1)
            throw new ArgumentOutOfRangeException(nameof(offset));
        var thickness = stripThickness ?? Current.StripThickness;
        var length = stripLength ?? Current.StripLength;
        if (!double.IsFinite(thickness) || thickness is < 4 or > 12 || !double.IsFinite(length) || length is < 48 or > 120)
            throw new ArgumentOutOfRangeException(nameof(stripThickness));
        // Validate before invalidating the current motion/hover generation.
        var geometry = new TimetableGeometry(Current.Expanded.TopLeft, scale);
        var next = NewIntent();
        Publish(next with { Edge = edge, EdgeOffset = offset, EdgeLevel = level,
            StripThickness = thickness, StripLength = length,
            Expanded = geometry, EdgePhase = next.State == TimetableState.EdgeDocked ? EdgePhase.Collapsed : EdgePhase.Expanded,
            BallMotion = BallMotionPhase.None });
    }
    public void BeginExit()
    {
        if (disposed || Current.Exiting) return;
        Publish(NewIntent() with { Exiting = true }, false);
    }
    public void Dispose() { if (disposed) return; BeginExit(); disposed = true; hover?.Dispose(); hover = null; Changed = null; HoverTriggered = null; }
}
