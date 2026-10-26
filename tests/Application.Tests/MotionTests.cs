using DlutTimetable.Application;
using DlutTimetable.Contracts;
using DlutTimetable.Domain;

namespace DlutTimetable.Tests;

public sealed class MotionTests
{
    private static SceneSnapshot Initial()=>new(TimetableState.Locked,EdgePhase.Expanded,new(new(80,80),1),new(120,120),EdgeDirection.Right,0.5,WindowLevel.Topmost);
    [Fact]
    public void CurtainToConvergenceHasContinuousBoundsCropAndCornerRadius()
    {
        var panel=new DipRect(80,80,780,468);var ball=new DipRect(120,120,44,44);
        var a=BallMotionPath.Evaluate(0.34-0.000001,panel,ball);
        var b=BallMotionPath.Evaluate(0.34+0.000001,panel,ball);
        Assert.InRange(Math.Abs(a.Radius-b.Radius),0,0.001);
        Assert.InRange(Math.Abs(a.Bounds.Height-b.Bounds.Height),0,0.001);
        Assert.InRange(Math.Abs(a.Crop-b.Crop),0,0.001);
    }
    [Fact]
    public void NormalTimelineCompletesWithinTheFullMotionBudgetWithoutAnyResidentTimer()
    {
        var clock=new ManualClock(DateTimeOffset.UtcNow);var motion=new MotionTimeline(clock,0);
        motion.Retarget(true,1,false);clock.Advance(TimeSpan.FromMilliseconds(MotionTimeline.NormalMilliseconds-1));Assert.False(motion.Advance());
        clock.Advance(TimeSpan.FromMilliseconds(1));Assert.True(motion.Advance());Assert.Equal(1,motion.Progress);Assert.False(motion.Running);
        Assert.Equal(0,clock.TimerCount);Assert.InRange(motion.Duration.TotalMilliseconds,320,420);
    }
    [Fact]
    public void RetargetPreservesPresentationAndOnlyTheLatestGenerationCompletes()
    {
        var clock=new ManualClock(DateTimeOffset.UtcNow);var motion=new MotionTimeline(clock,0);
        motion.Retarget(true,1,false);clock.Advance(TimeSpan.FromMilliseconds(160));var current=motion.Progress;
        motion.Retarget(false,2,false);Assert.Equal(current,motion.Progress,8);Assert.Equal(2,motion.Generation);
        clock.SetWallClock(clock.Now.AddDays(100));Assert.Equal(current,motion.Progress,8);
        clock.Advance(TimeSpan.FromMilliseconds(420));Assert.True(motion.Advance());Assert.Equal(0,motion.Progress);
    }
    [Fact]
    public void UpdatingAnEquivalentGoalDoesNotRestartItsDeadlineAndReducedMotionIsBounded()
    {
        var clock=new ManualClock(DateTimeOffset.UtcNow);var motion=new MotionTimeline(clock,1);
        motion.Retarget(false,1,true);clock.Advance(TimeSpan.FromMilliseconds(60));motion.Retarget(false,2,true);
        clock.Advance(TimeSpan.FromMilliseconds(20));Assert.True(motion.Advance());Assert.Equal(2,motion.Generation);
        Assert.InRange(motion.Duration.TotalMilliseconds,1,120);Assert.Equal(0,motion.Progress);
    }
    [Fact]
    public void NearEndpointReverseKeepsItsHitTargetForSeveralFramesWithinReducedMotionBudget()
    {
        var clock=new ManualClock(DateTimeOffset.UtcNow);var motion=new MotionTimeline(clock,0);
        motion.Retarget(true,1,false);clock.Advance(TimeSpan.FromMilliseconds(1));
        motion.Retarget(false,2,true);
        Assert.InRange(motion.Duration.TotalMilliseconds,48,120);
        clock.Advance(TimeSpan.FromMilliseconds(20));Assert.False(motion.Advance());
        clock.Advance(TimeSpan.FromMilliseconds(28));Assert.True(motion.Advance());Assert.Equal(0,motion.Progress);
    }
    [Fact]
    public void StateCommitsOnlyAtTheLastBallTargetAndPreservesExpandedGeometry()
    {
        using var scene=new WindowStateController(Initial(),TimeProvider.System,action=>action());
        var stable=new List<SceneSnapshot>();scene.Changed+=(state,isStable)=>{if(isStable)stable.Add(state);};
        var original=scene.Current.Expanded;
        scene.Execute(AppCommandKind.CollapseToBall);var old=scene.Current.Generation;
        Assert.Empty(stable);Assert.Equal(BallMotionPhase.ToBall,scene.Current.BallMotion);Assert.False(scene.Current.CommandsVisible);
        scene.Execute(AppCommandKind.CollapseToBall);Assert.Equal(BallMotionPhase.ToPanel,scene.Current.BallMotion);
        scene.CompleteBallMotion(old);Assert.Empty(stable);
        scene.CompleteBallMotion(scene.Current.Generation);
        Assert.Single(stable);Assert.Equal(TimetableState.Locked,stable[0].State);Assert.Equal(original,stable[0].Expanded);
        Assert.Equal(BallMotionPhase.None,stable[0].BallMotion);
    }
    [Fact]
    public void OrbReversesAnOpeningTargetAndRecoveryOrExitRejectsOldCompletions()
    {
        using var scene=new WindowStateController(Initial(),TimeProvider.System,action=>action());
        scene.Execute(AppCommandKind.CollapseToBall);scene.CompleteBallMotion(scene.Current.Generation);
        scene.OrbClick();Assert.Equal(BallMotionPhase.ToPanel,scene.Current.BallMotion);
        scene.OrbClick();Assert.Equal(BallMotionPhase.ToBall,scene.Current.BallMotion);
        var old=scene.Current.Generation;scene.StabilizeBallMotion();scene.CompleteBallMotion(old);
        Assert.Equal(BallMotionPhase.None,scene.Current.BallMotion);Assert.Equal(TimetableState.Ball,scene.Current.State);
        scene.OrbClick();old=scene.Current.Generation;scene.BeginExit();scene.CompleteBallMotion(old);
        Assert.True(scene.Current.Exiting);Assert.False(scene.Execute(AppCommandKind.RestoreTimetable));
    }
    [Fact]
    public void ColdRestartDoesNotResumeTransientProgress()
    {
        using var scene=new WindowStateController(Initial() with {State=TimetableState.Ball,BallMotion=BallMotionPhase.ToBall},TimeProvider.System,action=>action());
        Assert.Equal(BallMotionPhase.None,scene.Current.BallMotion);Assert.Equal(TimetableState.Ball,scene.Current.State);
    }
}
