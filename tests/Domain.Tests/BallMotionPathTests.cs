using DlutTimetable.Domain;

namespace DlutTimetable.Tests;

public sealed class BallMotionPathTests
{
    private static readonly DipRect Panel=new(-900,80,780,468),Orb=new(240,400,44,44);
    [Fact]
    public void DampedSettlementReboundsInsideBoundsAndEndsExactlyWithoutReducedMotionMovement()
    {
        var pose=BallMotionPath.Evaluate(0.5,Panel,Orb);
        var samples=new List<double>();
        for(var i=0;i<=1000;i++)
        {
            var t=i/1000d;var result=BallMotionPath.Settle(pose,t,2,false);
            Assert.True(result.Bounds.X>=pose.Bounds.X && result.Bounds.Y>=pose.Bounds.Y);
            Assert.True(result.Bounds.X+result.Bounds.Width<=pose.Bounds.X+pose.Bounds.Width+1e-8);
            Assert.True(result.Bounds.Y+result.Bounds.Height<=pose.Bounds.Y+pose.Bounds.Height+1e-8);
            Assert.InRange(result.BallScale,0.96,1);
            Assert.InRange(result.Bounds.X-pose.Bounds.X,0,4);
            Assert.Equal(pose,BallMotionPath.Settle(pose,t,2,true));
            samples.Add(result.BallScale);
        }
        // A contraction followed by a return and a smaller second contraction.
        Assert.True(samples[650]<samples[550]);Assert.True(samples[775]>samples[650]);
        Assert.True(samples[850]<samples[775]);Assert.True(samples[850]>samples[650]);
        Assert.Equal(pose,BallMotionPath.Settle(pose,0,2,false));
        Assert.Equal(pose,BallMotionPath.Settle(pose,1,2,false));
        Assert.Throws<ArgumentOutOfRangeException>(()=>BallMotionPath.Settle(pose,double.NaN,2,false));
    }
    [Fact]
    public void CurtainCropsBeforeMovingToTheOrbAndEndpointsAreExact()
    {
        var start=BallMotionPath.Evaluate(0,Panel,Orb);var curtain=BallMotionPath.Evaluate(0.34,Panel,Orb);var end=BallMotionPath.Evaluate(1,Panel,Orb);
        Assert.Equal(Panel,start.Bounds);Assert.Equal(1,start.TextureOpacity);Assert.Equal(0,start.BallOpacity);
        Assert.Equal(Panel.X,curtain.Bounds.X);Assert.Equal(Panel.Width,curtain.Bounds.Width);Assert.Equal(0.14,curtain.Crop,8);
        Assert.Equal(Panel.Height*0.14,curtain.Bounds.Height,8);
        Assert.Equal(Orb,end.Bounds);Assert.Equal(0,end.TextureOpacity);Assert.Equal(1,end.BallOpacity);
    }
    [Fact]
    public void PathIsContinuousBoundedAndNeverMutatesTheExpandedRectangle()
    {
        var before=BallMotionPath.Evaluate(0.34-1e-8,Panel,Orb);var after=BallMotionPath.Evaluate(0.34+1e-8,Panel,Orb);
        Assert.InRange(Math.Abs(before.Bounds.Height-after.Bounds.Height),0,1e-4);
        for(var i=0;i<=1000;i++)
        {
            var pose=BallMotionPath.Evaluate(i/1000d,Panel,Orb);
            Assert.InRange(pose.Bounds.X,Panel.X,Orb.X);Assert.InRange(pose.Bounds.Y,Panel.Y,Orb.Y);
            Assert.InRange(pose.Bounds.X+pose.Bounds.Width,Panel.X,Orb.X+Orb.Width);
            Assert.InRange(pose.Bounds.Y+pose.Bounds.Height,Panel.Y,Math.Max(Panel.Y+Panel.Height,Orb.Y+Orb.Height));
            Assert.InRange(pose.Crop,0.14-1e-8,1);Assert.InRange(pose.TextureOpacity,0,1);Assert.InRange(pose.BallOpacity,0,1);
        }
        Assert.Equal(new(-900,80,780,468),Panel);
    }
    [Fact]
    public void ReducedMotionCrossFadesStableShapesAndRejectsInvalidInput()
    {
        var pose=BallMotionPath.Evaluate(0.5,Panel,Orb,true);
        Assert.Equal(Panel,pose.Bounds);Assert.Equal(1,pose.Crop);Assert.Equal(0.5,pose.TextureOpacity);Assert.Equal(0.5,pose.BallOpacity);
        Assert.Throws<ArgumentOutOfRangeException>(()=>BallMotionPath.Evaluate(double.NaN,Panel,Orb));
        Assert.Throws<ArgumentOutOfRangeException>(()=>BallMotionPath.Evaluate(2,Panel,Orb));
    }
}
