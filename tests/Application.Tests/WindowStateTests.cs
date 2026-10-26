using DlutTimetable.Application;
using DlutTimetable.Contracts;
using DlutTimetable.Domain;

namespace DlutTimetable.Tests;

public sealed class WindowStateTests
{
    private static SceneSnapshot Initial(TimetableState state = TimetableState.Locked) =>
        new(state, EdgePhase.Collapsed, new(new(80, 70), 1.2), new(40, 40), EdgeDirection.Right, 0.5, WindowLevel.Topmost);
    private static ManualClock Clock() => new(DateTimeOffset.UtcNow);
    [Fact]
    public void PresentationPauseCancelsHoverAndRejectsLateMotionWithoutChangingIntent()
    {
        var clock=Clock();using var state=new WindowStateController(Initial(),clock,a=>a());
        state.Execute(AppCommandKind.DockToEdge);state.Hover(true);clock.Advance(TimeSpan.FromMilliseconds(900));
        state.SetPresentationPaused(true);var paused=state.Current;
        clock.Advance(TimeSpan.FromSeconds(2));state.Hover(true);
        Assert.Equal(paused,state.Current);Assert.Equal(EdgePhase.Collapsed,paused.EdgePhase);
        state.SetPresentationPaused(false);state.Hover(true);clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(EdgePhase.WaitingOpen,state.Current.EdgePhase);clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(EdgePhase.Opening,state.Current.EdgePhase);
        state.Execute(AppCommandKind.CollapseToBall);var oldGeneration=state.Current.Generation;
        state.SetPresentationPaused(true);var settled=state.Current;
        state.CompleteBallMotion(oldGeneration);
        Assert.Equal(settled,state.Current);Assert.Equal(TimetableState.Ball,settled.State);
        Assert.Equal(BallMotionPhase.None,settled.BallMotion);Assert.Equal(Initial().Expanded,settled.Expanded);
    }
    [Fact]
    public void CaptureSuspendsPendingCloseAndReleaseStartsANewFullSecondOutside()
    {
        var clock=Clock();using var state=new WindowStateController(Initial(),clock,action=>action());
        state.Execute(AppCommandKind.DockToEdge);state.Hover(true);clock.Advance(TimeSpan.FromSeconds(1));state.CompleteMotion(state.Current.Generation);
        state.Hover(false);clock.Advance(TimeSpan.FromMilliseconds(900));state.SetCaptured(true);
        clock.Advance(TimeSpan.FromSeconds(2));Assert.True(state.Current.PanelVisible);
        state.SetCaptured(false);clock.Advance(TimeSpan.FromMilliseconds(999));Assert.Equal(EdgePhase.WaitingClose,state.Current.EdgePhase);
        clock.Advance(TimeSpan.FromMilliseconds(1));Assert.Equal(EdgePhase.Closing,state.Current.EdgePhase);
    }
    [Fact]
    public void StripResizeCancelsOldHoverAndIsPreservedByOtherLayoutChanges()
    {
        var clock=Clock();using var state=new WindowStateController(Initial(),clock,action=>action());
        state.Execute(AppCommandKind.DockToEdge);state.Hover(true);clock.Advance(TimeSpan.FromMilliseconds(900));
        state.Configure(EdgeDirection.Top,0.4,WindowLevel.Topmost,1,12,120);
        clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.Equal(EdgePhase.Collapsed,state.Current.EdgePhase);
        Assert.Equal(12,state.Current.StripThickness);Assert.Equal(120,state.Current.StripLength);
        state.Configure(EdgeDirection.Bottom,0.2,WindowLevel.Desktop,0.8);
        Assert.Equal(12,state.Current.StripThickness);Assert.Equal(120,state.Current.StripLength);
        var before=state.Current;
        Assert.Throws<ArgumentOutOfRangeException>(()=>state.Configure(EdgeDirection.Left,0.5,WindowLevel.Desktop,1,double.NaN,64));
        Assert.Throws<ArgumentOutOfRangeException>(()=>state.Configure(EdgeDirection.Left,0.5,WindowLevel.Desktop,1,6,121));
        Assert.Equal(before,state.Current);
    }
    [Fact]
    public void PinChangesOnlyLockAndNeverGrantsTopmostOutsideTheEdgeState()
    {
        using var state = new WindowStateController(Initial(), Clock(), action => action());
        Assert.Equal(WindowLevel.Desktop, state.Current.EffectiveLevel);
        state.Execute(AppCommandKind.TogglePin);
        Assert.Equal(TimetableState.Movable, state.Current.State);
        Assert.Equal(WindowLevel.Desktop, state.Current.EffectiveLevel);
        state.Execute(AppCommandKind.TogglePin);
        Assert.Equal(TimetableState.Locked, state.Current.State);
    }
    [Fact]
    public void HoverUnderOneSecondNeverExpandsAndLeavingCancelsTheTimer()
    {
        var clock = Clock(); using var state = new WindowStateController(Initial(), clock, action => action());
        state.Execute(AppCommandKind.DockToEdge); state.Hover(true);
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(state.Current.PanelVisible);
        state.Hover(false); clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(EdgePhase.Collapsed, state.Current.EdgePhase);
        Assert.Null(clock.NextDue);
    }
    [Fact]
    public void SettingsSuspendClosingAndClosingSettingsStartsAFreshFullSecond()
    {
        var clock = Clock(); using var state = new WindowStateController(Initial(), clock, action => action());
        TimeSpan? measured = null; state.HoverTriggered += (_, elapsed) => measured = elapsed;
        state.Execute(AppCommandKind.DockToEdge); state.Hover(true); clock.Advance(TimeSpan.FromSeconds(1));
        state.CompleteMotion(state.Current.Generation);
        state.Hover(false); clock.Advance(TimeSpan.FromMilliseconds(800));
        state.SetSettingsOpen(true); clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(EdgePhase.Expanded, state.Current.EdgePhase);
        state.SetSettingsOpen(false, false); clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.Equal(EdgePhase.WaitingClose, state.Current.EdgePhase);
        clock.Advance(TimeSpan.FromMilliseconds(1));
        Assert.Equal(EdgePhase.Closing, state.Current.EdgePhase);
        Assert.Equal(TimeSpan.FromSeconds(1), measured);
    }
    [Fact]
    public void OpeningSettingsDuringClosingRetargetsToExpandedAndRejectsTheClosingCallback()
    {
        var clock = Clock(); using var state = new WindowStateController(Initial(), clock, action => action());
        state.Execute(AppCommandKind.DockToEdge); state.Hover(true); clock.Advance(TimeSpan.FromSeconds(1)); state.CompleteMotion(state.Current.Generation);
        state.Hover(false); clock.Advance(TimeSpan.FromSeconds(1));
        var closingGeneration = state.Current.Generation;
        state.SetSettingsOpen(true);
        Assert.Equal(EdgePhase.Opening, state.Current.EdgePhase);
        state.CompleteMotion(closingGeneration); Assert.Equal(EdgePhase.Opening, state.Current.EdgePhase);
        state.CompleteMotion(state.Current.Generation); clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Equal(EdgePhase.Expanded, state.Current.EdgePhase);
    }
    [Fact]
    public void ReenteringDuringClosingReversesWithANewGenerationAndRejectsOldCompletion()
    {
        var clock = Clock(); using var state = new WindowStateController(Initial(), clock, action => action());
        state.Execute(AppCommandKind.DockToEdge); state.Hover(true); clock.Advance(TimeSpan.FromSeconds(1)); state.CompleteMotion(state.Current.Generation);
        state.Hover(false); clock.Advance(TimeSpan.FromSeconds(1));
        var old = state.Current.Generation;
        Assert.Equal(EdgePhase.Closing, state.Current.EdgePhase);
        state.Hover(true);
        Assert.Equal(EdgePhase.Opening, state.Current.EdgePhase); Assert.True(state.Current.Generation > old);
        state.CompleteMotion(old); Assert.Equal(EdgePhase.Opening, state.Current.EdgePhase);
        state.CompleteMotion(state.Current.Generation); Assert.Equal(EdgePhase.Expanded, state.Current.EdgePhase);
    }
    [Fact]
    public void QueuedHoverFromThePreviousSceneCannotReopenAfterCollapsingToBall()
    {
        var queue = new Queue<Action>(); var clock = Clock();
        using var state = new WindowStateController(Initial(), clock, queue.Enqueue);
        state.Execute(AppCommandKind.DockToEdge); state.Hover(true); clock.Advance(TimeSpan.FromSeconds(1));
        state.Execute(AppCommandKind.CollapseToBall);
        Assert.Single(queue)();
        Assert.Equal(TimetableState.Ball, state.Current.State);
        Assert.Equal(WindowLevel.Desktop, state.Current.EffectiveLevel);
    }
    [Fact]
    public void OrbMovementDoesNotModifyExpandedGeometryAndClickReturnsLockedAtTheSavedLocation()
    {
        using var state = new WindowStateController(Initial(), Clock(), action => action());
        var expanded = state.Current.Expanded;
        state.Execute(AppCommandKind.CollapseToBall);
        state.SetGeometry(expanded, new(600, 300), true); state.OrbClick();
        Assert.Equal(TimetableState.Locked, state.Current.State); Assert.Equal(expanded, state.Current.Expanded);
        Assert.Equal(new DipPoint(600, 300), state.Current.BallCenter);
    }
    [Fact]
    public void PersistedEdgeAlwaysRestartsCollapsedRatherThanAtAMidAnimationPhase()
    {
        using var state = new WindowStateController(Initial(TimetableState.EdgeDocked) with { EdgePhase = EdgePhase.Opening }, Clock(), action => action());
        Assert.Equal(EdgePhase.Collapsed, state.Current.EdgePhase);
    }
    [Fact]
    public void PinFromExpandedEdgeReturnsDesktopLockedWhilePreservingGeometry()
    {
        var clock = Clock(); using var state = new WindowStateController(Initial(), clock, action => action());
        var geometry = state.Current.Expanded;
        state.Execute(AppCommandKind.DockToEdge); state.Hover(true); clock.Advance(TimeSpan.FromSeconds(1)); state.CompleteMotion(state.Current.Generation);
        Assert.Equal(WindowLevel.Topmost, state.Current.EffectiveLevel);
        state.Execute(AppCommandKind.TogglePin);
        Assert.Equal(TimetableState.Locked, state.Current.State); Assert.Equal(WindowLevel.Desktop, state.Current.EffectiveLevel);
        Assert.Equal(geometry, state.Current.Expanded);
    }
    [Fact]
    public void ExitInvalidatesDeferredCallbacksAndBlocksEveryCommandOrigin()
    {
        var queue = new Queue<Action>(); var clock = Clock(); var external = 0;
        using var state = new WindowStateController(Initial(), clock, queue.Enqueue);
        var router = new AppCommandRouter(state, queue.Enqueue, _ => external++);
        state.Execute(AppCommandKind.DockToEdge); state.Hover(true); clock.Advance(TimeSpan.FromSeconds(1));
        foreach (var origin in Enum.GetValues<CommandOrigin>()) router.Route(AppCommandKind.OpenSettings, origin);
        state.BeginExit();
        while (queue.TryDequeue(out var action)) action();
        Assert.True(state.Current.Exiting); Assert.False(state.Execute(AppCommandKind.TogglePin)); Assert.Equal(0, external);
    }
}
